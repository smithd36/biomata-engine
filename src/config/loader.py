"""
src/config/loader.py
─────────────────────────────
Load a Simulation from a YAML config file.

Plugin classes are imported dynamically via importlib — users reference
their own code (mygame.world.CityWorld) or builtins in the YAML config.

Example sim.yaml:
─────────────────
engine:
  ticks: 100
  seed: 42
  scheduler: sequential

world:
  class: mygame.world.CityWorld
  width: 20
  height: 20

llm:
  model: qwen2.5:14b
  base_url: http://localhost:11434

registry:
  class: mygame.registry.build_registry

social:
  class: src.plugins.builtin.simple_social.social.WeightedGraphSocial

agents:
  - id: agent_001
    name: Alice
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
"""
from __future__ import annotations

import importlib
from pathlib import Path
from typing import Any

try:
    import yaml
    _YAML_AVAILABLE = True
except ImportError:
    _YAML_AVAILABLE = False

try:
    from pydantic import ValidationError
    _PYDANTIC_AVAILABLE = True
except ImportError:
    _PYDANTIC_AVAILABLE = False


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

    # ── Validate config with Pydantic ──────────────────────────────────────
    if _PYDANTIC_AVAILABLE:
        try:
            sim_cfg_model = SimConfig.model_validate(cfg)
        except Exception as exc:
            raise ValueError(f"Invalid sim.yaml: {exc}") from exc
    else:
        # Fallback: use raw dict (pydantic not installed)
        sim_cfg_model = None

    eng_cfg  = (sim_cfg_model.engine if sim_cfg_model else None)
    sim_cfg  = SimulationConfig(
        ticks     = eng_cfg.ticks     if eng_cfg else cfg.get("engine", {}).get("ticks", 20),
        seed      = eng_cfg.seed      if eng_cfg else cfg.get("engine", {}).get("seed", 42),
        log_level = eng_cfg.log_level if eng_cfg else cfg.get("engine", {}).get("log_level", "normal"),
    )

    # ── World ─────────────────────────────────────────────────────────────
    world_raw   = dict(cfg.get("world", {}))
    world_class = _import(world_raw.pop("class"))
    world       = world_class(**world_raw)

    # ── Registry ──────────────────────────────────────────────────────────
    reg_raw = cfg.get("registry") or {}
    if "class" in reg_raw:
        build_fn = _import(reg_raw["class"])
        registry = build_fn()
    else:
        registry = ActionRegistry()

    # ── Social ────────────────────────────────────────────────────────────
    social = None
    if "social" in cfg and cfg["social"]:
        soc_raw   = dict(cfg["social"])
        soc_class = _import(soc_raw.pop("class"))
        social    = soc_class(**soc_raw)

    # ── Event bus ─────────────────────────────────────────────────────────
    bus = EventBus()
    if social is not None:
        bus.subscribe("action_completed", SocialEffectSubscriber(social))

    # ── Scheduler ─────────────────────────────────────────────────────────
    sched_name = cfg.get("engine", {}).get("scheduler", "simultaneous")
    scheduler  = SequentialScheduler() if sched_name == "sequential" else SimultaneousScheduler()

    # ── Agents ────────────────────────────────────────────────────────────
    agents  = []
    llm_cfg = cfg.get("llm", {})

    for a_cfg in cfg.get("agents", []):
        # Brain
        brain_raw   = dict(a_cfg.get("brain", {}))
        brain_class = _import(brain_raw.pop("class"))
        brain       = brain_class(llm_config=llm_cfg, **brain_raw)

        # Memory (optional — defaults to SimpleMemory)
        if "memory" in a_cfg and a_cfg["memory"]:
            mem_raw   = dict(a_cfg["memory"])
            mem_class = _import(mem_raw.pop("class"))
            memory    = mem_class(**mem_raw)
        else:
            memory = SimpleMemory()

        # State extension (optional)
        state_ext = None
        if "state_ext" in a_cfg and a_cfg["state_ext"]:
            ext_raw   = dict(a_cfg["state_ext"])
            ext_class = _import(ext_raw.pop("class"))
            state_ext = ext_class(**ext_raw)

        agent = Agent(
            id        = a_cfg["id"],
            name      = a_cfg["name"],
            brain     = brain,
            memory    = memory,
            state_ext = state_ext,
        )
        agents.append(agent)

        if social is not None:
            social.add_agent(agent.id, agent.name)

        placement = a_cfg.get("position") or {}
        if placement:
            from src.contracts.world import PlaceableWorld
            if isinstance(world, PlaceableWorld):
                world.place_agent(agent.id, **placement)

    return Simulation(
        agents    = agents,
        world     = world,
        registry  = registry,
        bus       = bus,
        scheduler = scheduler,
        config    = sim_cfg,
        social    = social,
    )
