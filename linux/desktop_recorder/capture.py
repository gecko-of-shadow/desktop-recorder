"""Start either the X11 ffmpeg grabber or the Wayland portal pipeline."""

from __future__ import annotations

import os
import shutil
import zipfile
from dataclasses import replace
from datetime import datetime

from . import ai_folder, ocr_index, zip_parts
from .audio import find_monitor, probe_pulse
from .audio_pin import SourcePin
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
        self._pin: SourcePin | None = None
        # Folder for AI: the folder and start time of the recording, until finish_folder runs the pass.
        self.ai_job: tuple[str, datetime, Settings] | None = None
        self._titles: ai_folder.TitleSampler | None = None

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
        if self._pin is not None:
            self._pin.stop()
            self._pin = None
        when = datetime.now()
        backend = capture_backend()
        self.ai_job = None
        if settings.output_mode == "folder":
            if backend != "wayland":
                return self._start_folder(settings, when, display, audio, program)
            return self._start_wayland_folder(settings, when, audio, program)
        if backend == "wayland":
            return self._start_wayland(settings, when, audio, program)
        error = self.x11.start(
            x11_argv(settings, when, display, audio, program),
            settings.folder,
            settings.fps,
        )
        if error is None:
            self.mode = "x11"
            self._pin_audio(audio)
        return error

    def _pin_audio(self, audio: str | None) -> None:
        """Keep the ffmpeg audio stream on the output the recording started with (audio_pin)."""
        if audio:
            self._pin = SourcePin(audio, self.x11.process_ids)
            self._pin.start()

    def _start_folder(
        self, settings: Settings, when: datetime, display: str | None, audio: str | None, ffmpeg: str
    ) -> str | None:
        folder = ai_folder.folder_for(settings.folder, when)
        x, y, w, h = settings.region
        region = (x, y, w, h) if w > 0 and h > 0 else None
        argv = ai_folder.work_argv(display, folder, ffmpeg, region, ai_folder.AI_FPS, audio, when)
        error = self.x11.start(argv, folder, ai_folder.AI_FPS)
        if error is not None:
            return error
        self.mode = "x11"
        self._pin_audio(audio)
        self.ai_job = (folder, when, settings)
        self._titles = ai_folder.TitleSampler(display)
        self._titles.start()
        return None

    def _start_wayland_folder(self, settings: Settings, when: datetime, audio: str | None, ffmpeg: str) -> str | None:
        """The portal stream at 10 fps into the work file. No sound, region or window titles on Wayland yet."""
        folder = ai_folder.folder_for(settings.folder, when)
        work = replace(settings, fps=ai_folder.AI_FPS)
        error = self._start_wayland(work, when, None, ffmpeg, folder)
        settings.wayland_restore_token = work.wayland_restore_token
        if error is not None:
            return error
        left_out = [name for name, on in (("sound", audio is not None), ("region", settings.region[2] > 0))
                    if on]
        if left_out:
            self.warning = "Folder for AI on Wayland has no " + " and no ".join(left_out) + " yet."
        self.ai_job = (folder, when, settings)
        return None

    def finish_folder(self, ffmpeg: str | None = None) -> ai_folder.PassResult | None:
        """After stop: the pass that writes the PNG frames, clip.mp4, index.md and the ZIP. Slow: not on the UI thread."""
        job = self.ai_job
        self.ai_job = None
        if job is None:
            return None
        folder, when, settings = job
        if not os.path.isfile(os.path.join(folder, ai_folder.WORK_FILE)):
            return ai_folder.PassResult(error=self.error or "The recording wrote no video.")
        ffmpeg = ffmpeg or shutil.which("ffmpeg") or "ffmpeg"
        result = ai_folder.run_pass(folder, when, ffmpeg,
                                    ai_folder.AI_FPS, settings.scroll_percent, settings.settle_ms,
                                    whisper_path=settings.whisper_path, whisper_model=settings.whisper_model)
        if result.ok and (settings.ocr_text or settings.redact_secrets):
            result.ocr = ocr_index.add_ocr(folder, result.zip_path, redact=settings.redact_secrets, ffmpeg=ffmpeg,
                                           text_section=settings.ocr_text)
        if result.ok and result.zip_path and settings.frames_per_zip > 0:
            try:
                result.zip_parts = zip_parts.make_parts(result.zip_path, settings.frames_per_zip)
            except (OSError, zipfile.BadZipFile) as exc:
                result.warnings.append("The ZIP parts were not made: " + str(exc))
        if self.error:
            result.warnings.insert(0, self.error)
        return result

    def _start_wayland(
        self, settings: Settings, when: datetime, audio: str | None, ffmpeg: str, work_folder: str | None = None
    ) -> str | None:
        try:
            from .portal import GioTransport, ScreenCast
        except Exception as exc:
            return "Wayland screen capture is unavailable: " + str(exc)
        transport = None
        try:
            transport = GioTransport()
            stream = ScreenCast(transport).open(settings.wayland_restore_token, settings.show_pointer)
        except Exception as exc:
            return "Wayland screen capture failed: " + str(exc)
        if stream.restore_token:
            settings.wayland_restore_token = stream.restore_token
        try:
            error = self.wayland.start(stream, settings, when, audio, ffmpeg, work_folder)
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
            if self._pin is not None:
                self._pin.stop()
                self._pin = None
        titles = self._titles
        self._titles = None
        if titles is not None and self.ai_job is not None:
            samples = titles.stop()
            if samples:
                try:
                    ai_folder.write_titles(self.ai_job[0], samples)
                except OSError:
                    pass

    @property
    def error(self) -> str | None:
        """Why the last recording's files cannot be trusted, or None."""
        if self.mode == "wayland":
            return self.wayland.error
        return self.x11.error

    def session_files(self) -> list[str]:
        if self.mode == "wayland":
            return self.wayland.session_files()
        return self.x11.session_files()
