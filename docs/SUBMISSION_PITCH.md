# Vox Commander — Submission Pitch

**One-line:** Voice control for the hardest possible target — a real-time strategy game — built so people who can't use a keyboard can still play.

**Built with:** Cursor + ElevenLabs Conversational AI + Anthropic Claude.
**Base game:** OpenHV (open-source, GPLv3, original CC0 assets — no IP risk).

---

## Why this matters

> *"If you can't use a keyboard, you can't play 99% of strategy games. So we taught one to listen."*

Real-time strategy is the most input-dense PC genre. Pro StarCraft players hit 300+ actions per minute. Mouse, keyboard, hotkeys, build menus, control groups, attack-moves, focus-fire — every second is a flood of physical input. That's why this genre is *closed* to anyone with motor impairments, RSI, low vision, or temporary disability (broken arm, post-surgery, holding a baby).

We targeted the hardest possible domain on purpose. If voice can replace mouse + keyboard *here*, voice can replace mouse + keyboard *anywhere*.

The result: a complete RTS — base building, economy, army management, multi-unit combat, tactical positioning — playable end-to-end by voice. Zero keyboard, zero mouse.

---

## Who it's for

| User | What they get |
|---|---|
| Players with motor impairments / RSI | A whole genre that was previously inaccessible |
| Players with low vision | Audio-led play loop: the XO confirms every order, commentator narrates events |
| Anyone with hands occupied | Cooking, holding a child, post-surgery, lying down — still play |
| Streamers / content creators | A new performative play style: bark commands, see them executed |
| RTS designers | A working blueprint for the input model the genre has been missing |

---

## What we built (the technical story)

A three-layer system that turns spoken English into in-game orders, with no proprietary game engine modifications:

**Layer 1 — ElevenLabs Conversational Agent (the voice).** Owns mic capture, streaming STT, intent decision via LLM, and TTS responses. Tuned to sub-second turn-taking. The "XO" persona is a calm tactical officer who confirms every order.

**Layer 2 — Python tool host.** Implements ~25 client-side tools the agent can call: `dispatch_command`, `read_state`, `set_pause`, `focus_fire`, `set_rally`, `hold_position`, `station_army`, `toggle_grid`, `toggle_labels`, etc. Owns dialogue context (so "that base" resolves correctly), fastpath dispatch for instant commands, and a TCP boundary to the game.

**Layer 3 — Custom OpenRA C# trait (the hands).** Runs *inside* the game's simulation. Receives JSON commands over localhost TCP, translates them into native `UnitOrder`s (the same orders a mouse click would issue). Emits live state snapshots back so the agent always knows what's on the map. Includes:

- Auto-mine logic (miners spread across ore deposits, deploy into Mining Towers)
- Auto-place logic (queued buildings auto-place when production completes)
- Friendly-name labels under units (`Tank-1`, `Bunker-3`) so commanders can target by name
- Battleship-style grid overlay (A1–F6) for precise voice targeting
- Four tactical attack modes (`scout`, `harass`, `assault`, `defend`)
- In-game side panel for click-driven fallback during dev

**No cheating.** Every order issued by voice is the same order a mouse click issues. Resources cost real money, buildings take real time to construct, units die in real combat. The voice layer is pure input replacement.

---

## What sets it apart on this hackathon

| Criterion | How Vox Commander stacks up |
|---|---|
| **Uses Cursor** | Whole project built in Cursor/Claude Code (4 days, 70+ commits) |
| **Uses ElevenLabs** | Conversational Agent (primary), TTS (commentator), REST API for agent provisioning, all three together |
| **No keyboard** | Literally true. The viral demo never touches one. |
| **Difficulty stress-tested** | An RTS is the worst-case input target. If it works here, it works anywhere. |
| **Real product, not a toy** | ~25 voice intents, full economy + combat loop, two-player skirmish, custom demo map |
| **Generalizable architecture** | The C# trait can be swapped for any other "actions backend" (desktop, web, IDE). The voice + Python tool layers are reusable as-is. |

---

## Submission video framing (60-90 seconds)

**Beats:**

1. *0–5s* — Black screen, white text: *"If you can't use a keyboard, you can't play 99% of strategy games."*
2. *5–10s* — Black screen, white text: *"So we taught one to listen."*
3. *10–15s* — Person at desk, hands clearly visible/off-screen. Opens the game.
4. *15–25s* — *"Standard opening."* → 4 buildings + miners queue in seconds.
5. *25–35s* — *"Scout the enemy."* → unit moves east, fog of war lifts. *"Build 10 tanks."*
6. *35–50s* — *"Focus fire on Bunker-1."* → 10 tanks converge on the named enemy structure. Tactical labels visible.
7. *50–60s* — *"Assault!"* → full army rolls. Combat. Commentator narrates.
8. *60–70s* — *"Pull back, defend the base."* → army recalls. Save the moment.
9. *70–80s* — *"Final push."* → win condition.
10. *80–90s* — End card: *"Built in Cursor. Voiced by ElevenLabs. Anyone can command an army now."*

**Hands-on-screen for the whole clip.** No keystrokes visible. No mouse cursor (or only used between matches).

---

## Repo / tech artifacts judges can audit

- C# trait + UI panel: `openhv-mod/OpenRA.Mods.HV/`
- Python voice service: `voice-service/`
- Agent provisioning + prompt update scripts: `voice-service/scripts/`
- Demo map: `openhv-mod/mods/hv/maps/vox-demo/`
- Spec: `docs/superpowers/specs/2026-05-13-vox-commander-design.md`
- Plan: `docs/superpowers/plans/2026-05-13-vox-commander-plan.md`

Every commit is named, atomic, and documents the *why* not just the *what*.
