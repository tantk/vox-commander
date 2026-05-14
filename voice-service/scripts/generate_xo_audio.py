"""Generate XO response voice lines that pair with the demo commander audio.

Each MP3 plays automatically ~2 seconds after its matching commander
line so the recording captures a complete commander → XO exchange
without the live agent needing to be in the loop.

Voice: Adam (pNInz6obpgDQGcFmaJgB) — same voice the live agent uses,
so a viewer can't tell pre-canned demo audio from a live agent run.
"""
from __future__ import annotations
import os
import sys
from pathlib import Path

from elevenlabs.client import ElevenLabs


XO_VOICE_ID = "pNInz6obpgDQGcFmaJgB"  # Adam — same as the live XO voice
TTS_MODEL = "eleven_flash_v2"


# Slug matches the commander_audio file slug; the demo button looks up
# xo_audio/<slug>.mp3 next to commander_audio/<slug>.mp3.
LINES: list[tuple[str, str]] = [
    ("01_status_report",  "Twenty thousand credits. Full force standing by."),
    ("02_build_units",    "Ten tanks queued. Five rocketeers queued."),
    ("03_pan_to_enemy",   "Panning to enemy base."),
    ("04_airstrike",      "Airstrike inbound. Bombers on the enemy base."),
    ("05_hold_position",  "Army moving to C3. Holding position."),
    ("06_focus_fire",     "Concentrating fire on Bunker-1."),
    ("07_full_assault",   "Assaulting, commander."),
    ("08_pull_back",      "Falling back. Defending the base."),
    ("09_rally_d4",       "Ten tanks queued. Rallying to D4."),
    ("10_final_assault",  "Final push. Ending it."),
    ("11_set_up_base",    "Base online. Production queued."),
    ("12_select_army",    "Army selected, commander."),
    ("13_show_grid",      "Grid visible."),
    ("14_pause_resume",   "Game paused. Standing by."),
]


def main() -> int:
    project_root = Path(__file__).resolve().parents[2]
    env_path = project_root / ".env"
    api_key = os.environ.get("ELEVENLABS_API_KEY")
    if not api_key and env_path.exists():
        for line in env_path.read_text().splitlines():
            if line.startswith("ELEVENLABS_API_KEY="):
                api_key = line.split("=", 1)[1].strip()
                break
    if not api_key:
        print("ELEVENLABS_API_KEY not set.", file=sys.stderr)
        return 1

    out_dir = project_root / "voice-service" / "xo_audio"
    out_dir.mkdir(parents=True, exist_ok=True)

    client = ElevenLabs(api_key=api_key)

    for slug, text in LINES:
        out_path = out_dir / f"{slug}.mp3"
        if out_path.exists():
            print(f"[xo-audio] {out_path.name} already exists; skipping")
            continue
        print(f"[xo-audio] generating {out_path.name}: {text!r}")
        try:
            audio = client.text_to_speech.convert(
                voice_id=XO_VOICE_ID,
                text=text,
                model_id=TTS_MODEL,
                voice_settings={"stability": 0.55, "similarity_boost": 0.75, "speed": 1.05},
            )
            data = b"".join(chunk for chunk in audio)
            out_path.write_bytes(data)
        except Exception as exc:
            print(f"[xo-audio] {slug} FAILED: {exc}", file=sys.stderr)

    print(f"[xo-audio] done. Files in {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
