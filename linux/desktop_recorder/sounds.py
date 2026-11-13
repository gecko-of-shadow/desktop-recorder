"""The same two-tone chime as the Windows build."""

from __future__ import annotations

import math
import shutil
import struct
import subprocess
import tempfile
import threading
import wave


def build_chime(first_hz: float, second_hz: float) -> bytes:
    rate = 22050
    per = rate * 9 // 100
    count = per * 2
    fade = rate * 0.008
    samples = bytearray()
    for index in range(count):
        position = index % per
        hz = first_hz if index < per else second_hz
        envelope = min(1.0, min(position, per - 1 - position) / fade)
        value = int(math.sin(2 * math.pi * hz * position / rate) * envelope * 0.25 * 32767)
        samples += struct.pack("<h", value)
    header = bytearray()
    header += b"RIFF"
    header += struct.pack("<I", 36 + len(samples))
    header += b"WAVEfmt "
    header += struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16)
    header += b"data"
    header += struct.pack("<I", len(samples))
    return bytes(header) + bytes(samples)


def play(up: bool) -> None:
    wav = build_chime(660, 880) if up else build_chime(880, 660)
    player = next((name for name in ("paplay", "pw-play", "aplay") if shutil.which(name)), None)
    if player is None:
        return
    try:
        handle = tempfile.NamedTemporaryFile(prefix="desktop-recorder-", suffix=".wav", delete=False)
        handle.write(wav)
        handle.close()
    except OSError:
        return

    def run() -> None:
        try:
            subprocess.run([player, handle.name], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        finally:
            try:
                os_remove(handle.name)
            except OSError:
                pass

    threading.Thread(target=run, daemon=True).start()


def os_remove(path: str) -> None:
    import os

    os.remove(path)


def wav_duration_samples(payload: bytes) -> int:
    """Used by tests. Returns the number of 16-bit samples."""
    with wave.open(__import__("io").BytesIO(payload), "rb") as reader:
        return reader.getnframes()
