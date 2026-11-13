"""GTK tray and settings window."""

from __future__ import annotations

import os
import subprocess
import sys

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk

from . import sounds
from .autostart import autostart_enabled, write_autostart
from .capture import Capture
from .clipboard import copy_files
from .ffmpeg_cmd import capture_backend
from .gnome_hotkey import update_gnome_shortcut
from .hotkey import parse_key
from .hotkey_x11 import X11Hotkey
from .settings import Settings, load_settings, save_settings, config_dir
from .single_instance import InstanceServer

QUALITY = (
    "Smallest files (text still readable)",
    "Balanced (recommended)",
    "Sharpest text (bigger files)",
)


def run() -> int:
    if os.environ.get("DISPLAY") and not os.environ.get("GDK_BACKEND"):
        # A Wayland session still gets an X display via XWayland. Prefer the real one.
        if capture_backend() == "wayland":
            os.environ["GDK_BACKEND"] = "wayland"
    app = Tray()
    if not app.owned:
        dialog = Gtk.MessageDialog(
            message_type=Gtk.MessageType.INFO,
            buttons=Gtk.ButtonsType.OK,
            text="Desktop Recorder is already running. Look for its icon in the tray.",
        )
        dialog.run()
        dialog.destroy()
        return 0
    Gtk.main()
    return 0


class Tray:
    def __init__(self) -> None:
        self.settings = load_settings()
        self.settings.start_on_login = autostart_enabled()
        self.capture = Capture()
        self.capture.x11.on_failed = self._failed
        self.hotkey: X11Hotkey | None = None
        socket_path = os.path.join(config_dir(), "control.sock")
        self.server = InstanceServer(socket_path, self._command)
        self.owned = self.server.start()
        if not self.owned:
            return
        self._build_menu()
        self._install_hotkey()
        self.refresh()
        if self.settings.record_on_launch:
            GLib.idle_add(self.start_recording)

    def _build_menu(self) -> None:
        self.menu = Gtk.Menu()
        self.status_item = Gtk.MenuItem(label="Not recording")
        self.status_item.set_sensitive(False)
        self.toggle_item = Gtk.MenuItem(label="Start recording")
        self.toggle_item.connect("activate", lambda *_a: self.toggle())
        folder_item = Gtk.MenuItem(label="Open recordings folder")
        folder_item.connect("activate", lambda *_a: self.open_folder())
        settings_item = Gtk.MenuItem(label="Settings...")
        settings_item.connect("activate", lambda *_a: self.show_settings())
        quit_item = Gtk.MenuItem(label="Exit")
        quit_item.connect("activate", lambda *_a: self.quit())
        for item in (
            self.status_item,
            Gtk.SeparatorMenuItem(),
            self.toggle_item,
            folder_item,
            settings_item,
            Gtk.SeparatorMenuItem(),
            quit_item,
        ):
            self.menu.append(item)
        self.menu.show_all()
        self._icon = None
        self._indicator = None
        if _use_appindicator():
            self._indicator = _indicator(self.menu, self._icon_path(False))
        if self._indicator is None:
            self._icon = Gtk.StatusIcon()
            self._icon.connect("popup-menu", self._popup)
            self._icon.connect("activate", lambda *_a: self.toggle())
            self._icon.set_visible(True)

    def _popup(self, _icon, button, time) -> None:
        self.menu.popup(None, None, None, None, button, time)

    def _command(self, command: str) -> str:
        box: dict[str, str] = {}

        def run() -> bool:
            if command == "TOGGLE":
                self.toggle()
            box["reply"] = "RECORDING" if self.capture.is_active else "STOPPED"
            return False

        # The socket thread must not touch GTK itself.
        if threading_is_main():
            run()
        else:
            done = __import__("threading").Event()

            def wrapped() -> bool:
                run()
                done.set()
                return False

            GLib.idle_add(wrapped)
            done.wait(5)
        return box.get("reply", "STOPPED")

    def start_recording(self, sound: bool = True) -> bool:
        error = self.capture.start(self.settings, os.environ.get("DISPLAY"))
        self.refresh()
        if error:
            self.notify("Desktop Recorder", error)
            return False
        if self.capture.warning:
            self.notify("Desktop Recorder", self.capture.warning)
        if sound and self.settings.play_sounds:
            sounds.play(True)
        return False

    def stop_recording(self, sound: bool = True) -> None:
        if not self.capture.is_active:
            return
        if sound and self.settings.play_sounds:
            sounds.play(False)
        self.capture.stop()
        self.refresh()
        if not self.settings.copy_to_clipboard:
            return
        files = self.capture.session_files()
        if not files:
            return
        try:
            copy_files(files)
            self.notify(
                "Desktop Recorder",
                "Video copied. Paste it into a folder or a chat."
                if len(files) == 1
                else f"{len(files)} video files copied. Paste them into a folder or a chat.",
            )
        except Exception as exc:
            self.notify("Desktop Recorder", "Could not copy the video: " + str(exc))

    def toggle(self) -> None:
        if self.capture.is_active:
            self.stop_recording()
        else:
            self.start_recording()

    def open_folder(self) -> None:
        os.makedirs(self.settings.folder, exist_ok=True)
        subprocess.Popen(["xdg-open", self.settings.folder])

    def show_settings(self) -> None:
        dialog = SettingsDialog(self.settings)
        if dialog.run() != Gtk.ResponseType.OK:
            dialog.destroy()
            return
        self.settings = dialog.result()
        dialog.destroy()
        try:
            save_settings(self.settings)
            write_autostart(_command_path(), self.settings.start_on_login)
        except Exception as exc:
            self.notify("Desktop Recorder", "Cannot save the settings: " + str(exc))
        self._install_hotkey()
        self.refresh()
        if self.capture.is_active:
            self.capture.stop()
            self.start_recording(False)

    def quit(self) -> None:
        self.stop_recording(False)
        if self.hotkey is not None:
            self.hotkey.stop()
        self.server.stop()
        if self._icon is not None:
            self._icon.set_visible(False)
        Gtk.main_quit()

    def refresh(self) -> None:
        recording = self.capture.is_active
        fps = f"{self.settings.fps:.3f}".rstrip("0").rstrip(".")
        self.status_item.set_label("Recording at " + fps + " fps" if recording else "Not recording")
        hotkey = ""
        if self.hotkey is not None and self.hotkey.registered:
            hotkey = "    " + self.settings.hotkey_text
        self.toggle_item.set_label(("Stop recording" if recording else "Start recording") + hotkey)
        tooltip = "Desktop Recorder: recording" if recording else "Desktop Recorder: stopped"
        path = self._icon_path(recording)
        if self._icon is not None:
            self._icon.set_from_file(path)
            self._icon.set_tooltip_text(tooltip)
        if self._indicator is not None:
            self._indicator.set_icon_full(path, tooltip)

    def _failed(self, message: str) -> None:
        def show() -> bool:
            self.refresh()
            if self.settings.play_sounds:
                sounds.play(False)
            self.notify("Desktop Recorder stopped", message[:250])
            return False

        GLib.idle_add(show)

    def notify(self, title: str, body: str) -> None:
        try:
            gi.require_version("Notify", "0.7")
            from gi.repository import Notify

            if not Notify.is_initted():
                Notify.init("Desktop Recorder")
            note = Notify.Notification.new(title, body, None)
            note.show()
        except Exception:
            sys.stderr.write(title + ": " + body + "\n")

    def _install_hotkey(self) -> None:
        if self.hotkey is not None:
            self.hotkey.stop()
            self.hotkey = None
        if capture_backend() == "x11":
            self.hotkey = X11Hotkey(
                os.environ.get("DISPLAY"),
                self.settings.hotkey_modifiers,
                self.settings.hotkey_key,
                lambda: GLib.idle_add(self.toggle),
            )
            if not self.hotkey.start():
                self.notify(
                    "Desktop Recorder",
                    self.settings.hotkey_text + " is used by another program, so the hotkey is off.",
                )
            return
        command = _command_path()
        if not command:
            self.notify("Desktop Recorder", "Install the program to enable the Wayland hotkey.")
            return

        def runner(args):
            return subprocess.run(args, capture_output=True, text=True, check=False)

        if not update_gnome_shortcut(
            self.settings.hotkey_modifiers,
            self.settings.hotkey_key,
            command + " --toggle",
            runner,
        ):
            self.notify(
                "Desktop Recorder",
                "This Wayland session has no GNOME shortcut setting, so the hotkey is off. Use the tray menu.",
            )

    def _icon_path(self, recording: bool) -> str:
        directory = config_dir()
        os.makedirs(directory, exist_ok=True)
        path = os.path.join(directory, "icon-on.png" if recording else "icon-off.png")
        if not os.path.exists(path):
            _write_icon(path, recording)
        return path


def _use_appindicator() -> bool:
    choice = os.environ.get("DESKTOP_RECORDER_TRAY", "").strip().lower()
    if choice == "appindicator":
        return True
    if choice == "status":
        return False
    desktop = os.environ.get("XDG_CURRENT_DESKTOP", "")
    return capture_backend() == "wayland" or "GNOME" in desktop.split(":")


def _indicator(menu, icon_path):
    try:
        gi.require_version("AyatanaAppIndicator3", "0.1")
        from gi.repository import AyatanaAppIndicator3 as AppIndicator
    except (ImportError, ValueError):
        return None
    indicator = AppIndicator.Indicator.new(
        "desktop-recorder",
        icon_path,
        AppIndicator.IndicatorCategory.APPLICATION_STATUS,
    )
    indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
    indicator.set_menu(menu)
    return indicator


def _write_icon(path: str, recording: bool) -> None:
    import cairo

    surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, 32, 32)
    ctx = cairo.Context(surface)
    ctx.set_source_rgba(0, 0, 0, 0)
    ctx.paint()
    ctx.set_source_rgb(1, 1, 1)
    ctx.rectangle(1, 3, 30, 21)
    ctx.rectangle(13, 24, 6, 3)
    ctx.rectangle(8, 27, 16, 3)
    ctx.fill()
    if recording:
        ctx.set_source_rgb(220 / 255, 30 / 255, 30 / 255)
    else:
        ctx.set_source_rgb(110 / 255, 110 / 255, 110 / 255)
    ctx.rectangle(4, 6, 24, 15)
    ctx.fill()
    if recording:
        ctx.set_source_rgb(1, 1, 1)
        ctx.arc(16, 13.5, 4, 0, 6.3)
        ctx.fill()
    surface.write_to_png(path)


def _command_path() -> str:
    command = os.environ.get("DESKTOP_RECORDER_COMMAND", "")
    if command and os.path.exists(command.split()[0]):
        return command
    local = os.path.expanduser("~/.local/bin/desktop-recorder")
    if os.path.exists(local):
        return local
    return ""


def threading_is_main() -> bool:
    import threading

    return threading.current_thread() is threading.main_thread()


class SettingsDialog(Gtk.Dialog):
    def __init__(self, current: Settings) -> None:
        super().__init__(title="Desktop Recorder settings")
        self.set_resizable(False)
        self.add_button("Cancel", Gtk.ResponseType.CANCEL)
        self.add_button("Save", Gtk.ResponseType.OK)
        box = self.get_content_area()
        grid = Gtk.Grid(column_spacing=8, row_spacing=8, margin=12)
        box.add(grid)
        self.folder = Gtk.Entry()
        self.folder.set_text(current.folder)
        browse = Gtk.Button(label="Browse...")
        browse.connect("clicked", self._browse)
        self.fps = Gtk.SpinButton.new_with_range(0.2, 60, 0.1)
        self.fps.set_digits(1)
        self.fps.set_value(current.fps)
        self.quality = Gtk.ComboBoxText()
        for name in QUALITY:
            self.quality.append_text(name)
        self.quality.set_active(current.quality)
        self.split = Gtk.SpinButton.new_with_range(0, 1440, 1)
        self.split.set_digits(0)
        self.split.set_value(current.split_minutes)
        self.startup = Gtk.CheckButton(label="Start Desktop Recorder when I log in")
        self.startup.set_active(current.start_on_login)
        self.record = Gtk.CheckButton(label="Start recording when Desktop Recorder starts")
        self.record.set_active(current.record_on_launch)
        self.copy = Gtk.CheckButton(label="Copy the video to the clipboard when recording stops")
        self.copy.set_active(current.copy_to_clipboard)
        self.sound = Gtk.CheckButton(label="Play a sound when recording starts and stops")
        self.sound.set_active(current.play_sounds)
        self.audio = Gtk.CheckButton(label="Record system audio (what you hear)")
        self.audio.set_active(current.record_audio)
        self.win = Gtk.CheckButton(label="Win")
        self.ctrl = Gtk.CheckButton(label="Ctrl")
        self.alt = Gtk.CheckButton(label="Alt")
        self.shift = Gtk.CheckButton(label="Shift")
        self.win.set_active(bool(current.hotkey_modifiers & 8))
        self.ctrl.set_active(bool(current.hotkey_modifiers & 2))
        self.alt.set_active(bool(current.hotkey_modifiers & 1))
        self.shift.set_active(bool(current.hotkey_modifiers & 4))
        self.key = Gtk.ComboBoxText()
        for char in [chr(code) for code in range(ord("A"), ord("Z") + 1)]:
            self.key.append_text(char)
        for digit in "0123456789":
            self.key.append_text(digit)
        for number in range(1, 13):
            self.key.append_text("F" + str(number))
        if current.hotkey_key not in [self.key.get_active_text()]:
            # get_active_text is empty until a row is active; search the model instead.
            pass
        self._select_key(current.hotkey_key)
        grid.attach(Gtk.Label(label="Save recordings to:", xalign=0), 0, 0, 2, 1)
        grid.attach(self.folder, 0, 1, 1, 1)
        grid.attach(browse, 1, 1, 1, 1)
        grid.attach(Gtk.Label(label="Frames per second (0.2 to 60):", xalign=0), 0, 2, 1, 1)
        grid.attach(self.fps, 1, 2, 1, 1)
        grid.attach(Gtk.Label(label="Quality:", xalign=0), 0, 3, 1, 1)
        grid.attach(self.quality, 1, 3, 1, 1)
        grid.attach(Gtk.Label(label="New file every (minutes, 0 = never):", xalign=0), 0, 4, 1, 1)
        grid.attach(self.split, 1, 4, 1, 1)
        mods = Gtk.Box(spacing=6)
        for box_ in (self.win, self.ctrl, self.alt, self.shift, self.key):
            mods.pack_start(box_, False, False, 0)
        grid.attach(Gtk.Label(label="Hotkey:", xalign=0), 0, 5, 1, 1)
        grid.attach(mods, 1, 5, 1, 1)
        row = 6
        for widget in (self.startup, self.record, self.copy, self.sound, self.audio):
            grid.attach(widget, 0, row, 2, 1)
            row += 1
        self.show_all()
        self.current = current

    def _select_key(self, name: str) -> None:
        model = self.key.get_model()
        for index, row in enumerate(model):
            if row[0] == name:
                self.key.set_active(index)
                return
        self.key.append_text(name)
        self.key.set_active(len(model) - 1)

    def _browse(self, _button) -> None:
        chooser = Gtk.FileChooserDialog(
            title="Recordings folder",
            parent=self,
            action=Gtk.FileChooserAction.SELECT_FOLDER,
        )
        chooser.add_button("Cancel", Gtk.ResponseType.CANCEL)
        chooser.add_button("Select", Gtk.ResponseType.OK)
        chooser.set_filename(self.folder.get_text())
        if chooser.run() == Gtk.ResponseType.OK:
            self.folder.set_text(chooser.get_filename() or "")
        chooser.destroy()

    def result(self) -> Settings:
        modifiers = (
            (8 if self.win.get_active() else 0)
            | (2 if self.ctrl.get_active() else 0)
            | (1 if self.alt.get_active() else 0)
            | (4 if self.shift.get_active() else 0)
        )
        key = self.key.get_active_text() or "D"
        if parse_key(key) is None or not (modifiers & (1 | 2 | 8) or key.startswith("F")):
            self.notify_invalid()
            key = self.current.hotkey_key
            modifiers = self.current.hotkey_modifiers
        folder = os.path.abspath(os.path.expanduser(self.folder.get_text().strip().strip('"')))
        if folder != "/":
            folder = folder.rstrip("/")
        updated = Settings(
            folder=folder,
            fps=self.fps.get_value(),
            quality=self.quality.get_active(),
            split_minutes=int(self.split.get_value()),
            record_on_launch=self.record.get_active(),
            copy_to_clipboard=self.copy.get_active(),
            play_sounds=self.sound.get_active(),
            hotkey_modifiers=modifiers,
            hotkey_key=parse_key(key) or key,
            record_audio=self.audio.get_active(),
            start_on_login=self.startup.get_active(),
            wayland_restore_token=self.current.wayland_restore_token,
            extra_lines=list(self.current.extra_lines),
        )
        return updated

    def notify_invalid(self) -> None:
        message = Gtk.MessageDialog(
            parent=self,
            message_type=Gtk.MessageType.WARNING,
            buttons=Gtk.ButtonsType.OK,
            text="For the hotkey, tick at least one of Win, Ctrl, Alt and pick a key. Shift alone works only with F1 to F12.",
        )
        message.run()
        message.destroy()

    def do_response(self, response_id: int) -> None:
        # Keep the dialog from closing when the hotkey is not valid.
        if response_id == Gtk.ResponseType.OK:
            modifiers = (
                (8 if self.win.get_active() else 0)
                | (2 if self.ctrl.get_active() else 0)
                | (1 if self.alt.get_active() else 0)
                | (4 if self.shift.get_active() else 0)
            )
            key = self.key.get_active_text() or ""
            parsed = parse_key(key)
            valid = parsed is not None and (modifiers & (1 | 2 | 8) or (parsed.startswith("F") and modifiers & 4))
            if modifiers & (1 | 2 | 8) and parsed:
                valid = True
            if not valid:
                self.notify_invalid()
                return
            folder = self.folder.get_text().strip().strip('"')
            if not os.path.isabs(os.path.expanduser(folder)):
                self.notify_folder("Give a full folder path, such as /home/you/Recordings.")
                return
            try:
                os.makedirs(os.path.expanduser(folder), exist_ok=True)
            except OSError as exc:
                self.notify_folder("Cannot create that folder: " + str(exc))
                return
        Gtk.Dialog.do_response(self, response_id)

    def notify_folder(self, text: str) -> None:
        message = Gtk.MessageDialog(
            parent=self,
            message_type=Gtk.MessageType.WARNING,
            buttons=Gtk.ButtonsType.OK,
            text=text,
        )
        message.run()
        message.destroy()
