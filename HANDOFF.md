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

Mark an item "checked" only after a run on Bran's PC. Mark all other claims UNVERIFIED.

---

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
