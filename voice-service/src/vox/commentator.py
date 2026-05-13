"""Event-driven commentary.

Two channels:
  - Commentator: dramatic narrator for player-side events (match_start,
    casualties, base_under_attack, victory).
  - Intel:       gravelly recon officer for enemy-side events (enemy
    started producing X, enemy army surge, enemy approaching).

Each channel has its own speak() function bound to a distinct ElevenLabs
voice. TTS callables are injected so unit tests can run without network.
"""
from __future__ import annotations
import random
from typing import Callable

from .protocol import Event

SpeakFn = Callable[[str], None]


# Pretty names for HV unit/structure internal names so the Intel voice
# announces "tanks" instead of "mbt".
_PRETTY = {
    "mbt": "tanks",
    "aatank": "anti-air tanks",
    "apc": "APCs",
    "artillery": "artillery",
    "missiletank": "missile tanks",
    "stealthtank": "stealth tanks",
    "miner": "miners",
    "miner2": "mining towers",
    "builder": "builders",
    "rifleman": "riflemen",
    "rocketeer": "rocketeers",
    "sniper": "snipers",
    "flamer": "flame troopers",
    "jetpacker": "jetpackers",
    "technician": "technicians",
    "generator": "a power plant",
    "storage": "a refinery",
    "module": "a barracks",
    "factory": "a vehicle factory",
    "radar": "a radar dome",
    "techcenter": "a tech center",
    "bunker": "a bunker",
    "turret": "a turret",
    "aaturret": "an anti-air turret",
}


def _pretty(name: str) -> str:
    return _PRETTY.get(name.lower(), name)


class Commentator:
    """Dramatic narrator for our side. Calls `speak` for big moments."""

    def __init__(self, speak: SpeakFn):
        self.speak = speak
        self._announced: set[str] = set()

    def handle(self, event: Event) -> None:
        kind = event.kind
        if kind == "match_start" and "start" not in self._announced:
            self.speak(
                random.choice([
                    "And the battle begins!",
                    "Forces deploying. Commander, the field is yours.",
                ])
            )
            self._announced.add("start")
            return
        if kind == "units_lost":
            count = int(event.payload.get("count", 0))
            if count >= 3:
                self.speak(
                    random.choice([
                        f"Casualties mount — {count} units down.",
                        f"We are losing ground — {count} lost!",
                    ])
                )
            return
        if kind == "base_under_attack":
            self.speak("Base under attack!")
            return
        if kind == "victory":
            self.speak("Enemy base destroyed. Victory, commander.")
            return
        if kind == "defeat":
            self.speak("Our base has fallen. Defeat.")
            return
        # state_snapshot and unknown kinds: silent.


class Intel:
    """Enemy-side narrator. Distinct voice from the main commentator."""

    def __init__(self, speak: SpeakFn):
        self.speak = speak

    def handle(self, event: Event) -> None:
        kind = event.kind
        if kind == "enemy_producing":
            target = _pretty(event.payload.get("kind", "something"))
            self.speak(
                random.choice([
                    f"Intel: enemy is producing {target}.",
                    f"Enemy started building {target}.",
                    f"Recon reports {target} in enemy production.",
                ])
            )
            return
        if kind == "enemy_army_surge":
            count = event.payload.get("count", "?")
            self.speak(
                random.choice([
                    f"Intel: enemy army at {count} combat units. They're massing.",
                    f"Enemy strength rising — {count} hostile combatants.",
                ])
            )
            return
        if kind == "enemy_approaching":
            dist = event.payload.get("distance", "?")
            self.speak(
                random.choice([
                    f"Intel: enemy {dist} cells from base. Inbound.",
                    f"Hostiles approaching — {dist} cells out.",
                    "Contact. Enemy on our doorstep.",
                ])
            )
            return
        # other kinds: silent.
