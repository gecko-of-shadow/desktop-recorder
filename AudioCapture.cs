// System audio capture with WASAPI loopback (docs/audio-transcript.md). Windows only. C# 5, no NuGet.
// The COM interfaces are declared here in vtable order. Only the methods up to the last one that the code calls
// are declared.
// UNVERIFIED: no run on Windows yet. Written and compiled on Linux (Mono) on 2026-11-20.
//
// The capture writes the mix format of the default render device (normally 48 kHz, 2 channels, 32-bit float)
// to a WAV file. WAV time 0 = the QPC time right after IAudioClient.Start. Loopback gives no packets while the
// PC plays nothing, so the writer adds silence by the QPC time of each packet (AudioClock.Correction), and
// at Stop up to the stop time.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

sealed class LoopbackCapture
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    const int ERender = 0, EConsole = 0;
    const int ClsctxAll = 23;
    const int SharedMode = 0;
    const int StreamFlagsLoopback = 0x00020000;
    const uint FlagSilent = 0x2, FlagTimestampError = 0x4;
    const long BufferDuration = 10000000;   // 1 s in 100 ns units
    static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    readonly ManualResetEvent started = new ManualResetEvent(false);
    volatile bool stopRequested;
    Thread thread;
    string path;
    string startError;
    string runError;
    long startTimestamp;        // Stopwatch.GetTimestamp() at WAV time 0
    long stopTimestamp;
    double seconds, peak = -1;

    // Raw QPC ticks (Stopwatch.GetTimestamp) at WAV time 0. 0 before a successful Start.
    public long StartTimestamp { get { return startTimestamp; } }

    // Starts the capture into a new WAV file. Returns null, or the reason why no audio is recorded.
    public string Start(string wavPath)
    {
        path = wavPath;
        thread = new Thread(Run);
        thread.IsBackground = true;
        thread.Name = "audio-loopback";
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!started.WaitOne(5000))
        {
            stopRequested = true;
            return "System audio is off for this recording: the audio device did not answer in 5 s.";
        }
        return startError;
    }

    long endTimestamp;          // the user pressed Stop: the audio ends here (0 = not yet)
    string note;

    // Stop was pressed. FFmpeg needs a moment to end, and the Stop sound of the app plays now: it must not be in the audio.
    public void MarkEnd()
    {
        Interlocked.CompareExchange(ref endTimestamp, Stopwatch.GetTimestamp(), 0);
    }

    // Stops the capture, fills silence up to now, and closes the WAV file.
    public AudioTake Stop()
    {
        stopTimestamp = Stopwatch.GetTimestamp();
        stopRequested = true;
        if (thread != null) thread.Join(5000);
        AudioTake t = new AudioTake();
        t.Path = path;
        t.Seconds = seconds;
        t.Peak = peak;
        t.Error = runError;
        t.Note = note;
        return t;
    }

    static double QpcSeconds(long ticks) { return ticks / (double)Stopwatch.Frequency; }

    // The playback device failed (removed, changed). Not a fault of the WAV file.
    sealed class DeviceError : Exception { public DeviceError(string m) : base(m) { } }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new DeviceError(what + " failed (0x" + hr.ToString("X8") + ").");
    }

    const int StreamFlagsConvert = unchecked((int)0x88000000);  // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
    const double PollSeconds = 0.25;

    // One open endpoint. All members are used on the capture thread only.
    sealed class Endpoint
    {
        public IMMDevice Device;
        public IAudioClient Client;
        public IAudioCaptureClient Capture;
        public string Id;
        bool running;

        // mix: the format of the WAV file (CoTaskMem). Zero on the first open: then it gets the mix format.
        public void Open(IMMDeviceEnumerator enumerator, ref IntPtr mix)
        {
            Check(enumerator.GetDefaultAudioEndpoint(ERender, EConsole, out Device), "GetDefaultAudioEndpoint (is a playback device on?)");
            Check(Device.GetId(out Id), "IMMDevice.GetId");
            object o;
            Guid iid = IidAudioClient;
            Check(Device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out o), "IMMDevice.Activate");
            Client = (IAudioClient)o;
            int flags = StreamFlagsLoopback;
            if (mix == IntPtr.Zero) Check(Client.GetMixFormat(out mix), "GetMixFormat");
            else flags |= StreamFlagsConvert;
            Check(Client.Initialize(SharedMode, flags, BufferDuration, 0, mix, IntPtr.Zero), "IAudioClient.Initialize");
            Guid cid = IidAudioCaptureClient;
            object c;
            Check(Client.GetService(ref cid, out c), "GetService(IAudioCaptureClient)");
            Capture = (IAudioCaptureClient)c;
        }

        public void Start() { Check(Client.Start(), "IAudioClient.Start"); running = true; }

        public void Close()
        {
            try { if (running) Client.Stop(); } catch (Exception) { }
            Release(Capture); Release(Client); Release(Device);
            Capture = null; Client = null; Device = null;
        }
    }

    static string DefaultId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice d = null;
        try
        {
            string id;
            if (enumerator.GetDefaultAudioEndpoint(ERender, EConsole, out d) < 0 || d.GetId(out id) < 0) return null;
            return id;
        }
        finally { Release(d); }
    }

    void Run()
    {
        IMMDeviceEnumerator enumerator = null;
        Endpoint current = null;
        IntPtr mix = IntPtr.Zero;
        WavWriter wav = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            current = new Endpoint();
            current.Open(enumerator, ref mix);
            int cbSize = Marshal.ReadInt16(mix, 16);
            byte[] raw = new byte[18 + Math.Max(0, cbSize)];
            Marshal.Copy(mix, raw, 0, raw.Length);
            WaveFormat format = WaveFormat.Parse(raw);
            wav = new WavWriter(path, format);
            current.Start();
            startTimestamp = Stopwatch.GetTimestamp();
            started.Set();

            double start = QpcSeconds(startTimestamp);
            int rate = format.SampleRate;
            byte[] buffer = new byte[0];
            long lastPatch = startTimestamp, lastPoll = startTimestamp;
            bool lost = false;
            while (!stopRequested)
            {
                Thread.Sleep(10);
                if (!lost)
                {
                    try { Drain(current.Capture, wav, start, rate, ref buffer, Interlocked.Read(ref endTimestamp)); }
                    catch (DeviceError) { lost = true; }
                }
                long now = Stopwatch.GetTimestamp();
                if (QpcSeconds(now - lastPoll) >= PollSeconds && !stopRequested)
                {
                    lastPoll = now;
                    string id = DefaultId(enumerator);
                    // No default device: keep the open one until it fails.
                    if (id != null && (lost || id != current.Id))
                    {
                        Endpoint next = new Endpoint();
                        try
                        {
                            next.Open(enumerator, ref mix);
                            next.Start();
                            if (!lost) { try { Drain(current.Capture, wav, start, rate, ref buffer, Interlocked.Read(ref endTimestamp)); } catch (DeviceError) { } }
                            current.Close();
                            current = next;
                            next = null;
                            lost = false;
                            note = "The playback device changed " + wav.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                + " s after the start of the sound. The sound of the new device is recorded from there.";
                        }
                        catch (DeviceError) { }         // the device is not ready yet: try again at the next poll
                        catch (COMException) { }
                        finally { if (next != null) next.Close(); }
                    }
                }
                if (QpcSeconds(Stopwatch.GetTimestamp() - lastPatch) >= 1)
                {
                    // Loopback gives no packets while the PC is silent. Write that silence now, not all of it at Stop.
                    // 2 s behind the clock: a packet that is still on its way must not be cut (the buffer is 1 s).
                    long due = AudioClock.Correction(wav.Frames, start, QpcSeconds(Stopwatch.GetTimestamp()) - 2.0, rate, 0);
                    if (due > 0 && Interlocked.Read(ref endTimestamp) == 0) wav.WriteSilence(due);
                    wav.PatchHeader();
                    lastPatch = Stopwatch.GetTimestamp();
                }
            }
            if (!lost) { try { Drain(current.Capture, wav, start, rate, ref buffer, Interlocked.Read(ref endTimestamp)); } catch (DeviceError) { } }
            if (lost) runError = "The playback device was lost and did not come back.";
        }
        catch (Exception e)
        {
            if (!started.WaitOne(0))
            {
                startError = "System audio is off for this recording: " + e.Message;
                started.Set();
            }
            else runError = "The audio capture stopped early: " + e.Message;
        }
        finally
        {
            if (current != null) current.Close();
            if (wav != null)
            {
                try
                {
                    // Silence up to the stop, so the WAV file covers the whole recording.
                    long end = stopTimestamp != 0 ? stopTimestamp : Stopwatch.GetTimestamp();
                    long mark = Interlocked.Read(ref endTimestamp);
                    if (mark != 0 && mark < end) end = mark;
                    long missing = AudioClock.Correction(wav.Frames, QpcSeconds(startTimestamp), QpcSeconds(end), wav.Format.SampleRate, 0);
                    if (missing > 0) wav.WriteSilence(missing);
                }
                catch (Exception) { }
                seconds = wav.Seconds;
                peak = wav.Peak;
                try { wav.Dispose(); } catch (Exception) { }
            }
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Release(enumerator);
        }
    }

    // Reads all waiting packets. The QPC time of each packet keeps the file on the clock.
    static void Drain(IAudioCaptureClient capture, WavWriter wav, double start, int rate, ref byte[] buffer, long endTicks)
    {
        uint next;
        Check(capture.GetNextPacketSize(out next), "GetNextPacketSize");
        while (next > 0)
        {
            IntPtr data;
            uint frames, flags;
            ulong devicePosition, qpc;
            Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc), "GetBuffer");
            try
            {
                int block = wav.Format.BlockAlign;
                long skip = 0;
                bool timed = (flags & FlagTimestampError) == 0;
                // use: the frames of this packet before the Stop press.
                long use = endTicks == 0 ? frames : timed ? AudioClock.FramesBefore(qpc * 1e-7, frames, QpcSeconds(endTicks), rate) : 0;
                if (timed && use > 0)
                {
                    // qpc is in 100 ns units.
                    long d = AudioClock.Correction(wav.Frames, start, qpc * 1e-7, rate, 0.05);
                    if (d > 0) wav.WriteSilence(d);
                    else if (d < 0) skip = Math.Min(-d, use);
                }
                long keep = use - skip;
                if (keep > 0)
                {
                    if ((flags & FlagSilent) != 0) wav.WriteSilence(keep);
                    else
                    {
                        int bytes = (int)(keep * block);
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        Marshal.Copy(new IntPtr(data.ToInt64() + skip * block), buffer, 0, bytes);
                        wav.Write(buffer, 0, bytes);
                    }
                }
            }
            finally { capture.ReleaseBuffer(frames); }
            Check(capture.GetNextPacketSize(out next), "GetNextPacketSize");
        }
    }

    static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch (Exception) { }
    }
}
