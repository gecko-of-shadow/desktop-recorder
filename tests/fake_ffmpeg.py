#!/usr/bin/env python3
"""Stand-in for ffmpeg used by the recorder tests.

The last argument is the output path. --fail prints an error and exits at once.
--slow waits before treating "q" as quit, so a stop and a start can overlap.
"""
import sys
import time


def main() -> int:
    args = sys.argv[1:]
    if not args:
        sys.stderr.write("missing output\n")
        return 1
    output = args[-1].replace("%03d", "000").replace("%d", "0")
    flags = set(args[:-1])
    with open(output, "wb") as handle:
        handle.write(b"x")
    if "--fail" in flags:
        sys.stderr.write("boom: bad input\n")
        sys.stderr.flush()
        return 1
    sys.stderr.write("started\n")
    sys.stderr.flush()
    while True:
        piece = sys.stdin.read(1)
        if piece == "" or piece == "q":
            break
    if "--slow" in flags:
        time.sleep(0.8)
    with open(output, "ab") as handle:
        handle.write(b"ok")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except BrokenPipeError:
        raise SystemExit(0)
