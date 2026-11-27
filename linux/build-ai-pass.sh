#!/bin/sh
# Compile ai_pass.exe (the Folder for AI pass, linux/ai_pass.cs) with mcs.
# Usage: linux/build-ai-pass.sh [output path]. Default: linux/desktop_recorder/ai_pass.exe.
# Exit code 3 when mcs is missing (sudo apt install mono-mcs).
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
out=${1:-$root/linux/desktop_recorder/ai_pass.exe}
if ! command -v mcs >/dev/null 2>&1; then
  echo "mcs was not found, so ai_pass.exe (Folder for AI) was not built. Install it with: sudo apt install mono-mcs" >&2
  exit 3
fi
cd "$root"
mcs -sdk:4.5 -nologo -nowarn:649,169,414 -target:exe -out:"$out" \
  -r:System.Drawing -r:System.IO.Compression -r:System.IO.Compression.FileSystem \
  linux/ai_pass.cs SettledFrames.cs AfterRecording.cs RecorderCore.cs RecordingSession.cs AudioTranscript.cs
echo "$out"
