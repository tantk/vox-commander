import asyncio
import pytest
from vox.protocol import Command, Event
from vox.game_socket import GameSocket


@pytest.mark.asyncio
async def test_send_command_and_receive_ack():
    server_lines: list[str] = []

    async def fake_server(reader, writer):
        line = await reader.readline()
        server_lines.append(line.decode())
        writer.write(b'{"type":"ack","id":"x","ok":true}\n')
        await writer.drain()
        writer.close()
        try:
            await writer.wait_closed()
        except Exception:
            pass

    srv = await asyncio.start_server(fake_server, "127.0.0.1", 0)
    port = srv.sockets[0].getsockname()[1]

    async with srv:
        gs = GameSocket("127.0.0.1", port)
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
        try:
            await writer.wait_closed()
        except Exception:
            pass

    srv = await asyncio.start_server(fake_server, "127.0.0.1", 0)
    port = srv.sockets[0].getsockname()[1]

    async with srv:
        gs = GameSocket("127.0.0.1", port, on_event=lambda e: received.append(e))
        await gs.connect()
        await asyncio.sleep(0.1)
        await gs.close()

    assert len(received) == 1
    assert received[0].kind == "match_start"
