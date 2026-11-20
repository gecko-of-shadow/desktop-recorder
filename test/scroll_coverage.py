"""scroll_coverage.py - independent check of the scroll-distance rule (AI capture Phase 1, condition B).

Input: a video and the times of the written frames (from process_clip.exe output).
Method (different from SettledFrames.cs): full search of the vertical shift between each pair of
frames, with the mean gray difference over the moving part of the picture (static rows at the top
and bottom are found and left out). Output: the rows of scrolled content that no written frame shows.

Usage: python -I test/scroll_coverage.py VIDEO TIMES_FILE
TIMES_FILE: process_clip output (lines "frame N t=..."), or one time per line.
"""
import json, re, subprocess, sys
import numpy as np

def main():
    video, times_file = sys.argv[1], sys.argv[2]
    info = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                           'stream=width,height', '-of', 'csv=p=0', video], capture_output=True, text=True).stdout
    w, h = [int(v) for v in info.strip().split(',')[:2]]
    pts = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'packet=pts_time',
                          '-of', 'csv=p=0', video], capture_output=True, text=True).stdout
    times = sorted(float(x.strip().strip(',')) for x in pts.split() if x.strip().strip(','))
    raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', video, '-map', '0:v:0', '-fps_mode', 'passthrough',
                          '-f', 'rawvideo', '-pix_fmt', 'gray', 'pipe:1'], capture_output=True).stdout
    n = len(raw) // (w * h)
    frames = np.frombuffer(raw[:n * w * h], dtype=np.uint8).reshape(n, h, w)[:, :, ::2].astype(np.int16)
    times = times[:n]
    # Static rows: rows that never change much between two frames.
    diffs = np.abs(np.diff(frames, axis=0)).mean(axis=2)          # (n-1, h)
    moving = diffs.max(axis=0) > 3
    rows = np.where(moving)[0]
    top, bottom = (int(rows.min()), int(rows.max()) + 1) if len(rows) else (0, h)
    band = frames[:, top:bottom, :]
    bh = bottom - top
    min_overlap = max(20, bh // 5)
    offsets = [0]
    for i in range(1, n):
        a, b = band[i - 1], band[i]
        err0 = np.abs(a - b).mean()
        best_s, best_e = 0, err0
        if err0 > 0.5:
            for s in range(-(bh - min_overlap), bh - min_overlap + 1):
                if s == 0:
                    continue
                if s > 0:
                    e = np.abs(a[s:] - b[:bh - s]).mean()
                else:
                    e = np.abs(a[:bh + s] - b[-s:]).mean()
                if e < best_e:
                    best_e, best_s = e, s
            if not best_e < 0.6 * err0:
                best_s = 0
        offsets.append(offsets[-1] + best_s)
    text = open(times_file, encoding='utf-8').read()
    found = re.findall(r'frame \d+ t=([0-9.]+)', text)
    written = [float(x) for x in found] if found else [float(x) for x in text.split()]
    idx = [int(np.argmin([abs(t - x) for t in times])) for x in written]
    lo = min(offsets); hi = max(offsets) + bh
    seen = np.zeros(hi - lo, dtype=bool)
    for o in offsets:
        seen[o - lo:o - lo + bh] = True
    cov = np.zeros(hi - lo, dtype=bool)
    for i in idx:
        cov[offsets[i] - lo:offsets[i] - lo + bh] = True
    missed = seen & ~cov
    gaps = []
    run = None
    for k, m in enumerate(missed):
        if m and run is None: run = k
        if not m and run is not None: gaps.append([run + lo, k + lo]); run = None
    if run is not None: gaps.append([run + lo, len(missed) + lo])
    print(json.dumps({'frames': n, 'band_rows': [top, bottom], 'total_scroll_px': int(offsets[-1]),
                      'scroll_range_px': int(max(offsets) - min(offsets)), 'seen_px': int(seen.sum()),
                      'missed_px': int(missed.sum()), 'missed_pct': round(100.0 * missed.sum() / max(1, seen.sum()), 2),
                      'gaps': gaps, 'written': len(idx),
                      'offsets_at_written': [int(offsets[i]) for i in idx]}))

if __name__ == '__main__':
    main()
