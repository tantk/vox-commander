# Vox Commander — Demo Video Script

**Target length:** ~2 minutes (120 seconds)
**Format:** vertical or 16:9 — pick based on platform. Both work; the in-game framing is mostly square so either reads.

## Pre-record checklist

- [ ] Close any unused apps; quiet workstation
- [ ] Mic gain tested; speak at conversational volume
- [ ] OpenHV resolution: 1920×1080 windowed
- [ ] OBS / ShadowPlay recording at 1080p60, audio split (game / mic separate tracks if possible)
- [ ] `vox-demo.bat` ready to double-click — skips the main menu
- [ ] Browser tab open to ElevenLabs dashboard in case anything misbehaves mid-take
- [ ] Drink water — there's a lot of talking

---

## SEGMENT A — Opening (0:00–0:10, 10 sec)

Black screen, centered serif text, fade-in fade-out timing baked into editor.

**Card 1 (0:00–0:04):**
```
If you can't use a keyboard,
you can't play 99% of strategy games.
```

**Card 2 (0:04–0:08):**
```
So we taught one to listen.
```

**Card 3 (0:08–0:10) — small text bottom corner:**
```
Built with Cursor + ElevenLabs · OpenHV
```

Optional background: low-key military/synth pad music starts here, continues underneath the demo.

---

## SEGMENT B — Live demo (0:10–1:50, ~100 sec)

Cut from black to game window. **Hands visible OFF the keyboard/mouse for the first 2 seconds** to establish the constraint.

You can use the **in-game ACTIVATE VOICE button** (one click is fine — that's the only mouse interaction the audience sees), or have it pre-armed via tkinter panel auto-spawn and the click happens before the recording starts.

Below: each beat = USER LINE → ROUGH XO RESPONSE → WHAT SHOWS ON SCREEN.

### Beat 1 — Cold open (0:10–0:18)

> 🎤 **"XO, status report."**
> 🤖 *"Twenty thousand credits. Full force standing by."* (or similar; gpt-5-mini will phrase)
> 🎬 Camera on our base. Grid (A–F / 1–6) visible at viewport edges. Unit labels (Tank-1, Power-3) showing.

### Beat 2 — Production (0:18–0:30)

> 🎤 **"Build me ten tanks. Train five rocketeers."**
> 🤖 *"Ten tanks queued. Five rocketeers queued."*
> 🎬 Build sidebar icons appear with countdowns. Cash drops visibly.

### Beat 3 — Reconnaissance via camera (0:30–0:38)

> 🎤 **"Pan to the enemy base."**
> 🤖 *"Panning to enemy base."*
> 🎬 Camera slides south to the red Creeps base. Their red labels visible: Tank-1, Bunker-1, Outpost-1.

### Beat 4 — Airstrike (0:38–0:50)

> 🎤 **"Airstrike on E5."**
> 🤖 *"Airstrike inbound. Target E5."*
> 🎬 Radar dome glows; bomber sprites fly across screen; explosion at E5 grid cell.
> 🔥 This is your "wait what" viral beat. The audio + visual moment is huge.

### Beat 5 — Specialised positioning (0:50–1:02)

> 🎤 **"Snipers and rocketeers to C3."**
> 🤖 *"Snipers and rocketeers moving to C3."* (may need to phrase as a single tool call or two — gpt-5-mini's call)
> 🎬 Selected unit types march to grid C3 chokepoint. Camera pans to follow.

### Beat 6 — Focus fire by name (1:02–1:14)

> 🎤 **"Focus fire on Bunker-1!"**
> 🤖 *"Concentrating fire on Bunker-1."*
> 🎬 Every tank converges on the red-labelled Bunker-1. Bunker takes damage, explodes.
> 🔥 Second viral beat — the in-game text label getting picked off by voice is unmistakeable.

### Beat 7 — Full assault (1:14–1:26)

> 🎤 **"Full assault! Send everything south."**
> 🤖 *"Assaulting, commander."*
> 🎬 Whole army rolls south. Commentator (dramatic voice): *"And the battle begins!"* (or a casualty line as enemy hits start)

### Beat 8 — Crisis + comeback (1:26–1:42)

(AI counter-attack arrives — Creeps was the Rogue bot, it pushes back)

> 🎤 **"Pull back! Defend!"**
> 🤖 *"Falling back. Defending."*
> 🎬 Army wheels north toward home.
> 🎤 **"Train ten more tanks. Rally them to D4."**
> 🤖 *"Tanks queued. Rallying D4."*
> 🎬 Factory cooks; new tanks stream out to D4 chokepoint.

### Beat 9 — Finishing blow (1:42–1:50)

> 🎤 **"Final assault. End it."**
> 🤖 *"Final push."*
> 🎬 Renewed army rolls south. Enemy base structures collapse one by one. The Creeps base falls.
> 🎙️ Commentator: ***"Enemy base destroyed. Victory, commander."***
> 🎬 Hold on victory frame for 1–2 seconds.

---

## SEGMENT C — Closing (1:50–2:00, 10 sec)

Cut from game footage. Black screen, fade-in.

**Card 1 (1:50–1:55):**
```
Built in Cursor.
Voiced by ElevenLabs.
```

**Card 2 (1:55–2:00):**
```
Anyone can command an army now.

github.com/<your-repo>
```

Optional small text at the very bottom:
```
Accessibility-first voice control · 24 tactical commands · zero keyboard, zero mouse
```

---

## Editing notes

1. **Two audio tracks if possible** — game/commentator on one, your mic + XO on the other. Lets you EQ them separately so the XO doesn't get buried under explosions.
2. **Captions on screen** — overlay the user's spoken lines as subtitles in the bottom third. Helps mute-scrollers (most viral viewers).
3. **Show the user's hands** — even a hand-on-mug-not-on-keyboard cutaway sells the thesis.
4. **The two viral beats are #4 (airstrike) and #6 (focus fire by name)** — make those moments breathe. Don't over-edit them. Let the cause→effect land.

## If something misbehaves mid-take

Common recoverable issues:

- **Agent doesn't fire a tool** → say it again with slightly different phrasing. Don't argue with the agent on camera.
- **AI hasn't built much yet** → cut to a later state in editing; rest of the script still flows
- **Audio leaks reasoning** → it shouldn't with `reasoning_effort: low`, but if it does, cut that take and re-shoot. Don't try to splice mid-leak.

## After-record cleanup

- Trim each segment with hard cuts at silence
- Add the three opening cards + two closing cards
- Background music throughout, ducked during XO lines
- Export 1080p60, ~10 Mbps bitrate, mp4
- Length should land at 1:55–2:05 after trimming

---

**Total live commands the user delivers: ~14**
**Total tool intents demonstrated: assault, build (×2 categories), pan_camera, airstrike, station_army (×2), focus_fire, defend, set_rally**
**Implicit demonstrations: grid overlay, friendly labels, enemy labels, commentator, victory event, voice activation button**
