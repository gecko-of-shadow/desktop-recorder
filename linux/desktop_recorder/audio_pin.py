"""Keep ffmpeg's system-audio stream on the output it started with.

PulseAudio treats a stream on the current default source as "follow the default", even when
ffmpeg asked for that source by name. With no microphone the default source is the monitor of
the default output, so switching the output (headphones, HDMI) moved the recording to the new
output's sound. A stream moved by a client to a source that is not the default stays there, so
this moves ffmpeg's stream back once, right after the server moves it.
"""

from __future__ import annotations

import ctypes
import os
import signal
import subprocess
import threading
from collections.abc import Callable

_PR_SET_PDEATHSIG = 1


def stream_sources(listing: str) -> dict[int, tuple[str, int]]:
    """Map source-output index to (source index, process id) from `pactl list source-outputs`."""
    found: dict[int, tuple[str, int]] = {}
    index = None
    source = ""
    for raw in listing.splitlines():
        line = raw.strip()
        if line.startswith("Source Output #"):
            index = _int(line[len("Source Output #") :])
            source = ""
        elif index is not None and line.startswith("Source:"):
            source = line[len("Source:") :].strip()
        elif index is not None and line.startswith("application.process.id = "):
            pid = _int(line.split("=", 1)[1].strip().strip('"'))
            if pid is not None:
                found[index] = (source, pid)
    return found


def source_index(name: str, short_listing: str) -> str | None:
    """Index of the named source in `pactl list short sources`."""
    for line in short_listing.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[1] == name:
            return parts[0]
    return None


class SourcePin:
    def __init__(self, source: str, process_ids: Callable[[], set[int]]) -> None:
        self.source = source
        self.process_ids = process_ids
        self.moves = 0
        self._proc: subprocess.Popen[str] | None = None
        self._lock = threading.Lock()
        self._stopped = False
        self._thread = threading.Thread(target=self._run, name="audio-pin", daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        with self._lock:
            self._stopped = True
            proc = self._proc
        if proc is not None and proc.poll() is None:
            proc.terminate()
        self._thread.join(3)

    def _run(self) -> None:
        try:
            prctl = ctypes.CDLL(None, use_errno=True).prctl
        except (OSError, AttributeError):
            prctl = None
        try:
            proc = subprocess.Popen(
                ["pactl", "subscribe"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                env=_env(),
                # Started from this thread, so pactl goes when this thread or the app does.
                preexec_fn=(lambda: prctl(_PR_SET_PDEATHSIG, int(signal.SIGKILL), 0, 0, 0)) if prctl else None,
            )
        except OSError:
            return
        with self._lock:
            self._proc = proc
            stopped = self._stopped
        if stopped:
            proc.terminate()
        try:
            self.check()
            for line in proc.stdout or ():
                if "on source-output" in line and "'remove'" not in line:
                    self.check()
        finally:
            if proc.poll() is None:
                proc.terminate()
            try:
                proc.wait(3)
            except subprocess.TimeoutExpired:
                proc.kill()
            if proc.stdout is not None:
                proc.stdout.close()

    def check(self) -> None:
        """Move ffmpeg's stream back if the server moved it to another source."""
        with self._lock:
            if self._stopped:
                return
        wanted = source_index(self.source, _pactl("list", "short", "sources"))
        if wanted is None:
            # The output is gone (unplugged); following the server is all that is left.
            return
        pids = self.process_ids()
        for index, (source, pid) in stream_sources(_pactl("list", "source-outputs")).items():
            if pid in pids and source != wanted:
                _pactl("move-source-output", str(index), self.source)
                self.moves += 1


def _pactl(*args: str) -> str:
    try:
        return subprocess.run(
            ["pactl", *args], check=False, capture_output=True, text=True, timeout=3, env=_env()
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def _env() -> dict[str, str]:
    # The pactl listing is translated; its labels are matched in English.
    return dict(os.environ, LC_ALL="C")


def _int(text: str) -> int | None:
    try:
        return int(text.strip())
    except ValueError:
        return None
