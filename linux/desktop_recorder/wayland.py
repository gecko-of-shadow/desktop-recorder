"""Portal screen cast to ffmpeg, through a GStreamer pipewiresrc."""

from __future__ import annotations

import os
import queue
import subprocess
import threading
from datetime import datetime

from .ffmpeg_cmd import wayland_ffmpeg_argv, wayland_pipeline
from .portal import PortalStream
from .settings import Settings


class WaylandCapture:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._pipeline = None
        self._proc: subprocess.Popen[bytes] | None = None
        self._pump: threading.Thread | None = None
        self._frames: queue.Queue[bytes | None] = queue.Queue(maxsize=2)
        self._started = False
        self._folder: str | None = None
        self._before: list[str] = []
        self._stream: PortalStream | None = None
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
    ) -> str | None:
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
        self._stream = stream
        self._folder = settings.folder
        self._before = _snapshot(settings.folder)

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
        pipeline.set_state(Gst.State.PLAYING)
        self._pipeline = pipeline
        self._started = True
        return None

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
            if self._proc is not None:
                self._offer(first_frame)
                return
            if width < 2 or height < 2:
                self.error = "The shared screen has no picture yet."
                return
            os.makedirs(settings.folder, exist_ok=True)
            argv = wayland_ffmpeg_argv(settings, when, width, height, audio_device, ffmpeg)
            self._proc = subprocess.Popen(
                argv,
                stdin=subprocess.PIPE,
                stderr=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
            )
            self._pump = threading.Thread(target=self._write_frames, name="wayland-frames", daemon=True)
            self._pump.start()
            self._offer(first_frame)

    def _offer(self, payload: bytes) -> None:
        try:
            self._frames.put(payload, timeout=0.05)
        except queue.Full:
            return

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
        proc = self._proc
        self._proc = None
        if proc is not None:
            try:
                proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
        stream = self._stream
        self._stream = None
        if stream is not None:
            try:
                os.close(stream.fd)
            except OSError:
                pass
        self._started = False

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


def _snapshot(folder: str) -> list[str]:
    if not os.path.isdir(folder):
        return []
    return [
        os.path.join(folder, name)
        for name in os.listdir(folder)
        if name.startswith("desktop_") and name.endswith(".mp4")
    ]
