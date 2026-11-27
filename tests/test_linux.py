"""Linux recorder rules: settings, arguments, portal choices, process control."""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "linux"))

from desktop_recorder.audio import monitor_from_listing  # noqa: E402
from desktop_recorder.autostart import autostart_enabled, write_autostart  # noqa: E402
from desktop_recorder.clipboard import uri_list  # noqa: E402
from desktop_recorder.ffmpeg_cmd import (  # noqa: E402
    capture_backend,
    wayland_ffmpeg_argv,
    wayland_pipeline,
    x11_argv,
    x11_target,
)
from desktop_recorder.gnome_hotkey import PATH, update_gnome_shortcut  # noqa: E402
from desktop_recorder.hotkey import try_set  # noqa: E402
from desktop_recorder.portal import PortalError, ScreenCast  # noqa: E402
from desktop_recorder import recording  # noqa: E402
from desktop_recorder.recording import Recording, stop_wait_seconds  # noqa: E402
from desktop_recorder.settings import Settings, parse_settings, serialize_settings  # noqa: E402
from desktop_recorder.single_instance import InstanceServer, send_command  # noqa: E402
from desktop_recorder.sounds import build_chime  # noqa: E402


FAKE = str(ROOT / "tests" / "fake_ffmpeg.py")


class HotkeyTests(unittest.TestCase):
    def test_matches_windows_rules(self) -> None:
        self.assertEqual(try_set("Win+Shift+D", (0, "A")), (12, "D"))
        self.assertIsNone(try_set("Shift+A", (12, "D")))
        self.assertIsNone(try_set("Ctrl+Enter", (12, "D")))
        self.assertIsNone(try_set("Ctrl+13", (12, "D")))
        self.assertEqual(try_set("Ctrl+5", (12, "D")), (2, "5"))
        self.assertEqual(try_set("Shift+F1", (12, "D")), (4, "F1"))


class SettingsTests(unittest.TestCase):
    def test_nan_and_linux_keys(self) -> None:
        parsed = parse_settings(
            "fps=NaN\nquality=9\nhotkey=Shift+A\nrecord_audio=0\n"
            "start_on_login=1\nwayland_restore_token=tok\ncustom_note=keep\n",
            Settings(folder="/tmp/Recordings"),
        )
        self.assertEqual(parsed.fps, 2)
        self.assertEqual(parsed.quality, 2)
        self.assertEqual(parsed.hotkey_text, "Win+Shift+D")
        self.assertFalse(parsed.record_audio)
        self.assertTrue(parsed.start_on_login)
        self.assertEqual(parsed.wayland_restore_token, "tok")
        self.assertEqual(parsed.extra_lines, ["custom_note=keep"])
        again = parse_settings(serialize_settings(parsed))
        self.assertEqual(again.extra_lines, ["custom_note=keep"])
        self.assertFalse(again.record_audio)
        self.assertEqual(again.wayland_restore_token, "tok")


class ArgumentTests(unittest.TestCase):
    def test_x11_and_wayland_commands(self) -> None:
        when = datetime(2026, 11, 13, 1, 2, 3, 127000)
        settings = Settings(folder="/home/me/Recordings", fps=2, quality=1, split_minutes=30, record_audio=True)
        argv = x11_argv(settings, when, ":1", "alsa_output.monitor")
        self.assertIn("x11grab", argv)
        self.assertNotIn("gdigrab", argv)
        self.assertEqual(argv[argv.index("x11grab") + 6], ":1.0")
        self.assertIn("alsa_output.monitor", argv)
        # Sound and picture share one clock that starts at `when`, and no encoder delay drops sound.
        shift = f"-{when.timestamp():.6f}"
        self.assertIn("-copyts", argv)
        self.assertEqual([argv[i + 1] for i, item in enumerate(argv) if item == "-itsoffset"], [shift, shift])
        self.assertLess(argv.index("pulse"), argv.index("x11grab"))
        self.assertEqual(argv[argv.index("-fragment_size") + 1], "9600")
        self.assertEqual(argv[argv.index("-map") + 1], "1:v")
        self.assertIn("aresample=async=1:first_pts=0", argv)
        self.assertTrue(argv[argv.index("-vf") + 1].startswith("fps=2:start_time=0,"))
        self.assertIn("stillimage,zerolatency", argv)
        silent = x11_argv(settings, when, ":1", None)
        self.assertNotIn("-copyts", silent)
        self.assertNotIn("-itsoffset", silent)
        self.assertIn("stillimage", silent)
        self.assertIn("expr:gte(t,n_forced*1800)", argv)
        self.assertTrue(argv[-1].endswith("desktop_2026-11-13_01-02-03_127_%03d.mp4"))
        percent = Settings(folder="/home/me/100%done", fps=2, split_minutes=1, record_audio=False)
        escaped = x11_argv(percent, when, ":0.0", None)
        self.assertIn("/home/me/100%%done/", escaped[-1])
        raw = wayland_ffmpeg_argv(settings, when, 1366, 768, "sink.monitor")
        self.assertIn("bgra", raw)
        self.assertIn("pipe:0", raw)
        self.assertIn("-shortest", raw)
        self.assertEqual(x11_target("localhost:1"), "localhost:1.0")
        self.assertEqual(x11_target(":0.0"), ":0.0")

    def test_backend_choice(self) -> None:
        previous = {name: os.environ.get(name) for name in ("DESKTOP_RECORDER_CAPTURE", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE")}
        try:
            os.environ.pop("DESKTOP_RECORDER_CAPTURE", None)
            os.environ.pop("WAYLAND_DISPLAY", None)
            os.environ["XDG_SESSION_TYPE"] = "x11"
            self.assertEqual(capture_backend(), "x11")
            os.environ["XDG_SESSION_TYPE"] = "wayland"
            self.assertEqual(capture_backend(), "wayland")
            os.environ["DESKTOP_RECORDER_CAPTURE"] = "x11"
            self.assertEqual(capture_backend(), "x11")
        finally:
            for name, value in previous.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

    def test_pipeline_targets_the_portal_node(self) -> None:
        text = wayland_pipeline(7, 15, "99", 2)
        self.assertIn("fd=7", text)
        self.assertIn("path=15", text)
        self.assertIn("target-object=99", text)
        self.assertIn("format=BGRA", text)
        self.assertIn("framerate=2/1", text)
        slow = wayland_pipeline(7, 15, None, 0.2)
        self.assertIn("framerate=1/5", slow)


class AudioTests(unittest.TestCase):
    def test_monitor_must_exist(self) -> None:
        listing = "10\talsa_output.pci.analog-stereo.monitor\tPipeWire\n"
        self.assertEqual(
            monitor_from_listing("alsa_output.pci.analog-stereo", listing),
            "alsa_output.pci.analog-stereo.monitor",
        )
        self.assertIsNone(monitor_from_listing("auto_null", listing))
        self.assertIsNone(monitor_from_listing("missing", listing))
        from desktop_recorder.audio import choose_monitor

        sinks = "35\tauto_null\tPipeWire\n41\tdr_speakers\tPipeWire\n"
        sources = "41\tdr_speakers.monitor\tPipeWire\n"
        self.assertEqual(choose_monitor("@DEFAULT_SINK@", sinks, sources), "dr_speakers.monitor")
        self.assertIsNone(choose_monitor("@DEFAULT_SINK@", "35\tauto_null\tPipeWire\n", "35\tauto_null.monitor\tPipeWire\n"))


class PortalTests(unittest.TestCase):
    def test_source_choices(self) -> None:
        class Fake:
            def __init__(self) -> None:
                self.calls = []

            def request(self, method, options):
                self.calls.append((method, dict(options)))
                if method == "CreateSession":
                    return {"session_handle": "/session/1"}
                if method == "Start":
                    return {
                        "streams": [(5, {"size": (1920, 1080), "pipewire-serial": "42"})],
                        "restore_token": "saved",
                    }
                return {}

            def open_pipewire(self, session):
                self.calls.append(("OpenPipeWireRemote", {"session": session}))
                return 17

            def close_session(self, session):
                self.calls.append(("Close", {"session": session}))

        fake = Fake()
        stream = ScreenCast(fake).open("old")
        methods = [call[0] for call in fake.calls]
        self.assertEqual(methods, ["CreateSession", "SelectSources", "Start", "OpenPipeWireRemote"])
        select = fake.calls[1][1]
        self.assertEqual(select["types"], 1)
        self.assertTrue(select["multiple"])
        self.assertEqual(select["cursor_mode"], 2)
        self.assertEqual(select["restore_token"], "old")
        self.assertEqual(stream.node_id, 5)
        self.assertEqual(stream.serial, "42")
        self.assertEqual(stream.fd, 17)
        self.assertEqual(stream.restore_token, "saved")
        self.assertEqual(stream.width, 1920)

    def test_cancel_closes_the_session(self) -> None:
        class Fake:
            def request(self, method, options):
                if method == "CreateSession":
                    return {"session_handle": "/session/1"}
                if method == "Start":
                    return {"streams": []}
                return {}

            def open_pipewire(self, session):
                raise AssertionError("no stream")

            def close_session(self, session):
                self.closed = session

        fake = Fake()
        with self.assertRaises(PortalError):
            ScreenCast(fake).open()
        self.assertEqual(fake.closed, "/session/1")


class RecordingTests(unittest.TestCase):
    def test_failure_stop_and_overlap(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-py-")
        old = os.path.join(folder, "desktop_old.mp4")
        with open(old, "w", encoding="utf-8") as handle:
            handle.write("old")
        session = Recording()
        session.check_file = lambda _path: None
        failed = []
        session.on_failed = failed.append
        output = os.path.join(folder, "desktop_new.mp4")
        self.assertIsNone(session.start(["python3", FAKE, "--fail", output], folder, 2))
        for _ in range(40):
            if not session.is_active:
                break
            threading.Event().wait(0.05)
        self.assertFalse(session.is_active)
        self.assertTrue(any("boom: bad input" in item for item in failed))

        failed.clear()
        output = os.path.join(folder, "desktop_ok.mp4")
        self.assertIsNone(session.start(["python3", FAKE, output], folder, 2))
        self.assertIsNotNone(session.start(["python3", FAKE, os.path.join(folder, "desktop_two.mp4")], folder, 2))
        with open(old, "a", encoding="utf-8") as handle:
            handle.write("touch")
        session.stop()
        self.assertEqual(failed, [])
        files = session.session_files()
        self.assertEqual(files, [output])
        self.assertGreater(os.path.getsize(output), 1)

        slow = os.path.join(folder, "desktop_slow.mp4")
        self.assertIsNone(session.start(["python3", FAKE, "--slow", slow], folder, 2))
        stopper = threading.Thread(target=session.stop)
        stopper.start()
        threading.Event().wait(0.15)
        self.assertIsNotNone(session.start(["python3", FAKE, os.path.join(folder, "desktop_other.mp4")], folder, 2))
        stopper.join(8)
        self.assertFalse(session.is_active)

    def test_bad_exit_and_damaged_file_are_errors(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-py-")
        session = Recording()
        session.check_file = lambda _path: None
        output = os.path.join(folder, "desktop_code.mp4")
        self.assertIsNone(session.start(["python3", FAKE, "--fail-at-stop", output], folder, 2))
        session.stop()
        self.assertIn("error code 3", session.error or "")

        session.check_file = lambda path: "partial file" if path.endswith("desktop_cut.mp4") else None
        cut = os.path.join(folder, "desktop_cut.mp4")
        self.assertIsNone(session.start(["python3", FAKE, cut], folder, 2))
        session.stop()
        self.assertIn("desktop_cut.mp4 is damaged: partial file", session.error or "")
        self.assertEqual(session.session_files(), [])
        self.assertTrue(os.path.exists(cut))

        good = os.path.join(folder, "desktop_good.mp4")
        self.assertIsNone(session.start(["python3", FAKE, good], folder, 2))
        session.stop()
        self.assertIsNone(session.error)
        self.assertEqual(session.session_files(), [good])

    def test_low_disk(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-py-")
        session = Recording()
        session.check_file = lambda _path: None
        session.min_free_start = 1 << 62
        error = session.start(["python3", FAKE, os.path.join(folder, "desktop_none.mp4")], folder, 2)
        self.assertIn("Not enough free disk space", error or "")
        self.assertFalse(session.is_active)

        session.min_free_start = 0
        session.min_free_recording = 1 << 62
        failed = []
        session.on_failed = failed.append
        previous = recording.DISK_CHECK_SECONDS
        recording.DISK_CHECK_SECONDS = 0.05
        try:
            output = os.path.join(folder, "desktop_low.mp4")
            self.assertIsNone(session.start(["python3", FAKE, output], folder, 2))
            for _ in range(100):
                if failed:
                    break
                threading.Event().wait(0.05)
        finally:
            recording.DISK_CHECK_SECONDS = previous
        self.assertFalse(session.is_active)
        self.assertTrue(any("stopped early" in item for item in failed))
        self.assertIn("stopped early", session.error or "")
        # The stop was clean ("q"), so the file was finished.
        with open(output, "rb") as handle:
            self.assertEqual(handle.read(), b"xok")


class ProcessLifetimeTests(unittest.TestCase):
    def test_stop_wait_matches_windows(self) -> None:
        # RecordingSession.StopWaitMs: min(25000, 15000 + 2000 / max(0.2, fps)).
        self.assertAlmostEqual(stop_wait_seconds(2), 16.0)
        self.assertAlmostEqual(stop_wait_seconds(0.2), 25.0)
        self.assertAlmostEqual(stop_wait_seconds(0.01), 25.0)
        self.assertAlmostEqual(stop_wait_seconds(60), 15.0 + 2.0 / 60)

    def test_ffmpeg_ends_when_the_app_is_killed(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-py-")
        # Real ffmpeg ignores end of input on stdin, so the stand-in must too.
        script = (
            "import sys, time\n"
            f"sys.path.insert(0, {str(ROOT / 'linux')!r})\n"
            "from desktop_recorder.recording import Recording\n"
            "r = Recording()\n"
            f"r.start([sys.executable, '-c', 'import time; time.sleep(60)'], {folder!r}, 2)\n"
            "print(r._proc.pid, flush=True)\n"
            "time.sleep(60)\n"
        )
        app = subprocess.Popen([sys.executable, "-c", script], stdout=subprocess.PIPE, text=True)
        try:
            child = int(app.stdout.readline())
            os.kill(app.pid, signal.SIGKILL)
            app.wait(5)
            gone = False
            for _ in range(60):
                try:
                    os.kill(child, 0)
                except ProcessLookupError:
                    gone = True
                    break
                time.sleep(0.05)
            self.assertTrue(gone, "ffmpeg outlived the killed app")
        finally:
            if app.poll() is None:
                app.kill()
                app.wait(5)
            app.stdout.close()

    def test_thread_failure_does_not_leave_ffmpeg_running(self) -> None:
        folder = tempfile.mkdtemp(prefix="dr-py-")
        output = os.path.join(folder, "desktop_thread.mp4")
        session = Recording()
        with mock.patch.object(threading.Thread, "start", side_effect=RuntimeError("can't start new thread")):
            error = session.start(["python3", FAKE, output], folder, 2)
        self.assertIn("can't start new thread", error or "")
        self.assertFalse(session.is_active)
        # "q" reached the stand-in, so it finished the file and exited.
        with open(output, "rb") as handle:
            self.assertEqual(handle.read(), b"xok")


def gtk_importable() -> bool:
    try:
        import gi

        gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk  # noqa: F401
    except (ImportError, ValueError):
        return False
    return True


@unittest.skipUnless(gtk_importable(), "GTK 3 for Python is not installed")
class ChimeOrderTests(unittest.TestCase):
    """The start and stop chimes must not be in a recording that has system audio."""

    def make_tray(self, record_audio: bool, log: list):
        from desktop_recorder import ui

        class FakeCapture:
            warning = None
            error = None
            is_active = False

            def start(self, _settings, _display):
                log.append("capture start")
                self.is_active = True
                return None

            def stop(self):
                log.append("capture stop")
                self.is_active = False

            def session_files(self):
                return []

        tray = ui.Tray.__new__(ui.Tray)
        tray.settings = Settings(folder="/tmp/dr", play_sounds=True, record_audio=record_audio, copy_to_clipboard=False)
        tray.capture = FakeCapture()
        tray.refresh = lambda: None
        tray.notify = lambda _title, body: log.append("notify " + body)
        return tray, ui

    def test_chimes_stay_out_of_audio_recordings(self) -> None:
        log: list = []
        tray, ui = self.make_tray(True, log)
        with mock.patch.object(ui.sounds, "play", lambda up, wait=False: log.append(f"chime {up} wait={wait}")):
            tray.start_recording()
            tray.stop_recording()
        self.assertEqual(log, ["chime True wait=True", "capture start", "capture stop", "chime False wait=False"])

    def test_silent_recordings_keep_the_quick_start(self) -> None:
        log: list = []
        tray, ui = self.make_tray(False, log)
        with mock.patch.object(ui.sounds, "play", lambda up, wait=False: log.append(f"chime {up} wait={wait}")):
            tray.start_recording()
        self.assertEqual(log, ["capture start", "chime True wait=False"])


class OtherTests(unittest.TestCase):
    def test_chime_and_clipboard_and_autostart(self) -> None:
        wav = build_chime(660, 880)
        self.assertTrue(wav.startswith(b"RIFF"))
        self.assertIn(b"data", wav[:44])
        text = uri_list(["/tmp/My Videos/desktop.mp4"])
        self.assertIn("file:///tmp/My%20Videos/desktop.mp4", text)
        directory = tempfile.mkdtemp(prefix="dr-auto-")
        path = os.path.join(directory, "desktop-recorder.desktop")
        write_autostart("/usr/local/bin/desktop-recorder", True, path)
        self.assertTrue(autostart_enabled(path))
        write_autostart("/usr/local/bin/desktop-recorder", False, path)
        self.assertFalse(autostart_enabled(path))
        self.assertTrue(os.path.exists(path))

    def test_socket_and_gnome_shortcut(self) -> None:
        directory = tempfile.mkdtemp(prefix="dr-sock-")
        path = os.path.join(directory, "control.sock")
        seen = []

        def on_command(command: str) -> str:
            seen.append(command)
            return "STOPPED"

        server = InstanceServer(path, on_command)
        self.assertTrue(server.start())
        second = InstanceServer(path, on_command)
        self.assertFalse(second.start())
        self.assertEqual(send_command(path, "TOGGLE"), "STOPPED")
        self.assertEqual(seen, ["TOGGLE"])
        server.stop()
        # A stale socket is replaced.
        open(path, "w", encoding="utf-8").close()
        self.assertTrue(server.start())
        server.stop()

        class Result:
            def __init__(self, stdout: str, returncode: int = 0) -> None:
                self.stdout = stdout
                self.returncode = returncode

        calls = []

        def runner(args):
            calls.append(args)
            if args[1] == "get":
                return Result("@as []")
            return Result("")

        self.assertTrue(update_gnome_shortcut(12, "D", "/usr/bin/desktop-recorder --toggle", runner))
        flat = [" ".join(item) for item in calls]
        self.assertTrue(any(PATH in item and "custom-keybindings" in item for item in flat))
        self.assertTrue(any("<Super><Shift>d" in item for item in flat))
        self.assertTrue(any("/usr/bin/desktop-recorder --toggle" in item for item in flat))


if __name__ == "__main__":
    unittest.main()
