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
        return failures;
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
        for (int i = 0; i < 120; i++) f.Feed(Page(100 + i), i / 10.0);   // new content every frame for 12 s
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
