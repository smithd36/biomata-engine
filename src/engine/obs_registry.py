"""
src/engine/obs_registry.py
────────────────────────────────
ObservationRegistry maps named observation slots → (schema, provider).

Architecture mirrors ActionRegistry:
  - register()       adds a (schema, provider) pair
  - schemas_for()    filters by agent capability tags
  - collect()        runs all matching providers and merges results
  - observations_prompt_section()  renders schemas for LLM system prompts

The registry is optional — simulations without one work identically to
pre-observation-contract behavior.
"""
from __future__ import annotations

import logging
from typing import Any

from src.contracts.observation import ObservationSchema, ObservationProvider

_logger = logging.getLogger(__name__)


class ObservationRegistry:
    """
    Central registry mapping observation names → (schema, provider).

    Usage
    -----
    obs_registry = ObservationRegistry()
    obs_registry.register(
        ObservationSchema("nearby_agents", "Agents within sensor range.",
                          {"id": str, "name": str, "distance": float}),
        NearbyAgentsProvider(radius=10.0),
    )
    merged = obs_registry.collect(agent_id, agent.capabilities, world)
    """

    def __init__(self) -> None:
        self._entries: dict[str, tuple[ObservationSchema, ObservationProvider]] = {}

    def register(self, schema: ObservationSchema, provider: ObservationProvider) -> None:
        if schema.name in self._entries:
            raise ValueError(f"Observation '{schema.name}' is already registered.")
        self._entries[schema.name] = (schema, provider)

    def observation_names(self) -> list[str]:
        return list(self._entries.keys())

    # ── Schema access ──────────────────────────────────────────────────────────

    def schemas(self) -> list[ObservationSchema]:
        """Return all registered ObservationSchema objects."""
        return [s for s, _ in self._entries.values()]

    def schemas_for(self, capabilities: frozenset[str]) -> list[ObservationSchema]:
        """
        Return schemas visible to an agent with the given capabilities.

        Visibility rules (identical to ActionRegistry.schemas_for):
          - Untagged schema (tags == frozenset())  → universal; always visible.
          - Tagged schema                          → visible only if agent's
            capabilities intersect the schema's tags.
        """
        result = []
        for schema, _ in self._entries.values():
            if not schema.tags or schema.tags & capabilities:
                result.append(schema)
        return result

    # ── Prompt rendering ───────────────────────────────────────────────────────

    def observations_prompt_section(
        self,
        capabilities: frozenset[str] | None = None,
    ) -> str:
        """
        Render visible observation schemas as a prompt section.

        Pass capabilities to filter by agent tags; omit (or pass None) to
        render all schemas (useful for debug / documentation).
        """
        schemas = (
            self.schemas_for(capabilities)
            if capabilities is not None
            else self.schemas()
        )
        if not schemas:
            return ""
        lines = ["OBSERVATION FIELDS:"]
        for schema in schemas:
            lines.append(schema.prompt_block())
        return "\n".join(lines)

    # ── Collection ─────────────────────────────────────────────────────────────

    def collect(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        "World",          # noqa: F821
    ) -> dict[str, Any]:
        """
        Run all capability-matching providers and merge their slices.

        Merge order: registration order; later providers overwrite earlier
        ones on key conflict.  World observations added on top of this dict
        after collection, so world always wins.

        Provider exceptions are logged and skipped — a buggy provider must
        not crash the simulation tick.
        """
        merged: dict[str, Any] = {}
        for schema, provider in self._entries.values():
            if schema.tags and not (schema.tags & capabilities):
                continue
            try:
                slice_ = provider.observe(agent_id, capabilities, world)
                if slice_:
                    merged.update(slice_)
            except Exception as exc:
                _logger.warning(
                    "ObservationRegistry: provider %r raised %s for agent %r — skipping slice",
                    schema.name,
                    type(exc).__name__,
                    agent_id,
                    exc_info=True,
                )
        return merged
