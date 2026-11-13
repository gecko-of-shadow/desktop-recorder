"""XDG autostart entry. Disabling hides it instead of removing the file."""

from __future__ import annotations

import os


def autostart_path() -> str:
    config = os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(config, "autostart", "desktop-recorder.desktop")


def write_autostart(command: str, enabled: bool, path: str | None = None) -> None:
    target = path or autostart_path()
    os.makedirs(os.path.dirname(target), exist_ok=True)
    hidden = "false" if enabled else "true"
    text = (
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=Desktop Recorder\n"
        "Comment=Record the desktop to MP4\n"
        f"Exec={command}\n"
        f"Hidden={hidden}\n"
        "X-GNOME-Autostart-enabled=" + ("true" if enabled else "false") + "\n"
        "Terminal=false\n"
    )
    temporary = target + ".tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        handle.write(text)
    os.replace(temporary, target)


def autostart_enabled(path: str | None = None) -> bool:
    target = path or autostart_path()
    if not os.path.exists(target):
        return False
    with open(target, "r", encoding="utf-8") as handle:
        for line in handle:
            if line.strip() == "Hidden=true":
                return False
            if line.strip() == "X-GNOME-Autostart-enabled=false":
                return False
    return True
