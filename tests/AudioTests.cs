// Unit tests for audio and transcript (AudioTranscript.cs, docs/audio-transcript.md). Called from CoreTests.Main.
// The WASAPI capture (AudioCapture.cs) needs Windows and is not tested here.
using System;
using System.Collections.Generic;
using System.IO;

static class AudioTests
{
    static int failures;

    static void Expect(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL audio: " + name);
    }

    public static int Run()
    {
        failures = 0;
        TestWaveFormat();
        TestWavWriter();
        TestClock();
        TestFilters();
        TestClipArgsWithAudio();
        TestSrt();
        TestIndexSection();
        TestFindWhisper();
        TestWhisperArguments();
        TestAiMessage();
        return failures;
    }

    static byte[] ExtensibleFloat()
    {
        byte[] raw = new byte[40];
        WaveFormat.Put16(raw, 0, 0xFFFE);
        WaveFormat.Put16(raw, 2, 2);
        WaveFormat.Put32(raw, 4, 48000);
        WaveFormat.Put32(raw, 8, 48000 * 8);
        WaveFormat.Put16(raw, 12, 8);
        WaveFormat.Put16(raw, 14, 32);
        WaveFormat.Put16(raw, 16, 22);
        WaveFormat.Put16(raw, 18, 32);
        WaveFormat.Put32(raw, 20, 3);
        Buffer.BlockCopy(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray(), 0, raw, 24, 16);
        return raw;
    }

    static void TestWaveFormat()
    {
        WaveFormat f = WaveFormat.Parse(ExtensibleFloat());
        Expect(f.IsFloat && f.Channels == 2 && f.SampleRate == 48000 && f.BlockAlign == 8 && f.BitsPerSample == 32, "extensible float");
        WaveFormat p = WaveFormat.Make(44100, 1, false);
        Expect(!p.IsFloat && p.BlockAlign == 2 && p.SampleRate == 44100, "pcm 16");
        bool threw = false;
        try { byte[] bad = ExtensibleFloat(); WaveFormat.Put16(bad, 24, 0x55); WaveFormat.Parse(bad); }
        catch (ArgumentException) { threw = true; }
        Expect(threw, "unknown sub format rejected");
    }

    static void TestWavWriter()
    {
        string path = Path.Combine(Path.GetTempPath(), "dr_audio_test_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WaveFormat f = WaveFormat.Parse(ExtensibleFloat());
            double peak, seconds;
            long frames;
            using (WavWriter w = new WavWriter(path, f))
            {
                w.WriteSilence(4800);                          // 0.1 s
                byte[] data = new byte[8 * 10];
                Buffer.BlockCopy(BitConverter.GetBytes(-0.25f), 0, data, 8 * 3, 4);
                w.Write(data, 0, data.Length - 3);             // a part frame at the end is not written
                frames = w.Frames;
                seconds = w.Seconds;
                peak = w.Peak;
            }
            Expect(frames == 4800 + 9, "frames counted, part frame dropped: " + frames);
            Expect(Math.Abs(seconds - 4809 / 48000.0) < 1e-9, "seconds");
            Expect(Math.Abs(peak - 0.25) < 1e-6, "peak of float samples: " + peak);
            byte[] file = File.ReadAllBytes(path);
            int dataBytes = 4809 * 8;
            Expect(file.Length == 12 + 8 + 40 + 8 + dataBytes, "file length " + file.Length);
            Expect(Text(file, 0) == "RIFF" && Text(file, 8) == "WAVE" && Text(file, 12) == "fmt " && Text(file, 60) == "data", "chunk names");
            Expect(BitConverter.ToInt32(file, 16) == 40 && BitConverter.ToUInt16(file, 20) == 0xFFFE, "fmt chunk is the mix format");
            Expect(BitConverter.ToInt32(file, 64) == dataBytes && BitConverter.ToInt32(file, 4) == file.Length - 8, "sizes patched");
            WaveFormat p = WaveFormat.Make(16000, 1, false);
            using (WavWriter w = new WavWriter(path, p))
            {
                w.Write(new byte[] { 0x00, 0x40, 0x00, 0xC0 }, 0, 4);   // 16384, -16384
                Expect(Math.Abs(w.Peak - 0.5) < 1e-6, "peak of 16-bit samples");
            }
        }
        finally { try { File.Delete(path); } catch (Exception) { } }
    }

    static string Text(byte[] b, int at)
    {
        return new string(new char[] { (char)b[at], (char)b[at + 1], (char)b[at + 2], (char)b[at + 3] });
    }

    static void TestClock()
    {
        // 48 kHz, start at QPC 100 s. 1 s of audio written, the next packet comes at 102 s: 1 s of silence first.
        Expect(AudioClock.Correction(48000, 100, 102, 48000, 0.05) == 48000, "gap filled");
        Expect(AudioClock.Correction(48000, 100, 101.03, 48000, 0.05) == 0, "jitter left alone");
        Expect(AudioClock.Correction(48000, 100, 100.9, 48000, 0.05) == -4800, "audio ahead is dropped");
        Expect(AudioClock.Correction(100, 100, 50, 48000, 0.05) == -100, "never drop more than was written");
        Expect(AudioClock.Correction(0, 100, 103.5, 48000, 0) == 168000, "silence up to the stop");
        // WAV time 0: 0.35 s before the FFmpeg clock, offset 0.10, first frame at 0.0 -> -0.45 s.
        Expect(Math.Abs(AudioClock.VideoTimeOfAudioStart(-0.35, 0.10, 0) + 0.45) < 1e-9, "shift");
        Expect(Math.Abs(AudioClock.VideoTimeOfAudioStart(0.2, double.NaN, 0.5) - 0.7) < 1e-9, "shift without clock offset");
        TestSilentPeak();
    }

    static void TestSilentPeak()
    {
        AudioTake t = new AudioTake();
        Expect(!t.IsSilent, "unknown peak is not silent");
        t.Peak = 0.0001;
        Expect(t.IsSilent, "low peak is silent");
        t.Peak = 0.01;
        Expect(!t.IsSilent, "-40 dBFS is not silent");
    }

    static void TestFilters()
    {
        Expect(AudioMux.AlignFilter(0.3) == "adelay=delays=300:all=1", "delay");
        Expect(AudioMux.AlignFilter(-0.45) == "atrim=start=0.450,asetpts=PTS-STARTPTS", "trim");
        Expect(AudioMux.AlignFilter(0.0001) == "anull", "no shift");
        string r = string.Join(" ", AudioMux.BuildRemuxArguments("v.mp4", "a.wav", -0.2, "o.mp4"));
        Expect(r == "-hide_banner -loglevel error -y -i v.mp4 -i a.wav -map 0:v:0 -map 1:a:0 -c:v copy -af atrim=start=0.200,asetpts=PTS-STARTPTS -c:a aac -b:a 160k -movflags +faststart o.mp4", "remux: " + r);
        string seg = string.Join(" ", AudioMux.BuildRemuxArguments("v.mp4", "a.wav", -1800.25, "o.mp4", 1800));
        Expect(seg.IndexOf("-af atrim=start=1800.250,asetpts=PTS-STARTPTS,atrim=end=1800.000 ", StringComparison.Ordinal) >= 0, "split part ends at the cut: " + seg);
        string w = string.Join(" ", AudioMux.BuildTranscriptWavArguments("a.wav", 0.5, "t.wav"));
        Expect(w.IndexOf("-af adelay=delays=500:all=1,aresample=16000 -ac 1 -c:a pcm_s16le t.wav", StringComparison.Ordinal) >= 0, "16 kHz: " + w);
        Expect(AudioMux.SegmentStart(0, 30) == 0 && AudioMux.SegmentStart(2, 30) == 3600 && AudioMux.SegmentStart(3, 0) == 0, "segment start");
    }

    static void TestClipArgsWithAudio()
    {
        StoredSettings s = new StoredSettings();
        List<double> t = new List<double>(new double[] { 0.0, 2.0 });
        string[] a = RecorderCore.BuildClipArguments(s, "w.mp4", "c.mp4", t, 15, true, "a.wav", -0.45);
        string j = string.Join(" ", a);
        Expect(j.IndexOf("-copyts -i w.mp4 -i a.wav -map 0:v:0 -map 1:a:0 -af atrim=start=0.450,asetpts=PTS-STARTPTS -c:a aac", StringComparison.Ordinal) >= 0, "clip with audio: " + j);
        Expect(j.IndexOf("-force_key_frames 0.000,1.990", StringComparison.Ordinal) >= 0 && a[a.Length - 1] == "c.mp4", "keyframes kept with audio");
        string none = string.Join(" ", RecorderCore.BuildClipArguments(s, "w.mp4", "c.mp4", t, 15, true));
        Expect(none.IndexOf("a.wav", StringComparison.Ordinal) < 0 && none.IndexOf("1:a", StringComparison.Ordinal) < 0 && none.IndexOf("-c:a", StringComparison.Ordinal) < 0, "clip without audio unchanged");
    }

    static void TestSrt()
    {
        string srt = "\uFEFF1\r\n00:00:01,200 --> 00:00:04,000\r\nHello there,\r\nsecond line.\r\n\r\n"
            + "2\r\n00:00:05.500 --> 00:00:06.000\r\n[BLANK_AUDIO]\r\n\r\n"
            + "3\r\n01:02:03,004 --> 01:02:05,000 X1:10\r\nA | pipe\r\n\r\n"
            + "4\r\n00:00:09,000 --> 00:00:08,000\r\n12\r\n5\r\n00:00:10,000 --> 00:00:11,000\r\nlast\r\n";
        List<TranscriptSegment> s = Transcript.ParseSrt(srt);
        Expect(s.Count == 4, "segments (blank audio left out): " + s.Count);
        if (s.Count == 4)
        {
            Expect(Math.Abs(s[0].Start - 1.2) < 1e-9 && Math.Abs(s[0].End - 4.0) < 1e-9 && s[0].Text == "Hello there, second line.", "first segment: " + s[0].Text);
            Expect(Math.Abs(s[1].Start - 3723.004) < 1e-9 && s[1].Text == "A | pipe", "hours and a cue setting after the end time");
            Expect(s[2].Text == "12" && s[2].End == s[2].Start, "a number as text; end before start is set to start");
            Expect(s[3].Text == "last" && Math.Abs(s[3].Start - 10) < 1e-9, "block without a blank line before it");
        }
        Expect(Transcript.ParseSrt("no times here").Count == 0 && Transcript.ParseSrt(null).Count == 0, "no segments");
        Expect(Transcript.ParseSrtTime("01:02.5") == 62.5 && Transcript.ParseSrtTime("x") < 0, "short time");
        string again = Transcript.FormatSrt(s);
        Expect(again.StartsWith("1\n00:00:01,200 --> 00:00:04,000\nHello there, second line.\n\n", StringComparison.Ordinal), "format: " + again);
        Expect(Transcript.ParseSrt(again).Count == 4, "round trip");
        Expect(Transcript.TimeoutMs(10) == 150000 && Transcript.TimeoutMs(5000) == 3600000, "timeout");
    }

    static void TestIndexSection()
    {
        List<TranscriptSegment> segs = new List<TranscriptSegment>();
        TranscriptSegment a = new TranscriptSegment();
        a.Start = 0.55; a.End = 2.0; a.Text = "We | click Save";
        segs.Add(a);
        string md = Transcript.IndexSection(true, segs, "whisper-cli (whisper.cpp, model ggml-base.bin), language auto", null);
        Expect(md.StartsWith("\n## Audio and transcript\nclip.mp4 has the system audio", StringComparison.Ordinal), "section head");
        Expect(md.IndexOf("| 0.6 s | 2.0 s | We \\| click Save |", StringComparison.Ordinal) >= 0, "row with pipe escape: " + md);
        Expect(md.IndexOf("time line of clip.mp4", StringComparison.Ordinal) >= 0, "time line named");
        string none = Transcript.IndexSection(true, null, null, "no Whisper program was found");
        Expect(none.IndexOf("No transcript: no Whisper program was found.", StringComparison.Ordinal) >= 0 && none.IndexOf("| Start", StringComparison.Ordinal) < 0, "no transcript");
        Expect(Transcript.IndexSection(false, null, null, "x").IndexOf("not in clip.mp4", StringComparison.Ordinal) >= 0, "audio not in clip");
        Expect(Transcript.IndexSection(true, new List<TranscriptSegment>(), "t", null).IndexOf("no speech", StringComparison.Ordinal) >= 0, "no speech");
    }

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "dr_whisper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "x");
    }

    static void TestFindWhisper()
    {
        string root = TempDir();
        try
        {
            string app = Path.Combine(root, "app");
            string bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(app);
            Directory.CreateDirectory(bin);
            string reason;
            Expect(Transcript.Find("", "", app, bin, false, out reason) == null && reason.IndexOf("no Whisper", StringComparison.Ordinal) >= 0, "nothing found");
            // whisper.cpp next to the app without a model: not usable, with a reason.
            Touch(Path.Combine(app, "whisper", "whisper-cli"));
            Expect(Transcript.Find("", "", app, bin, false, out reason) == null && reason.IndexOf("no model file", StringComparison.Ordinal) >= 0, "cpp without model: " + reason);
            Touch(Path.Combine(app, "whisper", "models", "ggml-tiny.bin"));
            Touch(Path.Combine(app, "whisper", "models", "ggml-small.en.bin"));
            Touch(Path.Combine(app, "whisper", "models", "ggml-medium-q5_0.bin"));
            WhisperTool t = Transcript.Find("", "", app, bin, false, out reason);
            Expect(t != null && t.Kind == WhisperTool.KindCpp && Path.GetFileName(t.Model) == "ggml-medium-q5_0.bin", "cpp with the preferred model: " + (t == null ? reason : t.Model));
            // A configured model, relative to the program.
            t = Transcript.Find("", "models/ggml-tiny.bin", app, bin, false, out reason);
            Expect(t != null && Path.GetFileName(t.Model) == "ggml-tiny.bin", "configured model");
            Expect(Transcript.Find("", "models/none.bin", app, bin, false, out reason) == null && reason.IndexOf("whisper_model", StringComparison.Ordinal) >= 0, "missing configured model");
            // On PATH only: an openai-style program. "whisper" next to the app does not count.
            string app2 = Path.Combine(root, "app2");
            Touch(Path.Combine(app2, "whisper"));
            Touch(Path.Combine(bin, "whisper-ctranslate2"));
            t = Transcript.Find("", "", app2, "/nope:" + bin, false, out reason);
            Expect(t != null && t.Kind == WhisperTool.KindOpenAi && Path.GetFileName(t.Path) == "whisper-ctranslate2" && t.Model == null, "openai-style on PATH");
            // A configured path wins, and its kind comes from the name.
            string cfg = Path.Combine(root, "x", "faster-whisper-xxl");
            Touch(cfg);
            t = Transcript.Find(cfg, "large-v3", app, bin, false, out reason);
            Expect(t != null && t.Path == cfg && t.Kind == WhisperTool.KindOpenAi && t.Model == "large-v3", "configured path");
            Expect(Transcript.Find(Path.Combine(root, "missing"), "", app, bin, false, out reason) == null && reason.IndexOf("whisper_path", StringComparison.Ordinal) >= 0, "missing configured path");
            // Windows names end in .exe.
            Touch(Path.Combine(root, "win", "faster-whisper-xxl.exe"));
            t = Transcript.Find("", "", Path.Combine(root, "win"), "", true, out reason);
            Expect(t != null && t.Path.EndsWith("faster-whisper-xxl.exe", StringComparison.Ordinal), "windows exe name");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        Expect(Transcript.PickModel(new List<string>(new string[] { "/m/ggml-zz.bin", "/m/ggml-aa.bin" })) == "/m/ggml-aa.bin", "unknown names: first by name");
    }

    static void TestWhisperArguments()
    {
        WhisperTool cpp = new WhisperTool();
        cpp.Path = "/w/whisper-cli"; cpp.Kind = WhisperTool.KindCpp; cpp.Model = "/w/ggml-base.bin";
        string srt;
        string a = string.Join(" ", Transcript.BuildArguments(cpp, "/f/t.wav", "/f", "", out srt));
        Expect(a == "-m /w/ggml-base.bin -f /f/t.wav -l auto -osrt -of " + Path.Combine("/f", "transcript_whisper") + " -np", "cpp args: " + a);
        Expect(srt == Path.Combine("/f", "transcript_whisper") + ".srt", "cpp srt path");
        WhisperTool py = new WhisperTool();
        py.Path = "/w/whisper"; py.Kind = WhisperTool.KindOpenAi;
        a = string.Join(" ", Transcript.BuildArguments(py, "/f/t.wav", "/f", "auto", out srt));
        Expect(a == "/f/t.wav --output_format srt --output_dir /f", "openai args, auto language: " + a);
        Expect(srt == Path.Combine("/f", "t.srt"), "openai srt path");
        py.Model = "small";
        a = string.Join(" ", Transcript.BuildArguments(py, "/f/t.wav", "/f", "de", out srt));
        Expect(a.EndsWith("--model small --language de", StringComparison.Ordinal), "openai model and language");
        Expect(py.Describe() == "whisper (openai-style, model small)", "describe");
    }

    static void TestAiMessage()
    {
        string f = @"C:\rec\desktop_2026-11-20_10-00-00";
        string both = RecordingIndex.AiMessage(f, true, true, true);
        Expect(both.IndexOf("4. Read the section \"Audio and transcript\"", StringComparison.Ordinal) >= 0, "transcript step");
        Expect(both.IndexOf("clip.mp4 also has the sound", StringComparison.Ordinal) >= 0, "audio line");
        string plain = RecordingIndex.AiMessage(f, true);
        Expect(plain.IndexOf("transcript", StringComparison.OrdinalIgnoreCase) < 0 && plain.IndexOf("sound", StringComparison.Ordinal) < 0, "old message unchanged");
        string failed = RecordingIndex.AiMessage(f, false, true, false);
        Expect(failed.IndexOf("failed", StringComparison.Ordinal) >= 0 && failed.IndexOf("has the sound", StringComparison.Ordinal) >= 0, "failed pass with audio");
    }
}
