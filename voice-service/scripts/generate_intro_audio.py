"""Generate the project intro voiceover.

A single ~6-7s line played at the start of the demo video, narrated by
George (dramatic narrator) — same voice as the in-game commentator, so
the intro and the live event commentary feel like the same persona.
"""
from __future__ import annotations
import os
import sys
from pathlib import Path

from elevenlabs.client import ElevenLabs


NARRATOR_VOICE_ID = "JBFqnCBsd6RMkjVDRZzb"  # George — dramatic narrator
TTS_MODEL = "eleven_flash_v2"


LINES: list[tuple[str, str]] = [
    ("00_intro",
     "Vox Commander. A hands-free real-time strategy game. "
     "No keyboard. No mouse. Just your voice."),
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

    out_dir = project_root / "voice-service" / "intro_audio"
    out_dir.mkdir(parents=True, exist_ok=True)

    client = ElevenLabs(api_key=api_key)

    for slug, text in LINES:
        out_path = out_dir / f"{slug}.mp3"
        print(f"[intro-audio] generating {out_path.name}: {text!r}")
        try:
            audio = client.text_to_speech.convert(
                voice_id=NARRATOR_VOICE_ID,
                text=text,
                model_id=TTS_MODEL,
                # Slight slowdown + extra stability — narrator energy, not
                # broadcast pace. similarity_boost stays high to keep
                # George's signature timbre intact across regenerations.
                voice_settings={"stability": 0.65, "similarity_boost": 0.80, "speed": 0.95},
            )
            data = b"".join(chunk for chunk in audio)
            out_path.write_bytes(data)
        except Exception as exc:
            print(f"[intro-audio] {slug} FAILED: {exc}", file=sys.stderr)
            return 1

    print(f"[intro-audio] done. Files in {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
