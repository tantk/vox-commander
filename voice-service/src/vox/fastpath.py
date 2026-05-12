"""Stateless-command short-circuit.

For intents that need no world-state lookup or reference resolution, skip the
resolver and pre-build the Command so dispatch is a single async write.
"""
from __future__ import annotations
import uuid
from .protocol import Command

STATELESS = {"stop", "hold", "meta_pause"}
ALIASES = {"hold": "stop"}  # spoken "hold" maps to game intent "stop"


def is_stateless(intent: str) -> bool:
    return intent in STATELESS


def build_command(intent: str, args: dict) -> Command:
    real = ALIASES.get(intent, intent)
    return Command(id=str(uuid.uuid4()), intent=real, args=dict(args or {}))
