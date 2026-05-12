"""Wire protocol for the Vox Commander bridge socket.

Pure data structures and (de)serialization. No I/O.
"""
from __future__ import annotations
import json
from dataclasses import dataclass, field
from typing import Any, Union


@dataclass
class Command:
    id: str
    intent: str
    args: dict[str, Any] = field(default_factory=dict)


@dataclass
class Event:
    kind: str
    ts: int
    payload: dict[str, Any] = field(default_factory=dict)


@dataclass
class Ack:
    id: str
    ok: bool
    error: str | None = None


Message = Union[Command, Event, Ack]


def encode_command(cmd: Command) -> str:
    obj = {"type": "command", "id": cmd.id, "intent": cmd.intent, "args": cmd.args}
    return json.dumps(obj, separators=(",", ":")) + "\n"


def decode_message(raw: str) -> Message:
    obj = json.loads(raw)
    t = obj.get("type")
    if t == "command":
        return Command(id=obj["id"], intent=obj["intent"], args=obj.get("args") or {})
    if t == "event":
        payload = {k: v for k, v in obj.items() if k not in ("type", "kind", "ts")}
        return Event(kind=obj["kind"], ts=obj["ts"], payload=payload)
    if t == "ack":
        return Ack(id=obj["id"], ok=bool(obj["ok"]), error=obj.get("error"))
    raise ValueError(f"unknown message type: {t!r}")
