"""One ffmpeg process, with the same stop / failure rules as the Windows build."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import threading
from collections.abc import Callable

from .settings import clamp_fps

# Below this much free space a recording does not start; below the second one it stops cleanly.
MIN_FREE_START = 200 * 1024 * 1024
MIN_FREE_RECORDING = 64 * 1024 * 1024
DISK_CHECK_SECONDS = 2.0


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
        self._low_disk: str | None = None
        self._bad: set[str] = set()
        self.on_failed: Callable[[str], None] | None = None
        # Set after stop when the files may be broken or short. None means the recording is good.
        self.error: str | None = None
        self.min_free_start = MIN_FREE_START
        self.min_free_recording = MIN_FREE_RECORDING
        self.check_file: Callable[[str], str | None] = check_video

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
        free = free_bytes(folder)
        if free is not None and free < self.min_free_start:
            return (
                f"Not enough free disk space in {folder}: {free // (1024 * 1024)} MB free, "
                f"at least {self.min_free_start // (1024 * 1024)} MB is needed."
            )
        with self._lock:
            if self._state != "idle":
                return "Desktop Recorder is already recording."
            existing = _snapshot(folder)
            try:
                proc = subprocess.Popen(
                    guarded(argv),
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
            self._low_disk = None
            self._bad = set()
            self.error = None
            self._state = "recording"
            self._idle.clear()
            self._watcher = threading.Thread(target=self._watch, args=(proc,), name="ffmpeg-stderr", daemon=True)
            try:
                self._watcher.start()
                threading.Thread(target=self._watch_disk, args=(proc, folder), name="disk-space", daemon=True).start()
            except RuntimeError as exc:
                self._proc = None
                self._watcher = None
                self._state = "idle"
                self._idle.set()
                orphan = proc
                failure = f"Cannot start the recording: {exc}"
            else:
                return None
        # No thread means nobody would stop or read ffmpeg, so do not leave it running.
        _finish(orphan, clamp_fps(fps))
        _close_pipes(orphan)
        return failure

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
            self._idle.wait(30)
            return
        killed = False
        try:
            killed = _finish(proc, fps)
        finally:
            if watcher is not None:
                watcher.join(10)
            _close_pipes(proc)
            error = self._judge(proc, killed)
            with self._lock:
                self.error = error
                self._state = "idle"
                self._idle.set()

    def _judge(self, proc: subprocess.Popen[bytes] | None, killed: bool = False) -> str | None:
        """Why the files of this recording cannot be trusted, or None."""
        with self._lock:
            text = "\n".join(self._stderr).strip()
            low_disk = self._low_disk
        problems = []
        if low_disk:
            problems.append(low_disk)
        code = None if proc is None else proc.returncode
        if killed:
            problems.append("ffmpeg did not finish the file in time, so the end of the recording can be missing.")
        elif code not in (0, None) and not low_disk:
            problems.append(f"ffmpeg ended with error code {code}." + (" " + text[-300:] if text else ""))
        bad = set()
        for path in self._new_files():
            reason = self.check_file(path)
            if reason:
                bad.add(os.path.normcase(path))
                problems.append(f"{os.path.basename(path)} is damaged: {reason}")
        with self._lock:
            self._bad = bad
        return "\n".join(problems) if problems else None

    def session_files(self) -> list[str]:
        """Good video files of the last recording. Damaged files stay on the disk but are left out."""
        with self._lock:
            bad = set(self._bad)
        return [path for path in self._new_files() if os.path.normcase(path) not in bad]

    def _new_files(self) -> list[str]:
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
                        # ffmpeg keeps grabbing after a write fails, so say it at once.
                        if "No space left on device" in text and self._low_disk is None and self._proc is proc:
                            self._low_disk = "The disk is full, so the recording stopped early."
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
        if not unexpected:
            return
        error = self._judge(proc) or message
        with self._lock:
            self.error = error
        if callback is not None:
            try:
                callback(error)
            except Exception:
                pass


    def _watch_disk(self, proc: subprocess.Popen[bytes], folder: str) -> None:
        """Stop cleanly before the disk is full, so the file ends properly instead of being cut."""
        while True:
            try:
                proc.wait(timeout=DISK_CHECK_SECONDS)
                return
            except subprocess.TimeoutExpired:
                pass
            with self._lock:
                if self._proc is not proc or self._state != "recording":
                    return
                message = self._low_disk
            free = free_bytes(folder)
            if message is None and free is not None and free < self.min_free_recording:
                message = f"Less than {self.min_free_recording // (1024 * 1024)} MB is free on the disk, so the recording stopped early."
            if message is None:
                continue
            with self._lock:
                if self._proc is not proc or self._state != "recording":
                    return
                self._low_disk = message
                callback = self.on_failed
            self.stop()
            if callback is not None:
                try:
                    callback(self.error or message)
                except Exception:
                    pass
            return


def free_bytes(folder: str) -> int | None:
    try:
        return shutil.disk_usage(folder).free
    except OSError:
        return None


def check_video(path: str) -> str | None:
    """Return why ffprobe cannot read the whole file, or None when it is good (or ffprobe is missing)."""
    ffprobe = shutil.which("ffprobe")
    if ffprobe is None:
        return None
    try:
        probe = subprocess.run(
            # -count_packets reads every packet, so a cut file reports "partial file".
            [ffprobe, "-v", "error", "-count_packets", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path],
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        return f"ffprobe failed: {exc}"
    errors = probe.stderr.strip()
    if probe.returncode != 0 or errors:
        return (errors.splitlines() or [f"ffprobe exit code {probe.returncode}"])[0][:200]
    try:
        duration = float(probe.stdout.strip())
    except ValueError:
        return "it has no duration."
    if duration <= 0:
        return "it is empty."
    return None


def _snapshot(folder: str) -> list[str]:
    found = []
    try:
        for name in os.listdir(folder):
            if name.startswith("desktop_") and name.endswith(".mp4"):
                found.append(os.path.join(folder, name))
    except OSError:
        return []
    return found


def stop_wait_seconds(fps: float) -> float:
    """How long ffmpeg gets to finish the file after "q". Same as RecordingSession.StopWaitMs on Windows."""
    return min(25.0, 15.0 + 2.0 / max(0.2, fps))


def _finish(proc: subprocess.Popen[bytes] | None, fps: float) -> bool:
    """Ask ffmpeg to finish the file. True when it had to be killed, so the end can be missing."""
    if proc is None:
        return False
    try:
        if proc.stdin is not None:
            proc.stdin.write(b"q")
            proc.stdin.flush()
        try:
            proc.wait(timeout=stop_wait_seconds(fps))
            return False
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)
            return True
    except Exception:
        killed = False
        try:
            if proc.poll() is None:
                proc.kill()
                killed = True
        except Exception:
            pass
        try:
            proc.wait(timeout=5)
        except Exception:
            pass
        return killed


GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "guard.py")


def guarded(argv: list[str]) -> list[str]:
    """Run ffmpeg under guard.py, which stops it once and cleanly if this app dies.

    stdin ("q") and stderr go straight to ffmpeg; the exit status is ffmpeg's.
    """
    program = shutil.which(argv[0])
    if program is None or not sys.executable or not os.path.isfile(GUARD):
        return argv
    return [sys.executable, GUARD, str(os.getpid()), program, *argv[1:]]


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
