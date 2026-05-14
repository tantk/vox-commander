"""Per-session log archive.

On voice-service startup, opens a fresh `voice-service/sessions/<ts>/`
directory, tees stdout/stderr into `agent.log`, snapshots OpenHV's
debug.log line count, and stages a shutdown hook that writes only
the trait-log lines emitted during this session into `trait.log`.

The point is to be able to look back at any past run and answer
"what did the agent call, what did the trait do" — instead of
fishing through one giant debug.log that mixes every session.
"""
from __future__ import annotations
import atexit
import os
import sys
from datetime import datetime
from pathlib import Path


def _resolve_openra_log() -> Path:
    appdata = os.environ.get("APPDATA")
    if appdata:
        candidate = Path(appdata) / "OpenRA" / "Logs" / "debug.log"
        if candidate.exists():
            return candidate
    return Path.home() / "Documents" / "OpenRA" / "Logs" / "debug.log"


class _Tee:
    def __init__(self, *streams):
        self._streams = streams

    def write(self, data):
        for s in self._streams:
            try:
                s.write(data)
                s.flush()
            except Exception:
                pass

    def flush(self):
        for s in self._streams:
            try:
                s.flush()
            except Exception:
                pass


def start_session(project_root: Path) -> Path:
    """Open a timestamped session dir, tee stdout/stderr into it, and
    stage a shutdown hook that copies the OpenHV debug-log delta.

    Returns the absolute session directory path.
    """
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_dir = project_root / "voice-service" / "sessions" / ts
    session_dir.mkdir(parents=True, exist_ok=True)

    # Snapshot how many lines are already in OpenHV's debug log so the
    # shutdown hook can capture only what THIS session produced.
    openra_log = _resolve_openra_log()
    start_lines = 0
    if openra_log.exists():
        try:
            with openra_log.open("r", encoding="utf-8", errors="replace") as f:
                for _ in f:
                    start_lines += 1
        except OSError:
            start_lines = 0

    agent_log = (session_dir / "agent.log").open("w", encoding="utf-8")
    sys.stdout = _Tee(sys.__stdout__, agent_log)
    sys.stderr = _Tee(sys.__stderr__, agent_log)

    def _on_shutdown():
        try:
            if openra_log.exists():
                with openra_log.open("r", encoding="utf-8", errors="replace") as f:
                    lines = list(f)
                delta = lines[start_lines:]
                (session_dir / "trait.log").write_text("".join(delta), encoding="utf-8")
        except Exception as exc:
            try:
                agent_log.write(f"[session-log] trait copy failed: {exc}\n")
            except Exception:
                pass
        try:
            agent_log.flush()
            agent_log.close()
        except Exception:
            pass

    atexit.register(_on_shutdown)

    print(f"[session] logging to {session_dir}")
    return session_dir
