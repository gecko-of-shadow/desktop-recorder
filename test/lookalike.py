"""Look-alike test for the "Changes only" filter (HANDOFF 6.10).

The app drops frames that do not change with mpdecimate. A change of one look-alike character
(for example "l" to "1") changes few pixels. This script checks which changes the filter keeps.

Method (offline, so each kept frame has a known state):
1. lookalike_render.exe draws one PNG for each state (ClearType text, like a real screen).
2. FFmpeg shows each state for HOLD ms, samples at FPS (like the capture), and runs the same
   filter chain as the app: format=bgra, format=yuv420p, mpdecimate=...:max=ceil(fps)-1.
3. showinfo gives the time of each kept frame. The time gives the state.

A state is "caught" when one or more kept frames show it. A state that is not caught is lost
from the video.

Usage:
  python test/lookalike.py --render-exe %TEMP%\\lookalike_render.exe --out %TEMP%\\lookalike
"""
import argparse
import math
import os
import re
import subprocess
import sys

STATES = ["x.y", "x,y", "O", "0", "l", "1", "I", "rn", "m", "a:b", "a;b", "a'b", "a`b", "-", "_"]

FILTERS = {
    # The app before HANDOFF 6.10: mpdecimate defaults (hi=64*12, lo=64*5, frac=0.33).
    "default": "",
    # The app since HANDOFF 6.10 (the same values as hi=64*4:lo=64*2).
    "app_6_10": "hi=256:lo=128:frac=0.33",
    "hi128_lo64": "hi=64*2:lo=64:frac=0.33",
}


def render(exe, out_dir, width, height, font_px, theme):
    subprocess.run([exe, out_dir, str(width), str(height), str(font_px), theme], check=True,
                   stdout=subprocess.DEVNULL)


def kept_times(state_dir, hold_ms, fps, params, ffmpeg):
    list_path = os.path.join(state_dir, "list_%d.txt" % hold_ms)
    with open(list_path, "w", encoding="ascii", newline="\n") as f:
        for i in range(len(STATES)):
            f.write("file 'state_%02d.png'\nduration %.3f\n" % (i, hold_ms / 1000.0))
        # The concat demuxer needs the last file again, else the last duration is not used.
        f.write("file 'state_%02d.png'\n" % (len(STATES) - 1))
    max_drop = max(1, int(math.ceil(fps)) - 1)
    mp = "mpdecimate=" + (params + ":" if params else "") + "max=%d" % max_drop
    vf = "fps=%g,format=bgra,format=yuv420p,%s,showinfo" % (fps, mp)
    cmd = [ffmpeg, "-hide_banner", "-nostats", "-f", "concat", "-safe", "0", "-i", list_path,
           "-vf", vf, "-fps_mode", "vfr", "-f", "null", "-"]
    p = subprocess.run(cmd, capture_output=True, text=True, cwd=state_dir)
    if p.returncode != 0:
        sys.exit(p.stderr[-2000:])
    return [float(m) for m in re.findall(r"pts_time:([0-9.]+)", p.stderr)]


def score(times, hold_ms):
    total = len(STATES) * hold_ms / 1000.0
    caught = {}
    for t in times:
        if t >= total - 1e-6:
            continue
        i = int(math.floor(t * 1000.0 / hold_ms + 1e-6))
        caught.setdefault(i, t - i * hold_ms / 1000.0)
    lost = [i for i in range(len(STATES)) if i not in caught]
    worst_delay = max(caught.values()) if caught else 0.0
    return caught, lost, worst_delay


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--render-exe", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--fps", type=float, default=10)
    ap.add_argument("--holds", default="1000,500,300,100")
    ap.add_argument("--size", default="1120x892")
    args = ap.parse_args()
    width, height = (int(v) for v in args.size.split("x"))
    holds = [int(h) for h in args.holds.split(",")]
    print("fps %g, region %dx%d, %d states, mpdecimate max=%d"
          % (args.fps, width, height, len(STATES), max(1, int(math.ceil(args.fps)) - 1)))
    print("| Font px | Theme | Hold ms | Filter | Caught | Kept frames | Worst delay s | Lost changes |")
    print("|---|---|---|---|---|---|---|---|")
    for font_px in (13, 16):
        for theme in ("light", "dark"):
            state_dir = os.path.join(args.out, "f%d_%s" % (font_px, theme))
            render(args.render_exe, state_dir, width, height, font_px, theme)
            for hold in holds:
                for name, params in FILTERS.items():
                    times = kept_times(state_dir, hold, args.fps, params, args.ffmpeg)
                    caught, lost, delay = score(times, hold)
                    lost_text = ", ".join("%s>%s" % (STATES[i - 1] if i else "start", STATES[i])
                                          for i in lost) or "-"
                    print("| %d | %s | %d | %s | %d/%d | %d | %.1f | %s |"
                          % (font_px, theme, hold, name, len(caught), len(STATES), len(times),
                             delay, lost_text))


if __name__ == "__main__":
    main()
