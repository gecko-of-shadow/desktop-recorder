// Audio and transcript for a recording (docs/audio-transcript.md). C# 5.
// No WinForms, no System.Drawing and no COM, so the Mono unit tests can compile this file.
// The WASAPI loopback capture itself is in AudioCapture.cs (Windows only).
//
// Time lines:
// - The WAV file starts at the QPC time when the capture started (WAV time 0). AudioCapture keeps it on the
//   QPC clock: it adds silence for gaps and drops audio that runs ahead (AudioClock.Correction).
// - RecordingSession has a clock that starts with FFmpeg, and ClockOffset = clock - FFmpeg stats time.
// - The stats time is 0 at the first video frame. The first frame has the time FirstTime in the work file.
// So WAV time 0 is at this time in the work file (and in clip.mp4, which keeps the time line with -copyts):
//   shift = (audio start on the session clock) - ClockOffset + FirstTime.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// One audio recording, as the app knows it after Stop.
sealed class AudioTake
{
    public string Path;                 // the WAV file
    public double StartClockSeconds;    // WAV time 0 on the clock of RecordingSession (negative: before FFmpeg)
    public double Seconds;              // length of the WAV file
    public double Peak = -1;            // largest sample, 0 to 1. -1 = not known (a format that is not read)
    public string Error;                // null, or why the capture stopped early (the WAV has silence after it)

    // Below this peak the audio is silent: no transcript (Whisper invents text on silence). About -66 dBFS.
    public const double SilentPeak = 0.0005;
    public bool IsSilent { get { return Peak >= 0 && Peak < SilentPeak; } }
}

// WAVE_FORMATEX (and WAVE_FORMATEXTENSIBLE) fields that the writer needs.
sealed class WaveFormat
{
    public int Tag, Channels, SampleRate, BlockAlign, BitsPerSample;
    public bool IsFloat;
    public byte[] Raw;                  // the struct as Windows gave it: 18 + cbSize bytes

    const int TagPcm = 1, TagFloat = 3, TagExtensible = 0xFFFE;

    public static WaveFormat Parse(byte[] raw)
    {
        if (raw == null || raw.Length < 16) throw new ArgumentException("short WAVEFORMATEX");
        WaveFormat f = new WaveFormat();
        f.Tag = BitConverter.ToUInt16(raw, 0);
        f.Channels = BitConverter.ToUInt16(raw, 2);
        f.SampleRate = (int)BitConverter.ToUInt32(raw, 4);
        f.BlockAlign = BitConverter.ToUInt16(raw, 12);
        f.BitsPerSample = BitConverter.ToUInt16(raw, 14);
        int tag = f.Tag;
        // WAVE_FORMATEXTENSIBLE: the SubFormat GUID starts at byte 24. Its first 2 bytes are the real tag.
        if (tag == TagExtensible && raw.Length >= 40) tag = BitConverter.ToUInt16(raw, 24);
        f.IsFloat = tag == TagFloat;
        if (tag != TagPcm && tag != TagFloat) throw new ArgumentException("unknown audio format tag " + tag.ToString(CultureInfo.InvariantCulture));
        if (f.Channels < 1 || f.SampleRate < 1000 || f.BlockAlign < 1) throw new ArgumentException("bad audio format");
        f.Raw = raw;
        return f;
    }

    // A plain format for tests and the fallback: 16-bit PCM or 32-bit float, no extension.
    public static WaveFormat Make(int rate, int channels, bool float32)
    {
        int bits = float32 ? 32 : 16;
        byte[] raw = new byte[18];
        Put16(raw, 0, float32 ? TagFloat : TagPcm);
        Put16(raw, 2, channels);
        Put32(raw, 4, rate);
        Put32(raw, 8, rate * channels * bits / 8);
        Put16(raw, 12, channels * bits / 8);
        Put16(raw, 14, bits);
        Put16(raw, 16, 0);
        return Parse(raw);
    }

    internal static void Put16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    internal static void Put32(byte[] b, int at, long v)
    {
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
    }
}

// Writes a WAV file while the audio comes in. The header gets the real sizes each second (PatchHeader) and at
// Close, so a file of a killed app still has (almost) the right length.
sealed class WavWriter : IDisposable
{
    readonly FileStream stream;
    readonly WaveFormat format;
    readonly int dataStart;
    long dataBytes;
    double peak;

    public WavWriter(string path, WaveFormat format)
    {
        this.format = format;
        stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] fmt = format.Raw;
        int fmtLength = fmt.Length;
        byte[] head = new byte[12 + 8 + fmtLength + (fmtLength & 1) + 8];
        Ascii(head, 0, "RIFF");
        Ascii(head, 8, "WAVE");
        Ascii(head, 12, "fmt ");
        WaveFormat.Put32(head, 16, fmtLength);
        Buffer.BlockCopy(fmt, 0, head, 20, fmtLength);
        int at = 20 + fmtLength + (fmtLength & 1);
        Ascii(head, at, "data");
        dataStart = at + 8;
        stream.Write(head, 0, head.Length);
        PatchHeader();
    }

    public WaveFormat Format { get { return format; } }
    public long Frames { get { return dataBytes / format.BlockAlign; } }
    public double Seconds { get { return Frames / (double)format.SampleRate; } }
    // Largest sample (0 to 1), or -1 when the format is not 16-bit PCM or 32-bit float.
    public double Peak { get { return (format.IsFloat && format.BitsPerSample == 32) || (!format.IsFloat && format.BitsPerSample == 16) ? peak : -1; } }

    public void Write(byte[] data, int offset, int count)
    {
        count -= count % format.BlockAlign;
        if (count <= 0) return;
        UpdatePeak(data, offset, count);
        stream.Write(data, offset, count);
        dataBytes += count;
    }

    public void WriteSilence(long frames)
    {
        if (frames <= 0) return;
        byte[] zero = new byte[Math.Min(65536, format.BlockAlign * 4096)];
        long bytes = frames * format.BlockAlign;
        while (bytes > 0)
        {
            int n = (int)Math.Min(zero.Length, bytes);
            stream.Write(zero, 0, n);
            bytes -= n;
            dataBytes += n;
        }
    }

    void UpdatePeak(byte[] data, int offset, int count)
    {
        if (format.IsFloat && format.BitsPerSample == 32)
        {
            for (int i = offset; i + 4 <= offset + count; i += 4)
            {
                float v = BitConverter.ToSingle(data, i);
                if (v < 0) v = -v;
                if (v > peak && !float.IsNaN(v) && !float.IsInfinity(v)) peak = v;
            }
        }
        else if (!format.IsFloat && format.BitsPerSample == 16)
        {
            for (int i = offset; i + 2 <= offset + count; i += 2)
            {
                int v = (short)(data[i] | (data[i + 1] << 8));
                if (v < 0) v = -v;
                double d = v / 32768.0;
                if (d > peak) peak = d;
            }
        }
    }

    public void PatchHeader()
    {
        long position = stream.Position;
        byte[] four = new byte[4];
        long riff = dataStart - 8 + dataBytes;
        WaveFormat.Put32(four, 0, riff > uint.MaxValue ? uint.MaxValue : riff);
        stream.Seek(4, SeekOrigin.Begin);
        stream.Write(four, 0, 4);
        WaveFormat.Put32(four, 0, dataBytes > uint.MaxValue ? uint.MaxValue : dataBytes);
        stream.Seek(dataStart - 4, SeekOrigin.Begin);
        stream.Write(four, 0, 4);
        stream.Seek(position, SeekOrigin.Begin);
        stream.Flush();
    }

    public void Dispose()
    {
        try { PatchHeader(); } catch (Exception) { }
        stream.Dispose();
    }

    static void Ascii(byte[] b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
    }
}

static class AudioClock
{
    // Frames to add (positive, as silence) or to drop (negative) before a packet, so the WAV file stays on the QPC
    // clock. framesWritten: frames in the file. packetSeconds - startSeconds: where the packet must start.
    // WASAPI loopback gives no packets while the PC plays nothing, so silence fills that time.
    // A difference within tolerance is not changed (normal jitter).
    public static long Correction(long framesWritten, double startSeconds, double packetSeconds, int sampleRate, double toleranceSeconds)
    {
        double want = (packetSeconds - startSeconds) * sampleRate;
        double diff = want - framesWritten;
        if (Math.Abs(diff) <= toleranceSeconds * sampleRate) return 0;
        long d = (long)Math.Round(diff);
        if (d < 0 && -d > framesWritten) d = -framesWritten;
        return d;
    }

    // WAV time 0 on the time line of the work file (see the top of this file).
    public static double VideoTimeOfAudioStart(double audioStartClock, double clockOffset, double firstFrameTime)
    {
        if (double.IsNaN(clockOffset)) clockOffset = 0;
        return audioStartClock - clockOffset + firstFrameTime;
    }
}

static class AudioMux
{
    public const string AudioFileName = "audio_system.wav";
    public const string TranscriptWavName = "transcript_16k.wav";
    public const string TranscriptFileName = "transcript.srt";

    static string Num(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

    // Audio filter that puts WAV time 0 at the time shift of the output (seconds). Positive: silence first.
    // Negative: the first part of the audio is cut. The output audio starts at time 0.
    public static string AlignFilter(double shift)
    {
        if (shift >= 0.0005)
            return "adelay=delays=" + ((long)Math.Round(shift * 1000)).ToString(CultureInfo.InvariantCulture) + ":all=1";
        if (shift <= -0.0005)
            return "atrim=start=" + Num(-shift) + ",asetpts=PTS-STARTPTS";
        return "anull";
    }

    // Adds the audio to a finished video file without a new video encode: stream copy, audio as AAC.
    // Used for the MP4 output and for a work file that stays in the folder.
    public static string[] BuildRemuxArguments(string video, string audio, double shift, string output)
    {
        return BuildRemuxArguments(video, audio, shift, output, 0);
    }

    // maxSeconds > 0: the audio ends there. A split MP4 file must get only its own part of the audio: without the
    // end, segment 0 of a 10 s test got all 10 s of audio for 4 s of video (checked with FFmpeg 6.1, 2026-11-20).
    // Not -shortest: it would cut the video when the audio is shorter.
    public static string[] BuildRemuxArguments(string video, string audio, double shift, string output, double maxSeconds)
    {
        List<string> a = new List<string>();
        a.Add("-hide_banner"); a.Add("-loglevel"); a.Add("error"); a.Add("-y");
        a.Add("-i"); a.Add(video);
        a.Add("-i"); a.Add(audio);
        a.Add("-map"); a.Add("0:v:0");
        a.Add("-map"); a.Add("1:a:0");
        a.Add("-c:v"); a.Add("copy");
        string filter = AlignFilter(shift);
        if (maxSeconds > 0) filter += ",atrim=end=" + Num(maxSeconds);
        a.Add("-af"); a.Add(filter);
        AddAac(a);
        a.Add("-movflags"); a.Add("+faststart");
        a.Add(output);
        return a.ToArray();
    }

    public static void AddAac(List<string> a)
    {
        a.Add("-c:a"); a.Add("aac");
        a.Add("-b:a"); a.Add("160k");
    }

    // 16 kHz mono 16-bit PCM for Whisper, already on the time line of clip.mp4. Then the transcript times need
    // no change.
    public static string[] BuildTranscriptWavArguments(string audio, double shift, string output)
    {
        return new string[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", audio,
            "-af", AlignFilter(shift) + ",aresample=16000",
            "-ac", "1", "-c:a", "pcm_s16le", output
        };
    }

    // The time of segment n (0, 1, 2 ...) of a split MP4 recording on the stats clock: n x split minutes.
    public static double SegmentStart(int index, int splitMinutes)
    {
        return splitMinutes > 0 ? index * splitMinutes * 60.0 : 0;
    }
}

// A Whisper program that the app found.
sealed class WhisperTool
{
    public const string KindCpp = "whisper.cpp";        // whisper-cli: -m model -f wav -osrt -of base
    public const string KindOpenAi = "openai-style";    // whisper, whisper-ctranslate2, faster-whisper-xxl
    public string Path;
    public string Kind;
    public string Model;                                // whisper.cpp: the ggml file. Other kinds: a name or null

    public string Describe()
    {
        string name = System.IO.Path.GetFileName(Path);
        string model = Model == null ? "default model" : "model " + System.IO.Path.GetFileName(Model);
        return name + " (" + Kind + ", " + model + ")";
    }
}

sealed class TranscriptSegment
{
    public double Start, End;
    public string Text;
}

static class Transcript
{
    // Names that the search looks for, in this order. Without ".exe" on other platforms (tests).
    static readonly string[] Names = { "whisper-cli", "faster-whisper-xxl", "whisper-ctranslate2", "whisper" };
    static readonly string[] SubFolders = { "", "whisper", "whisper.cpp", "Faster-Whisper-XXL" };
    // whisper.cpp model files, best first for speech with a normal CPU or GPU.
    static readonly string[] ModelOrder = { "large-v3-turbo", "medium", "small", "base", "large", "tiny" };

    // Finds Whisper. configuredPath and configuredModel come from the settings (empty = search).
    // Returns null and a reason when no usable program is found.
    public static WhisperTool Find(string configuredPath, string configuredModel, string appDirectory,
        string pathVariable, bool windows, out string reason)
    {
        reason = null;
        string exe = windows ? ".exe" : "";
        string path = null;
        if (!string.IsNullOrEmpty(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                reason = "whisper_path in the settings does not exist: " + configuredPath;
                return null;
            }
            path = configuredPath;
        }
        if (path == null && !string.IsNullOrEmpty(appDirectory))
        {
            foreach (string name in Names)
            {
                if (name == "whisper") continue;   // "whisper" next to the app is too general a name
                foreach (string sub in SubFolders)
                {
                    string p = System.IO.Path.Combine(System.IO.Path.Combine(appDirectory, sub), name + exe);
                    if (File.Exists(p)) { path = p; break; }
                }
                if (path != null) break;
            }
        }
        if (path == null && !string.IsNullOrEmpty(pathVariable))
        {
            char sep = windows ? ';' : ':';
            foreach (string name in Names)
            {
                foreach (string dir in pathVariable.Split(sep))
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    string p;
                    try { p = System.IO.Path.Combine(d, name + exe); } catch (ArgumentException) { continue; }
                    if (File.Exists(p)) { path = p; break; }
                }
                if (path != null) break;
            }
        }
        if (path == null)
        {
            reason = "no Whisper program was found (whisper-cli, faster-whisper-xxl, whisper-ctranslate2 or whisper)";
            return null;
        }
        WhisperTool t = new WhisperTool();
        t.Path = path;
        string file = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        t.Kind = file.StartsWith("whisper-cli", StringComparison.Ordinal) || file == "main" ? WhisperTool.KindCpp : WhisperTool.KindOpenAi;
        if (t.Kind == WhisperTool.KindOpenAi)
        {
            t.Model = string.IsNullOrEmpty(configuredModel) ? null : configuredModel;
            return t;
        }
        // whisper.cpp needs a model file.
        string toolDir = System.IO.Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(configuredModel))
        {
            string m = System.IO.Path.IsPathRooted(configuredModel) ? configuredModel : System.IO.Path.Combine(toolDir, configuredModel);
            if (!File.Exists(m))
            {
                reason = "whisper_model in the settings does not exist: " + m;
                return null;
            }
            t.Model = m;
            return t;
        }
        List<string> dirs = new List<string>();
        dirs.Add(toolDir);
        dirs.Add(System.IO.Path.Combine(toolDir, "models"));
        if (!string.IsNullOrEmpty(appDirectory))
        {
            dirs.Add(System.IO.Path.Combine(appDirectory, "models"));
            dirs.Add(System.IO.Path.Combine(System.IO.Path.Combine(appDirectory, "whisper"), "models"));
        }
        List<string> found = new List<string>();
        foreach (string d in dirs)
        {
            try { if (Directory.Exists(d)) found.AddRange(Directory.GetFiles(d, "ggml-*.bin")); }
            catch (Exception) { }
        }
        t.Model = PickModel(found);
        if (t.Model == null)
        {
            reason = System.IO.Path.GetFileName(path) + " was found, but no model file (ggml-*.bin) next to it or in a models folder";
            return null;
        }
        return t;
    }

    internal static string PickModel(List<string> files)
    {
        if (files == null || files.Count == 0) return null;
        // "ggml-small.en.bin" counts as "small"; quantized names such as "ggml-medium-q5_0.bin" also count.
        foreach (string want in ModelOrder)
        {
            List<string> match = new List<string>();
            foreach (string f in files)
            {
                string n = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.StartsWith("ggml-")) n = n.Substring(5);
                if (n == want || n.StartsWith(want + ".") || n.StartsWith(want + "-")) match.Add(f);
            }
            if (match.Count > 0)
            {
                match.Sort(StringComparer.OrdinalIgnoreCase);
                return match[0];
            }
        }
        List<string> all = new List<string>(files);
        all.Sort(StringComparer.OrdinalIgnoreCase);
        return all[0];
    }

    // The arguments for one run, and the SRT file that the run writes.
    public static string[] BuildArguments(WhisperTool tool, string wav, string outputDirectory, string language, out string srtPath)
    {
        string lang = string.IsNullOrEmpty(language) ? "auto" : language.Trim();
        List<string> a = new List<string>();
        if (tool.Kind == WhisperTool.KindCpp)
        {
            string outBase = System.IO.Path.Combine(outputDirectory, "transcript_whisper");
            a.Add("-m"); a.Add(tool.Model);
            a.Add("-f"); a.Add(wav);
            a.Add("-l"); a.Add(lang);
            a.Add("-osrt");
            a.Add("-of"); a.Add(outBase);
            a.Add("-np");
            srtPath = outBase + ".srt";
            return a.ToArray();
        }
        a.Add(wav);
        a.Add("--output_format"); a.Add("srt");
        a.Add("--output_dir"); a.Add(outputDirectory);
        if (tool.Model != null) { a.Add("--model"); a.Add(tool.Model); }
        if (lang != "auto") { a.Add("--language"); a.Add(lang); }
        srtPath = System.IO.Path.Combine(outputDirectory, System.IO.Path.GetFileNameWithoutExtension(wav) + ".srt");
        return a.ToArray();
    }

    // The longest time that Whisper may run: 2 min plus 3 x the audio length, at most 1 h.
    public static int TimeoutMs(double audioSeconds)
    {
        double s = 120 + 3 * Math.Max(0, audioSeconds);
        return (int)(Math.Min(3600, s) * 1000);
    }

    // Reads SRT text ("1", "00:00:01,200 --> 00:00:04,000", text lines, a blank line). Empty segments and
    // whisper.cpp "[BLANK_AUDIO]" are left out.
    public static List<TranscriptSegment> ParseSrt(string text)
    {
        List<TranscriptSegment> list = new List<TranscriptSegment>();
        if (text == null) return list;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim().TrimStart('﻿');
            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) { i++; continue; }
            double start = ParseSrtTime(line.Substring(0, arrow));
            string rest = line.Substring(arrow + 3).Trim();
            int space = rest.IndexOf(' ');
            double end = ParseSrtTime(space > 0 ? rest.Substring(0, space) : rest);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                // A number line right before the next time line belongs to the next block.
                if (i + 1 < lines.Length && lines[i + 1].IndexOf("-->", StringComparison.Ordinal) >= 0 && IsNumber(lines[i].Trim())) break;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(lines[i].Trim());
                i++;
            }
            string t = sb.ToString().Trim();
            if (start < 0 || end < 0 || t.Length == 0 || t == "[BLANK_AUDIO]") continue;
            TranscriptSegment s = new TranscriptSegment();
            s.Start = start;
            s.End = Math.Max(start, end);
            s.Text = t;
            list.Add(s);
        }
        return list;
    }

    static bool IsNumber(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    // "00:01:02,500" or "00:01:02.500" or "01:02.500". -1 when it is not a time.
    internal static double ParseSrtTime(string s)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        string[] parts = s.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return -1;
        double total = 0;
        for (int k = 0; k < parts.Length; k++)
        {
            double v;
            if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0) return -1;
            total = total * 60 + v;
        }
        return total;
    }

    public static string FormatSrt(IList<TranscriptSegment> segments)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(SrtTime(segments[i].Start)).Append(" --> ").Append(SrtTime(segments[i].End)).Append('\n');
            sb.Append(segments[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }

    static string SrtTime(double t)
    {
        long ms = (long)Math.Round(Math.Max(0, t) * 1000);
        return (ms / 3600000).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (ms / 60000 % 60).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (ms / 1000 % 60).ToString("00", CultureInfo.InvariantCulture) + ","
            + (ms % 1000).ToString("000", CultureInfo.InvariantCulture);
    }

    // The section at the end of index.md. audioInClip: clip.mp4 has the audio track.
    // segments == null: no transcript; note says why.
    public static string IndexSection(bool audioInClip, IList<TranscriptSegment> segments, string toolText, string note)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("\n## Audio and transcript\n");
        sb.Append(audioInClip ? "clip.mp4 has the system audio (the sound that the PC played).\n"
                              : "The system audio was recorded, but it is not in clip.mp4.\n");
        if (segments == null)
        {
            sb.Append("No transcript: ").Append(RecordingIndex.Cell(note ?? "not made")).Append(".\n");
            return sb.ToString();
        }
        sb.Append("Transcript: ").Append(RecordingIndex.Cell(toolText ?? "Whisper"))
          .Append(". A machine transcript: some words can be wrong. Times are seconds on the time line of clip.mp4.\n");
        if (segments.Count == 0)
        {
            sb.Append("Whisper found no speech.\n");
            return sb.ToString();
        }
        sb.Append('\n');
        sb.Append("| Start | End | Text |\n");
        sb.Append("|---|---|---|\n");
        foreach (TranscriptSegment s in segments)
        {
            sb.Append("| ").Append(s.Start.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s | ")
              .Append(s.End.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s | ")
              .Append(RecordingIndex.Cell(s.Text)).Append(" |\n");
        }
        return sb.ToString();
    }
}
