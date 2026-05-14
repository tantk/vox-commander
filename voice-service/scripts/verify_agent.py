"""Fetch the live agent config and print the fields that affect reasoning leaks.

Lets us confirm what's actually deployed (vs what's in our local script files).
"""
from __future__ import annotations
import json
import os
import sys
from pathlib import Path

import httpx


def read_env() -> dict[str, str]:
    out: dict[str, str] = {}
    env = Path(__file__).resolve().parents[2] / ".env"
    if not env.exists():
        return out
    for line in env.read_text().splitlines():
        if "=" in line and not line.strip().startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def main() -> int:
    env = read_env()
    api_key = env.get("ELEVENLABS_API_KEY") or os.environ.get("ELEVENLABS_API_KEY")
    agent_id = env.get("VOX_AGENT_ID") or os.environ.get("VOX_AGENT_ID")
    if not api_key or not agent_id:
        print("ELEVENLABS_API_KEY and VOX_AGENT_ID must be set.", file=sys.stderr)
        return 1

    url = f"https://api.elevenlabs.io/v1/convai/agents/{agent_id}"
    resp = httpx.get(url, headers={"xi-api-key": api_key}, timeout=30.0)
    if resp.status_code >= 400:
        print(f"HTTP {resp.status_code}: {resp.text}", file=sys.stderr)
        return 2

    data = resp.json()
    cc = data.get("conversation_config", {})
    agent = cc.get("agent", {})
    prompt = agent.get("prompt", {})
    turn = cc.get("turn", {})

    fields = {
        "llm": prompt.get("llm"),
        "temperature": prompt.get("temperature"),
        "reasoning_effort": prompt.get("reasoning_effort"),
        "tool count": len(prompt.get("tools") or prompt.get("tool_ids") or []),
        "system prompt length (chars)": len(prompt.get("prompt") or ""),
        "turn_eagerness": turn.get("turn_eagerness"),
        "turn_timeout": turn.get("turn_timeout"),
        "silence_end_call_timeout": turn.get("silence_end_call_timeout"),
    }
    print(f"agent_id: {agent_id}")
    print(f"name: {data.get('name')}")
    print("---")
    for k, v in fields.items():
        print(f"{k:30s}  {v}")
    # Sanity check: search the system prompt for the rule.
    sysp = prompt.get("prompt") or ""
    rule_present = "narrate your own reasoning" in sysp.lower() or "never narrate" in sysp.lower()
    print(f"{'prompt rule present':30s}  {rule_present}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
