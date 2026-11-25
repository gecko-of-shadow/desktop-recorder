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
    // Audio and transcript (docs/audio-transcript.md). AudioFile null = no audio.
    public string AudioFile;
    public double AudioStartClock;           // WAV time 0 on the clock of RecordingSession
    public double ClockOffset = double.NaN;  // RecordingSession.ClockOffset after the stop
    public double AudioPeak = -1;            // AudioTake.Peak
    public string AudioError;                // AudioTake.Error
    public string AudioNote;                 // AudioTake.Note: the capture went on, for example on a new playback device
    public string AudioStartError;           // why the capture did not start, or null
    public string Problem;                   // the recording ended early (FFmpeg stopped by itself), or null
    public WhisperTool Whisper;              // null = no transcript
    public string WhisperReason;             // why Whisper is null
    public string WhisperLanguage = "auto";
    public bool AudioIntoKeptWorkFile = true;  // false in the test tool: it must not change its input file
    // Called on the pass thread when a step starts (StepFrames, StepTranscript), for the "busy" indication. Can be null.
    public Action<string> Step;
    // How long Run waits for the transcript when the rest is ready (ms). -1 = until it is done (the test tool).
    // A transcript that needs more time is added later (AfterRecordingResult.TranscriptPending).
    public int TranscriptWaitMs = -1;
}

// The transcript runs on its own thread, next to the frames and clip.mp4 (Bran, 2026-11-24: the ZIP must not hang).
// Whisper took 1 to 84 s on the same PC. Out gets the transcript fields; Done is set when the thread ends.
sealed class TranscriptWork
{
    public readonly ManualResetEvent Done = new ManualResetEvent(false);
    public readonly AfterRecordingResult Out = new AfterRecordingResult();
    public bool Merged;
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
    public bool AudioRecorded;               // the input had a WAV file
    public bool AudioInClip;                 // clip.mp4 has the audio track
    public double AudioShift;                // WAV time 0 on the time line of clip.mp4
    public List<TranscriptSegment> Transcript;   // null = no transcript (TranscriptNote says why)
    public string TranscriptNote;
    public string TranscriptTool;
    public double TranscriptSeconds;
    public TranscriptWork Work;                  // the transcript thread, or null (no audio)
    public bool WorkFileStays;
    // True while the transcript is not in this result yet.
    public bool TranscriptPending { get { return Work != null && !Work.Merged; } }
    public int TranscriptRowsDropped;            // rows that Whisper wrote on silent or almost silent audio (SpeechCheck)
    public bool HasTranscript { get { return Transcript != null && Transcript.Count > 0; } }
}

static class AfterRecording
{
    public const string WorkFileName = "work_lossless.mp4";
    public const string ClipFileName = "clip.mp4";
    public const string IndexFileName = "index.md";
    public const string NoteFileName = "note.txt";
    // The pass ran to its end, and the work file stays (not in the ZIP: the name starts with work_lossless).
    public const string DoneMarkerName = "work_lossless.done";
    // The pass started, and the ZIP is not made yet.
    public const string PendingMarkerName = "work_lossless.pending";
    public const string StepFrames = "frames";
    public const string StepTranscript = "transcript";
    // In the first ZIP, when Whisper needs more time.
    public const string PendingNote = "the transcript is still being made. The app makes this ZIP again when it is done";
    // The output file of whisper.cpp, before transcript.srt is written.
    public const string WhisperOutputName = "transcript_whisper";

    static void ReportStep(AfterRecordingInput input, string step)
    {
        Action<string> handler = input.Step;
        if (handler == null) return;
        try { handler(step); } catch (Exception) { }
    }

    public static AfterRecordingResult Run(AfterRecordingInput input)
    {
        AfterRecordingResult r = new AfterRecordingResult();
        r.ClipPath = Path.Combine(input.Folder, ClipFileName);
        r.IndexPath = Path.Combine(input.Folder, IndexFileName);
        Stopwatch watch = Stopwatch.StartNew();
        ReportStep(input, StepFrames);
        if (!string.IsNullOrEmpty(input.Problem))
            r.Warnings.Add(Trim(input.Problem));
        try
        {
            ReadTimes(input, r);
            SetUpAudio(input, r);
            StartTranscript(input, r);
            Analyze(input, r);
            r.AnalyzeSeconds = watch.Elapsed.TotalSeconds;
            watch.Reset();
            watch.Start();
            bool audio = r.AudioRecorded;
            string error = EncodeClipWithFallback(input, r, true, audio);
            if (error != null && audio && !IsTimeout(error))
            {
                // The audio must not cost the clip: encode again without it (docs/audio-transcript.md).
                string second = EncodeClipWithFallback(input, r, true, false);
                if (second == null) { r.Warnings.Add("clip.mp4 has no audio. The audio encode failed, so the sound is only in " + AudioMux.AudioFileName + " in the recording folder: " + Trim(error)); error = null; audio = false; }
                else error += " Without audio: " + second;
            }
            r.AudioInClip = audio && error == null;
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
                if (r.Work == null) SetUpAudio(input, r);
                string error = EncodeClipWithFallback(input, r, false, r.AudioRecorded);
                if (error != null && r.AudioRecorded && !IsTimeout(error)) error = EncodeClipWithFallback(input, r, false, false);
                else if (error == null) r.AudioInClip = r.AudioRecorded;
                if (error == null) r.ClipWritten = true;
                else r.Error += " Normal clip.mp4 also failed: " + error;
            }
            catch (Exception e) { r.Error += " Normal clip.mp4 also failed: " + e.Message; }
        }
        try { if (File.Exists(r.ClipPath)) r.ClipBytes = new FileInfo(r.ClipPath).Length; } catch (Exception) { }
        bool workStays = !(r.Ok && input.DeleteWorkFile);
        // A work file that stays in the folder (failed pass, or the keep-work test switch) gets the audio too.
        if (workStays && r.AudioRecorded && input.AudioIntoKeptWorkFile) AddAudioToWorkFile(input, r);
        if (r.Ok && input.DeleteWorkFile)
        {
            try { File.Delete(input.WorkFile); }
            catch (Exception e) { r.Warnings.Add("Cannot delete the work file: " + e.Message); }
        }
        r.WorkFileStays = workStays;
        // A missing, failed or slow Whisper never makes the pass fail, and the ZIP does not wait for a slow one.
        if (r.Work != null)
        {
            if (!r.Work.Done.WaitOne(0)) ReportStep(input, StepTranscript);
            if (r.Work.Done.WaitOne(input.TranscriptWaitMs)) MergeTranscript(r);
            if (r.Ok)
            {
                try { WriteIndex(input, r, null); }
                catch (Exception e) { r.Warnings.Add("index.md has no transcript: " + e.Message); }
            }
        }
        if (!r.TranscriptPending) CleanUpAudio(input, r, workStays);
        return r;
    }

    // ------------------------------------------------------------ the transcript thread

    static void StartTranscript(AfterRecordingInput input, AfterRecordingResult r)
    {
        if (!r.AudioRecorded) return;
        TranscriptWork work = new TranscriptWork();
        work.Out.AudioShift = r.AudioShift;
        work.Out.FirstTime = r.FirstTime;
        work.Out.EndTime = r.EndTime;
        Thread t = new Thread(delegate()
        {
            try { MakeTranscript(input, work.Out); }
            catch (Exception) { }
            finally { work.Done.Set(); }
        });
        t.IsBackground = true;
        t.Name = "transcript";
        t.Start();
        r.Work = work;
    }

    // Puts the finished transcript into the result. Call it when r.Work.Done is set.
    public static void MergeTranscript(AfterRecordingResult r)
    {
        TranscriptWork w = r.Work;
        if (w == null || w.Merged) return;
        r.Transcript = w.Out.Transcript;
        r.TranscriptNote = w.Out.TranscriptNote;
        r.TranscriptTool = w.Out.TranscriptTool;
        r.TranscriptSeconds = w.Out.TranscriptSeconds;
        r.TranscriptRowsDropped = w.Out.TranscriptRowsDropped;
        w.Merged = true;
    }

    // The late transcript: into the result, then index.md (with the note), then the audio files that are not needed.
    public static void AddLateTranscript(AfterRecordingInput input, AfterRecordingResult r, string note)
    {
        MergeTranscript(r);
        if (r.Ok) WriteIndex(input, r, note);
        CleanUpAudio(input, r, r.WorkFileStays);
    }

    // ------------------------------------------------------------ audio and transcript

    static void SetUpAudio(AfterRecordingInput input, AfterRecordingResult r)
    {
        r.AudioRecorded = false;
        if (input.AudioFile == null)
        {
            if (!string.IsNullOrEmpty(input.AudioStartError)) AddOnce(r.Warnings, input.AudioStartError);
            return;
        }
        // More than a header: a header is 44 to 68 bytes, and 0.1 s of 8 kHz mono 8-bit audio is 800 bytes.
        try { r.AudioRecorded = File.Exists(input.AudioFile) && new FileInfo(input.AudioFile).Length > 1024; }
        catch (Exception) { r.AudioRecorded = false; }
        if (!r.AudioRecorded) { AddOnce(r.Warnings, "The audio file is missing or empty, so the recording has no audio."); return; }
        r.AudioShift = AudioClock.VideoTimeOfAudioStart(input.AudioStartClock, input.ClockOffset, r.FirstTime);
        if (double.IsNaN(input.ClockOffset))
            r.Warnings.Add("FFmpeg gave no clock, so the audio can be up to about 0.5 s off the video.");
        if (!string.IsNullOrEmpty(input.AudioNote)) r.Warnings.Add(input.AudioNote);
        if (!string.IsNullOrEmpty(input.AudioError)) r.Warnings.Add(input.AudioError + " The audio after that point is silence.");
    }

    static void AddOnce(List<string> list, string text)
    {
        if (!list.Contains(text)) list.Add(text);
    }

    static void MakeTranscript(AfterRecordingInput input, AfterRecordingResult r)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try { MakeTranscriptCore(input, r); }
        catch (Exception e) { r.Transcript = null; r.TranscriptNote = "the transcript failed: " + Trim(e.Message); }
        r.TranscriptSeconds = watch.Elapsed.TotalSeconds;
    }

    static void MakeTranscriptCore(AfterRecordingInput input, AfterRecordingResult r)
    {
        if (input.Whisper == null) { r.TranscriptNote = input.WhisperReason ?? "no Whisper program was found"; return; }
        // Whisper invents text on silence ("Thank you."), so silent audio gets no transcript.
        if (input.AudioPeak >= 0 && input.AudioPeak < AudioTake.SilentPeak) { r.TranscriptNote = "the audio was silent"; return; }
        string wav = Path.Combine(input.Folder, AudioMux.TranscriptWavName);
        string output;
        int exit;
        string[] prep = AudioMux.BuildTranscriptWavArguments(input.AudioFile, r.AudioShift, wav);
        if (!RecorderCore.RunFfmpeg(input.Ffmpeg, RecorderCore.JoinWindowsArguments(prep), ReadLimitMs(FileLength(input.AudioFile), 0), out output, out exit) || exit != 0)
        {
            r.TranscriptNote = "FFmpeg could not make the 16 kHz audio for Whisper: " + Trim(output);
            return;
        }
        double[] levels = null;
        try { levels = SpeechCheck.WindowLevels(wav); } catch (Exception) { levels = null; }
        if (!SpeechCheck.MayHaveSpeech(levels))
        {
            r.TranscriptNote = "the audio was silent or almost silent, so Whisper did not run";
            return;
        }
        string srt;
        string[] args = Transcript.BuildArguments(input.Whisper, wav, input.Folder, input.WhisperLanguage, out srt);
        try { if (File.Exists(srt)) File.Delete(srt); } catch (Exception) { }
        double seconds = Math.Max(0, r.EndTime - r.FirstTime);
        int timeout = Transcript.TimeoutMs(seconds);
        if (!RecorderCore.RunProgram(input.Whisper.Path, RecorderCore.JoinWindowsArguments(args), timeout, false, out output, out exit))
        {
            r.TranscriptNote = Path.GetFileName(input.Whisper.Path) + " did not start, or did not end in "
                + (timeout / 1000).ToString(CultureInfo.InvariantCulture) + " s";
            return;
        }
        if (exit != 0 || !File.Exists(srt))
        {
            r.TranscriptNote = Path.GetFileName(input.Whisper.Path) + " failed (exit code " + exit.ToString(CultureInfo.InvariantCulture) + "): " + Trim(LastLines(output, 3));
            return;
        }
        List<TranscriptSegment> segments = Transcript.ParseSrt(File.ReadAllText(srt, Encoding.UTF8));
        // Whisper writes text such as "Thank you." on silence (Bran's recordings of 2026-11-22). The 16 kHz copy is on
        // the time line of the rows: a row with almost no sound in its time is left out (SpeechCheck).
        int dropped;
        segments = SpeechCheck.Filter(segments, levels, out dropped);
        r.TranscriptRowsDropped = dropped;
        // No rows: no transcript.srt (an empty file helps nobody).
        if (segments.Count > 0)
            RecorderCore.AtomicWrite(Path.Combine(input.Folder, AudioMux.TranscriptFileName), Transcript.FormatSrt(segments));
        if (!string.Equals(Path.GetFileName(srt), AudioMux.TranscriptFileName, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(srt); } catch (Exception) { }
        }
        r.Transcript = segments;
        if (segments.Count == 0) r.TranscriptNote = "no speech was found";
        string lang = string.IsNullOrEmpty(input.WhisperLanguage) ? "auto" : input.WhisperLanguage;
        r.TranscriptTool = input.Whisper.Describe() + ", language " + lang;
    }

    static string LastLines(string text, int n)
    {
        string[] lines = (text ?? "").Replace("\r", "").Trim().Split('\n');
        int from = Math.Max(0, lines.Length - n);
        return string.Join(" ", lines, from, lines.Length - from);
    }

    static void AddAudioToWorkFile(AfterRecordingInput input, AfterRecordingResult r)
    {
        string temp = input.WorkFile + ".audio.tmp.mp4";
        try
        {
            if (!File.Exists(input.WorkFile)) return;
            string output;
            int exit;
            string[] a = AudioMux.BuildRemuxArguments(input.WorkFile, input.AudioFile, r.AudioShift, temp);
            if (RecorderCore.RunFfmpeg(input.Ffmpeg, RecorderCore.JoinWindowsArguments(a),
                ReadLimitMs(FileLength(input.WorkFile) + FileLength(input.AudioFile), 0), out output, out exit)
                && exit == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
            {
                ReplaceFile(temp, input.WorkFile);
                r.Warnings.Add("The work file has the audio too.");
                return;
            }
            r.Warnings.Add("The audio could not be added to the work file. " + AudioMux.AudioFileName + " stays in the folder.");
        }
        catch (Exception e) { r.Warnings.Add("The audio could not be added to the work file: " + e.Message); }
        try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
    }

    // The 16 kHz copy is a temporary file. The WAV file goes when clip.mp4 has the audio and no work file stays.
    static void CleanUpAudio(AfterRecordingInput input, AfterRecordingResult r, bool workStays)
    {
        try
        {
            string wav16 = Path.Combine(input.Folder, AudioMux.TranscriptWavName);
            if (File.Exists(wav16)) File.Delete(wav16);
        }
        catch (Exception) { }
        if (input.AudioFile == null || workStays || !r.AudioInClip) return;
        try { if (File.Exists(input.AudioFile)) File.Delete(input.AudioFile); } catch (Exception) { }
    }

    const string TimeoutText = "FFmpeg did not start or did not end in time.";

    static bool IsTimeout(string error)
    {
        return error != null && error.StartsWith(TimeoutText, StringComparison.Ordinal);
    }

    // Time limit of the clip encode. NVENC needs less than the length of the recording (a hung driver must not hold the
    // ZIP for 30 min). libx264 on a slow PC can need more.
    internal static int EncodeLimitMs(double seconds, bool nvenc)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        double limit = nvenc ? 60 + 2 * seconds : 300 + 10 * seconds;
        return (int)(1000 * Math.Min(limit, 6 * 3600));
    }

    // Time limit of a tool that reads the whole file once (ffprobe, the decode). Much more than it needs.
    internal static int ReadLimitMs(long bytes, double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        double limit = 120 + bytes / 10e6 + 10 * seconds;
        return (int)(1000 * Math.Min(limit, 6 * 3600));
    }

    static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }

    // Ends the process after the limit, so that the pass fails (and keeps the work file) and does not hang.
    static Timer KillAfter(Process p, int limitMs)
    {
        return new Timer(delegate { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }, null, limitMs, Timeout.Infinite);
    }

    static string EncodeClipWithFallback(AfterRecordingInput input, AfterRecordingResult r, bool keyframes, bool audio)
    {
        string error = EncodeClip(input, r, keyframes, input.ForceX264, audio);
        if (error != null && !input.ForceX264)
        {
            // An NVENC encode that hangs must not cost the time limit again.
            if (IsTimeout(error)) input.ForceX264 = true;
            // NVENC can fail (driver, other app): try libx264 once (review 2026-11-19).
            string second = EncodeClip(input, r, keyframes, true, audio);
            if (second == null) { r.Warnings.Add("NVENC failed for clip.mp4. libx264 encoded it."); return null; }
            error += " libx264: " + second;
        }
        return error;
    }

    // Writes index.md again, for example after the user typed a note.
    public static void WriteIndex(AfterRecordingInput input, AfterRecordingResult r, string note)
    {
        double length = Math.Max(0, r.EndTime - r.FirstTime);
        string text = RecordingIndex.Build(input.Start, input.UtcOffset, note, length, r.Width, r.Height,
            r.Frames, input.Titles, input.SizeLimit, r.Warnings, r.FirstTime);
        if (r.AudioRecorded)
            text += Transcript.IndexSection(r.AudioInClip, r.Transcript, r.TranscriptTool,
                r.Transcript == null ? (r.TranscriptPending ? PendingNote : r.TranscriptNote ?? "the transcript was not made") : null, r.TranscriptRowsDropped);
        RecorderCore.AtomicWrite(r.IndexPath, text);
    }

    // When the pass failed there is no index.md. The note goes into note.txt (review 2026-11-19).
    public static string SaveNote(AfterRecordingInput input, AfterRecordingResult r, string note)
    {
        string path = Path.Combine(input.Folder, NoteFileName);
        if (r != null && r.Ok)
        {
            // Another program can hold index.md for a moment. After 1 s the note goes into note.txt (the ZIP has it).
            for (int i = 0; i < 10; i++)
            {
                if (i > 0) Thread.Sleep(100);
                try { WriteIndex(input, r, note); }
                catch (Exception) { continue; }
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                return r.IndexPath;
            }
        }
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
        try
        {
            WriteZip(folder, root, temp);
            ReplaceFile(temp, zip);
        }
        catch (Exception)
        {
            // A failed ZIP must not leave its temporary file next to the recordings (live test, HANDOFF 7.22:
            // a read-only file with the ZIP name left "<name>.zip.tmp").
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            throw;
        }
        return zip;
    }

    // Puts temp in the place of path. The old file stays until the new one is complete. A chat app, a virus scanner or
    // Explorer can hold the old ZIP for a moment (a late note or transcript makes it again): try for 3 s.
    internal static void ReplaceFile(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return;
            }
            catch (Exception)
            {
                if (attempt >= 12 || !File.Exists(temp)) throw;
            }
            Thread.Sleep(250);
        }
    }

    // True when the fragmented MP4 has at least one finished fragment. FFmpeg writes a header of about 1 KB at once.
    public static bool HasVideo(string path)
    {
        try
        {
            byte[] head = new byte[65536];
            int n;
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                n = f.Read(head, 0, head.Length);
            for (int i = 0; i + 3 < n; i++)
                if (head[i] == 'm' && head[i + 1] == 'o' && head[i + 2] == 'o' && head[i + 3] == 'f') return true;
        }
        catch (Exception) { }
        return false;
    }

    // Deletes the files that the app made in the folder, and the folder when it is then empty. Other files stay (the
    // user can unpack the ZIP into the same place while the note box is open). A virus scanner or Explorer can hold a
    // file for a moment: it tries again. Returns null, or the error. stop (can be null) ends the tries.
    public static string DeleteFolder(string folder, int tries, int waitMs, Func<bool> stop)
    {
        string error = null;
        for (int i = 0; i < tries; i++)
        {
            if (i > 0) Thread.Sleep(waitMs);
            error = null;
            try
            {
                if (!Directory.Exists(folder)) return null;
                string frames = Path.Combine(folder, "frames");
                if (Directory.Exists(frames))
                {
                    foreach (string f in Directory.GetFiles(frames, "*.png"))
                        if (IsOwnFrame(Path.GetFileName(f))) File.Delete(f);
                    DeleteIfEmpty(frames);
                }
                foreach (string f in Directory.GetFiles(folder))
                    if (IsOwnFile(Path.GetFileName(f))) File.Delete(f);
                DeleteIfEmpty(folder);
            }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception e) { error = e.Message; }
            if (error == null || (stop != null && stop())) break;
        }
        return error;
    }

    static void DeleteIfEmpty(string dir)
    {
        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir, false);
    }

    internal static bool IsOwnFrame(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length < 3) return false;
        foreach (char c in stem) if (c < '0' || c > '9') return false;
        return true;
    }

    internal static bool IsOwnFile(string name)
    {
        string n = name.ToLowerInvariant();
        return n == IndexFileName || n == ClipFileName || n == NoteFileName || n == AudioMux.AudioFileName
            || n == AudioMux.TranscriptFileName || n == AudioMux.TranscriptWavName || n == "transcript_16k.srt"
            || n.StartsWith(WhisperOutputName, StringComparison.Ordinal) || n.StartsWith("work_lossless", StringComparison.Ordinal)
            || n == IndexFileName + ".tmp" || n == NoteFileName + ".tmp" || n == AudioMux.TranscriptFileName + ".tmp";
    }

    static void WriteZip(string folder, string root, string temp)
    {
        using (FileStream fs = new FileStream(temp, FileMode.CreateNew))
        using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("work_lossless", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                // Whisper can still write its file while the first ZIP is made.
                if (name.StartsWith(WhisperOutputName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("transcript_16k.srt", StringComparison.OrdinalIgnoreCase)) continue;
                // The WAV files are large, and clip.mp4 has the audio.
                if (name.Equals(AudioMux.AudioFileName, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(AudioMux.TranscriptWavName, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = file.Substring(full.Length).TrimStart('\\', '/').Replace('\\', '/');
                // PNG and MP4 are compressed already (Bran, 2026-11-22): store them. Text gets Deflate.
                string ext = Path.GetExtension(file);
                bool packed = ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
                archive.CreateEntryFromFile(file, root + "/" + rel, packed ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            }
        }
    }

    // The frame size and the time line of the work file.
    static void ReadTimes(AfterRecordingInput input, AfterRecordingResult r)
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
    }

    static void Analyze(AfterRecordingInput input, AfterRecordingResult r)
    {
        int width = r.Width, height = r.Height;
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
            + " -map 0:v:0 -vf " + RecorderCore.DecimatePad + ",mpdecimate=hi=0:lo=0:frac=0," + RecorderCore.DecimateCrop + ",showinfo -fps_mode passthrough -f rawvideo -pix_fmt bgr24 pipe:1";
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
        Timer killer = KillAfter(p, ReadLimitMs(FileLength(input.WorkFile), r.EndTime - r.FirstTime));
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
            killer.Dispose();
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
        int limit = ReadLimitMs(FileLength(file), 0);
        string text = RunToText(probe, "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 " + q, limit);
        string[] parts = text.Trim().Split(',');
        if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            throw new IOException("ffprobe gave no frame size: " + text.Trim());
        string packets = RunToText(probe, "-v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 " + q, limit);
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
        // The length is not known yet, and this decodes the file: 30 s for each MB is far above the real need.
        Timer killer = KillAfter(p, ReadLimitMs(FileLength(file) * 300, 0));
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
        killer.Dispose();
        p.Dispose();
        if (width <= 0 || height <= 0) throw new IOException("FFmpeg gave no frame size for the work file.");
        times.Sort();
        return times;
    }

    // keyframes = true: one keyframe at each settled frame and no other keyframe.
    // keyframes = false: a normal clip.mp4 (fallback after a failed pass).
    static string EncodeClip(AfterRecordingInput input, AfterRecordingResult r, bool keyframes, bool forceX264, bool audio)
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
            keyframes ? times : null, input.Fps, input.NvencAvailable && !forceX264,
            audio ? input.AudioFile : null, r.AudioShift);
        string output;
        int exit;
        if (!RecorderCore.RunFfmpeg(input.Ffmpeg, RecorderCore.JoinWindowsArguments(a), EncodeLimitMs(r.EndTime - r.FirstTime, input.NvencAvailable && !forceX264), out output, out exit))
            return TimeoutText;
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
        ChildProcessJob.Raise(p);
        return p;
    }

    static string RunToText(string exe, string args, int limitMs)
    {
        Process p = StartProcess(exe, args);
        Timer killer = KillAfter(p, limitMs);
        StringBuilder err = new StringBuilder();
        Thread t = DrainAsync(p.StandardError, err);
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        t.Join(5000);
        killer.Dispose();
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
