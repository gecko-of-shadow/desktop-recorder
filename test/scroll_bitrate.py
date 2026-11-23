#!/usr/bin/env python3
"""Video data during the scroll only, for the Phase B runs B11, B3, B9 and LL.

The 6.8 MB column holds the full file: still page before and after the scroll too.
This script sums the packet sizes of the moving frames (first to last kept frame of the 6.8 prep file)
and gives MB per minute of scrolling. It also gives the full file size and length.
Output: JSON on stdout. Needs ffprobe.
"""

import json
import os
import subprocess
import sys

PHASE_B = r'G:\CLAUDE CODE\Screen Vibe Recorder\test\results\phase_b'
RUNS = {'B11': 'CQ 17', 'B3': 'CQ 23', 'B9': 'CQ 28', 'LL': 'lossless'}


def packets(video):
    out = subprocess.check_output(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                                   'packet=pts_time,size,flags', '-of', 'json', video])
    pk = json.loads(out.decode('utf-8'))['packets']
    pk = [x for x in pk if x.get('pts_time') not in (None, 'N/A')]
    pk.sort(key=lambda x: float(x['pts_time']))
    return pk


def main():
    res = {}
    for run, label in RUNS.items():
        prep = json.load(open(os.path.join(PHASE_B, run + '.prep.json'), encoding='utf-8-sig'))
        pk = packets(prep['video'])
        # Motion only: from the first to the last kept (unique) frame. The prep span can also hold
        # still frames after the scroll (B9, B11, LL), which cost almost no data.
        first, last = prep['kept_index'][0], prep['kept_index'][-1]
        span = pk[first:last + 1]
        t0, t1 = float(span[0]['pts_time']), float(span[-1]['pts_time'])
        dur = (t1 - t0) + (t1 - t0) / max(1, len(span) - 1)   # add one frame time
        span_bytes = sum(int(x['size']) for x in span)
        all_bytes = sum(int(x['size']) for x in pk)
        file_bytes = os.path.getsize(prep['video'])
        res[run] = {
            'label': label,
            'packets': len(pk), 'decoded_frames_6_8': prep['frames_total'],
            'span_frames': len(span), 'span_seconds': round(dur, 3),
            'span_MB': round(span_bytes / 1e6, 3),
            'span_MB_per_min': round(span_bytes / 1e6 / dur * 60, 2),
            'keyframes_in_span': sum(1 for x in span if 'K' in x.get('flags', '')),
            'file_MB_1e6': round(file_bytes / 1e6, 3),
            'all_packets_MB': round(all_bytes / 1e6, 3),
        }
    json.dump(res, sys.stdout, indent=2)
    print()


if __name__ == '__main__':
    sys.exit(main())
