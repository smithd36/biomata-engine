"""
examples/corporate/sim/display.py
───────────────────────────────────
Terminal display for the corporate simulation.

No ASCII map — renders an org status table and action log.
"""
from __future__ import annotations

from rich.console import Console
from rich.table import Table

from src.engine.event_bus import (
    Event, TICK_START, TICK_END, ACTION_COMPLETED, ACTION_FAILED, AGENT_STEP_ERROR,
)

console = Console()


class CorporateDisplaySubscriber:
    """
    Event bus subscriber that renders corporate sim state to the terminal.
    Attach with: bus.subscribe("*", CorporateDisplaySubscriber(world, agents))
    """

    def __init__(self, world, agents) -> None:
        self.world  = world
        self.agents = agents

    def __call__(self, event: Event) -> None:
        if event.type == TICK_START:
            self._on_tick_start(event)
        elif event.type in (ACTION_COMPLETED, ACTION_FAILED):
            self._on_action(event)
        elif event.type == AGENT_STEP_ERROR:
            console.print(f"  [red]ERROR {event.data.get('agent_name')}: {event.data.get('error')}[/red]")

    def _on_tick_start(self, event: Event) -> None:
        d = event.data
        console.print(
            f"\n[bold]── Tick {d.get('tick')}[/bold] "
            f"│ {d.get('quarter')} │ market: {d.get('market')}"
        )
        # Org status bar
        parts = []
        for a in self.agents:
            ext = a.state_ext.snapshot() if a.state_ext else {}
            s   = ext.get("stress", "?")
            inf = ext.get("influence", "?")
            rep = ext.get("reputation", "?")
            bud = a.inventory.get("budget", 0)
            s_c = "red" if (isinstance(s, int) and s > 75) else "yellow" if (isinstance(s, int) and s > 50) else "green"
            parts.append(
                f"[bold]{a.name}[/bold] "
                f"str:[{s_c}]{s}[/{s_c}] inf:{inf} rep:{rep} $k:{bud}"
            )
        console.print("  " + "  │  ".join(parts))

    def _on_action(self, event: Event) -> None:
        d       = event.data
        name    = d.get("agent_name", "?")
        action  = d.get("action", "?")
        outcome = d.get("outcome", "")
        target  = d.get("target")
        reason  = d.get("reasoning", "")
        color   = "cyan" if event.type == ACTION_COMPLETED else "red"

        tgt_str = f" → {target}" if target else ""
        console.print(
            f"  [green]{name}[/green] "
            f"[{color}]{action}[/{color}]{tgt_str}  "
            f"[dim]{outcome[:90]}[/dim]"
        )
        if reason:
            console.print(f"      [dim italic]↳ {reason[:100]}[/dim italic]")


class CorporateSummaryPrinter:
    """Print final org state after sim.run() completes."""

    def __init__(self, world, agents, social, event_log) -> None:
        self.world     = world
        self.agents    = agents
        self.social    = social
        self.event_log = event_log

    def print_summary(self) -> None:
        t = Table(title="Final Corporate State", show_lines=True)
        for col in ("Name", "Role", "Dept", "Stress", "Influence", "Reputation", "Budget $k", "Relationships"):
            t.add_column(col)

        for a in self.agents:
            ext  = a.state_ext.snapshot() if a.state_ext else {}
            role = self.world.roles.get(a.id, "?")
            dept = self.world.departments.get(a.id, "?")
            rels = self.social.describe(a.id) if self.social else "—"
            t.add_row(
                a.name, role, dept,
                str(ext.get("stress", "?")),
                str(ext.get("influence", "?")),
                str(ext.get("reputation", "?")),
                str(a.inventory.get("budget", 0)),
                rels,
            )
        console.print(t)

        console.print("\n[bold]Org chart:[/bold]")
        console.print(self.world.org_summary())

        console.print("\n[bold]Recent org events:[/bold]")
        for e in list(self.world._events)[-10:]:
            console.print(f"  [dim]{self.world._fmt_event(e)}[/dim]")

        console.print("\n[bold]Event log (last 20):[/bold]")
        for line in self.event_log.tail(20):
            console.print(f"  [dim]{line}[/dim]")
