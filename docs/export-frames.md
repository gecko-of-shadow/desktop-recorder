# Design note: "Export frames"

Date: 2026-11-14. Status: exploration only. No app code is changed.

Language: ASD-STE100.

---

## 1. Problem

Some AI tools sample an uploaded video at a fixed rate. Gemini samples at 1 frame per second (fps) by default (checked, section 2). Desktop Recorder records at 10 fps by default. During a fast scroll, a 1 fps sample skips most frames, and thus skips text.

In Phase B (HANDOFF.md 6.8), the reader caught 300 of 300 lines. The reader got every unique frame as a PNG file. It did not get the video.

Idea: the app saves the unique frames of a recording as PNG files. The user gives the files to the AI tool.

---

## 2. How the AI tools take video and images

Sources were read on 2026-11-14. "Checked" = read on the official page in this session. "UNVERIFIED" = not found on an official page, or not read in full.

### 2.1 Video

| Tool | Video input | Sampling rate | Status |
|---|---|---|---|
| Gemini API | Yes | 1 fps by default ("extracting frames at 1 FPS"). The caller can set `fps` in a `processing` object (sample: `fps: 0.5`). | Checked: https://aistudio.google.com/docs/video-understanding (redirect from ai.google.dev) |
| Gemini app | Yes. Up to 2 GB for each video. Total length 5 min, 1 h with Google AI Pro or Ultra. | Not stated. The user cannot set it. | Size and length checked: https://support.google.com/gemini/answer/14903178 . Rate UNVERIFIED. |
| ChatGPT app and OpenAI API | API vision guide does not mention video. An OpenAI cookbook example splits a video into frames and sends them as images. | Not stated. | UNVERIFIED (help.openai.com gave HTTP 403). https://developers.openai.com/api/docs/guides/images-vision |
| Claude (API and claude.ai) | The vision page and the upload article do not mention video. | Not applicable. | UNVERIFIED that video is not accepted. https://platform.claude.com/docs/en/build-with-claude/vision |
| Grok app | Accepts MP4 and MOV files, up to 150 MB. | Not stated. | File types checked: https://docs.x.ai/grok/faq . Rate UNVERIFIED. |
| Grok API | No general video upload found. Video reading is only for videos in X posts (X Search tool). | Not stated. | Checked: https://docs.x.ai/developers/tools/x-search |

Gemini tokens for a video frame: the video page gives 258 tokens for each frame (66 at low resolution). The media-resolution page gives 70 tokens for each frame on Gemini 3 and 4 at the default setting. The two pages do not agree. An image at the default setting on Gemini 3 and 4 costs 1120 tokens (https://aistudio.google.com/docs/media-resolution). Thus Gemini probably reads a video frame at a lower resolution than a PNG image. UNVERIFIED: the effect on small text.

### 2.2 Images

| Tool | Images in one request or message | Size limits | Scaling | Formats |
|---|---|---|---|---|
| Claude API | 100 for each request (200k-token models), 600 for other models. Checked. | 10 MB for each image, 8000x8000 px. More than 20 images in a request: each side 3000 px or less. Checked. | Long side above 1568 px (standard tier) or 2576 px (high-resolution tier, Claude 4.7 and later) is scaled down. The tier values come from a summary of the page, not from the verbatim table. Partly checked. | JPEG, PNG, GIF, WebP |
| claude.ai | 20 for each message. Checked. | 10 MB for each image. | Same as the API. | Same |
| Gemini API | 3600 image files for each request. | 20 MB for the full inline request. | Images above 384 px are cut into 768x768 tiles. No maximum size stated. | PNG, JPEG, WEBP, HEIC, HEIF |
| Gemini app | 10 files for each prompt. | 100 MB for each file (not video). | Not stated. | Not read |
| OpenAI API | 1500 images for each request, 512 MB in total. | 30000 patches (32x32 px) for each image. A larger image is rejected, not scaled. | Depends on the model. Example gpt-5.6 `high`: fits in 2048x2048 px. | PNG, JPEG, WEBP, GIF (not animated) |
| ChatGPT app | UNVERIFIED (page gave HTTP 403). | UNVERIFIED | UNVERIFIED | UNVERIFIED |
| Grok API | "No limit" on the number of images. | 20 MiB for each image. | Not stated. | JPEG, PNG |
| Grok app | About 100 files (web), 20 (Android). | 150 MB for each file. | Not stated. | JPEG, PNG, WebP, HEIC, BMP |

Sources: https://platform.claude.com/docs/en/build-with-claude/vision , https://support.claude.com/en/articles/8241126-what-kinds-of-documents-can-i-upload-to-claude-ai , https://aistudio.google.com/docs/image-understanding , https://support.google.com/gemini/answer/14903178 , https://developers.openai.com/api/docs/guides/images-vision , https://docs.x.ai/docs/guides/image-understanding , https://docs.x.ai/grok/faq .

### 2.3 What this means for the export

- Gemini (video at 1 fps) is the main reason for the feature. For Claude, the user must give images, because no video input was found.
- The chat apps have low file limits: 20 for each message (claude.ai), 10 for each prompt (Gemini app).
- A frame must stay at or below 1568 px on its long side for Claude on the standard tier. The app must never scale a frame down (HANDOFF.md 4.3). Thus the export must keep the frame size of the video.

---

## 3. How to find the unique frames

### 3.1 The current test scripts

- `test/frames.py` exact mode: a frame is a duplicate only when its decoded pixels are identical. On lossy video this over-counts unique frames (HANDOFF.md 6.6: 99 unique frames for 41 real changes).
- `test/prep_frames.py`: mean absolute luma difference to the last kept frame, more than 0.1 = unique. The mean is over the full frame. Thus a small change in a large frame can stay below 0.1 (`frames.py` header: 11 of 54 bar code changes found in a 1000x400 frame).
- Both scripts are Python. The app has no Python at run time.

### 3.2 FFmpeg-only method

The app already ships FFmpeg. The `mpdecimate` filter drops a frame when it is almost the same as the last kept frame. It compares blocks of 8x8 px. Parameters: `hi` (a block that differs more than `hi` keeps the frame), `lo` and `frac` (the frame is kept when more than `frac` of the blocks differ more than `lo`). Defaults: `hi=64*12`, `lo=64*5`, `frac=0.33`.

Command (one pass, no Python):

```
ffmpeg -i INPUT.mp4 -vf "mpdecimate=hi=64*4:lo=64*2:frac=0.33" -fps_mode vfr OUTDIR\frame_%04d.png
```

`-fps_mode vfr` is necessary. Without it, FFmpeg fills the gaps with copies.

### 3.3 Tests (run on Bran's PC on 2026-11-14)

Script: `cmp_filters.py` in the session scratchpad (not in the repository). FFmpeg build as HANDOFF.md 6.5.

**Test 1: agreement with `prep_frames.py` on 6 Phase B videos.** Reference = the `prep_frames.py` rule (threshold 0.1) over the full video.

| Video | Frames | Reference unique | `mpdecimate` (default) | Lowered (`hi=64*4:lo=64*2`) | `select=gt(scene,0.001)` | `select=gt(scene,0.003)` |
|---|---|---|---|---|---|---|
| B3 (10 fps, 1000 px/s) | 119 | 77 | 77, 0 missed | 92, 0 missed | 76, 1 missed | 69, 8 missed |
| B5 (10 fps, 500 px/s) | 191 | 150 | 150, 0 missed | 164, 0 missed | 149, 1 missed | 148, 2 missed |
| B12 (Changes only, 15 fps) | 116 | 113 | 113, 0 missed | 114, 0 missed | 111, 2 missed | 104, 9 missed |
| LL (lossless) | 124 | 76 | 76, 0 missed | 76, 0 missed | 73, 3 missed | 70, 6 missed |
| B7s4 (4 fps, 3000 px/s) | 27 | 11 | 11, 0 missed | 16, 0 missed | 10, 1 missed | 10, 1 missed |
| B6 (10 fps, 2000 px/s) | 77 | 39 | 39, 0 missed | 52, 0 missed | 38, 1 missed | 35, 4 missed |

"Missed" = reference-unique frames that the filter drops. The `select` filter always drops frame 0 (its scene score is 0) and drops real scroll frames at 0.003. Do not use `select` with `scene`.

Default `mpdecimate` gives the same set as `prep_frames.py`. This shows agreement with `prep_frames.py`. It does not prove that no text is lost, because `prep_frames.py` can also miss small changes (3.1).

**Test 2: small changes.** A synthetic page, 1120x892, white, one line of 13 px static text. One 12 px text item at a fixed position changes each 1 s.

| Video | Changes | Default `mpdecimate` | Lowered (`hi=64*4:lo=64*2`) |
|---|---|---|---|
| Digit 0 to 19 (NVENC CQ 23) | 20 | 20 of 20, 0 extra | 20 of 20 |
| Look-alike sequence (NVENC CQ 23): `x.y`, `x,y`, `O`, `0`, `l`, `1`, `I`, `rn`, `m`, `a:b`, `a;b`, `a'b`, ``a`b``, `-`, `_` | 15 | 11 of 15 | 15 of 15, 1 extra |
| Same sequence, raw frames (no encode) | 15 | 11 of 15 | not run |
| Counter recording (app run dda420, 1880x860) | 104 | 104 of 104 | 104 of 104 |

Default `mpdecimate` dropped these 4 changes: `.` to `,`, `l` to `1`, `:` to `;`, `-` to `_`. These are the look-alike characters that the AI test needs. The lowered thresholds kept all 15.

**Test 3: cost of the lowered thresholds.** The lowered thresholds keep 0 to 15 more frames in each Phase B video (B3: 92 against 77). These extra frames come after the scroll stops: NVENC refines a still picture over some frames (HANDOFF.md 6.6). An extra frame costs reader time. It does not lose text.

**Test 4: VFR input.** B12 ("Changes only", variable frame rate): 114 PNG files, and `showinfo` counted 114 frames. No gap-fill copies.

**Test 5: speed and size.** B3, 1120x892, lowered thresholds: 92 PNG files in 0.45 s, 28 MB in total (about 310 KB for each file). The video is 4.3 MB.

**Test 6: long recording.** `C:\Users\Bran\Recordings\desktop_2026-11-14_19-39-08_166.mp4` (1920x1080, 10 fps, 334 s, normal desktop work): 2927 unique frames (default), 3027 (lowered). The two counts took 12 s together. A long work session has too many unique frames for any chat app.

### 3.4 Proposed method

Use `mpdecimate=hi=64*4:lo=64*2:frac=0.33` with `-fps_mode vfr`. Do not use `max`: the export must not add forced frames.

---

## 4. UI options

| Option | Good | Bad |
|---|---|---|
| A. Automatic after each recording (setting on/off) | No extra step. | Writes many MB for each recording, also when the user does not need it. A long recording gives thousands of files. |
| B. Tray menu item "Export frames of last recording" | The user decides. No cost when not used. Simple. | One extra click. |
| C. Tray menu item "Export frames..." with a file dialog | Works for any older recording. | Two extra steps. |
| D. Setting "After recording: copy frames to clipboard" | Fast for a user who always uses an AI chat. | Same cost as A. |

Recommendation: B, plus C as a second item. Add A as a setting later if Bran uses the export after most recordings.

Clipboard: .NET Framework 4 has `Clipboard.SetFileDropList`. It puts a list of files on the clipboard. The user can paste the files into Explorer. UNVERIFIED: that each chat web page (claude.ai, Gemini, ChatGPT, Grok) accepts a paste of a file list. A safe default: open the folder in Explorer and select the first file. The user drags the files into the chat.

---

## 5. Output options

| Option | Good | Bad |
|---|---|---|
| Folder of PNG files | Full resolution. Works with every tool that takes images. Same input as Phase B (300 of 300 lines). | Many files. Chat apps limit the count (2.2). |
| Contact sheet (FFmpeg `tile`, several frames in one image) | Fewer files. | The sheet is larger than one frame. B3 with `tile=1x2` gave 1120x1784 px. Claude standard tier scales this down to a long side of 1568 px, so the text gets smaller (HANDOFF.md 6.8 rule). A sheet is only safe when the full sheet stays at or below the reader limit. Example: a region of 700x400 px fits 1x3 (700x1200) under 1568 px. |
| Slideshow video at 1 fps (each unique frame shows for 1 s) | One file. A 1 fps sampler (Gemini) sees each unique frame once. Tested: B3 gave 92 frames, 92 s, 19 MB, 0.42 s to encode with `h264_nvenc`. | Gemini probably reads a video frame at fewer tokens than an image (2.1). UNVERIFIED: that small text stays readable. UNVERIFIED: that Gemini samples exactly on the frame times. |
| Both folder and sheets | Covers more tools. | More code and more disk use. |

Recommendation: a folder of PNG files as the main output. Contact sheets only as an option, and only when the app can compute a grid that stays at or below the limit. The slideshow is an idea for a Gemini test, not for version 1.

File names: put the order and the time in the name, for example `frame_0001_t001.7s.png`. The AI then knows the order and the time. The app can get the times from the FFmpeg `showinfo` output.

---

## 6. The image limit for one reader

Known limits:

| Limit | Value | Source |
|---|---|---|
| Claude agent in Phase B (one reader) | About 110 images. The B5 reader got 136 frames and saw only 49. | HANDOFF.md 6.8 (test result, not a documented limit) |
| claude.ai | 20 images for each message | Checked (2.2) |
| Gemini app | 10 files for each prompt | Checked (2.2) |
| Claude API | 100 or 600 for each request | Checked (2.2) |

Frame counts for real recordings (section 3.3): 11 to 164 frames for a 3 to 15 s scroll. About 3000 frames for 5.5 min of desktop work.

Proposal:

1. Put the frames into numbered sub-folders (chunks): `part_01`, `part_02`, ... Default chunk size: 20 (claude.ai). Setting: chunk size (10 for the Gemini app, 100 for an API).
2. Add a text file `README.txt` in the export folder: the number of frames, the number of parts, the frame size, and a ready prompt ("Read the frames in order. Part 1 of N."). UNVERIFIED: that a prompt in a file helps the AI.
3. When the export has more than a limit (for example 200 frames), show a warning in the status line. Then the user can record a shorter part.

---

## 7. Recommendation

Build version 1 as:

- Tray item "Export frames of last recording" and tray item "Export frames..." (file dialog).
- One FFmpeg pass: `mpdecimate=hi=64*4:lo=64*2:frac=0.33`, `-fps_mode vfr`, PNG, no scaling.
- Output folder next to the video: `<video name>_frames\part_NN\frame_NNNN_tSSS.Ss.png`, chunk size from a setting (default 20).
- After the export: open the folder in Explorer, and show "Frames: N in P parts" in the status line.
- Run FFmpeg on a background thread, the same as the stop of a recording (HANDOFF.md 6.7).

Cost in code (estimate, not measured):

| Part | Lines |
|---|---|
| `RecorderCore.cs`: build the export arguments, chunk plan, file names | 60 to 80 |
| `DesktopRecorder.cs`: two tray items, background run, status line, open folder | 100 to 140 |
| Settings: chunk size, (later) automatic export | 20 to 40 |
| `tests/CoreTests.cs`: arguments, chunk plan, names | 50 to 70 |
| Total | about 230 to 330 |

No new dependency. FFmpeg is already present.

Risks:

1. **Lost look-alike changes.** Default `mpdecimate` drops some 1-character changes (3.3, test 2). The lowered thresholds fixed all 15 test cases. A change of a smaller size (for example one pixel) can still be dropped. UNVERIFIED: changes smaller than one 12 px character.
2. **Extra frames.** The lowered thresholds keep up to 15 more frames for each scroll. This costs reader time, not text.
3. **Disk use.** About 310 KB for each PNG at 1120x892. 3000 frames = about 0.9 GB.
4. **Too many frames.** A long recording gives thousands of frames. No chat app takes that many. The export suits short recordings of a scroll.
5. **Clipboard.** UNVERIFIED that the chat web pages accept a pasted file list.
6. **Frame size.** A region larger than 1568 px on its long side is scaled down by Claude on the standard tier. The export cannot fix this, because the app must not scale down. The selector can show a hint.
7. **The AI tool samples the files too.** UNVERIFIED: that every tool reads every image in a large upload.

---

## 8. Separate finding: "Changes only" capture mode

`RecorderCore.cs` adds `mpdecimate=max=<ceil(fps)-1>` with the default thresholds when the frame-rate mode is "Changes only" (the default mode, HANDOFF.md section 5). Test 2 shows that the default thresholds drop 4 of 15 look-alike changes on raw frames too (no encode).

In the app, `max` keeps at least one frame each second. Thus a change that stays on the screen for 1 s or more gets into the video, up to 1 s late. A short change (less than 1 s) that a second small change replaces can be lost. An export cannot bring back a frame that the capture dropped.

This was not tested with the app. Proposed follow-up: test the lowered thresholds in the capture filter of "Changes only" (file size on a still screen, look-alike sequence). Do not change the code before that test.

---

## 9. Checked and not checked

Checked in this session (on Bran's PC or on official pages):

- All numbers in section 3.3.
- Gemini 1 fps default and the `fps` field in a `processing` object.
- claude.ai 20 images for each message. Claude API 100 or 600 images for each request. 3000 px limit above 20 images.
- The other facts in 2.1 and 2.2 that have a source URL and are not marked UNVERIFIED. A research agent read these pages. I read the Gemini video page and the Claude vision page again with a second agent.

Not checked:

- Claude scaling values 1568 px and 2576 px: from a summary of the page, not from the verbatim table.
- ChatGPT app limits (HTTP 403) and how ChatGPT reads video.
- How the Gemini app and Grok app sample video.
- Any AI read of exported frames, contact sheets or the slideshow video.
- Paste of a file list into the chat web pages.
- The code size (estimate only).

---

## 10. Commands for a manual export now (PowerShell 5.1)

Bran can test the method now, without app changes. Replace the file name with a real recording.

Make the output folder:

```
New-Item -ItemType Directory -Force "C:\Users\Bran\Recordings\frames_test"
```

Export the unique frames:

```
ffmpeg -i "C:\Users\Bran\Recordings\desktop_2026-11-14_21-04-05_647.mp4" -vf "mpdecimate=hi=64*4:lo=64*2:frac=0.33" -fps_mode vfr "C:\Users\Bran\Recordings\frames_test\frame_%04d.png"
```

Count the files:

```
(Get-ChildItem "C:\Users\Bran\Recordings\frames_test" -Filter *.png).Count
```
