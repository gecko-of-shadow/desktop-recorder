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

1. The hotkey opens the selector. A second hotkey ("record last region") skips the selector.
2. The selector is one borderless, topmost form that covers `SystemInformation.VirtualScreen` (all monitors).
3. The selector freezes a screenshot of the desktop and shows it dimmed. The selected area shows at full brightness.
4. On hover, the selector draws a border around the window under the cursor.
   - Get windows in Z-order with `EnumWindows`.
   - Get the real bounds with `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`. `GetWindowRect` includes the invisible shadow border.
   - Skip windows that are not visible, minimized, cloaked (`DWMWA_CLOAKED`), tool windows, and the selector itself.
   - Optional: detect child controls with `ChildWindowFromPointEx`.
5. Click = select the highlighted window. Drag = select a rectangle.
6. After the selection, the user can move and resize it with handles and arrow keys. Enter or double-click starts the recording. Esc cancels.
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
3. Default frame-rate mode: "Fixed" stays the default. Decide about "Changes only" after Phase B (run B12).

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

## 7. Tracker

| Item | Status |
|---|---|
| Research ShareX region capture | Done |
| Read current code | Done |
| Test kit (6.1) | Done. Self-tests pass on Bran's PC (score 28/28, frames 70/70). `frames.py --counter` read real recordings in Phase A (0 failed frames). `scroll.html` and `score.py`: no real recording yet. |
| Phase A test | A1 to A5 checked on Bran's PC (all PASS, see 6.5). A6 not done. Scroll stress run checked (6.6): no lost frames with A1 or A4 settings. |
| Region selector | Not done |
| Recording frame | Not done |
| ddagrab + NVENC backend | Not done |
| "Changes only" mode | Not done |
| Phase B test | Not done |
| README update | Not done |

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
