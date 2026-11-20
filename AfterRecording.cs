// The pass after a recording in folder mode (AI capture brief, Phase 1).
// Reads the lossless work file, writes frames\NNN.png for each settled frame, encodes clip.mp4 with a
// keyframe at each settled frame, and writes index.md. Runs on a background thread. C# 5.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

sealed class AfterRecordingInput
{
    public string Ffmpeg;
    public string WorkFile;
    public string Folder;
    public DateTime Start;
    public TimeSpan UtcOffset;
    public TitleTimeline Titles;
    public SettledOptions Options = new SettledOptions();
    public double Fps = RecorderCore.DefaultFps;
    public int SizeLimit = RecorderCore.DefaultSizeLimit;
    public StoredSettings Settings;          // encoder, quality and chroma for clip.mp4
    public bool NvencAvailable = true;
    public bool DeleteWorkFile;              // true in the app (Bran, 2026-11-19). The test tool keeps its input.
    public bool ForceX264;                   // the capture plan used libx264 (NVENC cannot encode this size)
    public double EndTimeHint = -1;          // the stop time on the time line of the video, when known
}

sealed class AfterRecordingResult
{
    public bool Ok;                          // frames, clip.mp4 with keyframes and index.md are written
    public bool ClipWritten;                 // clip.mp4 exists (also after the fallback encode)
    public string Error = "";
    public int Width, Height;
    public int DecodedFrames;
    public double FirstTime, EndTime;
    public List<SettledFrame> Frames = new List<SettledFrame>();   // Pixels are null
    public string ClipPath, IndexPath;
    public long ClipBytes;
    public double AnalyzeSeconds, EncodeSeconds;
    public long MinOffset, MaxOffset;        // scroll range seen in all frames (for the coverage test)
    public List<string> Warnings = new List<string>();
    public string MotionNote;                // not null: the picture did not settle for most of the recording
}

static class AfterRecording
{
    public const string WorkFileName = "work_lossless.mp4";
    public const string ClipFileName = "clip.mp4";
    public const string IndexFileName = "index.md";

    public static AfterRecordingResult Run(AfterRecordingInput input)
    {
        AfterRecordingResult r = new AfterRecordingResult();
        r.ClipPath = Path.Combine(input.Folder, ClipFileName);
        r.IndexPath = Path.Combine(input.Folder, IndexFileName);
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            Analyze(input, r);
            r.AnalyzeSeconds = watch.Elapsed.TotalSeconds;
            watch.Reset();
            watch.Start();
            string error = EncodeClip(input, r, true, input.ForceX264);
            if (error != null && !input.ForceX264)
            {
                // NVENC can fail (driver, other app): try libx264 once (review 2026-11-19).
                string second = EncodeClip(input, r, true, true);
                if (second == null) { r.Warnings.Add("NVENC failed for clip.mp4. libx264 encoded it."); error = null; }
                else error += " libx264: " + second;
            }
            r.EncodeSeconds = watch.Elapsed.TotalSeconds;
            if (error != null) throw new IOException("clip.mp4 with keyframes: " + error);
            r.ClipWritten = true;
            WriteIndex(input, r, null);
            r.Ok = true;
        }
        catch (Exception e)
        {
            r.Error = e.Message;
        }
        if (!r.Ok && !r.ClipWritten)
        {
            // Condition D (Bran, 2026-11-19): still give a normal clip.mp4.
            try
            {
                string error = EncodeClip(input, r, false, input.ForceX264);
                if (error != null && !input.ForceX264) error = EncodeClip(input, r, false, true);
                if (error == null) r.ClipWritten = true;
                else r.Error += " Normal clip.mp4 also failed: " + error;
            }
            catch (Exception e) { r.Error += " Normal clip.mp4 also failed: " + e.Message; }
        }
        try { if (File.Exists(r.ClipPath)) r.ClipBytes = new FileInfo(r.ClipPath).Length; } catch (Exception) { }
        if (r.Ok && input.DeleteWorkFile)
        {
            try { File.Delete(input.WorkFile); }
            catch (Exception e) { r.Warnings.Add("Cannot delete the work file: " + e.Message); }
        }
        return r;
    }

    // Writes index.md again, for example after the user typed a note.
    public static void WriteIndex(AfterRecordingInput input, AfterRecordingResult r, string note)
    {
        double length = Math.Max(0, r.EndTime - r.FirstTime);
        string text = RecordingIndex.Build(input.Start, input.UtcOffset, note, length, r.Width, r.Height,
            r.Frames, input.Titles, input.SizeLimit, r.Warnings, r.FirstTime);
        RecorderCore.AtomicWrite(r.IndexPath, text);
    }

    // When the pass failed there is no index.md. The note goes into note.txt (review 2026-11-19).
    public static string SaveNote(AfterRecordingInput input, AfterRecordingResult r, string note)
    {
        if (r != null && r.Ok)
        {
            WriteIndex(input, r, note);
            return r.IndexPath;
        }
        string path = Path.Combine(input.Folder, "note.txt");
        RecorderCore.AtomicWrite(path, (note ?? "") + "\r\n");
        return path;
    }

    // The ZIP of the folder, next to the folder. Made again on each call, so it has the newest index.md.
    public static string MakeZip(string folder)
    {
        string zip = folder.TrimEnd('\\', '/') + ".zip";
        string temp = zip + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        // Not the lossless work files: they are large, and the folder has clip.mp4 (review 2026-11-19).
        string root = Path.GetFileName(folder.TrimEnd('\\', '/'));
        using (FileStream fs = new FileStream(temp, FileMode.CreateNew))
        using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("work_lossless", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = file.Substring(full.Length).TrimStart('\\', '/').Replace('\\', '/');
                archive.CreateEntryFromFile(file, root + "/" + rel, CompressionLevel.Optimal);
            }
        }
        if (File.Exists(zip)) File.Delete(zip);
        File.Move(temp, zip);
        return zip;
    }

    static void Analyze(AfterRecordingInput input, AfterRecordingResult r)
    {
        int width, height;
        List<double> times = ReadFrameTimes(input.Ffmpeg, input.WorkFile, out width, out height);
        if (times.Count == 0) throw new IOException("The work file has no video frames.");
        r.Width = width;
        r.Height = height;
        r.FirstTime = times[0];
        double frameLength = 1.0 / Math.Max(0.2, input.Fps);
        r.EndTime = times[times.Count - 1] + frameLength;
        // In "Changes only" mode the last frame can be up to 1 s before Stop. The app knows the stop time.
        // EndTimeHint is on the stats clock, which starts at 0 at the first frame.
        double hint = input.EndTimeHint >= 0 ? input.EndTimeHint + r.FirstTime : -1;
        if (hint > r.EndTime && hint < r.EndTime + 5) r.EndTime = hint;

        string framesDir = Path.Combine(input.Folder, "frames");
        Directory.CreateDirectory(framesDir);
        SettledFrameFinder finder = new SettledFrameFinder(width, height, input.Options);
        // PNG files are written by 2 worker threads, so the analysis does not wait for them.
        // At most 8 frames wait in the queue (memory limit).
        System.Collections.Concurrent.BlockingCollection<SettledFrame> pngQueue =
            new System.Collections.Concurrent.BlockingCollection<SettledFrame>(8);
        Exception pngError = null;
        Thread[] pngWorkers = new Thread[2];
        for (int w = 0; w < pngWorkers.Length; w++)
        {
            pngWorkers[w] = new Thread(delegate()
            {
                foreach (SettledFrame f in pngQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        // Names use 3 digits. More than 999 frames get more digits (RenameForCount).
                        SavePng(f.Pixels, width, height, Path.Combine(framesDir, RecordingIndex.FrameFileName(f.Number, f.Number) + ".png"));
                    }
                    catch (Exception e) { pngError = e; }
                    f.Pixels = null;
                }
            });
            pngWorkers[w].IsBackground = true;
            pngWorkers[w].Start();
        }
        finder.FrameWritten += delegate(SettledFrame f)
        {
            r.Frames.Add(f);
            if (!pngQueue.IsAddingCompleted) pngQueue.Add(f);
        };

        // mpdecimate with all limits 0 drops only frames that are exactly the same as the frame before
        // (a lossless still screen). This makes the pipe much smaller in fixed-rate mode. showinfo gives the
        // time of each frame that stays; the reader thread puts the times in a queue, in frame order.
        string args = "-hide_banner -nostats -loglevel info -copyts -i " + RecorderCore.QuoteWindowsArgument(input.WorkFile)
            + " -map 0:v:0 -vf mpdecimate=hi=0:lo=0:frac=0,showinfo -fps_mode passthrough -f rawvideo -pix_fmt bgr24 pipe:1";
        Process p = StartProcess(input.Ffmpeg, args);
        StringBuilder errors = new StringBuilder();
        Queue<double> frameTimes = new Queue<double>();
        long tbNum = 0, tbDen = 0;
        bool errDone = false;
        object timeGate = new object();
        Thread errThread = new Thread(delegate()
        {
            try
            {
                string line;
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    double pt;
                    TryShowinfoTimeBase(line, ref tbNum, ref tbDen);
                    if (TryShowinfoTime(line, tbNum, tbDen, out pt))
                    {
                        lock (timeGate) { frameTimes.Enqueue(pt); Monitor.PulseAll(timeGate); }
                    }
                    else if (line.IndexOf("Parsed_", StringComparison.Ordinal) < 0)
                    {
                        lock (errors) { if (errors.Length < 4000) errors.AppendLine(line); }
                    }
                }
            }
            catch (Exception) { }
            lock (timeGate) { errDone = true; Monitor.PulseAll(timeGate); }
        });
        errThread.IsBackground = true;
        errThread.Start();
        int frameBytes = width * height * 3;
        byte[] frame = new byte[frameBytes];
        Stream stdout = p.StandardOutput.BaseStream;
        int index = 0;
        long minOff = 0, maxOff = 0;
        bool readOk = false;
        try
        {
            while (ReadExactly(stdout, frame, frameBytes))
            {
                double t;
                lock (timeGate)
                {
                    while (frameTimes.Count == 0 && !errDone) Monitor.Wait(timeGate, 30000);
                    if (frameTimes.Count == 0) throw new IOException("FFmpeg gave a frame without a time.");
                    t = frameTimes.Dequeue();
                }
                finder.Feed(frame, t);
                if (finder.TotalScroll < minOff) minOff = finder.TotalScroll;
                if (finder.TotalScroll > maxOff) maxOff = finder.TotalScroll;
                index++;
            }
            readOk = true;
        }
        finally
        {
            // On an error, end FFmpeg first: it can wait forever to write the next frame (review 2026-11-19).
            if (!readOk) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
            try { p.WaitForExit(60000); } catch (Exception) { }
            errThread.Join(5000);
            pngQueue.CompleteAdding();
            foreach (Thread w in pngWorkers) w.Join();
        }
        int exit = p.HasExited ? p.ExitCode : -1;
        p.Dispose();
        if (index == 0) throw new IOException("FFmpeg decoded no frames. " + errors.ToString().Trim());
        // A decoder error means that frames can be missing. Then the pass is not Ok, so the work file stays.
        if (exit != 0) throw new IOException("The decoder ended with code " + exit.ToString(CultureInfo.InvariantCulture) + ". " + Trim(errors.ToString()));

        // Finish can write more frames: start the PNG workers again for them.
        System.Collections.Concurrent.BlockingCollection<SettledFrame> tail = new System.Collections.Concurrent.BlockingCollection<SettledFrame>();
        finder.FrameWritten += delegate(SettledFrame f) { tail.Add(f); };
        finder.Finish(r.EndTime);
        // Moving content (task 2, 2026-11-20): say so in index.md, so the AI knows that the frames are samples.
        r.MotionNote = finder.MotionReport(r.EndTime);
        if (r.MotionNote != null) r.Warnings.Add(r.MotionNote);
        foreach (SettledFrame f in tail)
        {
            try { SavePng(f.Pixels, width, height, Path.Combine(framesDir, RecordingIndex.FrameFileName(f.Number, f.Number) + ".png")); }
            catch (Exception e) { pngError = e; }
            f.Pixels = null;
        }
        if (pngError != null) throw new IOException("Cannot write a PNG file: " + pngError.Message);
        r.DecodedFrames = index;
        r.MinOffset = minOff;
        r.MaxOffset = maxOff;
        if (r.Frames.Count >= 1000) RenameForCount(framesDir, r.Frames.Count);
    }

    // showinfo line: "[Parsed_showinfo_1 @ 0000...] n:   0 pts:   9000 pts_time:0.6     duration: ..."
    // showinfo prints "config in time_base: 1/15360, frame_rate: 15/1" once.
    internal static void TryShowinfoTimeBase(string line, ref long num, ref long den)
    {
        if (line == null || line.IndexOf("showinfo", StringComparison.Ordinal) < 0) return;
        int k = line.IndexOf("time_base:", StringComparison.Ordinal);
        if (k < 0) return;
        string rest = line.Substring(k + 10).Trim();
        int slash = rest.IndexOf('/');
        if (slash < 0) return;
        int e = slash + 1;
        while (e < rest.Length && char.IsDigit(rest[e])) e++;
        long n, d;
        if (long.TryParse(rest.Substring(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
            && long.TryParse(rest.Substring(slash + 1, e - slash - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out d) && n > 0 && d > 0)
        {
            num = n;
            den = d;
        }
    }

    // The exact time from "pts:" and the time base when known. pts_time has only 6 digits (review 2026-11-19).
    internal static bool TryShowinfoTime(string line, long tbNum, long tbDen, out double time)
    {
        time = 0;
        if (line == null || line.IndexOf("showinfo", StringComparison.Ordinal) < 0) return false;
        if (tbNum > 0 && tbDen > 0)
        {
            int p = line.IndexOf(" pts:", StringComparison.Ordinal);
            if (p >= 0 && line.IndexOf("pts_time:", StringComparison.Ordinal) >= 0)
            {
                int s = p + 5;
                while (s < line.Length && line[s] == ' ') s++;
                int e2 = s;
                while (e2 < line.Length && (char.IsDigit(line[e2]) || line[e2] == '-')) e2++;
                long pts;
                if (long.TryParse(line.Substring(s, e2 - s), NumberStyles.Integer, CultureInfo.InvariantCulture, out pts))
                {
                    time = pts * (double)tbNum / tbDen;
                    return true;
                }
            }
        }
        return TryShowinfoTime(line, out time);
    }

    internal static bool TryShowinfoTime(string line, out double time)
    {
        time = 0;
        if (line == null || line.IndexOf("showinfo", StringComparison.Ordinal) < 0) return false;
        int k = line.IndexOf("pts_time:", StringComparison.Ordinal);
        if (k < 0) return false;
        k += 9;
        int e = k;
        while (e < line.Length && line[e] != ' ') e++;
        return double.TryParse(line.Substring(k, e - k), NumberStyles.Float, CultureInfo.InvariantCulture, out time);
    }

    // With 1000 or more frames, use the same number of digits for all names.
    static void RenameForCount(string dir, int total)
    {
        for (int n = 1; n <= total; n++)
        {
            string from = Path.Combine(dir, RecordingIndex.FrameFileName(n, n) + ".png");
            string to = Path.Combine(dir, RecordingIndex.FrameFileName(n, total) + ".png");
            if (from != to && File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
    }

    // Presentation times of the video packets (sorted), and the frame size. Uses ffprobe next to ffmpeg.
    static List<double> ReadFrameTimes(string ffmpeg, string file, out int width, out int height)
    {
        string probe = FfprobePath(ffmpeg);
        if (probe == null) return ReadFrameTimesWithFfmpeg(ffmpeg, file, out width, out height);
        string q = RecorderCore.QuoteWindowsArgument(file);
        string text = RunToText(probe, "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 " + q);
        string[] parts = text.Trim().Split(',');
        if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            throw new IOException("ffprobe gave no frame size: " + text.Trim());
        string packets = RunToText(probe, "-v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 " + q);
        List<double> times = new List<double>();
        foreach (string line in packets.Split('\n'))
        {
            string s = line.Trim().TrimEnd(',');
            double t;
            if (s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) times.Add(t);
        }
        times.Sort();
        return times;
    }

    public static string FfprobePath(string ffmpeg)
    {
        string dir = Path.GetDirectoryName(ffmpeg) ?? "";
        string name = Path.GetFileName(ffmpeg).ToLowerInvariant().EndsWith(".exe") ? "ffprobe.exe" : "ffprobe";
        string path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    // Without ffprobe (only ffmpeg.exe next to the app): one quick pass that copies the packets, with showinfo
    // on nothing... A copy cannot use filters, so decode at a small size instead: times and the size of the stream.
    static List<double> ReadFrameTimesWithFfmpeg(string ffmpeg, string file, out int width, out int height)
    {
        width = 0;
        height = 0;
        string args = "-hide_banner -nostats -loglevel info -copyts -i " + RecorderCore.QuoteWindowsArgument(file)
            + " -map 0:v:0 -vf scale=16:16,showinfo -fps_mode passthrough -f null -";
        Process p = StartProcess(ffmpeg, args);
        List<double> times = new List<double>();
        long num = 0, den = 0;
        string line;
        System.Text.RegularExpressions.Regex size = new System.Text.RegularExpressions.Regex(@"Video: .*?, (\d{2,5})x(\d{2,5})");
        while ((line = p.StandardError.ReadLine()) != null)
        {
            if (width == 0)
            {
                System.Text.RegularExpressions.Match m = size.Match(line);
                if (m.Success)
                {
                    width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            TryShowinfoTimeBase(line, ref num, ref den);
            double t;
            if (TryShowinfoTime(line, num, den, out t)) times.Add(t);
        }
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.Dispose();
        if (width <= 0 || height <= 0) throw new IOException("FFmpeg gave no frame size for the work file.");
        times.Sort();
        return times;
    }

    // keyframes = true: one keyframe at each settled frame and no other keyframe.
    // keyframes = false: a normal clip.mp4 (fallback after a failed pass).
    static string EncodeClip(AfterRecordingInput input, AfterRecordingResult r, bool keyframes, bool forceX264)
    {
        List<double> times = new List<double>();
        if (keyframes) foreach (SettledFrame f in r.Frames) times.Add(f.Time);
        StoredSettings s = input.Settings ?? new StoredSettings();
        if (forceX264)
        {
            StoredSettings copy = RecorderCore.ParseSettings(RecorderCore.SerializeSettings(s), new StoredSettings());
            copy.Encoder = RecorderCore.EncoderX264;
            s = copy;
        }
        // A Windows command line holds at most 32767 characters. Keep the keyframe list below that
        // (about 3000 frames) and say so in index.md (review 2026-11-19).
        if (keyframes && RecorderCore.KeyframeList(times).Length > 24000)
        {
            int keep = times.Count;
            while (keep > 1 && RecorderCore.KeyframeList(times.GetRange(0, keep)).Length > 24000) keep = keep * 9 / 10;
            r.Warnings.Add("Only the first " + keep.ToString(CultureInfo.InvariantCulture) + " frames are keyframes in clip.mp4 (command line limit).");
            times = times.GetRange(0, keep);
        }
        string[] a = RecorderCore.BuildClipArguments(s, input.WorkFile, r.ClipPath,
            keyframes ? times : null, input.Fps, input.NvencAvailable && !forceX264);
        string output;
        int exit;
        if (!RecorderCore.RunFfmpeg(input.Ffmpeg, RecorderCore.JoinWindowsArguments(a), 30 * 60 * 1000, out output, out exit))
            return "FFmpeg did not start or did not end in time.";
        if (exit != 0) return "FFmpeg exit code " + exit.ToString(CultureInfo.InvariantCulture) + ": " + Trim(output);
        if (!File.Exists(r.ClipPath)) return "FFmpeg wrote no file.";
        return null;
    }

    static string Trim(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 300 ? s.Substring(0, 300) : s;
    }

    static void SavePng(byte[] bgr, int width, int height, string path)
    {
        using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int row = width * 3;
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgr, y * row, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row);
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    static Process StartProcess(string exe, string args)
    {
        Process p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args);
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.Start();
        ChildProcessJob.Add(p);
        return p;
    }

    static string RunToText(string exe, string args)
    {
        Process p = StartProcess(exe, args);
        StringBuilder err = new StringBuilder();
        Thread t = DrainAsync(p.StandardError, err);
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        t.Join(5000);
        int exit = p.ExitCode;
        p.Dispose();
        if (exit != 0) throw new IOException(Path.GetFileName(exe) + " failed: " + Trim(err.ToString()));
        return text;
    }

    static Thread DrainAsync(TextReader reader, StringBuilder into)
    {
        Thread t = new Thread(delegate()
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (into) { if (into.Length < 4000) into.AppendLine(line); }
                }
            }
            catch (Exception) { }
        });
        t.IsBackground = true;
        t.Start();
        return t;
    }

    static bool ReadExactly(Stream s, byte[] buffer, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = s.Read(buffer, done, count - done);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }
}
