Desktop Recorder 3.1.0 for Windows (portable)

Unzip this folder anywhere and double-click DesktopRecorder.exe.
Nothing is written to Program Files. Settings stay in
%APPDATA%\DesktopRecorder\settings.txt.

You need two things that Windows already has, or one install:

1. .NET Framework 4, which is part of Windows 10 and Windows 11.
2. ffmpeg, which is not in this zip.

ffmpeg is not bundled. A build that can record H.264 links libx264, and
that build is GPL. Install it instead of copying a binary into this zip:

    winget install -e --id Gyan.FFmpeg

Or put ffmpeg.exe in this folder, next to DesktopRecorder.exe.

Win+Shift+D opens the region selector. Drag a rectangle and release the
mouse button, or click a window. The recording starts at once. A red frame
and a Stop button show around the recorded area. They are not in the
video. Press Win+Shift+D again, or click Stop, to stop.

Each recording gives one ZIP file with index.md, frames\001.png and so
on, and clip.mp4. The ZIP goes on the clipboard. Press Ctrl+V in an AI
chat to attach it. Wait for "ZIP ready" (a popup and the note
box show it) before you paste. Settings has "MP4 only" for one video file.

An NVIDIA GPU encodes the video when the PC has one. Without it, the
program encodes on the CPU. Settings has a switch to record the sound of
the PC. With a local Whisper program (whisper.cpp), index.md also gets a
transcript.
Recordings go to C:\Users\<you>\Recordings.
