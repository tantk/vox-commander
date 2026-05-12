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
        self._announced: set[str] = set()

    def handle(self, event: Event) -> None:
        kind = event.kind
        if kind == "match_start" and "start" not in self._announced:
            self.speak(
                random.choice(
                    [
                        "And the battle begins!",
                        "Forces deploying. Commander, the field is yours.",
                    ]
                )
            )
            self._announced.add("start")
            return
        if kind == "units_lost":
            count = int(event.payload.get("count", 0))
            if count >= 3:
                self.speak(
                    random.choice(
                        [
                            f"Casualties mount — {count} units down.",
                            f"The commander is losing ground — {count} lost!",
                        ]
                    )
                )
            return
        if kind == "base_under_attack":
            self.speak("Base under attack!")
            return
        # state_snapshot and unknown kinds: silent
