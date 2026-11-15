#!/bin/sh
# Pack a compiled DesktopRecorder.exe into a portable zip.
# Usage: packaging/make-windows-zip.sh path/to/DesktopRecorder.exe [version] [output-dir]
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
exe=${1:?path to DesktopRecorder.exe}
version=${2:-1.0.0}
out_dir=${3:-$root}
name="desktop-recorder-${version}-windows-x64"
stage=$(mktemp -d)
dest="$stage/$name"
mkdir -p "$dest"
cp "$exe" "$dest/DesktopRecorder.exe"
cp "$root/packaging/windows/README.txt" "$dest/README.txt"
if command -v zip >/dev/null 2>&1; then
  (cd "$stage" && zip -r -q "$out_dir/$name.zip" "$name")
else
  # Give the paths as arguments, not inside the Python source. A Windows path such as
  # D:\a\_temp in the source becomes an escape sequence (\a = bell), and the zip cannot open.
  python3 - "$dest" "$out_dir/$name.zip" "$stage" << 'PY'
import os, sys, zipfile
src, out, stage = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for dirpath, _, names in os.walk(src):
        for name in names:
            full = os.path.join(dirpath, name)
            zf.write(full, os.path.relpath(full, stage))
PY
fi
rm -rf "$stage"
echo "$out_dir/$name.zip"
