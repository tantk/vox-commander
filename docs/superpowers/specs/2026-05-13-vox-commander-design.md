# Vox Commander — Design Spec

**Date:** 2026-05-13
**Status:** Draft, awaiting user review
**Context:** Cursor + ElevenLabs hackathon. Build something usable without touching a keyboard. Deliverable is a viral-style video.

## 1. Product

You play a commander in a real-time strategy game. You do not touch the keyboard or mouse. You bark orders into a microphone. Units obey, an XO talks back to you, and a commentator narrates the battle.

The base game is **OpenHV** (open-content RTS built on the OpenRA engine). OpenHV is GPLv3 with original CC0 assets, eliminating any IP risk associated with using Red Alert assets.

The hackathon submission is a recorded video demonstrating the experience. No public hosted build, no WebAssembly port.

## 2. Goals and non-goals

**Goals**

- A working voice loop where natural-language coach commands change game outcomes in real time.
- A demo video where the viewer can clearly see: (a) the human never touches the keyboard, (b) units respond to spoken orders within ~1 second, (c) the XO and commentator add personality through distinct ElevenLabs voices.
- Engineering that respects the global standards in `~/.claude/CLAUDE.md`: no quick hacks, proper libraries, change-before-doing, verify against captured behavior.

**Non-goals**

- Multiplayer.
- WebAssembly / browser build.
- Save / load, mod menu integration, campaign progression.
- Exhaustive command coverage. Ten intents are enough.
- Anti-cheat or robustness against adversarial speech.
- Localization. English-only.

## 3. Architecture

Two processes plus one managed service. ElevenLabs **Conversational Agent** is the primary brain — it owns mic capture, STT, the language model that decides intent, tool routing, and TTS playback. Python is the tool executor and event-driven commentator. C# is the in-game adapter.

```
   [Microphone] ──► ElevenLabs Conversational Agent
                    (managed STT + LLM + TTS, WebSocket SDK)
                              │  client-side tool calls
                              ▼
              ┌────────────────────────────────┐
              │  voice-service (Python)        │
              │  - agent_client.py  Conv Agent │ ──► registers tools, handles callbacks
              │                     WS client  │
              │  - tools.py         tool impls │ ──► dispatch_command, read_state, set_pause
              │  - fastpath.py      stateless  │ ──► <5ms short-circuit for stop/hold/sprint
              │                     dispatch   │
              │  - refs.py          dialogue   │ ──► resolves "that base" against context
              │                     context    │
              │  - commentator.py   event TTS  │ ──► event-driven, direct ElevenLabs TTS
              │  - game_socket.py   TCP client │
              │  - main.py          orchestrator│
              └──────────────┬─────────────────┘
                             │ JSON lines, localhost:7777
                             ▼
              ┌────────────────────────────────┐
              │  OpenHV + VoxTrait (C#)        │
              │  - VoxServerTrait              │ ◄── inbound commands
              │  - VoxEventEmitter             │ ──► outbound game events
              │  - VoxCommandHandlers          │     (issues UnitOrders)
              └──────────────┬─────────────────┘
                             │
                             ▼
                  [Display + Speakers]
                  ElevenLabs Agent voice + direct commentator TTS
                  OpenHV native audio mix (SFX, explosions)
```

The TCP boundary between Python and C# is the integration contract — either side can be developed and tested independently using a mock peer. ElevenLabs Conversational Agent is the boundary on the voice side — we treat it as a managed dependency, configured through their dashboard and reached via their WebSocket SDK.

### 3.1 Why this split

- **ElevenLabs Conversational Agent** as the primary brain: lower end-to-end latency than rolling our own STT + LLM + TTS pipeline (their managed product is tuned for sub-second turn-taking), less code, and a stronger hackathon thesis ("we used Cursor and ElevenLabs" lands harder when ElevenLabs is doing the heavy lifting, not just TTS).
- **Python** is the right place for tool implementations and prompt-engineering of the Agent's system prompt — fast iteration, mature SDKs, easy to pipe TCP packets.
- **C#** is the only way to issue real `UnitOrder`s in OpenRA without input simulation.
- A network socket between Python and C# is the cleanest decoupling and gives us a transport we can mock from either side during development.

### 3.2 Process lifecycle

1. User launches `vox-commander.bat` which starts the Python voice service.
2. Python voice service connects to ElevenLabs Conversational Agent over WebSocket, registers client-side tools, waits for both the OpenHV trait to connect on TCP and the user to start speaking.
3. User launches OpenHV manually (or the bat file launches it after voice-service is ready).
4. `VoxServerTrait` initializes on world load, connects to `localhost:7777`, and begins emitting events.
5. User speaks. ElevenLabs Agent transcribes and decides intent. Agent invokes a Python tool. Python dispatches via TCP. Game responds. Agent verbalizes acknowledgment.
6. On match end or trait disconnect, voice service returns to wait state. Agent connection stays alive.

## 4. Wire protocol

JSON line protocol over TCP. Each side writes `\n`-terminated JSON objects. No length prefix — newline delimited is sufficient for our payload sizes and simplifies debugging.

### 4.1 Command messages (Python → C#)

```json
{ "type": "command", "id": "uuid-v4", "intent": "select", "args": { "filter": "all_tanks" } }
{ "type": "command", "id": "uuid-v4", "intent": "move",   "args": { "target": "east_edge" } }
{ "type": "command", "id": "uuid-v4", "intent": "attack", "args": { "target_ref": "last_enemy_base" } }
{ "type": "command", "id": "uuid-v4", "intent": "build",  "args": { "unit": "rifle_infantry", "count": 5 } }
{ "type": "command", "id": "uuid-v4", "intent": "meta_pause", "args": { "paused": true } }
{ "type": "command", "id": "uuid-v4", "intent": "query",  "args": { "field": "cash" } }
```

### 4.2 Event messages (C# → Python)

```json
{ "type": "event", "kind": "match_start", "ts": 12345 }
{ "type": "event", "kind": "unit_destroyed", "ts": 12450, "actor": "tank_07", "by": "enemy" }
{ "type": "event", "kind": "base_under_attack", "ts": 12460, "structure": "barracks_01" }
{ "type": "event", "kind": "production_complete", "ts": 12490, "unit": "rifle_infantry" }
{ "type": "event", "kind": "state_snapshot", "ts": 12500, "cash": 1450, "power": 80, "units": 12 }
```

### 4.3 Ack messages (C# → Python)

```json
{ "type": "ack", "id": "uuid-v4", "ok": true }
{ "type": "ack", "id": "uuid-v4", "ok": false, "error": "no_selection" }
```

State snapshots are emitted on a 2 Hz tick. Events are emitted as they happen. Commands are acked immediately, success or failure.

## 5. Command grammar (v1, 10 intents)

| Intent | Example utterance | C# action |
|---|---|---|
| `select` | "select all tanks" / "select my engineers" | filter actors by type/owner, replace selection |
| `move` | "move them east" / "go to the refinery" | issue Move order to current selection |
| `attack` | "attack that base" / "kill the harvester" | AttackMove to resolved target |
| `attack_move` | "push to the bridge" | AttackMove order along a path |
| `stop` | "hold position" / "stop" | Stop order |
| `build` | "build five rifle infantry" / "queue two tanks" | enqueue production N times on appropriate factory |
| `produce_structure` | "build a barracks" | place + start construction |
| `harvest` | "send the harvester back to ore" | Harvest order |
| `meta_pause` | "pause" / "time out, coach" | toggle `World.Paused` |
| `query` | "how much money" / "what's my power" | XO replies via TTS, no game action |

Anything outside this grammar routes to the **XO Conversational Agent** for free-form reply.

### 5.1 Reference resolution

References to actors and locations are resolved in two stages.

**Stage 1 — Conversational Agent + Python tool (dialogue resolution).** The Agent's LLM produces tool-call arguments that already include concrete references. When the Python tool handler receives `dispatch_command(intent="attack", target_ref="that_base")`, it consults a short dialogue context (last ~10 turns + the most recent `state_snapshot` from C#) and rewrites the reference into something the trait can resolve:

- `target_ref="that_base"` + recent state snapshot showed `enemy_barracks_alpha` → rewrite to `target_ref="enemy_barracks_alpha"`
- `target="east"` → pass through as `target="east_edge"`
- `target_kind="harvester"` + owner implicit → pass through as `target_kind="enemy_harvester"`

If Python cannot disambiguate even with context, the tool returns an error like `{ok: false, error: "ambiguous_target"}` and the Agent's system prompt instructs it to ask the coach for clarification.

**Stage 2 — C# trait (world resolution).** The trait receives a concrete logical reference and resolves it against current world state. Known reference forms:

- `east_edge` / `west_edge` / `north_edge` / `south_edge` → a point near the named map boundary
- `enemy_base` → the centroid of enemy-owned structures
- `the_refinery` → the nearest refinery, preferring friendly
- `<actor_handle>` (e.g. `enemy_barracks_alpha`) → look up by the handle issued in a prior `state_snapshot`
- `enemy_<kind>` (e.g. `enemy_harvester`) → the closest enemy actor of that kind to the current selection

If world resolution fails (the structure was destroyed since the snapshot, no matching actor exists), the trait acks with `error` and the Agent verbalizes "I don't see what you mean, commander."

The split is deliberate: Python owns conversation context, C# owns world state. Neither side reaches into the other's domain.

### 5.2 Stateless-command short-circuit (the "fast-path")

A subset of intents need no world-state lookup, no reference resolution, no LLM ambiguity. They are pure side-effects on whatever the current selection is:

- `stop` / `hold` — issue Stop on current selection
- `sprint` (if implemented) — toggle sprint on current selection
- `meta_pause` — toggle `World.Paused`

When the Python tool handler sees one of these intents, it bypasses ref resolution entirely and writes the JSON to the TCP socket in the same async step. Round-trip is under 10 ms. These are the commands most likely to be barked rapid-fire in a viral clip ("STOP! HOLD! SHOOT!") so making them feel instant is high-leverage demo polish.

This is not a pre-LLM regex bypass — the Agent's LLM still does the transcription and intent decision. It is an execution short-circuit downstream of the tool call.

## 6. ElevenLabs surface

| Surface | Use |
|---|---|
| Conversational Agent (XO persona) | The primary brain. Owns mic capture, STT, intent decision via its LLM, tool routing to Python, and TTS playback. System-prompted as a Hard-Vacuum tactical officer who responds tersely to coach commands and engages in free-form tactical chat when asked. Three client-side tools registered: `dispatch_command(intent, args)`, `read_state(fields)`, `set_pause(paused)`. |
| Direct TTS (commentator) | Distinct dramatic voice. Triggered by C# events: match start, base under attack, large unit losses, production milestones, match end. Bypasses the Agent — Python calls ElevenLabs TTS API directly so the commentator can talk over the Agent's pauses without coordination headaches. |
| OpenHV native audio | Stays on for footsteps, weapons, explosions. |

### 6.1 Why one Agent for both commands and chat

The Agent's LLM is good enough to handle both "build five tanks" and "XO, what's the enemy doing?" — they're just different tool routings (the former dispatches a command, the latter calls `read_state` and verbalizes the answer). Splitting into two agents would double the latency of mode switching and double the dashboard configuration without buying us anything.

### 6.2 Commentator coexistence

Two voices producing audio concurrently is fine as long as they're distinct enough. We use markedly different ElevenLabs voices (e.g., XO: calm tactical male; Commentator: dramatic sports-announcer female) so overlapping speech reads as "two characters" not "broken audio." If overlap becomes a problem during testing, we add a simple priority queue in Python: commentator events at "high priority" duck the Agent's TTS for a beat.

Commentator triggers are hardcoded — six to ten rule-based event handlers. Not an LLM-driven director. This is intentional: keeps latency and cost predictable for the demo.

## 7. Scope cuts (hard YAGNI)

- One faction in OpenHV. Pick the one with the most visually striking units after a 15-minute survey.
- One small skirmish map shipped with OpenHV.
- Single-player vs medium AI.
- Ten intents above, nothing else.
- Six to ten commentator event triggers, hardcoded.
- One XO voice, one commentator voice.
- No save / load, no campaign, no UI changes inside OpenHV beyond what the trait needs.

## 8. File layout

```
C:\dev\elevenhack\cursor\
├── docs/superpowers/specs/2026-05-13-vox-commander-design.md   ← this file
├── docs/superpowers/plans/                                     ← implementation plan goes here
├── openhv-mod/
│   └── VoxTrait/
│       ├── VoxServerTrait.cs            ← TCP server + lifecycle
│       ├── VoxEventEmitter.cs           ← world events → JSON
│       ├── VoxCommandHandlers.cs        ← intent → UnitOrder
│       ├── VoxReferenceResolver.cs      ← logical refs → actor/point
│       └── VoxTrait.csproj
├── voice-service/
│   ├── pyproject.toml
│   ├── src/vox/
│   │   ├── agent_client.py              ← ElevenLabs Conv Agent WS client + tool registry
│   │   ├── tools.py                     ← dispatch_command, read_state, set_pause
│   │   ├── fastpath.py                  ← stateless-command short-circuit
│   │   ├── refs.py                      ← dialogue context + reference rewriting
│   │   ├── commentator.py               ← event → direct TTS dispatcher
│   │   ├── game_socket.py               ← TCP client to VoxTrait
│   │   ├── grammar.py                   ← command schema + validation
│   │   └── main.py                      ← orchestrator
│   └── tests/
└── vox-commander.bat                    ← launches both processes
```

## 9. Two-day plan

**Day 1 — spine.**

- Set up OpenHV from source on Windows. Build, launch a skirmish, confirm it runs.
- Create the OpenHV mod folder with a stub `VoxServerTrait` that opens a TCP socket and logs incoming JSON.
- Implement `move` end-to-end with a CLI test client (not voice yet): a Python script sends a hardcoded `move` command; tanks visibly move. This de-risks the C# integration boundary.
- Configure ElevenLabs Conversational Agent in dashboard: XO persona, system prompt, register `dispatch_command` and `read_state` tools.
- Wire Python `agent_client.py` to the Agent WebSocket SDK, register the same tools as client-side handlers. Speak "move east," watch the tool fire, watch the tank move. End of day: full vertical slice working for one intent.

**Day 2 — fill in and polish.**

- Implement the remaining nine intents in C# (handlers + reference resolution).
- Tune the Agent's system prompt so intent decisions are reliable.
- Implement the stateless-command short-circuit in `fastpath.py`.
- Wire the commentator with six to ten event triggers (event listener on TCP socket → direct ElevenLabs TTS).
- Pick faction and map. Tune unit balance if needed to make the demo readable.
- Record viral clip.

## 10. Risks

1. **OpenHV build on Windows.** OpenRA-engine projects require .NET 8 SDK. First build is ~10 minutes. Documented well; low risk but non-zero.
2. **OpenRA trait integration.** First hour of Day 1 is reading the trait/order source until the model is clear. Trait API is well-documented in the OpenRA wiki. Medium risk if there are pitfalls specific to OpenHV's mod structure.
3. **Voice round-trip latency.** Target user-stops-speaking → unit-responds under 800 ms for common commands. The Conversational Agent's managed pipeline is tuned for sub-second turn-taking, which is the primary mitigation. Secondary mitigation: the stateless-command short-circuit (Section 5.2) keeps the post-Agent dispatch under 10 ms for the rapid-fire commands ("stop," "hold," "pause") most likely to appear in the viral clip.
4. **Reference resolution ambiguity.** "Attack that base" with no prior mention is ambiguous. Mitigation: keep a small dialogue context in Python; if still ambiguous, XO asks for clarification.
5. **Pause-during-speech UX.** If the user speaks a long command, the game continues. Mitigation: the `meta_pause` intent gives them an explicit "TIME OUT" they can call. Optional Day 2 polish: auto-pause on STT activity start, resume on silence.

## 11. Out of scope explicitly

- WebAssembly / browser deployment.
- Mobile.
- Multi-language.
- Multiplayer.
- Streaming the demo live during the hackathon.
- Mod menu UI inside OpenHV.
- Anti-cheat, rate limiting, abuse handling.
- Persistent user profiles.

## 12. Open questions for the user

None at present. Once this spec is approved, the next step is the implementation plan.
