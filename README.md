# Desktop Recorder

A small tray program that records a part of your screen, so that an AI model can read the text on it. On Windows, one recording gives one ZIP file: the settled frames as PNG files, an `index.md` that describes each frame, and `clip.mp4` for people.

Current version: **3.2.0** (2026-11-25). Download it from the [newest release](https://github.com/gecko-of-shadow/desktop-recorder/releases/latest). See [CHANGELOG.md](CHANGELOG.md) for the version history.

You select the area with the text. The program records that area at full resolution, so each character stays sharp. A still screen costs almost no space, so the files stay small.

- **Windows 10 and 11:** the full program, with the region selector. C# on .NET Framework 4, a tray front end for [FFmpeg](https://ffmpeg.org).
- **Ubuntu Desktop 24.04:** a tray recorder (Python and GTK). Not released yet (branch `improve/ai-context-2026-11-27`): a region selector (X11) and the "Folder for AI" output with the engine of the Windows build. See [docs/linux-ai-folder.md](docs/linux-ai-folder.md).

## Features

Windows:

- **Region selector.** Win+Shift+D opens the selector over all monitors. Drag a rectangle, click a window, or press Space for all monitors. The recording starts when you release the mouse button.
- **One hotkey** starts and stops the recording. The hotkey is a setting.
- **Recording frame.** A red frame and a Stop button show around the recorded area. Windows keeps them out of the video.
- **GPU capture and encode.** `ddagrab` capture and `h264_nvenc` encode by default. The program uses `gdigrab` and `libx264` when the GPU path is not available.
- **"Changes only" mode** (default). Frames that do not change are dropped. A still screen costs almost nothing.
- **Status line** after each recording: the frame count, the `clip.mp4` size and the time until the files are ready (folder mode), or real frames, duplicates, dropped frames and file size ("MP4 only").
- **Folder for an AI** (default, new in 3.0.0). Each recording gives one ZIP file (since 3.1.0). It holds a folder with `frames\001.png` and so on, `index.md` and `clip.mp4`. See [Recording folder](#recording-folder-windows). "MP4 only" gives one MP4 file, as in version 2.0.2.
- **System audio and transcript** (optional, off by default, new in 3.0.0). The sound of the PC goes into the video. When a local Whisper program is present, `index.md` gets a transcript. See [Audio and transcript](#audio-and-transcript-windows).
- The finished recording goes on the **clipboard**: one ZIP file in folder mode, or the video in "MP4 only" mode. Press Ctrl+V to paste it into a chat or a folder.
- **Per-monitor DPI aware.** All positions and sizes are physical pixels.
- FFmpeg stops when the program stops, also after a crash.
- Start and stop sounds, an option to hide the mouse pointer, start with Windows, split long recordings into parts.

Ubuntu 24.04:

- Full-screen recording, or a region (`region=` in settings.txt, or the selector on X11). X11 uses FFmpeg `x11grab`. Wayland uses xdg-desktop-portal and PipeWire.
- System audio (what you hear), through the PulseAudio API that PipeWire provides.
- Not released yet: "Folder for AI" output (settled PNG frames, `index.md`, `clip.mp4`, a ZIP; with Mono). Optional: Whisper transcript (whisper.cpp), OCR text of the frames and black boxes over keys and passwords (tesseract). See [docs/linux-ai-folder.md](docs/linux-ai-folder.md).
- Tray icon, Super+Shift+D shortcut, and the same settings file format as Windows.

## Download and install

Download a portable archive from the [Releases page](https://github.com/gecko-of-shadow/desktop-recorder/releases/latest). Unzip or untar it and run it. Nothing is copied into Program Files, and the Linux archive does not need root.

The Releases page marks the newest version as Latest. The source of each earlier version stays available as a Git tag (`v1.0.0` to `v3.1.0`). [CHANGELOG.md](CHANGELOG.md) lists the changes in each version.

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

- In folder mode, the note box opens in the middle of the monitor of the recording. Type a note for the AI and press Enter (or click **Save note**), or click **No note**. Shift+Enter or Ctrl+Enter starts a new line. The note goes into `index.md` as "Note from Bran:" on one line (line breaks become spaces), or into `note.txt` with its lines if the frame pass fails. The program does not wait for the note. "MP4 only" mode has no note box.
- The note box does not lose typed text. Esc, the X button, Alt+F4 and **No note** close an empty box at once. When the box has text, they ask "Save this note?" in the box: **Save**, **Discard** or **Back** (back to the note). Exit in the tray menu asks the same question first; **Back** keeps the app open. When Windows shuts down, the text is saved. The box stays on top of other windows and does not close when you click another window. When it opens without the keyboard focus, its taskbar button flashes.
- The program makes the frames, `index.md` and `clip.mp4` in the background. When this work is done, the status line in the tray menu ends with "Ready ... s after Stop."
- Until the ZIP is on the clipboard, the app shows that it is busy: the tray icon turns, the tray menu and the note box say "Making the ZIP... (step: frames)" (then transcript, then ZIP), and a small popup at the bottom of the screen says the same when the note box is closed. When the ZIP is ready, the note box and the popup say "ZIP ready: Ctrl+V to paste" with the size, and a short high sound plays (when "Play sounds" is on). The popup does not take the keyboard focus, is not in the video, and closes after 4 s or with a click. When the ZIP fails, the popup and the note box give the reason.
- When "Copy to the clipboard" is on, the clipboard gets a short text at Stop: "Desktop Recorder is still making the ZIP of the last recording. Wait for "ZIP ready", then paste again." An early Ctrl+V pastes this text, not the recording before. When the copy is off, the app does not touch the clipboard.
- In folder mode, the output is one ZIP file only. The ZIP is next to where the recording folder was, and it has the same name as the folder (`desktop_<date>_<time>.zip`). The ZIP holds one folder with `index.md`, `frames\`, `clip.mp4` and, when the program made them, `transcript.srt` and `note.txt`. The clipboard holds the ZIP as a file, together with a text message for the AI that has the path of the ZIP. It never holds the loose files. Ctrl+V in a chat app attaches the ZIP. Ctrl+V in a terminal pastes the text. When the ZIP is made, the program deletes the recording folder. In "MP4 only" mode, the video file goes on the clipboard. The setting "Copy to the clipboard when the recording stops" (Settings, AI and audio) turns off the whole clipboard copy: the ZIP and the message. The ZIP is still made.
- A paste of the loose files into the Claude desktop chat was tested before this change (version 3.0.1 and earlier put the files on the clipboard). A Claude cloud session refused pastes of 8 and 12 files, and that is why the output is one ZIP now. Not tested: that a chat app or a cloud session accepts the ZIP.
- The tray menu shows a status line. Folder mode example: "Last recording: Frames: 5. clip.mp4: 0.12 MB. Ready 3.1 s after Stop." With audio, the line also gives the transcript result. "MP4 only" example: "Last recording: Real frames: 83 of 83. Duplicates: 0. Dropped: 0. File size: 0.05 MB."

The recording area is a fixed rectangle. If a window moves during the recording, the area does not move with it.

Other controls:

- A short sound plays when the recording starts and when it stops.
- Right-click the tray icon for the tray menu. At the top, a status tells the state: not recording, recording (with the elapsed time, the fps and the region size), processing the last recording, or closing. Under it is the result of the last recording. The commands: **Select region and record** (or **Stop recording**, in bold, with the hotkey at the right), Open recordings folder, Show last ZIP in Explorer, Copy ZIP of last recording, Settings, Exit. The ZIP commands are gray when there is no ZIP. The menu opens above the taskbar. While it is open, the status follows the state; the commands change when the menu closes, and a record command that the state made old does nothing.
- The note box and the tray menu follow the Windows app mode (Settings > Personalization > Colors > Choose your default app mode): light or dark. The Settings window is always light.
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
| C | Shows or hides a crosshair across the screen: a white line with a dark line on each side, so that it shows on light and on dark content. |
| M | Shows or hides the magnifier near the cursor. |
| P | Shows or hides the position and size of the selection. |
| Esc | Closes the selector. Nothing is recorded. |

The size label shows the size in physical pixels. The program makes the width and the height even numbers, because H.264 needs even sizes. It removes at most 1 px. It never scales the picture.

### Settings (Windows)

The Settings window has five pages in a list at the left: Recording, Capture and quality, Hotkey and selector, AI and audio, Startup and sounds. Each setting has a help text that gives its default. **Restore defaults** puts the defaults into the fields of the window; **Save** applies them. Ctrl+Tab goes to the next page. When a value is not valid (for example the hotkey), a red text at the bottom tells the problem, and the window opens that page.

| Setting (page) | Default | Notes |
|---|---|---|
| Save recordings to (Recording) | `C:\Users\<you>\Recordings` | The program makes the folder. |
| Frames per second (Recording) | 10 | 0.2 to 60. See [Choose a frame rate](#choose-a-frame-rate). |
| Quality (Capture and quality) | Balanced | Smallest files, Balanced, or Sharpest text. See [Capture and encoding](#capture-and-encoding-windows). In folder mode it applies only to `clip.mp4`. |
| Start a new file every (Recording) | 30 minutes | 0 means one file for each recording. "MP4 only" mode only: the field is off in folder mode, because folder mode does not split. |
| Hotkey (Hotkey and selector) | Win+Shift+D | Win, Ctrl, Alt and Shift, plus a letter, a digit or F1 to F12. Shift alone works only with F1 to F12. The same hotkey opens the selector and stops the recording. |
| Capture method (Capture and quality) | ddagrab (GPU) | ddagrab or gdigrab. |
| Encoder (Capture and quality) | Auto | Auto (NVENC, else x264), NVENC only, or x264 only. |
| Color detail (Capture and quality) | 4:2:0 | 4:2:0 or 4:4:4. In folder mode it applies only to `clip.mp4`. |
| Frame-rate mode (Recording) | Changes only | Fixed frame rate, or Changes only. |
| Output (Recording) | Folder for AI | Folder for AI (frames, `index.md`, `clip.mp4`), or MP4 only. See [Recording folder](#recording-folder-windows). |
| Start with Windows (Startup and sounds) | Off | Adds one entry for your user account only. The switch shows Off when Windows has the app turned off in Settings > Apps > Startup. The program does not change that Windows switch. |
| Copy to the clipboard when the recording stops (AI and audio) | On | In folder mode: the ZIP and the text message. |
| Play sounds (Startup and sounds) | On | Two rising tones for start, two falling tones for stop. |
| Hide the mouse pointer (Capture and quality) | On | Off shows the pointer in the video and in the frames. See [Good to know](#good-to-know). |
| Record the sound of the PC (AI and audio) | Off | Makes a transcript when Whisper is installed. The page tells if the app finds Whisper. See [Audio and transcript](#audio-and-transcript-windows). |
| Crosshair, Magnifier, Position and size (Hotkey and selector) | Off, On, On | The keys C, M and P in the selector change them too. |

Settings are stored in `%APPDATA%\DesktopRecorder\settings.txt`.

The app does not record when it starts. Every recording starts with the hotkey and an area that you select. The setting "Record when the app starts" (`record_on_launch`) was removed on 2026-11-22. An old `record_on_launch` line stays in `settings.txt`, because the Linux build uses that key. The Windows app ignores the line.

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

This is the default output since version 3.0.0. Since version 3.1.0, the app makes one ZIP of this folder after the frame pass, and then deletes the folder. The structure below is the structure inside the ZIP. The folder stays only when the frame pass fails.

```
desktop_2026-11-19_13-01-41\
    clip.mp4
    index.md
    frames\
        001.png
        002.png
```

- **`frames\`** has the settled frames only, as PNG files with no loss. A settled frame is the first frame after the screen stops changing for 400 ms. A large change (a page, a menu, a dialog) also counts as settled when only small parts still move, for example the mouse pointer. A page that shows for 2 captures (0.33 s at 6 fps) gets a frame too. A part that only pulses between the same states gets one frame each 5 seconds. A blinking text cursor makes no new frame. A small change that stays makes a new frame.
- During a long scroll, the program writes a frame each time the content moved 60% of the scrolled area, so no text is lost between two frames. When the screen changes without a stop, it writes a frame every 5 seconds.
- **`index.md`** starts with an instruction for the AI: the files are one screen recording, and they are context for your message. Then it has the start time with the UTC offset of the PC, your note, the length, the region size, the frame count, and one row for each frame: the time, the window title and what changed. It warns when the ZIP has more than 19 frame images (with `index.md`, more than 20 files): a chat app that unpacks the ZIP can limit the number of images that it reads, and some chat apps take only 20 files in one message. It also warns when the long side is above 2576 px, and when a frame has more than 3.75 million pixels. Current Claude models read images up to these sizes without making them smaller. Older Claude models make images with a long side above 1568 px smaller.
- **`clip.mp4`** is the video for people. Each frame in `frames\` is a keyframe of `clip.mp4`, at the time that `index.md` gives. The clip has no other keyframes.
- **Show last ZIP in Explorer** (tray menu) opens Explorer with the ZIP selected. **Copy ZIP of last recording** puts the existing ZIP on the clipboard. It does not make the ZIP again, because the folder is gone. Both items are off when the ZIP is not there. The ZIP leaves out the work file and the WAV files. It stores the PNG and MP4 files without compression (they are compressed already) and compresses the text files. The ZIP format is the normal Deflate format.

How it works: FFmpeg records a lossless 4:4:4 work file. After Stop, a background pass reads the work file, writes the frames, the index and the clip, and then deletes the work file. A recording in this mode is not split into parts. The Quality and Color detail settings apply only to `clip.mp4`. The work file and the PNG frames are always lossless.

If the pass fails, the work file stays and the program writes a normal `clip.mp4`. Your note then goes into `note.txt`, because there is no `index.md`. The program still makes the ZIP and puts it on the clipboard with a message for the AI that says the pass failed. In this case the program keeps the recording folder, because it holds the lossless work file. It also keeps the folder when the sound is not in `clip.mp4` (the folder has `audio_system.wav`). When a pass fails, the note box and the popup say so; they do not say "ZIP ready".

If FFmpeg stops by itself, the part until then becomes its own ZIP. When a UAC prompt, the lock screen or a display change ended the capture, the recording goes on in a new ZIP, if the screen comes back in 2 minutes. If the program or the PC stops before the ZIP is made, the program makes the ZIP at its next start (not on the clipboard).

These settings have no control in the Settings window. Change them in `settings.txt`: `settle_ms` (400), `still_interval_s` (5), `scroll_percent` (60), `size_limit` (2576). A `size_limit=1568` from version 3.0.0 (the old default) is read as 2576.

When the picture changes almost all the time (for example a video or a stream), it never settles. Then `index.md` has a warning line: the frames in that time are samples every 5 seconds, and the AI must use `clip.mp4` for the motion. Only a large change counts for this warning: one changed area with 96 px or more on its short side (half the short side of the region, when the region is smaller), or many small changed parts that together cover 96 x 96 px or more. A moving mouse pointer, a blinking text cursor and a small spinner do not count. A small change under a moving pointer (for example a tooltip) can wait for the next stop or for the sample every 5 seconds.

## Audio and transcript (Windows)

New in 3.0.0. Tested on one Windows 11 PC: the audio capture, and a transcript with whisper.cpp. Not tested: the sync of audio and video, long recordings with a transcript, and the Whisper programs other than whisper.cpp. Details: [docs/audio-transcript.md](docs/audio-transcript.md).

- Turn on "Record the sound of the PC" in Settings (page AI and audio). It is off by default. The page tells if the app finds a Whisper program.
- The program records what the default playback device plays (WASAPI loopback). When the default playback device changes during the recording, the program records the new device. The Stop sound of the program is not in the audio. No extra driver and no "Stereo Mix" device are necessary. The microphone is not recorded.
- Folder mode: `clip.mp4` gets the audio. "MP4 only" mode: each video file gets the audio after Stop.
- Transcript: set `whisper_path`, or put a Whisper program next to `DesktopRecorder.exe` (or in a subfolder `whisper`, `whisper.cpp` or `Faster-Whisper-XXL`), or on PATH. The program uses `whisper_path` first, then the folder of the app, then PATH. If `whisper_path` names a file that does not exist, there is no transcript. It looks for `whisper-cli.exe` (whisper.cpp), `faster-whisper-xxl.exe`, `whisper-ctranslate2.exe`, and `whisper.exe` (only on PATH, because the name is too general). The transcript goes into `index.md` (section "Audio and transcript") and into `transcript.srt`. Its times are on the time line of `clip.mp4`.
- whisper.cpp needs a model file `ggml-*.bin`: next to the program, in its `models` folder, or in `models` or `whisper\models` next to the app. With more than one model, the program uses the first of large-v3-turbo, medium, small, base, large and tiny.
- Without Whisper, the recording still has the audio, and `index.md` says that there is no transcript.
- Whisper can run for at most 2 minutes plus 3 times the recording length (at most 1 hour). If it takes longer, or if the audio is silent, `index.md` gives the reason that there is no transcript.
- Whisper does not run when the recording has less than 0.5 s of sound, and sound in less than 10% of its time (for example only the stop sound). `index.md` then says that the audio was silent or almost silent.
- Whisper writes text such as "Thank you." on silent or almost silent audio. The program measures the sound in the time of each transcript row (100 ms parts, sound = -90 dBFS or louder, that is 1 LSB of 16-bit audio). It leaves out a row that has sound in less than 10% of its time and for less than 0.4 s in total (1 s for a known text). A known Whisper text for silence (for example "Thank you." or "Thanks for watching!") in a long row (10 s or more, or most of a short recording) needs sound in half of its time. A short row with a known text stays, because a real "Thanks." is short. `index.md` says how many rows the program left out. When no row is left, it says "No speech was found", and the ZIP has no `transcript.srt`.
- Whisper can start a row at the start of its 30 s window when the speech comes later. A row with 1 s or more of digital silence at its start or end gets the time of its first and last sound (0.2 s margin).
- If the audio cannot go into the video, it stays as a WAV file: `audio_system.wav` in the recording folder, or `desktop_<time>.wav` next to the MP4 files.
- Settings without a control (edit `settings.txt`): `whisper_path` (a full path to the program), `whisper_model` (whisper.cpp: the model file; other programs: a model name), `whisper_language` (`auto`, or a code such as `en`).
- Whisper starts at Stop, at the same time as the frames and the clip. The ZIP waits at most 8 s for it. A slower transcript is added later: the program makes the same ZIP again, and the note box and the popup say so. Exit waits at most 10 s for a slow transcript.

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
- The mouse pointer is hidden by default (3.0.0 and earlier showed it). A shown pointer that moves keeps the screen from settling: in "Changes only" mode each pointer move adds a frame, and in folder mode the pointer can show in the PNG frames. Hover effects in other programs (button colors, tooltips) still add frames.

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
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /win32manifest:DesktopRecorder.manifest /win32icon:app.ico RecorderCore.cs RecordingSession.cs NativeMethods.cs RegionSelector.cs RecordingFrame.cs SettledFrames.cs AfterRecording.cs AudioTranscript.cs AudioCapture.cs UiModel.cs UiTheme.cs NoteForm.cs SettingsForm.cs TrayMenu.cs DesktopRecorder.cs
```

The app manifest (`DesktopRecorder.manifest`) makes the program per-monitor DPI aware and holds the version number.

To run the unit tests on Windows, build them:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /out:"$env:TEMP\CoreTests.exe" /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll tests\CoreTests.cs tests\AiCaptureTests.cs tests\AudioTests.cs tests\UiTests.cs UiModel.cs RecorderCore.cs RecordingSession.cs SettledFrames.cs AudioTranscript.cs AfterRecording.cs
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

Not tested: PCs with two or more monitors (also `gdigrab` with an area across two monitors), monitors with different scales, the `libx264` fallback in the program, PCs without an NVIDIA GPU, and AI tools other than Claude. The selector keys (`~`, Shift and Ctrl while you drag, C, M, P, the arrow keys and Esc) were tested in 3.1.0 (`HANDOFF.md` section 7.22).

Version 3.0.0 (folder output, audio, transcript) was tested on the same PC with Windows 11. `HANDOFF.md` sections 7.5 to 7.10 have the full tables. Summary:

- **Lossless capture.** Counter test at 15 fps for 10 s: 150 of 150 frames, in "Changes only" and in fixed mode. This test ran before the `-bf 0` change and did not run again after it.
- **Settled frames (offline).** A status line that changed 5 times gave 6 frames, one for each status. A blinking text cursor for 30 s gave 1 frame. 15 of 15 look-alike changes were caught. A scroll of 100 lines and 5 settings pages: 0 px missed.
- **Real recordings.** Status line: 6 frames, each status in one frame. Settings tour: all 5 pages, no blended frame. The keyframes of `clip.mp4` equal the PNG count in all folders, and `clip.mp4` starts at 0.0 s. Stop to clipboard: 1.0 to 1.8 s (upper limits).
- **Video content (offline, Linux and Mono, not on this PC).** A `testsrc2` test video gave 19 false "scroll" rows before the fix and 0 after.
- **Clipboard.** Ctrl+V in the Claude desktop chat attached `index.md` and 4 frames, and the chat read them.
- **Audio and transcript.** The WASAPI capture gave an AAC stream of 12.48 s for a 12.40 s video. whisper.cpp (large-v3-turbo, RTX 4060) wrote a transcript of a spoken test sentence with all words correct.

Not tested in 3.0.0: the sync of audio and video, long recordings with a transcript, a busy 60 s or 5 min recording, a real blinking text cursor, `clip.mp4` in a normal video player, Ctrl+V in claude.ai, Gemini or Explorer, Whisper programs other than whisper.cpp, the exe from the release zip (the test PC ran a local build of the same commit), and a PC without an NVIDIA GPU.

Version 3.0.1 (new defaults and index.md warnings): the unit tests passed and the app built on the same PC. The exe from the 3.0.1 release zip started on that PC and did not start a recording. Not tested: a recording with the 3.0.1 exe, and the first start with no settings file.

Version 3.1.0, transcript rows on silence, pointer and the motion warning, file-count text (`HANDOFF.md` section 7.19), offline on the same PC with five real recordings by Bran:

- **Transcript.** Three recordings with silent audio got "Thank you." rows from whisper.cpp. The program now leaves out all of them (2 of 2, 1 of 1 and 1 of 1), and `index.md` says "No speech was found". Two recordings with real speech kept all their rows (50 of 50 at about -60 dBFS, and 3 of 3). The quiet speech also kept all its rows when the audio was 10 dB and 20 dB quieter. The whisper.cpp options `-sns`, `-lpt` and `-nth` did not stop the false rows. Silero VAD removed them, but also most of the quiet real speech, so the program does not use VAD.
- **Motion warning.** The pointer was shown in these recordings, and their `index.md` said "the picture changed during" 81% to 92%. The lossless work files are gone, so the pass ran again on each `clip.mp4`. The motion share fell from 84% to 94% to 10% to 26%: no false warning. The frame count and the PNG files did not change. The offline tests of 3.0.0 gave the same results (status line, blinking cursor, 15 of 15 look-alike changes, scroll 0 px missed), and test videos with real motion (`testsrc2`, `mandelbrot`, `life`, a video area on a page) still get the warning (97% to 100%).

Version 3.1.0, live test of the ZIP-only build (note box, Settings window, tray menu, ZIP indication and the selector keys; `HANDOFF.md` section 7.22), on the same PC with synthetic input and real recordings: every check of the live test plan passed after 8 fixes (tray menu over the taskbar, crosshair on white pages, mixed tray status, two spaces for a line break in the note, a temporary file after a failed ZIP, and others). The full-screen check found no note box and no popup in the video. Not tested: 100% and 150% scale, a paste of the ZIP into a chat app, more monitors.

Version 3.1.0, real recordings by Bran with the ZIP-only build (`HANDOFF.md` section 7.23): each recording gave one ZIP and no folder, a note was in the ZIP, a Twitch stream got the motion warning, and the transcript kept real speech with no false rows.

Version 3.2.0 (fast ZIP, lost recordings, missed frames; `HANDOFF.md` sections 7.26 and 7.27), on the same PC: the unit tests and the offscreen UI preview passed. Offline passes on 100 test videos kept the frame counts of the baseline tests and added frames that the old version missed. A killed FFmpeg recording was recovered into a ZIP. One real recording by Bran (24.3 s, with sound, build of 1cc03e3) gave a complete ZIP about 5 s after Stop, and the two faults that it showed are corrected. Not tested: a recording with the final code, a real UAC prompt or lock screen, a change of the sound device, a sign-out, the sync of audio and video, and the exe from the release zip.

The test kit is in `test/`: a source text, an auto-scroll page, a frame-counter page, and scripts that count frames and score an AI copy.

## More documents

- [CHANGELOG.md](CHANGELOG.md): the version history.
- [HANDOFF.md](HANDOFF.md): requirements, decisions, test plans and full test results.
- [docs/export-frames.md](docs/export-frames.md): a design note for a possible "Export frames" feature. It is not part of the program.
- [docs/audio-transcript.md](docs/audio-transcript.md): the design note for the audio and the transcript, and for the motion note and the scroll fix of 2026-11-20.

## Related repository

[gecko-of-shadow/screen-vibe-recorder](https://github.com/gecko-of-shadow/screen-vibe-recorder) was made on 2026-11-15 from this repository, at commit `cb07dff` (between 2.0.0 and 2.0.1). It released Screen Vibe Recorder 2.1.0 on 2026-11-16: Desktop Recorder 2.0.0 plus a "Changes only" mode that keeps one still frame each 60 s. It does not have the Desktop Recorder commits of 2.0.1 to 3.0.1 (folder output, audio, transcript). Its branch `linux-port` ports the region selector and the other 2.0 features to Ubuntu 24.04 and is tested on GitHub Actions. That port is not merged into this repository or into its `main`, and it is not released.
