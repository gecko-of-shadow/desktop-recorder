# Desktop Recorder

A small tray program that records a part of your screen, so that an AI model can read the text on it. On Windows, one recording gives one folder: the settled frames as PNG files, an `index.md` that describes each frame, and `clip.mp4` for people.

Current version: **3.0.0** (2026-11-20). See [CHANGELOG.md](CHANGELOG.md) for the version history.

You select the area with the text. The program records that area at full resolution, so each character stays sharp. A still screen costs almost no space, so the files stay small.

- **Windows 10 and 11:** the full program, with the region selector. C# on .NET Framework 4, a tray front end for [FFmpeg](https://ffmpeg.org).
- **Ubuntu Desktop 24.04:** the earlier full-screen tray recorder (Python and GTK). It has no region selector. It has not changed since version 1.0.0.

## Features

Windows:

- **Region selector.** Win+Shift+D opens the selector over all monitors. Drag a rectangle, click a window, or press Space for all monitors. The recording starts when you release the mouse button.
- **One hotkey** starts and stops the recording. The hotkey is a setting.
- **Recording frame.** A red frame and a Stop button show around the recorded area. Windows keeps them out of the video.
- **GPU capture and encode.** `ddagrab` capture and `h264_nvenc` encode by default. The program uses `gdigrab` and `libx264` when the GPU path is not available.
- **"Changes only" mode** (default). Frames that do not change are dropped. A still screen costs almost nothing.
- **Status line** after each recording: the frame count, the `clip.mp4` size and the time until the files are ready (folder mode), or real frames, duplicates, dropped frames and file size ("MP4 only").
- **Folder for an AI** (default, new in 3.0.0). Each recording gives a folder with `frames\001.png` and so on, `index.md` and `clip.mp4`. See [Recording folder](#recording-folder-windows). "MP4 only" gives one MP4 file, as in version 2.0.2.
- **System audio and transcript** (optional, off by default, new in 3.0.0). The sound of the PC goes into the video. When a local Whisper program is present, `index.md` gets a transcript. See [Audio and transcript](#audio-and-transcript-windows).
- The finished recording (`index.md` and the frames, or the video) goes on the **clipboard**. Press Ctrl+V to paste it into a chat or a folder.
- **Per-monitor DPI aware.** All positions and sizes are physical pixels.
- FFmpeg stops when the program stops, also after a crash.
- Start and stop sounds, an option to hide the mouse pointer, start with Windows, split long recordings into parts.

Ubuntu 24.04:

- Full-screen recording. X11 uses FFmpeg `x11grab`. Wayland uses xdg-desktop-portal and PipeWire.
- System audio (what you hear), through the PulseAudio API that PipeWire provides.
- Tray icon, Super+Shift+D shortcut, and the same settings file format as Windows.

## Download and install

Download a portable archive from the [Releases page](https://github.com/gecko-of-shadow/desktop-recorder/releases/latest). Unzip or untar it and run it. Nothing is copied into Program Files, and the Linux archive does not need root.

| Platform | Archive | Start |
|---|---|---|
| Windows 10 and 11 (x64) | `desktop-recorder-<version>-windows-x64.zip` | `DesktopRecorder.exe` |
| Ubuntu 24.04 (x86_64) | `desktop-recorder-<version>-linux-x86_64.tar.gz` | `./desktop-recorder` |

FFmpeg is not inside the archives. An FFmpeg build that records H.264 links libx264 and is GPL, so you install it yourself.

**Windows.** .NET Framework 4 is part of Windows 10 and 11. Install FFmpeg in PowerShell:

```
winget install -e --id Gyan.FFmpeg
```

Or put `ffmpeg.exe` next to `DesktopRecorder.exe`. The GPU capture (`ddagrab`) needs FFmpeg 6.0 or later. The tests used the gyan.dev full build of 2026-04-30.

The program uses an NVIDIA GPU to encode when the PC has one. Without an NVIDIA GPU, it encodes on the CPU.

Windows SmartScreen can show a warning, because the program is not signed. Click **More info**, then **Run anyway**.

**Ubuntu 24.04.** The `README.txt` in the archive lists the apt packages that the program needs (the same list as in [Build from source](#ubuntu-2404)).

## Use it (Windows)

1. Press **Win+Shift+D**. The region selector opens over all monitors. The screen stops and goes darker.
2. Select the area to record:
   - Drag a rectangle, then release the mouse button. The recording starts at once.
   - Or move the cursor over a window. A border shows around the window. Click to record that window area.
   - Or press **Space** to record all monitors.
3. A thin red frame shows around the recorded area. A small **Stop** button shows below it. Both are outside the area, and Windows keeps them out of the video.
4. To stop, press **Win+Shift+D** again, or click **Stop**.

When the recording stops:

- In folder mode, a small note box opens. Type one line for the AI and press Enter, or press Esc. The note goes into `index.md` as "Note from Bran:" (into `note.txt` if the frame pass fails). The program does not wait for the note. "MP4 only" mode has no note box.
- The program makes the frames, `index.md` and `clip.mp4` in the background. When this work is done, the status line in the tray menu ends with "Ready ... s after Stop."
- `index.md` and the PNG frames go on the clipboard as files, together with a text message for the AI that has the folder path. Ctrl+V in a chat app attaches the files. Ctrl+V in a terminal pastes the text. `clip.mp4` is not on the clipboard, because some chat apps refuse video. In "MP4 only" mode, the video file goes on the clipboard. The setting "Copy the recording (folder or video) to the clipboard when it stops" turns off the whole clipboard copy: the files and the message.
- Ctrl+V into the Claude desktop chat is tested. Other chat apps are not tested. Some chat apps take only about 20 files in one message, so a recording with many frames may not fit. Then use "Copy ZIP of last recording".
- The tray menu shows a status line. Folder mode example: "Last recording: Frames: 5. clip.mp4: 0.12 MB. Ready 3.1 s after Stop." With audio, the line also gives the transcript result. "MP4 only" example: "Last recording: Real frames: 83 of 83. Duplicates: 0. Dropped: 0. File size: 0.05 MB."

The recording area is a fixed rectangle. If a window moves during the recording, the area does not move with it.

Other controls:

- A short sound plays when the recording starts and when it stops.
- Right-click the tray icon for: Select region and record (or Stop recording), Open recordings folder, Open last recording folder, Copy ZIP of last recording, Settings, Exit.
- Double-click the tray icon to open Settings.
- Exit stops a running recording. The program then stays in the tray until the files of the last recording are made. The tray shows "Finishing the last recording, then the app closes...".

### Region selector keys

| Key | Result |
|---|---|
| Drag, then release | Records the rectangle. |
| Click | Records the window under the cursor (its visible frame, without the shadow). |
| Shift while you drag | Keeps the selection square. |
| Ctrl while you drag | Moves the rectangle that you draw. |
| Arrow keys | Move the cursor 1 px. Shift + arrow keys move it 10 px. |
| Enter | Records the current selection. |
| Space | Records all monitors. |
| `~` | Records the monitor under the cursor. |
| C | Shows or hides a crosshair across the screen. |
| M | Shows or hides the magnifier near the cursor. |
| P | Shows or hides the position and size of the selection. |
| Esc | Closes the selector. Nothing is recorded. |

The size label shows the size in physical pixels. The program makes the width and the height even numbers, because H.264 needs even sizes. It removes at most 1 px. It never scales the picture.

### Settings (Windows)

| Setting | Default | Notes |
|---|---|---|
| Save recordings to | `C:\Users\<you>\Recordings` | The program makes the folder. |
| Frames per second | 10 | 0.2 to 60. See [Choose a frame rate](#choose-a-frame-rate). |
| Quality | Balanced | Smallest files, Balanced, or Sharpest text. See [Capture and encoding](#capture-and-encoding-windows). In folder mode it applies only to `clip.mp4`. |
| Start a new file every | 30 minutes | 0 means one file for each recording. "MP4 only" mode only. Folder mode does not split. |
| Hotkey | Win+Shift+D | Win, Ctrl, Alt and Shift, plus a letter, a digit or F1 to F12. Shift alone works only with F1 to F12. The same hotkey opens the selector and stops the recording. |
| Capture | ddagrab (GPU) | ddagrab or gdigrab. |
| Encoder | Auto | Auto (NVENC, else x264), NVENC only, or x264 only. |
| Color detail (chroma) | 4:2:0 | 4:2:0 or 4:4:4. In folder mode it applies only to `clip.mp4`. |
| Frame-rate mode | Changes only | Fixed frame rate, or Changes only. |
| Output | Folder for AI | Folder for AI (frames, `index.md`, `clip.mp4`), or MP4 only. See [Recording folder](#recording-folder-windows). |
| Start Desktop Recorder when Windows starts | Off | Adds one entry for your user account only. |
| Start recording (saved region) when Desktop Recorder starts | On | Records the last selected area, without the selector. With no saved area, or an area that is not on the screen now, it records the full desktop. |
| Copy the recording (folder or video) to the clipboard when it stops | On | |
| Play a sound when recording starts and stops | On | Two rising tones for start, two falling tones for stop. |
| Hide the mouse pointer in the video | Off | On removes the pointer from the video. See [Good to know](#good-to-know). |
| Record the sound of the PC, and make a transcript if Whisper is installed | Off | See [Audio and transcript](#audio-and-transcript-windows). |
| Region selector: Crosshair, Magnifier, Position | Off, On, On | The keys C, M and P in the selector change them too. |

Settings are stored in `%APPDATA%\DesktopRecorder\settings.txt`.

When you click Save during a recording, the recording continues. New capture settings apply to the next recording. A new hotkey applies at once.

## Use it (Ubuntu 24.04)

Start `desktop-recorder`. A monitor icon shows in the tray. On GNOME it uses an app indicator. On an X11 session such as Xfce it uses the classic tray.

- **Super+Shift+D** is the shortcut (the same keys as Win+Shift+D on Windows). On X11 the program grabs the keys. On GNOME Wayland it adds a custom shortcut named Desktop Recorder. Other Wayland desktops use the tray menu.
- On X11 the whole X display is recorded. On Wayland, Ubuntu asks which screen to share, and the recorder keeps the first screen that you select. The first Wayland recording opens the system screen-share dialog. Later recordings use that permission again.
- System audio is the monitor of the default output. It is recorded unless you turn it off in Settings. If that device is missing or does not answer within 2 seconds, the recording continues with video only and the tray says so.
- Settings are stored in `~/.config/DesktopRecorder/settings.txt`. "Start when I log in" writes `~/.config/autostart/desktop-recorder.desktop`.
- `desktop-recorder --help` shows the options. `desktop-recorder --check` prints the capture backend. `desktop-recorder --smoke 2` records two seconds with X11 and exits.

## Capture and encoding (Windows)

**Capture.**

- `ddagrab` (default) uses the Desktop Duplication API on the GPU. It records one monitor.
- `gdigrab` uses GDI on the CPU. It can record an area that crosses two monitors.
- The program uses `gdigrab` without a question in these cases: the area crosses two monitors; no area and more than one monitor; or `ddagrab` fails at the start.

**Encoder.**

- Auto (default) uses `h264_nvenc` on an NVIDIA GPU. When NVENC is not available or fails at the start, it uses `libx264` with preset `veryfast`.
- NVENC cannot encode an area smaller than about 146x50 px (the limit changes with the GPU) or larger than 4096x4096 px. For these sizes, the program uses `libx264`.

**Quality.**

| Quality | NVENC (CQ) | x264 (CRF) |
|---|---|---|
| Smallest files | 28 | 32 |
| Balanced | 23 | 27 |
| Sharpest text | 17 | 21 |

A lower number gives a sharper picture and a larger file. The AI read test read each NVENC level one time. The differences between the levels were not larger than the differences between two reads of the same frames. Sharpest text gave files about 1.5 times larger than Balanced.

**Chroma.** 4:2:0 plays in all players and browsers. 4:4:4 keeps full color detail, for colored text. In the read test, 4:4:4 gave no read gain, and the files were about 20% larger. Some browsers and players cannot play 4:4:4. FFmpeg can read it.

**Frame-rate mode.**

- Changes only (default): FFmpeg captures at the frame rate that you set and drops frames that do not change (`mpdecimate`, variable frame rate). Since version 2.0.1 the filter uses lower thresholds, so one-character changes (for example "x.y" to "x,y") are kept. The file keeps at least one frame each second, so the length of the file stays correct. A still screen costs almost nothing.
- Fixed frame rate: every frame is kept.

**Output.** In "Folder for AI" mode, see [Recording folder](#recording-folder-windows). In "MP4 only" mode: fragmented MP4. A recording is one file named `desktop_yyyy-mm-dd_hh-mm-ss_mmm.mp4`. When a recording is split, the parts are `desktop_yyyy-mm-dd_hh-mm-ss_mmm_000.mp4` and so on.

## Recording folder (Windows)

This is the default output since version 3.0.0.

```
desktop_2026-11-19_13-01-41\
    clip.mp4
    index.md
    frames\
        001.png
        002.png
```

- **`frames\`** has the settled frames only, as PNG files with no loss. A settled frame is the first frame after the screen stops changing for 400 ms. A blinking text cursor makes no new frame. A small change that stays makes a new frame.
- During a long scroll, the program writes a frame each time the content moved 60% of the scrolled area, so no text is lost between two frames. When the screen changes without a stop, it writes a frame every 5 seconds.
- **`index.md`** starts with an instruction for the AI: the files are one screen recording, and they are context for your message. Then it has the start time with the UTC offset of the PC, your note, the length, the region size, the frame count, and one row for each frame: the time, the window title and what changed. It warns when there are more than 20 frames, and when the long side is above 1568 px.
- **`clip.mp4`** is the video for people. Each frame in `frames\` is a keyframe of `clip.mp4`, at the time that `index.md` gives. The clip has no other keyframes.
- **Copy ZIP of last recording** (tray menu) makes `<folder>.zip` next to the folder and puts it on the clipboard. It replaces an older ZIP with that name. The ZIP leaves out the work file and the WAV files.

How it works: FFmpeg records a lossless 4:4:4 work file. After Stop, a background pass reads the work file, writes the frames, the index and the clip, and then deletes the work file. A recording in this mode is not split into parts. The Quality and Color detail settings apply only to `clip.mp4`. The work file and the PNG frames are always lossless.

If the pass fails, the work file stays and the program writes a normal `clip.mp4`. Your note then goes into `note.txt`, because there is no `index.md`. The clipboard holds the files that exist (or the folder, when there are none) and a message for the AI that says the pass failed.

These settings have no control in the Settings window. Change them in `settings.txt`: `settle_ms` (400), `still_interval_s` (5), `scroll_percent` (60), `size_limit` (1568).

When the picture changes almost all the time (for example a video or a stream), it never settles. Then `index.md` has a warning line: the frames in that time are samples every 5 seconds, and the AI must use `clip.mp4` for the motion.

## Audio and transcript (Windows)

New in 3.0.0. Tested on one Windows 11 PC: the audio capture, and a transcript with whisper.cpp. Not tested: the sync of audio and video, long recordings with a transcript, and the Whisper programs other than whisper.cpp. Details: [docs/audio-transcript.md](docs/audio-transcript.md).

- Turn on "Record the sound of the PC, and make a transcript if Whisper is installed" in Settings. It is off by default.
- The program records what the default playback device plays (WASAPI loopback). No extra driver and no "Stereo Mix" device are necessary. The microphone is not recorded.
- Folder mode: `clip.mp4` gets the audio. "MP4 only" mode: each video file gets the audio after Stop.
- Transcript: set `whisper_path`, or put a Whisper program next to `DesktopRecorder.exe` (or in a subfolder `whisper`, `whisper.cpp` or `Faster-Whisper-XXL`), or on PATH. The program uses `whisper_path` first, then the folder of the app, then PATH. If `whisper_path` names a file that does not exist, there is no transcript. It looks for `whisper-cli.exe` (whisper.cpp), `faster-whisper-xxl.exe`, `whisper-ctranslate2.exe`, and `whisper.exe` (only on PATH, because the name is too general). The transcript goes into `index.md` (section "Audio and transcript") and into `transcript.srt`. Its times are on the time line of `clip.mp4`.
- whisper.cpp needs a model file `ggml-*.bin`: next to the program, in its `models` folder, or in `models` or `whisper\models` next to the app. With more than one model, the program uses the first of large-v3-turbo, medium, small, base, large and tiny.
- Without Whisper, the recording still has the audio, and `index.md` says that there is no transcript.
- Whisper can run for at most 2 minutes plus 3 times the recording length (at most 1 hour). If it takes longer, or if the audio is silent, `index.md` gives the reason that there is no transcript.
- If the audio cannot go into the video, it stays as a WAV file: `audio_system.wav` in the recording folder, or `desktop_<time>.wav` next to the MP4 files.
- Settings without a control (edit `settings.txt`): `whisper_path` (a full path to the program), `whisper_model` (whisper.cpp: the model file; other programs: a model name), `whisper_language` (`auto`, or a code such as `en`).
- The transcript runs before the folder goes on the clipboard, so "Ready ... s after Stop" is longer.

## Choose a frame rate

When text scrolls, each frame must overlap the frame before it. If the scroll moves more than the area height between two frames, lines are lost.

Use this rule:

```
lowest safe fps = 1.5 x scroll speed (physical px/s) / (area height in px - 2 text lines)
```

- Physical px/s = page px/s x Windows display scale. At 125% scale, 1000 page px/s is 1250 physical px/s.
- 1.5 is a safety margin. The test found an error of about 6% in the rule.

Example: an area 892 px high, text lines 31 px high, a scroll of 1000 page px/s at 125% scale. The rule gives 1.5 x 1250 / 830 = 2.3 fps. A fast scroll of 3000 page px/s needs 6.8 fps.

In the test, 4, 6, 10 and 15 fps all caught 300 of 300 lines at 1000 page px/s. At 4 fps and 3000 page px/s (above the limit), the AI lost 45 of 300 lines.

With "Changes only", a higher frame rate costs space only while the screen changes. Thus 10 fps is a safe choice for most scrolls.

## Good to know

- On Windows, audio is recorded only when you turn it on in Settings. On Ubuntu, system audio is recorded unless you turn it off in Settings.
- The MP4 stays playable if the PC loses power during a recording. You lose about the last 5 seconds at the default settings, and more at a very low frame rate.
- If the program stops without a normal stop (for example, a crash), FFmpeg stops with it. A file shorter than 2 seconds can then be unreadable.
- Hide the mouse pointer when the screen is mostly still and you move the mouse. In "Changes only" mode, each pointer move otherwise adds a frame. Hover effects in other programs (button colors, tooltips) still add frames.

## Troubleshooting

**Part of the red frame shows in the video.**
This occurs only when the area touches the screen edge and Windows cannot exclude the frame. The program shows a warning. Windows 10 version 2004 or later is necessary to keep the frame out of the video.

**The recording uses x264, not NVENC.**
The tray balloon at the start of the recording gives the reason. Windows does not show the balloon when notifications are off. Update the NVIDIA driver, or select a larger area.

**"Protected folder access blocked" from Windows Security.**
Controlled folder access protects Videos, Documents, Pictures, Desktop and Music. The default folder, `C:\Users\<you>\Recordings`, is outside those. If you selected a protected folder, open Settings from the tray icon and select a different folder. Or allow both `DesktopRecorder.exe` and `ffmpeg.exe` in Windows Security.

**"ffmpeg.exe was not found."**
Run the winget command in [Download and install](#download-and-install), or put `ffmpeg.exe` next to `DesktopRecorder.exe`.

**The hotkey does nothing.**
Another program already uses that hotkey. The recorder shows a warning when that occurs. Select a different hotkey in Settings, or use the tray menu.

**Ubuntu: `~/.local/bin/desktop-recorder` is not found.**
Log out and back in, or run `export PATH="$HOME/.local/bin:$PATH"`.

## Build from source

### Windows

You need nothing extra. The C# compiler ships with Windows (.NET Framework 4, `csc.exe`).

Exit the running recorder first. If you do not, the build cannot replace the `.exe`.

Double-click `build.cmd`. It builds `DesktopRecorder.exe` in the repository folder, and installs FFmpeg with winget if FFmpeg is missing. Or run this in PowerShell in the folder:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /win32manifest:DesktopRecorder.manifest /win32icon:app.ico RecorderCore.cs RecordingSession.cs NativeMethods.cs RegionSelector.cs RecordingFrame.cs SettledFrames.cs AfterRecording.cs AudioTranscript.cs AudioCapture.cs DesktopRecorder.cs
```

The app manifest (`DesktopRecorder.manifest`) makes the program per-monitor DPI aware and holds the version number.

To run the unit tests on Windows, build them:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /out:"$env:TEMP\CoreTests.exe" tests\CoreTests.cs tests\AiCaptureTests.cs tests\AudioTests.cs RecorderCore.cs RecordingSession.cs SettledFrames.cs AudioTranscript.cs
```

Then run them. The tests write `ok` when all checks pass:

```
& "$env:TEMP\CoreTests.exe"
```

### Ubuntu 24.04

Install the packages, then build and install the program for your user:

```
sudo apt update
sudo apt install ffmpeg python3 python3-gi python3-gi-cairo \
  gir1.2-gtk-3.0 gir1.2-ayatanaappindicator3-0.1 gir1.2-notify-0.7 \
  gstreamer1.0-plugins-base gstreamer1.0-pipewire \
  pipewire pipewire-pulse wireplumber pulseaudio-utils \
  xdg-desktop-portal xdg-desktop-portal-gnome xdg-desktop-portal-gtk \
  xclip wl-clipboard
./build-linux.sh --install
export PATH="$HOME/.local/bin:$PATH"
desktop-recorder
```

`./build-linux.sh` alone only byte-compiles `linux/desktop_recorder`. With `--install` it copies the program to `~/.local/share/desktop-recorder`, writes the launcher `~/.local/bin/desktop-recorder` and the menu entry `~/.local/share/applications/desktop-recorder.desktop`. Set `PREFIX` to install somewhere else.

To run the tests on Ubuntu 24.04 (Mono runs the Windows rules; Python runs the Linux rules):

```
sudo apt install mono-devel libmono-system-windows-forms4.0-cil libmono-system-drawing4.0-cil x11-utils xdotool
./run-tests.sh
```

### Release archives

A `v*` tag starts the Release workflow (`.github/workflows/release.yml`). It builds the Windows zip with `packaging/make-windows-zip.sh` and the Ubuntu archive with `packaging/make-linux-tarball.sh`, and attaches both to the GitHub release. The tag message becomes the release notes.

## Test results

Version 2.0.x was tested on one PC: one 1920x1080 monitor at 125% scale, RTX 4060, FFmpeg 2026-04-30. `HANDOFF.md` sections 6.5 to 6.10 have the full tables. Summary:

- **Frame rate.** All capture and encoder settings kept every frame at 10 fps. `ddagrab` with NVENC also kept every frame at 30 fps. No frame was lost during a fast scroll.
- **Read test.** A Claude Sonnet reader read the frames of a 300-line test page. Every run below the frame-rate limit caught 300 of 300 lines. Errors were mostly in emoji lines, and they occurred on lossless frames too.
- **File size.** A 10 s recording of a still area in "Changes only" mode gave 11 frames and 12 KB.
- **Pointer.** In "Changes only" mode, a moving mouse pointer on a still screen made the file 18 times larger. With the pointer hidden, the program dropped those frames.
- **Look-alike changes (2.0.1).** An offline test caught 15 of 15 one-character changes, with 2 font sizes and light and dark themes.

Not tested: PCs with two or more monitors (also `gdigrab` with an area across two monitors), monitors with different scales, the `libx264` fallback in the program, PCs without an NVIDIA GPU, and AI tools other than Claude. In the selector, these keys are not tested: `~`, Shift and Ctrl while you drag, C, M and P.

Version 3.0.0 (folder output, audio, transcript) was tested on the same PC with Windows 11. `HANDOFF.md` sections 7.5 to 7.10 have the full tables. Summary:

- **Lossless capture.** Counter test at 15 fps for 10 s: 150 of 150 frames, in "Changes only" and in fixed mode. This test ran before the `-bf 0` change and did not run again after it.
- **Settled frames (offline).** A status line that changed 5 times gave 6 frames, one for each status. A blinking text cursor for 30 s gave 1 frame. 15 of 15 look-alike changes were caught. A scroll of 100 lines and 5 settings pages: 0 px missed.
- **Real recordings.** Status line: 6 frames, each status in one frame. Settings tour: all 5 pages, no blended frame. The keyframes of `clip.mp4` equal the PNG count in all folders, and `clip.mp4` starts at 0.0 s. Stop to clipboard: 1.0 to 1.8 s (upper limits).
- **Video content (offline, Linux and Mono, not on this PC).** A `testsrc2` test video gave 19 false "scroll" rows before the fix and 0 after.
- **Clipboard.** Ctrl+V in the Claude desktop chat attached `index.md` and 4 frames, and the chat read them.
- **Audio and transcript.** The WASAPI capture gave an AAC stream of 12.48 s for a 12.40 s video. whisper.cpp (large-v3-turbo, RTX 4060) wrote a transcript of a spoken test sentence with all words correct.

Not tested in 3.0.0: the sync of audio and video, long recordings with a transcript, a busy 60 s or 5 min recording, a real blinking text cursor, `clip.mp4` in a normal video player, Ctrl+V in claude.ai, Gemini or Explorer, Whisper programs other than whisper.cpp, the exe from the release zip (the test PC ran a local build of the same commit), and a PC without an NVIDIA GPU.

The test kit is in `test/`: a source text, an auto-scroll page, a frame-counter page, and scripts that count frames and score an AI copy.

## More documents

- [CHANGELOG.md](CHANGELOG.md): the version history.
- [HANDOFF.md](HANDOFF.md): requirements, decisions, test plans and full test results.
- [docs/export-frames.md](docs/export-frames.md): a design note for a possible "Export frames" feature. It is not part of the program.
- [docs/audio-transcript.md](docs/audio-transcript.md): the design note for the audio and the transcript, and for the motion note and the scroll fix of 2026-11-20.

## Related repository

[gecko-of-shadow/screen-vibe-recorder](https://github.com/gecko-of-shadow/screen-vibe-recorder) was made on 2026-11-15 from this repository (its `main` is commit `cb07dff`, between 2.0.0 and 2.0.1). Its branch `linux-port` ports the region selector and the other 2.0 features to Ubuntu 24.04 and is tested on GitHub Actions. That port is not merged into this repository and is not released.
