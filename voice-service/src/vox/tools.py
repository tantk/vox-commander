"""Client-side tool handlers invoked by the ElevenLabs agent.

These are the bridge between the Agent's tool calls and the C# VoxBridge trait.
Each handler:
  - rewrites references through the dialogue context (when relevant)
  - short-circuits stateless intents through fastpath
  - sends a Command over the TCP socket and awaits the ack
  - returns a dict the Agent verbalizes back to the user
"""
from __future__ import annotations
import asyncio
import uuid

from .fastpath import build_command, is_stateless
from .game_socket import GameSocket
from .protocol import Command
from .refs import RefResolver


class Tools:
    def __init__(self, game: GameSocket, resolver: RefResolver):
        self.game = game
        self.resolver = resolver

    async def dispatch_command(self, params: dict) -> dict:
        intent = params.get("intent", "")
        args = params.get("args") or {}

        if is_stateless(intent):
            cmd = build_command(intent, args)
        else:
            cmd = Command(
                id=str(uuid.uuid4()),
                intent=intent,
                args=self.resolver.rewrite(args),
            )

        try:
            ack = await self.game.send_and_await_ack(cmd, timeout=2.0)
        except asyncio.TimeoutError:
            return {"ok": False, "error": "timeout"}
        return {"ok": ack.ok, "error": ack.error}

    async def read_state(self, params: dict) -> dict:
        if not self.resolver.snapshots:
            return {"ok": False, "error": "no_snapshot_yet"}
        snap = self.resolver.snapshots[-1]
        fields = params.get("fields") or ["cash", "units", "enemies"]
        return {k: snap.get(k) for k in fields if k in snap}

    async def set_pause(self, params: dict) -> dict:
        return await self.dispatch_command(
            {"intent": "meta_pause", "args": {"paused": bool(params.get("paused", False))}}
        )
