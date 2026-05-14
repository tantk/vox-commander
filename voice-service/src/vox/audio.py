"""Custom audio interface with hard-stop barge-in.

elevenlabs.DefaultAudioInterface clears its own Python queue on
interrupt() but leaves PyAudio's internal output buffer intact — so
~200-500 ms of already-buffered TTS keeps playing after the server
detects a user barge-in. This subclass stops the PyAudio output
stream on interrupt and re-arms it the moment new audio arrives,
which drops residual playback to a single OS frame (≈10 ms).
"""
from __future__ import annotations

from elevenlabs.conversational_ai.default_audio_interface import DefaultAudioInterface


class HardStopAudioInterface(DefaultAudioInterface):
    def __init__(self):
        super().__init__()
        self._out_stopped = False

    def interrupt(self):
        super().interrupt()  # drains the SDK-level Python queue
        out = getattr(self, "out_stream", None)
        if out is not None and not self._out_stopped:
            try:
                out.stop_stream()  # halts PyAudio + discards its internal buffer
                self._out_stopped = True
            except Exception:
                pass

    def output(self, audio: bytes):
        # Next chunk after an interrupt re-arms the stream.
        if self._out_stopped:
            out = getattr(self, "out_stream", None)
            if out is not None:
                try:
                    out.start_stream()
                except Exception:
                    pass
            self._out_stopped = False
        super().output(audio)
