"""Blocking, one-shot TCP client for the OpenHV VoxBridge.

Used by the panel's Quick Actions buttons. Opens a fresh connection per
call, sends one command, waits for its ack, closes. Plain blocking
sockets — no asyncio — so it's trivial to call from a tkinter button
handler running on a worker thread.
"""
from __future__ import annotations
import json
import socket
import time
import uuid


def send_command(
    intent: str,
    args: dict | None = None,
    host: str = "127.0.0.1",
    port: int = 47777,
    timeout: float = 2.0,
) -> dict:
    """Send one command, return the ack as a dict.

    Returns shape: {"ok": bool, "error": str | None}
    Errors:
      - "connect_refused" : bridge not listening (game not in match)
      - "timeout"         : ack didn't arrive in time
      - or whatever the trait returned in the ack
    """
    cmd_id = str(uuid.uuid4())
    payload = (
        json.dumps(
            {
                "type": "command",
                "id": cmd_id,
                "intent": intent,
                "args": args or {},
            },
            separators=(",", ":"),
        )
        + "\n"
    ).encode("utf-8")

    try:
        s = socket.create_connection((host, port), timeout=timeout)
    except (ConnectionRefusedError, OSError):
        return {"ok": False, "error": "connect_refused"}

    try:
        s.sendall(payload)
        buf = b""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            s.settimeout(max(0.05, deadline - time.monotonic()))
            try:
                chunk = s.recv(4096)
            except socket.timeout:
                break
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if not line.strip():
                    continue
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if msg.get("type") == "ack" and msg.get("id") == cmd_id:
                    return {"ok": bool(msg.get("ok")), "error": msg.get("error")}
        return {"ok": False, "error": "timeout"}
    finally:
        try:
            s.close()
        except OSError:
            pass
