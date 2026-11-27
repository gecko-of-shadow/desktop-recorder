"""Folder for AI on Linux: a lossless work file during the recording, then the Windows pass after it.

During the recording, ffmpeg writes <folder>/work_lossless.mp4 (x11grab, libx264 -qp 0, 4:4:4, no pointer),
as the Windows app does in folder mode. After Stop, ai_pass.exe (linux/ai_pass.cs, built from the same
AfterRecording.cs and SettledFrames.cs as the Windows app) writes frames/NNN.png, clip.mp4 and index.md,
and the ZIP next to the folder. It runs with mono. Without mono or ai_pass.exe, the work file stays and
the reason is reported.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass, field
from datetime import datetime

from .ffmpeg_cmd import format_fps, stamp_of, x11_target

WORK_FILE = "work_lossless.mp4"
# Starts with "work_lossless", so the ZIP leaves it out (AfterRecording.WriteZip).
TITLES_FILE = "work_lossless.titles.tsv"
# The Windows default for folder mode. A settled frame needs a few captures in SettleSeconds (0.4 s).
AI_FPS = 10.0
PASS_EXE = "ai_pass.exe"


def folder_for(parent: str, when: datetime) -> str:
    return os.path.join(parent, "desktop_" + stamp_of(when))


def even_region(region: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    """x, y, width, height with an even width and height (libx264 4:4:4 needs no more, 4:2:0 clips need it)."""
    x, y, w, h = (int(v) for v in region)
    return max(0, x), max(0, y), max(2, w - w % 2), max(2, h - h % 2)


def work_argv(
    display: str | None,
    folder: str,
    ffmpeg: str = "ffmpeg",
    region: tuple[int, int, int, int] | None = None,
    fps: float = AI_FPS,
) -> list[str]:
    """ffmpeg for the lossless work file: what the pass reads. No pointer: it would hide text and count as a change."""
    gop = max(2, int(round(fps * 30)))
    target = x11_target(display)
    args = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-thread_queue_size", "1024", "-f", "x11grab", "-framerate", format_fps(fps), "-draw_mouse", "0"]
    if region is not None:
        x, y, w, h = even_region(region)
        args += ["-video_size", f"{w}x{h}", "-i", f"{target}+{x},{y}"]
    else:
        args += ["-i", target, "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0"]
    args += [
        "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-threads", "4", "-g", str(gop),
        "-pix_fmt", "yuv444p",
        # A killed ffmpeg loses at most 0.5 s (as on Windows).
        "-movflags", "+frag_keyframe+empty_moov", "-frag_duration", "500000", "-flush_packets", "1",
        os.path.join(folder, WORK_FILE),
    ]
    return args


def find_pass() -> tuple[list[str] | None, str]:
    """The command that runs ai_pass.exe, or None and the reason."""
    exe = os.environ.get("DESKTOP_RECORDER_AI_PASS") or os.path.join(os.path.dirname(__file__), PASS_EXE)
    if not os.path.isfile(exe):
        return None, PASS_EXE + " is missing. Run build-linux.sh with mcs (sudo apt install mono-mcs)."
    mono = shutil.which("mono")
    if not mono:
        return None, "mono was not found. Install it with: sudo apt install mono-runtime libmono-system-drawing4.0-cil"
    return [mono, exe], ""


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
    return result


def run_pass(
    folder: str,
    start: datetime,
    ffmpeg: str = "ffmpeg",
    fps: float = AI_FPS,
    scroll_percent: int = 60,
    settle_ms: int = 400,
    timeout: float = 3600,
) -> PassResult:
    command, reason = find_pass()
    if command is None:
        return PassResult(error=reason + " The work file stays in " + folder + ".")
    titles = os.path.join(folder, TITLES_FILE)
    argv = command + [ffmpeg, folder, format_fps(fps), start.strftime("%Y-%m-%dT%H:%M:%S"),
                      str(scroll_percent), str(settle_ms)]
    if os.path.isfile(titles):
        argv.append(titles)
    try:
        done = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return PassResult(error="The pass for the AI folder failed: " + str(exc))
    result = parse_pass_output(done.stdout)
    if done.returncode != 0 and not result.error:
        result.error = (done.stderr.strip().splitlines() or ["exit code %d" % done.returncode])[-1]
    if result.ok and os.path.isfile(titles):
        os.remove(titles)
    return result
