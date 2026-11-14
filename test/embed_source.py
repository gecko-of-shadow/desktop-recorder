#!/usr/bin/env python3
"""Copy the text of source.md into the "source" block of scroll.html.

scroll.html must work from file:// without a fetch. Thus the page keeps
a copy of source.md in <script type="text/plain" id="source">.
Run this script after source.md changes. It changes only that block.
"""

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

BLOCK_RE = re.compile(
    r'(<script type="text/plain" id="source">)(.*?)(</script>)',
    re.DOTALL,
)

# These sequences can end or break a script block in HTML.
FORBIDDEN = ("</script", "<!--", "<script")


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Copy the text of source.md into the <script type=\"text/plain\" "
            "id=\"source\"> block of scroll.html. The script changes only that block."
        )
    )
    parser.add_argument(
        "--source", type=Path, default=HERE / "source.md",
        help="Source text file. Default: source.md next to this script.",
    )
    parser.add_argument(
        "--html", type=Path, default=HERE / "scroll.html",
        help="HTML file to update. Default: scroll.html next to this script.",
    )
    parser.add_argument(
        "--check", action="store_true",
        help="Do not write. Exit with code 1 if the embed is not current.",
    )
    args = parser.parse_args()

    text = args.source.read_text(encoding="utf-8")
    text = text.lstrip("﻿").replace("\r\n", "\n").replace("\r", "\n")
    lowered = text.lower()
    for seq in FORBIDDEN:
        if seq in lowered:
            print(f"ERROR: source text contains '{seq}'. The HTML block cannot hold it.",
                  file=sys.stderr)
            return 2
    if not text.endswith("\n"):
        text += "\n"

    with open(args.html, encoding="utf-8", newline="") as f:
        html = f.read()

    matches = BLOCK_RE.findall(html)
    if len(matches) != 1:
        print(f"ERROR: found {len(matches)} source blocks in {args.html}. Expected 1.",
              file=sys.stderr)
        return 2

    new_html = BLOCK_RE.sub(lambda m: m.group(1) + "\n" + text + m.group(3), html)

    if args.check:
        if new_html == html:
            print("Embed is current.")
            return 0
        print("Embed is NOT current. Run embed_source.py.")
        return 1

    if new_html == html:
        print("No change. The embed is current.")
        return 0

    with open(args.html, "w", encoding="utf-8", newline="") as f:
        f.write(new_html)
    lines = text.count("\n")
    print(f"Wrote {lines} lines into {args.html}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
