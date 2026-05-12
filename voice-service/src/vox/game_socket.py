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
