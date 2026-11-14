#!/usr/bin/env python3
"""Stand-in for ffmpeg that writes stats lines, used by the recorder core tests.

The last argument is the output path. The script writes stats lines that end
with a lone carriage return, as FFmpeg does, and one error line. After "q" it
writes the final stats line (with dup= and drop=) and exits.
--fail writes an error line after the first stats lines and exits with code 1.
"""
import sys


def main() -> int:
    args = sys.argv[1:]
    if not args:
        sys.stderr.write("missing output\n")
        return 1
    output = args[-1].replace("%03d", "000").replace("%d", "0")
    with open(output, "wb") as handle:
        handle.write(b"12345")
    # dup= and drop= are missing when they are 0, as in the real FFmpeg build.
    sys.stderr.write("frame=    5 fps=0.0 q=23.0 size=       0KiB time=00:00:00.50 bitrate=N/A speed=1x    \r")
    sys.stderr.write("real warning line\n")
    sys.stderr.write("frame=   10 fps= 10 q=23.0 size=       1KiB time=00:00:01.00 bitrate=8.0kbits/s dup=1 speed=1x    \r")
    sys.stderr.flush()
    if "--fail" in args[:-1]:
        sys.stderr.write("boom: capture failed\n")
        sys.stderr.flush()
        return 1
    while True:
        piece = sys.stdin.read(1)
        if piece == "" or piece == "q":
            break
    sys.stderr.write("frame=   42 fps= 10 q=-1.0 Lsize=       2KiB time=00:00:04.20 bitrate=4.0kbits/s dup=3 drop=2 speed=1x    \r\n")
    sys.stderr.flush()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except BrokenPipeError:
        raise SystemExit(0)
