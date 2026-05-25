"""
src/engine/conversation.py
───────────────────────────
ConversationInbox: per-agent inbox for targeted speech events.

Agents post to it via SpeakHandler / SocializeHandler when a target_id is
given. IncomingMessagesProvider reads and clears it each tick so the target
agent receives the message exactly once.

Usage
-----
inbox = ConversationInbox()

# sender side (in an action handler)
inbox.deliver(target_id="villager_001", from_id="merchant_001", text="Fine day!")

# receiver side (in an observation provider)
messages = inbox.consume("villager_001")
# -> [SocialEvent(from_id="merchant_001", text="Fine day!")]
# subsequent consume() returns []
"""
from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass


@dataclass(frozen=True)
class SocialEvent:
    from_id: str
    text:    str


class ConversationInbox:
    """
    Lightweight per-agent inbox.

    Thread safety: single-threaded async tick loops only.
    No lock needed — all reads and writes happen in the same event loop.
    """

    def __init__(self) -> None:
        self._inbox: dict[str, list[SocialEvent]] = defaultdict(list)

    def deliver(self, target_id: str, from_id: str, text: str) -> None:
        """Post a social event to target_id's inbox."""
        self._inbox[target_id].append(SocialEvent(from_id=from_id, text=text))

    def consume(self, agent_id: str) -> list[SocialEvent]:
        """Return and clear all pending messages for agent_id."""
        return self._inbox.pop(agent_id, [])
