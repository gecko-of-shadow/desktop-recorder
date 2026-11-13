# Desktop Recorder

A small Windows tray program that records your desktop to MP4.

It is made for recordings that an AI model can read: full screen resolution so text stays sharp, and a low frame rate so the files stay small.

## Get it

1. Download `DesktopRecorder.exe` from the [Releases page](../../releases/latest).
2. Install ffmpeg, if you do not have it. In PowerShell:

   ```
   winget install -e --id Gyan.FFmpeg
   ```

3. Start `DesktopRecorder.exe`. A monitor icon appears in the tray. A red screen means recording, a grey screen means stopped.

Windows SmartScreen may warn you, because the program is not signed. Click **More info**, then **Run anyway**.

## Use it

- **Win+Shift+D** starts and stops recording from anywhere. You can change the hotkey in Settings.
- A short sound plays when recording starts and when it stops.
- Right-click the tray icon for: Start/Stop recording, Open recordings folder, Settings, Exit.
- Double-click the tray icon to open Settings.
- When a recording stops, the video file is copied to the clipboard. Press Ctrl+V to paste it into a chat or a folder.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Folder | `C:\Users\<you>\Recordings` | The program makes the folder. |
| Frames per second | 2 | 0.2 to 60. Lower means smaller files. |
| Quality | Balanced | Smallest files, Balanced, or Sharpest text. |
| New file every | 30 minutes | 0 means one file per recording. |
| Hotkey | Win+Shift+D | Win, Ctrl, Alt and Shift, plus a letter, a digit or F1 to F12. Shift alone works only with F1 to F12. |
| Start when Windows starts | Off | Adds one entry for your user account only. |
| Start recording when the program starts | On | |
| Copy the video to the clipboard when recording stops | On | |
| Play a sound when recording starts and stops | On | Two rising tones for start, two falling tones for stop. |

Settings are stored in `%APPDATA%\DesktopRecorder\settings.txt`.

## Good to know

- On Windows, no audio is recorded. On Ubuntu, system audio (what you hear) is recorded unless you turn it off in Settings.
- On Windows, all monitors are recorded as one picture. On Ubuntu with X11, the whole X display is recorded. On Wayland, Ubuntu asks which screen to share and the recorder keeps the first one you pick.
- The MP4 stays playable if the PC loses power in the middle of a recording. You lose about the last 5 seconds at the default settings, and more at a very low frame rate.
- A screen that does not change costs almost no space.

## Problems

**"Protected folder access blocked" from Windows Security.**
Controlled folder access protects Videos, Documents, Pictures, Desktop and Music. The default folder, `C:\Users\<you>\Recordings`, is outside those. If you picked a protected folder, open Settings from the tray icon and pick a different folder. Or allow both `DesktopRecorder.exe` and `ffmpeg.exe` in Windows Security.

**"ffmpeg.exe was not found."**
Run the winget command above, or put `ffmpeg.exe` next to `DesktopRecorder.exe`.

**The hotkey does nothing.**
Another program already uses that hotkey. The recorder shows a warning when that happens. Pick a different hotkey in Settings, or use the tray menu.

## Build it yourself

You need nothing extra. The C# compiler ships with Windows.

Double-click `build.cmd`, or run this in PowerShell in the folder:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll DesktopRecorder.cs
```

Exit the running recorder first, or the build cannot replace the `.exe`.

## How it works

The program is a tray front end for [ffmpeg](https://ffmpeg.org). On Windows it runs ffmpeg with the `gdigrab` screen capture and the H.264 encoder, and writes fragmented MP4. A recording is one file named `desktop_yyyy-mm-dd_hh-mm-ss_mmm.mp4`. When a recording is split, the parts are `desktop_yyyy-mm-dd_hh-mm-ss_mmm_000.mp4` and so on.

## Ubuntu Desktop 24.04

The Linux build is the same tray recorder. X11 uses ffmpeg's `x11grab`. Wayland (the default Ubuntu session) asks the desktop for a screen through xdg-desktop-portal and reads the picture from PipeWire, then ffmpeg encodes it. System audio uses the PulseAudio API, which PipeWire provides on Ubuntu 24.04. The Windows `.exe` is unchanged and still has no audio.

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

Log out and back in if `~/.local/bin` is not on your PATH yet. A monitor icon appears in the tray. On GNOME it uses an app indicator. On an X11 session such as Xfce it uses the classic tray.

- **Super+Shift+D** is the same shortcut as Win+Shift+D. On X11 the program grabs it. On GNOME Wayland it is a custom shortcut named Desktop Recorder. Other Wayland desktops keep the tray menu.
- The first Wayland recording opens the system screen-share dialog. Later recordings reuse that permission.
- System audio is the monitor of the default output. If that device is missing or does not answer, the recording continues with video only and the tray says so.
- Settings are stored in `~/.config/DesktopRecorder/settings.txt`. "Start when I log in" writes `~/.config/autostart/desktop-recorder.desktop`.
- `desktop-recorder --check` prints the capture backend. `desktop-recorder --smoke 2` records two seconds with X11 and exits.

To run the tests on Ubuntu 24.04 (Mono runs the Windows rules; Python runs the Linux rules):

```
sudo apt install mono-devel libmono-system-windows-forms4.0-cil libmono-system-drawing4.0-cil x11-utils xdotool
./run-tests.sh
```
