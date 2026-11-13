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
