// Test tool: runs the pass of AfterRecording.cs on any video file (offline tests, condition B).
// It does not change or delete the input file.
// Build (PowerShell, repo folder):
//   & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:"$env:TEMP\process_clip.exe" /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll test\process_clip.cs SettledFrames.cs AfterRecording.cs RecorderCore.cs RecordingSession.cs
// Run:
//   & "$env:TEMP\process_clip.exe" <ffmpeg.exe> <input.mp4> <output folder> <fps> [scroll percent] [settle ms]
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
        input.DeleteWorkFile = false;
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
}
