"""ffmpeg argument lists for X11 and for raw frames coming from PipeWire."""

from __future__ import annotations

import math
import os
from datetime import datetime

from .settings import Settings, clamp_fps, clamp_quality, clamp_split

CRF = (32, 27, 21)
GOP_SECONDS = 10


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


def _encoder(settings: Settings, when: datetime, audio: bool, synced: bool = False) -> list[str]:
    """synced: both inputs carry wall-clock times shifted to start at `when` (X11 with audio)."""
    fps = clamp_fps(settings.fps)
    quality = clamp_quality(settings.quality)
    split = clamp_split(settings.split_minutes)
    # A keyframe each 10 s: a tool that reads only the keyframes of the MP4 then sees each part of a
    # scroll (1080p scroll test, 27 Nov 2026: 94 of 220 lines with 30 s, 214 with 10 s).
    gop = max(2, int(round(fps * GOP_SECONDS)))
    lookahead = max(0, min(40, int(round(fps * 2))))
    # One frame thread when the two clocks must stay together: extra frame threads
    # hold video (and then audio) back, and "q" drops that audio.
    # Otherwise at most 4: ceil(fps) at 10 fps took 542 MB at 1080p, 4 threads 403 MB, with no
    # lost frame and a smaller file (27 Nov 2026).
    threads = 1 if synced else max(1, min(4, int(math.ceil(fps))))
    stamp = stamp_of(when)
    crop = "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0"
    if synced:
        # The MP4 starts every track at 0, so both must begin at `when`: repeat the first
        # frame and pad silence up to the first real sample.
        args = ["-vf", f"fps={format_fps(fps)}:start_time=0," + crop]
    else:
        args = ["-vf", crop]
    args += ["-c:v", "libx264", "-preset", "medium"]
    if synced:
        # No lookahead, B-frames, or extra frame threads: a delaying encoder holds video back,
        # ffmpeg then holds audio back to match, and "q" drops that audio. zerolatency also
        # does this (via sliced threads) but the files were much larger.
        args += [
            "-tune",
            "stillimage",
            "-crf",
            str(CRF[quality]),
            "-x264-params",
            "rc-lookahead=0:sync-lookahead=0:bframes=0:threads=1",
        ]
    else:
        args += ["-tune", "stillimage", "-crf", str(CRF[quality]), "-x264-params", f"rc-lookahead={lookahead}"]
    args += [
        "-threads",
        str(threads),
        "-g",
        str(gop),
        "-pix_fmt",
        "yuv444p" if settings.chroma == "yuv444" else "yuv420p",
    ]
    if audio:
        if synced:
            args += ["-af", "aresample=async=1:first_pts=0"]
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


def even_region(region: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    """x, y, width, height with an even width and height (libx264 4:4:4 needs no more, 4:2:0 clips need it)."""
    x, y, w, h = (int(v) for v in region)
    return max(0, x), max(0, y), max(2, w - w % 2), max(2, h - h % 2)


def x11_input(display: str | None, region: tuple[int, int, int, int]) -> list[str]:
    """The x11grab input: the whole screen, or the region (x, y, width, height; width 0 = whole screen)."""
    if region[2] > 0 and region[3] > 0:
        x, y, w, h = even_region(region)
        return ["-video_size", f"{w}x{h}", "-i", f"{x11_target(display)}+{x},{y}"]
    return ["-i", x11_target(display)]


def x11_argv(settings: Settings, when: datetime, display: str | None, audio_device: str | None, ffmpeg: str = "ffmpeg") -> list[str]:
    fps = format_fps(settings.fps)
    # x11grab and pulse both stamp packets with the wall clock, but by default ffmpeg starts
    # each input at 0 on its own. pulse opens later, so the sound came out about 1-3 s early.
    # Keep the real times (-copyts) and shift both inputs by the same start instant.
    shift = ["-itsoffset", f"-{when.timestamp():.6f}"] if audio_device else []
    args = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostats",
        "-y",
    ]
    if audio_device:
        # Open pulse first: ffmpeg opens inputs one by one and x11grab takes seconds to probe
        # at low fps, so sound from that time was never captured. Video stays track 0.
        # 50 ms packets (48 kHz stereo s16). Older ffmpeg (4.4) otherwise reads ~2 s at a time,
        # and those blocks do not carry exact times.
        args += ["-copyts", "-thread_queue_size", "1024", *shift, "-f", "pulse", "-fragment_size", "9600", "-i", audio_device]
    args += [
        "-thread_queue_size",
        "1024",
        *shift,
        "-f",
        "x11grab",
        "-framerate",
        fps,
        "-draw_mouse",
        "1" if settings.show_pointer else "0",
        *x11_input(display, settings.region),
    ]
    if audio_device:
        args += ["-map", "1:v", "-map", "0:a"]
    args += _encoder(settings, when, bool(audio_device), synced=bool(audio_device))
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
