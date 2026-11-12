// Desktop Recorder: a tray program that records the desktop to MP4 with ffmpeg.
// Builds with the C# compiler that ships with Windows (C# 5, .NET Framework 4.x). See build.cmd.

using System;
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

    public static readonly int[] Crf = { 32, 27, 21 };
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

    public static Settings Load()
    {
        Settings s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                double d;
                int i;
                if (key == "folder" && val.Length > 0) s.Folder = val;
                else if (key == "fps" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s.Fps = d;
                else if (key == "quality" && int.TryParse(val, out i)) s.Quality = i;
                else if (key == "split_minutes" && int.TryParse(val, out i)) s.SplitMinutes = i;
                else if (key == "record_on_launch") s.RecordOnLaunch = val == "1";
                else if (key == "copy_to_clipboard") s.CopyToClipboard = val == "1";
            }
        }
        catch (Exception) { }
        s.Fps = Math.Max(0.2, Math.Min(60, s.Fps));
        s.Quality = Math.Max(0, Math.Min(2, s.Quality));
        s.SplitMinutes = Math.Max(0, Math.Min(1440, s.SplitMinutes));
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, new string[]
        {
            "folder=" + Folder,
            "fps=" + Fps.ToString(CultureInfo.InvariantCulture),
            "quality=" + Quality,
            "split_minutes=" + SplitMinutes,
            "record_on_launch=" + (RecordOnLaunch ? "1" : "0"),
            "copy_to_clipboard=" + (CopyToClipboard ? "1" : "0")
        });
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
    Process proc;
    bool stopping;
    double fps;
    string sessionFolder;
    DateTime sessionStart;
    readonly StringBuilder errors = new StringBuilder();

    // Raised on a worker thread when ffmpeg ends by itself. The argument is ffmpeg's error text.
    public event Action<string> Failed;

    public bool IsRecording { get { return proc != null; } }

    public static string FindFfmpeg()
    {
        string local = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ffmpeg.exe");
        if (File.Exists(local)) return local;
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Links\ffmpeg.exe");
        if (File.Exists(winget)) return winget;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(';'))
        {
            try
            {
                if (dir.Trim().Length == 0) continue;
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }

    public static string BuildArguments(Settings s, DateTime now)
    {
        string fpsText = s.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        // One keyframe about every 30 seconds: long gaps keep the files small.
        int gop = Math.Max(2, (int)Math.Round(s.Fps * 30));
        StringBuilder a = new StringBuilder();
        a.Append("-hide_banner -loglevel error -nostats -y ");
        a.Append("-f gdigrab -framerate " + fpsText + " -draw_mouse 1 -i desktop ");
        // Full resolution so text stays readable. H.264 needs an even width and height.
        a.Append("-vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0\" ");
        a.Append("-c:v libx264 -preset medium -tune stillimage -crf " + Settings.Crf[s.Quality] + " ");
        a.Append("-g " + gop + " -pix_fmt yuv420p ");
        // Fragmented MP4: the file stays playable if the PC loses power or ffmpeg is killed.
        if (s.SplitMinutes > 0)
        {
            a.Append("-f segment -segment_time " + (s.SplitMinutes * 60) + " -segment_format mp4 ");
            a.Append("-segment_format_options movflags=+frag_keyframe+empty_moov ");
            a.Append("-reset_timestamps 1 -strftime 1 ");
            a.Append("\"" + Path.Combine(s.Folder, "desktop_%Y-%m-%d_%H-%M-%S.mp4") + "\"");
        }
        else
        {
            a.Append("-movflags +frag_keyframe+empty_moov ");
            a.Append("\"" + Path.Combine(s.Folder, "desktop_" + now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4") + "\"");
        }
        return a.ToString();
    }

    // Returns null on success, or a message to show.
    public string Start(Settings s)
    {
        if (proc != null) return null;
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return "ffmpeg.exe was not found. Run this in PowerShell: winget install -e --id Gyan.FFmpeg. " +
                "Or put ffmpeg.exe next to DesktopRecorder.exe.";
        try { Directory.CreateDirectory(s.Folder); }
        catch (Exception e) { return "Cannot create the folder " + s.Folder + ": " + e.Message; }

        ProcessStartInfo info = new ProcessStartInfo(ffmpeg, BuildArguments(s, DateTime.Now));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardError = true;

        Process p = new Process();
        p.StartInfo = info;
        p.EnableRaisingEvents = true;
        lock (errors) { errors.Length = 0; }
        p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (errors) { if (errors.Length < 2000) errors.AppendLine(e.Data); }
        };
        p.Exited += delegate(object sender, EventArgs e)
        {
            if (stopping || proc != p) return;
            proc = null;
            string text;
            lock (errors) { text = errors.ToString().Trim(); }
            Action<string> handler = Failed;
            if (handler != null) handler(text.Length > 0 ? text : "ffmpeg stopped.");
        };
        try { p.Start(); }
        catch (Exception e) { return "Cannot start ffmpeg: " + e.Message; }
        p.BeginErrorReadLine();
        stopping = false;
        fps = s.Fps;
        sessionFolder = s.Folder;
        sessionStart = DateTime.Now.AddSeconds(-2);
        proc = p;
        return null;
    }

    // The video files made by the last recording, oldest first.
    public string[] SessionFiles()
    {
        System.Collections.Generic.List<string> found = new System.Collections.Generic.List<string>();
        try
        {
            if (sessionFolder == null) return found.ToArray();
            foreach (string file in Directory.GetFiles(sessionFolder, "desktop_*.mp4"))
            {
                FileInfo info = new FileInfo(file);
                if (info.LastWriteTime >= sessionStart && info.Length > 0) found.Add(file);
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { }
        return found.ToArray();
    }

    public void Stop()
    {
        Process p = proc;
        if (p == null) return;
        stopping = true;
        proc = null;
        try
        {
            // "q" asks ffmpeg to finish the file. It reads it between frames, so wait longer at low fps.
            p.StandardInput.Write("q");
            p.StandardInput.Flush();
            int waitMs = (int)Math.Min(15000, 4000 + 2000 / Math.Max(0.2, fps));
            if (!p.WaitForExit(waitMs)) p.Kill();
        }
        catch (Exception)
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }
        p.Dispose();
    }
}

// A hidden window that listens for the global hotkey Win+Shift+D.
class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    public event Action Pressed;
    public readonly bool Registered;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
        // 0x4 = Shift, 0x8 = Win, 0x4000 = do not repeat while the keys are held.
        Registered = RegisterHotKey(Handle, 1, 0x4 | 0x8 | 0x4000, (uint)Keys.D);
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
            ui.BeginInvoke((MethodInvoker)delegate
            {
                Refresh();
                tray.ShowBalloonTip(8000, "Desktop Recorder stopped", Shorten(message), ToolTipIcon.Error);
            });
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
                "Win+Shift+D is used by another program, so the hotkey is off.", ToolTipIcon.Warning);
    }

    static string Shorten(string text)
    {
        return text.Length > 250 ? text.Substring(0, 250) : text;
    }

    void StartRecording()
    {
        string error = recorder.Start(settings);
        Refresh();
        if (error != null) tray.ShowBalloonTip(8000, "Desktop Recorder", error, ToolTipIcon.Error);
    }

    void Toggle()
    {
        if (recorder.IsRecording) { StopRecording(); Refresh(); }
        else StartRecording();
    }

    // Stops and, if the setting is on, puts the finished video file(s) on the clipboard, ready to paste.
    void StopRecording()
    {
        if (!recorder.IsRecording) return;
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
        catch (Exception) { }
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.Folder);
            Process.Start("explorer.exe", "\"" + settings.Folder + "\"");
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
            if (recorder.IsRecording)
            {
                // Restart so the new folder, fps and quality apply now.
                recorder.Stop();
                StartRecording();
            }
        }
    }

    void Quit()
    {
        StopRecording();
        hotkey.Close();
        tray.Visible = false;
        ExitThread();
    }

    void Refresh()
    {
        bool on = recorder.IsRecording;
        string fpsText = settings.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        statusItem.Text = on ? "Recording at " + fpsText + " fps" : "Not recording";
        toggleItem.Text = (on ? "Stop recording" : "Start recording") + (hotkey.Registered ? "\tWin+Shift+D" : "");
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
        ClientSize = new Size(470, 316);
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
        fpsBox.Value = (decimal)current.Fps;

        AddLabel("Quality:", 110, 180);
        qualityBox.SetBounds(200, 107, 255, 23);
        qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        qualityBox.Items.AddRange(Settings.QualityNames);
        qualityBox.SelectedIndex = current.Quality;

        AddLabel("Start a new file every (minutes, 0 = never):", 145);
        splitBox.SetBounds(360, 142, 95, 23);
        splitBox.Minimum = 0;
        splitBox.Maximum = 1440;
        splitBox.Value = current.SplitMinutes;

        startupBox.Text = "Start Desktop Recorder when Windows starts";
        startupBox.SetBounds(15, 180, 440, 22);
        try { startupBox.Checked = Settings.StartsWithWindows; } catch (Exception) { }

        recordBox.Text = "Start recording when Desktop Recorder starts";
        recordBox.SetBounds(15, 206, 440, 22);
        recordBox.Checked = current.RecordOnLaunch;

        copyBox.Text = "Copy the video to the clipboard when recording stops";
        copyBox.SetBounds(15, 232, 440, 22);
        copyBox.Checked = current.CopyToClipboard;

        Button save = new Button();
        save.Text = "Save";
        save.SetBounds(290, 276, 80, 27);
        save.Click += delegate { OnSave(); };
        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.SetBounds(375, 276, 80, 27);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[]
        {
            folderBox, browse, fpsBox, qualityBox, splitBox, startupBox, recordBox, copyBox, save, cancel
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

    void OnSave()
    {
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
        StartWithWindows = startupBox.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
