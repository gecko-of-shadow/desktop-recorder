// The "Folder for AI" pass for the Linux recorder. It runs the same pass as the Windows app (AfterRecording.cs,
// SettledFrames.cs) on the lossless work file that the Linux recorder wrote, then makes the ZIP.
// C# 5. Built by build-linux.sh with mcs; runs with mono (mono-runtime on Ubuntu 24.04).
//
// usage: ai_pass.exe <ffmpeg> <folder> <fps> <start, yyyy-MM-ddTHH:mm:ss> [scroll_percent] [settle_ms] [titles file]
//   <folder>/work_lossless.mp4 is the input. It is deleted after a good pass (as on Windows), unless
//   DESKTOP_RECORDER_KEEP_WORK=1. The titles file has one "<seconds>\t<window title>" line per title change.
// Output, one line each: ok=<True|False>, frames=<n>, zip=<path or empty>, warning=<text> (0 or more), error=<text>.
// Exit code 0 when the frames, clip.mp4 and index.md are written.
using System;
using System.Globalization;
using System.IO;

static class AiPass
{
    static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: ai_pass.exe ffmpeg folder fps start [scroll_percent] [settle_ms] [titles file]");
            return 2;
        }
        AfterRecordingInput input = new AfterRecordingInput();
        input.Ffmpeg = args[0];
        input.Folder = Path.GetFullPath(args[1]);
        input.WorkFile = Path.Combine(input.Folder, AfterRecording.WorkFileName);
        input.Fps = double.Parse(args[2], CultureInfo.InvariantCulture);
        input.Start = DateTime.ParseExact(args[3], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        input.UtcOffset = TimeZoneInfo.Local.GetUtcOffset(input.Start);
        StoredSettings s = new StoredSettings();
        s.Fps = input.Fps;
        if (args.Length > 4) s.ScrollPercent = int.Parse(args[4], CultureInfo.InvariantCulture);
        if (args.Length > 5) s.SettleMs = int.Parse(args[5], CultureInfo.InvariantCulture);
        input.Settings = s;
        input.Options = RecorderCore.SettledOptionsOf(s);
        input.SizeLimit = s.SizeLimit;
        input.NvencAvailable = false;   // the Linux recorder encodes with libx264
        input.Titles = new TitleTimeline();
        if (args.Length > 6 && File.Exists(args[6]))
        {
            foreach (string line in File.ReadAllLines(args[6]))
            {
                int tab = line.IndexOf('\t');
                double t;
                if (tab > 0 && double.TryParse(line.Substring(0, tab), NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                    input.Titles.Add(t, line.Substring(tab + 1));
            }
        }
        input.DeleteWorkFile = Environment.GetEnvironmentVariable("DESKTOP_RECORDER_KEEP_WORK") != "1";
        AfterRecordingResult r = AfterRecording.Run(input);
        string zip = "";
        if (r.Ok || r.ClipWritten)
        {
            try { zip = AfterRecording.MakeZip(input.Folder); }
            catch (Exception e) { r.Warnings.Add("The ZIP was not made: " + e.Message); }
        }
        Console.WriteLine("ok=" + r.Ok);
        Console.WriteLine("frames=" + r.Frames.Count);
        Console.WriteLine("zip=" + zip);
        foreach (string w in r.Warnings) Console.WriteLine("warning=" + OneLine(w));
        Console.WriteLine("error=" + OneLine(r.Error));
        return r.Ok ? 0 : 1;
    }

    static string OneLine(string s)
    {
        return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
