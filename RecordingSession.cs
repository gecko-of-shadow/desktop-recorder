// Owns one ffmpeg process. Stop, failure and a second Start share one state machine
// so a logoff and a tray click cannot launch two encoders or report a fake failure.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

sealed class RecordingSession
{
    const int Idle = 0;
    const int Recording = 1;
    const int Stopping = 2;

    readonly object gate = new object();
    readonly StringBuilder errors = new StringBuilder();
    readonly ManualResetEvent idleEvent = new ManualResetEvent(true);
    Process process;
    Thread watcher;
    int state = Idle;
    string folder;
    double fps = RecorderCore.DefaultFps;
    List<string> before = new List<string>();
    // Last FFmpeg stats line of the current or last recording. Guarded by statsGate.
    readonly object statsGate = new object();
    FfmpegProgress progress;
    bool hasProgress;
    int generation;

    // Raised on a background thread when ffmpeg ends by itself. The argument is ffmpeg's error text.
    public event Action<string> Failed;

    public bool IsRecording
    {
        get { lock (gate) return state != Idle; }
    }

    // Returns null on success, or a message to show.
    public string Start(string ffmpegPath, string arguments, string outputFolder, double framesPerSecond)
    {
        if (ffmpegPath == null || ffmpegPath.Length == 0) return "ffmpeg was not found.";
        if (outputFolder == null || outputFolder.Length == 0) return "The recordings folder is empty.";
        try { Directory.CreateDirectory(outputFolder); }
        catch (Exception e) { return "Cannot create the folder " + outputFolder + ": " + e.Message; }

        Process p = new Process();
        p.StartInfo = new ProcessStartInfo(ffmpegPath, arguments ?? "");
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardInput = true;
        p.StartInfo.RedirectStandardError = true;

        lock (gate)
        {
            if (state != Idle)
                return "Desktop Recorder is already recording.";
            List<string> existing = Snapshot(outputFolder);
            try { p.Start(); }
            catch (Exception e)
            {
                try { p.Dispose(); } catch (Exception) { }
                return "Cannot start ffmpeg: " + e.Message;
            }
            process = p;
            folder = outputFolder;
            fps = RecorderCore.ClampFps(framesPerSecond);
            before = existing;
            lock (errors) { errors.Length = 0; }
            int gen;
            lock (statsGate)
            {
                generation++;
                gen = generation;
                progress = new FfmpegProgress();
                hasProgress = false;
            }
            state = Recording;
            idleEvent.Reset();
            Process started = p;
            watcher = new Thread(delegate() { Watch(started, gen); });
            watcher.IsBackground = true;
            watcher.Name = "ffmpeg-stderr";
            try { watcher.Start(); }
            catch (Exception e)
            {
                state = Idle;
                process = null;
                idleEvent.Set();
                try { p.Kill(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
                return "Cannot start ffmpeg: " + e.Message;
            }
        }
        return null;
    }

    public void Stop()
    {
        Process p = null;
        Thread w = null;
        double frames = RecorderCore.DefaultFps;
        bool claimed = false;
        lock (gate)
        {
            if (state == Idle) return;
            if (state != Stopping)
            {
                state = Stopping;
                p = process;
                w = watcher;
                frames = fps;
                process = null;
                claimed = true;
            }
        }
        if (!claimed)
        {
            idleEvent.WaitOne();
            return;
        }
        try { StopProcess(p, frames); }
        finally
        {
            if (w != null)
            {
                try { w.Join(10000); } catch (Exception) { }
            }
            if (p != null)
            {
                try { p.Dispose(); } catch (Exception) { }
            }
            lock (gate)
            {
                state = Idle;
                idleEvent.Set();
            }
        }
    }

    // Video files made by the last recording, oldest first.
    // Files that were already in the folder are not included, even if something touches them.
    public string[] SessionFiles()
    {
        string dir;
        List<string> prior;
        lock (gate)
        {
            dir = folder;
            prior = new List<string>(before);
        }
        List<string> found = new List<string>();
        try
        {
            if (dir == null || !Directory.Exists(dir)) return new string[0];
            HashSet<string> skip = new HashSet<string>(prior, StringComparer.OrdinalIgnoreCase);
            string[] files = Directory.GetFiles(dir, "desktop_*.mp4");
            for (int i = 0; i < files.Length; i++)
            {
                if (skip.Contains(files[i])) continue;
                try
                {
                    if (new FileInfo(files[i]).Length > 0) found.Add(files[i]);
                }
                catch (Exception) { }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { }
        return found.ToArray();
    }

    static List<string> Snapshot(string dir)
    {
        List<string> list = new List<string>();
        try
        {
            if (Directory.Exists(dir)) list.AddRange(Directory.GetFiles(dir, "desktop_*.mp4"));
        }
        catch (Exception) { }
        return list;
    }

    static void StopProcess(Process p, double frames)
    {
        if (p == null) return;
        try
        {
            // "q" asks ffmpeg to finish the file. It reads it between frames, so wait longer at low fps.
            p.StandardInput.Write("q");
            p.StandardInput.Flush();
            int waitMs = (int)Math.Min(15000, 4000 + 2000 / Math.Max(0.2, frames));
            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(); } catch (Exception) { }
                try { p.WaitForExit(); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { p.WaitForExit(); } catch (Exception) { }
        }
    }

    // Stats of the last recording: frames, duplicates and drops from the last FFmpeg stats line,
    // and the size of the files that the recording made. Call it after Stop (or after Failed).
    public RecordingStats LastStats()
    {
        RecordingStats stats = new RecordingStats();
        lock (statsGate)
        {
            stats.HasProgress = hasProgress;
            stats.FramesCaptured = progress.Frames;
            stats.DuplicateFrames = progress.Duplicates;
            stats.DroppedFrames = progress.Dropped;
        }
        string[] files = SessionFiles();
        for (int i = 0; i < files.Length; i++)
        {
            try { stats.FileBytes += new FileInfo(files[i]).Length; }
            catch (Exception) { }
        }
        return stats;
    }

    void HandleLine(string line, int gen)
    {
        FfmpegProgress parsed;
        if (RecorderCore.TryParseProgress(line, out parsed))
        {
            // Stats lines do not go into the error text, or they fill the Failed message.
            lock (statsGate)
            {
                if (gen == generation)
                {
                    progress = parsed;
                    hasProgress = true;
                }
            }
            return;
        }
        lock (errors)
        {
            if (errors.Length < 2000) errors.AppendLine(line);
        }
    }

    void Watch(Process p, int gen)
    {
        try
        {
            // FFmpeg ends each stats line with a lone '\r'. StreamReader.ReadLine waits for the
            // next character after a '\r' (to look for "\r\n"), so the newest stats line would
            // stay unread until FFmpeg writes more. Split the lines here instead.
            TextReader reader = p.StandardError;
            char[] buffer = new char[4096];
            StringBuilder current = new StringBuilder();
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (current.Length > 0) HandleLine(current.ToString(), gen);
                        current.Length = 0;
                    }
                    else if (current.Length < 8192)
                    {
                        current.Append(c);
                    }
                }
            }
            if (current.Length > 0) HandleLine(current.ToString(), gen);
        }
        catch (Exception) { }
        try { p.WaitForExit(); } catch (Exception) { }

        string text;
        lock (errors) { text = errors.ToString().Trim(); }
        bool unexpected = false;
        Action<string> handler = null;
        lock (gate)
        {
            // A requested stop sets Stopping before the process exits, so this is not a failure.
            unexpected = state == Recording && process == p;
            if (unexpected)
            {
                process = null;
                state = Idle;
                idleEvent.Set();
                handler = Failed;
            }
        }
        if (!unexpected) return;
        try { p.Dispose(); } catch (Exception) { }
        if (handler == null) return;
        try { handler(text.Length > 0 ? text : "ffmpeg stopped."); }
        catch (Exception) { }
    }
}
