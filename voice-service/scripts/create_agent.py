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

def _empty():
    return {"type": "object", "properties": {}}


def _tool(name: str, desc: str, params=None, required=None):
    return {
        "type": "client",
        "name": name,
        "description": desc,
        "parameters": params or _empty(),
        "expects_response": True,
        "pre_tool_speech": "off",
        "response_timeout_secs": 3,
        **({"required": required} if required else {}),
    }


def _target_param():
    return {
        "type": "object",
        "properties": {
            "target": {
                "type": "string",
                "description": "Where to send units. Accepts: Battleship grid cells "
                "A1..F6 (e.g. 'B4'); map edges 'east_edge', 'west_edge', "
                "'north_edge', 'south_edge', 'center'; semantic 'base', "
                "'enemy_base', 'midpoint'; near-building 'near_storage', "
                "'near_factory', 'near_radar', 'near_outpost', 'near_module'.",
            },
        },
        "required": ["target"],
    }


TOOLS = [
    # Attack modes (no args)
    _tool("assault",       "Full army attack-move toward the enemy base centroid. Selects army automatically."),
    _tool("harass",        "Send 2-5 fast combat units to AttackMove the nearest visible enemy economy actor (miner / mining tower / storage / tanker)."),
    _tool("scout",         "Send the single fastest combat unit Move (no engagement) toward the enemy base centroid."),
    _tool("defend",        "Recall the full army back to our base centroid."),
    _tool("hold_position", "Stop the selected units and set their stance to Defend so they fire on threats in range but don't chase."),

    # Targeted attacks
    _tool("focus_fire",
        "Have all combat units in the current selection (or full army as fallback) concentrate fire on ONE named enemy actor.",
        params={
            "type": "object",
            "properties": {
                "target_label": {
                    "type": "string",
                    "description": "Friendly name of the enemy to focus, EXACTLY as labelled in-game with a hyphen — e.g. 'Tank-2', 'Bunker-1', 'Power-3', 'Rifle-4'. Never include spaces; always Prefix-Number.",
                },
            },
            "required": ["target_label"],
        }),
    _tool("attack_kind",
        "Attack the closest enemy of a given unit kind.",
        params={
            "type": "object",
            "properties": {
                "target_kind": {
                    "type": "string",
                    "description": "HV internal unit kind, e.g. 'rifleman', 'mbt', 'miner', 'storage', 'aatank'. Prefix with 'enemy_' to be explicit (default).",
                },
            },
            "required": ["target_kind"],
        }),

    # Movement
    _tool("station_army",
        "Select the full army (combat units only) and AttackMove them to a target location. The most common 'send the army to X' call.",
        params=_target_param()),
    _tool("move_selection",
        "Move the CURRENT selection to a target location (no auto-engagement). Use for relocating specific named units.",
        params=_target_param()),
    _tool("stop", "Stop the current selection's orders."),

    # Selection
    _tool("select_army", "Select all our combat-capable units. Call before any combat order that operates on selection."),
    _tool("select",
        "Select all our units of a given kind.",
        params={
            "type": "object",
            "properties": {
                "filter": {
                    "type": "string",
                    "description": "Unit kind to select: 'all_units' for everything, or a specific HV kind like 'mbt', 'rifleman', 'sniper', 'miner', 'builder'.",
                },
            },
        }),

    # Production
    _tool("build",
        "Queue combat units / vehicles / miners / builders in our factory or module.",
        params={
            "type": "object",
            "properties": {
                "unit": {
                    "type": "string",
                    "description": "HV unit internal name. Vehicles: 'mbt', 'aatank', 'apc', 'artillery', 'miner', 'builder'. Pods: 'rifleman', 'rocketeer', 'sniper', 'mortar', 'flamer', 'technician'.",
                },
                "count": {
                    "type": "integer",
                    "description": "How many to queue. Default 1.",
                },
            },
            "required": ["unit"],
        }),
    _tool("produce_structure",
        "Queue a building. Auto-places when production completes.",
        params={
            "type": "object",
            "properties": {
                "structure": {
                    "type": "string",
                    "description": "HV building internal name: 'generator', 'storage', 'module', 'factory', 'radar', 'techcenter', 'bunker', 'turret', 'aaturret'.",
                },
            },
            "required": ["structure"],
        }),
    _tool("deploy", "Issue DeployTransform on the current selection — turns a Builder into an Outpost."),
    _tool("auto_mine", "Send the currently selected miner(s) to the nearest unclaimed ore. Auto-fires on freshly-built miners — only call explicitly if a miner needs re-tasking."),
    _tool("harvest", "Send the current selection to harvest ore."),
    _tool("set_rally",
        "Set the rally point on all our production buildings. New units stream to this location.",
        params=_target_param()),

    # Meta
    _tool("read_state",
        "Read the latest game-state snapshot (cash, unit counts, visible enemies).",
        params={
            "type": "object",
            "properties": {
                "fields": {
                    "type": "array",
                    "description": "Subset of state fields to return.",
                    "items": {"type": "string", "description": "A field name e.g. 'cash', 'units', 'enemies'."},
                },
            },
        }),
    _tool("set_pause",
        "Pause or resume the game. Omit 'paused' to toggle.",
        params={
            "type": "object",
            "properties": {
                "paused": {
                    "type": "boolean",
                    "description": "True to pause, false to resume. Omit to toggle current state.",
                },
            },
        }),
    _tool("toggle_grid",
        "Show or hide the Battleship grid overlay (A1..F6). Omit 'visible' to toggle.",
        params={
            "type": "object",
            "properties": {
                "visible": {
                    "type": "boolean",
                    "description": "True to show, false to hide. Omit to toggle.",
                },
            },
        }),
    _tool("toggle_labels",
        "Show or hide the friendly-name labels under units. Omit 'visible' to toggle.",
        params={
            "type": "object",
            "properties": {
                "visible": {
                    "type": "boolean",
                    "description": "True to show, false to hide. Omit to toggle.",
                },
            },
        }),
    _tool("pan_camera",
        "Move the camera viewport so a target location is on screen. Use whenever the commander says 'look at', 'show me', 'pan to', 'go to', or asks about somewhere off-screen on a large map — fundamental to keyboard-and-mouse-free play.",
        params=_target_param()),
    _tool("airstrike",
        "Call in an airstrike (FlushBombers support power from the Radar Dome) on a target location. Requires a Radar building owned by the player. The power has a ~150s charge cooldown between uses; firing it before ready is a silent no-op (the in-game radar voice will announce when it's ready).",
        params=_target_param()),
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
            "turn_timeout": 15,
            "silence_end_call_timeout": 60,
            "turn_eagerness": "normal",
        },
        "conversation": {
            "max_duration_seconds": 1800,
        },
        "agent": {
            "first_message": "XO online. Awaiting orders, commander.",
            "language": "en",
            "prompt": {
                # gpt-5-mini: cost-effective + strong tool calling, per the
                # terminalmart project's validated config.
                "llm": "gpt-5-mini",
                "prompt": SYSTEM_PROMPT,
                # 0.2: prevents hallucinated past-tense tool calls.
                "temperature": 0.2,
                # CRITICAL for voice: keep reasoning out of the spoken
                # output channel. Per ElevenLabs docs, "none" is
                # recommended for conversational voice agents so the
                # model doesn't think out loud (which leaks into TTS).
                "reasoning_effort": "low",
                "tools": TOOLS,
            },
        },
    },
}


AGENT_NAME = BODY.get("name", "Vox XO")


def list_agents(api_key: str) -> list[dict]:
    """List all agents in the workspace, paginated."""
    headers = {"xi-api-key": api_key}
    agents: list[dict] = []
    cursor: str | None = None
    while True:
        params = {"page_size": 100}
        if cursor:
            params["cursor"] = cursor
        resp = httpx.get(
            "https://api.elevenlabs.io/v1/convai/agents",
            headers=headers, params=params, timeout=30.0,
        )
        if resp.status_code >= 400:
            print(f"[create-agent] list HTTP {resp.status_code}: {resp.text}", file=sys.stderr)
            return agents
        data = resp.json()
        agents.extend(data.get("agents") or [])
        cursor = data.get("next_cursor")
        if not cursor:
            break
    return agents


def delete_agent(api_key: str, agent_id: str) -> bool:
    headers = {"xi-api-key": api_key}
    resp = httpx.delete(
        f"https://api.elevenlabs.io/v1/convai/agents/{agent_id}",
        headers=headers, timeout=30.0,
    )
    if resp.status_code == 404:
        return True  # already gone, fine
    if resp.status_code >= 400:
        print(f"[create-agent] delete {agent_id} HTTP {resp.status_code}: {resp.text}", file=sys.stderr)
        return False
    return True


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

    # Idempotency: any pre-existing agent named AGENT_NAME gets deleted first
    # so we end up with exactly ONE Vox XO regardless of how many times this
    # script has been run.
    print(f"[create-agent] sweeping for existing '{AGENT_NAME}' agents...")
    existing = [a for a in list_agents(api_key) if a.get("name") == AGENT_NAME]
    if existing:
        for a in existing:
            aid = a.get("agent_id") or a.get("id")
            if not aid:
                continue
            ok = delete_agent(api_key, aid)
            print(f"[create-agent] deleted {aid} ({'ok' if ok else 'FAILED'})")
    else:
        print(f"[create-agent] no pre-existing '{AGENT_NAME}' found.")

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
