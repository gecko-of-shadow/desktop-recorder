"""ffmpeg argument lists for X11 and for raw frames coming from PipeWire."""

from __future__ import annotations

import math
import os
from datetime import datetime

from .settings import Settings, clamp_fps, clamp_quality, clamp_split

CRF = (32, 27, 21)


def format_fps(fps: float) -> str:
    text = f"{clamp_fps(fps):.3f}".rstrip("0").rstrip(".")
    return text or "0"


def fps_fraction(fps: float) -> tuple[int, int]:
    tenths = int(round(clamp_fps(fps) * 10))
    if tenths < 1:
        tenths = 1
    divisor = math.gcd(tenths, 10)
    return tenths // divisor, 10 // divisor


def stamp_of(when: datetime) -> str:
    return when.strftime("%Y-%m-%d_%H-%M-%S_") + f"{when.microsecond // 1000:03d}"


def x11_target(display: str | None) -> str:
    text = (display or ":0").strip()
    if text.startswith("unix:"):
        text = text[5:]
    _head, sep, tail = text.rpartition(":")
    if sep and tail and "." not in tail:
        return text + ".0"
    return text


def capture_backend() -> str:
    forced = os.environ.get("DESKTOP_RECORDER_CAPTURE", "").strip().lower()
    if forced in ("x11", "wayland"):
        return forced
    session = os.environ.get("XDG_SESSION_TYPE", "").strip().lower()
    if os.environ.get("WAYLAND_DISPLAY") or session == "wayland":
        return "wayland"
    return "x11"


def _encoder(settings: Settings, when: datetime, audio: bool) -> list[str]:
    fps = clamp_fps(settings.fps)
    quality = clamp_quality(settings.quality)
    split = clamp_split(settings.split_minutes)
    gop = max(2, int(round(fps * 30)))
    lookahead = max(0, min(40, int(round(fps * 2))))
    threads = max(1, min(16, int(math.ceil(fps))))
    stamp = stamp_of(when)
    args = [
        "-vf",
        "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0",
        "-c:v",
        "libx264",
        "-preset",
        "medium",
        "-tune",
        "stillimage",
        "-crf",
        str(CRF[quality]),
        "-x264-params",
        f"rc-lookahead={lookahead}",
        "-threads",
        str(threads),
        "-g",
        str(gop),
        "-pix_fmt",
        "yuv420p",
    ]
    if audio:
        args += ["-c:a", "aac", "-b:a", "128k"]
    if split > 0:
        seconds = split * 60
        folder = settings.folder.replace("%", "%%")
        args += [
            "-force_key_frames",
            f"expr:gte(t,n_forced*{seconds})",
            "-f",
            "segment",
            "-segment_time",
            str(seconds),
            "-segment_format",
            "mp4",
            "-segment_format_options",
            "movflags=+frag_keyframe+empty_moov:frag_duration=2000000:flush_packets=1",
            "-reset_timestamps",
            "1",
            os.path.join(folder, f"desktop_{stamp}_%03d.mp4"),
        ]
    else:
        args += [
            "-movflags",
            "+frag_keyframe+empty_moov",
            "-frag_duration",
            "2000000",
            "-flush_packets",
            "1",
            os.path.join(settings.folder, f"desktop_{stamp}.mp4"),
        ]
    return args


def x11_argv(settings: Settings, when: datetime, display: str | None, audio_device: str | None, ffmpeg: str = "ffmpeg") -> list[str]:
    fps = format_fps(settings.fps)
    args = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostats",
        "-y",
        "-thread_queue_size",
        "1024",
        "-f",
        "x11grab",
        "-framerate",
        fps,
        "-draw_mouse",
        "1",
        "-i",
        x11_target(display),
    ]
    if audio_device:
        args += ["-thread_queue_size", "1024", "-f", "pulse", "-i", audio_device]
    args += _encoder(settings, when, bool(audio_device))
    return args


def wayland_ffmpeg_argv(
    settings: Settings,
    when: datetime,
    width: int,
    height: int,
    audio_device: str | None,
    ffmpeg: str = "ffmpeg",
) -> list[str]:
    """Encode BGRA frames on stdin. Odd sizes are legal for BGRA; the crop makes them even."""
    args = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostats",
        "-y",
        "-f",
        "rawvideo",
        "-pixel_format",
        "bgra",
        "-video_size",
        f"{width}x{height}",
        "-framerate",
        format_fps(settings.fps),
        "-i",
        "pipe:0",
    ]
    if audio_device:
        args += ["-thread_queue_size", "1024", "-f", "pulse", "-i", audio_device, "-shortest"]
    args += _encoder(settings, when, bool(audio_device))
    return args


def wayland_pipeline(fd: int, node_id: int, serial: str | None, fps: float) -> str:
    rate_n, rate_d = fps_fraction(fps)
    source = f"pipewiresrc fd={int(fd)} path={int(node_id)} do-timestamp=true"
    if serial and all(ch.isalnum() or ch in "-_" for ch in serial):
        source += f" target-object={serial}"
    return (
        source
        + " ! videoconvert ! videorate ! "
        + f"video/x-raw,format=BGRA,framerate={rate_n}/{rate_d} ! "
        + "appsink name=sink emit-signals=true sync=false max-buffers=2 drop=true"
    )
