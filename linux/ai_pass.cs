// The "Folder for AI" pass for the Linux recorder. It runs the same pass as the Windows app (AfterRecording.cs,
// SettledFrames.cs) on the lossless work file that the Linux recorder wrote, then makes the ZIP.
// C# 5. Built by build-linux.sh with mcs; runs with mono (mono-runtime on Ubuntu 24.04).
//
// usage: ai_pass.exe <ffmpeg> <folder> <fps> <start, yyyy-MM-ddTHH:mm:ss> [key=value ...]
//   keys: scroll_percent, settle_ms, titles (file), audio (WAV file), whisper_path, whisper_model, whisper_language.
//   <folder>/work_lossless.mp4 is the input. It is deleted after a good pass (as on Windows), unless
//   DESKTOP_RECORDER_KEEP_WORK=1. The titles file has one "<seconds>\t<window title>" line per title change.
//   The audio WAV starts at video time 0 (the Linux recorder writes both from one ffmpeg on one clock).
//   Whisper: whisper_path, else whisper-cli (and the other names of AudioTranscript.cs) on PATH or next to this file.
// Output, one line each: ok=<True|False>, frames=<n>, zip=<path or empty>, warning=<text> (0 or more), error=<text>,
// sound_only_in_wav=<True|False>, and transcript=<text> with audio.
// usage: ai_pass.exe --delete-folder <folder>  (after the ZIP and the Linux steps; prints error=<text or empty>)
// Exit code 0 when the frames, clip.mp4 and index.md are written.
using System;
using System.Globalization;
using System.IO;

static class AiPass
{
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--delete-folder")
        {
            // After the ZIP: the files that the app made, and the folder when it is then empty (as on Windows).
            string error = AfterRecording.DeleteFolder(Path.GetFullPath(args[1]), 8, 500, null);
            Console.WriteLine("error=" + OneLine(error));
            return error == null ? 0 : 1;
        }
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: ai_pass.exe ffmpeg folder fps start [key=value ...]");
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
        string titles = null, audio = null, whisperPath = "", whisperModel = "", language = "auto";
        for (int i = 4; i < args.Length; i++)
        {
            int eq = args[i].IndexOf('=');
            if (eq < 1) { Console.Error.WriteLine("not key=value: " + args[i]); return 2; }
            string key = args[i].Substring(0, eq), val = args[i].Substring(eq + 1);
            if (key == "scroll_percent") s.ScrollPercent = int.Parse(val, CultureInfo.InvariantCulture);
            else if (key == "settle_ms") s.SettleMs = int.Parse(val, CultureInfo.InvariantCulture);
            else if (key == "titles") titles = val;
            else if (key == "audio") audio = val;
            else if (key == "whisper_path") whisperPath = val;
            else if (key == "whisper_model") whisperModel = val;
            else if (key == "whisper_language") language = val;
            else { Console.Error.WriteLine("unknown key: " + key); return 2; }
        }
        input.Settings = s;
        input.Options = RecorderCore.SettledOptionsOf(s);
        input.SizeLimit = s.SizeLimit;
        input.NvencAvailable = false;   // the Linux recorder encodes with libx264
        input.Titles = new TitleTimeline();
        if (titles != null && File.Exists(titles))
        {
            foreach (string line in File.ReadAllLines(titles))
            {
                int tab = line.IndexOf('\t');
                double t;
                if (tab > 0 && double.TryParse(line.Substring(0, tab), NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                    input.Titles.Add(t, line.Substring(tab + 1));
            }
        }
        if (!string.IsNullOrEmpty(audio))
        {
            input.AudioFile = Path.GetFullPath(audio);
            input.AudioStartClock = 0;
            input.ClockOffset = 0;
            string reason;
            input.Whisper = Transcript.Find(whisperPath, whisperModel, AppDomain.CurrentDomain.BaseDirectory,
                Environment.GetEnvironmentVariable("PATH") ?? "", false, out reason);
            input.WhisperReason = reason;
            input.WhisperLanguage = language;
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
        Console.WriteLine("sound_only_in_wav=" + (r.AudioRecorded && !r.AudioInClip));
        if (r.AudioRecorded)
            Console.WriteLine("transcript=" + (r.HasTranscript ? r.Transcript.Count + " segments" : OneLine(r.TranscriptNote)));
        return r.Ok ? 0 : 1;
    }

    static string OneLine(string s)
    {
        return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
