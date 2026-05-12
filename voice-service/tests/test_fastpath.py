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
