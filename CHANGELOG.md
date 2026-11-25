# Changelog

All notable changes to Desktop Recorder are in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).
Dates are the GitHub release dates (UTC). This file was made from the GitHub release notes and the commit history.

## [Unreleased]

## [3.2.0] - 2026-11-25

### Changed

- Windows, transcript: Whisper does not run when the recording has less than 0.5 s of sound, and sound in less than 10% of its time. Before, Whisper ran on recordings that had only the stop sound, and "Making the ZIP" stayed for up to 88 s with no transcript row as the result.
- Windows, folder mode: the ZIP does not wait for a slow Whisper. Whisper starts at Stop, next to the frames and the clip, and the ZIP waits at most 8 s for it. A slower transcript goes into the same ZIP later.
- Windows: FFmpeg and the program run above normal priority (Whisper does not). On a busy PC, the frame pass of a 15 s recording took 31 to 52 s before, and 7 to 11 s after.
- Windows, folder mode: the lossless capture uses `-delay 0` (up to 30 fps) and 0.5 s fragments. A killed FFmpeg loses less, and the sound, the transcript and the window titles fit the video better on a still screen.
- Windows, frames: a lower pixel tolerance (12, before 24), so light selection colors and slow fades count as changes.
- Windows: errors go into `error.log` next to `settings.txt`. An error of the window code does not end the program.

### Fixed

- Windows, hang: every helper program (ffprobe, the decode, the clip encode) has a time limit. An NVENC encode that hangs goes to libx264 one time. Exit can be forced after 30 s.
- Windows, lost recordings: the part before an FFmpeg failure is now a recording of its own (before, it stayed out of the ZIP). After a UAC prompt, the lock screen or a display change, the recording goes on in a new ZIP. The program makes the ZIP of a recording that was not finished at its next start. A refused sign-out does not stop the recording. Stop waits up to 15 s for FFmpeg (before 4 s).
- Windows, sound: the capture follows a new default playback device. The Stop sound is not in the audio. The recording folder stays when the WAV file is the only copy of the sound. `index.md` says why there is no sound. Short real speech on a silent PC stays in the transcript. One bad sample does not make the audio encode fail.
- Windows, clipboard and status: an older ZIP does not replace a newer one. A failed frame pass is not "ZIP ready", and an empty ZIP is not made. Each note box shows the state of its own recording.
- Windows, files: the program deletes only its own files from a recording folder, and tries again when another program holds a file. A note is on the disk at once. A `settings.txt` that cannot be read is not replaced with the defaults; `settings.txt.bak` is the backup.
- Windows, frames that were missed: a page or a menu under a moving pointer; a page of 0.33 s; the last value of a counter or a spinner; a small text that stayed and then went away; a slow fade; a change at the left 8 px or the last rows; a scroll beside a sidebar with text.
- Windows, frames: a page that loads does not get the text "scroll, N px up" in `index.md` any more, when the two pictures match only in a small, almost empty part.
- Windows, `index.md`: a popup without a title gets the title of the window that it belongs to.
- Windows, note box: the question "Save this note?" takes a letter only with Alt. Before, a typed "d" discarded the note.
- Windows, Settings: "Start with Windows" shows Off when Windows has the app turned off in its startup list. Before, it showed On. Save changes the startup entry only when the switch was changed. When you turn the switch on while Windows has the app off, the program offers to open the startup list of Windows; it does not change that list.

### Notes

- Tested on one PC: the unit tests, the offscreen UI preview, offline passes on real recordings and on 100 test videos, and the recovery of a killed FFmpeg. One real recording by Bran (24.3 s, with sound, build of 1cc03e3) gave a complete ZIP about 5 s after Stop. The two faults that it showed are corrected (HANDOFF 7.26, 7.27).
- Not tested: a recording with the final code, a real UAC prompt or lock screen, a change of the sound device, a sign-out, the sync of audio and video, the exe from the release zip.
- Known limit: over a playing video, a short popup can get no frame, because the samples are each 5 s. It is in `clip.mp4`.
- Ubuntu: no change.

## [3.1.0] - 2026-11-22

### Added

- Windows, folder mode: a "busy, then ready" indication for the ZIP (Bran, 2026-11-22). From Stop until the ZIP is on the clipboard, the tray icon turns, and the tray menu, the tooltip and the note box show "Making the ZIP... (step: frames)" (then transcript, then ZIP). A small popup at the bottom of the screen shows the same when the note box is closed. When the ZIP is ready, the note box and the popup say "ZIP ready: Ctrl+V to paste" with the size, and a short ready sound plays when "Play sounds" is on. A failed ZIP shows the reason. The popup does not take the focus, is not in the video, and closes after 4 s or with a click.
- Windows, folder mode: when "Copy to the clipboard" is on, the clipboard gets a "still making the ZIP, wait" text at Stop, so an early Ctrl+V does not paste the recording before.

### Changed

- Windows, note box after Stop (Bran, 2026-11-22): it opens in the middle of the monitor of the recording, larger, with a title, a line that says what the note is for, the recording name and length, and a text box with several lines. "Save note" (default) and "No note". Enter saves; Shift+Enter or Ctrl+Enter starts a new line (`index.md` still shows the note on one line). Esc, the X button, Alt+F4 and "No note" close an empty box at once. When the box has text, they ask "Save this note?" in the box: Save, Discard or Back. Exit in the tray menu asks the same question, and Back cancels the Exit. A Windows shutdown saves the text. The box stays on top, is in the taskbar, does not close when it loses the focus, and flashes its taskbar button when it opens without the focus. It follows the app theme (light or dark).
- Windows, Settings window: five pages in a left navigation list (Recording, Capture and quality, Hotkey and selector, AI and audio, Startup and sounds). Each setting is a card with a help text that gives the default. Switches replace the check boxes. A problem shows in red at the bottom and the window opens the page of the problem. New "Restore defaults" button: it puts the defaults into the fields, and Save applies them. "Start a new file every" is off in folder mode. The AI and audio page tells if Whisper is found. Every other setting, key, default and value range stays (see Removed). The window uses the light theme only.
- Windows, tray menu: a status at the top (not recording, recording with the elapsed time, processing, closing), groups, a bold "Select region and record" or "Stop recording" with the hotkey at the right, icons, and the app theme (light or dark). Every command stays. The tooltip tells the state.
- Windows, folder mode: the output of a recording is one ZIP file only (Bran, 2026-11-22). After the frame pass, the app always makes the ZIP next to where the recording folder was. The clipboard holds the ZIP as a file and the text message for the AI. It never holds the loose files. Then the app deletes the recording folder. When the frame pass fails, the folder stays (it holds the lossless work file), and the app still makes and copies the ZIP.
- A note typed in the note box after the ZIP is made goes into `index.md`, and the app makes the ZIP again (same path). The folder is deleted only when the note box is closed and the ZIP is done.
- Tray: "Open last recording folder" is now "Show last ZIP in Explorer". "Copy ZIP of last recording" copies the existing ZIP and does not make it again.
- The AI message on the clipboard and the instruction at the top of `index.md` now describe a ZIP, not a folder.
- The ZIP stores PNG and MP4 files without compression (they are compressed already) and compresses the text files. The ZIP format is the same.
- Exit waits for the ZIP and the deletion of the folder.
- "MP4 only" mode is not changed.
- Windows, folder mode: the file-count warning in `index.md` now describes the ZIP. It gives the number of frame images and of files (with `index.md`), and says that a chat app that unpacks the ZIP can limit the number of images that it reads. The limit stays at 20 files.

### Fixed

- Windows, folder mode: a good ZIP after a failed ZIP (for example after a note) now lets the app delete the recording folder. Before, the folder stayed.
- Windows, transcript: the app leaves out rows that Whisper wrote on silent or almost silent audio, for example "Thank you." (seen in three recordings by Bran on 2026-11-22). The app measures the sound in the time of each row: a row with sound in less than 10% of its time and for less than 1 s is left out, and a known Whisper text for silence in a long row (10 s or more, or most of a short recording) needs sound in half of its time. `index.md` gives the number of rows that the app left out. When no row is left, it says "No speech was found", and the folder has no `transcript.srt`. The whisper.cpp options for silence did not stop these rows on the test PC, and Silero VAD also removed quiet real speech, so the app does not use them.
- Windows, folder mode: a moving mouse pointer no longer gives the warning "the picture changed during N% of the recording". Only a frame pair with a large change counts as moving content: 96 px or more on the short side of one connected changed area (half the short side of the region, when the region is smaller), or many small changed parts that together cover 96 x 96 px or more. The area in the warning now covers only that moving content. The frame rules are not changed.

- Windows, tray menu: the menu opens above the taskbar. Before, its lower part covered the taskbar by 30 px (live test, HANDOFF 7.22).
- Windows, tray menu: when the state changes while the menu is open, the status title, the detail line and the dot change together. Before, the menu could say "Not recording" above "Wait for "ZIP ready" before you paste". A long detail is drawn on one line with "..." while the menu is open. A screen reader gets the current time of a recording.
- Windows, tray menu: a record command that a state change made old does nothing. Before, "Stop recording" in a menu that stayed open across a Stop opened the region selector.
- Windows, region selector: the crosshair has a dark line on each side of the white line, so that it shows on a white page.
- `index.md`: a line break in the note gives one space. Before, a Windows line break gave two spaces.
- Windows, folder mode: a failed ZIP does not leave `<name>.zip.tmp` next to the recordings.

### Removed

- Windows: the setting "Folder mode: copy one ZIP above this many files" (`clipboard_max_files`), which was never released. A settings file that has the key still loads with no error. The next Save does not write it.
- Windows: the setting "Record when the app starts" (`record_on_launch`; "Start recording (saved region) when Desktop Recorder starts" in 3.0.1), Bran, 2026-11-22. Every recording starts with the hotkey and an area that you select. The app does not record when it starts, also when an old settings file has `record_on_launch=1`. The old line stays in `settings.txt`, because the Linux build uses the key. The Windows app ignores it.

### Notes

- Live test on one PC (HANDOFF 7.22, synthetic input and real recordings at 125%): the note box, the Settings window, the tray menu, the ZIP indication (tray icon, note box line, popup, sound, wait text), the ZIP-only flow (T1 to T7), MP4-only mode and the selector keys (`~`, Shift, Ctrl, C, M, P, arrow keys, Esc) passed, in light and dark app mode. The note box and the popup were not in the video. Not tested: 100% and 150% scale, a paste of the ZIP into a chat app or a Claude cloud session, more monitors.
- UI redesign and ZIP indication: also drawn offscreen at 100%, 125% and 150% (HANDOFF 7.20, 7.21).
- Transcript and motion fixes: tested offline on the same PC with the `clip.mp4` files of three real recordings and with synthetic test videos (HANDOFF 7.19).
- Real recordings by Bran on 2026-11-22 with the ZIP-only build (HANDOFF 7.23): each recording gave one ZIP and no folder, a note was in the ZIP, a Twitch stream got the motion warning, and the transcript kept real speech with no false rows.
- Quality default: not changed (Balanced). Six more blind AI reads of each quality level showed no read difference larger than the reader spread (HANDOFF 6.11).
- Ubuntu: no change.

## [3.0.1] - 2026-11-20

### Changed

- Windows: new default, "Start recording (saved region) when Desktop Recorder starts" is off. Before, the app recorded at once when it started, and recorded the full desktop when it had no saved area.
- Windows: new default, the mouse pointer is hidden. A moving pointer keeps the screen from settling and can show in the PNG frames. The folder-mode tests ran with the pointer hidden.
- These new defaults apply only when `settings.txt` has no value for the key. An existing settings file keeps its values.
- Windows: the size warning in `index.md` is now for a long side above 2576 px (was 1568 px), or more than 3.75 million pixels. Current Claude models read images up to this size without making them smaller. A `size_limit=1568` in a 3.0.0 settings file is read as 2576.

### Fixed

- Windows: the file-count warning in `index.md` now counts `index.md` too. Before, a recording with 20 frames put 21 files on the clipboard but got no warning.

### Notes

- Tested: unit tests and the app build on one PC (Windows 11), and the Build workflow on GitHub. Not tested: a recording with the 3.0.1 build, the first start with no settings file, and the exe from the release zip.
- Ubuntu: no change.

## [3.0.0] - 2026-11-20

### Changed

- Windows: the default output is a folder for an AI, not one MP4 file. Each recording gives `frames\001.png` and so on (settled frames at full resolution), `index.md` (start time, note, one row for each frame with the time, the window title and what changed) and `clip.mp4` (keyframes at the frame times). Settings has "MP4 only" for the old output.
- Windows: in folder mode, the clipboard holds `index.md` and the PNG frames as files, plus a text message for the AI with the folder path. A chat app attaches the files. A terminal gets the text.
- Windows: in folder mode, a recording is not split into parts. "Start a new file every" applies only to "MP4 only". Quality and Color detail apply only to `clip.mp4`; the frames are always lossless.
- Windows: Exit stops a running recording, then waits until the files of the last recording are made.
- README: new structure (summary, features, install, use, build, troubleshooting).

### Added

- Windows: `index.md` starts with an instruction for the AI: the files are one screen recording and they are context for the message.
- Windows: a note box after Stop. The note goes into `index.md`.
- Windows: tray items "Open last recording folder" and "Copy ZIP of last recording".
- Windows: optional system audio (off by default) in `clip.mp4` and in the MP4 files. When a local Whisper program is present (whisper.cpp; faster-whisper-xxl, whisper-ctranslate2 and whisper are supported but not tested), `index.md` gets a transcript and the folder gets `transcript.srt`. See [docs/audio-transcript.md](docs/audio-transcript.md).
- Windows: `index.md` says when the picture did not settle for most of the recording (for example a video), so the AI knows that the frames are samples.

### Fixed

- Windows: moving video no longer gives false "scroll" rows. A shift must now match 80% of the edge pixels. A row for a screen that still changes does not name a scroll smaller than 2% of the moving area (for example a camera tilt in a video). Rows for settled frames still name the scroll.

### Notes

- Tested on one PC (Windows 11, one monitor, RTX 4060): folder output, paste into the Claude desktop chat, audio capture, and a transcript with whisper.cpp. Not tested: the sync of audio and video, long recordings with a transcript, Whisper programs other than whisper.cpp, other chat apps, a busy 60 s or 5 min recording, `clip.mp4` in a normal video player, the exe from the release zip (the test PC ran a local build of the same commit), and a PC without an NVIDIA GPU.
- Ubuntu: no change.

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

[Unreleased]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v3.2.0...HEAD
[3.2.0]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v3.1.0...v3.2.0
[3.1.0]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v3.0.1...v3.1.0
[3.0.1]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.2...v3.0.0
[2.0.2]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.1...v2.0.2
[2.0.1]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/gecko-of-shadow/desktop-recorder/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/gecko-of-shadow/desktop-recorder/tree/v1.0.0
