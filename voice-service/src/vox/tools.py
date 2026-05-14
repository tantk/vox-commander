"""Client-side tool handlers invoked by the ElevenLabs agent.

Each handler is registered as a SEPARATE tool with the agent so the LLM
sees a tight typed schema per action. Was a mega `dispatch_command(intent,
args)` tool — gpt-5-mini consistently called it with empty `args: {}` on
focus_fire and station_army because the args field was opaque. Splitting
into focused tools fixed that.

Every handler:
  - rewrites references through the dialogue context (when relevant)
  - short-circuits stateless intents through fastpath
  - sends a Command over the TCP socket and awaits the ack
  - returns a JSON STRING the Agent verbalises back to the user

ElevenLabs' ClientToolResult protocol requires the `result` field to be
a string, so every handler returns json.dumps(...) — never a raw dict.
"""
from __future__ import annotations
import asyncio
import json
import uuid

from .fastpath import build_command, is_stateless
from .game_socket import GameSocket
from .protocol import Command
from .refs import RefResolver


class Tools:
    def __init__(self, game: GameSocket, resolver: RefResolver):
        self.game = game
        self.resolver = resolver

    # ----- core send/await helper -----

    async def _send(self, intent: str, args: dict | None = None) -> str:
        args = args or {}
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
            return json.dumps({"ok": False, "error": "timeout"})
        return json.dumps({"ok": ack.ok, "error": ack.error})

    # ----- attack modes (no args — selection + targeting handled in trait) -----

    async def assault(self, _params: dict) -> str:
        return await self._send("assault")

    async def harass(self, _params: dict) -> str:
        return await self._send("harass")

    async def scout(self, _params: dict) -> str:
        return await self._send("scout")

    async def defend(self, _params: dict) -> str:
        return await self._send("defend")

    async def hold_position(self, _params: dict) -> str:
        return await self._send("hold_position")

    # ----- targeted attacks -----

    async def focus_fire(self, params: dict) -> str:
        label = (params.get("target_label") or "").strip()
        if not label:
            return json.dumps({"ok": False, "error": "missing_target_label"})
        return await self._send("focus_fire", {"target_label": label})

    async def attack_kind(self, params: dict) -> str:
        kind = (params.get("target_kind") or "").strip()
        if not kind:
            return json.dumps({"ok": False, "error": "missing_target_kind"})
        return await self._send("attack", {"target_kind": kind})

    # ----- station / movement -----

    async def station_army(self, params: dict) -> str:
        target = (params.get("target") or "").strip()
        if not target:
            return json.dumps({"ok": False, "error": "missing_target"})
        args = {"target": target}
        if "aggressive" in params:
            args["aggressive"] = bool(params["aggressive"])
        return await self._send("station_army", args)

    async def move_selection(self, params: dict) -> str:
        target = (params.get("target") or "").strip()
        if not target:
            return json.dumps({"ok": False, "error": "missing_target"})
        return await self._send("move", {"target": target})

    async def stop(self, _params: dict) -> str:
        return await self._send("stop")

    # ----- selection -----

    async def select_army(self, _params: dict) -> str:
        return await self._send("select_army")

    async def select(self, params: dict) -> str:
        return await self._send("select", {"filter": params.get("filter") or "all_units"})

    # ----- production -----

    async def build(self, params: dict) -> str:
        unit = (params.get("unit") or "").strip().lower()
        count = int(params.get("count") or 1)
        if not unit:
            return json.dumps({"ok": False, "error": "missing_unit"})
        return await self._send("build", {"unit": unit, "count": count})

    async def produce_structure(self, params: dict) -> str:
        structure = (params.get("structure") or "").strip().lower()
        if not structure:
            return json.dumps({"ok": False, "error": "missing_structure"})
        return await self._send("produce_structure", {"structure": structure})

    async def deploy(self, _params: dict) -> str:
        return await self._send("deploy")

    async def auto_mine(self, _params: dict) -> str:
        return await self._send("auto_mine")

    async def harvest(self, _params: dict) -> str:
        return await self._send("harvest")

    async def set_rally(self, params: dict) -> str:
        target = (params.get("target") or "").strip()
        if not target:
            return json.dumps({"ok": False, "error": "missing_target"})
        return await self._send("set_rally", {"target": target})

    # ----- meta -----

    async def read_state(self, params: dict) -> str:
        if not self.resolver.snapshots:
            return json.dumps({"ok": False, "error": "no_snapshot_yet"})
        snap = self.resolver.snapshots[-1]
        fields = params.get("fields") or ["cash", "units", "enemies"]
        return json.dumps({k: snap.get(k) for k in fields if k in snap})

    async def set_pause(self, params: dict) -> str:
        paused = params.get("paused")
        args = {} if paused is None else {"paused": bool(paused)}
        return await self._send("meta_pause", args)

    async def toggle_grid(self, params: dict) -> str:
        visible = params.get("visible")
        args = {} if visible is None else {"visible": bool(visible)}
        return await self._send("toggle_grid", args)

    async def toggle_labels(self, params: dict) -> str:
        visible = params.get("visible")
        args = {} if visible is None else {"visible": bool(visible)}
        return await self._send("toggle_labels", args)

    async def pan_camera(self, params: dict) -> str:
        target = (params.get("target") or "").strip()
        if not target:
            return json.dumps({"ok": False, "error": "missing_target"})
        return await self._send("pan_camera", {"target": target})

    async def airstrike(self, params: dict) -> str:
        target = (params.get("target") or "").strip()
        if not target:
            return json.dumps({"ok": False, "error": "missing_target"})
        return await self._send("airstrike", {"target": target})
