// Offscreen previews of the note box, the Settings window and the tray menu. No window is shown and no input is
// sent: each form is made, laid out at a scale, and drawn with Control.DrawToBitmap into a PNG file (client area
// only: the real window adds the Windows title bar). The tool also checks the layout and the Settings round trip,
// and returns 1 when a check fails. It never calls Settings.Save, the Run entry, Show or ShowDialog.
// Build (PowerShell, in the repository folder; one line):
//   & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:UiPreview /out:"$env:TEMP\UiPreview.exe"
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
//     /win32manifest:DesktopRecorder.manifest <the .cs files of build.cmd> test\ui_preview.cs
// Run: & "$env:TEMP\UiPreview.exe" "$env:TEMP\ui-preview"
// Native parts (check box marks, list arrows, spin buttons) draw at the DPI of this PC: correct in the 125% files
// on a 125% PC, simulated in the 100% and 150% files.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

static class UiPreview
{
    static int problems;
    static string outDir;

    [STAThread]
    static int Main(string[] args)
    {
        outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        float[] scales = { 1f, 1.25f, 1.5f };
        foreach (float s in scales) NotePreviews(s);
        Previews2(scales);
        ZipPreviews(scales);
        Console.WriteLine(problems == 0 ? "preview ok" : problems + " problems");
        return problems == 0 ? 0 : 1;
    }

    static void Problem(string text)
    {
        problems++;
        Console.WriteLine("PROBLEM " + text);
    }

    static string Pct(float scale)
    {
        return ((int)Math.Round(scale * 100)).ToString();
    }

    static void NotePreviews(float scale)
    {
        foreach (bool dark in new[] { false, true })
        {
            UiPalette p = dark ? UiPalette.DarkPalette() : UiPalette.Light();
            string theme = dark ? "dark" : "light";
            using (NoteForm f = new NoteForm("desktop_2026-11-22_14-16-55", 57.4, new Rectangle(0, 0, 1920, 1032), scale, 0, p))
            {
                Render(f, "note-" + Pct(scale) + "-" + theme);
                f.PreviewQuestion("Check the scroll at 0:40. The table at the end has the wrong total.", NoteCloseTrigger.EscapeKey);
                Render(f, "note-question-" + Pct(scale) + "-" + theme);
                f.PreviewQuestion("Check the scroll at 0:40.", NoteCloseTrigger.QuitApp);
                Render(f, "note-quit-question-" + Pct(scale) + "-" + theme);
            }
        }
    }

    // Draws the client area of the form into a PNG file, and checks its layout. WM_PRINT skips the child windows of
    // a window that is not shown, so the tool draws the form, then each visible child at its place (bottom first).
    public static void Render(Control c, string name)
    {
        // Handle creates the window also for a control that is not visible (Control.CreateControl does not).
        IntPtr unused = c.Handle;
        foreach (Control child in AllChildren(c)) unused = child.Handle;
        CheckLayout(c, name);
        Rectangle client = c.ClientRectangle;
        using (Bitmap bmp = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height), PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using (Bitmap whole = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                {
                    c.DrawToBitmap(whole, new Rectangle(0, 0, c.Width, c.Height));
                    g.DrawImage(whole, new Rectangle(0, 0, client.Width, client.Height), new Rectangle(ClientOffset(c), client.Size), GraphicsUnit.Pixel);
                }
                Compose(c, g, Point.Empty);
            }
            if (Blank(bmp)) Problem(name + ": the picture is blank");
            bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
        }
    }

    static void Compose(Control parent, Graphics g, Point origin)
    {
        for (int i = parent.Controls.Count - 1; i >= 0; i--)
        {
            Control child = parent.Controls[i];
            if (!Shown(child) || child.Width <= 0 || child.Height <= 0) continue;
            Point at = new Point(origin.X + child.Left, origin.Y + child.Top);
            using (Bitmap b = new Bitmap(child.Width, child.Height))
            {
                child.DrawToBitmap(b, new Rectangle(0, 0, child.Width, child.Height));
                g.DrawImage(b, at);
            }
            // WM_PRINT does not draw the selected text of a drop-down list. The tool draws it, so the picture
            // shows if the text fits. (The values themselves are checked by the round trip.)
            ComboBox combo = child as ComboBox;
            if (combo != null && combo.SelectedIndex >= 0)
            {
                Rectangle text = new Rectangle(at.X + 4, at.Y, child.Width - SystemInformation.VerticalScrollBarWidth - 6, child.Height);
                TextRenderer.DrawText(g, combo.Items[combo.SelectedIndex].ToString(), combo.Font, text,
                    combo.Enabled ? Color.FromArgb(27, 27, 27) : Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            Point inner = ClientOffset(child);
            Compose(child, g, new Point(at.X + inner.X, at.Y + inner.Y));
        }
    }

    static Point ClientOffset(Control c)
    {
        if (!c.IsHandleCreated) return Point.Empty;
        Point screen = c.PointToScreen(Point.Empty);
        NativeMethods.RECT r;
        if (!NativeMethods.GetWindowRect(c.Handle, out r)) return Point.Empty;
        return new Point(Math.Max(0, screen.X - r.Left), Math.Max(0, screen.Y - r.Top));
    }

    static bool Blank(Bitmap bmp)
    {
        Color first = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
                if (bmp.GetPixel(x, y) != first) return false;
        return true;
    }

    static IEnumerable<Control> AllChildren(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (Control grand in AllChildren(child)) yield return grand;
        }
    }

    // The own visible state of a control. Control.Visible is false for every child of a form that is not shown,
    // so the tool reads the WS_VISIBLE style of the child window, or the state before the handle exists.
    static bool Shown(Control c)
    {
        if (c.IsHandleCreated) return (NativeMethods.GetWindowLong(c.Handle, NativeMethods.GWL_STYLE) & 0x10000000) != 0;
        System.Reflection.MethodInfo m = typeof(Control).GetMethod("GetState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return m == null || (bool)m.Invoke(c, new object[] { 0x00000002 });
    }

    // Bug 3 of HANDOFF 6.7 (a label covered the input fields at 125%): no overlap between visible siblings, each
    // child inside its parent, and each text fits its control.
    static void CheckLayout(Control parent, string name)
    {
        List<Control> visible = new List<Control>();
        foreach (Control c in parent.Controls) if (Shown(c)) visible.Add(c);
        Rectangle area = parent.ClientRectangle;
        if (parent is ScrollableControl && ((ScrollableControl)parent).AutoScroll)
            area = new Rectangle(area.X, area.Y, area.Width, Math.Max(area.Height, ((ScrollableControl)parent).DisplayRectangle.Height));
        for (int i = 0; i < visible.Count; i++)
        {
            Control a = visible[i];
            if (!area.Contains(a.Bounds)) Problem(name + ": " + Describe(a) + " is outside " + Describe(parent) + " " + a.Bounds + " in " + area);
            for (int j = i + 1; j < visible.Count; j++)
                if (a.Bounds.IntersectsWith(visible[j].Bounds))
                    Problem(name + ": " + Describe(a) + " " + a.Bounds + " overlaps " + Describe(visible[j]) + " " + visible[j].Bounds);
            CheckText(a, name);
            if (a.Controls.Count > 0 && !(a is NumericUpDown) && !(a is TextFrame)) CheckLayout(a, name);
        }
    }

    static void CheckText(Control c, string name)
    {
        if (c is Label && c.Text.Length > 0)
        {
            Size need = Ui.Measure(c.Text, c.Font, c.Width);
            if (need.Height > c.Height || need.Width > c.Width) Problem(name + ": text is cut in " + Describe(c) + " need " + need + " have " + c.Size);
        }
        else if (c is Button)
        {
            int need = TextRenderer.MeasureText(c.Text.Replace("&", ""), c.Font).Width;
            if (need + 8 > c.Width) Problem(name + ": button text is cut: " + Describe(c));
        }
        else if (c is NavList)
        {
            NavList nav = (NavList)c;
            foreach (object item in nav.Items)
            {
                int need = TextRenderer.MeasureText(item.ToString(), nav.Font).Width + nav.ItemHeight * 3 / 2;
                if (need > nav.Width) Problem(name + ": page name is cut: \"" + item + "\" needs " + need + " has " + nav.Width);
            }
        }
        else if (c is ComboBox)
        {
            ComboBox box = (ComboBox)c;
            foreach (object item in box.Items)
            {
                int need = TextRenderer.MeasureText(item.ToString(), box.Font).Width + SystemInformation.VerticalScrollBarWidth + 8;
                if (need > box.Width) Problem(name + ": list text is cut: " + Describe(c) + " \"" + item + "\" needs " + need + " has " + box.Width);
            }
        }
        else if (c is CheckBox && !(c is ToggleSwitch))
        {
            int need = TextRenderer.MeasureText(c.Text, c.Font).Width + SystemInformation.MenuCheckSize.Width + 6;
            if (need > c.Width) Problem(name + ": check box text is cut: " + Describe(c));
        }
    }

    static string Describe(Control c)
    {
        string text = c.AccessibleName ?? c.Text;
        if (text != null && text.Length > 24) text = text.Substring(0, 24);
        return c.GetType().Name + "(" + text + ")";
    }

    static void Previews2(float[] scales)
    {
        foreach (float s in scales)
        {
            using (SettingsForm f = new SettingsForm(new Settings(), s))
            {
                foreach (SettingsPage page in SettingsCatalog.Pages)
                {
                    f.PreviewPage(page);
                    Render(f, "settings-" + page.ToString().ToLowerInvariant() + "-" + Pct(s));
                }
                f.PreviewPage(SettingsPage.Hotkey);
                f.PreviewMessage("For the hotkey, tick at least one of Win, Ctrl and Alt, and pick a key. Shift alone works only with F1 to F12.", true);
                Render(f, "settings-message-" + Pct(s));
            }
        }
        RoundTrips();
        TrayPreviews(scales);
    }

    // Settings to the window and back must give the same settings.txt text (this also catches an inverted switch).
    static void RoundTrips()
    {
        Settings defaults = new Settings();
        RoundTrip(defaults, defaults, false, "defaults");
        Settings all = new Settings();
        all.Folder = @"D:\Rec test";
        all.Fps = 15;
        all.Quality = 2;
        all.SplitMinutes = 5;
        all.CopyToClipboard = false;
        all.PlaySounds = false;
        all.ShowPointer = true;
        all.HotkeyModifiers = 2 | 1;
        all.HotkeyKey = Keys.F9;
        all.CaptureBackend = RecorderCore.BackendGdigrab;
        all.Encoder = RecorderCore.EncoderX264;
        all.ChromaFormat = RecorderCore.Chroma444;
        all.FrameRateMode = RecorderCore.ModeFixed;
        all.OutputMode = RecorderCore.OutputMp4;
        all.SelectorCrosshair = true;
        all.SelectorMagnifier = false;
        all.SelectorPosition = false;
        all.RecordAudio = true;
        // Fields that the window does not show: they must stay as they are.
        all.NvencCq = 30;
        all.SettleMs = 700;
        all.StillIntervalSeconds = 9;
        all.ScrollPercent = 50;
        all.SizeLimit = 1000;
        all.WhisperPath = @"C:\w\whisper-cli.exe";
        all.WhisperModel = "m.bin";
        all.WhisperLanguage = "de";
        all.Region = new Rectangle(1, 2, 300, 200);
        all.LastRegion = new Rectangle(5, 6, 400, 100);
        all.ExtraLines.Add("linux_only_key=1");
        RoundTrip(all, all, false, "all changed");
        // Restore defaults: the fields of the window get the defaults, the other fields stay.
        Settings expected = new Settings();
        expected.NvencCq = all.NvencCq; expected.SettleMs = all.SettleMs; expected.StillIntervalSeconds = all.StillIntervalSeconds;
        expected.ScrollPercent = all.ScrollPercent; expected.SizeLimit = all.SizeLimit; expected.WhisperPath = all.WhisperPath;
        expected.WhisperModel = all.WhisperModel; expected.WhisperLanguage = all.WhisperLanguage; expected.Region = all.Region;
        expected.LastRegion = all.LastRegion; expected.ExtraLines.Add("linux_only_key=1");
        RoundTrip(all, expected, true, "restore defaults");
    }

    static void RoundTrip(Settings input, Settings expected, bool restore, string name)
    {
        using (SettingsForm f = new SettingsForm(input, 1.25f))
        {
            if (restore) f.PreviewRestoreDefaults();
            Settings result;
            bool startWithWindows;
            SettingsPage page;
            Control field;
            string problem = f.ReadFields(out result, out startWithWindows, out page, out field);
            if (problem != null) { Problem("round trip " + name + ": " + problem); return; }
            if (restore && startWithWindows) Problem("round trip " + name + ": start with Windows is not off");
            string[] a = RecorderCore.SerializeSettings(expected.ToStored()).Split('\n');
            string[] b = RecorderCore.SerializeSettings(result.ToStored()).Split('\n');
            if (a.Length != b.Length) Problem("round trip " + name + ": " + a.Length + " lines, read back " + b.Length);
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) Problem("round trip " + name + ": expected " + a[i].Trim() + ", read back " + b[i].Trim());
            Console.WriteLine("round trip " + name + ": checked " + a.Length + " lines");
        }
    }

    static void TrayPreviews(float[] scales)
    {
        foreach (float s in scales)
        {
            foreach (bool dark in new[] { false, true })
            {
                UiPalette p = dark ? UiPalette.DarkPalette() : UiPalette.Light();
                string theme = dark ? "dark" : "light";
                using (TrayMenu menu = new TrayMenu())
                {
                    menu.ApplyLook(s, p);
                    menu.CreateWindow();
                    TrayState idle = MenuState(TrayPhase.Idle);
                    menu.Update(idle);
                    Render(menu.Strip, "tray-idle-" + Pct(s) + "-" + theme);
                    TrayState rec = MenuState(TrayPhase.Recording);
                    rec.ElapsedSeconds = 83;
                    rec.RegionWidth = 1880;
                    rec.RegionHeight = 860;
                    rec.HasLastZip = true;
                    rec.LastResult = "Last recording: Frames: 5. clip.mp4: 0.12 MB. Ready 3.1 s after Stop.";
                    menu.Update(rec);
                    Render(menu.Strip, "tray-recording-" + Pct(s) + "-" + theme);
                    TrayState busy = MenuState(TrayPhase.Processing);
                    busy.HasLastZip = true;
                    busy.LastResult = rec.LastResult;
                    menu.Update(busy);
                    Render(menu.Strip, "tray-processing-" + Pct(s) + "-" + theme);
                }
            }
        }
    }

    static TrayState MenuState(TrayPhase phase)
    {
        TrayState s = new TrayState();
        s.Phase = phase;
        s.Fps = 10;
        s.HotkeyText = "Win+Shift+D";
        s.HotkeyActive = true;
        return s;
    }

    // The "busy, then ready" indication of the ZIP (HANDOFF 7.21): note box line, popup, tray menu, busy icon.
    static void ZipPreviews(float[] scales)
    {
        long bytes = 35337011;   // 33.7 MB
        Rectangle area = new Rectangle(0, 0, 1920, 1032);
        foreach (float s in scales)
        {
            foreach (bool dark in new[] { false, true })
            {
                UiPalette p = dark ? UiPalette.DarkPalette() : UiPalette.Light();
                string tag = Pct(s) + "-" + (dark ? "dark" : "light");
                using (NoteForm f = new NoteForm("desktop_2026-11-22_19-40-12", 30.2, area, s, 0, p))
                {
                    f.SetZipStatus(ZipStep.Transcript, ZipStatus.NoteText(ZipStep.Transcript, 0, true, false, null));
                    Render(f, "note-zip-busy-" + tag);
                    f.SetZipStatus(ZipStep.Ready, ZipStatus.NoteText(ZipStep.Ready, bytes, true, false, null));
                    Render(f, "note-zip-ready-" + tag);
                    f.SetZipStatus(ZipStep.Failed, ZipStatus.NoteText(ZipStep.Failed, 0, true, false, "Access to the path is denied."));
                    Render(f, "note-zip-failed-" + tag);
                }
                using (ZipToast t = new ZipToast(area, s, p))
                {
                    t.Set(ZipStep.Frames, ZipStatus.PopupText(ZipStep.Frames, 0, true, false), ZipStatus.PopupDetail(ZipStep.Frames, null));
                    Render(t, "toast-busy-" + tag);
                    t.Set(ZipStep.Ready, ZipStatus.PopupText(ZipStep.Ready, bytes, true, false), ZipStatus.PopupDetail(ZipStep.Ready, null));
                    Render(t, "toast-ready-" + tag);
                    t.Set(ZipStep.Failed, ZipStatus.PopupText(ZipStep.Failed, 0, true, false), ZipStatus.PopupDetail(ZipStep.Failed, "Not enough space on the disk."));
                    Render(t, "toast-failed-" + tag);
                    if (t.Bottom > area.Bottom || t.Left < area.Left || t.Right > area.Right) Problem("toast-" + tag + ": outside the work area " + t.Bounds);
                }
                using (TrayMenu menu = new TrayMenu())
                {
                    menu.ApplyLook(s, p);
                    menu.CreateWindow();
                    TrayState z = MenuState(TrayPhase.Processing);
                    z.ZipBusy = true;
                    z.ZipStep = ZipStep.Transcript;
                    z.LastResult = "Last recording: Frames: 7. clip.mp4: 4.10 MB. Ready 6.2 s after Stop.";
                    menu.Update(z);
                    Render(menu.Strip, "tray-zip-" + tag);
                }
            }
        }
        // The 8 frames of the busy tray icon at 20 px (125%), 4 times larger, on a dark and a light taskbar color.
        int px = 20, zoom = 4, n = TrayIcons.BusyFrames;
        using (Bitmap sheet = new Bitmap(n * (px * zoom + 8) + 8, 2 * (px * zoom + 8) + 8))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int row = 0; row < 2; row++)
                for (int i = 0; i < n; i++)
                    using (Bitmap icon = TrayIcons.DrawBusy(px, i))
                    {
                        Rectangle r = new Rectangle(8 + i * (px * zoom + 8), 8 + row * (px * zoom + 8), px * zoom, px * zoom);
                        using (SolidBrush b = new SolidBrush(row == 0 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243))) g.FillRectangle(b, r);
                        g.DrawImage(icon, r);
                    }
            sheet.Save(Path.Combine(outDir, "tray-icon-busy-20.png"), ImageFormat.Png);
        }
    }
}
