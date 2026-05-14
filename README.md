# Vox Commander

Voice-controlled real-time strategy. Built on OpenHV for the Cursor + ElevenLabs hackathon.

No keyboard, no mouse — just spoken commands. See `docs/HACKATHON_DESCRIPTION.md` for the project pitch.

## Quick start (Windows)

**1. Clone this repo and OpenHV side by side.**

```cmd
git clone https://github.com/tantk/vox-commander.git
cd vox-commander
git clone https://github.com/OpenHV/OpenHV.git openhv
```

The `openhv/` folder is gitignored on purpose — we only ship our overlay in `openhv-mod/`.

**2. Apply the mod overlay.**

Copy everything from `openhv-mod/` into `openhv/`, preserving paths. From PowerShell:

```powershell
Copy-Item -Recurse -Force openhv-mod\* openhv\
```

**3. Build the HV mod DLL.**

```cmd
cd openhv
dotnet build OpenRA.Mods.HV\OpenRA.Mods.HV.csproj -c Release
cd ..
```

**4. Set up the voice service.**

```cmd
cd voice-service
python -m venv .venv
.venv\Scripts\python.exe -m pip install -e .
cd ..
```

**5. Drop in your ElevenLabs API key.**

```cmd
copy .env.example .env
```

Open `.env` and paste your key after `ELEVENLABS_API_KEY=`. (Alternatively, launch the game first and type it into the **API KEY** field in the in-game side panel — same effect.)

**6. Provision the agent (one-time).**

```cmd
cd voice-service
.venv\Scripts\python.exe scripts\create_agent.py
cd ..
```

This creates the "Vox XO" Conversational Agent on your ElevenLabs account and writes `VOX_AGENT_ID` + `VOX_COMMENTATOR_VOICE_ID` back into `.env`.

**7. Launch.**

```cmd
vox-demo.bat
```

Skips OpenHV's main menu and drops you straight into the demo skirmish. Click **ACTIVATE VOICE** on the side panel and start talking — try *"Standard opening"*, *"Build ten tanks"*, *"Airstrike the enemy base"*.

## Repo layout

```
openhv-mod/       C# traits + chrome YAML + demo map (our overlay)
voice-service/    Python voice agent + tools + GUI panel
docs/             Design spec, plan, hackathon description
vox-demo.bat      One-click game launcher
vox-commander.bat Standalone voice service launcher
```

## Troubleshooting

- **"OpenHV not found"** when running `vox-demo.bat` — you missed step 1. Clone OpenHV into `./openhv/`.
- **No bombers when you say "airstrike"** — make sure you have a Radar Dome built; the airstrike is dispatched from there.
- **Agent doesn't respond** — check `voice-service/main.log` (or the live log pane in the panel). Most failures are a missing or wrong `ELEVENLABS_API_KEY`.
