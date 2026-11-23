// live_ui_test.cs - drives the Desktop Recorder test build with synthetic input and checks the results (HANDOFF 7.22).
// Synthetic keys and mouse (keybd_event, mouse_event), UI Automation for window texts and positions, screenshots
// (CopyFromScreen, PrintWindow), the clipboard, the ZIP files, ffprobe, and a WASAPI loopback capture for the sounds.
// The process is per-monitor DPI aware: all coordinates are physical pixels. One monitor (the primary).
// Build (PowerShell, from the repository folder):
//   $fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
//   & "$fw\csc.exe" /nologo /target:exe /out:"$env:TEMP\svr-live-all\LiveUiTest.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll
//     /r:System.Management.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll
//     /r:"$fw\WPF\UIAutomationClient.dll" /r:"$fw\WPF\UIAutomationTypes.dll" /r:"$fw\WPF\WindowsBase.dll"
//     test\live_ui_test.cs AudioCapture.cs AudioTranscript.cs RecorderCore.cs RecordingSession.cs SettledFrames.cs AfterRecording.cs UiModel.cs
// Run: LiveUiTest.exe <command> [names]. Commands: exit-tray <tooltip prefix>, mp4, folder [names], more [names].
// Output: %TEMP%\svr-live-all\run-<command>-<time>\ (log.txt, results.tsv, PNG files). The results also go to
// %TEMP%\svr-live-all\results-all.tsv.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

static class LN
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] public static extern bool LockSetForegroundWindow(uint code);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}

sealed class LW
{
    public IntPtr H; public uint Pid; public string Title = ""; public string Cls = ""; public Rectangle Rect; public Rectangle Frame; public bool Visible;

    public static LW Of(IntPtr h)
    {
        LW w = new LW();
        w.H = h;
        if (h == IntPtr.Zero) return w;
        LN.GetWindowThreadProcessId(h, out w.Pid);
        StringBuilder s = new StringBuilder(512);
        LN.GetWindowText(h, s, 512); w.Title = s.ToString();
        s.Length = 0; LN.GetClassName(h, s, 512); w.Cls = s.ToString();
        LN.RECT r;
        if (LN.GetWindowRect(h, out r)) w.Rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        LN.RECT f;
        if (LN.DwmGetWindowAttribute(h, 9, out f, Marshal.SizeOf(typeof(LN.RECT))) == 0 && f.Right > f.Left) w.Frame = Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom);
        else w.Frame = w.Rect;
        w.Visible = LN.IsWindowVisible(h);
        return w;
    }

    public static List<LW> List(uint pid, bool visibleOnly)
    {
        List<LW> list = new List<LW>();
        LN.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            LN.GetWindowThreadProcessId(h, out p);
            if (pid != 0 && p != pid) return true;
            if (visibleOnly && !LN.IsWindowVisible(h)) return true;
            list.Add(Of(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture, "'{0}' {1},{2} {3}x{4}", Title, Frame.X, Frame.Y, Frame.Width, Frame.Height);
    }
}

// A small topmost window of the driver: the paste target (an Edit control), and the shell hook that sees window flashes.
sealed class LPad : Form
{
    public readonly TextBox Box = new TextBox();
    public readonly List<KeyValuePair<double, IntPtr>> Flashes = new List<KeyValuePair<double, IntPtr>>();
    public readonly List<string> ShellLog = new List<string>();
    uint shellMsg;

    public LPad()
    {
        Text = "LiveUiTest pad";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(10, 10);
        Size = new Size(420, 120);
        TopMost = true;
        ShowInTaskbar = false;
        Box.Multiline = true;
        Box.Dock = DockStyle.Fill;
        Controls.Add(Box);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        shellMsg = LN.RegisterWindowMessage("SHELLHOOK");
        LN.RegisterShellHookWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (shellMsg != 0 && m.Msg == (int)shellMsg)
        {
            long code = m.WParam.ToInt64();
            if (code == 0x8006) Flashes.Add(new KeyValuePair<double, IntPtr>(LT.Clock.Elapsed.TotalSeconds, m.LParam));
            if (ShellLog.Count < 400) ShellLog.Add(string.Format("0x{0:X} '{1}'", code, LW.Of(m.LParam).Title));
        }
        base.WndProc(ref m);
    }

    public Point BoxCenter()
    {
        Rectangle r = Box.RectangleToScreen(Box.ClientRectangle);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
}

static class LIn
{
    public static void Key(byte vk, bool up)
    {
        uint f = up ? 2u : 0u;
        if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C) f |= 1;
        LN.keybd_event(vk, 0, f, IntPtr.Zero);
    }

    public static void Press(byte vk) { Key(vk, false); LT.Wait(40); Key(vk, true); LT.Wait(60); }

    public static void Chord(params byte[] vks)
    {
        foreach (byte v in vks) Key(v, false);
        LT.Wait(60);
        for (int i = vks.Length - 1; i >= 0; i--) Key(vks[i], true);
        LT.Wait(80);
    }

    public static void Hotkey() { Chord(0x5B, 0x10, 0x44); }

    public static void Move(int x, int y)
    {
        Rectangle b = Screen.PrimaryScreen.Bounds;
        LN.mouse_event(0x8001, x * 65535 / (b.Width - 1), y * 65535 / (b.Height - 1), 0, IntPtr.Zero);
    }

    public static void LeftDown() { LN.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void LeftUp() { LN.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }

    public static void Click(int x, int y) { Move(x, y); LT.Wait(150); LeftDown(); LT.Wait(60); LeftUp(); LT.Wait(150); }
    public static void Click(Point p) { Click(p.X, p.Y); }

    public static void RightClick(int x, int y)
    {
        Move(x, y); LT.Wait(150);
        LN.mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); LT.Wait(60);
        LN.mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); LT.Wait(150);
    }

    public static void DoubleClick(int x, int y)
    {
        Move(x, y); LT.Wait(150);
        LeftDown(); LT.Wait(30); LeftUp(); LT.Wait(60); LeftDown(); LT.Wait(30); LeftUp(); LT.Wait(200);
    }

    // Moves with the left button down from (x1,y1) to (x2,y2). The caller releases the button.
    public static void DragOpen(int x1, int y1, int x2, int y2)
    {
        Move(x1, y1); LT.Wait(200);
        LeftDown(); LT.Wait(80);
        MoveSteps(x1, y1, x2, y2, 12);
        LT.Wait(250);
    }

    public static void MoveSteps(int x1, int y1, int x2, int y2, int steps)
    {
        for (int i = 1; i <= steps; i++) { Move(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps); LT.Wait(25); }
    }

    public static void Drag(int x1, int y1, int x2, int y2) { DragOpen(x1, y1, x2, y2); LeftUp(); LT.Wait(200); }

    public static void Type(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s)
        {
            if ("+^%~(){}[]".IndexOf(c) >= 0) b.Append('{').Append(c).Append('}');
            else b.Append(c);
        }
        SendKeys.SendWait(b.ToString());
        LT.Wait(150);
    }

    public static Point Cursor() { LN.POINT p; LN.GetCursorPos(out p); return new Point(p.X, p.Y); }
}

static class LShot
{
    public static Bitmap Screen()
    {
        Rectangle b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Bitmap bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static string Save(Bitmap bmp, string name)
    {
        string path = Path.Combine(LT.Out, name + ".png");
        bmp.Save(path, ImageFormat.Png);
        return Path.GetFileName(path);
    }

    public static Bitmap Crop(Bitmap s, Rectangle crop)
    {
        Rectangle c = Rectangle.Intersect(crop, new Rectangle(0, 0, s.Width, s.Height));
        if (c.Width < 1 || c.Height < 1) c = new Rectangle(0, 0, 1, 1);
        return s.Clone(c, s.PixelFormat);
    }

    public static string ShotCrop(string name, Rectangle crop)
    {
        using (Bitmap s = Screen())
        using (Bitmap part = Crop(s, crop))
            return Save(part, name);
    }

    // PrintWindow with PW_RENDERFULLCONTENT: the window content, also for a window that is excluded from screen capture (to test).
    public static Bitmap Print(IntPtr h)
    {
        LW w = LW.Of(h);
        if (w.Rect.Width < 1 || w.Rect.Height < 1) return null;
        Bitmap b = new Bitmap(w.Rect.Width, w.Rect.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            IntPtr hdc = g.GetHdc();
            try { LN.PrintWindow(h, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        // Cut the invisible resize border away: keep the DWM frame.
        Rectangle f = new Rectangle(w.Frame.X - w.Rect.X, w.Frame.Y - w.Rect.Y, w.Frame.Width, w.Frame.Height);
        if (f.X >= 0 && f.Y >= 0 && f.Right <= b.Width && f.Bottom <= b.Height && (f.Width != b.Width || f.Height != b.Height))
        {
            Bitmap c = b.Clone(f, b.PixelFormat);
            b.Dispose();
            return c;
        }
        return b;
    }

    public static double DarkShare(Bitmap b)
    {
        long dark = 0, all = 0;
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2)
            {
                Color c = b.GetPixel(x, y);
                all++;
                if (c.R < 8 && c.G < 8 && c.B < 8) dark++;
            }
        return all == 0 ? 1 : (double)dark / all;
    }

    // Pixels that differ by more than 24 in any channel, inside r. Returns the count and the bounding box.
    public static int Diff(Bitmap a, Bitmap b, Rectangle r, out Rectangle box)
    {
        int n = 0, l = int.MaxValue, t = int.MaxValue, rr = -1, bb = -1;
        Rectangle c = Rectangle.Intersect(r, new Rectangle(0, 0, Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height)));
        BitmapData da = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData db = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] ra = new int[a.Width], rb = new int[b.Width];
            for (int y = c.Top; y < c.Bottom; y++)
            {
                Marshal.Copy(da.Scan0 + y * da.Stride, ra, 0, a.Width);
                Marshal.Copy(db.Scan0 + y * db.Stride, rb, 0, b.Width);
                for (int x = c.Left; x < c.Right; x++)
                {
                    int p = ra[x], q = rb[x];
                    if (Math.Abs(((p >> 16) & 255) - ((q >> 16) & 255)) > 24 || Math.Abs(((p >> 8) & 255) - ((q >> 8) & 255)) > 24 || Math.Abs((p & 255) - (q & 255)) > 24)
                    {
                        n++;
                        if (x < l) l = x; if (y < t) t = y; if (x > rr) rr = x; if (y > bb) bb = y;
                    }
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        box = n == 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, rr + 1, bb + 1);
        return n;
    }

    // Count of pixels with a strong color (blue, green, red accents) and of dark pixels in a picture.
    public static void ColorCount(Bitmap b, out int saturated, out int dark)
    {
        saturated = 0; dark = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                Color c = b.GetPixel(x, y);
                int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                if (max - min > 70) saturated++;
                if (max < 110) dark++;
            }
    }
}

static class LU
{
    public static string Name(AutomationElement e) { try { return e.Current.Name; } catch (Exception) { return null; } }

    public static Rectangle Bounds(AutomationElement e)
    {
        try
        {
            System.Windows.Rect r = e.Current.BoundingRectangle;
            if (r.IsEmpty) return Rectangle.Empty;
            return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }
        catch (Exception) { return Rectangle.Empty; }
    }

    public static Point Center(AutomationElement e) { Rectangle r = Bounds(e); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

    public static List<AutomationElement> All(IntPtr h, ControlType type)
    {
        List<AutomationElement> list = new List<AutomationElement>();
        try
        {
            AutomationElement root = AutomationElement.FromHandle(h);
            Condition c = type == null ? Condition.TrueCondition : new PropertyCondition(AutomationElement.ControlTypeProperty, type);
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, c)) list.Add(e);
        }
        catch (Exception) { }
        return list;
    }

    public static AutomationElement Find(IntPtr h, ControlType type, string name)
    {
        foreach (AutomationElement e in All(h, type))
        {
            string n = Name(e);
            if (n == name) return e;
        }
        return null;
    }

    public static AutomationElement FindVisible(IntPtr h, ControlType type, string name)
    {
        foreach (AutomationElement e in All(h, type))
        {
            try { if (Name(e) == name && !e.Current.IsOffscreen && Bounds(e).Width > 0) return e; }
            catch (Exception) { }
        }
        return null;
    }

    public static string Value(AutomationElement e)
    {
        try
        {
            object p;
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out p)) return ((ValuePattern)p).Current.Value;
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out p)) return ((TextPattern)p).DocumentRange.GetText(-1);
        }
        catch (Exception) { }
        return null;
    }

    // A multiline TextBox is a Document in UI Automation, a single-line one an Edit.
    public static AutomationElement Edit(IntPtr h)
    {
        List<AutomationElement> list = All(h, ControlType.Edit);
        if (list.Count == 0) list = All(h, ControlType.Document);
        return list.Count > 0 ? list[0] : null;
    }

    public static string EditValue(IntPtr h) { AutomationElement e = Edit(h); return e == null ? null : Value(e); }

    // The ZIP line of the note box.
    public static string ZipLine(IntPtr note)
    {
        foreach (AutomationElement e in All(note, ControlType.Text))
        {
            string n = Name(e);
            if (n != null && (n.StartsWith("Making the ZIP") || n.StartsWith("ZIP ") || n.StartsWith("Could not make"))) return n;
        }
        return null;
    }

    public static string Texts(IntPtr h)
    {
        List<string> parts = new List<string>();
        foreach (AutomationElement e in All(h, ControlType.Text))
        {
            string n = Plain(Name(e));
            if (n.Length > 0) parts.Add(n);
        }
        return string.Join(" | ", parts.ToArray());
    }

    // The name without icon glyphs (Segoe Fluent Icons use the private use area).
    public static string Plain(string n)
    {
        if (n == null) return "";
        StringBuilder b = new StringBuilder();
        foreach (char c in n) if (c < 0xE000 || c > 0xF8FF) b.Append(c);
        return b.ToString().Trim();
    }

    public static string Dump(IntPtr h)
    {
        StringBuilder b = new StringBuilder();
        foreach (AutomationElement e in All(h, null))
        {
            try
            {
                Rectangle r = Bounds(e);
                b.AppendFormat(CultureInfo.InvariantCulture, "    {0} '{1}' {2},{3} {4}x{5}{6}{7}\n", e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                    Name(e), r.X, r.Y, r.Width, r.Height, e.Current.IsEnabled ? "" : " disabled", e.Current.IsOffscreen ? " offscreen" : "");
            }
            catch (Exception) { }
        }
        return b.ToString();
    }

    public static AutomationElement TrayButton(string prefix)
    {
        try
        {
            AutomationElement tray = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray == null) return null;
            foreach (AutomationElement e in tray.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                string n = Name(e);
                if (n != null && n.StartsWith(prefix, StringComparison.Ordinal)) return e;
            }
        }
        catch (Exception) { }
        return null;
    }
}

// Follows the app after a Stop: note box, its ZIP line, the popup, the clipboard, the folder and the ZIP, the tray, the focus.
sealed class LWatch
{
    public readonly uint Pid;
    public readonly string Folder, Zip;
    public readonly Stopwatch T = new Stopwatch();
    public long StartTicks;
    public IntPtr Note = IntPtr.Zero, LastNote = IntPtr.Zero, Toast = IntPtr.Zero, Fg = IntPtr.Zero;
    public string NoteLine, ToastText, ClipText, TrayName;
    public string[] ClipFiles = new string[0];
    public Rectangle NoteRect, ToastRect;
    public bool FolderExists, ZipExists;
    public double NoteOpenAt = -1, NoteCloseAt = -1, ReadyAt = -1, NoteReadyAt = -1, FailedAt = -1, FolderGoneAt = -1, ZipAt = -1, PlaceholderAt = -1, ZipOnClipAt = -1;
    public readonly List<double> ToastOn = new List<double>(), ToastOff = new List<double>();
    public readonly List<string> ToastTexts = new List<string>(), NoteLines = new List<string>(), TrayNames = new List<string>(), ClipLog = new List<string>();
    uint seenSeq, readSeq;
    double seqAt = -1;
    int tick;

    public LWatch(uint pid, string folder)
    {
        Pid = pid;
        Folder = folder;
        Zip = folder == null ? null : folder + ".zip";
    }

    public double Now { get { return T.Elapsed.TotalSeconds; } }

    public void Start()
    {
        T.Restart();
        StartTicks = Stopwatch.GetTimestamp();
        seenSeq = readSeq = LN.GetClipboardSequenceNumber();
        FolderExists = Folder != null && Directory.Exists(Folder);
        ZipExists = Zip != null && File.Exists(Zip);
        Fg = LN.GetForegroundWindow();
        Ev("watch start: folder " + FolderExists + ", zip " + ZipExists);
    }

    public void Ev(string s) { LT.Say(string.Format(CultureInfo.InvariantCulture, "  +{0,7:0.000}s {1}", Now, s)); }

    public void Tick()
    {
        tick++;
        IntPtr nb = LT.FindWindow(Pid, NoteRules.WindowTitle);
        if (nb != Note)
        {
            if (nb != IntPtr.Zero) { Note = nb; LastNote = nb; NoteOpenAt = Now; NoteRect = LW.Of(nb).Frame; NoteLine = null; Ev("note box open " + LW.Of(nb)); }
            else { Ev("note box closed"); NoteCloseAt = Now; Note = IntPtr.Zero; }
        }
        if (Note != IntPtr.Zero)
        {
            string line = LU.ZipLine(Note);
            if (line != null && line != NoteLine) { NoteLine = line; NoteLines.Add(line); Ev("note line: " + line); Mark(line); }
        }
        IntPtr ts = LT.FindToast(Pid);
        if (ts != Toast)
        {
            if (ts != IntPtr.Zero) { Toast = ts; ToastRect = LW.Of(ts).Frame; ToastOn.Add(Now); ToastText = null; Ev("popup on " + LW.Of(ts) + ", foreground '" + LT.FgTitle() + "'"); }
            else { ToastOff.Add(Now); Ev("popup off"); Toast = IntPtr.Zero; }
        }
        if (Toast != IntPtr.Zero)
        {
            string t = LU.Texts(Toast);
            if (!string.IsNullOrEmpty(t) && t != ToastText) { ToastText = t; ToastTexts.Add(t); Ev("popup text: " + t); Mark(t); }
        }
        uint s = LN.GetClipboardSequenceNumber();
        if (s != seenSeq) { seenSeq = s; seqAt = Now; }
        if (seenSeq != readSeq && seqAt >= 0 && Now - seqAt > 0.12) { readSeq = seenSeq; ReadClip(); }
        if (Folder != null)
        {
            bool fe = Directory.Exists(Folder);
            if (fe != FolderExists) { FolderExists = fe; if (!fe) FolderGoneAt = Now; Ev("folder " + (fe ? "exists" : "gone")); }
            bool ze = File.Exists(Zip);
            if (ze != ZipExists) { ZipExists = ze; if (ze) ZipAt = Now; Ev("zip " + (ze ? "exists" : "gone")); }
        }
        if (tick % 4 == 1)
        {
            string tn = LT.TrayName();
            if (tn != TrayName) { TrayName = tn; TrayNames.Add(tn); Ev("tray: " + tn); }
        }
        IntPtr fg = LN.GetForegroundWindow();
        if (fg != Fg) { Fg = fg; Ev("foreground: '" + LW.Of(fg).Title + "'"); }
    }

    void Mark(string text)
    {
        if (text.StartsWith("ZIP ready") && ReadyAt < 0) ReadyAt = Now;
        if (text.StartsWith("ZIP with your note ready") && NoteReadyAt < 0) NoteReadyAt = Now;
        if (text.StartsWith("Could not make the ZIP") && FailedAt < 0) FailedAt = Now;
    }

    void ReadClip()
    {
        string text = LT.ClipText();
        string[] files = LT.ClipFiles() ?? new string[0];
        ClipText = text;
        ClipFiles = files;
        if (text == ZipStatus.ClipboardPlaceholder && PlaceholderAt < 0) PlaceholderAt = Now;
        if (files.Length == 1 && Zip != null && string.Equals(files[0], Zip, StringComparison.OrdinalIgnoreCase)) ZipOnClipAt = Now;
        string line = "clipboard: files [" + string.Join(", ", files.Select(Path.GetFileName).ToArray()) + "], text " + LT.Short(text);
        ClipLog.Add(line);
        Ev(line);
    }

    public bool WaitUntil(Func<bool> cond, int ms)
    {
        Stopwatch s = Stopwatch.StartNew();
        while (true)
        {
            Tick();
            if (cond()) return true;
            if (s.ElapsedMilliseconds > ms) return false;
            LT.Wait(60);
        }
    }

    public void Run(int ms) { WaitUntil(delegate { return false; }, ms); }

    public bool Finished { get { return ZipExists && !FolderExists && (ReadyAt >= 0 || NoteReadyAt >= 0); } }
}

sealed class LRec
{
    public string Id, Cmd, Folder, Mp4;
    public Rectangle Region;
}

static partial class LT
{
    public const string Repo = @"G:\CLAUDE CODE\Screen Vibe Recorder";
    public const string TestFolder = @"C:\Users\Bran\Recordings\live-test-2026-11-22";
    public static readonly string Scratch = Path.Combine(Path.GetTempPath(), "svr-live-all");
    public static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"DesktopRecorder\settings.txt");
    public static readonly string BackupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"DesktopRecorder\settings.backup-2026-11-22-live-all.txt");
    public static readonly string ChromeProfile = Path.Combine(Path.GetTempPath(), "dr_counter_profile");
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static string Out;
    public static LPad Pad;
    static StreamWriter log;
    static readonly List<string> rows = new List<string>();
    public static readonly Rectangle Region1 = Rectangle.FromLTRB(160, 140, 1760, 940);

    public static string Exe { get { return Path.Combine(Repo, "DesktopRecorder-zip.exe"); } }

    [STAThread]
    static int Main(string[] args)
    {
        bool dpi = LN.SetProcessDpiAwarenessContext(new IntPtr(-4));
        LN.SetThreadExecutionState(0x80000003);
        Application.EnableVisualStyles();
        string cmd = args.Length > 0 ? args[0] : "help";
        Out = Path.Combine(Scratch, "run-" + cmd + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Out);
        log = new StreamWriter(Path.Combine(Out, "log.txt"), true, new UTF8Encoding(false));
        Say("command '" + string.Join(" ", args) + "', DPI context set " + dpi + ", screen " + Screen.PrimaryScreen.Bounds + ", work area " + Screen.PrimaryScreen.WorkingArea);
        Pad = new LPad();
        Pad.Show();
        Wait(400);
        int code = 0;
        try
        {
            List<string> names = args.Skip(1).ToList();
            switch (cmd)
            {
                case "exit-tray": ExitViaTray(args[1], "setup-exit-installed"); break;
                case "mp4": SessionMp4(names); break;
                case "folder": SessionFolder(names); break;
                case "more": SessionMore(names); break;
                case "look": SessionLook(names); break;
                case "settings": SessionSettings(names); break;
                default: Say("unknown command"); code = 2; break;
            }
        }
        catch (Exception e)
        {
            Say("ERROR " + e);
            code = 1;
            if (RecordingCmd() != null) { LIn.Hotkey(); Say("sent the stop hotkey"); }
        }
        finally
        {
            int pass = rows.Count(r => r.Contains("\tPASS\t")), fail = rows.Count(r => r.Contains("\tFAIL\t"));
            Say("results: " + pass + " PASS, " + fail + " FAIL. Folder: " + Out);
            try { Pad.Close(); } catch (Exception) { }
            LN.SetThreadExecutionState(0x80000000);
            log.Close();
        }
        return code;
    }

    // ------------------------------------------------------------ basics

    public static void Say(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s;
        Console.WriteLine(line);
        if (log != null) { log.WriteLine(line); log.Flush(); }
    }

    public static void Result(string id, string did, string expected, string actual, bool pass, string shot)
    {
        string row = string.Join("\t", new string[] { id, did, expected, actual, pass ? "PASS" : "FAIL", shot ?? "" });
        rows.Add(row);
        Say((pass ? "PASS " : "FAIL ") + id + ": " + actual + (string.IsNullOrEmpty(shot) ? "" : " [" + shot + "]"));
        File.AppendAllText(Path.Combine(Out, "results.tsv"), row + "\n", new UTF8Encoding(false));
        File.AppendAllText(Path.Combine(Scratch, "results-all.tsv"), DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + row + "\n", new UTF8Encoding(false));
    }

    public static void Wait(int ms)
    {
        Stopwatch s = Stopwatch.StartNew();
        while (s.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(Math.Max(1, Math.Min(10, ms - (int)s.ElapsedMilliseconds)));
        }
    }

    public static bool WaitFor(Func<bool> cond, int ms)
    {
        Stopwatch s = Stopwatch.StartNew();
        while (true)
        {
            if (cond()) return true;
            if (s.ElapsedMilliseconds > ms) return false;
            Wait(50);
        }
    }

    public static string Short(string s)
    {
        if (s == null) return "(none)";
        s = s.Replace("\r", "").Replace("\n", " / ");
        return "'" + (s.Length > 140 ? s.Substring(0, 140) + "..." : s) + "'";
    }

    public static string Run(string exe, string args, int ms)
    {
        ProcessStartInfo i = new ProcessStartInfo(exe, args);
        i.UseShellExecute = false; i.RedirectStandardOutput = true; i.RedirectStandardError = true; i.CreateNoWindow = true;
        using (Process p = Process.Start(i))
        {
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(ms);
            return o + e;
        }
    }

    // ------------------------------------------------------------ windows, tray, clipboard

    public static Process App()
    {
        Process[] p = Process.GetProcessesByName("DesktopRecorder-zip");
        return p.Length > 0 ? p[0] : null;
    }

    public static uint AppPid() { Process p = App(); return p == null ? 0 : (uint)p.Id; }

    public static IntPtr FindWindow(uint pid, string title)
    {
        foreach (LW w in LW.List(pid, true)) if (w.Title == title) return w.H;
        return IntPtr.Zero;
    }

    public static IntPtr FindToast(uint pid)
    {
        foreach (LW w in LW.List(pid, true))
            if (w.Title == "Desktop Recorder" && w.Frame.Width > 50 && w.Frame.Width < 900 && w.Frame.Height < 300) return w.H;
        return IntPtr.Zero;
    }

    public static string FgTitle() { return LW.Of(LN.GetForegroundWindow()).Title; }

    public static string TrayName() { AutomationElement b = LU.TrayButton("Desktop Recorder:"); return b == null ? null : LU.Name(b); }

    public static string ClipText()
    {
        for (int i = 0; i < 3; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception) { Wait(40); }
        }
        return "(clipboard busy)";
    }

    public static string[] ClipFiles()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return new string[0];
                System.Collections.Specialized.StringCollection l = Clipboard.GetFileDropList();
                string[] a = new string[l.Count];
                l.CopyTo(a, 0);
                return a;
            }
            catch (Exception) { Wait(40); }
        }
        return null;
    }

    public static string RecordingCmd()
    {
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='ffmpeg.exe'"))
                foreach (ManagementObject o in s.Get())
                {
                    string c = o["CommandLine"] as string;
                    if (c != null && (c.Contains("ddagrab") || c.Contains("gdigrab"))) return c;
                }
        }
        catch (Exception) { }
        return null;
    }

    public static Rectangle ParseRegion(string cmd)
    {
        if (cmd == null) return Rectangle.Empty;
        Match x = Regex.Match(cmd, @"offset_x[=\s]+(-?\d+)"), y = Regex.Match(cmd, @"offset_y[=\s]+(-?\d+)"), s = Regex.Match(cmd, @"video_size[=\s]+(\d+)x(\d+)");
        if (!s.Success) return Rectangle.Empty;
        return new Rectangle(x.Success ? int.Parse(x.Groups[1].Value) : 0, y.Success ? int.Parse(y.Groups[1].Value) : 0, int.Parse(s.Groups[1].Value), int.Parse(s.Groups[2].Value));
    }

    public static string R(Rectangle r) { return string.Format(CultureInfo.InvariantCulture, "{0},{1} {2}x{3}", r.X, r.Y, r.Width, r.Height); }

    public static void FocusPad()
    {
        LIn.Click(Pad.BoxCenter());
        Wait(200);
        if (LN.GetForegroundWindow() != Pad.Handle) { LIn.Key(0x12, false); LIn.Key(0x12, true); LN.SetForegroundWindow(Pad.Handle); Wait(200); }
    }

    public static bool Front(IntPtr h)
    {
        for (int i = 0; i < 6; i++)
        {
            LN.ShowWindow(h, 3);
            LIn.Key(0x12, false); LIn.Key(0x12, true);
            LN.SetForegroundWindow(h);
            Wait(300);
            if (LN.GetForegroundWindow() == h) return true;
        }
        return false;
    }

    public static IntPtr PageWindow()
    {
        foreach (LW w in LW.List(0, true)) if (w.Cls == "Chrome_WidgetWin_1" && w.Title.StartsWith("Frame Counter")) return w.H;
        return IntPtr.Zero;
    }

    public static void OpenPage()
    {
        if (PageWindow() == IntPtr.Zero)
        {
            string uri = new Uri(Path.Combine(Repo, @"test\counter.html")).AbsoluteUri;
            Process.Start("chrome", "--user-data-dir=\"" + ChromeProfile + "\" --no-first-run --hide-crash-restore-bubble --new-window --start-maximized \"" + uri + "\"");
            if (!WaitFor(delegate { return PageWindow() != IntPtr.Zero; }, 15000)) throw new Exception("The counter page did not open.");
            Wait(1500);
        }
        FrontPage();
    }

    public static void FrontPage()
    {
        IntPtr h = PageWindow();
        if (h == IntPtr.Zero) throw new Exception("The counter page is not open.");
        if (!Front(h)) Say("WARNING: the counter page did not come to the front; foreground '" + FgTitle() + "'");
        LIn.Move(1900, 600);
        Wait(200);
    }

    public static Rectangle TaskbarRect()
    {
        foreach (LW w in LW.List(0, true)) if (w.Cls == "Shell_TrayWnd") return w.Rect;
        return Rectangle.Empty;
    }

    public static List<IntPtr> ExplorerWindows()
    {
        List<IntPtr> l = new List<IntPtr>();
        foreach (LW w in LW.List(0, true)) if (w.Cls == "CabinetWClass") l.Add(w.H);
        return l;
    }

    public static IntPtr WaitNewExplorer(List<IntPtr> before, int ms)
    {
        IntPtr found = IntPtr.Zero;
        WaitFor(delegate
        {
            foreach (IntPtr h in ExplorerWindows()) if (!before.Contains(h)) { found = h; return true; }
            return false;
        }, ms);
        return found;
    }

    public static List<string> ExplorerSelection(IntPtr hwnd, out string folder)
    {
        List<string> sel = new List<string>();
        folder = null;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic win in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(win.HWND) != hwnd.ToInt64()) continue;
                    folder = (string)win.Document.Folder.Self.Path;
                    foreach (dynamic item in win.Document.SelectedItems()) sel.Add((string)item.Path);
                }
                catch (Exception) { }
            }
        }
        catch (Exception e) { Say("shell: " + e.Message); }
        return sel;
    }

    public static void CloseWindow(IntPtr h) { LN.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); Wait(800); }

    // ------------------------------------------------------------ settings, app start and Exit

    public static Dictionary<string, string> ReadSettings()
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(SettingsPath))
        {
            int i = line.IndexOf('=');
            if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
        }
        return d;
    }

    public static void WriteSettings(Dictionary<string, string> overrides)
    {
        if (App() != null) throw new Exception("Stop the test exe before the settings change.");
        List<string> lines = File.ReadAllLines(BackupPath).ToList();
        Dictionary<string, string> o = new Dictionary<string, string>(overrides);
        o["folder"] = TestFolder;
        for (int i = 0; i < lines.Count; i++)
        {
            int k = lines[i].IndexOf('=');
            if (k <= 0) continue;
            string key = lines[i].Substring(0, k);
            if (o.ContainsKey(key)) { lines[i] = key + "=" + o[key]; o.Remove(key); }
        }
        foreach (KeyValuePair<string, string> kv in o) lines.Add(kv.Key + "=" + kv.Value);
        File.WriteAllText(SettingsPath, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
        Say("settings written: " + string.Join(", ", overrides.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
    }

    public static void StartApp()
    {
        if (App() != null) throw new Exception("The test exe runs already.");
        Directory.CreateDirectory(TestFolder);
        Process.Start(Exe);
        if (!WaitFor(delegate { return TrayName() != null; }, 20000)) throw new Exception("The tray icon of the test exe did not show.");
        Wait(1500);
        Say("test exe started, pid " + AppPid() + ", tray '" + TrayName() + "'");
    }

    // Opens the tray menu with a right-click on the tray icon. Returns the menu window.
    public static LW OpenTrayMenu(string prefix, uint pid)
    {
        AutomationElement b = LU.TrayButton(prefix);
        if (b == null) throw new Exception("No tray icon '" + prefix + "'.");
        List<IntPtr> before = LW.List(pid, true).Select(w => w.H).ToList();
        Point c = LU.Center(b);
        LIn.RightClick(c.X, c.Y);
        LW menu = null;
        WaitFor(delegate
        {
            foreach (LW w in LW.List(pid, true))
                if (!before.Contains(w.H) && w.Frame.Height > 60 && w.Frame.Width > 100) { menu = w; return true; }
            return false;
        }, 3000);
        if (menu == null) throw new Exception("The tray menu did not open.");
        Wait(250);
        return LW.Of(menu.H);
    }

    public static AutomationElement MenuItem(LW menu, string startsWith)
    {
        foreach (AutomationElement e in LU.All(menu.H, ControlType.MenuItem))
        {
            string n = LU.Name(e);
            if (n != null && n.StartsWith(startsWith, StringComparison.Ordinal)) return e;
        }
        return null;
    }

    public static string MenuItems(LW menu)
    {
        List<string> l = new List<string>();
        foreach (AutomationElement e in LU.All(menu.H, ControlType.MenuItem))
        {
            bool en = true;
            try { en = e.Current.IsEnabled; } catch (Exception) { }
            l.Add(LU.Name(e) + (en ? "" : " (off)"));
        }
        return string.Join(" | ", l.ToArray());
    }

    public static void ClickMenu(LW menu, string startsWith)
    {
        AutomationElement e = MenuItem(menu, startsWith);
        if (e == null) throw new Exception("No menu item '" + startsWith + "': " + MenuItems(menu));
        LIn.Click(LU.Center(e));
    }

    public static void ExitViaTray(string prefix, string id)
    {
        string procName = prefix.StartsWith("Desktop Recorder: stopped") ? "DesktopRecorder" : "DesktopRecorder-zip";
        Process[] ps = Process.GetProcessesByName(procName);
        if (ps.Length == 0) throw new Exception("No process " + procName + ".");
        uint appPid = (uint)ps[0].Id;
        LW menu = OpenTrayMenu(prefix, appPid);
        string items = MenuItems(menu);
        ClickMenu(menu, "Exit");
        bool gone = WaitFor(delegate { try { return Process.GetProcessById((int)appPid).HasExited; } catch (ArgumentException) { return true; } }, 120000);
        Result(id, "Tray menu > Exit (" + procName + ", pid " + appPid + ")", "The app ends", "menu: " + items + "; ended " + gone, gone, null);
    }

    // ------------------------------------------------------------ recordings

    public static bool WaitSelector(uint pid)
    {
        return WaitFor(delegate { return FindWindow(pid, "Select region") != IntPtr.Zero; }, 4000);
    }

    public static string[] Entries()
    {
        if (!Directory.Exists(TestFolder)) return new string[0];
        return Directory.GetFileSystemEntries(TestFolder);
    }

    // After the selector accepted: waits for FFmpeg and the new folder (folder mode) or file (MP4 mode).
    public static LRec Started(string id, string[] before)
    {
        LRec rec = new LRec();
        rec.Id = id;
        string cmd = null;
        WaitFor(delegate { cmd = RecordingCmd(); return cmd != null; }, 6000);
        rec.Cmd = cmd;
        rec.Region = ParseRegion(cmd);
        if (cmd == null) { Say(id + ": FFmpeg did not start"); return rec; }
        WaitFor(delegate
        {
            foreach (string e in Entries())
                if (!before.Contains(e) && Path.GetFileName(e).StartsWith("desktop_"))
                {
                    if (Directory.Exists(e)) { rec.Folder = e; return true; }
                    if (e.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { rec.Mp4 = e; return true; }
                }
            return false;
        }, 6000);
        Say(id + ": recording " + R(rec.Region) + ", folder " + rec.Folder + ", mp4 " + rec.Mp4);
        return rec;
    }

    public static LRec StartDrag(string id, Rectangle r)
    {
        uint pid = AppPid();
        FrontPage();
        string[] before = Entries();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception(id + ": the selector did not open.");
        Wait(400);
        LIn.Drag(r.Left, r.Top, r.Right - 1, r.Bottom - 1);
        LRec rec = Started(id, before);
        LIn.Move(1900, 600);
        return rec;
    }

    public static bool WaitRecordingEnd(int ms) { return WaitFor(delegate { return RecordingCmd() == null; }, ms); }

    // ------------------------------------------------------------ ZIP and video checks

    public sealed class ZipInfo
    {
        public bool Opened; public string Error; public List<string> Names = new List<string>(); public string Index; public int Frames; public bool Clip, Work, Srt; public long Bytes;
        public string Note { get { if (Index == null) return null; Match m = Regex.Match(Index, @"Note from Bran:\s*(.*)"); return m.Success ? m.Groups[1].Value.Trim() : null; } }
    }

    public static ZipInfo ReadZip(string path)
    {
        ZipInfo z = new ZipInfo();
        try
        {
            z.Bytes = new FileInfo(path).Length;
            using (ZipArchive a = ZipFile.OpenRead(path))
            {
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    z.Names.Add(e.FullName);
                    if (e.FullName.EndsWith("/index.md")) using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8)) z.Index = r.ReadToEnd();
                    if (e.FullName.Contains("/frames/") && e.FullName.EndsWith(".png")) z.Frames++;
                    if (e.FullName.EndsWith("/clip.mp4")) z.Clip = true;
                    if (e.FullName.Contains("work_lossless")) z.Work = true;
                    if (e.FullName.EndsWith("/transcript.srt")) z.Srt = true;
                }
            }
            z.Opened = true;
        }
        catch (Exception e) { z.Error = e.Message; }
        return z;
    }

    public static string ZipSummary(ZipInfo z)
    {
        if (!z.Opened) return "ZIP did not open: " + z.Error;
        return string.Format(CultureInfo.InvariantCulture, "ZIP {0:0.0} MB, {1} entries, index.md {2}, frames {3}, clip.mp4 {4}, work file {5}, note {6}",
            z.Bytes / 1048576.0, z.Names.Count, z.Index != null, z.Frames, z.Clip, z.Work, Short(z.Note));
    }

    public static string ExtractClip(string zip)
    {
        string dir = Path.Combine(Out, "clip_" + Path.GetFileNameWithoutExtension(zip));
        Directory.CreateDirectory(dir);
        string clip = Path.Combine(dir, "clip.mp4");
        using (ZipArchive a = ZipFile.OpenRead(zip))
            foreach (ZipArchiveEntry e in a.Entries) if (e.FullName.EndsWith("/clip.mp4")) e.ExtractToFile(clip, true);
        return clip;
    }

    public static Size Probe(string file, out string text)
    {
        text = Run("ffprobe", "-v error -select_streams v:0 -show_entries stream=width,height,codec_name,pix_fmt -show_entries format=duration -of default=nw=1 \"" + file + "\"", 60000).Trim();
        Match w = Regex.Match(text, @"width=(\d+)"), h = Regex.Match(text, @"height=(\d+)");
        text = text.Replace("\r", "").Replace("\n", " ");
        if (!w.Success || !h.Success) return Size.Empty;
        return new Size(int.Parse(w.Groups[1].Value), int.Parse(h.Groups[1].Value));
    }

    // ------------------------------------------------------------ sounds (WASAPI loopback of this PC)

    static LoopbackCapture loop;
    static string loopPath;

    public static bool LoopActive { get { return loop != null; } }

    public static string ShotCrop(string name, Rectangle r) { return LShot.ShotCrop(name, r); }

    public static void AudioStart(string id)
    {
        loopPath = Path.Combine(Out, id + "_loopback.wav");
        loop = new LoopbackCapture();
        string err = loop.Start(loopPath);
        if (err != null) { Say("loopback: " + err); loop = null; }
    }

    // Times (s, on the watcher clock) of the blocks with each tone. The watcher clock started at watchStartTicks.
    public static string AudioStop(long watchStartTicks, out double firstHigh)
    {
        firstHigh = -1;
        if (loop == null) return "no loopback";
        long start = loop.StartTimestamp;
        loop.Stop();
        loop = null;
        int rate;
        float[] x = ReadWavMono(loopPath, out rate);
        if (x == null || rate <= 0) return "loopback WAV not read";
        double offset = (start - watchStartTicks) / (double)Stopwatch.Frequency;
        double[] freqs = { 660, 880, 1047, 1568 };
        List<string>[] hits = new List<string>[4];
        double[] first = { -1, -1, -1, -1 };
        int n = 2048, hop = 1024;
        for (int f = 0; f < 4; f++) hits[f] = new List<string>();
        double lastHit = -10;
        for (int s = 0; s + n <= x.Length; s += hop)
        {
            double energy = 0;
            for (int i = 0; i < n; i++) energy += x[s + i] * x[s + i];
            double rms = Math.Sqrt(energy / n);
            if (rms < 0.001) continue;
            for (int f = 0; f < 4; f++)
            {
                double w = 2 * Math.PI * freqs[f] / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                for (int i = 0; i < n; i++) { double s0 = x[s + i] + c * s1 - s2; s2 = s1; s1 = s0; }
                double power = s1 * s1 + s2 * s2 - c * s1 * s2;
                double ratio = power / (energy * n / 2 + 1e-12);
                if (ratio > 0.25)
                {
                    double t = offset + (s + n / 2) / (double)rate;
                    if (first[f] < 0) first[f] = t;
                    if (t - lastHit > 0.3 || hits[f].Count == 0) hits[f].Add(t.ToString("0.00", CultureInfo.InvariantCulture));
                    lastHit = t;
                }
            }
        }
        firstHigh = first[3];
        StringBuilder b = new StringBuilder();
        for (int f = 0; f < 4; f++) b.AppendFormat(CultureInfo.InvariantCulture, "{0} Hz at [{1}] ", freqs[f], string.Join(", ", hits[f].Take(8).ToArray()));
        return b.ToString().Trim();
    }

    static float[] ReadWavMono(string path, out int rate)
    {
        rate = 0;
        try
        {
            byte[] b = File.ReadAllBytes(path);
            int pos = 12, channels = 0, bits = 0, tag = 0, dataPos = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    tag = BitConverter.ToUInt16(b, pos + 8); channels = BitConverter.ToUInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22);
                    if (tag == 0xFFFE && len >= 40) tag = BitConverter.ToUInt16(b, pos + 32);
                }
                else if (id == "data")
                {
                    dataPos = pos + 8;
                    dataLen = (len <= 0 || dataPos + len > b.Length) ? b.Length - dataPos : len;
                    break;
                }
                if (len < 0) break;
                pos += 8 + len + (len & 1);
            }
            if (dataPos < 0 || channels < 1 || bits < 16) return null;
            int bytes = bits / 8, frames = dataLen / (bytes * channels);
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = dataPos + (i * channels + c) * bytes;
                    if (tag == 3 && bits == 32) sum += BitConverter.ToSingle(b, p);
                    else if (bits == 16) sum += BitConverter.ToInt16(b, p) / 32768.0;
                    else if (bits == 32) sum += BitConverter.ToInt32(b, p) / 2147483648.0;
                }
                mono[i] = (float)(sum / channels);
            }
            return mono;
        }
        catch (Exception e) { Say("WAV read: " + e.Message); return null; }
    }
}
