"""
src/config/roles.py
────────────────────
Role resolution helpers. Roles are flat bundles declared in sim.yaml under
the `roles:` key; agents reference them via `role: RoleName`.

During agent construction the loader calls expand_agent_brain() and
expand_agent_capabilities() to apply role defaults without overwriting
anything the agent declared explicitly.

Brain provider shortcuts
────────────────────────
  brain:
    provider: ollama   →  src.plugins.builtin.ollama.brain.OllamaLLMBrain
    provider: idle     →  src.plugins.builtin.idle_brain.brain.IdleBrain
    provider: replay   →  src.plugins.builtin.replay_brain.brain.ReplayBrain

  brain:
    class: myproject.brain.CustomBrain
    my_kwarg: value

Extending the provider map
──────────────────────────
  Add entries to BRAIN_PROVIDERS at import time:
    from src.config.roles import BRAIN_PROVIDERS
    BRAIN_PROVIDERS["my_provider"] = "myproject.brain.MyBrain"
"""
from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from src.config.schema import AgentConfig, RoleConfig

log = logging.getLogger(__name__)

BRAIN_PROVIDERS: dict[str, str] = {
    "ollama":  "src.plugins.builtin.ollama.brain.OllamaLLMBrain",
    "idle":    "src.plugins.builtin.idle_brain.brain.IdleBrain",
    "replay":  "src.plugins.builtin.replay_brain.brain.ReplayBrain",
}


def resolve_brain_class(role_cfg: "RoleConfig") -> tuple[str | None, dict[str, Any]]:
    """
    Return (brain_class_path, brain_kwargs) from a role's brain config.
    Returns (None, {}) when no brain is configured on the role.

    Raises ValueError for an unknown provider name.
    """
    if role_cfg.brain is None:
        return None, {}

    brain   = role_cfg.brain
    kwargs  = dict(brain.kwargs())

    if brain.class_:
        return brain.class_, kwargs

    if brain.provider:
        key = brain.provider.lower().strip()
        cls = BRAIN_PROVIDERS.get(key)
        if cls is None:
            raise ValueError(
                f"Unknown brain provider '{brain.provider}'. "
                f"Known providers: {sorted(BRAIN_PROVIDERS)}. "
                f"Use 'class: dotted.path' for a custom brain."
            )
        return cls, kwargs

    return None, {}


def expand_capabilities(a_cfg: "AgentConfig", role_cfg: "RoleConfig") -> frozenset[str]:
    """
    Return the union of the agent's explicit capabilities and the role's capabilities.
    Agent-level always takes precedence (by being included, not by overriding).
    """
    return frozenset(a_cfg.capabilities) | frozenset(role_cfg.capabilities)


def expand_brain_config(a_cfg: "AgentConfig", role_cfg: "RoleConfig") -> "ComponentConfig":  # noqa: F821
    """
    Return the effective brain ComponentConfig for an agent.

    Priority:
      1. Agent's explicit brain (if set)
      2. Role's brain (if role has one)
      3. Raises ValueError if neither is present

    Returns the agent's existing brain ComponentConfig unchanged if set.
    """
    from src.config.schema import ComponentConfig

    if a_cfg.brain is not None:
        return a_cfg.brain

    brain_class, brain_kwargs = resolve_brain_class(role_cfg)
    if brain_class is None:
        raise ValueError(
            f"Agent '{a_cfg.id}': no brain configured "
            f"(set brain: on the agent or assign a role with a brain: block)"
        )

    return ComponentConfig.model_validate({"class": brain_class, **brain_kwargs})


def export_roles_json(roles: dict[str, "RoleConfig"], path: str | Path) -> None:
    """
    Write a JSON sidecar for Unity editor/runtime validation.

    Produces an array of role objects (Unity-friendly; avoids dict serialization
    limitations in Unity's JsonUtility). Re-run after changing the roles: block.

    Example:
      from src.config.roles import export_roles_json
      export_roles_json(cfg.roles, "unity_sdk/Runtime/Resources/BiomataRoles.json")
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    role_list = []
    for name, rc in roles.items():
        brain_provider = None
        brain_class    = None
        if rc.brain is not None:
            brain_provider = rc.brain.provider or None
            brain_class    = rc.brain.class_   or None

        role_list.append({
            "name":           name,
            "capabilities":   list(rc.capabilities),
            "brain_provider": brain_provider,
            "brain_class":    brain_class,
        })

    payload: dict[str, Any] = {"version": "1", "roles": role_list}
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    log.info("Exported roles → %s  (%d role(s))", path, len(role_list))
