"""
src — a framework for autonomous agent simulations.

Public extension API:

    from src.contracts import World, Brain, Memory, SocialSystem
    from src.contracts import AgentStateExtension, ActionHandler
    from src.contracts import Intent, ActionResult, ActionSchema
    from src.contracts import AgentView, WorldContext, BrainContext
    from src.engine import Simulation, SimulationConfig
    from src.engine import EventBus, Event
"""
from src.engine.simulation import Simulation, SimulationConfig
from src.engine.event_bus import EventBus, Event
