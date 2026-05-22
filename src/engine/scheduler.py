"""
src/engine/scheduler.py
────────────────────────────────
Scheduler controls the order and concurrency of agent steps within a tick.

Two built-in implementations:

  SimultaneousScheduler  — all agents decide concurrently (asyncio.gather).
                           Fast; agents act on the same world snapshot.
                           Default for most simulations.

  SequentialScheduler    — agents step one at a time in a fixed order.
                           Deterministic; each agent sees the results of
                           all previous agents' actions this tick.
                           Preferred for debugging and reproducible research.

Add your own by implementing the Scheduler protocol.
"""
from __future__ import annotations

import asyncio
from typing import Any, Callable, Coroutine, Protocol, runtime_checkable


StepFn = Callable[["Agent"], Coroutine[Any, Any, Any]]  # noqa: F821


@runtime_checkable
class Scheduler(Protocol):
    async def run_tick(
        self,
        agents: list["Agent"],          # noqa: F821
        step_fn: StepFn,
    ) -> list[tuple["Agent", Any]]:     # noqa: F821
        """
        Run one tick for all agents.

        Parameters
        ----------
        agents  : the live Agent objects for this tick
        step_fn : async callable(agent) → result | Exception

        Returns a list of (agent, result) pairs in deterministic order.
        Exceptions are caught and returned as the result value, not raised.
        """
        ...


class SimultaneousScheduler:
    """
    All agents run concurrently. Fast but non-deterministic in terms of
    which agent's world-mutations the others observe mid-tick.
    """
    async def run_tick(
        self,
        agents: list,
        step_fn: StepFn,
    ) -> list[tuple]:
        tasks = [step_fn(agent) for agent in agents]
        raw   = await asyncio.gather(*tasks, return_exceptions=True)
        return list(zip(agents, raw))


class SequentialScheduler:
    """
    Agents step one at a time in the order they appear in the agents list.
    Fully deterministic when combined with a seeded RNG and LLM replay.
    Each agent observes the mutations made by all prior agents this tick.
    """
    def __init__(self, order: list[str] | None = None):
        """
        order: optional list of agent_ids defining the step sequence.
               If None, uses the order of the agents list passed to run_tick.
        """
        self._order = order

    async def run_tick(
        self,
        agents: list,
        step_fn: StepFn,
    ) -> list[tuple]:
        if self._order:
            id_to_agent = {a.id: a for a in agents}
            ordered = [id_to_agent[aid] for aid in self._order if aid in id_to_agent]
        else:
            ordered = list(agents)

        results = []
        for agent in ordered:
            try:
                result = await step_fn(agent)
            except Exception as e:
                result = e
            results.append((agent, result))
        return results