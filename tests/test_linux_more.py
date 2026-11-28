"""Linux recorder: audio probe, Wayland frame pipe and other checks added on 27 Nov 2026."""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
import zipfile
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "linux"))

from datetime import datetime  # noqa: E402

from desktop_recorder import ai_folder  # noqa: E402
from desktop_recorder.audio import probe_pulse  # noqa: E402
from desktop_recorder.ffmpeg_cmd import x11_argv  # noqa: E402
from desktop_recorder import ocr_index, zip_parts  # noqa: E402
from desktop_recorder.region_select import WHOLE_SCREEN, drag_region  # noqa: E402
from desktop_recorder.settings import Settings, parse_settings, serialize_settings  # noqa: E402
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


class EncoderAndDefaultsTests(unittest.TestCase):
    def argv(self, **changes) -> list[str]:
        s = Settings(folder="/tmp/rec")
        for k, v in changes.items():
            setattr(s, k, v)
        return x11_argv(s, datetime(2026, 11, 27, 17, 0, 0), ":0", None, "ffmpeg")

    def value(self, argv: list[str], flag: str) -> str:
        return argv[argv.index(flag) + 1]

    def test_pointer_keyframes_threads_chroma(self) -> None:
        argv = self.argv()
        self.assertEqual(self.value(argv, "-draw_mouse"), "0")
        self.assertEqual(self.value(argv, "-g"), "20")          # 10 s at 2 fps
        self.assertEqual(self.value(argv, "-threads"), "2")
        self.assertEqual(self.value(argv, "-pix_fmt"), "yuv420p")
        self.assertEqual(self.value(argv, "-preset"), "medium")
        self.assertEqual(self.value(self.argv(show_pointer=True), "-draw_mouse"), "1")
        fast = self.argv(fps=30.0)
        self.assertEqual(self.value(fast, "-threads"), "4")
        self.assertEqual(self.value(fast, "-g"), "300")
        self.assertEqual(self.value(self.argv(chroma="yuv444"), "-pix_fmt"), "yuv444p")

    def test_new_defaults_and_keys(self) -> None:
        d = Settings(folder="/tmp/rec")
        self.assertFalse(d.record_on_launch)
        self.assertFalse(d.show_pointer)
        self.assertEqual((d.chroma, d.output_mode, d.region, d.settle_ms, d.scroll_percent),
                         ("yuv420", "mp4", (0, 0, 0, 0), 400, 60))
        text = ("show_pointer=1\nchroma=yuv444\noutput_mode=folder\nregion=10,20,640,480\n"
                "settle_ms=300\nscroll_percent=99\nwhisper_path=/w/whisper-cli\nwhisper_model=/w/m.bin\n")
        p = parse_settings(text, Settings(folder="/tmp/rec"))
        self.assertEqual((p.show_pointer, p.chroma, p.output_mode, p.region, p.settle_ms, p.scroll_percent),
                         (True, "yuv444", "folder", (10, 20, 640, 480), 300, 95))
        self.assertEqual(parse_settings(serialize_settings(p), Settings(folder="/x")).region, (10, 20, 640, 480))
        bad = parse_settings("chroma=rgb\noutput_mode=zip\nregion=1,2,3\nsettle_ms=x\n", Settings(folder="/tmp/rec"))
        self.assertEqual((bad.chroma, bad.output_mode, bad.region, bad.settle_ms), ("yuv420", "mp4", (0, 0, 0, 0), 400))
        self.assertEqual((p.whisper_path, p.whisper_model), ("/w/whisper-cli", "/w/m.bin"))
        self.assertEqual(parse_settings(serialize_settings(p), Settings(folder="/x")).whisper_model, "/w/m.bin")
        self.assertEqual(p.extra_lines, [])


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

    def test_work_argv_with_audio_shares_one_clock(self) -> None:
        when = datetime(2026, 11, 27, 17, 0, 0)
        argv = ai_folder.work_argv(":5", "/tmp/rec", "ffmpeg", (0, 0, 640, 480), 10, "mon.monitor", when)
        shift = f"-{when.timestamp():.6f}"
        self.assertIn("-copyts", argv)
        self.assertEqual([argv[i + 1] for i, a in enumerate(argv) if a == "-itsoffset"], [shift, shift])
        self.assertEqual(argv[argv.index("-f") + 1], "pulse")
        self.assertEqual(argv[argv.index("-vf") + 1], "fps=10:start_time=0")
        self.assertEqual([argv[i + 1] for i, a in enumerate(argv) if a == "-map"], ["1:v", "0:a"])
        self.assertEqual(argv[argv.index("-af") + 1], "aresample=async=1:first_pts=0")
        self.assertEqual(argv[-1], "/tmp/rec/audio_system.wav")
        self.assertLess(argv.index("/tmp/rec/work_lossless.mp4"), argv.index("-map", argv.index("-map") + 1))
        silent = ai_folder.work_argv(":5", "/tmp/rec", "ffmpeg", None, 10, None, when)
        self.assertNotIn("-itsoffset", silent)
        self.assertNotIn("-map", silent)
        self.assertEqual(silent[silent.index("-vf") + 1], "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0")

    def test_run_pass_passes_named_options(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-args-")
        echo = os.path.join(folder, "echo.py")
        with open(echo, "w", encoding="utf-8") as handle:
            handle.write("import sys\nprint('ok=True')\nprint('frames=2')\nprint('transcript=3 segments')\n"
                         "for a in sys.argv[1:]:\n    print('warning=' + a)\n")
        ai_folder.write_titles(folder, [(0.0, "A")])
        open(os.path.join(folder, ai_folder.AUDIO_FILE), "wb").close()
        with mock.patch("desktop_recorder.ai_folder.find_pass", lambda: ([sys.executable, echo], "")):
            r = ai_folder.run_pass(folder, datetime(2026, 11, 27, 17, 0, 0), "ff", 10, 70, 300,
                                   whisper_path="/w/whisper-cli", whisper_model="/w/m.bin")
        self.assertTrue(r.ok)
        self.assertEqual(r.transcript, "3 segments")
        self.assertEqual(r.warnings, ["ff", folder, "10", "2026-11-27T17:00:00", "scroll_percent=70", "settle_ms=300",
                                      "titles=" + os.path.join(folder, ai_folder.TITLES_FILE),
                                      "audio=" + os.path.join(folder, ai_folder.AUDIO_FILE),
                                      "whisper_path=/w/whisper-cli", "whisper_model=/w/m.bin"])

    def test_parse_pass_output(self) -> None:
        r = ai_folder.parse_pass_output("ok=True\nframes=18\nzip=/r/a.zip\nwarning=w1\nwarning=w2\nerror=\nsound_only_in_wav=True\n")
        self.assertTrue(r.ok)
        self.assertTrue(r.sound_only_in_wav)
        self.assertEqual((r.frames, r.zip_path, r.warnings, r.error), (18, "/r/a.zip", ["w1", "w2"], ""))

    def test_missing_pass_keeps_the_work_file(self) -> None:
        with mock.patch.dict(os.environ, {"DESKTOP_RECORDER_AI_PASS": "/nonexistent/ai_pass.exe"}):
            r = ai_folder.run_pass("/tmp/rec", datetime(2026, 11, 27, 17, 0, 0))
        self.assertFalse(r.ok)
        self.assertIn("ai_pass.exe is missing", r.error)
        self.assertIn("/tmp/rec", r.error)

    def test_title_sampler(self) -> None:
        titles = iter(["A", "A", None, "B", "B", "C"] + ["C"] * 50)
        sampler = ai_folder.TitleSampler(":9", every=0.01, read=lambda _d: next(titles))
        sampler.start()
        time.sleep(0.3)
        samples = sampler.stop()
        self.assertEqual([t for _c, t in samples], ["A", "B", "C"])
        self.assertTrue(all(b[0] >= a[0] for a, b in zip(samples, samples[1:])))

    def test_folder_mode_capture_without_pass(self) -> None:
        # Capture in folder mode with a fake ffmpeg: the work file goes into its own folder, the MP4 list is
        # empty, and finish_folder reports the missing pass and keeps the work file.
        from desktop_recorder.capture import Capture

        parent = tempfile.mkdtemp(prefix="dr-folder-")
        settings = Settings(folder=parent)
        settings.output_mode = "folder"
        settings.record_audio = False
        settings.region = (8, 16, 101, 51)
        cap = Capture()
        cap.x11.check_file = lambda _p: None
        with mock.patch.dict(os.environ, {"DESKTOP_RECORDER_CAPTURE": "x11",
                                          "DESKTOP_RECORDER_AI_PASS": "/nonexistent/ai_pass.exe"}), \
                mock.patch("desktop_recorder.ai_folder.active_title", lambda _d: "Editor"):
            self.assertIsNone(cap.start(settings, ":0", str(ROOT / "tests" / "fake_ffmpeg.py")))
            self.assertIsNotNone(cap.ai_job)
            folder = cap.ai_job[0]
            time.sleep(0.3)
            cap.stop()
            self.assertEqual(cap.session_files(), [])
            result = cap.finish_folder()
        self.assertIsNotNone(result)
        self.assertFalse(result.ok)
        self.assertIn("ai_pass.exe is missing", result.error)
        self.assertTrue(os.path.isfile(os.path.join(folder, ai_folder.WORK_FILE)))
        with open(os.path.join(folder, ai_folder.TITLES_FILE), encoding="utf-8") as handle:
            self.assertIn("\tEditor", handle.read())
        self.assertIsNone(cap.ai_job)

    def test_finish_folder_deletes_the_folder_after_the_zip(self) -> None:
        # As on Windows (one ZIP is the output): the folder goes after a good pass and the Linux steps, and stays
        # when the WAV is the only copy of the sound, when the pass failed, or with DESKTOP_RECORDER_KEEP_WORK=1.
        from desktop_recorder.capture import Capture

        def finish(result: ai_folder.PassResult, env: dict) -> list:
            folder = tempfile.mkdtemp(prefix="dr-del-")
            open(os.path.join(folder, ai_folder.WORK_FILE), "wb").close()
            result.zip_path = folder + ".zip"
            open(result.zip_path, "wb").close()
            settings = Settings(folder=folder)
            settings.ocr_text = settings.redact_secrets = False
            cap = Capture()
            cap.ai_job = (folder, datetime(2026, 11, 27, 17, 0, 0), settings)
            deleted: list = []
            with mock.patch.dict(os.environ, env), \
                    mock.patch("desktop_recorder.ai_folder.run_pass", lambda *_a, **_k: result), \
                    mock.patch("desktop_recorder.ai_folder.delete_folder", lambda f: deleted.append(f) or "busy"):
                cap.finish_folder("ffmpeg")
            self.assertEqual(deleted, [folder] if deleted else [])
            return deleted

        self.assertTrue(finish(ai_folder.PassResult(ok=True), {}))
        good = ai_folder.PassResult(ok=True)
        finish(good, {})
        self.assertEqual(good.warnings, ["The ZIP is ready, but the folder was not deleted: busy"])
        self.assertFalse(finish(ai_folder.PassResult(ok=True, sound_only_in_wav=True), {}))
        self.assertFalse(finish(ai_folder.PassResult(ok=False), {}))
        self.assertFalse(finish(ai_folder.PassResult(ok=True), {"DESKTOP_RECORDER_KEEP_WORK": "1"}))

    def test_folder_mode_pins_its_audio(self) -> None:
        from desktop_recorder import capture as capture_mod

        pins: list = []

        class FakePin:
            def __init__(self, source, process_ids):
                pins.append((source, sorted(process_ids())))

            def start(self):
                pins.append("start")

            def stop(self):
                pins.append("stop")

        settings = Settings(folder=tempfile.mkdtemp(prefix="dr-pin-"))
        settings.output_mode = "folder"
        cap = capture_mod.Capture()
        cap.x11.check_file = lambda _p: None
        with mock.patch.dict(os.environ, {"DESKTOP_RECORDER_CAPTURE": "x11",
                                          "DESKTOP_RECORDER_AI_PASS": "/nonexistent/ai_pass.exe"}), \
                mock.patch.object(capture_mod, "find_monitor", lambda: "speakers.monitor"), \
                mock.patch.object(capture_mod, "probe_pulse", lambda _d: True), \
                mock.patch.object(capture_mod, "SourcePin", FakePin), \
                mock.patch("desktop_recorder.ai_folder.active_title", lambda _d: None):
            self.assertIsNone(cap.start(settings, ":0", str(ROOT / "tests" / "fake_ffmpeg.py")))
            time.sleep(0.2)
            cap.stop()
        self.assertEqual(pins[0][0], "speakers.monitor")
        self.assertTrue(pins[0][1])   # the ffmpeg process ids
        self.assertEqual(pins[1:], ["start", "stop"])

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
        self.assertFalse(r.sound_only_in_wav)
        open(os.path.join(folder, "mine.txt"), "w").close()     # a file that the app did not make stays
        self.assertIsNone(ai_folder.delete_folder(folder))
        self.assertEqual(os.listdir(folder), ["mine.txt"])
        os.remove(os.path.join(folder, "mine.txt"))
        self.assertIsNone(ai_folder.delete_folder(folder))
        self.assertFalse(os.path.exists(folder))
        self.assertTrue(os.path.isfile(r.zip_path))



class WaylandFolderTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "needs ffmpeg")
    def test_portal_frames_go_to_the_work_file(self) -> None:
        parent = tempfile.mkdtemp(prefix="dr-wl-folder-")
        folder = os.path.join(parent, "desktop_x")
        cap = WaylandCapture()
        cap._reset()
        cap._started = True
        cap._work_folder = folder
        settings = Settings(folder=parent)
        settings.fps = ai_folder.AI_FPS
        for i in range(30):   # 66 x 48 BGRA, a gray that changes each frame
            cap._ensure_encoder(settings, datetime(2026, 11, 27, 17, 0, 0), 66, 48, None, "ffmpeg",
                                bytes([i * 8, i * 8, i * 8, 255]) * (66 * 48))
            time.sleep(0.02)
        cap.stop()
        self.assertIsNone(cap.error)
        self.assertEqual(cap.session_files(), [])
        probe = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-select_streams", "v",
                                "-show_entries", "stream=nb_read_frames,width,height,pix_fmt,r_frame_rate",
                                "-of", "csv=p=0", os.path.join(folder, ai_folder.WORK_FILE)],
                               capture_output=True, text=True)
        width, height, pix_fmt, rate, frames = probe.stdout.strip().split(",")
        self.assertEqual((width, height, pix_fmt, rate), ("66", "48", "yuv444p", "10/1"))
        self.assertEqual(int(frames) + cap.dropped_frames, 30)
        self.assertGreaterEqual(int(frames), 25)

    def test_audio_wav_starts_with_the_first_frame(self) -> None:
        argv = ai_folder.wav_argv("/tmp/rec", "mon.monitor", 1764284400.25, "ffmpeg")
        self.assertEqual(argv[argv.index("-itsoffset") + 1], "-1764284400.250000")
        self.assertIn("-copyts", argv)
        self.assertEqual(argv[argv.index("-af") + 1], "aresample=async=1:first_pts=0")
        self.assertEqual(argv[-1], "/tmp/rec/audio_system.wav")
        # The WAV ffmpeg starts with the first frame and gets "q" at stop.
        parent = tempfile.mkdtemp(prefix="dr-wl-audio-")
        folder = os.path.join(parent, "desktop_x")
        fake = os.path.join(parent, "fake_wav.py")
        with open(fake, "w", encoding="utf-8") as handle:
            handle.write("import sys\nopen(sys.argv[1] + '.started', 'w').close()\n"
                         "data = sys.stdin.read(1)\nopen(sys.argv[1], 'w').write('stopped by ' + data)\n")
        starts: list = []

        def fake_argv(work, device, start, _ffmpeg):
            starts.append((device, start))
            return [sys.executable, fake, os.path.join(work, ai_folder.AUDIO_FILE)]

        cap = WaylandCapture()
        cap._reset()
        cap._started = True
        cap._work_folder = folder
        settings = Settings(folder=parent)
        settings.fps = ai_folder.AI_FPS
        before = time.time()
        with mock.patch.object(ai_folder, "wav_argv", fake_argv):
            for _ in range(3):
                cap._ensure_encoder(settings, datetime(2026, 11, 27), 4, 4, "mon.monitor", self.ffmpeg_or_skip(),
                                    bytes(64))
            cap.stop()
        self.assertEqual(len(starts), 1)
        self.assertEqual(starts[0][0], "mon.monitor")
        self.assertGreaterEqual(starts[0][1], before)
        with open(os.path.join(folder, ai_folder.AUDIO_FILE), encoding="utf-8") as handle:
            self.assertEqual(handle.read(), "stopped by q")

    def ffmpeg_or_skip(self) -> str:
        if not shutil.which("ffmpeg"):
            self.skipTest("needs ffmpeg")
        return "ffmpeg"

    def test_capture_uses_the_portal_in_folder_mode(self) -> None:
        from desktop_recorder.capture import Capture

        calls: list = []

        def fake_wayland(_self, settings, _when, audio, _ffmpeg, work_folder=None):
            calls.append((settings.fps, audio, work_folder))
            settings.wayland_restore_token = "token-2"
            return None

        settings = Settings(folder="/tmp/dr-wl")
        settings.output_mode = "folder"
        settings.record_audio = False
        settings.region = (0, 0, 640, 480)
        cap = Capture()
        with mock.patch.dict(os.environ, {"DESKTOP_RECORDER_CAPTURE": "wayland"}), \
                mock.patch.object(Capture, "_start_wayland", fake_wayland):
            self.assertIsNone(cap.start(settings, None, "ffmpeg"))
        self.assertEqual(len(calls), 1)
        fps, audio, work_folder = calls[0]
        self.assertEqual((fps, audio), (ai_folder.AI_FPS, None))
        self.assertTrue(os.path.basename(work_folder).startswith("desktop_"))
        self.assertEqual(cap.ai_job[0], work_folder)
        self.assertEqual(settings.wayland_restore_token, "token-2")
        self.assertEqual(settings.fps, Settings(folder="/x").fps)
        self.assertEqual(cap.warning, "Folder for AI on Wayland has no region yet.")

FAKE_TESSERACT = """#!/usr/bin/env python3
import os, sys
pages = {"001.png": ["Title bar", "line one", "line two"], "002.png": ["Title  bar", "line two", "line three", "x"],
         "003.png": ["LINE ONE", "password: hunter22 ok", "key AKIAABCDEFGHIJKLMNOP"]}
print("level\\tpage_num\\tblock_num\\tpar_num\\tline_num\\tword_num\\tleft\\ttop\\twidth\\theight\\tconf\\ttext")
for n, line in enumerate(pages[os.path.basename(sys.argv[1])]):
    print(f"4\\t1\\t1\\t1\\t{n}\\t0\\t0\\t{n * 20}\\t300\\t16\\t-1\\t")
    for k, word in enumerate(line.split()):
        print(f"5\\t1\\t1\\t1\\t{n}\\t{k}\\t{k * 60}\\t{n * 20}\\t50\\t16\\t95\\t{word}")
"""


class OcrIndexTests(unittest.TestCase):
    def make_folder(self) -> tuple[str, str]:
        parent = tempfile.mkdtemp(prefix="dr-ocr-")
        folder = os.path.join(parent, "desktop_x")
        os.makedirs(os.path.join(folder, "frames"))
        first = os.path.join(folder, "frames", "001.png")
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=white:s=400x80",
                        "-frames:v", "1", first], check=True)
        for name in ("002.png", "003.png"):
            shutil.copy(first, os.path.join(folder, "frames", name))
        with open(os.path.join(folder, "index.md"), "w", encoding="utf-8") as handle:
            handle.write("# Recording\n| Frame |\n")
        zip_path = folder + ".zip"
        with zipfile.ZipFile(zip_path, "w") as z:
            z.writestr("desktop_x/index.md", "# Recording\n| Frame |\n")
            z.writestr("desktop_x/frames/001.png", b"png1")
            z.writestr("desktop_x/frames/003.png", b"png3")
            z.writestr("desktop_x/clip.mp4", b"clip")
        tesseract = os.path.join(parent, "tesseract")
        with open(tesseract, "w", encoding="utf-8") as handle:
            handle.write(FAKE_TESSERACT)
        os.chmod(tesseract, 0o755)
        return folder, tesseract

    def test_only_new_lines_per_frame(self) -> None:
        fresh = ocr_index.new_lines([["Title bar", "line one", "line two"], ["Title  bar", "line two", "line three", "x"],
                                     ["LINE ONE"]])
        self.assertEqual(fresh, [["Title bar", "line one", "line two"], ["line three"], []])

    def test_secret_words(self) -> None:
        texts = [["bravo", "token", "Q0037", "end"], ["password=hunter22"], ["Password:", "hunter22", "x"],
                 ["-----BEGIN", "RSA", "PRIVATE", "KEY-----"], ["commit", "6d7a22a0c1b2c3d4e5f60718293a4b5c6d7e8f90"],
                 ["ghp_" + "a1B2" * 9], ["export", "TOKEN=", "abc"], ["sk-" + "Zx9" * 8],
                 ["key", "AKIA12345678", "9QeWJKY40uvSwMFLZDe1", "the", "end"], ["getUserNameFromTheAccount", "x"],
                 ["Q7Zk2mP9xL4vB8nR3tW6yH1c"]]
        words = [ocr_index.Word((n, 0, 0), k, n, 1, 1, t) for n, line in enumerate(texts) for k, t in enumerate(line)]
        hits = ocr_index.secret_words(words)
        self.assertEqual(sorted(words[i].text for i in hits),
                         sorted(["password=hunter22", "hunter22", "PRIVATE", "ghp_" + "a1B2" * 9, "abc", "sk-" + "Zx9" * 8,
                                 "AKIA12345678", "9QeWJKY40uvSwMFLZDe1", "Q7Zk2mP9xL4vB8nR3tW6yH1c"]))
        self.assertEqual(ocr_index.lines_of(words[:4], {2: "x"}), ["bravo token [redacted] end"])

    def test_index_and_zip_get_the_section(self) -> None:
        folder, tesseract = self.make_folder()
        with open(os.path.join(folder, "frames", "003.png"), "rb") as handle:
            before = handle.read()
        self.assertEqual(ocr_index.add_ocr(folder, folder + ".zip", tesseract),
                         "OCR: 6 lines; redacted 2 words in 1 frame, no clip.mp4 in the ZIP")
        with open(os.path.join(folder, "index.md"), encoding="utf-8") as handle:
            text = handle.read()
        self.assertIn(ocr_index.SECTION, text)
        self.assertIn("### 002.png\n    line three\n", text)
        self.assertIn("### 003.png\n    password: [redacted] ok\n    key [redacted]\n", text)
        self.assertNotIn("hunter22", text)
        self.assertNotIn("AKIA", text)
        self.assertIn("Redacted (black boxes): 2 words in 003.png (AWS key, password or token)", text)
        with open(os.path.join(folder, "frames", "003.png"), "rb") as handle:
            after = handle.read()
        self.assertNotEqual(before, after)
        with zipfile.ZipFile(folder + ".zip") as z:
            self.assertEqual(z.read("desktop_x/index.md").decode("utf-8"), text)
            self.assertEqual(z.read("desktop_x/frames/003.png"), after)
            self.assertEqual(z.read("desktop_x/frames/001.png"), b"png1")
            self.assertEqual(sorted(z.namelist()), ["desktop_x/frames/001.png", "desktop_x/frames/003.png",
                                                    "desktop_x/index.md"])
        self.assertEqual(ocr_index.add_ocr(folder, folder + ".zip", tesseract), "OCR was already in index.md")

    def test_no_redaction_keeps_the_clip(self) -> None:
        folder, tesseract = self.make_folder()
        self.assertEqual(ocr_index.add_ocr(folder, folder + ".zip", tesseract, redact=False), "OCR: 6 lines")
        with zipfile.ZipFile(folder + ".zip") as z:
            self.assertEqual(z.read("desktop_x/clip.mp4"), b"clip")
            self.assertIn("hunter22", z.read("desktop_x/index.md").decode("utf-8"))

    def test_redaction_without_the_text(self) -> None:
        folder, tesseract = self.make_folder()
        self.assertEqual(ocr_index.add_ocr(folder, folder + ".zip", tesseract, text_section=False),
                         "no OCR text; redacted 2 words in 1 frame, no clip.mp4 in the ZIP")
        with open(os.path.join(folder, "index.md"), encoding="utf-8") as handle:
            text = handle.read()
        self.assertIn("## Redaction\n\nRedacted (black boxes): 2 words in 003.png", text)
        self.assertNotIn("line one", text)
        clean, _t = self.make_folder()
        with open(os.path.join(clean, "index.md"), encoding="utf-8") as handle:
            before = handle.read()
        self.assertEqual(ocr_index.add_ocr(clean, None, tesseract, redact=False, text_section=False), "no secrets found")
        with open(os.path.join(clean, "index.md"), encoding="utf-8") as handle:
            self.assertEqual(handle.read(), before)

    def test_without_tesseract(self) -> None:
        folder, _t = self.make_folder()
        with mock.patch("shutil.which", lambda _n: None):
            self.assertEqual(ocr_index.add_ocr(folder, None), "no OCR (tesseract is not installed)")

    def test_setting_round_trip(self) -> None:
        self.assertTrue(Settings(folder="/x").ocr_text)
        self.assertTrue(Settings(folder="/x").redact_secrets)
        off = parse_settings("ocr_text=0\nredact_secrets=0\n", Settings(folder="/x"))
        self.assertEqual((off.ocr_text, off.redact_secrets), (False, False))
        again = parse_settings(serialize_settings(off), Settings(folder="/x"))
        self.assertEqual((again.ocr_text, again.redact_secrets), (False, False))

    @unittest.skipUnless(shutil.which("tesseract") and shutil.which("ffmpeg"), "needs tesseract and ffmpeg")
    def test_real_tesseract(self) -> None:
        folder, _t = self.make_folder()
        png = os.path.join(folder, "frames", "001.png")
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=white:s=640x120",
                        "-vf", "drawtext=text='Build failed at step seven':fontsize=36:x=20:y=40", "-frames:v", "1",
                        png], check=True)
        for name in ("002.png", "003.png"):
            shutil.copy(png, os.path.join(folder, "frames", name))
        result = ocr_index.add_ocr(folder, None)
        with open(os.path.join(folder, "index.md"), encoding="utf-8") as handle:
            text = handle.read()
        self.assertIn("Build failed at step seven", text, result)
        self.assertEqual(text.count("Build failed"), 1)

class ZipPartsTests(unittest.TestCase):
    def make_zip(self, frames: int) -> str:
        path = os.path.join(tempfile.mkdtemp(prefix="dr-parts-"), "desktop_x.zip")
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("desktop_x/index.md", "# Recording 1\nFor the AI.\n| 001 |\n")
            z.writestr("desktop_x/clip.mp4", b"clip")
            z.writestr("desktop_x/transcript.srt", "1\n")
            for i in range(1, frames + 1):
                z.writestr(f"desktop_x/frames/{i:03d}.png", f"png{i}")
        return path

    def test_parts(self) -> None:
        path = self.make_zip(45)
        parts = zip_parts.make_parts(path, 19)
        self.assertEqual([os.path.basename(p) for p in parts],
                         ["desktop_x_part1of3.zip", "desktop_x_part2of3.zip", "desktop_x_part3of3.zip"])
        names = [sorted(zipfile.ZipFile(p).namelist()) for p in parts]
        self.assertEqual([len([n for n in ns if "/frames/" in n]) for ns in names], [19, 19, 7])
        self.assertIn("desktop_x_part1of3/clip.mp4", names[0])
        self.assertIn("desktop_x_part1of3/transcript.srt", names[0])
        self.assertNotIn("desktop_x_part2of3/clip.mp4", names[1])
        self.assertIn("desktop_x_part3of3/frames/045.png", names[2])
        self.assertEqual(zipfile.ZipFile(parts[2]).read("desktop_x_part3of3/frames/045.png"), b"png45")
        index = zipfile.ZipFile(parts[1]).read("desktop_x_part2of3/index.md").decode("utf-8")
        self.assertEqual(index.splitlines()[0], "# Recording 1")
        self.assertIn("Part 2 of 3: this ZIP has the images of frames 020 to 038. The table", index)
        self.assertIn("desktop_x_part1of3.zip, desktop_x_part3of3.zip", index)
        self.assertTrue(os.path.isfile(path))

    def test_no_parts_when_small_or_off(self) -> None:
        self.assertEqual(zip_parts.make_parts(self.make_zip(19), 19), [])
        self.assertEqual(zip_parts.make_parts(self.make_zip(30), 0), [])

    def test_setting(self) -> None:
        self.assertEqual(Settings(folder="/x").frames_per_zip, 0)
        self.assertEqual(parse_settings("frames_per_zip=19\n", Settings(folder="/x")).frames_per_zip, 19)
        self.assertEqual(parse_settings("frames_per_zip=500\n", Settings(folder="/x")).frames_per_zip, 100)
        self.assertEqual(parse_settings(serialize_settings(Settings(folder="/x", frames_per_zip=7)),
                                        Settings(folder="/x")).frames_per_zip, 7)

class SettingsDialogTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("DISPLAY"), "needs an X display")
    def test_mp4_only_fields_follow_the_output(self) -> None:
        try:
            from desktop_recorder import ui
        except (ImportError, ValueError):
            self.skipTest("GTK 3 for Python is not installed")
        current = Settings(folder="/tmp/rec")
        current.output_mode = "folder"
        dialog = ui.SettingsDialog(current)
        try:
            mp4_only = (dialog.fps, dialog.quality, dialog.split, dialog.pointer, dialog.chroma)
            self.assertFalse(any(w.get_sensitive() for w in mp4_only))
            self.assertIn("ZIP", dialog.copy.get_label())
            dialog.output.set_active(0)
            self.assertTrue(all(w.get_sensitive() for w in mp4_only))
            self.assertEqual(dialog.result().output_mode, "mp4")
        finally:
            dialog.destroy()

class RegionSelectorTests(unittest.TestCase):
    def test_drag_region(self) -> None:
        self.assertEqual(drag_region(421, 301, 100, 80, 1280, 800), (100, 80, 320, 220))
        self.assertEqual(drag_region(-50, -9, 2000, 900, 1280, 800), (0, 0, 1280, 800))
        self.assertIsNone(drag_region(10, 10, 20, 200, 1280, 800))
        self.assertIsNone(drag_region(5, 5, 5, 5, 1280, 800))

    def test_region_goes_to_ffmpeg(self) -> None:
        s = Settings(folder="/tmp/rec")
        s.region = (101, 50, 641, 361)
        argv = x11_argv(s, datetime(2026, 11, 27, 17, 0, 0), ":3", None)
        self.assertEqual(argv[argv.index("-video_size") + 1], "640x360")
        self.assertEqual(argv[argv.index("-i") + 1], ":3.0+101,50")
        whole = x11_argv(Settings(folder="/tmp/rec"), datetime(2026, 11, 27, 17, 0, 0), ":3", None)
        self.assertNotIn("-video_size", whole)
        self.assertEqual(whole[whole.index("-i") + 1], ":3.0")

    def test_tray_records_the_selected_region_once(self) -> None:
        try:
            from desktop_recorder import ui
        except (ImportError, ValueError):
            self.skipTest("GTK 3 for Python is not installed")
        started: list = []

        class FakeCapture:
            warning = error = ai_job = None
            is_active = False

            def start(self, settings, _display):
                started.append(settings.region)
                self.is_active = True

            def stop(self):
                self.is_active = False

            def session_files(self):
                return []

        tray = ui.Tray.__new__(ui.Tray)
        tray.settings = Settings(folder="/tmp/dr", play_sounds=False, copy_to_clipboard=False)
        tray.settings.region = (0, 0, 0, 0)
        tray.capture = FakeCapture()
        tray.refresh = lambda: None
        with mock.patch.object(ui, "select_region", lambda done: done((10, 20, 300, 200))):
            tray.select_and_record()
        tray.stop_recording(False)
        tray.start_recording(False)
        self.assertEqual(started, [(10, 20, 300, 200), (0, 0, 0, 0)])
        self.assertEqual(tray.settings.region, (0, 0, 0, 0))

    def test_new_recording_during_the_pass_keeps_both_jobs(self) -> None:
        try:
            from desktop_recorder import ui
        except (ImportError, ValueError):
            self.skipTest("GTK 3 for Python is not installed")
        passes: list = []
        gate = threading.Event()

        class FakeCapture:
            warning = None
            is_active = True
            ai_job: object = ("/r/first", None, None)
            error: object = "first error"

            def stop(self):
                self.is_active = False

            def finish_folder(self, job=None, error=None):
                gate.wait(5)            # the pass starts after the next recording began
                passes.append((job, error))

        tray = ui.Tray.__new__(ui.Tray)
        tray.settings = Settings(folder="/tmp/dr", play_sounds=False)
        tray.capture = FakeCapture()
        tray.refresh = lambda: None
        tray.notify = lambda *_a: None
        with mock.patch.object(ui.GLib, "idle_add", lambda *_a: None):
            tray.stop_recording(False)
            tray.capture.ai_job, tray.capture.error = ("/r/second", None, None), None   # the next recording
            gate.set()
            for _ in range(100):
                if passes:
                    break
                time.sleep(0.02)
        self.assertEqual(passes, [(("/r/first", None, None), "first error")])
        self.assertEqual(tray.capture.ai_job, ("/r/second", None, None))

    def test_hotkey_opens_the_selector_in_folder_mode(self) -> None:
        try:
            from desktop_recorder import ui
        except (ImportError, ValueError):
            self.skipTest("GTK 3 for Python is not installed")
        calls: list = []
        tray = ui.Tray.__new__(ui.Tray)
        tray.settings = Settings(folder="/tmp/dr")
        tray.capture = mock.Mock(is_active=False)
        tray.select_and_record = lambda: calls.append("selector")
        tray.toggle = lambda: calls.append("toggle")
        tray.settings.output_mode = "folder"
        tray.hotkey_pressed()
        tray.capture.is_active = True
        tray.hotkey_pressed()   # stops
        tray.capture.is_active = False
        tray.settings.output_mode = "mp4"
        tray.hotkey_pressed()
        self.assertEqual(calls, ["selector", "toggle", "toggle"])

    @unittest.skipUnless(os.environ.get("DISPLAY") and shutil.which("xdotool"), "needs an X display and xdotool")
    def test_selector_with_real_input(self) -> None:
        import gi

        gi.require_version("Gtk", "3.0")
        from gi.repository import GLib, Gtk
        from desktop_recorder.region_select import select_region

        def run(actions: list[str]) -> list:
            got: list = []

            def done(region) -> None:
                got.append(region)
                Gtk.main_quit()

            def drive() -> None:
                time.sleep(1.0)
                for action in actions:
                    subprocess.run(["xdotool", *action.split()], check=False)
                    time.sleep(0.15)

            self.assertIsNone(select_region(done))
            threading.Thread(target=drive, daemon=True).start()
            GLib.timeout_add(10000, Gtk.main_quit)
            Gtk.main()
            return got

        self.assertEqual(run(["mousemove 100 80", "mousedown 1", "mousemove 250 200", "mousemove 421 301",
                              "mouseup 1"]), [(100, 80, 320, 220)])
        self.assertEqual(run(["key Escape"]), [None])
        self.assertEqual(run(["mousemove 30 30", "click 1", "key space"]), [WHOLE_SCREEN])

if __name__ == "__main__":
    unittest.main()
