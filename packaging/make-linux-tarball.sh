#!/bin/sh
# Build a portable Ubuntu 24.04 archive from this repo.
# Usage: packaging/make-linux-tarball.sh [version] [output-dir]
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
version=${1:-1.0.0}
out_dir=${2:-$root}
name="desktop-recorder-${version}-linux-x86_64"
stage=$(mktemp -d)
dest="$stage/$name"
mkdir -p "$dest/desktop_recorder"
cp -a "$root/linux/desktop_recorder/." "$dest/desktop_recorder/"
find "$dest" -type d -name '__pycache__' -exec rm -rf {} + 2>/dev/null || true
find "$dest" -name '*.pyc' -delete
cp "$root/packaging/linux/desktop-recorder" "$dest/desktop-recorder"
cp "$root/packaging/linux/README.txt" "$dest/README.txt"
chmod +x "$dest/desktop-recorder"
tar -C "$stage" -czf "$out_dir/$name.tar.gz" "$name"
rm -rf "$stage"
echo "$out_dir/$name.tar.gz"
