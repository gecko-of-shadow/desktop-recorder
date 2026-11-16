# Changelog

All notable changes to Desktop Recorder are in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).
Dates are the GitHub release dates (UTC). This file was made from the GitHub release notes and the commit history.

## [Unreleased]

### Changed

- README: shorter, in a fixed order (description, status and latest release, download, usage, build, layout, license). It links to the latest release and says that the repository has no license file. No program change.

## [2.0.2] - 2026-11-15

### Changed

- Windows: the tray icons have more detail from Bran's icon set. All lines have a soft glow. The recording icon has a pink-to-blue ring, a red dot and red sound-wave bars. The brackets stay crisp at 16 to 24 px.
- Release workflow: the release notes now come from the tag message, not from the commit message.

### Notes

- Ubuntu: no change.

## [2.0.1] - 2026-11-15

### Fixed

- Windows: "Changes only" mode keeps small text changes. With the default `mpdecimate` thresholds, the filter dropped some one-character changes (for example "x.y" to "x,y", "a:b" to "a;b", "a'b" to "a`b") when the change stayed on the screen for less than 1 s, and showed a longer change up to 0.7 s late. The filter now uses `hi=256`, `lo=128`, `frac=0.33`. In the offline test it caught 15 of 15 changes at once, with about the same number of frames.

### Added

- Design note `docs/export-frames.md`: an idea for an "Export frames" feature. It does not change the program.
- Test files `test/lookalike.py` and `test/lookalike_render.cs`.

### Changed

- Code comments now match the test results. No code change.
- `packaging/make-windows-zip.sh` gives the paths to Python as arguments. The zip step of the v2.0.0 release workflow failed because of this.

### Notes

- Ubuntu: no change.
- Not tested: look-alike changes in a real screen capture, and frame rates other than 10 fps.

## [2.0.0] - 2026-11-15

A new Windows program. The Ubuntu program did not change.

### Added

- Region selector. Win+Shift+D opens it over all monitors. Drag a rectangle and release the mouse button, or click a window. The recording starts at once. Space records all monitors, `~` the monitor under the cursor, Esc cancels. Options: crosshair, magnifier, position text (keys C, M, P).
- Recording frame: a red frame and a Stop button around the recorded area. Windows keeps them out of the video.
- GPU capture and encode: `ddagrab` and `h264_nvenc` by default. The program uses `gdigrab` and `libx264` when the GPU path is not available or fails at the start.
- "Changes only" frame-rate mode (`mpdecimate`, variable frame rate). The file keeps at least one frame each second, so its length stays correct.
- New settings: capture, encoder, chroma (4:2:0 or 4:4:4), frame-rate mode, hide the mouse pointer, and the selector options.
- Status line after each recording: real frames, duplicates, dropped frames, file size.
- Per-monitor DPI awareness (app manifest). All positions and sizes are physical pixels.
- New program icon and tray icons.
- Test kit in `test/` (scroll page, frame-counter page, frame and score scripts).

### Changed

- One hotkey: Win+Shift+D opens the selector and also stops the recording.
- Default frame rate is 10 fps (was 2).
- Default frame-rate mode is "Changes only".
- Start and stop are faster. The NVENC check runs in the background when the program starts. Stop removes the frame at once and FFmpeg finishes the file in the background.
- Save in Settings does not restart a running recording. New capture settings apply to the next recording.

### Fixed

- FFmpeg stops when the program stops, also after a crash (Windows job object).
- The Settings dialog layout at 125% scale: the labels covered the input fields.

### Notes

- Tested on one PC (one 1920x1080 monitor at 125%, RTX 4060, FFmpeg 2026-04-30). No frame was lost at 10 fps with any capture and encoder setting, or at 30 fps with `ddagrab` and NVENC. An AI read test caught 300 of 300 lines of a scrolling text page.
- Not tested: PCs with two or more monitors, monitors with different scales, PCs without an NVIDIA GPU, the `libx264` fallback in the program, and some selector keys (`~`, C, M, P, and Shift and Ctrl while you drag).
- The Windows zip of this release was built on the test PC from tag v2.0.0, because the zip step of the release workflow failed. The Ubuntu archive comes from the release workflow.

## [1.0.0] - 2026-11-13

First release, with portable archives for Windows and Ubuntu 24.04. FFmpeg is not inside the archives, because an H.264 build links libx264 and is GPL.

### Added

- Windows tray recorder: records the full desktop to MP4 with FFmpeg. Tray menu, Settings window, Win+Shift+D hotkey, start with Windows, copy of the finished video to the clipboard.
- Start and stop sounds (a setting turns them off), and a hotkey setting (Win, Ctrl, Alt, Shift plus a letter, a digit or F1 to F12).
- Ubuntu 24.04 tray program (GTK) with the same settings file. X11 uses FFmpeg `x11grab`. Wayland uses the xdg-desktop-portal ScreenCast and GStreamer `pipewiresrc`, then FFmpeg encodes. System audio through the PulseAudio API of PipeWire. If the audio device is missing or does not answer within 2 seconds, the recording continues video-only. Windows records no audio.
- Release workflow: a `v*` tag builds a Windows zip and an Ubuntu tar.gz and attaches them to the GitHub release.

### Changed

- The default folder is `C:\Users\<you>\Recordings`, not Videos. Controlled folder access blocked writes to Videos.
- When FFmpeg is missing, the message gives the winget command.
- The tray icon is a monitor shape: grey when stopped, red with a white dot when recording.
- A killed recording stays playable: FFmpeg writes a fragment every 2 seconds.

### Fixed

- `fps=NaN` or `Infinity` in the settings no longer crashes the Settings window or the argument builder. Those values fall back to 2 fps.
- When FFmpeg fails, the tray shows FFmpeg's own error text.
- Stop and a second start can no longer overlap, and a requested stop does not show a failure balloon.
- The clipboard only receives files from the recording that just finished.
- Output paths with quotes, trailing backslashes or `%` are passed to FFmpeg safely. Split recordings use a millisecond stamp and a part number.
- The hotkey parser accepts only a letter, a digit or F1 to F24.
- Settings are replaced atomically, and lines the Windows build does not understand are kept.
- A clipboard failure shows a warning. The folder window and the tray icon are released on exit.
- Split files cut on time: a keyframe is forced at each split boundary.

### Notes

- Not tested: a GNOME Wayland session, real audio samples, and the Windows program on a Windows desktop (the exe was built by GitHub Actions).

[Unreleased]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.2...HEAD
[2.0.2]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.1...v2.0.2
[2.0.1]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v1.0.0
