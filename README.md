# Desktop Recorder

A tray program that records a part of your screen to MP4, so that an AI model can read the text in the video.

## What it does

You select the area with the text. The program records that area at full resolution, so each character stays sharp. In the default "Changes only" mode, frames that do not change are dropped, so a still screen costs almost no space.

- **Windows 10 and 11:** the full program, with the region selector. C# on .NET Framework 4, a tray front end for [FFmpeg](https://ffmpeg.org).
- **Ubuntu Desktop 24.04:** the earlier full-screen tray recorder (Python and GTK). It has no region selector. It has not changed since version 1.0.0.

## Status

Latest release: **[v2.0.2](https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v2.0.2)** (2026-11-15). See [CHANGELOG.md](CHANGELOG.md) for all versions.

## Download and install

Download a portable archive from the [latest release](https://github.com/gecko-of-shadow/desktop-recorder/releases/latest). Unzip or untar it and run it. Nothing is copied into Program Files. The Linux archive does not need root.

| Platform | Archive | Start |
|---|---|---|
| Windows 10 and 11 (x64) | `desktop-recorder-<version>-windows-x64.zip` | `DesktopRecorder.exe` |
| Ubuntu 24.04 (x86_64) | `desktop-recorder-<version>-linux-x86_64.tar.gz` | `./desktop-recorder` |

FFmpeg is not in the archives, because an FFmpeg build that records H.264 links libx264 and is GPL. Install it yourself.

**Windows.** .NET Framework 4 is part of Windows 10 and 11. Install FFmpeg in PowerShell:

```
winget install -e --id Gyan.FFmpeg
```

Or put `ffmpeg.exe` next to `DesktopRecorder.exe`. The GPU capture (`ddagrab`) needs FFmpeg 6.0 or later. The program encodes on an NVIDIA GPU when the PC has one, and on the CPU when it does not. The program is not signed, so Windows SmartScreen can show a warning. Click **More info**, then **Run anyway**.

**Ubuntu 24.04.** The `README.txt` in the archive lists the apt packages that the program needs. It is the same list as in [Build from source](#ubuntu-2404).

## Usage

### Windows

1. Press **Win+Shift+D**. The region selector opens over all monitors. The screen stops and goes darker.
2. Select the area. Drag a rectangle and release the mouse button, or click a window, or press **Space** for all monitors. The recording starts at once.
3. A red frame and a **Stop** button show around the area. Windows keeps them out of the video.
4. To stop, press **Win+Shift+D** again, or click **Stop**.

When the recording stops, the video file goes on the clipboard, and the tray menu shows a status line (real frames, duplicates, dropped frames, file size). The area is a fixed rectangle. It does not follow a window that moves. A sound plays at start and stop. Right-click the tray icon for the menu. Double-click it to open Settings.

Region selector keys:

| Key | Result |
|---|---|
| Drag, then release | Records the rectangle. |
| Click | Records the window under the cursor (its visible frame, without the shadow). |
| Shift / Ctrl while you drag | Keeps the selection square / moves the rectangle. |
| Arrow keys | Move the cursor 1 px (Shift: 10 px). |
| Enter | Records the current selection. |
| Space | Records all monitors. |
| `~` | Records the monitor under the cursor. |
| C / M / P | Shows or hides the crosshair / magnifier / position and size. |
| Esc | Closes the selector. Nothing is recorded. |

Sizes are physical pixels. The program makes the width and height even (H.264 needs even sizes). It removes at most 1 px and never scales the picture.

Settings (stored in `%APPDATA%\DesktopRecorder\settings.txt`):

| Setting | Default | Notes |
|---|---|---|
| Save recordings to | `C:\Users\<you>\Recordings` | The program makes the folder. |
| Frames per second | 10 | 0.2 to 60. See [Choose a frame rate](#choose-a-frame-rate). |
| Quality | Balanced | Smallest files, Balanced, or Sharpest text. |
| Start a new file every | 30 minutes | 0 means one file for each recording. |
| Hotkey | Win+Shift+D | Win, Ctrl, Alt and Shift, plus a letter, a digit or F1 to F12. Shift alone works only with F1 to F12. |
| Capture | ddagrab (GPU) | ddagrab or gdigrab. |
| Encoder | Auto | Auto (NVENC, else x264), NVENC only, or x264 only. |
| Color detail (chroma) | 4:2:0 | 4:2:0 or 4:4:4. |
| Frame-rate mode | Changes only | Fixed frame rate, or Changes only. |
| Start Desktop Recorder when Windows starts | Off | One entry for your user account only. |
| Start recording (saved region) when Desktop Recorder starts | On | Records the last area. With no saved area, or an area that is not on the screen now, it records the full desktop. |
| Copy the video to the clipboard when recording stops | On | |
| Play a sound when recording starts and stops | On | |
| Hide the mouse pointer in the video | Off | In "Changes only" mode, each pointer move adds a frame. |
| Region selector: Crosshair, Magnifier, Position | Off, On, On | Keys C, M and P change them too. |

When you click Save during a recording, the recording continues. New capture settings apply to the next recording. A new hotkey applies at once.

### Ubuntu 24.04

Start `desktop-recorder`. A monitor icon shows in the tray (an app indicator on GNOME, the classic tray on X11 sessions such as Xfce).

- **Super+Shift+D** is the shortcut (the same keys as Win+Shift+D on Windows). On X11 the program grabs the keys. On GNOME Wayland it adds a custom shortcut named Desktop Recorder. Other Wayland desktops use the tray menu.
- X11 records the whole X display with FFmpeg `x11grab`. Wayland uses xdg-desktop-portal and PipeWire: Ubuntu asks which screen to share, and later recordings use that permission again.
- System audio (the monitor of the default output) is recorded unless you turn it off in Settings. If that device is missing or does not answer within 2 seconds, the recording continues with video only.
- Settings are stored in `~/.config/DesktopRecorder/settings.txt`, in the same format as on Windows.
- `desktop-recorder --help` shows the options. `--check` prints the capture backend. `--smoke 2` records two seconds with X11 and exits.

### Capture and encoding (Windows)

- **Capture.** `ddagrab` (default) uses the Desktop Duplication API on the GPU and records one monitor. `gdigrab` uses GDI on the CPU and can record an area across two monitors. The program uses `gdigrab` when the area crosses two monitors, when there is no area and more than one monitor, or when `ddagrab` fails at the start.
- **Encoder.** Auto uses `h264_nvenc` on an NVIDIA GPU, else `libx264` with preset `veryfast`. NVENC cannot encode an area smaller than about 146x50 px (the limit changes with the GPU) or larger than 4096x4096 px. For these sizes the program uses `libx264`.
- **Quality.** Smallest files: NVENC CQ 28, x264 CRF 32. Balanced: CQ 23, CRF 27. Sharpest text: CQ 17, CRF 21.
- **Chroma.** 4:2:0 plays everywhere. 4:4:4 keeps full color detail, but some browsers and players cannot play it.
- **Changes only.** FFmpeg drops frames that do not change (`mpdecimate`, variable frame rate). Since 2.0.1 the thresholds are lower, so one-character changes (for example "x.y" to "x,y") are kept. The file keeps at least one frame each second, so its length stays correct.
- **Output.** Fragmented MP4, named `desktop_yyyy-mm-dd_hh-mm-ss_mmm.mp4`. Split parts end in `_000`, `_001` and so on. If the PC loses power, the file stays playable and you lose about the last 5 seconds at the default settings. Windows records no audio.

### Choose a frame rate

When text scrolls, each frame must overlap the frame before it. Use this rule:

```
lowest safe fps = 1.5 x scroll speed (physical px/s) / (area height in px - 2 text lines)
```

Physical px/s = page px/s x the Windows display scale. Example: an area 892 px high, text lines 31 px high, 1000 page px/s at 125% scale: 1.5 x 1250 / 830 = 2.3 fps. In "Changes only" mode a higher frame rate costs space only while the screen changes, so 10 fps is a safe choice for most scrolls.

### Troubleshooting

- **Part of the red frame shows in the video.** This occurs only when the area touches the screen edge. Windows 10 version 2004 or later is necessary to keep the frame out of the video.
- **The recording uses x264, not NVENC.** The tray balloon at the start gives the reason. Update the NVIDIA driver, or select a larger area.
- **"Protected folder access blocked".** Controlled folder access protects Videos, Documents, Pictures, Desktop and Music. Select a different folder in Settings, or allow `DesktopRecorder.exe` and `ffmpeg.exe` in Windows Security.
- **"ffmpeg.exe was not found."** Run the winget command above, or put `ffmpeg.exe` next to `DesktopRecorder.exe`.
- **The hotkey does nothing.** Another program uses that hotkey. Select a different hotkey in Settings, or use the tray menu.
- **Ubuntu: `~/.local/bin/desktop-recorder` is not found.** Log out and back in, or run `export PATH="$HOME/.local/bin:$PATH"`.

## Build from source

### Windows

You need nothing extra: the C# compiler (`csc.exe`) is part of .NET Framework 4. Exit the running recorder first, or the build cannot replace the `.exe`.

Double-click `build.cmd`. It builds `DesktopRecorder.exe` in the repository folder and installs FFmpeg with winget if FFmpeg is missing. Or run this in PowerShell:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /win32manifest:DesktopRecorder.manifest /win32icon:app.ico RecorderCore.cs RecordingSession.cs NativeMethods.cs RegionSelector.cs RecordingFrame.cs DesktopRecorder.cs
```

`DesktopRecorder.manifest` makes the program per-monitor DPI aware and holds the version number.

Unit tests (they print `ok` when all checks pass):

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /out:"$env:TEMP\CoreTests.exe" tests\CoreTests.cs RecorderCore.cs RecordingSession.cs
& "$env:TEMP\CoreTests.exe"
```

### Ubuntu 24.04

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

`./build-linux.sh` alone only byte-compiles `linux/desktop_recorder`. With `--install` it copies the program to `~/.local/share/desktop-recorder` and writes `~/.local/bin/desktop-recorder` and `~/.local/share/applications/desktop-recorder.desktop`. Set `PREFIX` to install somewhere else.

Tests (Mono runs the Windows rules, Python runs the Linux rules):

```
sudo apt install mono-devel libmono-system-windows-forms4.0-cil libmono-system-drawing4.0-cil x11-utils xdotool
./run-tests.sh
```

### Release archives

A `v*` tag starts `.github/workflows/release.yml`. It builds the Windows zip (`packaging/make-windows-zip.sh`) and the Ubuntu archive (`packaging/make-linux-tarball.sh`) and attaches both to the GitHub release. The tag message becomes the release notes.

## Project layout

```
DesktopRecorder.cs      Windows tray app and Settings window
RecorderCore.cs         settings, capture plan and FFmpeg arguments
RecordingSession.cs     runs FFmpeg and reads its statistics
RegionSelector.cs       region selector
RecordingFrame.cs       red frame and Stop button
NativeMethods.cs        Windows API declarations
linux/desktop_recorder/ Ubuntu program (Python, GTK)
packaging/              archive scripts, icons, archive README files
tests/                  unit tests (C# and Python)
test/                   test kit: scroll and frame-counter pages, frame and score scripts
docs/                   design notes
HANDOFF.md              requirements, decisions and full test results
```

Tests on one PC (one 1920x1080 monitor at 125%, RTX 4060) found no lost frames at 10 fps with any capture and encoder setting. Not tested: two or more monitors, monitors with different scales, PCs without an NVIDIA GPU, and the `libx264` fallback in the program. `HANDOFF.md` sections 6.5 to 6.10 have the full results.

Related: [gecko-of-shadow/screen-vibe-recorder](https://github.com/gecko-of-shadow/screen-vibe-recorder) was made from this repository on 2026-11-15 (from commit `cb07dff`). Its unmerged, unreleased branch `linux-port` ports the region selector and the other 2.0 features to Ubuntu 24.04.

## License

The repository has no license file.
