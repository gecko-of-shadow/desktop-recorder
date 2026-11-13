"""Start either the X11 ffmpeg grabber or the Wayland portal pipeline."""

from __future__ import annotations

import os
import shutil
from datetime import datetime

from .audio import find_monitor, probe_pulse
from .ffmpeg_cmd import capture_backend, x11_argv
from .recording import Recording
from .settings import Settings
from .wayland import WaylandCapture


class Capture:
    def __init__(self) -> None:
        self.x11 = Recording()
        self.wayland = WaylandCapture()
        self.mode: str | None = None
        self.warning: str | None = None
        self._portal_session: str | None = None

    @property
    def is_active(self) -> bool:
        return self.x11.is_active or self.wayland.active

    def start(self, settings: Settings, display: str | None = None, ffmpeg: str | None = None) -> str | None:
        program = ffmpeg or shutil.which("ffmpeg")
        if not program:
            return "ffmpeg was not found. Install it with: sudo apt install ffmpeg"
        if display is None:
            display = os.environ.get("DISPLAY")
        audio = find_monitor() if settings.record_audio else None
        self.warning = None
        if settings.record_audio and audio is None:
            self.warning = "System audio was not found, so this recording is silent."
        elif audio and not probe_pulse(audio):
            self.warning = "System audio did not respond, so this recording is silent."
            audio = None
        when = datetime.now()
        backend = capture_backend()
        if backend == "wayland":
            return self._start_wayland(settings, when, audio, program)
        error = self.x11.start(
            x11_argv(settings, when, display, audio, program),
            settings.folder,
            settings.fps,
        )
        if error is None:
            self.mode = "x11"
        return error

    def _start_wayland(self, settings: Settings, when: datetime, audio: str | None, ffmpeg: str) -> str | None:
        try:
            from .portal import GioTransport, PortalError, ScreenCast
        except Exception as exc:
            return "Wayland screen capture is unavailable: " + str(exc)
        transport = None
        try:
            transport = GioTransport()
            stream = ScreenCast(transport).open(settings.wayland_restore_token)
        except Exception as exc:
            return "Wayland screen capture failed: " + str(exc)
        if stream.restore_token:
            settings.wayland_restore_token = stream.restore_token
        try:
            error = self.wayland.start(stream, settings, when, audio, ffmpeg)
        except Exception as exc:
            error = "Wayland screen capture failed: " + str(exc)
        if error:
            self.wayland.stop()
            try:
                if transport is not None:
                    transport.close_session(stream.session)
            except Exception:
                pass
            return error
        self._portal_session = stream.session
        self.mode = "wayland"
        return None

    def stop(self) -> None:
        mode = self.mode
        if mode == "wayland":
            self.wayland.stop()
            session = self._portal_session
            self._portal_session = None
            if session:
                try:
                    from .portal import GioTransport

                    GioTransport().close_session(session)
                except Exception:
                    pass
        else:
            self.x11.stop()

    def session_files(self) -> list[str]:
        if self.mode == "wayland":
            return self.wayland.session_files()
        return self.x11.session_files()
