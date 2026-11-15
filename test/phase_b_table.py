#!/usr/bin/env python3
"""Build the Phase B result table (HANDOFF.md 6.3) from the files in test/results/phase_b.

Input for each run:
  RUN.json        phase_b.ps1 (settings, file, FFmpeg command, CPU, window titles)
  RUN.prep.json   prep_frames.py (scroll span, kept frames)
  RUN.score.json  score.py (lines caught, errors)
Output: a Markdown table on stdout. Runs without a score file show "not read".
"""

import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, 'results', 'phase_b')
ORDER = ['LL', 'B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7', 'B7s4', 'B8', 'B9', 'B11', 'B12', 'B13', 'B14']
SCALE = 1.25          # Windows scaling on Bran's PC
LINE_PX = 31          # physical px per text line at font 100 %
EMOJI = set('L%03d' % i for i in range(221, 241))


def load(name):
    p = os.path.join(RES, name)
    if not os.path.exists(p):
        return None
    with open(p, encoding='utf-8-sig') as f:
        return json.load(f)


def duration(path):
    try:
        out = subprocess.check_output(['ffprobe', '-v', 'error', '-show_entries', 'format=duration',
                                       '-of', 'csv=p=0', path])
        return float(out.decode().strip())
    except Exception:
        return None


def main():
    rows = []
    head = ['Run', 'Setting', 'Step px / limit', 'Dup in scroll', 'Frames given', 'Lines caught',
            'Err. lines (no Zalgo)', 'Err. lines outside L221-L240', 'CER', 'WER', 'MB', 'MB/min', 'CPU %']
    for run in ORDER:
        r = load(run + '.json')
        p = load(run + '.prep.json')
        s = load(run + '.score.json')
        if run == 'B14':
            r = {'Fps': 10, 'Speed': 1000, 'Font': 100, 'Chroma': 'yuv420', 'Mode': 'fixed',
                 'File': p['video'] if p else '', 'CpuPct': None,
                 'Cmd': 'old app v1.0.0: gdigrab full desktop, libx264 medium, CRF 21'}
        if r is None or p is None:
            continue
        height = 892
        step = r['Speed'] * SCALE / r['Fps']
        limit = height - 2 * LINE_PX * r['Font'] / 100.0
        cq = re.search(r'-cq (\d+)', r.get('Cmd') or '')
        enc = 'NVENC CQ ' + cq.group(1) if cq else ('lossless' if run == 'LL' else 'x264 CRF 21')
        setting = '%g fps, %d px/s, font %d%%, %s, %s, %s' % (
            r['Fps'], r['Speed'], r['Font'], '4:4:4' if r['Chroma'] == 'yuv444' else '4:2:0',
            'changes only' if r['Mode'] == 'changes' else 'fixed', enc)
        if run == 'B14':
            setting = 'old app v1.0.0: 10 fps, gdigrab full desktop, x264 medium CRF 21 (cropped)'
        k = p['kept_index']
        gaps = sum(1 for a, b in zip(k, k[1:]) if b - a > 1)
        dur = duration(r['File']) if r.get('File') else None
        mb = os.path.getsize(r['File']) / 1048576.0 if r.get('File') and os.path.exists(r['File']) else None
        if s:
            sm = s['summary']
            oz = sm['outside_zalgo']
            no_emoji = sum(1 for e in s['error_lines'] if e['tag'] not in EMOJI and e['tag'] != 'L290')
            caught = '%d/300' % sm['lines_caught']
            err = str(sm['errors_outside_zalgo'])
            ne = str(no_emoji)
            cer = '%.2f%%' % (100 * oz['cer_all'])
            wer = '%.2f%%' % (100 * oz['wer_all'])
        else:
            caught = err = ne = cer = wer = 'not read'
        rows.append([run, setting, '%d / %d' % (step, limit), str(gaps), str(p['kept']), caught, err, ne,
                     cer, wer, '%.2f' % mb if mb else '-', '%.1f' % (mb / dur * 60) if mb and dur else '-',
                     '%.2f' % r['CpuPct'] if r.get('CpuPct') is not None else '-'])
    print('| ' + ' | '.join(head) + ' |')
    print('|' + '---|' * len(head))
    for row in rows:
        print('| ' + ' | '.join(row) + ' |')


if __name__ == '__main__':
    sys.exit(main())
