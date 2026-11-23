#!/usr/bin/env python3
"""Compare the frames that the 6.8 readers saw with the frames that prep_frames.py makes now.

Input:
  - the 6.8 reader transcripts (Claude Code subagent .jsonl files),
  - test/results/phase_b/blind_map.json (run -> R name),
  - test/results/phase_b/reads2/frames_by_run/RUN/f###.png (made now with prep_frames.py).

For each Read call of a reader, the script finds the frame name (f###.png) and the image
that the tool gave to the model. A PNG image is compared byte for byte with the new file.
A JPEG image is only counted (the tool changed the format), and its size is recorded.

Output: a JSON report on stdout. Standard library only.
"""

import base64
import glob
import hashlib
import json
import os
import re
import struct
import sys

SUBAGENTS = r'C:\Users\Bran\.claude\projects\G--CLAUDE-CODE-Screen-Vibe-Recorder\27960a22-7cbe-4c76-9d1f-74ffeca1b6e9\subagents'
PHASE_B = r'G:\CLAUDE CODE\Screen Vibe Recorder\test\results\phase_b'
RUNS = {'B3': ['R05'], 'B9': ['R07'], 'B11': ['R15'], 'LL': ['R04', 'R04b']}


def jpeg_dims(data):
    i = 2
    while i < len(data) - 9:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC1, 0xC2):
            h, w = struct.unpack('>HH', data[i + 5:i + 9])
            return w, h
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            i += 2
            continue
        i += 2 + struct.unpack('>H', data[i + 2:i + 4])[0]
    return None


def reader_for(rname):
    """Return the transcript path whose first prompt names the folder ...\\read\\RNAME."""
    for p in sorted(glob.glob(os.path.join(SUBAGENTS, 'agent-*.jsonl'))):
        with open(p, encoding='utf-8') as f:
            for line in f:
                d = json.loads(line)
                if d.get('type') == 'user':
                    c = d['message']['content']
                    if isinstance(c, list):
                        c = ' '.join(x.get('text', '') for x in c if isinstance(x, dict))
                    if re.search(r'\\read\\%s\r?\n' % re.escape(rname), c):
                        return p
                    break
    return None


def main():
    report = {}
    for run, rnames in RUNS.items():
        new_dir = os.path.join(PHASE_B, 'reads2', 'frames_by_run', run)
        new_files = sorted(os.listdir(new_dir))
        for rname in rnames:
            path = reader_for(rname)
            if path is None:
                report['%s/%s' % (run, rname)] = 'transcript not found'
                continue
            calls = {}   # tool_use id -> frame name
            png_same = png_diff = jpeg = other = 0
            jpeg_frames = set()
            jpeg_new_sizes = []
            png_new_sizes = []
            read_frames = set()
            with open(path, encoding='utf-8') as f:
                for line in f:
                    d = json.loads(line)
                    msg = d.get('message', {})
                    if d.get('type') == 'assistant':
                        for b in msg.get('content', []):
                            if b.get('type') == 'tool_use' and b.get('name') == 'Read':
                                m = re.search(r'(f\d{3}\.png)$', b['input'].get('file_path', ''))
                                if m:
                                    calls[b['id']] = m.group(1)
                    if d.get('type') == 'user' and isinstance(msg.get('content'), list):
                        for b in msg['content']:
                            if b.get('type') != 'tool_result' or b.get('tool_use_id') not in calls:
                                continue
                            name = calls[b['tool_use_id']]
                            read_frames.add(name)
                            new_path = os.path.join(new_dir, name)
                            new_bytes = open(new_path, 'rb').read() if os.path.exists(new_path) else None
                            for x in (b.get('content') or []):
                                if not isinstance(x, dict) or x.get('type') != 'image':
                                    continue
                                data = base64.b64decode(x['source']['data'])
                                mt = x['source'].get('media_type')
                                if mt == 'image/png':
                                    if new_bytes is not None and hashlib.sha256(data).digest() == hashlib.sha256(new_bytes).digest():
                                        png_same += 1
                                    else:
                                        png_diff += 1
                                    if new_bytes is not None:
                                        png_new_sizes.append(len(new_bytes))
                                elif mt == 'image/jpeg':
                                    jpeg += 1
                                    jpeg_frames.add(name)
                                    if new_bytes is not None:
                                        jpeg_new_sizes.append(len(new_bytes))
                                    report.setdefault('_jpeg_dims', set()).add(str(jpeg_dims(data)))
                                else:
                                    other += 1
            report['%s/%s' % (run, rname)] = {
                'transcript': os.path.basename(path),
                'frames_in_new_folder': len(new_files),
                'distinct_frames_read': len(read_frames),
                'frames_not_read': sorted(set(new_files) - read_frames),
                'png_images_byte_equal_to_new_file': png_same,
                'png_images_different': png_diff,
                'jpeg_images': jpeg,
                'distinct_frames_sent_as_jpeg': len(jpeg_frames),
                'smallest_png_file_sent_as_jpeg_bytes': min(jpeg_new_sizes) if jpeg_new_sizes else None,
                'largest_png_file_sent_as_png_bytes': max(png_new_sizes) if png_new_sizes else None,
            }
    if '_jpeg_dims' in report:
        report['_jpeg_dims'] = sorted(report['_jpeg_dims'])
    json.dump(report, sys.stdout, indent=2)
    print()


if __name__ == '__main__':
    sys.exit(main())
