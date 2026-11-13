"""Find the monitor source for the default output.

PipeWire on Ubuntu 24.04 exposes that source through the PulseAudio API
(pipewire-pulse), so ffmpeg's pulse demuxer works for both servers.
"""

from __future__ import annotations

import subprocess


def monitor_from_listing(default_sink: str, sources_text: str) -> str | None:
    sink = (default_sink or "").strip()
    if not sink or sink == "auto_null":
        return None
    monitor = sink + ".monitor"
    for line in sources_text.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[1] == monitor:
            return monitor
    return None


def choose_monitor(default_sink: str, sinks_text: str, sources_text: str) -> str | None:
    name = (default_sink or "").strip()
    if name in ("", "auto_null", "@DEFAULT_SINK@"):
        name = _first_real_sink(sinks_text)
    return monitor_from_listing(name, sources_text)


def probe_pulse(device: str, timeout: float = 2.0) -> bool:
    """True when ffmpeg can open this Pulse/PipeWire source.

    A suspended source that never delivers a buffer would otherwise block the
    whole recording, including the picture. A short probe drops audio instead.
    """
    if not device:
        return False
    try:
        proc = subprocess.Popen(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "pulse",
                "-i",
                device,
                "-t",
                "0.2",
                "-f",
                "null",
                "-",
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except OSError:
        return False
    try:
        return proc.wait(timeout=timeout) == 0
    except subprocess.TimeoutExpired:
        proc.kill()
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass
        return False


def find_monitor() -> str | None:
    try:
        sink = subprocess.run(
            ["pactl", "get-default-sink"],
            check=False,
            capture_output=True,
            text=True,
            timeout=3,
        )
        sources = subprocess.run(
            ["pactl", "list", "short", "sources"],
            check=False,
            capture_output=True,
            text=True,
            timeout=3,
        )
        sinks = subprocess.run(
            ["pactl", "list", "short", "sinks"],
            check=False,
            capture_output=True,
            text=True,
            timeout=3,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if sources.returncode != 0:
        return None
    name = sink.stdout.strip() if sink.returncode == 0 else ""
    sink_text = sinks.stdout if sinks.returncode == 0 else ""
    return choose_monitor(name, sink_text, sources.stdout)


def _first_real_sink(sinks_text: str) -> str:
    for line in sinks_text.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[1] not in ("auto_null", "@DEFAULT_SINK@"):
            return parts[1]
    return ""
