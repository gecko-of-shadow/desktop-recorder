// live_ui_sessions.cs - the test scenarios of the live test (HANDOFF 7.22). Tools: live_ui_test.cs.
// Session "mp4": tray menu idle (7.20 item 1), selector keys S1 to S6, MP4-only mode (T6).
// Session "folder": note box, ZIP indication, clipboard, tray items (7.20 items 2 to 5 and 9 to 11, 13; T1 to T5, T7).
// Session "more": Exit with a note that has text (item 4), a failed ZIP (item 12).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

static partial class LT
{
    const string Tray = "Desktop Recorder:";
    static readonly string WhisperCli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\DesktopRecorder\whisper\whisper-cli.exe");
    static readonly string WhisperModel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\DesktopRecorder\whisper\models\ggml-large-v3-turbo-q5_0.bin");
    static string LastZip;

    static Rectangle Inflate(Rectangle r, int n) { r.Inflate(n, n); return r; }

    static string Hash(Bitmap b)
    {
        BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        b.UnlockBits(d);
        using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create()) return BitConverter.ToString(md5.ComputeHash(buf));
    }

    static string SavePrint(IntPtr h, string name)
    {
        if (h == IntPtr.Zero || !LN.IsWindow(h)) return "";
        using (Bitmap b = LShot.Print(h))
        {
            if (b == null) return "";
            double dark = LShot.DarkShare(b);
            string f = LShot.Save(b, name);
            Say("PrintWindow " + name + ": black share " + dark.ToString("0.00", CultureInfo.InvariantCulture));
            return f;
        }
    }

    static string FocusInfo(IntPtr note)
    {
        try
        {
            AutomationElement f = AutomationElement.FocusedElement;
            IntPtr h = new IntPtr(f.Current.NativeWindowHandle);
            IntPtr top = h == IntPtr.Zero ? IntPtr.Zero : LN.GetAncestor(h, 2);
            return f.Current.ControlType.ProgrammaticName.Replace("ControlType.", "") + (top == note ? " in the note box" : " in '" + LW.Of(top).Title + "'");
        }
        catch (Exception e) { return "focus unknown: " + e.Message; }
    }

    static bool WaitQuestion(LWatch w, bool shown, int ms)
    {
        return w.WaitUntil(delegate
        {
            if (w.Note == IntPtr.Zero) return !shown;
            bool q = LU.FindVisible(w.Note, ControlType.Button, NoteRules.AnswerDiscard) != null;
            return q == shown;
        }, ms);
    }

    static void ClickButton(IntPtr h, string name)
    {
        AutomationElement e = LU.FindVisible(h, ControlType.Button, name);
        if (e == null) throw new Exception("No visible button '" + name + "' in '" + LW.Of(h).Title + "'.\n" + LU.Dump(h));
        LIn.Click(LU.Center(e));
    }

    static void ClickEdit(IntPtr note)
    {
        AutomationElement e = LU.Edit(note);
        if (e == null) throw new Exception("No text box in the note box.");
        LIn.Click(LU.Center(e));
        Wait(200);
    }

    // Closes the note boxes that a failed scenario left open (Esc, then Discard if it asks).
    static void CloseNoteBoxes()
    {
        uint pid = AppPid();
        for (int i = 0; i < 4; i++)
        {
            IntPtr nb = FindWindow(pid, NoteRules.WindowTitle);
            if (nb == IntPtr.Zero) return;
            LN.SetForegroundWindow(nb);
            Wait(200);
            LIn.Press(0x1B);
            Wait(500);
            AutomationElement d = LU.FindVisible(nb, ControlType.Button, NoteRules.AnswerDiscard);
            if (d != null) { LIn.Click(LU.Center(d)); Wait(500); }
        }
    }

    static LWatch StopWatch(LRec rec, string how, bool audio)
    {
        if (audio) AudioStart(rec.Id);
        LWatch w = new LWatch(AppPid(), rec.Folder);
        w.Start();
        if (how == "hotkey") LIn.Hotkey();
        w.WaitUntil(delegate { return w.Note != IntPtr.Zero; }, 8000);
        return w;
    }

    static void FinalChecks(LRec rec, LWatch w, string expectNote, string id)
    {
        string zip = rec.Folder + ".zip";
        ZipInfo z = ReadZip(zip);
        if (z.Opened) LastZip = zip;
        string probe = "";
        Size sz = Size.Empty;
        if (z.Opened && z.Clip) sz = Probe(ExtractClip(zip), out probe);
        bool folderGone = !Directory.Exists(rec.Folder);
        string[] files = ClipFiles() ?? new string[0];
        string text = ClipText();
        bool clipOk = files.Length == 1 && string.Equals(files[0], zip, StringComparison.OrdinalIgnoreCase) && text != null && text.Contains(zip);
        bool noteOk = expectNote == null ? z.Note == null : z.Note == expectNote;
        bool order = w.NoteCloseAt < 0 || w.FolderGoneAt < 0 || w.FolderGoneAt >= w.NoteCloseAt;
        bool contentOk = z.Opened && z.Index != null && z.Frames > 0 && z.Clip && !z.Work;
        Result(id + "-zip", "ZIP after the note box closed", "index.md, frames, clip.mp4, no work file, note " + Short(expectNote) + ", folder deleted after the box closed",
            ZipSummary(z) + "; folder gone " + folderGone + string.Format(CultureInfo.InvariantCulture, " (box closed {0:0.0} s, folder gone {1:0.0} s)", w.NoteCloseAt, w.FolderGoneAt),
            contentOk && noteOk && folderGone && order, null);
        Result(id + "-clipboard", "Clipboard after ZIP ready", "Only the ZIP as a file drop, and text with the ZIP path",
            "files [" + string.Join(", ", files) + "], text " + Short(text), clipOk, null);
        Result(id + "-video", "clip.mp4 in the ZIP (ffprobe)", "size " + rec.Region.Width + "x" + rec.Region.Height, probe, !sz.IsEmpty && sz == rec.Region.Size, null);
    }

    static void TrayAnimation(LWatch w, string id)
    {
        AutomationElement b = LU.TrayButton(Tray);
        if (b == null) { Result(id + "-tray-icon", "Tray icon after Stop", "turning arc", "no tray button", false, null); return; }
        Rectangle br = LU.Bounds(b);
        Rectangle icon = new Rectangle(br.X + br.Width / 2 - 14, br.Y + br.Height / 2 - 14, 28, 28);
        List<Bitmap> frames = new List<Bitmap>();
        List<string> hashes = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            using (Bitmap s = LShot.Screen())
            {
                Bitmap c = LShot.Crop(s, icon);
                string h = Hash(c);
                if (!hashes.Contains(h)) frames.Add(c); else c.Dispose();
                hashes.Add(h);
            }
            w.Tick();
            Wait(40);
        }
        int distinct = hashes.Distinct().Count();
        string name = "";
        int n = Math.Min(8, frames.Count);
        if (n > 0)
        {
            using (Bitmap strip = new Bitmap(icon.Width * 4 * n, icon.Height * 4))
            {
                using (Graphics g = Graphics.FromImage(strip))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    for (int i = 0; i < n; i++) g.DrawImage(frames[i], new Rectangle(i * icon.Width * 4, 0, icon.Width * 4, icon.Height * 4));
                }
                name = LShot.Save(strip, id + "-tray-icon-frames");
            }
        }
        foreach (Bitmap f in frames) f.Dispose();
        Result(id + "-tray-icon", "Tray icon just after Stop: 16 crops in about 2 s", "The arc turns: 3 or more different frames", distinct + " different frames of 16 (tooltip '" + TrayName() + "')", distinct >= 3, name);
    }

    static void ExplorerPaste(string id, string zip)
    {
        string target = Path.Combine(Scratch, "paste-target-" + id);
        Directory.CreateDirectory(target);
        List<IntPtr> before = ExplorerWindows();
        Process.Start("explorer.exe", "\"" + target + "\"");
        IntPtr h = WaitNewExplorer(before, 8000);
        if (h == IntPtr.Zero) { Result(id + "-explorer-paste", "Ctrl+V in Explorer", "ZIP pasted", "no new Explorer window", false, null); return; }
        Wait(1500);
        LIn.Key(0x12, false); LIn.Key(0x12, true);
        LN.SetForegroundWindow(h);
        Wait(500);
        string expect = Path.Combine(target, Path.GetFileName(zip));
        LIn.Chord(0x11, 0x56);
        bool ok = WaitFor(delegate { return File.Exists(expect) && new FileInfo(expect).Length == new FileInfo(zip).Length; }, 8000);
        if (!ok)
        {
            LW ew = LW.Of(h);
            LIn.Click(ew.Frame.X + ew.Frame.Width - 120, ew.Frame.Y + ew.Frame.Height - 60);
            LIn.Chord(0x11, 0x56);
            ok = WaitFor(delegate { return File.Exists(expect) && new FileInfo(expect).Length == new FileInfo(zip).Length; }, 8000);
        }
        Result(id + "-explorer-paste", "Ctrl+V in a new Explorer window of a scratch folder", "The new ZIP pastes (same size)", ok ? "pasted " + Path.GetFileName(expect) : "not pasted, foreground '" + FgTitle() + "'", ok, null);
        CloseWindow(h);
    }

    // ============================================================ session mp4

    static void SessionMp4(List<string> names)
    {
        Dictionary<string, string> s = new Dictionary<string, string>();
        s["output_mode"] = "mp4";
        WriteSettings(s);
        StartApp();
        OpenPage();
        string[] order = { "A1", "S4", "S5", "S6", "S1", "S2", "S3", "STALE" };
        foreach (string n in order)
        {
            if (names.Count > 0 && !names.Contains(n)) continue;
            try
            {
                switch (n)
                {
                    case "A1": TrayIdle(); break;
                    case "S4": S4(); break;
                    case "S5": S5(); break;
                    case "S6": S6(); break;
                    case "S1": S1(); break;
                    case "S2": S2(); break;
                    case "S3": S3(); break;
                    case "STALE": StaleClick(); break;
                }
            }
            catch (Exception e)
            {
                Result(n + "-error", "scenario " + n, "no error", e.Message, false, null);
                if (RecordingCmd() != null) { LIn.Hotkey(); WaitRecordingEnd(10000); }
                if (FindWindow(AppPid(), "Select region") != IntPtr.Zero) LIn.Press(0x1B);
            }
            Wait(1000);
        }
        ExitViaTray(Tray, "A1-exit-mp4");
    }

    // A menu that stays open across a hotkey Stop (MP4 mode: no note box takes the focus) still shows
    // "Stop recording". A click on it must not open the selector (review finding, HANDOFF 7.22).
    static void StaleClick()
    {
        uint pid = AppPid();
        string[] before = Entries();
        LRec rec = StartDrag("STALE", Region1);
        Wait(2500);
        LW m = OpenTrayMenu(Tray, pid);
        string shown = MenuItems(m);
        LIn.Hotkey();
        bool ended = WaitRecordingEnd(10000);
        Wait(800);
        bool menuOpen = LN.IsWindowVisible(m.H);
        AutomationElement stop = MenuItem(m, "Stop recording");
        if (stop != null && menuOpen) LIn.Click(LU.Center(stop));
        bool selector = WaitFor(delegate { return FindWindow(pid, "Select region") != IntPtr.Zero; }, 2500);
        if (selector) LIn.Press(0x1B);
        if (LN.IsWindowVisible(m.H)) LIn.Press(0x1B);
        Result("A2-stale-stop", "MP4 mode: menu open during a recording, Stop with the hotkey, then click the old 'Stop recording'",
            "Nothing happens: no selector, no new recording", "menu at open: " + shown + "; FFmpeg ended " + ended + ", menu still open " + menuOpen + ", item found " + (stop != null) + ", selector opened " + selector,
            ended && menuOpen && stop != null && !selector, null);
        Wait(1500);
    }
    static void TrayIdle()
    {
        uint pid = AppPid();
        Rectangle bar = TaskbarRect();
        AutomationElement b = LU.TrayButton(Tray);
        Say("tray button " + R(LU.Bounds(b)) + " '" + LU.Name(b) + "', taskbar " + R(bar));
        LW m1 = OpenTrayMenu(Tray, pid);
        Point cur = LIn.Cursor();
        string items = MenuItems(m1);
        string shot1 = ShotCrop("A1-menu-first", Inflate(m1.Frame, 16));
        Say("menu " + m1 + "\n" + LU.Dump(m1.H));
        bool above = m1.Frame.Bottom <= bar.Top + 1;
        bool atPointer = cur.X >= m1.Frame.Left - 4 && cur.X <= m1.Frame.Right + 4;
        Result("A1-open-1", "Right-click the tray icon, first open after the start", "Menu above the taskbar, at the pointer",
            "menu " + R(m1.Frame) + ", taskbar top " + bar.Top + ", pointer " + cur.X + "," + cur.Y, above && atPointer, shot1);
        AutomationElement show = MenuItem(m1, "Show last ZIP in Explorer"), copy = MenuItem(m1, "Copy ZIP of last recording");
        if (show == null || copy == null) throw new Exception("A1: menu items not found: " + items);
        bool grayShow = !show.Current.IsEnabled, grayCopy = !copy.Current.IsEnabled;
        Result("A1-items", "Read the menu items (UI Automation)", "Status 'Not recording', record command with Win+Shift+D, Show and Copy ZIP off before a recording",
            items, items.Contains("Select region and record") && grayShow && grayCopy, shot1);
        AutomationElement open = MenuItem(m1, "Open recordings folder");
        LIn.Move(LU.Center(open).X, LU.Center(open).Y);
        Wait(500);
        string hover = ShotCrop("A1-menu-hover", Inflate(m1.Frame, 16));
        LIn.Click(LU.Center(show));
        Wait(800);
        bool stillOpen = LN.IsWindowVisible(m1.H);
        int explorers = ExplorerWindows().Count;
        LIn.Press(0x1B);
        Wait(500);
        bool closed = !LN.IsWindowVisible(m1.H);
        Result("A1-gray-click", "Click the gray 'Show last ZIP in Explorer', then Esc", "Nothing happens; Esc closes the menu",
            "menu still open " + stillOpen + ", Explorer windows " + explorers + ", closed by Esc " + closed, stillOpen && closed, hover);
        LIn.Move(1700, 700);
        Wait(300);
        LW m2 = OpenTrayMenu(Tray, pid);
        string shot2 = ShotCrop("A1-menu-second", Inflate(m2.Frame, 16));
        Result("A1-open-2", "Second right-click on the tray icon", "Same place and size; bottom above the taskbar", "menu " + R(m2.Frame) + " (first " + R(m1.Frame) + ")",
            m2.Frame.Bottom <= bar.Top + 1 && m2.Frame.Size == m1.Frame.Size, shot2);
        List<IntPtr> before = ExplorerWindows();
        ClickMenu(m2, "Open recordings folder");
        IntPtr h = WaitNewExplorer(before, 8000);
        string folder = null;
        if (h != IntPtr.Zero) { Wait(1500); ExplorerSelection(h, out folder); CloseWindow(h); }
        Result("A1-open-folder", "Tray menu > Open recordings folder", "Explorer opens the test folder", "window " + (h != IntPtr.Zero) + ", folder " + folder,
            h != IntPtr.Zero && string.Equals(folder, TestFolder, StringComparison.OrdinalIgnoreCase), null);
        LW m3 = OpenTrayMenu(Tray, pid);
        ClickMenu(m3, "Settings");
        IntPtr sw = IntPtr.Zero;
        WaitFor(delegate { sw = FindWindow(pid, "Desktop Recorder settings"); return sw != IntPtr.Zero; }, 5000);
        string sshot = "";
        if (sw != IntPtr.Zero)
        {
            Wait(800);
            sshot = ShotCrop("A1-settings-open", Inflate(LW.Of(sw).Frame, 8));
            ClickButton(sw, "Cancel");
            Wait(800);
        }
        bool gone = sw != IntPtr.Zero && !LN.IsWindowVisible(sw);
        Result("A1-settings-cancel", "Tray menu > Settings, then Cancel", "The Settings window opens and closes", "opened " + (sw != IntPtr.Zero) + ", closed " + gone, gone, sshot);
    }

    static void S4()
    {
        uint pid = AppPid();
        Dictionary<string, string> s0 = ReadSettings();
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S4: the selector did not open.");
        LIn.Move(960, 540);
        Wait(600);
        Bitmap a = LShot.Screen();
        LIn.Press(0x43); Wait(400); Bitmap b = LShot.Screen();
        LIn.Press(0x4D); Wait(400); Bitmap c = LShot.Screen();
        LIn.Press(0x50); Wait(400); Bitmap d = LShot.Screen();
        LIn.Press(0x43); LIn.Press(0x4D); LIn.Press(0x50); Wait(400); Bitmap e = LShot.Screen();
        LIn.Press(0x1B);
        Wait(800);
        Rectangle box;
        int row = LShot.Diff(a, b, new Rectangle(0, 538, 1920, 5), out box);
        int col = LShot.Diff(a, b, new Rectangle(958, 0, 5, 1080), out box);
        int mag = LShot.Diff(b, c, new Rectangle(0, 0, 1920, 1080), out box);
        Rectangle magBox = box;
        int pos = LShot.Diff(c, d, new Rectangle(0, 0, 1920, 1080), out box);
        Rectangle posBox = box;
        int back = LShot.Diff(a, e, new Rectangle(0, 0, 1920, 1080), out box);
        string fa = LShot.Save(LShot.Crop(a, new Rectangle(560, 300, 800, 520)), "S4-a-all-on");
        LShot.Save(LShot.Crop(b, new Rectangle(560, 300, 800, 520)), "S4-b-crosshair-off");
        LShot.Save(LShot.Crop(c, new Rectangle(560, 300, 800, 520)), "S4-c-magnifier-off");
        string fl = LShot.Save(LShot.Crop(c, new Rectangle(0, 0, 900, 140)), "S4-c-label-with-position");
        LShot.Save(LShot.Crop(d, new Rectangle(0, 0, 900, 140)), "S4-d-label-no-position");
        a.Dispose(); b.Dispose(); c.Dispose(); d.Dispose(); e.Dispose();
        Result("S4-C", "Selector open, crosshair on (Bran's setting); press C", "The crosshair goes away", "changed pixels: row 540 " + row + ", column 960 " + col, row > 800 && col > 400, fa);
        Result("S4-M", "Press M", "The magnifier goes away", "changed pixels " + mag + " in " + R(magBox), mag > 2000 && magBox.Left >= 960 - 20, null);
        Result("S4-P", "Press P", "The position text ('at x, y') goes away from the size label", "changed pixels " + pos + " in " + R(posBox), pos > 30, fl);
        Result("S4-CMP-back", "Press C, M and P again", "The picture is the same as at the start", "changed pixels " + back, back < 50, null);
        Dictionary<string, string> s1 = ReadSettings();
        // An odd number of presses: the app must save the new value, and the next selector must start with it.
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S4: the selector did not open (2).");
        LIn.Move(960, 540); Wait(400);
        LIn.Press(0x43); Wait(300);
        LIn.Press(0x1B); Wait(800);
        Dictionary<string, string> s2 = ReadSettings();
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S4: the selector did not open (3).");
        LIn.Move(960, 540); Wait(500);
        Bitmap f = LShot.Screen();
        LIn.Press(0x43); Wait(400);
        Bitmap g = LShot.Screen();
        LIn.Press(0x1B); Wait(800);
        int row2 = LShot.Diff(f, g, new Rectangle(0, 538, 1920, 5), out box);
        f.Dispose(); g.Dispose();
        Dictionary<string, string> s3 = ReadSettings();
        string v = "start " + s0["selector_crosshair"] + s0["selector_magnifier"] + s0["selector_position"] + ", after C M P twice " + s1["selector_crosshair"] + s1["selector_magnifier"] + s1["selector_position"]
            + ", after C once " + s2["selector_crosshair"] + ", after C again " + s3["selector_crosshair"] + "; next selector: C changed " + row2 + " pixels on row 540";
        Result("S4-saved", "Read selector_crosshair, selector_magnifier, selector_position in settings.txt", "C, M and P are saved: an odd number of presses flips the value",
            v, s1["selector_crosshair"] == s0["selector_crosshair"] && s2["selector_crosshair"] != s0["selector_crosshair"] && s3["selector_crosshair"] == s0["selector_crosshair"] && row2 > 800, null);
    }

    static void S5()
    {
        uint pid = AppPid();
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S5: the selector did not open.");
        LIn.Move(960, 540);
        Wait(400);
        Point p0 = LIn.Cursor();
        for (int i = 0; i < 3; i++) LIn.Press(0x27);
        Point p1 = LIn.Cursor();
        for (int i = 0; i < 2; i++) LIn.Press(0x28);
        Point p2 = LIn.Cursor();
        LIn.Key(0x10, false); LIn.Press(0x27); LIn.Key(0x10, true);
        Point p3 = LIn.Cursor();
        LIn.Key(0x10, false); LIn.Press(0x28); LIn.Key(0x10, true);
        Point p4 = LIn.Cursor();
        LIn.Key(0x10, false); LIn.Press(0x25); LIn.Key(0x10, true);
        LIn.Press(0x26);
        Point p5 = LIn.Cursor();
        Wait(400);
        string shot = ShotCrop("S5-magnifier", new Rectangle(p5.X - 30, p5.Y - 30, 360, 360));
        bool ok = p1 == new Point(p0.X + 3, p0.Y) && p2 == new Point(p1.X, p1.Y + 2) && p3 == new Point(p2.X + 10, p2.Y) && p4 == new Point(p3.X, p3.Y + 10)
            && p5 == new Point(p4.X - 10, p4.Y - 1);
        Result("S5-cursor", "Selector, no drag: Right x3, Down x2, Shift+Right, Shift+Down, Shift+Left, Up", "Cursor steps 1 px, and 10 px with Shift",
            string.Format("{0} > {1} > {2} > {3} > {4} > {5}", P(p0), P(p1), P(p2), P(p3), P(p4), P(p5)), ok, shot);
        string[] before = Entries();
        LIn.DragOpen(600, 300, 900, 500);
        for (int i = 0; i < 5; i++) LIn.Press(0x27);
        for (int i = 0; i < 3; i++) LIn.Press(0x28);
        Wait(300);
        Point pe = LIn.Cursor();
        string shot2 = ShotCrop("S5-drag-label", new Rectangle(560, 230, 600, 330));
        LIn.LeftUp();
        LRec rec = Started("S5", before);
        LIn.Move(1900, 600);
        Result("S5-drag-arrows", "Drag 600,300 to 900,500, then Right x5 and Down x3 with the button down, then release", "Cursor at 905,503; region 600,300 306x204",
            "cursor " + P(pe) + ", FFmpeg region " + R(rec.Region), pe == new Point(905, 503) && rec.Region == new Rectangle(600, 300, 306, 204), shot2);
        Wait(1500);
        StopMp4(rec, "S5", new Rectangle(600, 300, 306, 204), before);
    }

    static string P(Point p) { return p.X + "," + p.Y; }

    static string StatusName(LW menu)
    {
        foreach (AutomationElement e in LU.All(menu.H, ControlType.Button)) return LU.Name(e) ?? "";
        return "";
    }

    static void StopMp4(LRec rec, string id, Rectangle expected, string[] before)
    {
        LIn.Hotkey();
        bool ended = WaitRecordingEnd(15000);
        Wait(2000);
        bool noteBox = FindWindow(AppPid(), NoteRules.WindowTitle) != IntPtr.Zero;
        string probe = "";
        Size sz = rec.Mp4 != null ? Probe(rec.Mp4, out probe) : Size.Empty;
        List<string> added = Entries().Where(e => !before.Contains(e)).Select(Path.GetFileName).ToList();
        string[] files = ClipFiles() ?? new string[0];
        bool clipOk = files.Length == 1 && rec.Mp4 != null && string.Equals(files[0], rec.Mp4, StringComparison.OrdinalIgnoreCase);
        bool onlyMp4 = added.Count == 1 && added[0].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        Result(id + "-video", "Stop at once; ffprobe of the MP4", "video " + expected.Width + "x" + expected.Height, "FFmpeg ended " + ended + ", " + probe, !sz.IsEmpty && sz == expected.Size, null);
        Result("T6-" + id, "MP4 only mode: new files and the clipboard after Stop", "One MP4, no folder, no ZIP, no note box; the MP4 on the clipboard",
            "new [" + string.Join(", ", added.ToArray()) + "], note box " + noteBox + ", clipboard [" + string.Join(", ", files.Select(Path.GetFileName).ToArray()) + "]",
            onlyMp4 && !noteBox && clipOk, null);
    }

    static void S6()
    {
        uint pid = AppPid();
        Dictionary<string, string> s0 = ReadSettings();
        string[] before = Entries();
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S6: the selector did not open.");
        LIn.DragOpen(500, 300, 800, 500);
        LIn.Press(0x1B);
        Wait(400);
        bool closed1 = FindWindow(pid, "Select region") == IntPtr.Zero;
        LIn.LeftUp();
        Wait(1500);
        bool noRec1 = RecordingCmd() == null;
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S6: the selector did not open (2).");
        LIn.Move(960, 540);
        Wait(300);
        LIn.Press(0x1B);
        Wait(400);
        bool closed2 = FindWindow(pid, "Select region") == IntPtr.Zero;
        Wait(1200);
        bool noRec2 = RecordingCmd() == null;
        Dictionary<string, string> s1 = ReadSettings();
        bool same = s0["region"] == s1["region"] && s0["last_region"] == s1["last_region"];
        bool noFiles = Entries().Length == before.Length;
        Result("S6", "Esc during a drag, and Esc with no drag", "The selector closes; no recording; no new file; region and last_region not changed",
            "closed " + closed1 + "/" + closed2 + ", no FFmpeg " + noRec1 + "/" + noRec2 + ", no new file " + noFiles + ", region '" + s1["region"] + "' last_region '" + s1["last_region"] + "' (before '" + s0["region"] + "', '" + s0["last_region"] + "')",
            closed1 && closed2 && noRec1 && noRec2 && same && noFiles, null);
    }

    static void S1()
    {
        uint pid = AppPid();
        FrontPage();
        string[] before = Entries();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S1: the selector did not open.");
        LIn.Move(960, 540);
        Wait(500);
        string shot = ShotCrop("S1-before-tilde", new Rectangle(0, 0, 900, 140));
        LIn.Press(0xC0);
        LRec rec = Started("S1", before);
        LIn.Move(1900, 600);
        Result("S1-tilde", "Selector open, press ~ (VK 0xC0)", "Recording of the active monitor: 0,0 1920x1080", "FFmpeg region " + R(rec.Region), rec.Region == new Rectangle(0, 0, 1920, 1080), shot);
        Wait(1500);
        StopMp4(rec, "S1", new Rectangle(0, 0, 1920, 1080), before);
    }

    static void S2()
    {
        uint pid = AppPid();
        FrontPage();
        string[] before = Entries();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S2: the selector did not open.");
        LIn.Move(500, 300); Wait(200);
        LIn.LeftDown(); Wait(80);
        LIn.Key(0x10, false);
        LIn.MoveSteps(500, 300, 900, 500, 12);
        Wait(300);
        string shot = ShotCrop("S2-before-release", new Rectangle(440, 230, 560, 560));
        LIn.LeftUp(); Wait(150);
        LIn.Key(0x10, true);
        LRec rec = Started("S2", before);
        LIn.Move(1900, 600);
        Result("S2-shift-square", "Drag 500,300 to 900,500 with Shift held", "A square: 500,300 400x400", "FFmpeg region " + R(rec.Region), rec.Region == new Rectangle(500, 300, 400, 400), shot);
        Wait(1500);
        StopMp4(rec, "S2", new Rectangle(500, 300, 400, 400), before);
    }

    static void S3()
    {
        uint pid = AppPid();
        FrontPage();
        string[] before = Entries();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("S3: the selector did not open.");
        LIn.DragOpen(500, 300, 800, 500);
        LIn.Key(0x11, false); Wait(100);
        LIn.MoveSteps(800, 500, 1000, 600, 10);
        Wait(300);
        string shot = ShotCrop("S3-before-release", new Rectangle(440, 230, 700, 450));
        LIn.LeftUp(); Wait(150);
        LIn.Key(0x11, true);
        LRec rec = Started("S3", before);
        LIn.Move(1900, 600);
        Result("S3-ctrl-move", "Drag 500,300 to 800,500, hold Ctrl, move to 1000,600", "The new rectangle moves by +200,+100: 700,400 300x200", "FFmpeg region " + R(rec.Region),
            rec.Region == new Rectangle(700, 400, 300, 200), shot);
        Wait(1500);
        StopMp4(rec, "S3", new Rectangle(700, 400, 300, 200), before);
    }

    // ============================================================ session folder

    static void SessionFolder(List<string> names)
    {
        Dictionary<string, string> s = new Dictionary<string, string>();
        s["output_mode"] = "folder";
        s["whisper_path"] = WhisperCli;
        s["whisper_model"] = WhisperModel;
        WriteSettings(s);
        StartApp();
        OpenPage();
        string[] order = { "R13", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10" };
        foreach (string n in order)
        {
            if (names.Count > 0 && !names.Contains(n)) continue;
            if (App() == null) StartApp();
            try
            {
                switch (n)
                {
                    case "R13": R13(); break;
                    case "R1": R1(); break;
                    case "R2": R2(); break;
                    case "R3": R3(); break;
                    case "R4": R4(); break;
                    case "R5": R5(); break;
                    case "R6": R6(); break;
                    case "R7": R7(); break;
                    case "R8": R8(); break;
                    case "R9": R9(); break;
                    case "R10": R10(); break;
                }
            }
            catch (Exception e)
            {
                Result(n + "-error", "scenario " + n, "no error", e.Message, false, null);
                if (RecordingCmd() != null) { LIn.Hotkey(); WaitRecordingEnd(10000); Wait(1500); }
                CloseNoteBoxes();
                if (loopActive()) { double x; AudioStop(Stopwatch.GetTimestamp(), out x); }
            }
            Wait(1500);
        }
        if (App() != null) ExitViaTray(Tray, "A1-exit-folder");
    }

    static bool loopActive() { return LoopActive; }

    // Diagnosis of the stale tray menu (HANDOFF 7.22): the status name while the menu is open during the ZIP work.
    static void R13()
    {
        uint pid = AppPid();
        for (int round = 0; round < 2; round++)
        {
            LRec rec = StartDrag("R13-" + round, Region1);
            Wait(12000);
            LWatch w = new LWatch(pid, rec.Folder);
            w.Start();
            if (round == 0) LIn.Hotkey();
            else
            {
                LW m0 = OpenTrayMenu(Tray, pid);
                Say("  menu during the recording: " + Short(StatusName(m0)));
                ClickMenu(m0, "Stop recording");
            }
            w.WaitUntil(delegate { return w.Note != IntPtr.Zero; }, 8000);
            w.Run(400);
            LW m = OpenTrayMenu(Tray, pid);
            string last = null;
            List<string> seen = new List<string>();
            for (int i = 0; i < 30; i++)
            {
                string n = StatusName(m);
                if (n != last) { seen.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0}s {1}", w.Now, Short(n))); last = n; }
                w.Tick();
                Wait(80);
            }
            string shot = ShotCrop("R13-menu-" + round, Inflate(m.Frame, 12));
            LIn.Press(0x1B);
            Wait(300);
            Result("R13-" + round, (round == 0 ? "Stop with the hotkey" : "Stop with the tray menu") + "; open the tray menu during the ZIP work; read the status about each 0.1 s",
                "The busy status first", string.Join(" >> ", seen.ToArray()), seen.Count > 0 && seen[0].Contains("Making the ZIP"), shot);
            w.WaitUntil(delegate { return w.Finished; }, 40000);
            CloseNoteBoxes();
            w.WaitUntil(delegate { return w.Finished; }, 20000);
            Wait(1000);
        }
    }

    static void R1()
    {
        LRec rec = StartDrag("R1", Region1);
        Wait(6000);
        LWatch w = StopWatch(rec, "hotkey", true);
        if (w.Note == IntPtr.Zero) throw new Exception("R1: no note box.");
        LW nb = LW.Of(w.Note);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int dx = nb.Frame.X + nb.Frame.Width / 2 - (wa.X + wa.Width / 2), dy = nb.Frame.Y + nb.Frame.Height / 2 - (wa.Y + wa.Height / 2);
        bool fg = LN.GetForegroundWindow() == w.Note;
        string focus = FocusInfo(w.Note);
        Result("A3-position", "Note box after Stop", "Middle of the work area", "box " + R(nb.Frame) + ", offset from the middle " + dx + "," + dy, Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2, null);
        Result("A3-focus", "Note box after Stop", "The box is in front and the text box has the focus", "foreground note box " + fg + ", focus: " + focus, fg && (focus.StartsWith("Edit in the note box") || focus.StartsWith("Document in the note box")), null);
        LIn.Type("T1 note typed at once");
        string val = LU.EditValue(w.Note);
        Result("A3-type", "Type at once (no click)", "The text is in the note box", Short(val), val == "T1 note typed at once", null);
        uint aff;
        LN.GetWindowDisplayAffinity(w.Note, out aff);
        Result("A13-affinity-note", "GetWindowDisplayAffinity of the note box", "0x11 (WDA_EXCLUDEFROMCAPTURE)", "0x" + aff.ToString("X"), aff == 0x11, null);
        string pw = SavePrint(w.Note, "R1-note-printwindow");
        string bb = ShotCrop("R1-note-bitblt", nb.Frame);
        LIn.Press(0x0D);
        w.WaitUntil(delegate { return w.Note == IntPtr.Zero; }, 3000);
        bool beforeReady = w.ReadyAt < 0 && w.NoteReadyAt < 0;
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        w.Run(1500);
        double hi;
        string audio = AudioStop(w.StartTicks, out hi);
        double ready = w.NoteReadyAt >= 0 && (w.ReadyAt < 0 || w.NoteReadyAt < w.ReadyAt) ? w.NoteReadyAt : w.ReadyAt;
        Result("R1-saved-while-busy", "Enter in the note box before the ZIP was ready", "The note is saved; the ZIP has it", "Enter before ready " + beforeReady + "; popup texts: " + string.Join(" > ", w.ToastTexts.ToArray()), true, pw);
        FinalChecks(rec, w, "T1 note typed at once", "T1");
        Result("A9-sound-R1", "WASAPI loopback from Stop to the end", "The ready sound (1047 then 1568 Hz) near 'ready' (" + ready.ToString("0.0", CultureInfo.InvariantCulture) + " s)", audio,
            hi >= 0 && Math.Abs(hi - ready) < 1.5, null);
    }

    static void R2()
    {
        uint pid = AppPid();
        LRec rec = StartDrag("R2", Region1);
        Wait(3000);
        LW m = OpenTrayMenu(Tray, pid);
        string st1 = StatusName(m);
        Bitmap a = LShot.Screen();
        string s1 = LShot.Save(LShot.Crop(a, Inflate(m.Frame, 12)), "A2-menu-recording-1");
        Wait(2100);
        string st2 = StatusName(m);
        Bitmap b = LShot.Screen();
        string s2 = LShot.Save(LShot.Crop(b, Inflate(m.Frame, 12)), "A2-menu-recording-2");
        Rectangle box;
        int changed = LShot.Diff(a, b, new Rectangle(m.Frame.X, m.Frame.Y, m.Frame.Width, m.Frame.Height / 4), out box);
        a.Dispose(); b.Dispose();
        Result("A2-time", "Tray menu open during a recording; two readings 2 s apart", "'Recording 0:0x' counts while the menu stays open (screen and screen reader name)",
            Short(st1) + " >> " + Short(st2) + "; changed pixels in the status area " + changed, st1 != st2 && st2.StartsWith("Recording") && changed > 10 && MenuItem(m, "Stop recording") != null, s1 + ", " + s2);
        Wait(14000);
        AudioStart("R2");
        LWatch w = new LWatch(pid, rec.Folder);
        w.Start();
        ClickMenu(m, "Stop recording");
        w.WaitUntil(delegate { return w.Note != IntPtr.Zero; }, 8000);
        Result("A2-stop", "Tray menu > Stop recording", "The recording stops; the note box opens", "FFmpeg capture runs " + (RecordingCmd() != null) + ", note box " + (w.Note != IntPtr.Zero), w.Note != IntPtr.Zero, null);
        TrayAnimation(w, "A9");
        if (w.ReadyAt < 0)
        {
            LW m2 = OpenTrayMenu(Tray, pid);
            string b1 = StatusName(m2);
            string s3 = ShotCrop("A9-menu-busy", Inflate(m2.Frame, 12));
            w.WaitUntil(delegate { return w.ReadyAt >= 0; }, 40000);
            w.Run(700);
            string b2 = StatusName(m2);
            string s4 = ShotCrop("A9-menu-ready-while-open", Inflate(m2.Frame, 12));
            LIn.Press(0x1B);
            Wait(300);
            Result("A9-tray-busy", "Tray menu open while the ZIP is made, and still open when it is ready",
                "Busy: 'Making the ZIP... (step: ...)' and 'Wait for \"ZIP ready\"'; then 'Not recording' with the hotkey line, never a mix",
                Short(b1) + " >> " + Short(b2), b1.StartsWith("Making the ZIP") && b1.Contains("Wait for") && b2.StartsWith("Not recording") && !b2.Contains("Wait for"), s3 + ", " + s4);
        }
        w.WaitUntil(delegate { return w.ReadyAt >= 0; }, 40000);
        w.Run(500);
        string ready = w.NoteLines.LastOrDefault() ?? "";
        Result("A9-note-ready", "Note box line after the ZIP is ready", "'ZIP ready. Press Ctrl+V to paste (... MB).'", ready, ready.StartsWith("ZIP ready. Press Ctrl+V to paste ("), SavePrint(w.Note, "A9-note-ready-printwindow"));
        List<string> steps = w.NoteLines;
        string[] want = { ZipStatus.BusyText(ZipStep.Stopping), ZipStatus.BusyText(ZipStep.Frames), ZipStatus.BusyText(ZipStep.Transcript), ZipStatus.BusyText(ZipStep.Zip) };
        int last = -1;
        bool inOrder = true;
        foreach (string st in steps) { int k = Array.IndexOf(want, st); if (k < 0) continue; if (k < last) inOrder = false; last = k; }
        Result("A9-steps", "Note box line during the work (read about each 0.1 s)", "stopping > frames > transcript > ZIP > ready, in this order (a short step can be missed)", string.Join(" > ", steps.ToArray()),
            inOrder && steps.Contains(want[1]) && steps.Count > 0 && steps[steps.Count - 1].StartsWith("ZIP ready"), null);
        ExplorerPaste("A9", rec.Folder + ".zip");
        bool stays = w.Note != IntPtr.Zero && Directory.Exists(rec.Folder);
        Result("T3-folder-stays", "Note box open for more than 10 s after ZIP ready", "The folder stays while the box is open", "box open " + (w.Note != IntPtr.Zero) + ", folder " + Directory.Exists(rec.Folder) + string.Format(CultureInfo.InvariantCulture, " at {0:0.0} s (ready {1:0.0} s)", w.Now, w.ReadyAt),
            stays && w.Now - w.NoteOpenAt > 10, null);
        ClickEdit(w.Note);
        LIn.Type("T3 note after ZIP ready");
        LIn.Press(0x0D);
        w.WaitUntil(delegate { return w.NoteReadyAt >= 0 && !w.FolderExists && w.ZipExists; }, 60000);
        w.Run(1500);
        double hi;
        string audio = AudioStop(w.StartTicks, out hi);
        FinalChecks(rec, w, "T3 note after ZIP ready", "T3");
        Result("A11-late-note", "Save a note after 'ZIP ready'", "The ZIP is made again at the same path and has the note; the popup says 'ZIP with your note ready'",
            string.Join(" > ", w.ToastTexts.ToArray()) + (w.ToastTexts.Any(t => t.StartsWith(ZipStatus.BusyText(ZipStep.Zip))) ? "" : " (the busy 'ZIP' step was too short to see)"),
            w.NoteReadyAt >= 0 && File.Exists(rec.Folder + ".zip"), null);
        Result("A9-sound", "WASAPI loopback from Stop to the end", string.Format(CultureInfo.InvariantCulture, "1568 Hz near ready ({0:0.0} s) and near the note ZIP ({1:0.0} s)", w.ReadyAt, w.NoteReadyAt), audio,
            hi >= 0 && Math.Abs(hi - w.ReadyAt) < 1.5, null);
        Result("A9-tooltip", "Tray tooltip during the work", "'Desktop Recorder: making the ZIP (...)'", string.Join(" > ", w.TrayNames.ToArray()), w.TrayNames.Any(t => t != null && t.Contains("Desktop Recorder: making the ZIP")), null);
    }

    static void R3()
    {
        LRec rec = StartDrag("R3", Region1);
        Wait(6000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R3: no note box.");
        double open = w.NoteOpenAt;
        LIn.Press(0x1B);
        bool closed = w.WaitUntil(delegate { return w.Note == IntPtr.Zero; }, 1500);
        double closeDelay = w.NoteCloseAt - open;
        bool busy = w.WaitUntil(delegate { return w.Toast != IntPtr.Zero && w.ToastText != null; }, 3000) && w.ToastText.StartsWith("Making the ZIP");
        string busyShot = busy ? SavePrint(w.Toast, "A10-popup-busy-printwindow") : "";
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        w.Run(1000);
        Result("A4-esc-empty", "Esc in the empty note box", "The box closes at once, no question", "closed " + closed + string.Format(CultureInfo.InvariantCulture, " after {0:0.00} s", closeDelay), closed && closeDelay < 1.5, null);
        Result("A10-busy-popup", "Note box closed before the ZIP is ready", "The busy popup shows", "popup texts: " + string.Join(" > ", w.ToastTexts.ToArray()), busy, busyShot);
        FinalChecks(rec, w, null, "T2");
    }

    static void R4()
    {
        LRec rec = StartDrag("R4", Region1);
        Wait(30000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R4: no note box.");
        ClickButton(w.Note, NoteRules.NoNoteText);
        w.WaitUntil(delegate { return w.Note == IntPtr.Zero && w.PlaceholderAt >= 0; }, 3000);
        Pad.Box.Text = "";
        FocusPad();
        LIn.Chord(0x11, 0x56);
        Wait(200);
        string pasted = Pad.Box.Text;
        w.Tick();
        bool busyAtPaste = w.ReadyAt < 0 && w.ZipOnClipAt < 0;
        Result("A9-early-paste", "Ctrl+V in an Edit control just after Stop (ZIP busy " + busyAtPaste + " at the paste)", "The wait text pastes, not the ZIP of the recording before", Short(pasted),
            busyAtPaste && pasted == ZipStatus.ClipboardPlaceholder, null);
        bool busy = w.WaitUntil(delegate { return w.Toast != IntPtr.Zero && w.ToastText != null; }, 3000) && w.ToastText.StartsWith("Making the ZIP");
        string busyShot = busy ? SavePrint(w.Toast, "A10-popup-busy-printwindow") : "";
        bool clicked = false, closedByClick = false;
        int n0 = 0;
        double clickAt = 1e9;
        if (busy && w.ReadyAt < 0)
        {
            Rectangle tr = w.ToastRect;
            LIn.Click(tr.X + tr.Width / 2, tr.Y + tr.Height / 2);
            clicked = true;
            clickAt = w.Now;
            closedByClick = w.WaitUntil(delegate { return w.Toast == IntPtr.Zero; }, 2000);
            n0 = w.ToastTexts.Count;
            FocusPad();
            Pad.Box.Text = "";
        }
        bool popup = w.WaitUntil(delegate { return w.Toast != IntPtr.Zero && w.ToastText != null && w.ToastText.StartsWith("ZIP ready"); }, 40000);
        double on = w.ToastOn.Count > 0 ? w.ToastOn[w.ToastOn.Count - 1] : -1;
        bool fgAtShow = LN.GetForegroundWindow() == Pad.Handle;
        LIn.Type("abc");
        bool fgPad = LN.GetForegroundWindow() == Pad.Handle;
        string typed = Pad.Box.Text;
        uint aff = 0;
        int exStyle = 0;
        if (w.Toast != IntPtr.Zero) { LN.GetWindowDisplayAffinity(w.Toast, out aff); exStyle = LN.GetWindowLong(w.Toast, -20); }
        string toastShot = SavePrint(w.Toast, "A9-popup-ready-printwindow");
        Rectangle wa = Screen.PrimaryScreen.WorkingArea, tr2 = w.ToastRect;
        int cx = tr2.X + tr2.Width / 2 - (wa.X + wa.Width / 2), gap = wa.Bottom - tr2.Bottom;
        w.WaitUntil(delegate { return w.Toast == IntPtr.Zero; }, 12000);
        double dur = w.Now - on;
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        w.Run(1000);
        List<string> after = w.ToastTexts.Skip(n0).ToList();
        int onsAfter = w.ToastOn.Count(t => t > clickAt);
        bool backOnlyReady = after.Count > 0 && after[0].StartsWith("ZIP ready") && !after.Any(t => t.StartsWith("Making the ZIP"));
        Result("A10-click", "'No note' before the ZIP is ready; click the busy popup", "The popup closes, and comes back only with 'ZIP ready'",
            "busy popup " + busy + ", clicked " + clicked + ", closed " + closedByClick + ", popups after the click " + onsAfter + ": " + string.Join(" > ", after.ToArray()),
            busy && clicked && closedByClick && backOnlyReady, busyShot);
        Result("A9-popup", "Ready popup while an Edit control of another window has the focus; type 'abc' while it shows",
            "Bottom middle of the work area, about 4 s, does not take the focus, out of the capture",
            string.Format(CultureInfo.InvariantCulture, "popup {0}, centre offset {1}, gap {2} px, shown {3:0.0} s, focus at show {4}, after typing {5}, typed '{6}', affinity 0x{7:X}, WS_EX_NOACTIVATE {8}", R(tr2), cx, gap, dur, fgAtShow, fgPad, typed, aff, (exStyle & 0x08000000) != 0),
            popup && fgAtShow && fgPad && typed == "abc" && Math.Abs(cx) <= 2 && gap >= 0 && gap < 80 && dur > 3 && dur < 6 && aff == 0x11, toastShot);
        FinalChecks(rec, w, null, "A10");
    }

    static void R5()
    {
        LRec rec = StartDrag("R5", Region1);
        Wait(6000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R5: no note box.");
        LIn.Type("T7 text to discard");
        LIn.Press(0x1B);
        bool q = WaitQuestion(w, true, 2500);
        string texts = LU.Texts(w.Note);
        string qshot = SavePrint(w.Note, "T7-question-printwindow");
        ClickButton(w.Note, NoteRules.AnswerBack);
        bool qGone = WaitQuestion(w, false, 2500);
        string kept = LU.EditValue(w.Note);
        Result("T7-back", "Type text, Esc, then Back", "The question shows; Back returns to the text, the text stays",
            "question " + q + " (" + Short(texts) + "), back " + qGone + ", text " + Short(kept), q && qGone && kept == "T7 text to discard" && texts.Contains(NoteRules.Question), qshot);
        w.WaitUntil(delegate { return w.ReadyAt >= 0; }, 40000);
        w.Run(1000);
        bool folderWhileOpen = Directory.Exists(rec.Folder);
        ClickEdit(w.Note);
        LIn.Press(0x1B);
        bool q2 = WaitQuestion(w, true, 2500);
        ClickButton(w.Note, NoteRules.AnswerDiscard);
        w.WaitUntil(delegate { return w.Note == IntPtr.Zero; }, 2500);
        w.WaitUntil(delegate { return !w.FolderExists; }, 20000);
        w.Run(800);
        Result("T7-discard", "Esc again, then Discard (after ZIP ready)", "The box closes; the folder stays until the box closes, then goes",
            "question " + q2 + ", folder while the box was open " + folderWhileOpen + string.Format(CultureInfo.InvariantCulture, ", box closed {0:0.0} s, folder gone {1:0.0} s", w.NoteCloseAt, w.FolderGoneAt),
            q2 && folderWhileOpen && w.NoteCloseAt > 0 && w.FolderGoneAt >= w.NoteCloseAt, null);
        FinalChecks(rec, w, null, "T7a");
    }

    static void R6()
    {
        LRec rec = StartDrag("R6", Region1);
        Wait(6000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R6: no note box.");
        LIn.Type("line one");
        LIn.Key(0x10, false); LIn.Press(0x0D); LIn.Key(0x10, true);
        LIn.Type("line two");
        string v = LU.EditValue(w.Note);
        Result("A4-shift-enter", "Type 'line one', Shift+Enter, 'line two'", "Two lines in the box, the box stays open", Short(v) + ", box open " + (w.Note != IntPtr.Zero),
            v == "line one\r\nline two" && w.Note != IntPtr.Zero, null);
        LIn.Chord(0x12, 0x73);
        bool q1 = WaitQuestion(w, true, 2500);
        string t1 = LU.Texts(w.Note);
        ClickButton(w.Note, NoteRules.AnswerBack);
        bool back = WaitQuestion(w, false, 2500);
        AutomationElement close = LU.Find(w.Note, ControlType.Button, "Close");
        if (close == null) throw new Exception("R6: no Close button in the title bar.\n" + LU.Dump(w.Note));
        LIn.Click(LU.Center(close));
        bool q2 = WaitQuestion(w, true, 2500);
        string qshot = SavePrint(w.Note, "A4-question-close-printwindow");
        ClickButton(w.Note, NoteRules.AnswerSave);
        w.WaitUntil(delegate { return w.Note == IntPtr.Zero; }, 2500);
        Result("A4-altf4-x", "Alt+F4 with text, Back; then the X button, Save", "Both ask 'Save this note?'; Back keeps the box; Save saves and closes",
            "Alt+F4 question " + q1 + ", back " + back + ", X question " + q2 + ", closed " + (w.Note == IntPtr.Zero), q1 && back && q2 && w.Note == IntPtr.Zero && t1.Contains(NoteRules.Question), qshot);
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        w.Run(1000);
        FinalChecks(rec, w, "line one line two", "T7b");
    }

    static void R7()
    {
        uint pid = AppPid();
        LRec rec = StartDrag("R7", Region1);
        Wait(4000);
        IntPtr sb = FindWindow(pid, "Stop recording");
        uint aff = 0;
        if (sb != IntPtr.Zero) LN.GetWindowDisplayAffinity(sb, out aff);
        FocusPad();
        // The foreground window (the pad) locks SetForegroundWindow, so the app cannot take the focus at Stop.
        bool locked = LN.LockSetForegroundWindow(1);
        LWatch w = new LWatch(pid, rec.Folder);
        w.Start();
        int f0 = Pad.Flashes.Count, l0 = Pad.ShellLog.Count;
        LIn.Hotkey();
        w.WaitUntil(delegate { return w.Note != IntPtr.Zero; }, 8000);
        w.Run(2500);
        bool fgNote = LN.GetForegroundWindow() == w.Note;
        int flashes = Pad.Flashes.Skip(f0).Count(f => f.Value == w.LastNote);
        Rectangle bar = TaskbarRect();
        string barShot = ShotCrop("A5-taskbar-flash", new Rectangle(bar.X + 300, bar.Y, 1000, bar.Height));
        LN.LockSetForegroundWindow(2);
        string shell = string.Join("; ", Pad.ShellLog.Skip(l0).Take(12).ToArray());
        Result("A5-flash", "Pad in front locks SetForegroundWindow; Stop with the hotkey", "The box opens without the focus, and its taskbar button flashes",
            "lock " + locked + ", foreground note box " + fgNote + ", foreground '" + FgTitle() + "', flash events of the box " + flashes + "; shell hook: " + shell + "; Stop button affinity 0x" + aff.ToString("X"),
            !fgNote && flashes > 0, barShot);
        IntPtr page = PageWindow();
        LIn.Click(300, 900);
        Wait(600);
        Rectangle nr = w.NoteRect;
        LN.POINT p;
        p.X = nr.X + nr.Width / 2; p.Y = nr.Y + nr.Height / 2;
        IntPtr top = LN.GetAncestor(LN.WindowFromPoint(p), 2);
        Result("A4-on-top", "Click the counter page (a normal window) beside the note box", "The note box stays on top", "foreground '" + FgTitle() + "', window at the box centre '" + LW.Of(top).Title + "'",
            top == w.Note && LN.GetForegroundWindow() == page, null);
        ClickButton(w.Note, NoteRules.NoNoteText);
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        w.Run(1000);
        FinalChecks(rec, w, null, "A5");
        // The Stop button: a click stops the recording.
        LRec r2 = StartDrag("R7b", Region1);
        Wait(3000);
        IntPtr sb2 = FindWindow(pid, "Stop recording");
        if (sb2 == IntPtr.Zero) throw new Exception("R7: no Stop button.");
        LW sw = LW.Of(sb2);
        LWatch w2 = new LWatch(pid, r2.Folder);
        w2.Start();
        LIn.Click(sw.Frame.X + sw.Frame.Width / 2, sw.Frame.Y + sw.Frame.Height / 2);
        bool opened = w2.WaitUntil(delegate { return w2.Note != IntPtr.Zero; }, 8000);
        bool fgBox = LN.GetForegroundWindow() == w2.Note;
        bool ended = WaitRecordingEnd(8000);
        Result("A5-stop-button", "Click the Stop button below the region", "The recording stops and the note box opens with the focus",
            "note box " + opened + ", FFmpeg ended " + ended + ", foreground note box " + fgBox, opened && ended && fgBox, null);
        LIn.Press(0x1B);
        w2.WaitUntil(delegate { return w2.Finished; }, 40000);
    }

    // Saturated and dark pixels in each frame of a video inside one rectangle (ffmpeg crop to PNG files).
    static string Overlay(string clip, Rectangle r, string tag, out int maxSat, out int maxDark, out int frames)
    {
        maxSat = 0; maxDark = 0; frames = 0;
        string dir = Path.Combine(Out, "overlay_" + tag);
        Directory.CreateDirectory(dir);
        Run("ffmpeg", string.Format(CultureInfo.InvariantCulture, "-v error -i \"{0}\" -vf crop={1}:{2}:{3}:{4} -fps_mode passthrough \"{5}\\f_%04d.png\"", clip, r.Width, r.Height, r.X, r.Y, dir), 40000);
        string worst = "";
        foreach (string f in Directory.GetFiles(dir, "f_*.png").OrderBy(x => x))
        {
            using (Bitmap b = new Bitmap(f))
            {
                int sat, dark;
                LShot.ColorCount(b, out sat, out dark);
                frames++;
                if (sat > maxSat) { maxSat = sat; worst = Path.GetFileName(f); }
                if (dark > maxDark) maxDark = dark;
            }
        }
        return worst;
    }

    static void R8()
    {
        uint pid = AppPid();
        LRec r1 = StartDrag("R8a", Rectangle.FromLTRB(400, 300, 800, 600));
        Wait(4000);
        LWatch w1 = StopWatch(r1, "hotkey", false);
        if (w1.Note == IntPtr.Zero) throw new Exception("R8: no note box.");
        ClickButton(w1.Note, NoteRules.NoNoteText);
        w1.WaitUntil(delegate { return w1.Toast != IntPtr.Zero; }, 3000);
        Rectangle popupRect = w1.ToastRect;
        string[] before = Entries();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("R8: the selector did not open.");
        Wait(300);
        LIn.Press(0x20);
        LRec r2 = Started("R8b", before);
        LIn.Move(1900, 600);
        Stopwatch since = Stopwatch.StartNew();
        double busyOn = w1.Toast != IntPtr.Zero ? 0 : -1;
        Bitmap baseShot = LShot.Screen();
        w1.WaitUntil(delegate { return (w1.ReadyAt >= 0 || w1.FailedAt >= 0) && w1.Toast == IntPtr.Zero; }, 60000);
        double popupTimes = since.Elapsed.TotalSeconds;
        Wait(1500);
        LWatch w2 = StopWatch(r2, "hotkey", false);
        w2.Run(1200);
        Rectangle noteRect = w2.NoteRect;
        LIn.Press(0x1B);
        w2.WaitUntil(delegate { return w2.Finished; }, 40000);
        w2.Run(1000);
        // The clipboard of R8a is checked by FinalChecks of the later R8b: R8b replaced it (correct).
        FinalChecks(r1, w1, null, "R8a");
        FinalChecks(r2, w2, null, "R8b");
        int bs, bd;
        using (Bitmap bc = LShot.Crop(baseShot, popupRect)) LShot.ColorCount(bc, out bs, out bd);
        baseShot.Dispose();
        string clip = ExtractClip(r2.Folder + ".zip");
        int ps, pd, pf, ns = -1, nd = -1, nf = 0;
        string pw = Overlay(clip, popupRect, "popup", out ps, out pd, out pf);
        string nw = noteRect.IsEmpty ? "" : Overlay(clip, noteRect, "note", out ns, out nd, out nf);
        Result("A13-popup-not-in-video", "Full-screen recording made while the popup of the recording before showed (busy, then ready) for " + popupTimes.ToString("0.0", CultureInfo.InvariantCulture) + " s",
            "No popup pixels in the video: strong colours and dark pixels as on the screen capture",
            string.Format(CultureInfo.InvariantCulture, "popup {0}: {1} frames, most strong-colour pixels {2} ({3}), most dark pixels {4}; screen capture: {5} strong, {6} dark", R(popupRect), pf, ps, pw, pd, bs, bd),
            pf > 0 && ps <= bs + 30 && pd <= bd + 300, null);
        Result("A13-note-not-in-video", "Same video: the last frames, when the note box of this recording opened", "No note box pixels (blue button) in the video",
            string.Format(CultureInfo.InvariantCulture, "note box {0}: {1} frames, most strong-colour pixels {2} ({3})", R(noteRect), nf, ns, nw), nf > 0 && ns >= 0 && ns <= 30, null);
    }

    static void R9()
    {
        uint pid = AppPid();
        if (LastZip == null) throw new Exception("R9: no ZIP of a recording before.");
        List<IntPtr> before = ExplorerWindows();
        LW m = OpenTrayMenu(Tray, pid);
        ClickMenu(m, "Show last ZIP in Explorer");
        IntPtr h = WaitNewExplorer(before, 8000);
        string folder = null;
        List<string> sel = new List<string>();
        if (h != IntPtr.Zero) { Wait(2500); sel = ExplorerSelection(h, out folder); CloseWindow(h); }
        Result("T4-show", "Tray menu > Show last ZIP in Explorer", "Explorer opens the folder with the last ZIP selected",
            "window " + (h != IntPtr.Zero) + ", folder " + folder + ", selected [" + string.Join(", ", sel.ToArray()) + "]",
            h != IntPtr.Zero && sel.Count == 1 && string.Equals(sel[0], LastZip, StringComparison.OrdinalIgnoreCase), null);
        Clipboard.SetText("LiveUiTest marker");
        Wait(300);
        m = OpenTrayMenu(Tray, pid);
        ClickMenu(m, "Copy ZIP of last recording");
        Wait(1000);
        string[] files = ClipFiles() ?? new string[0];
        string text = ClipText();
        Result("T4-copy", "Tray menu > Copy ZIP of last recording", "The last ZIP on the clipboard as a file", "files [" + string.Join(", ", files) + "], text " + Short(text),
            files.Length == 1 && string.Equals(files[0], LastZip, StringComparison.OrdinalIgnoreCase), null);
    }

    static void R10()
    {
        uint pid = AppPid();
        LRec rec = StartDrag("R10", Region1);
        Wait(15000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R10: no note box.");
        LIn.Press(0x1B);
        w.WaitUntil(delegate { return w.Note == IntPtr.Zero; }, 2000);
        LW m = OpenTrayMenu(Tray, pid);
        string items = MenuItems(m);
        bool busyAtExit = w.ReadyAt < 0;
        ClickMenu(m, "Exit");
        double exitAt = w.Now;
        bool ended = w.WaitUntil(delegate { return App() == null; }, 40000);
        double endAt = w.Now;
        ZipInfo z = ReadZip(rec.Folder + ".zip");
        bool folderGone = !Directory.Exists(rec.Folder);
        Result("T5-exit-during-zip", "Tray > Exit while the ZIP is made (busy " + busyAtExit + ")", "The app waits; then the ZIP is complete, the folder is deleted, and the app ends",
            string.Format(CultureInfo.InvariantCulture, "Exit at {0:0.0} s, app ended {1} at {2:0.0} s; {3}; folder gone {4}; tray: {5}", exitAt, ended, endAt, ZipSummary(z), folderGone, string.Join(" > ", w.TrayNames.ToArray())),
            busyAtExit && ended && z.Opened && z.Clip && z.Frames > 0 && folderGone && endAt - exitAt > 0.3, null);
    }

    // ============================================================ session look (screenshots for docs\ui, Settings tree)

    static bool DarkNow()
    {
        object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return v is int && (int)v == 0;
    }

    static void StartAppShow()
    {
        if (App() != null) throw new Exception("The test exe runs already.");
        Directory.CreateDirectory(TestFolder);
        ProcessStartInfo i = new ProcessStartInfo(Exe);
        i.UseShellExecute = false;
        i.EnvironmentVariables["DESKTOP_RECORDER_SHOW_IN_CAPTURE"] = "1";
        Process.Start(i);
        if (!WaitFor(delegate { return TrayName() != null; }, 20000)) throw new Exception("The tray icon of the test exe did not show.");
        Wait(1500);
        Say("test exe started with DESKTOP_RECORDER_SHOW_IN_CAPTURE=1, pid " + AppPid());
    }

    // The menu with a small margin, cut at the top of the taskbar (no taskbar icons in a public picture).
    static string MenuShot(LW m, string name, Rectangle bar)
    {
        Rectangle r = Inflate(m.Frame, 12);
        if (r.Bottom > bar.Top) r.Height = bar.Top - r.Y;
        return ShotCrop(name, r);
    }

    static void SessionLook(List<string> names)
    {
        Dictionary<string, string> s = new Dictionary<string, string>();
        s["output_mode"] = "folder";
        s["whisper_path"] = WhisperCli;
        s["whisper_model"] = WhisperModel;
        WriteSettings(s);
        StartAppShow();
        OpenPage();
        uint pid = AppPid();
        string theme = DarkNow() ? "dark" : "light";
        bool full = !names.Contains("dark-only");
        Rectangle bar = TaskbarRect();
        LW m = OpenTrayMenu(Tray, pid);
        Wait(400);
        MenuShot(m, "live-tray-idle-125-" + theme, bar);
        LIn.Press(0x1B);
        Wait(400);
        string[] before = Entries();
        FrontPage();
        LIn.Hotkey();
        if (!WaitSelector(pid)) throw new Exception("look: no selector");
        LIn.DragOpen(560, 300, 1000, 560);
        Wait(400);
        if (full) ShotCrop("live-selector-drag-125", new Rectangle(480, 220, 800, 600));
        LIn.LeftUp();
        LRec rec = Started("L1", before);
        LIn.Move(1900, 600);
        Wait(1500);
        IntPtr sb = FindWindow(pid, "Stop recording");
        if (full && sb != IntPtr.Zero)
        {
            LW sw = LW.Of(sb);
            ShotCrop("live-frame-stop-125", Rectangle.Union(Inflate(rec.Region, 30), Inflate(sw.Frame, 16)));
        }
        m = OpenTrayMenu(Tray, pid);
        Wait(1200);
        MenuShot(m, "live-tray-recording-125-" + theme, bar);
        LIn.Press(0x1B);
        Wait(2500);
        LWatch w = StopWatch(rec, "hotkey", false);
        Wait(150);
        ShotCrop("live-note-busy-125-" + theme, Inflate(w.NoteRect, 16));
        if (w.ReadyAt < 0)
        {
            m = OpenTrayMenu(Tray, pid);
            Say("  busy menu status: " + Short(StatusName(m)));
            MenuShot(m, "live-tray-busy-125-" + theme, bar);
            LIn.Press(0x1B);
        }
        w.WaitUntil(delegate { return w.ReadyAt >= 0; }, 40000);
        w.Run(250);
        using (Bitmap scr = LShot.Screen())
        {
            LShot.Save(LShot.Crop(scr, Inflate(w.NoteRect, 16)), "live-note-ready-125-" + theme);
            if (w.Toast != IntPtr.Zero) LShot.Save(LShot.Crop(scr, Inflate(w.ToastRect, 16)), "live-popup-ready-125-" + theme);
        }
        ClickEdit(w.Note);
        LIn.Type("A note for the screenshot");
        LIn.Press(0x1B);
        WaitQuestion(w, true, 2500);
        Wait(300);
        ShotCrop("live-note-question-125-" + theme, Inflate(w.NoteRect, 16));
        ClickButton(w.Note, NoteRules.AnswerDiscard);
        w.WaitUntil(delegate { return w.Finished; }, 40000);
        LRec r2 = StartDrag("L2", Region1);
        Wait(12000);
        LWatch w2 = StopWatch(r2, "hotkey", false);
        ClickButton(w2.Note, NoteRules.NoNoteText);
        if (w2.WaitUntil(delegate { return w2.Toast != IntPtr.Zero && w2.ToastText != null && w2.ToastText.StartsWith("Making"); }, 3000))
        {
            Wait(150);
            ShotCrop("live-popup-busy-125-" + theme, Inflate(w2.ToastRect, 16));
        }
        w2.WaitUntil(delegate { return w2.Finished; }, 40000);
        if (full)
        {
            LastZip = r2.Folder + ".zip";
            R9();
            SettingsDump();
        }
        ExitViaTray(Tray, "A1-exit-look-" + theme);
    }

    static IntPtr OpenSettingsByDoubleClick(uint pid)
    {
        AutomationElement b = LU.TrayButton(Tray);
        Point c = LU.Center(b);
        LIn.DoubleClick(c.X, c.Y);
        IntPtr sw = IntPtr.Zero;
        WaitFor(delegate { sw = FindWindow(pid, "Desktop Recorder settings"); return sw != IntPtr.Zero; }, 6000);
        Wait(800);
        return sw;
    }

    static void SettingsDump()
    {
        uint pid = AppPid();
        IntPtr sw = OpenSettingsByDoubleClick(pid);
        Result("A6-open", "Double-click the tray icon", "The Settings window opens", "window " + (sw != IntPtr.Zero), sw != IntPtr.Zero, null);
        if (sw == IntPtr.Zero) return;
        for (int page = 0; page < 5; page++)
        {
            LW w = LW.Of(sw);
            ShotCrop("live-settings-page" + (page + 1) + "-125", Inflate(w.Frame, 10));
            Say("settings page " + (page + 1) + " tree:\n" + LU.Dump(sw));
            LIn.Chord(0x11, 0x09);
            Wait(500);
        }
        ClickButton(sw, "Cancel");
        Wait(600);
    }

    // ============================================================ session settings (7.20 item 6)

    static string RunValue()
    {
        object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "DesktopRecorder", null);
        return v as string ?? "(none)";
    }

    static string ToggleOf(IntPtr sw, string name)
    {
        AutomationElement e = LU.FindVisible(sw, ControlType.CheckBox, name);
        if (e == null) return "(missing)";
        object p;
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out p)) return ((TogglePattern)p).Current.ToggleState.ToString();
        return "(no toggle)";
    }

    static void ClickCheck(IntPtr sw, string name)
    {
        AutomationElement e = LU.FindVisible(sw, ControlType.CheckBox, name);
        if (e == null) throw new Exception("No check box '" + name + "'.");
        LIn.Click(LU.Center(e));
        Wait(300);
    }

    static double SpinValue(IntPtr sw, string name)
    {
        AutomationElement e = LU.FindVisible(sw, ControlType.Spinner, name);
        if (e == null) return -1;
        // UI Automation gives no value for this NumericUpDown: read the text of its edit window (WM_GETTEXT).
        IntPtr h = new IntPtr(e.Current.NativeWindowHandle);
        for (IntPtr c = SpinChild(h, IntPtr.Zero); c != IntPtr.Zero; c = SpinChild(h, c))
        {
            System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
            LN.GetClassName(c, cls, 256);
            if (cls.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) < 0) continue;
            System.Text.StringBuilder t = new System.Text.StringBuilder(256);
            SpinText(c, 0x000D, new IntPtr(256), t);
            double d;
            if (double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return -2;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowEx")] static extern IntPtr SpinChild(IntPtr parent, IntPtr after, string cls = null, string title = null);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] static extern IntPtr SpinText(IntPtr h, uint msg, IntPtr w, System.Text.StringBuilder l);
    static void SpinUp(IntPtr sw, string name)
    {
        AutomationElement e = LU.FindVisible(sw, ControlType.Spinner, name);
        if (e == null) throw new Exception("No spinner '" + name + "'.");
        Rectangle r = LU.Bounds(e);
        LIn.Click(r.Right - 8, r.Top + r.Height / 4);
        Wait(300);
    }

    static void GoPage(IntPtr sw, string page)
    {
        AutomationElement e = LU.FindVisible(sw, ControlType.ListItem, page);
        if (e == null) throw new Exception("No page '" + page + "'.");
        LIn.Click(LU.Center(e));
        Wait(400);
    }

    static bool PageSelected(IntPtr sw, string page)
    {
        AutomationElement e = LU.Find(sw, ControlType.ListItem, page);
        object p;
        return e != null && e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p) && ((SelectionItemPattern)p).Current.IsSelected;
    }

    static string Values(IntPtr sw)
    {
        GoPage(sw, "Recording");
        string fps = SpinValue(sw, "Frames per second").ToString(CultureInfo.InvariantCulture);
        GoPage(sw, "Capture and quality");
        string hide = ToggleOf(sw, "Hide the mouse pointer");
        GoPage(sw, "Hotkey and selector");
        string cross = ToggleOf(sw, "Crosshair");
        GoPage(sw, "AI and audio");
        string audio = ToggleOf(sw, "Record the sound of the PC");
        GoPage(sw, "Startup and sounds");
        string sounds = ToggleOf(sw, "Play sounds"), startWin = ToggleOf(sw, "Start with Windows");
        return "fps " + fps + ", hide pointer " + hide + ", crosshair " + cross + ", sound of the PC " + audio + ", play sounds " + sounds + ", start with Windows " + startWin;
    }

    static string Keys5(Dictionary<string, string> d)
    {
        return "fps=" + d["fps"] + " show_pointer=" + d["show_pointer"] + " selector_crosshair=" + d["selector_crosshair"] + " record_audio=" + d["record_audio"] + " play_sounds=" + d["play_sounds"] + " hotkey=" + d["hotkey"];
    }

    static void SessionSettings(List<string> names)
    {
        Dictionary<string, string> s = new Dictionary<string, string>();
        s["output_mode"] = "folder";
        WriteSettings(s);
        StartApp();
        uint pid = AppPid();
        string run0 = RunValue();
        Dictionary<string, string> s0 = ReadSettings();
        IntPtr sw = OpenSettingsByDoubleClick(pid);
        if (sw == IntPtr.Zero) throw new Exception("Settings did not open.");
        string v0 = Values(sw);
        GoPage(sw, "Recording");
        SpinUp(sw, "Frames per second");
        GoPage(sw, "Capture and quality");
        ClickCheck(sw, "Hide the mouse pointer");
        GoPage(sw, "Hotkey and selector");
        ClickCheck(sw, "Crosshair");
        GoPage(sw, "AI and audio");
        ClickCheck(sw, "Record the sound of the PC");
        GoPage(sw, "Startup and sounds");
        ClickCheck(sw, "Play sounds");
        string vChanged = Values(sw);
        ClickButton(sw, "Save");
        bool closed = WaitFor(delegate { return !LN.IsWindowVisible(sw); }, 3000);
        Wait(500);
        Dictionary<string, string> s1 = ReadSettings();
        string run1 = RunValue();
        bool saved = s1["fps"] == "7" && s1["show_pointer"] == "0" && s1["selector_crosshair"] == "0" && s1["record_audio"] == "0" && s1["play_sounds"] == "0";
        Result("A6-save", "One change on each page (fps +1, hide pointer, crosshair, sound of the PC, play sounds), Save",
            "The window closes; settings.txt has the 5 new values", "before: " + Keys5(s0) + "; after: " + Keys5(s1) + "; closed " + closed + "; fields at the start: " + v0,
            closed && saved, null);
        Result("A6-run-entry", "Run entry before and after Save", "Report only (HANDOFF 7.8: Save writes the path of the exe that saves)",
            "before " + run0 + "; after " + run1, true, null);
        sw = OpenSettingsByDoubleClick(pid);
        string v1 = Values(sw);
        Result("A6-reopen", "Open Settings again", "The fields show the saved values", v1, v1 == vChanged, null);
        GoPage(sw, "Recording");
        SpinUp(sw, "Frames per second");
        ClickButton(sw, "Cancel");
        WaitFor(delegate { return !LN.IsWindowVisible(sw); }, 3000);
        Wait(500);
        Dictionary<string, string> s2 = ReadSettings();
        sw = OpenSettingsByDoubleClick(pid);
        GoPage(sw, "Recording");
        double fps2 = SpinValue(sw, "Frames per second");
        Result("A6-cancel", "fps +1, then Cancel; open again", "Nothing changes", "settings.txt " + Keys5(s2) + "; field fps " + fps2, Keys5(s2) == Keys5(s1) && fps2 == 7, null);
        ClickButton(sw, "Restore defaults");
        Wait(500);
        string vDef = Values(sw);
        string defShot = ShotCrop("A6-restore-defaults", Inflate(LW.Of(sw).Frame, 10));
        ClickButton(sw, "Cancel");
        WaitFor(delegate { return !LN.IsWindowVisible(sw); }, 3000);
        Wait(500);
        Dictionary<string, string> s3 = ReadSettings();
        Result("A6-restore-cancel", "Restore defaults, then Cancel", "The fields show the defaults (10 fps, pointer hidden, crosshair off, sound off, sounds on, Start with Windows off); settings.txt does not change",
            "fields after Restore defaults: " + vDef + "; settings.txt " + Keys5(s3), Keys5(s3) == Keys5(s1) && vDef.StartsWith("fps 10, hide pointer On, crosshair Off, sound of the PC Off, play sounds On, start with Windows Off"), defShot);
        sw = OpenSettingsByDoubleClick(pid);
        GoPage(sw, "Hotkey and selector");
        ClickCheck(sw, "Hotkey: Win");
        GoPage(sw, "Recording");
        ClickButton(sw, "Save");
        Wait(800);
        bool open = LN.IsWindowVisible(sw);
        bool onHotkey = PageSelected(sw, "Hotkey and selector");
        string texts = LU.Texts(sw);
        string badShot = ShotCrop("A6-bad-hotkey", Inflate(LW.Of(sw).Frame, 10));
        ClickButton(sw, "Cancel");
        WaitFor(delegate { return !LN.IsWindowVisible(sw); }, 3000);
        Wait(500);
        Dictionary<string, string> s4 = ReadSettings();
        Result("A6-bad-hotkey", "Clear Win (Shift+D is left), go to the Recording page, Save", "The window stays open on the Hotkey page with a red message; nothing is saved",
            "open " + open + ", Hotkey page " + onHotkey + ", hotkey in settings.txt " + s4["hotkey"] + "; texts: " + Short(texts.Length > 300 ? texts.Substring(texts.Length - 300) : texts),
            open && onHotkey && s4["hotkey"] == s1["hotkey"], badShot);
        ExitViaTray(Tray, "A1-exit-settings");
    }

    // ============================================================ session more

    static void SessionMore(List<string> names)
    {
        Dictionary<string, string> s = new Dictionary<string, string>();
        s["output_mode"] = "folder";
        s["whisper_path"] = WhisperCli;
        s["whisper_model"] = WhisperModel;
        WriteSettings(s);
        OpenPage();
        if (names.Count == 0 || names.Contains("R11"))
        {
            try { R11(); }
            catch (Exception e) { Result("R11-error", "scenario R11", "no error", e.Message, false, null); CloseNoteBoxes(); }
        }
        if (names.Count == 0 || names.Contains("R12"))
        {
            if (App() == null) StartApp();
            try { R12(); }
            catch (Exception e) { Result("R12-error", "scenario R12", "no error", e.Message, false, null); CloseNoteBoxes(); }
        }
        if (App() != null) ExitViaTray(Tray, "A1-exit-more");
    }

    static void R11()
    {
        if (App() == null) StartApp();
        uint pid = AppPid();
        FrontPage();
        LRec rec = StartDrag("R11", Region1);
        Wait(6000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R11: no note box.");
        LIn.Type("Exit test text");
        LW m = OpenTrayMenu(Tray, pid);
        ClickMenu(m, "Exit");
        bool q = WaitQuestion(w, true, 3000);
        string texts = LU.Texts(w.Note);
        string qshot = SavePrint(w.Note, "A4-exit-question-printwindow");
        ClickButton(w.Note, NoteRules.AnswerBack);
        w.Run(2500);
        bool alive = App() != null;
        string kept = LU.EditValue(w.Note);
        Result("A4-exit-back", "Text in the note box; tray Exit; Back", "The question names the Exit; Back keeps the app and the text",
            "question " + q + " (" + Short(texts) + "), app runs " + alive + ", text " + Short(kept), q && alive && kept == "Exit test text" && texts.Contains("Desktop Recorder closes after this"), qshot);
        m = OpenTrayMenu(Tray, pid);
        ClickMenu(m, "Exit");
        bool q2 = WaitQuestion(w, true, 3000);
        ClickButton(w.Note, NoteRules.AnswerDiscard);
        bool ended = w.WaitUntil(delegate { return App() == null; }, 40000);
        ZipInfo z = ReadZip(rec.Folder + ".zip");
        Result("A4-exit-discard", "Tray Exit again; Discard", "The box closes; the app ends after the ZIP; no note in the ZIP; folder deleted",
            "question " + q2 + ", app ended " + ended + "; " + ZipSummary(z) + "; folder gone " + !Directory.Exists(rec.Folder), q2 && ended && z.Opened && z.Note == null && !Directory.Exists(rec.Folder), null);
    }

    static void R12()
    {
        FrontPage();
        LRec rec = StartDrag("R12", Region1);
        string blocker = rec.Folder + ".zip";
        File.WriteAllText(blocker, "LiveUiTest: a read-only file that blocks the ZIP (HANDOFF 7.20 item 12).");
        File.SetAttributes(blocker, FileAttributes.ReadOnly);
        Wait(5000);
        LWatch w = StopWatch(rec, "hotkey", false);
        if (w.Note == IntPtr.Zero) throw new Exception("R12: no note box.");
        bool failed = w.WaitUntil(delegate { return w.FailedAt >= 0; }, 40000);
        w.Run(1500);
        string noteShot = SavePrint(w.Note, "A12-note-failed-printwindow");
        string toastShot = SavePrint(w.Toast, "A12-popup-failed-printwindow");
        string line = w.NoteLine, popup = w.ToastText, clip = w.ClipText;
        LIn.Press(0x1B);
        w.Run(3000);
        double popupOn = w.ToastOn.Count > 0 ? w.ToastOn[0] : -1;
        w.WaitUntil(delegate { return w.Toast == IntPtr.Zero; }, 15000);
        double shown = w.ToastOff.Count > 0 && popupOn >= 0 ? w.ToastOff[w.ToastOff.Count - 1] - popupOn : -1;
        bool folderStays = Directory.Exists(rec.Folder);
        Result("A12-failed", "A read-only file has the ZIP name; Stop; box open", "Box line and popup say 'Could not make the ZIP' with the reason; the wait text does not stay on the clipboard; the folder stays",
            "line " + Short(line) + "; popup " + Short(popup) + string.Format(CultureInfo.InvariantCulture, " (shown {0:0.0} s)", shown) + "; clipboard " + Short(clip) + "; folder stays " + folderStays,
            failed && line != null && line.StartsWith("Could not make the ZIP") && popup != null && popup.Contains("Could not make the ZIP") && clip != ZipStatus.ClipboardPlaceholder && clip != null && clip.Contains("could not make the ZIP") && folderStays,
            noteShot + ", " + toastShot);
        File.SetAttributes(blocker, FileAttributes.Normal);
        File.Delete(blocker);
    }
}
