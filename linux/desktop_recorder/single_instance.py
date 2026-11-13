"""One tray process per user. A second start sends a command over a socket."""

from __future__ import annotations

import os
import socket
import threading
from collections.abc import Callable


class InstanceServer:
    def __init__(self, path: str, on_command: Callable[[str], str]) -> None:
        self.path = path
        self._on_command = on_command
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._sock: socket.socket | None = None

    def start(self) -> bool:
        """Return False when another live instance already owns the socket."""
        self._stop.clear()
        directory = os.path.dirname(self.path)
        if directory:
            os.makedirs(directory, exist_ok=True)
            try:
                os.chmod(directory, 0o700)
            except OSError:
                pass
        if _reachable(self.path):
            return False
        if os.path.exists(self.path):
            os.unlink(self.path)
        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        sock.bind(self.path)
        os.chmod(self.path, 0o600)
        sock.listen(2)
        sock.settimeout(0.3)
        self._sock = sock
        self._thread = threading.Thread(target=self._serve, name="desktop-recorder-socket", daemon=True)
        self._thread.start()
        return True

    def stop(self) -> None:
        self._stop.set()
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
        if self._thread is not None:
            self._thread.join(2)
        if os.path.exists(self.path):
            try:
                os.unlink(self.path)
            except OSError:
                pass

    def _serve(self) -> None:
        while not self._stop.is_set():
            try:
                conn, _addr = self._sock.accept()  # type: ignore[union-attr]
            except (socket.timeout, OSError):
                continue
            try:
                data = conn.recv(64).decode("utf-8", errors="replace").strip()
                # A liveness probe connects and closes without sending a command.
                if not data:
                    continue
                reply = self._on_command(data)
                conn.sendall((reply + "\n").encode("utf-8"))
            except Exception:
                pass
            finally:
                conn.close()


def send_command(path: str, command: str, timeout: float = 2.0) -> str | None:
    if not _reachable(path):
        return None
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.settimeout(timeout)
    try:
        sock.connect(path)
        sock.sendall((command.strip() + "\n").encode("utf-8"))
        return sock.recv(128).decode("utf-8", errors="replace").strip()
    except OSError:
        return None
    finally:
        sock.close()


def _reachable(path: str) -> bool:
    if not os.path.exists(path):
        return False
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.settimeout(0.2)
    try:
        sock.connect(path)
        return True
    except OSError:
        return False
    finally:
        sock.close()
