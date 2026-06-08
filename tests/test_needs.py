"""
tests/test_needs.py
───────────────────
Tests for NeedsExtension and SelfNeedsProvider.

Covers:
  - get/set with clamping
  - tick() decay
  - apply_mutations() from action results
  - urgent_advice() threshold
  - snapshot serialization round-trip
  - SelfNeedsProvider with and without NeedsExtension

Run with: pytest tests/test_needs.py -v
"""
from __future__ import annotations

import pickle
from unittest.mock import AsyncMock, MagicMock

import pytest

from src.contracts.snapshot import AgentSnapshot
from src.engine.agent import Agent
from src.plugins.builtin.needs.extension import NeedsExtension
from src.plugins.builtin.observations.providers import SelfNeedsProvider


# ── Helpers ───────────────────────────────────────────────────────────────────

def _make_agent(agent_id: str = "a1", with_needs: bool = True) -> Agent:
    brain  = MagicMock()
    brain.decide = AsyncMock(return_value=MagicMock(action="idle"))
    memory = MagicMock()
    memory.recall.return_value = []
    ext = NeedsExtension({"hunger": 50.0, "energy": 80.0}) if with_needs else None
    return Agent(id=agent_id, name="Test", brain=brain, memory=memory, state_ext=ext)


# ── NeedsExtension ────────────────────────────────────────────────────────────

class TestNeedsExtension:
    def test_get_set(self):
        ext = NeedsExtension({"hunger": 50.0})
        assert ext.get_need("hunger") == 50.0
        ext.set_need("hunger", 75.0)
        assert ext.get_need("hunger") == 75.0

    def test_get_missing_returns_default(self):
        ext = NeedsExtension({})
        assert ext.get_need("hunger") == 0.0
        assert ext.get_need("hunger", 99.0) == 99.0

    def test_set_clamps_at_max(self):
        ext = NeedsExtension({"hunger": 50.0})
        ext.set_need("hunger", 200.0)
        assert ext.get_need("hunger") == 100.0

    def test_set_clamps_at_min(self):
        ext = NeedsExtension({"hunger": 50.0})
        ext.set_need("hunger", -10.0)
        assert ext.get_need("hunger") == 0.0

    def test_custom_clamp(self):
        ext = NeedsExtension({"stress": 5.0}, clamp=(0.0, 10.0))
        ext.set_need("stress", 15.0)
        assert ext.get_need("stress") == 10.0

    def test_tick_applies_decay(self):
        ext = NeedsExtension({"hunger": 50.0}, decay_rates={"hunger": 5.0})
        ext.tick()
        assert ext.get_need("hunger") == 45.0

    def test_tick_clamps_at_zero(self):
        ext = NeedsExtension({"hunger": 3.0}, decay_rates={"hunger": 10.0})
        ext.tick()
        assert ext.get_need("hunger") == 0.0

    def test_tick_no_decay_rates(self):
        ext = NeedsExtension({"hunger": 50.0})
        ext.tick()
        assert ext.get_need("hunger") == 50.0  # unchanged

    def test_tick_only_decays_known_needs(self):
        ext = NeedsExtension({"hunger": 50.0}, decay_rates={"energy": 5.0})
        ext.tick()
        assert ext.get_need("hunger") == 50.0  # energy not in needs, no error

    def test_apply_mutations_delta(self):
        ext = NeedsExtension({"hunger": 50.0, "energy": 80.0})
        ext.apply_mutations({"hunger": -10.0, "energy": 5.0})
        assert ext.get_need("hunger") == 40.0
        assert ext.get_need("energy") == 85.0

    def test_apply_mutations_ignores_unknown_keys(self):
        ext = NeedsExtension({"hunger": 50.0})
        ext.apply_mutations({"nonexistent": 99.0})
        assert ext.get_need("hunger") == 50.0

    def test_apply_mutations_ignores_non_numeric_deltas(self):
        ext = NeedsExtension({"hunger": 50.0})
        ext.apply_mutations({"hunger": "bad"})
        assert ext.get_need("hunger") == 50.0

    def test_urgent_advice_critical(self):
        ext = NeedsExtension({"hunger": 5.0, "energy": 80.0})
        advice = ext.urgent_advice()
        assert "hunger" in advice
        assert "energy" not in advice

    def test_urgent_advice_empty_when_ok(self):
        ext = NeedsExtension({"hunger": 50.0, "energy": 80.0})
        assert ext.urgent_advice() == ""

    def test_to_prompt_str(self):
        ext = NeedsExtension({"hunger": 50.0})
        s = ext.to_prompt_str()
        assert "hunger" in s
        assert "50.0" in s

    def test_to_prompt_str_empty(self):
        ext = NeedsExtension({})
        assert ext.to_prompt_str() == ""


# ── Snapshot round-trip ───────────────────────────────────────────────────────

class TestNeedsSnapshot:
    def test_serialize_restore_round_trip(self):
        ext = NeedsExtension(
            needs        = {"hunger": 60.0, "energy": 45.0},
            decay_rates  = {"hunger": 2.0},
            clamp        = (0.0, 100.0),
        )
        data = ext.serialize()
        restored = NeedsExtension(needs={})
        restored.restore(data)
        assert restored.needs == {"hunger": 60.0, "energy": 45.0}
        assert restored.decay_rates == {"hunger": 2.0}

    def test_restore_is_idempotent(self):
        ext = NeedsExtension({"hunger": 60.0})
        data = ext.serialize()
        ext.restore(data)
        ext.restore(data)
        assert ext.get_need("hunger") == 60.0

    def test_snapshot_dict(self):
        ext = NeedsExtension({"hunger": 60.0}, decay_rates={"hunger": 1.0})
        snap = ext.snapshot()
        assert snap["needs"] == {"hunger": 60.0}
        assert snap["decay_rates"] == {"hunger": 1.0}

    def test_snapshot_does_not_share_reference(self):
        ext = NeedsExtension({"hunger": 60.0})
        snap = ext.snapshot()
        snap["needs"]["hunger"] = 99.0
        assert ext.get_need("hunger") == 60.0  # original unchanged

    def test_agent_state_ext_persists_through_snapshot(self):
        ext = NeedsExtension({"hunger": 60.0, "energy": 80.0})
        data = ext.serialize()

        ext2 = NeedsExtension({"hunger": 0.0, "energy": 0.0})
        ext2.restore(data)
        assert ext2.get_need("hunger") == 60.0
        assert ext2.get_need("energy") == 80.0


# ── SelfNeedsProvider ─────────────────────────────────────────────────────────

class TestSelfNeedsProvider:
    def test_returns_needs_when_present(self):
        agent    = _make_agent("a1", with_needs=True)
        provider = SelfNeedsProvider([agent])
        obs      = provider.observe("a1", frozenset(), MagicMock())
        assert obs == {"needs": {"hunger": 50.0, "energy": 80.0}}

    def test_returns_empty_when_no_extension(self):
        agent    = _make_agent("a1", with_needs=False)
        provider = SelfNeedsProvider([agent])
        obs      = provider.observe("a1", frozenset(), MagicMock())
        assert obs == {}

    def test_returns_empty_for_unknown_agent(self):
        agent    = _make_agent("a1", with_needs=True)
        provider = SelfNeedsProvider([agent])
        obs      = provider.observe("unknown", frozenset(), MagicMock())
        assert obs == {}

    def test_returns_empty_when_state_ext_is_different_type(self):
        agent          = _make_agent("a1", with_needs=False)
        agent.state_ext = object()  # not a NeedsExtension
        provider = SelfNeedsProvider([agent])
        obs      = provider.observe("a1", frozenset(), MagicMock())
        assert obs == {}

    def test_live_list_reference_picks_up_new_agents(self):
        agents   = []
        provider = SelfNeedsProvider(agents)
        assert provider.observe("a2", frozenset(), MagicMock()) == {}

        agents.append(_make_agent("a2", with_needs=True))
        obs = provider.observe("a2", frozenset(), MagicMock())
        assert "needs" in obs

    def test_needs_dict_is_a_copy(self):
        agent    = _make_agent("a1", with_needs=True)
        provider = SelfNeedsProvider([agent])
        obs      = provider.observe("a1", frozenset(), MagicMock())
        obs["needs"]["hunger"] = 999.0
        assert agent.state_ext.get_need("hunger") == 50.0  # original unchanged
