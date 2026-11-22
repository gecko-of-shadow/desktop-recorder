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
        TestSpeechCheck();
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
        // Rows that SpeechCheck left out (2026-11-22).
        string allOut = Transcript.IndexSection(true, new List<TranscriptSegment>(), "t", null, 2);
        Expect(allOut.IndexOf("No speech was found. The app left out 2 rows that Whisper wrote: the audio was silent or almost silent in that time.", StringComparison.Ordinal) >= 0
            && allOut.IndexOf("| Start", StringComparison.Ordinal) < 0, "all rows left out: no speech, no table: " + allOut);
        string partOut = Transcript.IndexSection(true, segs, "t", null, 1);
        Expect(partOut.IndexOf("The app left out 1 row that Whisper wrote", StringComparison.Ordinal) >= 0
            && partOut.IndexOf("| 0.6 s | 2.0 s |", StringComparison.Ordinal) > partOut.IndexOf("left out", StringComparison.Ordinal), "1 row left out, table stays");
        Expect(md.IndexOf("left out", StringComparison.Ordinal) < 0, "no left-out line when nothing is left out");
    }

    // A 16 kHz mono 16-bit WAV: FFmpeg puts a LIST chunk before the data chunk, so the test does too.
    static byte[] Wav16k(short[] samples, bool listChunk)
    {
        MemoryStream m = new MemoryStream();
        BinaryWriter w = new BinaryWriter(m);
        byte[] list = listChunk ? new byte[] { (byte)'I', (byte)'N', (byte)'F', (byte)'O', (byte)'I', (byte)'S', (byte)'F', (byte)'T', 3, 0, 0, 0, (byte)'a', (byte)'b', 0, 0 } : new byte[0];
        int dataBytes = samples.Length * 2;
        w.Write(new char[] { 'R', 'I', 'F', 'F' }); w.Write(4 + 24 + (listChunk ? 8 + list.Length : 0) + 8 + dataBytes);
        w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        if (listChunk) { w.Write(new char[] { 'L', 'I', 'S', 'T' }); w.Write(list.Length); w.Write(list); }
        w.Write(new char[] { 'd', 'a', 't', 'a' }); w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return m.ToArray();
    }

    // A stream that gives 1 to 3 bytes for each Read and cannot seek.
    sealed class TrickleStream : Stream
    {
        readonly byte[] data;
        int pos, step;
        public TrickleStream(byte[] d) { data = d; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, 1 + step++ % 3), data.Length - pos);
            Buffer.BlockCopy(data, pos, buffer, offset, n);
            pos += n;
            return n;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return data.Length; } }
        public override long Position { get { return pos; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    static TranscriptSegment Row(double start, double end, string text)
    {
        TranscriptSegment s = new TranscriptSegment();
        s.Start = start; s.End = end; s.Text = text;
        return s;
    }

    // Whisper writes "Thank you." on silence (Bran's recordings of 2026-11-22). SpeechCheck leaves such rows out,
    // and keeps quiet real speech (about -60 dBFS in the recording of 14:45).
    static void TestSpeechCheck()
    {
        // 0 to 20 s: digital silence with two clicks of 0.2 s (at 5 s and 12 s). 20 to 30 s: steady sound at -60 dBFS.
        short[] a = new short[30 * 16000];
        Random rnd = new Random(7);
        for (int i = 0; i < 3200; i++) { a[5 * 16000 + i] = (short)(i % 2 == 0 ? 8000 : -8000); a[12 * 16000 + i] = (short)(i % 2 == 0 ? 8000 : -8000); }
        for (int i = 20 * 16000; i < a.Length; i++) a[i] = (short)rnd.Next(-57, 58);   // uniform noise: RMS 57 / sqrt(3) = -60 dBFS
        double[] levels = SpeechCheck.WindowLevels(new MemoryStream(Wav16k(a, true)));
        Expect(levels != null && levels.Length == 300, "levels: 300 windows of 100 ms, got " + (levels == null ? "null" : levels.Length.ToString()));
        if (levels == null || levels.Length != 300) return;
        Expect(double.IsNegativeInfinity(levels[0]) && double.IsNegativeInfinity(levels[199]), "digital silence has no level");
        Expect(Math.Abs(levels[50] - 20 * Math.Log10(8000 / 32768.0)) < 0.1, "click level: " + levels[50]);
        Expect(Math.Abs(levels[250] + 60) < 1.5, "steady sound near -60 dBFS: " + levels[250]);
        Expect(SpeechCheck.WindowLevels(new MemoryStream(Wav16k(a, false))).Length == 300, "levels without a LIST chunk");
        // A float WAV is not read: all rows stay.
        MemoryStream f = new MemoryStream(Wav16k(new short[1600], false));
        byte[] fb = f.ToArray();
        fb[20] = 3;   // format tag 3 = IEEE float
        Expect(SpeechCheck.WindowLevels(new MemoryStream(fb)) == null && SpeechCheck.Keep(Row(0, 30, "Thank you."), null), "float WAV: no check, row kept");

        Expect(Math.Abs(SpeechCheck.SoundShare(levels, 0, 20) - 0.02) < 1e-9, "share of 0 to 20 s: 4 of 200 windows");
        Expect(SpeechCheck.SoundShare(levels, 20, 30) == 1, "share of 20 to 30 s");
        Expect(Math.Abs(SpeechCheck.SoundShare(levels, 25, 40) - 50 / 150.0) < 1e-9, "after the end of the audio counts as silence");
        Expect(SpeechCheck.SoundShare(levels, 5.05, 5.05) == 1, "a row shorter than one window uses the window at its start");

        // Silence (and silence with clicks): left out, with any text.
        Expect(!SpeechCheck.Keep(Row(0, 20, "Thank you."), levels), "Thank you. on silence with clicks: out");
        Expect(!SpeechCheck.Keep(Row(0, 20, "Some other words here."), levels), "any text on almost silent audio: out");
        Expect(!SpeechCheck.Keep(Row(13, 19, "Hello there."), levels), "text on digital silence: out");
        // Quiet real sound: kept, also "Thank you.".
        Expect(SpeechCheck.Keep(Row(20, 30, "Thank you."), levels), "Thank you. on steady sound: kept");
        Expect(SpeechCheck.Keep(Row(21, 23.5, "So Peter, what was your first thought?"), levels), "quiet speech: kept");
        // Past the end of the audio: a known text needs half of its time with sound.
        Expect(!SpeechCheck.Keep(Row(25, 40, "Thank you."), levels), "known text past the end: out");
        Expect(SpeechCheck.Keep(Row(25, 40, "And so we went home."), levels), "other text past the end: kept (share 0.33)");
        // A short row on a short sound stays, also with a known text: a real "Thanks." is short (review 2026-11-22).
        Expect(SpeechCheck.Keep(Row(5.0, 5.2, "Thanks."), levels) && SpeechCheck.Keep(Row(5.0, 5.2, "Yes."), levels), "short row on a short sound: kept");
        // Near silence needs both: sound in less than 10% of the row and for less than 1.0 s.
        Expect(SpeechCheck.Keep(Row(18.5, 20.2, "Words at the end."), levels) && !SpeechCheck.Keep(Row(17, 20.1, "Words at the end."), levels),
            "share limit 0.1: 2 of 17 windows kept, 1 of 31 out: " + SpeechCheck.SoundShare(levels, 18.5, 20.2) + " / " + SpeechCheck.SoundShare(levels, 17, 20.1));
        Expect(SpeechCheck.Keep(Row(0, 21.0, "A long row with a short real sentence at its end."), levels),
            "long row with 1.4 s of sound at its end (share 0.067): kept");
        Expect(!SpeechCheck.Keep(Row(0, 20.3, "A long row with clicks."), levels), "long row with 0.7 s of sound (share 0.034): out");
        // A known text over most of a short recording: on silence, Whisper writes it over all of the audio.
        double[] shortAudio = new double[30];
        for (int i = 0; i < 30; i++) shortAudio[i] = i >= 25 ? -20 : double.NegativeInfinity;   // 3 s, the stop sound at the end
        Expect(!SpeechCheck.Keep(Row(0, 3.0, "Thank you."), shortAudio) && SpeechCheck.Keep(Row(0, 3.0, "We are done here."), shortAudio),
            "short recording: known text over all of it is out, other text stays (share 0.17)");
        Expect(SpeechCheck.Keep(Row(2.5, 3.0, "Thank you."), shortAudio), "short recording: short known row on sound stays");
        // The parser joins samples that are split between reads, and reads to the end for a data size of 0.
        byte[] wav = Wav16k(a, true);
        double[] trickle = SpeechCheck.WindowLevels(new TrickleStream(wav));
        bool same = trickle != null && trickle.Length == levels.Length;
        for (int i = 0; same && i < levels.Length; i++) same = trickle[i].Equals(levels[i]);
        Expect(same, "levels from 1 to 3 bytes for each read");
        int dataAt = -1;
        for (int i = 12; i + 4 <= wav.Length; i++) if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a') { dataAt = i; break; }
        wav[dataAt + 4] = 0; wav[dataAt + 5] = 0; wav[dataAt + 6] = 0; wav[dataAt + 7] = 0;
        double[] zeroSize = SpeechCheck.WindowLevels(new MemoryStream(wav));
        Expect(zeroSize != null && zeroSize.Length == 300, "data size 0: read to the end");
        Expect(SpeechCheck.WindowLevels(new MemoryStream(Wav16k(new short[400], false))) == null, "less than half a window: no levels (no check)");

        Expect(SpeechCheck.IsKnownPhrase(" Thank you. ") && SpeechCheck.IsKnownPhrase("THANK YOU!") && SpeechCheck.IsKnownPhrase("Bye-bye.")
            && SpeechCheck.IsKnownPhrase("Thanks for watching!") && SpeechCheck.IsKnownPhrase("ご視聴ありがとうございました"), "known texts");
        Expect(!SpeechCheck.IsKnownPhrase("Thank you for the help.") && !SpeechCheck.IsKnownPhrase("") && !SpeechCheck.IsKnownPhrase(null), "not known texts");

        List<TranscriptSegment> rows = new List<TranscriptSegment>();
        rows.Add(Row(0, 20, "Thank you."));
        rows.Add(Row(20, 25, "Real words."));
        rows.Add(Row(25, 40, "Thank you."));
        int dropped;
        List<TranscriptSegment> kept = SpeechCheck.Filter(rows, levels, out dropped);
        Expect(dropped == 2 && kept.Count == 1 && kept[0].Text == "Real words.", "filter: 1 kept, 2 left out");
        kept = SpeechCheck.Filter(rows, null, out dropped);
        Expect(dropped == 0 && kept.Count == 3, "filter without levels keeps all rows");
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
            // A Windows folder has a drive colon, so the ":" list of the other platforms cannot hold it.
            // On Windows, this case uses the Windows rules (";" list, ".exe" names).
            bool winPaths = Path.DirectorySeparatorChar == '\\';
            string onPath = winPaths ? "whisper-ctranslate2.exe" : "whisper-ctranslate2";
            Touch(Path.Combine(bin, onPath));
            t = Transcript.Find("", "", app2, winPaths ? "Z:\\nope;" + bin : "/nope:" + bin, winPaths, out reason);
            Expect(t != null && t.Kind == WhisperTool.KindOpenAi && Path.GetFileName(t.Path) == onPath && t.Model == null, "openai-style on PATH");
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
        string f = @"C:\rec\desktop_2026-11-20_10-00-00.zip";
        string both = RecordingIndex.AiMessage(f, true, true, true);
        Expect(both.IndexOf("4. Read the section \"Audio and transcript\"", StringComparison.Ordinal) >= 0, "transcript step");
        Expect(both.IndexOf("clip.mp4 also has the sound", StringComparison.Ordinal) >= 0, "audio line");
        string plain = RecordingIndex.AiMessage(f, true);
        Expect(plain.IndexOf("transcript", StringComparison.OrdinalIgnoreCase) < 0 && plain.IndexOf("sound", StringComparison.Ordinal) < 0, "old message unchanged");
        string failed = RecordingIndex.AiMessage(f, false, true, false);
        Expect(failed.IndexOf("failed", StringComparison.Ordinal) >= 0 && failed.IndexOf("has the sound", StringComparison.Ordinal) >= 0, "failed pass with audio");
    }
}
