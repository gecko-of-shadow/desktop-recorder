// Unit tests for the AI capture folder (AI capture brief, Phase 1). Called from CoreTests.Main.
using System;
using System.Collections.Generic;
using System.Globalization;

static class AiCaptureTests
{
    static int failures;

    static void Expect(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL ai: " + name);
    }

    public static int Run()
    {
        failures = 0;
        TestSettingsKeys();
        TestCaptureArgs();
        TestClipArgs();
        TestSmallHelpers();
        TestStill();
        TestSmallChangeStays();
        TestBlink();
        TestLargeChange();
        TestStillChanging();
        TestScroll();
        TestIndex();
        TestCounterStops();
        TestPendingThenScroll();
        TestScrollTurnsBack();
        TestAiMessage();
        TestVideoIsNotScroll();
        TestNoisyShiftIsNotScroll();
        TestScrollStillFound();
        TestMotionReport();
        return failures;
    }

    static void TestAiMessage()
    {
        string folder = @"C:\rec\desktop_2026-11-19_20-10-00";
        string ok = RecordingIndex.AiMessage(folder, true);
        Expect(ok.IndexOf(folder + "\r\n", StringComparison.Ordinal) >= 0, "message has the folder on its own line");
        Expect(ok.IndexOf("index.md", StringComparison.Ordinal) >= 0 && ok.IndexOf("clip.mp4", StringComparison.Ordinal) >= 0, "message names the files");
        string bad = RecordingIndex.AiMessage(folder, false);
        Expect(bad.IndexOf(folder, StringComparison.Ordinal) >= 0, "failed message has the folder");
        Expect(bad.IndexOf("failed", StringComparison.Ordinal) >= 0 && bad.IndexOf("note.txt", StringComparison.Ordinal) >= 0, "failed message");
    }

    static void TestSettingsKeys()
    {
        StoredSettings d = new StoredSettings();
        Expect(d.OutputMode == RecorderCore.OutputFolder, "default output folder");
        Expect(d.SettleMs == 400 && d.StillIntervalSeconds == 5 && d.ScrollPercent == 60 && d.SizeLimit == 1568, "defaults");
        StoredSettings p = RecorderCore.ParseSettings("output_mode=mp4\r\nsettle_ms=300\r\nstill_interval_s=7\r\nscroll_percent=200\r\nsize_limit=2000\r\n", d);
        Expect(p.OutputMode == RecorderCore.OutputMp4, "output mp4");
        Expect(p.SettleMs == 300 && p.StillIntervalSeconds == 7 && p.SizeLimit == 2000, "ints");
        Expect(p.ScrollPercent == 95, "scroll clamp");
        StoredSettings q = RecorderCore.ParseSettings(RecorderCore.SerializeSettings(p), new StoredSettings());
        Expect(q.OutputMode == RecorderCore.OutputMp4 && q.SettleMs == 300 && q.ScrollPercent == 95, "round trip");
        StoredSettings bad = RecorderCore.ParseSettings("output_mode=zip\r\n", d);
        Expect(bad.OutputMode == RecorderCore.OutputFolder, "bad output mode keeps default");
        SettledOptions o = RecorderCore.SettledOptionsOf(p);
        Expect(Math.Abs(o.SettleSeconds - 0.3) < 1e-9 && o.StillIntervalSeconds == 7 && Math.Abs(o.ScrollFraction - 0.95) < 1e-9, "options");
    }

    static List<MonitorBounds> OneMonitor()
    {
        List<MonitorBounds> m = new List<MonitorBounds>();
        m.Add(new MonitorBounds(0, 0, 0, 1920, 1080));
        return m;
    }

    static void TestCaptureArgs()
    {
        StoredSettings s = new StoredSettings();
        s.Folder = @"C:\R";
        s.Fps = 15;
        s.SplitMinutes = 30;
        s.FrameRateMode = RecorderCore.ModeChanges;
        string work = @"C:\R\desktop_x\work_lossless.mp4";
        string[] a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(10, 20, 1302, 960), OneMonitor(), new DateTime(2026, 11, 19), true, work);
        string j = string.Join(" ", a);
        Expect(a[a.Length - 1] == work, "work file output");
        Expect(j.IndexOf("-tune lossless", StringComparison.Ordinal) >= 0, "lossless tune");
        Expect(j.IndexOf("-bf 0", StringComparison.Ordinal) >= 0, "no B-frames in the work file");
        Expect(j.IndexOf("high444p", StringComparison.Ordinal) >= 0, "444 profile");
        Expect(j.IndexOf("format=yuv444p,mpdecimate", StringComparison.Ordinal) >= 0, "444 before mpdecimate");
        Expect(j.IndexOf("segment", StringComparison.Ordinal) < 0, "no split in folder mode");
        Expect(j.IndexOf("-cq", StringComparison.Ordinal) < 0, "no cq when lossless");
        Expect(j.IndexOf("frag_keyframe", StringComparison.Ordinal) >= 0, "fragmented work file");
        s.FrameRateMode = RecorderCore.ModeFixed;
        j = string.Join(" ", RecorderCore.BuildCaptureArguments(s, new CaptureRegion(10, 20, 1302, 960), OneMonitor(), DateTime.Now, true, work));
        Expect(j.IndexOf("-rgb_mode yuv444", StringComparison.Ordinal) >= 0, "rgb_mode in fixed mode");
        j = string.Join(" ", RecorderCore.BuildCaptureArguments(s, new CaptureRegion(10, 20, 1302, 960), OneMonitor(), DateTime.Now, false, work));
        Expect(j.IndexOf("-c:v libx264", StringComparison.Ordinal) >= 0 && j.IndexOf("-qp 0", StringComparison.Ordinal) >= 0
            && j.IndexOf("-pix_fmt yuv444p", StringComparison.Ordinal) >= 0, "x264 lossless fallback");
        // MP4 mode is not changed.
        string[] old = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(10, 20, 1302, 960), OneMonitor(), new DateTime(2026, 11, 13, 1, 2, 3, 127), true);
        Expect(old[old.Length - 1].EndsWith("_%03d.mp4", StringComparison.Ordinal), "mp4 mode still splits");
        Expect(RecorderCore.RecordingFolderName(new DateTime(2026, 11, 19, 13, 1, 41, 912)) == "desktop_2026-11-19_13-01-41", "folder name");
    }

    static void TestClipArgs()
    {
        StoredSettings s = new StoredSettings();
        List<double> t = new List<double>(new double[] { 0.6, 2.5333333, 0.6, 0.005 });
        string[] a = RecorderCore.BuildClipArguments(s, "w.mp4", "c.mp4", t, 15, true);
        string j = string.Join(" ", a);
        Expect(j.IndexOf("-copyts", StringComparison.Ordinal) >= 0, "copyts");
        Expect(j.IndexOf("-enc_time_base 1:90000", StringComparison.Ordinal) >= 0, "fine encoder time base");
        Expect(j.IndexOf("-g 999999", StringComparison.Ordinal) >= 0, "no periodic keyframes");
        Expect(j.IndexOf("-forced-idr 1", StringComparison.Ordinal) >= 0, "forced idr");
        Expect(j.IndexOf("-force_key_frames 0.000,0.590,2.523", StringComparison.Ordinal) >= 0, "keyframe list minus 0.01, sorted, unique: " + j);
        Expect(j.IndexOf("-c:v h264_nvenc", StringComparison.Ordinal) >= 0 && j.IndexOf("-cq 23", StringComparison.Ordinal) >= 0, "nvenc balanced");
        Expect(a[a.Length - 1] == "c.mp4", "clip output");
        j = string.Join(" ", RecorderCore.BuildClipArguments(s, "w.mp4", "c.mp4", t, 15, false));
        Expect(j.IndexOf("scenecut=0", StringComparison.Ordinal) >= 0 && j.IndexOf("-forced-idr", StringComparison.Ordinal) < 0, "x264 keyframes");
        j = string.Join(" ", RecorderCore.BuildClipArguments(s, "w.mp4", "c.mp4", null, 15, true));
        Expect(j.IndexOf("-force_key_frames", StringComparison.Ordinal) < 0 && j.IndexOf("-g 450", StringComparison.Ordinal) >= 0, "fallback clip");
    }

    static void TestSmallHelpers()
    {
        Expect(RecordingIndex.UtcOffsetText(TimeSpan.FromHours(-3)) == "UTC-03:00", "offset -3");
        Expect(RecordingIndex.UtcOffsetText(new TimeSpan(5, 30, 0)) == "UTC+05:30", "offset +5:30");
        Expect(RecordingIndex.UtcOffsetText(TimeSpan.Zero) == "UTC+00:00", "offset 0");
        Expect(RecordingIndex.FrameFileName(7, 20) == "007" && RecordingIndex.FrameFileName(7, 1200) == "0007", "file names");
        Expect(Math.Abs(RecorderCore.ParseClockTime("00:01:02.50") - 62.5) < 1e-9, "clock time");
        Expect(RecorderCore.ParseClockTime("N/A") < 0, "clock n/a");
        FfmpegProgress p;
        Expect(RecorderCore.TryParseProgress("frame=  100 fps= 10 q=23.0 size=  256KiB time=00:00:10.60 bitrate= 1kbits/s speed=1x", out p)
            && Math.Abs(p.TimeSeconds - 10.6) < 1e-9, "progress time");
        Expect(RecorderCore.TryParseProgress("frame=1 fps=0 q=0 size=0KiB time=N/A bitrate=N/A speed=N/A", out p) && p.TimeSeconds < 0, "progress no time");
        TitleTimeline tl = new TitleTimeline();
        tl.Add(0.2, "A");
        tl.Add(0.4, "A");
        tl.Add(3.0, "B");
        Expect(tl.Count == 2 && tl.At(0) == "A" && tl.At(2.9) == "A" && tl.At(3.0) == "B" && tl.At(99) == "B", "title timeline");
    }

    // ------------------------------------------------------------ synthetic frames

    const int W = 96, H = 120;

    static byte[] Page(int seed)
    {
        byte[] f = new byte[W * H * 3];
        for (int i = 0; i < f.Length; i++) f[i] = 255;
        // "Text": dark marks on some rows, a different pattern for each row.
        for (int y = 4; y < H - 4; y += 6)
            for (int x = 4; x < W - 4; x++)
                if (((x * 7 + y * 13 + seed * 31) % 11) < 4) Set(f, x, y, 20);
        return f;
    }

    static void Set(byte[] f, int x, int y, byte v)
    {
        int i = (y * W + x) * 3;
        f[i] = v; f[i + 1] = v; f[i + 2] = v;
    }

    static void Fill(byte[] f, int x0, int y0, int w, int h, byte v)
    {
        for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) Set(f, x, y, v);
    }

    static SettledFrameFinder Finder()
    {
        return new SettledFrameFinder(W, H, new SettledOptions());
    }

    static void TestStill()
    {
        SettledFrameFinder f = Finder();
        byte[] p = Page(1);
        for (int i = 0; i < 50; i++) f.Feed(p, 0.6 + i / 10.0);
        f.Finish(5.6);
        Expect(f.Written.Count == 1 && f.Written[0].Kind == FrameKind.First && Math.Abs(f.Written[0].Time - 0.6) < 1e-9, "still page: 1 frame at the first time");
    }

    static void TestSmallChangeStays()
    {
        SettledFrameFinder f = Finder();
        byte[] a = Page(1);
        byte[] b = Page(1);
        Fill(b, 40, 50, 5, 7, 0);          // one "character" changes and stays
        for (int i = 0; i < 100; i++) f.Feed(i < 30 ? a : b, i / 10.0);
        f.Finish(10);
        Expect(f.Written.Count == 2, "small change that stays: 2 frames, got " + f.Written.Count);
        if (f.Written.Count == 2)
        {
            Expect(Math.Abs(f.Written[1].Time - 3.0) < 1e-9, "small change time");
            Expect(f.Written[1].BoxWidth > 0 && f.Written[1].BoxWidth <= 16, "small change box");
        }
    }

    static void TestBlink()
    {
        SettledFrameFinder f = Finder();
        byte[] on = Page(2);
        byte[] off = Page(2);
        Fill(on, 60, 90, 2, 12, 0);        // caret
        Fill(off, 60, 90, 2, 12, 255);
        for (int i = 0; i < 300; i++)
        {
            double t = i / 15.0;
            f.Feed(((int)(t * 1000) / 530) % 2 == 0 ? on : off, t);
        }
        f.Finish(20);
        Expect(f.Written.Count == 1, "blinking caret: 1 frame, got " + f.Written.Count);
    }

    static void TestLargeChange()
    {
        SettledFrameFinder f = Finder();
        byte[] a = Page(3), b = Page(9);
        Fill(b, 0, 0, W, H / 2, 90);
        for (int i = 0; i < 60; i++) f.Feed(i < 20 ? a : b, i / 10.0);
        f.Finish(6);
        Expect(f.Written.Count == 2 && f.Written[1].ChangedFraction >= 0.3, "large change: 2 frames");
    }

    static void TestStillChanging()
    {
        SettledFrameFinder f = Finder();
        // New content every frame for 12 s. Not Page(100 + i): Page(seed + 1) is Page(seed) moved up by 12 rows,
        // so that sequence is a real scroll (checked on 2026-11-20, all 28800 bytes of rows 16 to 115 the same).
        for (int i = 0; i < 120; i++) f.Feed(VideoFrame(i / 10.0), i / 10.0);
        f.Finish(12);
        int still = 0;
        foreach (SettledFrame w in f.Written) if (w.Kind == FrameKind.StillChanging) still++;
        Expect(still == 2, "still changing every 5 s: 2 frames, got " + still);
        Expect(f.Written[f.Written.Count - 1].Kind == FrameKind.Last, "last frame kept");
    }

    static void TestScroll()
    {
        // A tall page, seen through a view of H rows, scrolled 8 px each frame (no static rows).
        int tall = H * 6;
        byte[] content = new byte[W * tall * 3];
        for (int i = 0; i < content.Length; i++) content[i] = 255;
        for (int y = 2; y < tall - 2; y += 5)
        {
            int len = 10 + (y * 37 % 70);
            for (int x = 4; x < 4 + len && x < W - 2; x++)
                if (((x + y * 3) % 7) < 5) { int i = (y * W + x) * 3; content[i] = 0; content[i + 1] = 0; content[i + 2] = 0; }
        }
        SettledFrameFinder f = Finder();
        int pos = 0, step = 8, frames = (tall - H) / step;
        for (int k = 0; k <= frames; k++)
        {
            byte[] view = new byte[W * H * 3];
            Buffer.BlockCopy(content, pos * W * 3, view, 0, view.Length);
            f.Feed(view, k / 15.0);
            if (k < frames) pos += step;
        }
        f.Finish(frames / 15.0 + 1);
        Expect(Math.Abs(f.TotalScroll - pos) <= 2, "scroll measured " + f.TotalScroll + " of " + pos);
        long last = long.MinValue;
        bool gap = false;
        int parts = 0;
        foreach (SettledFrame w in f.Written)
        {
            if (last != long.MinValue && w.ContentOffset - last > H) gap = true;
            last = w.ContentOffset;
            if (w.Kind == FrameKind.ScrollPart) parts++;
        }
        Expect(!gap, "scroll: no gap between written frames");
        Expect(parts >= (pos / H), "scroll parts written: " + parts);
    }

    // Review 2026-11-19, finding 1: a value that changes 3 times fast and then stays must be in a frame.
    static void TestCounterStops()
    {
        SettledFrameFinder f = Finder();
        byte[][] v = new byte[4][];
        for (int k = 0; k < 4; k++) { v[k] = Page(4); Fill(v[k], 40, 50, 6, 8, 0); Fill(v[k], 41 + k, 51, 2, 6, 255); }
        // 0 until 2 s, then 1, 2, 3 every 0.5 s, then 3 stays until 12 s.
        for (int i = 0; i < 180; i++)
        {
            double t = i / 15.0;
            int k = t < 2 ? 0 : t < 2.5 ? 1 : t < 3 ? 2 : 3;
            f.Feed(v[k], t);
        }
        f.Finish(12);
        SettledFrame last = f.Written[f.Written.Count - 1];
        // The last written frame must show value 3.
        bool shows3 = last.Pixels == null || (last.Pixels[((51 * W) + 44) * 3] == 255 && last.Pixels[((51 * W) + 41) * 3] == 0);
        Expect(shows3, "final counter value written, frames " + f.Written.Count);
    }

    static byte[] TallContent(int tall)
    {
        byte[] content = new byte[W * tall * 3];
        for (int i = 0; i < content.Length; i++) content[i] = 255;
        for (int y = 2; y < tall - 2; y += 5)
        {
            int len = 10 + (y * 37 % 70);
            for (int x = 4; x < 4 + len && x < W - 2; x++)
                if (((x + y * 3) % 7) < 5) { int i = (y * W + x) * 3; content[i] = 0; content[i + 1] = 0; content[i + 2] = 0; }
        }
        return content;
    }

    static byte[] View(byte[] content, int pos)
    {
        byte[] view = new byte[W * H * 3];
        Buffer.BlockCopy(content, pos * W * 3, view, 0, view.Length);
        return view;
    }

    static bool NoGap(SettledFrameFinder f, long from, long to)
    {
        List<long> offs = new List<long>();
        foreach (SettledFrame w in f.Written) offs.Add(w.ContentOffset);
        offs.Sort();
        long covered = from;
        foreach (long o in offs) { if (o > covered) return false; covered = Math.Max(covered, o + H); }
        return covered >= to;
    }

    // Finding 2: a pending small change that is written during a scroll must not reset the scroll count.
    static void TestPendingThenScroll()
    {
        byte[] content = TallContent(H * 8);
        byte[] first = View(content, 0);
        byte[] changed = View(content, 0);
        Fill(changed, 80, 10, 5, 7, 0);
        SettledFrameFinder f = Finder();
        int pos = 0;
        int i = 0;
        for (; i < 15; i++) f.Feed(first, i / 15.0);           // 1 s still
        for (; i < 22; i++) f.Feed(changed, i / 15.0);         // small change settles, becomes pending
        // Scroll 6 px a frame from now on: the pending deadline comes during the scroll.
        int frames = 0;
        while (pos + 6 <= H * 7)
        {
            pos += 6;
            byte[] v = View(content, pos);
            Fill(v, 80, 10, 5, 7, 0);
            f.Feed(v, i / 15.0);
            i++;
            frames++;
        }
        f.Finish(i / 15.0 + 1);
        Expect(NoGap(f, 0, pos + H), "pending then scroll: no gap");
    }

    // Finding 6: a scroll that turns back without a pause keeps the furthest view.
    static void TestScrollTurnsBack()
    {
        byte[] content = TallContent(H * 4);
        SettledFrameFinder f = Finder();
        int i = 0, pos = 0;
        f.Feed(View(content, 0), 0);
        for (int k = 0; k < 9; k++) { pos += 6; i++; f.Feed(View(content, pos), i / 15.0); }   // 54 px down (45%)
        for (int k = 0; k < 9; k++) { pos -= 6; i++; f.Feed(View(content, pos), i / 15.0); }   // back up
        f.Finish(i / 15.0 + 1);
        Expect(NoGap(f, 0, 54 + H), "scroll turns back: furthest view written");
    }

    // ------------------------------------------------------------ task 2 (Bran's Twitch recording, 2026-11-20)

    // "Video": smooth color waves and a moving box, new content in each frame. Not a scroll.
    static byte[] VideoFrame(double t)
    {
        byte[] f = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double a = Math.Sin(x / 7.0 + t * 3.1) + Math.Cos(y / 5.0 - t * 2.3) + Math.Sin((x + y) / 9.0 + t * 1.7);
                int v = (int)((a + 3) / 6 * 255);
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                int i = (y * W + x) * 3;
                f[i] = (byte)v; f[i + 1] = (byte)((v * 3) & 255); f[i + 2] = (byte)(255 - v);
            }
        int cx = (int)((Math.Sin(t * 1.3) * 0.4 + 0.5) * W), cy = (int)((Math.Cos(t * 0.9) * 0.4 + 0.5) * H);
        for (int y = Math.Max(0, cy - 10); y < Math.Min(H, cy + 10); y++)
            for (int x = Math.Max(0, cx - 14); x < Math.Min(W, cx + 14); x++) Set(f, x, y, 240);
        return f;
    }

    static int ScrollFrames(SettledFrameFinder f)
    {
        int n = 0;
        foreach (SettledFrame w in f.Written) if (w.ScrollPixels != 0) n++;
        return n;
    }

    // Frame 006 of the Twitch recording was "scroll, 573 px up (still scrolling)": video motion, not a scroll.
    static void TestVideoIsNotScroll()
    {
        SettledFrameFinder f = Finder();
        for (int i = 0; i < 12 * 15; i++) f.Feed(VideoFrame(i / 15.0), i / 15.0);
        f.Finish(12);
        Expect(f.TotalScroll == 0 && ScrollFrames(f) == 0, "video is not a scroll: total " + f.TotalScroll + ", scroll frames " + ScrollFrames(f));
    }

    // A shift that lowers the pixel error but does not match well: the content moved, and noise of up to
    // +-48 changed every pixel (video). The old rule (error below 75% of no shift) took it as a scroll.
    static void TestNoisyShiftIsNotScroll()
    {
        byte[] content = TallContent(H * 3);
        Random rnd = new Random(5);
        SettledFrameFinder f = Finder();
        int pos = 0;
        for (int i = 0; i < 40; i++)
        {
            byte[] v = View(content, pos);
            for (int k = 0; k < v.Length; k += 3)
            {
                int n = rnd.Next(-48, 49);
                for (int c = 0; c < 3; c++) { int x = v[k + c] + n; v[k + c] = (byte)(x < 0 ? 0 : x > 255 ? 255 : x); }
            }
            f.Feed(v, i / 15.0);
            pos += 4;
        }
        f.Finish(40 / 15.0 + 1);
        Expect(f.RejectedShifts > 0, "noisy shift: the match check ran (rejected " + f.RejectedShifts + ")");
        Expect(f.TotalScroll == 0, "noisy shift is not a scroll: total " + f.TotalScroll);
    }

    // The match check must keep a real scroll, also with a static header and a static column at the left.
    // Lines of random length and pattern. TallContent repeats every 70 rows, so a step of 7 px and a step of
    // -63 px look the same in it.
    static byte[] RandomContent(int tall, int seed)
    {
        Random r = new Random(seed);
        byte[] content = new byte[W * tall * 3];
        for (int i = 0; i < content.Length; i++) content[i] = 255;
        for (int y = 2; y < tall - 10; y += 5 + r.Next(5))
        {
            int len = 8 + r.Next(W - 14);
            for (int x = 4; x < 4 + len && x < W - 2; x++)
                if (r.Next(10) < 6)
                    for (int dy = 0; dy < 2; dy++) { int i = ((y + dy) * W + x) * 3; content[i] = 0; content[i + 1] = 0; content[i + 2] = 0; }
        }
        return content;
    }

    static void TestScrollStillFound()
    {
        byte[] content = RandomContent(H * 6, 3);
        byte[] header = RandomContent(H, 11);
        Fill(header, 0, 0, W, 16, 60);
        for (int x = 4; x < 60; x += 3) Fill(header, x, 5, 2, 6, 230);   // header "text"
        SettledFrameFinder f = Finder();
        int pos = 0, frames = 60;
        for (int k = 0; k <= frames; k++)
        {
            byte[] v = View(content, pos);
            Buffer.BlockCopy(header, 0, v, 0, W * 3 * 16);                   // static header, 16 rows
            Fill(v, 0, 16, 20, H - 16, 200);                                   // static column, 20 px, no text
            // Known limit (HANDOFF 7.7): a static column WITH text lowers the error ratio of VerifiedShift, because
            // PixelError compares the full width. The old and the new rule then both miss some steps.
            f.Feed(v, k / 15.0);
            if (k < frames) pos += 7;
        }
        f.Finish(frames / 15.0 + 1);
        Expect(Math.Abs(f.TotalScroll - pos) <= 2 && f.RejectedShifts == 0,
            "scroll with header and column: measured " + f.TotalScroll + " of " + pos + ", rejected " + f.RejectedShifts);
    }

    static void TestMotionReport()
    {
        SettledFrameFinder video = Finder();
        for (int i = 0; i < 12 * 15; i++) video.Feed(VideoFrame(i / 15.0), i / 15.0);
        video.Finish(12);
        string note = video.MotionReport(12);
        Expect(note != null && note.IndexOf("% of the recording", StringComparison.Ordinal) >= 0
            && note.IndexOf("clip.mp4", StringComparison.Ordinal) >= 0, "motion note for video: " + note);
        // A still page with one change: no note.
        SettledFrameFinder still = Finder();
        byte[] a = Page(1), b = Page(1);
        Fill(b, 40, 50, 5, 7, 0);
        for (int i = 0; i < 150; i++) still.Feed(i < 30 ? a : b, i / 15.0);
        still.Finish(10);
        Expect(still.MotionReport(10) == null, "no motion note for a still page");
        // A short video (under 8 s): no note.
        SettledFrameFinder brief = Finder();
        for (int i = 0; i < 5 * 15; i++) brief.Feed(VideoFrame(i / 15.0), i / 15.0);
        brief.Finish(5);
        Expect(brief.MotionReport(5) == null, "no motion note under 8 s");
        // "Changes only" mode: a still screen keeps 1 frame each second. Gaps of 1 s are not motion.
        SettledFrameFinder sparse = Finder();
        for (int i = 0; i < 20; i++) sparse.Feed(VideoFrame(i * 0.37), i);
        sparse.Finish(20);
        Expect(sparse.MotionReport(20) == null, "1 s gaps are not motion");
        // A long scroll is not "moving content": the scroll rule writes its frames.
        byte[] content = TallContent(H * 8);
        SettledFrameFinder scroll = Finder();
        int pos = 0;
        for (int k = 0; k < 150; k++) { scroll.Feed(View(content, pos), k / 15.0); pos += 4; }
        scroll.Finish(10);
        Expect(scroll.MotionReport(10) == null, "no motion note for a scroll (moving " + scroll.MovingSeconds.ToString("0.0") + " s)");
    }

    static void TestIndex()
    {
        List<SettledFrame> frames = new List<SettledFrame>();
        for (int i = 1; i <= 21; i++)
        {
            SettledFrame s = new SettledFrame();
            s.Number = i;
            s.Time = 0.6 + i;
            s.Kind = i == 1 ? FrameKind.First : FrameKind.Settled;
            s.ChangedFraction = 0.01;
            s.BoxX = 10; s.BoxY = 20; s.BoxWidth = 30; s.BoxHeight = 12;
            frames.Add(s);
        }
        TitleTimeline t = new TitleTimeline();
        t.Add(0, "Grok Bot");
        t.Add(5, "Grok Bot | Settings");
        string md = RecordingIndex.Build(new DateTime(2026, 11, 19, 13, 1, 41), TimeSpan.FromHours(-3), "look at usage by bot",
            119.5, 1302, 960, frames, t, 1568, null);
        Expect(md.StartsWith("# Recording 2026-11-19 13:01:41 (UTC-03:00)\n", StringComparison.Ordinal), "header");
        Expect(md.IndexOf("Note from Bran: look at usage by bot\n", StringComparison.Ordinal) >= 0, "note");
        Expect(md.IndexOf("Length: 119.5 s\n", StringComparison.Ordinal) >= 0 && md.IndexOf("Region: 1302 x 960\n", StringComparison.Ordinal) >= 0, "length and region");
        Expect(md.IndexOf("Frames: 21\n", StringComparison.Ordinal) >= 0, "frame count");
        Expect(md.IndexOf("Warning: more than 20 frames", StringComparison.Ordinal) >= 0, "20 frame warning");
        Expect(md.IndexOf("more than 1568 px", StringComparison.Ordinal) < 0, "no size warning at 1302");
        Expect(md.IndexOf("| 001 | 1.6 s | Grok Bot | first frame |", StringComparison.Ordinal) >= 0, "first row");
        Expect(md.IndexOf("| 005 | 5.6 s | Grok Bot \\| Settings | new window content |", StringComparison.Ordinal) >= 0, "title change row and pipe escape");
        Expect(md.IndexOf("| 006 | 6.6 s | Grok Bot \\| Settings | small change at x 10, y 20 (30 x 12 px) |", StringComparison.Ordinal) >= 0, "small change row");
        // Titles are on the stats clock (0 at the first frame). With a first frame at 0.6 s, frame 5 (5.6 s) is
        // 5.0 s after the first frame, so it gets the second title only with the shift.
        string shifted = RecordingIndex.Build(new DateTime(2026, 11, 19, 13, 1, 41), TimeSpan.FromHours(-3), null,
            119.5, 1302, 960, frames, t, 1568, null, 0.6);
        Expect(shifted.IndexOf("| 005 | 5.6 s | Grok Bot \\| Settings |", StringComparison.Ordinal) >= 0
            && shifted.IndexOf("| 004 | 4.6 s | Grok Bot |", StringComparison.Ordinal) >= 0, "title shift by the first frame time");
        string big = RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 1920, 1080, frames.GetRange(0, 2), t, 1568, null);
        Expect(big.IndexOf("more than 1568 px", StringComparison.Ordinal) >= 0, "size warning at 1920");
        Expect(big.IndexOf("Note from Bran:", StringComparison.Ordinal) < 0, "no note line");
    }
}
