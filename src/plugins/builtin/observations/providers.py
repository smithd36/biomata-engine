"""
src/plugins/builtin/observations/providers.py
────────────────────────────────────────────────
Reference ObservationProvider implementations.

These are ready-to-use providers for common cross-domain observations.
Register them in an ObservationRegistry alongside your domain-specific ones.

  SimulationTimeProvider    — current tick number
  SocialContextProvider     — per-agent relationship summary (needs social system)
  IncomingMessagesProvider  — pending inbox messages from ConversationInbox
  FunctionProvider          — wraps any callable as a provider
  SelfNeedsProvider         — injects agent's own needs (hunger, energy, etc.)
"""
from __future__ import annotations

import logging
from typing import Any, Callable, TYPE_CHECKING

_logger = logging.getLogger(__name__)

if TYPE_CHECKING:
    from src.engine.agent import Agent
    from src.engine.conversation import ConversationInbox
    from src.plugins.builtin.needs.extension import NeedsExtension


class SimulationTimeProvider:
    """
    Injects the current simulation tick into every agent's observation.

    Produces: {"simulation_tick": int}
    """

    def observe(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        Any,
    ) -> dict[str, Any]:
        return {"simulation_tick": world.current_tick}


class SocialContextProvider:
    """
    Injects a text summary of an agent's social relationships.

    Delegates to social.describe(agent_id), which is part of the SocialSystem
    protocol. Works with any compliant SocialSystem implementation.

    Produces: {"social_relationships": str}

    Parameters
    ----------
    social
        Any SocialSystem-protocol instance (WeightedGraphSocial, VillageRelationships, etc.).
    """

    def __init__(self, social: Any) -> None:
        self._social = social

    def observe(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        Any,
    ) -> dict[str, Any]:
        try:
            summary = self._social.describe(agent_id)
            if not summary:
                return {}
            return {"social_relationships": summary}
        except Exception as exc:
            _logger.warning(
                "SocialContextProvider failed for agent %r: %s",
                agent_id,
                exc,
                exc_info=True,
            )
            return {}


class IncomingMessagesProvider:
    """
    Injects pending inbox messages from a ConversationInbox into observations.

    Messages are consumed (cleared) after delivery so agents process each
    message exactly once.

    Produces:
      incoming_messages : list[{"from": str, "text": str}]

    Only included in the observation dict when there are pending messages.
    """

    def __init__(self, inbox: "ConversationInbox") -> None:
        self._inbox = inbox

    def observe(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        Any,
    ) -> dict[str, Any]:
        messages = self._inbox.consume(agent_id)
        if not messages:
            return {}
        return {
            "incoming_messages": [
                {"from": m.from_id, "text": m.text}
                for m in messages
            ],
        }


class FunctionProvider:
    """
    Adapts a plain callable into an ObservationProvider.

    The callable receives (agent_id, world) and returns a dict slice.
    Capabilities are not forwarded — use this for universal observations.

    Example
    -------
    registry.register(
        ObservationSchema("weather", "Current weather conditions."),
        FunctionProvider(lambda aid, w: {"weather": "sunny"}),
    )
    """

    def __init__(self, fn: Callable[[str, Any], dict[str, Any]]) -> None:
        self._fn = fn

    def observe(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        Any,
    ) -> dict[str, Any]:
        return self._fn(agent_id, world)


class SelfNeedsProvider:
    """
    Injects an agent's own needs (hunger, energy, warmth, etc.) into observations.

    Only active when the agent has a NeedsExtension on state_ext.
    Returns {} silently for agents without one — no errors, no keys injected.

    Produces: {"needs": {"hunger": 72.0, "energy": 40.0}}

    Parameters
    ----------
    agents
        Pass sim.agents directly.  The provider holds a live reference so
        dynamically registered agents are picked up automatically.

    Example
    -------
        from src.plugins.builtin.needs import NeedsExtension
        from src.plugins.builtin.observations.providers import SelfNeedsProvider

        provider = SelfNeedsProvider(sim.agents)
        obs_registry.register(
            ObservationSchema("needs", "Agent's current physiological needs."),
            provider,
        )
    """

    def __init__(self, agents: "list[Agent]") -> None:
        self._agents = agents

    def observe(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        Any,
    ) -> dict[str, Any]:
        from src.plugins.builtin.needs.extension import NeedsExtension
        for agent in self._agents:
            if agent.id == agent_id:
                if isinstance(agent.state_ext, NeedsExtension):
                    return {"needs": dict(agent.state_ext.needs)}
                return {}
        return {}
