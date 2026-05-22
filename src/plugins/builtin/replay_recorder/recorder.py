"""
src/plugins/builtin/replay_recorder/recorder.py
─────────────────────────────────────────────────────────
JSONReplayRecorderSubscriber: writes simulation events to a JSONL file.

Each line is a self-contained JSON object. Subscribers can selectively
listen to any subset of events — the recorder captures only what it sees.

Events recorded when subscribed to the bus:

  tick_start / tick_end     — tick boundaries; useful for replaying timing
  action_completed          — agent name, action, target, reasoning, outcome
  action_failed             — same fields + failure details
  brain_decided             — full prompt, raw LLM output, parsed intent
                              (emitted only by brains that opt in, e.g. OllamaLLMBrain)

Usage — opt-in subscription (subscribe only to what you need):

    from src.plugins.builtin.replay_recorder.recorder import JSONReplayRecorderSubscriber
    from src.engine.event_bus import ACTION_COMPLETED, ACTION_FAILED, BRAIN_DECIDED

    recorder = JSONReplayRecorderSubscriber("runs/run1.jsonl")
    bus.subscribe(ACTION_COMPLETED, recorder)
    bus.subscribe(ACTION_FAILED,    recorder)
    bus.subscribe(BRAIN_DECIDED,    recorder)   # omit if prompt/raw not needed

Usage — catch-all (records every event type):

    bus.subscribe("*", recorder)

Call recorder.close() when the simulation ends (or use as a context manager).
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from src.engine.event_bus import Event


class JSONReplayRecorderSubscriber:
    """
    Writes one JSON record per event to a JSONL file.

    Designed to be selectively subscribed — attach it only to the event types
    you care about. The EventBus wildcard "*" gives you everything.

    Each record schema:
      {
        "type":     "<event type string>",
        "tick":     <int>,
        "agent_id": "<str>",
        ... all fields from event.data ...
      }
    """

    def __init__(self, path: str | Path):
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        self._file = open(p, "w", encoding="utf-8")

    def __call__(self, event: Event) -> None:
        record: dict[str, Any] = {
            "type":     event.type,
            "tick":     event.tick,
            "agent_id": event.agent_id,
        }
        record.update(event.data)
        self._file.write(json.dumps(record, default=str) + "\n")
        self._file.flush()

    # ── Context manager support ────────────────────────────────────────────────

    def close(self) -> None:
        if not self._file.closed:
            self._file.close()

    def __enter__(self) -> "JSONReplayRecorderSubscriber":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass
