#!/usr/bin/env python3
"""frames_selftest.py - self-test for frames.py.

The script makes synthetic videos with known numbers, runs frames.py on
them, and compares the results with the known numbers.

Videos:
  1. testsrc2 at 4 fps, converted to 12 fps (each picture 3 times).
     Encoded with ffv1 (lossless), h264_nvenc lossless and h264_nvenc lossy.
  2. A variable frame rate (VFR) file with known timestamps.
  3. Bar code videos. A Python generator draws the bar code format of
     counter.html into raw frames, with known skips, repeats and one
     backward jump. One video has only the page. One video has a fake
     browser frame around the page.
  4. Optional (--browser): screenshots of counter.html?freeze=N from a
     headless Chromium browser (Edge or Chrome), at 3 scale factors.

The script uses only the standard library, ffmpeg and ffprobe.
It writes all files in the work folder. It does not delete files.
"""

import argparse
import glob
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FRAMES_PY = os.path.join(HERE, "frames.py")
COUNTER_HTML = os.path.join(HERE, "counter.html")

RESULTS = []


def check(name, cond, detail=""):
    RESULTS.append((name, bool(cond), detail))
    print("%s  %s  %s" % ("PASS" if cond else "FAIL", name, detail))


def ffmpeg(args, stdin=None):
    cmd = ["ffmpeg", "-v", "error", "-y"] + args
    res = subprocess.run(cmd, input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if res.returncode != 0:
        raise RuntimeError("ffmpeg failed: %s\n%s" % (" ".join(cmd), res.stderr.decode("utf-8", "replace")))


def run_frames(video, json_path, *extra):
    cmd = [sys.executable, FRAMES_PY, video, "--json", json_path] + list(extra)
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    out = res.stdout.decode("utf-8", "replace")
    if not os.path.isfile(json_path):
        raise RuntimeError("frames.py failed:\n" + out + res.stderr.decode("utf-8", "replace"))
    with open(json_path, encoding="utf-8") as f:
        return json.load(f), res.returncode, out


# ------------------------------------------------------------ bar code generator
# This code draws the format of counter.html. It is written separately from
# the decoder in frames.py.

LEFT = [1, 0, 0, 1]
RIGHT = [1, 1, 0, 1]


def barcode_cells(value):
    v = value % (1 << 24)
    g = v ^ (v >> 1)
    bits = [(g >> b) & 1 for b in range(23, -1, -1)]
    return LEFT + bits + [sum(bits) % 2] + RIGHT


def draw_rect(buf, width, x, y, w, h, color):
    row = bytes([color]) * w
    for yy in range(y, y + h):
        base = yy * width + x
        buf[base:base + w] = row


def draw_strip(buf, width, x0, y0, c, value):
    """Draw the clock band and the data band. (x0, y0) = top-left of the grid."""
    cells = barcode_cells(value)
    for k in range(33):
        x = x0 + k * c
        if k % 2 == 0:
            draw_rect(buf, width, x, y0, c, c, 0)
        if cells[k]:
            draw_rect(buf, width, x, y0 + c, c, 2 * c, 0)


def page_strip_geometry(page_w):
    """Same rule as counter.html: canvas = page width x (page width / 7)."""
    canvas_h = page_w // 7
    c = min(page_w // 35, canvas_h // 5)
    x0 = (page_w - 33 * c) // 2
    y0 = (canvas_h - 3 * c) // 2
    return c, x0, y0


def make_sequence():
    """Known values: steps of 6, some repeats, some skips, one backward jump."""
    seq = []
    v = 1000
    for i in range(300):
        if i == 250:
            v = 5                      # page reload: backward jump
        elif i % 37 == 5:
            pass                       # repeat: same value again
        elif i % 53 == 7:
            v += 18                    # skip
        else:
            v += 6
        seq.append(v)
    return seq


def expected_counter(seq):
    hist = {}
    repeats = backward = 0
    for a, b in zip(seq, seq[1:]):
        if a == b:
            repeats += 1
        elif b < a:
            backward += 1
        else:
            hist[str(b - a)] = hist.get(str(b - a), 0) + 1
    return {"unique_values": len(set(seq)), "repeats": repeats,
            "backward_jumps": backward, "step_histogram": hist}


def lcg(seed):
    x = seed
    while True:
        x = (1103515245 * x + 12345) & 0x7FFFFFFF
        yield x


def gen_barcode_video(path, seq, chrome, codec_args, fps=30):
    if chrome:
        W, H = 1600, 1000
        px, py, pw, ph = 37, 91, 1520, 880
    else:
        W, H = 1280, 720
        px, py, pw, ph = 0, 0, 1280, 720
    c, sx, sy = page_strip_geometry(pw)
    rnd = lcg(12345)
    base = bytearray(b"\xff" * (W * H))
    if chrome:
        base[:] = bytes([45]) * (W * H)
        # Fake tabs and toolbar: blocks of fixed pseudo-random gray levels.
        for _ in range(120):
            x = next(rnd) % (W - 60)
            y = next(rnd) % (py - 20)
            draw_rect(base, W, x, y, 10 + next(rnd) % 50, 4 + next(rnd) % 16, next(rnd) % 256)
        draw_rect(base, W, px, py, pw, ph, 255)
    frames = bytearray()
    raw = []
    for i, v in enumerate(seq):
        buf = bytearray(base)
        draw_strip(buf, W, px + sx, py + sy, c, v)
        # Large "digits" below the strip: black blocks that depend on the value.
        ty = py + pw // 7 + 40
        for d, ch in enumerate(str(v)):
            n = int(ch)
            for b in range(4):
                if (n >> b) & 1:
                    draw_rect(buf, W, px + 60 + d * 110 + (b % 2) * 45, ty + (b // 2) * 90, 40, 80, 0)
        raw.append(bytes(buf))
    data = b"".join(raw)
    ffmpeg(["-f", "rawvideo", "-pix_fmt", "gray", "-s", "%dx%d" % (W, H), "-r", str(fps), "-i", "-"]
           + codec_args + [path], stdin=data)
    return c


# ------------------------------------------------------------ tests

def test_dup(work):
    src = ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=4", "-vf", "fps=12", "-t", "10"]
    exp = {"total_frames": 120, "unique_frames": 40, "duplicate_frames": 80,
           "longest_duplicate_run": 2}
    variants = [
        ("dup_ffv1.mkv", ["-c:v", "ffv1"], []),
        ("dup_nvenc_lossless.mp4", ["-c:v", "h264_nvenc", "-tune", "lossless"], []),
        ("dup_nvenc_lossy.mp4", ["-c:v", "h264_nvenc", "-pix_fmt", "yuv420p"], ["--threshold", "0.1"]),
        ("dup_nvenc_frag.mp4", ["-c:v", "h264_nvenc", "-pix_fmt", "yuv420p",
                                "-movflags", "+frag_keyframe+empty_moov+default_base_moof"],
         ["--threshold", "0.1"]),
    ]
    for name, codec, extra in variants:
        path = os.path.join(work, name)
        try:
            ffmpeg(src + codec + [path])
        except RuntimeError as e:
            check(name + " encode", False, str(e).splitlines()[0])
            continue
        rep, rc, _ = run_frames(path, path + ".json", *extra)
        for k, v in exp.items():
            check("%s %s" % (name, k), rep[k] == v, "got %s, expected %s" % (rep[k], v))
        check(name + " duration", abs(rep["duration_s"] - 10.0) < 1e-3, "got %s" % rep["duration_s"])
        check(name + " real_fps", abs(rep["real_fps"] - 4.0) < 1e-3, "got %s" % rep["real_fps"])
        if name.endswith("lossy.mp4"):
            rep0, _, _ = run_frames(path, path + ".exact.json")
            print("INFO  %s exact mode (no threshold): unique %d, duplicate %d" %
                  (name, rep0["unique_frames"], rep0["duplicate_frames"]))

    # PNG output on the lossless file.
    path = os.path.join(work, "dup_ffv1.mkv")
    out = os.path.join(work, "dup_ffv1_png")
    os.makedirs(out, exist_ok=True)
    before = set(os.listdir(out))
    rep, rc, _ = run_frames(path, path + ".png.json", "--png", "--out", out)
    pngs = sorted(p for p in os.listdir(out) if p.endswith(".png"))
    check("png count", len(pngs) == 40, "got %d" % len(pngs))
    check("png name", "frame_000003_00000250.png" in pngs,
          "first names %s" % pngs[:3])
    with open(os.path.join(out, pngs[1]), "rb") as f:
        head = f.read(26)
    check("png header rgb", head[:8] == b"\x89PNG\r\n\x1a\n" and head[25] == 2, "color type %d" % head[25])
    # Read back one PNG with ffmpeg and compare with the decoded frame.
    ref = os.path.join(work, "dup_ffv1_frame3.rgb")
    ffmpeg(["-i", path, "-vf", "select=eq(n\\,3)", "-fps_mode", "passthrough", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "rgb24", ref])
    back = os.path.join(work, "dup_ffv1_png3.rgb")
    ffmpeg(["-i", os.path.join(out, "frame_000003_00000250.png"), "-f", "rawvideo", "-pix_fmt", "rgb24", back])
    check("png pixels", open(ref, "rb").read() == open(back, "rb").read())


def test_vfr(work):
    path = os.path.join(work, "vfr_nvenc.mp4")
    ffmpeg(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=10", "-t", "10",
            "-vf", "select=lt(mod(n\\,10)\\,3)", "-fps_mode", "vfr",
            "-c:v", "h264_nvenc", "-pix_fmt", "yuv420p", path])
    rep, rc, _ = run_frames(path, path + ".json")
    check("vfr total_frames", rep["total_frames"] == 30, "got %s" % rep["total_frames"])
    check("vfr unique_frames", rep["unique_frames"] == 30, "got %s" % rep["unique_frames"])
    exp_pts = [round(s + j * 0.1, 3) for s in range(10) for j in range(3)]
    got_pts = [round(f["pts"], 3) for f in rep["frames"]]
    check("vfr timestamps", got_pts == exp_pts, "first got %s" % got_pts[:7])
    check("vfr duration", 9.25 <= rep["duration_s"] <= 10.05, "got %s (last pts 9.2 + last frame)" % rep["duration_s"])
    print("INFO  vfr nominal fps %s, real fps %s" % (rep["nominal_fps"], rep["real_fps"]))


def test_barcode(work):
    seq = make_sequence()
    exp = expected_counter(seq)
    variants = [
        ("bar_page_nvenc.mp4", False, ["-c:v", "h264_nvenc", "-pix_fmt", "yuv420p"]),
        ("bar_chrome_nvenc.mp4", True, ["-c:v", "h264_nvenc", "-pix_fmt", "yuv420p"]),
        ("bar_chrome_nvenc_cq45.mp4", True, ["-c:v", "h264_nvenc", "-pix_fmt", "yuv420p",
                                              "-rc", "vbr", "-cq", "45", "-b:v", "0"]),
        ("bar_page_ffv1.mkv", False, ["-c:v", "ffv1"]),
    ]
    duration = 10.0
    for name, chrome, codec in variants:
        path = os.path.join(work, name)
        c = gen_barcode_video(path, seq, chrome, codec)
        # Expected fps that gives PASS, and one that gives FAIL.
        u = exp["unique_values"]
        f_pass = u / (0.98 * duration) - 0.5
        f_fail = u / (0.98 * duration) + 0.5
        rep, rc, _ = run_frames(path, path + ".json", "--counter", "--expected-fps", "%.3f" % f_pass)
        cs = rep["counter"]
        check("%s failed_frames (cell %d px)" % (name, c), cs["failed_frames"] == 0, "got %d" % cs["failed_frames"])
        values = [f["value"] for f in rep["frames"]]
        check(name + " values", values == seq,
              "first mismatch at %s" % next((i for i, (a, b) in enumerate(zip(values, seq)) if a != b), None))
        for k, v in exp.items():
            check("%s %s" % (name, k), cs[k] == v, "got %s, expected %s" % (cs[k], v))
        check(name + " real_capture_rate", abs(cs["real_capture_rate"] - u / duration) < 1e-3,
              "got %s" % cs["real_capture_rate"])
        check(name + " PASS at %.3f fps" % f_pass, cs["result"] == "PASS" and rc == 0, cs["result"])
        rep2, rc2, _ = run_frames(path, path + ".fail.json", "--counter", "--expected-fps", "%.3f" % f_fail)
        check(name + " FAIL at %.3f fps" % f_fail, rep2["counter"]["result"] == "FAIL" and rc2 == 1,
              rep2["counter"]["result"])
        if name == "bar_page_nvenc.mp4":
            rep3, _, _ = run_frames(path, path + ".thr.json", "--threshold", "0.1")
            check(name + " threshold 0.1 duplicate_frames", rep3["duplicate_frames"] == exp["repeats"],
                  "got %s, expected %s" % (rep3["duplicate_frames"], exp["repeats"]))
        if name.endswith(".mkv"):
            reps = exp["repeats"]
            check(name + " duplicate_frames = repeats", rep["duplicate_frames"] == reps,
                  "got %s, expected %s" % (rep["duplicate_frames"], reps))


def find_browser(arg):
    if arg and arg != "auto":
        return arg
    cands = [
        os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
    ]
    for c in cands:
        if os.path.isfile(c):
            return c
    return shutil.which("msedge") or shutil.which("chrome")


def test_browser(work, browser):
    exe = find_browser(browser)
    if not exe:
        check("browser found", False, "no Edge or Chrome found")
        return
    url = pathlib.Path(COUNTER_HTML).as_uri()
    for scale, value in (("1", 123456), ("1.25", 8388607), ("1.5", 16777215)):
        png = os.path.join(work, "counter_shot_%s.png" % scale.replace(".", "_"))
        profile = os.path.join(work, "browser_profile")
        cmd = [exe, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
               "--user-data-dir=" + profile, "--window-size=1280,800",
               "--force-device-scale-factor=" + scale, "--virtual-time-budget=3000",
               "--screenshot=" + png, url + "?freeze=%d" % value]
        subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
        if not os.path.isfile(png):
            check("screenshot scale %s" % scale, False, "no PNG written")
            continue
        rep, rc, _ = run_frames(png, png + ".json", "--counter")
        got = rep["frames"][0].get("value")
        check("counter.html scale %s (%dx%d) value" % (scale, rep["width"], rep["height"]), got == value,
              "got %s, expected %s" % (got, value))


def test_browser_live(work, exe):
    """Screenshot without freeze: the live rAF path must give a valid value."""
    url = pathlib.Path(COUNTER_HTML).as_uri()
    png = os.path.join(work, "counter_shot_live.png")
    cmd = [exe, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
           "--user-data-dir=" + os.path.join(work, "browser_profile"), "--window-size=1280,800",
           "--virtual-time-budget=2000", "--screenshot=" + png, url + "?start=777"]
    subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
    if not os.path.isfile(png):
        check("screenshot live", False, "no PNG written")
        return
    rep, rc, _ = run_frames(png, png + ".json", "--counter")
    got = rep["frames"][0].get("value")
    check("counter.html live value", got is not None and got >= 777, "got %s (start 777)" % got)


def main():
    ap = argparse.ArgumentParser(description="Self-test for frames.py. It makes synthetic videos in a work "
                                             "folder and checks the numbers that frames.py reports.")
    ap.add_argument("--work", metavar="DIR",
                    help="Work folder. Default: a new folder in %%TEMP%%. The script does not delete it.")
    ap.add_argument("--browser", metavar="EXE", nargs="?", const="auto",
                    help="Also test counter.html with headless screenshots. Give the browser path, "
                         "or no value to find Edge or Chrome.")
    args = ap.parse_args()
    work = args.work or tempfile.mkdtemp(prefix="frames_selftest_")
    os.makedirs(work, exist_ok=True)
    print("Work folder: " + work)
    test_dup(work)
    test_vfr(work)
    test_barcode(work)
    if args.browser:
        test_browser(work, args.browser)
        exe = find_browser(args.browser)
        if exe:
            test_browser_live(work, exe)
    bad = [r for r in RESULTS if not r[1]]
    print("\n%d checks, %d failed" % (len(RESULTS), len(bad)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
