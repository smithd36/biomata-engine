"""
examples/village/sim/social.py
──────────────────────────────
VillageRelationships: bilateral familiarity + affinity tracker.

  familiarity : 0.0 (strangers) → 1.0 (close friends)   — increases with every interaction
  affinity    : -1.0 (hostile)  → 1.0 (trusted)          — reflects interaction quality

Implements the SocialSystem protocol so it can be wired via sim.yaml:

  social:
    class: examples.village.sim.social.VillageRelationships

Social updates flow through the canonical platform path:
  SocializeHandler → ActionResult.side_effects → SocialEffectSubscriber → update()

Agent names are populated by the engine via add_agent() during simulation setup.
"""
from __future__ import annotations

import pickle
from src.engine.conversation import ConversationInbox


# ── Agent roles table ─────────────────────────────────────────────────────────
# Used by NearbyAgentsProvider to annotate nearby-agent observations.
# Keep in sync with sim.yaml agents list.

AGENT_ROLES: dict[str, str] = {
    "guard_001":     "Guard",
    "guard_002":     "Guard",
    "villager_001":  "Villager",
    "villager_002":  "Villager",
    "merchant_001":  "Merchant",
    "farmer_001":    "Farmer",
    "innkeeper_001": "Innkeeper",
    "traveler_001":  "Traveler",
    "townsfolk_001": "Townsfolk",
    "townsfolk_002": "Townsfolk",
    "villager_003":  "Villager",
    "villager_004":  "Villager",
    "scholar_001":   "Scholar",
}


# ── VillageRelationships ──────────────────────────────────────────────────────

class VillageRelationships:
    """
    Bilateral relationship store keyed by a canonical sorted pair (a_id, b_id).

    Implements the SocialSystem protocol. Wired by the engine via sim.yaml;
    the loader calls add_agent() for each agent after construction so that
    summary_for() can resolve agent ids to names.

    update(from_id, to_id, delta) maps to the SocialSystem contract:
      - delta applied to affinity
      - familiarity increases by a fixed increment on every call (any interaction
        raises familiarity; the sign of delta determines affinity direction)
    """

    def __init__(self) -> None:
        self._pairs: dict[tuple[str, str], dict[str, float]] = {}
        self._names: dict[str, str] = {}

    # ── SocialSystem protocol ──────────────────────────────────────────────────

    def add_agent(self, agent_id: str, name: str) -> None:
        """Register an agent so their name appears in relationship summaries."""
        self._names[agent_id] = name

    def update(self, from_id: str, to_id: str, delta: float) -> None:
        """
        SocialSystem.update: apply a relationship change.

        Familiarity grows by 0.05 on every call (any interaction brings agents
        closer regardless of quality). Affinity shifts by delta — positive for
        friendly interactions, negative for hostile ones.

        Canonical caller: SocialEffectSubscriber (via ActionResult.side_effects).
        """
        rel = self._ensure(from_id, to_id)
        rel["familiarity"] = min(1.0, rel["familiarity"] + 0.05)
        rel["affinity"]    = min(1.0, max(-1.0, rel["affinity"] + delta))

    def relationship(self, from_id: str, to_id: str) -> float:
        """SocialSystem: return affinity score between two agents (-1.0 to 1.0)."""
        pair = self._pairs.get(self._key(from_id, to_id))
        return round(pair["affinity"], 2) if pair else 0.0

    def describe(self, agent_id: str) -> str:
        """SocialSystem: human-readable summary of agent's relationships."""
        return self.summary_for(agent_id)

    def serialize(self) -> bytes:
        """SocialSystem / Snapshotable: capture full state."""
        return pickle.dumps({"pairs": self._pairs, "names": self._names})

    def restore(self, data: bytes) -> None:
        """SocialSystem / Snapshotable: restore from serialized bytes."""
        state = pickle.loads(data)
        self._pairs = state["pairs"]
        self._names = state.get("names", {})

    # ── Extended village API ───────────────────────────────────────────────────

    def get(self, a: str, b: str) -> dict[str, float]:
        """Return familiarity + affinity dict for a pair."""
        return dict(self._ensure(a, b))

    def get_relationships(self, agent_id: str) -> dict[str, dict[str, float]]:
        """Return all relationships involving agent_id."""
        result: dict[str, dict[str, float]] = {}
        for (x, y), rel in self._pairs.items():
            if x == agent_id:
                result[y] = dict(rel)
            elif y == agent_id:
                result[x] = dict(rel)
        return result

    def interact(self, a: str, b: str, positive: bool = True) -> None:
        """Record a social interaction. Convenience wrapper around update()."""
        self.update(a, b, 0.02 if positive else -0.05)

    def summary_for(
        self,
        agent_id: str,
        names: dict[str, str] | None = None,
        top_n: int = 5,
    ) -> str:
        """Compact relationship summary suitable for LLM prompts."""
        rels = self.get_relationships(agent_id)
        if not rels:
            return "No established relationships yet."
        _names = names if names is not None else self._names
        sorted_rels = sorted(
            rels.items(),
            key=lambda kv: kv[1]["familiarity"],
            reverse=True,
        )[:top_n]
        parts: list[str] = []
        for other_id, rel in sorted_rels:
            name = _names.get(other_id, other_id)
            fam  = rel["familiarity"]
            aff  = rel["affinity"]
            if fam < 0.1:
                desc = "stranger"
            elif fam < 0.3:
                desc = "acquaintance"
            elif fam < 0.6:
                desc = "familiar"
            else:
                desc = "friend"
            aff_sign = "+" if aff > 0.05 else ("-" if aff < -0.05 else "~")
            parts.append(f"{name} ({desc}{aff_sign})")
        return ", ".join(parts)

    # ── Internal helpers ───────────────────────────────────────────────────────

    @staticmethod
    def _key(a: str, b: str) -> tuple[str, str]:
        return (a, b) if a < b else (b, a)

    def _ensure(self, a: str, b: str) -> dict[str, float]:
        k = self._key(a, b)
        if k not in self._pairs:
            self._pairs[k] = {"familiarity": 0.0, "affinity": 0.0}
        return self._pairs[k]


# ── Module-level inbox singleton ──────────────────────────────────────────────
# ConversationInbox is message-routing infrastructure, not social state.
# It is shared between SpeakHandler/SocializeHandler (senders) and
# IncomingMessagesProvider (receiver). The social system (VillageRelationships)
# is no longer a singleton — it is constructed by the engine from sim.yaml.

_inbox: ConversationInbox = ConversationInbox()


def get_inbox() -> ConversationInbox:
    """Return the shared village conversation inbox."""
    return _inbox
