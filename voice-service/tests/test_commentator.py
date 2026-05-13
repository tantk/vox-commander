from vox.commentator import Commentator, Intel
from vox.protocol import Event


class FakeTTS:
    def __init__(self):
        self.spoken: list[str] = []

    def speak(self, text: str) -> None:
        self.spoken.append(text)


def test_match_start_emits_intro():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="match_start", ts=0))
    assert any("battle" in s.lower() or "deploying" in s.lower() for s in tts.spoken)


def test_match_start_only_announced_once():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="match_start", ts=0))
    c.handle(Event(kind="match_start", ts=1))
    assert len(tts.spoken) == 1


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


def test_base_under_attack_emits_warning():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="base_under_attack", ts=0))
    assert tts.spoken and "attack" in tts.spoken[0].lower()


def test_state_snapshot_is_silent():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="state_snapshot", ts=0, payload={"cash": 100}))
    assert tts.spoken == []


def test_intel_enemy_producing_translates_kind():
    tts = FakeTTS()
    i = Intel(tts.speak)
    i.handle(Event(kind="enemy_producing", ts=0, payload={"kind": "mbt", "queue": "Tank"}))
    assert tts.spoken
    assert any("tank" in s.lower() for s in tts.spoken)


def test_intel_army_surge_announces():
    tts = FakeTTS()
    i = Intel(tts.speak)
    i.handle(Event(kind="enemy_army_surge", ts=0, payload={"count": "8", "delta": "4"}))
    assert tts.spoken
    assert "8" in tts.spoken[0]


def test_intel_approaching_announces():
    tts = FakeTTS()
    i = Intel(tts.speak)
    i.handle(Event(kind="enemy_approaching", ts=0, payload={"distance": "12"}))
    assert tts.spoken


def test_intel_unknown_event_silent():
    tts = FakeTTS()
    i = Intel(tts.speak)
    i.handle(Event(kind="state_snapshot", ts=0, payload={}))
    assert tts.spoken == []


def test_commentator_victory_announces():
    tts = FakeTTS()
    c = Commentator(tts.speak)
    c.handle(Event(kind="victory", ts=0))
    assert tts.spoken and "destroyed" in tts.spoken[0].lower()
