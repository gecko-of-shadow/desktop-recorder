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
            string error = EncodeClip(input, r, true);
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
                string error = EncodeClip(input, r, false);
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
            r.Frames, input.Titles, input.SizeLimit, r.Warnings);
        RecorderCore.AtomicWrite(r.IndexPath, text);
    }

    // The ZIP of the folder, next to the folder. Made again on each call, so it has the newest index.md.
    public static string MakeZip(string folder)
    {
        string zip = folder.TrimEnd('\\', '/') + ".zip";
        string temp = zip + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        ZipFile.CreateFromDirectory(folder, temp, CompressionLevel.Optimal, true);
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
            pngQueue.Add(f);
        };

        // mpdecimate with all limits 0 drops only frames that are exactly the same as the frame before
        // (a lossless still screen). This makes the pipe much smaller in fixed-rate mode. showinfo gives the
        // time of each frame that stays; the reader thread puts the times in a queue, in frame order.
        string args = "-hide_banner -nostats -loglevel info -copyts -i " + RecorderCore.QuoteWindowsArgument(input.WorkFile)
            + " -map 0:v:0 -vf mpdecimate=hi=0:lo=0:frac=0,showinfo -fps_mode passthrough -f rawvideo -pix_fmt bgr24 pipe:1";
        Process p = StartProcess(input.Ffmpeg, args);
        StringBuilder errors = new StringBuilder();
        Queue<double> frameTimes = new Queue<double>();
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
                    if (TryShowinfoTime(line, out pt))
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
        }
        finally
        {
            try { p.WaitForExit(); } catch (Exception) { }
            errThread.Join(5000);
        }
        int exit = p.ExitCode;
        p.Dispose();
        if (index == 0) throw new IOException("FFmpeg decoded no frames. " + errors.ToString().Trim());
        if (exit != 0) r.Warnings.Add("The decoder ended with code " + exit.ToString(CultureInfo.InvariantCulture) + ".");

        finder.Finish(r.EndTime);
        pngQueue.CompleteAdding();
        foreach (Thread w in pngWorkers) w.Join();
        if (pngError != null) throw new IOException("Cannot write a PNG file: " + pngError.Message);
        r.DecodedFrames = index;
        r.MinOffset = minOff;
        r.MaxOffset = maxOff;
        if (r.Frames.Count >= 1000) RenameForCount(framesDir, r.Frames.Count);
    }

    // showinfo line: "[Parsed_showinfo_1 @ 0000...] n:   0 pts:   9000 pts_time:0.6     duration: ..."
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
        if (!File.Exists(path)) throw new IOException("ffprobe was not found next to ffmpeg: " + path);
        return path;
    }

    // keyframes = true: one keyframe at each settled frame and no other keyframe.
    // keyframes = false: a normal clip.mp4 (fallback after a failed pass).
    static string EncodeClip(AfterRecordingInput input, AfterRecordingResult r, bool keyframes)
    {
        List<double> times = new List<double>();
        if (keyframes) foreach (SettledFrame f in r.Frames) times.Add(f.Time);
        string[] a = RecorderCore.BuildClipArguments(input.Settings ?? new StoredSettings(), input.WorkFile, r.ClipPath,
            keyframes ? times : null, input.Fps, input.NvencAvailable);
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
