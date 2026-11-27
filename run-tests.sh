#!/bin/sh
# Core tests (the Windows rules, under Mono) and the Linux tests.
set -eu
cd "$(dirname "$0")"
if [ -z "${DISPLAY:-}" ] && command -v xdpyinfo >/dev/null 2>&1 && xdpyinfo -display :1 >/dev/null 2>&1; then
  export DISPLAY=:1
fi
mcs -sdk:4.5 -nologo -target:exe -out:/tmp/desktop-recorder-tests.exe -r:System.Drawing -r:System.IO.Compression -r:System.IO.Compression.FileSystem \
  tests/CoreTests.cs tests/AiCaptureTests.cs tests/AudioTests.cs tests/UiTests.cs UiModel.cs RecorderCore.cs RecordingSession.cs SettledFrames.cs AudioTranscript.cs AfterRecording.cs
mono /tmp/desktop-recorder-tests.exe
PYTHONPATH=linux python3 -m unittest discover -s tests -v
python3 test/score_selftest.py
python3 test/frames_selftest.py --work "$(mktemp -d /tmp/frames_selftest_XXXXXX)"
