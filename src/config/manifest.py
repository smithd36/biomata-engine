"""
src/config/manifest.py
────────────────────────────
ActionManifest — loads simulation/actions.yaml and produces ActionSchema objects.

The manifest is the shared source of truth for action names, descriptions,
parameters, required capabilities, and Unity engine command hints.  Python uses
it to reduce registry boilerplate; Unity uses the exported JSON sidecar to
validate ActionHandlerBase coverage at editor time.

Usage — Python
──────────────
  from src.config.manifest import ActionManifest

  manifest = ActionManifest.load("simulation/actions.yaml")

  # Build a registry by supplying handlers
  registry = manifest.build_registry({
      "idle":    IdleHandler(),
      "patrol":  PatrolHandler(),
      "alert":   AlertHandler(),
  })

  # Or look up schemas for manual registration
  registry = ActionRegistry()
  registry.register(manifest.schema("patrol"), PatrolHandler())

  # Export JSON for Unity (run once after editing actions.yaml)
  manifest.export_json("unity_sdk/Runtime/Resources/BiomataActions.json")

Usage — sim.yaml
────────────────
  registry:
    class: myproject.registry.build_registry
    manifest: simulation/actions.yaml

  The loader detects the manifest key, loads it, and passes it as
  `manifest=ActionManifest(...)` to the registry factory function.
  The factory can then call manifest.schema("patrol") instead of
  constructing ActionSchema objects by hand.

Backwards compatibility
────────────────────────
  Existing manual ActionRegistry factories continue to work unchanged.
  The manifest is an optional supplement, not a replacement.
"""
from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import yaml

from src.contracts.action import ActionHint, ActionHandler, ActionSchema
from src.engine.registry import ActionRegistry

log = logging.getLogger(__name__)

_HINT_MAP: dict[str, ActionHint] = {
    "host":   ActionHint.HOST,
    "engine": ActionHint.ENGINE,
    "hybrid": ActionHint.HYBRID,
}


class ManifestValidationError(ValueError):
    """Raised when actions.yaml fails structural or semantic validation."""


class ActionManifest:
    """
    Loaded and validated action manifest.

    Vends ActionSchema objects and can build an ActionRegistry.
    Immutable after construction — create a new instance to reflect changes.
    """

    def __init__(self, schemas: list[ActionSchema], source_path: Path | None = None):
        self._schemas: dict[str, ActionSchema] = {s.name: s for s in schemas}
        self.source_path = source_path

    # ── Construction ──────────────────────────────────────────────────────────

    @classmethod
    def load(cls, path: str | Path) -> "ActionManifest":
        """Load and validate an actions.yaml file. Raises ManifestValidationError on failure."""
        path = Path(path)
        if not path.exists():
            raise FileNotFoundError(f"Action manifest not found: {path}")

        with path.open("r", encoding="utf-8") as f:
            data = yaml.safe_load(f)

        if not isinstance(data, dict):
            raise ManifestValidationError(f"{path}: expected a YAML mapping at root")
        if "actions" not in data:
            raise ManifestValidationError(f"{path}: missing required top-level key 'actions'")

        return cls._parse(data["actions"], source_path=path)

    @classmethod
    def _parse(cls, raw: list[Any], source_path: Path | None = None) -> "ActionManifest":
        if not isinstance(raw, list):
            raise ManifestValidationError("'actions' must be a YAML sequence (list)")

        seen:    set[str]           = set()
        schemas: list[ActionSchema] = []

        for i, entry in enumerate(raw):
            if not isinstance(entry, dict):
                raise ManifestValidationError(f"actions[{i}]: must be a mapping")

            name = entry.get("name")
            if not name or not isinstance(name, str):
                raise ManifestValidationError(
                    f"actions[{i}]: 'name' is required and must be a non-empty string"
                )
            name = name.strip()
            if name in seen:
                raise ManifestValidationError(f"duplicate action name '{name}'")
            seen.add(name)

            description       = str(entry.get("description") or "")
            parameters_schema = dict(entry.get("parameters") or {})
            raw_caps          = entry.get("required_capabilities") or []
            required_caps     = frozenset(str(c) for c in raw_caps)
            kind_str          = str(entry.get("kind") or "hybrid").lower().strip()

            if kind_str not in _HINT_MAP:
                raise ManifestValidationError(
                    f"action '{name}': 'kind' must be one of {sorted(_HINT_MAP)}, got '{kind_str}'"
                )

            schemas.append(ActionSchema(
                name=name,
                description=description,
                parameters_schema=parameters_schema,
                execution_hint=_HINT_MAP[kind_str],
                required_capabilities=required_caps,
            ))

        return cls(schemas, source_path=source_path)

    # ── Schema access ─────────────────────────────────────────────────────────

    def schema(self, name: str) -> ActionSchema:
        """Return the ActionSchema for an action. Raises KeyError if not declared."""
        try:
            return self._schemas[name]
        except KeyError:
            src = f" ({self.source_path})" if self.source_path else ""
            raise KeyError(f"action '{name}' is not declared in the manifest{src}") from None

    def schemas(self) -> list[ActionSchema]:
        """All ActionSchema objects, in declaration order."""
        return list(self._schemas.values())

    def names(self) -> list[str]:
        """All declared action names, in declaration order."""
        return list(self._schemas.keys())

    def __len__(self) -> int:
        return len(self._schemas)

    def __contains__(self, name: str) -> bool:
        return name in self._schemas

    # ── Registry builder ──────────────────────────────────────────────────────

    def build_registry(self, handlers: dict[str, ActionHandler]) -> ActionRegistry:
        """
        Build an ActionRegistry from this manifest and a name → handler mapping.

          - Actions in the manifest with a matching handler are registered.
          - Actions in the manifest without a handler are skipped with a warning.
          - Handler names not in the manifest raise ManifestValidationError —
            this catches typos before they become silent runtime failures.

        Example
        -------
          registry = manifest.build_registry({
              "idle":    IdleHandler(),
              "patrol":  PatrolHandler(),
              "alert":   AlertHandler(),
          })
        """
        undeclared = set(handlers) - set(self._schemas)
        if undeclared:
            src = self.source_path or "actions.yaml"
            raise ManifestValidationError(
                f"handlers reference action(s) not declared in manifest: {sorted(undeclared)}\n"
                f"Check for typos or add the missing action(s) to {src}."
            )

        registry = ActionRegistry()
        for name, schema in self._schemas.items():
            handler = handlers.get(name)
            if handler is None:
                log.warning(
                    "manifest action '%s' has no handler — skipped (agents cannot execute it)",
                    name,
                )
                continue
            registry.register(schema, handler)

        return registry

    # ── JSON export (for Unity) ───────────────────────────────────────────────

    def export_json(self, path: str | Path) -> None:
        """
        Write a JSON sidecar for Unity editor/runtime validation.

        Run once after editing actions.yaml:
          manifest.export_json("unity_sdk/Runtime/Resources/BiomataActions.json")

        Commit the generated file alongside your Unity project.
        Unity reads it via Resources.Load<TextAsset>("BiomataActions").
        """
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)

        payload: dict[str, Any] = {
            "version": "1",
            "actions": [
                {
                    "name":                  s.name,
                    "description":           s.description,
                    "parameters":            s.parameters_schema,
                    "required_capabilities": sorted(s.required_capabilities),
                    "execution_hint":        s.execution_hint.value,
                }
                for s in self._schemas.values()
            ],
        }
        path.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
        log.info("Exported manifest → %s  (%d action(s))", path, len(self._schemas))
