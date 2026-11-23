#!/usr/bin/env python3
"""HANDOFF 6.11: more AI reads of the Phase B runs B3 (CQ 23), B9 (CQ 28), B11 (CQ 17)
and LL (lossless control). Reader: Claude Sonnet 5.5 through the Message Batches API.

Steps. Each step can run again. Each step reads and writes files in OUT (test/results/phase_b/reads2):
  blind     give each run a neutral set name (S1..S4) in random order        -> blind_map2.json
  upload    upload the frames to the Files API (they expire after 7 days)     -> files.json
  count     count the input tokens of one request for each set (free call)    -> count.json
  submit    create a batch (sets x reads). The batch id is written at once.   -> batches.json
  wait      poll the open batches until they end (run it in the background)
  collect   save each result: raw JSON, reply, copy, checks, usage and cost   -> raw/, reads/, results.json
  score     run test/score.py on each new copy and on the 6.8 copies         -> scores/
  table     mean and spread for each run, per-line matrix                     -> table.md, summary.json

The reader sees only the prompt text, the labels f001.png, f002.png ... and the frames.
It never sees a run name, a set name or a CQ value. The custom_id stays on the client side.

Frames: make them first with test/prep_frames.py (same settings as 6.8):
  python test/prep_frames.py VIDEO test/results/phase_b/reads2/frames_by_run/RUN

Needs: the anthropic package (2.x) and ANTHROPIC_API_KEY in the environment.
"""

import argparse
import difflib
import hashlib
import itertools
import json
import math
import os
import random
import re
import subprocess
import sys
import time

REPO = r'G:\CLAUDE CODE\Screen Vibe Recorder'
PHASE_B = os.path.join(REPO, 'test', 'results', 'phase_b')
OUT = os.path.join(PHASE_B, 'reads2')
SOURCE = os.path.join(REPO, 'test', 'source.md')
SCORE = os.path.join(REPO, 'test', 'score.py')

MODEL = 'claude-sonnet-5-5'
READS = 3
MAX_TOKENS = 100000
EXPIRE_S = 7 * 24 * 3600
# USD per million tokens, Message Batches price for Claude Sonnet 5.5 (50% of $2 / $10).
# Cache prices are 50% of the standard cache prices. This job does not use the cache.
PRICE_BATCH = {'input': 1.00, 'output': 5.00, 'cache_write_5m': 1.25, 'cache_write_1h': 2.00, 'cache_read': 0.05}

RUNS = ['B11', 'B3', 'B9', 'LL']   # in CQ order: 17, 23, 28, lossless
LABEL = {'B11': 'CQ 17 (Sharpest)', 'B3': 'CQ 23 (Balanced)', 'B9': 'CQ 28 (Smallest)', 'LL': 'lossless control'}
OLD_READS = {'B11': ['R15'], 'B3': ['R05'], 'B9': ['R07'], 'LL': ['R04', 'R04b']}
MB_68 = {'B11': 6.34, 'B3': 4.33, 'B9': 3.08, 'LL': 9.97}          # HANDOFF 6.8, MB column
MBMIN_68 = {'B11': 33.7, 'B3': 21.8, 'B9': 16.4, 'LL': 48.2}       # HANDOFF 6.8, MB/min column
EMOJI = set('L%03d' % i for i in range(221, 241))
ZALGO = 'L290'
TAG_LINE = re.compile(r'^\s*L(\d{3})\b')

# The 6.8 prompt (Claude Code subagent, 2026-11-14), with the two paths as placeholders.
PROMPT_68 = """You are a text reader for a screen-recording test. Your only input is a folder of PNG frames from one video of a page that scrolls up.

Folder: {FOLDER}
Files: f001.png, f002.png, and so on, in time order.

Rules:
1. Open only the PNG files in that folder, with the Read tool. Do not open, search or list any other file or folder. Do not run commands.
2. Read every frame, in order. Do not skip frames.
3. Most text lines start with a tag: the letter L and 3 digits, for example L007. Copy each tagged line exactly as you see it: every letter, digit, space, symbol, accent and emoji. Do not correct, complete or guess text. Look-alike characters (O and 0, l and 1 and I, rn and m) matter.
4. Skip a line that the top or bottom edge of a frame cuts. Read it in another frame where it is complete.
5. A line shows in several frames. Write each tag one time. When frames disagree, use the copy that you see most clearly.
6. If you cannot read part of a line, copy the part you can read and write [?] for the rest.
7. Write the result with the Write tool to this file:
   {OUTFILE}
   One line for each tag, in tag order: the tag, one space, then the text. No headers, no notes, no code fences, no blank lines. Do not start any other line with a tag.
8. Your final reply: only the number of frames you read and the number of tags you wrote. Nothing else."""

# The API prompt: rules 2 to 6 are the same. Only the input and output lines changed
# (no folder, no Read or Write tool, the reply is the copy).
PROMPT = """You are a text reader for a screen-recording test. Your only input is a set of PNG frames from one video of a page that scrolls up.

Frames: f001.png, f002.png, and so on, in time order. They are attached below this text. A label with the file name comes before each frame.

Rules:
1. Use only the attached frames.
2. Read every frame, in order. Do not skip frames.
3. Most text lines start with a tag: the letter L and 3 digits, for example L007. Copy each tagged line exactly as you see it: every letter, digit, space, symbol, accent and emoji. Do not correct, complete or guess text. Look-alike characters (O and 0, l and 1 and I, rn and m) matter.
4. Skip a line that the top or bottom edge of a frame cuts. Read it in another frame where it is complete.
5. A line shows in several frames. Write each tag one time. When frames disagree, use the copy that you see most clearly.
6. If you cannot read part of a line, copy the part you can read and write [?] for the rest.
7. Write the result as your reply. One line for each tag, in tag order: the tag, one space, then the text. No headers, no notes, no code fences, no blank lines. Do not start any other line with a tag.
8. After the last tagged line, write one blank line. Then write one last line with only the number of frames you read and the number of tags you wrote. Nothing else."""


# ---------------------------------------------------------------- helpers

def p(*parts):
    return os.path.join(OUT, *parts)


def load(path, default=None):
    if not os.path.exists(path):
        return default
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def save(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


def client():
    import anthropic
    if not os.environ.get('ANTHROPIC_API_KEY'):
        sys.exit('ANTHROPIC_API_KEY is not set.')
    return anthropic.Anthropic(max_retries=5)


def frames_of(run):
    d = p('frames_by_run', run)
    return sorted(f for f in os.listdir(d) if re.match(r'^f\d{3}\.png$', f))


def blind_map():
    m = load(p('blind_map2.json'))
    if not m:
        sys.exit('Run the "blind" step first.')
    return m['sets']          # set name -> run


def run_of_cid(cid, sets):
    return sets[cid.split('-')[0]]


def build_params(file_list, max_tokens, effort=None):
    content = [{'type': 'text', 'text': PROMPT}]
    for name, fid in file_list:
        content.append({'type': 'text', 'text': name})
        content.append({'type': 'image', 'source': {'type': 'file', 'file_id': fid}})
    # Thinking: adaptive is the default on Claude Sonnet 5.5. "summarized" only makes the
    # thinking visible (same thinking, same price). Effort: not set (API default).
    params = {
        'model': MODEL,
        'max_tokens': max_tokens,
        'thinking': {'type': 'adaptive', 'display': 'summarized'},
        'messages': [{'role': 'user', 'content': content}],
    }
    if effort:
        params['output_config'] = {'effort': effort}
    return params


def cost_of(usage):
    u = usage or {}
    cc = u.get('cache_creation') or {}
    w5 = cc.get('ephemeral_5m_input_tokens')
    w1 = cc.get('ephemeral_1h_input_tokens')
    if w5 is None and w1 is None:
        w5, w1 = u.get('cache_creation_input_tokens') or 0, 0
    total = ((u.get('input_tokens') or 0) * PRICE_BATCH['input']
             + (u.get('output_tokens') or 0) * PRICE_BATCH['output']
             + (w5 or 0) * PRICE_BATCH['cache_write_5m']
             + (w1 or 0) * PRICE_BATCH['cache_write_1h']
             + (u.get('cache_read_input_tokens') or 0) * PRICE_BATCH['cache_read'])
    return total / 1e6


def split_reply(text):
    """Return (copy, trailer). The copy ends at the last line that starts with a tag."""
    lines = text.split('\n')
    last = None
    for i, line in enumerate(lines):
        if TAG_LINE.match(line):
            last = i
    if last is None:
        return text, ''
    copy = '\n'.join(lines[:last + 1]).rstrip() + '\n'
    trailer = '\n'.join(lines[last + 1:]).strip()
    return copy, trailer


# ---------------------------------------------------------------- steps

def step_blind(args):
    if os.path.exists(p('blind_map2.json')):
        print('blind_map2.json exists; not changed:', load(p('blind_map2.json'))['sets'])
        return
    names = ['S1', 'S2', 'S3', 'S4']
    runs = list(RUNS)
    random.SystemRandom().shuffle(runs)
    sets = dict(zip(names, runs))
    save(p('blind_map2.json'), {'sets': sets, 'note': 'set name -> run. Random order (SystemRandom).'})
    write_text(p('prompt.txt'), PROMPT + '\n')
    old = PROMPT_68.replace('{FOLDER}', '<folder>').replace('{OUTFILE}', '<file>')
    diff = '\n'.join(difflib.unified_diff(old.splitlines(), PROMPT.splitlines(), '6.8 prompt (subagent)', '6.11 prompt (API)', lineterm=''))
    write_text(p('prompt_diff.txt'), diff + '\n')
    print('sets:', sets)


def step_upload(args):
    c = client()
    sets = blind_map()
    files = load(p('files.json'), {})
    for s, run in sorted(sets.items()):
        have = {e['name']: e for e in files.get(s, [])}
        out = []
        for name in frames_of(run):
            path = p('frames_by_run', run, name)
            data = open(path, 'rb').read()
            sha = hashlib.sha256(data).hexdigest()
            e = have.get(name)
            if e and e['sha256'] == sha:
                out.append(e)
                continue
            meta = c.files.upload(file=(name, data, 'image/png'), expires_in_seconds=EXPIRE_S)
            e = {'name': name, 'id': meta.id, 'bytes': len(data), 'sha256': sha,
                 'expires_at': str(getattr(meta, 'expires_at', None))}
            out.append(e)
            # Keep the entries that are not processed yet, so a new run does not upload them again.
            done = {y['name'] for y in out}
            files[s] = out + [x for x in have.values() if x['name'] not in done]
            save(p('files.json'), files)
        files[s] = out
        save(p('files.json'), files)
        print(s, 'frames uploaded:', len(out), 'MB %.2f' % (sum(e['bytes'] for e in out) / 1e6))


def file_list(s):
    files = load(p('files.json'), {})
    if s not in files:
        sys.exit('No uploaded files for %s. Run the "upload" step.' % s)
    return [(e['name'], e['id']) for e in sorted(files[s], key=lambda e: e['name'])]


def step_count(args):
    """The count endpoint does not take file sources. Count the smallest set with inline base64
    images, count the text of each set without images, and derive the input tokens of each set."""
    import base64
    c = client()
    sets = blind_map()
    result = {}

    def count(content):
        return c.messages.count_tokens(model=MODEL, messages=[{'role': 'user', 'content': content}],
                                       thinking={'type': 'adaptive'}).input_tokens

    text_only = {}
    for s, run in sorted(sets.items()):
        content = [{'type': 'text', 'text': PROMPT}] + [{'type': 'text', 'text': n} for n in frames_of(run)]
        text_only[s] = count(content)
    small = min(sets, key=lambda s: sum(os.path.getsize(p('frames_by_run', sets[s], n)) for n in frames_of(sets[s])))
    run = sets[small]
    content = [{'type': 'text', 'text': PROMPT}]
    for n in frames_of(run):
        data = base64.b64encode(open(p('frames_by_run', run, n), 'rb').read()).decode('ascii')
        content.append({'type': 'text', 'text': n})
        content.append({'type': 'image', 'source': {'type': 'base64', 'media_type': 'image/png', 'data': data}})
    full_small = count(content)
    per_image = (full_small - text_only[small]) / float(len(frames_of(run)))
    for s, run in sorted(sets.items()):
        n = len(frames_of(run))
        result[s] = {'frames': n, 'text_tokens': text_only[s], 'tokens_per_image_measured': per_image,
                     'image_tokens_if_not_scaled': 40 * 32 * n,
                     'input_tokens': int(round(text_only[s] + n * per_image))}
        print(s, result[s])
    result['_note'] = 'Measured with inline base64 on %s (%d tokens). Other sets: text count + frames x tokens per image.' % (small, full_small)
    save(p('count.json'), result)


def parse_reads(spec):
    out = []
    for part in spec.split(','):
        if '-' in part:
            a, b = part.split('-')
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def step_submit(args):
    c = client()
    sets = blind_map()
    want_sets = args.sets.split(',') if args.sets else sorted(sets)
    results = load(p('results.json'), {})
    batches = load(p('batches.json'), [])
    pending = set()
    for b in batches:
        if not b.get('collected'):
            pending.update(b['custom_ids'])
    requests = []
    for s in want_sets:
        fl = file_list(s)
        for k in parse_reads(args.reads):
            cid = '%s-%s%d' % (s, args.id_prefix, k)
            if cid in pending or (results.get(cid, {}).get('status') == 'succeeded' and not args.force):
                print('skip', cid, '(pending or done)')
                continue
            requests.append({'custom_id': cid, 'params': build_params(fl, args.max_tokens, args.effort)})
    if not requests:
        print('Nothing to submit.')
        return
    order = list(requests)
    random.SystemRandom().shuffle(order)
    batch = c.messages.batches.create(requests=order)
    entry = {'id': batch.id, 'created': time.strftime('%Y-%m-%d %H:%M:%S %z'), 'note': args.note,
             'custom_ids': [r['custom_id'] for r in order], 'max_tokens': args.max_tokens,
             'effort': args.effort or 'not set (API default)',
             'model': MODEL, 'prompt_sha256': hashlib.sha256(PROMPT.encode('utf-8')).hexdigest(),
             'collected': False}
    batches.append(entry)
    save(p('batches.json'), batches)
    print('batch', batch.id, 'requests', len(order), batch.processing_status)


def step_wait(args):
    c = client()
    log = open(p('wait.log'), 'a', encoding='utf-8')
    t0 = time.time()
    while True:
        batches = load(p('batches.json'), [])
        open_ids = [b['id'] for b in batches if not b.get('ended')]
        if not open_ids:
            print('All batches ended.')
            break
        for bid in open_ids:
            b = c.messages.batches.retrieve(bid)
            rc = b.request_counts
            line = '%s %s %s processing=%d succeeded=%d errored=%d canceled=%d expired=%d' % (
                time.strftime('%H:%M:%S'), bid, b.processing_status, rc.processing, rc.succeeded,
                rc.errored, rc.canceled, rc.expired)
            print(line)
            log.write(line + '\n')
            log.flush()
            if b.processing_status == 'ended':
                batches = load(p('batches.json'), [])
                for e in batches:
                    if e['id'] == bid:
                        e['ended'] = True
                        e['ended_at'] = str(b.ended_at)
                save(p('batches.json'), batches)
        if time.time() - t0 > args.timeout:
            print('Timeout. Run "wait" again.')
            break
        time.sleep(args.interval)
    if args.then_collect:
        step_collect(args)


def check_copy(copy):
    tags = []
    for line in copy.splitlines():
        m = TAG_LINE.match(line)
        if m:
            tags.append(int(m.group(1)))
    distinct = set(tags)
    return {
        'tag_lines': len(tags),
        'distinct_tags': len(distinct),
        'repeated_tags': sorted('L%03d' % t for t in distinct if tags.count(t) > 1),
        'tags_out_of_range': sorted('L%03d' % t for t in distinct if not 1 <= t <= 300),
        'missing_tags': len(set(range(1, 301)) - distinct),
        'in_tag_order': tags == sorted(tags),
    }


def step_collect(args):
    c = client()
    sets = blind_map()
    count = load(p('count.json'), {})
    results = load(p('results.json'), {})
    batches = load(p('batches.json'), [])
    for e in batches:
        if e.get('collected'):
            continue
        b = c.messages.batches.retrieve(e['id'])
        if b.processing_status != 'ended':
            print(e['id'], 'not ended:', b.processing_status)
            continue
        for item in c.messages.batches.results(e['id']):
            cid = item.custom_id
            raw = item.model_dump(mode='json')
            save(p('raw', cid + '.json'), raw)
            r = item.result
            entry = {'batch_id': e['id'], 'status': r.type, 'run': run_of_cid(cid, sets)}
            if r.type == 'succeeded':
                msg = r.message
                text = ''.join(blk.text for blk in msg.content if blk.type == 'text')
                thinking = [blk.thinking for blk in msg.content if blk.type == 'thinking']
                copy, trailer = split_reply(text)
                write_text(p('reads', cid + '.reply.txt'), text)
                write_text(p('reads', cid + '.txt'), copy)
                if thinking:
                    write_text(p('thinking', cid + '.txt'), '\n\n---\n\n'.join(t or '' for t in thinking))
                usage = msg.usage.model_dump(mode='json') if msg.usage else {}
                s = cid.split('-')[0]
                expected = count.get(s, {}).get('input_tokens')
                entry.update({
                    'model': msg.model,
                    'stop_reason': msg.stop_reason,
                    'usage': usage,
                    'cost_usd': round(cost_of(usage), 4),
                    'trailer': trailer,
                    'checks': dict(check_copy(copy),
                                   model_ok=msg.model == MODEL,
                                   stop_ok=msg.stop_reason == 'end_turn',
                                   input_tokens_counted=expected,
                                   input_tokens_equal_count=(usage.get('input_tokens') == expected) if expected else None,
                                   min_image_tokens=40 * 32 * len(file_list(s))),
                })
            else:
                entry['error'] = raw.get('result')
            results[cid] = entry
            print(cid, entry['status'], entry.get('stop_reason'), entry.get('usage', {}).get('input_tokens'),
                  entry.get('usage', {}).get('output_tokens'), entry.get('cost_usd'),
                  entry.get('checks', {}).get('distinct_tags'), repr(entry.get('trailer', ''))[:60])
        e['collected'] = True
        save(p('results.json'), results)
        save(p('batches.json'), batches)
    total = sum(v.get('cost_usd') or 0 for v in results.values())
    print('Reader cost so far (from usage): $%.4f' % total)


def run_score(copy_path, json_path, txt_path):
    os.makedirs(os.path.dirname(json_path), exist_ok=True)
    out = subprocess.run([sys.executable, '-B', SCORE, SOURCE, copy_path, '--json', json_path, '--show', '-1'],
                         capture_output=True, text=True, encoding='utf-8')
    write_text(txt_path, out.stdout + out.stderr)
    if 'ERROR:' in out.stdout:
        print('score.py error for', copy_path)


def step_score(args):
    results = load(p('results.json'), {})
    for cid, e in sorted(results.items()):
        if not valid_read(cid, e):
            continue
        j = p('scores', cid + '.score.json')
        if os.path.exists(j) and not args.force:
            continue
        run_score(p('reads', cid + '.txt'), j, p('scores', cid + '.score.txt'))
        print('scored', cid)
    for run, names in OLD_READS.items():
        for r in names:
            j = p('scores', 'old_%s.score.json' % r)
            if os.path.exists(j) and not args.force:
                continue
            run_score(os.path.join(PHASE_B, 'reads', r + '.txt'), j, p('scores', 'old_%s.score.txt' % r))
            print('scored old', r)


# ---------------------------------------------------------------- analysis

def metrics(score):
    sm = score['summary']
    errs = score['error_lines']
    sec = score['sections']
    return {
        'caught': sm['lines_caught'],
        'err_nz': sm['errors_outside_zalgo'],
        'err_out': sum(1 for e in errs if e['tag'] not in EMOJI and e['tag'] != ZALGO),
        'cer': sm['outside_zalgo']['cer_all'],
        'wer': sm['outside_zalgo']['wer_all'],
        'char_err_out': sum(l['char_dist'] for l in score['lines']
                            if l['tag'] not in EMOJI and l['tag'] != ZALGO),
        'err_tags': [e['tag'] for e in errs if e['tag'] != ZALGO],
        'diffs': {e['tag']: e['diff'] for e in errs},
        'sections': {k: v['lines_with_errors'] for k, v in sec.items()},
        'repeats': len(score['repeated_tags']),
        'untagged': score['copy_parse']['untagged_lines'],
        'unreadable': len(score['copy_parse']['unreadable_tags']),
        'unknown': len(score['copy_parse']['unknown_tags']),
        'l300_exact': next((l['exact'] for l in score['lines'] if l['tag'] == 'L300'), None),
    }


def stats(values):
    n = len(values)
    if n == 0:
        return None
    m = sum(values) / n
    sd = math.sqrt(sum((v - m) ** 2 for v in values) / (n - 1)) if n > 1 else 0.0
    return {'n': n, 'mean': m, 'sd': sd, 'min': min(values), 'max': max(values), 'values': values}


def perm_test(a, b):
    """Exact two-sided permutation test of the difference in means. Return (diff, p, splits)."""
    pool = a + b
    obs = abs(sum(a) / len(a) - sum(b) / len(b))
    hits = total = 0
    for idx in itertools.combinations(range(len(pool)), len(a)):
        sa = [pool[i] for i in idx]
        sb = [pool[i] for i in range(len(pool)) if i not in idx]
        d = abs(sum(sa) / len(sa) - sum(sb) / len(sb))
        total += 1
        if d >= obs - 1e-12:
            hits += 1
    return sum(a) / len(a) - sum(b) / len(b), hits / total, total


def sign_test(plus, minus):
    """Two-sided exact sign test. Return p."""
    n = plus + minus
    if n == 0:
        return 1.0
    k = min(plus, minus)
    tail = sum(math.comb(n, i) for i in range(0, k + 1)) / 2 ** n
    return min(1.0, 2 * tail)


SECTION_ORDER = ['prose', 'code', 'sha256', 'base64', 'lookalike', 'table', 'accents_emoji', 'colored', 'mixed', 'mixed_tail']
SRC_SECTIONS = [('prose', 1, 60), ('code', 61, 120), ('sha256', 121, 140), ('base64', 141, 170),
                ('lookalike', 171, 200), ('table', 201, 220), ('accents_emoji', 221, 240),
                ('colored', 241, 270), ('mixed', 271, 289), ('zalgo', 290, 290), ('mixed_tail', 291, 300)]
MAIN_ID = re.compile(r'^S\d-r(\d+)$')


def section_of(tag):
    num = int(tag[1:])
    return next((n for n, lo, hi in SRC_SECTIONS if lo <= num <= hi), 'other')


def valid_read(cid, e):
    """rules.md 3: a read counts only if it ended normally, on the right model, with tagged lines."""
    return (MAIN_ID.match(cid) is not None and e.get('status') == 'succeeded'
            and e.get('stop_reason') == 'end_turn' and e.get('model') == MODEL
            and (e.get('checks') or {}).get('tag_lines', 0) > 0)


def fmt_stat(s, pct=False, digits=2):
    if not s:
        return '-'
    if pct:
        return '%.2f%% (%.2f) [%.2f-%.2f]' % (100 * s['mean'], 100 * s['sd'], 100 * s['min'], 100 * s['max'])
    return '%.*f (%.*f) [%g-%g]' % (digits, s['mean'], digits, s['sd'], s['min'], s['max'])


def step_table(args):
    sets = blind_map()
    results = load(p('results.json'), {})
    bitrate = load(p('scroll_bitrate.json'), {})
    new = {run: [] for run in RUNS}
    old = {run: [] for run in RUNS}
    rejected = []
    for cid, e in sorted(results.items()):
        if not valid_read(cid, e):
            rejected.append({'id': cid, 'status': e.get('status'), 'stop_reason': e.get('stop_reason')})
            continue
        sc = load(p('scores', cid + '.score.json'))
        if sc is None:
            rejected.append({'id': cid, 'status': 'not scored'})
            continue
        m = metrics(sc)
        m.update(id=cid, k=int(MAIN_ID.match(cid).group(1)), cost=e.get('cost_usd'),
                 out_tokens=e.get('usage', {}).get('output_tokens'),
                 thinking_tokens=(e.get('usage', {}).get('output_tokens_details') or {}).get('thinking_tokens'),
                 trailer=e.get('trailer'), checks=e.get('checks'))
        new[run_of_cid(cid, sets)].append(m)
    for run, names in OLD_READS.items():
        for r in names:
            sc = load(p('scores', 'old_%s.score.json' % r))
            if sc is not None:
                m = metrics(sc)
                m['id'] = r
                old[run].append(m)
    for run in RUNS:
        new[run].sort(key=lambda x: x['k'])

    groups = {
        'new_r1_r3': {run: [x for x in new[run] if x['k'] <= 3] for run in RUNS},
        'new_r1_r6': {run: [x for x in new[run] if x['k'] <= 6] for run in RUNS},
        'pooled_68_plus_r1_r3': {run: old[run] + [x for x in new[run] if x['k'] <= 3] for run in RUNS},
    }
    keys = ['caught', 'err_nz', 'err_out', 'cer', 'wer', 'char_err_out']
    summary = {'groups': {}, 'rejected': rejected,
               'reads': {run: {'new': new[run], 'old': old[run]} for run in RUNS}}
    for g, data in groups.items():
        summary['groups'][g] = {run: {k: stats([x[k] for x in data[run]]) for k in keys} for run in RUNS}
        for run in RUNS:
            summary['groups'][g][run]['sections'] = {
                s: stats([x['sections'].get(s, 0) for x in data[run]]) for s in SECTION_ORDER}

    pairs = [('B11', 'B3'), ('B11', 'B9'), ('B3', 'B9'), ('LL', 'B11'), ('LL', 'B3'), ('LL', 'B9')]
    tests = []
    for g, data in groups.items():
        for a, b in pairs:
            for k in ('err_out', 'err_nz', 'cer'):
                va, vb = [x[k] for x in data[a]], [x[k] for x in data[b]]
                if len(va) > 1 and len(vb) > 1:
                    d, pv, n = perm_test(va, vb)
                    tests.append({'group': g, 'a': a, 'b': b, 'metric': k, 'diff_mean': d, 'p': pv, 'splits': n,
                                  'n_a': len(va), 'n_b': len(vb)})
    summary['tests'] = tests

    # Per-line matrix (new reads r1 to r6) and the LL baseline (rules.md 6).
    ext = groups['new_r1_r6']
    matrix = {}
    for run in RUNS:
        for x in ext[run]:
            for t in x['err_tags']:
                matrix.setdefault(t, {r: 0 for r in RUNS})[run] += 1
    n_reads = {run: len(ext[run]) for run in RUNS}
    ll_clean = {t for t, row in matrix.items() if row['LL'] == 0}
    lossy_only = {run: sorted((t, matrix[t][run]) for t in ll_clean if matrix[t][run] > 0)
                  for run in RUNS if run != 'LL'}
    lossy_only_events = {run: sum(c for _, c in v) for run, v in lossy_only.items()}
    summary['line_matrix'] = matrix
    summary['lossy_only_lines'] = lossy_only
    summary['lossy_only_events'] = lossy_only_events
    signs = []
    for a, b in pairs:
        plus = minus = 0
        for t, row in matrix.items():
            if t in EMOJI or t == ZALGO:
                continue
            ra, rb = row[a] / float(max(1, n_reads[a])), row[b] / float(max(1, n_reads[b]))
            if ra > rb:
                plus += 1
            elif rb > ra:
                minus += 1
        signs.append({'a': a, 'b': b, 'lines_a_worse': plus, 'lines_b_worse': minus, 'p': sign_test(plus, minus)})
    summary['line_sign_tests'] = signs
    cost_all = sum(v.get('cost_usd') or 0 for v in results.values())
    cost_valid = sum(x['cost'] or 0 for run in RUNS for x in new[run])
    summary['reader_cost_usd'] = {'all_requests': cost_all, 'valid_reads': cost_valid}
    save(p('summary.json'), summary)

    # ------------------------------------------------------------ Markdown
    md = []
    md.append('Reads that do not count (rules.md 3 and 4): %s\n' % (
        ', '.join('%s (%s)' % (r['id'], r.get('stop_reason') or r['status']) for r in rejected) or 'none'))
    md.append('## Table A: each read\n')
    md.append('| Run | Read | Lines caught | Err. lines (no Zalgo) | Err. lines outside L221-L240 | CER | WER | SHA-256 | Base64 | Look-alike | Code | Output tokens (thinking) | Cost $ |')
    md.append('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for run in RUNS:
        for x in old[run] + new[run]:
            rid = ('6.8 ' + x['id']) if x['id'].startswith('R') else x['id']
            md.append('| %s | %s | %d/300 | %d | %d | %.2f%% | %.2f%% | %d | %d | %d | %d | %s | %s |' % (
                run, rid, x['caught'], x['err_nz'], x['err_out'], 100 * x['cer'], 100 * x['wer'],
                x['sections'].get('sha256', 0), x['sections'].get('base64', 0), x['sections'].get('lookalike', 0),
                x['sections'].get('code', 0),
                '%s (%s)' % (x['out_tokens'], x['thinking_tokens']) if x.get('out_tokens') is not None else '-',
                '%.3f' % x['cost'] if x.get('cost') is not None else '-'))
    md.append('')
    titles = {'new_r1_r3': 'Table B: new reads r1 to r3 (API, all frames PNG) - the job table',
              'new_r1_r6': 'Table C: new reads r1 to r6 (API, all frames PNG) - extended',
              'pooled_68_plus_r1_r3': 'Table D: 6.8 read(s) + new reads r1 to r3 (two methods mixed)'}
    for g in ('new_r1_r3', 'new_r1_r6', 'pooled_68_plus_r1_r3'):
        md.append('## %s\n' % titles[g])
        md.append('| Run | Reads | Lines caught (mean) | Err. lines (no Zalgo): mean (SD) [min-max] | Err. lines outside L221-L240: mean (SD) [min-max] | CER: mean (SD) [min-max] | WER: mean (SD) [min-max] | MB (6.8 file) | MB/min during the scroll |')
        md.append('|---|---|---|---|---|---|---|---|---|')
        for run in RUNS:
            s = summary['groups'][g][run]
            if not s['err_nz']:
                continue
            br = bitrate.get(run, {})
            md.append('| %s %s | %d | %.1f | %s | %s | %s | %s | %.2f | %s |' % (
                run, LABEL[run], s['err_nz']['n'], s['caught']['mean'], fmt_stat(s['err_nz']),
                fmt_stat(s['err_out']), fmt_stat(s['cer'], pct=True), fmt_stat(s['wer'], pct=True), MB_68[run],
                '%.1f' % br['span_MB_per_min'] if br else '-'))
        md.append('')
    md.append('## Table E: error lines by section, new reads r1 to r6 (mean for each read)\n')
    md.append('| Run | ' + ' | '.join(SECTION_ORDER) + ' |')
    md.append('|---|' + '---|' * len(SECTION_ORDER))
    for run in RUNS:
        s = summary['groups']['new_r1_r6'][run]['sections']
        md.append('| %s | %s |' % (run, ' | '.join('%.2f' % s[k]['mean'] if s[k] else '-' for k in SECTION_ORDER)))
    md.append('')
    md.append('## Table F: permutation tests (exact, two-sided, difference of the means)\n')
    md.append('| Reads | A | B | Metric | n A / n B | Mean A - mean B | p | Splits |')
    md.append('|---|---|---|---|---|---|---|---|')
    for t in tests:
        scale = 100 if t['metric'] == 'cer' else 1
        md.append('| %s | %s | %s | %s | %d / %d | %+.3f%s | %.3f | %d |' % (
            t['group'], t['a'], t['b'], t['metric'], t['n_a'], t['n_b'], t['diff_mean'] * scale,
            ' pp' if t['metric'] == 'cer' else '', t['p'], t['splits']))
    md.append('')
    md.append('## Table G: lines with an error in the new reads r1 to r6 (number of reads with an error)\n')
    md.append('Reads: %s.\n' % ', '.join('%s %d' % (r, n_reads[r]) for r in RUNS))
    md.append('| Line | Section | ' + ' | '.join(RUNS) + ' | Wrong in a lossy run, never in LL |')
    md.append('|---|---|' + '---|' * len(RUNS) + '---|')
    for t in sorted(matrix):
        row = matrix[t]
        flag = 'yes' if t in ll_clean else ''
        md.append('| %s | %s | %s | %s |' % (t, section_of(t), ' | '.join(str(row[r]) for r in RUNS), flag))
    md.append('')
    md.append('Error events on lines that are never wrong in LL: %s\n' % ', '.join(
        '%s %d (%d lines)' % (r, lossy_only_events[r], len(lossy_only[r])) for r in lossy_only))
    md.append('## Table H: line-paired sign test (r1 to r6, error share per line, lines outside L221-L240 and L290)\n')
    md.append('| A | B | Lines where A is worse | Lines where B is worse | p |')
    md.append('|---|---|---|---|---|')
    for s in signs:
        md.append('| %s | %s | %d | %d | %.3f |' % (s['a'], s['b'], s['lines_a_worse'], s['lines_b_worse'], s['p']))
    md.append('')
    md.append('Reader cost from the API usage fields (batch price): all requests $%.4f, valid reads $%.4f.' % (
        cost_all, cost_valid))
    write_text(p('table.md'), '\n'.join(md) + '\n')
    print('\n'.join(md))


def main():
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('step', choices=['blind', 'upload', 'count', 'submit', 'wait', 'collect', 'score', 'table'])
    ap.add_argument('--sets', help='for submit: set names, for example S2 or S1,S3')
    ap.add_argument('--reads', default='1-%d' % READS, help='for submit: read numbers, for example 1 or 2-3')
    ap.add_argument('--max-tokens', type=int, default=MAX_TOKENS)
    ap.add_argument('--effort', choices=['low', 'medium', 'high', 'xhigh', 'max'], help='for submit: output_config.effort')
    ap.add_argument('--id-prefix', default='r', help='for submit: custom_id is SET-PREFIXk (r = main reads)')
    ap.add_argument('--note', default='')
    ap.add_argument('--force', action='store_true')
    ap.add_argument('--interval', type=int, default=30)
    ap.add_argument('--timeout', type=int, default=24 * 3600)
    ap.add_argument('--then-collect', action='store_true')
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    {'blind': step_blind, 'upload': step_upload, 'count': step_count, 'submit': step_submit,
     'wait': step_wait, 'collect': step_collect, 'score': step_score, 'table': step_table}[args.step](args)


if __name__ == '__main__':
    sys.exit(main())
