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
from elevenlabs.conversational_ai.default_audio_interface import DefaultAudioInterface

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
        client_tools.register("dispatch_command", self.tools.dispatch_command, is_async=True)
        client_tools.register("read_state",       self.tools.read_state,       is_async=True)
        client_tools.register("set_pause",        self.tools.set_pause,        is_async=True)

        self.conv = Conversation(
            client,
            agent_id,
            requires_auth=True,
            audio_interface=DefaultAudioInterface(),
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
