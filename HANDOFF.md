# HANDOFF: Desktop Recorder rebuild

Date: 2026-11-14. Repository: `gecko-of-shadow/desktop-recorder` (public). Owner: Bran.

Write all replies, notes and commit messages in ASD-STE100 (Simplified Technical English), in full.

---

## 1. Goal

Rebuild Desktop Recorder. The app records the screen so that an AI model can read the video. The AI takes frames out of the video and reads the text in each frame.

The new app must:

1. Let the user select the part of the screen to record, like the ShareX region selector.
2. Capture the real frame rate that the user sets. It must not drop frames.
3. Keep text sharp enough for an AI to read each character.
4. Keep files small.

A test proves points 2 to 4. Section 6 gives the test.

---

## 2. Current app (checked in the code on 2026-11-14)

- C# WinForms on .NET Framework 4. `build.cmd` compiles it with the `csc.exe` that ships with Windows. No NuGet packages.
- Files: `DesktopRecorder.cs` (UI and tray), `RecorderCore.cs` (settings and FFmpeg arguments), `RecordingSession.cs` (FFmpeg process), `tests/CoreTests.cs`.
- A Linux port is in `linux/` (Python, GTK). Do not change the Linux port in this task.
- `RecorderCore.BuildWindowsArguments` builds this FFmpeg command:
  - Capture: `gdigrab`, input `desktop`. It records all monitors as one picture. It has no region option.
  - Encoder: `libx264`, `-preset medium`, `-tune stillimage`, `-pix_fmt yuv420p`.
  - Quality: CRF 32 (Smallest files), 27 (Balanced), 21 (Sharpest text).
  - Threads: `ceil(fps)`, from 1 to 16. At 4 fps, x264 gets 4 threads.
  - Output: fragmented MP4. Optional split with the segment muxer.
- Default fps in the README: 2. Range: 0.2 to 60.
- The code has no DPI awareness call and no app manifest.

---

## 3. Problems found

### 3.1 From the Grok test (results NOT checked by Claude)

Bran ran a read test with a Grok bot. Bran recorded a scrolling Markdown file (`ai_tokenization_parsing_anomalies.md`, 269 lines) and Grok read the frames.

| fps | Quality | Lines caught | Word errors |
|---|---|---|---|
| 4 | Smallest | 85% | 0.2% |
| 4 | Balanced | 67% | 9.4% |
| 4 | Sharpest | 67% | 8.0% |
| 10 | Smallest | 100% | 1.5% |
| 10 | Balanced | 99% | 2.4% |
| 10 | Sharpest | 100% | 0% |

Grok reported these points:

- At 10 fps, many frames were exact copies of the frame before. The real rate was about 5 fps during fast scrolling.
- At 4 fps, the scroll moved one screen or more between two frames. Text was lost.
- Quality changed only look-alike characters: 0/O and 1/l/I.

Flaws in that test:

- The scroll speed was different between the 4 fps runs and the 10 fps runs.
- Grok found the quality setting of the 10 fps videos from the file size only.
- Different line splits made some scores unreliable.

### 3.2 Probable causes in the code (UNVERIFIED, test them)

- **Dropped frames.** `gdigrab` uses GDI BitBlt. It is slow on a large desktop. `libx264 -preset medium` is also slow. When a frame is late, FFmpeg repeats the last frame. This gives the "copies of the frame before".
- **Blur on colored text.** `yuv420p` stores color at half resolution. Colored text and syntax highlighting get soft edges.
- **Wasted pixels.** The app records all monitors. The text that the user wants is a small part of each frame.
- **DPI.** Without DPI awareness, a region selector gets wrong coordinates on a monitor scaled above 100%.

---

## 4. Requirements for the new app

### 4.1 Region selector (based on ShareX region capture)

ShareX features to copy (source: getsharex.com docs and changelog):

- Click a window or a control that the selector detects, to capture that item.
- Drag a rectangle.
- Magnifier near the cursor, for exact positions.
- Arrow keys move the cursor 1 px. Shift + arrow keys move it 10 px.
- Shift while dragging keeps a square. Ctrl while dragging moves the selection.
- Space selects the full screen. `~` selects the active monitor.
- "Last region" records the same area again.
- Option: show the position and size of the selection.
- Option: screen-wide crosshair.
- Option: dim the area outside the selection.
- ShareX ignores tool windows that cannot be activated during window detection.

Behavior of the new selector:

1. The hotkey opens the selector. The same hotkey stops a recording. (Changed by Bran on 2026-11-14, see section 5. The "record last region" hotkey and the separate stop hotkey are removed.)
2. The selector is one borderless, topmost form that covers `SystemInformation.VirtualScreen` (all monitors).
3. The selector freezes a screenshot of the desktop and shows it dimmed. The selected area shows at full brightness.
4. On hover, the selector draws a border around the window under the cursor.
   - Get windows in Z-order with `EnumWindows`.
   - Get the real bounds with `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`. `GetWindowRect` includes the invisible shadow border.
   - Skip windows that are not visible, minimized, cloaked (`DWMWA_CLOAKED`), tool windows, and the selector itself.
   - Optional: detect child controls with `ChildWindowFromPointEx`.
5. Click = select the highlighted window. Drag = select a rectangle.
6. The release of the mouse after a drag starts the recording. A click on a highlighted window also starts it. Enter, Space and `~` start it from the keyboard. Esc cancels. (Changed by Bran on 2026-11-14. Before this change, the user moved and resized the selection with handles and arrow keys, then pressed Enter. The GUI test checked the arrow keys, Shift + arrow keys, Esc and Enter on that build.)
7. Show the size in px next to the selection. Make the width and the height even numbers, because H.264 needs even sizes.
8. Show the magnifier (for example 10x10 px enlarged 8 times) next to the cursor.
9. Save the last region in the settings file.
10. Make the process per-monitor DPI aware (app manifest or `SetProcessDpiAwarenessContext`). Use physical pixels everywhere.

### 4.2 Frame during recording

- Show a thin colored border around the recorded region. Put the border OUTSIDE the region so that it is not in the video.
- Make the border windows click-through: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`.
- Also call `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on the border windows (Windows 10 2004 and later). UNVERIFIED: that `gdigrab` and `ddagrab` respect this flag. Test it.
- Show a small Stop button below the region. Exclude it from capture the same way.

### 4.3 Capture and encoding

Make the capture backend a setting, so the test can compare the backends:

| Backend | FFmpeg input | Notes |
|---|---|---|
| A (old) | `gdigrab` with `-offset_x`, `-offset_y`, `-video_size` | CPU capture. Keep as fallback. |
| B (new) | `ddagrab` (Desktop Duplication API) with `output_idx`, `offset_x`, `offset_y`, `video_size`, `framerate` | GPU capture. Needs FFmpeg 6.0 or later. ShareX added ddagrab support. A region must stay on one monitor. |

Encoders:

- Default: `h264_nvenc` (Bran has an NVIDIA GPU). Keep `libx264` as fallback when NVENC is not available.
- Test `yuv420p` against `yuv444p` (H.264 High 4:4:4). UNVERIFIED: some browsers and players cannot play 4:4:4. FFmpeg can read it.
- With `libx264`, test a faster preset (`veryfast`) against `medium`.

Frame-rate modes:

- **Fixed:** the current behavior.
- **Changes only (test this):** capture at a high rate (for example 15 or 30 fps) and drop frames that do not change, with the `mpdecimate` filter and `-fps_mode vfr`. A still screen costs almost nothing. A fast scroll keeps all frames. UNVERIFIED: that every AI frame tool handles variable frame rate correctly.

Other rules:

- Never scale the picture down.
- Keep the fragmented MP4 output, so a killed recording stays playable.
- Keep all current settings: folder, hotkey, sounds, clipboard copy, split, start with Windows.
- Add a status line after each recording: real frames captured, duplicate frames, file size. FFmpeg can report duplicate frames (`dup=`) in its stats.

---

## 5. Decisions (Bran answered on 2026-11-14)

1. Platform: stay on .NET Framework 4 with `csc.exe` and `build.cmd`. No NuGet. No .NET 8.
2. Window selection: version 1 records the fixed rectangle only. The recording does not follow a window that moves.
3. Default frame-rate mode: "Changes only" (Bran decided after Phase B, 2026-11-14). Code default changed; Bran's settings file set to `frame_rate_mode=changes`. The Linux port keeps its own default (not changed in this task).
4. Start flow (GUI test, 2026-11-14): Win+Shift+D, drag a box, release the mouse. The recording starts at once. No Enter.
5. Stop: the same hotkey (Win+Shift+D) stops. No separate stop hotkey.
6. Remove the "record last region" feature (hotkey, tray item, settings row). Bran does not use it.
7. Save in Settings does not restart a running recording. New capture settings apply to the next recording (bug 4 in 6.7).
8. Icons: Bran's 3D icon for the exe, and his tray icon set for the tray (6.7).
9. Mouse pointer: hidden by default (`show_pointer=0`). The "Hide the mouse pointer" check box in Settings is on by default. Bran decided on 2026-11-20 (7.13). Before that, the default was "shown" (Bran, 2026-11-14, 6.9).
10. Default frame rate: 10 fps (was 2). Bran decided on 2026-11-14. Reason: the 6.8 rule needs 2.3 fps for 1000 CSS px/s at 125% in an 892 px area. 10 fps is safe up to about 4400 CSS px/s. Bran's settings file already had `fps=10`.
11. Quality default: keep Balanced (CQ 23). More reads of B3, B9 and B11 are put off to a later date (Bran, 2026-11-14). Data in 6.11; Bran decides.
12. Release: push `test-kit`, merge it into `main`, publish v2.0.0 (Bran, 2026-11-14). The version is 2.0.0 because the hotkey now opens the selector, not a full-desktop recording.
13. "Start recording (saved region) when Desktop Recorder starts": off by default (`record_on_launch=0`). Bran decided on 2026-11-20 (7.13).

---

## 6. Test plan

### 6.1 Test kit to build

1. **Source text** (`test/source.md`, about 300 lines). Each line starts with a tag `L001` to `L300`. Content:
   - Plain prose.
   - Code with `{}[]<>|`.
   - SHA-256 hashes and Base64 lines. The reader can decode them to check the copy.
   - Look-alike traps: `O0Oo0`, `Il1|`, `rn` and `m`.
   - A table, accented letters, emoji, one line of Zalgo text.
   - Some lines in colored text (to test 4:2:0 against 4:4:4).
2. **Auto-scroll page** (`test/scroll.html`, one self-contained file). It shows the source text and scrolls at a fixed speed in px/s. Controls: speed (200, 500, 1000, 2000 px/s), font size (100%, 125%, 150%), start delay.
3. **Frame-counter page** (`test/counter.html`). It shows a large frame number that changes on each display refresh, and a binary bar code of the same number. It measures the real capture rate without an AI.
4. **Scoring script** (`test/score.py`). Input: the source file and the AI copy. Output: lines caught, character error rate, word error rate, and a list of missed line tags. Match lines by tag, so line splits do not matter.
5. **Frame script** (`test/frames.py`). Input: a video. Output: total frames, unique frames (by hash), duplicate frames, real fps, and PNG files of the unique frames.

### 6.2 Phase A: capture rate (no AI)

Record `counter.html` for 10 s at each setting. Count the unique frame numbers in the video.

| Run | Backend | Encoder | fps |
|---|---|---|---|
| A1 | gdigrab | libx264 medium | 10 |
| A2 | gdigrab | libx264 veryfast | 10 |
| A3 | gdigrab | h264_nvenc | 10 |
| A4 | ddagrab | h264_nvenc | 10 |
| A5 | ddagrab | h264_nvenc | 30 |
| A6 | best of A1 to A5 | | 10, region only |

Pass: unique frames are 98% or more of the expected frames.

### 6.3 Phase B: AI read test

Record `scroll.html`. The AI reads the unique frames and writes a copy. `score.py` scores the copy.

Keep the font at 100% and the speed at 1000 px/s unless the row says something different.

| Run | Variable | Values |
|---|---|---|
| B1 to B4 | fps (best backend, 4:2:0) | 4, 6, 10, 15 |
| B5 to B7 | scroll speed (best fps) | 500, 2000, 3000 px/s |
| B8 | chroma | 4:4:4 |
| B9 to B11 | quality (NVENC CQ) | 3 levels |
| B12 | frame-rate mode | Changes only |
| B13 | font size | 125% |
| B14 | baseline | old app, old settings |

Record these values for each run: real fps, lines caught, character error rate, word error rate, MB per minute, CPU %.

Rule to check: a gap occurs when (scroll speed ÷ real fps) is more than (region height − 2 lines). If the data follows this rule, the app can show the lowest safe fps for a scroll speed.

Pass for the final default: 100% of lines caught, 0 errors outside the Zalgo line, at 1000 px/s.

---

### 6.4 Test kit limits (found by the test agents on 2026-11-14)

- `scroll.html` speed is in CSS px/s. At 125% Windows scaling, 1000 px/s moves 1250 physical px/s. Convert with the DPI scale before you use the gap rule in 6.3.
- `scroll.html` cuts long lines in a narrow window. Minimum window width in CSS px: 824 (font 100%), 1040 (125%), 1184 (150%).
- Flag emoji on L232 show as letter pairs on Windows. The AI copy can contain letter pairs there.
- `frames.py --threshold` is not reliable on lossy video. Use exact mode (default) for Phase B.
- `score.py`: tell the AI not to add table headers or notes that start with a tag.

### 6.5 Phase A results (run on Bran's PC on 2026-11-14)

Setup (checked):

- PC: one monitor, 1920x1080 physical, 125% scale, 119 Hz. GPU: RTX 4060, driver 621.01. CPU: 12 logical cores.
- FFmpeg: gyan.dev full build 2026-04-30 (`cc3ca17127`). It has `gdigrab`, `ddagrab`, `h264_nvenc`, `libx264`.
- Page: `test/counter.html` in a maximized Chrome window. Region = the page area: x 0, y 108, 1920x912 physical px. A DPI-aware call measured the page area. A snapshot PNG from each backend decoded correctly.
- Script: `test/phase_a.ps1`. It runs FFmpeg directly, 10 s for each run, one run at a time. Cursor off. Fragmented MP4. The script does not set `-fps_mode`. A late frame shows as a gap in the timestamps, not as a copy (see the smoke run below).
- Encoder settings: libx264 = old app settings (`-tune stillimage`, CRF 27, threads = ceil(fps)). h264_nvenc = `-preset p4 -rc vbr -cq 23`.
- Output: `test/results/phase_a/` (Git ignores it). Each run has the MP4, the FFmpeg log and command, and the `frames.py` JSON. `summary.json` has the table below.

| Run | Backend | Encoder | fps | Frames | Unique values | Expected | Real fps | Result | MB (10 s) | CPU % | Page Hz |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A1 | gdigrab | libx264 medium | 10 | 100 | 100 | 100 | 10.0 | PASS | 0.189 | 2.6 | 118.9 |
| A2 | gdigrab | libx264 veryfast | 10 | 100 | 100 | 100 | 10.0 | PASS | 0.236 | 2.1 | 118.8 |
| A3 | gdigrab | h264_nvenc | 10 | 100 | 100 | 100 | 10.0 | PASS | 0.412 | 0.9 | 118.8 |
| A4 | ddagrab | h264_nvenc | 10 | 100 | 100 | 100 | 10.0 | PASS | 0.418 | 0.3 | 120.2 |
| A5 | ddagrab | h264_nvenc | 30 | 300 | 300 | 300 | 30.0 | PASS | 0.927 | 0.3 | 120.0 |

Column notes:

- Unique values = different counter numbers that `frames.py --counter` read. All frames decoded (0 failed). No counter value repeated in any run.
- CPU % = FFmpeg process CPU time ÷ wall time ÷ 12 cores. It does not include GPU load.
- Page Hz = (last counter value − first counter value) ÷ 10 s. Near 119 means Chrome did not slow the page. All runs are valid.
- FFmpeg reported no `dup=` and no `drop=` in any run. This proves little: the output timing is passed through, so FFmpeg does not add copies. The unique-value count is the real check.
- The frame timestamps of A1 and A4 step by 0.1 s with no gap.
- In A1, the counter steps were 10 to 15 values (median 12, 119 Hz ÷ 10 fps). No step was long. Thus no capture was late by a full frame.

Checked: all numbers in the table, from runs on Bran's PC.

Not checked:

- Encoder load with full-screen motion. The counter page changes only a small area. Thus each encoder had little work. The Grok test (3.1) saw copies of frames during fast scrolling, when every pixel changes. This test did not reproduce that load. UNVERIFIED: `gdigrab` capture time does not depend on the content. No run tested it.
- `gdigrab` at 30 fps. Only `ddagrab` ran at 30 fps (A5).
- File size comparison. libx264 CRF 27 and NVENC CQ 23 are not the same quality level. Do not compare the MB column between encoders. Phase B (B9 to B11) compares quality.
- A 2 s smoke run of A3 gave 18 of 20 values. Its timestamps jump from 0.6 s to 0.9 s near the start: two captures were late. The 10 s run of A3 gave 100 of 100. The probable cause is the start of `gdigrab`. Not checked.
- Multi-monitor PCs. This PC has one monitor.

Conclusion:

1. At 10 fps on this PC, no backend dropped frames. The cause of the Grok "copies of the frame before" result is still unknown. Possible causes (not checked): encoder load during a scroll, the old full-desktop capture, or the browser that did not repaint during the scroll.
2. Recommended backend for the app: `ddagrab` + `h264_nvenc`. All five runs tie on capture rate. The choice rests on two weaker points: the lowest CPU (0.3%, under light load), and A5, which kept 30 fps with no loss. Keep `gdigrab` + `h264_nvenc` as the fallback (0.9% CPU, no loss at 10 fps). Keep `libx264 veryfast` as the fallback when NVENC is not available.
3. Backend for A6: `ddagrab` + `h264_nvenc` (A4 settings). A1 to A5 already used one region (1920x912, nearly the full monitor). Thus A6 with a smaller region adds little on this PC.
4. Scroll stress run: done. See 6.6.

### 6.6 Scroll stress run (run on Bran's PC on 2026-11-14)

Goal: check the encoder load when every pixel changes. The counter page (6.5) did not give this load.

Setup (checked): `scroll.html#speed=2000&font=100&delay=0` in a maximized Chrome window. Same region as 6.5 (x 0, y 108, 1920x912). 2000 CSS px/s = 2500 physical px/s at 125%. 10 s recordings at 10 fps. The scroll started about 1 s after FFmpeg started and lasted about 4 s. After that, the page stood still. A background workflow (code agents, `csc` builds) was active on the PC during the runs.

| Run | Settings | Frames | Unique frames, threshold 0.1 | Duplicates during the scroll | MB (10 s) | CPU % |
|---|---|---|---|---|---|---|
| S1 | A1: gdigrab + libx264 medium, CRF 27 | 100 | 40 (frames 0, 11 to 49) | 0 | 2.355 | 3.0 |
| S2 | A4: ddagrab + h264_nvenc, CQ 23 | 100 | 41 (frames 0, 9 to 47, and 73) | 0 | 3.075 | 0.3 |

Result: both settings captured every frame during the scroll (39 unique frames in a row each). Each frame moved about 250 physical px, so a lost capture would show as a pair of equal frames. The Grok result "copies of the frame before" (3.1) did not occur on this PC with these settings. Frame 73 in S2 is a small encoder change on a still page, not a scroll frame.

New test kit limit (checked): `frames.py` exact mode (the default) counts still frames after motion as unique. Both encoders keep refining a still picture, so the decoded frames are not bit-identical. Exact mode gave 60 (S1) and 99 (S2) unique frames for the same videos. Thus exact mode can only over-count unique frames. It never hides a lost frame. For Phase B this means extra frames for the AI to read, not missed text. For a count of real changes, use `--threshold 0.1`.

Not checked: why the old app gave copies in the Grok test. Possible causes: the old full-desktop capture, a different monitor setup at the time of that test, a different scroll method, or the Grok frame count itself.

### 6.7 GUI test of the new app (run on Bran's PC on 2026-11-14)

Setup: same PC as 6.5. App built with `csc.exe` from branch `test-kit`. Synthetic input (`keybd_event`, `mouse_event`) and DPI-aware screenshots drove most steps. Bran did the steps that need eyes: the border, the Stop button, the tray status line, and the Settings dialog. `test/app_runs.ps1` made the recordings with the app itself. Output: `test/results/app_runs/` (Git ignores it).

Bugs found and fixed:

| # | Bug | Fix (commit) |
|---|---|---|
| 1 | The selector did not open. `CopyFromScreen(SourceCopy \| CaptureBlt)` throws `InvalidEnumArgumentException` on .NET Framework. The app showed the error only in a tray balloon, and Windows did not show the balloon. | Use `SourceCopy` only (06767e2). |
| 2 | "Changes only": a 10 s recording of a still page gave a file of 0.1 s (1 frame). The file lost its real length. | `mpdecimate=max=ceil(fps)-1`: at least one frame each second. Checked: 11 frames, 10.1 s, 12 KB (00056cc). |
| 3 | Settings dialog at 125%: the labels covered the input fields (fps value, split value, the start of each list, the Win check box). Cause: the first control in `Controls` is on top, and the labels were added first. | Put the labels behind the other controls (0ffacbf). Checked with a screenshot. |
| 5 | When the app ended without a normal stop (killed, crash), FFmpeg kept recording with no way to stop it. Found when a test script stopped the app during Bran's recording: FFmpeg recorded the full screen for 5.5 min. | FFmpeg runs in a job object with "kill on job close", so it ends with the app. Checked: app killed, FFmpeg ended. A file shorter than one fragment (2 s) then has no readable length. |
| 4 | Save in Settings during a recording stopped it and started a new file. The clipboard got only the second file (1 s), without the part that Bran wanted. | Save no longer touches a running recording. New capture settings apply to the next recording (5fee682). Not checked with a real click. |

Start and stop time (Bran: "start and stop feel laggy"; measured with synthetic keys and `counter.html`, commit cc64aa4):

| Step | Before | After |
|---|---|---|
| Hotkey to selector on the screen | under 0.3 s | same |
| First recording after app start: key to FFmpeg and border | 0.54 s (NVENC test on the UI thread) | 0.24 s (test runs in the background at app start) |
| Later recordings: key to FFmpeg | 0.11 s | same |
| Stop key to border gone | after FFmpeg ends: 0.4 s with NVENC, 4.7 s with x264 | about 0.2 s; FFmpeg finishes on a background thread |
| FFmpeg start to first frame | 0.45 s | same (ddagrab start-up) |

Bran: "Feels better."
Changes that Bran asked for during the test: release starts the recording (bb620d4), one hotkey starts and stops, no "record last region" (0ffacbf). See section 5. Bran also gave an app icon and a tray icon set: `app.ico` (from `packaging/icon/app-icon-source.jpg`) is the exe icon and the Settings icon, and the tray icons (idle, recording) are drawn in code from the set (fa7b718). Not checked: the icons in Bran's real tray.

Final build check (cc64aa4 and later): `dda420` on `counter.html` at 10 fps: 99 of 99 values, one file, PASS. Real capture rate at 4 and 6 fps (needed for 6.8): 40 of 40 and 59 of 59 values in 10 s, PASS. The mean counter step gives 3.9 and 5.9 fps; the difference to 4 and 6 is within the page timing (119 to 120.5 Hz).

Selector (checked):

- Win+Shift+D opens the selector. Dimming outside the selection works (screen pixel 31 → 16).
- Hover draws a border around the window under the cursor, with the size label. The magnifier shows near the cursor.
- Click on a window selects it. Drag selects a rectangle. Drag 440,172 to 1421,601 gave 982x430 (even sizes, inclusive pixels).
- Before the change in 4.1 point 6: Right, Right, Shift+Down moved the selection by 2 px and 10 px. Esc cancelled and saved nothing.
- After the change: release after a drag starts ddagrab at the dragged offsets. A window click starts at once.
- Space selected 0,0,1920x1080 (physical pixels). The video size equals the selection in every run. DPI scale is correct on this PC.
- Not checked: `~` (on one monitor it gives the same result as Space), Shift (square) and Ctrl (move) while dragging, crosshair key C.

Recording frame (checked):

- Bran saw the red border and the Stop button. A click on Stop ended the recording.
- Windows accepted `WDA_EXCLUDEFROMCAPTURE` on all border and Stop windows (`GetWindowDisplayAffinity` = 0x11).
- Region away from the screen edges: 0 border-colored pixels on the outer rows and columns of each video.
- Full-screen region (border and Stop button forced inside the region): 0 border-colored pixels in the 2 px edge and in the Stop button area, with ddagrab and with gdigrab. Both backends respect the flag.

Recordings of `counter.html` (10 s, 10 fps, region 20,120,1880x860, quality Balanced = NVENC CQ 23):

| Run | Backend | Chroma | Mode | Encoder (from the FFmpeg command) | Profile / pix_fmt | Unique values | Expected | Result | MB | CPU % |
|---|---|---|---|---|---|---|---|---|---|---|
| dda420 | ddagrab | 4:2:0 | Fixed | h264_nvenc | Main / yuv420p | 104 | 104 | PASS | 0.459 | 0.03 |
| gdi420 | gdigrab | 4:2:0 | Fixed | h264_nvenc | Main / yuv420p | 104 | 104 | PASS | 0.460 | 0.5 |
| dda444 | ddagrab | 4:4:4 | Fixed | h264_nvenc, `-rgb_mode yuv444` | High 4:4:4 / yuv444p | 99 | 99 | PASS | 0.535 | 0.1 |
| gdi444 | gdigrab | 4:4:4 | Fixed | h264_nvenc, `-rgb_mode yuv444` | High 4:4:4 / yuv444p | 104 | 104 | PASS | 0.563 | 0.59 |
| ddachg | ddagrab | 4:2:0 | Changes only | h264_nvenc | Main / yuv420p | 104 | 104 | PASS | 0.472 | 0.93 |
| gdichg | gdigrab | 4:2:0 | Changes only | h264_nvenc | Main / yuv420p | 103 | 103 | PASS | 0.442 | 0.91 |
| split | ddagrab | 4:2:0 | Fixed, split 1 min | h264_nvenc | Main / yuv420p | 600 + 123 | 600 + 123 | PASS | 2.566 + 0.527 | 0.04 |

- No run used a fallback. All runs had 0 failed frames.
- Split: the counter continues across the cut (67283 in file 1, 67295 in file 2: one normal step of about 12 values at 119 Hz).
- "Changes only" on a still area (1880x140): before fix 2, 1 frame and 0.1 s. After fix 2, 11 frames and 10.1 s.
- CPU % = FFmpeg CPU time ÷ wall time ÷ 12 cores, measured from second 2 to the stop.
- The first split run is invalid: the Claude window came to the front during the run. `app_runs.ps1` now maximizes the test page and brings it to the front before each run.

Status line (checked): Bran read "Last recording: Real frames: 83 of 83. Duplicates: 0. Dropped: 0. File size: 0.05 MB." The file had 83 frames and 0.048 MB.

Settings (checked by Bran): Settings opened during a recording. With Settings open, Win+Shift+D opened the selector and a recording started. Bran's region in that test was the full screen, so this test did not check an offset error. On that build, Save restarted the recording (new file, same region, border back). Bug 4 removed the restart: Save now keeps the running recording. The new behavior is not checked with a real click.

Not checked:

- Multi-monitor PCs and monitors with different DPI.
- libx264 fallback in the app (NVENC worked in every run).
- `~`, Shift and Ctrl while dragging, the crosshair key.
- The tray balloons: Windows did not show them on this PC. The tray menu shows the status line.

### 6.8 Phase B results (run on Bran's PC on 2026-11-14)

Setup (checked):

- Same PC as 6.5. App from branch `test-kit` (ddagrab + h264_nvenc, quality Balanced = CQ 23 unless the row says something different). `test/scroll.html` in a new maximized Chrome window for each run. Region x 8, y 120, 1120x892 physical px (1400x892 at font 125%). The widest line (L136) ends at x 1101. The long side stays under 1568 px, so the image reader does not scale the frames down.
- Scroll distance: 7415 CSS px at font 100% (7.4 s at 1000 px/s). 1 CSS px = 1.25 physical px. One text line = 31 physical px.
- Scripts: `test/phase_b.ps1` (recordings), `test/prep_frames.py` (scroll span, duplicates with threshold 0.1, crop without scaling for B14), `test/phase_b_table.py` (table). Output: `test/results/phase_b/` (Git ignores it), with the AI copies in `reads/`.
- LL = lossless baseline: FFmpeg directly, ddagrab + `h264_nvenc -tune lossless`, 4:4:4. It shows the errors of the reader alone.
- B10 = B3 (Balanced is CQ 23). B5 to B7, B8, B9, B11 and B13 ran at 10 fps. 6.3 asks for the best fps from B1 to B4; the recordings were made before the reads, so 10 fps (large margin) was used. B7s4 = extra run: 3000 px/s at 4 fps, to test the gap rule above the limit. B14 = old app v1.0.0 with Bran's old settings (10 fps, Sharpest = CRF 21, full desktop), cropped to the same region.
- Every run passed the window-title check (the test page stayed in front from start to stop).
- Reader: one fresh Claude Sonnet agent for each run, same prompt. It saw only a folder of PNG frames with a neutral name (R01 to R15, map in `blind_map.json`). It did not see the source. `score.py` scored each copy.

| Run | Setting | Step px / limit | Dup in scroll | Frames given | Lines caught | Err. lines (no Zalgo) | Err. lines outside L221-L240 | CER | WER | MB | MB/min | CPU % |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| LL | 10 fps, 1000 px/s, font 100%, 4:4:4, fixed, lossless | 125 / 830 | 0 | 75 | 300/300 | 6 | 2 | 0.15% | 0.61% | 9.97 | 48.2 | 0.13 |
| B1 | 4 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 312 / 830 | 0 | 31 | 300/300 | 6 | 2 | 0.35% | 0.70% | 2.27 | 12.1 | 0.00 |
| B2 | 6 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 208 / 830 | 0 | 45 | 300/300 | 11 | 6 | 0.45% | 0.90% | 3.01 | 15.9 | 0.05 |
| B3 | 10 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 125 / 830 | 0 | 74 | 300/300 | 11 | 5 | 0.39% | 0.86% | 4.33 | 21.8 | 0.00 |
| B4 | 15 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 83 / 830 | 0 | 113 | 300/300 | 5 | 0 | 0.37% | 0.78% | 5.84 | 30.8 | 0.08 |
| B5 | 10 fps, 500 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 62 / 830 | 0 | 149 | 300/300 | 11 | 6 | 0.41% | 1.02% | 7.50 | 23.6 | 0.01 |
| B6 | 10 fps, 2000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 250 / 830 | 0 | 38 | 300/300 | 7 | 2 | 0.19% | 0.70% | 2.66 | 20.8 | 0.11 |
| B7 | 10 fps, 3000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 375 / 830 | 0 | 24 | 300/300 | 9 | 5 | 0.42% | 0.82% | 2.03 | 18.1 | 0.00 |
| B7s4 | 4 fps, 3000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 23 | 937 / 830 | 0 | 10 | 255/300 | 8 | 6 | 15.37% | 15.30% | 1.11 | 9.9 | 0.03 |
| B8 | 10 fps, 1000 px/s, font 100%, 4:4:4, fixed, NVENC CQ 23 | 125 / 830 | 0 | 75 | 300/300 | 8 | 3 | 0.16% | 0.70% | 5.09 | 27.0 | 0.04 |
| B9 | 10 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 28 | 125 / 830 | 0 | 75 | 300/300 | 9 | 4 | 0.43% | 0.94% | 3.08 | 16.4 | 0.01 |
| B11 | 10 fps, 1000 px/s, font 100%, 4:2:0, fixed, NVENC CQ 17 | 125 / 830 | 0 | 75 | 300/300 | 6 | 1 | 0.33% | 0.70% | 6.34 | 33.7 | 0.09 |
| B12 | 15 fps, 1000 px/s, font 100%, 4:2:0, changes only, NVENC CQ 23 | 83 / 830 | 0 | 112 | 300/300 | 5 | 1 | 0.26% | 0.74% | 5.80 | 31.9 | 0.74 |
| B13 | 10 fps, 1000 px/s, font 125%, 4:2:0, fixed, NVENC CQ 23 | 125 / 814 | 0 | 92 | 300/300 | 6 | 2 | 0.16% | 0.61% | 5.77 | 25.9 | 0.06 |
| B14 | old app v1.0.0: 10 fps, gdigrab full desktop, x264 medium CRF 21 (cropped) | 125 / 830 | 0 | 75 | 300/300 | 8 | 4 | 0.44% | 0.74% | 5.97 | 25.6 | - |

Column notes:

- Step px / limit = scroll per frame (speed x 1.25 / fps, physical px) / (region height - 2 lines). The gap rule in 6.3 predicts lost lines when the step is more than the limit.
- Dup in scroll = duplicate frames inside the scroll span. 0 in every run: no capture was late, also with the old app.
- Frames given = unique frames in the scroll span (plus the END screen in some runs).
- Err. lines (no Zalgo) = lines with any error, outside L290. Err. lines outside L221-L240 = the same without the accent and emoji lines.
- CER and WER: outside Zalgo, a missed line counts as wrong.
- Real fps: 4, 6, 10 and 15 fps had no duplicate in the scroll, and the counter runs in 6.7 measured the real rate at 4, 6 and 10 fps (PASS). 15 fps is not checked with the counter.
- B5: the reader got 149 frames but saw only 49 (see test limits). Its read is partial.
- B14 MB: size of the full-desktop file of the old app (1920x1080), not of the cropped region.
- MB/min = file size / file length. The files hold about 2.5 s of still page before the scroll and some s after it, so this is not a value for a steady scroll.
- CPU % = FFmpeg CPU time / wall time / 12 cores. GPU load not included.

Results:

1. **Lines caught.** Every run with the step under the limit caught 300 of 300 lines: 4, 6, 10 and 15 fps at 1000 px/s, and 10 fps at 500, 2000 and 3000 px/s. B7s4 (step 937 px from the settings, limit 830 px) caught 255 of 300. It missed 10 groups of 3 to 6 lines: L001-L004, L034-L038, L067-L071, L101-L105, L134-L139, L169-L171, L200-L202, L232-L236, L265-L269, L297-L300. One group for each frame step, plus the start and the end. **The gap rule holds in direction: lines are lost only above the limit.** Not exact: the groups are about 33 lines apart (about 1015 px), but speed and fps give about 937 to 960 px (the page measured 2995.6 px/s; the capture ran at 4 fps, see 6.7). The cause of this difference of about 6% is not known. Use a safety margin.
2. **Lowest safe fps** (from the rule, for this region): fps > scroll speed x scale / (region height - 2 lines). Example: 1000 CSS px/s at 125% in an 892 px region needs more than 1.5 fps. 3000 CSS px/s needs more than 4.5 fps. Because of the 6% difference in result 1, add a margin (for example 1.5 x). The app can show this value.
3. **Reader noise.** Two reads of the same lossless frames gave 6 and 4 error lines (2 and 0 outside L221-L240). The reader always fails some emoji lines: flags show as letter pairs (6.4), and ZWJ families and professions often become `[?]` or a similar emoji. So the pass rule "0 errors outside Zalgo" cannot pass with this reader, even on lossless frames.
4. **Errors outside L221-L240** (Base64 and look-alike lines): 0 to 2 for LL (two reads), B1, B4, B6, B11, B12, B13. 3 to 6 for B2, B3, B5, B7, B8, B9, B14. The runs at CQ 23 alone spread from 0 to 6 lines. That spread is larger than the gap between CQ 17 (B11, 1 line), CQ 23 (B3, 5 lines) and CQ 28 (B9, 4 lines). **With one read for each run, the data does not show a quality effect.** Compression damage, if any, is below the reader noise for this test.
5. **4:4:4 (B8)** gave no gain: the colored lines (L241-L270) had 0 errors in all runs, also with 4:2:0. Files are about 20% larger.
6. **"Changes only" (B12)** read as well as fixed 15 fps (B4) and gave the same file size during a scroll. Its gain is on a still screen (6.7: 12 KB for 10 s). UNVERIFIED: that every AI frame tool handles variable frame rate.
7. **Old app (B14)**: 300/300, 0 duplicates. Two Base64 lines lost long parts (L162, L168). The Grok result in 3.1 (copies of frames) did not occur on this PC with the old app either.
8. **Font 125% (B13)**: 300/300, 2 look-alike errors (O and 0). No gain over 100% with this reader.

Pass rule in 6.3 (100% lines, 0 errors outside Zalgo at 1000 px/s): no run passes, because the reader fails emoji lines on lossless frames too. With the emoji lines left out, B4 (15 fps) has 0 errors, and B11 (CQ 17) and B12 have 1.

Recommendation (Bran decides, see section 5 point 3):

- Keep fixed fps as the default, with ddagrab + NVENC, 4:2:0. 10 fps has a large margin (step 125 px against a limit of 830 px).
- Quality: the data does not support a change. CQ 17 costs about 1.5 x the size of CQ 23. To decide, read B3, B9 and B11 two or more times each.
- "Changes only": no read gain during a scroll. Offer it as an option for long recordings with still screens.

Test limits (found in Phase B):

- One reader agent cannot read more than about 110 images. The B5 reader (136 frames) got images for only 49 frames and still wrote 300 tags. Treat the B5 read as partial. Split big runs into chunks next time.
- One read for each run. The differences in the error columns between runs are within the reader noise.
- The reader model was Claude Sonnet. Other AI tools (Grok, Gemini) can read differently.

Not checked: multi-monitor PCs, other AI readers, 2 or more reads for each run, CPU % for B14.

### 6.9 Mouse pointer setting (run on Bran's PC on 2026-11-14)

Bran asked for a setting to hide the mouse pointer. New key `show_pointer` (default 1 = the old behavior). Check box in Settings: "Hide the mouse pointer in the video" (not checked by default). Off sets `draw_mouse` to 0 for ddagrab and gdigrab.

Test: 10 s, 10 fps, ddagrab + h264_nvenc CQ 23, region 1120x892, still screen. A script moved the pointer in a circle (radius 250 px, one turn each 2 s) inside the region.

| Mode | Pointer | Frames | KB |
|---|---|---|---|
| Changes only | shown | 97 | 215.5 |
| Changes only | hidden | 10 | 12.0 |
| Fixed | shown | 100 | 208.5 |
| Fixed | hidden | 100 | 136.3 |

- Changes only: a hidden pointer makes the file 18 x smaller. A mouse move changes no pixel, so `mpdecimate` drops the frames.
- Fixed: 35% smaller. The frame count stays the same.
- A decoded frame showed the pointer in the "shown" file and no pointer in the "hidden" file.
- Limit: Chrome did not come to the front, so the region held a still Claude window, not `scroll.html`. Hover effects in other apps (button colors, tooltips) still change pixels when the pointer is hidden.

Not checked: the check box with a real click; the new build in Bran's tray (the running app locked `DesktopRecorder.exe`).

### 6.10 Look-alike changes in "Changes only" mode (run on Bran's PC on 2026-11-14)

Reason: the "Export frames" design note (`docs/export-frames.md`) found that default `mpdecimate` dropped 4 of 15 one-character look-alike changes. The app used default `mpdecimate` (hi=768, lo=320, frac=0.33) in "Changes only" mode.

Method (offline, so each kept frame has a known state): `test/lookalike_render.cs` draws 15 states of a 1120x892 page with GDI+ ClearType text (Consolas). Only one item changes between states: `x.y`, `x,y`, `O`, `0`, `l`, `1`, `I`, `rn`, `m`, `a:b`, `a;b`, `a'b`, ``a`b``, `-`, `_`. `test/lookalike.py` shows each state for a hold time, samples at 10 fps, and runs the app chain: `format=bgra,format=yuv420p,mpdecimate=...:max=9`. showinfo gives the time of each kept frame.

| Filter | 13 px light | 13 px dark | 16 px light and dark |
|---|---|---|---|
| Default, hold 100 to 500 ms | 14/15 (lost `x.y`>`x,y`) | 12/15 (lost `x.y`>`x,y`, `a:b`>`a;b`, `a'b`>``a`b``) | 15/15 |
| Default, hold 700 to 1700 ms | 15/15, up to 0.7 s late (caught by the forced frame of `max=9`) | 15/15, up to 0.7 s late | 15/15, at once |
| `hi=256:lo=128:frac=0.33`, all holds | 15/15, at once | 15/15, at once | 15/15, at once |

- Holds of exactly 1000 ms are not valid. The forced frame falls on each change (10 frames), so the default looked perfect. Use holds that are not multiples of 1 s.
- `hi=128:lo=64` gave the same result as `hi=256:lo=128`.
- Frame count: the new filter kept the same number of frames as the default, or 1 to 3 more (each extra frame is a change that the default dropped). A still page still keeps 1 frame each second (`max`).

Change: the app now uses `mpdecimate=hi=256:lo=128:frac=0.33:max=ceil(fps)-1`. Unit tests updated and pass.

App check (`test/app_runs.ps1`, `counter.html`, region 20,120,1880x860, 10 fps, new build): ddachg 104 of 104 values, PASS, 0.464 MB. gdichg 99 of 99, PASS, 0.448 MB. The FFmpeg command in `summary_6_10.json` has the new filter.

Not checked: look-alike changes in a real screen capture (the states were drawn offline, not captured); the file size of a long still recording with the new filter (the offline test shows the same frame count); fps other than 10.

### 6.11 More reads of B3, B9 and B11 for the quality default (run on Bran's PC on 2026-11-22)

Goal: decision 11. In 6.8, each run had one read, and the reader noise was larger than the quality effect. This test reads the same frames more times. B11 = CQ 17 (Sharpest), B3 = CQ 23 (Balanced), B9 = CQ 28 (Smallest), LL = lossless control.

Setup (checked):

- Frames: `test/prep_frames.py` with the 6.8 settings (threshold 0.1, min dark 0.002, no crop). The new reports are equal to the 6.8 prep files in all keys, also `kept_index`. Frames: B11 75, B3 74, B9 75, LL 75, each a 1120x892 PNG.
- The PNG images that the 6.8 readers got are byte-equal to the new files (B3 68, B9 53, B11 67, LL 75 images, compared in the 6.8 subagent transcripts).
- Reader: Claude Sonnet 5.5 (`claude-sonnet-5-5`) through the Message Batches API. One request for each read: the prompt, then for each frame a text label (`f001.png`, `f002.png` ...) and the frame as PNG through the Files API (`file_id`). Thinking: adaptive (the default), display summarized. Effort: not set (the API default). The 6.8 effort is not known. `max_tokens`: 100000 for the first two LL reads, 40000 for all other reads (cost limit; the model does not see this value). No fallbacks: batches do not allow them.
- Prompt: the 6.8 prompt, found in the 6.8 transcripts (16 subagents, all `claude-sonnet-5-5`, one prompt). Rules 2 to 6 are the same. Only the input and output lines changed: no folder, no Read or Write tool, the reply is the copy, and the frame and tag count is the last line after a blank line. Files: `reads2/prompt.txt` and `reads2/prompt_diff.txt`.
- Full size: each frame cost 1283 input tokens (40 x 32 patches of 28 px, plus 3). Thus the API did not scale the frames down. Each request had 97,060 input tokens (B3: 95,772), equal to the token count before the run.
- Blind: a request holds no run name, no set name and no CQ value. Set names S1 to S4 in random order (`reads2/blind_map2.json`: S1 LL, S2 B11, S3 B3, S4 B9).
- Rules written before the first lossy read (`reads2/rules.md`): 6 reads for each run; the job table uses r1 to r3; a read counts only with `stop_reason` = `end_turn`; a complete copy with errors is data and is not replaced.
- Scoring: `test/score.py`, with the 6.8 columns (rules of `test/phase_b_table.py`). The 6.8 copies, scored again with the same code, give the 6.8 values: B11 6/1, B3 11/5, B9 9/4, LL 6/2 and 4/0.
- All 24 reads count: `end_turn`, 300 different tags in tag order, no repeated tag, 300 of 300 lines caught. Each reader gave the correct frame count (74 for B3, 75 for the others).

Finding about the 6.8 reads (checked in the 6.8 transcripts): the Claude Code Read tool gave each frame file above about 500 KB to the model as a JPEG (full size, 1120x892), not as a PNG. Lower quality gives larger PNG files. Frames sent as JPEG: B9 (CQ 28) 24 of the 70 frames that its reader opened (it did not open f071 to f075), B3 (CQ 23) 16 of 74, B11 (CQ 17) 8 of 75, LL 0 of 75. Thus the 6.8 reads had one more lossy step, and the step was larger at a higher CQ. Its effect on those reads is not checked. The new reads send all frames as PNG.

Method difference (checked): on the same LL frames, the API reader made more errors than the 6.8 reader. Error lines outside L221-L240: 5.33 (mean of 6 new reads) against 1.0 (mean of the 2 reads in 6.8). Most of them are Base64 slips. Twice the reader changed a word inside the Base64 ("beside" to "near", "during" to "duping"). The API reader thinks little on clean frames (LL: 980 to 6,624 thinking tokens). With effort `max`, one LL read used all 100,000 output tokens for thinking and wrote no copy ($0.60; not counted). Thus Table D mixes two methods. Its spread shows the method gap too, not only the reader noise.

Flags (L232, checked in a crop): the screen shows the flags as letter pairs "DE FR JP ..." (6.4), in LL and in B9 (`reads2/visual/L231_L235_f056_x2.png`). The reads of the lossy runs copied the letters (3 to 4 of 6 reads of each run). The new LL reads wrote flag emoji in 6 of 6 reads, and `score.py` counts that as correct. Thus the reader rebuilt the flags from the letters on the clean frames. This is reader behavior, not a picture error. The cause is not checked.

Table B: the 3 new reads r1 to r3 (the job table). Values: mean (SD) [min-max].

| Run | Reads | Lines caught | Err. lines (no Zalgo) | Err. lines outside L221-L240 | CER | WER | MB (6.8 file) | MB/min during the scroll |
|---|---|---|---|---|---|---|---|---|
| B11, CQ 17 | 3 | 300 | 5.00 (1.00) [4-6] | 2.33 (1.53) [1-4] | 0.10% (0.04) | 0.37% (0.11) | 6.34 | 53.0 |
| B3, CQ 23 | 3 | 300 | 7.67 (3.06) [5-11] | 3.67 (2.52) [1-6] | 0.24% (0.09) | 0.60% (0.21) | 4.33 | 36.7 |
| B9, CQ 28 | 3 | 300 | 6.00 (1.73) [5-8] | 1.67 (1.15) [1-3] | 0.13% (0.11) | 0.42% (0.20) | 3.08 | 25.8 |
| LL, lossless | 3 | 300 | 9.67 (0.58) [9-10] | 6.00 (1.00) [5-7] | 0.23% (0.02) | 0.52% (0.06) | 9.97 | 82.5 |

Table C: all 6 new reads r1 to r6 (extended; decided before the first lossy read).

| Run | Reads | Lines caught | Err. lines (no Zalgo) | Err. lines outside L221-L240 | CER | WER |
|---|---|---|---|---|---|---|
| B11, CQ 17 | 6 | 300 | 6.83 (2.64) [4-11] | 3.33 (2.16) [1-7] | 0.17% (0.13) | 0.49% (0.20) |
| B3, CQ 23 | 6 | 300 | 7.83 (2.04) [5-11] | 3.83 (1.94) [1-6] | 0.27% (0.09) | 0.61% (0.14) |
| B9, CQ 28 | 6 | 300 | 7.33 (2.73) [5-11] | 2.50 (1.97) [1-6] | 0.18% (0.11) | 0.52% (0.24) |
| LL, lossless | 6 | 300 | 9.17 (0.98) [8-10] | 5.33 (1.21) [4-7] | 0.22% (0.07) | 0.50% (0.05) |

Table D: the 6.8 read and the new reads r1 to r3 (4 reads; LL 5 reads: R04, R04b and 3 new). The job asked for this table. It mixes two methods (see above).

| Run | Reads | Err. lines (no Zalgo) | Err. lines outside L221-L240 | CER | WER |
|---|---|---|---|---|---|
| B11, CQ 17 | 4 | 5.25 (0.96) [4-6] | 2.00 (1.41) [1-4] | 0.16% (0.12) | 0.45% (0.19) |
| B3, CQ 23 | 4 | 8.50 (3.00) [5-11] | 4.00 (2.16) [1-6] | 0.28% (0.11) | 0.66% (0.21) |
| B9, CQ 28 | 4 | 6.75 (2.06) [5-9] | 2.25 (1.50) [1-4] | 0.20% (0.18) | 0.55% (0.31) |
| LL, lossless | 5 | 7.80 (2.68) [4-10] | 4.00 (2.92) [0-7] | 0.22% (0.05) | 0.56% (0.07) |

Column notes:

- Columns as in 6.8. CER and WER: outside Zalgo, a missed line counts as wrong. No read missed a line.
- MB (6.8 file) = the full file, also the still page before and after the scroll. MB/min during the scroll = the video packets from the first to the last kept frame (7.4 to 7.5 s of scroll; `reads2/scroll_bitrate.json`). Against CQ 23: CQ 17 is 1.44 x (1.46 x for the file), CQ 28 is 0.70 x (0.71 x for the file).
- Error lines by section (Table C reads, mean for each read): Base64 B11 2.00, B3 2.50, B9 1.17, LL 4.33. Look-alike B11 1.33, B3 1.33, B9 1.33, LL 1.00. SHA-256, code, prose, table, colored and mixed lines: 0 in all reads.

Is the difference between CQ 17, 23 and 28 larger than the spread? **No.**

- Main column (error lines outside L221-L240), 6 reads for each run: CQ 17 3.33, CQ 23 3.83, CQ 28 2.50. The largest difference is 1.33 lines. The SD inside each run is 1.9 to 2.2 lines. The ranges overlap (1-7, 1-6, 1-6). The order is not the CQ order: CQ 28 has the lowest mean.
- Exact permutation tests (two-sided), 6 against 6 reads: main column p = 0.36 to 0.79, error lines without Zalgo p = 0.54 to 0.84, CER p = 0.15 to 0.93. With 3 against 3 reads, a p below 0.10 is not possible; the tests gave p = 0.40 to 0.70 for the main column. Table D, main column: p = 0.29 to 1.00.
- LL is not better than the lossy runs. In the main column it is worse (5.33; against CQ 28: p = 0.026, not corrected for the number of tests), mostly from Base64 slips.
- The reader thinks more on lossy frames (mean thinking tokens: CQ 17 6,088, CQ 23 7,052, CQ 28 8,829, LL 2,530). More thinking goes with fewer errors (Spearman rho = -0.68 over the 24 reads, permutation p about 0.0005). Thus the error count measures the work of the reader too, not only the picture. The cause is not checked.

Repeated errors (post hoc). Most errors move from read to read. Some errors repeat in most reads of one run and never occur in LL. Rule (made after a look at the data): the same wrong copy in 3 or more of the 6 new reads of a run, and never in the 6 new LL reads. L232 (flags) is left out (see above).

| Run | Lines with a repeated error | Wrong copy |
|---|---|---|
| B11, CQ 17 | 0 | - |
| B3, CQ 23 | 1: L198 (4 of 6) | "B00K8" as "B0OK8" (a zero as the letter O) |
| B9, CQ 28 | 2: L191 (4 of 6; also the 6.8 read), L234 (6 of 6) | L191 "0Il" as "0I1" (l as the digit 1); L234 one skin tone a shade too dark (🏽 as 🏾) |

- The count goes up with CQ: 0, 1, 2 lines. L191 and L198 are look-alike errors, the error type that the Grok test reported in 3.1. L234 is inside L221-L240. Thus the main column has one repeated line for CQ 23 (L198) and one for CQ 28 (L191).
- L234: the 6.8 B3 read (R05, CQ 23) made the same shade change. Thus L234 is CQ 28-only in the new method only.
- The reader thinks less on LL frames, so "never in LL" is a weak filter. The stronger comparison is B9 against B11 (recorded one after the other, 75 frames each): L191 is wrong in 4 of 6 against 1 of 6 new reads.
- L191 for B9: 5 of 7 reads (6.8 + new) against 1 of 22 reads of the other runs: Fisher exact p = 0.001. L198 for B3: 4 of 7 against 0 of 22: p = 0.0015. Both p values are not corrected for the about 40 lines that had an error.
- Only one recording for each CQ. B3 came from another time of the session than B9 and B11, so the text sits at other positions in its frames. Thus a CQ effect and a frame-position effect cannot be separated.
- Pixels: frame f046 at 3x zoom shows the same shapes of "0Il" and "B00K8" in all four runs, with light ringing at CQ 28 (`reads2/visual/L191_L198_f046_x3.png`). Frame f056 at 2x zoom shows softer emoji colors at CQ 28 than in LL (`reads2/visual/L231_L235_f056_x2.png`). The other frames that show these lines are not checked.

Recommendation (Bran decides):

1. Keep Balanced (CQ 23) as the default. The error lines for each read do not differ between CQ 17, 23 and 28 by more than the read-to-read spread.
2. Smallest (CQ 28): by the columns fixed in `reads2/rules.md`, it reads as well as CQ 23, and it is 29% smaller. Against it: 2 repeated errors (one look-alike character, one color shade), post hoc, from one recording. Advice: do not make it the default yet. This point is not settled.
3. Sharpest (CQ 17) had no repeated error line, but it is 1.46 x the size of CQ 23, and the evidence is weak (post hoc rule, one recording for each CQ). Keep it as a manual choice for recordings in which single characters must be exact (IDs, keys, hashes).
4. In folder mode (the default output since 3.0.0), the AI reads lossless PNG frames, and the quality setting changes only `clip.mp4` (7.12). Thus this choice matters mostly for "MP4 only".
5. If Bran wants a firm answer about look-alike characters: make 3 new recordings at each CQ (other frame positions) and read each one 3 to 6 times with `test/quality_reads.py`. Each read costs about $0.18 at batch price.

Cost (checked, from the `usage` fields of the API, batch price $1 / $5 per million input / output tokens): $4.81 for all 25 requests (2,418,772 input and 478,291 output tokens). The 24 reads that count: $4.21 (mean $0.18; 10,625 to 28,519 output tokens). The effort `max` read: $0.60. The limit was $12. The 299 frames on the Files API (free) expire by themselves on 2026-11-30 at 00:40 UTC. No deletion is necessary.

Checked:

- The frames, the prompt, full size (token count), the blind set names, the 24 reads with the checks above, the scores, the tables, the tests, the repeated-error rule, the cost.
- The JPEG step and the missing B9 frames of the 6.8 reads.

Not checked:

- The cause of the repeated errors (L191, L198, L234) in the pixels. Only one frame for each line was looked at.
- Why the reader rebuilt the flags of L232 on the LL frames only.
- A CQ effect separate from a frame-position effect (one recording for each CQ).
- Other AI readers (Gemini, ChatGPT, Grok), other effort levels, and the 6.8 method with all frames as PNG.
- Font sizes other than 100%, other scroll speeds, 4:4:4 at these CQ values, dark pages, other content.
- The effect of the JPEG step on the 6.8 reads.
- The cause of the link between thinking tokens and errors.

Files (Git ignores them): `test/results/phase_b/reads2/`: `rules.md`, `blind_map2.json`, `prompt.txt`, `prompt_diff.txt`, `files.json`, `count.json`, `batches.json`, `results.json` (status, usage, cost and checks of each request), `raw/` (full batch results), `reads/` (copies and full replies), `thinking/` (thinking summaries), `scores/` (24 new and 5 old copies), `table.md` (tables A to H), `summary.json`, `line_tests.txt`, `check_68_frames.json`, `scroll_bitrate.json`, `prep/`, `frames_by_run/`, `visual/`.
Scripts: `test/quality_reads.py`, `test/check_68_frames.py` and `test/scroll_bitrate.py`. Each has the repository paths at the top (and `check_68_frames.py` the path of the 6.8 transcripts).

Commands of this run, in order (`quality_reads.py`; the defaults are `--reads 1-3` and `--max-tokens 100000`):

1. `python test/prep_frames.py VIDEO test/results/phase_b/reads2/frames_by_run/RUN` for B3, B9, B11 and LL (VIDEO from `RUN.prep.json`).
2. `blind`, then `score` (scores the 6.8 copies), `upload`, `count`.
3. Pilot: `submit --sets S1 --reads 1`. Diagnostics: `submit --sets S1 --reads 2` and `submit --sets S1 --reads 1 --id-prefix m --effort max`.
4. Main batch: `submit --sets S1,S2,S3,S4 --reads 1-6 --max-tokens 40000` (S1 r1 and r2 are skipped as done).
5. `wait --then-collect`, `score`, `table`.

## 7. Tracker

| Item | Status |
|---|---|
| Research ShareX region capture | Done |
| Read current code | Done |
| Test kit (6.1) | Done. Self-tests pass on Bran's PC (score 28/28, frames 70/70). `frames.py --counter` read real recordings in Phase A (0 failed frames). `scroll.html` and `score.py`: no real recording yet. |
| Phase A test | A1 to A5 checked on Bran's PC (all PASS, see 6.5). A6 not done. Scroll stress run checked (6.6): no lost frames with A1 or A4 settings. |
| Region selector | Checked on Bran's PC (6.7). Bug 1 fixed. Flow changed by Bran: release starts, one hotkey starts and stops, no "record last region". |
| Recording frame | Checked on Bran's PC (6.7). Border and Stop button show and work. WDA_EXCLUDEFROMCAPTURE works with ddagrab and gdigrab. |
| ddagrab + NVENC backend | Checked on Bran's PC (6.7): ddagrab and gdigrab with NVENC, 4:2:0 and 4:4:4, no lost frames. libx264 fallback, multi-monitor and multi-GPU: UNVERIFIED. |
| "Changes only" mode | Checked on Bran's PC (6.7, 6.8). Bug 2 fixed. Default since 2026-11-14 (section 5). UNVERIFIED: that every AI frame tool handles variable frame rate. |
| GUI test of the new app on Bran's PC | Done (6.7). 5 bugs fixed. Start and stop made faster. Not checked: multi-monitor, `~`, Shift and Ctrl while dragging. |
| Phase B test | Done (6.8). Gap rule holds. No run passes the 6.3 pass rule, because the reader fails emoji lines also on lossless frames. Decision about the default quality and "Changes only" is open for Bran. |
| README update | Done (2026-11-14). `README.md` describes the new Windows app: selector flow and keys, all settings with the code defaults, capture and encoder fallbacks, quality table (CQ 28/23/17, CRF 32/27/21), "Changes only", the frame-rate rule from 6.8, a test summary, and the Windows unit-test commands. `packaging/windows/README.txt` has the new start and stop text. Checked: each default and label against `RecorderCore.cs`, `DesktopRecorder.cs` and `RegionSelector.cs`; the README unit-test commands ran as written on Bran's PC (tests: `ok`); the app build ran with the README options but a different `/out` path (the running app locks `DesktopRecorder.exe`); the Releases page has v1.0.0 only (`gh release list`). Not checked: the README on GitHub (not pushed). |
| Pointer setting (6.9) | Done. Unit tests pass. Size test checked on Bran's PC. Settings dialog not checked with a real click. Bran sees no need for that check (2026-11-14). |
| Save in Settings during a recording (bug 4) | Checked by Bran on 2026-11-14: the recording continues. |
| libx264 fallback in the app | Bran: "seems ok" (2026-11-14). Not run. Still UNVERIFIED. |
| Default fps 10 | Done (decision 10). Unit tests pass. |
| Release v2.0.0 | Done (7.2). |
| Look-alike changes in "Changes only" (6.10) | Done. Lower `mpdecimate` thresholds. Offline test 15/15. App runs PASS. Released in v2.0.1. |
| Release v2.0.1 | Done (7.3). |
| Release v2.0.2 | Done (7.4). Tray icons not checked in Bran's real tray. |
| AI capture folder (7.5, 7.6) | Real recordings checked on Bran's PC (7.6). Merged into `main` and released in v3.0.0 (7.11). |
| Audio and transcript (7.7) | Off by default. Released in v3.0.0. WASAPI capture and a whisper.cpp transcript checked on Bran's PC (7.9, 7.10). Audio sync and other Whisper programs UNVERIFIED. |
| Twitch recording faults (7.7) | Motion note and false-scroll fix done, tested on synthetic files. UNVERIFIED on the real recording. Clipboard fault fixed in 7.9. |
| README and release notes for v3.0.0 | Done (7.12). |
| New defaults and index.md warnings | Done (7.13, 7.14). |
| Release v3.0.1 | Done (7.15). The exe from the zip is not run on a PC. |
| ZIP on the clipboard (7.16) | Code done, test build installed. Cloud paste test open. Not released. |
| One ZIP only in folder mode (7.18) | Code, unit tests and a ZIP check done on branch `zip-only`. Not installed, not pushed, not released. No real recording and no paste test yet. Replaces 7.16. |
| False transcript rows, pointer motion warning, file-count text (7.19) | Code, unit tests and offline tests on three real recordings done on branch `zip-only`. Not installed, not pushed, not released. No real recording with this build yet. |
| UI redesign: note box, Settings, tray menu (7.20) | Code, unit tests and offscreen previews (100%, 125%, 150%) done on branch `zip-only`. No live test (Bran used the PC). Not installed, not pushed, not released. Live test plan in 7.20. |
| "Busy, then ready" indication for the ZIP (7.21) | Code, unit tests, offscreen previews and an independent review done on branch `zip-only`. No live test. Not installed, not pushed, not released. Live test items 9 to 13 in 7.20. |

### 7.1 Later and future work (Bran, 2026-11-14)

- Done (2026-11-22): more reads of B3, B9 and B11 to decide the quality default (decision 11). See 6.11.
- Later: Phase A run A6.
- Later: selector keys `~`, C, M, P, and Shift and Ctrl while dragging. Not checked yet.
- Later: the tray icons in Bran's real tray. Not checked yet.
- Not now: multi-monitor PCs and monitors with different scales.
- Future: a PC without an NVIDIA GPU.
- New session: idea to explore, an "Export frames" feature (unique frames as PNG) for AI tools that sample a video at 1 fps (for example Gemini).
- Done (2026-11-14): correct the stale comment in `RecorderCore.cs` that says no 4:4:4 encode ran (6.7 shows it ran). Also corrected: the comments on `NvencCqForQuality` (Phase B result), `RecordingFrame.cs` (WDA_EXCLUDEFROMCAPTURE checked) and the Settings dialog in `DesktopRecorder.cs` (125% checked). Comments only, no code change.

### 7.2 Release v2.0.0

Done on 2026-11-14. https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v2.0.0

- `test-kit` pushed. `main` fast-forwarded to d96cbc3 (no merge commit). The Build workflow on `main` passed (Windows build, Mono core tests, Linux tests).
- Tag v2.0.0 = d96cbc3. The Release workflow failed in the Windows zip step: `packaging/make-windows-zip.sh` put the path `D:\a\_temp` inside the Python source, and `\a` became a control character. The Linux job passed.
- Fix: the script gives the paths to Python as arguments (commit after d96cbc3). Not checked on a GitHub runner yet. It runs on the next tag.
- The Windows zip of v2.0.0 was built on Bran's PC from the tagged source (`csc.exe`, same options as the workflow) with the fixed script. The Linux archive is the artifact of the release run. Both are attached. The downloaded zip has the same SHA-256 as the local zip.
- `git tag -F` removed the `##` heading lines from the tag message. The release notes come from a file (`gh release create --notes-file`).
- Not checked: the v2.0.0 zip on a second PC.

### 7.3 Release v2.0.1

Done on 2026-11-14. https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v2.0.1

- Tag v2.0.1 = a7c8144 (6.10 filter fix, merged side-session commits, version 2.0.1). Build workflow on `main` passed.
- The Release workflow passed in full (windows, linux, publish). The fixed `make-windows-zip.sh` works on the GitHub runner.
- Both archives downloaded and checked: each has the 2.0.1 README.
- Workflow limit: the release body was the commit message, not the tag message. `actions/checkout` does not fetch the annotated tag object, so `git tag -l --format=%(contents)` gives the commit text. The notes were set after with `gh release edit --notes-file`. Do the same for the next release, or fix `release.yml` (for example `fetch-tags: true` or `git fetch --force origin "refs/tags/$tag:refs/tags/$tag"`). Fixed in v2.0.2 with the `git fetch` line (7.4).

Mark an item "checked" only after a run on Bran's PC. Mark all other claims UNVERIFIED.

---

### 7.4 Release v2.0.2

Done on 2026-11-15. https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v2.0.2

- Content: the new tray icons (2fe9d86) and the `release.yml` fix for the release notes. Version 2.0.2 in the manifest and in the two archive README files.
- Before the tag: the app build and the unit tests passed on Bran's PC (tests: `ok`). The Build workflow on `main` passed.
- Tag v2.0.2 = 89c03d9 (annotated). The Release workflow passed in full (windows, linux, publish).
- Checked: the release body equals the tag message, so the `release.yml` fix works. The release is Latest and not a draft.
- Checked: both archives downloaded. The zip has `DesktopRecorder.exe` and a README that shows 2.0.2. The Linux README shows 2.0.2.
- The subject of commit 89c03d9 starts with a BOM character (`Set-Content -Encoding utf8` in PowerShell 5.1 writes a BOM). Not corrected: a correction needs a force push of `main`. Write message files with `[IO.File]::WriteAllText` and `UTF8Encoding($false)`.
- Not checked: the tray icons in Bran's real tray; the v2.0.2 zip on a second PC.


### 7.5 AI capture folder, Phase 1 (2026-11-19, branch `ai-capture`, not pushed)

Source: `desktop-recorder-ai-capture-brief.md` (Version 2, not in Git) and Bran's decisions and conditions A to H of 2026-11-19.

What the code does now:

- New output mode "folder" (default). "MP4 only" keeps the old behavior (setting `output_mode`).
- In folder mode, FFmpeg records a lossless 4:4:4 work file (`work_lossless.mp4`, fragmented). No split.
- After Stop, a pass on a background thread (`AfterRecording.cs`) writes `frames\NNN.png`, `clip.mp4` and `index.md`. Then it deletes the work file. When the pass fails, the work file stays and a normal `clip.mp4` is written (condition D).
- Settled-frame rules (`SettledFrames.cs`): 400 ms still time (`settle_ms`), blink and spinner masks, a frame each 5 s during motion (`still_interval_s`), a scroll-distance rule at 60% of the moving area (`scroll_percent`, condition A), a frame before a step that would leave a gap, and a frame at a turn of the scroll.
- `clip.mp4`: `-copyts`, `-g 999999`, `-forced-idr 1`, `-force_key_frames` with each time minus 0.01 s (condition C). libx264 with `scenecut=0` when NVENC is not used.
- `index.md`: start time with the numeric UTC offset (condition F), note, length, region, frame count, a warning above 20 frames and above 1568 px (condition G), and one row for each frame. Times are on the time line of `clip.mp4` (condition H).
- Window titles: the window at the center of the region, every 250 ms. The FFmpeg stats clock puts them on the video time line. Checked from two capture logs: the stats `time=` starts at 0 at the first frame (first packets at 0.2 s and 0.27 s). Thus the index adds the time of the first frame before it looks up a title.
- Note box after Stop. It does not block the pass. When the pass fails, the note goes into `note.txt`.
- Clipboard: the folder. Tray items: "Open last recording folder", "Copy ZIP of last recording" (the ZIP leaves out the work files).

Tests run on Bran's PC (offline, from files):

| Test | Result |
|---|---|
| Unit tests (`tests/CoreTests.cs` + `tests/AiCaptureTests.cs`) | ok |
| Synthetic, status line changes 5 times in 60 s, with a blinking caret | 6 frames, each status in one frame (fixed and "changes" mode) |
| Synthetic, blinking caret 30 s | 1 frame |
| Synthetic, 15 look-alike changes (`test/settled_render.cs`) | 15 of 15 |
| Synthetic, scroll of 100 lines and 5 settings pages | 0 px missed (own measure and `test/scroll_coverage.py`) |
| Keyframes of `clip.mp4` (brief ffprobe command) | equal to the PNG count in all runs |
| Bran's clip `desktop_2026-11-19_14-02-29_611.mp4` (condition B), lossy input | 50%: 37 frames; 60%: 34 frames; 70%: 33 frames; 0% missed in each (independent check `test/scroll_coverage.py`). 34 keyframes at the same times as the 34 index rows (first 0.600 s). |
| `clip.mp4` size, Bran's clip at quality 0 (Smallest) | 3.1 MB. The original file is 2.1 MB. Causes: 34 IDR keyframes in 19 s, and a second encode of a lossy file. |
| Pass time (condition D), lossless files made from Bran's lossy recordings, code of commit 68b2872 and later | 60 s, 1302x960: 3.6 s. 5 min, 1920x1080: 33 s. Worst case: lossy noise makes almost every frame a change. |
| Pass failure (a file named `frames` blocks the folder) | `ok=False`, normal `clip.mp4` written (1 keyframe), input kept. |
| Note and ZIP | The note is in `index.md`. The ZIP has `clip.mp4`, `index.md` and the frames, no work file. |
| Pass without `ffprobe.exe` (only `ffmpeg.exe`) | Same 6 frames on the status video. |

Not checked:

- Condition E (counter test of the lossless capture at 15 fps). Two tries failed: the display was off (ddagrab gets no frames), then Chrome did not come to the front and the capture recorded another window. The script now brings the page to the front and records nothing when another window stays in front. Run `test/lossless_counter.ps1` with the PC left alone for 40 s.
- That `clip.mp4` made with `-copyts` plays in a standard player, and the brief's keyframe extraction command.
- The Mono build and the GitHub CI with the new files.
- `index.md` writes "Note:", the brief shows "Note from Bran:". The README user text still describes the MP4 output only.
- The pixel tolerance (24), the block minimum (3) and the 1% density gate were tuned on lossy files. Check them on the first real work file.
- Any recording with the new app (brief section 8 tests). The new build is not installed. The running v2.0.2 locks `DesktopRecorder.exe`.
- The time from Stop to "clipboard ready" in the app (the app shows it in the status line). The real value adds the FFmpeg stop (about 0.4 s, 6.7).
- Title timing against real window changes.
- Extra frames on Bran's clip come from lossy encoder noise on still frames. A lossless work file should not have this noise. UNVERIFIED.
- Known limits: a scroll step larger than about 80% of the moving area cannot be measured; the scroll rule uses the box of all changed rows, so other changes during a scroll make the box larger; a log-off (SessionEnding) keeps the work file but runs no pass.

### 7.6 AI capture, real recordings (2026-11-19, branch `ai-capture`, not pushed)

Setup: build `DesktopRecorder-ai.exe` from the work tree (same `csc` options as `build.cmd`, new `/out` name). Bran's settings file: 15 fps, "changes" mode, quality 0, pointer hidden, folder mode. The installed app (`Desktop\tools\screen-vibe-recorder-2.1.0-windows-x64\DesktopRecorder.exe`) was stopped for the tests and is not changed.

Bran asked the agent to make the recordings. The script `test\ai_capture_runs.ps1` made them with synthetic input: the hotkey, a drag in the selector, and the note in the note box. Log files: `test\results\ai_capture_runs*.log` (Git ignores them).

**Condition E (counter test of the lossless capture, 15 fps, 10 s, method of 6.5): PASS.**

| Run | Frames | Counter values | Failed frames | Work file | Result |
|---|---|---|---|---|---|
| Changes only | 150 | 150 of 150 | 0 | High 4:4:4 Predictive, yuv444p | PASS |
| Fixed | 150 | 150 of 150 | 0 | High 4:4:4 Predictive, yuv444p | PASS |

- Cause of the failure in try 3: `lossless_counter.ps1` stops Chrome with force, so the next Chrome start showed "Restore pages?" on top of the bar code. Fix: `--hide-crash-restore-bubble`.
- Each run has one counter step of 16 or 17 values (the median is 8). One capture was late by about one frame time. No value was lost.
- This test ran before the `-bf 0` change below. Not run again after that change.

**Faults that the real recordings found (all corrected):**

| # | Fault | Cause | Fix |
|---|---|---|---|
| 1 | Times in `index.md` started at 3.0 s on a still screen. The window titles were then looked up at wrong times. | The lossless work file had B-frames. A fragmented MP4 then starts at (B-frame delay x frame time). "Changes" mode keeps 1 frame each second on a still screen, so the start was 3 x 1 s. The "0.2 s" in 7.5 was probably the same fault at 15 fps (3 x 0.067 s; not tested). | `-bf 0` in the lossless capture. Checked: the first frame is at 0.0 s in all later recordings. |
| 2 | Keyframes in `clip.mp4` were up to 1 s after their time in `index.md` (index 14.3 s, keyframe 15.0 s). | The clip encoder used the frame rate of the work file (1 fps on a still screen) as its clock. | `-enc_time_base 1:90000` in the clip encode. Checked: keyframes at 11.267, 21.267 ... and index rows at 11.3, 21.3 ... |
| 3 | The note box was in the last 2 frames of the video and made 2 false "scroll" rows. | The box opens while FFmpeg still captures. | The note box has `WDA_EXCLUDEFROMCAPTURE`. Checked: the second tour has no note box and no false rows. |

Other changes: `index.md` writes "Note from Bran:". With `DESKTOP_RECORDER_KEEP_WORK=1` in the environment, the app keeps the lossless work file (test switch). `README.md` describes the folder output. New test page `test\status.html`.

**Recordings with the corrected build (brief section 8):**

| Test | Folder | Length | PNG | Keyframes | Stop to clipboard | Result |
|---|---|---|---|---|---|---|
| Status line, 5 changes | `desktop_2026-11-19_19-17-17` | 59.3 s | 6 | 6 | 1.3 s | PASS. Each of the 6 status texts is in one frame (looked at the frames). |
| Settings tour, 5 pages | `desktop_2026-11-19_19-19-09` | 44.7 s | 20 | 20 | 1.6 s | PASS with notes. All 5 pages show. No frame shows a blend or blur. |
| Caret | `desktop_2026-11-19_19-18-27`, `..._19-26-36` | 31 s, 31 s | 1, 17 | 1, 17 | 1.2 s | NOT PROVEN. See below. |
| 60 s | `desktop_2026-11-19_19-20-04` | 61.1 s | 1 | 1 | 1.0 s | Still screen only. See below. |
| 5 min | `desktop_2026-11-19_19-21-17` | 302.3 s | 3 | 3 | 1.8 s | Almost still screen. See below. |

- The keyframe count is equal to the PNG count in all 6 folders. Each `clip.mp4` starts at 0.0 s. FFmpeg decoded two of them (status, 5 min) to the end with no decode error, and their packet DTS values increase. The check command printed time stamp warnings from its own null output (1 fps clock), not from the clip.
- "Stop to clipboard" = the time from the stop hotkey until the script saw the new folder on the clipboard. The script looks at the clipboard only after it typed the note (about 1 s). Thus each value is an upper limit for "Ready ... s after Stop" in the tray menu, and values near 1 s can hide a shorter time. The tray text was not read.
- Tour notes: 4 of the 20 frames have the mark "(still scrolling)" (scroll-distance rule, condition A). They are sharp frames during a scroll. A page change shows as "scroll ... up" or "small change" in the "What changed" column (known limit: the scroll rule uses the box of all changed rows). The page title was outside the region that the script selected.
- Caret: probable cause: Windows stops the blink of the text cursor after 5 s with no input (`CaretTimeout` = 5000 ms in the registry of this PC; the effect was not tested directly). In the third run the cursor was in the region for 13.7 s and gave 1 frame, but the 14 kept capture frames show the same cursor pixels, so the cursor did not blink. In runs 1 and 2 the cursor was outside the region. Bran stopped run 3, and the rest of that recording shows the Claude window. The synthetic caret test (7.5) is still the only proof.
- 60 s and 5 min: the scroll page did not start (the Space key from the script had no effect), so these recordings show a still page. In the 5 min recording the Claude window came to the front before the end. The times 1.0 s and 1.8 s are thus for still screens.
- Pass time on busy content (offline, `test\process_clip.cs`): the tour work file (44 s, 1400x900, real lossless): 1.3 s. The same file 7 times in a row (309 s, 637 changed frames, 134 PNG): 5.8 s. This is not a real 5 min recording.

**Noise thresholds on real lossless files (open point of 7.5): no change needed.** The pass gave the same frames with pixel tolerance 24 (the default), 8 and 2: status 6, 6, 6; tour 20, 20, 21 (first tour 22, 22, 21). A real lossless work file has no encoder noise on still frames. This also confirms the last "Not checked" point of 7.5.

**`clip.mp4` size:** tour 1.45 MB (work file 4.2 MB), status 0.29 MB (work file 0.05 MB). On a still screen the clip is larger than the lossless work file, because each settled frame is an IDR keyframe.

**Time zone:** `index.md` shows "UTC-06:00". Windows on this PC is set to "Alberta Standard Time" with the offset -06:00. The index follows the PC setting. If the setting is wrong, correct it in Windows.

Not checked:

- A real recording of a blinking text cursor (needs input in the 5 s before and during the recording).
- The time from Stop to "ready" for a real 60 s and a real 5 min recording with a busy screen.
- That `clip.mp4` plays in a normal player (only FFmpeg decoded it). The brief's keyframe extraction command gave 20 PNG files for the tour clip; they were not compared with `frames\` pixel by pixel.
- The counter test after the `-bf 0` change. The tray text "Ready ... s after Stop". The note box with a real key press. "Copy ZIP" in the app. The Settings window of the new build.
- The Mono build and the GitHub CI with these changes.
- The final test of the brief (a new AI session reads the folder and answers 5 questions).

The x264 lossless fallback did not get `-bf 0`: its preset `ultrafast` has no B-frames (x264 default for that preset; not run on this PC).

Settings file after the tests, against the copy from before: 5 new keys (`output_mode=folder`, `settle_ms=400`, `still_interval_s=5`, `scroll_percent=60`, `size_limit=1568`), and `region` and `last_region` changed from `167,92,1482,762` to `0,40,1400,940` (the last test region). The clipboard holds the last test folder.

Shared items: the new build uses the same settings file (`%APPDATA%\DesktopRecorder\settings.txt`) and the same "start with Windows" entry as the installed app. The new keys in the settings file do no harm to the installed app (not checked with the 2.1.0 exe).

**State at the end of the session (2026-11-19, 20:02):**

- Bran approved the deletion of the test items. The recording folders that the table above names are deleted, and so are the log files in `test/results/` and the scratchpad files of both sessions that were in the list. The results in this section cannot be checked again from those files.
- Bran uses the new build now: `DesktopRecorder-ai.exe` in the repository folder, built from commit e53e69e (Git ignores no such name, so do not add it to a commit). It runs without the keep-work switch. The "start with Windows" entry (`HKCU` Run, name `DesktopRecorder`) points at this exe.
- The 2.1.0 app in `Desktop/tools/screen-vibe-recorder-2.1.0-windows-x64` is stopped and not changed. Bran keeps it as the fallback.
- The settings file from before the tests is saved as `%APPDATA%/DesktopRecorder/settings.backup-2026-11-19-before-ai-test.txt` (old region `167,92,1482,762`).
- Other files of the first session are still in its scratchpad (`syn_*.mp4`, `sheet.png`, some folders). They were not in the deletion list.
- Next steps: the three open tests above (caret, busy 60 s and 5 min, counter test after `-bf 0`), then the final AI read test of the brief, then Bran decides about push and release.

### 7.7 Audio, transcript and the Twitch faults (2026-11-20, cloud session, branch `ai-capture`)

Bran's PC was off. The work ran in a cloud container: Linux, Mono 6.8 (`mcs`), FFmpeg 6.1, Python 3 with PIL. No Windows, no `csc.exe`, no NVENC, no screen, no sound device. The brief file `desktop-recorder-ai-capture-brief.md` is not in Git and was not read.

Note: 7.5 and 7.6 say "not pushed". Since commit 535753c, `ai-capture` is on GitHub.

Design note: [docs/audio-transcript.md](docs/audio-transcript.md). It has the research, the decisions, the time line rule, the test plan for Bran's PC (section 9) and all details.

**Task 1: audio and transcript (off by default).**

- Setting: check box "Record the sound of the PC, and make a transcript if Whisper is installed", key `record_audio` (same key and meaning as the Linux build; Windows default 0). Keys without a control: `whisper_path`, `whisper_model`, `whisper_language` (`auto`).
- Capture: own WASAPI loopback with COM interop (`AudioCapture.cs`), no NuGet. FFmpeg `dshow` needs "Stereo Mix" or a virtual driver; FFmpeg has no WASAPI input (UNVERIFIED, not read in this session). The capture writes `audio_system.wav` (mix format). Silence fills the time with no packets, by the QPC time of each packet.
- Mux after Stop, not live (a live pipe can delay the video capture). Rule: WAV time 0 is at `(audio start - clock start) - ClockOffset + first frame time` on the time line of the work file. `RecordingSession.ClockStartTimestamp` is new.
- Folder mode: `clip.mp4` gets the audio (AAC 160 kb/s). If that encode fails, the clip is encoded without audio. A work file that stays gets the audio too. "MP4 only": each video file gets the audio by a stream copy after Stop; a split file gets only its own part.
- Transcript: whisper.cpp (`whisper-cli` + `ggml-*.bin`) or an openai-style program (`faster-whisper-xxl`, `whisper-ctranslate2`, `whisper`). Input: 16 kHz copy on the time line of `clip.mp4`. Output: section "Audio and transcript" at the end of `index.md`, and `transcript.srt`. A missing, failing, slow (limit 2 min + 3 x length) or silent case writes the reason and never fails the pass. The AI message on the clipboard names the transcript and the audio.
- Microphone: design only (note, section 8).

**Task 2: faults from Bran's Twitch recording (full screen, 25.9 s, 7 frames).**

| Fault | Change | Test |
|---|---|---|
| Video never settles, so only 5 s fallback frames | `index.md` gets a warning when the picture changed (not a scroll) during 70% or more of a recording of 8 s or more: the frames are samples, use `clip.mp4` for the motion. Chosen over "suggest MP4 only", because the folder keeps the titles, note, audio and transcript, and only `index.md` can tell the AI. The tray status line says it too. | Synthetic: note for a video player page, `testsrc2`, `mandelbrot`, `life`. No note for 3 scroll videos and `cellauto`. Unit test `TestMotionReport`. |
| Frame 006: false "scroll, 573 px up" from video motion | A shift counts only when 80% or more of the edge pixels in the changed columns match after the shift (`ShiftMatchRatio`). Also: the band votes and the step before always get the fine check (a sort tie could drop the true step). | `testsrc2` 26 s: 19 false scroll frames before, 0 after. Plain scroll, header + sidebar, video inside a scrolling page: 0 missed rows before and after. Unit test `TestNoisyShiftIsNotScroll` fails with the old rule (-824 px), passes with the new rule. |
| Clipboard: file drop and text together (risk, not checked) | No change. The note (section 10.3) has the risk, 3 possible fixes and a paste test for Bran. | - |

**Bran's YouTube test recording (`desktop_2026-11-20_00-00-01_531.mp4`, "MP4 only", 19.9 s, lossy, no audio), pass run under Mono (note 10.2.1):** the motion note shows (98%). No large false scroll before or after the change. The new match check rejected 1 shift and accepted 4 shifts of 1 to 3 px (probably real camera motion in the video). Change, approved by Bran: rows of a changing screen do not name a scroll below 2% of the moving area (text only, frames unchanged). Checked on this file: no row names a scroll. Not checked: audio (the file has none), the 573 px fault (this file does not have it).

Test changes:

- `TestStillChanging` used `Page(100 + i)`. `Page(seed + 1)` is `Page(seed)` moved by 12 rows (checked: 28800 of 28800 bytes equal), so that test fed a real scroll and passed only by a sort tie. It now uses a video-like frame.
- `CoreTests` used `record_audio=1` as an example of a Linux-only key. That key is now a Windows key too. The tests use `start_on_login=1` for that and check `record_audio` separately.
- New file `tests/AudioTests.cs` (WAV writer, clock rule, filters, clip arguments, SRT reader, index section, Whisper search and arguments, AI message).
- `test/process_clip.cs` has environment switches for the audio (`AUDIO_FILE`, `AUDIO_START`, `CLOCK_OFFSET`, `AUDIO_PEAK`, `WHISPER_PATH`, `WHISPER_MODEL`, `WHISPER_LANGUAGE`, `AUDIO_INTO_WORK`, `DELETE_WORK`).
- Build lists: `build.cmd`, `.github/workflows/build.yml`, `.github/workflows/release.yml`, `run-tests.sh` and the README commands have `AudioTranscript.cs` and `AudioCapture.cs` (and `tests/AudioTests.cs` for the tests).

Checked (cloud, 2026-11-20):

- Unit tests with Mono: `ok`. `xvfb-run -a ./run-tests.sh`: Mono tests `ok`, 13 Linux tests OK.
- The whole app compiles with Mono `mcs /target:winexe` (all 10 source files). This is not proof for `csc.exe`.
- Full pass with audio under Mono (`test/process_clip.cs`), WAV from `WavWriter` (float, `WAVE_FORMAT_EXTENSIBLE`), beeps at WAV time 1, 4, 7 s: shift -0.45 s gave beeps at 0.550, 3.550, 6.550 s in `clip.mp4` and in the transcript; shift +0.30 s gave 1.300, 4.300, 7.300 s. Keyframes still equal the frame rows.
- Fake Whisper programs (Python scripts with the same arguments): whisper.cpp form, openai form with model and language, failing program, missing program, missing `whisper_path`, silent audio. All gave `ok=True` and the reason in `index.md`.
- Failed pass (a file named `frames` blocks the folder): normal `clip.mp4` and the kept work file have the audio at the right times; the WAV stays. Good pass with the work file deleted: the WAV is deleted, `transcript.srt` stays.
- "MP4 only" FFmpeg commands on fragmented segments of 4 s: each file has its own audio part (found and fixed: without an end limit, segment 0 got all the audio).

Not checked:

- Anything on Windows: the `csc.exe` build, the WASAPI capture, the Settings check box, the start balloon, NVENC clip encode with audio, `Recorder.AddAudioToMp4Files`, the GitHub CI with the new files.
- A real Whisper program, its arguments and its time. The argument names come from the documentation as the agent knows it.
- The size of the `ClockOffset` error, and drift on long recordings.
- Bran's Twitch recording with the new rules (the file is not in the cloud).
- The clipboard risk.
- Known limit, not changed: a static column with text (sidebar) can make the scroll measure miss steps, with the old and the new rule (`PixelError` uses the full width). See note 10.2.

Next steps for Bran: build with the commands in note section 9, run the unit tests, then tests 1 to 7 of note section 9, the paste test of note section 10.3, and the pass with `test\process_clip.cs` on the `clip.mp4` of the Twitch recording (the work file is deleted after a good pass; the clip is lossy, so expect extra frames from encoder noise, see 7.5).

**State of Bran's PC at the end of the cloud session (2026-11-20, checked from Bran's PowerShell screenshots):**

- The running recorder is the old 2.1.0 app (`C:\Users\Bran\Desktop\tools\screen-vibe-recorder-2.1.0-windows-x64\DesktopRecorder.exe`). It has no "Folder for AI" output, so Bran's recordings were MP4 only, although the settings file has `output_mode=folder`.
- The "start with Windows" entry (`HKCU\...\Run`, name `DesktopRecorder`) points at the 2.1.0 app again, not at `DesktopRecorder-ai.exe` (7.6). Cause not checked; probably a Save in the Settings of the 2.1.0 app with "start with Windows" on.
- Not found yet: the path of the repository folder and of `DesktopRecorder-ai.exe` on Bran's PC.
- Next steps: find the folder (`Get-ChildItem -Path $env:USERPROFILE -Filter DesktopRecorder-ai.exe -Recurse -ErrorAction SilentlyContinue`), exit the 2.1.0 app from the tray, `git fetch origin ai-capture`, `git checkout ai-capture`, `git pull origin ai-capture`, build `DesktopRecorder-ai.exe` (command in `docs/audio-transcript.md` section 9), start it, then Save in its Settings with "start with Windows" on. Then do the tests of 7.7.
- The draft pull request https://github.com/gecko-of-shadow/desktop-recorder/pull/4 (ai-capture into main, do not merge) exists for the Windows CI build. CI passed on a27b22f.

### 7.8 Install of the new build on Bran's PC (2026-11-20, 00:20)

This section replaces the "State of Bran's PC" list in 7.7.

- Found at the start: no recorder process. The Run entry pointed at the old `DesktopRecorder-ai.exe` in the repository folder (build of 2026-11-19, without the audio code).
- Checked: `csc.exe` builds the app from a4fc8c6 (first Windows build of the audio code). One warning (CS0649), no error.
- Unit tests on Windows: 1 failure, "audio: openai-style on PATH". Cause: the test gave a ":" list with a Windows folder (drive colon) to the non-Windows rules. It is a test fault, not an app fault. `tests/AudioTests.cs` now uses the Windows rules for that case on Windows. Result after the change: `ok`. The change is not committed.
- Installed: `%LOCALAPPDATA%\Programs\DesktopRecorder\DesktopRecorder.exe` (SHA-256 starts with 59773B28) and `README.txt`. The app no longer runs from the repository folder, so a build does not hit a locked file.
- Checked: the Run entry (`HKCU` Run, name `DesktopRecorder`) and the Start Menu shortcut "Screen Vibe Recorder" point at the installed exe. The process runs from that path.
- Checked: one recording with synthetic keys (hotkey, Space, 6 s, hotkey, Enter). Folder mode gave `clip.mp4`, `index.md` and 4 PNG frames. FFmpeg ended after Stop.
- Not checked: audio and transcript on Windows (`record_audio` is off), the Settings window, the tray icons, a restart of Windows.
- A Save in the Settings of an old exe with "start with Windows" on writes the path of that old exe into the Run entry.

### 7.9 Clipboard holds files, not the folder (2026-11-20, 00:30)

- Fault (checked, Bran's screenshot): Bran pasted a recording into the Claude desktop chat. The clipboard held the folder, and the chat made a file of 0 bytes with the folder name. This is the risk of 7.7 and of `docs/audio-transcript.md` section 10.3.
- Decision (Bran): in folder mode, the clipboard holds `index.md` and the PNG frames as files, plus the AI text message. No `clip.mp4`, because Claude chat refuses video. No setting.
- Code: `RecordingIndex.ClipboardFiles` in `SettledFrames.cs`. When the folder has no `index.md` and no frames (failed pass), the clipboard holds the folder as before. Unit test `TestClipboardFiles`. Tests: `ok`.
- Checked on Bran's PC with the installed build (SHA-256 starts with 0EFE3BDD): after a test recording, the file drop list had `index.md`, `001.png` and `002.png`, and the text had the folder path.
- Not checked: a real Ctrl+V in the Claude chat app, claude.ai, Gemini and Explorer. Claude chat takes 20 files in one message; a recording with more than 19 frames is over that limit.
- Committed in 8e6fa73.
- Checked by Bran at 00:32 (screenshot): Ctrl+V in the Claude desktop chat attached `index.md` and 4 frames, and the chat read them.
- Checked at 00:32: Bran set `record_audio=1`. The recording `desktop_2026-11-20_00-31-39` has an AAC audio stream of 12.48 s in `clip.mp4` (video 12.40 s). This is the first WASAPI capture on Windows. `index.md` says "No transcript: no Whisper program was found". Not checked: that the audio is audible and in step with the video; a transcript with a real Whisper program.

### 7.10 Whisper on Bran's PC and the instruction in index.md (2026-11-20, 00:41)

- Installed (Bran approved the downloads): whisper.cpp build b5454, `whisper-bin-win-cuda-12.4.0-x64.zip` from GitHub `ggml-org/whisper.cpp`, and the model `ggml-large-v3-turbo-q5_0.bin` from Hugging Face `ggerganov/whisper.cpp`. Both SHA-256 values were equal to the values that the sites give. Place: `%LOCALAPPDATA%\Programs\DesktopRecorder\whisper` (program) and `whisper\models` (model). The app finds them without a setting.
- Checked by hand: `whisper-cli.exe` made a correct transcript of 25 s of YouTube audio in 2.7 s on the RTX 4060.
- Checked in the app: a recording of a spoken test sentence (Windows speech) gave `transcript.srt` and the section "Audio and transcript" in `index.md` with all words correct. The whole sentence is one row (0.0 s to 10.8 s).
- New: `index.md` has an instruction for the AI in line 2 (`RecordingIndex.AiNote`). Reason: a chat app takes the files from the clipboard and not the text message, so the AI got no instruction. Unit test updated. Tests: `ok`.
- Not checked: the time from Stop to "ready" with a transcript on a long recording; the sync of audio and video; the effect of the instruction on Claude chat, Gemini and Grok.
- Committed in bcee269.

### 7.11 Release v3.0.0

Done on 2026-11-20. https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v3.0.0

- Bran: "merge it into main and release it". The version is 3.0.0 (agent's choice) because the default output is now a folder, not one MP4 file.
- Content: all of `ai-capture` (7.5 to 7.10). Version 3.0.0 in the manifest, the README and the two archive README files. CHANGELOG section 3.0.0.
- Before the merge: unit tests `ok` and the app build on Bran's PC. The Build workflow passed on 05de6bf (pull request 4: build and linux).
- `main` fast-forwarded from 133fbe8 to 05de6bf (no merge commit). Pull request 4 shows as merged.
- Tag v3.0.0 = 05de6bf (annotated). The Release workflow passed in full (windows, linux, publish).
- Checked: the release is Latest and not a draft. The release body equals the tag message. Both archives downloaded. The zip has `DesktopRecorder.exe` and a README that shows 3.0.0. The Linux README shows 3.0.0.
- Bran's PC runs a local build of 05de6bf (SHA-256 starts with 54806862) in `%LOCALAPPDATA%\Programs\DesktopRecorder`, not the exe from the zip.
- Not checked: the exe from the release zip on a PC; the v3.0.0 zip on a second PC; the points in 7.9 and 7.10 that are not checked.
- The branch `ai-capture` still exists.
- 7.12 changed the release body. It no longer equals the tag message.

### 7.12 README, CHANGELOG and release notes after v3.0.0 (2026-11-20)

- Bran: "make sure the readme and release info reflects everything that's new with the app".
- Method: a review workflow compared README.md, CHANGELOG [3.0.0], `packaging/windows/README.txt` and the release body with the v3.0.0 code and HANDOFF 7.5 to 7.11. A second agent tried to refute each finding. All 29 findings held, some with narrower wording.
- README: removed the "branch `ai-capture`, not in a release, not tested on Windows" text. Added: the AI instruction in `index.md`, the folder-mode status line, the note box only in folder mode and `note.txt` after a failed pass, the clipboard setting covers files and message, the tested paste (Claude desktop chat only) and the 20-file risk, Exit waits for the last pass, ZIP place and contents, Quality and Color detail apply only to `clip.mp4` in folder mode, no split in folder mode, the Whisper search order, subfolders, model order and time limit, WAV files that can stay, and a 3.0.0 test summary.
- CHANGELOG [3.0.0]: no split and lossless frames in folder mode, Exit waits, the 2% scroll rule, only whisper.cpp tested, a longer "Not tested" list.
- Release body: edited with `gh release edit v3.0.0 --notes-file`. Tag and assets not changed. The tag message still has the old text.
- Not changed: `packaging/windows/README.txt` (inside the released zip), the test switch `DESKTOP_RECORDER_KEEP_WORK` (left out of the README on purpose).
- Not checked: the 20-file limit of the Claude chat (from 7.9, not tested).

### 7.13 New defaults: no recording at start, pointer hidden (2026-11-20)

- Bran: "turn off record on launch and hide the pointer".
- Reasons: `record_on_launch=1` came from the v1 design (98136d2), when the app always recorded the full desktop. With no saved area it recorded the full desktop with no question, also at login. All folder-mode tests (7.6) ran with the pointer hidden. A moving pointer keeps the screen from settling.
- Code: `RecordOnLaunch = false` and `ShowPointer = false` in `StoredSettings` (`RecorderCore.cs`) and in the app settings class (`DesktopRecorder.cs`). The test helper `Base()` sets `ShowPointer = true`, so the argument tests still check `draw_mouse 1` first. New test "explicit record on launch".
- An existing `settings.txt` keeps its values. Bran's file already had `record_on_launch=0` and `show_pointer=0`.
- Checked on Bran's PC: unit tests `ok`. The app builds with `csc.exe` (CS0649 warnings only).
- Not checked: the first start of the new build with no settings file, and its Settings window. Not released. The Linux port is not changed.

### 7.14 The two warnings in index.md (2026-11-20)

- Bran: "Yes, fix both" (the file-count warning and the size warning). This replaces the values of condition G (7.5).
- File count: the clipboard holds `index.md` and every frame (7.9). The warning now fires when frames + 1 > 20 (`RecordingIndex.FileWarningLimit`). Before, it fired only above 20 frames, so a 20-frame recording (21 files, as in the 7.6 settings tour) got no warning.
- Size: `DefaultSizeLimit` 1568 -> 2576 px on the long side, plus a warning above 3.75 million pixels (`DefaultPixelLimit`). Source (checked in this session): the Claude API skill docs, `shared/model-migration.md`. Opus 4.7 and later, Sonnet 5 and later, and Fable read images up to 2576 px and 3.75 MP. Opus 4.6, Sonnet 4.6 and older scale above 1568 px. A 1920x1080 frame now gets no warning.
- Every 3.0.0 settings file has `size_limit=1568` (7.6), so a load reads 1568 as 2576 (`OldDefaultSizeLimit`). A user who wants 1568 cannot set exactly that value; 1567 or 1569 works.
- Tests: new checks for 19, 20 and 21 frames, 1920x1080, 3840x1080, 2560x1600 and 2560x1440, and for the 1568 load. Unit tests `ok` on Bran's PC. The app builds.
- Not checked: the image limits of other chat apps (Gemini, ChatGPT, Grok); the 20-file limit of the Claude chat (7.9). Released in v3.0.1 (7.15).

### 7.15 Release v3.0.1

Done on 2026-11-20. https://github.com/gecko-of-shadow/desktop-recorder/releases/tag/v3.0.1

- Bran: "release it as 3.0.1".
- Content: 7.13 (no recording at start, pointer hidden) and 7.14 (the two index.md warnings). Version 3.0.1 in the manifest, the README and the two archive README files. CHANGELOG section 3.0.1.
- Before the tag: unit tests `ok` and the app build on Bran's PC. The Build workflow passed on `main`.
- Tag v3.0.1 = ffcb4fa (annotated, `--cleanup=verbatim`). The Release workflow passed in full (linux, windows, publish).
- Checked: the release is Latest and not a draft. The release body equals the tag message. Both archives downloaded. The zip has `DesktopRecorder.exe` (manifest version 3.0.1.0, SHA-256 starts with cd5202653090b3aa, has the new warning texts) and a README that shows 3.0.1. The Linux README shows 3.0.1.
- Not checked: the exe from the zip on a PC. Bran's PC still runs the local build of 05de6bf (3.0.0) in `%LOCALAPPDATA%\Programs\DesktopRecorder`.

Install on Bran's PC (2026-11-20, 05:41, Bran: "Yes, install it"):

- The app did not run before the install. The old exe and README are kept as `DesktopRecorder-3.0.0-backup.exe` and `README-3.0.0-backup.txt` in the same folder.
- Installed the exe and README from the release zip into `%LOCALAPPDATA%\Programs\DesktopRecorder`. SHA-256 starts with CD5202653090B3AA (same as the release zip).
- Checked: the app starts from that path and responds. No FFmpeg process started (no recording at start). The Run entry points at that exe. The settings file keeps `record_on_launch=0`, `show_pointer=0`, `output_mode=folder` and `size_limit=1568` (read as 2576).
- Not checked: a recording with the 3.0.1 exe, the new warnings in a real `index.md`, the Settings window.

### 7.16 ZIP on the clipboard above a file count (2026-11-20, not released)

- Fault (Bran): a paste into a new Claude cloud session in the Claude desktop app gave a "file limit" error. Recordings: 12 files (05:42) and 8 files (05:43). The desktop chat took 5 files (7.9). The help center gives 20 files for a normal chat. The cloud session limit is not documented. Cause: probably the file count. Not proven.
- Bran: "Build a fix now".
- Code: setting `clipboard_max_files` (default 20, range 1 to 1000) and a Settings row "Folder mode: copy one ZIP above this many files (1 = always)". `RecordingIndex.ClipboardUsesZip`: a ZIP when the files are more than the value, or always when the value is 1. The ZIP is made on a background thread with `AfterRecording.MakeZip` (same ZIP as the tray item; it has `clip.mp4`). Exit waits for it. A note saved after the pass makes the ZIP again, so the ZIP has the note. One lock keeps two ZIP calls apart.
- Checked on Bran's PC: unit tests `ok`, the app builds. Test build installed (SHA-256 starts with 7085A9DD4E06E502). The 3.0.1 exe is kept as `DesktopRecorder-3.0.1-backup.exe`. A synthetic test recording (Space, 5 s) with `clipboard_max_files=1`: the clipboard held `desktop_2026-11-20_14-25-32.zip` (5.9 MB: `clip.mp4`, `index.md`, `transcript.srt`, 3 frames) and the AI message. The app wrote the key back to `settings.txt`.
- Bran's settings file now has `clipboard_max_files=1` (set by the agent for the test).
- Not checked: that a Claude cloud session accepts the ZIP (Bran must paste it); a paste of 2 files into a cloud session (shows if a value from 2 to 7 has a use); the Settings row at 125%; the ZIP with a note typed after the pass; the README text on GitHub.
- Do not release before the cloud paste test.

### 7.17 Release cleanup on GitHub (2026-11-20)

- Bran: "update anything out of date on readmes and releases on github. remove all but the recent stable releases for each OS/install type". Bran approved the list: delete the releases, keep the tags.
- Deleted (with their zip and tar.gz assets): the `desktop-recorder` releases v3.0.0, v2.0.2, v2.0.1, v2.0.0, v1.0.0. Kept: v3.0.1 (Latest, Windows zip and Linux tar.gz) and all 6 tags. `screen-vibe-recorder` v2.1.0 is the only release of that repository and is kept.
- `desktop-recorder` `main` e126ff9 (pushed): the README says that only the latest release stays and that earlier versions are tags, adds a 3.0.1 test line, and corrects the `screen-vibe-recorder` facts. CHANGELOG link 1.0.0 now points at the tag.
- v3.0.1 release body: the test line says that the zip exe started on Bran's PC, and a line names the tags. The release body no longer equals the tag message.
- `screen-vibe-recorder` `main` 9ffb28d (pushed): README and CHANGELOG say that Desktop Recorder has versions up to 3.0.1.
- Checked: `gh release list` shows v3.0.1 only; `releases/latest` = v3.0.1; the tags exist; the tag and compare links give HTTP 200.
- Commit 50f880c (7.16) was rebased on top of e126ff9 and is still not pushed, so the GitHub README does not name `clipboard_max_files`. This section is in the same local-only state until the next push.
- Not checked: the open pull request 1 of `screen-vibe-recorder` (`docs/cleanup-2026-11-16`) is not changed and can now conflict.

Reinstall on Bran's PC (2026-11-21, Bran: "uninstall it from my PC and then install"):

- Removed (Bran approved): the 7.16 test build (SHA-256 7085A9DD4E06E502), `README.txt`, the backups `DesktopRecorder-3.0.0-backup.exe`, `DesktopRecorder-3.0.1-backup.exe` and `README-3.0.0-backup.txt`, the Run entry and the Start Menu shortcut "Screen Vibe Recorder". Kept: the `whisper` folder and the settings files.
- Installed from the v3.0.1 release zip: `DesktopRecorder.exe` (SHA-256 starts with CD5202653090B3AA, the same as 7.15) and `README.txt` in `%LOCALAPPDATA%\Programs\DesktopRecorder`. New Run entry and Start Menu shortcut point at that exe.
- Checked: the app runs from that path and responds. No FFmpeg process started.
- `settings.txt` still has `clipboard_max_files=1` from the 7.16 test. 3.0.1 does not use that key.
- Not checked: a recording with this exe; a restart of Windows.

### 7.18 One ZIP only in folder mode (2026-11-22, branch `zip-only`, not pushed, not released)

- Bran decided on 2026-11-22: in folder mode, the output of a recording is one ZIP file. This replaces 7.16 (`clipboard_max_files`) and the file drop of 7.9.
- Flow after the frame pass (`OnPassDone` in `DesktopRecorder.cs`): the app always makes the ZIP (`AfterRecording.MakeZip`, on a background thread). The ZIP is next to where the folder was, with the name `<folder>.zip`. The clipboard holds the ZIP as a file drop and the AI text message. It never holds the loose files. Then the app deletes the recording folder.
- Pass failed: the folder stays (it holds the lossless work file). The app still makes and copies the ZIP. The ZIP has `clip.mp4` and `note.txt`, not the work file.
- Note box: a note that Bran saves after the ZIP is made goes into `index.md` (or `note.txt`), and the app makes the ZIP again at the same path. The folder is deleted only when the note box is closed (saved or closed) and no ZIP is waiting. The rule is `RecordingIndex.CanDeleteFolder` (unit tested). Job state: `PassJob.Zip`, `ZipError`, `ZipPending`, `NoteOpen`.
- The folder is not deleted when a ZIP call failed. The balloon says so. When the delete itself fails, the ZIP stays and the balloon names the error.
- Exit waits for the ZIP and the delete (`passesRunning`, `ExitIfDone`).
- `clipboard_max_files` is removed: code, Settings row (the rows below moved up by 26 px, the dialog is 26 px shorter), tests, README. `RecorderCore.cs` reads the old key and drops it, so an old `settings.txt` loads with no error. The next Save does not write it. Bran's `settings.txt` still has `clipboard_max_files=1` until he saves. `RecordingIndex.ClipboardFiles` and `ClipboardUsesZip` are removed.
- Tray: "Show last ZIP in Explorer" (`explorer.exe /select,"path"`) replaces "Open last recording folder". "Copy ZIP of last recording" copies the existing ZIP and does not make it again. Both are off when the ZIP file is not there.
- `RecordingIndex.AiMessage` and `AiNote` describe a ZIP. The message now has the path of the ZIP and the line "Unpack the ZIP first if you cannot read inside it."
- `MakeZip`: PNG and MP4 entries use `CompressionLevel.NoCompression`. Other files use `Optimal`. The format is the same ZIP. Note: .NET Framework writes a "stored" entry as a Deflate stream of uncompressed blocks (method 8, size + 5 bytes), not as ZIP method 0. Readers open it as usual.
- "MP4 only" mode is not changed.
- The unit-test build and `run-tests.sh` now include `AfterRecording.cs` and the two compression references, because the tests call `MakeZip`.

Checked on Bran's PC (2026-11-22):

- The app builds with the `csc.exe` command of `build.cmd` (`/out:DesktopRecorder-zip.exe`; CS0649 warnings only). The running app was not stopped. The build was not installed.
- Unit tests: `ok`. New tests: `TestZipOnly` (the cleanup rule for 7 cases, the ZIP texts, `MakeZip` entries, compression and the same path on a second call) and the old-key checks in `TestSettingsKeys`.
- `test\process_clip.cs` on a synthetic lossless work file (3 still scenes, NVENC, 640x480, 15 fps) with `NOTE_ZIP=1`: `ok=True`, 3 frames, 3 keyframes. `System.IO.Compression` read the ZIP: `desktop_test/clip.mp4`, `index.md`, `frames/001.png` to `003.png`, no work file. Python `zipfile` read it too (`testzip` found no error). The PNG and MP4 entries are 5 bytes larger than the files (stored blocks). `index.md` is 1003 bytes, 520 compressed.

Not checked:

- A real recording with this build. The flow in `DesktopRecorder.cs` (ZIP, clipboard, delete of the folder, the note box, Exit) has no automatic test. Only `CanDeleteFolder` has a unit test.
- The note box with a note saved after the ZIP (second ZIP, then delete), a note saved while the first ZIP is made, a closed box with no note, and Exit while the note box is open. In the last case the ZIP exists and the folder stays (the cleanup waits for the box).
- A pass that failed in the app, and the ZIP of that case.
- A real paste of the ZIP into the Claude desktop chat, a Claude cloud session, claude.ai, Gemini or Explorer. This was the reason for the change. It is still unknown if a cloud session takes one ZIP.
- The Settings window at 125% (rows moved), the tray items and the balloons.
- The Mono build and the GitHub CI with `AfterRecording.cs` in `run-tests.sh` (compiles under `csc.exe` only; the whole app compiled under Mono in 7.7).
- The file-count warning in `index.md` (more than 20 files) is not changed. It now describes the files after the ZIP is unpacked. Bran can decide if it is still useful.
- `DesktopRecorder-zip.exe` is in the repository folder (not committed). Test files from the check are in `%TEMP%\zip-only-test` (not deleted).

### 7.19 False transcript rows, pointer motion warning, file-count text (2026-11-22, branch `zip-only`, not pushed, not released)

Job from Bran (headless agent run): job A (false transcript rows), job B (false "the picture changed during 81%" warning), job C (file-count warning for a ZIP).

Test data: three real recordings by Bran, copied to `%TEMP%\svr-job-7-19` (the originals in `C:\Users\Bran\Recordings` are not changed):

- r1 = `desktop_2026-11-22_14-16-55` (57.3 s, Claude Console in Chrome).
- r2 = `desktop_2026-11-22_14-29-27` (18.1 s, PowerShell and the Claude app).
- r3 = `desktop_2026-11-22_14-45-44` (126.9 s, Claude Console in Chrome, quiet speech from a video in the audio).
- Extra (after the review): r4 = `desktop_2026-11-22_14-24-54` (15.0 s, Claude Code in a terminal, a "Thank you." row and "92%") and r5 = `desktop_2026-11-22_15-05-23` (34.3 s, Claude Console, real speech and "89%").

The lossless work files and the original WAV files are gone. All pass runs used the lossy `clip.mp4` and its AAC audio.

**Job A: false transcript rows.**

Facts (checked):

- The audio of r1 and r2 is digital silence, except the stop sound of the app in the last 0.5 s and a few clicks in r2 (10.5 s, 15.5 to 16.5 s). whisper-cli with the app arguments wrote "Thank you." from 0 to 30 s and from 30 to 60 s (r1), and from 0 to 30 s (r2, an 18.1 s recording).
- r3 has real speech at about -60 dBFS RMS (0.5 s windows) and clicks at -14 dBFS peak. Whisper wrote a coherent conversation (29 rows in the app, 50 rows in a new run). r3 has no "Thank you." row: only r1 and r2 show the fault.
- The peak check of 3.0.0 (`AudioTake.SilentPeak`) did not stop Whisper: the stop sound has a peak of -21 dBFS.
- whisper-cli options (the build in `%LOCALAPPDATA%\Programs\DesktopRecorder\whisper`; `--help` lists VAD options): `-sns`, `-lpt 0`, `-nth 0.3 -lpt 0` and `-nth 0.1 -lpt 0` kept the "Thank you." rows on r1 and r2.
- Silero VAD: model `ggml-silero-v6.2.0.bin` from Hugging Face `ggml-org/whisper-vad` (SHA-256 equal to the value of the site), in the scratch folder only, not installed. `--vad` at threshold 0.5 removed the false rows, but r3 kept only 6 of about 30 real rows. At 0.2 and 0.05, r3 kept 23 and 24 rows, but r1 and r2 got "Thank you." and "Bye." again, on the stop sound and on the clicks.

Fix: `SpeechCheck` in `AudioTranscript.cs`, called in `AfterRecording.MakeTranscriptCore`.

- It reads the 16 kHz copy (`transcript_16k.wav`, on the time line of the rows) in parts and computes the RMS level of each 100 ms window. Sound = -90 dBFS or louder (an RMS of 1 LSB in 16-bit audio). The first version used -70 dBFS; see the volume check below.
- Near silence: sound in less than 10% of the row and for less than 1.0 s (window count). Such a row is left out, with any text.
- A known Whisper text for silence (for example "Thank you.", "Thanks for watching!", "Bye.", "you"; compared without case and punctuation) in a long row (10 s or more, or 80% of the audio or more) needs sound in 50% of its time. A short row with a known text is not checked by this rule.
- Windows after the end of the audio count as silent (Whisper pads the last 30 s window). A file that is not 16-bit PCM, or with less than 50 ms of audio, is not checked: all rows stay.
- `index.md` says: "The app left out N rows that Whisper wrote: the audio was silent or almost silent in that time. Whisper writes text such as "Thank you." on silence." When no row is left, the section starts with "No speech was found.", and the folder (thus the ZIP) has no `transcript.srt`. The tray status line then says "No transcript: no speech was found." (before: "No transcript: ." for an empty transcript).

Results (checked; `test\process_clip.cs` with `AUDIO_FILE` = the audio of each clip as a 48 kHz float WAV, and the installed whisper-cli and model):

| Recording | Rows from Whisper | Kept | Left out | Sound of the left-out rows |
|---|---|---|---|---|
| r1 | 2 ("Thank you.", 0 to 30 s and 30 to 60 s) | 0 | 2 | share 0.000 and 0.010 (0 s and 0.3 s) |
| r2 | 1 ("Thank you.", 0 to 30 s) | 0 | 1 | share 0.030 (0.9 s) |
| r3 | 50 (real, quiet) | 50 | 0 | - (each row: share 0.5 or more) |
| r4 | 1 ("Thank you.") | 0 | 1 | not printed |
| r5 | 3 (real) | 3 | 0 | - |

The 29 rows of the original r3 transcript (made by the app) also all stay (probe tool on the r3 audio).

Volume check (checked, after the advisor review): the r3 audio 10, 20 and 30 dB quieter (FFmpeg `volume`), full pass with Whisper. Whisper still wrote 35, 11 and 18 rows. The first floor (-70 dBFS) dropped 6, 7 and 17 of them. With -90 dBFS: 0 of 35 and 0 of 11 dropped (30 dB quieter: 6 of 18, speech at about -90 dBFS, near the 16-bit floor). The false rows of r1, r2 and r4 stay out at every floor from -70 to -95 dBFS (digital silence). Final code: r1 2 out, r2 1 out, r4 1 out, r3 50 of 50, r5 3 of 3. Unit test: `TestSpeechCheck` and new checks in `TestIndexSection` (`tests/AudioTests.cs`).

**Job B: false motion warning.**

Cause (checked):

- Bran's `settings.txt` has `show_pointer=1`. The pointer is in the video: a crop of r1 from 0.47 to 0.6 s shows the hand pointer at three places, and a menu item with a hover color.
- In "changes" mode each pointer move keeps a frame (15 fps). The old rule counted each frame pair with any change (3 px in one 8 x 8 block) that was not a scroll as motion.
- On the lossy clips, the old rule counted 628, 201 and 1538 motion pairs. In most of these pairs, the largest group of changed blocks (blocks that touch) had a short side of 16 to 32 px. Only 23, 46 and 84 pairs had a group with a short side of 96 px or more (1.5 s, 3.6 s and 6.2 s).
- Encoder noise is not the cause: the app ran the pass on the lossless work file and wrote 81%, 81% and 86%.

Fix: `SettledOptions.MotionMinSide` (96) and `SettledFrameFinder.LargeGroups` in `SettledFrames.cs`.

- A frame pair counts as moving content only when one group of changed 8 x 8 blocks has a short side of 96 px or more, or when all changed blocks together cover 96 x 96 px or more (144 blocks; many small moving parts, for example a dark video; added after the review). When the region is small, the limit is half the short side of the region (the unit tests use 96 x 120 frames: 48 px).
- The area in the warning is now the box of the large groups (or of all groups, when only the total is large), not of all changes (before: the pointer path made it the full screen).
- The frame rules are not changed. A moving pointer still keeps the screen from settling, so the rows can still say "screen still changing" (the 5 s samples).

Results (checked; HEAD and the final code on the same clips):

| Recording | App, lossless (before) | HEAD, lossy clip | Final, lossy clip | Frames HEAD / final |
|---|---|---|---|---|
| r1 | 81%, 21 frames | 84% | 11.7% (no warning) | 20 / 20 |
| r2 | 81%, 7 frames | 88% | 25.5% (no warning) | 6 / 6 |
| r3 | 86%, 37 frames | 88% | 10.7% (no warning) | 36 / 36 |
| r4 | 92%, 5 frames | 94% | 20.1% (no warning) | 5 / 5 |
| r5 | 89%, 10 frames | 90% | 9.6% (no warning) | 9 / 9 |

- The PNG files of HEAD and final are equal (MD5) in all five. `index.md` of r1 to r3 differs only in the motion warning (removed) and the file-count text (job C).
- The final values are the moving seconds / length (for example 6.69 / 57.3 s). Before the total-area path they were 2.7%, 19.9% and 4.9% (r1 to r3). The extra pairs are probably hover effects, page changes and encoder noise of the lossy clip (not checked one by one).
- Analysis time (first version of the fix, no debug output): HEAD / new 4.38 / 4.40 s, 1.82 / 1.89 s, 12.31 / 11.24 s. Not measured again after the total-area path (one counter more in the same loop).

Regression checks (checked; `test\settled_render.cs`, lossless NVENC 4:4:4 at 15 fps, "fixed" and "changes" gave the same results):

- 7.5 tests: status line 6 frames (0, 8.0, 18.0, 29.5, 41.0, 52.3 s), blinking caret 1 frame, look-alike 15 of 15, scroll 0 px missed (10 frames), pages 0 px missed (11 frames). Same as 7.5.
- New scenario `pointer` (26 s, the pointer moves with 3 stops of 1 s, status change at 12.8 s): no motion note, 9 frames, a settled frame at 12.8 s.
- New scenario `player` (480 x 270 video area): motion note 97% (86% before the total-area path), area x 400, y 296, 488 x 280 px. The 8 px extra width probably comes from 3 extra bytes in the pipe of the renderer (FFmpeg: "Packet too small (3)" in every render, also before this job). Not checked, not changed.
- 7.7 tests (FFmpeg lavfi, 26 s, NVENC lossless): motion note for `testsrc2` 320 x 240 and 1280 x 720, `mandelbrot`, `life` 320 x 240 and 640 x 480 (all 100% with the final code). No note for `cellauto` (a scroll). Same as 7.7.
- Unit tests `TestPointerIsNotMotion` (96 x 120 and 640 x 360 frames) and `TestManySmallPartsAreMotion` (60 moving parts of 8 x 8 px on a dark 640 x 360 picture). Mutation checks: with `MotionMinSide = 8`, the 3 pointer checks fail (pointer 9.4 s and 9.1 s of motion; the video area included the pointer path). Without the total-area path, the many-parts check fails (0.20 s of motion).

**Independent review (a code-reviewer agent, before the commit).** No critical fault. Confirmed: the WAV parser (stereo, odd chunk sizes, data size 0, 0xFFFFFFFF and too large, reads of 1 to 3 bytes), the window index, the callers, `LargeGroups` order and box, no frame-rule change, C# 5 syntax. Findings and changes:

1. A long row with a short real sentence (2.9 s of speech at the end of 0 to 30 s) was dropped by the share rule alone. Changed: near silence needs a share below 10% and less than 1.0 s of sound.
2. A short real "Thanks." (0.4 s) was dropped by the 0.5 s minimum for known texts. That minimum only guarded a pattern seen with VAD ("Bye." on the stop sound), and the app does not use VAD. Changed: no minimum; the known-text rule applies only to long rows (Whisper writes such text over its full 30 s window on silence).
3. Real video with only small changed groups could lose the motion note. Changed: the total-area path (see above). No real video recording was on this PC (the YouTube and Twitch files of 7.7 are not in `C:\Users\Bran\Recordings`).
4. Parser paths without tests. Added: reads of 1 to 3 bytes, data size 0, less than half a window.
5. An empty level array dropped all rows. Changed: no full window gives no check.

**Job C: file-count warning.** New text: "Warning: the ZIP has 21 frame images. With index.md, that is 22 files. A chat app that unpacks the ZIP can limit the number of images that it reads. Some chat apps accept only 20 files in one message." The limit stays: frames + 1 > 20. `TestIndex` checks 19, 20 and 21 frames.

Decisions made without Bran:

1. No VAD and no new whisper-cli option (data above).
2. The limits -90 dBFS, 10% and 1.0 s, 50% for a known text in a row of 10 s or 80% of the audio, and the phrase list. The phrase list has only texts from arXiv 2501.11378, community reports and this PC. The texts in Japanese, Russian and German come from community reports (not checked).
3. No `transcript.srt` when no row is left (before: an empty file).
4. `MotionMinSide` = 96 px: about 2.5 times the largest pointer group at 125% (16 to 40 px). The total-area path uses the same value (96 x 96 px).
5. The frame rules for a moving pointer are not changed (see above).

Not checked:

- A real recording with this build. The job said not to stop the running app. The pass ran offline with `test\process_clip.cs`, which calls the same `AfterRecording.Run`.
- The lossless work files and the original WAV files of the three recordings (gone).
- Real speech with long digital silence inside one row (for example Windows speech, HANDOFF 7.10: one row of 10.8 s). The 10% and 1.0 s limits are for such rows.
- Known limits: a real utterance of less than 1 s in a row more than 10 times longer, with digital silence around it, is left out. A real known text (for example "Thank you.") in a long row with less than 50% sound is left out. A false "Thank you." on steady faint noise (no silence) stays.
- Higher DPI or a larger pointer size. A small video area (short side under 96 px, and less than 96 x 96 px of changed blocks) no longer gives the motion note.
- A real video recording (no such file on the PC). The motion rule was checked with synthetic video only.
- The Mono build and the GitHub CI (no Mono on this PC).
- Seen, not changed: the stop sound of the app is in the audio of each recording when `play_sounds=1` (the last 0.5 s of r1 and r2).
- Files: `DesktopRecorder-zip.exe` (rebuilt with the final code, SHA-256 starts with 0B77F8654024C9AD, not committed) in the repository folder. Scratch files in `%TEMP%\svr-job-7-19` (copies of the recordings, test videos, the VAD model, logs; not deleted).

Next steps for Bran:

1. Pointer: `settings.txt` has `show_pointer=1` now (7.15 recorded 0, and decision 9 says hidden by default). The false warning is gone, but a moving pointer still keeps the screen from settling, so rows can still say "screen still changing" and changes during pointer moves get a frame only at the next stop or 5 s sample. Choose: turn "Hide the mouse pointer" on again, or ask for a frame-rule change that ignores the pointer.
2. Record about 20 s of a real video or stream with the test build and check that `index.md` has the motion note. The motion rule saw only synthetic video.
3. Make one real recording with silent audio and one with speech, and check the transcript section.
4. Decide about push, merge and release of `zip-only` (7.18 and 7.19).

### 7.20 UI redesign: note box, Settings window, tray menu (2026-11-22, branch `zip-only`, not pushed, not released)

Job from Bran (headless agent run). Bran's change at 18:27: code only, no live test, because Bran used the PC. The agent did not start the app, did not stop the installed app, sent no input, took no screenshot and showed no window.

**Files.** New: `UiModel.cs` (rules, no WinForms, unit tested), `UiTheme.cs` (colors, fonts, DWM calls, custom controls), `NoteForm.cs`, `SettingsForm.cs`, `TrayMenu.cs`, `tests/UiTests.cs`, `test/ui_preview.cs` (offscreen preview tool). Changed: `DesktopRecorder.cs` (the old `NoteForm` and `SettingsForm` classes are removed; `TrayApp` uses the new parts), `tests/CoreTests.cs`, the build lists (`build.cmd`, `run-tests.sh`, `.github/workflows/build.yml`, `release.yml`, README build and test commands), `README.md`, `CHANGELOG.md`. The recording, the capture, the frame pass and the settings file format are not changed.

**Part 1: note box** (`NoteForm.cs`, rules in `NoteRules`).

- Opens in the middle of the work area of the monitor that has the largest part of the recording region (the primary monitor for a whole-desktop recording). A second open box moves 28 epx down and right.
- Content: title "Note for this recording", the line "The note goes into index.md in the ZIP. The AI reads it as context for this recording.", the recording name and length, a text box with several lines (2000 characters at most), a hint line, and the buttons **Save note** (default) and **No note**. The text box has the focus.
- Keys: Enter saves. Shift+Enter and Ctrl+Enter start a new line (the key handler inserts the line break itself). Ctrl+A selects all.
- Close rules (unit tested): an empty box closes at once for every trigger. With text, only **Save note** and a Windows shutdown (also Task Manager and `Application.Exit`) save without a question. Esc, the X button, Alt+F4, the taskbar "Close window", **No note** and Exit in the tray menu show "Save this note?" in the box with **Save**, **Discard** and **Back**. Back returns to the text. For Exit, Back also cancels the Exit (`QuitCancelled` clears `quitRequested`).
- The question is in the box, not in a second window (advisor review): no second window to keep out of the video, and no nested modal loop during Exit.
- `TrayApp.Quit` asks the open note boxes first. When a box asks, Quit returns; the answer closes the box, and its `FormClosed` calls Quit again (`quitLoop` stops a nested call). `ExitIfDone` also waits for `openNotes.Count == 0`. The MP4 stop path now calls `ExitIfDone()` (before: `ExitNow()` at once).
- On top (`TopMost`), in the taskbar, does not close when it loses the focus. When it opens without the foreground, `FlashWindowEx(FLASHW_ALL | FLASHW_TIMERNOFG)` flashes it until the user opens it.
- DPI: the box is per-monitor-v2 aware. `ShowNoteBox` sets the thread context to per-monitor-v2 (before: system aware), because the hotkey can come while the system-aware Settings window is open. The box lays itself out for the DPI of its monitor (`GetScaleForPoint`). It does not handle `WM_DPICHANGED` (mixed DPI is "not now", 7.1): a box dragged to a monitor with another scale keeps its pixel size.
- `NativeMethods.ExcludeFromCapture` stays on the box. The ZIP and folder rules of 7.18 are not changed: the folder is deleted only after the box is closed (`job.NoteOpen`, `TryCleanup`), and a note saved after the pass makes the ZIP again.
- `index.md` writes the note on one line (`RecordingIndex.Cell` turns line breaks into spaces; `SettledFrames.cs` is out of scope). `note.txt` keeps the lines. The hint tells this.

**Part 2: Settings window** (`SettingsForm.cs`, texts in `SettingsCatalog`).

- Left navigation with five pages: Recording; Capture and quality; Hotkey and selector; AI and audio; Startup and sounds. Reason: Microsoft recommends left navigation for 5 to 10 equal categories and top navigation (tabs) for 5 or fewer; this window has 5, so both are possible. The left list won because it is the pattern of the Windows 11 Settings app, and a WinForms `TabControl` cannot be drawn in that style (its tab row and border stay classic).
- Each setting is a card: title, help text with the default, control at the right (or under the text for the folder and the hotkey). Switches (`ToggleSwitch`, a `CheckBox` subclass) replace the check boxes; the hotkey keeps check boxes for Win, Ctrl, Alt and Shift.
- "Start a new file every" is off when Output is "Folder for AI" (folder mode never splits). The AI and audio page shows the result of `Transcript.Find` and names the settings that are in `settings.txt` only.
- A problem (hotkey, folder) shows in red at the bottom; the window opens the page and focuses the field. No message box.
- **Restore defaults**: puts the values of `new Settings()` into the fields, and "Start with Windows" off. Save applies them. The fields that the window does not show (regions, `nvenc_cq`, `settle_ms`, Whisper keys, Linux lines) stay.
- Ctrl+Tab and Ctrl+Page Down: next page. Ctrl+Shift+Tab and Ctrl+Page Up: page before.
- System DPI aware, as before (`ShowSettings`); the scale is the DPI of the screen DC in that context. `AutoScaleMode.None`; every position comes from the scale.
- Light theme only. Reason: the native `ComboBox`, the spin buttons of `NumericUpDown` and the check boxes draw with the light visual style. A dark version needs undocumented theme names (`DarkMode_CFD`) or new controls. Bran's app mode is light (registry, 2026-11-22).
- Every key, default and value range stays. Save does not restart a running recording (decision 7). `OnSave` is split: `ReadFields` reads the fields with no file or registry change; `OnSave` then makes the folder and closes.

**Part 3: tray menu** (`TrayMenu.cs`, texts in `TrayModel`).

- `ContextMenuStrip` with a status item at the top (a colored dot, "Not recording", "Recording  1:23", "Stopping the recording...", "Processing the last recording..." or "Closing Desktop Recorder...", a detail line, and the last result), then three groups: record; folder and ZIP commands; Settings and Exit.
- The record command is bold, with the hotkey at the right (not shown when another program has the hotkey). Icons from Segoe Fluent Icons (Segoe MDL2 Assets on Windows 10). Disabled commands are gray. The record command is off while FFmpeg stops and while the app closes.
- Position (the known problem): the dotnet/winforms source of `ContextMenuStrip.ShowInTaskbar` computes the place from the menu size before the `Opening` event, so a menu that changes in `Opening` opens at a wrong place. The app never changes the menu in `Opening`: `Refresh` updates it at each state change, a 1 s timer updates the time while recording (the status item has a fixed width), and the right mouse button down sets the scale and the theme before the button up opens the menu. The menu window is made at start, under the per-monitor-v2 context, so its size is known before the first open. The advisor advised against an own show path (foreground and keyboard open are hard to check without a live test).
- Scale: the DPI of the monitor under the pointer, at each right button down. Theme: the app mode. Windows 11: small rounded corners (`DWMWCP_ROUNDSMALL`) and the border color.
- The tooltip is "Desktop Recorder: not recording / recording / processing / closing" (at most 63 characters).

**Theme.** One switch for all three parts: `Ui.ThemeValue = "AppsUseLightTheme"` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`). Microsoft documents the WinRT `UISettings` check; .NET Framework 4 with `csc.exe` cannot call WinRT easily, so the app reads the registry value. Dark title bar with `DWMWA_USE_IMMERSIVE_DARK_MODE` (20). Colors after the Windows 11 light and dark themes, with the default Windows blue as accent (not Bran's purple accent). Fonts: Segoe UI and Segoe UI Semibold in pixel units (14 epx text, 12 epx captions, 20 epx titles).

**Checked (this job, on Bran's PC, no window shown):**

- The app builds with the `csc.exe` command of `build.cmd` (`/out:DesktopRecorder-zip.exe`; CS0649 warnings only).
- Unit tests: `ok` (new `tests/UiTests.cs`: duration text, every close trigger with and without text, the answers, the Enter keys, the clean note, the 19 catalog keys against `SerializeSettings`, each default in the help texts against `StoredSettings`, the tray phase, commands and texts, the tooltip length).
- `UiModel.cs` and `tests/UiTests.cs` compile with `/noconfig` and only `System.dll` and `System.Core.dll` (no System.Drawing, no WinForms), as Mono in `run-tests.sh` needs.
- Offscreen preview (`test/ui_preview.cs`) at 100%, 125% and 150%, light and dark: 0 layout problems. The check: every visible control is inside its parent, visible sibling controls do not overlap (HANDOFF 6.7 bug 3), the text of each label, button, check box, list item and navigation item fits.
- Settings round trip in the preview: the window reads back the same `settings.txt` lines for the defaults (29 lines), for all values changed (30 lines, also the fields that the window does not show), and after Restore defaults (30 lines).
- The preview found and the agent fixed: the recording name of the note box was never placed (it used `Visible`, which is false before the form shows); the page names were cut in the navigation; two list texts were cut at 100%; the restore check (the preview first used `PerformClick`, which does nothing on a hidden form).
- Previews looked at by the agent and by a review agent: see the list below.

**Independent review (a code-reviewer agent, in the foreground, 2026-11-22; it got the diff 2669732..HEAD and the PNG files).** No critical fault. It confirmed: the same 19 settings, keys, defaults and ranges; Save does not restart a recording; the note `Saved` and `FormClosed` code keeps `job.NoteOpen`, `TryCleanup` and the second ZIP; the six tray commands call the old methods; Exit waits for passes, ZIPs, cleanup and note boxes; the DPI context of each window; no C# 6 syntax. All 7 findings were checked against the code and accepted:

1. Exit did not stop a running recording while a note box asked. Fixed: Quit stops a folder recording before it asks (as the old code did). `TrayModel.PhaseOf` puts "Recording" before "Closing", so an MP4 recording keeps "Stop recording" in the menu until ExitNow stops it. New unit checks.
2. A refused sign-out left the box in the "decided" state, so a later X closed it without a question. Fixed: the shutdown path saves but stays undecided; `RaiseSaved` does not save the same text two times; `CloseForQuit` tests only `IsDisposed`.
3. An open menu could grow when a new state came (NotifyIcon placed it from the old size). Fixed: while the menu is open, only the time changes; the new state applies in `Closed`.
4. The focus went to the text frame panel, not the text box. Fixed: `ActiveControl = frame.Box`.
5. The automatic scroll bar changed `ScrollBars`, which makes the edit window again (undo and IME input lost). Fixed: the vertical scroll bar is always there (the native bar is light in dark mode).
6. A screen reader did not read the question. Fixed: the Save button of the question gets the question as `AccessibleDescription`.
7. (Older fault, now visible.) The hotkey could start a recording while the app closed; ExitIfDone could then end the app before its pass. Fixed: `SelectAndRecord` does nothing while `quitRequested` is true.

After the fixes (checked): the app builds, unit tests `ok`, the preview checks `preview ok`, the 3 round trips pass, and the note previews are new (scroll bar). Rejected findings: none.

**Offscreen previews (NOT real screenshots).** The preview tool draws each form with `Control.DrawToBitmap` into a PNG file, without showing it. Limits: client area only (the real window adds the Windows title bar and DWM effects: rounded corners, shadow); the tool draws the selected text of each drop-down list itself (`WM_PRINT` skips it); native parts (check marks, list arrows, spin buttons) draw at the DPI of this PC (125%), so they are correct in the 125% files and simulated in the 100% and 150% files.

- Note box: [note-125-light](docs/ui/offscreen-note-125-light.png), [note-125-dark](docs/ui/offscreen-note-125-dark.png), [note-question-125-light](docs/ui/offscreen-note-question-125-light.png), [note-quit-question-125-dark](docs/ui/offscreen-note-quit-question-125-dark.png), [note-100-light](docs/ui/offscreen-note-100-light.png), [note-150-light](docs/ui/offscreen-note-150-light.png).
- Settings: [settings-recording-125](docs/ui/offscreen-settings-recording-125.png), [settings-capture-125](docs/ui/offscreen-settings-capture-125.png), [settings-hotkey-125](docs/ui/offscreen-settings-hotkey-125.png), [settings-aiaudio-125](docs/ui/offscreen-settings-aiaudio-125.png), [settings-startup-125](docs/ui/offscreen-settings-startup-125.png), [settings-message-125](docs/ui/offscreen-settings-message-125.png), [settings-recording-100](docs/ui/offscreen-settings-recording-100.png), [settings-recording-150](docs/ui/offscreen-settings-recording-150.png).
- Tray menu: [tray-idle-125-light](docs/ui/offscreen-tray-idle-125-light.png), [tray-recording-125-light](docs/ui/offscreen-tray-recording-125-light.png), [tray-recording-125-dark](docs/ui/offscreen-tray-recording-125-dark.png), [tray-processing-125-light](docs/ui/offscreen-tray-processing-125-light.png), [tray-recording-100-light](docs/ui/offscreen-tray-recording-100-light.png), [tray-recording-150-light](docs/ui/offscreen-tray-recording-150-light.png).

**Not checked (needs the live test below):** the real windows on the screen; every key and click; the focus at open; the flash; the menu position at the first open and on a later open; the rounded corners and the shadow of the menu; dark mode on the screen; a real recording with this build (note box out of the video); the Settings Save with a real click; Windows 10; another monitor or scale; a screen reader. The Mono build and the GitHub CI with the new files (no Mono on this PC).

**Open decisions for Bran:**

1. Tray menu theme: it follows the app mode (light on Bran's PC). Windows' own taskbar menus follow the Windows mode (dark on Bran's PC). To change: `Ui.ThemeValue = "SystemUsesLightTheme"` in `UiTheme.cs` (this also changes the note box).
2. Line breaks in the note: `index.md` shows the note on one line. A change needs `RecordingIndex.Build` in `SettledFrames.cs`.
3. Accent color: the default Windows blue. Bran's accent color (purple) is not used.
4. Settings in dark mode: not done (reason above).

**Live test plan (about 20 minutes, hands off, for a later job).** Stop the installed app first (save `settings.txt` before), start `DesktopRecorder-zip.exe`, use synthetic input (`test\app_runs.ps1` style) and DPI-aware screenshots at 125%:

1. Tray: right-click the tray icon. Screenshot. Check: the menu opens above the taskbar at the pointer, its bottom edge does not go below the taskbar top on the first open and on the second open, rounded corners, status "Not recording", bold record command with "Win+Shift+D". Hover one command (rounded mark). Click each command once: Open recordings folder, Settings (then Cancel), Show last ZIP / Copy ZIP (gray before a recording), Exit last.
2. Tray during a recording: start with the hotkey and a drag. Open the menu: "Recording 0:0x" counts each second while the menu stays open; "Stop recording" stops.
3. Note box (folder mode): after Stop, screenshot. Check: middle of the monitor, the focus is in the text box (type at once), not in the video (decode the last frames of `clip.mp4` in the ZIP).
4. Note keys: Enter with text saves (the note is in `index.md` of the new ZIP). Shift+Enter adds a line. Esc with an empty box closes. Esc with text: question; then Save, then (next recording) Discard, then (next) Back (text kept). The X button and Alt+F4 with text: question. Click another window: the box stays on top. Exit with a box that has text: question; Back keeps the app; Discard closes the box and the app.
5. Flash: start a recording, click another app, stop with the Stop button: if the box does not get the focus, its taskbar button flashes.
6. Settings: open (double-click the tray icon). Screenshot of each page (Ctrl+Tab). Change one setting on each page, Save, open again: the values stay, and `settings.txt` has the keys. Change and Cancel: nothing changes. Restore defaults, Cancel: nothing changes. A bad hotkey (no Win, Ctrl or Alt): red text, Hotkey page.
7. Dark mode: set the app mode to dark (Settings > Personalization > Colors), open the tray menu and a note box, screenshot, set it back to light.
8. If possible without a sign-out: 100% and 150% (otherwise "not checked").
9. ZIP indication (7.21): record 20 s in folder mode and stop. The tray icon turns, the tray menu shows "Making the ZIP... (step: ...)", and the note box line moves from "stopping" to "frames" (and "transcript" with audio) to "ZIP". Press Ctrl+V in Notepad at once: the wait text pastes, not the ZIP before. When the ZIP is ready: the note box line says "ZIP ready. Press Ctrl+V to paste (... MB)", the popup shows at the bottom center for about 4 s without taking the focus (type in Notepad while it shows), the ready sound plays, and Ctrl+V in Explorer pastes the new ZIP.
10. Close the note box (No note) before the ZIP is ready: the busy popup shows. Click it: it closes and does not come back until "ZIP ready".
11. Save a note after "ZIP ready": the line says "ZIP" again, then "ZIP with your note ready"; the ZIP has the note.
12. Make the ZIP fail (for example a file named like the ZIP that is read-only): the popup and the note box say "Could not make the ZIP" with the reason, and the clipboard does not keep the wait text.
13. Decode the last frames of `clip.mp4`: no popup and no busy bar in the video. Check Win+V (clipboard history) for the extra wait text.

At the end: restore `settings.txt`, start the installed app again.

### 7.21 "Busy, then ready" indication for the ZIP (2026-11-22, branch `zip-only`, not pushed, not released)

Request from Bran (19:43, after a real test of the ZIP-only build): "Some sort of indication that the zip is being created would be helpful. So I don't try to paste too soon." A 30 s Twitch recording made a ZIP of 33.7 MB. Until the ZIP is on the clipboard, Ctrl+V pastes the old clipboard content (often the recording before). Tray balloons do not show on Bran's PC (6.7). The override of 7.20 applied: code only, no app start, no input, no screenshot.

Research: Microsoft progress guidance (an indeterminate indicator when the time of the work is not known; determinate when it is bounded) and Windows tools (Snipping Tool shows a banner "copied to the clipboard" after a capture; ShareX shows a toast and plays a sound after a copy). Sources: https://learn.microsoft.com/windows/apps/design/controls/progress-controls and https://learn.microsoft.com/en-us/windows/win32/uxguide/progress-bars. The work after Stop has no measurable percentage, so the app shows a busy bar plus the name of the step.

**What the app does now (folder mode only; "MP4 only" is not changed):**

- Steps (`ZipStep` in `UiModel.cs`): stopping, frames, transcript, ZIP, then ready or failed. The frame pass reports "frames" and "transcript" with the new callback `AfterRecordingInput.Step` (`AfterRecording.cs`); the app sets "stopping" at Stop and "ZIP" when the ZIP starts. Texts: `ZipStatus` (unit tested).
- Tray icon: from Stop until the ZIP is on the clipboard, an arc turns around the ring (8 frames, 125 ms, made once for each icon size and kept). Tray menu status: "Making the ZIP... (step: frames)" and "Wait for "ZIP ready" before you paste." Tooltip: "Desktop Recorder: making the ZIP (frames)". A new recording or Exit shows its own status first.
- Note box: a line under the recording name. Busy: a moving bar and "Making the ZIP... (step: ...)". Ready: a green check mark and "ZIP ready. Press Ctrl+V to paste (33.7 MB)." Failed: a red mark and the error. After a late note: "ZIP with your note ready".
- Popup (`ZipToast` in `NoteForm.cs`): bottom center of the work area of the monitor of the recording. It does not take the focus (`WS_EX_NOACTIVATE`, `ShowWithoutActivation`), it is topmost by window style (the `TopMost` property can take the focus), it is out of the video (`ExcludeFromCapture`), and a click closes it. Busy: only when the note box is closed, and not again after the user closed it. Ready: "ZIP ready: Ctrl+V to paste (33.7 MB)" for 4 s. Failed: "Could not make the ZIP" with the reason, for 10 s. It follows the app theme.
- Sound: with "Play sounds" on, a short high chime (1047 Hz to 1568 Hz) when the ZIP is ready. Not during a new recording (the system audio capture records it) and not at Exit.
- Clipboard at Stop (decision without Bran, item 5 of the request): when "Copy to the clipboard" is on, the clipboard gets the text "Desktop Recorder is still making the ZIP of the last recording. Wait for "ZIP ready", then paste again." An early Ctrl+V then pastes this text, not the ZIP of the recording before. Reason: with the copy on, the app replaces the clipboard a few seconds later in any case, so the change only comes earlier; the old content is the main cause of the wrong paste. When the copy is off, the app does not touch the clipboard. When the ZIP fails or cannot be copied, the app replaces the wait text with the result (the text does not stay). Windows clipboard history (Win+V) can keep the wait text as an extra item.
- An older recording whose ZIP is ready while a newer recording is still busy does not replace the clipboard of the newer one (its note box says why, and "Copy ZIP of last recording" still works).
- 7.18 rules: not changed (ZIP path, the folder is deleted after the note box closes and no ZIP is pending, a late note makes the ZIP again, Exit waits). One fix: a good ZIP after a failed ZIP now clears `ZipError`, so the folder cleanup runs (before, the folder stayed).

**Independent review (a code-reviewer agent, in the foreground; it got `git diff HEAD`).** No critical fault, no C# 6 syntax. All 7 findings were checked against the code and accepted: (1) the wait text stayed on the clipboard after a failed ZIP or copy: now replaced with the result; (2) "ready" and the sound came while a second ZIP (late note) was still written: "ready" now waits for `ZipPending == 0`; (3) a stale `ZipError` kept the folder after a later good ZIP: cleared; (4) a busy popup closed by the user came back at the next step: it stays closed until ready or failed; (5) an older ZIP replaced the wait text of a newer recording, and the sound could play during a recording: both stopped; (6) the popup and the sound at Exit: not shown; (7) the busy timer is disposed at Exit. Rejected findings: none.

**Checked (this job, on Bran's PC, no window shown):**

- The app builds (`/out:DesktopRecorder-zip.exe`; CS0649 warnings only). Unit tests `ok` (new `TestZipStatus`: every step text in the note box, popup, tray menu and tooltip, the times, the MB format, the copy error texts). `UiModel.cs` still compiles with `/noconfig` (no System.Drawing, no WinForms).
- Offscreen previews at 100%, 125% and 150%, light and dark: `preview ok` (no overlap, no cut text, the popup inside the work area). The checks found and the agent fixed two layout faults (a mark and a detail line that read `Visible` before the form shows) and a too narrow mark at 150%.
- Previews (NOT real screenshots, see 7.20): [note-zip-busy-125-light](docs/ui/offscreen-note-zip-busy-125-light.png), [note-zip-ready-125-dark](docs/ui/offscreen-note-zip-ready-125-dark.png), [note-zip-failed-125-light](docs/ui/offscreen-note-zip-failed-125-light.png), [toast-busy-125-light](docs/ui/offscreen-toast-busy-125-light.png), [toast-ready-125-light](docs/ui/offscreen-toast-ready-125-light.png), [toast-ready-125-dark](docs/ui/offscreen-toast-ready-125-dark.png), [toast-ready-150-light](docs/ui/offscreen-toast-ready-150-light.png), [toast-failed-125-dark](docs/ui/offscreen-toast-failed-125-dark.png), [tray-zip-125-light](docs/ui/offscreen-tray-zip-125-light.png), [tray-zip-125-dark](docs/ui/offscreen-tray-zip-125-dark.png), [tray-icon-busy-20](docs/ui/offscreen-tray-icon-busy-20.png) (the 8 frames at 20 px, 4 times larger). The note box previews of 7.20 are new (the box has the ZIP line now).

**Not checked:** everything on the screen (live test items 9 to 13 below): the popup position, that it does not take the focus, the click, the animation of the tray icon, the sound, the clipboard text at Stop with a real paste, the times on a real recording, Windows clipboard history. The busy bar and the popup in a real recording (they must not be in the video).

## 8. Rules for the agent

- Commands for Bran's PC: PowerShell 5.1. No `&&` or `||`. One command in each fenced code block.
- Never delete a file, branch or other item without Bran's approval. Ask once, near the end, with a list.
- FFmpeg encodes: use `h264_nvenc` or `hevc_nvenc`. Use `libx264` only as the fallback in the app or when Bran asks.
- Never write a token or password in output. Use a placeholder.
- Do not weaken Windows security. Do not suggest turning off Defender or Controlled Folder Access.
- Write commit messages from a file, in STE100.
- Keep the Linux port working. Do not change files in `linux/`.

---

## 9. Sources

- ShareX region capture docs: https://getsharex.com/docs/region-capture
- ShareX changelog (ddagrab support, region options): https://getsharex.com/changelog
- ShareX screen recording guide: https://getsharex.com/blog/how-to-record-screen-windows/
- ShareX feature list: https://getsharex.com/
