#!/usr/bin/env python3
"""frames.py - count unique frames in a video and read the counter bar code.

Use this script for the capture-rate test (HANDOFF.md 6.2) and to get the
unique frames for the AI read test (HANDOFF.md 6.3).

The script uses only the Python standard library. It runs ffmpeg and ffprobe
as subprocesses. Both must be on PATH.

DUPLICATE RULE
  Exact mode (default, --threshold 0): a frame is a duplicate when its
  decoded RGB pixels are identical to the frame before it. The script
  compares RGB, not only luma, so a change of color only is a real change.
  Threshold mode (--threshold T): a frame is a duplicate when the mean
  absolute difference to the last unique frame is T or less (0-255 scale).
  The script measures the difference on a subsampled grid of green values.
  Note: a lossy encoder can change a repeated frame by a small amount. Then
  exact mode counts too few duplicates. Example (self-test, h264_nvenc,
  default quality, RGB decode): exact mode found 116 unique frames, the
  true number is 40. The repeated frames had a difference of 0.015 or
  less. Real changes had 9.1 or more. Use --threshold 0.1 on lossy video. A small real change
  (one bar code cell, one short word) can give a difference below 1, so do
  not use a large value.
  Limit of threshold mode: the mean is over the full frame. Thus the same
  T is too large for a small change in a large frame, and too small for
  encoder noise in a small frame. Adversarial test, h264_nvenc QP 40,
  T = 0.1: a 1000x400 frame with a bar code of 4 px cells gave 11 unique
  frames (true: 54). A 160x120 frame gave 56 unique frames (true: 54).
  With --counter, the report shows "dup rule disagreements": frames where
  the duplicate flag and the counter value do not agree. For the
  capture-rate result, use the counter values, not the unique frames.

DURATION
  duration = last timestamp - first timestamp + duration of the last frame.
  The script gets the timestamps from ffprobe (packet pts, sorted). The
  duration of the last frame comes from the packet. When the packet has no
  duration, the script uses the median frame interval.
  real fps = unique frames / duration.

BAR CODE FORMAT (version 1). counter.html draws the same format.
  - Colors: black and white only. Black = 1. White = 0.
  - Grid: 33 columns. Cell size C = integer device pixels, the same for
    width and height. A white quiet zone of at least 1 cell surrounds the
    grid.
  - Rows (top to bottom):
      Clock band, 1 cell high: column k is black when k is even.
        It starts and ends with a black cell. The decoder measures the
        edge of each cell from this band.
      Data band, 2 cells high, directly below the clock band:
        columns 0-3   left guard   B W W B
        columns 4-27  24 data bits, Gray code of the value, most
                      significant bit first (column 4 = bit 23)
        column  28    parity: black when the count of 1 bits in the
                      Gray code is odd (even parity over columns 4-28)
        columns 29-32 right guard  B B W B
  - Gray code: g = v XOR (v >> 1), with v = counter mod 2^24.
  - The decoder finds the strip automatically. It scans rows for 33 runs
    of the same width with alternate colors (the clock band). Then it reads
    the data band below the clock band. It accepts a result only when all
    guard cells and the parity cell are correct. Thus the strip can be at
    any position, also inside a frame with browser chrome around the page.

OUTPUT
  A table on the console (ASCII only) and, with --json, a JSON report.
"""

import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import zlib
from fractions import Fraction

COLS = 33
MOD = 1 << 24
LEFT_GUARD = (1, 0, 0, 1)
RIGHT_GUARD = (1, 1, 0, 1)
RUN_RE = re.compile(rb"0+|1+")


# ---------------------------------------------------------------- ffprobe

def run_ffprobe(args):
    cmd = ["ffprobe", "-v", "error"] + args
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if res.returncode != 0:
        raise SystemExit("ERROR: ffprobe failed: " + res.stderr.decode("utf-8", "replace").strip())
    return res.stdout.decode("utf-8", "replace")


def probe_stream(path):
    out = run_ffprobe(["-select_streams", "v:0", "-show_entries",
                       "stream=width,height,r_frame_rate,avg_frame_rate,codec_name,pix_fmt,nb_frames",
                       "-show_entries", "format=duration,format_name",
                       "-of", "json", path])
    data = json.loads(out)
    if not data.get("streams"):
        raise SystemExit("ERROR: the file has no video stream: " + path)
    st = data["streams"][0]
    fmt = data.get("format", {})
    return st, fmt


def parse_rate(text):
    try:
        f = Fraction(text)
        return float(f) if f > 0 else None
    except (ValueError, ZeroDivisionError, TypeError):
        return None


def to_float(text):
    try:
        return float(text)
    except (ValueError, TypeError):
        return None


def probe_timestamps(path):
    """Return a sorted list of (pts, duration) for the packets of the first video stream."""
    out = run_ffprobe(["-select_streams", "v:0", "-show_entries", "packet=pts_time,duration_time",
                       "-of", "csv=p=0", path])
    items = []
    for line in out.splitlines():
        parts = line.strip().split(",")
        if not parts or parts[0] == "":
            continue
        items.append((to_float(parts[0]), to_float(parts[1]) if len(parts) > 1 else None))
    if items and all(p is not None for p, _ in items):
        items.sort(key=lambda x: x[0])
    return items


def probe_frame_timestamps(path):
    """Slow path: decode with ffprobe and read the frame timestamps."""
    out = run_ffprobe(["-select_streams", "v:0", "-show_entries",
                       "frame=pts_time,best_effort_timestamp_time,duration_time",
                       "-of", "csv=p=0", path])
    items = []
    for line in out.splitlines():
        parts = line.strip().split(",")
        if not parts or parts[0] == "" and len(parts) < 2:
            continue
        pts = to_float(parts[0])
        if pts is None and len(parts) > 1:
            pts = to_float(parts[1])
        dur = to_float(parts[2]) if len(parts) > 2 else None
        items.append((pts, dur))
    return items


# ---------------------------------------------------------------- decoding

def iter_frames(path, width, height, pix_fmt):
    """Decode the video with ffmpeg and yield the raw bytes of each frame."""
    bpp = 3 if pix_fmt == "rgb24" else 1
    size = width * height * bpp
    errf = tempfile.TemporaryFile()
    cmd = ["ffmpeg", "-v", "error", "-nostdin", "-i", path, "-map", "0:v:0",
           "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", pix_fmt, "-"]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=errf, bufsize=size * 2)
    try:
        while True:
            buf = proc.stdout.read(size)
            if not buf:
                break
            if len(buf) < size:
                print("WARNING: the last frame is incomplete. The script ignores it.", file=sys.stderr)
                break
            yield buf
    finally:
        proc.stdout.close()
        rc = proc.wait()
        errf.seek(0)
        err = errf.read().decode("utf-8", "replace").strip()
        errf.close()
        if rc != 0:
            raise SystemExit("ERROR: ffmpeg failed (code %d): %s" % (rc, err))


def luma_plane(buf, pix_fmt):
    """Return one byte per pixel. For rgb24 the script uses the green channel."""
    return buf if pix_fmt == "gray" else buf[1::3]


# ---------------------------------------------------------------- PNG

def write_png(path, buf, width, height, pix_fmt):
    color_type, bpp = (2, 3) if pix_fmt == "rgb24" else (0, 1)
    stride = width * bpp
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        raw += buf[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        f.write(chunk(b"IEND", b""))


# ---------------------------------------------------------------- bar code

_TABLES = {}


def bin_table(thr):
    """Translation table: byte < thr -> b'1' (black), else b'0' (white)."""
    t = _TABLES.get(thr)
    if t is None:
        t = bytes((0x31 if i < thr else 0x30) for i in range(256))
        _TABLES[thr] = t
    return t


def gray_to_bin(g):
    v = g
    shift = 1
    while shift < 24:
        v ^= v >> shift
        shift <<= 1
    return v & (MOD - 1)


def find_clocks(row):
    """Find clock band candidates in one row. Yield (cells, thr).

    cells is a list of 33 (start, end) pixel spans. The caller must check
    each candidate with the guard and parity cells."""
    lo, hi = min(row), max(row)
    if hi - lo < 64:
        return
    thr = (lo + hi + 1) // 2
    bits = row.translate(bin_table(thr))
    runs = [(m.start(), m.end(), bits[m.start()] == 0x31) for m in RUN_RE.finditer(bits)]
    n = len(runs)
    for i in range(n - COLS + 1):
        if not runs[i][2] or runs[i][1] - runs[i][0] < 3:
            continue
        widths = [runs[i + k][1] - runs[i + k][0] for k in range(COLS)]
        med = sorted(widths)[COLS // 2]
        lo_w, hi_w = med * 0.75 - 1, med * 1.25 + 1
        if not all(lo_w <= x <= hi_w for x in widths):
            continue
        # Quiet zone: white before and after, or the edge of the frame.
        if i > 0 and (runs[i - 1][1] - runs[i - 1][0]) < 0.5 * med:
            continue
        if i + COLS < n and (runs[i + COLS][1] - runs[i + COLS][0]) < 0.5 * med:
            continue
        yield [(runs[i + k][0], runs[i + k][1]) for k in range(COLS)], thr


def sample_cells(plane, width, y, centers, thr):
    base = y * width
    return [1 if plane[base + x] < thr else 0 for x in centers]


def clock_ok(bits):
    return all(b == (1 if k % 2 == 0 else 0) for k, b in enumerate(bits))


def decode_data(bits):
    """Check guards and parity. Return the value or None."""
    if tuple(bits[0:4]) != LEFT_GUARD or tuple(bits[29:33]) != RIGHT_GUARD:
        return None
    data = bits[4:28]
    if (sum(data) & 1) != bits[28]:
        return None
    g = 0
    for b in data:
        g = (g << 1) | b
    return gray_to_bin(g)


class BarcodeReader:
    """Find and read the bar code. Keep the geometry between frames."""

    def __init__(self, width, height):
        self.width = width
        self.height = height
        self.geom = None  # (centers, data_rows, thr)
        self.searches = 0

    def read(self, plane):
        if self.geom is not None:
            v = self._read_geom(plane, self.geom)
            if v is not None:
                return v
        self.searches += 1
        geom = self._search(plane)
        if geom is None:
            return None
        self.geom = geom
        return self._read_geom(plane, geom)

    def _read_geom(self, plane, geom):
        centers, clock_row, data_rows, thr = geom
        if not clock_ok(sample_cells(plane, self.width, clock_row, centers, thr)):
            return None
        rows = [sample_cells(plane, self.width, y, centers, thr) for y in data_rows]
        bits = [1 if sum(col) * 2 > len(col) else 0 for col in zip(*rows)]
        return decode_data(bits)

    def _search(self, plane):
        w, h = self.width, self.height
        step = max(1, h // 300)
        for y in range(0, h, step):
            row = plane[y * w:(y + 1) * w]
            for cells, thr in find_clocks(row):
                geom = self._try_clock(plane, y, cells, thr)
                if geom is not None:
                    return geom
        return None

    def _try_clock(self, plane, y, cells, thr):
        w, h = self.width, self.height
        centers = [(s + e - 1) // 2 for s, e in cells]
        # Find the top and the bottom of the clock band.
        top = y
        while top > 0 and clock_ok(sample_cells(plane, w, top - 1, centers, thr)):
            top -= 1
        bot = y
        while bot + 1 < h and clock_ok(sample_cells(plane, w, bot + 1, centers, thr)):
            bot += 1
        ch = bot - top + 1           # clock band height = 1 cell
        first = bot + 1              # first row of the data band
        data_rows = [first + int(round(ch * f)) for f in (0.5, 1.0, 1.5)]
        if data_rows[-1] >= h:
            return None
        geom = (centers, (top + bot) // 2, data_rows, thr)
        if self._read_geom(plane, geom) is not None:
            return geom
        return None


# ---------------------------------------------------------------- analysis

def mean_abs_diff(a, b):
    n = len(a)
    if n == 0:
        return 0.0
    return sum(abs(x - y) for x, y in zip(a, b)) / n


def analyse(args):
    path = args.video
    if not os.path.isfile(path):
        raise SystemExit("ERROR: file not found: " + path)
    st, fmt = probe_stream(path)
    width, height = int(st["width"]), int(st["height"])
    r_rate = parse_rate(st.get("r_frame_rate"))
    avg_rate = parse_rate(st.get("avg_frame_rate"))
    nominal = avg_rate or r_rate

    ts = probe_timestamps(path)
    # Always decode RGB. With gray, a change of color only (same luma) gave
    # a false duplicate, and the result changed with --png.
    pix_fmt = "rgb24"
    out_dir = None
    if args.png:
        out_dir = args.out or (os.path.splitext(path)[0] + "_frames")
        os.makedirs(out_dir, exist_ok=True)
        old = [n for n in os.listdir(out_dir) if n.startswith("frame_") and n.endswith(".png")]
        if old:
            print("WARNING: the PNG folder already has %d frame PNG files from an earlier run. "
                  "The script does not delete them. Use a new folder." % len(old), file=sys.stderr)

    # Subsample grid for threshold mode: about 40000 points. The step must
    # not divide the width, so that the grid does not use the same columns
    # in each row.
    plane_len = width * height
    grid_step = max(1, plane_len // 40000)
    while grid_step > 1 and width % grid_step == 0:
        grid_step += 1

    reader = BarcodeReader(width, height) if args.counter else None

    frames = []
    prev_hash = None
    ref_sample = None
    for idx, buf in enumerate(iter_frames(path, width, height, pix_fmt)):
        plane = luma_plane(buf, pix_fmt)
        if args.threshold > 0:
            sample = plane[::grid_step]
            if ref_sample is None:
                dup = False
                diff = None
            else:
                diff = mean_abs_diff(sample, ref_sample)
                dup = diff <= args.threshold
            if not dup:
                ref_sample = sample
        else:
            hsh = hashlib.blake2b(buf, digest_size=16).digest()
            dup = prev_hash is not None and hsh == prev_hash
            prev_hash = hsh
            diff = None
        rec = {"index": idx, "dup": dup}
        if diff is not None:
            rec["diff"] = round(diff, 3)
        if reader is not None:
            rec["value"] = reader.read(plane)
        frames.append(rec)
        if args.png and not dup:
            # Write the PNG now with a temporary name. The final name needs
            # the timestamp, so the script renames the file later.
            tmp = os.path.join(out_dir, "frame_%06d.tmp" % idx)
            write_png(tmp, buf, width, height, pix_fmt)
            rec["_tmp"] = tmp

    total = len(frames)
    if total == 0:
        raise SystemExit("ERROR: ffmpeg decoded no frames.")

    # Timestamps.
    ts_source = "packets"
    if len(ts) != total or any(p is None for p, _ in ts):
        ts = probe_frame_timestamps(path)
        ts_source = "frames"
    if len(ts) != total or any(p is None for p, _ in ts):
        fps = nominal or 1.0
        ts = [(i / fps, 1.0 / fps) for i in range(total)]
        ts_source = "nominal fps (no timestamps in the file)"
        if total > 1:
            print("WARNING: timestamps not available. The script uses the nominal frame rate.",
                  file=sys.stderr)
    t0 = ts[0][0]
    for rec, (p, d) in zip(frames, ts):
        rec["pts"] = round(p, 6)
    intervals = [ts[i + 1][0] - ts[i][0] for i in range(total - 1)]
    med_int = sorted(intervals)[len(intervals) // 2] if intervals else None
    last_dur = ts[-1][1]
    if not last_dur or last_dur <= 0:
        last_dur = med_int if med_int else (1.0 / nominal if nominal else 0.0)
    duration = ts[-1][0] - t0 + last_dur

    if args.png:
        for rec in frames:
            tmp = rec.pop("_tmp", None)
            if tmp:
                ms = int(round(rec["pts"] * 1000))
                name = "frame_%06d_%08d.png" % (rec["index"], ms)
                final = os.path.join(out_dir, name)
                os.replace(tmp, final)
                rec["png"] = name

    unique = sum(1 for r in frames if not r["dup"])
    dups = total - unique
    longest = run = 0
    for r in frames:
        run = run + 1 if r["dup"] else 0
        longest = max(longest, run)

    report = {
        "file": os.path.abspath(path),
        "width": width,
        "height": height,
        "codec": st.get("codec_name"),
        "pix_fmt": st.get("pix_fmt"),
        "r_frame_rate": st.get("r_frame_rate"),
        "avg_frame_rate": st.get("avg_frame_rate"),
        "nominal_fps": round(nominal, 4) if nominal else None,
        "timestamp_source": ts_source,
        "total_frames": total,
        "duration_s": round(duration, 6),
        "unique_frames": unique,
        "duplicate_frames": dups,
        "longest_duplicate_run": longest,
        "real_fps": round(unique / duration, 4) if duration > 0 else None,
        "duplicate_rule": ("exact hash" if args.threshold <= 0 else
                           "mean abs diff <= %g (grid step %d)" % (args.threshold, grid_step)),
        "png_dir": os.path.abspath(out_dir) if out_dir else None,
    }

    if reader is not None:
        report["counter"] = counter_stats(frames, duration, args.expected_fps)
    report["frames"] = frames
    return report


def counter_stats(frames, duration, expected_fps):
    values = [r["value"] for r in frames]
    failed = [r["index"] for r in frames if r["value"] is None]
    good = [v for v in values if v is not None]
    unique_vals = len(set(good))
    repeats = 0
    gaps = {}
    backward = 0
    wraps = 0
    steps = 0
    prev = None
    for v in good:
        if prev is not None:
            if v == prev:
                repeats += 1
            else:
                d = v - prev
                if d < 0 and prev > MOD - 100000 and v < 100000:
                    d += MOD
                    wraps += 1
                if d < 0:
                    backward += 1
                else:
                    steps += 1
                    gaps[d] = gaps.get(d, 0) + 1
        prev = v
    gap_count = sum(c for d, c in gaps.items() if d > 1)
    skipped = sum((d - 1) * c for d, c in gaps.items() if d > 1)
    # The page counts at the display refresh rate. Thus each step is
    # normally more than 1 (for example 6 at 60 Hz and 10 fps). A "long
    # step" is more than 1.5 x the median step. It shows a missed capture.
    all_steps = sorted(d for d, c in gaps.items() for _ in range(c))
    med_step = all_steps[len(all_steps) // 2] if all_steps else 0
    long_steps = sum(c for d, c in gaps.items() if med_step and d > 1.5 * med_step)
    # Frames where the duplicate flag and the counter value do not agree.
    disagree = 0
    for a, b in zip(frames, frames[1:]):
        if a["value"] is not None and b["value"] is not None and b["dup"] != (a["value"] == b["value"]):
            disagree += 1
    res = {
        "median_step": med_step,
        "long_steps": long_steps,
        "dup_rule_disagreements": disagree,
        "decoded_frames": len(good),
        "failed_frames": len(failed),
        "failed_indexes": failed,
        "first_value": good[0] if good else None,
        "last_value": good[-1] if good else None,
        "unique_values": unique_vals,
        "repeats": repeats,
        "steps": steps,
        "steps_with_gap": gap_count,
        "skipped_values": skipped,
        "step_histogram": {str(k): gaps[k] for k in sorted(gaps)},
        "backward_jumps": backward,
        "wraps": wraps,
        "real_capture_rate": round(unique_vals / duration, 4) if duration > 0 else None,
    }
    if expected_fps:
        need = 0.98 * expected_fps * duration
        res["expected_fps"] = expected_fps
        res["expected_values"] = round(expected_fps * duration, 3)
        res["pass_minimum"] = round(need, 3)
        res["result"] = "PASS" if unique_vals >= need - 1e-9 else "FAIL"
    return res


# ---------------------------------------------------------------- output

def print_report(rep):
    rows = [
        ("File", rep["file"]),
        ("Size", "%dx%d %s %s" % (rep["width"], rep["height"], rep["codec"], rep["pix_fmt"])),
        ("Nominal fps", "%s (r=%s avg=%s)" % (rep["nominal_fps"], rep["r_frame_rate"], rep["avg_frame_rate"])),
        ("Timestamps from", rep["timestamp_source"]),
        ("Total frames", rep["total_frames"]),
        ("Duration (s)", "%.3f" % rep["duration_s"]),
        ("Unique frames", rep["unique_frames"]),
        ("Duplicate frames", rep["duplicate_frames"]),
        ("Longest dup run", rep["longest_duplicate_run"]),
        ("Real fps", rep["real_fps"]),
        ("Duplicate rule", rep["duplicate_rule"]),
    ]
    if rep.get("png_dir"):
        rows.append(("PNG folder", rep["png_dir"]))
    c = rep.get("counter")
    if c:
        hist = " ".join("%s:%d" % (k, v) for k, v in c["step_histogram"].items()) or "-"
        rows += [
            ("-- Counter --", ""),
            ("Decoded frames", c["decoded_frames"]),
            ("Failed frames", c["failed_frames"]),
            ("Values", "%s .. %s" % (c["first_value"], c["last_value"])),
            ("Unique values", c["unique_values"]),
            ("Repeats", c["repeats"]),
            ("Steps with gap", "%d (skipped values %d)" % (c["steps_with_gap"], c["skipped_values"])),
            ("Step histogram", hist),
            ("Long steps", "%d (more than 1.5 x median step %d)" % (c["long_steps"], c["median_step"])),
            ("Dup rule disagreements", c["dup_rule_disagreements"]),
            ("Backward jumps", c["backward_jumps"]),
            ("Wraps", c["wraps"]),
            ("Real capture rate", c["real_capture_rate"]),
        ]
        if "result" in c:
            rows.append(("Expected", "%s fps x %.3f s = %s values; need >= %s" % (
                c["expected_fps"], rep["duration_s"], c["expected_values"], c["pass_minimum"])))
            rows.append(("RESULT", c["result"]))
    w = max(len(k) for k, _ in rows)
    for k, v in rows:
        line = "%-*s  %s" % (w, k, v)
        print(line.encode("ascii", "replace").decode("ascii"))


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Count unique frames in a video. Optional: read the counter.html bar code. "
                    "The script uses ffmpeg and ffprobe from PATH.",
        epilog="Example: python frames.py rec.mp4 --counter --expected-fps 10 --json rec.json")
    ap.add_argument("video", help="Video file to examine. A PNG file also works (one frame).")
    ap.add_argument("--out", metavar="DIR",
                    help="Folder for the PNG files. Default: <video name>_frames next to the video.")
    ap.add_argument("--png", action="store_true",
                    help="Write each unique frame as a color PNG file frame_<index>_<ms>.png.")
    ap.add_argument("--counter", action="store_true",
                    help="Read the counter.html bar code in each frame and report the capture rate.")
    ap.add_argument("--threshold", type=float, default=0.0, metavar="T",
                    help="Duplicate limit: mean absolute difference to the last unique frame, "
                         "0-255 scale. Default 0 = exact pixel match. Use 0.1 for lossy video.")
    ap.add_argument("--expected-fps", type=float, metavar="F",
                    help="With --counter: print PASS when unique values >= 98%% of F x duration, "
                         "else FAIL.")
    ap.add_argument("--json", metavar="PATH", help="Write the full report (with each frame) to this JSON file.")
    args = ap.parse_args(argv)
    if args.expected_fps and not args.counter:
        ap.error("--expected-fps needs --counter")

    rep = analyse(args)
    print_report(rep)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(rep, f, indent=1)
    c = rep.get("counter")
    if c and c.get("result") == "FAIL":
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
