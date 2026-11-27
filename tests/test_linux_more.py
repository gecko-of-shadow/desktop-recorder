"""Linux recorder: audio probe, Wayland frame pipe and other checks added on 27 Nov 2026."""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
import zipfile
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "linux"))

from datetime import datetime  # noqa: E402

from desktop_recorder import ai_folder  # noqa: E402
from desktop_recorder.audio import probe_pulse  # noqa: E402
from desktop_recorder.settings import Settings  # noqa: E402
from desktop_recorder.wayland import WaylandCapture  # noqa: E402


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


# Stand-in for ffmpeg on the Wayland pipe: 256 KB on stderr first (more than a pipe holds), then it counts
# the bytes on stdin and writes the count to the output file (the last argument).
NOISY_FFMPEG = """#!/usr/bin/env python3
import sys
sys.stderr.write(("x" * 99 + "\\n") * 2600)
sys.stderr.flush()
n = 0
while True:
    piece = sys.stdin.buffer.read(65536)
    if not piece:
        break
    n += len(piece)
with open(sys.argv[-1].replace("%03d", "000"), "w") as f:
    f.write(str(n))
if "FAIL" in sys.argv[-1]:
    sys.stderr.write("boom: bad frame size\\n")
    sys.exit(3)
"""


class WaylandPipeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.mkdtemp(prefix="dr-wayland-")
        self.ffmpeg = os.path.join(self.folder, "noisy_ffmpeg.py")
        with open(self.ffmpeg, "w", encoding="utf-8") as f:
            f.write(NOISY_FFMPEG)
        os.chmod(self.ffmpeg, 0o755)

    def record(self, cap: WaylandCapture, frames: int, folder: str) -> float:
        """One recording without GStreamer: what start() and on_sample do around the pipeline."""
        settings = Settings(folder=folder)
        settings.record_audio = False
        cap._reset()
        cap._started = True
        frame = bytes(4 * 4 * 4)
        for _ in range(frames):
            cap._ensure_encoder(settings, datetime(2026, 11, 27, 17, 0, 0), 4, 4, None, self.ffmpeg, frame)
            time.sleep(0.01)
        start = time.monotonic()
        cap.stop()
        return time.monotonic() - start

    def written(self, folder: str) -> int:
        names = [n for n in os.listdir(folder) if n.endswith(".mp4")]
        self.assertEqual(len(names), 1, names)
        with open(os.path.join(folder, names[0]), encoding="utf-8") as f:
            return int(f.read())

    def test_noisy_stderr_does_not_block(self) -> None:
        cap = WaylandCapture()
        took = self.record(cap, 20, os.path.join(self.folder, "a"))
        self.assertLess(took, 3.0)
        self.assertEqual(self.written(os.path.join(self.folder, "a")), 20 * 64)
        self.assertIsNone(cap.error)

    def test_second_recording_gets_its_frames(self) -> None:
        cap = WaylandCapture()
        cap._started = True
        cap.stop()   # a stop with no frame (the portal gave no picture) left a None in the queue
        self.record(cap, 10, os.path.join(self.folder, "b"))
        self.assertEqual(self.written(os.path.join(self.folder, "b")), 10 * 64)

    def test_no_new_ffmpeg_after_stop(self) -> None:
        cap = WaylandCapture()
        self.record(cap, 3, os.path.join(self.folder, "c"))
        cap._ensure_encoder(Settings(folder=os.path.join(self.folder, "c")), datetime(2026, 11, 27), 4, 4,
                            None, self.ffmpeg, bytes(64))
        self.assertIsNone(cap._proc)

    def test_ffmpeg_error_is_kept(self) -> None:
        cap = WaylandCapture()
        self.record(cap, 3, os.path.join(self.folder, "FAIL"))
        self.assertEqual(cap.error, "ffmpeg stopped with an error: boom: bad frame size")


class AiFolderTests(unittest.TestCase):
    def test_work_argv(self) -> None:
        argv = ai_folder.work_argv(":5", "/tmp/rec", "ffmpeg", region=(11, 20, 641, 481))
        self.assertIn("-draw_mouse", argv)
        self.assertEqual(argv[argv.index("-draw_mouse") + 1], "0")
        self.assertEqual(argv[argv.index("-video_size") + 1], "640x480")
        self.assertEqual(argv[argv.index("-i") + 1], ":5.0+11,20")
        self.assertEqual(argv[argv.index("-qp") + 1], "0")
        self.assertEqual(argv[argv.index("-pix_fmt") + 1], "yuv444p")
        self.assertEqual(argv[-1], "/tmp/rec/work_lossless.mp4")
        whole = ai_folder.work_argv(":5", "/tmp/rec")
        self.assertNotIn("-video_size", whole)
        self.assertEqual(whole[whole.index("-i") + 1], ":5.0")

    def test_parse_pass_output(self) -> None:
        r = ai_folder.parse_pass_output("ok=True\nframes=18\nzip=/r/a.zip\nwarning=w1\nwarning=w2\nerror=\n")
        self.assertTrue(r.ok)
        self.assertEqual((r.frames, r.zip_path, r.warnings, r.error), (18, "/r/a.zip", ["w1", "w2"], ""))

    def test_missing_pass_keeps_the_work_file(self) -> None:
        with mock.patch.dict(os.environ, {"DESKTOP_RECORDER_AI_PASS": "/nonexistent/ai_pass.exe"}):
            r = ai_folder.run_pass("/tmp/rec", datetime(2026, 11, 27, 17, 0, 0))
        self.assertFalse(r.ok)
        self.assertIn("ai_pass.exe is missing", r.error)
        self.assertIn("/tmp/rec", r.error)

    @unittest.skipUnless(
        shutil.which("mono") and shutil.which("ffmpeg") and os.environ.get("DISPLAY")
        and os.path.isfile(os.environ.get("DESKTOP_RECORDER_AI_PASS", "")),
        "needs mono, ffmpeg, an X display and DESKTOP_RECORDER_AI_PASS (run-tests.sh builds it)",
    )
    def test_end_to_end_on_this_display(self) -> None:
        parent = tempfile.mkdtemp(prefix="dr-ai-")
        when = datetime(2026, 11, 27, 17, 0, 0)
        folder = ai_folder.folder_for(parent, when)
        os.makedirs(folder)
        argv = ai_folder.work_argv(os.environ["DISPLAY"], folder, "ffmpeg", region=(0, 0, 320, 240))
        proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
        time.sleep(2.5)
        _out, err = proc.communicate(b"q", timeout=30)
        self.assertEqual(proc.returncode, 0, err)
        ai_folder.write_titles(folder, [(0.0, "Test   window"), (1.0, "Other")])
        r = ai_folder.run_pass(folder, when)
        self.assertTrue(r.ok, r.error)
        self.assertGreaterEqual(r.frames, 1)
        self.assertEqual(r.zip_path, folder + ".zip")
        names = zipfile.ZipFile(r.zip_path).namelist()
        root = os.path.basename(folder)
        self.assertIn(root + "/index.md", names)
        self.assertIn(root + "/clip.mp4", names)
        self.assertIn(root + "/frames/001.png", names)
        self.assertFalse([n for n in names if "work_lossless" in n])
        self.assertFalse(os.path.exists(os.path.join(folder, ai_folder.WORK_FILE)))
        with open(os.path.join(folder, "index.md"), encoding="utf-8") as handle:
            self.assertIn("Test window", handle.read())


if __name__ == "__main__":
    unittest.main()
