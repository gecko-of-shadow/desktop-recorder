"""Linux recorder rules: settings, arguments, portal choices, process control."""

from __future__ import annotations

import os
import sys
import tempfile
import threading
import unittest
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
from desktop_recorder.recording import Recording  # noqa: E402
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
        self.assertEqual(argv[argv.index("-i") + 1], ":1.0")
        self.assertIn("alsa_output.monitor", argv)
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
