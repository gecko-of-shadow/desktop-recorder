"""Portal screen cast to ffmpeg, through a GStreamer pipewiresrc."""

from __future__ import annotations

import collections
import os
import queue
import subprocess
import threading
import time
from datetime import datetime

from . import ai_folder
from .ffmpeg_cmd import wayland_ffmpeg_argv, wayland_pipeline
from .portal import PortalStream
from .recording import stop_wait_seconds
from .settings import Settings


class WaylandCapture:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._pipeline = None
        self._proc: subprocess.Popen[bytes] | None = None
        self._audio_proc: subprocess.Popen[bytes] | None = None   # Folder for AI: audio_system.wav
        self._pump: threading.Thread | None = None
        self._reader: threading.Thread | None = None
        self._frames: queue.Queue[bytes | None] = queue.Queue(maxsize=2)
        self._started = False
        self._folder: str | None = None
        self._work_folder: str | None = None   # Folder for AI: the lossless work file goes here
        self._before: list[str] = []
        self._stream: PortalStream | None = None
        self._fps = 2.0
        self._stderr_tail: collections.deque[str] = collections.deque(maxlen=20)
        self.dropped_frames = 0
        self.error: str | None = None

    @property
    def active(self) -> bool:
        return self._started

    def start(
        self,
        stream: PortalStream,
        settings: Settings,
        when: datetime,
        audio_device: str | None,
        ffmpeg: str = "ffmpeg",
        work_folder: str | None = None,
    ) -> str | None:
        """work_folder: write the Folder for AI work file (and audio_system.wav) there instead of the MP4."""
        description = wayland_pipeline(stream.fd, stream.node_id, stream.serial, settings.fps)
        try:
            import gi

            gi.require_version("Gst", "1.0")
            from gi.repository import Gst
        except (ImportError, ValueError) as exc:
            return (
                "Wayland capture needs GStreamer PipeWire support. "
                "Install gstreamer1.0-plugins-base and gstreamer1.0-pipewire. "
                + str(exc)
            )
        Gst.init(None)
        pipeline = Gst.parse_launch(description)
        sink = pipeline.get_by_name("sink")
        if sink is None:
            return "Could not build the PipeWire capture pipeline."
        self.error = None
        self._stream = stream
        self._folder = settings.folder
        self._before = _snapshot(settings.folder)
        self._reset()
        self._work_folder = work_folder

        def on_sample(element):
            sample = element.emit("pull-sample")
            if sample is None:
                return Gst.FlowReturn.OK
            caps = sample.get_caps()
            if caps is None or caps.get_size() < 1:
                return Gst.FlowReturn.OK
            structure = caps.get_structure(0)
            width = int(structure.get_value("width"))
            height = int(structure.get_value("height"))
            if self._proc is not None and self._frames.full():
                # ffmpeg is behind: drop this frame before the copy (a 1080p BGRA frame is 8 MB).
                self.dropped_frames += 1
                return Gst.FlowReturn.OK
            buffer = sample.get_buffer()
            ok, info = buffer.map(Gst.MapFlags.READ)
            if not ok:
                return Gst.FlowReturn.OK
            try:
                payload = bytes(info.data)
            finally:
                buffer.unmap(info)
            self._ensure_encoder(settings, when, width, height, audio_device, ffmpeg, payload)
            return Gst.FlowReturn.OK

        sink.connect("new-sample", on_sample)
        bus = pipeline.get_bus()
        bus.add_signal_watch()

        def on_message(_bus, message):
            if message.type == Gst.MessageType.ERROR:
                err, _debug = message.parse_error()
                self.error = str(err)
                self.stop()

        bus.connect("message", on_message)
        self._pipeline = pipeline
        self._started = True
        pipeline.set_state(Gst.State.PLAYING)
        return None

    def _reset(self) -> None:
        """Fresh state for a new recording. A None left by the last stop would end the new frame pump at once."""
        self._frames = queue.Queue(maxsize=2)
        self._stderr_tail.clear()
        self.dropped_frames = 0
        self.error = None

    def _ensure_encoder(
        self,
        settings: Settings,
        when: datetime,
        width: int,
        height: int,
        audio_device: str | None,
        ffmpeg: str,
        first_frame: bytes,
    ) -> None:
        with self._lock:
            if not self._started:
                return   # a late frame after stop must not start a new ffmpeg
            if self._proc is not None:
                self._offer(first_frame)
                return
            if width < 2 or height < 2:
                self.error = "The shared screen has no picture yet."
                return
            if self._work_folder:
                os.makedirs(self._work_folder, exist_ok=True)
                argv = ai_folder.pipe_work_argv(self._work_folder, width, height, ffmpeg, settings.fps)
                if audio_device:
                    # Video time 0 is this frame; the WAV starts at the same wall-clock time.
                    self._audio_proc = subprocess.Popen(
                        ai_folder.wav_argv(self._work_folder, audio_device, time.time(), ffmpeg),
                        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            else:
                os.makedirs(settings.folder, exist_ok=True)
                argv = wayland_ffmpeg_argv(settings, when, width, height, audio_device, ffmpeg)
            self._proc = subprocess.Popen(
                argv,
                stdin=subprocess.PIPE,
                stderr=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
            )
            self._fps = settings.fps
            # Read stderr all the time: an unread pipe that fills (64 KB) blocks ffmpeg, and then the frame pump.
            self._reader = threading.Thread(target=self._read_errors, args=(self._proc,), name="wayland-ffmpeg-stderr",
                                            daemon=True)
            self._reader.start()
            self._pump = threading.Thread(target=self._write_frames, name="wayland-frames", daemon=True)
            self._pump.start()
            self._offer(first_frame)

    def _offer(self, payload: bytes) -> None:
        try:
            self._frames.put(payload, timeout=0.05)
        except queue.Full:
            self.dropped_frames += 1

    def _read_errors(self, proc: subprocess.Popen[bytes]) -> None:
        if proc.stderr is None:
            return
        for raw in proc.stderr:
            line = raw.decode("utf-8", "replace").strip()
            if line:
                self._stderr_tail.append(line)
        proc.stderr.close()

    def _write_frames(self) -> None:
        proc = self._proc
        if proc is None or proc.stdin is None:
            return
        while True:
            item = self._frames.get()
            if item is None:
                break
            try:
                proc.stdin.write(item)
            except Exception:
                break
        try:
            proc.stdin.close()
        except Exception:
            pass

    def stop(self) -> None:
        with self._lock:
            self._started = False
        pipeline = self._pipeline
        self._pipeline = None
        if pipeline is not None:
            try:
                import gi

                gi.require_version("Gst", "1.0")
                from gi.repository import Gst

                pipeline.set_state(Gst.State.NULL)
            except Exception:
                pass
        while True:
            try:
                self._frames.get_nowait()
            except queue.Empty:
                break
        try:
            self._frames.put_nowait(None)
        except queue.Full:
            pass
        if self._pump is not None:
            self._pump.join(5)
        audio = self._audio_proc
        self._audio_proc = None
        if audio is not None:
            _quit(audio)
        proc = self._proc
        self._proc = None
        if proc is not None:
            try:
                proc.wait(timeout=stop_wait_seconds(self._fps))
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
            if self._reader is not None:
                self._reader.join(2)   # ffmpeg's last line (its error) can still be in the pipe after it ended
            if proc.returncode not in (0, None) and self.error is None:
                last = self._stderr_tail[-1] if self._stderr_tail else "exit code %s" % proc.returncode
                self.error = "ffmpeg stopped with an error: " + last
        stream = self._stream
        self._stream = None
        if stream is not None:
            try:
                os.close(stream.fd)
            except OSError:
                pass

    def session_files(self) -> list[str]:
        if not self._folder or not os.path.isdir(self._folder):
            return []
        skip = {os.path.normcase(path) for path in self._before}
        found = []
        for name in os.listdir(self._folder):
            if not (name.startswith("desktop_") and name.endswith(".mp4")):
                continue
            path = os.path.join(self._folder, name)
            if os.path.normcase(path) in skip:
                continue
            try:
                if os.path.getsize(path) > 0:
                    found.append(path)
            except OSError:
                continue
        found.sort(key=lambda path: path.lower())
        return found


def _quit(proc: subprocess.Popen[bytes]) -> None:
    """"q" lets ffmpeg write the WAV header; kill only when it does not end."""
    try:
        if proc.stdin is not None:
            proc.stdin.write(b"q")
            proc.stdin.close()
    except OSError:
        pass
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=5)


def _snapshot(folder: str) -> list[str]:
    if not os.path.isdir(folder):
        return []
    return [
        os.path.join(folder, name)
        for name in os.listdir(folder)
        if name.startswith("desktop_") and name.endswith(".mp4")
    ]
