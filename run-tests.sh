#!/bin/sh
# Core tests (the Windows rules, under Mono) and the Linux tests.
set -eu
cd "$(dirname "$0")"
if [ -z "${DISPLAY:-}" ] && command -v xdpyinfo >/dev/null 2>&1 && xdpyinfo -display :1 >/dev/null 2>&1; then
  export DISPLAY=:1
fi
mcs -sdk:4.5 -nologo -target:exe -out:/tmp/desktop-recorder-tests.exe \
  tests/CoreTests.cs RecorderCore.cs RecordingSession.cs
mono /tmp/desktop-recorder-tests.exe
PYTHONPATH=linux python3 -m unittest discover -s tests -v
