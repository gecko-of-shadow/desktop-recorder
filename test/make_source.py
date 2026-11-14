#!/usr/bin/env python3
"""Make the source text for the AI read test.

This script writes test/source.md. The file has exactly 300 lines.
Each line starts with a tag "L001 " to "L300 ".
The output is always the same. The script uses a fixed seed for the Zalgo line only.
"""

import argparse
import base64
import hashlib
import random
import sys
import unicodedata
from pathlib import Path

MAX_LEN = 90
ZALGO_TAG = 290
ZALGO_SEED = 290

# L001-L060: plain English prose.
PROSE = """
The morning train left the station four minutes late, and nobody seemed to mind.
Marta keeps a jar of buttons on her desk, sorted by color and then by size.
Our office printer jams every Tuesday, which no engineer has ever explained.
A heron stood in the shallow pond, ignoring the joggers on the gravel path.
The recipe asks for two cups of flour, a pinch of salt, and some patience.
Last winter the river froze solid for nine days, a record in this valley.
He wrote the grant proposal in one night and slept through the next morning.
Bright yellow kayaks drifted past the lighthouse just after the fog lifted.
The museum closed its east wing for repairs after a pipe burst in March.
Please return library books to the slot beside the main entrance door.
Our neighbor grows tomatoes on the balcony and gives half of them away.
The quarterly report shows revenue up, but shipping costs rose even faster.
Thunder rolled across the hills while the dog hid under the kitchen table.
She tuned the old piano by ear, one string at a time, over three evenings.
The bakery on Elm Street sells out of rye bread before nine o'clock.
Volunteers planted forty oak saplings along the edge of the school field.
My grandfather repaired watches in a tiny shop that smelled of machine oil.
The committee postponed the vote because two members were stuck in traffic.
A cold draft came through the attic window that never quite closed.
Every spring the swallows return to nest under the barn roof.
The hiking trail climbs steeply for two miles before it reaches the ridge.
He forgot his umbrella at the station and walked home through the drizzle.
Our team ships small updates often, because large releases hide bugs.
The violinist paused mid-phrase when a phone rang in the third row.
Fresh snow covered the parking lot, erasing every painted line.
The cat learned to open the pantry door by pulling on the towel.
Customs officers checked each crate twice before the ship could unload.
A good map shows what to ignore as clearly as what to notice.
The night shift nurse wrote neat notes in green ink for the next team.
Satellite images revealed an old road buried under the desert sand.
Two cyclists argued politely about the fastest route to the harbor.
The town hall clock gains a minute every week, so locals ignore it.
Rain gauges at the airport recorded 41 millimeters in a single hour.
Her first novel sold poorly, but her second one won a regional prize.
The ferry crossing takes forty minutes when the wind is from the west.
Engineers replaced the bridge bearings without closing a single lane.
A stray kite tangled itself in the power lines above the market.
The orchestra rehearsed the final movement until the conductor smiled.
Bees ignored the plastic flowers and went straight for the clover.
Tomorrow's lecture covers soil chemistry, so bring the lab handbook.
The tailor measured my sleeves twice and still made them too short.
Wind turbines on the ridge turned slowly in the evening calm.
Nobody claimed the blue scarf left on the bench near the fountain.
The startup moved offices three times before it found a quiet street.
An old ledger in the archive listed wages paid in salt and grain.
The chess club meets on Thursdays, and newcomers always play first.
Fog horns sounded across the bay long before the sun came up.
The plumber found a wedding ring lodged deep inside the sink trap.
Students built a small weather station from spare parts and tape.
The orchard owner tracks frost dates in a notebook going back decades.
After the storm, the beach was covered in kelp and broken shells.
My laptop fan screams whenever a video call lasts more than an hour.
The translator kept a list of words that have no English equivalent.
A farmers' market opened in the empty lot behind the train depot.
The lighthouse keeper's log mentions a whale sighting every August.
Small typos in contracts can cost far more than the paper they sit on.
The goalkeeper saved a penalty and then slipped in the mud.
Archivists scanned ten thousand postcards before the funding ran out.
A quiet room, a clear task, and a deadline make most work easier.
The prose section ends here; code samples begin on the next line.
"""

# L061-L120: source code (C#, Python, JSON, shell). Indentation is kept.
CODE = r"""
// C#: clamp the frame rate before the encoder starts
public static int ClampFps(double fps) {
    if (fps < 0.2 || fps > 60) throw new ArgumentOutOfRangeException(nameof(fps));
    return (int)Math.Ceiling(fps);
}
var regions = new List<Rectangle> { new Rectangle(0, 0, 1920, 1080) };
Dictionary<string, int[]> map = new() { ["a"] = new[] { 1, 2, 3 } };
bool even = (width & 1) == 0 && (height & 1) == 0;
foreach (var r in regions.Where(x => x.Width > 0)) {
    Console.WriteLine($"Region {r.X},{r.Y} size {r.Width}x{r.Height}");
}
string args = string.Join(" ", parts) + " -pix_fmt yuv444p";
int flags = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
Func<int, int, int> gcd = null; gcd = (a, b) => b == 0 ? a : gcd(b, a % b);
if (list?.Count > 0 && list[0] is Rectangle { Width: > 2 } first) Use(first);
# Python: count unique frames by hash
def unique_frames(paths: list[str]) -> dict[str, int]:
    seen: dict[str, int] = {}
    for p in paths:
        h = hashlib.sha256(open(p, "rb").read()).hexdigest()[:16]
        seen[h] = seen.get(h, 0) + 1
    return {k: v for k, v in seen.items() if v >= 1}
matrix = [[i * j for j in range(4)] for i in range(3)]
print(f"{len(matrix)=} {matrix[2][3]=}")
mask = 0b1010 | 0b0101 ^ 0b1111 & ~0b0011
with open("out.txt", "w", encoding="utf-8") as fh: fh.write("ok\n")
top5 = lambda xs: sorted(xs, key=lambda t: (t[1], -t[0]))[:5]
assert {1, 2} <= {1, 2, 3} and [0] * 3 == [0, 0, 0]
class Frame(NamedTuple): idx: int; pts: float; digest: str
if not (0 < fps <= 60): raise ValueError(f"fps out of range: {fps}")
{
  "recorder": {
    "backend": "ddagrab",
    "encoder": "h264_nvenc",
    "fps": 10,
    "region": [120, 80, 1280, 720],
    "chroma": "yuv444p",
    "split_minutes": null,
    "hotkeys": { "select": "Ctrl+Shift+R", "last": "Ctrl+Shift+L" },
    "sounds": true
  }
}
# shell: probe the stream and count frames
ffprobe -v error -count_frames -show_entries stream=nb_read_frames in.mp4
for f in frames/*.png; do sha256sum "$f"; done | sort | uniq -c -w 64 | head
grep -E '^L[0-9]{3} ' source.md | wc -l
find . -name '*.mkv' -size +10M -printf '%s %p\n' | sort -rn > big.txt
awk -F'|' 'NR > 1 { sum += $3 } END { print sum }' table.txt
[ -d out ] || mkdir -p out && echo "ready" >> log.txt 2>&1
export PATH="$HOME/bin:$PATH"; command -v ffmpeg >/dev/null || exit 1
tar -czf backup-$(date +%Y%m%d).tgz ./test ./tests
curl -fsS "https://example.com/api?id=42&mode=raw" | jq '.items[] | {id, name}'
sed -n '121,140p' source.md | cut -d' ' -f2-
echo $(( (1920 * 1080 * 3) >> 1 )) bytes per 4:2:0 frame
diff <(sort a.txt) <(sort b.txt) || true
ls *.mp4 | xargs -n 1 -P 4 -I{} ffprobe -v error -show_format {}
if [[ $fps -gt 30 ]]; then echo "high rate"; else echo "low rate"; fi
ffmpeg -i in.mkv -vf mpdecimate -fps_mode vfr -c:v h264_nvenc out.mp4
case "$1" in start|stop) run "$1" ;; *) echo "usage" >&2 ;; esac
printf '%05d\n' {1..3} | paste -sd, -
"""

# L121-L140: SHA-256 words. Each word is 10 characters or fewer.
SHA_WORDS = [
    "apple", "Zebra", "recorder", "frame042", "O0O0",
    "Il1l", "café", "naïve", "ddagrab", "nvenc",
    "42", "yuv444p", "Region", "scroll", "kitten",
    "mpdecimate", "Tuesday", "x", "rnm", "ŁÓDŹ",
]

# L141-L170: Base64 sentences. "{t}" becomes the line tag.
B64_TEMPLATES = [
    "This is line {t}.",
    "Line {t} says the capture worked.",
    "Decode {t} and compare it with the source.",
    "{t}: the quick brown fox jumps over the lazy dog.",
    "Frame count for {t} is forty two.",
    "Tag {t} hides inside this Base64 string.",
    "{t} was encoded on a quiet Friday.",
    "Check {t} twice; the letters must match.",
    "Seven blue boxes sit beside {t}.",
    "{t} reads: zero errors please.",
    "The answer for {t} is 1729.",
    "Hello from {t}, the reader found me.",
    "{t} contains digits 0123456789.",
    "If you can read {t}, the scroll was slow enough.",
    "Mixed Case Test For {t} Here.",
    "{t} ends with a question mark?",
    "Rivers bend; {t} does not.",
    "Do not skip {t} during the test.",
    "{t}: AI readers must copy this exactly.",
    "Pack my box with five dozen jugs ({t}).",
    "{t} -- dashes, commas, and dots.",
    "Nothing to see at {t}, move along.",
    "{t} sits between two other Base64 lines.",
    "Coffee break scheduled after {t}.",
    "Paris, Oslo, Lima, Rome: {t}.",
    "Even sizes only, says {t}.",
    "{t} uses plain ASCII text.",
    "Sixty bytes is the limit near {t}.",
    "Almost done with Base64 at {t}.",
    "Final Base64 sample is {t}.",
]

# L171-L200: look-alike traps.
TRAPS = r"""
Order ID: O0O-0o0-OO0 (letters O and o, digits 0).
Serial Il1|-1lI|-|1Il is not a typo.
The modern barn has a corner room, not a comer room.
clear vs dear: the clerk said "clear", not "dear".
vvide vs wide: write "wide" with one w, not "vvide".
Codes 5S5S, S5S5, 8B8B, B8B8, 2Z2Z, Z2Z2 in that order.
Sample code: l0Ol1I|oO0 (mixed letters and digits).
rnicro vs micro: the label says "micro", the typo says "rnicro".
Batch B-8058-SB2Z shipped to bay 5B, slot 8S.
burn, bum, barn, bam, corn, com: six different words.
Path C:\Il1\0O0\rn_m\cl_d.txt
Hex 0x00B8 is valid; 0xOOB8 with letters O is not.
Flight IL1O1 departs gate I1 at 10:01.
"cloud" and "doud" differ; "clip" and "dip" differ too.
vvorld, world, wvorld: only the second is a real word.
Variable names: lI1, Il1, l1I, I1l, 1lI, 1Il.
Model ZZ22-Z2Z2 and model 2Z2Z-22ZZ are different parts.
S5 S55 SS5 5SS 55S: five tokens, check each one.
Room 8B8 is next to room B88 and room 88B.
The rn-m test: "fern" vs "fem", "warn" vs "wam", "turn" vs "tum".
Ticket #O1l-0Il-o1L waits in queue |A|B|C|.
"modem" vs "rnodern": the second one is wrong on purpose.
vv/w test: savvy, wwvv, vwvw, wvwv.
clam vs dam, clock vs dock, close vs dose.
Account ref 0O8B-5S2Z-Il1l-rnm0 (test data only).
Pipe chain: a|l|1|I|b has four pipes.
Year 2O25 uses a letter O; year 2025 uses a zero.
BOOK8, B00K8, 8OOKB, 8OOK8: four different strings.
Ill1t ill-lit lilies lie still in Illinois.
End of traps: O0Oo0 Il1| rn/m cl/d vv/w 5S 8B 2Z.
"""

# L201-L220: table rows (count, word, price).
TABLE = [
    (17, "apple", "3.50"), (204, "birch", "0.75"), (9, "cobalt", "12.00"),
    (3310, "delta", "1.05"), (46, "ember", "7.25"), (0, "fjord", "99.99"),
    (812, "garnet", "4.40"), (57, "harbor", "0.01"), (1024, "iris", "16.00"),
    (333, "juniper", "2.22"), (76, "kelp", "8.10"), (5, "lantern", "41.30"),
    (650, "mango", "0.99"), (28, "nickel", "3.14"), (9001, "onyx", "0.50"),
    (12, "pepper", "6.75"), (480, "quartz", "11.11"), (64, "raven", "2.56"),
    (3, "saffron", "120.00"), (777, "tundra", "0.07"),
]

# L221-L240: accented letters and emoji (some multi-codepoint).
ACCENTS = """
Café crème, naïve résumé, and a jalapeño piñata: é ï ñ.
Über München fährt der Zug; Grüße aus Köln: ü ö ä ß.
El señor Núñez vive en España desde el año 2009.
Smørrebrød på Østerbro; blåbær og rødgrød: ø å.
Ça va? Le garçon parle français, s'il vous plaît: ç.
Straße, Fußball, weiß, heiß: four words with ß.
Łódź, Gdańsk, Wrocław: ł ó ń in Polish city names.
Győr and Pécs: Hungarian ő and é; also hős and erős.
Žižkov, Češka, Šibenik: ž č š in Czech and Croatian.
Åland, Ærø, Øresund: Nordic letters å æ ø.
Emoji: 😀 😎 🚀 🎉 ✅ ❌ ⚠️ 🔥
Flags: 🇩🇪 🇫🇷 🇯🇵 🇧🇷 🇺🇸 🇳🇴 🇵🇱
ZWJ family: 👨‍👩‍👧‍👦 and 👩‍👩‍👦 and 👨‍👨‍👧
Skin tones: 👍🏻 👍🏽 👍🏿 👋🏼
Professions: 👩‍💻 👨‍🚀 🧑‍🍳 👩‍🔬
Mixed: café ☕, pizza 🍕, sushi 🍣, crêpe 🥞.
Weather: ☀️ 🌧️ ⛈️ ❄️ 🌈 (sun, rain, storm, snow, rainbow).
Rainbow flag 🏳️‍🌈 and pirate flag 🏴‍☠️ are ZWJ sequences.
Ångström, façade, coöperate, rôle, déjà vu, über, Zoë.
Keycaps: 1️⃣ 2️⃣ 3️⃣ #️⃣ and hearts ❤️ 💙 💚.
"""

# L241-L270: colored lines (scroll.html draws them in color).
COLORED = r"""
Red alert: the encoder queue is full at 31 frames.
if (queue.Count > 30) DropOldest(queue);
Blue text is harder to read in 4:2:0, so we test it here.
color = {"r": 220, "g": 40, "b": 40}
Green means the capture is healthy and on time.
assert fps_real >= 0.98 * fps_set, "dropped frames"
Magenta lines often blur when the chroma is subsampled.
const hue = (index * 60) % 360; // cycle six colors
Orange warnings appear when CPU use passes 80 percent.
SELECT tag, COUNT(*) FROM reads WHERE ok = 0 GROUP BY tag;
Teal is the last color in the cycle before red returns.
<span class="warn">disk almost full</span>
Syntax highlighting turns keywords purple and strings green.
for i in range(241, 271): print(f"L{i:03d}")
The colored block tests yuv420p against yuv444p.
let ok = frames.every(f => f.unique && !f.late);
Thin red strokes lose detail first when color is halved.
#define MAX_REGION (7680 * 4320)
Read this sentence slowly and copy every comma, colon; and dash.
git log --oneline --graph | head -n 12
Green on white has low luminance contrast, so watch the edges.
var total = items.Sum(i => i.Price * i.Qty);
Thin strokes in letters such as f, j, and t blur easily.
x = [0xFF, 0x80, 0x00]  # orange in RGB
Teal digits: 3.14159 2.71828 1.41421 1.61803
fn main() { println!("color test {}", 42); }
Magenta brackets: ( ) [ ] { } < > must survive.
if [ "$CHROMA" = "444" ]; then echo full; fi
Orange URL: https://example.org/color?mode=444&fps=10
End of the colored block; black text returns next.
"""

# L271-L289: mixed prose and code.
MIXED = r"""
The parser reads each tag, then compares the rest of the line.
cer = edits / max(1, len(reference))
Our baseline run used gdigrab at 2 fps and lost many lines.
ffmpeg -f lavfi -i ddagrab=framerate=30 -c:v h264_nvenc out.mkv
A scroll at 1000 px/s moves 100 px between frames at 10 fps.
gap = speed / fps > region_h - 2 * line_h
Two readers copied the same video and disagreed on 3 lines.
public record Score(int Caught, double Cer, double Wer);
Keep the font at 100 percent unless the run says otherwise.
{ "run": "B12", "mode": "changes-only", "mb_per_min": 1.8 }
Duplicate frames waste bytes but do not lose text.
while read -r tag rest; do echo "$tag"; done < source.md
The region selector snaps width and height to even numbers.
width -= width % 2; height -= height % 2;
Variable frame rate saves space when the screen is still.
dups = total - len(set(hashes))
A missed line is worse than a misread character.
echo "L$(printf '%03d' 289)"
Only the Zalgo line below may fail without failing the test.
"""

# L290: Zalgo base phrase. The script adds combining marks to each letter.
ZALGO_BASE = "Zalgo: he comes to test the reader"

# L291-L300: mixed prose, numbers, dates, URLs.
TAIL = """
Release 2.4.1 shipped on 2026-11-14 at 09:30 UTC.
Docs: https://getsharex.com/docs/region-capture (read 2026-11-14).
Invoice 2026-0457 totals $1,284.50, due 30 Dec 2026.
Pi to ten places is 3.1415926535; e is 2.7182818284.
The meeting moved from 14:00 to 15:45 on Friday, 20 Nov 2026.
Mirror: http://mirror.example.net/pub/v1.0/files?id=7&lang=en
Population grew from 48,210 in 1990 to 61,975 in 2020.
Phone support (fake): +44 20 7946 0958, ext. 312.
The test ran 14 times; 13 passed and 1 timed out after 600 s.
This is the last line. Thank you for reading every frame.
"""


def block(text):
    """Split a text block into lines. Remove the first and last empty line."""
    lines = text.split("\n")
    if lines and lines[0] == "":
        lines = lines[1:]
    if lines and lines[-1] == "":
        lines = lines[:-1]
    return lines


def zalgo(text, seed):
    """Add 2 to 4 combining marks to each letter. Use a fixed seed."""
    rng = random.Random(seed)
    marks = [chr(c) for c in range(0x0300, 0x0370)]
    out = []
    for ch in text:
        out.append(ch)
        if ch.isalpha():
            for _ in range(rng.randint(2, 4)):
                out.append(rng.choice(marks))
    return "".join(out)


def base_len(text):
    """Count the characters that are not combining marks."""
    return sum(1 for ch in text if not unicodedata.combining(ch))


def tag(n):
    return "L%03d" % n


def build_lines():
    """Return the 300 lines of the source text, with tags."""
    bodies = {}

    def put(start, end, items):
        expected = end - start + 1
        if len(items) != expected:
            sys.exit("Section L%03d-L%03d has %d lines. It must have %d."
                     % (start, end, len(items), expected))
        for i, body in enumerate(items):
            bodies[start + i] = body

    put(1, 60, block(PROSE))
    put(61, 120, block(CODE))
    put(121, 140, ["sha256(%s) = %s" % (w, hashlib.sha256(w.encode("utf-8")).hexdigest())
                   for w in SHA_WORDS])
    put(141, 170, ["b64 " + base64.b64encode(t.format(t=tag(141 + i)).encode("ascii")).decode("ascii")
                   for i, t in enumerate(B64_TEMPLATES)])
    put(171, 200, block(TRAPS))
    put(201, 220, ["| %d | %s | %s |" % row for row in TABLE])
    put(221, 240, block(ACCENTS))
    put(241, 270, block(COLORED))
    put(271, 289, block(MIXED))
    put(290, 290, [zalgo(ZALGO_BASE, ZALGO_SEED)])
    put(291, 300, block(TAIL))

    lines = [tag(n) + " " + bodies[n] for n in range(1, 301)]

    # Check the shared spec before the file is written.
    seen = set()
    for n, line in enumerate(lines, start=1):
        if n == ZALGO_TAG:
            size = base_len(line)
        else:
            size = len(line)
        if size > MAX_LEN:
            sys.exit("Line %s has %d characters. The limit is %d." % (tag(n), size, MAX_LEN))
        if line != line.rstrip() or "\t" in line:
            sys.exit("Line %s has a tab or trailing space." % tag(n))
        # Short code lines such as "}" can repeat. Longer lines must be unique.
        body = line[5:]
        if len(body.strip()) < 8:
            continue
        if body.strip() in seen:
            sys.exit("Line %s repeats an earlier line." % tag(n))
        seen.add(body.strip())
    if any("</script" in line.lower() for line in lines):
        sys.exit("The text must not contain '</script'.")
    return lines


def main():
    parser = argparse.ArgumentParser(
        description="Write the 300-line source text for the AI read test. "
                    "The output is the same each time.")
    parser.add_argument(
        "-o", "--out",
        default=str(Path(__file__).resolve().parent / "source.md"),
        help="Path of the output file. Default: source.md next to this script.")
    args = parser.parse_args()

    lines = build_lines()
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    print("Wrote %d lines to %s" % (len(lines), args.out))


if __name__ == "__main__":
    main()
