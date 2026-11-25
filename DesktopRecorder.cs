// Desktop Recorder: a tray program that records a screen region to MP4 with ffmpeg.
// Builds with the C# compiler that ships with Windows (C# 5, .NET Framework 4.x). See build.cmd.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        // First: all coordinates (selector, frame, ffmpeg offsets) are physical pixels.
        // The manifest (DesktopRecorder.manifest) also sets this. This call is the fallback.
        NativeMethods.EnableDpiAwareness();
        bool first;
        using (Mutex mutex = new Mutex(true, "DesktopRecorder_SingleInstance", out first))
        {
            if (!first)
            {
                MessageBox.Show("Desktop Recorder is already running. Look for its icon in the tray.",
                    "Desktop Recorder");
                return;
            }
            // Above normal, as FFmpeg (ChildProcessJob.Raise): the frame pass runs in this process. Idle, it uses nothing.
            ChildProcessJob.Raise(Process.GetCurrentProcess());
            // An error on the UI thread must not end the app (and the recording). It goes into error.log.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { LogError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { LogError(e.ExceptionObject as Exception); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    // %APPDATA%\DesktopRecorder\error.log, next to settings.txt. At most 200 KB.
    public static void LogError(Exception e)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Settings.FilePath), "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 200000) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" + e + "\r\n\r\n");
        }
        catch (Exception) { }
    }
}

class Settings
{
    public string Folder;
    public double Fps = RecorderCore.DefaultFps;
    public int Quality = 1;          // 0 = smallest, 1 = balanced, 2 = sharpest
    public int SplitMinutes = 30;    // 0 = one file per recording
    public bool CopyToClipboard = true;
    public bool PlaySounds = true;
    public bool ShowPointer = false;
    public int HotkeyModifiers = 4 | 8;   // 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win
    public Keys HotkeyKey = Keys.D;
    // Capture and encoding (HANDOFF 4.3).
    public string CaptureBackend = RecorderCore.BackendDdagrab;
    public string Encoder = RecorderCore.EncoderAuto;
    public string ChromaFormat = RecorderCore.Chroma420;
    public string FrameRateMode = RecorderCore.ModeChanges;   // default since Phase B (HANDOFF 5)
    public int NvencCq = RecorderCore.DefaultNvencCq;
    // Physical px, virtual-screen coordinates. Empty = no region (whole desktop).
    public Rectangle Region = Rectangle.Empty;
    public Rectangle LastRegion = Rectangle.Empty;
    // Region selector options (keys C, M and P in the selector).
    public bool SelectorCrosshair = false;
    public bool SelectorMagnifier = true;
    public bool SelectorPosition = true;
    // AI capture folder (AI capture brief, Phase 1). See StoredSettings for the meaning.
    public string OutputMode = RecorderCore.OutputFolder;
    public int SettleMs = 400;
    public int StillIntervalSeconds = 5;
    public int ScrollPercent = 60;
    public int SizeLimit = RecorderCore.DefaultSizeLimit;
    // Audio and transcript (docs/audio-transcript.md). See StoredSettings.
    public bool RecordAudio = false;
    public string WhisperPath = "";
    public string WhisperModel = "";
    public string WhisperLanguage = "auto";
    // Keys written by the Linux build. Saved again so a shared settings file does not lose them.
    public List<string> ExtraLines = new List<string>();

    public static readonly int[] Crf = RecorderCore.Crf;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DesktopRecorder";
    const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public Settings()
    {
        // Not in Videos or Documents: Windows "Controlled folder access" blocks writing there.
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Recordings");
    }

    public static string FilePath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopRecorder", "settings.txt");
        }
    }

    public StoredSettings ToStored()
    {
        StoredSettings stored = new StoredSettings();
        stored.Folder = Folder;
        stored.Fps = Fps;
        stored.Quality = Quality;
        stored.SplitMinutes = SplitMinutes;
        stored.CopyToClipboard = CopyToClipboard;
        stored.PlaySounds = PlaySounds;
        stored.ShowPointer = ShowPointer;
        stored.HotkeyModifiers = HotkeyModifiers;
        stored.HotkeyKey = (int)HotkeyKey;
        stored.CaptureBackend = CaptureBackend;
        stored.Encoder = Encoder;
        stored.ChromaFormat = ChromaFormat;
        stored.FrameRateMode = FrameRateMode;
        stored.NvencCq = NvencCq;
        stored.SelectorCrosshair = SelectorCrosshair;
        stored.SelectorMagnifier = SelectorMagnifier;
        stored.SelectorPosition = SelectorPosition;
        stored.OutputMode = OutputMode;
        stored.SettleMs = SettleMs;
        stored.StillIntervalSeconds = StillIntervalSeconds;
        stored.ScrollPercent = ScrollPercent;
        stored.SizeLimit = SizeLimit;
        stored.RecordAudio = RecordAudio;
        stored.WhisperPath = WhisperPath;
        stored.WhisperModel = WhisperModel;
        stored.WhisperLanguage = WhisperLanguage;
        stored.RegionX = Region.X;
        stored.RegionY = Region.Y;
        stored.RegionWidth = Region.Width;
        stored.RegionHeight = Region.Height;
        stored.LastRegionX = LastRegion.X;
        stored.LastRegionY = LastRegion.Y;
        stored.LastRegionWidth = LastRegion.Width;
        stored.LastRegionHeight = LastRegion.Height;
        stored.ExtraLines = new List<string>(ExtraLines ?? new List<string>());
        return stored;
    }

    public void Apply(StoredSettings stored)
    {
        Folder = stored.Folder;
        Fps = stored.Fps;
        Quality = stored.Quality;
        SplitMinutes = stored.SplitMinutes;
        CopyToClipboard = stored.CopyToClipboard;
        PlaySounds = stored.PlaySounds;
        ShowPointer = stored.ShowPointer;
        HotkeyModifiers = stored.HotkeyModifiers;
        HotkeyKey = (Keys)stored.HotkeyKey;
        CaptureBackend = RecorderCore.NormalizeBackend(stored.CaptureBackend);
        Encoder = RecorderCore.NormalizeEncoder(stored.Encoder);
        ChromaFormat = RecorderCore.NormalizeChroma(stored.ChromaFormat);
        FrameRateMode = RecorderCore.NormalizeMode(stored.FrameRateMode);
        NvencCq = RecorderCore.ClampNvencCq(stored.NvencCq);
        SelectorCrosshair = stored.SelectorCrosshair;
        SelectorMagnifier = stored.SelectorMagnifier;
        SelectorPosition = stored.SelectorPosition;
        OutputMode = RecorderCore.NormalizeOutputMode(stored.OutputMode);
        SettleMs = stored.SettleMs;
        StillIntervalSeconds = stored.StillIntervalSeconds;
        ScrollPercent = stored.ScrollPercent;
        SizeLimit = stored.SizeLimit;
        RecordAudio = stored.RecordAudio;
        WhisperPath = stored.WhisperPath ?? "";
        WhisperModel = stored.WhisperModel ?? "";
        WhisperLanguage = RecorderCore.IsLanguageCode(stored.WhisperLanguage) ? stored.WhisperLanguage : "auto";
        Region = ToRectangle(stored.RegionX, stored.RegionY, stored.RegionWidth, stored.RegionHeight);
        LastRegion = ToRectangle(stored.LastRegionX, stored.LastRegionY, stored.LastRegionWidth, stored.LastRegionHeight);
        ExtraLines = new List<string>(stored.ExtraLines ?? new List<string>());
    }

    static Rectangle ToRectangle(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return Rectangle.Empty;
        return new Rectangle(x, y, w, h);
    }

    // A full copy, so a settings dialog can change only its own fields.
    public Settings Copy()
    {
        Settings copy = new Settings();
        copy.Apply(ToStored());
        return copy;
    }

    // True: settings.txt is there, but it could not be read. The app then does not save by itself (only Save in the
    // Settings window does), or the default values would replace the settings of the user.
    public bool LoadFailed;

    public static Settings Load()
    {
        Settings s = new Settings();
        bool failed;
        string text = RecorderCore.ReadSettingsText(FilePath, out failed);
        s.LoadFailed = failed;
        try { if (text != null) s.Apply(RecorderCore.ParseSettings(text, s.ToStored())); }
        catch (Exception) { s.LoadFailed = true; }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        RecorderCore.WriteSettingsText(FilePath, RecorderCore.SerializeSettings(ToStored()));
        LoadFailed = false;
    }

    // The hotkey as text, such as "Win+Shift+D".
    public string HotkeyText
    {
        get { return RecorderCore.HotkeyText(HotkeyModifiers, (int)HotkeyKey); }
    }

    // Reads text such as "Win+Shift+D". Keeps the current hotkey if the text is not valid.
    public void SetHotkey(string text)
    {
        int mods = HotkeyModifiers;
        int key = (int)HotkeyKey;
        if (!RecorderCore.TrySetHotkey(text, ref mods, ref key)) return;
        HotkeyModifiers = mods;
        HotkeyKey = (Keys)key;
    }

    // Shift alone is not enough for a letter or a digit: Shift+A must still type a capital A.
    public static bool IsValidHotkey(int mods, Keys key)
    {
        return RecorderCore.IsValidHotkey(mods, (int)key);
    }

    public static bool TryParseKey(string name, out Keys key)
    {
        int virtualKey;
        if (!RecorderCore.TryParseKey(name, out virtualKey))
        {
            key = Keys.None;
            return false;
        }
        key = (Keys)virtualKey;
        return true;
    }

    // "D5" is the name of the 5 key. Show it as "5".
    public static string KeyName(Keys key)
    {
        return RecorderCore.KeyName((int)key);
    }

    public static bool StartsWithWindows
    {
        get
        {
            string entry = null;
            byte[] approved = null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                if (key != null) entry = key.GetValue(RunName) as string;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey))
                if (key != null) approved = key.GetValue(RunName) as byte[];
            return StartupRule.IsOn(entry, approved);
        }
        set
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
        }
    }
}

class Recorder
{
    readonly RecordingSession session = new RecordingSession();
    // NVENC probe result for each ffmpeg path. The probe runs once for each path.
    static readonly Dictionary<string, bool> nvencUsable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // System audio (docs/audio-transcript.md). The capture runs from before FFmpeg starts until after it stops.
    // An automatic restart after a failed start (fallback) keeps the same capture.
    LoopbackCapture audio;
    AudioTake lastAudio;
    // Why the audio is off for this recording (the capture did not start), or null.
    public string AudioNote;

    public Recorder()
    {
        session.Failed += delegate(string message)
        {
            Action<string> handler = Failed;
            if (handler != null) handler(message);
        };
    }

    // Raised on a worker thread when ffmpeg ends by itself. The argument is ffmpeg's error text.
    public event Action<string> Failed;

    public bool IsRecording { get { return session.IsRecording; } }

    public static string FindFfmpeg()
    {
        string exeDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return RecorderCore.FindFfmpeg(exeDirectory, path, true);
    }

    // True when this ffmpeg build lists h264_nvenc AND a short test encode with h264_nvenc works.
    // The gyan.dev builds list h264_nvenc also on a PC without an NVIDIA GPU, so the list alone is not proof.
    // The list is the quick first check. Both run only the first time for each ffmpeg path.
    static bool IsNvencUsable(string ffmpeg)
    {
        bool usable;
        lock (nvencUsable)
        {
            if (nvencUsable.TryGetValue(ffmpeg, out usable)) return usable;
        }
        usable = RecorderCore.ProbeNvencListed(ffmpeg, 5000) && RecorderCore.TestNvencEncode(ffmpeg, 15000);
        lock (nvencUsable) { nvencUsable[ffmpeg] = usable; }
        return usable;
    }

    // Runs the NVENC test once in the background at app start. Without it, the first recording waited
    // about 0.4 s more for the test (measured on Bran's PC).
    public static void WarmUp(Settings s)
    {
        if (RecorderCore.NormalizeEncoder(s.Encoder) != RecorderCore.EncoderAuto) return;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string ffmpeg = FindFfmpeg();
                if (ffmpeg != null) IsNvencUsable(ffmpeg);
            }
            catch (Exception) { }
        });
    }

    // After an NVENC failure during a recording: "auto" uses libx264 until the app restarts.
    public static void MarkNvencUnusable()
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        lock (nvencUsable) { nvencUsable[ffmpeg] = false; }
    }

    // The monitors in physical px (the process is DPI aware).
    // Index = the ddagrab output_idx: the DXGI output of adapter 0 with the same device name (or the same
    // desktop rectangle). The order of Screen.AllScreens is not the DXGI order. Index -1 = not on adapter 0,
    // so PlanCapture uses gdigrab for that monitor. UNVERIFIED: a run on a PC with 2 or more monitors.
    public static List<MonitorBounds> Monitors()
    {
        List<MonitorBounds> list = new List<MonitorBounds>();
        Screen[] screens = Screen.AllScreens;
        List<DxgiOutputInfo> outputs = NativeMethods.GetDxgiOutputs();
        for (int i = 0; i < screens.Length; i++)
        {
            Rectangle b = screens[i].Bounds;
            int index = RecorderCore.MatchDxgiOutput(screens[i].DeviceName, b.X, b.Y, b.Width, b.Height,
                outputs, screens.Length);
            list.Add(new MonitorBounds(index, b.X, b.Y, b.Width, b.Height));
        }
        return list;
    }

    // Records the region (physical px, virtual-screen coordinates). An empty region records the whole desktop.
    // Returns null on success, or a message to show. plan tells which backend and encoder the recording uses.
    // forceGdigrab and forceX264 are for the automatic fallback after a failed start (TrayApp).
    public string Start(Settings s, Rectangle region, bool forceGdigrab, bool forceX264, out CapturePlan plan)
    {
        string folder;
        return Start(s, region, forceGdigrab, forceX264, null, out plan, out folder);
    }

    // Folder mode (s.OutputMode = folder): FFmpeg records the lossless work file into a new recording folder,
    // or into reuseFolder (the automatic fallback after a failed start). recordingFolder is that folder,
    // or null in MP4 mode.
    public string Start(Settings s, Rectangle region, bool forceGdigrab, bool forceX264, string reuseFolder,
        out CapturePlan plan, out string recordingFolder)
    {
        plan = null;
        recordingFolder = null;
        // A new recording (not a restart with the capture still running) has no audio from before.
        if (audio == null) { lastAudio = null; AudioNote = null; }
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return "ffmpeg.exe was not found. Run this in PowerShell: winget install -e --id Gyan.FFmpeg. " +
                "Or put ffmpeg.exe next to DesktopRecorder.exe.";
        StoredSettings stored = s.ToStored();
        if (forceGdigrab) stored.CaptureBackend = RecorderCore.BackendGdigrab;
        if (forceX264) stored.Encoder = RecorderCore.EncoderX264;
        // Only "auto" needs the probe. An explicit encoder is used as set.
        bool nvenc = RecorderCore.NormalizeEncoder(stored.Encoder) == RecorderCore.EncoderAuto
            ? IsNvencUsable(ffmpeg) : true;
        CaptureRegion capture = region.IsEmpty
            ? new CaptureRegion(0, 0, 0, 0)
            : new CaptureRegion(region.X, region.Y, region.Width, region.Height);
        List<MonitorBounds> monitors = Monitors();
        plan = RecorderCore.PlanCapture(stored, capture, monitors, nvenc);
        DateTime now = DateTime.Now;
        if (RecorderCore.NormalizeOutputMode(s.OutputMode) == RecorderCore.OutputMp4)
        {
            string arguments = RecorderCore.JoinWindowsArguments(
                RecorderCore.BuildCaptureArguments(stored, capture, monitors, now, nvenc));
            // The WAV file has the name of the video: desktop_<time>.wav. Not "desktop_*.mp4", so it is not
            // counted as a video of this recording.
            if (s.RecordAudio) StartAudio(s.Folder, "desktop_" + now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture) + ".wav");
            string mp4Error = session.Start(ffmpeg, arguments, s.Folder, s.Fps);
            if (mp4Error != null) DiscardAudio();
            return mp4Error;
        }
        string folder = reuseFolder;
        if (folder == null)
        {
            string name = RecorderCore.RecordingFolderName(now);
            folder = Path.Combine(s.Folder, name);
            for (int n = 2; Directory.Exists(folder) && n < 100; n++)
                folder = Path.Combine(s.Folder, name + "_" + n.ToString(CultureInfo.InvariantCulture));
        }
        string work = Path.Combine(folder, AfterRecording.WorkFileName);
        string folderArguments = RecorderCore.JoinWindowsArguments(
            RecorderCore.BuildCaptureArguments(stored, capture, monitors, now, nvenc, work));
        if (s.RecordAudio) StartAudio(folder, AudioMux.AudioFileName);
        string error = session.Start(ffmpeg, folderArguments, folder, s.Fps, AfterRecording.WorkFileName);
        if (error == null) recordingFolder = folder;
        else if (reuseFolder == null) DiscardAudio();
        else StopAudio();   // a failed restart: the recording is over, keep what the WAV file has
        return error;
    }

    // Starts the capture before FFmpeg, so the WAV file covers the first frame. A capture that runs already
    // (restart after a failed start) goes on. A failure only turns the audio off (AudioNote).
    void StartAudio(string folder, string fileName)
    {
        if (audio != null) return;
        AudioNote = null;
        lastAudio = null;
        try
        {
            Directory.CreateDirectory(folder);
            LoopbackCapture c = new LoopbackCapture();
            string error = c.Start(Path.Combine(folder, fileName));
            if (error == null) { audio = c; return; }
            AudioTake failed = c.Stop();
            AudioNote = error;
            TryDelete(failed.Path);
        }
        catch (Exception e) { AudioNote = "System audio is off for this recording: " + e.Message; }
    }

    void DiscardAudio()
    {
        AudioTake t = StopAudio();
        if (t != null) TryDelete(t.Path);
        lastAudio = null;
    }

    static void TryDelete(string path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    // Stops the audio capture (after FFmpeg). Returns the take, or null when no audio was recorded.
    // StartClockSeconds uses the clock of the last FFmpeg start (after a fallback restart: the new one).
    public AudioTake StopAudio()
    {
        LoopbackCapture c = audio;
        audio = null;
        if (c == null) return lastAudio;
        AudioTake t = c.Stop();
        t.StartClockSeconds = (c.StartTimestamp - session.ClockStartTimestamp) / (double)Stopwatch.Frequency;
        lastAudio = t;
        return t;
    }

    // The audio of the last recording (after Stop), or null.
    public AudioTake LastAudio { get { return lastAudio; } }

    // NVENC is usable for the clip.mp4 encode after the recording (the same probe as the capture).
    public static bool NvencForClip(Settings s)
    {
        if (RecorderCore.NormalizeEncoder(s.Encoder) != RecorderCore.EncoderAuto) return true;
        string ffmpeg = FindFfmpeg();
        return ffmpeg != null && IsNvencUsable(ffmpeg);
    }

    public double ClockSeconds { get { return session.ClockSeconds; } }
    public double ClockOffset { get { return session.ClockOffset; } }

    public string[] SessionFiles()
    {
        return session.SessionFiles();
    }

    public RecordingStats LastStats()
    {
        return session.LastStats();
    }

    // FFmpeg did not end by itself at the last Stop.
    public bool KilledAtStop { get { return session.KilledAtStop; } }

    // The Stop sound must not be in the audio: the audio ends at this time.
    public void MarkAudioEnd()
    {
        LoopbackCapture c = audio;
        if (c != null) c.MarkEnd();
    }

    public void Stop()
    {
        session.Stop();
        StopAudio();
    }

    // MP4 output: adds the audio to each video file of the last recording (stream copy, audio as AAC).
    // Split files: file n starts at n x split minutes on the clock of FFmpeg. Returns null, or a note.
    public string AddAudioToMp4Files(Settings s)
    {
        AudioTake t = lastAudio;
        if (t == null || t.Path == null || !File.Exists(t.Path)) return null;
        string ffmpeg = FindFfmpeg();
        string[] files = session.SessionFiles();
        if (ffmpeg == null || files.Length == 0) return "The audio stays in " + Path.GetFileName(t.Path) + ".";
        double offset = session.ClockOffset;
        bool ok = true;
        for (int i = 0; i < files.Length; i++)
        {
            int split = RecorderCore.ClampSplitMinutes(s.SplitMinutes);
            double shift = AudioClock.VideoTimeOfAudioStart(t.StartClockSeconds, offset, 0) - AudioMux.SegmentStart(i, split);
            // Each file but the last ends at the next cut.
            double end = split > 0 && i < files.Length - 1 ? split * 60.0 : 0;
            string temp = files[i] + ".audio.tmp";
            string output;
            int exit;
            string[] a = AudioMux.BuildRemuxArguments(files[i], t.Path, shift, temp, end);
            // The temp name does not end in .mp4: give the format.
            List<string> args = new List<string>(a);
            args.Insert(args.Count - 1, "-f");
            args.Insert(args.Count - 1, "mp4");
            if (RecorderCore.RunFfmpeg(ffmpeg, RecorderCore.JoinWindowsArguments(args.ToArray()), 10 * 60 * 1000, out output, out exit)
                && exit == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
            {
                try { AfterRecording.ReplaceFile(temp, files[i]); continue; }
                catch (Exception) { }
            }
            ok = false;
            TryDelete(temp);
        }
        if (!ok) return "The audio could not be added to every video file. It stays in " + Path.GetFileName(t.Path) + ".";
        TryDelete(t.Path);
        return null;
    }
}

// Two short tones, made in memory. Start goes up, stop goes down. With "record the sound of the PC" on, the
// start tone can be at the start of the audio (the capture starts before the tone plays). UNVERIFIED.
static class Sounds
{
    static readonly System.Media.SoundPlayer startSound = Make(660, 880);
    static readonly System.Media.SoundPlayer stopSound = Make(880, 660);
    // Higher and shorter in pitch than start and stop: the ZIP is on the clipboard (Bran, 2026-11-22).
    static readonly System.Media.SoundPlayer readySound = Make(1047, 1568);

    public static void PlayStart() { Play(startSound); }
    public static void PlayStop() { Play(stopSound); }
    public static void PlayReady() { Play(readySound); }

    static void Play(System.Media.SoundPlayer player)
    {
        try { player.Play(); } catch (Exception) { }
    }

    static System.Media.SoundPlayer Make(double firstHz, double secondHz)
    {
        return new System.Media.SoundPlayer(new MemoryStream(RecorderCore.BuildChimeWav(firstHz, secondHz)));
    }
}

// A hidden window that listens for the global hotkeys. Each hotkey has its own id.
class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    readonly HashSet<int> registered = new HashSet<int>();

    // The argument is the id of the hotkey.
    public event Action<int> Pressed;

    public HotkeyWindow()
    {
        CreateParams parameters = new CreateParams();
        parameters.Caption = "Desktop Recorder";
        CreateHandle(parameters);
    }

    public bool IsRegistered(int id)
    {
        return registered.Contains(id);
    }

    // Replaces the hotkey with this id. Returns false if another program already uses it.
    // modifiers: 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win.
    public bool Register(int id, int modifiers, Keys key)
    {
        if (registered.Remove(id)) UnregisterHotKey(Handle, id);
        // 0x4000 = do not repeat while the keys are held.
        if (!RegisterHotKey(Handle, id, (uint)modifiers | 0x4000, (uint)key)) return false;
        registered.Add(id);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            Action<int> handler = Pressed;
            if (handler != null) handler(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    public void Close()
    {
        foreach (int id in registered) UnregisterHotKey(Handle, id);
        registered.Clear();
        DestroyHandle();
    }
}

class TrayApp : ApplicationContext
{
    const int SelectHotkeyId = 1;

    readonly HotkeyWindow hotkey = new HotkeyWindow();
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Recorder recorder = new Recorder();
    readonly Control ui = new Control();
    readonly TrayMenu menu = new TrayMenu();
    string lastResult;              // "Last recording: ..." in the tray menu, or null
    bool lastZipAvailable;          // the ZIP of the last recording is there
    readonly System.Windows.Forms.Timer clockTimer = new System.Windows.Forms.Timer();   // the time in the menu
    Settings settings = Settings.Load();
    SettingsForm openForm;
    RecordingFrame frame;
    Rectangle recordingRegion = Rectangle.Empty;   // empty = whole desktop
    double recordingFps;
    // Automatic fallback after a failed start (HANDOFF 6.5 order): the plan and the time of the last start,
    // and the fallbacks that the current recording uses already.
    CapturePlan recordingPlan;
    int recordingStartTicks;
    bool forcedGdigrab, forcedX264;
    const int FallbackWindowMs = 10000;
    bool selecting;
    volatile bool stopping;   // FFmpeg is finishing the file on a background thread
    bool stopRequested;       // the user stopped the current recording: a late Failed is not a failure
    volatile bool sessionEnding;   // Windows signs out or shuts down
    // Folder mode (AI capture brief, Phase 1).
    string recordingFolder;                 // null in MP4 mode
    DateTime recordingStartTime;
    Settings recordingSettings;             // the settings of the running recording
    readonly List<KeyValuePair<double, string>> titleSamples = new List<KeyValuePair<double, string>>();
    readonly System.Windows.Forms.Timer titleTimer = new System.Windows.Forms.Timer();
    PassJob lastJob;                        // the last recording in folder mode
    Icon currentIcon;
    IntPtr currentIconHandle = IntPtr.Zero;

    public TrayApp()
    {
        IntPtr unused = ui.Handle; // makes the window that lets worker threads call the UI thread
        RegisterHotkeys();

        titleTimer.Interval = 250;
        titleTimer.Tick += delegate { SampleTitle(); };
        // The tray menu (TrayMenu.cs). Each command does what the old menu item did.
        menu.Command += delegate(TrayCommand command)
        {
            switch (command)
            {
                case TrayCommand.Record: Toggle(); break;
                case TrayCommand.OpenFolder: OpenFolder(); break;
                case TrayCommand.ShowLastZip: OpenLastFolder(); break;
                case TrayCommand.CopyLastZip: CopyZip(); break;
                case TrayCommand.Settings: ShowSettings(); break;
                default: QuitCommand(); break;
            }
        };
        // Scale and theme before the menu opens: the right button down comes before the button up that opens it.
        tray.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            menu.ApplyLook(NativeMethods.GetScaleForPoint(Cursor.Position));
            UpdateMenu();
        };
        // The menu window is made now: per-monitor DPI aware (the process default), and its size is known
        // before the first open.
        menu.ApplyLook(NativeMethods.GetScaleForPoint(Cursor.Position));
        menu.CreateWindow();
        tray.ContextMenuStrip = menu.Strip;
        busyTimer.Tick += delegate { BusyTick(); };
        clockTimer.Interval = 1000;
        clockTimer.Tick += delegate { menu.UpdateClock(MenuState()); };
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;

        recorder.Failed += delegate(string message)
        {
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    // The user stopped it (its own job has the file), or Windows ends the session (FFmpeg can end first).
                    if (stopRequested || sessionEnding) return;
                    if (TryFallback(message)) return;
                    // No restart: the recording is over, so the audio capture stops too.
                    AudioTake take = recorder.StopAudio();
                    DisposeFrame();
                    titleTimer.Stop();
                    Refresh();
                    if (settings.PlaySounds) Sounds.PlayStop();
                    if (recordingFolder != null && WorkFileHasData(recordingFolder))
                    {
                        // Keep what was recorded: the pass makes the frames and clip.mp4 from the work file.
                        Rectangle region = recordingRegion;
                        bool seen = unchecked(Environment.TickCount - recordingStartTicks) >= 3000;
                        StartPass("FFmpeg stopped: " + Shorten(message));
                        // A UAC prompt, the lock screen or a display change ended the capture. Record on in a new folder.
                        if (seen && RecorderCore.IsCaptureLoss(message)) ResumeLater(region);
                        return;
                    }
                    string stats = UpdateStats();
                    if (take != null && take.Path != null && File.Exists(take.Path))
                        stats += " The audio is in " + Path.GetFileName(take.Path) + ".";
                    // Stats first: Shorten cuts the end of a long ffmpeg message.
                    tray.ShowBalloonTip(8000, "Desktop Recorder stopped",
                        Shorten(stats + "\n" + message), ToolTipIcon.Error);
                });
            }
            catch (InvalidOperationException) { }
        };
        // SessionEnded, not SessionEnding: a program can refuse the sign-out, and the recording then goes on.
        SystemEvents.SessionEnded += delegate
        {
            sessionEnding = true;
            recorder.Stop();
        };

        resumeTimer.Interval = 1000;
        resumeTimer.Tick += delegate { ResumeTick(); };

        hotkey.Pressed += delegate(int id)
        {
            if (CancelResume()) return;
            if (recorder.IsRecording)
            {
                StopRecording();
                Refresh();
            }
            else SelectAndRecord();
        };

        Refresh();
        Recorder.WarmUp(settings);
        string warning = HotkeyWarning();
        if (warning != null) tray.ShowBalloonTip(6000, "Desktop Recorder", warning, ToolTipIcon.Warning);
        RecoverOldRecordings();
    }

    // ------------------------------------------------------------ record on after a capture loss

    readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();
    Rectangle resumeRegion;
    DateTime resumeUntil;
    const int ResumeSeconds = 120;   // not longer: after a long lock, the user does not expect a recording

    void ResumeLater(Rectangle region)
    {
        resumeRegion = region;
        resumeUntil = DateTime.Now.AddSeconds(ResumeSeconds);
        resumeTimer.Start();
    }

    void ResumeTick()
    {
        if (quitRequested || sessionEnding || selecting || stopping || recorder.IsRecording || DateTime.Now > resumeUntil)
        {
            resumeTimer.Stop();
            return;
        }
        if (!NativeMethods.InputDesktopAccessible()) return;
        resumeTimer.Stop();
        StartRecordingWith(resumeRegion, true, true, "The screen capture stopped. The recording goes on in a new ZIP.",
            forcedGdigrab, forcedX264, null);
    }

    // The user pressed the hotkey to stop while the app waits to record on. True = it waited.
    bool CancelResume()
    {
        if (!resumeTimer.Enabled) return false;
        resumeTimer.Stop();
        Refresh();
        return true;
    }

    // ------------------------------------------------------------ recordings that were not finished

    // The app or the PC stopped during a recording or its pass: the folder still has the lossless work file. Makes
    // the ZIP in the background. Not on the clipboard: the user did not just stop it.
    void RecoverOldRecordings()
    {
        Settings copy = settings.Copy();
        System.Threading.Thread t = new System.Threading.Thread(delegate()
        {
            try
            {
                System.Threading.Thread.Sleep(5000);
                if (!Directory.Exists(copy.Folder)) return;
                foreach (string dir in Directory.GetDirectories(copy.Folder, "desktop_*"))
                {
                    string work = Path.Combine(dir, AfterRecording.WorkFileName);
                    string pending = Path.Combine(dir, AfterRecording.PendingMarkerName);
                    if (!File.Exists(work) && !File.Exists(pending)) continue;
                    // A new recording and its ZIP come first.
                    while (!quitRequested && (recorder.IsRecording || stopping || passesRunning > 0)) System.Threading.Thread.Sleep(2000);
                    if (quitRequested || sessionEnding) return;
                    if (string.Equals(dir, recordingFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    bool zip = File.Exists(dir + ".zip");
                    bool marker = File.Exists(Path.Combine(dir, AfterRecording.DoneMarkerName));
                    try
                    {
                        if (RecordingIndex.NeedsRecovery(Path.GetFileName(dir), AfterRecording.HasVideo(work), zip, marker)) RecoverOne(dir, copy);
                        else if (File.Exists(pending) && !File.Exists(work) && !zip && File.Exists(Path.Combine(dir, AfterRecording.IndexFileName)))
                            RecoverZip(dir);
                    }
                    catch (Exception e) { Program.LogError(e); }   // the next folder must not wait for this one
                }
            }
            catch (Exception e) { Program.LogError(e); }
        });
        t.IsBackground = true;
        t.Name = "recovery";
        t.Start();
    }

    // The app ended after the pass and before the ZIP. The folder stays: what the WAV file has is not known here.
    void RecoverZip(string dir)
    {
        MakeZipLocked(dir, null);
        try
        {
            ui.BeginInvoke((MethodInvoker)delegate
            {
                lastResult = "Recovered recording: " + Path.GetFileName(dir) + ".zip";
                UpdateMenu();
            });
        }
        catch (InvalidOperationException) { }
    }

    void RecoverOne(string dir, Settings s)
    {
        if (Recorder.FindFfmpeg() == null) return;
        DateTime start;
        RecordingIndex.TryFolderTime(Path.GetFileName(dir), out start);
        AfterRecordingInput input = NewInput(dir, start, s, s.Fps);
        input.TranscriptWaitMs = -1;
        input.Titles = new TitleTimeline();
        input.Problem = "The app or the PC stopped before this recording was finished, so the end can be missing.";
        string wav = Path.Combine(dir, AudioMux.AudioFileName);
        if (File.Exists(wav)) input.AudioFile = wav;
        // The frames of the pass that was cut off can be different ones.
        string frames = Path.Combine(dir, "frames");
        if (Directory.Exists(frames))
            foreach (string f in Directory.GetFiles(frames, "*.png"))
                if (AfterRecording.IsOwnFrame(Path.GetFileName(f))) File.Delete(f);
        PassJob job = new PassJob();
        job.Input = input;
        job.Done = true;
        job.Step = ZipStep.Zip;
        job.CopyError = "this recording was not finished before.";
        RunPass(job);
        AfterRecordingResult r = job.Result;
        string notePath = Path.Combine(dir, AfterRecording.NoteFileName);
        if (r.Ok && File.Exists(notePath)) AfterRecording.SaveNote(input, r, File.ReadAllText(notePath).Trim());
        if (HasContent(r)) job.Zip = MakeZipLocked(dir, null);
        else job.ZipError = Shorten(r.Error);
        job.ZipBytes = FileBytes(job.Zip);
        job.FailText = ZipStatus.Problem(r.Ok, HasContent(r), job.Zip != null, job.ZipError);
        try
        {
            ui.BeginInvoke((MethodInvoker)delegate
            {
                job.Step = ZipStatus.FinalStep(r.Ok, job.ZipError);
                if (lastJob == null && job.Zip != null) lastJob = job;
                lastResult = "Recovered recording: " + Path.GetFileName(dir) + (job.Zip != null ? ".zip" : " (failed)");
                UpdateLastMenu();
                if (!recorder.IsRecording && passesRunning == 0 && !quitRequested) ShowToast(job);
                TryCleanup(job);
            });
        }
        catch (InvalidOperationException) { }
    }

    static bool HasContent(AfterRecordingResult r)
    {
        return r != null && (r.Ok || r.ClipWritten || r.Frames.Count > 0);
    }

    // FFmpeg stopped by itself. When that happens in the first seconds and a fallback is left
    // (NVENC -> libx264, ddagrab -> gdigrab), start again with the fallback. Returns true when it did.
    bool TryFallback(string message)
    {
        if (recorder.IsRecording || recordingPlan == null) return false;
        if (unchecked(Environment.TickCount - recordingStartTicks) > FallbackWindowMs) return false;
        bool useGdigrab, useX264;
        string reason;
        if (!RecorderCore.ChooseFallback(recordingPlan, settings.Encoder, message,
            out useGdigrab, out useX264, out reason)) return false;
        bool hasVideo = recordingFolder != null && WorkFileHasData(recordingFolder);
        // A UAC prompt in the first seconds is not a fault of ddagrab (ResumeLater).
        if (hasVideo && RecorderCore.IsCaptureLoss(message)) return false;
        if (useX264 && RecorderCore.LooksLikeNvencError(message)) Recorder.MarkNvencUnusable();
        Rectangle region = recordingRegion;
        DisposeFrame();
        string firstLine = (message ?? "").Replace("\r", "").Split('\n')[0];
        string reuse = recordingFolder;
        if (hasVideo)
        {
            // The part before the failure is a recording of its own. Before, it stayed out of the ZIP and was lost.
            recorder.StopAudio();
            titleTimer.Stop();
            StartPass("FFmpeg stopped: " + Shorten(message));
            reuse = null;
        }
        bool started = StartRecordingWith(region, false, true, reason + "\nFFmpeg: " + firstLine,
            forcedGdigrab || useGdigrab, forcedX264 || useX264, reuse);
        if (!started && hasVideo && settings.PlaySounds) Sounds.PlayStop();
        return started || hasVideo;
    }

    // A balloon text must be shorter than 256 characters.
    static string Shorten(string text)
    {
        if (text == null) return "";
        return text.Length > 250 ? text.Substring(0, 250) : text;
    }

    void RegisterHotkeys()
    {
        hotkey.Register(SelectHotkeyId, settings.HotkeyModifiers, settings.HotkeyKey);
    }

    // Null when both hotkeys work.
    string HotkeyWarning()
    {
        List<string> used = new List<string>();
        if (!hotkey.IsRegistered(SelectHotkeyId)) used.Add(settings.HotkeyText);
        if (used.Count == 0) return null;
        return string.Join(" and ", used.ToArray()) + (used.Count == 1 ? " is" : " are")
            + " used by another program, so that hotkey is off.";
    }

    void SaveSettingsQuietly()
    {
        if (settings.LoadFailed) return;
        try { settings.Save(); }
        catch (Exception e)
        {
            tray.ShowBalloonTip(6000, "Desktop Recorder", "Cannot save the settings: " + e.Message, ToolTipIcon.Warning);
        }
    }

    // The record hotkey and the first tray item: show the selector, then record the selection.
    void SelectAndRecord()
    {
        // No new recording while the app closes: ExitIfDone would end the app before its pass (review 2026-11-22).
        if (selecting || stopping || recorder.IsRecording || quitRequested) return;
        selecting = true;
        Rectangle? picked;
        RegionSelector.ShowCrosshair = settings.SelectorCrosshair;
        RegionSelector.ShowMagnifier = settings.SelectorMagnifier;
        RegionSelector.ShowPosition = settings.SelectorPosition;
        // The hotkey can come while the settings dialog is open. That dialog sets the thread to system aware,
        // so set per-monitor aware here: the selector needs physical pixels.
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextPerMonitorAwareV2);
        try
        {
            // No preselected region: the release of the mouse starts the recording, so a drag must always
            // draw a new box.
            picked = RegionSelector.Show(null);
        }
        catch (Exception e)
        {
            tray.ShowBalloonTip(8000, "Desktop Recorder", "The region selector failed: " + Shorten(e.Message), ToolTipIcon.Error);
            return;
        }
        finally
        {
            NativeMethods.RestoreThreadDpiContext(oldDpi);
            selecting = false;
            SaveSelectorOptions();
        }
        if (!picked.HasValue) return; // cancelled
        settings.Region = picked.Value;
        settings.LastRegion = picked.Value;
        SaveSettingsQuietly();
        StartRecording(picked.Value, true, true, null);
    }

    // Keys C, M and P in the selector change these options. Keep them for the next start of the app.
    void SaveSelectorOptions()
    {
        if (settings.SelectorCrosshair == RegionSelector.ShowCrosshair
            && settings.SelectorMagnifier == RegionSelector.ShowMagnifier
            && settings.SelectorPosition == RegionSelector.ShowPosition) return;
        settings.SelectorCrosshair = RegionSelector.ShowCrosshair;
        settings.SelectorMagnifier = RegionSelector.ShowMagnifier;
        settings.SelectorPosition = RegionSelector.ShowPosition;
        SaveSettingsQuietly();
    }

    // region: physical px, virtual-screen coordinates. Empty = whole desktop (no frame).
    void StartRecording(Rectangle region, bool sound, bool balloon, string note)
    {
        StartRecordingWith(region, sound, balloon, note, false, false);
    }

    void StartRecordingWith(Rectangle region, bool sound, bool balloon, string note, bool forceGdigrab, bool forceX264)
    {
        StartRecordingWith(region, sound, balloon, note, forceGdigrab, forceX264, null);
    }

    // True = the recording started.
    bool StartRecordingWith(Rectangle region, bool sound, bool balloon, string note, bool forceGdigrab, bool forceX264,
        string reuseFolder)
    {
        CapturePlan plan;
        string folder;
        bool restart = reuseFolder != null;
        if (restart)
        {
            // Keep the file of the failed start: the new FFmpeg writes the same name with -y (review 2026-11-19).
            try
            {
                string old = Path.Combine(reuseFolder, AfterRecording.WorkFileName);
                if (File.Exists(old))
                {
                    string kept = Path.Combine(reuseFolder, "work_lossless_failed_" + DateTime.Now.ToString("HHmmss_fff", CultureInfo.InvariantCulture) + ".mp4");
                    File.Move(old, kept);
                }
            }
            catch (Exception) { }
        }
        string error = recorder.Start(settings, region, forceGdigrab, forceX264, reuseFolder, out plan, out folder);
        string frameWarning = null;
        if (error == null)
        {
            stopRequested = false;
            recordingFolder = folder;
            if (!restart)
            {
                recordingStartTime = DateTime.Now;
                recordingSettings = settings.Copy();
            }
            lock (titleSamples) titleSamples.Clear();
            if (folder != null)
            {
                SampleTitle();
                titleTimer.Start();
            }
            recordingRegion = region;
            recordingFps = settings.Fps;
            recordingPlan = plan;
            recordingStartTicks = Environment.TickCount;
            forcedGdigrab = forceGdigrab;
            forcedX264 = forceX264;
            frameWarning = ShowFrame(region);
            if (frameWarning != null) balloon = true;
        }
        Refresh();
        if (error != null)
        {
            tray.ShowBalloonTip(8000, "Desktop Recorder", Shorten(error), ToolTipIcon.Error);
            return false;
        }
        if (sound && settings.PlaySounds) Sounds.PlayStart();
        string planNote = plan != null ? plan.Note : "";
        if (recorder.AudioNote != null) balloon = true;
        if (balloon || (planNote != null && planNote.Length > 0))
        {
            string text = "Recording started";
            if (plan != null) text += " (" + plan.Backend + ", " + plan.Encoder + ")";
            text += ".";
            if (note != null) text += "\n" + note;
            if (planNote != null && planNote.Length > 0) text += "\n" + planNote;
            if (frameWarning != null) text += "\n" + frameWarning;
            if (recorder.AudioNote != null) text += "\n" + recorder.AudioNote;
            tray.ShowBalloonTip(3000, "Desktop Recorder", Shorten(text), ToolTipIcon.Info);
        }
        return true;
    }

    // Returns a warning when part of the frame is inside the region and Windows did not accept
    // WDA_EXCLUDEFROMCAPTURE, so the frame can be in the video. Else returns null.
    string ShowFrame(Rectangle region)
    {
        DisposeFrame();
        if (region.IsEmpty) return null;
        // The frame needs physical pixels (see SelectAndRecord).
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextPerMonitorAwareV2);
        try
        {
            frame = new RecordingFrame(region);
            // The click comes from the Stop button form. Stop after its click handler ends.
            frame.StopClicked += delegate
            {
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        StopRecording();
                        Refresh();
                    });
                }
                catch (InvalidOperationException) { }
            };
            frame.Show();
            if (frame.PartlyInsideRegion && !frame.ExcludedFromCapture)
                return "The region touches the screen edge, so part of the red frame or the Stop button is inside it. "
                    + "Windows cannot keep them out of the video.";
            return null;
        }
        catch (Exception)
        {
            // The recording continues without the frame.
            DisposeFrame();
            return null;
        }
        finally
        {
            NativeMethods.RestoreThreadDpiContext(oldDpi);
        }
    }

    void DisposeFrame()
    {
        if (frame == null) return;
        try { frame.Dispose(); } catch (Exception) { }
        frame = null;
    }

    // Reads the stats of the last recording into the tray menu, and returns the status line.
    string UpdateStats()
    {
        string text;
        try { text = RecorderCore.FormatStats(recorder.LastStats()); }
        catch (Exception) { text = "No stats."; }
        lastResult = "Last recording: " + text;
        UpdateMenu();
        return text;
    }

    void Toggle()
    {
        if (CancelResume()) return;
        if (recorder.IsRecording) { StopRecording(); Refresh(); }
        else SelectAndRecord();
    }

    // Stops, shows the status line and, if the setting is on, puts the finished video file(s) on the clipboard.
    // The border goes away and the sound plays at once. FFmpeg finishes the file on a background thread
    // (0.4 s with NVENC, more with x264), then FinishStop runs on the UI thread. wait = true stops
    // synchronously (used by Quit).
    void StopRecording(bool sound = true, bool wait = false)
    {
        if (stopping) return;
        if (!recorder.IsRecording)
        {
            DisposeFrame();
            return;
        }
        // Before the Stop sound, so that it is not in the audio.
        recorder.MarkAudioEnd();
        if (sound && settings.PlaySounds) Sounds.PlayStop();
        DisposeFrame();
        titleTimer.Stop();
        Stopwatch fromStop = Stopwatch.StartNew();
        double stopClock = recorder.ClockSeconds;
        if (wait && recordingFolder == null)
        {
            stopRequested = true;
            recorder.Stop();
            mp4AudioNote = recorder.AddAudioToMp4Files(recordingSettings ?? settings);
            FinishStop();
            return;
        }
        // Folder mode always stops in the background. Quit waits for the pass (see Quit).
        PassJob job = recordingFolder != null ? CreateJob(null) : null;
        if (job != null && wait) job.NoNoteBox = true;
        stopRequested = true;
        stopping = true;
        if (job != null) passesRunning++;
        // The note box does not wait for the pass, and the pass does not wait for the note box (Bran, 2026-11-19).
        if (job != null && !job.NoNoteBox) ShowNoteBox(job);
        if (job != null) BeginZipWork(job, ZipStep.Stopping);
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { recorder.Stop(); }
            catch (Exception) { }
            if (job != null)
            {
                // The clock offset is final after the stop.
                job.Input.Titles = BuildTitles();
                double offset = recorder.ClockOffset;
                if (!double.IsNaN(offset)) job.Input.EndTimeHint = stopClock - offset;
                SetAudioInput(job.Input, recorder.LastAudio, offset);
                if (recorder.KilledAtStop)
                    job.Input.Problem = "FFmpeg did not end in time at Stop, so the last moment can be missing.";
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        stopping = false;
                        SetStep(job, ZipStep.Frames);
                    });
                }
                catch (InvalidOperationException) { stopping = false; }
                RunPass(job);
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        passesRunning--;
                        OnPassDone(job, fromStop);
                        Refresh();
                    });
                }
                catch (InvalidOperationException) { }
                return;
            }
            // MP4 output: the audio goes into the video file(s) before they go to the clipboard.
            string audioNote = null;
            try { audioNote = recorder.AddAudioToMp4Files(recordingSettings ?? settings); }
            catch (Exception e) { audioNote = "The audio could not be added: " + e.Message; }
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    stopping = false;
                    mp4AudioNote = audioNote;
                    FinishStop();
                    Refresh();
                    ExitIfDone();
                });
            }
            catch (InvalidOperationException) { stopping = false; }
        });
    }

    string mp4AudioNote;   // a note about the audio of the last MP4 recording, or null

    // The audio of the recording for the pass (docs/audio-transcript.md).
    static void SetAudioInput(AfterRecordingInput input, AudioTake take, double clockOffset)
    {
        if (take == null || take.Path == null) return;
        input.AudioFile = take.Path;
        input.AudioStartClock = take.StartClockSeconds;
        input.ClockOffset = clockOffset;
        input.AudioPeak = take.Peak;
        input.AudioError = take.Error;
        input.AudioNote = take.Note;
    }

    // ------------------------------------------------------------ folder mode (AI capture brief, Phase 1)

    volatile int passesRunning;
    volatile bool quitRequested;

    static bool WorkFileHasData(string folder)
    {
        return AfterRecording.HasVideo(Path.Combine(folder, AfterRecording.WorkFileName));
    }

    // Window title at the center of the region, every 250 ms, with the clock of the recording.
    void SampleTitle()
    {
        Rectangle r = recordingRegion.IsEmpty ? NativeMethods.GetVirtualScreen() : recordingRegion;
        string title = NativeMethods.WindowTitleAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        double clock = recorder.ClockSeconds;
        lock (titleSamples)
        {
            if (titleSamples.Count > 0 && titleSamples[titleSamples.Count - 1].Value == title) return;
            titleSamples.Add(new KeyValuePair<double, string>(clock, title));
        }
    }

    // The titles on the time line of the video (condition H): video time = clock - ClockOffset.
    // ClockOffset comes from the FFmpeg stats lines. Without it, the clock is used as it is.
    TitleTimeline BuildTitles()
    {
        double offset = recorder.ClockOffset;
        if (double.IsNaN(offset)) offset = 0;
        TitleTimeline t = new TitleTimeline();
        lock (titleSamples)
        {
            foreach (KeyValuePair<double, string> s in titleSamples) t.Add(s.Key - offset, s.Value);
        }
        return t;
    }

    // The ZIP waits at most this time for the transcript when everything else is ready. Whisper starts at Stop, next
    // to the frames and clip.mp4, so a normal transcript is in the first ZIP. A slower one is added later.
    const int TranscriptWaitMs = 8000;

    PassJob CreateJob(string problem)
    {
        Settings s = recordingSettings ?? settings;
        AfterRecordingInput input = NewInput(recordingFolder, recordingStartTime, s, recordingFps);
        input.Titles = BuildTitles();
        // The clip encode uses libx264 when the capture plan did (NVENC cannot encode this size, or it failed).
        input.ForceX264 = (recordingPlan != null && recordingPlan.Encoder == RecorderCore.EncoderX264) || forcedX264;
        if (problem != null)
            input.Problem = "The recording ended by itself before the user stopped it, so anything after its end is missing. " + problem;
        if (s.RecordAudio) input.AudioStartError = recorder.AudioNote;
        PassJob job = new PassJob();
        job.Input = input;
        job.Number = ++jobNumber;
        job.Problem = problem;
        // The pass reports its steps on its own thread; the indication runs on the UI thread.
        input.Step = delegate(string step)
        {
            ZipStep next = step == AfterRecording.StepTranscript ? ZipStep.Transcript : ZipStep.Frames;
            try { ui.BeginInvoke((MethodInvoker)delegate { SetStep(job, next); }); }
            catch (InvalidOperationException) { }
        };
        return job;
    }

    // Everything that does not depend on the running recording (also for a recording that was not finished).
    static AfterRecordingInput NewInput(string folder, DateTime start, Settings s, double fps)
    {
        AfterRecordingInput input = new AfterRecordingInput();
        input.Ffmpeg = Recorder.FindFfmpeg();
        input.Folder = folder;
        input.WorkFile = Path.Combine(folder, AfterRecording.WorkFileName);
        input.Start = start;
        // Condition F (Bran, 2026-11-19): the numeric UTC offset of this PC at the start time.
        input.UtcOffset = TimeZoneInfo.Local.GetUtcOffset(start);
        input.Settings = s.ToStored();
        input.Options = RecorderCore.SettledOptionsOf(input.Settings);
        input.Fps = fps;
        input.SizeLimit = s.SizeLimit;
        // Test switch: with DESKTOP_RECORDER_KEEP_WORK=1 in the environment, the lossless work file stays
        // in the folder. The pass can then run again on the same capture (test\process_clip.cs).
        input.DeleteWorkFile = Environment.GetEnvironmentVariable("DESKTOP_RECORDER_KEEP_WORK") != "1";
        input.TranscriptWaitMs = TranscriptWaitMs;
        if (s.RecordAudio)
        {
            string reason;
            input.Whisper = Transcript.Find(s.WhisperPath, s.WhisperModel, Path.GetDirectoryName(Application.ExecutablePath),
                Environment.GetEnvironmentVariable("PATH") ?? "", true, out reason);
            input.WhisperReason = reason;
            input.WhisperLanguage = s.WhisperLanguage;
        }
        return input;
    }

    // Background thread. Never throws.
    void RunPass(PassJob job)
    {
        try
        {
            if (job.Input.Ffmpeg == null) throw new IOException("ffmpeg.exe was not found.");
            // Until the ZIP is there (RecoverOldRecordings). It goes with the folder.
            try { File.WriteAllText(Path.Combine(job.Input.Folder, AfterRecording.PendingMarkerName), ""); }
            catch (Exception) { }
            job.Input.NvencAvailable = Recorder.NvencForClip(job.Input.Settings == null ? new Settings() : SettingsFrom(job.Input.Settings));
            job.Result = AfterRecording.Run(job.Input);
            // The pass ran to its end and the work file stays: RecoverOldRecordings must not try again at each start.
            // Not when Windows ends the session: the failure then comes from the shutdown.
            if (job.Result.WorkFileStays && !sessionEnding)
            {
                try { File.WriteAllText(Path.Combine(job.Input.Folder, AfterRecording.DoneMarkerName), ""); }
                catch (Exception) { }
            }
        }
        catch (Exception e)
        {
            job.Result = new AfterRecordingResult();
            job.Result.Error = e.Message;
        }
    }

    static Settings SettingsFrom(StoredSettings stored)
    {
        Settings s = new Settings();
        s.Apply(stored);
        return s;
    }

    // Starts the pass on a background thread (after FFmpeg stopped by itself).
    void StartPass(string problem)
    {
        if (recordingFolder == null) return;
        PassJob job = CreateJob(problem);
        BeginZipWork(job, ZipStep.Frames);
        // The Failed handler stopped the audio already.
        SetAudioInput(job.Input, recorder.LastAudio, recorder.ClockOffset);
        Stopwatch fromStop = Stopwatch.StartNew();
        passesRunning++;
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            RunPass(job);
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    passesRunning--;
                    OnPassDone(job, fromStop);
                    Refresh();
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    // UI thread, after the pass.
    void OnPassDone(PassJob job, Stopwatch fromStop)
    {
        job.Done = true;
        // A recording that was stopped earlier can be ready later: the tray items keep the newer one.
        if (lastJob == null || job.Number >= lastJob.Number || (lastJob.Zip == null && lastJob.ZipError != null)) lastJob = job;
        AfterRecordingResult r = job.Result ?? new AfterRecordingResult();
        string earlyNote = job.Note;
        Action before = null;
        if (earlyNote != null) before = delegate { AfterRecording.SaveNote(job.Input, r, earlyNote); };
        string clip = (r.ClipBytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        string text = r.Ok
            ? "Frames: " + r.Frames.Count.ToString(CultureInfo.InvariantCulture) + ". clip.mp4: " + clip + "."
            : "The frame pass failed. " + (r.ClipWritten ? "clip.mp4 (" + clip + ") has no settled keyframes. " : "")
              + "The work file stays in the folder. " + r.Error;
        if (r.AudioRecorded)
            text += r.HasTranscript ? " Transcript: " + r.Transcript.Count.ToString(CultureInfo.InvariantCulture) + " lines ("
                    + r.TranscriptSeconds.ToString("0", CultureInfo.InvariantCulture) + " s)."
                : r.TranscriptPending ? " The transcript is still being made."
                : r.Ok ? " No transcript: " + (r.TranscriptNote ?? "") + "." : "";
        if (r.AudioRecorded && !r.AudioInClip && HasContent(r))
            text += " The sound is not in clip.mp4: " + AudioMux.AudioFileName + " stays in the folder.";
        if (!r.AudioRecorded && !string.IsNullOrEmpty(job.Input.AudioStartError))
            text = "No sound: " + job.Input.AudioStartError + " " + text;
        if (r.MotionNote != null) text += " The screen did not settle (moving content): the frames are samples.";
        if (job.Problem != null) text = job.Problem + "\n" + text;
        // Folder mode gives one ZIP only (Bran, 2026-11-22). The ZIP is made on a background thread.
        // Exit waits for it (passesRunning). The folder is deleted when the ZIP is done and the note box is closed.
        job.ZipPending++;
        SetStep(job, ZipStep.Zip);
        job.TranscriptPending = r.TranscriptPending;
        MakeZipAsync(job, before, delegate(string error)
        {
            FinishPass(job, fromStop, r, text, error);
            if (job.TranscriptPending) AddTranscriptLater(job, r);
            TryCleanup(job);
        });
    }

    // Whisper was slower than the wait limit: the ZIP is ready without the transcript. When Whisper ends, index.md and
    // the ZIP are made again (same path). Exit waits for it (passesRunning).
    void AddTranscriptLater(PassJob job, AfterRecordingResult r)
    {
        passesRunning++;
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            // Exit waits at most 10 s for a slow Whisper. The ZIP is complete without the transcript, and the sound is in
            // clip.mp4. The folder then stays.
            bool done = false;
            DateTime quitAt = DateTime.MaxValue;
            while (!(done = r.Work.Done.WaitOne(250)))
            {
                if (!quitRequested) quitAt = DateTime.MaxValue;
                else if (quitAt == DateTime.MaxValue) quitAt = DateTime.UtcNow.AddSeconds(10);
                else if (DateTime.UtcNow > quitAt) break;
            }
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    passesRunning--;
                    if (!done)
                    {
                        // Exit was cancelled (Back in the note box), or a note box still asks: wait again.
                        if (!quitRequested || openNotes.Count > 0) { AddTranscriptLater(job, r); return; }
                        Refresh();
                        ExitIfDone();
                        return;
                    }
                    job.ZipPending++;
                    SetStep(job, ZipStep.Zip);
                    MakeZipAsync(job, delegate { AfterRecording.AddLateTranscript(job.Input, r, job.Note); }, delegate(string error)
                    {
                        job.TranscriptPending = false;
                        job.TranscriptAdded = error == null && r.HasTranscript;
                        job.ZipBytes = FileBytes(job.Zip);
                        if (error == null && r.HasTranscript) RefreshClipboardMessage(job, r);
                        job.FailText = ZipStatus.Problem(r.Ok, HasContent(r), job.Zip != null, error);
                        SetStep(job, ZipStatus.FinalStep(r.Ok, error));
                        TryCleanup(job);
                    });
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    // The AI message names the transcript. Only when the clipboard still holds the ZIP of this recording.
    void RefreshClipboardMessage(PassJob job, AfterRecordingResult r)
    {
        if (!job.ZipCopied || job.Zip == null) return;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            System.Collections.Specialized.StringCollection now = Clipboard.GetFileDropList();
            if (now.Count != 1 || !string.Equals(now[0], job.Zip, StringComparison.OrdinalIgnoreCase)) return;
            PutZipOnClipboard(job.Zip, r);
        }
        catch (Exception) { }
    }

    // Two formats: a text paste (Claude Code, terminal) gets the message with the path of the ZIP, and a file paste
    // (chat app, Explorer) gets the ZIP. Never the loose files.
    static void PutZipOnClipboard(string zip, AfterRecordingResult r)
    {
        System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
        list.Add(zip);
        DataObject data = new DataObject();
        data.SetFileDropList(list);
        data.SetText(RecordingIndex.AiMessage(zip, r.Ok, r.AudioInClip, r.HasTranscript), TextDataFormat.UnicodeText);
        // Another program can hold the clipboard for a moment.
        Clipboard.SetDataObject(data, true, 10, 100);
    }

    // Only one ZIP at a time: MakeZip uses one temporary file next to the folder.
    static readonly object zipLock = new object();

    // before (can be null) writes index.md or the note. It runs in the same lock: no ZIP reads a half-written file.
    static string MakeZipLocked(string folder, Action before)
    {
        lock (zipLock)
        {
            if (before != null) before();
            string zip = AfterRecording.MakeZip(folder);
            try { File.Delete(Path.Combine(folder, AfterRecording.PendingMarkerName)); } catch (Exception) { }
            return zip;
        }
    }

    // Makes the ZIP of the job folder on a background thread. done runs on the UI thread with the error text (null = good).
    // The caller has counted job.ZipPending. This method counts passesRunning, so Exit waits.
    void MakeZipAsync(PassJob job, Action before, Action<string> done)
    {
        passesRunning++;
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string zip = null, error = null;
            try
            {
                // No frame and no clip: no ZIP (it would be empty, and it would go to the clipboard as "ready").
                if (!HasContent(job.Result))
                {
                    if (before != null) before();   // the note goes into note.txt
                    throw new IOException("The frame pass failed: " + Shorten(job.Result == null ? "" : job.Result.Error));
                }
                zip = MakeZipLocked(job.Input.Folder, before);
            }
            catch (Exception e) { error = e.Message; }
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    passesRunning--;
                    job.ZipPending--;
                    // A good ZIP after a failed one clears the error, so the folder cleanup can run (review 2026-11-22).
                    if (error == null) { job.Zip = zip; job.ZipError = null; } else job.ZipError = error;
                    done(error);
                    Refresh();
                    ExitIfDone();
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    void ExitIfDone()
    {
        if (quitRequested && passesRunning == 0 && !stopping && openNotes.Count == 0) ExitNow();
    }

    // The folder is deleted only when all of this is true (Bran, 2026-11-22):
    // the pass is done and good (a failed pass keeps the work file), the ZIP is made with no error and no ZIP is
    // waiting (a note makes the ZIP again), and the note box is closed.
    void TryCleanup(PassJob job)
    {
        if (job.Cleaning || job.Cleaned || job.Result == null) return;
        bool zipExists = false;
        try { zipExists = job.Zip != null && File.Exists(job.Zip); }
        catch (Exception) { }
        bool soundOnlyInWav = true;
        try { soundOnlyInWav = job.Result.AudioRecorded && !job.Result.AudioInClip && job.Input.AudioFile != null && File.Exists(job.Input.AudioFile); }
        catch (Exception) { }
        if (!RecordingIndex.CanDeleteFolder(job.Done, job.Result.Ok, zipExists, job.ZipPending, job.ZipError != null, job.NoteOpen,
            job.TranscriptPending, soundOnlyInWav)) return;
        string folder = job.Input.Folder;
        job.Cleaning = true;
        passesRunning++;
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string error = AfterRecording.DeleteFolder(folder, 8, 500, delegate { return quitRequested; });
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    passesRunning--;
                    job.Cleaning = false;
                    if (error == null) job.Cleaned = true;
                    else tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("The ZIP is ready, but the folder was not deleted: " + error), ToolTipIcon.Warning);
                    UpdateLastMenu();
                    Refresh();
                    ExitIfDone();
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    // The tray items work on the ZIP of the last recording.
    void UpdateLastMenu()
    {
        bool has = false;
        try { has = lastJob != null && lastJob.Zip != null && File.Exists(lastJob.Zip); }
        catch (Exception) { }
        lastZipAvailable = has;
        UpdateMenu();
    }

    // UI thread. zipError: the ZIP failed (the folder stays, the clipboard gets no files).
    void FinishPass(PassJob job, Stopwatch fromStop, AfterRecordingResult r, string text, string zipError)
    {
        string balloonText = text;
        bool copied = false;
        string zip = job.Zip;
        if (zipError != null) balloonText += "\nCould not make the ZIP. The folder stays: " + zipError;
        // A newer recording that is still busy has its "wait" text on the clipboard: an older ZIP must not replace it.
        // Also when it is ready: the older ZIP must not replace the newer one.
        bool newer = indicatorJob != null && indicatorJob.Number > job.Number && (indicatorJob.PlaceholderSet || indicatorJob.ZipCopied);
        string copyError = null;
        if (settings.CopyToClipboard && zipError == null && zip != null && newer)
            copyError = ZipStatus.NewerHasClipboard;
        if (settings.CopyToClipboard && zipError == null && zip != null && !newer)
        {
            try
            {
                PutZipOnClipboard(zip, r);
                copied = true;
            }
            catch (Exception e)
            {
                balloonText += "\nCould not copy the ZIP to the clipboard: " + e.Message;
                copyError = e.Message;
            }
        }
        // The "wait" text must not stay on the clipboard: replace it with the result.
        if (job.PlaceholderSet && !copied && !newer)
        {
            string reason = zipError != null ? "could not make the ZIP of the last recording: " + zipError
                : copyError != null ? "could not copy the ZIP to the clipboard: " + copyError
                : "did not copy the ZIP (the clipboard copy is off).";
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "Desktop Recorder " + reason + (zip != null ? " ZIP: " + zip : "")), true, 3, 50); }
            catch (Exception) { }
        }
        // Condition D (Bran, 2026-11-19): the time from Stop to "clipboard ready".
        job.SecondsFromStop = fromStop.Elapsed.TotalSeconds;
        string ready = " Ready " + job.SecondsFromStop.ToString("0.0", CultureInfo.InvariantCulture) + " s after Stop.";
        if (lastJob == job) lastResult = "Last recording: " + Shorten(text.Replace("\n", " ") + ready);
        UpdateLastMenu();
        tray.ShowBalloonTip(5000, "Desktop Recorder",
            Shorten((copied ? "AI message and ZIP copied. Press Ctrl+V to paste.\n" : "Recording stopped.\n") + balloonText + ready),
            r.Ok && zipError == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        // The indication: tray, note box and popup say "ready" (or the error), and the ready sound plays.
        job.ZipCopied = copied;
        job.CopyError = copyError;
        job.ZipBytes = FileBytes(zip);
        job.FailText = ZipStatus.Problem(r.Ok, HasContent(r), zip != null, zipError);
        SetStep(job, ZipStatus.FinalStep(r.Ok, zipError));
    }

    static long FileBytes(string path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception) { return 0; }
    }

    // ------------------------------------------------------------ "busy, then ready" (Bran, 2026-11-22)

    PassJob indicatorJob;            // the last recording in folder mode: the indication shows its state
    int jobNumber;
    ZipToast toast;
    PassJob toastJob;                // the recording that the popup shows

    // Stop (or FFmpeg stopped by itself): the work for the ZIP starts.
    void BeginZipWork(PassJob job, ZipStep step)
    {
        indicatorJob = job;
        job.Step = step;
        job.Region = recordingRegion;
        // An early Ctrl+V must not paste the ZIP of the recording before (HANDOFF 7.21): until the new ZIP is on the
        // clipboard, the clipboard has a short text that says to wait. Only when the clipboard copy is on: then the
        // app replaces the clipboard a few seconds later in any case.
        if (settings.CopyToClipboard)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, ZipStatus.ClipboardPlaceholder), true, 3, 50);
                job.PlaceholderSet = true;
            }
            catch (Exception) { }
        }
        UpdateIndicator();
    }

    void SetStep(PassJob job, ZipStep step)
    {
        if (job == null) return;
        // A late step message of the pass must not take back "ready" or "failed".
        if (!ZipStatus.Busy(job.Step) && ZipStatus.Busy(step) && step != ZipStep.Zip) return;
        // "Ready" only when no ZIP is pending: a note saved during the first ZIP makes a second one (review 2026-11-22).
        if (!ZipStatus.Busy(step) && job.ZipPending > 0) step = ZipStep.Zip;
        bool nowReady = step == ZipStep.Ready && job.Step != ZipStep.Ready;
        job.Step = step;
        // The ready sound: not while the app closes, and not during a new recording (the loopback audio records it).
        if (nowReady && settings.PlaySounds && !quitRequested && job == indicatorJob && !recorder.IsRecording)
            Sounds.PlayReady();
        if (job == indicatorJob) { UpdateIndicator(); return; }
        // An older recording: its own note box shows its state, and a failure shows in the popup.
        UpdateNoteBox(job);
        if (step == ZipStep.Failed && !quitRequested) ShowToast(job);
    }

    void UpdateNoteBox(PassJob job)
    {
        if (job.NoteBox == null || job.NoteBox.IsDisposed) return;
        job.NoteBox.SetZipStatus(job.Step, ZipStatus.NoteText(job.Step, job.ZipBytes, job.ZipCopied,
                ZipStatus.What(job.ZipUpdated, job.TranscriptAdded), job.ZipError, job.CopyError, job.FailText)
            + (job.Step == ZipStep.Ready && job.TranscriptPending ? " " + ZipStatus.PendingText : ""));
    }

    // Tray icon, tray menu and tooltip (Refresh), the line in the note box, and the popup.
    void UpdateIndicator()
    {
        Refresh();
        PassJob job = indicatorJob;
        if (job == null) return;
        bool busy = ZipStatus.Busy(job.Step);
        UpdateNoteBox(job);
        // No popup while the app closes: ExitNow follows at once.
        if (quitRequested) { CloseToast(); return; }
        // The popup: while busy only when the note box is closed (the box shows it) and the user did not close the
        // busy popup; always for ready and failed.
        // A failure of an older recording stays on the screen for its time.
        if (busy && toast != null && !toast.IsDisposed && toastJob != null && toastJob != job && toastJob.Step == ZipStep.Failed) return;
        if (busy && (job.NoteBox != null || job.ToastDismissed)) { CloseToast(); return; }
        ShowToast(job);
    }

    void ShowToast(PassJob job)
    {
        // Per-monitor DPI aware, like the note box (the thread can be system aware while Settings is open).
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextPerMonitorAwareV2);
        try
        {
            Rectangle area = job.Region.IsEmpty ? Screen.PrimaryScreen.WorkingArea : NativeMethods.GetMonitorWorkArea(job.Region);
            if (toast == null || toast.IsDisposed || toast.WorkArea != area)
            {
                CloseToast();
                float scale = NativeMethods.GetScaleForPoint(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
                ZipToast created = new ZipToast(area, scale, Ui.CurrentPalette());
                created.FormClosed += delegate
                {
                    if (created.ClosedByUser && indicatorJob != null && toastJob == indicatorJob && ZipStatus.Busy(indicatorJob.Step))
                        indicatorJob.ToastDismissed = true;
                };
                toast = created;
            }
            toastJob = job;
            string detail = ZipStatus.PopupDetail(job.Step, job.ZipError, job.CopyError)
                ?? (job.Step == ZipStep.Ready && job.TranscriptPending ? ZipStatus.PendingText : null);
            // FFmpeg stopped by itself: the user can still think that it records.
            if (job.Problem != null) detail = ZipStatus.EarlyEnd(job.Problem) + (detail == null ? "" : " " + detail);
            toast.Set(job.Step, ZipStatus.PopupText(job.Step, job.ZipBytes, job.ZipCopied, ZipStatus.What(job.ZipUpdated, job.TranscriptAdded), job.CopyError, job.FailText),
                detail, job.Problem != null ? ZipStatus.FailedSeconds : 0);
            if (!toast.Visible) toast.Show();
        }
        catch (Exception) { CloseToast(); }
        finally { NativeMethods.RestoreThreadDpiContext(oldDpi); }
    }

    void CloseToast()
    {
        ZipToast t = toast;
        toast = null;
        if (t == null || t.IsDisposed) return;
        try { t.Close(); t.Dispose(); } catch (Exception) { }
    }

    // Open note boxes. Exit waits until they are closed (a note with text asks first).
    readonly List<NoteForm> openNotes = new List<NoteForm>();
    bool quitLoop;   // Quit is closing the note boxes: their FormClosed must not start Quit again

    void ShowNoteBox(PassJob job)
    {
        // Per-monitor DPI aware, like the selector and the frame: the box uses the physical pixels and the DPI of the
        // monitor of the recording (NoteForm lays itself out for that scale). The hotkey can come while the Settings
        // dialog (system aware) is open, so set the context here.
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextPerMonitorAwareV2);
        NoteForm form = null;
        try
        {
            Rectangle area = recordingRegion.IsEmpty ? Screen.PrimaryScreen.WorkingArea : NativeMethods.GetMonitorWorkArea(recordingRegion);
            float scale = NativeMethods.GetScaleForPoint(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
            string name = job.Input.Folder != null ? Path.GetFileName(job.Input.Folder) : null;
            double seconds = (DateTime.Now - recordingStartTime).TotalSeconds;
            form = new NoteForm(name, seconds, area, scale, openNotes.Count, Ui.CurrentPalette());
            form.Saved += delegate(string note)
            {
                job.Note = note;
                if (!job.Done)
                {
                    // On the disk at once: a shutdown before the ZIP must not lose it. The pass moves it into index.md.
                    try { RecorderCore.AtomicWrite(Path.Combine(job.Input.Folder, AfterRecording.NoteFileName), note + "\r\n"); }
                    catch (Exception) { }
                }
                if (job.Done && job.Result != null)
                {
                    try
                    {
                        // The ZIP path stays the same. Make the ZIP again, so that it has the note.
                        // The folder is deleted after this ZIP (TryCleanup).
                        string where = job.Result.Ok ? AfterRecording.IndexFileName : "note.txt";
                        job.ZipPending++;
                        job.ZipUpdated = true;
                        SetStep(job, ZipStep.Zip);
                        MakeZipAsync(job, delegate { AfterRecording.SaveNote(job.Input, job.Result, note); }, delegate(string error)
                        {
                            if (error != null)
                                tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("Cannot make the ZIP again, so it has no note. The folder stays: " + error), ToolTipIcon.Warning);
                            job.ZipBytes = FileBytes(job.Zip);
                            job.FailText = ZipStatus.Problem(job.Result.Ok, HasContent(job.Result), job.Zip != null, error);
                            SetStep(job, ZipStatus.FinalStep(job.Result.Ok, error));
                            TryCleanup(job);
                        });
                        if (!job.Result.Ok)
                            tray.ShowBalloonTip(4000, "Desktop Recorder", Shorten("The frame pass failed, so the note is in " + where), ToolTipIcon.Info);
                    }
                    catch (Exception e) { tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("Cannot save the note: " + e.Message), ToolTipIcon.Warning); }
                }
            };
            // Back to the question of Exit: the app stays open.
            form.QuitCancelled += delegate
            {
                quitRequested = false;
                Refresh();
            };
            // The folder stays while the note box is open. Saved or closed, the box then lets the cleanup run.
            job.NoteOpen = true;
            openNotes.Add(form);
            job.NoteBox = form;
            NoteForm closing = form;
            form.FormClosed += delegate
            {
                openNotes.Remove(closing);
                job.NoteOpen = false;
                if (job.NoteBox == closing) job.NoteBox = null;
                TryCleanup(job);
                // The box showed the ZIP work. When it closes before the ZIP is ready, the popup takes over.
                if (job == indicatorJob && ZipStatus.Busy(job.Step)) UpdateIndicator();
                // The box was the last thing that Exit waited for.
                if (quitRequested && !quitLoop) Quit();
            };
            form.Show();
            form.Activate();
        }
        catch (Exception)
        {
            job.NoteOpen = false;
            if (form != null)
            {
                openNotes.Remove(form);
                try { form.Dispose(); } catch (Exception) { }
            }
        }
        finally { NativeMethods.RestoreThreadDpiContext(oldDpi); }
    }

    // Opens Explorer with the ZIP of the last recording selected.
    void OpenLastFolder()
    {
        if (lastJob == null || lastJob.Zip == null) return;
        try
        {
            if (!File.Exists(lastJob.Zip)) { MessageBox.Show("The ZIP is not there any more:\n" + lastJob.Zip, "Desktop Recorder"); return; }
            Process started = Process.Start("explorer.exe", "/select,\"" + lastJob.Zip + "\"");
            if (started != null) started.Dispose();
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Desktop Recorder"); }
    }

    // Puts the existing ZIP of the last recording on the clipboard. It does not make the ZIP again:
    // the folder is gone after the first ZIP.
    void CopyZip()
    {
        if (lastJob == null || lastJob.Zip == null) return;
        string zip = lastJob.Zip;
        if (!File.Exists(zip))
        {
            tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("The ZIP is not there any more: " + zip), ToolTipIcon.Warning);
            UpdateLastMenu();
            return;
        }
        try
        {
            System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
            list.Add(zip);
            Clipboard.SetFileDropList(list);
            tray.ShowBalloonTip(4000, "Desktop Recorder", Shorten("ZIP copied. Press Ctrl+V to paste it.\n" + zip), ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("The ZIP was not copied: " + e.Message + "\n" + zip), ToolTipIcon.Warning);
        }
    }

    // After FFmpeg ended: the status line and the clipboard.
    void FinishStop()
    {
        string stats = UpdateStats();
        if (mp4AudioNote != null) { stats += "\n" + mp4AudioNote; mp4AudioNote = null; }
        if (!settings.CopyToClipboard)
        {
            tray.ShowBalloonTip(4000, "Desktop Recorder", "Recording stopped.\n" + stats, ToolTipIcon.Info);
            return;
        }
        // One balloon for the clipboard text and the stats: a second balloon replaces the first.
        try
        {
            string[] files = recorder.SessionFiles();
            if (files.Length == 0)
            {
                tray.ShowBalloonTip(4000, "Desktop Recorder", "Recording stopped.\n" + stats, ToolTipIcon.Info);
                return;
            }
            System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
            list.AddRange(files);
            Clipboard.SetFileDropList(list);
            tray.ShowBalloonTip(4000, "Desktop Recorder",
                (files.Length == 1 ? "Video copied. Press Ctrl+V to paste it."
                                   : files.Length + " video files copied. Press Ctrl+V to paste them.")
                + "\n" + stats,
                ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            tray.ShowBalloonTip(6000, "Desktop Recorder",
                Shorten(stats + "\nCould not copy the video to the clipboard: " + e.Message), ToolTipIcon.Warning);
        }
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.Folder);
            Process started = Process.Start("explorer.exe", RecorderCore.QuoteWindowsArgument(settings.Folder));
            if (started != null) started.Dispose();
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Desktop Recorder"); }
    }

    void ShowSettings()
    {
        if (openForm != null) { openForm.Activate(); return; }
        // The dialog does not use screen coordinates. Make it system DPI aware, so Windows scales it on a
        // monitor with a different DPI. In a per-monitor aware window, .NET Framework WinForms scales only once,
        // to the system DPI, and the dialog is too small on a monitor with a higher DPI.
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextSystemAware);
        SettingsForm created;
        try
        {
            // The window lays itself out for the system DPI (the DPI of the screen DC in this context).
            float scale = 1f;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            created = new SettingsForm(settings, scale);
        }
        catch (Exception)
        {
            NativeMethods.RestoreThreadDpiContext(oldDpi);
            throw;
        }
        using (SettingsForm form = created)
        {
            openForm = form;
            DialogResult result;
            try { result = form.ShowDialog(); }
            finally
            {
                // The window keeps its DPI mode. Windows made after this line are per-monitor aware again.
                NativeMethods.RestoreThreadDpiContext(oldDpi);
            }
            openForm = null;
            if (result != DialogResult.OK) return;
            // The form copies the current settings. Keep a region that the selector saved while the form was open.
            Settings updated = form.Result;
            updated.Region = settings.Region;
            updated.LastRegion = settings.LastRegion;
            settings = updated;
            try
            {
                settings.Save();
                // Only when the user changed the switch: a Save for another setting must not add or remove the entry.
                if (form.StartWithWindowsChanged)
                {
                    Settings.StartsWithWindows = form.StartWithWindows;
                    // The switch of Windows stays with the user: the app does not change it.
                    if (form.StartWithWindows && !Settings.StartsWithWindows) OfferStartupList();
                }
            }
            catch (Exception e) { MessageBox.Show("Cannot save the settings: " + e.Message, "Desktop Recorder"); }
            RegisterHotkeys();
            string warning = HotkeyWarning();
            if (warning != null)
                MessageBox.Show(warning + " Pick a different hotkey in Settings.", "Desktop Recorder");
            // Do not restart a running recording. A restart cut the recording into two files, and the clipboard
            // got only the second file (Bran's test, 2026-11-14). The new capture settings apply to the next
            // recording. The new hotkey applies now (RegisterHotkeys above).
            Refresh();
        }
    }

    static void OfferStartupList()
    {
        if (MessageBox.Show("Windows has Desktop Recorder turned off in its startup list, so it does not start with Windows yet.\n\n"
            + "Open the list now? Then turn on Desktop Recorder there.", "Desktop Recorder", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            Process started = Process.Start("ms-settings:startupapps");
            if (started != null) started.Dispose();
        }
        catch (Exception e) { MessageBox.Show("Cannot open the startup list: " + e.Message, "Desktop Recorder"); }
    }

    DateTime quitAt;

    // Exit in the tray menu. A second Exit 30 s later offers to close without the wait (a program that hangs).
    void QuitCommand()
    {
        if (quitRequested && (stopping || passesRunning > 0) && openNotes.Count == 0 && (DateTime.UtcNow - quitAt).TotalSeconds >= 30)
        {
            if (MessageBox.Show("The last recording is not ready.\n\nClose now? The recording stays in its folder, and the app makes the ZIP at the next start.",
                "Desktop Recorder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) ExitNow();
            return;
        }
        if (!quitRequested) quitAt = DateTime.UtcNow;
        Quit();
    }

    bool exited;

    void Quit()
    {
        resumeTimer.Stop();
        // Folder mode: the pass makes the folder from the work file. Wait for it, so the recording is not lost
        // (review 2026-11-19). The tray shows that the app is finishing. ExitNow runs after the pass.
        quitRequested = true;
        // Stop a folder recording first, as before (review 2026-11-22): its pass and ZIP then run while a note box
        // asks. An MP4 recording stops in ExitNow, as before; until then the menu keeps "Stop recording".
        if (recorder.IsRecording && recordingFolder != null && !stopping) StopRecording(false, true);
        // A note box with text asks Save, Discard or Back first (Bran, 2026-11-22). Back cancels the Exit
        // (QuitCancelled). Save and Discard close the box, and its FormClosed calls Quit again.
        bool waitForNotes = false;
        quitLoop = true;
        try
        {
            foreach (NoteForm note in openNotes.ToArray())
                if (!note.CloseForQuit()) waitForNotes = true;
        }
        finally { quitLoop = false; }
        if (waitForNotes) { Refresh(); return; }
        if (stopping || passesRunning > 0)
        {
            Refresh();
            return;
        }
        ExitNow();
    }

    void ExitNow()
    {
        if (exited) return;
        exited = true;
        StopRecording(false, true); // no sound: the program ends before the sound can play. Wait for the file.
        DisposeFrame();
        hotkey.Close();
        tray.Visible = false;
        tray.Icon = null;
        if (currentIcon != null) currentIcon.Dispose();
        if (currentIconHandle != IntPtr.Zero) DestroyIcon(currentIconHandle);
        currentIcon = null;
        currentIconHandle = IntPtr.Zero;
        busyTimer.Stop();
        busyTimer.Dispose();
        CloseToast();
        tray.Icon = null;
        DisposeBusyIcons();
        tray.Dispose();
        clockTimer.Stop();
        menu.Dispose();
        ExitThread();
    }

    // The state that the tray menu shows. During a recording: the fps and the region of that recording (a settings
    // change applies to the next one). One hotkey starts and stops (Bran, 2026-11-14).
    TrayState MenuState()
    {
        TrayState s = new TrayState();
        s.Phase = TrayModel.PhaseOf(recorder.IsRecording, stopping, passesRunning, quitRequested);
        s.ElapsedSeconds = (DateTime.Now - recordingStartTime).TotalSeconds;
        s.Fps = s.Phase == TrayPhase.Recording ? recordingFps : settings.Fps;
        s.RegionWidth = recordingRegion.Width;
        s.RegionHeight = recordingRegion.Height;
        s.HotkeyText = settings.HotkeyText;
        s.HotkeyActive = hotkey.IsRegistered(SelectHotkeyId);
        s.HasLastZip = lastZipAvailable;
        s.LastResult = lastResult;
        s.ZipBusy = indicatorJob != null && ZipStatus.Busy(indicatorJob.Step);
        s.ZipStep = indicatorJob != null ? indicatorJob.Step : ZipStep.Ready;
        return s;
    }

    void UpdateMenu()
    {
        menu.Update(MenuState());
    }

    // The busy tray icon: 8 pictures, made once for each icon size, shown in turn by busyTimer.
    readonly System.Windows.Forms.Timer busyTimer = new System.Windows.Forms.Timer();
    Icon[] busyIcons;
    IntPtr[] busyHandles;
    int busyIconSize, busyFrame;

    void ShowBusyIcon(int px)
    {
        if (busyIcons == null || busyIconSize != px)
        {
            DisposeBusyIcons();
            busyIcons = new Icon[TrayIcons.BusyFrames];
            busyHandles = new IntPtr[TrayIcons.BusyFrames];
            for (int i = 0; i < busyIcons.Length; i++)
                using (Bitmap bmp = TrayIcons.DrawBusy(px, i))
                {
                    busyHandles[i] = bmp.GetHicon();
                    busyIcons[i] = Icon.FromHandle(busyHandles[i]);
                }
            busyIconSize = px;
        }
        tray.Icon = busyIcons[busyFrame % busyIcons.Length];
        if (!busyTimer.Enabled)
        {
            busyTimer.Interval = 125;
            busyTimer.Start();
        }
    }

    void BusyTick()
    {
        if (busyIcons == null) return;
        busyFrame++;
        tray.Icon = busyIcons[busyFrame % busyIcons.Length];
    }

    void DisposeBusyIcons()
    {
        if (busyIcons == null) return;
        for (int i = 0; i < busyIcons.Length; i++)
        {
            if (busyIcons[i] != null) busyIcons[i].Dispose();
            if (busyHandles[i] != IntPtr.Zero) DestroyIcon(busyHandles[i]);
        }
        busyIcons = null;
        busyHandles = null;
    }

    void Refresh()
    {
        // While FFmpeg finishes the file, show the stopped state: the user already pressed Stop.
        bool on = recorder.IsRecording && !stopping;
        TrayState state = MenuState();
        menu.Update(state);
        tray.Text = TrayModel.Tooltip(state);
        if (state.Phase == TrayPhase.Recording) clockTimer.Start();
        else clockTimer.Stop();

        // Tray icon from Bran's icon set (packaging/icon/tray-icon-set-source.jpg): corner brackets, a ring
        // and a dot. Idle: light blue lines and a small dot. Recording: pink-to-blue brackets and a red dot.
        // Drawn at the real tray size, so the thin lines stay sharp (20 px at 125 %).
        int px = Math.Max(16, SystemInformation.SmallIconSize.Width);
        // From Stop until the ZIP is on the clipboard: a turning arc (Bran, 2026-11-22).
        if (!on && state.ZipBusy)
        {
            ShowBusyIcon(px);
            return;
        }
        busyTimer.Stop();
        using (Bitmap bmp = TrayIcons.Draw(px, on))
        {
            IntPtr handle = bmp.GetHicon();
            Icon icon = Icon.FromHandle(handle);
            tray.Icon = icon;
            if (currentIcon != null) currentIcon.Dispose();
            if (currentIconHandle != IntPtr.Zero) DestroyIcon(currentIconHandle);
            currentIcon = icon;
            currentIconHandle = handle;
        }
    }
}

// Draws the two tray icons (idle and recording) in code, after Bran's icon set
// (packaging/icon/tray-icon-set-source.jpg). Drawn at the real tray size, so the lines stay sharp.
// Each line gets a soft glow first: a wide, half-transparent copy of the line.
static class TrayIcons
{
    static readonly Color IdleBlue = Color.FromArgb(175, 220, 235);
    static readonly Color Pink = Color.FromArgb(235, 120, 255);
    static readonly Color Blue = Color.FromArgb(70, 200, 255);
    static readonly Color Red = Color.FromArgb(255, 35, 85);

    public static Bitmap Draw(int size, bool recording)
    {
        Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 16f;
            // Whole-pixel strokes and half-pixel positions keep the lines crisp at 16 to 24 px.
            bool small = size < 24;
            float stroke = Math.Max(1f, (float)Math.Round(1.2f * k));
            float glow = stroke * (small ? 2.2f : 2.6f);
            int glowAlpha = small ? 40 : 60;
            float inset = stroke / 2f + 1f;
            float len = 4.2f * k;
            float far = size - inset;
            Color top = recording ? Pink : IdleBlue;
            Color bottom = recording ? Blue : IdleBlue;

            // Corner brackets: top pair pink, bottom pair blue (recording), all light blue (idle).
            PointF[][] topBrackets =
            {
                new[] { new PointF(inset, inset + len), new PointF(inset, inset), new PointF(inset + len, inset) },
                new[] { new PointF(far - len, inset), new PointF(far, inset), new PointF(far, inset + len) },
            };
            PointF[][] bottomBrackets =
            {
                new[] { new PointF(inset, far - len), new PointF(inset, far), new PointF(inset + len, far) },
                new[] { new PointF(far - len, far), new PointF(far, far), new PointF(far, far - len) },
            };
            foreach (PointF[] p in topBrackets) GlowLines(g, p, top, stroke, glow, recording ? glowAlpha + 15 : glowAlpha);
            foreach (PointF[] p in bottomBrackets) GlowLines(g, p, bottom, stroke, glow, recording ? glowAlpha + 15 : glowAlpha);

            // The ring sits a little higher while recording, to make room for the sound wave.
            float cx = size / 2f;
            float cy = recording ? size / 2f - 0.5f * k : size / 2f;
            float ring = (recording ? 4.0f : 4.3f) * k;
            RectangleF ringRect = new RectangleF(cx - ring, cy - ring, 2 * ring, 2 * ring);
            float ringStroke = Math.Max(1f, 1.05f * k);
            if (recording)
            {
                // Glow, then a ring that goes from pink (top left) to blue (bottom right).
                using (Pen glowPen = new Pen(Color.FromArgb(glowAlpha + 15, Pink), ringStroke * 2.4f))
                    g.DrawEllipse(glowPen, ringRect);
                RectangleF gradRect = RectangleF.Inflate(ringRect, 1f, 1f);
                using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(gradRect, Pink, Blue, 45f))
                using (Pen ringPen = new Pen(grad, ringStroke))
                    g.DrawEllipse(ringPen, ringRect);
                // Dark inner ring between the outer ring and the red dot.
                float inner = ring - 1.35f * k;
                if (!small)
                    using (Pen innerPen = new Pen(Color.FromArgb(150, 90, 80, 140), Math.Max(0.8f, 0.6f * k)))
                        g.DrawEllipse(innerPen, cx - inner, cy - inner, 2 * inner, 2 * inner);
                // Red dot with a glow.
                float dot = 2.2f * k;
                if (!small)
                    using (Brush halo = new SolidBrush(Color.FromArgb(90, Red)))
                        g.FillEllipse(halo, cx - dot - 0.9f * k, cy - dot - 0.9f * k, 2 * dot + 1.8f * k, 2 * dot + 1.8f * k);
                using (Brush dotBrush = new SolidBrush(Red))
                    g.FillEllipse(dotBrush, cx - dot, cy - dot, 2 * dot, 2 * dot);
                // Small highlight on the red dot.
                float hl = 0.7f * k;
                using (Brush hlBrush = new SolidBrush(Color.FromArgb(150, 255, 170, 190)))
                    g.FillEllipse(hlBrush, cx - 0.9f * k - hl, cy - 0.9f * k - hl, 2 * hl, 2 * hl);
                // Small red dot at the right side, in the gap between the right brackets.
                float side = Math.Max(0.8f, 0.65f * k);
                float sx = far - 0.2f * k;
                using (Brush sideBrush = new SolidBrush(Red))
                    g.FillEllipse(sideBrush, sx - side, cy - side, 2 * side, 2 * side);
                // Sound wave: short red bars below the ring, between the bottom brackets.
                // Bars are whole pixels wide and start on whole pixels, so they do not blur.
                float[] heights = small ? new[] { 0.5f, 1f, 0.5f } : new[] { 0.35f, 0.65f, 1f, 0.65f, 0.35f };
                int barW = Math.Max(1, (int)Math.Round(0.5f * k));
                int gap = barW * 2;
                float waveY = (cy + ring + far) / 2f + 0.4f * k;
                float maxH = Math.Max(2f, 2.2f * k);
                int x0 = (int)Math.Round(cx - barW / 2f - gap * (heights.Length - 1) / 2f);
                var oldSmoothing = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                using (Brush barBrush = new SolidBrush(Red))
                    for (int i = 0; i < heights.Length; i++)
                    {
                        int h = Math.Max(1, (int)Math.Round(heights[i] * maxH));
                        g.FillRectangle(barBrush, x0 + i * gap, (int)Math.Round(waveY - h / 2f), barW, h);
                    }
                g.SmoothingMode = oldSmoothing;
            }
            else
            {
                using (Pen glowPen = new Pen(Color.FromArgb(glowAlpha, IdleBlue), ringStroke * 2.4f))
                    g.DrawEllipse(glowPen, ringRect);
                using (Pen ringPen = new Pen(IdleBlue, ringStroke))
                    g.DrawEllipse(ringPen, ringRect);
                float dot = 1.5f * k;
                using (Brush dotBrush = new SolidBrush(IdleBlue))
                    g.FillEllipse(dotBrush, cx - dot, cy - dot, 2 * dot, 2 * dot);
            }
        }
        return bmp;
    }

    public const int BusyFrames = 8;

    // The "busy" icon (from Stop until the ZIP is on the clipboard): the idle brackets, a faint ring and a bright
    // arc that turns by 45 degrees in each frame.
    public static Bitmap DrawBusy(int size, int frame)
    {
        Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 16f;
            bool small = size < 24;
            float stroke = Math.Max(1f, (float)Math.Round(1.2f * k));
            float glow = stroke * (small ? 2.2f : 2.6f);
            int glowAlpha = small ? 40 : 60;
            float inset = stroke / 2f + 1f;
            float len = 4.2f * k;
            float far = size - inset;
            PointF[][] brackets =
            {
                new[] { new PointF(inset, inset + len), new PointF(inset, inset), new PointF(inset + len, inset) },
                new[] { new PointF(far - len, inset), new PointF(far, inset), new PointF(far, inset + len) },
                new[] { new PointF(inset, far - len), new PointF(inset, far), new PointF(inset + len, far) },
                new[] { new PointF(far - len, far), new PointF(far, far), new PointF(far, far - len) },
            };
            foreach (PointF[] p in brackets) GlowLines(g, p, IdleBlue, stroke, glow, glowAlpha);
            float c = size / 2f, ring = 4.3f * k;
            RectangleF ringRect = new RectangleF(c - ring, c - ring, 2 * ring, 2 * ring);
            float ringStroke = Math.Max(1.2f, 1.4f * k);
            using (Pen faint = new Pen(Color.FromArgb(70, IdleBlue), ringStroke)) g.DrawEllipse(faint, ringRect);
            using (Pen arc = new Pen(Blue, ringStroke * 1.15f))
            {
                arc.StartCap = arc.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(arc, ringRect, -90f + frame * (360f / BusyFrames), 110f);
            }
        }
        return bmp;
    }

    // Draws a wide, half-transparent copy of the line (the glow), then the line itself.
    static void GlowLines(Graphics g, PointF[] points, Color color, float stroke, float glow, int glowAlpha)
    {
        using (Pen glowPen = new Pen(Color.FromArgb(glowAlpha, color), glow))
        using (Pen pen = new Pen(color, stroke))
        {
            glowPen.StartCap = glowPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            glowPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            g.DrawLines(glowPen, points);
            g.DrawLines(pen, points);
        }
    }
}

// One recording in folder mode: the input of the pass, its result, and the note.
sealed class PassJob
{
    public AfterRecordingInput Input;
    public AfterRecordingResult Result;
    public bool Done;
    public string Note;              // null = no note (yet)
    public string Problem;           // why the recording ended, when FFmpeg stopped by itself
    public string FailText;          // ZipStatus.Problem: what is wrong when the state is Failed
    public int Number;               // 1, 2, 3 ... in the order of the recordings (0 = a recovered recording)
    public double SecondsFromStop;   // Stop to "clipboard ready"
    // UI thread only:
    public string Zip;               // path of the ZIP (next to the folder), null until the first ZIP is made
    public string ZipError;          // the last ZIP failed: the folder stays
    public int ZipPending;           // ZIP calls that are not done (the first one, and one for each note)
    public bool NoteOpen;            // the note box is open: the folder stays
    public bool Cleaning, Cleaned;   // the folder is being deleted, or is deleted
    public bool NoNoteBox;           // Quit: no note box
    // The "busy, then ready" indication (UI thread only, Bran 2026-11-22):
    public ZipStep Step = ZipStep.Stopping;
    public long ZipBytes;            // the size of the ZIP when it is ready
    public bool ZipCopied;           // the ZIP is on the clipboard
    public bool ZipUpdated;          // the ZIP was made again with a late note
    public bool TranscriptPending;   // the ZIP is ready, and Whisper still works: the folder stays
    public bool TranscriptAdded;     // the ZIP was made again with the transcript
    public NoteForm NoteBox;         // the open note box of this recording, or null
    public Rectangle Region;         // the recording region (the popup shows on its monitor); empty = whole desktop
    public bool PlaceholderSet;      // the "wait" text went on the clipboard at Stop
    public bool ToastDismissed;      // the user closed the busy popup: show it again only for ready or failed
    public string CopyError;         // why the ready ZIP is not on the clipboard, or null
}
