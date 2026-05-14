"""Vox Commander orchestrator. Wires Agent + GameSocket + Commentator together."""
from __future__ import annotations
import asyncio
import os
import signal
import threading

from dotenv import load_dotenv
from elevenlabs.client import ElevenLabs
from elevenlabs.play import play  # NOTE: `from elevenlabs import play` imports the module, not the fn

from .agent_client import AgentClient
from .commentator import Commentator, Intel
from .game_socket import GameSocket
from .protocol import Event
from .refs import RefResolver
from .session_log import start_session
from .tools import Tools


def _make_tts_speak(env_var: str, channel: str) -> callable:
    """Build a TTS-speak function bound to one ElevenLabs voice.

    Failures are logged but don't propagate — broken audio must not take
    down the command loop.
    """
    api_key = os.environ.get("ELEVENLABS_API_KEY")
    voice_id = os.environ.get(env_var)
    if not api_key or not voice_id:
        def disabled(text: str) -> None:
            print(f"[{channel}-disabled] {text}")
        return disabled

    client = ElevenLabs(api_key=api_key)

    def speak(text: str) -> None:
        print(f"[{channel}] {text}")
        try:
            audio = client.text_to_speech.convert(
                voice_id=voice_id,
                text=text,
                model_id="eleven_flash_v2",
            )
            play(audio)
        except Exception as exc:
            print(f"[{channel}-error] {exc}")

    return speak


def make_commentator_speak() -> callable:
    return _make_tts_speak("VOX_COMMENTATOR_VOICE_ID", "commentator")


def make_intel_speak() -> callable:
    return _make_tts_speak("VOX_INTEL_VOICE_ID", "intel")


async def amain() -> None:
    project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    load_dotenv(dotenv_path=os.path.join(project_root, ".env"))
    start_session(__import__("pathlib").Path(project_root))

    host = os.environ.get("VOX_BRIDGE_HOST", "127.0.0.1")
    port = int(os.environ.get("VOX_BRIDGE_PORT", "47777"))

    resolver = RefResolver()
    commentator = Commentator(make_commentator_speak())
    intel = Intel(make_intel_speak())

    def on_event(event: Event) -> None:
        if event.kind == "state_snapshot":
            resolver.ingest_snapshot({"kind": "state_snapshot", **event.payload})
        commentator.handle(event)
        intel.handle(event)

    game = GameSocket(host, port, on_event=on_event)
    print(f"[main] connecting to bridge {host}:{port} (will wait up to 120s for the game) ...")
    await game.connect(retry_interval=2.0, max_wait=120.0)
    print("[main] bridge connected")

    tools = Tools(game, resolver)
    loop = asyncio.get_running_loop()
    agent = AgentClient(tools, loop)

    stop_event = asyncio.Event()

    def _on_signal(*_) -> None:
        stop_event.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            signal.signal(sig, _on_signal)
        except (ValueError, OSError):
            pass  # not main thread or unsupported signal on Windows

    if not os.environ.get("VOX_AGENT_ID"):
        print("[main] VOX_AGENT_ID not set — running in headless mode (no voice).")
        print("[main] Use scripts/cli_send.py to test commands manually.")
        await stop_event.wait()
    else:
        agent_thread = threading.Thread(
            target=agent.start, daemon=True, name="agent-session"
        )
        agent_thread.start()
        print("[main] agent session started; speak to the XO")
        await stop_event.wait()
        print("[main] shutting down")
        agent.stop()

    await game.close()


def main() -> None:
    try:
        asyncio.run(amain())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
