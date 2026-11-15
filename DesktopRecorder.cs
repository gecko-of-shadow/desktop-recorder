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
    public double Fps = 2;
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
        plan = null;
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
        string arguments = RecorderCore.JoinWindowsArguments(
            RecorderCore.BuildCaptureArguments(stored, capture, monitors, DateTime.Now, nvenc));
        return session.Start(ffmpeg, arguments, s.Folder, s.Fps);
    }

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
        MenuItem settingsItem = new MenuItem("Settings...");
        settingsItem.Click += delegate { ShowSettings(); };
        MenuItem exitItem = new MenuItem("Exit");
        exitItem.Click += delegate { Quit(); };
        tray.ContextMenu = new ContextMenu(new MenuItem[]
        {
            statusItem, statsItem, new MenuItem("-"), toggleItem, folderItem, settingsItem,
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
                    Refresh();
                    if (settings.PlaySounds) Sounds.PlayStop();
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
            forcedGdigrab || useGdigrab, forcedX264 || useX264);
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
        CapturePlan plan;
        string error = recorder.Start(settings, region, forceGdigrab, forceX264, out plan);
        string frameWarning = null;
        if (error == null)
        {
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
        if (wait)
        {
            recorder.Stop();
            FinishStop();
            return;
        }
        stopping = true;
        Refresh();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { recorder.Stop(); }
            catch (Exception) { }
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    stopping = false;
                    FinishStop();
                    Refresh();
                });
            }
            catch (InvalidOperationException) { stopping = false; }
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
        statusItem.Text = on ? "Recording at " + fpsText + " fps, " + where : "Not recording";
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

// Draws the two tray icons (idle and recording) in code.
static class TrayIcons
{
    public static Bitmap Draw(int size, bool recording)
    {
        Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 16f;
            float stroke = Math.Max(1.25f, 1.3f * k);
            float inset = 0.5f * stroke + 0.3f * k;
            float len = 4.6f * k;
            float far = size - inset;
            Color top = recording ? Color.FromArgb(225, 120, 255) : Color.FromArgb(175, 220, 235);
            Color bottom = recording ? Color.FromArgb(80, 195, 255) : Color.FromArgb(175, 220, 235);
            using (Pen topPen = new Pen(top, stroke))
            using (Pen bottomPen = new Pen(bottom, stroke))
            {
                topPen.StartCap = topPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                bottomPen.StartCap = bottomPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                // Corner brackets.
                g.DrawLines(topPen, new[] { new PointF(inset, inset + len), new PointF(inset, inset), new PointF(inset + len, inset) });
                g.DrawLines(topPen, new[] { new PointF(far - len, inset), new PointF(far, inset), new PointF(far, inset + len) });
                g.DrawLines(bottomPen, new[] { new PointF(inset, far - len), new PointF(inset, far), new PointF(inset + len, far) });
                g.DrawLines(bottomPen, new[] { new PointF(far - len, far), new PointF(far, far), new PointF(far, far - len) });
            }
            float c = size / 2f;
            float ring = 4.3f * k;
            Color ringColor = recording ? Color.FromArgb(205, 175, 255) : Color.FromArgb(175, 220, 235);
            using (Pen ringPen = new Pen(ringColor, Math.Max(1f, 1.1f * k)))
                g.DrawEllipse(ringPen, c - ring, c - ring, 2 * ring, 2 * ring);
            float dot = recording ? 2.9f * k : 1.5f * k;
            using (Brush dotBrush = new SolidBrush(recording ? Color.FromArgb(255, 40, 90) : Color.FromArgb(175, 220, 235)))
                g.FillEllipse(dotBrush, c - dot, c - dot, 2 * dot, 2 * dot);
        }
        return bmp;
    }
}

class SettingsForm : Form
{
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
        // UNVERIFIED: the look at 125% and on a second monitor with a different DPI (no GUI run yet).
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
        ClientSize = new Size(470, 569);
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

        startupBox.Text = "Start Desktop Recorder when Windows starts";
        startupBox.SetBounds(15, 355, 440, 22);
        try { startupBox.Checked = Settings.StartsWithWindows; } catch (Exception) { }

        recordBox.Text = "Start recording (saved region) when Desktop Recorder starts";
        recordBox.SetBounds(15, 381, 440, 22);
        recordBox.Checked = current.RecordOnLaunch;

        copyBox.Text = "Copy the video to the clipboard when recording stops";
        copyBox.SetBounds(15, 407, 440, 22);
        copyBox.Checked = current.CopyToClipboard;

        soundBox.Text = "Play a sound when recording starts and stops";
        soundBox.SetBounds(15, 433, 440, 22);
        soundBox.Checked = current.PlaySounds;

        pointerBox.Text = "Hide the mouse pointer in the video";
        pointerBox.SetBounds(15, 459, 440, 22);
        pointerBox.Checked = !current.ShowPointer;

        AddLabel("Region selector:", 488, 110);
        crosshairBox.Text = "Crosshair";
        crosshairBox.SetBounds(130, 485, 95, 22);
        crosshairBox.Checked = current.SelectorCrosshair;
        magnifierBox.Text = "Magnifier";
        magnifierBox.SetBounds(230, 485, 95, 22);
        magnifierBox.Checked = current.SelectorMagnifier;
        positionBox.Text = "Position";
        positionBox.SetBounds(330, 485, 95, 22);
        positionBox.Checked = current.SelectorPosition;

        Button save = new Button();
        save.Text = "Save";
        save.SetBounds(290, 529, 80, 27);
        save.Click += delegate { OnSave(); };
        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.SetBounds(375, 529, 80, 27);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[]
        {
            folderBox, browse, fpsBox, qualityBox, splitBox, winBox, ctrlBox, altBox, shiftBox, keyBox,
            backendBox, encoderBox, chromaBox, modeBox,
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
        Result.SelectorCrosshair = crosshairBox.Checked;
        Result.SelectorMagnifier = magnifierBox.Checked;
        Result.SelectorPosition = positionBox.Checked;
        StartWithWindows = startupBox.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
