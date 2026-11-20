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
9. Mouse pointer: shown by default (`show_pointer=1`). The "Hide the mouse pointer" check box in Settings is off by default (6.9). Bran decided on 2026-11-14.
10. Default frame rate: 10 fps (was 2). Bran decided on 2026-11-14. Reason: the 6.8 rule needs 2.3 fps for 1000 CSS px/s at 125% in an 892 px area. 10 fps is safe up to about 4400 CSS px/s. Bran's settings file already had `fps=10`.
11. Quality default: keep Balanced (CQ 23). More reads of B3, B9 and B11 are put off to a later date (Bran, 2026-11-14).
12. Release: push `test-kit`, merge it into `main`, publish v2.0.0 (Bran, 2026-11-14). The version is 2.0.0 because the hotkey now opens the selector, not a full-desktop recording.

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
| AI capture folder (7.5, 7.6) | On branch `ai-capture` (pushed). Real recordings checked on Bran's PC (7.6). Not merged, not released. |
| Audio and transcript (7.7) | Code and unit tests done in a cloud session (Linux, Mono). Off by default. UNVERIFIED on Windows. |
| Twitch recording faults (7.7) | Motion note and false-scroll fix done, tested on synthetic files. UNVERIFIED on the real recording. Clipboard risk written down, not changed. |

### 7.1 Later and future work (Bran, 2026-11-14)

- Later: more reads of B3, B9 and B11 to decide the quality default (decision 11).
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
