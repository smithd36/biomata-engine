"""
src/service/events.py
──────────────────────────────────────────────────────────────
EventStreamAdapter: bridges the engine's synchronous EventBus to
service-layer ServiceEvent handlers.

The adapter subscribes to the EventBus as a single catch-all listener
and fans outbound events to all registered ServiceEvent handlers.
Handlers registered on the adapter receive ServiceEvent (not the raw
engine Event), keeping the transport layer isolated from engine internals.

Subscription management
───────────────────────
Each subscribe() call returns a unique subscription_id string.
unsubscribe() removes by that ID — O(1) via dict lookup.

Performance
───────────
Handlers are bucketed by event-type at subscribe time so the per-event
delivery path is O(matching_handlers), not O(all_handlers). When no
handler matches an emitted event, _on_engine_event short-circuits without
allocating a ServiceEvent — important at 100–500 agents where ACTION_*
events fire hundreds of times per tick.

Thread-safety
─────────────
The engine and EventBus are single-threaded/asyncio. The adapter does
not add any locking. If a transport pushes events across threads, the
caller is responsible for coordination.
"""
from __future__ import annotations

import uuid
from typing import Any

from src.engine.event_bus import Event, EventBus
from src.service.dto import ServiceEvent


class EventStreamAdapter:
    """
    Bridges the engine EventBus to service-layer ServiceEvent handlers.

    Attach to an EventBus once at session creation; detach with close().

        adapter = EventStreamAdapter(bus, session_id="sess-1")
        sub_id  = adapter.subscribe("tick_end", my_handler)
        # … ticks run …
        adapter.unsubscribe(sub_id)
        adapter.close()
    """

    def __init__(self, bus: EventBus, session_id: str) -> None:
        self._bus        = bus
        self._session_id = session_id

        # Bucketed handlers — event_type → {sub_id: handler}
        # Wildcard ("*") subscribers in their own bucket.
        self._by_type:  dict[str, dict[str, Any]] = {}
        self._wildcard: dict[str, Any]            = {}
        # sub_id → bucket key ("*" for wildcard, else event_type) — for O(1) unsubscribe
        self._sub_index: dict[str, str] = {}

        # Single wildcard listener on the bus — avoids N bus subscriptions
        bus.subscribe("*", self._on_engine_event)

    # ── Public API ────────────────────────────────────────────────────────────

    def subscribe(self, event_type: str | None, handler: Any) -> str:
        """
        Register handler for service events of event_type.

        event_type=None means all event types.
        Returns a subscription_id for later unsubscribe().
        """
        sub_id = uuid.uuid4().hex
        if event_type is None:
            self._wildcard[sub_id] = handler
            self._sub_index[sub_id] = "*"
        else:
            bucket = self._by_type.get(event_type)
            if bucket is None:
                bucket = {}
                self._by_type[event_type] = bucket
            bucket[sub_id] = handler
            self._sub_index[sub_id] = event_type
        return sub_id

    def unsubscribe(self, subscription_id: str) -> None:
        """Remove a handler by its subscription_id. No-op if not found."""
        key = self._sub_index.pop(subscription_id, None)
        if key is None:
            return
        if key == "*":
            self._wildcard.pop(subscription_id, None)
        else:
            bucket = self._by_type.get(key)
            if bucket is not None:
                bucket.pop(subscription_id, None)
                if not bucket:
                    del self._by_type[key]

    def close(self) -> None:
        """
        Detach from the EventBus and clear all handlers.
        Call when the session is shut down.
        """
        self._bus.unsubscribe("*", self._on_engine_event)
        self._by_type.clear()
        self._wildcard.clear()
        self._sub_index.clear()

    # ── Internal ──────────────────────────────────────────────────────────────

    def _on_engine_event(self, event: Event) -> None:
        """Translate one engine Event to a ServiceEvent and fan out to subscribers.

        Short-circuits when no handlers match — avoids ServiceEvent allocation
        for events nobody is listening to (common case at scale).
        """
        specific = self._by_type.get(event.type)
        wildcard = self._wildcard

        if not specific and not wildcard:
            return  # nobody listening — skip allocation entirely

        # Defensive copy of event.data so subscriber mutation can't leak back
        # into the engine. Empty dicts skip the copy (cheap shortcut).
        data = dict(event.data) if event.data else {}
        svc_event = ServiceEvent(
            session_id = self._session_id,
            event_type = event.type,
            tick       = event.tick,
            agent_id   = event.agent_id,
            data       = data,
        )
        if specific:
            for handler in specific.values():
                handler(svc_event)
        if wildcard:
            for handler in wildcard.values():
                handler(svc_event)
