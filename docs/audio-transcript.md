# Design note: audio and transcript

Date: 2026-11-20. Branch: `ai-capture`. Status: implemented, tested on Linux only (Mono and FFmpeg). No run on Windows yet.

Language: ASD-STE100.

"Checked" = a test in the cloud session of 2026-11-20 (Linux, Mono 6.8, FFmpeg 6.1) proved it. "UNVERIFIED" = no test proved it. Every claim about a real recording on Windows is UNVERIFIED until a run on Bran's PC.

---

## 1. Goal

Bran asked for audio and a transcript after a test with a Twitch stream (2026-11-20).

- Claude cannot hear audio. Gemini can read the audio in a video. A text transcript is the part that every AI can use.
- Thus the app records the system audio (the sound that the PC plays), puts it into the video files, and writes a transcript into `index.md` when a local Whisper program is present.

## 2. Decisions

| # | Decision | Reason |
|---|---|---|
| 1 | One setting, off by default: "Record the sound of the PC, and make a transcript if Whisper is installed". Key `record_audio`. | Bran: the PC almost always plays some sound, so the app must not detect audio by itself. The Linux build uses the same key with the same meaning (`1` = record system audio), so a shared settings file stays correct. The Linux default (on) is not changed. |
| 2 | Own WASAPI loopback capture in C# (COM interop), not FFmpeg. | See section 3. |
| 3 | Mux after Stop, not live. | See section 4. |
| 4 | Whisper is optional. A missing, failing or slow Whisper never makes the pass fail. | The task says so. The clip and the frames are more important than the transcript. |
| 5 | Microphone: not in this step. | The task names it as a second step. Section 8 has a design. |

## 3. How to capture the system audio (.NET Framework 4, `csc.exe`, no NuGet)

| Option | Result |
|---|---|
| FFmpeg `dshow` with "Stereo Mix" | Needs a "Stereo Mix" device. Many sound drivers do not have one, or have it off. Not reliable. |
| FFmpeg `dshow` with a virtual device (for example "virtual-audio-capturer" of screen-capture-recorder) | Needs an extra install with a driver. Not acceptable as a default. |
| FFmpeg WASAPI input | FFmpeg has no WASAPI input device (the device list in the FFmpeg documentation has `dshow`, `gdigrab` and others, but no `wasapi`). UNVERIFIED: not read in this session, and not checked against the gyan.dev build on Bran's PC. |
| NAudio (`WasapiLoopbackCapture`) | A NuGet package. Not allowed (HANDOFF 5, decision 1). |
| WinRT `AudioGraph` or `Windows.Media.Capture` | Needs WinRT projections, which `csc.exe` of .NET Framework 4 does not give without extra reference files. Not tried. |
| **Own WASAPI loopback with COM interop** | **Chosen.** About 250 lines (`AudioCapture.cs`). Only Windows system DLLs. The interfaces are `IMMDeviceEnumerator`, `IMMDevice`, `IAudioClient` (with `AUDCLNT_STREAMFLAGS_LOOPBACK`) and `IAudioCaptureClient`. |
| Process loopback (`ActivateAudioInterfaceAsync`, Windows 10 2004 and later) | Can record the sound of one program only. Possible later, for example "record only the browser". Not needed now. |

How `AudioCapture.cs` works (UNVERIFIED on Windows; it compiles with `mcs`):

1. It runs on its own MTA thread. It opens the default playback device (`eRender`, `eConsole`) and reads its mix format (normally 48 kHz, 2 channels, 32-bit float, `WAVE_FORMAT_EXTENSIBLE`).
2. `IAudioClient.Initialize` in shared mode with the loopback flag and a 1 s buffer. The thread reads the packets every 10 ms.
3. `WavWriter` writes the mix format as it is (the `fmt` chunk is the struct that Windows gives). The header gets the real sizes each second, so a file of a killed app is still readable.
4. WAV time 0 = the QPC time right after `IAudioClient.Start`. Loopback gives no packets while the PC plays nothing. Thus the writer compares the QPC time of each packet (`GetBuffer`, 100 ns units) with the frames in the file. A gap of more than 50 ms becomes silence. Audio that runs more than 50 ms ahead is dropped (`AudioClock.Correction`). At Stop, silence fills the file up to the stop time.
5. A device error during the recording (for example, headphones removed) stops the capture. The rest of the file is silence, and `index.md` says so.
6. A start error (no playback device) turns the audio off for that recording only. The start balloon says why. The video recording continues.

## 4. Live mux or mux after Stop

A live mux would give FFmpeg a second input, for example a named pipe that the app fills with the WASAPI data. Reasons against it:

- FFmpeg waits for the pipe when it opens and probes the input. A slow or empty pipe can delay the video capture. Goal 2 of the app is "no dropped frames".
- The time stamps of `ddagrab` and of a raw pipe input start from different clocks. The sync would need tests on Windows that this cloud session cannot run.
- The fallback restart (NVENC to x264, ddagrab to gdigrab) would need a second pipe.

A mux after Stop uses the clocks that the app has already, and FFmpeg can test it on Linux. Thus the app muxes after Stop.

### 4.1 Time line

- `RecordingSession` has a clock that starts with FFmpeg. `ClockOffset` = clock − FFmpeg stats time (from the stats lines). The stats time is 0 at the first video frame.
- The new `RecordingSession.ClockStartTimestamp` is the QPC value at clock 0. `LoopbackCapture.StartTimestamp` is the QPC value at WAV time 0. Both come from `Stopwatch.GetTimestamp()`, so they use the same clock.
- WAV time 0 on the time line of the work file and of `clip.mp4`:

```
shift = (audio start − clock start) − ClockOffset + time of the first frame in the work file
```

- The audio filter puts WAV time 0 at `shift`: `adelay` when the shift is positive, `atrim` + `asetpts` when it is negative (the normal case: the audio starts before FFmpeg).

Checked (FFmpeg 6.1, `test/process_clip.cs` with a test WAV from `WavWriter`, beeps at WAV time 1, 4 and 7 s):

| Shift | Beeps in `clip.mp4` | Beeps in the transcript |
|---|---|---|
| −0.45 s (audio 0.35 s before the clock, offset 0.10 s) | 0.550, 3.550, 6.550 s | 0.55, 3.55, 6.55 s |
| +0.30 s | 1.300, 4.300, 7.300 s | 1.30, 4.30, 7.30 s |

UNVERIFIED: the error of `ClockOffset` on a real recording. It is the smallest (clock − stats time) of all stats lines, so it is too large by the delay of the fastest stats line. Probable size: some tens of ms. A test on Bran's PC must measure it (section 9).

UNVERIFIED: drift between the audio device clock and QPC over a long recording. The writer keeps the file within 50 ms of QPC, so drift cannot grow. Not tested.

## 5. Where the audio goes

| Output | What happens | Status |
|---|---|---|
| Folder mode, `clip.mp4` | The clip encode gets the WAV as a second input, with the align filter, AAC 160 kb/s. If that encode fails, the clip is encoded again without audio and `index.md` says so. | Checked on Linux (libx264). NVENC path UNVERIFIED. |
| Folder mode, work file | The work file is deleted after a good pass, so it gets the audio only when it stays (failed pass, or `DESKTOP_RECORDER_KEEP_WORK=1`): a stream copy with the audio, then the new file replaces the old one. | Checked on Linux. |
| Folder mode, failed pass | The normal `clip.mp4` gets the audio. `audio_system.wav` stays in the folder. | Checked on Linux. |
| "MP4 only" mode | After Stop, each video file gets the audio by a stream copy (`-c:v copy`). A split file n gets the audio from n × split minutes, and each file but the last ends at the next cut. The WAV file is deleted when all files have the audio. | The FFmpeg commands are checked on Linux with 4 s segments. The app code (`Recorder.AddAudioToMp4Files`) is UNVERIFIED. |
| "MP4 only" mode, FFmpeg stopped by itself | No mux. The WAV file stays next to the video, and the balloon names it. | UNVERIFIED. |

Found and fixed during the test: without an end limit, split file 0 of a 10 s test got all 10 s of audio for 4 s of video. The limit is `atrim=end=<split length>`. The app does not use `-shortest`, because it can cut the video when the audio is shorter.

Files in the recording folder:

```
desktop_2026-11-20_10-00-00\
    clip.mp4            (video + audio)
    index.md            (with the section "Audio and transcript")
    transcript.srt      (only when Whisper made a transcript)
    frames\...
```

`audio_system.wav` is deleted when `clip.mp4` has the audio and no work file stays. `transcript_16k.wav` is always deleted. "Copy ZIP" leaves out both WAV files.

## 6. Transcript

### 6.1 Programs

| Program | How the app finds it | Command |
|---|---|---|
| whisper.cpp (`whisper-cli.exe`) | `whisper_path`, or next to the app (also in the subfolders `whisper`, `whisper.cpp`), or on PATH. Needs a model `ggml-*.bin` next to it, in its `models` folder, or in `models` next to the app. Order: large-v3-turbo, medium, small, base, large, tiny. `whisper_model` sets a file. | `-m <model> -f transcript_16k.wav -l <language> -osrt -of transcript_whisper -np` |
| faster-whisper-xxl, whisper-ctranslate2, openai-whisper (`whisper`) | `whisper_path`, or next to the app, or on PATH. `whisper` counts only on PATH or in `whisper_path`, because the name is too general. | `transcript_16k.wav --output_format srt --output_dir <folder> [--model <whisper_model>] [--language <code>]` |

- `whisper_language`: `auto` (default) or a code such as `en` or `de`.
- The input is a 16 kHz mono 16-bit WAV that FFmpeg makes with the same align filter as the clip. Thus the SRT times are on the time line of `clip.mp4` with no further change.
- Time limit: 2 min + 3 × the recording length, at most 1 h. Then the app ends Whisper and `index.md` says so.
- Silent audio (peak below 0.0005, about −66 dBFS) gets no transcript. Reason: Whisper often invents text such as "Thank you." on silence (known Whisper behavior, not tested here).
- Since 2026-11-22 (HANDOFF 7.19): the app also checks each transcript row against the 16 kHz copy (`SpeechCheck` in `AudioTranscript.cs`). A row with sound (−70 dBFS or louder, 100 ms parts) in less than 10% of its time and for less than 1 s is left out. A known Whisper text for silence in a long row (10 s or more, or 80% of the audio) needs sound in half of its time. Reason: three recordings by Bran had "Thank you." rows on digital silence with a few clicks and the stop sound, so the peak check did not stop Whisper.
- Checked on Linux with two fake programs (Python scripts that take the same arguments): whisper.cpp form and openai form, the language and model options, a program that fails (exit code 3), a missing program, a missing `whisper_path`, and silent audio. In each case the pass was `ok` and `index.md` gave the reason.
- UNVERIFIED: a real Whisper program. The argument names come from the documentation of each program as I know it; they were not run in this session. Run the test in section 9.

### 6.2 Output

`index.md` ends with this section (example from the Linux test):

```
## Audio and transcript
clip.mp4 has the system audio (the sound that the PC played).
Transcript: whisper-cli (whisper.cpp, model ggml-base.bin), language de. A machine transcript: some words can be wrong. Times are seconds on the time line of clip.mp4.

| Start | End | Text |
|---|---|---|
| 0.6 s | 0.8 s | beep 1 \| lang de |
```

Without a transcript, the section says why, for example "No transcript: no Whisper program was found (...)" or "No transcript: the audio was silent."

`transcript.srt` has the same segments in SRT form.

The AI message on the clipboard gets a step 4 when a transcript is present ("Read the section "Audio and transcript" at the end of index.md ..."), and one line when `clip.mp4` has audio ("clip.mp4 also has the sound of the PC. If you can hear the audio of a video, listen to it.").

### 6.3 Time from Stop to clipboard

The transcript runs inside the pass, after `clip.mp4` and `index.md`. The clipboard waits for it, so the AI message can name the transcript. This makes "Ready ... s after Stop" longer when the audio setting is on. The status line shows the Whisper time ("Transcript: 12 lines (8 s)").

Option if this is too slow for Bran: put the clipboard text first, run Whisper after it, and only rewrite `index.md`. Then the AI message cannot know if a transcript will come. Not done.

## 7. Settings

| Key | Default | Control |
|---|---|---|
| `record_audio` | 0 | Check box in Settings. |
| `whisper_path` | empty (search) | None. Edit `settings.txt`. |
| `whisper_model` | empty (search) | None. |
| `whisper_language` | `auto` | None. A bad value keeps `auto`. |

## 8. Second step: microphone (design only)

1. A second check box "Also record the microphone", off by default, key `record_microphone`.
2. A second `LoopbackCapture`-like object with `eCapture` and no loopback flag (a normal capture of the default microphone). Its own WAV file and its own QPC start.
3. Two audio tracks in `clip.mp4` (`-map 1:a -map 2:a`), or one mixed track (`amix`). Two tracks keep the voices apart, but some players and AI tools read only the first track. Proposal: one mixed track in `clip.mp4`, and two transcripts.
4. Transcript: Whisper once for each source. `index.md` gets one table with a column "Source" (PC or microphone), sorted by time.
5. Risk: echo. Without headphones, the microphone records the PC sound too, so the transcript has each line twice. Remedy: say so in Settings, or leave out microphone lines that match a PC line at the same time.

## 9. Test on Bran's PC (not done)

Build and unit tests (PowerShell 5.1, in the repository folder; the running app locks `DesktopRecorder.exe`, so build to a new name):

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder-ai.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /win32manifest:DesktopRecorder.manifest /win32icon:app.ico RecorderCore.cs RecordingSession.cs NativeMethods.cs RegionSelector.cs RecordingFrame.cs SettledFrames.cs AfterRecording.cs AudioTranscript.cs AudioCapture.cs DesktopRecorder.cs
```

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /out:"$env:TEMP\CoreTests.exe" tests\CoreTests.cs tests\AiCaptureTests.cs tests\AudioTests.cs RecorderCore.cs RecordingSession.cs SettledFrames.cs AudioTranscript.cs
```

```powershell
& "$env:TEMP\CoreTests.exe"
```

Tests with the app (set the check box in Settings first):

1. **Sync.** Play a video with a visible and audible event (for example a clap or a count with a timer on the screen) in the region. Stop. In `clip.mp4`, compare the time of the sound with the time of the picture. Pass: 0.1 s or less.
2. **Silence.** Record 30 s with no sound, then 10 s of sound. Pass: `clip.mp4` is as long as the video, and the sound starts at the right time.
3. **Long recording.** 10 min with a talk or a video. Pass: the sync at the end is within 0.1 s.
4. **No device.** Turn off all playback devices (or remove the headphones that are the only device) and record. Pass: the video records, and the start balloon says that the audio is off.
5. **Whisper.** Put `whisper-cli.exe` and a model (for example `ggml-base.bin`) in a folder `whisper` next to the app. Record a talk. Pass: `index.md` has the section with the right words at the right times. Note the Whisper time in the status line.
6. **MP4 only** with split 1 min, 2.5 min of sound. Pass: each file has its own part of the audio.
7. **AI read.** Give the folder to Claude (transcript only) and to Gemini (`clip.mp4` with audio). Ask about what was said.

## 10. Other changes in the same work (task 2 of 2026-11-20)

### 10.1 Video content that never settles

Bran's Twitch recording (full screen, 25.9 s) gave only the 5 s fallback frames, because a video never settles.

Two options: (A) detect continuous motion and say so in `index.md`; (B) suggest the "MP4 only" mode.

Choice: **A**. Reasons:

- The folder output still gives more than "MP4 only": the frames every 5 s, the window titles, the note, and now the audio and the transcript. "MP4 only" would lose all of these.
- The AI must know that the frames are samples, not settled frames. Only `index.md` can tell the AI this. A hint to Bran cannot.
- A change of mode helps only the next recording. The note helps this recording.

Rule (`SettledFrameFinder.MotionReport`): add the time between two frames that differ (not a scroll) when they are 0.4 s (`settle_ms`) or less apart. When this time is 70% or more of a recording of 8 s or more, `index.md` gets a warning line:

```
Warning: the picture changed during 100% of the recording (area x 32, y 32, 456 x 264 px), for example a video or an animation. Such content does not settle, so the frames in that time are samples every 5 s ("screen still changing"). Use clip.mp4 to see the motion.
```

The tray status line also says "The screen did not settle (moving content): the frames are samples."

Checked on synthetic files: the note shows for a "player" page (a moving area in a still page), `testsrc2`, `mandelbrot` and `life`. It does not show for three scroll videos and `cellauto` (a real scroll). Gaps of 1 s ("Changes only" mode on a still screen) do not count. UNVERIFIED: Bran's Twitch recording (the file is not in the cloud).

### 10.2 False scroll from video motion

Frame 006 of the Twitch recording was "scroll, 573 px up (still scrolling)". The old rule took a shift when its pixel error was below 75% of the error with no shift. Moving video can meet this rule by chance.

New rule (`SettledFrameFinder.MatchedEdgeFraction`): after the best shift is found, 80% or more of the edge pixels in the changed columns must match after the shift (each color channel within the pixel tolerance, 24). Flat areas do not count, because a flat area matches any shift. A real scroll of a lossless capture matched 0.99 to 1.00 in the tests.

Also fixed: the coarse search could lose the true step on a sparse page (many shifts with error 0, and the sort does not keep an order for ties). The band votes and the step before now always get the fine check.

Checked on synthetic files (before → after):

| File | Scroll frames | Missed rows |
|---|---|---|
| `testsrc2`, 26 s (moving content, no scroll) | 19 → 0 | - |
| Plain scroll, 3660 rows | 15 → 15 | 0 → 0 |
| Scroll with a static header and sidebar | 20 → 20 | 0 → 0 |
| Scroll with a playing video inside the page | 15 → 15 | 0 → 0 |
| `cellauto` (a real scroll of 2 px each frame) | 6 → 6 | - |

New unit test `TestNoisyShiftIsNotScroll`: a shift with noise of ±48 on each pixel. The old rule measured a false scroll of −824 px. The new rule measures 0.

Known limit (not changed): `PixelError` compares the full width of the frame. A static column WITH text (a sidebar) adds error at the true shift, so the 75% rule can miss some steps of a real scroll. A synthetic test with a 20 px text column in a 96 px frame lost steps with the old and the new rule. The 640 px test with a 120 px sidebar lost no rows. Possible fix: compare only the changed columns, as the new match check does. Not done, because it changes the scroll measure for all recordings and needs the real test files.

UNVERIFIED: the Twitch recording itself.

### 10.2.1 Bran's YouTube test recording (2026-11-20)

File: `desktop_2026-11-20_00-00-01_531.mp4` ("MP4 only" output, 1920x1020, 19.9 s, 15 fps "Changes only", lossy, no audio track). Content: a YouTube video in Chrome. The pass ran on it under Mono with the code before (535753c) and after (a27b22f) the task 2 changes.

| | Before | After |
|---|---|---|
| Frames | 5 (first, 3 x "screen still changing", last) | the same 5 |
| Large false scroll | none | none |
| Motion note | none | "the picture changed during 98% of the recording (area x 16, y 16, 1848 x 976 px) ..." |
| Scroll values in the rows | -3, +4, +3 px | -3, +2, +3 px |

- The new match check rejected 1 of the 5 shifts that passed the error rule (match 0.76). It accepted 4 shifts of 1 to 3 px (match 0.80 to 0.92). These are probably real motion in the video (a camera tilt). A pixel check cannot tell them from a slow scroll.
- Change (Bran approved): a row of a changing screen ("screen still changing", or the last frame when the screen was changing) does not name a scroll below 2% of the moving area (`RecordingIndex.SmallScrollFraction`; the area is the height of the change box, or the region height). Settled rows keep the scroll text. The frames do not change. Checked on this file: no row names a scroll. Unit test `TestSmallScrollText`.
- This file did not repeat the 573 px fault, so the 573 px fix is proved on synthetic files only.

### 10.3 Clipboard: file drop and text (risk, no change)

In folder mode the clipboard holds two formats: a file drop (the folder) and Unicode text (the AI message). Each program that pastes picks one format.

Risk (not checked): a chat app that accepts files (a browser page such as claude.ai, or a desktop chat app) can take the file drop first. Then Ctrl+V attaches the folder, or nothing (many web pages cannot take a folder), and the AI message is lost. Explorer takes the file drop, and a terminal or a text box takes the text, as planned.

Possible fixes:

1. A setting "Clipboard: AI message / folder / both" (default "both", as now).
2. Text only by default, and a tray item "Copy folder" for Explorer.
3. Put the folder path only in the text (it is there already), and drop the file drop.

Test before a change: copy a recording, then press Ctrl+V in claude.ai (Chrome), in the Claude desktop app, in Gemini, in a terminal, and in Explorer. Write down what each one pasted. Do not change the behavior without this proof.

## 11. Checked and not checked

Checked (cloud session, 2026-11-20, Linux):

- Unit tests (`tests/CoreTests.cs`, `tests/AiCaptureTests.cs`, `tests/AudioTests.cs`) pass with Mono. `run-tests.sh` passes (also the 13 Linux tests).
- The whole app compiles with Mono `mcs` (`/target:winexe`, all 10 source files). This is not a `csc.exe` build.
- `WavWriter` makes a float `WAVE_FORMAT_EXTENSIBLE` file that FFmpeg reads (48 kHz, 2 channels, right length and beep times).
- The pass with audio (`test/process_clip.cs` under Mono): alignment, transcript with fake Whisper programs, failure cases, a failed pass, and the delete rules for the WAV files.
- The FFmpeg commands for "MP4 only" with split files.

Not checked:

- Any run on Windows: the WASAPI capture, the COM interop, the MTA thread, the device-error path, the start balloon, the Settings check box, the NVENC clip encode with audio, `Recorder.AddAudioToMp4Files`.
- A real Whisper program and its time on Bran's PC.
- The size of the `ClockOffset` error, and drift on a long recording.
- That the start sound of the app is in the audio. Probably yes: the capture starts before the start sound plays.
- The clipboard risk in 10.3.
- Bran's Twitch recording with the new scroll and motion rules.
