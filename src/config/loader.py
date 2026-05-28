"""
src/config/loader.py
─────────────────────────────
Load a Simulation from a YAML config file.

Plugin classes are imported dynamically via importlib — users reference
their own code (mygame.world.CityWorld) or builtins in the YAML config.

All construction uses the validated SimConfig model (Pydantic) — no raw
dict access after validation. ComponentConfig.kwargs() forwards extra
YAML fields to constructors so adding new config keys requires no loader
changes.

Example sim.yaml:
─────────────────
engine:
  ticks: 100
  seed: 42
  scheduler: sequential
  scheduler_order: [agent_001, agent_002]   # optional; sequential only

world:
  class: mygame.world.CityWorld
  width: 20
  height: 20

llm:
  model: qwen2.5:14b
  base_url: http://localhost:11434

social:
  class: src.plugins.builtin.simple_social.social.WeightedGraphSocial

registry:
  class: mygame.registry.build_registry

agents:
  - id: agent_001
    name: Alice
    inventory:
      gold: 10
      torch: 2
    brain:
      class: src.plugins.builtin.ollama.brain.OllamaLLMBrain
      personality:
        traits: [curious, cautious]
        goals: [explore the city]
        backstory: A newcomer to the city.
    memory:
      class: src.plugins.builtin.simple_memory.memory.SimpleMemory
      capacity: 30
    state_ext:
      class: mygame.vitals.CityVitals
─────────────────

Factory function convention
───────────────────────────
Registry and observation-registry entries reference factory callables rather
than classes. The loader uses inspect.signature to pass only the kwargs the
factory declares, so zero-arg factories are unaffected. Supported injected
kwargs:

  social   — the configured SocialSystem instance (or None)

Extra YAML keys on registry/observations blocks are also forwarded when the
factory signature declares matching parameter names.

Example:
  def build_my_obs_registry(social=None, sensor_radius=8.0): ...
"""
from __future__ import annotations

import importlib
import inspect
from pathlib import Path
from typing import Any

try:
    import yaml
    _YAML_AVAILABLE = True
except ImportError:
    _YAML_AVAILABLE = False


def _import(dotted_path: str) -> Any:
    """Dynamically import a class or callable from a dotted module path."""
    module_path, _, attr = dotted_path.rpartition(".")
    if not module_path:
        raise ValueError(
            f"Invalid class path {dotted_path!r} — expected 'module.ClassName'"
        )
    try:
        module = importlib.import_module(module_path)
    except ModuleNotFoundError as e:
        raise ImportError(
            f"Cannot import module '{module_path}' for class '{attr}'. "
            f"Is the package installed and the path correct? ({e})"
        ) from e
    if not hasattr(module, attr):
        raise AttributeError(
            f"Module '{module_path}' has no attribute '{attr}'"
        )
    return getattr(module, attr)


def _call_factory(fn: Any, **kwargs: Any) -> Any:
    """
    Call fn with the kwargs it declares in its signature.

    If fn accepts **kwargs (VAR_KEYWORD), all provided kwargs are forwarded —
    this handles brain constructors like OllamaLLMBrain that absorb extras.

    If fn declares only specific parameters, only matching kwargs are forwarded —
    this handles registry factories that opt into specific injected values
    (e.g. `social`) while ignoring everything else.

    Zero-arg callables continue to work unchanged.
    """
    try:
        sig    = inspect.signature(fn)
        params = sig.parameters
    except (ValueError, TypeError):
        return fn()

    # If any parameter accepts **kwargs, pass everything through
    has_var_kw = any(
        p.kind == inspect.Parameter.VAR_KEYWORD
        for p in params.values()
    )
    if has_var_kw:
        return fn(**kwargs)

    accepted = {k: v for k, v in kwargs.items() if k in params}
    return fn(**accepted)


def load_simulation(path: str) -> "Simulation":             # noqa: F821
    if not _YAML_AVAILABLE:
        raise ImportError("PyYAML is required: pip install pyyaml")

    raw = Path(path).read_text(encoding="utf-8")
    cfg = yaml.safe_load(raw)
    return _build_simulation(cfg, base_dir=str(Path(path).parent))


def _build_simulation(cfg: dict, base_dir: str = ".") -> "Simulation":  # noqa: F821
    from src.config.schema import SimConfig
    from src.engine.simulation import Simulation, SimulationConfig
    from src.engine.registry import ActionRegistry
    from src.engine.event_bus import EventBus, SocialEffectSubscriber
    from src.engine.scheduler import SimultaneousScheduler, SequentialScheduler
    from src.engine.agent import Agent
    from src.plugins.builtin.simple_memory.memory import SimpleMemory

    # ── Validate ───────────────────────────────────────────────────────────
    try:
        from pydantic import ValidationError
    except ImportError as exc:
        raise ImportError("pydantic is required: pip install pydantic") from exc

    try:
        sim = SimConfig.model_validate(cfg)
    except Exception as exc:
        raise ValueError(f"Invalid sim.yaml: {exc}") from exc

    # ── Engine config ──────────────────────────────────────────────────────
    eng     = sim.engine
    sim_cfg = SimulationConfig(
        ticks     = eng.ticks,
        seed      = eng.seed,
        log_level = eng.log_level,
    )

    # ── World ──────────────────────────────────────────────────────────────
    world_class = _import(sim.world.class_)
    world       = world_class(**sim.world.kwargs())

    # ── Social (built before registry so factories can receive it) ─────────
    social = None
    if sim.social is not None:
        soc_class = _import(sim.social.class_)
        social    = soc_class(**sim.social.kwargs())

    # ── Registry ───────────────────────────────────────────────────────────
    if sim.registry is not None:
        registry_kwargs = dict(sim.registry.kwargs())

        # If a manifest path is declared, load it and pass the ActionManifest object
        # to the factory so it can call manifest.schema("x") instead of constructing
        # ActionSchema objects by hand.
        if "manifest" in registry_kwargs:
            from src.config.manifest import ActionManifest
            manifest_path = Path(base_dir) / registry_kwargs.pop("manifest")
            registry_kwargs["manifest"] = ActionManifest.load(manifest_path)

        build_fn = _import(sim.registry.class_)
        registry = _call_factory(build_fn, social=social, **registry_kwargs)
    else:
        registry = ActionRegistry()

    # ── Observation registry (optional) ────────────────────────────────────
    if sim.observations is not None:
        obs_fn       = _import(sim.observations.class_)
        obs_registry = _call_factory(obs_fn, social=social, **sim.observations.kwargs())
    else:
        obs_registry = None

    # ── Event bus ──────────────────────────────────────────────────────────
    bus = EventBus()
    if social is not None:
        bus.subscribe("action_completed", SocialEffectSubscriber(social))

    # ── Scheduler ──────────────────────────────────────────────────────────
    if eng.scheduler == "sequential":
        scheduler = SequentialScheduler(order=eng.scheduler_order or None)
    else:
        scheduler = SimultaneousScheduler()

    # ── Agents ─────────────────────────────────────────────────────────────
    agents = []
    for a_cfg in sim.agents:
        # ── Role expansion ──────────────────────────────────────────────────
        # Agent-explicit settings always take precedence over role defaults.
        role_cfg = None
        if a_cfg.role is not None:
            role_cfg = sim.roles.get(a_cfg.role)
            if role_cfg is None:
                raise ValueError(
                    f"Agent '{a_cfg.id}': role '{a_cfg.role}' is not declared "
                    f"in the roles: block of sim.yaml"
                )

        # Capabilities: union of explicit + role (role may be empty frozenset)
        if role_cfg is not None:
            from src.config.roles import expand_capabilities, expand_brain_config
            capabilities = expand_capabilities(a_cfg, role_cfg)
            brain_cfg    = expand_brain_config(a_cfg, role_cfg)
        else:
            capabilities = frozenset(a_cfg.capabilities)
            if a_cfg.brain is None:
                raise ValueError(
                    f"Agent '{a_cfg.id}': no brain configured "
                    f"(set brain: on the agent or assign a role with a brain: block)"
                )
            brain_cfg = a_cfg.brain

        # ── Brain ───────────────────────────────────────────────────────────
        brain_class = _import(brain_cfg.class_)
        brain       = _call_factory(brain_class, llm_config=sim.llm, **brain_cfg.kwargs())

        # ── Memory ──────────────────────────────────────────────────────────
        if a_cfg.memory is not None:
            mem_class = _import(a_cfg.memory.class_)
            memory    = mem_class(**a_cfg.memory.kwargs())
        else:
            memory = SimpleMemory()

        # ── State extension ─────────────────────────────────────────────────
        state_ext = None
        if a_cfg.state_ext is not None:
            ext_class = _import(a_cfg.state_ext.class_)
            state_ext = ext_class(**a_cfg.state_ext.kwargs())

        # Propagate role name into metadata so downstream systems can inspect it
        metadata = dict(a_cfg.metadata)
        if a_cfg.role:
            metadata.setdefault("role", a_cfg.role)

        agent = Agent(
            id           = a_cfg.id,
            name         = a_cfg.name,
            brain        = brain,
            memory       = memory,
            inventory    = dict(a_cfg.inventory),
            state_ext    = state_ext,
            capabilities = capabilities,
            metadata     = metadata,
        )
        agents.append(agent)

        if social is not None:
            social.add_agent(agent.id, agent.name)

        if a_cfg.position:
            from src.contracts.world import PlaceableWorld
            if isinstance(world, PlaceableWorld):
                world.place_agent(agent.id, **a_cfg.position)

    return Simulation(
        agents       = agents,
        world        = world,
        registry     = registry,
        bus          = bus,
        scheduler    = scheduler,
        config       = sim_cfg,
        social       = social,
        obs_registry = obs_registry,
    )
