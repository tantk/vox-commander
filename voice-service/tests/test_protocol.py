import json
import pytest
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
    with pytest.raises(ValueError):
        decode_message('{"type":"bogus"}')
