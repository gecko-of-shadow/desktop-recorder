"""Tray app, plus --check, --smoke and --toggle."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import time

from .audio import find_monitor
from .capture import Capture
from .ffmpeg_cmd import capture_backend
from .settings import Settings, config_dir
from .single_instance import send_command

HELP = """\
Desktop Recorder records the desktop to MP4.

  desktop-recorder              tray icon
  desktop-recorder --toggle     start or stop the running tray
  desktop-recorder --check      print the capture backend
  desktop-recorder --smoke SEC  record SEC seconds with X11 and exit
"""


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    if not args:
        from .ui import run

        return run()
    if args[0] in ("-h", "--help"):
        print(HELP)
        return 0
    if args[0] == "--check":
        return check()
    if args[0] == "--toggle":
        return toggle()
    if args[0] == "--smoke":
        seconds = float(args[1]) if len(args) > 1 else 1.5
        return smoke(seconds)
    print(HELP, file=sys.stderr)
    return 2


def check() -> int:
    ffmpeg = shutil.which("ffmpeg")
    print("ffmpeg:", ffmpeg or "missing")
    print("backend:", capture_backend())
    print("display:", os.environ.get("DISPLAY", ""))
    print("wayland:", os.environ.get("WAYLAND_DISPLAY", ""))
    print("audio:", find_monitor() or "none")
    return 0 if ffmpeg else 1


def toggle() -> int:
    reply = send_command(os.path.join(config_dir(), "control.sock"), "TOGGLE")
    if reply is None:
        print("Desktop Recorder is not running.", file=sys.stderr)
        return 1
    print(reply)
    return 0


def smoke(seconds: float) -> int:
    os.environ["DESKTOP_RECORDER_CAPTURE"] = "x11"
    folder = tempfile.mkdtemp(prefix="desktop-recorder-smoke-")
    settings = Settings(
        folder=folder,
        fps=2,
        split_minutes=0,
        record_audio=False,
        record_on_launch=False,
        play_sounds=False,
        copy_to_clipboard=False,
    )
    capture = Capture()
    error = capture.start(settings, os.environ.get("DISPLAY"))
    if error:
        print(error, file=sys.stderr)
        return 1
    time.sleep(max(0.5, seconds))
    capture.stop()
    files = capture.session_files()
    if not files:
        print("Recording produced no file.", file=sys.stderr)
        return 1
    probe = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=nw=1",
            files[0],
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    print(probe.stdout.strip() or probe.stderr.strip())
    print(files[0])
    if probe.returncode != 0:
        return 1
    try:
        duration = float(probe.stdout.strip().split("=", 1)[-1])
    except ValueError:
        return 1
    return 0 if duration >= 0.2 else 1


if __name__ == "__main__":
    raise SystemExit(main())
