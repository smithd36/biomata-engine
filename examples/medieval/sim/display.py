"""
examples/medieval/sim/display.py
──────────────────────────────────
RichDisplaySubscriber: renders tick output to the terminal.

This is now just an event bus subscriber — the engine has zero rendering code.
Swap this for a WebSocket subscriber, a Pygame renderer, or silence it entirely.
"""
from __future__ import annotations

from rich.console import Console
from rich.table import Table

from src.engine.event_bus import (
    Event, TICK_START, TICK_END, ACTION_COMPLETED, ACTION_FAILED, AGENT_STEP_ERROR
)

console = Console()


class RichDisplaySubscriber:
    """
    Subscribe to the event bus to render a live medieval sim to the terminal.
    Attach with: bus.subscribe("*", RichDisplaySubscriber(world, agents, social))
    """

    def __init__(self, world, agents, social=None):
        self.world   = world
        self.agents  = agents
        self.social  = social

    def __call__(self, event: Event) -> None:
        if event.type == TICK_START:
            self._on_tick_start(event)
        elif event.type in (ACTION_COMPLETED, ACTION_FAILED):
            self._on_action(event)
        elif event.type == AGENT_STEP_ERROR:
            console.print(f"  [red]{event.data.get('agent_name','?')} errored: {event.data.get('error')}[/red]")
        elif event.type == TICK_END:
            self._on_tick_end(event)

    def _on_tick_start(self, event: Event) -> None:
        d = event.data
        console.print(
            f"\n[bold]── Tick {d.get('tick')}[/bold] | "
            f"{d.get('season')} | {d.get('weather')}"
        )
        console.print(self.world.ascii_map(self.agents))

    def _on_action(self, event: Event) -> None:
        d        = event.data
        name     = d.get("agent_name", "?")
        action   = d.get("action", "?")
        outcome  = d.get("outcome", "")
        target   = d.get("target")
        loc      = d.get("location", "?")
        reasoning = d.get("reasoning", "")

        # Vitals indicators — read from agents list
        agent = next((a for a in self.agents if a.id == event.agent_id), None)
        ext   = agent.state_ext.snapshot() if (agent and agent.state_ext) else {}
        hunger = ext.get("hunger", 0)
        energy = ext.get("energy", 100)
        h = "🔴" if hunger > 70 else "🟡" if hunger > 40 else "🟢"
        e = "💤" if energy < 20 else "😴" if energy < 50 else "⚡"

        cell    = self.world.grid.cell_for(event.agent_id)
        loc_str = f"({cell.x},{cell.y})" if cell else "?"
        tgt_str = f"→ {target}" if target else ""
        color   = "yellow" if event.type == ACTION_COMPLETED else "red"

        console.print(
            f"  {h}{e} [green]{name}[/green]{loc_str} "
            f"[{color}]{action}[/{color}]{tgt_str}  "
            f"[dim]{outcome[:80]}[/dim]"
        )
        if reasoning:
            console.print(f"      [dim italic]↳ {reasoning[:100]}[/dim italic]")

    def _on_tick_end(self, event: Event) -> None:
        pass  # could print a separator


class SummaryPrinter:
    """Call print_summary() after sim.run() to display final state."""

    def __init__(self, world, agents, social, event_log):
        self.world     = world
        self.agents    = agents
        self.social    = social
        self.event_log = event_log

    def print_summary(self) -> None:
        t = Table(title="Final Agent States", show_lines=True)
        for col in ("Name", "Health", "Hunger", "Energy", "Location", "Inventory", "Relationships"):
            t.add_column(col)
        for a in self.agents:
            cell = self.world.grid.cell_for(a.id)
            loc  = f"{cell.name} ({cell.x},{cell.y})" if cell else "?"
            inv  = ", ".join(f"{k}:{v}" for k, v in a.inventory.items()) or "—"
            ext  = a.state_ext.snapshot() if a.state_ext else {}
            t.add_row(
                a.name,
                str(ext.get("health", "n/a")),
                str(ext.get("hunger", "n/a")),
                str(ext.get("energy", "n/a")),
                loc, inv,
                self.social.describe(a.id) if self.social else "—",
            )
        console.print(t)

        if self.social:
            console.print("\n[bold]Social graph:[/bold]")
            for u, v, d in self.social.g.edges(data=True):
                un    = self.social.g.nodes[u]["name"]
                vn    = self.social.g.nodes[v]["name"]
                w     = d["weight"]
                color = "green" if w > 0.3 else "red" if w < -0.3 else "yellow"
                console.print(f"  [{color}]{un} → {vn}: {w:+.2f}[/{color}]")

        console.print("\n[bold]Event log (last 20):[/bold]")
        for line in self.event_log.tail(20):
            console.print(f"  [dim]{line}[/dim]")