#!/usr/bin/env python3
"""Score an AI copy of test/source.md against the source.

The AI reads the frames of a test video and writes a copy of the text.
This script compares the copy with the source, line by line.
It matches lines by tag (L001 to L300). It does not use line positions.

Standard library only.
"""

import argparse
import difflib
import json
import re
import sys
import unicodedata

ZALGO_TAG = 290

# Section map from the shared spec (HANDOFF.md section 6.1 and the task spec).
SECTIONS = [
    ("prose", 1, 60),
    ("code", 61, 120),
    ("sha256", 121, 140),
    ("base64", 141, 170),
    ("lookalike", 171, 200),
    ("table", 201, 220),
    ("accents_emoji", 221, 240),
    ("colored", 241, 270),
    ("mixed", 271, 289),
    ("zalgo", 290, 290),
    ("mixed_tail", 291, 300),
]

HELP_EPILOG = """\
How the script reads the copy:
  - A tag is "L" plus 3 digits, for example L007.
  - The script removes Markdown fence lines (``` or ~~~) that hold no other text.
    It also removes Markdown table rule rows, for example "|---|---|".
  - Before a tag at the start of a line, the script removes spaces, bullets
    (- * +), quote marks (>), "#", "|", "**", backticks and list numbers
    ("12."). It accepts a tag in brackets: "[L007]" or "(L007)". It accepts
    a tag with no space before a letter: "L007The". It does not change the
    text after the tag.
  - Text from one tag to the next tag belongs to the first tag. The script
    joins continuation lines with one space.
  - A tag in the middle of a line starts a new line (two source lines that
    the AI joined). The script splits only on a tag that is in the source and
    that is not a word in the source text of the open tag.
  - A blank line closes the open tag. Text after a blank line and before the
    next tag is "untagged text". The script counts it and does not score it.
    This stops AI notes at the end of the copy from counting as errors.
  - A line that starts with a damaged tag (for example "LO41" or "L4I1") also
    closes the open tag. The script reports it as an unreadable tag.
  - If a tag occurs more than once, the script uses the copy with the lowest
    character distance. It reports the repeat count.

Normalization (source and copy): Unicode NFC, trim, each run of white space
becomes one space. The script does not change case or punctuation.

Metrics:
  CER = sum of character Levenshtein distances / sum of source line lengths.
  WER = sum of word Levenshtein distances / sum of source word counts.
  Words are split on spaces. Lengths are in Unicode code points.
  "caught" variants use only the lines that are in the copy.
  "all" variants count a missing line as fully wrong (distance = its length).
  Rates are a sum over lines, not a mean of line rates.
  "errors outside Zalgo" = number of caught lines, except L290, that are not
  exactly equal to the source after normalization.

Exit code: always 0, also when the copy has errors or a file cannot be read.
Then the script prints a line that starts with "ERROR:".
"""

TAG_AT_START = re.compile(
    r"^[\s>*+\-#|`\"'\[\(]*"  # bullets, quotes, table bars, bold, backticks
    r"(?:\d{1,4}[.)]\s+)?"  # list numbers such as "12. "
    r"[*_`]*L(\d{3})(?!\d)[*_`]*[\])]?"  # the tag, maybe in bold, code or brackets
    r"(?:[:.]?(?:\s|$)|(?=[^\W\d_]))"  # optional colon, then space or end, or a letter
)
TAG_IN_LINE = re.compile(r"(?:(?<=\s)|(?<=^))[\[(]?L(\d{3})[\])]?(?=\s|$|[:|])")
# A damaged tag: L or a look-alike, then 3 digits or look-alike characters.
DAMAGED_TAG = re.compile(r"^[\s>*+\-#|`\"']*[LI1l|][0-9OoIlSZBDQ]{3}(?=\s|$|[:|])")
FENCE = re.compile(r"^\s*(```|~~~)[\w+-]*\s*$")
# A Markdown table rule row, for example "|---|:--:|". It holds no source text.
TABLE_RULE = re.compile(r"^\s*\|?(\s*:?-+:?\s*\|)+(\s*:?-+:?\s*)?$")
TAGLIKE = re.compile(r"(?<!\S)L\d{3}(?=\s|$|[:|])")
WS = re.compile(r"\s+")


class InputError(Exception):
    """A file cannot be read."""


def normalize(text):
    """Return the text in NFC, trimmed, with each white-space run as one space."""
    return WS.sub(" ", unicodedata.normalize("NFC", text)).strip()


def read_text(path):
    """Read a text file. Accept UTF-8 (with or without BOM) and UTF-16 with BOM."""
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except OSError as err:
        raise InputError("cannot read %s (%s)." % (path, err))
    if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16")
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError as err:
        raise InputError("%s is not UTF-8 or UTF-16 text (%s)." % (path, err))


def levenshtein(a, b):
    """Return the edit distance between two sequences. Use two rows."""
    if a == b:
        return 0
    if len(a) < len(b):
        a, b = b, a
    if not b:
        return len(a)
    previous = list(range(len(b) + 1))
    for i, item_a in enumerate(a, 1):
        current = [i]
        for j, item_b in enumerate(b, 1):
            cost = 0 if item_a == item_b else 1
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost))
        previous = current
    return previous[-1]


def words(text):
    return text.split(" ") if text else []


def section_of(num):
    for name, low, high in SECTIONS:
        if low <= num <= high:
            return name
    return "other"


def compress_ranges(nums):
    """Return a list such as ["L041-L044", "L120"] from sorted numbers."""
    out = []
    nums = sorted(nums)
    i = 0
    while i < len(nums):
        j = i
        while j + 1 < len(nums) and nums[j + 1] == nums[j] + 1:
            j += 1
        if j == i:
            out.append("L%03d" % nums[i])
        else:
            out.append("L%03d-L%03d" % (nums[i], nums[j]))
        i = j + 1
    return out


def short_diff(src, got, width=100):
    """Return a short inline diff: {-removed+added} with some context."""
    matcher = difflib.SequenceMatcher(None, src, got, autojunk=False)
    parts = []
    for op, i1, i2, j1, j2 in matcher.get_opcodes():
        if op == "equal":
            seg = src[i1:i2]
            if len(seg) > 12:
                seg = seg[:5] + "…" + seg[-5:]
            parts.append(seg)
        else:
            parts.append("{-%s+%s}" % (src[i1:i2], got[j1:j2]))
    text = "".join(parts)
    if len(text) > width:
        text = text[: width - 3] + "..."
    return text


def parse_source(text):
    """Return {tag number: normalized body} and a list of warnings."""
    lines = {}
    warnings = []
    for index, raw in enumerate(text.splitlines(), 1):
        if not raw.strip():
            warnings.append("source line %d is blank" % index)
            continue
        match = re.match(r"^L(\d{3})(?:\s|$)", raw)
        if not match:
            warnings.append("source line %d has no tag: %r" % (index, raw[:40]))
            continue
        num = int(match.group(1))
        if num in lines:
            warnings.append("source tag L%03d occurs more than once" % num)
            continue
        lines[num] = normalize(raw[match.end():])
    for num, body in lines.items():
        for token in TAGLIKE.findall(body):
            warnings.append("source L%03d has a tag-like word %r in its text" % (num, token))
    return lines, warnings


def parse_copy(text, source):
    """Split the copy into segments by tag.

    Return (candidates, info). candidates maps a tag number to a list of
    normalized bodies. info holds counts of fences, untagged text and
    unreadable tags.
    """
    candidates = {}
    info = {
        "fence_lines": 0,
        "untagged_lines": 0,
        "untagged_samples": [],
        "unreadable_tags": [],
        "unknown_tags": [],
        "splits_in_line": 0,
    }
    open_tag = None
    open_parts = []

    def body_words(num):
        """Return the words of a source line, without a trailing ":" or "|"."""
        return {w.rstrip(":|") for w in words(source.get(num, ""))}

    def close():
        nonlocal open_tag, open_parts
        if open_tag is not None:
            candidates.setdefault(open_tag, []).append(normalize(" ".join(open_parts)))
        open_tag = None
        open_parts = []

    def add_untagged(line):
        info["untagged_lines"] += 1
        if len(info["untagged_samples"]) < 5:
            info["untagged_samples"].append(line.strip()[:60])

    def feed(rest):
        """Add text to the open tag. Split on a tag in the middle of the text."""
        nonlocal open_tag, open_parts
        pos = 0
        for match in TAG_IN_LINE.finditer(rest):
            num = int(match.group(1))
            if num not in source or num == open_tag:
                continue
            if open_tag is not None and ("L%03d" % num) in body_words(open_tag):
                continue
            before = rest[pos:match.start()]
            if open_tag is not None:
                open_parts.append(before)
            elif before.strip():
                add_untagged(before)
            close()
            info["splits_in_line"] += 1
            open_tag = num
            pos = match.end()
            if rest[pos:pos + 1] == ":":
                pos += 1
        tail = rest[pos:]
        if open_tag is not None:
            open_parts.append(tail)
        elif tail.strip():
            add_untagged(tail)

    for line in text.splitlines():
        if FENCE.match(line) or TABLE_RULE.match(line):
            info["fence_lines"] += 1
            continue
        if not line.strip():
            close()
            continue
        match = TAG_AT_START.match(line)
        if match:
            close()
            num = int(match.group(1))
            if num not in source:
                info["unknown_tags"].append("L%03d" % num)
                continue  # Text after an unknown tag is not scored.
            open_tag = num
            feed(line[match.end():])
            continue
        damaged = DAMAGED_TAG.match(line)
        # A damaged tag must hold a letter after the first character.
        # This stops a number such as "1000" from counting as a damaged tag.
        # A word of the source text of the open tag is not a damaged tag.
        if (damaged and re.search(r"[A-Za-z]", damaged.group(0).strip()[1:])
                and not (open_tag is not None and line.split()[0].rstrip(":|") in body_words(open_tag))):
            close()
            info["unreadable_tags"].append(damaged.group(0).strip())
            continue
        if open_tag is None:
            add_untagged(line)
            continue
        feed(line)
    close()
    return candidates, info


def rate(num, den):
    return (num / den) if den else 0.0


def score(source, candidates):
    """Compare each source line with its best candidate. Return the result dict."""
    per_line = {}
    for num in sorted(source):
        src = source[num]
        src_words = words(src)
        entry = {
            "tag": "L%03d" % num,
            "section": section_of(num),
            "src_chars": len(src),
            "src_words": len(src_words),
            "caught": num in candidates,
            "copies": len(candidates.get(num, [])),
        }
        if entry["caught"]:
            best = None
            for body in candidates[num]:
                dist = levenshtein(src, body)
                if best is None or dist < best[0]:
                    best = (dist, body)
                if dist == 0:
                    break
            dist, body = best
            entry["char_dist"] = dist
            entry["word_dist"] = levenshtein(src_words, words(body))
            entry["cer"] = rate(dist, len(src))
            entry["exact"] = dist == 0
            entry["copy"] = body
            entry["diff"] = "" if dist == 0 else short_diff(src, body)
        else:
            entry["char_dist"] = len(src)
            entry["word_dist"] = len(src_words)
            entry["cer"] = 1.0
            entry["exact"] = False
        per_line[num] = entry

    def totals(nums):
        nums = list(nums)
        caught = [n for n in nums if per_line[n]["caught"]]
        c_dist = sum(per_line[n]["char_dist"] for n in caught)
        w_dist = sum(per_line[n]["word_dist"] for n in caught)
        c_len = sum(per_line[n]["src_chars"] for n in caught)
        w_len = sum(per_line[n]["src_words"] for n in caught)
        a_cdist = sum(per_line[n]["char_dist"] for n in nums)
        a_wdist = sum(per_line[n]["word_dist"] for n in nums)
        a_clen = sum(per_line[n]["src_chars"] for n in nums)
        a_wlen = sum(per_line[n]["src_words"] for n in nums)
        return {
            "lines": len(nums),
            "caught": len(caught),
            "caught_pct": 100.0 * rate(len(caught), len(nums)),
            "lines_with_errors": sum(1 for n in caught if not per_line[n]["exact"]),
            "char_errors_caught": c_dist,
            "word_errors_caught": w_dist,
            "cer_caught": rate(c_dist, c_len),
            "wer_caught": rate(w_dist, w_len),
            "cer_all": rate(a_cdist, a_clen),
            "wer_all": rate(a_wdist, a_wlen),
        }

    all_nums = sorted(source)
    no_zalgo = [n for n in all_nums if n != ZALGO_TAG]
    sections = {}
    for name, low, high in SECTIONS + [("other", 0, 0)]:
        nums = [n for n in all_nums if section_of(n) == name]
        if nums:
            sections[name] = dict(totals(nums), range="L%03d-L%03d" % (low, high) if name != "other" else "")

    missed = [n for n in all_nums if not per_line[n]["caught"]]
    errors_outside = [n for n in no_zalgo if per_line[n]["caught"] and not per_line[n]["exact"]]
    repeats = {per_line[n]["tag"]: per_line[n]["copies"] for n in all_nums if per_line[n]["copies"] > 1}

    zalgo = None
    if ZALGO_TAG in per_line:
        z = per_line[ZALGO_TAG]
        zalgo = {k: z.get(k) for k in ("caught", "copies", "char_dist", "word_dist", "cer", "exact", "diff")}
        # Compare the base characters in NFD, without the combining marks.
        nfd_src = unicodedata.normalize("NFD", source[ZALGO_TAG])
        base_src = "".join(ch for ch in nfd_src if not unicodedata.combining(ch))
        zalgo["base_chars_src"] = len(base_src)
        zalgo["combining_marks_src"] = len(nfd_src) - len(base_src)
        if z["caught"]:
            nfd_copy = unicodedata.normalize("NFD", z["copy"])
            base_copy = "".join(ch for ch in nfd_copy if not unicodedata.combining(ch))
            zalgo["base_char_dist"] = levenshtein(base_src, base_copy)
            zalgo["combining_marks_copy"] = len(nfd_copy) - len(base_copy)

    return {
        "summary": {
            "source_lines": len(all_nums),
            "lines_caught": len(all_nums) - len(missed),
            "lines_caught_pct": 100.0 * rate(len(all_nums) - len(missed), len(all_nums)),
            "errors_outside_zalgo": len(errors_outside),
            "all_lines": totals(all_nums),
            "outside_zalgo": totals(no_zalgo),
        },
        "missed_tags": compress_ranges(missed),
        "missed_count": len(missed),
        "repeated_tags": repeats,
        "sections": sections,
        "zalgo": zalgo,
        "error_lines": [
            {k: per_line[n][k] for k in ("tag", "section", "cer", "char_dist", "word_dist", "copies", "diff")}
            for n in all_nums
            if per_line[n]["caught"] and not per_line[n]["exact"]
        ],
        "lines": [per_line[n] for n in all_nums],
    }


def pct(value):
    return "%.2f%%" % (100.0 * value)


def print_report(result, info, warnings, show):
    s = result["summary"]
    a = s["all_lines"]
    o = s["outside_zalgo"]
    print("=== SCORE ===")
    print("Lines caught          : %d / %d (%.2f%%)" % (s["lines_caught"], s["source_lines"], s["lines_caught_pct"]))
    print("Errors outside Zalgo  : %d lines" % s["errors_outside_zalgo"])
    print()
    print("                       all lines      outside Zalgo (no L290)")
    print("CER, caught lines   :  %-14s %s" % (pct(a["cer_caught"]), pct(o["cer_caught"])))
    print("CER, missed = wrong :  %-14s %s" % (pct(a["cer_all"]), pct(o["cer_all"])))
    print("WER, caught lines   :  %-14s %s" % (pct(a["wer_caught"]), pct(o["wer_caught"])))
    print("WER, missed = wrong :  %-14s %s" % (pct(a["wer_all"]), pct(o["wer_all"])))
    print("Char errors (caught):  %-14d %d" % (a["char_errors_caught"], o["char_errors_caught"]))
    print("Word errors (caught):  %-14d %d" % (a["word_errors_caught"], o["word_errors_caught"]))
    print()
    print("Missed tags (%d): %s" % (result["missed_count"], ", ".join(result["missed_tags"]) or "none"))
    reps = result["repeated_tags"]
    if reps:
        listed = ", ".join("%s x%d" % (k, v) for k, v in list(reps.items())[:20])
        more = " (+%d more)" % (len(reps) - 20) if len(reps) > 20 else ""
        print("Repeated tags (%d, best copy used): %s%s" % (len(reps), listed, more))
    else:
        print("Repeated tags: none")
    print()
    print("Per section:")
    print("  %-14s %-10s %7s %8s %7s %9s %9s" % ("section", "range", "caught", "errLines", "chErr", "CER", "WER"))
    for name, sec in result["sections"].items():
        print("  %-14s %-10s %3d/%-3d %8d %7d %9s %9s" % (
            name, sec["range"], sec["caught"], sec["lines"], sec["lines_with_errors"],
            sec["char_errors_caught"], pct(sec["cer_caught"]), pct(sec["wer_caught"])))
    print()
    z = result["zalgo"]
    if z is None:
        print("Zalgo line L%03d: not in source" % ZALGO_TAG)
    elif not z["caught"]:
        print("Zalgo line L%03d: MISSED" % ZALGO_TAG)
    else:
        print("Zalgo line L%03d: exact=%s CER=%s char_dist=%d base_char_dist=%d marks src=%d copy=%d copies=%d" % (
            ZALGO_TAG, z["exact"], pct(z["cer"]), z["char_dist"], z["base_char_dist"],
            z["combining_marks_src"], z["combining_marks_copy"], z["copies"]))
    print()
    errs = result["error_lines"]
    limit = len(errs) if show < 0 else min(show, len(errs))
    print("Lines with errors (%d, showing %d). Diff format: {-source+copy}, … = equal text skipped" % (len(errs), limit))
    for e in errs[:limit]:
        print("  %s %-13s CER %7s  %s" % (e["tag"], e["section"], pct(e["cer"]), e["diff"]))
    print()
    print("Copy parse: %d fence and table rule lines removed, %d in-line splits, %d untagged lines ignored, "
          "%d unreadable tags, %d unknown tags" % (
              info["fence_lines"], info["splits_in_line"], info["untagged_lines"],
              len(info["unreadable_tags"]), len(info["unknown_tags"])))
    if info["unreadable_tags"]:
        print("  Unreadable tags: %s" % ", ".join(info["unreadable_tags"][:20]))
    if info["unknown_tags"]:
        print("  Unknown tags: %s" % ", ".join(info["unknown_tags"][:20]))
    if info["untagged_samples"]:
        print("  Untagged samples: %s" % " | ".join(info["untagged_samples"]))
    if warnings:
        print()
        print("Source warnings (%d):" % len(warnings))
        for w in warnings[:20]:
            print("  " + w)


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(
        description="Compare an AI copy with test/source.md. Match lines by tag (L001-L300). "
        "Print lines caught, character error rate, word error rate, missed tags, "
        "lines with errors, errors per section and the Zalgo line (L290) result.",
        epilog=HELP_EPILOG,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("source", metavar="SOURCE", help="Source file (test/source.md).")
    parser.add_argument("copy", metavar="COPY", help="Text that the AI wrote after it read the video frames.")
    parser.add_argument("--json", metavar="PATH", help="Write all numbers and per-line data to this JSON file (UTF-8).")
    parser.add_argument("--show", metavar="N", type=int, default=30,
                        help="Show N lines with errors in the report. Use -1 to show all. Default: 30.")
    args = parser.parse_args(argv)

    try:
        source_text = read_text(args.source)
        copy_text = read_text(args.copy)
    except InputError as err:
        print("ERROR: %s" % err)
        return 0
    source, warnings = parse_source(source_text)
    if not source:
        print("ERROR: the source has no tagged lines.")
        return 0
    candidates, info = parse_copy(copy_text, source)
    result = score(source, candidates)
    result["copy_parse"] = info
    result["source_warnings"] = warnings
    result["files"] = {"source": args.source, "copy": args.copy}
    print_report(result, info, warnings, args.show)
    if args.json:
        print()
        try:
            with open(args.json, "w", encoding="utf-8", newline="\n") as handle:
                json.dump(result, handle, ensure_ascii=False, indent=2)
        except OSError as err:
            print("ERROR: cannot write %s (%s)." % (args.json, err))
            return 0
        print("JSON written: %s" % args.json)
    return 0


if __name__ == "__main__":
    sys.exit(main())
