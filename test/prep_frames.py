#!/usr/bin/env python3
"""Prepare the frames of a Phase B video for the AI reader (HANDOFF.md 6.3).

Steps:
  1. Decode every frame (ffmpeg, optional crop, no scaling) to gray for the checks
     and to PNG for the reader.
  2. Drop a frame when it is a duplicate of the frame before it
     (mean absolute difference <= threshold, same rule as frames.py --threshold).
  3. Keep only the scroll span: from the first frame with text to the last frame
     with text before the first blank frame that follows the text. This removes
     the idle page before the scroll and the END screen after it.
  4. Copy the kept PNG files, renamed f001.png, f002.png, ..., to the output folder.

"Text" means more than --min-dark of the pixels have a luma below 100. NVENC can
leave a light ghost of old text on a blank page; the luma limit ignores it.

Usage:
  python test/prep_frames.py VIDEO OUTDIR [--crop W:H:X:Y] [--threshold 0.1]
Standard library and ffmpeg only.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile


def probe_size(path, crop):
    if crop:
        w, h, _, _ = (int(v) for v in crop.split(':'))
        return w, h
    out = subprocess.check_output(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                                   '-show_entries', 'stream=width,height', '-of', 'csv=p=0', path])
    w, h = out.decode().strip().split(',')[:2]
    return int(w), int(h)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('video')
    ap.add_argument('outdir')
    ap.add_argument('--crop', help='W:H:X:Y crop in pixels (no scaling)')
    ap.add_argument('--threshold', type=float, default=0.1)
    ap.add_argument('--min-dark', type=float, default=0.002, help='fraction of dark pixels for a text frame')
    args = ap.parse_args(argv)

    w, h = probe_size(args.video, args.crop)
    vf = ['crop=' + args.crop] if args.crop else []
    tmp = tempfile.mkdtemp(prefix='prep_frames_')
    try:
        # PNG for the reader. -fps_mode passthrough keeps one PNG for each decoded frame.
        png_vf = vf + ['format=rgb24']
        subprocess.check_call(['ffmpeg', '-v', 'error', '-i', args.video, '-vf', ','.join(png_vf),
                               '-fps_mode', 'passthrough', os.path.join(tmp, '%06d.png')])
        gray_vf = vf + ['format=gray']
        raw = subprocess.check_output(['ffmpeg', '-v', 'error', '-i', args.video, '-vf', ','.join(gray_vf),
                                       '-fps_mode', 'passthrough', '-f', 'rawvideo', '-'])
        size = w * h
        n = len(raw) // size
        frames = [raw[i * size:(i + 1) * size] for i in range(n)]
        pngs = sorted(f for f in os.listdir(tmp) if f.endswith('.png'))
        if len(pngs) != n:
            print('PNG count %d does not match gray count %d' % (len(pngs), n), file=sys.stderr)
            return 2

        step = 4
        def dark_fraction(f):
            sample = f[::step]
            return sum(1 for b in sample if b < 100) / float(len(sample))

        def mad(a, b):
            sa, sb = a[::step], b[::step]
            return sum(abs(x - y) for x, y in zip(sa, sb)) / float(len(sa))

        dark = [dark_fraction(f) for f in frames]
        has_text = [d > args.min_dark for d in dark]
        first = next((i for i in range(n) if has_text[i]), None)
        if first is None:
            print('No frame with text.', file=sys.stderr)
            return 3
        last = first
        for i in range(first, n):
            if not has_text[i]:
                break
            last = i

        kept = []
        prev = None
        for i in range(first, last + 1):
            if prev is not None and mad(frames[i], frames[prev]) <= args.threshold:
                continue
            kept.append(i)
            prev = i

        if os.path.isdir(args.outdir):
            shutil.rmtree(args.outdir)
        os.makedirs(args.outdir)
        for k, i in enumerate(kept, 1):
            shutil.copyfile(os.path.join(tmp, pngs[i]), os.path.join(args.outdir, 'f%03d.png' % k))
        report = {'video': args.video, 'frames_total': n, 'span_first': first, 'span_last': last,
                  'span_frames': last - first + 1, 'kept': len(kept),
                  'duplicates_in_span': last - first + 1 - len(kept), 'kept_index': kept,
                  'size': [w, h], 'crop': args.crop}
        # The report goes to stdout only: the reader folder must not hold any hint about the run.
        print(json.dumps(report))
        return 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
