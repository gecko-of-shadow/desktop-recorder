"""Run ffmpeg so that it gets exactly one SIGTERM when the app dies.

Usage: python3 guard.py APP_PID ffmpeg [args...]

The parent-death signal comes from the thread that started the child, not from the app as a
whole: Linux sends it when that thread ends (even if the app goes on) and again for every app
thread when the app dies. ffmpeg takes a second SIGTERM as "exit now", which threw away the
last seconds of the file. This process takes those signals instead, passes one SIGTERM on only
when the app is really gone, and exits with ffmpeg's status. Standard library only, no threads.
"""

from __future__ import annotations

import ctypes
import os
import signal
import subprocess
import sys

_PR_SET_PDEATHSIG = 1


def main() -> int:
    if len(sys.argv) < 3:
        sys.stderr.write("usage: guard.py APP_PID PROGRAM [ARGS...]\n")
        return 2
    app = int(sys.argv[1])
    argv = sys.argv[2:]
    prctl = ctypes.CDLL(None, use_errno=True).prctl
    state = {"child": None, "app_gone": False, "sent": False}

    def stop_child() -> None:
        child = state["child"]
        if child is not None and not state["sent"]:
            state["sent"] = True
            try:
                child.send_signal(signal.SIGTERM)
            except OSError:
                pass

    def on_term(_signum, _frame) -> None:
        # When only one app thread ended, Linux hands this process to another thread of the
        # same app, so the parent id stays the same.
        if os.getppid() == app:
            return
        state["app_gone"] = True
        stop_child()

    signal.signal(signal.SIGTERM, on_term)
    # Ctrl+C in a terminal reaches ffmpeg directly; do not also die here.
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    prctl(_PR_SET_PDEATHSIG, int(signal.SIGTERM), 0, 0, 0)
    if os.getppid() != app:
        return 1
    guard = os.getpid()

    def preexec() -> None:
        signal.signal(signal.SIGINT, signal.SIG_DFL)
        # This process has one thread, so ffmpeg goes when it goes (also when the app kills it
        # after the stop timeout).
        prctl(_PR_SET_PDEATHSIG, int(signal.SIGKILL), 0, 0, 0)
        if os.getppid() != guard:
            os._exit(1)

    try:
        state["child"] = subprocess.Popen(argv, preexec_fn=preexec)
    except OSError as exc:
        sys.stderr.write(f"Cannot start {argv[0]}: {exc}\n")
        return 127
    if state["app_gone"]:
        stop_child()
    code = state["child"].wait()
    if code < 0:
        signal.signal(-code, signal.SIG_DFL)
        os.kill(os.getpid(), -code)
        return 128 - code
    return code


if __name__ == "__main__":
    raise SystemExit(main())
