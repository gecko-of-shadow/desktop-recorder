#!/usr/bin/env python3
"""Self-test for score.py.

The test writes copies of source.md with known damage to a temporary folder.
It runs score.py on each copy. It computes the expected numbers with its own
code and compares them with the score.py JSON output.
Standard library only. Exit code 0 when all tests pass, 1 when a test fails.
"""
import argparse, json, os, shutil, subprocess, sys, tempfile, unicodedata, re
REPO = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(REPO, "source.md")
argparse.ArgumentParser(description="Run damage tests on score.py with test/source.md. "
                        "Print PASS or FAIL for each test.").parse_args()
OUT = tempfile.mkdtemp(prefix="score_selftest_")
FAILS = []
lines = open(SRC, encoding="utf-8").read().splitlines()
body = {i: l[5:] for i, l in enumerate(lines, 1)}  # Text after "Lnnn ".

def norm(t): return re.sub(r"\s+", " ", unicodedata.normalize("NFC", t)).strip()
def lev(a, b):
    m = [[0]*(len(b)+1) for _ in range(len(a)+1)]
    for i in range(len(a)+1): m[i][0] = i
    for j in range(len(b)+1): m[0][j] = j
    for i in range(1, len(a)+1):
        for j in range(1, len(b)+1):
            m[i][j] = min(m[i-1][j]+1, m[i][j-1]+1, m[i-1][j-1]+(a[i-1]!=b[j-1]))
    return m[-1][-1]

def expected(got, keep=None):
    """got: {n: normalized best body} for caught lines. keep: tags to use."""
    src = {n: norm(b) for n, b in body.items() if keep is None or n in keep}
    got = {n: v for n, v in got.items() if n in src}
    cd = sum(lev(src[n], got[n]) for n in got); cl = sum(len(src[n]) for n in got)
    wd = sum(lev(src[n].split(" "), got[n].split(" ")) for n in got); wl = sum(len(src[n].split(" ")) for n in got)
    missed = sorted(set(src) - set(got))
    acd = cd + sum(len(src[n]) for n in missed); acl = sum(len(s) for s in src.values())
    awd = wd + sum(len(src[n].split(" ")) for n in missed); awl = sum(len(s.split(" ")) for s in src.values())
    errs = sorted(n for n in got if got[n] != src[n] and n != 290)
    return dict(caught=len(got), cer_caught=(cd/cl if cl else 0.0), wer_caught=(wd/wl if wl else 0.0), cer_all=acd/acl, wer_all=awd/awl, missed=missed, errs=errs)

def run(name, text, got, extra=None):
    p = os.path.join(OUT, name + ".md"); j = os.path.join(OUT, name + ".json")
    open(p, "w", encoding="utf-8", newline="\n").write(text)
    r = subprocess.run([sys.executable, os.path.join(REPO, "score.py"), SRC, p, "--json", j], capture_output=True, text=True, encoding="utf-8")
    if not os.path.exists(j): print(name, "NO JSON", r.stdout[-500:]); return
    res = json.load(open(j, encoding="utf-8")); os.remove(j)
    e = expected(got); s = res["summary"]; a = s["all_lines"]
    bad = []
    if s["lines_caught"] != e["caught"]: bad.append("caught %d!=%d" % (s["lines_caught"], e["caught"]))
    ez = expected(got, keep=set(body) - {290})
    for k in ("cer_caught", "wer_caught", "cer_all", "wer_all"):
        if abs(a[k] - e[k]) > 1e-12: bad.append("%s %.6f!=%.6f" % (k, a[k], e[k]))
        if abs(s["outside_zalgo"][k] - ez[k]) > 1e-12: bad.append("outside_zalgo %s %.6f!=%.6f" % (k, s["outside_zalgo"][k], ez[k]))
    for sname, sec in res["sections"].items():
        lo, hi = [int(x[1:]) for x in sec["range"].split("-")]
        es = expected(got, keep=set(range(lo, hi + 1)))
        nerr = sum(1 for n in got if lo <= n <= hi and got[n] != norm(body[n]))
        if sec["caught"] != es["caught"] or sec["lines_with_errors"] != nerr or abs(sec["cer_caught"] - es["cer_caught"]) > 1e-12:
            bad.append("section %s caught %d/%d errLines %d/%d" % (sname, sec["caught"], es["caught"], sec["lines_with_errors"], nerr))
    mt = [x["tag"] for x in res["lines"] if not x["caught"]]
    if mt != ["L%03d" % n for n in e["missed"]]: bad.append("missed %s != %s" % (res["missed_tags"], e["missed"]))
    got_errs = [int(x["tag"][1:]) for x in res["error_lines"] if x["tag"] != "L290"]
    if got_errs != e["errs"] or s["errors_outside_zalgo"] != len(e["errs"]): bad.append("errs %s (n=%d) != %s" % (got_errs, s["errors_outside_zalgo"], e["errs"]))
    secsum = sum(v["caught"] for v in res["sections"].values())
    if secsum != e["caught"]: bad.append("section caught sum %d" % secsum)
    if extra: bad += extra(res)
    if bad: FAILS.append(name)
    print(("PASS " if not bad else "FAIL ") + name + ("" if not bad else ": " + "; ".join(bad)))
    if bad: print("   missed:", res["missed_tags"], "parse:", {k: v for k, v in res["copy_parse"].items() if v})

ALL = {n: norm(b) for n, b in body.items()}
def tagged(n, b=None): return "L%03d %s" % (n, body[n] if b is None else b)

# 1 drop 10 lines (in two runs, one of them over a section border)
drop = {5, 6, 7, 58, 59, 60, 61, 62, 200, 300}
run("t01_drop10", "\n".join(tagged(n) for n in body if n not in drop) + "\n",
    {n: v for n, v in ALL.items() if n not in drop},
    lambda r: [] if r["missed_tags"] == ["L005-L007", "L058-L062", "L200", "L300"] and r["sections"]["prose"]["caught"] == 54 and r["sections"]["code"]["caught"] == 58 else ["ranges/sections %s %s" % (r["missed_tags"], r["sections"]["prose"]["caught"])])

# 2 swap 0/O in 5 lines
sw = {}
for n in (3, 63, 121, 171, 291):
    b = body[n]; i = min([k for k in (b.find("0"), b.find("O")) if k >= 0])
    sw[n] = b[:i] + ("O" if b[i] == "0" else "0") + b[i+1:]
g = dict(ALL); g.update({n: norm(v) for n, v in sw.items()})
run("t02_swap0O", "\n".join(tagged(n, sw.get(n)) for n in body) + "\n", g,
    lambda r: [] if r["summary"]["all_lines"]["char_errors_caught"] == 5 else ["char errors %d" % r["summary"]["all_lines"]["char_errors_caught"]])

# 3 join two lines (L010+L011), and a table pair (L205+L206)
out = []
for n in body:
    if n in (11, 206): continue
    if n in (10, 205): out.append(tagged(n) + " " + tagged(n + 1))
    else: out.append(tagged(n))
run("t03_join", "\n".join(out) + "\n", ALL)

# 4 split one line in two (L020 and code L070)
out = []
for n in body:
    t = tagged(n)
    if n in (20, 70):
        k = t.rfind(" ", 0, len(t)//2 + 5); out += [t[:k], t[k+1:]]
    else: out.append(t)
run("t04_split", "\n".join(out) + "\n", ALL)

# 5 wrap all in a fence
run("t05_fence", "```markdown\n" + "\n".join(tagged(n) for n in body) + "\n```\n", ALL)

# 6 bullets
run("t06_bullets", "\n".join("- " + tagged(n) for n in body) + "\n", ALL)

# 7 repeat tags: one good + one bad version; bad first for some, good first for others
out = []
for n in body:
    if n in (30, 150, 230): out += [tagged(n, "XX" + body[n]), tagged(n)]
    elif n in (31, 151): out += [tagged(n), tagged(n, body[n][:-3])]
    else: out.append(tagged(n))
run("t07_repeat", "\n".join(out) + "\n", ALL,
    lambda r: [] if r["repeated_tags"] == {"L030": 2, "L031": 2, "L150": 2, "L151": 2, "L230": 2} else ["repeats %s" % r["repeated_tags"]])

# 8 change only L290 (strip marks)
plain = "".join(c for c in unicodedata.normalize("NFD", body[290]) if not unicodedata.combining(c))
g = dict(ALL); g[290] = norm(plain)
run("t08_zalgo", "\n".join(tagged(n, plain if n == 290 else None) for n in body) + "\n", g,
    lambda r: [] if r["summary"]["errors_outside_zalgo"] == 0 and r["zalgo"]["base_char_dist"] == 0 and r["zalgo"]["combining_marks_copy"] == 0 and not r["zalgo"]["exact"] else ["zalgo %s" % r["zalgo"]])

# 9 drop L290 only
run("t09_zalgo_missing", "\n".join(tagged(n) for n in body if n != 290) + "\n", {n: v for n, v in ALL.items() if n != 290})

# 10 CRLF + BOM + numbered list + bold tags + heading
out = ["%d. **L%03d** %s" % (n, n, body[n]) if n % 3 == 0 else ("## " + tagged(n) if n % 3 == 1 else "> " + tagged(n)) for n in body]
run("t10_styles", "\ufeff" + "\r\n".join(out) + "\r\n", ALL)

# 11 markdown table of the table section with header and separator rows, inside the document
out = []
for n in body:
    if n == 201: out += ["", "| Tag | n | word | price |", "|---|---|---|---|"]
    out.append(("| " + tagged(n)) if 201 <= n <= 220 else tagged(n))
    if n == 220: out.append("")
run("t11_mdtable", "\n".join(out) + "\n", ALL)

# 12 AI notes at the end and an intro paragraph
run("t12_notes", "Here is the transcript:\n\n" + "\n".join(tagged(n) for n in body) + "\n\nNote: some frames were blurry.\nL290 was hard.\n", ALL)

# 13 tag with colon, and no space after the tag (L050)
out = [("L%03d: %s" % (n, body[n])) for n in body]
run("t13_colon", "\n".join(out) + "\n", ALL)

# 14 each line split at every space of the lookalike section (continuation lines that look like damaged tags)
out = []
for n in body:
    if 171 <= n <= 200: out += tagged(n).split(" ")
    else: out.append(tagged(n))
run("t14_lookalike_wrap", "\n".join(out) + "\n", ALL)

# 15 reverse order
run("t15_reverse", "\n".join(tagged(n) for n in reversed(list(body))) + "\n", ALL)

# 16 empty copy
run("t16_empty", "", {})

# 17 brackets and parentheses around the tag
run("t17_brackets", "\n".join(("[L%03d] %s" if n % 2 else "(L%03d) %s") % (n, body[n]) for n in body) + "\n", ALL)
# 18 damaged tags: the line is lost, the line before stays clean
dm = {41: "LO41", 141: "L14I", 205: "L2O5"}
run("t18_damaged", "\n".join((dm[n] + " " + body[n]) if n in dm else tagged(n) for n in body) + "\n",
    {n: v for n, v in ALL.items() if n not in dm},
    lambda r: [] if sorted(r["copy_parse"]["unreadable_tags"]) == sorted(dm.values()) else ["unreadable %s" % r["copy_parse"]["unreadable_tags"]])
# 19 copy in NFD
run("t19_nfd", unicodedata.normalize("NFD", "\n".join(tagged(n) for n in body)) + "\n", ALL)
# 20 tag glued to text (no space)
run("t20_glued", "\n".join(("L050" + body[50]) if n == 50 else tagged(n) for n in body) + "\n",
    ALL)  # after fix: the glued tag is read as L050
# 21 KNOWN LIMIT: L290 is missing. An AI note after a blank line starts with "L290".
# The script cannot tell the note from a copy line. It counts the note as L290.
g = dict(ALL); g[290] = "could not be read."
run("t21_note_names_tag_KNOWN", "\n".join(tagged(n) for n in body if n != 290) + "\n\nL290 could not be read.\n", g)
# 22 Markdown table header and rule row with no blank line before them.
# The script removes the rule row. KNOWN LIMIT: the header row has no tag,
# so the script adds it to L200 as a continuation line.
out=[]
for n in body:
    if n == 201: out += ["| Tag | n | word | price |", "|---|:--:|---|---|"]
    out.append(("| " + tagged(n)) if 201 <= n <= 220 else tagged(n))
g = dict(ALL); g[200] = norm(body[200] + " | Tag | n | word | price |")
run("t22_mdtable_noblank_KNOWN", "\n".join(out) + "\n", g,
    lambda r: [] if r["copy_parse"]["fence_lines"] == 1 else ["rule rows removed %d" % r["copy_parse"]["fence_lines"]])
# 23 tab and NBSP after tag
run("t23_ws", "\n".join(("L%03d\t%s" if n % 2 else "L%03d\u00a0%s") % (n, body[n]) for n in body) + "\n", ALL)
# 24 a join where the second tag has a colon
out=[]
for n in body:
    if n == 101: continue
    out.append(tagged(n) + " L101: " + body[101] if n == 100 else tagged(n))
run("t24_join_colon", "\n".join(out) + "\n", ALL)
# 25 bracket tags with a join in the line
out=[]
for n in body:
    if n == 11: continue
    out.append("[L010] " + body[10] + " [L011] " + body[11] if n == 10 else "[L%03d] %s" % (n, body[n]))
run("t25_bracket_join", "\n".join(out) + "\n", ALL)
# 26 every line split at every space (worst wrap), whole file
out=[]
for n in body: out += tagged(n).split()
run("t26_wrap_all", "\n".join(out) + "\n", ALL)

# 27 hash and Base64 lines wrapped at column 40 (continuation starts inside a token)
out=[]
for n in body:
    t = tagged(n)
    if 121 <= n <= 170: out += [t[i:i+40] for i in range(0, len(t), 40)]
    else: out.append(t)
g = dict(ALL)
for n in range(121, 171):
    t = tagged(n); g[n] = norm(" ".join(t[i:i+40] for i in range(0, len(t), 40))[5:])
run("t27_wrap_col40", "\n".join(out) + "\n", g,
    lambda r: [] if not r["repeated_tags"] and not r["copy_parse"]["unreadable_tags"] and not r["copy_parse"]["splits_in_line"] else ["parse %s %s" % (r["repeated_tags"], r["copy_parse"])])

# 28 exit code 0 and an ERROR line when the JSON path cannot be written
p = os.path.join(OUT, "ok.md"); open(p, "w", encoding="utf-8").write("\n".join(lines) + "\n")
r = subprocess.run([sys.executable, os.path.join(REPO, "score.py"), SRC, p, "--json", os.path.join(OUT, "no", "such", "dir", "x.json")],
                   capture_output=True, text=True, encoding="utf-8")
ok = r.returncode == 0 and "ERROR:" in r.stdout and "Traceback" not in r.stderr
if not ok: FAILS.append("t28_json_unwritable")
print(("PASS" if ok else "FAIL") + " t28_json_unwritable" + ("" if ok else ": rc=%d %s" % (r.returncode, r.stderr.strip()[-200:])))

shutil.rmtree(OUT, ignore_errors=True)
print("%d tests failed: %s" % (len(FAILS), ", ".join(FAILS)) if FAILS else "All tests passed.")
sys.exit(1 if FAILS else 0)
