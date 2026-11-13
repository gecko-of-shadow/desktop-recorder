"""Live X11 checks. Skipped when this machine has no display."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "linux"))

from desktop_recorder.hotkey_x11 import X11Hotkey  # noqa: E402
from desktop_recorder.__main__ import smoke  # noqa: E402


def display_works() -> bool:
    if not os.environ.get("DISPLAY"):
        return False
    if not shutil.which("ffmpeg") or not shutil.which("ffprobe"):
        return False
    probe = subprocess.run(["xdpyinfo"], check=False, capture_output=True)
    return probe.returncode == 0


@unittest.skipUnless(display_works(), "no X11 display on this machine")
class X11SessionTests(unittest.TestCase):
    def test_hotkey_grab(self) -> None:
        if not shutil.which("xdotool"):
            self.skipTest("xdotool is not installed")
        hits = threading.Event()
        grab = X11Hotkey(os.environ.get("DISPLAY"), 2 | 4, "F12", hits.set)
        try:
            self.assertTrue(grab.start(), "X11 did not accept Ctrl+Shift+F12")
            subprocess.run(["xdotool", "key", "ctrl+shift+F12"], check=False)
            self.assertTrue(hits.wait(2), "hotkey was not delivered")
        finally:
            grab.stop()

    def test_smoke_recording(self) -> None:
        self.assertEqual(smoke(1.2), 0)


if __name__ == "__main__":
    unittest.main()
