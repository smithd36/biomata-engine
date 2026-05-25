"""
src/plugins/builtin/ollama/brain.py
─────────────────────────────────────────────
OllamaLLMBrain: a Brain implementation backed by a local Ollama model.

Personality, backstory, and prompt templates live here — not in Agent.
To use a different provider, implement the Brain protocol with the same
decide() signature.

Prompt architecture:
  System prompt  — configurable role sentence + actions + observation schema reference
  User prompt    — character, state, nearby agents, generic perception, memories

The prompt is observation-driven: all keys in the observation dict not owned
by the engine are rendered generically. No domain-specific field names are
assumed. Simulations that emit domain-specific keys (e.g. time_of_day,
nearby_pois, social_relationships) will have them rendered verbatim.

Configuration (in sim.yaml under each agent's brain: block):
  system_prompt: "You are a medieval knight in a castle simulation."
  personality:
    traits: [...]
    goals:  [...]
    backstory: ...
"""
from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field
from typing import Any

import httpx

from src.contracts.action import Intent, ActionSchema, parse_intent
from src.contracts.brain import BrainContext, Observation
from src.contracts.observation import ObservationSchema
from src.contracts.world import AgentView


_DEFAULT_SYSTEM_PROMPT = "You are an autonomous participant in a simulated environment."

_SYSTEM_TEMPLATE = """\
{system_prompt}

{actions_section}
{obs_section}
You MUST respond with a single JSON object and NOTHING else (no markdown, no explanation):
{{
  "action": "<action name from the list above>",
  "target": "<agent_id of your target, or null>",
  "parameters": {{ <action-specific params, or {{}}> }},
  "reasoning": "<one sentence: why you chose this action>"
}}"""

# Keys unconditionally injected by the engine — always excluded from the
# generic PERCEPTION section (see contracts/observation.py for full spec).
_ENGINE_KEYS: frozenset[str] = frozenset({
    "agent_id", "agent_name", "inventory",
    "state_ext", "state_advice", "state_str",
    "nearby_agents",
})

# Redundant derived keys never useful to show an LLM.
_REDUNDANT_KEYS: frozenset[str] = frozenset({
    "nearby_count",  # len(nearby_agents) — LLM can count
})

_SKIP_KEYS = _ENGINE_KEYS | _REDUNDANT_KEYS


@dataclass
class Personality:
    traits:    list[str] = field(default_factory=lambda: ["curious"])
    goals:     list[str] = field(default_factory=lambda: ["survive"])
    backstory: str       = "A wanderer."


class OllamaLLMBrain:
    """
    Calls a local Ollama model to decide actions.
    Owns: personality, system prompt, prompt assembly, LLM call, response parsing.

    All instances share a class-level semaphore that caps concurrent HTTP
    calls to Ollama.  Ollama queues excess requests internally, but N
    simultaneous long-running requests from asyncio.gather can all hit the
    httpx timeout before Ollama gets to them.  The semaphore keeps at most
    `max_concurrent` (default 2) requests in-flight at once so the
    SimultaneousScheduler doesn't trigger mass timeouts when agent count
    exceeds Ollama's effective throughput.
    """

    # Shared across all instances — one semaphore per process, sized on first use.
    _semaphore: asyncio.Semaphore | None = None
    _semaphore_size: int = 2              # overridden by the first brain constructed

    @classmethod
    def _get_semaphore(cls) -> asyncio.Semaphore:
        if cls._semaphore is None:
            cls._semaphore = asyncio.Semaphore(cls._semaphore_size)
        return cls._semaphore

    def __init__(
        self,
        personality:   Personality | dict | None = None,
        llm_config:    dict | None = None,
        system_prompt: str | None = None,
        **kwargs,
    ):
        if isinstance(personality, dict):
            personality = Personality(**personality)
        self.personality   = personality or Personality()
        self.system_prompt = system_prompt or _DEFAULT_SYSTEM_PROMPT

        llm_cfg          = llm_config or {}
        self.model        = llm_cfg.get("model", "qwen2.5:14b")
        self.base_url     = llm_cfg.get("base_url", "http://localhost:11434")
        self.temperature  = llm_cfg.get("temperature", 0.8)

        # Allow the YAML llm block to set semaphore size once.
        # Class-level so the first brain to construct wins; all others share it.
        max_concurrent = int(llm_cfg.get("max_concurrent", 2))
        if OllamaLLMBrain._semaphore is None:
            OllamaLLMBrain._semaphore_size = max_concurrent

    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        from src.engine.event_bus import Event, BRAIN_DECIDED
        system = self._build_system(actions, context.observation_schemas)
        prompt = self._build_prompt(agent, observation, context)
        raw    = await self._call_ollama(prompt, system)
        intent = parse_intent(raw, valid_actions={s.name for s in actions})
        if context.emit is not None:
            context.emit(Event(
                type     = BRAIN_DECIDED,
                tick     = context.tick,
                agent_id = agent.id,
                data     = {
                    "agent_name": agent.name,
                    "system":     system,
                    "prompt":     prompt,
                    "raw_output": raw,
                    "intent": {
                        "action":     intent.action,
                        "target":     intent.target,
                        "parameters": intent.parameters,
                        "reasoning":  intent.reasoning,
                    },
                },
            ))
        return intent

    # ── Prompt assembly ───────────────────────────────────────────────────────

    def _build_system(
        self,
        actions:             list[ActionSchema],
        observation_schemas: list[ObservationSchema] | None = None,
    ) -> str:
        action_lines = ["AVAILABLE ACTIONS (use exactly these names):"]
        for schema in actions:
            action_lines.append(schema.prompt_block())
        actions_section = "\n".join(action_lines)

        obs_section = ""
        if observation_schemas:
            obs_lines = ["\nOBSERVATION FIELDS (these keys appear in your perception):"]
            for schema in observation_schemas:
                obs_lines.append(schema.prompt_block())
            obs_section = "\n".join(obs_lines) + "\n"

        return _SYSTEM_TEMPLATE.format(
            system_prompt   = self.system_prompt,
            actions_section = actions_section,
            obs_section     = obs_section,
        )

    def _build_prompt(
        self,
        agent:   AgentView,
        obs:     Observation,
        context: BrainContext,
    ) -> str:
        inv_str = ", ".join(f"{k}:{v}" for k, v in agent.inventory.items()) or "empty"

        # ── Character (brain-owned; always present) ────────────────────────────
        goals_str = "\n".join(f"  - {g}" for g in self.personality.goals)
        character_block = (
            f"=== YOUR CHARACTER ===\n"
            f"Name: {agent.name} [{agent.id}]\n"
            f"Traits: {', '.join(self.personality.traits)}\n"
            f"Goals:\n{goals_str}\n\n"
            f"Backstory: {self.personality.backstory}"
        )

        # ── State (engine-injected keys; always present) ───────────────────────
        state_lines = [f"=== YOUR STATE ===", f"Inventory: {inv_str}"]
        state_str = obs.get("state_str", "")
        if state_str:
            state_lines.append(state_str)
        state_advice = obs.get("state_advice", "")
        if state_advice:
            state_lines.append(state_advice)
        state_block = "\n".join(state_lines)

        # ── Nearby agents (engine concept; formatted for readability) ──────────
        nearby = obs.get("nearby_agents", [])
        if nearby:
            nearby_lines = []
            for a in nearby:
                name = a.get("name", "?")
                aid  = a.get("id", "?")
                extra = {k: v for k, v in a.items() if k not in ("id", "name", "inventory", "ext")}
                if extra:
                    extra_str = "  " + "  ".join(f"{k}:{v}" for k, v in extra.items())
                else:
                    extra_str = ""
                nearby_lines.append(f"  • {name} [{aid}]{extra_str}")
            nearby_str = "\n".join(nearby_lines)
        else:
            nearby_str = "  (no one nearby)"
        nearby_block = f"=== AGENTS NEARBY ===\n{nearby_str}"

        # ── Generic perception (all remaining obs keys) ────────────────────────
        perception_lines: list[str] = []
        for k, v in obs.items():
            if k in _SKIP_KEYS or v in (None, "", [], {}):
                continue
            if isinstance(v, (dict, list)):
                perception_lines.append(f"  {k}: {json.dumps(v, separators=(',', ':'))}")
            else:
                perception_lines.append(f"  {k}: {v}")

        perception_block = (
            "=== PERCEPTION ===\n" + "\n".join(perception_lines)
            if perception_lines else ""
        )

        # ── Memories (context-owned) ───────────────────────────────────────────
        memories_block = f"=== YOUR MEMORIES ===\n{context.memory or 'No memories yet.'}"

        sections = [character_block, state_block, nearby_block]
        if perception_block:
            sections.append(perception_block)
        sections.append(memories_block)
        sections.append("Respond with a JSON object only.")

        return "\n\n".join(sections)

    # ── LLM call ──────────────────────────────────────────────────────────────

    async def _call_ollama(self, prompt: str, system: str) -> str:
        payload = {
            "model":   self.model,
            "prompt":  prompt,
            "system":  system,
            "stream":  False,
            "options": {"temperature": self.temperature, "seed": -1},
        }
        async with OllamaLLMBrain._get_semaphore():
            async with httpx.AsyncClient(timeout=90) as client:
                r = await client.post(f"{self.base_url}/api/generate", json=payload)
                r.raise_for_status()
                return r.json()["response"].strip()
