"""Dialogue-context reference resolution.

The Agent's LLM may produce semi-concrete references like "that_base" or
"harvester". We rewrite them into handles the C# trait knows how to look up.
"""
from __future__ import annotations
from collections import deque


class RefResolver:
    def __init__(self, history_size: int = 6):
        self.snapshots: deque[dict] = deque(maxlen=history_size)

    def ingest_snapshot(self, snap: dict) -> None:
        self.snapshots.append(snap)

    def rewrite(self, args: dict) -> dict:
        out = dict(args)
        if "target_ref" in out:
            out["target_ref"] = self._resolve_target_ref(out["target_ref"])
        if "target_kind" in out:
            out["target_kind"] = self._resolve_kind(out["target_kind"])
        return out

    def _resolve_target_ref(self, ref: str) -> str:
        if ref in ("that_base", "the_base", "their_base", "that"):
            for snap in reversed(self.snapshots):
                enemies = snap.get("enemies", [])
                bases = [e for e in enemies if e.get("kind") in ("barracks", "factory", "hq", "construction_yard")]
                if bases:
                    return bases[-1]["handle"]
            return "__ambiguous__"
        return ref

    def _resolve_kind(self, kind: str) -> str:
        return f"enemy_{kind}" if not kind.startswith(("enemy_", "friendly_")) else kind
