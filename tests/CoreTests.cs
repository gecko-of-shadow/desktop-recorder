// Standalone checks for the recorder core. No test framework: mono tests/CoreTests.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

static class CoreTests
{
    static int failures;

    static void Expect(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL " + name);
    }

    static void ExpectEqual(string actual, string expected, string name)
    {
        if (actual == expected) return;
        failures++;
        Console.Error.WriteLine("FAIL " + name);
        Console.Error.WriteLine("  actual:   " + actual);
        Console.Error.WriteLine("  expected: " + expected);
    }

    static int Main()
    {
        TestClamp();
        TestHotkey();
        TestSettings();
        TestArguments();
        TestBackendSettings();
        TestLastRegionHotkey();
        TestRegion();
        TestPlan();
        TestNvencSizeLimits();
        TestDxgiMatch();
        TestFallback();
        TestSelectorOptions();
        TestCaptureArguments();
        TestNvencProbe();
        TestProgressParser();
        TestQuoting();
        TestFindFfmpeg();
        TestWav();
        TestAtomicWrite();
        TestSession();
        if (failures > 0)
        {
            Console.Error.WriteLine(failures + " failed");
            return 1;
        }
        Console.WriteLine("ok");
        return 0;
    }

    static void TestClamp()
    {
        Expect(RecorderCore.ClampFps(double.NaN) == 2, "nan fps");
        Expect(RecorderCore.ClampFps(double.PositiveInfinity) == 2, "inf fps");
        Expect(RecorderCore.ClampFps(double.NegativeInfinity) == 2, "neg inf fps");
        Expect(RecorderCore.ClampFps(-4) == 0.2, "negative fps");
        Expect(RecorderCore.ClampFps(0) == 0.2, "zero fps");
        Expect(RecorderCore.ClampFps(0.2) == 0.2, "min fps");
        Expect(RecorderCore.ClampFps(2) == 2, "default fps");
        Expect(RecorderCore.ClampFps(60) == 60, "max fps");
        Expect(RecorderCore.ClampFps(61) == 60, "too fast");
        Expect(RecorderCore.ClampQuality(-3) == 0, "quality low");
        Expect(RecorderCore.ClampQuality(9) == 2, "quality high");
        Expect(RecorderCore.ClampSplitMinutes(-1) == 0, "split low");
        Expect(RecorderCore.ClampSplitMinutes(2000) == 1440, "split high");
    }

    static void TestHotkey()
    {
        int mods = 0;
        int key = 0;
        Expect(RecorderCore.TrySetHotkey("Win+Shift+D", ref mods, ref key), "parse default hotkey");
        Expect(mods == (8 | 4) && key == (int)'D', "default mods and key");
        ExpectEqual(RecorderCore.HotkeyText(mods, key), "Win+Shift+D", "hotkey text");
        Expect(!RecorderCore.TrySetHotkey("Shift+A", ref mods, ref key), "shift letter rejected");
        Expect(mods == (8 | 4) && key == (int)'D', "rejected hotkey keeps previous");
        Expect(!RecorderCore.TrySetHotkey("Ctrl+13", ref mods, ref key), "numeric enum rejected");
        Expect(!RecorderCore.TrySetHotkey("Ctrl+Enter", ref mods, ref key), "enter rejected");
        Expect(!RecorderCore.TrySetHotkey("Ctrl+Control", ref mods, ref key), "modifier as key rejected");
        Expect(RecorderCore.TrySetHotkey("Ctrl+5", ref mods, ref key), "digit");
        Expect(key == (int)'5', "digit virtual key");
        ExpectEqual(RecorderCore.KeyName(key), "5", "digit name");
        Expect(RecorderCore.TrySetHotkey("Ctrl+D5", ref mods, ref key) && key == (int)'5', "D5 alias");
        Expect(RecorderCore.TrySetHotkey("Shift+F1", ref mods, ref key), "shift function");
        Expect(key == 0x70, "F1 virtual key");
        Expect(RecorderCore.TrySetHotkey("Shift+F24", ref mods, ref key), "F24");
        Expect(!RecorderCore.TrySetHotkey("Shift+F25", ref mods, ref key), "F25 rejected");
        Expect(!RecorderCore.TrySetHotkey("A", ref mods, ref key), "bare letter rejected");
        Expect(!RecorderCore.TrySetHotkey(null, ref mods, ref key), "null hotkey");
        ExpectEqual(RecorderCore.KeyName((int)'A'), "A", "letter name");
        ExpectEqual(RecorderCore.KeyName(0x70), "F1", "F1 name");
    }

    static void TestSettings()
    {
        StoredSettings defaults = new StoredSettings();
        defaults.Folder = @"C:\Users\me\Recordings";
        string text = "fps=NaN\r\nquality=9\r\nsplit_minutes=-5\r\nhotkey=Shift+A\r\nrecord_audio=1\r\nwayland_restore_token=abc\r\n";
        StoredSettings parsed = RecorderCore.ParseSettings(text, defaults);
        Expect(parsed.Fps == 2, "nan in file becomes default");
        Expect(parsed.Quality == 2, "quality clamped on load");
        Expect(parsed.SplitMinutes == 0, "split clamped on load");
        Expect(parsed.HotkeyModifiers == (8 | 4) && parsed.HotkeyKey == (int)'D', "bad hotkey keeps default");
        Expect(parsed.Folder == defaults.Folder, "missing folder keeps default");
        Expect(parsed.ExtraLines.Count == 2, "two linux lines kept");
        string saved = RecorderCore.SerializeSettings(parsed);
        Expect(saved.Contains("record_audio=1"), "audio line saved");
        Expect(saved.Contains("wayland_restore_token=abc"), "token line saved");
        Expect(saved.Contains("fps=2\r\n"), "fps serialized");
        Expect(saved.Contains("hotkey=Win+Shift+D\r\n"), "hotkey serialized");
        StoredSettings again = RecorderCore.ParseSettings(saved, defaults);
        Expect(again.ExtraLines.Count == 2, "extras survive a second load");
        Expect(again.CopyToClipboard && again.PlaySounds && again.RecordOnLaunch, "bool defaults when omitted");

        StoredSettings off = RecorderCore.ParseSettings("record_on_launch=0\r\ncopy_to_clipboard=0\r\nplay_sounds=0\r\nfps=2.5\r\n", defaults);
        Expect(!off.RecordOnLaunch && !off.CopyToClipboard && !off.PlaySounds, "explicit off");
        Expect(off.Fps == 2.5, "fractional fps");
    }

    static void TestArguments()
    {
        StoredSettings s = new StoredSettings();
        s.Folder = @"C:\Users\me\Recordings";
        s.Fps = 2;
        s.Quality = 1;
        s.SplitMinutes = 30;
        DateTime now = new DateTime(2026, 11, 13, 1, 2, 3, 127);
        string[] args = RecorderCore.BuildWindowsArguments(s, now);
        string joined = RecorderCore.JoinWindowsArguments(args);
        Expect(IndexOf(args, "gdigrab") >= 0, "gdigrab");
        Expect(IndexOf(args, "x11grab") < 0, "windows stays off x11grab");
        Expect(IndexOf(args, "pulse") < 0, "windows stays silent");
        Expect(IndexOf(args, "pipewire") < 0, "windows stays off pipewire");
        Expect(args[IndexOf(args, "-crf") + 1] == "27", "balanced crf");
        Expect(args[IndexOf(args, "-g") + 1] == "60", "gop");
        Expect(args[IndexOf(args, "-threads") + 1] == "2", "threads");
        Expect(args[IndexOf(args, "-x264-params") + 1] == "rc-lookahead=4", "lookahead");
        Expect(args[IndexOf(args, "-segment_time") + 1] == "1800", "split seconds");
        Expect(args[IndexOf(args, "-force_key_frames") + 1] == "expr:gte(t,n_forced*1800)", "forced keyframes");
        Expect(args[args.Length - 1] == @"C:\Users\me\Recordings\desktop_2026-11-13_01-02-03_127_%03d.mp4", "segment name");
        Expect(!joined.Contains(" -strftime "), "no strftime");

        s.Folder = @"C:\100%done";
        s.SplitMinutes = 1;
        args = RecorderCore.BuildWindowsArguments(s, now);
        Expect(args[args.Length - 1] == @"C:\100%%done\desktop_2026-11-13_01-02-03_127_%03d.mp4", "percent escaped");

        s.Folder = @"C:\100%done";
        s.SplitMinutes = 0;
        s.Quality = 0;
        s.Fps = double.NaN;
        args = RecorderCore.BuildWindowsArguments(s, now);
        Expect(args[IndexOf(args, "-crf") + 1] == "32", "smallest crf");
        Expect(args[IndexOf(args, "-framerate") + 1] == "2", "nan fps argument");
        Expect(args[args.Length - 1] == @"C:\100%done\desktop_2026-11-13_01-02-03_127.mp4", "percent literal when not splitting");
        Expect(IndexOf(args, "-movflags") >= 0, "single file movflags");
        Expect(IndexOf(args, "-force_key_frames") < 0, "no forced keyframes without split");

        s.Fps = 0.2;
        s.Quality = 2;
        s.SplitMinutes = 0;
        s.Folder = @"C:\Users\me\Recordings";
        args = RecorderCore.BuildWindowsArguments(s, now);
        Expect(args[IndexOf(args, "-crf") + 1] == "21", "sharp crf");
        Expect(args[IndexOf(args, "-framerate") + 1] == "0.2", "slow fps text");
        Expect(args[IndexOf(args, "-threads") + 1] == "1", "slow threads");
        Expect(args[IndexOf(args, "-g") + 1] == "6", "slow gop");

        s.Folder = @"C:\My Videos";
        s.Fps = 2;
        s.SplitMinutes = 0;
        string path = RecorderCore.JoinWindowsArguments(RecorderCore.BuildWindowsArguments(s, now));
        Expect(path.EndsWith("\"C:\\My Videos\\desktop_2026-11-13_01-02-03_127.mp4\"", StringComparison.Ordinal), "quoted spaced path");

        // The old entry point stays on gdigrab + libx264, now with preset veryfast and stats on.
        s = new StoredSettings();
        s.Folder = @"C:\R";
        args = RecorderCore.BuildWindowsArguments(s, now);
        Expect(args[IndexOf(args, "-preset") + 1] == "veryfast", "wrapper veryfast");
        Expect(IndexOf(args, "h264_nvenc") < 0, "wrapper no nvenc");
        Expect(IndexOf(args, "lavfi") < 0, "wrapper no ddagrab");
        Expect(IndexOf(args, "-stats") >= 0 && IndexOf(args, "-nostats") < 0, "wrapper stats on");
        Expect(args[IndexOf(args, "-vf") + 1] == "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0", "wrapper even crop");
        s.RegionX = -100;
        s.RegionY = 50;
        s.RegionWidth = 641;
        s.RegionHeight = 481;
        args = RecorderCore.BuildWindowsArguments(s, now);
        Expect(args[IndexOf(args, "-offset_x") + 1] == "-100", "wrapper region x");
        Expect(args[IndexOf(args, "-video_size") + 1] == "640x480", "wrapper region even");
    }

    static StoredSettings Base()
    {
        StoredSettings s = new StoredSettings();
        s.Folder = @"C:\R";
        s.Fps = 10;
        s.Quality = 1;
        s.SplitMinutes = 0;
        return s;
    }

    static List<MonitorBounds> OneMonitor()
    {
        List<MonitorBounds> list = new List<MonitorBounds>();
        list.Add(new MonitorBounds(0, 0, 0, 1920, 1080));
        return list;
    }

    static List<MonitorBounds> TwoMonitors()
    {
        // Second monitor on the left of the primary one, as DXGI output 1.
        List<MonitorBounds> list = new List<MonitorBounds>();
        list.Add(new MonitorBounds(0, 0, 0, 1920, 1080));
        list.Add(new MonitorBounds(1, -2560, -200, 2560, 1440));
        return list;
    }

    static string Joined(string[] args)
    {
        return " " + string.Join(" ", args) + " ";
    }

    static void TestLastRegionHotkey()
    {
        StoredSettings d = new StoredSettings();
        Expect(d.LastRegionHotkeyModifiers == (1 | 2 | 4) && d.LastRegionHotkeyKey == (int)'R', "default last region hotkey");

        // An old file without the key keeps the default.
        StoredSettings o = RecorderCore.ParseSettings("hotkey=Win+Shift+D\r\n", d);
        Expect(o.LastRegionHotkeyModifiers == (1 | 2 | 4) && o.LastRegionHotkeyKey == (int)'R', "old file last region hotkey default");
        Expect(o.ExtraLines.Count == 0, "last region hotkey no extras");
        Expect(RecorderCore.SerializeSettings(o).Contains("last_region_hotkey=Ctrl+Alt+Shift+R\r\n"), "last region hotkey serialized");

        // Round trip of a changed value.
        StoredSettings c = RecorderCore.ParseSettings("last_region_hotkey=Win+F9\r\n", d);
        Expect(c.LastRegionHotkeyModifiers == 8 && c.LastRegionHotkeyKey == 0x78, "last region hotkey parsed");
        StoredSettings again = RecorderCore.ParseSettings(RecorderCore.SerializeSettings(c), d);
        Expect(again.LastRegionHotkeyModifiers == 8 && again.LastRegionHotkeyKey == 0x78, "last region hotkey round trip");
        Expect(again.HotkeyModifiers == (8 | 4) && again.HotkeyKey == (int)'D', "first hotkey unchanged");

        // A bad value keeps the value from before.
        StoredSettings bad = RecorderCore.ParseSettings("last_region_hotkey=Shift+A\r\n", d);
        Expect(bad.LastRegionHotkeyModifiers == (1 | 2 | 4) && bad.LastRegionHotkeyKey == (int)'R', "bad last region hotkey keeps default");
    }

    static void TestBackendSettings()
    {
        StoredSettings d = new StoredSettings();
        Expect(d.CaptureBackend == "ddagrab", "default backend");
        Expect(d.Encoder == "auto", "default encoder");
        Expect(d.ChromaFormat == "yuv420", "default chroma");
        Expect(d.FrameRateMode == "fixed", "default mode");
        Expect(d.NvencCq == 23, "default cq");
        Expect(RecorderCore.RegionOf(d).IsEmpty && RecorderCore.LastRegionOf(d).IsEmpty, "default no region");

        // An old settings file (before this change) loads with the new defaults.
        string old = "folder=C:\\Rec\r\nfps=4\r\nquality=0\r\nsplit_minutes=10\r\nrecord_on_launch=0\r\ncopy_to_clipboard=1\r\nplay_sounds=1\r\nhotkey=Win+Shift+D\r\n";
        StoredSettings o = RecorderCore.ParseSettings(old, d);
        Expect(o.Folder == "C:\\Rec" && o.Fps == 4 && o.Quality == 0 && o.SplitMinutes == 10 && !o.RecordOnLaunch, "old file values");
        Expect(o.CaptureBackend == "ddagrab" && o.Encoder == "auto" && o.ChromaFormat == "yuv420"
            && o.FrameRateMode == "fixed" && o.NvencCq == 23, "old file new defaults");
        Expect(RecorderCore.RegionOf(o).IsEmpty, "old file no region");
        Expect(o.ExtraLines.Count == 0, "old file no extras");

        // Round trip.
        StoredSettings s = new StoredSettings();
        s.Folder = @"C:\R";
        s.CaptureBackend = "gdigrab";
        s.Encoder = "libx264";
        s.ChromaFormat = "yuv444";
        s.FrameRateMode = "changes";
        s.NvencCq = 30;
        s.RegionX = -1920;
        s.RegionY = 108;
        s.RegionWidth = 1200;
        s.RegionHeight = 700;
        s.LastRegionX = 10;
        s.LastRegionY = 20;
        s.LastRegionWidth = 300;
        s.LastRegionHeight = 400;
        s.ExtraLines.Add("record_audio=1");
        string text = RecorderCore.SerializeSettings(s);
        Expect(text.Contains("capture_backend=gdigrab\r\n"), "backend saved");
        Expect(text.Contains("region=-1920,108,1200,700\r\n"), "region saved");
        Expect(text.Contains("last_region=10,20,300,400\r\n"), "last region saved");
        StoredSettings r = RecorderCore.ParseSettings(text, new StoredSettings());
        Expect(r.CaptureBackend == "gdigrab" && r.Encoder == "libx264" && r.ChromaFormat == "yuv444"
            && r.FrameRateMode == "changes" && r.NvencCq == 30, "round trip options");
        Expect(r.RegionX == -1920 && r.RegionY == 108 && r.RegionWidth == 1200 && r.RegionHeight == 700, "round trip region");
        Expect(r.LastRegionX == 10 && r.LastRegionY == 20 && r.LastRegionWidth == 300 && r.LastRegionHeight == 400, "round trip last region");
        Expect(r.ExtraLines.Count == 1, "round trip extras");
        ExpectEqual(RecorderCore.SerializeSettings(r), text, "second save identical");

        // Bad values keep the defaults. Bad lines of our keys are not kept as extras.
        string bad = "capture_backend=dxgi\r\nencoder=hevc\r\nchroma=yuv422\r\nframe_rate_mode=smart\r\nnvenc_cq=abc\r\nregion=1,2,3\r\nlast_region=a,b,c,d\r\n";
        StoredSettings b = RecorderCore.ParseSettings(bad, d);
        Expect(b.CaptureBackend == "ddagrab" && b.Encoder == "auto" && b.ChromaFormat == "yuv420"
            && b.FrameRateMode == "fixed" && b.NvencCq == 23, "bad values keep defaults");
        Expect(RecorderCore.RegionOf(b).IsEmpty && RecorderCore.LastRegionOf(b).IsEmpty, "bad region empty");
        Expect(b.ExtraLines.Count == 0, "bad known keys not extras");
        Expect(RecorderCore.ParseSettings("nvenc_cq=0", d).NvencCq == 1, "cq low clamp");
        Expect(RecorderCore.ParseSettings("nvenc_cq=99", d).NvencCq == 51, "cq high clamp");
        StoredSettings zero = RecorderCore.ParseSettings("region=50,60,0,400", d);
        Expect(zero.RegionX == 0 && zero.RegionY == 0 && zero.RegionWidth == 0 && zero.RegionHeight == 0, "zero width clears region");

        // Defaults passed in (as the UI does) survive a parse of a file that does not name them.
        StoredSettings custom = new StoredSettings();
        custom.CaptureBackend = "gdigrab";
        custom.RegionWidth = 100;
        custom.RegionHeight = 100;
        StoredSettings kept = RecorderCore.ParseSettings("fps=3", custom);
        Expect(kept.CaptureBackend == "gdigrab" && kept.RegionWidth == 100, "copy settings keeps new fields");
    }

    static void TestRegion()
    {
        CaptureRegion r = new CaptureRegion(3, 5, 641, 479);
        Expect(!r.IsEmpty, "region not empty");
        CaptureRegion e = r.MakeEven();
        Expect(e.X == 3 && e.Y == 5 && e.Width == 640 && e.Height == 478, "odd sizes shrink to even");
        CaptureRegion e2 = RecorderCore.MakeEven(new CaptureRegion(0, 0, 800, 600));
        Expect(e2.Width == 800 && e2.Height == 600, "even sizes unchanged");
        Expect(new CaptureRegion(0, 0, 1, 100).MakeEven().IsEmpty, "1 px wide becomes empty");
        Expect(new CaptureRegion(0, 0, 0, 100).IsEmpty, "zero width empty");
        Expect(new CaptureRegion(0, 0, 100, -4).IsEmpty, "negative height empty");
        Expect(new CaptureRegion().IsEmpty, "default empty");

        MonitorBounds m = new MonitorBounds(1, -2560, -200, 2560, 1440);
        Expect(m.Contains(new CaptureRegion(-2560, -200, 2560, 1440)), "monitor contains itself");
        Expect(m.Contains(new CaptureRegion(-100, 0, 100, 100)), "inside on the right edge");
        Expect(!m.Contains(new CaptureRegion(-100, 0, 101, 100)), "crosses the right edge");
        Expect(!m.Contains(new CaptureRegion(-2561, 0, 10, 10)), "crosses the left edge");
        Expect(!m.Contains(new CaptureRegion()), "empty region not contained");

        Expect(RecorderCore.NvencCqForQuality(23, 0) == 28, "cq smallest");
        Expect(RecorderCore.NvencCqForQuality(23, 1) == 23, "cq balanced");
        Expect(RecorderCore.NvencCqForQuality(23, 2) == 17, "cq sharpest");
        Expect(RecorderCore.NvencCqForQuality(50, 0) == 51, "cq clamp high");
        Expect(RecorderCore.NvencCqForQuality(3, 2) == 1, "cq clamp low");
        Expect(RecorderCore.ResolveEncoder("auto", true) == "h264_nvenc", "auto with nvenc");
        Expect(RecorderCore.ResolveEncoder("auto", false) == "libx264", "auto without nvenc");
        Expect(RecorderCore.ResolveEncoder(null, false) == "libx264", "null encoder");
        Expect(RecorderCore.ResolveEncoder("libx264", true) == "libx264", "forced x264");
        Expect(RecorderCore.ResolveEncoder("h264_nvenc", false) == "h264_nvenc", "forced nvenc");
    }

    static void TestPlan()
    {
        StoredSettings s = Base();
        // Region inside the primary monitor.
        CapturePlan p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 108, 1920, 912), TwoMonitors(), true);
        Expect(p.Backend == "ddagrab" && p.OutputIndex == 0 && p.OffsetX == 0 && p.OffsetY == 108
            && p.Width == 1920 && p.Height == 912 && p.Note == "", "plan primary");
        // Region inside the left monitor: offsets relative to that monitor, DXGI index from the list.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(-2000, 100, 801, 601), TwoMonitors(), true);
        Expect(p.Backend == "ddagrab" && p.OutputIndex == 1 && p.OffsetX == 560 && p.OffsetY == 300
            && p.Width == 800 && p.Height == 600, "plan left monitor");
        // Region across two monitors: gdigrab fallback with a note.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(-100, 100, 400, 300), TwoMonitors(), true);
        Expect(p.Backend == "gdigrab" && !p.WholeDesktop && p.OffsetX == -100 && p.OffsetY == 100
            && p.Width == 400 && p.Height == 300 && p.Note.Length > 0, "plan across monitors falls back");
        // Region outside every monitor.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(5000, 0, 100, 100), TwoMonitors(), true);
        Expect(p.Backend == "gdigrab" && p.Note.Length > 0, "plan outside falls back");
        // No region, one monitor: ddagrab on that monitor.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), OneMonitor(), true);
        Expect(p.Backend == "ddagrab" && p.Width == 1920 && p.Height == 1080 && p.OffsetX == 0, "plan whole single monitor");
        // No region, odd monitor size.
        List<MonitorBounds> odd = new List<MonitorBounds>();
        odd.Add(new MonitorBounds(0, 0, 0, 1365, 767));
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), odd, true);
        Expect(p.Width == 1364 && p.Height == 766, "plan odd monitor even");
        // No region, two monitors: gdigrab whole desktop.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), TwoMonitors(), true);
        Expect(p.Backend == "gdigrab" && p.WholeDesktop && p.Note.Length > 0, "plan whole multi monitor falls back");
        // No monitor list.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 0, 100, 100), null, true);
        Expect(p.Backend == "gdigrab" && !p.WholeDesktop, "plan null monitors falls back");
        // gdigrab chosen: never ddagrab, no note.
        s.CaptureBackend = "gdigrab";
        p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 0, 400, 300), OneMonitor(), true);
        Expect(p.Backend == "gdigrab" && p.Note == "", "plan gdigrab setting");
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), OneMonitor(), false);
        Expect(p.WholeDesktop && p.Encoder == "libx264", "plan gdigrab whole x264");
    }

    static void TestNvencSizeLimits()
    {
        StoredSettings s = Base();
        // Two 2560x1440 monitors side by side.
        List<MonitorBounds> wide = new List<MonitorBounds>();
        wide.Add(new MonitorBounds(0, 0, 0, 2560, 1440));
        wide.Add(new MonitorBounds(1, 2560, 0, 2560, 1440));
        // No region: gdigrab whole desktop at 5120x1440. NVENC cannot encode it.
        CapturePlan p = RecorderCore.PlanCapture(s, new CaptureRegion(), wide, true);
        Expect(p.Backend == "gdigrab" && p.WholeDesktop && p.Encoder == "libx264"
            && p.Note.IndexOf("5120x1440") >= 0, "nvenc whole desktop 5120 uses x264");
        // Space in the selector: one region over both monitors.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 0, 5120, 1440), wide, true);
        Expect(p.Backend == "gdigrab" && p.Encoder == "libx264", "nvenc region 5120 uses x264");
        string[] a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(0, 0, 5120, 1440), wide, new DateTime(2026, 1, 1), true);
        Expect(IndexOf(a, "libx264") >= 0 && IndexOf(a, "h264_nvenc") < 0, "nvenc 5120 arguments use x264");
        // One 5K monitor: ddagrab, but libx264.
        List<MonitorBounds> fiveK = new List<MonitorBounds>();
        fiveK.Add(new MonitorBounds(0, 0, 0, 5120, 2880));
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), fiveK, true);
        Expect(p.Backend == "ddagrab" && p.Encoder == "libx264", "nvenc 5K monitor uses x264");
        // Small region (status line).
        p = RecorderCore.PlanCapture(s, new CaptureRegion(10, 10, 120, 30), OneMonitor(), true);
        Expect(p.Backend == "ddagrab" && p.Encoder == "libx264" && p.Note.IndexOf("120x30") >= 0, "nvenc 120x30 uses x264");
        // Explicit NVENC also changes, because NVENC cannot encode the size.
        s.Encoder = "h264_nvenc";
        p = RecorderCore.PlanCapture(s, new CaptureRegion(10, 10, 120, 30), OneMonitor(), false);
        Expect(p.Encoder == "libx264", "explicit nvenc 120x30 uses x264");
        s.Encoder = "auto";
        // Limits: 146x50 and 4096x4096 stay on NVENC.
        p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 0, 146, 50), OneMonitor(), true);
        Expect(p.Encoder == "h264_nvenc" && p.Note == "", "nvenc minimum size kept");
        List<MonitorBounds> big = new List<MonitorBounds>();
        big.Add(new MonitorBounds(0, 0, 0, 4096, 4096));
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), big, true);
        Expect(p.Encoder == "h264_nvenc", "nvenc maximum size kept");
        p = RecorderCore.PlanCapture(s, new CaptureRegion(0, 0, 144, 50), OneMonitor(), true);
        Expect(p.Encoder == "libx264", "nvenc below minimum width");
    }

    static void TestDxgiMatch()
    {
        // DXGI gives index 0 to the primary. Screen.AllScreens lists the secondary (left) monitor first.
        List<DxgiOutputInfo> outputs = new List<DxgiOutputInfo>();
        DxgiOutputInfo primary = new DxgiOutputInfo();
        primary.Index = 0; primary.DeviceName = "\\\\.\\DISPLAY2"; primary.X = 0; primary.Y = 0; primary.Width = 1920; primary.Height = 1080;
        DxgiOutputInfo left = new DxgiOutputInfo();
        left.Index = 1; left.DeviceName = "\\\\.\\DISPLAY1"; left.X = -2560; left.Y = -200; left.Width = 2560; left.Height = 1440;
        outputs.Add(primary);
        outputs.Add(left);
        // AllScreens order: DISPLAY1 (left) first, then DISPLAY2 (primary).
        int leftIndex = RecorderCore.MatchDxgiOutput("\\\\.\\DISPLAY1", -2560, -200, 2560, 1440, outputs, 2);
        int primaryIndex = RecorderCore.MatchDxgiOutput("\\\\.\\display2", 0, 0, 1920, 1080, outputs, 2);
        Expect(leftIndex == 1 && primaryIndex == 0, "dxgi match by name, primary not first");
        // Name not known, rectangle matches.
        Expect(RecorderCore.MatchDxgiOutput("", -2560, -200, 2560, 1440, outputs, 2) == 1, "dxgi match by rectangle");
        // Monitor on a second GPU: not on adapter 0.
        Expect(RecorderCore.MatchDxgiOutput("\\\\.\\DISPLAY3", 1920, 0, 1920, 1080, outputs, 3) == -1, "dxgi no match");
        // No DXGI data.
        Expect(RecorderCore.MatchDxgiOutput("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, null, 1) == 0, "dxgi null one monitor");
        Expect(RecorderCore.MatchDxgiOutput("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, null, 2) == -1, "dxgi null two monitors");

        // The plan uses the matched index, and gdigrab for a monitor with index -1.
        StoredSettings s = Base();
        List<MonitorBounds> monitors = new List<MonitorBounds>();
        monitors.Add(new MonitorBounds(leftIndex, -2560, -200, 2560, 1440));
        monitors.Add(new MonitorBounds(primaryIndex, 0, 0, 1920, 1080));
        monitors.Add(new MonitorBounds(-1, 1920, 0, 1920, 1080));
        CapturePlan p = RecorderCore.PlanCapture(s, new CaptureRegion(100, 100, 800, 600), monitors, true);
        Expect(p.Backend == "ddagrab" && p.OutputIndex == 0 && p.OffsetX == 100, "plan primary not first in list");
        p = RecorderCore.PlanCapture(s, new CaptureRegion(-2000, 100, 800, 600), monitors, true);
        Expect(p.Backend == "ddagrab" && p.OutputIndex == 1 && p.OffsetX == 560 && p.OffsetY == 300, "plan left monitor dxgi index");
        p = RecorderCore.PlanCapture(s, new CaptureRegion(2000, 100, 800, 600), monitors, true);
        Expect(p.Backend == "gdigrab" && p.OffsetX == 2000 && p.Note.Length > 0, "plan monitor on second gpu uses gdigrab");
        List<MonitorBounds> unknown = new List<MonitorBounds>();
        unknown.Add(new MonitorBounds(-1, 0, 0, 1920, 1080));
        p = RecorderCore.PlanCapture(s, new CaptureRegion(), unknown, true);
        Expect(p.Backend == "gdigrab" && p.WholeDesktop, "plan whole desktop unknown output uses gdigrab");
    }

    static void TestFallback()
    {
        bool gdi, x264;
        string reason;
        CapturePlan p = new CapturePlan();
        p.Backend = "ddagrab";
        p.Encoder = "h264_nvenc";
        Expect(RecorderCore.ChooseFallback(p, "auto", "[h264_nvenc @ 0x1] Cannot load nvcuda.dll", out gdi, out x264, out reason)
            && x264 && !gdi && reason.Length > 0, "fallback nvenc error auto");
        Expect(RecorderCore.ChooseFallback(p, "auto", "No capable devices found", out gdi, out x264, out reason)
            && x264 && !gdi, "fallback no capable devices");
        Expect(RecorderCore.ChooseFallback(p, "auto", "No such filter: 'ddagrab'", out gdi, out x264, out reason)
            && gdi && !x264, "fallback ddagrab error");
        Expect(RecorderCore.ChooseFallback(p, "h264_nvenc", "Cannot load nvcuda.dll", out gdi, out x264, out reason)
            && gdi && !x264, "fallback explicit nvenc keeps nvenc");
        p.Backend = "gdigrab";
        Expect(RecorderCore.ChooseFallback(p, "auto", "some error", out gdi, out x264, out reason)
            && x264 && !gdi, "fallback gdigrab nvenc auto");
        Expect(!RecorderCore.ChooseFallback(p, "h264_nvenc", "some error", out gdi, out x264, out reason), "fallback explicit nvenc gdigrab none");
        p.Encoder = "libx264";
        Expect(!RecorderCore.ChooseFallback(p, "auto", "Cannot load nvcuda.dll", out gdi, out x264, out reason), "fallback none left");
        Expect(!RecorderCore.ChooseFallback(null, "auto", "x", out gdi, out x264, out reason), "fallback null plan");
    }

    static void TestSelectorOptions()
    {
        StoredSettings d = new StoredSettings();
        Expect(!d.SelectorCrosshair && d.SelectorMagnifier && d.SelectorPosition, "selector defaults");
        StoredSettings s = new StoredSettings();
        s.SelectorCrosshair = true;
        s.SelectorMagnifier = false;
        s.SelectorPosition = false;
        string text = RecorderCore.SerializeSettings(s);
        Expect(text.Contains("selector_crosshair=1") && text.Contains("selector_magnifier=0")
            && text.Contains("selector_position=0"), "selector options written");
        StoredSettings back = RecorderCore.ParseSettings(text, new StoredSettings());
        Expect(back.SelectorCrosshair && !back.SelectorMagnifier && !back.SelectorPosition, "selector options read");
        StoredSettings old = RecorderCore.ParseSettings("fps=2\r\n", new StoredSettings());
        Expect(!old.SelectorCrosshair && old.SelectorMagnifier && old.SelectorPosition, "selector options old file");
        Expect(old.ExtraLines.Count == 0, "selector keys not extra");
    }

    static void TestCaptureArguments()
    {
        DateTime now = new DateTime(2026, 11, 14, 1, 2, 3, 4);
        CaptureRegion region = new CaptureRegion(0, 108, 1921, 913);

        // ddagrab + NVENC (recommended, HANDOFF 6.5). Same as Phase A run A4, plus the region.
        StoredSettings s = Base();
        string[] a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        string j = Joined(a);
        Expect(j.Contains(" -f lavfi -i ddagrab=output_idx=0:framerate=10:draw_mouse=1:offset_x=0:offset_y=108:video_size=1920x912 "), "dda input: " + j);
        Expect(j.Contains(" -c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0 "), "nvenc args: " + j);
        Expect(IndexOf(a, "-vf") < 0, "dda nvenc no filter");
        Expect(IndexOf(a, "-pix_fmt") < 0, "dda nvenc no pix_fmt");
        Expect(IndexOf(a, "-tune") < 0 && IndexOf(a, "-x264-params") < 0 && IndexOf(a, "-threads") < 0, "no x264 options on nvenc");
        Expect(IndexOf(a, "-fps_mode") < 0, "fixed mode no fps_mode");
        Expect(IndexOf(a, "-stats") >= 0 && IndexOf(a, "-nostats") < 0, "stats on");
        Expect(a[IndexOf(a, "-g") + 1] == "300", "nvenc gop");
        Expect(IndexOf(a, "scale") < 0 && !j.Contains("scale="), "never scale");
        Expect(j.Contains(" -movflags +frag_keyframe+empty_moov -frag_duration 2000000 -flush_packets 1 "), "fragmented mp4");
        Expect(a[a.Length - 1] == @"C:\R\desktop_2026-11-14_01-02-03_004.mp4", "output name");

        // Quality levels map to CQ.
        s.Quality = 0;
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        Expect(a[IndexOf(a, "-cq") + 1] == "28", "cq smallest arg");
        s.Quality = 2;
        s.NvencCq = 20;
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        Expect(a[IndexOf(a, "-cq") + 1] == "14", "cq sharpest from base 20");

        // ddagrab + NVENC 4:4:4: stays on the GPU, rgb_mode and profile.
        s = Base();
        s.ChromaFormat = "yuv444";
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        j = Joined(a);
        Expect(j.Contains(" -profile:v high444p ") && j.Contains(" -rgb_mode yuv444 "), "nvenc 444: " + j);
        Expect(IndexOf(a, "-vf") < 0, "nvenc 444 no download");

        // ddagrab + libx264 (NVENC missing): download, veryfast, pix_fmt.
        s = Base();
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, false);
        j = Joined(a);
        Expect(a[IndexOf(a, "-vf") + 1] == "hwdownload,format=bgra", "dda x264 download: " + j);
        Expect(j.Contains(" -c:v libx264 -preset veryfast -tune stillimage -crf 27 "), "x264 args: " + j);
        Expect(a[IndexOf(a, "-pix_fmt") + 1] == "yuv420p", "x264 420");
        Expect(a[IndexOf(a, "-threads") + 1] == "10", "x264 threads");
        s.ChromaFormat = "yuv444";
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, false);
        Expect(a[IndexOf(a, "-pix_fmt") + 1] == "yuv444p", "x264 444");
        Expect(IndexOf(a, "-rgb_mode") < 0 && IndexOf(a, "-profile:v") < 0, "x264 no nvenc options");

        // gdigrab region + NVENC.
        s = Base();
        s.CaptureBackend = "gdigrab";
        a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(-500, 20, 301, 201), OneMonitor(), now, true);
        j = Joined(a);
        Expect(j.Contains(" -f gdigrab -framerate 10 -draw_mouse 1 -offset_x -500 -offset_y 20 -video_size 300x200 -i desktop "), "gdigrab region: " + j);
        Expect(IndexOf(a, "-vf") < 0, "gdigrab region no crop");
        Expect(IndexOf(a, "h264_nvenc") >= 0, "gdigrab nvenc");

        // gdigrab whole desktop keeps the even crop.
        a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(), OneMonitor(), now, true);
        Expect(a[IndexOf(a, "-vf") + 1] == "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0", "gdigrab whole crop");
        Expect(IndexOf(a, "-offset_x") < 0, "gdigrab whole no offset");

        // ddagrab region across monitors falls back to gdigrab args.
        s = Base();
        a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(-100, 0, 400, 300), TwoMonitors(), now, true);
        Expect(IndexOf(a, "gdigrab") >= 0 && IndexOf(a, "lavfi") < 0, "fallback args gdigrab");
        Expect(a[IndexOf(a, "-offset_x") + 1] == "-100", "fallback offset");

        // "Changes only" mode: mpdecimate after a format filter, and vfr.
        s = Base();
        s.FrameRateMode = "changes";
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        j = Joined(a);
        Expect(a[IndexOf(a, "-vf") + 1] == "hwdownload,format=bgra,format=yuv420p,mpdecimate=max=9", "dda changes chain: " + j);
        Expect(a[IndexOf(a, "-fps_mode") + 1] == "vfr", "changes vfr");
        Expect(IndexOf(a, "-rgb_mode") < 0, "changes 420 no rgb_mode");
        s.ChromaFormat = "yuv444";
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        Expect(a[IndexOf(a, "-vf") + 1] == "hwdownload,format=bgra,format=yuv444p,mpdecimate=max=9", "dda changes 444 chain");
        Expect(IndexOf(a, "-profile:v") >= 0 && IndexOf(a, "-rgb_mode") < 0, "changes 444 profile without rgb_mode");
        s.ChromaFormat = "yuv420";
        s.CaptureBackend = "gdigrab";
        a = RecorderCore.BuildCaptureArguments(s, new CaptureRegion(), OneMonitor(), now, false);
        Expect(a[IndexOf(a, "-vf") + 1] == "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0,format=yuv420p,mpdecimate=max=9", "gdigrab changes chain");
        Expect(a[IndexOf(a, "-framerate") + 1] == "10", "changes captures at the fps setting");

        // Split: same segment muxer as before. NVENC adds forced-idr.
        s = Base();
        s.SplitMinutes = 5;
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, true);
        j = Joined(a);
        Expect(j.Contains(" -force_key_frames expr:gte(t,n_forced*300) -forced-idr 1 -f segment -segment_time 300 -segment_format mp4 "), "nvenc split: " + j);
        Expect(a[a.Length - 1] == @"C:\R\desktop_2026-11-14_01-02-03_004_%03d.mp4", "split name");
        a = RecorderCore.BuildCaptureArguments(s, region, OneMonitor(), now, false);
        Expect(IndexOf(a, "-forced-idr") < 0, "x264 split no forced-idr");
        Expect(IndexOf(a, "-segment_format_options") >= 0, "split fragmented");

        // The 4-argument overload treats "auto" as NVENC.
        a = RecorderCore.BuildCaptureArguments(Base(), region, OneMonitor(), now);
        Expect(IndexOf(a, "h264_nvenc") >= 0, "4-arg overload nvenc");
        // Null settings do not crash.
        a = RecorderCore.BuildCaptureArguments(null, new CaptureRegion(), null, now, false);
        Expect(IndexOf(a, "gdigrab") >= 0, "null settings");
    }

    static void TestNvencProbe()
    {
        string list = "Encoders:\r\n V..... = Video\r\n ------\r\n V....D libx264              libx264 H.264 (codec h264)\r\n V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\r\n";
        Expect(RecorderCore.HasNvencEncoder(list), "nvenc listed");
        string none = " V....D libx264              libx264 H.264 (codec h264)\n V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)\n";
        Expect(!RecorderCore.HasNvencEncoder(none), "only hevc_nvenc");
        Expect(!RecorderCore.HasNvencEncoder(""), "empty list");
        Expect(!RecorderCore.HasNvencEncoder(null), "null list");
        Expect(!RecorderCore.HasNvencEncoder("Unknown encoder 'h264_nvenc'"), "error text is not a listing");
    }

    static void TestProgressParser()
    {
        FfmpegProgress p;
        Expect(RecorderCore.TryParseProgress("frame=  100 fps= 10 q=23.0 size=     256KiB time=00:00:10.00 bitrate= 209.7kbits/s dup=3 drop=1 speed=1.0x", out p), "full line");
        Expect(p.Frames == 100 && p.Duplicates == 3 && p.Dropped == 1, "full values");
        Expect(RecorderCore.TryParseProgress("frame=   10 fps=0.0 q=-1.0 Lsize=N/A time=00:00:00.80 bitrate=N/A speed=64.3x elapsed=0:00:00.01    ", out p), "line without dup and drop");
        Expect(p.Frames == 10 && p.Duplicates == 0 && p.Dropped == 0, "missing dup and drop are 0");
        Expect(RecorderCore.TryParseProgress("frame=5 fps=5 q=23.0 size=0KiB time=00:00:01.00 bitrate=N/A drop=7 speed=1x", out p), "drop only");
        Expect(p.Frames == 5 && p.Duplicates == 0 && p.Dropped == 7, "drop only values");
        Expect(!RecorderCore.TryParseProgress("[gdigrab @ 0000] Capturing whole desktop", out p), "log line");
        Expect(!RecorderCore.TryParseProgress("Error opening input: frame=1", out p), "error line with frame= inside");
        Expect(!RecorderCore.TryParseProgress("frame=N/A fps=0", out p), "frame not a number");
        Expect(!RecorderCore.TryParseProgress("", out p), "empty line");
        Expect(!RecorderCore.TryParseProgress(null, out p), "null line");

        RecordingStats st = new RecordingStats();
        st.HasProgress = true;
        st.FramesCaptured = 600;
        st.DuplicateFrames = 3;
        st.DroppedFrames = 0;
        st.FileBytes = 1572864;
        ExpectEqual(RecorderCore.FormatStats(st), "Real frames: 597 of 600. Duplicates: 3. Dropped: 0. File size: 1.50 MB.", "stats text");
        st.FramesCaptured = 100;
        st.DuplicateFrames = 48;
        ExpectEqual(RecorderCore.FormatStats(st), "Real frames: 52 of 100. Duplicates: 48. Dropped: 0. File size: 1.50 MB.", "stats real frames");
        st.DuplicateFrames = 150;
        Expect(RecorderCore.FormatStats(st).StartsWith("Real frames: 0 of 100."), "stats real frames not negative");
        st.HasProgress = false;
        ExpectEqual(RecorderCore.FormatStats(st), "Frames: unknown. File size: 1.50 MB.", "stats text without progress");
    }

    static int IndexOf(string[] args, string value)
    {
        for (int i = 0; i < args.Length; i++) if (args[i] == value) return i;
        return -1;
    }

    static void TestQuoting()
    {
        ExpectEqual(RecorderCore.QuoteWindowsArgument("a"), "a", "plain quote");
        ExpectEqual(RecorderCore.QuoteWindowsArgument("a b"), "\"a b\"", "space quote");
        ExpectEqual(RecorderCore.QuoteWindowsArgument("a\"b"), "\"a\\\"b\"", "embedded quote");
        ExpectEqual(RecorderCore.QuoteWindowsArgument("foo\\"), "foo\\", "bare slash needs no quotes");
        ExpectEqual(RecorderCore.QuoteWindowsArgument("foo \\"), "\"foo \\\\\"", "quoted trailing slash");
        string round = RecorderCore.QuoteWindowsArgument("C:\\My Videos\\a\"b\\");
        ExpectEqual(SplitLast(round), "C:\\My Videos\\a\"b\\", "quote roundtrip");
    }

    // Enough of CommandLineToArgvW to check one quoted argument.
    static string SplitLast(string cmd)
    {
        StringBuilder sb = new StringBuilder();
        int i = 0;
        bool quote = false;
        while (i < cmd.Length)
        {
            char c = cmd[i];
            if (!quote)
            {
                if (c == '"') { quote = true; i++; continue; }
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '\\')
            {
                int slashes = 0;
                while (i < cmd.Length && cmd[i] == '\\') { slashes++; i++; }
                if (i < cmd.Length && cmd[i] == '"')
                {
                    for (int k = 0; k < slashes / 2; k++) sb.Append('\\');
                    if ((slashes % 2) == 1) { sb.Append('"'); i++; }
                    else { quote = false; i++; }
                }
                else
                {
                    for (int k = 0; k < slashes; k++) sb.Append('\\');
                }
                continue;
            }
            if (c == '"') { quote = false; i++; continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    static void TestFindFfmpeg()
    {
        string root = Path.Combine(Path.GetTempPath(), "dr-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string localDir = Path.Combine(root, "app");
            string pathDir = Path.Combine(root, "path");
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(pathDir);
            string local = Path.Combine(localDir, "ffmpeg.exe");
            File.WriteAllText(local, "x");
            File.WriteAllText(Path.Combine(pathDir, "ffmpeg.exe"), "x");
            ExpectEqual(RecorderCore.FindFfmpeg(localDir, pathDir, true), local, "local exe wins");
            string quoted = "\"" + pathDir + "\"";
            ExpectEqual(RecorderCore.FindFfmpeg(Path.Combine(root, "missing"), ";;" + quoted + ";", true),
                Path.Combine(pathDir, "ffmpeg.exe"), "quoted PATH entry");
            string linuxDir = Path.Combine(root, "linux");
            Directory.CreateDirectory(linuxDir);
            string linuxBin = Path.Combine(linuxDir, "ffmpeg");
            File.WriteAllText(linuxBin, "x");
            File.WriteAllText(Path.Combine(linuxDir, "ffmpeg.exe"), "x");
            // The Linux PATH separator is ':'. A Windows path such as C:\... has a ':' after the drive letter,
            // so this check can run only where paths have no drive letter (Mono on Linux).
            if (Path.DirectorySeparatorChar == '/')
                ExpectEqual(RecorderCore.FindFfmpeg(null, linuxDir, false), linuxBin, "linux name has no exe");
            Expect(RecorderCore.FindFfmpeg(Path.Combine(root, "missing"), "", true) == null
                || RecorderCore.FindFfmpeg(Path.Combine(root, "missing"), "", true).IndexOf("WinGet", StringComparison.OrdinalIgnoreCase) >= 0,
                "empty path finds nothing or the real winget link");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    static void TestWav()
    {
        byte[] wav = RecorderCore.BuildChimeWav(660, 880);
        Expect(wav.Length > 44, "wav has a body");
        ExpectEqual(Encoding.ASCII.GetString(wav, 0, 4), "RIFF", "riff");
        ExpectEqual(Encoding.ASCII.GetString(wav, 8, 4), "WAVE", "wave");
        int dataBytes = BitConverter.ToInt32(wav, 40);
        Expect(dataBytes == wav.Length - 44, "data size matches");
        Expect(dataBytes % 2 == 0, "samples are 16-bit");
        short first = BitConverter.ToInt16(wav, 44);
        Expect(first == 0, "fade starts at silence");
        bool any = false;
        for (int i = 44; i < wav.Length; i += 2)
        {
            if (BitConverter.ToInt16(wav, i) != 0) { any = true; break; }
        }
        Expect(any, "chime is not silent");
    }

    static void TestAtomicWrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dr-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.txt");
            RecorderCore.AtomicWrite(path, "folder=A\r\n");
            RecorderCore.AtomicWrite(path, "folder=B\r\n");
            ExpectEqual(File.ReadAllText(path), "folder=B\r\n", "atomic replace");
            Expect(!File.Exists(path + ".tmp"), "temp file removed");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    static string FakePath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "tests", "fake_ffmpeg.py");
    }

    static void TestSession()
    {
        string fake = FakePath();
        Expect(File.Exists(fake), "fake ffmpeg exists");
        if (!File.Exists(fake)) return;
        string python = "python3";
        TestFailureIsReported(python, fake);
        TestCleanStop(python, fake);
        TestSecondStartRejected(python, fake);
        TestStartWhileStopping(python, fake);
        TestSessionStats(python, Path.Combine(Directory.GetCurrentDirectory(), "tests", "fake_ffmpeg_stats.py"));
    }

    static void TestSessionStats(string python, string fake)
    {
        Expect(File.Exists(fake), "fake stats ffmpeg exists");
        if (!File.Exists(fake)) return;
        string dir = Path.Combine(Path.GetTempPath(), "dr-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RecordingSession session = new RecordingSession();
        try
        {
            RecordingStats empty = session.LastStats();
            Expect(!empty.HasProgress && empty.FramesCaptured == 0, "no stats before start");
            string output = Path.Combine(dir, "desktop_stats.mp4");
            Expect(session.Start(python, Arg(fake) + " " + Arg(output), dir, 2) == null, "stats start");
            // Poll, so a slow test machine does not fail the check.
            RecordingStats mid = session.LastStats();
            for (int wait = 0; wait < 100 && mid.FramesCaptured != 10; wait++)
            {
                Thread.Sleep(50);
                mid = session.LastStats();
            }
            Expect(mid.HasProgress && mid.FramesCaptured == 10 && mid.DuplicateFrames == 1 && mid.DroppedFrames == 0,
                "stats while recording: " + mid.FramesCaptured + "/" + mid.DuplicateFrames);
            session.Stop();
            RecordingStats st = session.LastStats();
            Expect(st.HasProgress, "stats after stop");
            Expect(st.FramesCaptured == 42 && st.DuplicateFrames == 3 && st.DroppedFrames == 2,
                "final stats: " + st.FramesCaptured + "/" + st.DuplicateFrames + "/" + st.DroppedFrames);
            Expect(st.FileBytes == 5, "file size: " + st.FileBytes);

            // A failed run keeps the real error text, without the stats lines in it.
            ManualResetEvent done = new ManualResetEvent(false);
            string message = null;
            session.Failed += delegate(string m) { message = m; done.Set(); };
            string other = Path.Combine(dir, "desktop_stats2.mp4");
            Expect(session.Start(python, Arg(fake) + " --fail " + Arg(other), dir, 2) == null, "stats restart");
            Expect(done.WaitOne(4000), "stats failure reported");
            Expect(message != null && message.Contains("real warning line") && message.Contains("boom")
                && !message.Contains("frame="), "error text without stats: " + message);
            RecordingStats failed = session.LastStats();
            Expect(failed.FramesCaptured == 10 && failed.DuplicateFrames == 1, "stats reset on start and kept after failure");
        }
        finally
        {
            session.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static string Arg(string text)
    {
        return RecorderCore.QuoteWindowsArgument(text);
    }

    static void TestFailureIsReported(string python, string fake)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dr-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RecordingSession session = new RecordingSession();
        ManualResetEvent done = new ManualResetEvent(false);
        string message = null;
        session.Failed += delegate(string m) { message = m; done.Set(); };
        try
        {
            string output = Path.Combine(dir, "desktop_fail.mp4");
            string error = session.Start(python, Arg(fake) + " --fail " + Arg(output), dir, 2);
            Expect(error == null, "failing ffmpeg still starts");
            Expect(done.WaitOne(4000), "failure is reported");
            Expect(message != null && message.IndexOf("boom: bad input", StringComparison.Ordinal) >= 0, "stderr kept: " + message);
            Expect(!session.IsRecording, "failed session is idle");
        }
        finally
        {
            session.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static void TestCleanStop(string python, string fake)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dr-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string oldFile = Path.Combine(dir, "desktop_old.mp4");
        File.WriteAllText(oldFile, "old");
        RecordingSession session = new RecordingSession();
        string message = null;
        session.Failed += delegate(string m) { message = m; };
        try
        {
            string output = Path.Combine(dir, "desktop_new.mp4");
            string error = session.Start(python, Arg(fake) + " " + Arg(output), dir, 2);
            Expect(error == null, "clean start");
            Expect(session.IsRecording, "recording flag");
            Thread.Sleep(200);
            File.AppendAllText(oldFile, "touch");
            session.Stop();
            Expect(message == null, "stop is not a failure");
            Expect(!session.IsRecording, "stopped flag");
            string[] files = session.SessionFiles();
            Expect(files.Length == 1, "only the new file: " + files.Length);
            if (files.Length == 1) Expect(string.Equals(files[0], output, StringComparison.OrdinalIgnoreCase), "new file path");
            Expect(new FileInfo(output).Length > 1, "ffmpeg was asked to finish");
        }
        finally
        {
            session.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static void TestSecondStartRejected(string python, string fake)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dr-two-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RecordingSession session = new RecordingSession();
        try
        {
            string output = Path.Combine(dir, "desktop_one.mp4");
            Expect(session.Start(python, Arg(fake) + " " + Arg(output), dir, 2) == null, "first start");
            string again = session.Start(python, Arg(fake) + " " + Arg(Path.Combine(dir, "desktop_two.mp4")), dir, 2);
            Expect(again != null, "second start rejected");
            Expect(session.IsRecording, "still recording");
            session.Stop();
            Expect(File.Exists(output), "first file kept");
            Expect(!File.Exists(Path.Combine(dir, "desktop_two.mp4")), "second ffmpeg did not start");
        }
        finally
        {
            session.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static void TestStartWhileStopping(string python, string fake)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dr-overlap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RecordingSession session = new RecordingSession();
        try
        {
            string output = Path.Combine(dir, "desktop_slow.mp4");
            Expect(session.Start(python, Arg(fake) + " --slow " + Arg(output), dir, 2) == null, "slow start");
            Thread stopper = new Thread(delegate() { session.Stop(); });
            stopper.IsBackground = true;
            stopper.Start();
            Thread.Sleep(150);
            string during = session.Start(python, Arg(fake) + " " + Arg(Path.Combine(dir, "desktop_other.mp4")), dir, 2);
            Expect(during != null, "start while stopping rejected");
            stopper.Join(8000);
            Expect(!session.IsRecording, "idle after overlapping stop");
        }
        finally
        {
            session.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
