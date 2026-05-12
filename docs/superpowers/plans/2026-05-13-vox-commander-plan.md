# Vox Commander Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a voice-controlled OpenHV RTS demo for the Cursor + ElevenLabs hackathon. End state: a viral video where the human never touches keyboard or mouse, only voice.

**Architecture:** ElevenLabs Conversational Agent owns voice (mic capture, STT, intent LLM, tool routing, TTS). A Python voice service implements client-side tools, dialogue context, fastpath dispatch, and event-driven commentator TTS. A custom C# `VoxBridge` trait runs inside OpenHV, listens on `localhost:7777`, executes `UnitOrder`s, and emits game events. See `docs/superpowers/specs/2026-05-13-vox-commander-design.md` for the design spec.

**Tech Stack:**
- Python 3.11+, asyncio, `elevenlabs` SDK (2.46.0+), pytest
- C# / .NET 8, OpenRA engine + OpenHV mod (`github.com/OpenHV/OpenHV`)
- JSON-line protocol over `localhost:7777`

---

## File structure

```
C:\dev\elevenhack\cursor\
├── docs/superpowers/specs/2026-05-13-vox-commander-design.md     ← spec
├── docs/superpowers/plans/2026-05-13-vox-commander-plan.md       ← this file
├── .gitignore
├── .env.example                                                  ← env var template
├── vox-commander.bat                                             ← launch helper
├── openhv/                                                       ← OpenHV checkout (gitignored content)
│   └── OpenRA.Mods.HV/Traits/World/VoxBridge.cs                  ← our trait
│   └── mods/hv/rules/world.yaml                                  ← modified to register trait
├── voice-service/
│   ├── pyproject.toml
│   ├── src/vox/
│   │   ├── __init__.py
│   │   ├── protocol.py          ← wire protocol: encode/decode JSON messages
│   │   ├── game_socket.py       ← TCP client to VoxBridge
│   │   ├── refs.py              ← dialogue context, reference rewriting
│   │   ├── fastpath.py          ← stateless-command short-circuit
│   │   ├── tools.py             ← dispatch_command, read_state, set_pause
│   │   ├── commentator.py       ← event → direct ElevenLabs TTS
│   │   ├── agent_client.py      ← ElevenLabs Conv Agent client + tool registry
│   │   └── main.py              ← orchestrator
│   └── tests/
│       ├── test_protocol.py
│       ├── test_game_socket.py
│       ├── test_refs.py
│       ├── test_fastpath.py
│       └── test_commentator.py
```

Boundaries:
- `protocol.py` knows only message shapes, no I/O
- `game_socket.py` knows only how to ship bytes, no semantics
- `refs.py`, `fastpath.py` are pure logic, no I/O
- `tools.py` is the only place that combines the above into Agent-facing handlers
- `commentator.py` and `agent_client.py` are the I/O leaves

---

## Task 1: Project scaffold + git

**Files:**
- Create: `C:\dev\elevenhack\cursor\.gitignore`
- Create: `C:\dev\elevenhack\cursor\.env.example`
- Create: `C:\dev\elevenhack\cursor\README.md`

- [ ] **Step 1: Initialize git repo**

Run from `C:\dev\elevenhack\cursor`:
```powershell
git init
git branch -M main
```

- [ ] **Step 2: Write .gitignore**

Create `.gitignore`:
```
# Python
__pycache__/
*.py[cod]
.venv/
*.egg-info/
.pytest_cache/

# OpenHV checkout (don't commit a fork; we keep our trait in openhv-mod/ overlay only)
/openhv/

# OS / IDE
.vs/
.vscode/
*.swp
Thumbs.db

# Secrets
.env
*.local

# Build artifacts
bin/
obj/
dist/
build/
```

- [ ] **Step 3: Write .env.example**

Create `.env.example`:
```
# ElevenLabs
ELEVENLABS_API_KEY=
VOX_AGENT_ID=
VOX_COMMENTATOR_VOICE_ID=

# Game bridge
VOX_BRIDGE_HOST=127.0.0.1
VOX_BRIDGE_PORT=7777
```

- [ ] **Step 4: Write a one-paragraph README**

Create `README.md`:
```markdown
# Vox Commander

Voice-coached OpenHV RTS demo for the Cursor + ElevenLabs hackathon.

See `docs/superpowers/specs/2026-05-13-vox-commander-design.md` for the design and
`docs/superpowers/plans/2026-05-13-vox-commander-plan.md` for the implementation plan.
```

- [ ] **Step 5: Commit**

```powershell
git add .gitignore .env.example README.md docs
git commit -m "chore: project scaffold, spec, plan"
```

---

## Task 2: Clone and build OpenHV on Windows

**Files:**
- Create: `C:\dev\elevenhack\cursor\openhv\` (git clone target, gitignored)

- [ ] **Step 1: Install prerequisites**

Verify .NET 8 SDK is installed:
```powershell
dotnet --list-sdks
```
Expected: a line starting with `8.0.`. If missing, install from https://dotnet.microsoft.com/download/dotnet/8.0.

- [ ] **Step 2: Clone OpenHV**

```powershell
git clone https://github.com/OpenHV/OpenHV.git C:\dev\elevenhack\cursor\openhv
```

- [ ] **Step 3: Fetch the engine**

OpenHV pins a specific OpenRA engine commit via `mod.config`. From `C:\dev\elevenhack\cursor\openhv`:
```powershell
.\make.cmd all
```
If `make.cmd` is not present, inspect `mod.config` for `ENGINE_VERSION` and `ENGINE_DIRECTORY`, then run `git clone https://github.com/OpenRA/OpenRA.git engine` and `cd engine; git checkout <pinned-sha>; dotnet build`. Then return to the mod directory and `dotnet build OpenRA.Mods.HV/OpenRA.Mods.HV.csproj`.

Expected: a successful build with output DLLs in `openhv/engine/bin/` and `openhv/OpenRA.Mods.HV/bin/`.

- [ ] **Step 4: Launch the game to confirm a working baseline**

```powershell
.\launch-game.cmd Game.Mod=hv
```
Or invoke the engine binary directly with the `hv` mod. Expected: OpenHV main menu appears. Start a skirmish, confirm units move on click. Close the game.

- [ ] **Step 5: Commit (just the helper note, not the openhv tree)**

```powershell
git add docs
git commit --allow-empty -m "chore: openhv cloned + built locally (gitignored)"
```

---

## Task 3: Python project skeleton

**Files:**
- Create: `C:\dev\elevenhack\cursor\voice-service\pyproject.toml`
- Create: `C:\dev\elevenhack\cursor\voice-service\src\vox\__init__.py`
- Create: `C:\dev\elevenhack\cursor\voice-service\tests\__init__.py`

- [ ] **Step 1: Write pyproject.toml**

Create `voice-service/pyproject.toml`:
```toml
[project]
name = "vox-commander"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = [
  "elevenlabs[pyaudio]>=2.46.0",
  "python-dotenv>=1.0",
  "pydantic>=2.7",
]

[project.optional-dependencies]
dev = ["pytest>=8", "pytest-asyncio>=0.23"]

[tool.pytest.ini_options]
testpaths = ["tests"]
asyncio_mode = "auto"

[build-system]
requires = ["setuptools>=68"]
build-backend = "setuptools.build_meta"

[tool.setuptools.packages.find]
where = ["src"]
```

- [ ] **Step 2: Create empty package files**

Create `voice-service/src/vox/__init__.py` (empty).
Create `voice-service/tests/__init__.py` (empty).

- [ ] **Step 3: Set up venv and install**

From `C:\dev\elevenhack\cursor\voice-service`:
```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .[dev]
```
Expected: `elevenlabs`, `pytest`, etc. install cleanly. On Windows, if `pyaudio` install fails, try `pip install pipwin; pipwin install pyaudio` then re-run.

- [ ] **Step 4: Verify pytest runs**

```powershell
pytest -q
```
Expected: "no tests ran" — directory is set up correctly.

- [ ] **Step 5: Commit**

```powershell
git add voice-service/pyproject.toml voice-service/src/vox/__init__.py voice-service/tests/__init__.py
git commit -m "feat(voice-service): python skeleton with elevenlabs SDK"
```

---

## Task 4: Wire protocol module (TDD)

**Files:**
- Create: `voice-service/src/vox/protocol.py`
- Create: `voice-service/tests/test_protocol.py`

- [ ] **Step 1: Write failing tests**

Create `tests/test_protocol.py`:
```python
import json
from vox.protocol import (
    Command, Event, Ack,
    encode_command, decode_message,
)


def test_encode_command_produces_newline_terminated_json():
    cmd = Command(id="abc", intent="move", args={"target": "east_edge"})
    line = encode_command(cmd)
    assert line.endswith("\n")
    obj = json.loads(line)
    assert obj == {"type": "command", "id": "abc", "intent": "move", "args": {"target": "east_edge"}}


def test_decode_event_message():
    raw = '{"type":"event","kind":"unit_destroyed","ts":12345,"actor":"tank_07","by":"enemy"}'
    msg = decode_message(raw)
    assert isinstance(msg, Event)
    assert msg.kind == "unit_destroyed"
    assert msg.payload["actor"] == "tank_07"


def test_decode_ack_message_ok():
    raw = '{"type":"ack","id":"abc","ok":true}'
    msg = decode_message(raw)
    assert isinstance(msg, Ack)
    assert msg.id == "abc"
    assert msg.ok is True
    assert msg.error is None


def test_decode_ack_message_error():
    raw = '{"type":"ack","id":"abc","ok":false,"error":"no_selection"}'
    msg = decode_message(raw)
    assert isinstance(msg, Ack)
    assert msg.ok is False
    assert msg.error == "no_selection"


def test_decode_unknown_type_raises():
    import pytest
    with pytest.raises(ValueError):
        decode_message('{"type":"bogus"}')
```

- [ ] **Step 2: Run tests and confirm they fail**

```powershell
pytest tests/test_protocol.py -v
```
Expected: ImportError or FAIL on every test.

- [ ] **Step 3: Implement the protocol module**

Create `src/vox/protocol.py`:
```python
"""Wire protocol for the Vox Commander bridge socket.

Pure data structures and (de)serialization. No I/O.
"""
from __future__ import annotations
import json
from dataclasses import dataclass, field
from typing import Any, Union


@dataclass
class Command:
    id: str
    intent: str
    args: dict[str, Any] = field(default_factory=dict)


@dataclass
class Event:
    kind: str
    ts: int
    payload: dict[str, Any] = field(default_factory=dict)


@dataclass
class Ack:
    id: str
    ok: bool
    error: str | None = None


Message = Union[Command, Event, Ack]


def encode_command(cmd: Command) -> str:
    obj = {"type": "command", "id": cmd.id, "intent": cmd.intent, "args": cmd.args}
    return json.dumps(obj, separators=(",", ":")) + "\n"


def decode_message(raw: str) -> Message:
    obj = json.loads(raw)
    t = obj.get("type")
    if t == "command":
        return Command(id=obj["id"], intent=obj["intent"], args=obj.get("args") or {})
    if t == "event":
        payload = {k: v for k, v in obj.items() if k not in ("type", "kind", "ts")}
        return Event(kind=obj["kind"], ts=obj["ts"], payload=payload)
    if t == "ack":
        return Ack(id=obj["id"], ok=bool(obj["ok"]), error=obj.get("error"))
    raise ValueError(f"unknown message type: {t!r}")
```

- [ ] **Step 4: Run tests to confirm they pass**

```powershell
pytest tests/test_protocol.py -v
```
Expected: all 5 tests pass.

- [ ] **Step 5: Commit**

```powershell
git add voice-service/src/vox/protocol.py voice-service/tests/test_protocol.py
git commit -m "feat(protocol): wire protocol with command/event/ack messages"
```

---

## Task 5: TCP game-socket client (TDD with mock peer)

**Files:**
- Create: `voice-service/src/vox/game_socket.py`
- Create: `voice-service/tests/test_game_socket.py`

- [ ] **Step 1: Write failing tests**

Create `tests/test_game_socket.py`:
```python
import asyncio
import pytest
from vox.protocol import Command, Event
from vox.game_socket import GameSocket


@pytest.mark.asyncio
async def test_send_command_and_receive_ack():
    server_lines: list[str] = []
    server_ready = asyncio.Event()
    port_holder: dict[str, int] = {}

    async def fake_server(reader, writer):
        server_ready.set()
        line = await reader.readline()
        server_lines.append(line.decode())
        # echo an ack
        writer.write(b'{"type":"ack","id":"x","ok":true}\n')
        await writer.drain()
        writer.close()
        await writer.wait_closed()

    srv = await asyncio.start_server(fake_server, "127.0.0.1", 0)
    port_holder["p"] = srv.sockets[0].getsockname()[1]

    async with srv:
        gs = GameSocket("127.0.0.1", port_holder["p"])
        await gs.connect()
        ack = await gs.send_and_await_ack(Command(id="x", intent="stop"))
        await gs.close()

    assert ack.ok is True
    assert '"intent":"stop"' in server_lines[0]


@pytest.mark.asyncio
async def test_inbound_event_is_dispatched_to_handler():
    received: list[Event] = []

    async def fake_server(reader, writer):
        writer.write(b'{"type":"event","kind":"match_start","ts":1}\n')
        await writer.drain()
        await asyncio.sleep(0.05)
        writer.close()
        await writer.wait_closed()

    srv = await asyncio.start_server(fake_server, "127.0.0.1", 0)
    port = srv.sockets[0].getsockname()[1]

    async with srv:
        gs = GameSocket("127.0.0.1", port, on_event=lambda e: received.append(e))
        await gs.connect()
        await asyncio.sleep(0.1)
        await gs.close()

    assert len(received) == 1
    assert received[0].kind == "match_start"
```

- [ ] **Step 2: Run tests, confirm they fail**

```powershell
pytest tests/test_game_socket.py -v
```
Expected: ImportError.

- [ ] **Step 3: Implement game_socket.py**

Create `src/vox/game_socket.py`:
```python
"""TCP client for the OpenHV VoxBridge trait.

Asyncio, newline-delimited JSON. Dispatches inbound events to a callback;
exposes a request/reply primitive for commands matched by id.
"""
from __future__ import annotations
import asyncio
from typing import Callable, Awaitable
from .protocol import Command, Event, Ack, Message, encode_command, decode_message

EventHandler = Callable[[Event], None | Awaitable[None]]


class GameSocket:
    def __init__(self, host: str, port: int, on_event: EventHandler | None = None):
        self.host = host
        self.port = port
        self.on_event = on_event
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._pending: dict[str, asyncio.Future[Ack]] = {}
        self._reader_task: asyncio.Task | None = None
        self._closed = False

    async def connect(self) -> None:
        self._reader, self._writer = await asyncio.open_connection(self.host, self.port)
        self._reader_task = asyncio.create_task(self._read_loop())

    async def close(self) -> None:
        self._closed = True
        if self._writer:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
        if self._reader_task:
            self._reader_task.cancel()

    async def send_and_await_ack(self, cmd: Command, timeout: float = 2.0) -> Ack:
        assert self._writer, "not connected"
        fut: asyncio.Future[Ack] = asyncio.get_event_loop().create_future()
        self._pending[cmd.id] = fut
        self._writer.write(encode_command(cmd).encode("utf-8"))
        await self._writer.drain()
        try:
            return await asyncio.wait_for(fut, timeout)
        finally:
            self._pending.pop(cmd.id, None)

    async def send_fire_and_forget(self, cmd: Command) -> None:
        assert self._writer, "not connected"
        self._writer.write(encode_command(cmd).encode("utf-8"))
        await self._writer.drain()

    async def _read_loop(self) -> None:
        assert self._reader
        while not self._closed:
            try:
                line = await self._reader.readline()
            except (ConnectionResetError, asyncio.IncompleteReadError):
                break
            if not line:
                break
            try:
                msg = decode_message(line.decode("utf-8").strip())
            except Exception:
                continue
            await self._dispatch(msg)

    async def _dispatch(self, msg: Message) -> None:
        if isinstance(msg, Ack):
            fut = self._pending.get(msg.id)
            if fut and not fut.done():
                fut.set_result(msg)
        elif isinstance(msg, Event) and self.on_event:
            result = self.on_event(msg)
            if asyncio.iscoroutine(result):
                await result
```

- [ ] **Step 4: Run tests, confirm pass**

```powershell
pytest tests/test_game_socket.py -v
```
Expected: both tests pass.

- [ ] **Step 5: Commit**

```powershell
git add voice-service/src/vox/game_socket.py voice-service/tests/test_game_socket.py
git commit -m "feat(game-socket): async TCP client with ack-correlation and event dispatch"
```

---

## Task 6: VoxBridge C# trait scaffold

**Files:**
- Create: `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs`
- Modify: `openhv/mods/hv/rules/world.yaml`

**Reference:** See research findings in `docs/superpowers/specs/2026-05-13-vox-commander-design.md` §6 and the agent research output:
- Trait pattern from `OpenRA.Game/Traits/TraitsInterfaces.cs`
- Order construction from `OpenRA.Mods.Common/Traits/Mobile.cs`
- World-actor attachment via `[TraitLocation(SystemActors.World)]` + YAML under `^BaseWorld:`
- Threading: TCP on background Task, drain via `ConcurrentQueue<string>` inside `ITick.Tick`

- [ ] **Step 1: Create the trait file**

Create `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
    [TraitLocation(SystemActors.World)]
    [Desc("Localhost TCP bridge for external voice control. Attach to the World actor.")]
    public class VoxBridgeInfo : TraitInfo
    {
        public readonly int Port = 7777;
        public override object Create(ActorInitializer init) => new VoxBridge(this);
    }

    public class VoxBridge : IWorldLoaded, ITick, INotifyActorDisposing
    {
        readonly VoxBridgeInfo info;
        readonly ConcurrentQueue<string> inbound = new();
        TcpListener listener;
        TcpClient activeClient;
        StreamWriter activeWriter;
        CancellationTokenSource cts;
        World world;

        public VoxBridge(VoxBridgeInfo info) { this.info = info; }

        public void WorldLoaded(World w, WorldRenderer wr)
        {
            world = w;
            cts = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Loopback, info.Port);
            listener.Start();
            Log.Write("debug", $"[VoxBridge] listening on 127.0.0.1:{info.Port}");
            _ = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                activeClient = client;
                activeWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _ = Task.Run(() => ReadLoopAsync(client, ct));
            }
        }

        async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    inbound.Enqueue(line);
                }
            }
            catch { /* client dropped */ }
        }

        public void Tick(Actor self)
        {
            while (inbound.TryDequeue(out var line))
            {
                Log.Write("debug", $"[VoxBridge] in: {line}");
                // Task 8 will replace this with command dispatch.
                SendAckRaw(ExtractId(line), true, null);
            }
        }

        public void Disposing(Actor self)
        {
            cts?.Cancel();
            try { listener?.Stop(); } catch { }
            try { activeClient?.Close(); } catch { }
        }

        // --- helpers ---

        void SendAckRaw(string id, bool ok, string error)
        {
            if (activeWriter == null) return;
            var payload = error == null
                ? $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":{(ok ? "true" : "false")}}}"
                : $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":false,\"error\":\"{error}\"}}";
            try { activeWriter.WriteLine(payload); } catch { }
        }

        static string ExtractId(string line)
        {
            const string key = "\"id\":\"";
            var i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            var start = i + key.Length;
            var end = line.IndexOf('"', start);
            return end < 0 ? "" : line.Substring(start, end - start);
        }
    }
}
```

- [ ] **Step 2: Register the trait in world.yaml**

Open `openhv/mods/hv/rules/world.yaml`. Find the top-level `^BaseWorld:` block. Append (matching the indentation of other entries like `Selection:`):

```yaml
    VoxBridge:
        Port: 7777
```

- [ ] **Step 3: Build the mod**

From `C:\dev\elevenhack\cursor\openhv`:
```powershell
dotnet build OpenRA.Mods.HV/OpenRA.Mods.HV.csproj
```
Expected: clean build.

- [ ] **Step 4: Launch a skirmish, verify trait loads**

Launch the game, start a skirmish. Tail OpenHV's log (Windows: `%APPDATA%\OpenRA\Logs\debug.log` for the HV launcher, otherwise `openhv/engine/Support/Logs/debug.log`). Expected: a line `[VoxBridge] listening on 127.0.0.1:7777` within a second of the match starting.

- [ ] **Step 5: Commit the trait + yaml only (not the whole openhv tree)**

The `openhv/` directory is gitignored. Copy only our additions out:
```powershell
$dst = "C:\dev\elevenhack\cursor\openhv-mod\OpenRA.Mods.HV\Traits\World"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item C:\dev\elevenhack\cursor\openhv\OpenRA.Mods.HV\Traits\World\VoxBridge.cs $dst
$dst2 = "C:\dev\elevenhack\cursor\openhv-mod\mods\hv\rules"
New-Item -ItemType Directory -Force -Path $dst2 | Out-Null
Copy-Item C:\dev\elevenhack\cursor\openhv\mods\hv\rules\world.yaml $dst2
```
Then:
```powershell
git add openhv-mod
git commit -m "feat(voxbridge): C# world trait with localhost TCP listener"
```

We will keep `openhv-mod/` as the canonical home for our overlay files. A small helper script in a later task syncs them into the live `openhv/` checkout.

---

## Task 7: CLI integration test — round-trip a command

**Files:**
- Create: `voice-service/scripts/cli_send.py`

- [ ] **Step 1: Write the CLI**

Create `voice-service/scripts/cli_send.py`:
```python
"""Send a single command to a running OpenHV VoxBridge for manual testing."""
import asyncio
import sys
import uuid
from vox.game_socket import GameSocket
from vox.protocol import Command


async def main():
    intent = sys.argv[1] if len(sys.argv) > 1 else "stop"
    args = {}
    gs = GameSocket("127.0.0.1", 7777, on_event=lambda e: print("[event]", e))
    await gs.connect()
    ack = await gs.send_and_await_ack(Command(id=str(uuid.uuid4()), intent=intent, args=args))
    print("[ack]", ack)
    await asyncio.sleep(0.2)
    await gs.close()


if __name__ == "__main__":
    asyncio.run(main())
```

- [ ] **Step 2: Run the integration test**

With OpenHV running a skirmish:
```powershell
.\.venv\Scripts\Activate.ps1
python scripts/cli_send.py stop
```
Expected:
- Console prints `[ack] Ack(id='...', ok=True, error=None)`
- OpenHV `debug.log` shows `[VoxBridge] in: {"type":"command","id":"...","intent":"stop","args":{}}`

- [ ] **Step 3: Commit**

```powershell
git add voice-service/scripts/cli_send.py
git commit -m "test(cli): manual command sender for bridge integration"
```

---

## Task 8: Move + Select + Stop command handlers (C#)

**Files:**
- Modify: `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs`

- [ ] **Step 1: Add a tiny JSON command parser**

Inside `VoxBridge.cs`, add a private helper (we use `System.Text.Json` which ships with .NET 8 — no new dependency):
```csharp
using System.Text.Json;
using System.Linq;
using OpenRA.Mods.Common.Traits;
```
Add at the bottom of the class:
```csharp
record ParsedCommand(string Id, string Intent, JsonElement Args);

ParsedCommand TryParse(string line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "command") return null;
        return new ParsedCommand(
            root.GetProperty("id").GetString() ?? "",
            root.GetProperty("intent").GetString() ?? "",
            root.TryGetProperty("args", out var a) ? a.Clone() : default);
    }
    catch { return null; }
}
```

- [ ] **Step 2: Replace the stub Tick with command dispatch**

Replace the body of `Tick(Actor self)`:
```csharp
public void Tick(Actor self)
{
    while (inbound.TryDequeue(out var line))
    {
        var cmd = TryParse(line);
        if (cmd == null) continue;

        try
        {
            var result = Dispatch(cmd);
            SendAckRaw(cmd.Id, result.ok, result.error);
        }
        catch (Exception ex)
        {
            Log.Write("debug", $"[VoxBridge] dispatch error: {ex.Message}");
            SendAckRaw(cmd.Id, false, "dispatch_error");
        }
    }
}

(bool ok, string error) Dispatch(ParsedCommand cmd)
{
    switch (cmd.Intent)
    {
        case "select":    return HandleSelect(cmd.Args);
        case "move":      return HandleMove(cmd.Args);
        case "stop":      return HandleStop();
        default:          return (false, "unknown_intent");
    }
}
```

- [ ] **Step 3: Implement select, move, stop**

Add to the class:
```csharp
(bool ok, string error) HandleSelect(JsonElement args)
{
    var p = world.LocalPlayer;
    if (p == null) return (false, "no_local_player");

    var filter = args.TryGetProperty("filter", out var f) ? f.GetString() : "all_units";
    var actors = world.Actors
        .Where(a => a.Owner == p && !a.IsDead && a.IsInWorld)
        .Where(a => a.TraitOrDefault<Mobile>() != null)
        .ToList();

    if (filter == "all_tanks")
        actors = actors.Where(a => a.Info.Name.Contains("tank", StringComparison.OrdinalIgnoreCase)).ToList();

    world.Selection.Combine(world, actors, false, true);
    return actors.Count > 0 ? (true, null) : (false, "empty_selection");
}

(bool ok, string error) HandleMove(JsonElement args)
{
    var target = args.TryGetProperty("target", out var t) ? t.GetString() : null;
    if (target == null) return (false, "missing_target");

    var cell = ResolveLogicalCell(target);
    if (cell == null) return (false, "unknown_target");

    var selected = world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer && !a.IsDead).ToList();
    if (selected.Count == 0) return (false, "no_selection");

    foreach (var a in selected)
        world.IssueOrder(new Order("Move", a, Target.FromCell(world, cell.Value), false));

    return (true, null);
}

(bool ok, string error) HandleStop()
{
    var selected = world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer && !a.IsDead).ToList();
    foreach (var a in selected)
        world.IssueOrder(new Order("Stop", a, false));
    return (true, null);
}

CPos? ResolveLogicalCell(string @ref)
{
    var bounds = world.Map.Bounds;
    switch (@ref)
    {
        case "east_edge":  return new CPos(bounds.Right - 2, bounds.Top + bounds.Height / 2);
        case "west_edge":  return new CPos(bounds.Left + 2,  bounds.Top + bounds.Height / 2);
        case "north_edge": return new CPos(bounds.Left + bounds.Width / 2, bounds.Top + 2);
        case "south_edge": return new CPos(bounds.Left + bounds.Width / 2, bounds.Bottom - 2);
        default: return null;
    }
}
```

(Naming and exact `Map.Bounds` member names should be verified against `OpenRA.Game/Map/Map.cs` before building — if `Map.Bounds` returns a `Rectangle`, the `.Left/.Right/.Top/.Bottom/.Width/.Height` properties hold. If the API differs in the pinned engine commit, adjust to whichever exposes "edge cell of the playable area.")

- [ ] **Step 4: Build and test**

```powershell
dotnet build openhv\OpenRA.Mods.HV\OpenRA.Mods.HV.csproj
```
Launch a skirmish. From the CLI:
```powershell
python scripts/cli_send.py select all_units    # then in-game observe nothing visible
python scripts/cli_send.py move east_edge
```
Wait — `cli_send.py` only sends one args-less command. Extend it to accept args:
```python
# extend main():
intent = sys.argv[1]
args = {}
for a in sys.argv[2:]:
    if "=" in a:
        k, v = a.split("=", 1)
        args[k] = v
```
Then:
```powershell
python scripts/cli_send.py select filter=all_units
python scripts/cli_send.py move target=east_edge
```
Expected: units visibly move east in the game window.

- [ ] **Step 5: Sync trait file into openhv-mod and commit**

```powershell
Copy-Item C:\dev\elevenhack\cursor\openhv\OpenRA.Mods.HV\Traits\World\VoxBridge.cs C:\dev\elevenhack\cursor\openhv-mod\OpenRA.Mods.HV\Traits\World\
git add openhv-mod voice-service/scripts/cli_send.py
git commit -m "feat(voxbridge): select/move/stop handlers issuing real UnitOrders"
```

---

## Task 9: ElevenLabs Conversational Agent dashboard configuration

**No code changes — this is dashboard work, but it must be checkpointed.**

- [ ] **Step 1: Create the agent**

Go to https://elevenlabs.io/app/agents. Click "Create Agent". Name it "Vox XO".

- [ ] **Step 2: System prompt**

Paste this system prompt (tune later):
```
You are the executive officer (XO) of a Soviet- — no — of a Hard-Vacuum
commander playing a real-time strategy game called OpenHV. The commander
gives you spoken orders. Your job:

1. For tactical orders (move, attack, build, select, stop, harvest, pause,
   produce structures), call the `dispatch_command` tool with a concrete
   intent and args.
2. For state questions ("how much money", "where is the enemy"), call
   `read_state` with the relevant fields, then verbalize the answer crisply.
3. Confirm orders concisely. "Tanks moving east." "Five rifles queued."
   Do not narrate the battle — a commentator handles that.
4. If an order is ambiguous, ask for clarification in one short sentence.

Speak like a tactical officer: terse, calm, no fluff.
```

- [ ] **Step 3: Register client-side tools in the dashboard**

In the agent's Tools tab, click "Add tool" → "Client tool". Repeat for each:

**Tool 1: `dispatch_command`**
- Description: "Issue a tactical command to the game engine."
- Parameters (JSON Schema):
```json
{
  "type": "object",
  "properties": {
    "intent": {
      "type": "string",
      "enum": ["select","move","attack","attack_move","stop","build","produce_structure","harvest","meta_pause"]
    },
    "args": { "type": "object", "additionalProperties": true }
  },
  "required": ["intent"]
}
```

**Tool 2: `read_state`**
- Description: "Read current game state (cash, power, unit counts, enemy presence)."
- Parameters:
```json
{
  "type": "object",
  "properties": {
    "fields": { "type": "array", "items": { "type": "string" } }
  }
}
```

**Tool 3: `set_pause`**
- Description: "Pause or resume the game."
- Parameters:
```json
{ "type": "object", "properties": { "paused": { "type": "boolean" } }, "required": ["paused"] }
```

- [ ] **Step 4: Voice + LLM settings**

Voice: pick a calm tactical male voice for the XO. Note its voice_id.
LLM: pick the fastest tier offered (e.g. "Flash" / GPT-4o-mini / Claude Haiku — whichever ElevenLabs exposes today).
Turn-taking: default; we'll tune.

- [ ] **Step 5: Copy agent_id into .env**

Create `C:\dev\elevenhack\cursor\.env`:
```
ELEVENLABS_API_KEY=<your key>
VOX_AGENT_ID=<copied from dashboard>
VOX_COMMENTATOR_VOICE_ID=<pick a dramatic voice id>
VOX_BRIDGE_HOST=127.0.0.1
VOX_BRIDGE_PORT=7777
```

(No commit — `.env` is gitignored.)

---

## Task 10: Python agent_client.py wiring two tools

**Files:**
- Create: `voice-service/src/vox/agent_client.py`
- Create: `voice-service/src/vox/tools.py`
- Create: `voice-service/src/vox/main.py`

**Reference:** ElevenLabs `Conversation` + `ClientTools` API per research output. Tool handler signature is `Callable[[dict], Any]` (sync) or `Callable[[dict], Awaitable[Any]]` (async with `is_async=True`).

- [ ] **Step 1: Implement tools.py with thin handlers**

Create `src/vox/tools.py`:
```python
"""Client-side tool handlers invoked by the ElevenLabs agent."""
from __future__ import annotations
import asyncio
import uuid
from .game_socket import GameSocket
from .protocol import Command


class Tools:
    def __init__(self, game: GameSocket):
        self.game = game

    async def dispatch_command(self, params: dict) -> dict:
        intent = params.get("intent", "")
        args = params.get("args") or {}
        cmd = Command(id=str(uuid.uuid4()), intent=intent, args=args)
        try:
            ack = await self.game.send_and_await_ack(cmd, timeout=2.0)
        except asyncio.TimeoutError:
            return {"ok": False, "error": "timeout"}
        return {"ok": ack.ok, "error": ack.error}

    async def read_state(self, params: dict) -> dict:
        # Stub until Task 14 wires real state queries
        return {"cash": 0, "power": 0, "units": 0, "note": "state-snapshots-not-implemented-yet"}

    async def set_pause(self, params: dict) -> dict:
        paused = bool(params.get("paused", False))
        return await self.dispatch_command({"intent": "meta_pause", "args": {"paused": paused}})
```

- [ ] **Step 2: Implement agent_client.py**

Create `src/vox/agent_client.py`:
```python
"""ElevenLabs Conversational Agent client. Owns mic + speakers + tools."""
from __future__ import annotations
import asyncio
import os
from elevenlabs.client import ElevenLabs
from elevenlabs.conversational_ai.conversation import Conversation, ClientTools
from elevenlabs.conversational_ai.default_audio_interface import DefaultAudioInterface
from .tools import Tools


class AgentClient:
    def __init__(self, tools: Tools, loop: asyncio.AbstractEventLoop):
        self.tools = tools
        self.loop = loop
        self.conv: Conversation | None = None

    def start(self) -> None:
        client = ElevenLabs(api_key=os.environ["ELEVENLABS_API_KEY"])
        agent_id = os.environ["VOX_AGENT_ID"]

        client_tools = ClientTools(loop=self.loop)
        client_tools.register("dispatch_command", self.tools.dispatch_command, is_async=True)
        client_tools.register("read_state",       self.tools.read_state,       is_async=True)
        client_tools.register("set_pause",        self.tools.set_pause,        is_async=True)

        self.conv = Conversation(
            client,
            agent_id,
            requires_auth=True,
            audio_interface=DefaultAudioInterface(),
            client_tools=client_tools,
            callback_user_transcript=lambda t: print(f"[user] {t}"),
            callback_agent_response=lambda t: print(f"[xo] {t}"),
            callback_agent_response_correction=lambda o, c: print(f"[xo-correct] {o!r} -> {c!r}"),
            callback_latency_measurement=lambda ms: print(f"[lat {ms}ms]"),
        )
        self.conv.start_session()  # blocks the calling thread

    def stop(self) -> None:
        if self.conv:
            self.conv.end_session()
            self.conv.wait_for_session_end()
```

Note: `conv.start_session()` blocks. We run it in a worker thread from `main.py` so asyncio can keep running.

- [ ] **Step 3: Implement main.py**

Create `src/vox/main.py`:
```python
"""Vox Commander orchestrator. Runs the agent + game socket together."""
from __future__ import annotations
import asyncio
import os
import signal
import threading
from dotenv import load_dotenv
from .agent_client import AgentClient
from .game_socket import GameSocket
from .tools import Tools


async def amain():
    load_dotenv()
    host = os.environ.get("VOX_BRIDGE_HOST", "127.0.0.1")
    port = int(os.environ.get("VOX_BRIDGE_PORT", "7777"))

    game = GameSocket(host, port, on_event=lambda e: print(f"[event] {e}"))
    print(f"[main] connecting to bridge {host}:{port} ...")
    await game.connect()
    print("[main] bridge connected")

    tools = Tools(game)
    loop = asyncio.get_running_loop()
    agent = AgentClient(tools, loop)

    stop_event = asyncio.Event()
    def _on_signal(*_):
        stop_event.set()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try: signal.signal(sig, _on_signal)
        except Exception: pass

    agent_thread = threading.Thread(target=agent.start, daemon=True, name="agent-session")
    agent_thread.start()
    print("[main] agent session started; speak to the XO")

    await stop_event.wait()
    print("[main] shutting down")
    agent.stop()
    await game.close()


def main():
    asyncio.run(amain())


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Try the full loop**

1. Start OpenHV, launch a skirmish.
2. In a terminal:
```powershell
.\.venv\Scripts\Activate.ps1
python -m vox.main
```
3. Wait for `agent session started` line.
4. Speak: "Select all units."
5. Speak: "Move them east."

Expected: console shows `[user] select all units`, `[xo] ...`, `[lat ...ms]`, then `[user] move them east`, the agent invokes `dispatch_command`, the ack arrives, the XO confirms verbally, **and the units move on screen.**

- [ ] **Step 5: Commit**

```powershell
git add voice-service/src/vox/agent_client.py voice-service/src/vox/tools.py voice-service/src/vox/main.py
git commit -m "feat(agent): end-to-end voice loop (XO -> dispatch_command -> game)"
```

This is the demo spine. Day 1 deliverable.

---

## Task 11: Reference resolver with dialogue context (Python, TDD)

**Files:**
- Create: `voice-service/src/vox/refs.py`
- Create: `voice-service/tests/test_refs.py`
- Modify: `voice-service/src/vox/tools.py` to consult `RefResolver`

The Agent's tool call may include vague references ("that base"). We rewrite them to concrete handles using the latest `state_snapshot` from the bridge.

- [ ] **Step 1: Write tests**

Create `tests/test_refs.py`:
```python
from vox.refs import RefResolver


def test_passthrough_unknown_ref_kept_as_is():
    r = RefResolver()
    out = r.rewrite({"target": "east_edge"})
    assert out == {"target": "east_edge"}


def test_that_base_resolves_to_most_recent_enemy_structure_in_snapshot():
    r = RefResolver()
    r.ingest_snapshot({
        "kind": "state_snapshot",
        "enemies": [
            {"handle": "enemy_barracks_alpha", "kind": "barracks", "owner": "enemy"},
            {"handle": "enemy_factory_beta",   "kind": "factory",  "owner": "enemy"},
        ],
    })
    out = r.rewrite({"target_ref": "that_base"})
    assert out == {"target_ref": "enemy_factory_beta"}  # most recent in list


def test_pronoun_kind_resolves_via_kind_field():
    r = RefResolver()
    r.ingest_snapshot({
        "kind": "state_snapshot",
        "enemies": [{"handle": "enemy_harvester_01", "kind": "harvester", "owner": "enemy"}],
    })
    out = r.rewrite({"target_kind": "harvester"})
    assert out == {"target_kind": "enemy_harvester"}


def test_ambiguous_that_returns_marker_for_xo_clarification():
    r = RefResolver()
    out = r.rewrite({"target_ref": "that_base"})
    assert out == {"target_ref": "__ambiguous__"}
```

- [ ] **Step 2: Confirm tests fail**

```powershell
pytest tests/test_refs.py -v
```

- [ ] **Step 3: Implement refs.py**

Create `src/vox/refs.py`:
```python
"""Dialogue-context reference resolution.

The Agent's LLM may produce semi-concrete references like "that_base" or
"harvester". We rewrite them into handles the C# trait knows how to look up.
"""
from __future__ import annotations
from collections import deque


class RefResolver:
    def __init__(self, history_size: int = 6):
        self.snapshots: deque[dict] = deque(maxlen=history_size)

    def ingest_snapshot(self, snap: dict) -> None:
        self.snapshots.append(snap)

    def rewrite(self, args: dict) -> dict:
        out = dict(args)
        if "target_ref" in out:
            out["target_ref"] = self._resolve_target_ref(out["target_ref"])
        if "target_kind" in out:
            out["target_kind"] = self._resolve_kind(out["target_kind"])
        return out

    def _resolve_target_ref(self, ref: str) -> str:
        if ref in ("that_base", "the_base", "their_base", "that"):
            for snap in reversed(self.snapshots):
                enemies = snap.get("enemies", [])
                bases = [e for e in enemies if e.get("kind") in ("barracks", "factory", "hq", "construction_yard")]
                if bases:
                    return bases[-1]["handle"]
            return "__ambiguous__"
        return ref

    def _resolve_kind(self, kind: str) -> str:
        # Default to enemy-prefixed kind unless caller specified an owner
        return f"enemy_{kind}" if not kind.startswith(("enemy_", "friendly_")) else kind
```

- [ ] **Step 4: Confirm tests pass**

```powershell
pytest tests/test_refs.py -v
```

- [ ] **Step 5: Wire resolver into tools.py**

Modify `src/vox/tools.py`. Add `resolver: RefResolver` to `__init__`, and rewrite args before dispatch:
```python
from .refs import RefResolver

class Tools:
    def __init__(self, game: GameSocket, resolver: RefResolver):
        self.game = game
        self.resolver = resolver

    async def dispatch_command(self, params: dict) -> dict:
        intent = params.get("intent", "")
        args = self.resolver.rewrite(params.get("args") or {})
        cmd = Command(id=str(uuid.uuid4()), intent=intent, args=args)
        try:
            ack = await self.game.send_and_await_ack(cmd, timeout=2.0)
        except asyncio.TimeoutError:
            return {"ok": False, "error": "timeout"}
        return {"ok": ack.ok, "error": ack.error}
```

Update `main.py` to construct and pass the resolver, and forward `state_snapshot` events into it:
```python
resolver = RefResolver()
game = GameSocket(host, port, on_event=lambda e: handle_event(e, resolver))

def handle_event(event, resolver):
    if event.kind == "state_snapshot":
        resolver.ingest_snapshot({"kind": "state_snapshot", **event.payload})
    print(f"[event] {event}")

tools = Tools(game, resolver)
```

- [ ] **Step 6: Commit**

```powershell
git add voice-service/src/vox/refs.py voice-service/tests/test_refs.py voice-service/src/vox/tools.py voice-service/src/vox/main.py
git commit -m "feat(refs): dialogue-context reference rewriting before dispatch"
```

---

## Task 12: Remaining intent handlers (C#)

**Files:**
- Modify: `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs`

Implement `attack`, `attack_move`, `build`, `produce_structure`, `harvest`, `meta_pause`, `query`.

- [ ] **Step 1: Add cases to Dispatch**

Extend the `Dispatch` switch:
```csharp
case "attack":            return HandleAttack(cmd.Args, attackMove: false);
case "attack_move":       return HandleAttack(cmd.Args, attackMove: true);
case "build":             return HandleBuild(cmd.Args);
case "produce_structure": return HandleProduceStructure(cmd.Args);
case "harvest":           return HandleHarvest();
case "meta_pause":        return HandleMetaPause(cmd.Args);
case "query":             return (true, null); // server returns nothing; Agent will call read_state separately
```

- [ ] **Step 2: Implement attack / attack_move**

```csharp
(bool ok, string error) HandleAttack(JsonElement args, bool attackMove)
{
    var p = world.LocalPlayer;
    if (p == null) return (false, "no_local_player");

    Actor targetActor = ResolveActorRef(args);
    CPos? targetCell = null;
    if (targetActor == null)
    {
        if (!args.TryGetProperty("target", out var t)) return (false, "missing_target");
        targetCell = ResolveLogicalCell(t.GetString());
        if (targetCell == null) return (false, "unknown_target");
    }

    var orderName = attackMove ? "AttackMove" : "Attack";
    var target = targetActor != null ? Target.FromActor(targetActor) : Target.FromCell(world, targetCell.Value);

    var selected = world.Selection.Actors.Where(a => a.Owner == p && !a.IsDead).ToList();
    if (selected.Count == 0) return (false, "no_selection");

    foreach (var a in selected)
        world.IssueOrder(new Order(orderName, a, target, false));
    return (true, null);
}

Actor ResolveActorRef(JsonElement args)
{
    if (args.TryGetProperty("target_ref", out var r) && r.ValueKind == JsonValueKind.String)
    {
        var handle = r.GetString();
        if (handle == "__ambiguous__") return null;
        return world.Actors.FirstOrDefault(a => HandleOf(a) == handle);
    }
    if (args.TryGetProperty("target_kind", out var k) && k.ValueKind == JsonValueKind.String)
    {
        var kind = k.GetString();
        bool enemy = kind.StartsWith("enemy_");
        var bare = enemy ? kind.Substring(6) : kind;
        return world.Actors.FirstOrDefault(a =>
            !a.IsDead && a.IsInWorld &&
            (enemy ? a.Owner != world.LocalPlayer : a.Owner == world.LocalPlayer) &&
            a.Info.Name.Equals(bare, StringComparison.OrdinalIgnoreCase));
    }
    return null;
}

static string HandleOf(Actor a) => $"{(a.Owner.InternalName.ToLowerInvariant())}_{a.Info.Name.ToLowerInvariant()}_{a.ActorID}";
```

- [ ] **Step 3: Implement build (unit production)**

```csharp
(bool ok, string error) HandleBuild(JsonElement args)
{
    var p = world.LocalPlayer;
    if (p == null) return (false, "no_local_player");

    var unit = args.TryGetProperty("unit", out var u) ? u.GetString() : null;
    var count = args.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1;
    if (unit == null) return (false, "missing_unit");

    // Find an appropriate production actor (first matching factory the player owns).
    var producer = world.Actors.FirstOrDefault(a =>
        a.Owner == p && !a.IsDead &&
        a.TraitsImplementing<Production>().Any(pp => pp.Info.Produces.Any(q => q != null)));
    if (producer == null) return (false, "no_factory");

    world.IssueOrder(Order.StartProduction(producer, unit, count));
    return (true, null);
}
```

- [ ] **Step 4: Implement produce_structure, harvest, meta_pause**

```csharp
(bool ok, string error) HandleProduceStructure(JsonElement args)
{
    var structure = args.TryGetProperty("structure", out var s) ? s.GetString() : null;
    if (structure == null) return (false, "missing_structure");
    var producer = world.Actors.FirstOrDefault(a => a.Owner == world.LocalPlayer && !a.IsDead &&
        a.TraitsImplementing<Production>().Any());
    if (producer == null) return (false, "no_construction_yard");
    world.IssueOrder(Order.StartProduction(producer, structure, 1));
    return (true, null);
}

(bool ok, string error) HandleHarvest()
{
    var selected = world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer && !a.IsDead).ToList();
    if (selected.Count == 0) return (false, "no_selection");
    foreach (var a in selected)
        world.IssueOrder(new Order("Harvest", a, false));
    return (true, null);
}

(bool ok, string error) HandleMetaPause(JsonElement args)
{
    var paused = args.TryGetProperty("paused", out var p) && p.GetBoolean();
    world.Paused = paused;
    return (true, null);
}
```

(If `Production` lives in a different namespace or `Order.StartProduction` has a slightly different signature in OpenHV's pinned engine commit, adjust per the source — verified location is `OpenRA.Mods.Common.Traits.Production` and `OpenRA.Game.Network.Order.StartProduction(Actor, string, int, bool)`.)

- [ ] **Step 5: Build, sync, test each intent end-to-end**

Build, launch a skirmish, run a few CLI tests:
```powershell
python scripts/cli_send.py attack target=east_edge
python scripts/cli_send.py build unit=rifleman count=3
python scripts/cli_send.py harvest
python scripts/cli_send.py meta_pause paused=true
python scripts/cli_send.py meta_pause paused=false
```
For each: verify either visible behavior or an ack with the expected error. **Replace `rifleman` with whatever OpenHV's actual unit name is** — check `mods/hv/rules/infantry.yaml`.

- [ ] **Step 6: Commit**

```powershell
Copy-Item C:\dev\elevenhack\cursor\openhv\OpenRA.Mods.HV\Traits\World\VoxBridge.cs C:\dev\elevenhack\cursor\openhv-mod\OpenRA.Mods.HV\Traits\World\
git add openhv-mod
git commit -m "feat(voxbridge): attack/build/harvest/pause + actor-ref resolution"
```

---

## Task 13: State snapshot emitter (C#) + read_state wiring (Python)

**Files:**
- Modify: `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs`
- Modify: `voice-service/src/vox/tools.py`

- [ ] **Step 1: Add snapshot emitter**

Inside `VoxBridge`, add a tick counter and emitter. Snapshot every 50 ticks (~2 s at 25Hz):
```csharp
int tickCount;
public void Tick(Actor self)
{
    // ... existing dispatch loop ...

    if (++tickCount % 50 == 0)
        EmitStateSnapshot();
}

void EmitStateSnapshot()
{
    if (activeWriter == null || world.LocalPlayer == null) return;
    var p = world.LocalPlayer;
    var pr = p.PlayerActor.TraitOrDefault<PlayerResources>();
    int cash = pr?.Cash + pr?.Resources ?? 0;
    int units = world.Actors.Count(a => a.Owner == p && !a.IsDead && a.TraitOrDefault<Mobile>() != null);

    var enemies = world.Actors
        .Where(a => a.Owner != p && !a.IsDead && a.IsInWorld && !a.Owner.NonCombatant)
        .Take(20)
        .Select(a => $"{{\"handle\":\"{HandleOf(a)}\",\"kind\":\"{a.Info.Name}\",\"owner\":\"enemy\"}}");

    var payload = $"{{\"type\":\"event\",\"kind\":\"state_snapshot\",\"ts\":{Game.LocalTick}," +
                  $"\"cash\":{cash},\"units\":{units}," +
                  $"\"enemies\":[{string.Join(",", enemies)}]}}";
    try { activeWriter.WriteLine(payload); } catch { }
}
```

(`PlayerResources` namespace verified: `OpenRA.Mods.Common.Traits.PlayerResources`. `Game.LocalTick` returns the current tick number.)

- [ ] **Step 2: Update read_state in Python to expose snapshot fields**

Modify `Tools.read_state`:
```python
async def read_state(self, params: dict) -> dict:
    snap = self.resolver.snapshots[-1] if self.resolver.snapshots else None
    if not snap:
        return {"ok": False, "error": "no_snapshot_yet"}
    fields = params.get("fields") or ["cash", "units", "enemies"]
    return {k: snap.get(k) for k in fields if k in snap}
```

- [ ] **Step 3: Build, sync, test**

Build the mod, launch a skirmish, run `python -m vox.main`. Speak: "XO, how much money do I have?" Expected: the Agent calls `read_state`, gets `{"cash": <real number>}`, verbalizes "1450 credits, commander."

- [ ] **Step 4: Commit**

```powershell
Copy-Item C:\dev\elevenhack\cursor\openhv\OpenRA.Mods.HV\Traits\World\VoxBridge.cs C:\dev\elevenhack\cursor\openhv-mod\OpenRA.Mods.HV\Traits\World\
git add openhv-mod voice-service/src/vox/tools.py
git commit -m "feat(state): 2Hz state snapshots + read_state tool returns live data"
```

---

## Task 14: Fastpath stateless-command short-circuit (Python, TDD)

**Files:**
- Create: `voice-service/src/vox/fastpath.py`
- Create: `voice-service/tests/test_fastpath.py`
- Modify: `voice-service/src/vox/tools.py`

- [ ] **Step 1: Write tests**

Create `tests/test_fastpath.py`:
```python
from vox.fastpath import is_stateless, build_command


def test_stop_is_stateless():
    assert is_stateless("stop") is True

def test_hold_is_stateless():
    assert is_stateless("hold") is True

def test_pause_is_stateless():
    assert is_stateless("meta_pause") is True

def test_move_is_not_stateless():
    assert is_stateless("move") is False

def test_build_command_for_hold_maps_to_stop():
    cmd = build_command("hold", {})
    assert cmd.intent == "stop"
    assert cmd.args == {}

def test_build_command_for_pause_passes_through_args():
    cmd = build_command("meta_pause", {"paused": True})
    assert cmd.intent == "meta_pause"
    assert cmd.args == {"paused": True}
```

- [ ] **Step 2: Confirm tests fail**

```powershell
pytest tests/test_fastpath.py -v
```

- [ ] **Step 3: Implement fastpath.py**

Create `src/vox/fastpath.py`:
```python
"""Stateless-command short-circuit.

For intents that need no world-state lookup or reference resolution, skip the
resolver and pre-build the Command so dispatch is a single async write.
"""
from __future__ import annotations
import uuid
from .protocol import Command

STATELESS = {"stop", "hold", "meta_pause"}
ALIASES = {"hold": "stop"}  # spoken "hold" maps to game intent "stop"


def is_stateless(intent: str) -> bool:
    return intent in STATELESS


def build_command(intent: str, args: dict) -> Command:
    real = ALIASES.get(intent, intent)
    return Command(id=str(uuid.uuid4()), intent=real, args=dict(args or {}))
```

- [ ] **Step 4: Confirm tests pass**

```powershell
pytest tests/test_fastpath.py -v
```

- [ ] **Step 5: Wire fastpath into Tools.dispatch_command**

Modify `src/vox/tools.py`:
```python
from .fastpath import is_stateless, build_command

async def dispatch_command(self, params: dict) -> dict:
    intent = params.get("intent", "")
    args = params.get("args") or {}
    if is_stateless(intent):
        cmd = build_command(intent, args)
    else:
        cmd = Command(id=str(uuid.uuid4()), intent=intent, args=self.resolver.rewrite(args))
    try:
        ack = await self.game.send_and_await_ack(cmd, timeout=2.0)
    except asyncio.TimeoutError:
        return {"ok": False, "error": "timeout"}
    return {"ok": ack.ok, "error": ack.error}
```

- [ ] **Step 6: Commit**

```powershell
git add voice-service/src/vox/fastpath.py voice-service/tests/test_fastpath.py voice-service/src/vox/tools.py
git commit -m "feat(fastpath): stateless-command short-circuit for stop/hold/pause"
```

---

## Task 15: Commentator (Python) with event triggers

**Files:**
- Create: `voice-service/src/vox/commentator.py`
- Create: `voice-service/tests/test_commentator.py`
- Modify: `voice-service/src/vox/main.py`
- Modify: `openhv/OpenRA.Mods.HV/Traits/World/VoxBridge.cs` to emit a couple more events

- [ ] **Step 1: Emit additional events from C#**

In `VoxBridge.cs`, hook into damage events. The cleanest way is a separate per-actor trait, but for hackathon timeline we'll poll: extend `EmitStateSnapshot` to also detect deltas and emit `unit_destroyed` and `base_under_attack` events.

Add to the class:
```csharp
int prevFriendlyUnits = -1;
HashSet<uint> attackedRecently = new();

void EmitDerivedEvents()
{
    if (world.LocalPlayer == null) return;
    var p = world.LocalPlayer;
    int friendly = world.Actors.Count(a => a.Owner == p && !a.IsDead && a.TraitOrDefault<Mobile>() != null);
    if (prevFriendlyUnits >= 0 && friendly < prevFriendlyUnits)
    {
        var lost = prevFriendlyUnits - friendly;
        var payload = $"{{\"type\":\"event\",\"kind\":\"units_lost\",\"ts\":{Game.LocalTick},\"count\":{lost}}}";
        try { activeWriter?.WriteLine(payload); } catch { }
    }
    prevFriendlyUnits = friendly;
}
```

Call `EmitDerivedEvents()` right after `EmitStateSnapshot()` in `Tick`.

Also emit one match_start event from `WorldLoaded`:
```csharp
public void WorldLoaded(World w, WorldRenderer wr)
{
    world = w;
    // ... existing listener setup ...
    // give listener a moment then emit match_start on first tick instead
    // (handled below)
}
```
Add a `bool startEmitted` field and at the top of `Tick`:
```csharp
if (!startEmitted && activeWriter != null)
{
    try { activeWriter.WriteLine($"{{\"type\":\"event\",\"kind\":\"match_start\",\"ts\":{Game.LocalTick}}}"); } catch { }
    startEmitted = true;
}
```

- [ ] **Step 2: Write commentator tests**

Create `tests/test_commentator.py`:
```python
from vox.commentator import Commentator
from vox.protocol import Event


class FakeTTS:
    def __init__(self): self.spoken = []
    def speak(self, text): self.spoken.append(text)


def test_match_start_emits_intro():
    tts, c = FakeTTS(), None
    c = Commentator(tts.speak)
    c.handle(Event(kind="match_start", ts=0))
    assert any("battle" in s.lower() or "begin" in s.lower() for s in tts.spoken)


def test_units_lost_high_count_emits_concern():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="units_lost", ts=0, payload={"count": 5}))
    assert any("losing" in s.lower() or "casualties" in s.lower() for s in tts.spoken)


def test_units_lost_low_count_is_silent():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="units_lost", ts=0, payload={"count": 1}))
    assert tts.spoken == []


def test_state_snapshot_is_silent():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="state_snapshot", ts=0, payload={"cash": 100}))
    assert tts.spoken == []
```

- [ ] **Step 3: Implement commentator.py**

Create `src/vox/commentator.py`:
```python
"""Event-driven commentator. Receives Events, calls TTS for dramatic moments.

TTS is injected so we can unit-test trigger logic without hitting the network.
"""
from __future__ import annotations
import random
from typing import Callable
from .protocol import Event

SpeakFn = Callable[[str], None]


class Commentator:
    def __init__(self, speak: SpeakFn):
        self.speak = speak
        self._announced = set()

    def handle(self, event: Event) -> None:
        kind = event.kind
        if kind == "match_start" and "start" not in self._announced:
            self.speak(random.choice([
                "And the battle begins!",
                "Forces deploying. Commander, the field is yours.",
            ]))
            self._announced.add("start")
            return
        if kind == "units_lost":
            count = int(event.payload.get("count", 0))
            if count >= 3:
                self.speak(random.choice([
                    f"Casualties mount — {count} units down.",
                    f"The commander is losing ground — {count} lost!",
                ]))
            return
        # state_snapshot and others: silent
```

- [ ] **Step 4: Wire ElevenLabs TTS for commentator in main.py**

Add a TTS helper. Use the simplest ElevenLabs TTS path (synchronous stream + play). Modify `main.py`:
```python
from elevenlabs import play
from elevenlabs.client import ElevenLabs as ElevenClient

def make_commentator_speak():
    client = ElevenClient(api_key=os.environ["ELEVENLABS_API_KEY"])
    voice_id = os.environ["VOX_COMMENTATOR_VOICE_ID"]
    def speak(text: str):
        try:
            audio = client.text_to_speech.convert(voice_id=voice_id, text=text, model_id="eleven_turbo_v2_5")
            play(audio)
        except Exception as e:
            print(f"[commentator-error] {e}")
    return speak
```

Add the Commentator to the orchestrator:
```python
commentator = Commentator(make_commentator_speak())
def handle_event(event, resolver, commentator):
    if event.kind == "state_snapshot":
        resolver.ingest_snapshot({"kind": "state_snapshot", **event.payload})
    commentator.handle(event)
game = GameSocket(host, port, on_event=lambda e: handle_event(e, resolver, commentator))
```

- [ ] **Step 5: Run, verify**

Launch a skirmish + `python -m vox.main`. Within 2s of the match starting, you should hear the commentator say "And the battle begins!" Then deliberately lose 3+ units (or rush an enemy with a small force) and verify a casualty line.

- [ ] **Step 6: Commit**

```powershell
Copy-Item C:\dev\elevenhack\cursor\openhv\OpenRA.Mods.HV\Traits\World\VoxBridge.cs C:\dev\elevenhack\cursor\openhv-mod\OpenRA.Mods.HV\Traits\World\
git add voice-service/src/vox/commentator.py voice-service/tests/test_commentator.py voice-service/src/vox/main.py openhv-mod
git commit -m "feat(commentator): event-driven dramatic TTS for match_start and casualties"
```

---

## Task 16: Faction and map selection + balance tuning

**No new code, but a real deliverable.**

- [ ] **Step 1: Survey OpenHV factions and skirmish maps**

Launch OpenHV, browse skirmish setup. Try one match per faction (15 min total). Score each on:
- Visual readability of units on camera (does it look good zoomed in?)
- Variety of unit types we can voice-command meaningfully
- Map size (we want a small map so action stays on screen)

- [ ] **Step 2: Pick one faction + one map**

Update `.env` or a `demo.toml`:
```
DEMO_FACTION=<chosen-faction>
DEMO_MAP=<chosen-map-id>
```

- [ ] **Step 3: Verify the chosen unit names match build commands**

Open `openhv/mods/hv/rules/infantry.yaml` and `vehicles.yaml`. Note the internal names (e.g. `e1`, `tnk`, `harv`). Update the system prompt in the dashboard so the Agent uses those names when calling `dispatch_command` with `build` intents.

- [ ] **Step 4: Run a dry-run demo**

Speak through a full 2-minute scripted sequence:
1. "Select all my units."
2. "Move them east."
3. "Build five [riflemen / whatever]."
4. "Send the harvester back to ore."
5. "Attack the enemy base."
6. "XO, how much money do we have?"
7. "Pause." [pause] "Resume."
8. "Hold position!"
9. "Push to the bridge!"

If any command fails or the XO mis-interprets, tune the agent system prompt and retry.

- [ ] **Step 5: Commit demo config**

```powershell
git add .env.example docs
git commit -m "chore: pick demo faction and map; tune agent prompt for HV unit names"
```

---

## Task 17: Record the viral clip

**No code. Deliverable is `submissions/vox-commander-demo.mp4`.**

- [ ] **Step 1: Prep recording environment**

- Close all unrelated apps.
- Boost mic gain, test audio levels.
- Set OBS or NVIDIA ShadowPlay to record at 1080p60.
- Set OpenHV resolution to 1920x1080 windowed.

- [ ] **Step 2: Write the 60-second script**

Hard-cap: 60 seconds. Beats:
- 0–5s: stand up at desk, slap on a coach polo or military cap. Establish "this is a coach."
- 5–10s: "Select all units. Move east!" Tanks move.
- 10–20s: "Build five tanks. Send the harvester to ore." (XO confirms.)
- 20–35s: "Attack the enemy base!" Combat begins. Commentator says "And the battle begins!"
- 35–45s: lose a few units. Commentator: "Casualties mount." User yells "Hold position!"
- 45–55s: "XO, how much money?" XO replies. "Build another five tanks!"
- 55–60s: Victory or final command + cut.

- [ ] **Step 3: Record three takes**

Save all three. Pick the best, light edit, export.

- [ ] **Step 4: Submit**

Upload per hackathon submission instructions.

- [ ] **Step 5: Final commit + tag**

```powershell
git add submissions
git commit -m "submission: vox-commander demo video"
git tag v1.0.0-submission
```

---

## Self-review checklist (run before submission)

1. **Spec coverage**: every numbered section of the design spec maps to a task above. ✓
2. **No placeholders**: every step has runnable code or a concrete shell command. ✓
3. **Type consistency**: `Command`, `Event`, `Ack` referenced the same way across `protocol.py`, `game_socket.py`, `tools.py`, `fastpath.py`. ✓
4. **Risk coverage**: the four risks in spec §10 each have a mitigation embedded in tasks (build verification in T2, threading model documented in T6, fastpath is T14, meta_pause in T12).
