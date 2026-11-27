"""Folder for AI, Linux only: the ZIP in parts of at most N frame images (setting frames_per_zip, 0 = off).

Some chat apps take at most 20 files in one message or read only some images of a ZIP (README, "the ZIP has more
than 19 frame images"). Each part has the full index.md (the whole table and text, so each part explains the
whole recording) with one line that says which images it has. clip.mp4 and transcript.srt go into part 1. The
one big ZIP stays next to the parts.
"""

from __future__ import annotations

import os
import re
import zipfile

FRAME = re.compile(r"^frames/(\d+)\.png$")


def part_name(zip_path: str, k: int, n: int) -> str:
    return zip_path[:-4] + f"_part{k}of{n}.zip"


def make_parts(zip_path: str, per_part: int) -> list[str]:
    """The part ZIPs, or [] when the ZIP has per_part frames or fewer (or per_part is 0)."""
    if per_part <= 0:
        return []
    with zipfile.ZipFile(zip_path) as whole:
        entries = {}
        root = ""
        for info in whole.infolist():
            root, _, name = info.filename.partition("/")
            if name:
                entries[name] = (info, whole.read(info))
    number = {name: m.group(1) for name in entries if (m := FRAME.match(name))}
    frames = sorted(number, key=lambda name: int(number[name]))
    if len(frames) <= per_part or "index.md" not in entries:
        return []
    groups = [frames[i:i + per_part] for i in range(0, len(frames), per_part)]
    n = len(groups)
    index = entries["index.md"][1].decode("utf-8")
    paths = []
    for k, group in enumerate(groups, 1):
        first, last = number[group[0]], number[group[-1]]
        names = ", ".join(os.path.basename(part_name(zip_path, j, n)) for j in range(1, n + 1) if j != k)
        line = (f"Part {k} of {n}: this ZIP has the images of frames {first} to {last}"
                + (" and clip.mp4" if k == 1 and "clip.mp4" in entries else "")
                + f". The table and the text in this file cover the whole recording. The other images are in {names}.")
        title, _, rest = index.partition("\n")
        text = title + "\n" + line + "\n" + rest
        keep = set(group) | ({"clip.mp4", "transcript.srt", "note.txt"} if k == 1 else {"note.txt"})
        path = part_name(zip_path, k, n)
        part_root = f"{root}_part{k}of{n}"
        temp = path + ".tmp"
        with zipfile.ZipFile(temp, "w") as part:
            for name, (info, data) in entries.items():
                if name == "index.md":
                    data = text.encode("utf-8")
                elif name not in keep:
                    continue
                copy = zipfile.ZipInfo(part_root + "/" + name, info.date_time)
                copy.compress_type = info.compress_type
                part.writestr(copy, data)
        os.replace(temp, path)
        paths.append(path)
    return paths
