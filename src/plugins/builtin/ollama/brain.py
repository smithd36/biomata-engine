"""
src/plugins/builtin/ollama/brain.py
─────────────────────────────────────────────
OllamaLLMBrain: a Brain implementation backed by a local Ollama model.

Personality, backstory, and prompt templates live here — not in Agent.
To use a different provider, implement the Brain protocol with the same
decide() signature.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import httpx

from src.contracts.action import Intent, ActionSchema, parse_intent
from src.contracts.brain import BrainContext, Observation
from src.contracts.world import AgentView


@dataclass
class Personality:
    traits:    list[str] = field(default_factory=lambda: ["curious"])
    goals:     list[str] = field(default_factory=lambda: ["survive"])
    backstory: str       = "A wanderer."


_SYSTEM_TEMPLATE = """\
You are an autonomous character in a simulation.

{actions_section}

You MUST respond with a single JSON object and NOTHING else (no markdown, no explanation):
{{
  "action": "<action name from the list above>",
  "target": "<agent_id of your target, or null>",
  "parameters": {{ <action-specific params, or {{}}> }},
  "reasoning": "<one sentence: why you chose this action>"
}}"""


class OllamaLLMBrain:
    """
    Calls a local Ollama model to decide actions.
    Owns: personality, prompt assembly, LLM call, response parsing.
    """

    def __init__(
        self,
        personality: Personality | dict | None = None,
        llm_config:  dict | None = None,
        **kwargs,
    ):
        if isinstance(personality, dict):
            personality = Personality(**personality)
        self.personality  = personality or Personality()

        llm_cfg           = llm_config or {}
        self.model        = llm_cfg.get("model", "qwen2.5:14b")
        self.base_url     = llm_cfg.get("base_url", "http://localhost:11434")
        self.temperature  = llm_cfg.get("temperature", 0.8)

    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        from src.engine.event_bus import Event, BRAIN_DECIDED
        system = self._build_system(actions)
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

    def _build_system(self, actions: list[ActionSchema]) -> str:
        lines = ["AVAILABLE ACTIONS (use exactly these names):"]
        for schema in actions:
            lines.append(schema.prompt_block())
        return _SYSTEM_TEMPLATE.format(actions_section="\n".join(lines))

    def _build_prompt(
        self,
        agent:   AgentView,
        obs:     Observation,
        context: BrainContext,
    ) -> str:
        inv_str    = ", ".join(f"{k}:{v}" for k, v in agent.inventory.items()) or "empty"
        nearby     = obs.get("nearby_agents", [])
        nearby_str = "\n".join(
            f"  • {a['name']} [{a['id']}] inv:{a['inventory']} ext:{a['ext']}"
            for a in nearby
        ) or "  (no one nearby)"

        skip = {"nearby_agents", "agent_id", "agent_name", "inventory",
                "state_ext", "state_advice", "state_str"}
        perception_lines = "\n".join(
            f"  {k}: {v}" for k, v in obs.items() if k not in skip
        )
        meta_str = " | ".join(
            f"{k}: {v}" for k, v in context.metadata.items()
            if k in ("tick", "season", "weather")
        )

        return f"""
=== YOUR CHARACTER ===
Name: {agent.name} [{agent.id}]
Traits: {', '.join(self.personality.traits)}
Goals: {', '.join(self.personality.goals)}
Backstory: {self.personality.backstory}

=== YOUR STATE ===
Inventory: {inv_str}
{obs.get('state_str', '')}
{obs.get('state_advice', '')}

=== WORLD ===
{meta_str}

=== YOUR PERCEPTION ===
{perception_lines}

=== AGENTS NEARBY ===
{nearby_str}

=== YOUR MEMORIES ===
{context.memory or 'No memories yet.'}

Respond with a JSON object only. Use agent_id (e.g. "agent_002") in the "target" field.
""".strip()

    # ── LLM call ──────────────────────────────────────────────────────────────

    async def _call_ollama(self, prompt: str, system: str) -> str:
        payload = {
            "model":   self.model,
            "prompt":  prompt,
            "system":  system,
            "stream":  False,
            "options": {"temperature": self.temperature, "seed": -1},
        }
        async with httpx.AsyncClient(timeout=90) as client:
            r = await client.post(f"{self.base_url}/api/generate", json=payload)
            r.raise_for_status()
            return r.json()["response"].strip()
