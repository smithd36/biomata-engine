"""
examples/village/sim/obs_registry.py
──────────────────────────────────────
Builds the ObservationRegistry for the village simulation.

Receives the social system (VillageRelationships) from the engine loader so
observation providers read from the same canonical instance that the
SocialEffectSubscriber writes to.

Capability tags:
  (none) — universal: every agent sees this
  social  — agents with the 'social' capability (villagers, merchants, LLM agents)

Called by the config loader via:
  observations:
    class: examples.village.sim.obs_registry.build_village_obs_registry
"""
from __future__ import annotations

from src.contracts.observation import ObservationSchema
from src.engine.obs_registry import ObservationRegistry
from src.plugins.builtin.observations.providers import IncomingMessagesProvider
from examples.village.sim.social import get_inbox, AGENT_ROLES
from examples.village.sim.observations import (
    TimeOfDayProvider,
    SelfStateProvider,
    NearbyPoiProvider,
    NearbyAgentsProvider,
    SocialMemoryProvider,
)


def build_village_obs_registry(social=None, **kwargs) -> ObservationRegistry:
    r     = ObservationRegistry()
    inbox = get_inbox()

    # ── Universal (all agents) ─────────────────────────────────────────────────

    r.register(
        ObservationSchema(
            "simulation_time",
            "Current time of day and simulation tick.",
            {"simulation_tick": int, "time_of_day": str},
            examples=[{"simulation_tick": 12, "time_of_day": "morning"}],
        ),
        TimeOfDayProvider(),
    )

    r.register(
        ObservationSchema(
            "self_state",
            "Your current position and nearest landmark.",
            {"position": str, "nearest_poi": str},
            examples=[{"position": "(13.8, 0.2)", "nearest_poi": "Market (0.4m)"}],
        ),
        SelfStateProvider(),
    )

    r.register(
        ObservationSchema(
            "nearby_pois",
            "The closest points of interest and their distances.",
            {"name": str, "x": float, "z": float, "distance": float},
            examples=[{
                "nearby_pois": [
                    {"name": "Market",     "x": 14.0, "z":  0.0, "distance": 0.4},
                    {"name": "TownSquare", "x":  0.0, "z":  0.0, "distance": 14.0},
                ]
            }],
        ),
        NearbyPoiProvider(top_n=4),
    )

    r.register(
        ObservationSchema(
            "incoming_messages",
            "Speech directed at you since your last tick. Reply or acknowledge these. "
            "Only present when someone has spoken to you.",
            {"from": str, "text": str},
            examples=[{
                "incoming_messages": [
                    {"from": "merchant_001", "text": "Fine goods today, friend!"},
                ]
            }],
        ),
        IncomingMessagesProvider(inbox),
    )

    # ── Social agents only ─────────────────────────────────────────────────────

    r.register(
        ObservationSchema(
            "nearby_agents",
            "Other villagers within sensor range (8m). Use their id in 'target' field.",
            {"id": str, "name": str, "distance": float, "role": str},
            tags=frozenset({"social"}),
            examples=[{
                "nearby_agents": [
                    {"id": "villager_001", "name": "Mira", "distance": 3.2, "role": "Villager"},
                    {"id": "merchant_001", "name": "Silas", "distance": 6.0, "role": "Merchant"},
                ]
            }],
        ),
        NearbyAgentsProvider(sensor_radius=8.0, roles=AGENT_ROLES),
    )

    if social is not None:
        r.register(
            ObservationSchema(
                "social_relationships",
                "Your familiarity and affinity with villagers you have interacted with.",
                {"social_relationships": str},
                tags=frozenset({"social"}),
                examples=[{"social_relationships": "Mira (familiar+), Silas (acquaintance~)"}],
            ),
            SocialMemoryProvider(social),
        )

    return r
