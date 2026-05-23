"""IdleBrain — zero-dependency brain that always returns 'idle'.

Useful for SDK smoke tests, transport-level integration tests, and CI runs
that need a registered agent but don't want to depend on Ollama / a replay file.
"""
from src.plugins.builtin.idle_brain.brain import IdleBrain

__all__ = ["IdleBrain"]
