"""Folder for AI: a measured audio/video length note in index.md (Linux only)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess

CLIP = "clip.mp4"
INDEX = "index.md"
_HAS_AUDIO = "clip.mp4 has the system audio (the sound that the PC played)."
_ALREADY = ("The system audio is ", "Audio and video are the same length ")


def _ffprobe(ffmpeg: str) -> str | None:
    directory = os.path.dirname(ffmpeg)
    name = "ffprobe.exe" if os.path.basename(ffmpeg).lower().endswith(".exe") else "ffprobe"
    if directory:
        path = os.path.join(directory, name)
        if os.path.isfile(path):
            return path
    return shutil.which(name)


def lengths(clip: str, ffmpeg: str = "ffmpeg") -> tuple[float, float] | None:
    """Video and audio duration in seconds, or None when clip.mp4 has no audio or times cannot be read."""
    probe = _ffprobe(ffmpeg)
    if probe is None or not os.path.isfile(clip):
        return None
    try:
        raw = subprocess.run(
            [probe, "-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "json", clip],
            capture_output=True, text=True, timeout=30, check=False,
        )
        streams = json.loads(raw.stdout).get("streams") or []
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError, ValueError):
        return None
    video = audio = None
    for stream in streams:
        try:
            duration = float(stream["duration"])
        except (KeyError, TypeError, ValueError):
            continue
        kind = stream.get("codec_type")
        if kind == "video" and video is None:
            video = duration
        elif kind == "audio" and audio is None:
            audio = duration
    if video is None or audio is None:
        return None
    return video, audio


def sentence(video: float, audio: float) -> str:
    gap = abs(video - audio)
    if gap < 0.005:
        return f"Audio and video are the same length ({video:.2f} s)."
    which = "shorter" if audio < video else "longer"
    return (f"The system audio is {gap:.2f} s {which} than the video "
            f"(video {video:.2f} s, audio {audio:.2f} s).")


def add_note(folder: str, zip_path: str | None = None, ffmpeg: str = "ffmpeg") -> str:
    """Insert the sentence into index.md (folder and ZIP). The sentence, or empty."""
    index = os.path.join(folder, INDEX)
    clip = os.path.join(folder, CLIP)
    if not os.path.isfile(index):
        return ""
    with open(index, encoding="utf-8") as handle:
        text = handle.read()
    if any(mark in text for mark in _ALREADY) or _HAS_AUDIO not in text:
        return ""
    pair = lengths(clip, ffmpeg)
    if pair is None:
        return ""
    note = sentence(*pair)
    text = text.replace(_HAS_AUDIO, _HAS_AUDIO + "\n" + note, 1)
    with open(index, "w", encoding="utf-8") as handle:
        handle.write(text)
    if zip_path and os.path.isfile(zip_path):
        from .ocr_index import rewrite_zip
        rewrite_zip(zip_path, {INDEX: text.encode("utf-8")}, set())
    return note
