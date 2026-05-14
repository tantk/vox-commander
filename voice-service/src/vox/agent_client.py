"""ElevenLabs Conversational Agent client.

Owns the mic + speakers + LLM via the managed Agent. Registers three
client-side tools (dispatch_command, read_state, set_pause) whose handlers
live in `tools.py`. The session is blocking — run it from a worker thread.
"""
from __future__ import annotations
import asyncio
import os

from elevenlabs.client import ElevenLabs
from elevenlabs.conversational_ai.conversation import ClientTools, Conversation

from .audio import HardStopAudioInterface
from .tools import Tools


class AgentClient:
    def __init__(self, tools: Tools, loop: asyncio.AbstractEventLoop):
        self.tools = tools
        self.loop = loop
        self.conv: Conversation | None = None

    def start(self) -> None:
        api_key = os.environ["ELEVENLABS_API_KEY"]
        agent_id = os.environ["VOX_AGENT_ID"]
        client = ElevenLabs(api_key=api_key)

        client_tools = ClientTools(loop=self.loop)
        # Focused tools — one registration per intent. The agent's tool
        # schema is now per-action with typed args, so gpt-5-mini reliably
        # fills required fields (target / target_label / unit / etc.)
        # instead of sending {} on the old mega-tool.
        t = self.tools
        # Attack modes (no args)
        client_tools.register("assault",          t.assault,          is_async=True)
        client_tools.register("harass",           t.harass,           is_async=True)
        client_tools.register("scout",            t.scout,            is_async=True)
        client_tools.register("defend",           t.defend,           is_async=True)
        client_tools.register("hold_position",    t.hold_position,    is_async=True)
        # Targeted attacks
        client_tools.register("focus_fire",       t.focus_fire,       is_async=True)
        client_tools.register("attack_kind",      t.attack_kind,      is_async=True)
        # Movement
        client_tools.register("station_army",     t.station_army,     is_async=True)
        client_tools.register("move_selection",   t.move_selection,   is_async=True)
        client_tools.register("stop",             t.stop,             is_async=True)
        # Selection
        client_tools.register("select_army",      t.select_army,      is_async=True)
        client_tools.register("select",           t.select,           is_async=True)
        # Production
        client_tools.register("build",            t.build,            is_async=True)
        client_tools.register("produce_structure", t.produce_structure, is_async=True)
        client_tools.register("deploy",           t.deploy,           is_async=True)
        client_tools.register("auto_mine",        t.auto_mine,        is_async=True)
        client_tools.register("harvest",          t.harvest,          is_async=True)
        client_tools.register("set_rally",        t.set_rally,        is_async=True)
        # Meta
        client_tools.register("read_state",       t.read_state,       is_async=True)
        client_tools.register("set_pause",        t.set_pause,        is_async=True)
        client_tools.register("toggle_grid",      t.toggle_grid,      is_async=True)
        client_tools.register("toggle_labels",    t.toggle_labels,    is_async=True)
        client_tools.register("pan_camera",       t.pan_camera,       is_async=True)

        self.conv = Conversation(
            client,
            agent_id,
            requires_auth=True,
            audio_interface=HardStopAudioInterface(),
            client_tools=client_tools,
            callback_user_transcript=lambda t: print(f"[user] {t}"),
            callback_agent_response=lambda t: print(f"[xo]   {t}"),
            callback_agent_response_correction=lambda o, c: print(f"[xo*]  {o!r} -> {c!r}"),
            callback_latency_measurement=lambda ms: print(f"[lat]  {ms}ms"),
        )
        self.conv.start_session()  # blocks the calling thread

    def stop(self) -> None:
        if self.conv:
            try:
                self.conv.end_session()
                self.conv.wait_for_session_end()
            except Exception:
                pass
