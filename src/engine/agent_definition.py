"""
src/engine/agent_definition.py
────────────────────────────────────────────────────
AgentDefinition: declarative specification for runtime agent creation.

Separates the *specification* of an agent from the *instantiation* of one,
enabling fast structural validation before any imports are attempted and
making the registration pathway testable without a live simulation.

Typical flow
────────────
    defn   = AgentDefinition(id="scout_001", name="Scout", brain_class="...", ...)
    errors = validate_definition(defn)
    if errors:
        raise ...
    agent  = build_agent_from_definition(defn)
    sim.register_agent(defn)   # calls build_agent_from_definition internally
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from src.engine.agent import Agent


# ── Validation helpers ────────────────────────────────────────────────────────

_ID_RE:  re.Pattern[str] = re.compile(r'^[A-Za-z0-9_\-]+$')
_ID_MAX: int = 128


# ── Error type ────────────────────────────────────────────────────────────────

@dataclass
class AgentDefinitionError(Exception):
    """
    Raised when an AgentDefinition fails structural validation or
    fails to be constructed (bad dotted path, bad kwargs).

    field   — which field caused the error (e.g., "id", "brain_class").
    message — human-readable explanation.
    """
    field:   str
    message: str

    def __str__(self) -> str:
        return f"{self.field}: {self.message}"


# ── Definition dataclass ──────────────────────────────────────────────────────

@dataclass
class AgentDefinition:
    """
    All information required to create an agent at runtime.

    Fields mirror the sim.yaml agent config schema so the same attributes
    are available whether an agent is declared in YAML or via the
    register_agent transport call.

    id
        Unique identifier within the session.  Alphanumeric, underscores,
        and hyphens only; max 128 characters.

    name
        Human-readable display name.

    brain_class
        Dotted Python path to a Brain-protocol implementation.
        Example: ``"src.plugins.builtin.idle_brain.brain.IdleBrain"``

    brain_config
        Keyword arguments forwarded to the brain constructor.

    memory_class
        Optional dotted path to a Memory-protocol implementation.
        Defaults to SimpleMemory when None.

    memory_config
        Keyword arguments forwarded to the memory constructor.

    capabilities
        Capability tags that gate which action schemas and observation
        providers are visible to the agent — same semantics as the YAML
        ``capabilities`` list.

    inventory
        Starting item counts, e.g. ``{"gold": 10}``.

    metadata
        Arbitrary key/value pairs attached to the agent for downstream
        inspection (e.g. which Unity scene the agent belongs to).
        Not consumed by the engine tick loop.
    """
    id:            str
    name:          str
    brain_class:   str
    brain_config:  dict[str, Any] = field(default_factory=dict)
    memory_class:  str | None     = None
    memory_config: dict[str, Any] = field(default_factory=dict)
    capabilities:  list[str]      = field(default_factory=list)
    inventory:     dict[str, Any] = field(default_factory=dict)
    metadata:      dict[str, Any] = field(default_factory=dict)


# ── Validation ────────────────────────────────────────────────────────────────

def validate_definition(defn: AgentDefinition) -> list[AgentDefinitionError]:
    """
    Return a list of structural validation errors for *defn*.

    An empty list means the definition is valid and safe to pass to
    ``build_agent_from_definition()``.

    Importability of ``brain_class`` and ``memory_class`` is deliberately
    NOT checked here — that is deferred to construction so validation
    remains fast and side-effect-free.
    """
    errors: list[AgentDefinitionError] = []

    # id — non-empty, character set, length
    if not defn.id or not defn.id.strip():
        errors.append(AgentDefinitionError("id", "must be a non-empty string"))
    elif not _ID_RE.match(defn.id):
        errors.append(AgentDefinitionError(
            "id",
            "must contain only alphanumeric characters, underscores, and hyphens",
        ))
    elif len(defn.id) > _ID_MAX:
        errors.append(AgentDefinitionError("id", f"must be {_ID_MAX} characters or fewer"))

    # name — non-empty
    if not defn.name or not defn.name.strip():
        errors.append(AgentDefinitionError("name", "must be a non-empty string"))

    # brain_class — non-empty dotted path
    if not defn.brain_class or not defn.brain_class.strip():
        errors.append(AgentDefinitionError(
            "brain_class", "must be a non-empty dotted Python path"
        ))

    # capabilities — each element must be a string
    for i, cap in enumerate(defn.capabilities):
        if not isinstance(cap, str):
            errors.append(AgentDefinitionError(
                f"capabilities[{i}]",
                f"must be a string, got {type(cap).__name__}",
            ))

    # inventory — keys must be strings
    for k in defn.inventory:
        if not isinstance(k, str):
            errors.append(AgentDefinitionError(
                "inventory",
                f"keys must be strings, got key of type {type(k).__name__}",
            ))

    return errors


# ── Construction ──────────────────────────────────────────────────────────────

def build_agent_from_definition(defn: AgentDefinition) -> "Agent":
    """
    Instantiate an ``Agent`` from a validated ``AgentDefinition``.

    Raises
    ------
    ImportError / AttributeError
        ``brain_class`` or ``memory_class`` dotted path does not resolve.
    AgentDefinitionError
        Construction of brain or memory failed (wraps the original exception
        with a field annotation so callers can surface a targeted message).
    """
    from src.config.loader import _import as _loader_import
    from src.engine.agent import Agent
    from src.plugins.builtin.simple_memory.memory import SimpleMemory

    # Brain
    try:
        brain_cls = _loader_import(defn.brain_class)
    except (ImportError, AttributeError) as exc:
        raise ImportError(f"brain_class '{defn.brain_class}': {exc}") from exc

    try:
        brain = brain_cls(**defn.brain_config)
    except Exception as exc:
        raise AgentDefinitionError(
            "brain_config", f"brain constructor raised: {exc}"
        ) from exc

    # Memory
    if defn.memory_class:
        try:
            memory_cls = _loader_import(defn.memory_class)
        except (ImportError, AttributeError) as exc:
            raise ImportError(f"memory_class '{defn.memory_class}': {exc}") from exc
        try:
            memory = memory_cls(**defn.memory_config)
        except Exception as exc:
            raise AgentDefinitionError(
                "memory_config", f"memory constructor raised: {exc}"
            ) from exc
    else:
        memory = SimpleMemory()

    return Agent(
        id           = defn.id,
        name         = defn.name,
        brain        = brain,
        memory       = memory,
        inventory    = dict(defn.inventory),
        capabilities = frozenset(defn.capabilities),
        metadata     = dict(defn.metadata),
    )
