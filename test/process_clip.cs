// Test tool: runs the pass of AfterRecording.cs on any video file (offline tests, condition B).
// It does not change or delete the input file, unless AUDIO_INTO_WORK=1 or DELETE_WORK=1 is set (see below).
// Build (PowerShell, repo folder):
//   & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:"$env:TEMP\process_clip.exe" /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll test\process_clip.cs SettledFrames.cs AfterRecording.cs RecorderCore.cs RecordingSession.cs AudioTranscript.cs
// Run:
//   & "$env:TEMP\process_clip.exe" <ffmpeg.exe> <input.mp4> <output folder> <fps> [scroll percent] [settle ms]
// Audio test (docs/audio-transcript.md), environment variables:
//   AUDIO_FILE = a WAV file, AUDIO_START = WAV time 0 on the clock (s, default 0), CLOCK_OFFSET (s, default 0),
//   AUDIO_PEAK (default -1 = not known), WHISPER_PATH, WHISPER_MODEL, WHISPER_LANGUAGE,
//   AUDIO_INTO_WORK=1 (adds the audio to the input file: this changes the input),
//   DELETE_WORK=1 (deletes the input after a good pass, as the app does: give a copy).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

static class ProcessClip
{
    static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: process_clip ffmpeg input folder fps [scroll%] [settle_ms]"); return 2; }
        AfterRecordingInput input = new AfterRecordingInput();
        input.Ffmpeg = args[0];
        input.WorkFile = Path.GetFullPath(args[1]);
        input.Folder = Path.GetFullPath(args[2]);
        input.Fps = double.Parse(args[3], CultureInfo.InvariantCulture);
        StoredSettings s = new StoredSettings();
        if (args.Length > 4) s.ScrollPercent = int.Parse(args[4], CultureInfo.InvariantCulture);
        if (args.Length > 5) s.SettleMs = int.Parse(args[5], CultureInfo.InvariantCulture);
        input.Options = RecorderCore.SettledOptionsOf(s);
        if (args.Length > 6) input.Options.PixelTolerance = int.Parse(args[6], CultureInfo.InvariantCulture);
        if (args.Length > 7) input.Options.MinChangedPixels = int.Parse(args[7], CultureInfo.InvariantCulture);
        string q = Environment.GetEnvironmentVariable("CLIP_QUALITY");
        if (!string.IsNullOrEmpty(q)) s.Quality = int.Parse(q, CultureInfo.InvariantCulture);
        input.Settings = s;
        input.Start = File.GetCreationTime(input.WorkFile);
        input.UtcOffset = TimeZoneInfo.Local.GetUtcOffset(input.Start);
        input.Titles = new TitleTimeline();
        // DELETE_WORK=1: as in the app, the pass deletes the input after a good pass. Use a copy.
        input.DeleteWorkFile = Environment.GetEnvironmentVariable("DELETE_WORK") == "1";
        string audio = Environment.GetEnvironmentVariable("AUDIO_FILE");
        if (!string.IsNullOrEmpty(audio))
        {
            input.AudioFile = Path.GetFullPath(audio);
            input.AudioStartClock = EnvDouble("AUDIO_START", 0);
            input.ClockOffset = EnvDouble("CLOCK_OFFSET", 0);
            input.AudioPeak = EnvDouble("AUDIO_PEAK", -1);
            // AUDIO_INTO_WORK=1 tests the step that adds the audio to a kept work file. It changes the input file.
            input.AudioIntoKeptWorkFile = Environment.GetEnvironmentVariable("AUDIO_INTO_WORK") == "1";
            string reason;
            input.Whisper = Transcript.Find(Environment.GetEnvironmentVariable("WHISPER_PATH"), Environment.GetEnvironmentVariable("WHISPER_MODEL"),
                "", "", Path.DirectorySeparatorChar == '\\', out reason);
            input.WhisperReason = reason;
            string lang = Environment.GetEnvironmentVariable("WHISPER_LANGUAGE");
            if (!string.IsNullOrEmpty(lang)) input.WhisperLanguage = lang;
        }
        // TRANSCRIPT_WAIT_MS: as in the app (8000), the pass does not wait longer for Whisper. Default: until it is done.
        input.TranscriptWaitMs = (int)EnvDouble("TRANSCRIPT_WAIT_MS", -1);
        Directory.CreateDirectory(input.Folder);
        if (Environment.GetEnvironmentVariable("SETTLED_DEBUG") == "1") SettledFrameFinder.Debug = delegate(string m) { Console.WriteLine("dbg " + m); };
        DateTime t0 = DateTime.UtcNow;
        AfterRecordingResult r = AfterRecording.Run(input);
        double total = (DateTime.UtcNow - t0).TotalSeconds;
        Console.WriteLine("t_compare=" + SettledFrameFinder.TCompare.Elapsed.TotalSeconds + " t_shift=" + SettledFrameFinder.TShift.Elapsed.TotalSeconds + " n_shift=" + SettledFrameFinder.NShift);
        // Coverage: each frame shows content rows [offset, offset + height). Rows seen in all frames: [min, max + height).
        long seenFrom = r.MinOffset, seenTo = r.MaxOffset + r.Height;
        List<long[]> spans = new List<long[]>();
        foreach (SettledFrame f in r.Frames) spans.Add(new long[] { f.ContentOffset, f.ContentOffset + r.Height });
        spans.Sort(delegate(long[] a, long[] b) { return a[0].CompareTo(b[0]); });
        long covered = 0, cursor = seenFrom;
        foreach (long[] sp in spans)
        {
            long a0 = Math.Max(sp[0], cursor), a1 = Math.Min(sp[1], seenTo);
            if (a1 > a0) { covered += a1 - a0; cursor = a1; }
        }
        long seen = seenTo - seenFrom;
        Console.WriteLine("ok=" + r.Ok + " clip=" + r.ClipWritten + " error=" + r.Error);
        Console.WriteLine("size=" + r.Width + "x" + r.Height + " decoded=" + r.DecodedFrames + " written=" + r.Frames.Count
            + " first=" + r.FirstTime.ToString("0.000", CultureInfo.InvariantCulture) + " end=" + r.EndTime.ToString("0.000", CultureInfo.InvariantCulture));
        Console.WriteLine("analyze_s=" + r.AnalyzeSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + " encode_s=" + r.EncodeSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + " total_s=" + total.ToString("0.00", CultureInfo.InvariantCulture) + " clip_bytes=" + r.ClipBytes);
        Console.WriteLine("scroll_seen_px=" + seen + " covered_px=" + covered + " missed_px=" + (seen - covered)
            + " min_off=" + r.MinOffset + " max_off=" + r.MaxOffset);
        foreach (string w in r.Warnings) Console.WriteLine("warning=" + w);
        if (r.AudioRecorded)
            Console.WriteLine("audio_in_clip=" + r.AudioInClip + " audio_shift=" + r.AudioShift.ToString("0.000", CultureInfo.InvariantCulture)
                + " transcript=" + (r.Transcript == null ? "none (" + r.TranscriptNote + ")" : r.Transcript.Count + " segments, " + r.TranscriptRowsDropped + " left out")
                + " transcript_s=" + r.TranscriptSeconds.ToString("0.00", CultureInfo.InvariantCulture));
        if (r.TranscriptPending)
        {
            Console.WriteLine("zip_ready_s=" + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " transcript pending, zip " + AfterRecording.MakeZip(input.Folder));
            r.Work.Done.WaitOne();
            AfterRecording.AddLateTranscript(input, r, null);
            Console.WriteLine("zip_with_transcript_s=" + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " segments " + (r.Transcript == null ? 0 : r.Transcript.Count) + ", zip " + AfterRecording.MakeZip(input.Folder));
        }
        if (Environment.GetEnvironmentVariable("NOTE_ZIP") == "1")
        {
            Console.WriteLine("note_file=" + AfterRecording.SaveNote(input, r, "test note from process_clip"));
            string zip = AfterRecording.MakeZip(input.Folder);
            using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip))
                foreach (System.IO.Compression.ZipArchiveEntry e in z.Entries) Console.WriteLine("zip_entry=" + e.FullName + " " + e.Length);
        }
        foreach (SettledFrame f in r.Frames)
            Console.WriteLine("frame " + f.Number + " t=" + f.Time.ToString("0.000", CultureInfo.InvariantCulture) + " kind=" + f.Kind
                + " scroll=" + f.ScrollPixels + " off=" + f.ContentOffset + " changed=" + f.ChangedFraction.ToString("0.0000", CultureInfo.InvariantCulture)
                + " box=" + f.BoxX + "," + f.BoxY + "," + f.BoxWidth + "x" + f.BoxHeight);
        return r.Ok ? 0 : 1;
    }

    static double EnvDouble(string name, double fallback)
    {
        string v = Environment.GetEnvironmentVariable(name);
        double d;
        return !string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
    }
}
