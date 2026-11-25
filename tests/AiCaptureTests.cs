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
        TestZipOnly();
        TestLateTranscript();
        TestLostRecordings();
        TestZipFailLeavesNoTemp();
        TestVideoIsNotScroll();
        TestNoisyShiftIsNotScroll();
        TestScrollStillFound();
        TestMotionReport();
        TestPointerIsNotMotion();
        TestManySmallPartsAreMotion();
        TestSmallScrollText();
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

    // Folder mode gives one ZIP only (Bran, 2026-11-22).
    // A folder with the ZIP name makes MakeZip fail (a read-only file does not block a delete on Linux; the live test R12 used one). The live test (HANDOFF 7.22) found the temporary
    // "<name>.zip.tmp" left next to the recordings after that failure.
    static void TestZipFailLeavesNoTemp()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dr_zipfail_" + Guid.NewGuid().ToString("N"));
        string folder = System.IO.Path.Combine(dir, "desktop_test");
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "index.md"), "test");
        string zip = folder + ".zip";
        System.IO.Directory.CreateDirectory(zip);
        bool threw = false;
        try { AfterRecording.MakeZip(folder); }
        catch (Exception) { threw = true; }
        Expect(threw, "zip fail: MakeZip throws when a folder has the ZIP name");
        Expect(!System.IO.File.Exists(zip + ".tmp"), "zip fail: no temporary file is left");
        System.IO.Directory.Delete(dir, true);
    }

    // Bran, 2026-11-24: the ZIP must not wait for a slow Whisper, and the transcript must not be lost.
    static void TestLateTranscript()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dr-late-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            AfterRecordingInput input = new AfterRecordingInput();
            input.Folder = folder;
            input.Start = new DateTime(2026, 11, 24, 12, 0, 0);
            input.Titles = new TitleTimeline();
            input.AudioFile = System.IO.Path.Combine(folder, AudioMux.AudioFileName);
            AfterRecordingResult r = new AfterRecordingResult();
            r.Ok = true;
            r.AudioRecorded = true;
            r.AudioInClip = true;
            r.IndexPath = System.IO.Path.Combine(folder, AfterRecording.IndexFileName);
            r.Width = 640; r.Height = 480; r.EndTime = 10;
            Expect(!r.TranscriptPending, "no transcript thread: not pending");
            r.Work = new TranscriptWork();
            Expect(r.TranscriptPending, "transcript thread not merged: pending");
            System.IO.File.WriteAllText(input.AudioFile, "x");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, AfterRecording.WhisperOutputName + ".srt"), "half");
            AfterRecording.WriteIndex(input, r, null);
            string first = System.IO.File.ReadAllText(r.IndexPath);
            Expect(first.Contains("No transcript: " + AfterRecording.PendingNote + "."), "first index.md says that the transcript is still being made");
            string zip = AfterRecording.MakeZip(folder);
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip))
                Expect(z.Entries.Count == 1 && z.Entries[0].FullName.EndsWith("index.md"), "first ZIP: no half-written Whisper file");
            // Whisper ends.
            TranscriptSegment s = new TranscriptSegment();
            s.Start = 1; s.End = 2; s.Text = "late words";
            r.Work.Out.Transcript = new List<TranscriptSegment>();
            r.Work.Out.Transcript.Add(s);
            r.Work.Out.TranscriptTool = "whisper-cli";
            r.Work.Done.Set();
            AfterRecording.AddLateTranscript(input, r, "my note");
            string second = System.IO.File.ReadAllText(r.IndexPath);
            Expect(second.Contains("late words") && second.Contains("my note"), "second index.md has the transcript and the note");
            Expect(!r.TranscriptPending && r.HasTranscript, "merged");
            Expect(!System.IO.File.Exists(input.AudioFile), "the WAV file goes after the transcript (clip.mp4 has the audio)");
            // The old ZIP is open in another program for a moment: the new ZIP still takes its place.
            System.IO.FileStream hold = new System.IO.FileStream(zip, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
            System.Threading.Thread release = new System.Threading.Thread(delegate() { System.Threading.Thread.Sleep(700); hold.Dispose(); });
            release.Start();
            AfterRecording.MakeZip(folder);
            release.Join();
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip))
            using (System.IO.StreamReader text = new System.IO.StreamReader(z.Entries[0].Open()))
                Expect(text.ReadToEnd().Contains("late words"), "the ZIP that was open has the transcript");
            Expect(!System.IO.File.Exists(zip + ".tmp"), "no temporary ZIP left");
            System.IO.File.Delete(zip);
        }
        finally { System.IO.Directory.Delete(folder, true); }
    }

    // Bug hunt, 2026-11-24: recordings, sound and settings that could be lost.
    static void TestLostRecordings()
    {
        DateTime t;
        Expect(RecordingIndex.TryFolderTime("desktop_2026-11-24_12-30-05", out t) && t == new DateTime(2026, 11, 24, 12, 30, 5), "folder time");
        Expect(RecordingIndex.TryFolderTime("desktop_2026-11-24_12-30-05_2", out t) && !RecordingIndex.TryFolderTime("desktop_2026-11-24_12-30-05x", out t)
            && !RecordingIndex.TryFolderTime("live-test-2026-11-22", out t) && !RecordingIndex.TryFolderTime(null, out t), "other folder names");
        Expect(RecordingIndex.NeedsRecovery("desktop_2026-11-24_12-30-05", true, false, false, 90), "unfinished recording");
        Expect(!RecordingIndex.NeedsRecovery("desktop_2026-11-24_12-30-05", false, false, false, 90)
            && !RecordingIndex.NeedsRecovery("desktop_2026-11-24_12-30-05", true, true, false, 90)
            && !RecordingIndex.NeedsRecovery("desktop_2026-11-24_12-30-05", true, false, true, 90)
            && !RecordingIndex.NeedsRecovery("desktop_2026-11-24_12-30-05", true, false, false, 5)
            && !RecordingIndex.NeedsRecovery("other", true, false, false, 90), "no work file, a ZIP, a marker, a running recording, another folder");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, true, 0, false, false, false, true), "cleanup: the WAV file is the only copy of the sound");
        Expect(RecorderCore.IsCaptureLoss("[ddagrab @ 0] AcquireNextFrame failed: 887a0026") && !RecorderCore.IsCaptureLoss("No space left") && !RecorderCore.IsCaptureLoss(null), "capture loss");
        Expect(AfterRecording.EncodeLimitMs(15, true) == 90000 && AfterRecording.EncodeLimitMs(double.NaN, true) == 60000
            && AfterRecording.EncodeLimitMs(1e9, false) > 0, "encode limit");
        Expect(AfterRecording.ReadLimitMs(1000000000L, 60) == 820000 && AfterRecording.ReadLimitMs(long.MaxValue, 0) > 0, "read limit");
        Expect(RecordingSession.StopWaitMs(6) > 15000 && RecordingSession.StopWaitMs(0.01) == 25000, "stop limit");

        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dr-lost-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(folder, "frames"));
        try
        {
            string work = System.IO.Path.Combine(folder, AfterRecording.WorkFileName);
            System.IO.File.WriteAllText(work, "....ftypisom....moov....mvex");
            Expect(!AfterRecording.HasVideo(work) && !AfterRecording.HasVideo(work + "x"), "header only: no video");
            System.IO.File.WriteAllText(work, "....ftypisom....moov....mvex....moof....mdat");
            Expect(AfterRecording.HasVideo(work), "a fragment: video");

            // index.md is open in another program: the note goes into note.txt, and the ZIP has it.
            AfterRecordingInput input = new AfterRecordingInput();
            input.Folder = folder;
            input.Titles = new TitleTimeline();
            AfterRecordingResult r = new AfterRecordingResult();
            r.Ok = true; r.Width = 640; r.Height = 480; r.EndTime = 5;
            r.IndexPath = System.IO.Path.Combine(folder, AfterRecording.IndexFileName);
            string notePath = System.IO.Path.Combine(folder, AfterRecording.NoteFileName);
            AfterRecording.WriteIndex(input, r, null);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                using (new System.IO.FileStream(r.IndexPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                    Expect(AfterRecording.SaveNote(input, r, "held note") == notePath, "held index.md: note.txt");
                Expect(System.IO.File.ReadAllText(notePath).Contains("held note"), "note.txt has the note");
            }
            else System.IO.File.WriteAllText(notePath, "x");
            AfterRecording.SaveNote(input, r, "my note");
            Expect(System.IO.File.ReadAllText(r.IndexPath).Contains("my note") && !System.IO.File.Exists(notePath), "index.md has the note, note.txt is gone");

            // The cleanup deletes only the files of the app.
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "frames", "001.png"), "x");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "my own.txt"), "x");
            Expect(AfterRecording.DeleteFolder(folder, 2, 10, null) == null, "delete: no error");
            Expect(System.IO.File.Exists(System.IO.Path.Combine(folder, "my own.txt")) && System.IO.Directory.GetFileSystemEntries(folder).Length == 1, "delete: a file of the user stays");
            System.IO.File.Delete(System.IO.Path.Combine(folder, "my own.txt"));
            Expect(AfterRecording.DeleteFolder(folder, 2, 10, null) == null && !System.IO.Directory.Exists(folder), "delete: empty folder goes");

            // settings.txt
            System.IO.Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "settings.txt");
            bool failed;
            Expect(RecorderCore.ReadSettingsText(path, out failed) == null && !failed, "no settings file");
            RecorderCore.WriteSettingsText(path, "fps=6\n");
            RecorderCore.WriteSettingsText(path, "fps=7\n");
            Expect(RecorderCore.ReadSettingsText(path, out failed) == "fps=7\n" && System.IO.File.ReadAllText(path + ".bak") == "fps=6\n", "settings and backup");
            System.IO.File.WriteAllText(path, "\0\0\0");
            Expect(RecorderCore.ReadSettingsText(path, out failed) == "fps=6\n" && !failed, "empty settings file: backup");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                using (new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                    Expect(RecorderCore.ReadSettingsText(path, out failed) == null && failed, "held settings file: failed, not the defaults");
        }
        finally { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
    }

    static void TestZipOnly()
    {
        // The folder is deleted only when the ZIP is done and good, the pass is good, and the note box is closed.
        Expect(RecordingIndex.CanDeleteFolder(true, true, true, 0, false, false), "cleanup: all good");
        Expect(!RecordingIndex.CanDeleteFolder(false, true, true, 0, false, false), "cleanup: pass not done");
        Expect(!RecordingIndex.CanDeleteFolder(true, false, true, 0, false, false), "cleanup: failed pass keeps the work file");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, false, 0, false, false), "cleanup: no ZIP");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, true, 1, false, false), "cleanup: a ZIP is still made (note)");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, true, 0, true, false), "cleanup: the last ZIP failed");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, true, 0, false, true), "cleanup: note box open");
        Expect(!RecordingIndex.CanDeleteFolder(true, true, true, 0, false, false, true), "cleanup: Whisper still works in the folder");
        Expect(RecordingIndex.CanDeleteFolder(true, true, true, 0, false, false, false), "cleanup: Whisper is done");

        // The messages describe a ZIP, not a folder.
        string zip = @"C:\rec\desktop_2026-11-22_10-00-00.zip";
        string ok = RecordingIndex.AiMessage(zip, true);
        Expect(ok.IndexOf("ZIP", StringComparison.Ordinal) >= 0 && ok.IndexOf(zip + "\r\n", StringComparison.Ordinal) >= 0, "message names the ZIP");
        Expect(ok.IndexOf("in this folder", StringComparison.Ordinal) < 0, "message does not say folder for the recording");
        Expect(RecordingIndex.AiNote.IndexOf("ZIP", StringComparison.Ordinal) >= 0, "index.md note names the ZIP");
        Expect(RecordingIndex.AiMessage(zip, false).IndexOf("the ZIP has no frames", StringComparison.Ordinal) >= 0, "failed message names the ZIP");

        // MakeZip: PNG and MP4 are stored, text is compressed, work files and WAV files are left out.
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dr-zip-" + Guid.NewGuid().ToString("N"));
        string folder = System.IO.Path.Combine(root, "desktop_test");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(folder, "frames"));
            byte[] data = new byte[20000];
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "index.md"), new string('a', 20000));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "clip.mp4"), data);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "frames", "001.png"), data);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "work_lossless.mp4"), data);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "audio_system.wav"), data);
            string made = AfterRecording.MakeZip(folder);
            Expect(made == folder + ".zip" && System.IO.File.Exists(made), "ZIP is next to the folder");
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(made))
            {
                Expect(z.Entries.Count == 3, "ZIP has 3 entries");
                foreach (System.IO.Compression.ZipArchiveEntry e in z.Entries)
                {
                    // Mono's ZipArchive compresses an entry also with CompressionLevel.NoCompression
                    // (Build workflow on Linux, 2026-11-22). The app runs on .NET Framework, so check it there only.
                    if (e.FullName.EndsWith(".png") || e.FullName.EndsWith(".mp4"))
                    {
                        if (Type.GetType("Mono.Runtime") == null)
                            Expect(e.CompressedLength >= e.Length, "stored without compression: " + e.FullName);
                    }
                    else
                        Expect(e.CompressedLength < e.Length / 4, "text is compressed: " + e.FullName);
                    Expect(e.FullName.StartsWith("desktop_test/", StringComparison.Ordinal), "entry is in the folder: " + e.FullName);
                }
            }
            // A second ZIP replaces the first one at the same path.
            Expect(AfterRecording.MakeZip(folder) == made, "the ZIP path stays the same");
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch (Exception) { } }
    }

    static void TestSettingsKeys()
    {
        StoredSettings d = new StoredSettings();
        Expect(d.OutputMode == RecorderCore.OutputFolder, "default output folder");
        Expect(d.SettleMs == 400 && d.StillIntervalSeconds == 5 && d.ScrollPercent == 60 && d.SizeLimit == 2576, "defaults");
        Expect(RecorderCore.ParseSettings("size_limit=1568\r\n", d).SizeLimit == 2576, "old default 1568 reads as the new default");
        StoredSettings p = RecorderCore.ParseSettings("output_mode=mp4\r\nsettle_ms=300\r\nstill_interval_s=7\r\nscroll_percent=200\r\nsize_limit=2000\r\n", d);
        Expect(p.OutputMode == RecorderCore.OutputMp4, "output mp4");
        Expect(p.SettleMs == 300 && p.StillIntervalSeconds == 7 && p.SizeLimit == 2000, "ints");
        Expect(p.ScrollPercent == 95, "scroll clamp");
        // clipboard_max_files was removed on 2026-11-22. An old file with the key loads with no error, and Save drops it.
        StoredSettings z = RecorderCore.ParseSettings("clipboard_max_files=1\r\nsettle_ms=300\r\n", d);
        Expect(z.SettleMs == 300, "old clipboard_max_files key does not stop the load");
        Expect(RecorderCore.SerializeSettings(z).IndexOf("clipboard_max_files", StringComparison.Ordinal) < 0, "clipboard_max_files is not written");
        Expect(RecorderCore.ParseSettings("clipboard_max_files=x\r\n", d).OutputMode == RecorderCore.OutputFolder, "bad old clipboard_max_files value is ignored");
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
        Expect(RecorderCore.HasNoEncoderDelay(j) && j.IndexOf("-frag_duration 500000", StringComparison.Ordinal) >= 0, "no encoder delay and short fragments in the work file: " + j);
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

    // A mouse pointer, 10 x 16 px: a dark outline with a white inside, as the Windows arrow.
    static void Pointer(byte[] f, int fw, int fh, int x0, int y0)
    {
        for (int y = 0; y < 16; y++)
            for (int x = 0; x <= Math.Min(9, y * 10 / 16); x++)
            {
                int px = x0 + x, py = y0 + y;
                if (px < 0 || py < 0 || px >= fw || py >= fh) continue;
                bool edge = x == 0 || x == Math.Min(9, y * 10 / 16) || y == 15;
                int i = (py * fw + px) * 3;
                byte v = edge ? (byte)0 : (byte)255;
                f[i] = v; f[i + 1] = v; f[i + 2] = v;
            }
    }

    // "Text" page of any size: dark marks on some rows.
    static byte[] BigPage(int fw, int fh)
    {
        byte[] f = new byte[fw * fh * 3];
        for (int i = 0; i < f.Length; i++) f[i] = 250;
        for (int y = 10; y < fh - 10; y += 14)
            for (int x = 10; x < fw - 10; x++)
                if (((x * 7 + y * 13) % 11) < 4) for (int dy = 0; dy < 3; dy++) { int i = ((y + dy) * fw + x) * 3; f[i] = 30; f[i + 1] = 30; f[i + 2] = 30; }
        return f;
    }

    // A moving mouse pointer is not moving content: Bran's recordings of 2026-11-22 (pointer shown, still pages) got
    // "the picture changed during 81%". A video area is moving content, and the note gives its area only.
    static void TestPointerIsNotMotion()
    {
        // 96 x 120 frames: the limit is half the short side of the region (48 px). Slow and fast pointer moves.
        SettledFrameFinder small = Finder();
        byte[] page = Page(4);
        for (int i = 0; i < 12 * 15; i++)
        {
            double t = i / 15.0;
            byte[] fr = (byte[])page.Clone();
            double speed = i < 90 ? 1.0 : 4.0;
            Pointer(fr, W, H, (int)(40 + 35 * Math.Sin(t * 2.1 * speed)), (int)(50 + 45 * Math.Cos(t * 1.3 * speed)));
            small.Feed(fr, t);
        }
        small.Finish(12);
        Expect(small.MotionReport(12) == null && small.MovingSeconds < 0.5, "moving pointer is not motion (moving " + small.MovingSeconds.ToString("0.00") + " s)");

        // 640 x 360 frames: the limit is 96 px. The pointer moves all the time; a 200 x 120 video plays in the second test.
        const int BW = 640, BH = 360;
        byte[] big = BigPage(BW, BH);
        Random rnd = new Random(11);
        for (int run = 0; run < 2; run++)
        {
            bool video = run == 1;
            SettledFrameFinder f = new SettledFrameFinder(BW, BH, new SettledOptions());
            for (int i = 0; i < 10 * 15; i++)
            {
                double t = i / 15.0;
                byte[] fr = (byte[])big.Clone();
                if (video)
                    for (int y = 150; y < 270; y++)
                        for (int x = 300; x < 500; x++) { int k = (y * BW + x) * 3; fr[k] = (byte)rnd.Next(256); fr[k + 1] = (byte)rnd.Next(256); fr[k + 2] = (byte)rnd.Next(256); }
                // Fast at the start (separate old and new pointer), then slow (they touch).
                double speed = i < 60 ? 3.0 : 0.5;
                Pointer(fr, BW, BH, (int)(120 + 100 * Math.Sin(t * 1.7 * speed)), (int)(180 + 150 * Math.Cos(t * 1.1 * speed)));
                f.Feed(fr, t);
            }
            f.Finish(10);
            string note = f.MotionReport(10);
            if (!video) Expect(note == null && f.MovingSeconds < 0.5, "640 x 360: moving pointer is not motion (moving " + f.MovingSeconds.ToString("0.00") + " s)");
            else Expect(note != null && note.IndexOf("(area x 296, y 144, 208 x 128 px)", StringComparison.Ordinal) >= 0,
                "640 x 360: video is motion, area of the video only: " + note);
        }
    }

    // Many small moving parts (a dark video with sparks or stars) are moving content, although each group of changed
    // blocks is small: all groups together cover more than 96 x 96 px (review 2026-11-22).
    static void TestManySmallPartsAreMotion()
    {
        const int BW = 640, BH = 360, Parts = 60;
        Random rnd = new Random(23);
        int[] px = new int[Parts], py = new int[Parts];
        for (int k = 0; k < Parts; k++) { px[k] = rnd.Next(BW - 8); py[k] = rnd.Next(BH - 8); }
        SettledFrameFinder f = new SettledFrameFinder(BW, BH, new SettledOptions());
        for (int i = 0; i < 10 * 15; i++)
        {
            byte[] fr = new byte[BW * BH * 3];
            for (int j = 0; j < fr.Length; j++) fr[j] = 16;
            for (int k = 0; k < Parts; k++)
            {
                px[k] = Math.Max(0, Math.Min(BW - 8, px[k] + rnd.Next(-12, 13)));
                py[k] = Math.Max(0, Math.Min(BH - 8, py[k] + rnd.Next(-12, 13)));
                for (int y = py[k]; y < py[k] + 8; y++)
                    for (int x = px[k]; x < px[k] + 8; x++) { int q = (y * BW + x) * 3; fr[q] = 230; fr[q + 1] = 230; fr[q + 2] = 200; }
            }
            f.Feed(fr, i / 15.0);
        }
        f.Finish(10);
        Expect(f.MotionReport(10) != null, "many small moving parts are motion (moving " + f.MovingSeconds.ToString("0.00") + " s)");
    }

    // A few pixels of "scroll" in a changing screen (video) are not named; a real scroll is (2026-11-20).
    static void TestSmallScrollText()
    {
        SettledFrame f = new SettledFrame();
        f.Kind = FrameKind.StillChanging;
        f.ScrollPixels = -3;
        f.BoxHeight = 976;
        Expect(RecordingIndex.Describe(f, "", "", 1020) == "screen still changing", "small scroll not named: " + RecordingIndex.Describe(f, "", "", 1020));
        f.ScrollPixels = 40;
        Expect(RecordingIndex.Describe(f, "", "", 1020) == "screen still changing, scroll, 40 px down", "real scroll named");
        f.BoxHeight = 0;
        f.ScrollPixels = 3;
        Expect(RecordingIndex.Describe(f, "", "", 1020) == "screen still changing", "no box: region height is the area");
        f.Kind = FrameKind.Last;
        f.ChangedFraction = 0.4;
        Expect(RecordingIndex.Describe(f, "", "", 1020) == "large change, last frame (screen was changing)", "last frame: small scroll not named");
        f.Kind = FrameKind.Settled;
        Expect(RecordingIndex.Describe(f, "", "", 1020) == "scroll, 3 px down", "settled frame keeps a small scroll");
        f.Kind = FrameKind.StillChanging;
        Expect(RecordingIndex.Describe(f, "", "") == "screen still changing, scroll, 3 px down", "old overload unchanged");
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
        Expect(md.IndexOf("\nFor the AI that reads this file:", StringComparison.Ordinal) > 0
            && md.IndexOf("For the AI that reads this file:", StringComparison.Ordinal) < md.IndexOf("Length:", StringComparison.Ordinal), "instruction for the AI near the top");
        Expect(md.IndexOf("Note from Bran: look at usage by bot\n", StringComparison.Ordinal) >= 0, "note");
        Expect(md.IndexOf("Length: 119.5 s\n", StringComparison.Ordinal) >= 0 && md.IndexOf("Region: 1302 x 960\n", StringComparison.Ordinal) >= 0, "length and region");
        Expect(md.IndexOf("Frames: 21\n", StringComparison.Ordinal) >= 0, "frame count");
        Expect(md.IndexOf("Warning: the ZIP has 21 frame images. With index.md, that is 22 files. A chat app that unpacks the ZIP can limit the number of images that it reads. Some chat apps accept only 20 files in one message.\n", StringComparison.Ordinal) >= 0, "file count warning at 21 frames");
        Expect(md.IndexOf("more than 2576 px", StringComparison.Ordinal) < 0 && md.IndexOf("million pixels", StringComparison.Ordinal) < 0, "no size warning at 1302");
        Expect(md.IndexOf("| 001 | 1.6 s | Grok Bot | first frame |", StringComparison.Ordinal) >= 0, "first row");
        Expect(md.IndexOf("| 005 | 5.6 s | Grok Bot \\| Settings | new window content |", StringComparison.Ordinal) >= 0, "title change row and pipe escape");
        Expect(md.IndexOf("| 006 | 6.6 s | Grok Bot \\| Settings | small change at x 10, y 20 (30 x 12 px) |", StringComparison.Ordinal) >= 0, "small change row");
        // Titles are on the stats clock (0 at the first frame). With a first frame at 0.6 s, frame 5 (5.6 s) is
        // 5.0 s after the first frame, so it gets the second title only with the shift.
        string shifted = RecordingIndex.Build(new DateTime(2026, 11, 19, 13, 1, 41), TimeSpan.FromHours(-3), null,
            119.5, 1302, 960, frames, t, 1568, null, 0.6);
        Expect(shifted.IndexOf("| 005 | 5.6 s | Grok Bot \\| Settings |", StringComparison.Ordinal) >= 0
            && shifted.IndexOf("| 004 | 4.6 s | Grok Bot |", StringComparison.Ordinal) >= 0, "title shift by the first frame time");
        string big = RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 1920, 1080, frames.GetRange(0, 2), t, 2576, null);
        Expect(big.IndexOf("Warning:", StringComparison.Ordinal) < 0, "no warning at 1920 x 1080 and 2 frames");
        Expect(RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 1920, 1080, frames.GetRange(0, 19), t, 2576, null).IndexOf("files in one message", StringComparison.Ordinal) < 0, "no file warning at 19 frames (20 files)");
        Expect(RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 1920, 1080, frames.GetRange(0, 20), t, 2576, null).IndexOf("Warning: the ZIP has 20 frame images. With index.md, that is 21 files.", StringComparison.Ordinal) >= 0, "file warning at 20 frames (21 files)");
        Expect(RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 3840, 1080, frames.GetRange(0, 2), t, 2576, null).IndexOf("more than 2576 px", StringComparison.Ordinal) >= 0, "size warning at 3840");
        Expect(RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 2560, 1600, frames.GetRange(0, 2), t, 2576, null).IndexOf("more than 3.75 million pixels", StringComparison.Ordinal) >= 0, "pixel warning at 2560 x 1600");
        Expect(RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, null, 1, 2560, 1440, frames.GetRange(0, 2), t, 2576, null).IndexOf("Warning:", StringComparison.Ordinal) < 0, "no warning at 2560 x 1440");
        Expect(big.IndexOf("Note from Bran:", StringComparison.Ordinal) < 0, "no note line");
        // The note box saves CR LF line breaks. The live test (HANDOFF 7.22) found two spaces for one line break.
        string twoLines = RecordingIndex.Build(DateTime.Now, TimeSpan.Zero, "line one\r\nline two\nline three", 1, 640, 480, frames.GetRange(0, 2), t, 2576, null);
        Expect(twoLines.IndexOf("Note from Bran: line one line two line three\n", StringComparison.Ordinal) >= 0, "one space for each line break in the note");
    }
}
