"""Global hotkey on X11.

NumLock and CapsLock are grabbed as well, or the shortcut dies when they are on.
Wayland compositors do not deliver these grabs; GNOME uses a custom shortcut instead.
"""

from __future__ import annotations

import ctypes
import select
import threading
from collections.abc import Callable

ShiftMask = 1
LockMask = 2
ControlMask = 4
Mod1Mask = 8
Mod2Mask = 16
Mod4Mask = 64
GrabModeAsync = 1
KeyPress = 2


class _XEvent(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("pad", ctypes.c_byte * 188)]


def x_modifiers(modifiers: int) -> int:
    value = 0
    if modifiers & 1:
        value |= Mod1Mask
    if modifiers & 2:
        value |= ControlMask
    if modifiers & 4:
        value |= ShiftMask
    if modifiers & 8:
        value |= Mod4Mask
    return value


def keysym_for(key: str) -> int:
    if len(key) == 1 and "A" <= key <= "Z":
        return ord(key)
    if len(key) == 1 and key.isdigit():
        return ord(key)
    if key.startswith("F") and key[1:].isdigit():
        number = int(key[1:])
        if 1 <= number <= 24:
            return 0xFFBE + number - 1
    return 0


class X11Hotkey:
    def __init__(self, display_name: str | None, modifiers: int, key: str, callback: Callable[[], None]) -> None:
        self._display_name = display_name
        self._modifiers = modifiers
        self._key = key
        self._callback = callback
        self._stop = threading.Event()
        self._ready = threading.Event()
        self._thread: threading.Thread | None = None
        self._grab_failed = False
        self._error_handler = None
        self.registered = False

    def start(self) -> bool:
        self._thread = threading.Thread(target=self._run, name="x11-hotkey", daemon=True)
        self._thread.start()
        self._ready.wait(2)
        return self.registered

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(2)

    def _run(self) -> None:
        lib = ctypes.CDLL("libX11.so.6")
        lib.XInitThreads()
        lib.XOpenDisplay.restype = ctypes.c_void_p
        lib.XOpenDisplay.argtypes = [ctypes.c_char_p]
        lib.XDefaultRootWindow.restype = ctypes.c_ulong
        lib.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        lib.XKeysymToKeycode.restype = ctypes.c_uint
        lib.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        lib.XGrabKey.restype = ctypes.c_int
        lib.XGrabKey.argtypes = [
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.c_uint,
            ctypes.c_ulong,
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
        ]
        lib.XUngrabKey.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint, ctypes.c_ulong]
        lib.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.XConnectionNumber.restype = ctypes.c_int
        lib.XConnectionNumber.argtypes = [ctypes.c_void_p]
        lib.XPending.restype = ctypes.c_int
        lib.XPending.argtypes = [ctypes.c_void_p]
        lib.XNextEvent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        lib.XCloseDisplay.argtypes = [ctypes.c_void_p]
        handler_type = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)

        def on_error(_display, _event):
            self._grab_failed = True
            return 0

        self._error_handler = handler_type(on_error)
        lib.XSetErrorHandler.argtypes = [handler_type]
        lib.XSetErrorHandler(self._error_handler)
        name = None if not self._display_name else self._display_name.encode()
        display = lib.XOpenDisplay(name)
        if not display:
            self._ready.set()
            return
        try:
            root = lib.XDefaultRootWindow(display)
            keycode = lib.XKeysymToKeycode(display, keysym_for(self._key))
            if keycode == 0:
                return
            base = x_modifiers(self._modifiers)
            extras = (0, LockMask, Mod2Mask, LockMask | Mod2Mask)
            for extra in extras:
                lib.XGrabKey(display, keycode, base | extra, root, 0, GrabModeAsync, GrabModeAsync)
            lib.XSync(display, 0)
            if self._grab_failed:
                for extra in extras:
                    lib.XUngrabKey(display, keycode, base | extra, root)
                return
            self.registered = True
            self._ready.set()
            fd = lib.XConnectionNumber(display)
            event = _XEvent()
            while not self._stop.is_set():
                ready, _, _ = select.select([fd], [], [], 0.2)
                if not ready:
                    continue
                while lib.XPending(display):
                    lib.XNextEvent(display, ctypes.byref(event))
                    if event.type == KeyPress:
                        try:
                            self._callback()
                        except Exception:
                            pass
            for extra in extras:
                lib.XUngrabKey(display, keycode, base | extra, root)
        finally:
            self._ready.set()
            lib.XCloseDisplay(display)
