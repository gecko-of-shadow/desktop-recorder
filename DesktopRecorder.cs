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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }
}

class Settings
{
    public string Folder;
    public double Fps = RecorderCore.DefaultFps;
    public int Quality = 1;          // 0 = smallest, 1 = balanced, 2 = sharpest
    public int SplitMinutes = 30;    // 0 = one file per recording
    public bool RecordOnLaunch = true;
    public bool CopyToClipboard = true;
    public bool PlaySounds = true;
    public bool ShowPointer = true;
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
    // Keys written by the Linux build. Saved again so a shared settings file does not lose them.
    public List<string> ExtraLines = new List<string>();

    public static readonly int[] Crf = RecorderCore.Crf;
    public static readonly string[] QualityNames =
    {
        "Smallest files (text still readable)",
        "Balanced (recommended)",
        "Sharpest text (bigger files)"
    };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DesktopRecorder";

    public Settings()
    {
        // Not in Videos or Documents: Windows "Controlled folder access" blocks writing there.
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Recordings");
    }

    static string FilePath
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
        stored.RecordOnLaunch = RecordOnLaunch;
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
        RecordOnLaunch = stored.RecordOnLaunch;
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

    public static Settings Load()
    {
        Settings s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            s.Apply(RecorderCore.ParseSettings(File.ReadAllText(FilePath), s.ToStored()));
        }
        catch (Exception) { }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        RecorderCore.AtomicWrite(FilePath, RecorderCore.SerializeSettings(ToStored()));
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
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(RunName) != null;
            }
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
            return session.Start(ffmpeg, arguments, s.Folder, s.Fps);
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
        string error = session.Start(ffmpeg, folderArguments, folder, s.Fps, AfterRecording.WorkFileName);
        if (error == null) recordingFolder = folder;
        return error;
    }

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

    public void Stop()
    {
        session.Stop();
    }
}

// Two short tones, made in memory. Start goes up, stop goes down. The recording has no audio, so they are not recorded.
static class Sounds
{
    static readonly System.Media.SoundPlayer startSound = Make(660, 880);
    static readonly System.Media.SoundPlayer stopSound = Make(880, 660);

    public static void PlayStart() { Play(startSound); }
    public static void PlayStop() { Play(stopSound); }

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
    readonly MenuItem statusItem = new MenuItem("Not recording");
    readonly MenuItem statsItem = new MenuItem("");
    readonly MenuItem toggleItem = new MenuItem("Select region and record");
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
    bool stopping;   // FFmpeg is finishing the file on a background thread
    // Folder mode (AI capture brief, Phase 1).
    string recordingFolder;                 // null in MP4 mode
    DateTime recordingStartTime;
    Settings recordingSettings;             // the settings of the running recording
    readonly List<KeyValuePair<double, string>> titleSamples = new List<KeyValuePair<double, string>>();
    readonly System.Windows.Forms.Timer titleTimer = new System.Windows.Forms.Timer();
    PassJob lastJob;                        // the last recording in folder mode
    readonly MenuItem openLastItem = new MenuItem("Open last recording folder");
    readonly MenuItem zipItem = new MenuItem("Copy ZIP of last recording");
    Icon currentIcon;
    IntPtr currentIconHandle = IntPtr.Zero;

    public TrayApp()
    {
        IntPtr unused = ui.Handle; // makes the window that lets worker threads call the UI thread
        RegisterHotkeys();

        statusItem.Enabled = false;
        statsItem.Enabled = false;
        statsItem.Visible = false;
        toggleItem.Click += delegate { Toggle(); };
        MenuItem folderItem = new MenuItem("Open recordings folder");
        folderItem.Click += delegate { OpenFolder(); };
        openLastItem.Click += delegate { OpenLastFolder(); };
        zipItem.Click += delegate { CopyZip(); };
        openLastItem.Enabled = false;
        zipItem.Enabled = false;
        titleTimer.Interval = 250;
        titleTimer.Tick += delegate { SampleTitle(); };
        MenuItem settingsItem = new MenuItem("Settings...");
        settingsItem.Click += delegate { ShowSettings(); };
        MenuItem exitItem = new MenuItem("Exit");
        exitItem.Click += delegate { Quit(); };
        tray.ContextMenu = new ContextMenu(new MenuItem[]
        {
            statusItem, statsItem, new MenuItem("-"), toggleItem, folderItem, openLastItem, zipItem, settingsItem,
            new MenuItem("-"), exitItem
        });
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;

        recorder.Failed += delegate(string message)
        {
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    if (TryFallback(message)) return;
                    DisposeFrame();
                    titleTimer.Stop();
                    Refresh();
                    if (settings.PlaySounds) Sounds.PlayStop();
                    if (recordingFolder != null && WorkFileHasData(recordingFolder))
                    {
                        // Keep what was recorded: the pass makes the frames and clip.mp4 from the work file.
                        StartPass(null, "FFmpeg stopped: " + Shorten(message));
                        return;
                    }
                    string stats = UpdateStats();
                    // Stats first: Shorten cuts the end of a long ffmpeg message.
                    tray.ShowBalloonTip(8000, "Desktop Recorder stopped",
                        Shorten(stats + "\n" + message), ToolTipIcon.Error);
                });
            }
            catch (InvalidOperationException) { }
        };
        SystemEvents.SessionEnding += delegate { recorder.Stop(); };

        hotkey.Pressed += delegate(int id)
        {
            if (recorder.IsRecording)
            {
                StopRecording();
                Refresh();
            }
            else SelectAndRecord();
        };

        Refresh();
        if (!settings.RecordOnLaunch) Recorder.WarmUp(settings);
        if (settings.RecordOnLaunch) StartSavedRegion(true, false);
        string warning = HotkeyWarning();
        if (warning != null) tray.ShowBalloonTip(6000, "Desktop Recorder", warning, ToolTipIcon.Warning);
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
        if (useX264) Recorder.MarkNvencUnusable();
        Rectangle region = recordingRegion;
        DisposeFrame();
        string firstLine = (message ?? "").Replace("\r", "").Split('\n')[0];
        StartRecordingWith(region, false, true, reason + "\nFFmpeg: " + firstLine,
            forcedGdigrab || useGdigrab, forcedX264 || useX264, recordingFolder);
        return true;
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
        try { settings.Save(); }
        catch (Exception e)
        {
            tray.ShowBalloonTip(6000, "Desktop Recorder", "Cannot save the settings: " + e.Message, ToolTipIcon.Warning);
        }
    }

    // Starts with the saved region, without the selector. Used at launch and after a settings change.
    // With no saved region, or a region that is not on the screen now, it records the whole desktop (as before).
    void StartSavedRegion(bool sound, bool balloon)
    {
        Rectangle region = Rectangle.Empty;
        string note = null;
        if (!settings.Region.IsEmpty)
        {
            Rectangle? onScreen = RegionSelector.NormalizeRegion(settings.Region);
            if (onScreen.HasValue) region = onScreen.Value;
            else note = "The saved region is not on the screen now. Recording the whole desktop.";
        }
        StartRecording(region, sound, balloon || note != null, note);
    }

    // The record hotkey and the first tray item: show the selector, then record the selection.
    void SelectAndRecord()
    {
        if (selecting || stopping || recorder.IsRecording) return;
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
            // draw a new box. The last region has its own hotkey.
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

    void StartRecordingWith(Rectangle region, bool sound, bool balloon, string note, bool forceGdigrab, bool forceX264,
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
                    string kept = Path.Combine(reuseFolder, "work_lossless_failed_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".mp4");
                    File.Move(old, kept);
                }
            }
            catch (Exception) { }
        }
        string error = recorder.Start(settings, region, forceGdigrab, forceX264, reuseFolder, out plan, out folder);
        string frameWarning = null;
        if (error == null)
        {
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
            return;
        }
        if (sound && settings.PlaySounds) Sounds.PlayStart();
        string planNote = plan != null ? plan.Note : "";
        if (balloon || (planNote != null && planNote.Length > 0))
        {
            string text = "Recording started";
            if (plan != null) text += " (" + plan.Backend + ", " + plan.Encoder + ")";
            text += ".";
            if (note != null) text += "\n" + note;
            if (planNote != null && planNote.Length > 0) text += "\n" + planNote;
            if (frameWarning != null) text += "\n" + frameWarning;
            tray.ShowBalloonTip(3000, "Desktop Recorder", Shorten(text), ToolTipIcon.Info);
        }
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
        statsItem.Text = "Last recording: " + text;
        statsItem.Visible = true;
        return text;
    }

    void Toggle()
    {
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
        if (sound && settings.PlaySounds) Sounds.PlayStop();
        DisposeFrame();
        titleTimer.Stop();
        Stopwatch fromStop = Stopwatch.StartNew();
        double stopClock = recorder.ClockSeconds;
        if (wait && recordingFolder == null)
        {
            recorder.Stop();
            FinishStop();
            return;
        }
        // Folder mode always stops in the background. Quit waits for the pass (see Quit).
        PassJob job = recordingFolder != null ? CreateJob(null) : null;
        if (job != null && wait) job.NoNoteBox = true;
        // The note box does not wait for the pass, and the pass does not wait for the note box (Bran, 2026-11-19).
        if (job != null && !job.NoNoteBox) ShowNoteBox(job);
        stopping = true;
        if (job != null) passesRunning++;
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
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        stopping = false;
                        Refresh();
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
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    stopping = false;
                    FinishStop();
                    Refresh();
                    if (quitRequested) ExitNow();
                });
            }
            catch (InvalidOperationException) { stopping = false; }
        });
    }

    // ------------------------------------------------------------ folder mode (AI capture brief, Phase 1)

    int passesRunning;
    bool quitRequested;

    static bool WorkFileHasData(string folder)
    {
        try
        {
            FileInfo f = new FileInfo(Path.Combine(folder, AfterRecording.WorkFileName));
            return f.Exists && f.Length > 0;
        }
        catch (Exception) { return false; }
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

    PassJob CreateJob(string problem)
    {
        Settings s = recordingSettings ?? settings;
        AfterRecordingInput input = new AfterRecordingInput();
        input.Ffmpeg = Recorder.FindFfmpeg();
        input.Folder = recordingFolder;
        input.WorkFile = Path.Combine(recordingFolder, AfterRecording.WorkFileName);
        input.Start = recordingStartTime;
        // Condition F (Bran, 2026-11-19): the numeric UTC offset of this PC at the start time.
        input.UtcOffset = TimeZoneInfo.Local.GetUtcOffset(recordingStartTime);
        input.Settings = s.ToStored();
        input.Options = RecorderCore.SettledOptionsOf(input.Settings);
        input.Fps = recordingFps;
        input.SizeLimit = s.SizeLimit;
        // Test switch: with DESKTOP_RECORDER_KEEP_WORK=1 in the environment, the lossless work file stays
        // in the folder. The pass can then run again on the same capture (test\process_clip.cs).
        input.DeleteWorkFile = Environment.GetEnvironmentVariable("DESKTOP_RECORDER_KEEP_WORK") != "1";
        input.Titles = BuildTitles();
        // The clip encode uses libx264 when the capture plan did (NVENC cannot encode this size, or it failed).
        input.ForceX264 = (recordingPlan != null && recordingPlan.Encoder == RecorderCore.EncoderX264) || forcedX264;
        PassJob job = new PassJob();
        job.Input = input;
        job.Problem = problem;
        return job;
    }

    // Background thread. Never throws.
    static void RunPass(PassJob job)
    {
        try
        {
            if (job.Input.Ffmpeg == null) throw new IOException("ffmpeg.exe was not found.");
            job.Input.NvencAvailable = Recorder.NvencForClip(job.Input.Settings == null ? new Settings() : SettingsFrom(job.Input.Settings));
            job.Result = AfterRecording.Run(job.Input);
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
    void StartPass(string unused, string problem)
    {
        if (recordingFolder == null) return;
        PassJob job = CreateJob(problem);
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
        lastJob = job;
        AfterRecordingResult r = job.Result ?? new AfterRecordingResult();
        if (job.Note != null)
        {
            try { AfterRecording.SaveNote(job.Input, r, job.Note); } catch (Exception) { }
        }
        string clip = (r.ClipBytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        string text = r.Ok
            ? "Frames: " + r.Frames.Count.ToString(CultureInfo.InvariantCulture) + ". clip.mp4: " + clip + "."
            : "The frame pass failed. " + (r.ClipWritten ? "clip.mp4 (" + clip + ") has no settled keyframes. " : "")
              + "The work file stays in the folder. " + r.Error;
        if (job.Problem != null) text = job.Problem + "\n" + text;
        string balloonText = text;
        bool copied = false;
        if (settings.CopyToClipboard && Directory.Exists(job.Input.Folder))
        {
            try
            {
                System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
                list.Add(job.Input.Folder);
                Clipboard.SetFileDropList(list);
                copied = true;
            }
            catch (Exception e) { balloonText += "\nCould not copy the folder to the clipboard: " + e.Message; }
        }
        // Condition D (Bran, 2026-11-19): the time from Stop to "clipboard ready".
        job.SecondsFromStop = fromStop.Elapsed.TotalSeconds;
        string ready = " Ready " + job.SecondsFromStop.ToString("0.0", CultureInfo.InvariantCulture) + " s after Stop.";
        statsItem.Text = "Last recording: " + Shorten(text.Replace("\n", " ") + ready);
        statsItem.Visible = true;
        openLastItem.Enabled = Directory.Exists(job.Input.Folder);
        zipItem.Enabled = openLastItem.Enabled;
        tray.ShowBalloonTip(5000, "Desktop Recorder",
            Shorten((copied ? "Folder copied. Press Ctrl+V to paste it.\n" : "Recording stopped.\n") + balloonText + ready),
            r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        if (quitRequested && passesRunning == 0 && !stopping) ExitNow();
    }

    void ShowNoteBox(PassJob job)
    {
        IntPtr oldDpi = NativeMethods.SetThreadDpiContext(NativeMethods.DpiContextSystemAware);
        try
        {
            NoteForm form = new NoteForm();
            form.Saved += delegate(string note)
            {
                job.Note = note;
                if (job.Done && job.Result != null)
                {
                    try
                    {
                        string where = AfterRecording.SaveNote(job.Input, job.Result, note);
                        if (!job.Result.Ok)
                            tray.ShowBalloonTip(4000, "Desktop Recorder", Shorten("The frame pass failed, so the note is in " + where), ToolTipIcon.Info);
                    }
                    catch (Exception e) { tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("Cannot save the note: " + e.Message), ToolTipIcon.Warning); }
                }
            };
            form.Show();
            form.Activate();
        }
        catch (Exception) { }
        finally { NativeMethods.RestoreThreadDpiContext(oldDpi); }
    }

    void OpenLastFolder()
    {
        if (lastJob == null) return;
        try
        {
            Process started = Process.Start("explorer.exe", RecorderCore.QuoteWindowsArgument(lastJob.Input.Folder));
            if (started != null) started.Dispose();
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Desktop Recorder"); }
    }

    // Makes the ZIP of the last folder on a background thread, then puts it on the clipboard.
    void CopyZip()
    {
        if (lastJob == null) return;
        string folder = lastJob.Input.Folder;
        zipItem.Enabled = false;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string zip = null, error = null;
            try { zip = AfterRecording.MakeZip(folder); }
            catch (Exception e) { error = e.Message; }
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    zipItem.Enabled = true;
                    if (error != null)
                    {
                        tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("Cannot make the ZIP: " + error), ToolTipIcon.Warning);
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
                        tray.ShowBalloonTip(5000, "Desktop Recorder", Shorten("ZIP made, but not copied: " + e.Message + "\n" + zip), ToolTipIcon.Warning);
                    }
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    // After FFmpeg ended: the status line and the clipboard.
    void FinishStop()
    {
        string stats = UpdateStats();
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
        try { created = new SettingsForm(settings); }
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
                Settings.StartsWithWindows = form.StartWithWindows;
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

    void Quit()
    {
        // Folder mode: the pass makes the folder from the work file. Wait for it, so the recording is not lost
        // (review 2026-11-19). The tray shows that the app is finishing. ExitNow runs after the pass.
        quitRequested = true;
        if (recorder.IsRecording && recordingFolder != null && !stopping) StopRecording(false, true);
        if (stopping || passesRunning > 0)
        {
            statusItem.Text = "Finishing the last recording, then the app closes...";
            tray.Text = "Desktop Recorder: finishing";
            return;
        }
        ExitNow();
    }

    void ExitNow()
    {
        StopRecording(false, true); // no sound: the program ends before the sound can play. Wait for the file.
        DisposeFrame();
        hotkey.Close();
        tray.Visible = false;
        tray.Icon = null;
        if (currentIcon != null) currentIcon.Dispose();
        if (currentIconHandle != IntPtr.Zero) DestroyIcon(currentIconHandle);
        currentIcon = null;
        currentIconHandle = IntPtr.Zero;
        tray.Dispose();
        ExitThread();
    }

    void Refresh()
    {
        // While FFmpeg finishes the file, show the stopped state: the user already pressed Stop.
        bool on = recorder.IsRecording && !stopping;
        // During a recording, show the fps of that recording: a settings change applies to the next one.
        string fpsText = (on ? recordingFps : settings.Fps).ToString("0.###", CultureInfo.InvariantCulture);
        string where = recordingRegion.IsEmpty ? "whole desktop"
            : recordingRegion.Width.ToString(CultureInfo.InvariantCulture) + "x"
              + recordingRegion.Height.ToString(CultureInfo.InvariantCulture) + " region";
        statusItem.Text = on ? "Recording at " + fpsText + " fps, " + where
            : passesRunning > 0 ? "Making the frames of the last recording..." : "Not recording";
        // One hotkey starts and stops (Bran, 2026-11-14).
        toggleItem.Text = (on ? "Stop recording" : "Select region and record")
            + (hotkey.IsRegistered(SelectHotkeyId) ? "\t" + settings.HotkeyText : "");
        tray.Text = on ? "Desktop Recorder: recording" : "Desktop Recorder: stopped";

        // Tray icon from Bran's icon set (packaging/icon/tray-icon-set-source.jpg): corner brackets, a ring
        // and a dot. Idle: light blue lines and a small dot. Recording: pink-to-blue brackets and a red dot.
        // Drawn at the real tray size, so the thin lines stay sharp (20 px at 125 %).
        int px = Math.Max(16, SystemInformation.SmallIconSize.Width);
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
    public double SecondsFromStop;   // Stop to "clipboard ready"
    public bool NoNoteBox;           // Quit: no note box
}

// The note box after Stop (AI capture brief, Phase 1, item 3). One line. Enter saves, Esc closes.
// It does not block the pass. index.md gets the note when the user saves it, also after the pass.
sealed class NoteForm : Form
{
    readonly TextBox box = new TextBox();
    public event Action<string> Saved;

    public NoteForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Desktop Recorder: note for the AI";
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(420, 92);
        Font = SystemFonts.MessageBoxFont;
        Label label = new Label();
        label.Text = "Note for index.md (optional). Enter saves. Esc closes.";
        label.SetBounds(10, 8, 400, 20);
        box.SetBounds(10, 30, 400, 23);
        box.MaxLength = 500;
        Button save = new Button();
        save.Text = "Save";
        save.SetBounds(250, 60, 76, 26);
        save.Click += delegate { SaveNote(); };
        Button close = new Button();
        close.Text = "Close";
        close.SetBounds(334, 60, 76, 26);
        close.DialogResult = DialogResult.Cancel;
        close.Click += delegate { Close(); };
        Controls.AddRange(new Control[] { box, save, close, label });
        AcceptButton = save;
        CancelButton = close;
        ResumeLayout(false);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - 16, work.Bottom - Height - 16);
    }

    // The note box opens while FFmpeg still captures the last frames. Keep it out of the video.
    // Real recording on 2026-11-19: the box was in the last 2 frames and made 2 false "scroll" rows.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
    }

    void SaveNote()
    {
        string text = box.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        Action<string> h = Saved;
        if (h != null && text.Length > 0) h(text);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Dispose();
    }
}

class SettingsForm : Form
{
    static readonly string[] OutputNames = { "Folder for AI: frames, index.md, clip.mp4 (recommended)", "MP4 only (as before)" };
    static readonly string[] OutputValues = { RecorderCore.OutputFolder, RecorderCore.OutputMp4 };
    readonly ComboBox outputBox = new ComboBox();
    static readonly string[] BackendNames = { "ddagrab: GPU capture (recommended)", "gdigrab: CPU capture (fallback)" };
    static readonly string[] BackendValues = { RecorderCore.BackendDdagrab, RecorderCore.BackendGdigrab };
    static readonly string[] EncoderNames = { "Auto: NVENC, else x264 (recommended)", "NVIDIA NVENC (h264_nvenc)", "x264 on the CPU (libx264)" };
    static readonly string[] EncoderValues = { RecorderCore.EncoderAuto, RecorderCore.EncoderNvenc, RecorderCore.EncoderX264 };
    static readonly string[] ChromaNames = { "4:2:0 (plays everywhere)", "4:4:4 (sharper colored text)" };
    static readonly string[] ChromaValues = { RecorderCore.Chroma420, RecorderCore.Chroma444 };
    static readonly string[] ModeNames = { "Fixed frame rate", "Changes only (drops still frames)" };
    static readonly string[] ModeValues = { RecorderCore.ModeFixed, RecorderCore.ModeChanges };

    readonly Settings current;
    readonly TextBox folderBox = new TextBox();
    readonly NumericUpDown fpsBox = new NumericUpDown();
    readonly ComboBox qualityBox = new ComboBox();
    readonly NumericUpDown splitBox = new NumericUpDown();
    readonly CheckBox startupBox = new CheckBox();
    readonly CheckBox recordBox = new CheckBox();
    readonly CheckBox copyBox = new CheckBox();
    readonly CheckBox soundBox = new CheckBox();
    readonly CheckBox pointerBox = new CheckBox();
    readonly CheckBox winBox = new CheckBox();
    readonly CheckBox ctrlBox = new CheckBox();
    readonly CheckBox altBox = new CheckBox();
    readonly CheckBox shiftBox = new CheckBox();
    readonly ComboBox keyBox = new ComboBox();
    readonly ComboBox backendBox = new ComboBox();
    readonly ComboBox encoderBox = new ComboBox();
    readonly ComboBox chromaBox = new ComboBox();
    readonly ComboBox modeBox = new ComboBox();
    readonly CheckBox crosshairBox = new CheckBox();
    readonly CheckBox magnifierBox = new CheckBox();
    readonly CheckBox positionBox = new CheckBox();

    public Settings Result;
    public bool StartWithWindows;

    public SettingsForm(Settings current)
    {
        this.current = current;
        // TrayApp makes this dialog system DPI aware (see ShowSettings). The layout below is in 96 DPI units,
        // Windows Forms scales it to the system DPI, and Windows scales the window on other monitors.
        // Checked at 125% on Bran's PC with a screenshot, after the fix for bug 3 (HANDOFF 6.7).
        // UNVERIFIED: the look on a second monitor with a different DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Desktop Recorder settings";
        // The app icon (app.ico, built into the exe with /win32icon).
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 604);
        Font = SystemFonts.MessageBoxFont;

        AddLabel("Save recordings to:", 15);
        folderBox.SetBounds(15, 37, 355, 23);
        folderBox.Text = current.Folder;
        Button browse = new Button();
        browse.Text = "Browse...";
        browse.SetBounds(378, 36, 77, 25);
        browse.Click += delegate
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.SelectedPath = folderBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath;
            }
        };

        AddLabel("Frames per second (0.2 to 60). Lower = smaller files:", 75);
        fpsBox.SetBounds(360, 72, 95, 23);
        fpsBox.DecimalPlaces = 1;
        fpsBox.Increment = 1;
        fpsBox.Minimum = 0.2M;
        fpsBox.Maximum = 60;
        try { fpsBox.Value = (decimal)current.Fps; }
        catch (Exception) { fpsBox.Value = 2; }

        AddLabel("Quality:", 110, 180);
        qualityBox.SetBounds(200, 107, 255, 23);
        qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        qualityBox.Items.AddRange(Settings.QualityNames);
        qualityBox.SelectedIndex = current.Quality >= 0 && current.Quality < qualityBox.Items.Count ? current.Quality : 1;

        AddLabel("Start a new file every (minutes, 0 = never):", 145);
        splitBox.SetBounds(360, 142, 95, 23);
        splitBox.Minimum = 0;
        splitBox.Maximum = 1440;
        int split = current.SplitMinutes;
        if (split < 0) split = 0;
        if (split > 1440) split = 1440;
        splitBox.Value = split;

        AddLabel("Hotkey:", 180, 75);
        SetUpModifier(winBox, "Win", 90, 178, 8, current.HotkeyModifiers);
        SetUpModifier(ctrlBox, "Ctrl", 148, 178, 2, current.HotkeyModifiers);
        SetUpModifier(altBox, "Alt", 206, 178, 1, current.HotkeyModifiers);
        SetUpModifier(shiftBox, "Shift", 264, 178, 4, current.HotkeyModifiers);
        SetUpKeyBox(keyBox, 177, current.HotkeyKey);

        AddLabel("Capture:", 215, 180);
        SetUpChoice(backendBox, 212, BackendNames, BackendValues, current.CaptureBackend);
        AddLabel("Encoder:", 250, 180);
        SetUpChoice(encoderBox, 247, EncoderNames, EncoderValues, current.Encoder);
        AddLabel("Color detail (chroma):", 285, 180);
        SetUpChoice(chromaBox, 282, ChromaNames, ChromaValues, current.ChromaFormat);
        AddLabel("Frame-rate mode:", 320, 180);
        SetUpChoice(modeBox, 317, ModeNames, ModeValues, current.FrameRateMode);
        AddLabel("Output:", 355, 180);
        SetUpChoice(outputBox, 352, OutputNames, OutputValues, current.OutputMode);

        startupBox.Text = "Start Desktop Recorder when Windows starts";
        startupBox.SetBounds(15, 390, 440, 22);
        try { startupBox.Checked = Settings.StartsWithWindows; } catch (Exception) { }

        recordBox.Text = "Start recording (saved region) when Desktop Recorder starts";
        recordBox.SetBounds(15, 416, 440, 22);
        recordBox.Checked = current.RecordOnLaunch;

        copyBox.Text = "Copy the recording (folder or video) to the clipboard when it stops";
        copyBox.SetBounds(15, 442, 440, 22);
        copyBox.Checked = current.CopyToClipboard;

        soundBox.Text = "Play a sound when recording starts and stops";
        soundBox.SetBounds(15, 468, 440, 22);
        soundBox.Checked = current.PlaySounds;

        pointerBox.Text = "Hide the mouse pointer in the video";
        pointerBox.SetBounds(15, 494, 440, 22);
        pointerBox.Checked = !current.ShowPointer;

        AddLabel("Region selector:", 523, 110);
        crosshairBox.Text = "Crosshair";
        crosshairBox.SetBounds(130, 520, 95, 22);
        crosshairBox.Checked = current.SelectorCrosshair;
        magnifierBox.Text = "Magnifier";
        magnifierBox.SetBounds(230, 520, 95, 22);
        magnifierBox.Checked = current.SelectorMagnifier;
        positionBox.Text = "Position";
        positionBox.SetBounds(330, 520, 95, 22);
        positionBox.Checked = current.SelectorPosition;

        Button save = new Button();
        save.Text = "Save";
        save.SetBounds(290, 564, 80, 27);
        save.Click += delegate { OnSave(); };
        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.SetBounds(375, 564, 80, 27);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[]
        {
            folderBox, browse, fpsBox, qualityBox, splitBox, winBox, ctrlBox, altBox, shiftBox, keyBox,
            backendBox, encoderBox, chromaBox, modeBox, outputBox,
            startupBox, recordBox, copyBox, soundBox, pointerBox, crosshairBox, magnifierBox, positionBox, save, cancel
        });
        // The first control in Controls is on top. AddLabel adds the labels first, so they covered
        // the input fields (the fps value, the start of each list, the Win check box) at 125% on
        // Bran's PC. Put the labels behind all other controls.
        List<Control> labels = new List<Control>();
        foreach (Control c in Controls) if (c is Label) labels.Add(c);
        foreach (Control c in labels) c.SendToBack();
        AcceptButton = save;
        CancelButton = cancel;
        ResumeLayout(false);
    }

    void AddLabel(string text, int top, int width = 340)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(15, top, width, 20);
        Controls.Add(label);
    }

    static void SetUpModifier(CheckBox box, string text, int left, int top, int bit, int mods)
    {
        box.Text = text;
        box.SetBounds(left, top, 56, 22);
        box.Checked = (mods & bit) != 0;
    }

    static void SetUpKeyBox(ComboBox box, int top, Keys key)
    {
        box.SetBounds(360, top, 95, 23);
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        for (char c = 'A'; c <= 'Z'; c++) box.Items.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) box.Items.Add(c.ToString());
        for (int f = 1; f <= 12; f++) box.Items.Add("F" + f);
        string keyName = Settings.KeyName(key);
        if (!box.Items.Contains(keyName)) box.Items.Add(keyName);
        box.SelectedItem = keyName;
    }

    static void SetUpChoice(ComboBox box, int top, string[] names, string[] values, string value)
    {
        box.SetBounds(200, top, 255, 23);
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.Items.AddRange(names);
        int index = Array.IndexOf(values, value);
        box.SelectedIndex = index >= 0 ? index : 0;
    }

    static string ChoiceValue(ComboBox box, string[] values)
    {
        int index = box.SelectedIndex;
        return index >= 0 && index < values.Length ? values[index] : values[0];
    }

    // Reads one hotkey row. Returns false and shows a message when the row is not a usable hotkey.
    bool ReadHotkey(string name, CheckBox win, CheckBox ctrl, CheckBox alt, CheckBox shift, ComboBox key,
        out int mods, out Keys hotkeyKey)
    {
        mods = (win.Checked ? 8 : 0) | (ctrl.Checked ? 2 : 0) | (alt.Checked ? 1 : 0) | (shift.Checked ? 4 : 0);
        if (!Settings.TryParseKey((string)key.SelectedItem ?? "", out hotkeyKey)
            || !Settings.IsValidHotkey(mods, hotkeyKey))
        {
            MessageBox.Show(this, "For the " + name + ", tick at least one of Win, Ctrl, Alt and pick a key. "
                + "Shift alone works only with F1 to F12.", Text);
            return false;
        }
        return true;
    }

    void OnSave()
    {
        int mods;
        Keys hotkeyKey;
        if (!ReadHotkey("hotkey", winBox, ctrlBox, altBox, shiftBox, keyBox, out mods, out hotkeyKey)) return;
        string folder = folderBox.Text.Trim().Trim('"');
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
        {
            MessageBox.Show(this, "Give a full folder path, such as D:\\Recordings.", Text);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception e)
        {
            MessageBox.Show(this, "Cannot create that folder: " + e.Message, Text);
            return;
        }
        // Start from a copy, so the fields that this form does not show (regions, NVENC CQ,
        // Linux lines) stay as they are.
        Result = current.Copy();
        Result.Folder = folder.TrimEnd('\\', '/').Length == 2 ? folder : folder.TrimEnd('\\', '/');
        Result.Fps = (double)fpsBox.Value;
        Result.Quality = qualityBox.SelectedIndex;
        Result.SplitMinutes = (int)splitBox.Value;
        Result.RecordOnLaunch = recordBox.Checked;
        Result.CopyToClipboard = copyBox.Checked;
        Result.PlaySounds = soundBox.Checked;
        Result.ShowPointer = !pointerBox.Checked;
        Result.HotkeyModifiers = mods;
        Result.HotkeyKey = hotkeyKey;
        Result.CaptureBackend = ChoiceValue(backendBox, BackendValues);
        Result.Encoder = ChoiceValue(encoderBox, EncoderValues);
        Result.ChromaFormat = ChoiceValue(chromaBox, ChromaValues);
        Result.FrameRateMode = ChoiceValue(modeBox, ModeValues);
        Result.OutputMode = ChoiceValue(outputBox, OutputValues);
        Result.SelectorCrosshair = crosshairBox.Checked;
        Result.SelectorMagnifier = magnifierBox.Checked;
        Result.SelectorPosition = positionBox.Checked;
        StartWithWindows = startupBox.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
