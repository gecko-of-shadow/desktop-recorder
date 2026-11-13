"""Copy finished recordings as a file URI list, the Linux form of a file drop."""

from __future__ import annotations

import os
import shutil
import subprocess
import urllib.parse


def uri_list(paths: list[str]) -> str:
    lines = ["file://" + urllib.parse.quote(os.path.abspath(path)) for path in paths]
    return "\n".join(lines) + "\n"


def copy_files(paths: list[str]) -> None:
    payload = uri_list(paths).encode("utf-8")
    if os.environ.get("WAYLAND_DISPLAY") and shutil.which("wl-copy"):
        command = ["wl-copy", "-t", "text/uri-list"]
    elif shutil.which("xclip"):
        command = ["xclip", "-selection", "clipboard", "-t", "text/uri-list"]
    elif shutil.which("wl-copy"):
        command = ["wl-copy", "-t", "text/uri-list"]
    else:
        raise RuntimeError("Install xclip or wl-clipboard to copy the video.")
    subprocess.run(command, input=payload, check=True, timeout=5)
