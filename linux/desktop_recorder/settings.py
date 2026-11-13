"""settings.txt, same keys as the Windows build, plus Linux-only lines."""

from __future__ import annotations

import os
from dataclasses import dataclass, field

from .hotkey import text_of, try_set

DEFAULT_FPS = 2.0
MIN_FPS = 0.2
MAX_FPS = 60.0


def clamp_fps(value: float) -> float:
    if value != value or value in (float("inf"), float("-inf")):
        return DEFAULT_FPS
    if value < MIN_FPS:
        return MIN_FPS
    if value > MAX_FPS:
        return MAX_FPS
    return value


def clamp_quality(value: int) -> int:
    if value < 0:
        return 0
    if value > 2:
        return 2
    return value


def clamp_split(value: int) -> int:
    if value < 0:
        return 0
    if value > 1440:
        return 1440
    return value


def config_dir() -> str:
    base = os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(base, "DesktopRecorder")


def settings_path() -> str:
    return os.path.join(config_dir(), "settings.txt")


def default_folder() -> str:
    return os.path.join(os.path.expanduser("~"), "Recordings")


@dataclass
class Settings:
    folder: str = ""
    fps: float = DEFAULT_FPS
    quality: int = 1
    split_minutes: int = 30
    record_on_launch: bool = True
    copy_to_clipboard: bool = True
    play_sounds: bool = True
    hotkey_modifiers: int = 4 | 8
    hotkey_key: str = "D"
    record_audio: bool = True
    start_on_login: bool = False
    wayland_restore_token: str = ""
    extra_lines: list[str] = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.folder:
            self.folder = default_folder()

    @property
    def hotkey_text(self) -> str:
        return text_of(self.hotkey_modifiers, self.hotkey_key)


_KNOWN = {
    "folder",
    "fps",
    "quality",
    "split_minutes",
    "record_on_launch",
    "copy_to_clipboard",
    "play_sounds",
    "hotkey",
    "record_audio",
    "start_on_login",
    "wayland_restore_token",
}


def parse_settings(text: str, defaults: Settings | None = None) -> Settings:
    current = defaults or Settings()
    result = Settings(
        folder=current.folder,
        fps=current.fps,
        quality=current.quality,
        split_minutes=current.split_minutes,
        record_on_launch=current.record_on_launch,
        copy_to_clipboard=current.copy_to_clipboard,
        play_sounds=current.play_sounds,
        hotkey_modifiers=current.hotkey_modifiers,
        hotkey_key=current.hotkey_key,
        record_audio=current.record_audio,
        start_on_login=current.start_on_login,
        wayland_restore_token=current.wayland_restore_token,
    )
    for raw in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        line = raw.strip()
        if not line:
            continue
        if "=" not in line:
            result.extra_lines.append(line)
            continue
        key, val = line.split("=", 1)
        key = key.strip()
        val = val.strip()
        if key not in _KNOWN:
            result.extra_lines.append(key + "=" + val)
            continue
        _apply(result, key, val)
    result.fps = clamp_fps(result.fps)
    result.quality = clamp_quality(result.quality)
    result.split_minutes = clamp_split(result.split_minutes)
    return result


def _apply(result: Settings, key: str, val: str) -> None:
    if key == "folder":
        if val and "\n" not in val and "\r" not in val:
            result.folder = val
        return
    if key == "fps":
        try:
            result.fps = float(val)
        except ValueError:
            pass
        return
    if key == "quality":
        try:
            result.quality = int(val)
        except ValueError:
            pass
        return
    if key == "split_minutes":
        try:
            result.split_minutes = int(val)
        except ValueError:
            pass
        return
    if key == "record_on_launch":
        result.record_on_launch = val == "1"
        return
    if key == "copy_to_clipboard":
        result.copy_to_clipboard = val == "1"
        return
    if key == "play_sounds":
        result.play_sounds = val == "1"
        return
    if key == "record_audio":
        result.record_audio = val == "1"
        return
    if key == "start_on_login":
        result.start_on_login = val == "1"
        return
    if key == "wayland_restore_token":
        result.wayland_restore_token = val
        return
    if key == "hotkey":
        updated = try_set(val, (result.hotkey_modifiers, result.hotkey_key))
        if updated is not None:
            result.hotkey_modifiers, result.hotkey_key = updated


def serialize_settings(settings: Settings) -> str:
    fps = clamp_fps(settings.fps)
    fps_text = f"{fps:.3f}".rstrip("0").rstrip(".")
    lines = [
        "folder=" + settings.folder,
        "fps=" + fps_text,
        "quality=" + str(clamp_quality(settings.quality)),
        "split_minutes=" + str(clamp_split(settings.split_minutes)),
        "record_on_launch=" + ("1" if settings.record_on_launch else "0"),
        "copy_to_clipboard=" + ("1" if settings.copy_to_clipboard else "0"),
        "play_sounds=" + ("1" if settings.play_sounds else "0"),
        "hotkey=" + settings.hotkey_text,
        "record_audio=" + ("1" if settings.record_audio else "0"),
        "start_on_login=" + ("1" if settings.start_on_login else "0"),
        "wayland_restore_token=" + (settings.wayland_restore_token or ""),
    ]
    for extra in settings.extra_lines:
        if extra.strip():
            lines.append(extra.strip())
    return "\n".join(lines) + "\n"


def load_settings(path: str | None = None) -> Settings:
    target = path or settings_path()
    if not os.path.exists(target):
        return Settings()
    with open(target, "r", encoding="utf-8") as handle:
        return parse_settings(handle.read())


def save_settings(settings: Settings, path: str | None = None) -> None:
    target = path or settings_path()
    os.makedirs(os.path.dirname(target), exist_ok=True)
    temporary = target + ".tmp"
    with open(temporary, "w", encoding="utf-8") as handle:
        handle.write(serialize_settings(settings))
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, target)
