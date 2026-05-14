"""Generate the commander's voice lines for the demo video via ElevenLabs TTS.

Outputs an MP3 per line into voice-service/commander_audio/, numbered in
demo-script order. Play them in sequence during recording (either through
speakers near your mic, or via a virtual audio cable into mic input for
a cleaner capture).

Voice: Bill (mature, gravelly, military commander vibe) — deliberately
distinct from the three in-game voices (Adam=XO, George=commentator,
Brian=intel).
"""
from __future__ import annotations
import os
import sys
from pathlib import Path

from elevenlabs.client import ElevenLabs


# Bill — mature, gravelly, "senior commander" timbre
COMMANDER_VOICE_ID = "pqHfZKP75CvOlQylNhV4"
TTS_MODEL = "eleven_flash_v2"


# 14 commander lines from docs/DEMO_VIDEO_SCRIPT.md (Segment B, beats 1-9).
# Numbered for playback order. Filename suffix is a slug for quick reference.
LINES: list[tuple[str, str]] = [
    ("01_status_report",      "XO, status report."),
    ("02_build_units",        "Build me ten tanks. Train five rocketeers."),
    ("03_pan_to_enemy",       "Pan to the enemy base."),
    ("04_airstrike",          "Airstrike on E5."),
    ("05_hold_position",      "Snipers and rocketeers to C3. Hold position."),
    ("06_focus_fire",         "Focus fire on Bunker-1."),
    ("07_full_assault",       "Full assault. Send everything south."),
    ("08_pull_back",          "Pull back! Defend the base!"),
    ("09_rally_d4",           "Train ten more tanks. Rally them to D4."),
    ("10_final_assault",      "Final assault. End it."),
    # Optional spares — handy if you want alternate takes mid-record
    ("11_set_up_base",        "Set up the base. Standard opening."),
    ("12_select_army",        "Select my army."),
    ("13_show_grid",          "Show me the grid."),
    ("14_pause_resume",       "Pause the game."),
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

    out_dir = project_root / "voice-service" / "commander_audio"
    out_dir.mkdir(parents=True, exist_ok=True)

    client = ElevenLabs(api_key=api_key)

    for slug, text in LINES:
        out_path = out_dir / f"{slug}.mp3"
        if out_path.exists():
            print(f"[commander-audio] {out_path.name} already exists; skipping")
            continue
        print(f"[commander-audio] generating {out_path.name}: {text!r}")
        try:
            audio = client.text_to_speech.convert(
                voice_id=COMMANDER_VOICE_ID,
                text=text,
                model_id=TTS_MODEL,
                # Slightly more variance for cinematic delivery — these are
                # one-shot reads, not live conversation, so we can afford it.
                voice_settings={"stability": 0.45, "similarity_boost": 0.75},
            )
            # The SDK returns a generator of audio chunks; concatenate.
            data = b"".join(chunk for chunk in audio)
            out_path.write_bytes(data)
        except Exception as exc:
            print(f"[commander-audio] {slug} FAILED: {exc}", file=sys.stderr)

    print(f"[commander-audio] done. Files in {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
