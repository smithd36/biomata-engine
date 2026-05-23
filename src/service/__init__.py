"""
src/service
──────────────────────────────────────────────────────────────
Transport-layer boundary for the simulation engine.

Public surface — import from here, not from sub-modules:

    from src.service import (
        # DTOs
        AgentObservationDTO, StepRequest, AgentDecisionDTO,
        StepResponse, ServiceEvent, SimulationStatus,
        # Interfaces
        SessionState, EventHandler, SimulationController,
        # Concrete session
        SimulationSession, SessionError, create_session,
        # Event adapter (advanced use)
        EventStreamAdapter,
    )

Transport adapters depend only on this package.
The core engine (src/engine/, src/contracts/) never imports from here.
"""
from src.service.dto import (
    AgentDecisionDTO,
    AgentObservationDTO,
    ServiceEvent,
    SimulationStatus,
    StepRequest,
    StepResponse,
)
from src.service.events import EventStreamAdapter
from src.service.interfaces import (
    EventHandler,
    SessionState,
    SimulationController,
)
from src.service.session import (
    SessionError,
    SimulationSession,
    create_session,
)

__all__ = [
    # DTOs
    "AgentObservationDTO",
    "StepRequest",
    "AgentDecisionDTO",
    "StepResponse",
    "ServiceEvent",
    "SimulationStatus",
    # Interfaces
    "SessionState",
    "EventHandler",
    "SimulationController",
    # Session
    "SimulationSession",
    "SessionError",
    "create_session",
    # Adapter
    "EventStreamAdapter",
]
