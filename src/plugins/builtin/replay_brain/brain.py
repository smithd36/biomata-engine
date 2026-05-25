"""
src/plugins/builtin/replay_brain/brain.py
───────────────────────────────────────────────────
ReplayBrain: deterministic Brain for reproducible research and testing.

Two modes:

  record  — wraps an inner Brain; records every Intent to a JSONL file.
             Use this to capture a live run for later replay.

  replay  — reads Intents from a previously recorded JSONL file.
             LLM is never called; output is 100% deterministic.

YAML config — record mode:

  brain:
    class: src.plugins.builtin.replay_brain.brain.ReplayBrain
    mode: record
    path: runs/run1.jsonl
    inner:
      class: src.plugins.builtin.ollama.brain.OllamaLLMBrain
      personality:
        traits: [strategic]
        goals: [survive]

YAML config — replay mode:

  brain:
    class: src.plugins.builtin.replay_brain.brain.ReplayBrain
    mode: replay
    path: runs/run1.jsonl

Record file format (JSONL, one object per decide() call):

  {"tick": 1, "agent_id": "agent_001", "action": "move",
   "target": null, "parameters": {"direction": "north"}, "reasoning": "..."}
"""
from __future__ import annotations

import importlib
import json
import pickle
from collections import defaultdict
from pathlib import Path
from typing import Any

from src.contracts.action import Intent, ActionSchema
from src.contracts.brain import BrainContext, Observation
from src.contracts.world import AgentView


class ReplayBrain:
    RECORD = "record"
    REPLAY = "replay"

    def __init__(
        self,
        mode:       str  = REPLAY,
        path:       str  = "replay.jsonl",
        inner:      Any  = None,   # Brain instance, or dict with "class" key for YAML nesting
        llm_config: dict | None = None,
        **kwargs,
    ):
        self.mode = mode
        self.path = Path(path)

        if self.mode == self.RECORD:
            if isinstance(inner, dict):
                inner = self._instantiate(inner, llm_config)
            if inner is None:
                raise ValueError("ReplayBrain in record mode requires an inner brain.")
            self.inner    = inner
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self._out     = open(self.path, "w", encoding="utf-8")
            self._cursors: dict[str, int] = {}
        else:
            self.inner    = None
            self._by_agent = self._load(self.path)
            self._cursors  = defaultdict(int)
            self._out      = None

    # ── Brain protocol ─────────────────────────────────────────────────────────

    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        if self.mode == self.RECORD:
            intent = await self.inner.decide(agent, observation, actions, context)
            self._write(agent.id, context.tick, intent)
            return intent
        else:
            return self._next(agent.id, context.tick)

    # ── Record helpers ─────────────────────────────────────────────────────────

    def _write(self, agent_id: str, tick: int, intent: Intent) -> None:
        record = {
            "tick":       tick,
            "agent_id":   agent_id,
            "action":     intent.action,
            "target":     intent.target,
            "parameters": intent.parameters,
            "reasoning":  intent.reasoning,
        }
        assert self._out is not None
        self._out.write(json.dumps(record) + "\n")
        self._out.flush()

    # ── Replay helpers ─────────────────────────────────────────────────────────

    def _load(self, path: Path) -> dict[str, list[dict]]:
        if not path.exists():
            raise FileNotFoundError(f"ReplayBrain: replay file not found: {path}")
        by_agent: dict[str, list[dict]] = defaultdict(list)
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line:
                    r = json.loads(line)
                    by_agent[r.get("agent_id", "unknown")].append(r)
        return by_agent

    def _next(self, agent_id: str, tick: int) -> Intent:
        records = self._by_agent.get(agent_id, [])
        idx     = self._cursors[agent_id]
        if idx < len(records):
            r = records[idx]
            self._cursors[agent_id] = idx + 1
            return Intent(
                action     = r.get("action", "idle"),
                target     = r.get("target"),
                parameters = r.get("parameters", {}),
                reasoning  = r.get("reasoning", "(replayed)"),
            )
        return Intent(action="idle", reasoning=f"(replay exhausted at tick {tick})")

    # ── Dynamic inner-brain instantiation from YAML dict ──────────────────────

    @staticmethod
    def _instantiate(cfg: dict, llm_config: dict | None) -> Any:
        raw        = dict(cfg)
        class_path = raw.pop("class")
        mod_path, _, cls_name = class_path.rpartition(".")
        module = importlib.import_module(mod_path)
        cls    = getattr(module, cls_name)
        return cls(llm_config=llm_config or {}, **raw)

    # ── Snapshotable ───────────────────────────────────────────────────────────

    def serialize(self) -> bytes:
        """
        Capture replay cursor positions so simulation can resume from exactly
        the same point in the JSONL file after a restore.

        In record mode, the cursor dict is empty (nothing to restore); the
        partially-written output file is left intact and will be appended to
        on resume — callers should flush or close the file before snapshotting
        if strict replay fidelity is required.
        """
        return pickle.dumps({
            "mode":    self.mode,
            "cursors": dict(self._cursors),
        })

    def restore(self, data: bytes) -> None:
        """
        Restore cursor positions. Only applied when the stored mode matches
        the current mode; a mismatch (e.g. restoring a record snapshot into a
        replay brain) is silently ignored to avoid corrupting state.
        """
        state = pickle.loads(data)
        if state.get("mode") == self.mode:
            self._cursors = defaultdict(int, state.get("cursors", {}))

    # ── Cleanup ────────────────────────────────────────────────────────────────

    def close(self) -> None:
        if self._out is not None:
            self._out.close()
            self._out = None

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass
