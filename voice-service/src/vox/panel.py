"""Vox Commander voice-control panel.

A small tkinter window that:
  - shows the saved ElevenLabs API key (masked) and lets you change it
  - shows a big toggle button to activate / deactivate the voice service
  - starts/stops the voice service as a child Python process

Launched automatically by the OpenHV VoxBridge trait when a match starts.
Can also be run manually: `python -m vox.panel`
"""
from __future__ import annotations
import os
import re
import subprocess
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import font as tkfont

from .sync_bridge import send_command

ROOT = Path(__file__).resolve().parents[3]      # project root (C:\dev\elevenhack\cursor)
ENV_PATH = ROOT / ".env"
VOX_PY = ROOT / "voice-service" / ".venv" / "Scripts" / "python.exe"


# ---------------- .env I/O ----------------

def read_env() -> dict[str, str]:
    if not ENV_PATH.exists():
        return {}
    out: dict[str, str] = {}
    for line in ENV_PATH.read_text().splitlines():
        if "=" in line and not line.strip().startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def upsert_env(updates: dict[str, str]) -> None:
    text = ENV_PATH.read_text() if ENV_PATH.exists() else ""
    for key, value in updates.items():
        pattern = rf"^{re.escape(key)}=.*$"
        if re.search(pattern, text, flags=re.MULTILINE):
            text = re.sub(pattern, f"{key}={value}", text, flags=re.MULTILINE)
        else:
            if text and not text.endswith("\n"):
                text += "\n"
            text += f"{key}={value}\n"
    ENV_PATH.write_text(text)


def mask(value: str) -> str:
    if not value:
        return ""
    if len(value) <= 8:
        return "●" * len(value)
    return "●" * (len(value) - 6) + value[-6:]


# ---------------- voice service supervisor ----------------

class ServiceSupervisor:
    def __init__(self):
        self.proc: subprocess.Popen | None = None
        self._stdout_thread: threading.Thread | None = None
        self.log_tail: list[str] = []
        self._on_log: callable = lambda line: None
        self._on_exit: callable = lambda: None

    def is_running(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def start(self, on_log, on_exit) -> None:
        if self.is_running():
            return
        self._on_log = on_log
        self._on_exit = on_exit
        env = os.environ.copy()
        env["PYTHONUNBUFFERED"] = "1"
        self.proc = subprocess.Popen(
            [str(VOX_PY), "-u", "-m", "vox.main"],
            cwd=str(ROOT / "voice-service"),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        self._stdout_thread = threading.Thread(target=self._reader, daemon=True)
        self._stdout_thread.start()

    def stop(self) -> None:
        if not self.is_running():
            return
        try:
            self.proc.terminate()
            self.proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        except Exception:
            pass

    def _reader(self) -> None:
        assert self.proc is not None
        for line in self.proc.stdout:
            line = line.rstrip()
            self.log_tail.append(line)
            self.log_tail = self.log_tail[-200:]
            try:
                self._on_log(line)
            except Exception:
                pass
        try:
            self._on_exit()
        except Exception:
            pass


# ---------------- UI ----------------

BG = "#0d1117"
FG = "#e6edf3"
ACCENT = "#3b82f6"
ACCENT_ACTIVE = "#22c55e"
ACCENT_DANGER = "#ef4444"
MUTED = "#6e7681"
PANEL = "#161b22"
BORDER = "#30363d"


class Panel:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.supervisor = ServiceSupervisor()
        self.env = read_env()

        root.title("Vox Commander")
        root.configure(bg=BG)
        root.geometry("540x720")
        root.minsize(480, 480)
        try:
            root.attributes("-topmost", True)
            root.after(800, lambda: root.attributes("-topmost", False))
        except Exception:
            pass

        title_font = tkfont.Font(family="Segoe UI", size=18, weight="bold")
        body_font = tkfont.Font(family="Segoe UI", size=10)
        mono_font = tkfont.Font(family="Consolas", size=9)
        button_font = tkfont.Font(family="Segoe UI", size=14, weight="bold")

        # ----- scrollable container -----
        outer = tk.Frame(root, bg=BG)
        outer.pack(fill="both", expand=True)

        canvas = tk.Canvas(outer, bg=BG, highlightthickness=0)
        scrollbar = tk.Scrollbar(outer, orient="vertical", command=canvas.yview, bg=BG, troughcolor=PANEL)
        canvas.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)

        body = tk.Frame(canvas, bg=BG)
        body_window = canvas.create_window((0, 0), window=body, anchor="nw")

        def _on_body_configure(_evt):
            canvas.configure(scrollregion=canvas.bbox("all"))
        body.bind("<Configure>", _on_body_configure)

        def _on_canvas_configure(evt):
            canvas.itemconfig(body_window, width=evt.width)
        canvas.bind("<Configure>", _on_canvas_configure)

        def _on_mousewheel(evt):
            canvas.yview_scroll(int(-1 * (evt.delta / 120)), "units")
        canvas.bind_all("<MouseWheel>", _on_mousewheel)

        self._canvas = canvas
        self._scroll_unbind_target = canvas

        # ----- header -----
        header = tk.Frame(body, bg=BG)
        header.pack(fill="x", padx=20, pady=(18, 4))
        tk.Label(header, text="VOX COMMANDER", font=title_font, bg=BG, fg=FG).pack(anchor="w")
        tk.Label(
            header,
            text="Voice control for OpenHV — Cursor × ElevenLabs hackathon",
            font=body_font, bg=BG, fg=MUTED,
        ).pack(anchor="w")

        # ----- API key card -----
        keycard = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        keycard.pack(fill="x", padx=20, pady=(14, 8))

        tk.Label(keycard, text="ELEVENLABS API KEY", font=body_font, bg=PANEL, fg=MUTED).pack(anchor="w", padx=14, pady=(10, 0))

        keyrow = tk.Frame(keycard, bg=PANEL)
        keyrow.pack(fill="x", padx=14, pady=(2, 12))

        self.key_var = tk.StringVar(value=self.env.get("ELEVENLABS_API_KEY", ""))
        self.key_display_var = tk.StringVar(value=mask(self.key_var.get()) or "not set")
        self.key_display = tk.Label(keyrow, textvariable=self.key_display_var, font=mono_font, bg=PANEL, fg=FG, anchor="w")
        self.key_display.pack(side="left", fill="x", expand=True)

        self.change_btn = tk.Button(
            keyrow, text="Change", font=body_font,
            bg=PANEL, fg=ACCENT, activeforeground=ACCENT, activebackground=PANEL,
            relief="flat", cursor="hand2", borderwidth=0,
            command=self._on_change_key,
        )
        self.change_btn.pack(side="right")

        # hidden edit row (revealed on Change)
        self.edit_row = tk.Frame(keycard, bg=PANEL)
        self.edit_entry = tk.Entry(self.edit_row, show="●", font=mono_font, bg="#0b1117", fg=FG, insertbackground=FG, relief="flat", borderwidth=4)
        self.edit_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        tk.Button(
            self.edit_row, text="Save", font=body_font,
            bg=ACCENT, fg="white", activebackground=ACCENT, activeforeground="white",
            relief="flat", cursor="hand2", borderwidth=0, padx=14,
            command=self._on_save_key,
        ).pack(side="right")

        # ----- agent id card (read-only info) -----
        agentcard = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        agentcard.pack(fill="x", padx=20, pady=(4, 8))
        tk.Label(agentcard, text="AGENT", font=body_font, bg=PANEL, fg=MUTED).pack(anchor="w", padx=14, pady=(10, 0))
        agent_id = self.env.get("VOX_AGENT_ID", "")
        tk.Label(
            agentcard,
            text=f"Vox XO  ·  {agent_id or 'not provisioned'}",
            font=mono_font, bg=PANEL, fg=FG,
        ).pack(anchor="w", padx=14, pady=(2, 12))

        # ----- big activate button -----
        self.toggle_btn = tk.Button(
            body, text="ACTIVATE VOICE", font=button_font,
            bg=ACCENT, fg="white", activebackground=ACCENT, activeforeground="white",
            relief="flat", cursor="hand2", borderwidth=0, pady=14,
            command=self._on_toggle,
        )
        self.toggle_btn.pack(fill="x", padx=20, pady=(12, 8))

        self.status_var = tk.StringVar(value="Voice service idle. Click ACTIVATE VOICE to start.")
        tk.Label(body, textvariable=self.status_var, font=body_font, bg=BG, fg=MUTED).pack(anchor="w", padx=22)

        # ----- quick actions (button grid, bypasses ElevenLabs) -----
        actions = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        actions.pack(fill="x", padx=20, pady=(12, 6))
        tk.Label(actions, text="QUICK ACTIONS  ·  no voice, free", font=body_font, bg=PANEL, fg=MUTED).pack(anchor="w", padx=14, pady=(10, 4))

        grid = tk.Frame(actions, bg=PANEL)
        grid.pack(fill="x", padx=10, pady=(0, 12))

        # (label, intent, args) tuples in display order.
        # Labels match the in-game build menu display names (HV calls the
        # generator "Power Plant", the mbt "Assault Tank" etc.).
        # Combo actions (multiple intents in sequence) use intent="__combo__"
        # with args["steps"] = [(intent, args), ...].
        self.quick_actions: list[tuple[str, str, dict]] = [
            ("Standard Opening", "__combo__", {"steps": [
                # Note: miner2 (Mining Tower) is NOT directly buildable — it's
                # created when a miner deploys on ore. The auto_mine handler
                # in the trait does that automatically when a miner appears.
                ("produce_structure", {"structure": "generator"}),
                ("produce_structure", {"structure": "storage"}),
                ("produce_structure", {"structure": "factory"}),
                ("produce_structure", {"structure": "radar"}),
            ]}),
            ("Select All",         "select_all",        {}),
            ("Deploy Builder",     "deploy",            {}),
            ("Build Power Plant",  "produce_structure", {"structure": "generator"}),
            ("Build Storage",      "produce_structure", {"structure": "storage"}),
            ("Build Vehicle Factory", "produce_structure", {"structure": "factory"}),
            ("Build Radar Dome",   "produce_structure", {"structure": "radar"}),
            ("Build Mining Tower", "produce_structure", {"structure": "miner2"}),
            ("Train Miner",        "build",             {"unit": "miner", "count": 1}),
            ("Auto Mine (selected)", "auto_mine",       {}),
            ("Train 5 Assault Tank", "build",           {"unit": "mbt", "count": 5}),
            ("Train 3 Rifleman",   "build",             {"unit": "rifleman", "count": 3}),
            ("Harvest",            "harvest",           {}),
            ("Attack East",        "attack_move",       {"target": "east_edge"}),
            ("Stop",               "stop",              {}),
            ("Pause",              "meta_pause",        {"paused": True}),
            ("Toggle Labels",      "toggle_labels",     {}),
        ]

        for i, (label, intent, args) in enumerate(self.quick_actions):
            row, col = divmod(i, 2)
            btn = tk.Button(
                grid, text=label, font=body_font,
                bg="#1f2937", fg=FG, activebackground=ACCENT, activeforeground="white",
                relief="flat", cursor="hand2", borderwidth=0, padx=8, pady=8,
                command=lambda i=intent, a=args, lbl=label: self._on_quick_action(lbl, i, a),
            )
            btn.grid(row=row, column=col, sticky="ew", padx=4, pady=3)
        grid.columnconfigure(0, weight=1)
        grid.columnconfigure(1, weight=1)

        # ----- log tail -----
        logframe = tk.Frame(body, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        logframe.pack(fill="x", padx=20, pady=(10, 18))
        tk.Label(logframe, text="LIVE LOG", font=body_font, bg=PANEL, fg=MUTED).pack(anchor="w", padx=14, pady=(8, 0))
        # Fixed height + own scroll so the outer canvas scrollbar handles overall
        # layout while this widget gives in-place log scrolling.
        log_row = tk.Frame(logframe, bg=PANEL)
        log_row.pack(fill="x", padx=8, pady=(2, 8))
        self.log_widget = tk.Text(
            log_row, bg=PANEL, fg=FG, font=mono_font, relief="flat",
            borderwidth=0, padx=12, pady=6, height=12, state="disabled",
            insertbackground=FG, wrap="none",
        )
        log_scroll = tk.Scrollbar(log_row, orient="vertical", command=self.log_widget.yview, bg=BG, troughcolor=PANEL)
        self.log_widget.configure(yscrollcommand=log_scroll.set)
        log_scroll.pack(side="right", fill="y")
        self.log_widget.pack(side="left", fill="both", expand=True)

        root.protocol("WM_DELETE_WINDOW", self._on_close)

    # ----- key field -----

    def _on_change_key(self):
        self.edit_row.pack(fill="x", padx=14, pady=(0, 12))
        self.edit_entry.delete(0, "end")
        self.edit_entry.insert(0, self.key_var.get())
        self.edit_entry.focus_set()
        self.change_btn.configure(state="disabled")

    def _on_save_key(self):
        new = self.edit_entry.get().strip()
        if new:
            self.key_var.set(new)
            upsert_env({"ELEVENLABS_API_KEY": new})
            self.env = read_env()
        self.key_display_var.set(mask(self.key_var.get()) or "not set")
        self.edit_row.pack_forget()
        self.change_btn.configure(state="normal")
        self._append_log("[panel] api key saved")

    # ----- quick action buttons -----

    def _on_quick_action(self, label: str, intent: str, args: dict):
        self._append_log(f"[btn] {label} -> intent={intent} args={args}")
        threading.Thread(
            target=self._send_quick_action,
            args=(label, intent, args),
            daemon=True,
        ).start()

    def _send_quick_action(self, label: str, intent: str, args: dict):
        port = int(self.env.get("VOX_BRIDGE_PORT", "47777"))
        host = self.env.get("VOX_BRIDGE_HOST", "127.0.0.1")

        # Combo actions: run a sequence of (intent, args) steps; log each.
        if intent == "__combo__":
            steps = args.get("steps") or []
            for sub_intent, sub_args in steps:
                try:
                    ack = send_command(sub_intent, sub_args, host=host, port=port, timeout=2.0)
                except Exception as exc:
                    ack = {"ok": False, "error": f"exception: {exc}"}
                line = (
                    f"[btn]   {sub_intent} {sub_args}: ok"
                    if ack.get("ok")
                    else f"[btn]   {sub_intent} {sub_args}: FAILED ({ack.get('error') or 'unknown'})"
                )
                self.root.after(0, self._append_log, line)
            self.root.after(0, self._append_log, f"[btn] {label}: complete")
            return

        try:
            ack = send_command(intent, args, host=host, port=port, timeout=2.0)
        except Exception as exc:
            ack = {"ok": False, "error": f"exception: {exc}"}
        msg = (
            f"[btn] {label}: ok"
            if ack.get("ok")
            else f"[btn] {label}: FAILED ({ack.get('error') or 'unknown'})"
        )
        self.root.after(0, self._append_log, msg)

    # ----- voice service toggle -----

    def _on_toggle(self):
        if self.supervisor.is_running():
            self._set_state("stopping")
            self.supervisor.stop()
        else:
            if not self.key_var.get() or not self.env.get("VOX_AGENT_ID"):
                self.status_var.set("Set API key and agent id first.")
                return
            self._set_state("starting")
            self.supervisor.start(
                on_log=lambda line: self.root.after(0, self._append_log, line),
                on_exit=lambda: self.root.after(0, self._on_service_exit),
            )
            self._set_state("running")

    def _set_state(self, state: str):
        if state == "starting":
            self.toggle_btn.configure(text="STARTING…", bg=MUTED, state="disabled")
            self.status_var.set("Spawning voice service…")
        elif state == "running":
            self.toggle_btn.configure(text="DEACTIVATE VOICE", bg=ACCENT_DANGER, state="normal")
            self.status_var.set("Voice is LIVE. Speak naturally — the XO is listening.")
        elif state == "stopping":
            self.toggle_btn.configure(text="STOPPING…", bg=MUTED, state="disabled")
            self.status_var.set("Stopping voice service…")
        elif state == "idle":
            self.toggle_btn.configure(text="ACTIVATE VOICE", bg=ACCENT, state="normal")
            self.status_var.set("Voice service idle. Click ACTIVATE VOICE to start.")

    def _on_service_exit(self):
        self._set_state("idle")
        self._append_log("[panel] voice service exited")

    def _append_log(self, line: str):
        self.log_widget.configure(state="normal")
        self.log_widget.insert("end", line + "\n")
        self.log_widget.see("end")
        self.log_widget.configure(state="disabled")

    # ----- shutdown -----

    def _on_close(self):
        self.supervisor.stop()
        self.root.destroy()


def main() -> int:
    root = tk.Tk()
    Panel(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
