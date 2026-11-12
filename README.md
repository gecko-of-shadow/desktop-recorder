# Desktop Recorder

A small Windows tray program that records your desktop to MP4.

It is made for recordings that an AI model can read: full screen resolution so text stays sharp, and a low frame rate so the files stay small.

## Get it

1. Download `DesktopRecorder.exe` from the [Releases page](../../releases/latest).
2. Install ffmpeg, if you do not have it. In PowerShell:

   ```
   winget install -e --id Gyan.FFmpeg
   ```

3. Start `DesktopRecorder.exe`. A round icon appears in the tray. Red means recording, grey means stopped.

Windows SmartScreen may warn you, because the program is not signed. Click **More info**, then **Run anyway**.

## Use it

- **Win+Shift+D** starts and stops recording from anywhere.
- Right-click the tray icon for: Start/Stop recording, Open recordings folder, Settings, Exit.
- Double-click the tray icon to open Settings.
- When a recording stops, the video file is copied to the clipboard. Press Ctrl+V to paste it into a chat or a folder.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Folder | `Videos\Desktop Recordings` | The program makes the folder. |
| Frames per second | 2 | 0.2 to 60. Lower means smaller files. |
| Quality | Balanced | Smallest files, Balanced, or Sharpest text. |
| New file every | 30 minutes | 0 means one file per recording. |
| Start when Windows starts | Off | Adds one entry for your user account only. |
| Start recording when the program starts | On | |
| Copy the video to the clipboard when recording stops | On | |

Settings are stored in `%APPDATA%\DesktopRecorder\settings.txt`.

## Good to know

- No audio is recorded.
- All monitors are recorded as one picture.
- The MP4 stays playable if the PC loses power in the middle of a recording.
- A screen that does not change costs almost no space.

## Problems

**"Protected folder access blocked" from Windows Security.**
Controlled folder access protects Videos, Documents, Pictures, Desktop and Music. Open Settings from the tray icon and pick a folder outside those, such as `C:\Users\<you>\Recordings`. Or allow both `DesktopRecorder.exe` and `ffmpeg.exe` in Windows Security.

**"ffmpeg.exe was not found."**
Run the winget command above, or put `ffmpeg.exe` next to `DesktopRecorder.exe`.

**The hotkey does nothing.**
Another program already uses Win+Shift+D. The recorder shows a warning at start when that happens. Use the tray menu instead.

## Build it yourself

You need nothing extra. The C# compiler ships with Windows.

Double-click `build.cmd`, or run this in PowerShell in the folder:

```
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopRecorder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll DesktopRecorder.cs
```

Exit the running recorder first, or the build cannot replace the `.exe`.

## How it works

The program is a tray front end for [ffmpeg](https://ffmpeg.org). It runs ffmpeg with the `gdigrab` screen capture and the H.264 encoder, and writes fragmented MP4.
