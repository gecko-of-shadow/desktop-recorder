// Desktop Recorder: a tray program that records the desktop to MP4 with ffmpeg.
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
    public int HotkeyModifiers = 4 | 8;   // 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win
    public Keys HotkeyKey = Keys.D;
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
        stored.HotkeyModifiers = HotkeyModifiers;
        stored.HotkeyKey = (int)HotkeyKey;
        stored.ExtraLines = ExtraLines ?? new List<string>();
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
        HotkeyModifiers = stored.HotkeyModifiers;
        HotkeyKey = (Keys)stored.HotkeyKey;
        ExtraLines = stored.ExtraLines ?? new List<string>();
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

    public static string BuildArguments(Settings s, DateTime now)
    {
        return RecorderCore.JoinWindowsArguments(RecorderCore.BuildWindowsArguments(s.ToStored(), now));
    }

    // Returns null on success, or a message to show.
    public string Start(Settings s)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return "ffmpeg.exe was not found. Run this in PowerShell: winget install -e --id Gyan.FFmpeg. " +
                "Or put ffmpeg.exe next to DesktopRecorder.exe.";
        return session.Start(ffmpeg, BuildArguments(s, DateTime.Now), s.Folder, s.Fps);
    }

    public string[] SessionFiles()
    {
        return session.SessionFiles();
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

// A hidden window that listens for the global hotkey. The default is Win+Shift+D.
class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    public event Action Pressed;
    public bool Registered;

    public HotkeyWindow()
    {
        CreateParams parameters = new CreateParams();
        parameters.Caption = "Desktop Recorder";
        CreateHandle(parameters);
    }

    // Replaces the hotkey. Returns false if another program already uses it.
    // modifiers: 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win.
    public bool Register(int modifiers, Keys key)
    {
        if (Registered) UnregisterHotKey(Handle, 1);
        // 0x4000 = do not repeat while the keys are held.
        Registered = RegisterHotKey(Handle, 1, (uint)modifiers | 0x4000, (uint)key);
        return Registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            Action handler = Pressed;
            if (handler != null) handler();
        }
        base.WndProc(ref m);
    }

    public void Close()
    {
        if (Registered) UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

class TrayApp : ApplicationContext
{
    readonly HotkeyWindow hotkey = new HotkeyWindow();
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Recorder recorder = new Recorder();
    readonly Control ui = new Control();
    readonly MenuItem statusItem = new MenuItem("Not recording");
    readonly MenuItem toggleItem = new MenuItem("Start recording");
    Settings settings = Settings.Load();
    SettingsForm openForm;
    Icon currentIcon;
    IntPtr currentIconHandle = IntPtr.Zero;

    public TrayApp()
    {
        IntPtr unused = ui.Handle; // makes the window that lets worker threads call the UI thread
        hotkey.Register(settings.HotkeyModifiers, settings.HotkeyKey);

        statusItem.Enabled = false;
        toggleItem.Click += delegate { Toggle(); };
        MenuItem folderItem = new MenuItem("Open recordings folder");
        folderItem.Click += delegate { OpenFolder(); };
        MenuItem settingsItem = new MenuItem("Settings...");
        settingsItem.Click += delegate { ShowSettings(); };
        MenuItem exitItem = new MenuItem("Exit");
        exitItem.Click += delegate { Quit(); };
        tray.ContextMenu = new ContextMenu(new MenuItem[]
        {
            statusItem, new MenuItem("-"), toggleItem, folderItem, settingsItem, new MenuItem("-"), exitItem
        });
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;

        recorder.Failed += delegate(string message)
        {
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    Refresh();
                    if (settings.PlaySounds) Sounds.PlayStop();
                    tray.ShowBalloonTip(8000, "Desktop Recorder stopped", Shorten(message), ToolTipIcon.Error);
                });
            }
            catch (InvalidOperationException) { }
        };
        SystemEvents.SessionEnding += delegate { recorder.Stop(); };

        hotkey.Pressed += delegate
        {
            Toggle();
            if (recorder.IsRecording)
                tray.ShowBalloonTip(2000, "Desktop Recorder", "Recording started.", ToolTipIcon.Info);
        };

        Refresh();
        if (settings.RecordOnLaunch) StartRecording();
        if (!hotkey.Registered)
            tray.ShowBalloonTip(6000, "Desktop Recorder",
                settings.HotkeyText + " is used by another program, so the hotkey is off.", ToolTipIcon.Warning);
    }

    static string Shorten(string text)
    {
        return text.Length > 250 ? text.Substring(0, 250) : text;
    }

    void StartRecording(bool sound = true)
    {
        string error = recorder.Start(settings);
        Refresh();
        if (error != null) tray.ShowBalloonTip(8000, "Desktop Recorder", error, ToolTipIcon.Error);
        else if (sound && settings.PlaySounds) Sounds.PlayStart();
    }

    void Toggle()
    {
        if (recorder.IsRecording) { StopRecording(); Refresh(); }
        else StartRecording();
    }

    // Stops and, if the setting is on, puts the finished video file(s) on the clipboard, ready to paste.
    void StopRecording(bool sound = true)
    {
        if (!recorder.IsRecording) return;
        // Play before the stop: the stop can wait some seconds for ffmpeg to finish the file.
        if (sound && settings.PlaySounds) Sounds.PlayStop();
        recorder.Stop();
        if (!settings.CopyToClipboard) return;
        try
        {
            string[] files = recorder.SessionFiles();
            if (files.Length == 0) return;
            System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
            list.AddRange(files);
            Clipboard.SetFileDropList(list);
            tray.ShowBalloonTip(4000, "Desktop Recorder",
                files.Length == 1 ? "Video copied. Press Ctrl+V to paste it."
                                  : files.Length + " video files copied. Press Ctrl+V to paste them.",
                ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            tray.ShowBalloonTip(6000, "Desktop Recorder",
                "Could not copy the video to the clipboard: " + e.Message, ToolTipIcon.Warning);
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
        using (SettingsForm form = new SettingsForm(settings))
        {
            openForm = form;
            DialogResult result = form.ShowDialog();
            openForm = null;
            if (result != DialogResult.OK) return;
            settings = form.Result;
            try
            {
                settings.Save();
                Settings.StartsWithWindows = form.StartWithWindows;
            }
            catch (Exception e) { MessageBox.Show("Cannot save the settings: " + e.Message, "Desktop Recorder"); }
            if (!hotkey.Register(settings.HotkeyModifiers, settings.HotkeyKey))
                MessageBox.Show(settings.HotkeyText + " is used by another program, so the hotkey is off. "
                    + "Pick a different hotkey in Settings.", "Desktop Recorder");
            Refresh();
            if (recorder.IsRecording)
            {
                // Restart so the new folder, fps and quality apply now.
                recorder.Stop();
                StartRecording(false);
            }
        }
    }

    void Quit()
    {
        StopRecording(false); // no sound: the program ends before the sound can play
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
        bool on = recorder.IsRecording;
        string fpsText = settings.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        statusItem.Text = on ? "Recording at " + fpsText + " fps" : "Not recording";
        toggleItem.Text = (on ? "Stop recording" : "Start recording") + (hotkey.Registered ? "\t" + settings.HotkeyText : "");
        tray.Text = on ? "Desktop Recorder: recording" : "Desktop Recorder: stopped";

        using (Bitmap bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                // A monitor: white frame and stand, red screen with a white dot while recording, grey when stopped.
                using (Brush frame = new SolidBrush(Color.White))
                {
                    g.FillRectangle(frame, 1, 3, 30, 21);
                    g.FillRectangle(frame, 13, 24, 6, 3);
                    g.FillRectangle(frame, 8, 27, 16, 3);
                }
                using (Brush screen = new SolidBrush(on ? Color.FromArgb(220, 30, 30) : Color.FromArgb(110, 110, 110)))
                    g.FillRectangle(screen, 4, 6, 24, 15);
                if (on)
                    using (Brush dot = new SolidBrush(Color.White)) g.FillEllipse(dot, 12, 9.5f, 8, 8);
            }
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

class SettingsForm : Form
{
    readonly TextBox folderBox = new TextBox();
    readonly NumericUpDown fpsBox = new NumericUpDown();
    readonly ComboBox qualityBox = new ComboBox();
    readonly NumericUpDown splitBox = new NumericUpDown();
    readonly CheckBox startupBox = new CheckBox();
    readonly CheckBox recordBox = new CheckBox();
    readonly CheckBox copyBox = new CheckBox();
    readonly CheckBox soundBox = new CheckBox();
    readonly CheckBox winBox = new CheckBox();
    readonly CheckBox ctrlBox = new CheckBox();
    readonly CheckBox altBox = new CheckBox();
    readonly CheckBox shiftBox = new CheckBox();
    readonly ComboBox keyBox = new ComboBox();

    public Settings Result;
    public bool StartWithWindows;

    public SettingsForm(Settings current)
    {
        Text = "Desktop Recorder settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 377);
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

        startupBox.Text = "Start Desktop Recorder when Windows starts";
        AddLabel("Hotkey:", 180, 70);
        SetUpModifier(winBox, "Win", 90, 8, current);
        SetUpModifier(ctrlBox, "Ctrl", 148, 2, current);
        SetUpModifier(altBox, "Alt", 206, 1, current);
        SetUpModifier(shiftBox, "Shift", 264, 4, current);
        keyBox.SetBounds(360, 177, 95, 23);
        keyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        for (char c = 'A'; c <= 'Z'; c++) keyBox.Items.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) keyBox.Items.Add(c.ToString());
        for (int f = 1; f <= 12; f++) keyBox.Items.Add("F" + f);
        string keyName = Settings.KeyName(current.HotkeyKey);
        if (!keyBox.Items.Contains(keyName)) keyBox.Items.Add(keyName);
        keyBox.SelectedItem = keyName;

        startupBox.SetBounds(15, 215, 440, 22);
        try { startupBox.Checked = Settings.StartsWithWindows; } catch (Exception) { }

        recordBox.Text = "Start recording when Desktop Recorder starts";
        recordBox.SetBounds(15, 241, 440, 22);
        recordBox.Checked = current.RecordOnLaunch;

        copyBox.Text = "Copy the video to the clipboard when recording stops";
        copyBox.SetBounds(15, 267, 440, 22);
        copyBox.Checked = current.CopyToClipboard;

        soundBox.Text = "Play a sound when recording starts and stops";
        soundBox.SetBounds(15, 293, 440, 22);
        soundBox.Checked = current.PlaySounds;

        Button save = new Button();
        save.Text = "Save";
        save.SetBounds(290, 337, 80, 27);
        save.Click += delegate { OnSave(); };
        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.SetBounds(375, 337, 80, 27);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[]
        {
            folderBox, browse, fpsBox, qualityBox, splitBox, winBox, ctrlBox, altBox, shiftBox, keyBox,
            startupBox, recordBox, copyBox, soundBox, save, cancel
        });
        AcceptButton = save;
        CancelButton = cancel;
    }

    void AddLabel(string text, int top, int width = 340)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(15, top, width, 20);
        Controls.Add(label);
    }

    void SetUpModifier(CheckBox box, string text, int left, int bit, Settings current)
    {
        box.Text = text;
        box.SetBounds(left, 178, 56, 22);
        box.Checked = (current.HotkeyModifiers & bit) != 0;
    }

    void OnSave()
    {
        int mods = (winBox.Checked ? 8 : 0) | (ctrlBox.Checked ? 2 : 0) | (altBox.Checked ? 1 : 0)
            | (shiftBox.Checked ? 4 : 0);
        Keys hotkeyKey;
        if (!Settings.TryParseKey((string)keyBox.SelectedItem ?? "", out hotkeyKey)
            || !Settings.IsValidHotkey(mods, hotkeyKey))
        {
            MessageBox.Show(this, "For the hotkey, tick at least one of Win, Ctrl, Alt and pick a key. "
                + "Shift alone works only with F1 to F12.", Text);
            return;
        }
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
        Result = new Settings();
        Result.Folder = folder.TrimEnd('\\', '/').Length == 2 ? folder : folder.TrimEnd('\\', '/');
        Result.Fps = (double)fpsBox.Value;
        Result.Quality = qualityBox.SelectedIndex;
        Result.SplitMinutes = (int)splitBox.Value;
        Result.RecordOnLaunch = recordBox.Checked;
        Result.CopyToClipboard = copyBox.Checked;
        Result.PlaySounds = soundBox.Checked;
        Result.HotkeyModifiers = mods;
        Result.HotkeyKey = hotkeyKey;
        StartWithWindows = startupBox.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
