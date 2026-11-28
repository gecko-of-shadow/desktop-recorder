"""Folder for AI, Linux only: the text on each frame (tesseract) in index.md, and secrets blacked out.

Each frame lists only the lines that no earlier frame had, so a scroll adds its new lines and a still page adds
nothing. The images stay the truth: OCR misreads some characters, and index.md says so.

Redaction (best effort): a word that looks like a key or token (patterns below), or the word after "password:",
gets a black box in the PNG and "[redacted]" in the text. When a frame had one, clip.mp4 leaves the ZIP, because
the video is not redacted. OCR can miss a secret (a misread character breaks a pattern), so this lowers the risk;
it does not remove it.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import zipfile
from concurrent.futures import ThreadPoolExecutor
from typing import NamedTuple

SECTION = "## Text on screen (OCR)"
# The Windows index (SettledFrames.AiNote) lists clip.mp4 in the ZIP. After a redaction the clip is dropped.
CLIP_IN_ZIP = "It holds this file, the PNG images in the frames folder, and clip.mp4."
CLIP_NOT_IN_ZIP = ("It holds this file and the PNG images in the frames folder. "
                   "clip.mp4 is not in the ZIP, because the video is not redacted.")
WORKERS = 4
PAD = 4   # px around a redacted word

# Prefixes need only 8 more characters: OCR often splits a long token into words (a space in the middle), and
# then the rest is caught as a continuation. I, l and 1 are one class for OCR ("AIza" was read as "Alza").
SECRET_PATTERNS = [
    ("AWS key", re.compile(r"(AKIA|ASIA)[0-9A-Z]{8,}")),
    ("GitHub token", re.compile(r"(gh[pousr]_|github_pat_)[A-Za-z0-9_]{8,}")),
    ("API key", re.compile(r"sk-[A-Za-z0-9_-]{8,}")),
    ("Slack token", re.compile(r"xox[abprs]-[A-Za-z0-9-]{8,}")),
    ("Google API key", re.compile(r"A[Il1]za[0-9A-Za-z_-]{8,}")),
    ("JWT", re.compile(r"eyJ[A-Za-z0-9_-]{8,}")),
    ("private key", re.compile(r"PRIVATE")),   # with "BEGIN" earlier on the line, see secret_words
]
# "password: x", "token=x", "Bearer x": the value is the word after the key word (or after "=" in the same word).
KEY_WORDS = re.compile(r"^(password|passwd|pwd|passphrase|secret|token|api[_-]?key|apikey|bearer)[:=]?(.*)$", re.I)
# A random-looking run: 20 or more letters, digits, "_" or "-" with upper case, lower case and digits, not only hex
# (a commit id is context, not a secret).
RUN = re.compile(r"[A-Za-z0-9_-]{20,}")
PLAIN_WORD = re.compile(r"^[a-z]{3,}[.,:;]?$")   # ends a continuation; "fj" in a JWT did (27 Nov 2026)
TO_LINE_END = {"JWT"}   # a bearer token is the rest of its line
MAX_CONTINUATION = 6


def _random_run(text: str) -> bool:
    for run in RUN.findall(text):
        if re.fullmatch(r"[0-9a-fA-F]+", run):
            continue
        if re.search(r"[a-z]", run) and re.search(r"[A-Z]", run) and re.search(r"[0-9]", run):
            return True
    return False


class Word(NamedTuple):
    line: tuple[int, int, int]   # block, paragraph, line of tesseract
    left: int
    top: int
    width: int
    height: int
    text: str


def read_words(png: str, tesseract: str, timeout: float = 60) -> list[Word]:
    # One thread each: OpenMP threads in each of the parallel runs made the box slower, not faster (27 Nov 2026).
    env = dict(os.environ, OMP_THREAD_LIMIT="1")
    done = subprocess.run([tesseract, png, "-", "--psm", "3", "tsv"], capture_output=True, text=True, env=env,
                          timeout=timeout, check=False)
    words = []
    for row in done.stdout.splitlines()[1:]:
        cols = row.split("\t")
        if len(cols) < 12 or cols[0] != "5" or not cols[11].strip():
            continue
        b, p, n, _w, left, top, width, height = (int(v) for v in cols[2:10])
        words.append(Word((b, p, n), left, top, width, height, cols[11].strip()))
    return words


def secret_words(words: list[Word]) -> dict[int, str]:
    """Index of each word to black out, and why."""
    hits: dict[int, str] = {}

    def continue_after(i: int, kind: str) -> None:
        # The rest of a token that OCR split: the next words on the line, up to a plain word.
        limit = len(words) if kind in TO_LINE_END else min(len(words), i + 1 + MAX_CONTINUATION)
        for j in range(i + 1, limit):
            text = words[j].text
            if words[j].line != words[i].line:
                break
            if kind not in TO_LINE_END and (PLAIN_WORD.match(text) or (len(text) < 4 and not re.search(r"\d", text))):
                break
            hits.setdefault(j, kind)

    for i, word in enumerate(words):
        for kind, pattern in SECRET_PATTERNS:
            if pattern.search(word.text):
                if kind == "private key" and not any(
                        w.line == word.line and "BEGIN" in w.text for w in words[max(0, i - 3):i]):
                    continue
                hits[i] = kind
                if kind != "private key":
                    continue_after(i, kind)
                break
        if i not in hits and _random_run(word.text):
            hits[i] = "random-looking string"
            continue_after(i, "random-looking string")
        key = KEY_WORDS.match(word.text)
        if key is None:
            continue
        if len(key.group(2)) >= 4:
            hits[i] = "password or token"   # "password=hunter22" in one word
        elif (key.group(0).endswith((":", "=")) or key.group(1).lower() == "bearer") \
                and i + 1 < len(words) and words[i + 1].line == word.line:
            hits[i + 1] = "password or token"
            continue_after(i + 1, "JWT" if key.group(1).lower() == "bearer" else "password or token")
    return hits


def lines_of(words: list[Word], hidden: dict[int, str]) -> list[str]:
    lines: list[str] = []
    current = None
    for i, word in enumerate(words):
        text = "[redacted]" if i in hidden else word.text
        if word.line != current:
            lines.append(text)
            current = word.line
        else:
            lines[-1] += " " + text
    return lines


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


def section(names: list[str], fresh: list[list[str]], redacted: dict[str, list[str]], text: bool = True) -> str:
    out = ["", SECTION if text else "## Redaction", ""]
    if text:
        out[2:2] = ["Machine OCR (tesseract) of each frame. Each frame lists only the lines that no earlier frame had. "
                    "OCR can misread characters: the image is the truth."]
    if redacted:
        kinds = sorted({k for found in redacted.values() for k in found})   # redacted: frame -> kind of each word
        out += [f"Redacted (black boxes): {sum(len(v) for v in redacted.values())} words in "
                f"{', '.join(sorted(redacted))} ({', '.join(kinds)}). clip.mp4 is not in the ZIP, because the video "
                "is not redacted.", ""]
    for name, lines in zip(names, fresh if text else []):
        out.append(f"### {name}")
        if lines:
            out.extend(f"    {line}" for line in lines)
        else:
            out.append("(no new text)")
        out.append("")
    return "\n".join(out)


def black_out(png: str, words: list[Word], ffmpeg: str) -> None:
    boxes = ",".join(f"drawbox=x={max(0, w.left - PAD)}:y={max(0, w.top - PAD)}:w={w.width + 2 * PAD}:"
                     f"h={w.height + 2 * PAD}:color=black:t=fill" for w in words)
    temp = png + ".tmp.png"
    subprocess.run([ffmpeg, "-loglevel", "error", "-y", "-i", png, "-vf", boxes, temp], check=True, timeout=60)
    os.replace(temp, png)


def add_ocr(folder: str, zip_path: str | None, tesseract: str | None = None, redact: bool = True,
            ffmpeg: str = "ffmpeg", text_section: bool = True) -> str:
    """Black out secrets in the frames and append the section to index.md, in the folder and in the ZIP.

    text_section=False: redaction only (index.md gets a note only when something was blacked out)."""
    tesseract = tesseract or shutil.which("tesseract")
    if not tesseract:
        return "no OCR (tesseract is not installed)"
    index = os.path.join(folder, "index.md")
    frames = os.path.join(folder, "frames")
    if not os.path.isfile(index) or not os.path.isdir(frames):
        return "no OCR (no index.md or frames)"
    with open(index, encoding="utf-8") as handle:
        text = handle.read()
    if SECTION in text or "\n## Redaction\n" in text:
        return "OCR was already in index.md"
    names = sorted(n for n in os.listdir(frames) if n.endswith(".png"))
    try:
        with ThreadPoolExecutor(WORKERS) as pool:
            per_frame = list(pool.map(lambda n: read_words(os.path.join(frames, n), tesseract), names))
        hidden = [secret_words(words) if redact else {} for words in per_frame]
        redacted = {}
        for name, words, hits in zip(names, per_frame, hidden):
            if hits:
                black_out(os.path.join(frames, name), [words[i] for i in sorted(hits)], ffmpeg)
                redacted[name] = list(hits.values())
    except (OSError, subprocess.SubprocessError) as exc:
        return "no OCR: " + str(exc)
    fresh = new_lines([lines_of(words, hits) for words, hits in zip(per_frame, hidden)])
    if not text_section and not redacted:
        return "no secrets found"
    text = text.rstrip("\n") + "\n" + section(names, fresh, redacted, text_section)
    if redacted:
        text = text.replace(CLIP_IN_ZIP, CLIP_NOT_IN_ZIP, 1)
    with open(index, "w", encoding="utf-8") as handle:
        handle.write(text)
    if zip_path and os.path.isfile(zip_path):
        replace = {"index.md": text.encode("utf-8")}
        for name in redacted:
            with open(os.path.join(frames, name), "rb") as handle:
                replace["frames/" + name] = handle.read()
        rewrite_zip(zip_path, replace, {"clip.mp4"} if redacted else set())
    result = f"OCR: {sum(len(f) for f in fresh)} lines" if text_section else "no OCR text"
    if redacted:
        frames_text = "1 frame" if len(redacted) == 1 else f"{len(redacted)} frames"
        result += f"; redacted {sum(len(h) for h in hidden)} words in {frames_text}, no clip.mp4 in the ZIP"
    return result


def rewrite_zip(zip_path: str, replace: dict[str, bytes], drop: set[str]) -> None:
    """Write the ZIP again. Names are relative to its one root folder; other entries are copied as they are."""
    temp = zip_path + ".tmp"
    with zipfile.ZipFile(zip_path) as old, zipfile.ZipFile(temp, "w") as new:
        for info in old.infolist():
            name = info.filename.split("/", 1)[1] if "/" in info.filename else info.filename
            if name in drop:
                continue
            data = replace[name] if name in replace else old.read(info)
            new.writestr(info, data, compress_type=info.compress_type)
    os.replace(temp, zip_path)
