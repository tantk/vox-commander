"""Provision the Vox Commander XO agent via the ElevenLabs API.

Posts the system prompt + three client-side tools, then writes the
resulting agent_id into the project .env so the orchestrator can connect.
"""
from __future__ import annotations
import json
import os
import re
import sys
from pathlib import Path

import httpx

SYSTEM_PROMPT = """\
You are the executive officer (XO) of a commander playing a real-time
strategy game called OpenHV. The commander gives you spoken orders. Your
job:

1. For tactical orders (move, attack, build, select, stop, harvest, pause,
   produce structures), call the `dispatch_command` tool with a concrete
   intent and args.
2. For state questions ("how much money", "where is the enemy", "how many
   units"), call `read_state` with the relevant fields, then verbalize the
   answer crisply.
3. Confirm orders concisely. "Tanks moving east." "Five rifles queued."
   Do not narrate the battle in detail — a commentator handles that.
4. If an order is ambiguous, ask for clarification in one short sentence.

Speak like a tactical officer: terse, calm, no fluff. Two short sentences
maximum per turn.

Valid intent values for dispatch_command:
- select       - args.filter: "all_units" or a unit kind like "rifleman"
- move         - args.target: "east_edge" | "west_edge" | "north_edge" | "south_edge" | "center"
- stop         - no args (halts current selection)
- attack       - args.target_ref: an enemy actor handle from a recent state_snapshot,
                 or args.target_kind: e.g. "rifleman" (resolves to closest enemy of that kind)
- attack_move  - same args as attack, but units engage along the way
- build        - args.unit: internal unit name e.g. "rifleman", args.count: integer
- produce_structure - args.structure: internal structure name
- harvest      - no args (sends current selection to harvest ore)
- meta_pause   - args.paused: true|false

Use exact string values. When in doubt, ask.
"""

PARAMS_DISPATCH = {
    "type": "object",
    "properties": {
        "intent": {
            "type": "string",
            "description": "Tactical intent — must be one of the documented values.",
        },
        "args": {
            "type": "object",
            "description": "Intent-specific arguments. See system prompt for shape.",
        },
    },
    "required": ["intent"],
}

PARAMS_READ_STATE = {
    "type": "object",
    "properties": {
        "fields": {
            "type": "array",
            "description": "Subset of state fields to return. Omit to return all.",
            "items": {
                "type": "string",
                "description": "A single state field name (e.g. cash, units, enemies).",
            },
        },
    },
}

PARAMS_SET_PAUSE = {
    "type": "object",
    "properties": {
        "paused": {
            "type": "boolean",
            "description": "True to pause the game, false to resume.",
        },
    },
    "required": ["paused"],
}

TOOLS = [
    {
        "type": "client",
        "name": "dispatch_command",
        "description": "Issue a tactical command to the game engine.",
        "parameters": PARAMS_DISPATCH,
        "expects_response": True,
        "pre_tool_speech": "off",
        "response_timeout_secs": 3,
    },
    {
        "type": "client",
        "name": "read_state",
        "description": "Read current game state — cash, unit counts, enemies visible.",
        "parameters": PARAMS_READ_STATE,
        "expects_response": True,
        "pre_tool_speech": "off",
        "response_timeout_secs": 3,
    },
    {
        "type": "client",
        "name": "set_pause",
        "description": "Pause or resume the game.",
        "parameters": PARAMS_SET_PAUSE,
        "expects_response": True,
        "pre_tool_speech": "off",
        "response_timeout_secs": 3,
    },
]

VOICE_ID_XO_ADAM = "pNInz6obpgDQGcFmaJgB"          # deep tactical male
VOICE_ID_COMMENTATOR_GEORGE = "JBFqnCBsd6RMkjVDRZzb"  # dramatic narrator

BODY = {
    "name": "Vox XO",
    "tags": ["vox-commander", "hackathon"],
    "conversation_config": {
        "tts": {
            "model_id": "eleven_flash_v2",
            "voice_id": VOICE_ID_XO_ADAM,
            "stability": 0.55,
            "speed": 1.05,
        },
        "turn": {
            "turn_timeout": 4,
            "turn_eagerness": "normal",
        },
        "conversation": {
            "max_duration_seconds": 1800,
        },
        "agent": {
            "first_message": "XO online. Awaiting orders, commander.",
            "language": "en",
            "prompt": {
                "llm": "gemini-2.5-flash",
                "prompt": SYSTEM_PROMPT,
                "temperature": 0.3,
                "tools": TOOLS,
            },
        },
    },
}


def main() -> int:
    api_key = os.environ.get("ELEVENLABS_API_KEY")
    if not api_key:
        env_path = Path(__file__).resolve().parents[2] / ".env"
        if env_path.exists():
            for line in env_path.read_text().splitlines():
                if line.startswith("ELEVENLABS_API_KEY="):
                    api_key = line.split("=", 1)[1].strip()
                    break
    if not api_key:
        print("ELEVENLABS_API_KEY not set; aborting.", file=sys.stderr)
        return 1

    url = "https://api.elevenlabs.io/v1/convai/agents/create"
    headers = {"xi-api-key": api_key, "Content-Type": "application/json"}

    print("[create-agent] POSTing agent config...")
    resp = httpx.post(url, headers=headers, json=BODY, timeout=30.0)
    if resp.status_code >= 400:
        print(f"[create-agent] HTTP {resp.status_code}: {resp.text}", file=sys.stderr)
        return 2

    data = resp.json()
    agent_id = data.get("agent_id") or data.get("id")
    if not agent_id:
        print(f"[create-agent] no agent_id in response: {data}", file=sys.stderr)
        return 3
    print(f"[create-agent] agent_id = {agent_id}")

    env_path = Path(__file__).resolve().parents[2] / ".env"
    text = env_path.read_text() if env_path.exists() else ""

    def upsert(key: str, value: str, src: str) -> str:
        pattern = rf"^{re.escape(key)}=.*$"
        if re.search(pattern, src, flags=re.MULTILINE):
            return re.sub(pattern, f"{key}={value}", src, flags=re.MULTILINE)
        return src + ("" if src.endswith("\n") or not src else "\n") + f"{key}={value}\n"

    text = upsert("VOX_AGENT_ID", agent_id, text)
    text = upsert("VOX_COMMENTATOR_VOICE_ID", VOICE_ID_COMMENTATOR_GEORGE, text)
    env_path.write_text(text)
    print(f"[create-agent] wrote VOX_AGENT_ID and VOX_COMMENTATOR_VOICE_ID to {env_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
