# Folder for AI on Linux

Added 27 Nov 2026. Settings: `output_mode=folder` (Settings window: Output, "Folder for AI").

## What it does

1. During the recording, ffmpeg writes `<Recordings>/desktop_<time>/work_lossless.mp4`: x11grab at 10 fps,
   no pointer, libx264 `-qp 0` 4:4:4, fragmented MP4. `region=x,y,width,height` in `settings.txt` limits it to
   a part of the screen (width 0 = whole screen). The active window title is read once a second with `xprop`.
   With "Record system audio" on, the same ffmpeg also writes `audio_system.wav` from the Pulse/PipeWire monitor.
   Both inputs use one clock (`-copyts`, shifted by the start time), so WAV time 0 is video time 0.
2. After Stop, `ai_pass.exe` runs the Windows pass (`AfterRecording.cs`, `SettledFrames.cs`, unchanged) under
   Mono. It writes `frames/NNN.png`, `clip.mp4` and `index.md`, deletes the work file, and makes
   `desktop_<time>.zip` next to the folder. With audio, `clip.mp4` gets the sound and, when whisper.cpp is
   found, `index.md` gets the transcript and the ZIP gets `transcript.srt` (same rules as on Windows: README,
   "Audio and transcript"). Whisper: `whisper_path` and `whisper_model` in `settings.txt`, else `whisper-cli`
   on PATH or next to `ai_pass.exe` with a `ggml-*.bin` model next to it or in `models`. The ZIP goes to the clipboard when "Copy ... to the clipboard" is on.

3. When `tesseract` is installed (`sudo apt install tesseract-ocr`) and `ocr_text=1` (default), `index.md` (in the
   folder and in the ZIP) gets a section "Text on screen (OCR)": for each frame, the lines that no earlier frame
   had. A scroll adds its new lines; a still page adds nothing. 18 frames at 1280 x 800: about 3 s, 11 KB.

The pass is slow for a long recording, so it runs on a background thread; the tray shows a message at the end.

## Needs

- `mono-runtime` and `libmono-system-drawing4.0-cil` to run the pass, `mono-mcs` to build `ai_pass.exe`
  (`linux/build-ai-pass.sh`; `build-linux.sh` and the tarball script call it).
- Without Mono or `ai_pass.exe`, the work file stays in the folder and the message says why. MP4 mode does not
  need Mono.

## Limits (27 Nov 2026)

- Wayland: the portal stream (10 fps, BGRA through GStreamer) goes into the same work file. No sound, no
  region and no window titles there yet; the tray says what was left out. Not tested on a real GNOME Wayland
  session: the test fed an X11 grab through the same code (`WaylandCapture._ensure_encoder`).
- Whisper is not packaged. Build whisper.cpp (`cmake -B build && cmake --build build`) and get a model.
  The tiny.en model makes rough row times (the first row starts at 0 s even when the speech starts later).
- The region selector (tray: "Select region and record", X11) has drag, Space (whole screen) and Esc. It has
  no window click and no C, M, P keys of the Windows selector, and the hotkey does not open it. Its region is
  for that recording only; `region=` in `settings.txt` is the region of the other recordings (MP4 and folder).

## Measured (scripted documents under Xvfb, `evidence/improve` of the 27 Nov 2026 test run)

| Test | Folder for AI | MP4 (2 fps), frames for the same budget |
|---|---|---|
| 55 s steady scroll, 220 lines, OCR lines found | 215 of 220 in 18 PNG (2.1 MB) | 215 in 18 evenly spaced frames; 71 in its 3 keyframes |
| 61 s still page, 8 notices of 0.8 s, notices found | 9 of 9 in 19 PNG (1.7 MB) | 2 of 9 in 19 evenly spaced frames; 8 of 9 in all 122 frames |
| 55 s scroll + 2 x 11 s speech (jfk.wav played into the sink) | 15 PNG, `clip.mp4` h264 + aac, 3 transcript rows (whisper.cpp tiny.en), pass 6.3 s; speech end in the WAV 0.13 s from the playback end | - |
| Wayland code path (frames fed as the portal would), 55 s scroll, 220 lines | 215 of 220 in 18 PNG, 0 dropped of 549, pass 8.3 s | - |
