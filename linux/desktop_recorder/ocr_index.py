"""Folder for AI, Linux only: the text on each frame (tesseract) in index.md, as searchable text.

Each frame lists only the lines that no earlier frame had, so a scroll adds its new lines and a still page adds
nothing. The images stay the truth: OCR misreads some characters, and index.md says so.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import zipfile
from concurrent.futures import ThreadPoolExecutor

SECTION = "## Text on screen (OCR)"
WORKERS = 4


def read_text(png: str, tesseract: str, timeout: float = 60) -> list[str]:
    # One thread each: OpenMP threads in each of the parallel runs made the box slower, not faster (27 Nov 2026).
    env = dict(os.environ, OMP_THREAD_LIMIT="1")
    done = subprocess.run([tesseract, png, "-", "--psm", "3"], capture_output=True, text=True, env=env,
                          timeout=timeout, check=False)
    return [" ".join(line.split()) for line in done.stdout.splitlines()]


def _key(line: str) -> str:
    return re.sub(r"[^0-9a-z]", "", line.lower())


def new_lines(per_frame: list[list[str]]) -> list[list[str]]:
    """For each frame, the lines with 3 or more letters or digits that no earlier frame had."""
    seen: set[str] = set()
    result = []
    for lines in per_frame:
        fresh = []
        for line in lines:
            key = _key(line)
            if len(key) < 3 or key in seen:
                continue
            seen.add(key)
            fresh.append(line)
        result.append(fresh)
    return result


def section(names: list[str], fresh: list[list[str]]) -> str:
    out = ["", SECTION,
           "Machine OCR (tesseract) of each frame. Each frame lists only the lines that no earlier frame had. "
           "OCR can misread characters: the image is the truth.", ""]
    for name, lines in zip(names, fresh):
        out.append(f"### {name}")
        if lines:
            out.extend(f"    {line}" for line in lines)
        else:
            out.append("(no new text)")
        out.append("")
    return "\n".join(out)


def add_ocr(folder: str, zip_path: str | None, tesseract: str | None = None) -> str:
    """Append the section to index.md in the folder and in the ZIP. Returns a short result for the tray."""
    tesseract = tesseract or shutil.which("tesseract")
    if not tesseract:
        return "no OCR (tesseract is not installed)"
    index = os.path.join(folder, "index.md")
    frames = os.path.join(folder, "frames")
    if not os.path.isfile(index) or not os.path.isdir(frames):
        return "no OCR (no index.md or frames)"
    names = sorted(n for n in os.listdir(frames) if n.endswith(".png"))
    try:
        with ThreadPoolExecutor(WORKERS) as pool:
            per_frame = list(pool.map(lambda n: read_text(os.path.join(frames, n), tesseract), names))
    except (OSError, subprocess.SubprocessError) as exc:
        return "no OCR: " + str(exc)
    fresh = new_lines(per_frame)
    with open(index, encoding="utf-8") as handle:
        text = handle.read()
    if SECTION in text:
        return "OCR was already in index.md"
    text = text.rstrip("\n") + "\n" + section(names, fresh)
    with open(index, "w", encoding="utf-8") as handle:
        handle.write(text)
    if zip_path and os.path.isfile(zip_path):
        _replace_in_zip(zip_path, "index.md", text)
    return f"OCR: {sum(len(f) for f in fresh)} lines"


def _replace_in_zip(zip_path: str, name: str, text: str) -> None:
    """Write the ZIP again with a new <root>/<name>; the other entries are copied as they are."""
    temp = zip_path + ".tmp"
    with zipfile.ZipFile(zip_path) as old, zipfile.ZipFile(temp, "w") as new:
        for info in old.infolist():
            if info.filename.split("/")[-1] == name and info.filename.count("/") <= 1:
                new.writestr(info, text.encode("utf-8"), compress_type=zipfile.ZIP_DEFLATED)
            else:
                new.writestr(info, old.read(info))
    os.replace(temp, zip_path)
