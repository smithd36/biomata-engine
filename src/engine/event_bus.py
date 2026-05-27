"""
src/engine/event_bus.py
────────────────────────────────
A simple synchronous publish/subscribe event bus.

Why synchronous first:
  - Deterministic ordering (important for researchers)
  - No asyncio complexity at the subscriber level
  - Easy to make async later if needed

Standard event types are defined as string constants.
Subscribers register a callable per event type (or "*" for all events).

Usage
-----
bus = EventBus()
bus.subscribe("action_completed", my_analytics_fn)
bus.subscribe("*", replay_recorder.on_event)

bus.emit(Event(type="action_completed", tick=1, agent_id="agent_001", data={...}))
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass, field
from typing import Any, Callable


# ── Event ─────────────────────────────────────────────────────────────────────

@dataclass
class Event:
    type:      str
    tick:      int
    agent_id:  str
    data:      dict[str, Any] = field(default_factory=dict)
    # data keys are event-type specific (see constants below)


# ── Standard event type constants ─────────────────────────────────────────────
# Use these rather than raw strings so typos are caught at import time.

TICK_START         = "tick_start"
TICK_END           = "tick_end"
ACTION_COMPLETED   = "action_completed"
ACTION_FAILED      = "action_failed"
BRAIN_DECIDED      = "brain_decided"       # emitted by brains that record prompt/raw output
AGENT_STEP_ERROR   = "agent_step_error"
AGENT_REGISTERED   = "agent_registered"   # emitted after successful runtime registration
AGENT_UNREGISTERED = "agent_unregistered" # emitted after successful runtime unregistration


# ── EventBus ──────────────────────────────────────────────────────────────────

Subscriber = Callable[[Event], None]

class EventBus:
    """
    Synchronous publish/subscribe bus.

    Subscribe with a specific event type or "*" for all events.
    Emit order within a tick is deterministic (insertion order of subscribers).
    """

    def __init__(self):
        self._subs: dict[str, list[Subscriber]] = {}

    def subscribe(self, event_type: str, fn: Subscriber) -> None:
        self._subs.setdefault(event_type, []).append(fn)

    def unsubscribe(self, event_type: str, fn: Subscriber) -> None:
        if event_type in self._subs:
            self._subs[event_type] = [s for s in self._subs[event_type] if s is not fn]

    def emit(self, event: Event) -> None:
        # Hot path — called O(agents × events_per_agent) per tick.
        # Avoid allocating empty-list defaults and prefer `is not None` over truthiness.
        subs = self._subs.get(event.type)
        if subs is not None:
            for fn in subs:
                fn(event)
        wildcard = self._subs.get("*")
        if wildcard is not None:
            for fn in wildcard:
                fn(event)

    def clear(self) -> None:
        self._subs.clear()


# ── Built-in subscribers ──────────────────────────────────────────────────────

class SocialEffectSubscriber:
    """
    Reads social side_effects from ACTION_COMPLETED events and updates
    the social system. Decouples handlers from SocialSystem entirely.
    """
    def __init__(self, social: Any):
        self.social = social

    def __call__(self, event: Event) -> None:
        for effect in event.data.get("side_effects", []):
            if effect.get("type") == "social":
                self.social.update(effect["from"], effect["to"], effect["delta"])


class EventLogSubscriber:
    """Appends a plain-text log entry for every action completed."""

    MAX_ENTRIES = 1000

    def __init__(self):
        self.log: deque[str] = deque(maxlen=self.MAX_ENTRIES)

    def __call__(self, event: Event) -> None:
        if event.type == ACTION_COMPLETED:
            d = event.data
            self.log.append(
                f"[t{event.tick}] {d.get('agent_name','?')}@{d.get('location','?')}: "
                f"{d.get('action','?')} — {d.get('outcome','?')}"
            )

    def tail(self, n: int = 20) -> list[str]:
        return list(self.log)[-n:]


class ObservabilitySubscriber:
    """
    Calls user-provided hooks on specific event types.
    Zero overhead when hooks are not set.
    """
    def __init__(
        self,
        on_tick:   Callable[[int], None]          | None = None,
        on_action: Callable[[Event], None]        | None = None,
        on_failure: Callable[[Event], None]       | None = None,
        on_event:  Callable[[Event], None]        | None = None,
    ):
        self._hooks = {
            TICK_START:       on_tick    and (lambda e: on_tick(e.tick)),
            ACTION_COMPLETED: on_action,
            ACTION_FAILED:    on_failure,
            "*":              on_event,
        }

    def __call__(self, event: Event) -> None:
        hook = self._hooks.get(event.type) or self._hooks.get("*")
        if hook:
            hook(event)