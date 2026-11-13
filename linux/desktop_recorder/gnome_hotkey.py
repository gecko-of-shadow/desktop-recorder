"""Register the shortcut with GNOME on Wayland.

X grabs do not see keys on a Wayland session. GNOME still runs a custom
keybinding that launches this program with --toggle.
"""

from __future__ import annotations

import ast
from collections.abc import Callable

from .hotkey import gtk_accelerator

SCHEMA = "org.gnome.settings-daemon.plugins.media-keys"
BINDING = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding"
PATH = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/desktop-recorder/"


def update_gnome_shortcut(modifiers: int, key: str, command: str, runner: Callable[..., object]) -> bool:
    """Point the Desktop Recorder custom shortcut at this command. False if gsettings is missing."""
    try:
        current = runner(["gsettings", "get", SCHEMA, "custom-keybindings"])
    except OSError:
        return False
    text = _stdout(current)
    if text is None:
        return False
    paths = _parse_list(text)
    if PATH not in paths:
        paths.append(PATH)
        listed = "[" + ", ".join("'" + item + "'" for item in paths) + "]"
        if not _ok(runner(["gsettings", "set", SCHEMA, "custom-keybindings", listed])):
            return False
    binding = gtk_accelerator(modifiers, key)
    prefix = ["gsettings", "set", BINDING + ":" + PATH]
    steps = (
        prefix + ["name", "Desktop Recorder"],
        prefix + ["binding", binding],
        prefix + ["command", command],
    )
    return all(_ok(runner(step)) for step in steps)


def _stdout(result: object) -> str | None:
    code = getattr(result, "returncode", 0)
    if code not in (0, None):
        return None
    return str(getattr(result, "stdout", "") or "")


def _ok(result: object) -> bool:
    return getattr(result, "returncode", 0) in (0, None)


def _parse_list(text: str) -> list[str]:
    raw = text.strip()
    if raw in ("@as []", "[]", ""):
        return []
    if raw.startswith("@as "):
        raw = raw[4:].strip()
    try:
        value = ast.literal_eval(raw)
    except (SyntaxError, ValueError):
        return []
    if isinstance(value, (list, tuple)):
        return [str(item) for item in value]
    return []
