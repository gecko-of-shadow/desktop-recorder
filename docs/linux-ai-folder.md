# Folder for AI on Linux

Added 27 Nov 2026. Settings: `output_mode=folder` (Settings window: Output, "Folder for AI").

## What it does

1. During the recording, ffmpeg writes `<Recordings>/desktop_<time>/work_lossless.mp4`: x11grab at 10 fps,
   no pointer, libx264 `-qp 0` 4:4:4, fragmented MP4. `region=x,y,width,height` in `settings.txt` limits it to
   a part of the screen (width 0 = whole screen). The active window title is read once a second with `xprop`.
2. After Stop, `ai_pass.exe` runs the Windows pass (`AfterRecording.cs`, `SettledFrames.cs`, unchanged) under
   Mono. It writes `frames/NNN.png`, `clip.mp4` and `index.md`, deletes the work file, and makes
   `desktop_<time>.zip` next to the folder. The ZIP goes to the clipboard when "Copy ... to the clipboard" is on.

The pass is slow for a long recording, so it runs on a background thread; the tray shows a message at the end.

## Needs

- `mono-runtime` and `libmono-system-drawing4.0-cil` to run the pass, `mono-mcs` to build `ai_pass.exe`
  (`linux/build-ai-pass.sh`; `build-linux.sh` and the tarball script call it).
- Without Mono or `ai_pass.exe`, the work file stays in the folder and the message says why. MP4 mode does not
  need Mono.

## Limits (27 Nov 2026)

- X11 only. On Wayland, folder mode records an MP4 and says so.
- No sound in folder mode yet (the Windows pass can take a WAV; the Linux capture of it is not written).
- No region selector window yet: the region comes from `settings.txt`.

## Measured (scripted documents under Xvfb, `evidence/improve` of the 27 Nov 2026 test run)

| Test | Folder for AI | MP4 (2 fps), frames for the same budget |
|---|---|---|
| 55 s steady scroll, 220 lines, OCR lines found | 215 of 220 in 18 PNG (2.1 MB) | 215 in 18 evenly spaced frames; 71 in its 3 keyframes |
| 61 s still page, 8 notices of 0.8 s, notices found | 9 of 9 in 19 PNG (1.7 MB) | 2 of 9 in 19 evenly spaced frames; 8 of 9 in all 122 frames |
