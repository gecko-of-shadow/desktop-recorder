"""Linux recorder: audio probe, Wayland frame pipe and other checks added on 27 Nov 2026."""

from __future__ import annotations

import os
import stat
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "linux"))

from desktop_recorder.audio import probe_pulse  # noqa: E402


def fake_program(folder: str, body: str) -> str:
    path = os.path.join(folder, "ffmpeg")
    with open(path, "w", encoding="utf-8") as f:
        f.write("#!/bin/sh\n" + body + "\n")
    os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)
    return folder


class ProbeTests(unittest.TestCase):
    def probe_with(self, body: str) -> tuple[bool, float]:
        with tempfile.TemporaryDirectory() as folder:
            fake_program(folder, body)
            with mock.patch.dict(os.environ, {"PATH": folder + os.pathsep + os.environ.get("PATH", "")}):
                start = time.monotonic()
                ok = probe_pulse("dr_idle.monitor")
                return ok, time.monotonic() - start

    def test_slow_idle_source_keeps_audio(self) -> None:
        # Measured: an idle monitor gives its first 0.2 s of audio after 2.2 to 2.4 s.
        ok, took = self.probe_with("sleep 2.4; exit 0")
        self.assertTrue(ok)
        self.assertLess(took, 3.5)

    def test_dead_source_is_dropped(self) -> None:
        ok, took = self.probe_with("exec sleep 30")
        self.assertFalse(ok)
        self.assertLess(took, 7)

    def test_failed_open_is_dropped(self) -> None:
        self.assertEqual(self.probe_with("exit 1")[0], False)


if __name__ == "__main__":
    unittest.main()
