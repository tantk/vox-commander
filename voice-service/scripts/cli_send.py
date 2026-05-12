"""Send a single command to a running OpenHV VoxBridge for manual testing.

Usage:
    python scripts/cli_send.py stop
    python scripts/cli_send.py move target=east_edge
    python scripts/cli_send.py select filter=all_units
"""
from __future__ import annotations
import asyncio
import sys
import uuid

from vox.game_socket import GameSocket
from vox.protocol import Command


async def main() -> int:
    if len(sys.argv) < 2:
        print("usage: cli_send.py <intent> [k=v ...]")
        return 2

    intent = sys.argv[1]
    args: dict[str, str | bool | int] = {}
    for raw in sys.argv[2:]:
        if "=" not in raw:
            continue
        k, v = raw.split("=", 1)
        if v.lower() in ("true", "false"):
            args[k] = v.lower() == "true"
        else:
            try:
                args[k] = int(v)
            except ValueError:
                args[k] = v

    gs = GameSocket("127.0.0.1", 7777, on_event=lambda e: print(f"[event] {e}"))
    await gs.connect()
    print(f"[send] intent={intent} args={args}")
    try:
        ack = await gs.send_and_await_ack(
            Command(id=str(uuid.uuid4()), intent=intent, args=args),
            timeout=2.0,
        )
        print(f"[ack] {ack}")
    except asyncio.TimeoutError:
        print("[ack] TIMEOUT")
        return 1
    finally:
        await asyncio.sleep(0.2)
        await gs.close()
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
