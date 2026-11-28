"""Folder for AI on Linux: a lossless work file during the recording, then the Windows pass after it.

During the recording, ffmpeg writes <folder>/work_lossless.mp4 (x11grab, libx264 -qp 0, 4:4:4, no pointer),
as the Windows app does in folder mode. After Stop, ai_pass.exe (linux/ai_pass.cs, built from the same
AfterRecording.cs and SettledFrames.cs as the Windows app) writes frames/NNN.png, clip.mp4 and index.md,
and the ZIP next to the folder. It runs with mono. Without mono or ai_pass.exe, the work file stays and
the reason is reported.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime

from .ffmpeg_cmd import format_fps, stamp_of, x11_input

WORK_FILE = "work_lossless.mp4"
AUDIO_FILE = "audio_system.wav"   # the Windows name; the pass puts it into clip.mp4 and leaves it out of the ZIP
# Starts with "work_lossless", so the ZIP leaves it out (AfterRecording.WriteZip).
TITLES_FILE = "work_lossless.titles.tsv"
# The Windows default for folder mode. A settled frame needs a few captures in SettleSeconds (0.4 s).
EVEN_CROP = "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0"
AI_FPS = 10.0
PASS_EXE = "ai_pass.exe"


def folder_for(parent: str, when: datetime) -> str:
    return os.path.join(parent, "desktop_" + stamp_of(when))


def work_argv(
    display: str | None,
    folder: str,
    ffmpeg: str = "ffmpeg",
    region: tuple[int, int, int, int] | None = None,
    fps: float = AI_FPS,
    audio_device: str | None = None,
    when: datetime | None = None,
) -> list[str]:
    """ffmpeg for the lossless work file: what the pass reads. No pointer: it would hide text and count as a change.

    With audio_device, the same ffmpeg also writes audio_system.wav. Both inputs keep their wall-clock times
    (-copyts) shifted by the same start instant, and both outputs start at that instant (the fps filter repeats
    the first frame, aresample pads silence), so WAV time 0 is video time 0 (as x11_argv does for the MP4).
    """
    shift = ["-itsoffset", f"-{(when or datetime.now()).timestamp():.6f}"] if audio_device else []
    args = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostats", "-y"]
    if audio_device:
        args += ["-copyts", "-thread_queue_size", "1024", *shift, "-f", "pulse", "-fragment_size", "9600",
                 "-i", audio_device]
    args += ["-thread_queue_size", "1024", *shift, "-f", "x11grab", "-framerate", format_fps(fps), "-draw_mouse", "0"]
    filters = [f"fps={format_fps(fps)}:start_time=0"] if audio_device else []
    args += x11_input(display, region or (0, 0, 0, 0))
    if region is None:
        filters.append(EVEN_CROP)
    if audio_device:
        args += ["-map", "1:v"]
    if filters:
        args += ["-vf", ",".join(filters)]
    args += _work_output(folder, fps)
    if audio_device:
        args += ["-map", "0:a", "-af", "aresample=async=1:first_pts=0", "-c:a", "pcm_s16le",
                 os.path.join(folder, AUDIO_FILE)]
    return args


def pipe_work_argv(folder: str, width: int, height: int, ffmpeg: str = "ffmpeg", fps: float = AI_FPS) -> list[str]:
    """Wayland: the BGRA frames of the portal stream (GStreamer videorate keeps fps) on stdin, same work file."""
    return [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", f"{width}x{height}",
            "-framerate", format_fps(fps), "-i", "pipe:0", "-vf", EVEN_CROP, *_work_output(folder, fps)]


def wav_argv(folder: str, audio_device: str, start: float, ffmpeg: str = "ffmpeg") -> list[str]:
    """Wayland: the system audio by itself, from `start` (wall clock, s): the time of the first video frame.

    The pulse demuxer stamps packets with the wall clock; -copyts and the shift keep them, aresample pads silence
    from `start` to the first packet. So WAV time 0 is the time of the first frame, as in work_argv.
    """
    return [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-copyts", "-thread_queue_size", "1024", "-itsoffset", f"-{start:.6f}",
            "-f", "pulse", "-fragment_size", "9600", "-i", audio_device,
            "-af", "aresample=async=1:first_pts=0", "-c:a", "pcm_s16le", os.path.join(folder, AUDIO_FILE)]


def _work_output(folder: str, fps: float) -> list[str]:
    gop = max(2, int(round(fps * 30)))
    return [
        "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-threads", "4", "-g", str(gop),
        "-pix_fmt", "yuv444p",
        # A killed ffmpeg loses at most 0.5 s (as on Windows).
        "-movflags", "+frag_keyframe+empty_moov", "-frag_duration", "500000", "-flush_packets", "1",
        os.path.join(folder, WORK_FILE),
    ]


def find_pass() -> tuple[list[str] | None, str]:
    """The command that runs ai_pass.exe, or None and the reason."""
    exe = os.environ.get("DESKTOP_RECORDER_AI_PASS") or os.path.join(os.path.dirname(__file__), PASS_EXE)
    if not os.path.isfile(exe):
        return None, PASS_EXE + " is missing. Run build-linux.sh with mcs (sudo apt install mono-mcs)."
    mono = shutil.which("mono")
    if not mono:
        return None, "mono was not found. Install it with: sudo apt install mono-runtime libmono-system-drawing4.0-cil"
    return [mono, exe], ""


def delete_folder(folder: str, timeout: float = 60) -> str | None:
    """After the ZIP, as on Windows: the files that the app made, then the folder when it is empty. None or the error."""
    command, reason = find_pass()
    if command is None:
        return reason
    try:
        done = subprocess.run(command + ["--delete-folder", folder], capture_output=True, text=True, timeout=timeout,
                              check=False)
    except (OSError, subprocess.SubprocessError) as exc:
        return str(exc)
    error = parse_pass_output(done.stdout).error
    return error or (None if done.returncode == 0 else f"exit code {done.returncode}")


def write_titles(folder: str, samples: list[tuple[float, str]]) -> str:
    path = os.path.join(folder, TITLES_FILE)
    with open(path, "w", encoding="utf-8") as handle:
        for clock, title in samples:
            handle.write(f"{clock:.3f}\t{' '.join(title.split())}\n")
    return path


@dataclass
class PassResult:
    ok: bool = False
    frames: int = 0
    zip_path: str = ""
    warnings: list[str] = field(default_factory=list)
    error: str = ""
    transcript: str = ""   # "<n> segments", or why there is none; empty without audio
    ocr: str = ""          # "OCR: <n> lines", or why there is none (ocr_index.add_ocr)
    sound_only_in_wav: bool = False   # the WAV is the only copy of the sound: the folder must stay
    zip_parts: list[str] = field(default_factory=list)   # zip_parts.make_parts, when frames_per_zip is set


def parse_pass_output(text: str) -> PassResult:
    result = PassResult()
    for line in text.splitlines():
        key, sep, value = line.partition("=")
        if not sep:
            continue
        if key == "ok":
            result.ok = value == "True"
        elif key == "frames" and value.isdigit():
            result.frames = int(value)
        elif key == "zip":
            result.zip_path = value
        elif key == "warning":
            result.warnings.append(value)
        elif key == "error":
            result.error = value
        elif key == "transcript":
            result.transcript = value
        elif key == "sound_only_in_wav":
            result.sound_only_in_wav = value == "True"
    return result


def run_pass(
    folder: str,
    start: datetime,
    ffmpeg: str = "ffmpeg",
    fps: float = AI_FPS,
    scroll_percent: int = 60,
    settle_ms: int = 400,
    timeout: float = 3600,
    whisper_path: str = "",
    whisper_model: str = "",
) -> PassResult:
    command, reason = find_pass()
    if command is None:
        return PassResult(error=reason + " The work file stays in " + folder + ".")
    titles = os.path.join(folder, TITLES_FILE)
    audio = os.path.join(folder, AUDIO_FILE)
    argv = command + [ffmpeg, folder, format_fps(fps), start.strftime("%Y-%m-%dT%H:%M:%S"),
                      f"scroll_percent={scroll_percent}", f"settle_ms={settle_ms}"]
    if os.path.isfile(titles):
        argv.append("titles=" + titles)
    if os.path.isfile(audio):
        argv += ["audio=" + audio, "whisper_path=" + whisper_path, "whisper_model=" + whisper_model]
    try:
        done = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return PassResult(error="The pass for the AI folder failed: " + str(exc))
    result = parse_pass_output(done.stdout)
    if done.returncode != 0 and not result.error:
        result.error = (done.stderr.strip().splitlines() or ["exit code %d" % done.returncode])[-1]
    if result.ok and os.path.isfile(titles) and os.environ.get("DESKTOP_RECORDER_KEEP_WORK") != "1":
        os.remove(titles)
    return result


def _window_ids(text: str) -> list[str]:
    ids: list[str] = []
    for part in text.replace(",", " ").split():
        if not part.startswith("0x"):
            continue
        try:
            if int(part, 16) != 0:
                ids.append(part)
        except ValueError:
            continue
    return ids


def _window_name(props: str) -> str | None:
    for line in props.splitlines():
        found = re.search(r'= "(.*)"$', line)
        if found:
            return found.group(1).replace('\\"', '"').replace("\\\\", "\\")
    return None


def _xprop(display: str | None, args: list[str]) -> str | None:
    command = ["xprop"] + (["-display", display] if display else []) + args
    try:
        return subprocess.run(command, capture_output=True, text=True, timeout=2).stdout
    except (OSError, subprocess.TimeoutExpired):
        return None


def active_title(display: str | None) -> str | None:
    """Title of the EWMH active window, or None when xprop or the window manager does not tell."""
    root = _xprop(display, ["-root", "_NET_ACTIVE_WINDOW"])
    if not root:
        return None
    ids = _window_ids(root)
    if not ids:
        return None
    props = _xprop(display, ["-id", ids[0], "_NET_WM_NAME", "WM_NAME"])
    return _window_name(props or "")


def stacked_title(display: str | None) -> str | None:
    """Title of the top window in the EWMH stacking list (what is on top of the screen)."""
    root = _xprop(display, ["-root", "_NET_CLIENT_LIST_STACKING"])
    if not root:
        return None
    ids = _window_ids(root)
    if not ids:
        return None
    props = _xprop(display, ["-id", ids[-1], "_NET_WM_NAME", "WM_NAME"])
    return _window_name(props or "")


def visible_title(display: str | None) -> str | None:
    """The active window, or the top stacked window when the active one has no title."""
    return active_title(display) or stacked_title(display)


class TitleSampler:
    """The active window title every 250 ms, on the clock of the recording (0 = start)."""

    def __init__(self, display: str | None, every: float = 0.25, read=None) -> None:
        self.samples: list[tuple[float, str]] = []
        self._display = display
        self._every = every
        self._read = read or visible_title
        self._stop = threading.Event()
        self._start = time.monotonic()
        self._thread = threading.Thread(target=self._run, name="window-titles", daemon=True)

    def start(self) -> None:
        self._start = time.monotonic()
        self._thread.start()

    def _run(self) -> None:
        while not self._stop.is_set():
            clock = time.monotonic() - self._start   # before xprop, so a slow read does not shift the time
            title = self._read(self._display)
            if title is not None and (not self.samples or self.samples[-1][1] != title):
                if len(self.samples) >= 4096:
                    del self.samples[1::2]   # bounded, as TitleSamples on Windows
                self.samples.append((clock, title))
            # After a closed window the next one can appear in a few hundred ms: do not wait the full interval.
            self._stop.wait(0.1 if title is None else self._every)

    def stop(self) -> list[tuple[float, str]]:
        self._stop.set()
        if self._thread.is_alive():
            self._thread.join(5)
        return list(self.samples)
