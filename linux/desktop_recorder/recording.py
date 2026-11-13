"""One ffmpeg process, with the same stop / failure rules as the Windows build."""

from __future__ import annotations

import os
import subprocess
import threading
from collections.abc import Callable

from .settings import clamp_fps


class Recording:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._state = "idle"
        self._proc: subprocess.Popen[bytes] | None = None
        self._watcher: threading.Thread | None = None
        self._folder: str | None = None
        self._before: list[str] = []
        self._fps = 2.0
        self._stderr: list[str] = []
        self._idle = threading.Event()
        self._idle.set()
        self.on_failed: Callable[[str], None] | None = None

    @property
    def is_active(self) -> bool:
        with self._lock:
            return self._state != "idle"

    def start(self, argv: list[str], folder: str, fps: float) -> str | None:
        if not argv or not argv[0]:
            return "ffmpeg was not found."
        if not folder:
            return "The recordings folder is empty."
        try:
            os.makedirs(folder, exist_ok=True)
        except OSError as exc:
            return f"Cannot create the folder {folder}: {exc}"
        with self._lock:
            if self._state != "idle":
                return "Desktop Recorder is already recording."
            existing = _snapshot(folder)
            try:
                proc = subprocess.Popen(
                    argv,
                    stdin=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                )
            except OSError as exc:
                return f"Cannot start ffmpeg: {exc}"
            self._proc = proc
            self._folder = folder
            self._before = existing
            self._fps = clamp_fps(fps)
            self._stderr = []
            self._state = "recording"
            self._idle.clear()
            self._watcher = threading.Thread(target=self._watch, args=(proc,), name="ffmpeg-stderr", daemon=True)
            self._watcher.start()
        return None

    def stop(self) -> None:
        with self._lock:
            if self._state == "idle":
                return
            if self._state == "stopping":
                claimed = False
                proc = None
                watcher = None
                fps = self._fps
            else:
                claimed = True
                self._state = "stopping"
                proc = self._proc
                watcher = self._watcher
                fps = self._fps
                self._proc = None
        if not claimed:
            self._idle.wait(20)
            return
        try:
            _finish(proc, fps)
        finally:
            if watcher is not None:
                watcher.join(10)
            _close_pipes(proc)
            with self._lock:
                self._state = "idle"
                self._idle.set()

    def session_files(self) -> list[str]:
        with self._lock:
            folder = self._folder
            prior = list(self._before)
        if not folder or not os.path.isdir(folder):
            return []
        skip = {os.path.normcase(path) for path in prior}
        found = []
        try:
            for name in os.listdir(folder):
                if not (name.startswith("desktop_") and name.endswith(".mp4")):
                    continue
                path = os.path.join(folder, name)
                if os.path.normcase(path) in skip:
                    continue
                try:
                    if os.path.getsize(path) > 0:
                        found.append(path)
                except OSError:
                    continue
        except OSError:
            return []
        found.sort(key=lambda path: path.lower())
        return found

    def _watch(self, proc: subprocess.Popen[bytes]) -> None:
        try:
            if proc.stderr is not None:
                for line in proc.stderr:
                    text = line.decode("utf-8", errors="replace").rstrip("\r\n")
                    with self._lock:
                        used = sum(len(item) for item in self._stderr)
                        if used < 2000:
                            self._stderr.append(text)
        except Exception:
            pass
        try:
            proc.wait()
        except Exception:
            pass
        callback = None
        message = "ffmpeg stopped."
        with self._lock:
            unexpected = self._state == "recording" and self._proc is proc
            text = "\n".join(self._stderr).strip()
            if unexpected:
                self._proc = None
                self._state = "idle"
                self._idle.set()
                callback = self.on_failed
                message = text or "ffmpeg stopped."
        _close_pipes(proc)
        if unexpected and callback is not None:
            try:
                callback(message)
            except Exception:
                pass


def _snapshot(folder: str) -> list[str]:
    found = []
    try:
        for name in os.listdir(folder):
            if name.startswith("desktop_") and name.endswith(".mp4"):
                found.append(os.path.join(folder, name))
    except OSError:
        return []
    return found


def _finish(proc: subprocess.Popen[bytes] | None, fps: float) -> None:
    if proc is None:
        return
    try:
        if proc.stdin is not None:
            proc.stdin.write(b"q")
            proc.stdin.flush()
        wait_s = min(15.0, 4.0 + 2.0 / max(0.2, fps))
        try:
            proc.wait(timeout=wait_s)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)
    except Exception:
        try:
            if proc.poll() is None:
                proc.kill()
        except Exception:
            pass
        try:
            proc.wait(timeout=5)
        except Exception:
            pass


def _close_pipes(proc: subprocess.Popen[bytes] | None) -> None:
    if proc is None:
        return
    for stream in (proc.stdin, proc.stderr, proc.stdout):
        if stream is None:
            continue
        try:
            stream.close()
        except Exception:
            pass
