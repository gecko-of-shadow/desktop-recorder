// The Settings window (Bran, 2026-11-22): five pages in a left navigation list, one card for each setting with a
// help text that gives the default, and Save / Cancel / Restore defaults at the bottom.
// Left navigation: Microsoft recommends it for 5 to 10 equal categories (NavigationView guidance), it is the
// pattern of the Windows 11 Settings app, and a WinForms TabControl cannot be drawn in that style.
// Light theme only: the native list boxes (ComboBox), spin boxes (NumericUpDown) and check boxes draw with the
// light visual style, and a dark version needs undocumented theme names or new controls (see HANDOFF 7.20).
// The window is system DPI aware (TrayApp.ShowSettings) and lays itself out with an explicit scale.
// The pages, titles, help texts and choices come from SettingsCatalog (UiModel.cs, unit tested).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

sealed class SettingsForm : Form
{
    public Settings Result;
    public bool StartWithWindows;

    readonly Settings current;
    readonly UiPalette palette = UiPalette.Light();
    readonly UiFonts fonts;
    readonly NavList nav;
    readonly Dictionary<SettingsPage, Panel> pages = new Dictionary<SettingsPage, Panel>();
    readonly Dictionary<string, Control> editors = new Dictionary<string, Control>();
    readonly TextFrame folder = new TextFrame();
    readonly FluentButton browse = new FluentButton(), restore = new FluentButton(), save = new FluentButton(), cancel = new FluentButton();
    readonly ComboBox output = new ComboBox(), mode = new ComboBox(), quality = new ComboBox(), backend = new ComboBox(),
        encoder = new ComboBox(), chroma = new ComboBox(), key = new ComboBox();
    readonly NumericUpDown fps = new NumericUpDown(), split = new NumericUpDown();
    readonly ToggleSwitch pointer = new ToggleSwitch(), crosshair = new ToggleSwitch(), magnifier = new ToggleSwitch(),
        position = new ToggleSwitch(), copy = new ToggleSwitch(), audio = new ToggleSwitch(), startup = new ToggleSwitch(),
        launch = new ToggleSwitch(), sounds = new ToggleSwitch();
    readonly CheckBox win = new CheckBox(), ctrl = new CheckBox(), alt = new CheckBox(), shift = new CheckBox();
    readonly Label message;
    readonly string whisperText;
    int footerTop;

    const int NavWidth = 248, ContentWidth = 640, FooterHeight = 64;

    public SettingsForm(Settings current, float scale)
    {
        this.current = current;
        fonts = new UiFonts(scale);
        whisperText = WhisperStatus(current);
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Desktop Recorder settings";
        // The app icon (app.ico, built into the exe with /win32icon).
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        Font = fonts.Body;

        nav = new NavList(palette, fonts);
        nav.TabIndex = 0;
        nav.AccessibleName = "Settings pages";
        foreach (SettingsPage page in SettingsCatalog.Pages) nav.Items.Add(SettingsCatalog.PageTitle(page));
        Controls.Add(nav);

        int tab = 1;
        foreach (SettingsPage page in SettingsCatalog.Pages)
        {
            Panel panel = new Panel();
            panel.BackColor = palette.Window;
            panel.AutoScroll = true;
            panel.TabIndex = tab;
            panel.Visible = false;
            pages[page] = panel;
            Controls.Add(panel);
        }

        message = Ui.Text(this, "", fonts.Caption, palette.TextSecondary, palette.Window, 0, 0, 10);
        SetUpButton(restore, "Restore defaults", false, 2);
        SetUpButton(save, "Save", true, 3);
        SetUpButton(cancel, "Cancel", false, 4);
        restore.Click += delegate { RestoreDefaults(); };
        save.Click += delegate { OnSave(); };
        cancel.DialogResult = DialogResult.Cancel;
        browse.Click += delegate { Browse(); };
        AcceptButton = save;
        CancelButton = cancel;

        SetUpChoices();
        BuildPages();
        Fill(current, ReadStartsWithWindows());
        nav.SelectedIndexChanged += delegate { ShowPage(SettingsCatalog.Pages[Math.Max(0, nav.SelectedIndex)]); };
        nav.SelectedIndex = 0;
        ShowPage(SettingsPage.Recording);
        ActiveControl = nav;
        ResumeLayout(false);
    }

    int S(int epx) { return fonts.S(epx); }

    static bool ReadStartsWithWindows()
    {
        try { return Settings.StartsWithWindows; } catch (Exception) { return false; }
    }

    static string WhisperStatus(Settings s)
    {
        try
        {
            string reason;
            WhisperTool tool = Transcript.Find(s.WhisperPath, s.WhisperModel, Path.GetDirectoryName(Application.ExecutablePath),
                Environment.GetEnvironmentVariable("PATH") ?? "", true, out reason);
            if (tool != null) return "Whisper found: " + tool.Describe() + ".";
            string why = string.IsNullOrEmpty(reason) ? "no Whisper program was found" : reason;
            return "Transcript: " + why + ". The recording gets the audio, but no transcript.";
        }
        catch (Exception) { return "The Whisper search failed. The recording gets the audio, but no transcript."; }
    }

    void SetUpButton(FluentButton b, string text, bool accent, int tabIndex)
    {
        b.Text = text;
        b.Accent = accent;
        b.TabIndex = tabIndex;
        b.Style(palette, fonts.Scale, fonts.Body);
        Controls.Add(b);
    }

    void SetUpChoices()
    {
        Choices(output, SettingsCatalog.Output);
        Choices(mode, SettingsCatalog.Mode);
        Choices(quality, SettingsCatalog.Quality);
        Choices(backend, SettingsCatalog.Backend);
        Choices(encoder, SettingsCatalog.Encoder);
        Choices(chroma, SettingsCatalog.Chroma);
        key.DropDownStyle = ComboBoxStyle.DropDownList;
        for (char c = 'A'; c <= 'Z'; c++) key.Items.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) key.Items.Add(c.ToString());
        for (int f = 1; f <= 12; f++) key.Items.Add("F" + f);
        fps.DecimalPlaces = 1;
        fps.Increment = 1;
        fps.Minimum = (decimal)RecorderCore.MinFps;
        fps.Maximum = (decimal)RecorderCore.MaxFps;
        fps.TextAlign = HorizontalAlignment.Right;
        split.Minimum = 0;
        split.Maximum = 1440;
        split.TextAlign = HorizontalAlignment.Right;
        // Folder for AI never starts a new file: the minutes apply to MP4 only.
        output.SelectedIndexChanged += delegate { split.Enabled = SettingsCatalog.ValueAt(SettingsCatalog.Output, output.SelectedIndex) == RecorderCore.OutputMp4; };
        win.Text = "Win"; ctrl.Text = "Ctrl"; alt.Text = "Alt"; shift.Text = "Shift";
    }

    static void Choices(ComboBox box, SettingChoice[] list)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (SettingChoice c in list) box.Items.Add(c.Text);
    }

    // ------------------------------------------------------------------ the pages

    void BuildPages()
    {
        Page(SettingsPage.Recording, delegate(Panel p, ref int y)
        {
            folder.Box.Multiline = false;
            folder.Style(palette, fonts.Scale, fonts.Body);
            browse.Text = "Browse...";
            browse.Style(palette, fonts.Scale, fonts.Body);
            StackedCard(p, ref y, "folder", new Control[] { folder, browse }, delegate(CardPanel card, int x, int top, int width)
            {
                int bw = S(120);
                folder.Place(x, top, width - bw - S(8), S(32));
                browse.SetBounds(x + width - bw, top, bw, S(32));
                return S(32);
            });
            InlineCard(p, ref y, "output_mode", output, S(248));
            InlineCard(p, ref y, "fps", fps, S(100));
            InlineCard(p, ref y, "frame_rate_mode", mode, S(248));
            InlineCard(p, ref y, "split_minutes", split, S(100));
        });
        Page(SettingsPage.Capture, delegate(Panel p, ref int y)
        {
            InlineCard(p, ref y, "quality", quality, S(248));
            InlineCard(p, ref y, "capture_backend", backend, S(248));
            InlineCard(p, ref y, "encoder", encoder, S(248));
            InlineCard(p, ref y, "chroma", chroma, S(248));
            InlineCard(p, ref y, "show_pointer", pointer, 0);
        });
        Page(SettingsPage.Hotkey, delegate(Panel p, ref int y)
        {
            StackedCard(p, ref y, "hotkey", new Control[] { win, ctrl, alt, shift, key }, delegate(CardPanel card, int x, int top, int width)
            {
                int h = S(32), cx = x;
                foreach (CheckBox box in new[] { win, ctrl, alt, shift })
                {
                    box.AutoSize = false;
                    box.Font = fonts.Body;
                    box.BackColor = palette.Surface;
                    box.ForeColor = palette.Text;
                    int w = TextRenderer.MeasureText(box.Text, fonts.Body).Width + SystemInformation.MenuCheckSize.Width + S(16);
                    box.SetBounds(cx, top, w, h);
                    cx += w + S(8);
                }
                Label plus = new Label();
                plus.Text = "+";
                plus.Font = fonts.Body;
                plus.ForeColor = palette.TextSecondary;
                plus.BackColor = palette.Surface;
                plus.TextAlign = ContentAlignment.MiddleCenter;
                plus.SetBounds(cx, top, S(20), h);
                card.Controls.Add(plus);
                cx += S(20) + S(8);
                key.Font = fonts.Body;
                key.SetBounds(cx, top + (h - key.Height) / 2, S(96), key.Height);
                return h;
            });
            Section(p, ref y, SettingsCatalog.SelectorHeading, SettingsCatalog.SelectorIntro);
            InlineCard(p, ref y, "selector_crosshair", crosshair, 0);
            InlineCard(p, ref y, "selector_magnifier", magnifier, 0);
            InlineCard(p, ref y, "selector_position", position, 0);
        });
        Page(SettingsPage.AiAudio, delegate(Panel p, ref int y)
        {
            InlineCard(p, ref y, "copy_to_clipboard", copy, 0);
            InlineCard(p, ref y, "record_audio", audio, 0);
            Label w = Ui.Text(p, whisperText, fonts.Caption, palette.TextSecondary, palette.Window, S(16), y, CardWidth() - S(32));
            w.AccessibleName = "Whisper";
            y = w.Bottom + S(4);
            Section(p, ref y, SettingsCatalog.FileOnlyHeading, SettingsCatalog.FileOnlyNote());
        });
        Page(SettingsPage.Startup, delegate(Panel p, ref int y)
        {
            InlineCard(p, ref y, SettingsCatalog.StartWithWindowsKey, startup, 0);
            InlineCard(p, ref y, "record_on_launch", launch, 0);
            InlineCard(p, ref y, "play_sounds", sounds, 0);
        });
        LayoutWindow();
    }

    delegate void PageBuilder(Panel panel, ref int y);
    delegate int ControlRow(CardPanel card, int x, int top, int width);

    int CardWidth() { return S(ContentWidth) - S(12) - S(28); }

    // Title and intro of the page, then its cards. The panel height is set in LayoutWindow.
    void Page(SettingsPage page, PageBuilder build)
    {
        Panel p = pages[page];
        int y = S(20);
        Label title = Ui.Text(p, SettingsCatalog.PageTitle(page), fonts.Subtitle, palette.Text, palette.Window, 0, y, CardWidth());
        y = title.Bottom + S(4);
        Label intro = Ui.Text(p, SettingsCatalog.PageIntro(page), fonts.Body, palette.TextSecondary, palette.Window, 0, y, CardWidth());
        y = intro.Bottom + S(16);
        build(p, ref y);
        p.Tag = y + S(16);   // the content height
    }

    void Section(Panel p, ref int y, string heading, string text)
    {
        y += S(16);
        Label h = Ui.Text(p, heading, fonts.BodyStrong, palette.Text, palette.Window, 0, y, CardWidth());
        y = h.Bottom + S(2);
        Label t = Ui.Text(p, text, fonts.Caption, palette.TextSecondary, palette.Window, 0, y, CardWidth());
        y = t.Bottom + S(8);
    }

    CardPanel NewCard(Panel p, string key)
    {
        CardPanel card = new CardPanel();
        card.Palette = palette;
        card.UiScale = fonts.Scale;
        card.BackColor = palette.Surface;
        card.TabIndex = p.Controls.Count;
        p.Controls.Add(card);
        return card;
    }

    // Title and help at the left, the control at the right (width 0 = a switch).
    void InlineCard(Panel p, ref int y, string key, Control control, int controlWidth)
    {
        SettingInfo info = SettingsCatalog.Get(key);
        CardPanel card = NewCard(p, key);
        int width = CardWidth(), pad = S(16);
        ToggleSwitch toggle = control as ToggleSwitch;
        if (toggle != null) toggle.Style(palette, fonts.Scale, fonts.Body, palette.Surface);
        else control.Font = fonts.Body;
        int cw = toggle != null ? toggle.Width : controlWidth;
        int textW = width - 2 * pad - cw - S(24);
        Label title = Ui.Text(card, info.Title, fonts.Body, palette.Text, palette.Surface, pad, S(12), textW);
        Label help = Ui.Text(card, info.Help, fonts.Caption, palette.TextSecondary, palette.Surface, pad, title.Bottom + S(2), textW);
        int ch = toggle != null ? toggle.Height : control.Height;
        int height = Math.Max(help.Bottom + S(12), ch + S(24));
        card.SetBounds(0, y, width, height);
        control.SetBounds(width - pad - cw, (height - ch) / 2, cw, ch);
        Describe(control, info);
        card.Controls.Add(control);
        y += height + S(4);
    }

    // Title and help over the whole width, then a row of controls under them.
    void StackedCard(Panel p, ref int y, string key, Control[] controls, ControlRow row)
    {
        SettingInfo info = SettingsCatalog.Get(key);
        CardPanel card = NewCard(p, key);
        int width = CardWidth(), pad = S(16);
        Label title = Ui.Text(card, info.Title, fonts.Body, palette.Text, palette.Surface, pad, S(12), width - 2 * pad);
        Label help = Ui.Text(card, info.Help, fonts.Caption, palette.TextSecondary, palette.Surface, pad, title.Bottom + S(2), width - 2 * pad);
        foreach (Control c in controls) card.Controls.Add(c);
        int top = help.Bottom + S(10);
        int rowH = row(card, pad, top, width - 2 * pad);
        card.SetBounds(0, y, width, top + rowH + S(14));
        foreach (Control c in controls) Describe(c, info);
        if (key == "folder") folder.Box.AccessibleName = info.Title;
        y += card.Height + S(4);
    }

    void Describe(Control c, SettingInfo info)
    {
        editors[info.Key] = c;
        if (c is CheckBox && !(c is ToggleSwitch)) c.AccessibleName = info.Title + ": " + c.Text;
        else if (c != browse) c.AccessibleName = info.Title;
        c.AccessibleDescription = info.Help;
    }

    // Sizes the window: the navigation at the left, the page at the right, the footer at the bottom.
    void LayoutWindow()
    {
        int contentH = 0;
        foreach (Panel p in pages.Values) contentH = Math.Max(contentH, (int)p.Tag);
        int width = S(NavWidth) + S(ContentWidth);
        Rectangle work = Screen.GetWorkingArea(Cursor.Position);
        int maxH = Math.Max(S(360), work.Height - S(64));
        int height = Math.Min(contentH + S(FooterHeight), maxH);
        ClientSize = new Size(width, height);
        footerTop = height - S(FooterHeight);
        nav.SetBounds(S(12), S(16), S(NavWidth) - S(24), footerTop - S(16));
        foreach (Panel p in pages.Values) p.SetBounds(S(NavWidth), 0, S(ContentWidth) - S(12), footerTop);
        int by = footerTop + (S(FooterHeight) - S(32)) / 2;
        int bw = S(120);
        cancel.SetBounds(width - S(24) - bw, by, bw, S(32));
        save.SetBounds(cancel.Left - S(8) - bw, by, bw, S(32));
        int rw = TextRenderer.MeasureText(restore.Text, fonts.Body).Width + S(32);
        restore.SetBounds(S(24), by, rw, S(32));
        int mx = restore.Right + S(16);
        message.SetBounds(mx, footerTop + S(8), save.Left - S(16) - mx, S(FooterHeight) - S(16));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Pen p = new Pen(palette.Divider)) e.Graphics.DrawLine(p, 0, footerTop, ClientSize.Width, footerTop);
    }

    void ShowPage(SettingsPage page)
    {
        foreach (KeyValuePair<SettingsPage, Panel> kv in pages) kv.Value.Visible = kv.Key == page;
        int index = Array.IndexOf(SettingsCatalog.Pages, page);
        if (nav.SelectedIndex != index) nav.SelectedIndex = index;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Tab and Ctrl+Page Down: next page. Ctrl+Shift+Tab and Ctrl+Page Up: page before.
        int step = 0;
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.PageDown)) step = 1;
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab) || keyData == (Keys.Control | Keys.PageUp)) step = -1;
        if (step != 0)
        {
            int n = SettingsCatalog.Pages.Length;
            nav.SelectedIndex = (nav.SelectedIndex + step + n) % n;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------------ values

    // Puts the values of s into the fields. Restore defaults calls it with new Settings(): only the fields of this
    // window change, and Save still applies them.
    void Fill(Settings s, bool startsWithWindows)
    {
        folder.Box.Text = s.Folder;
        output.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Output, RecorderCore.NormalizeOutputMode(s.OutputMode));
        split.Enabled = SettingsCatalog.ValueAt(SettingsCatalog.Output, output.SelectedIndex) == RecorderCore.OutputMp4;
        try { fps.Value = (decimal)RecorderCore.ClampFps(s.Fps); }
        catch (Exception) { fps.Value = (decimal)RecorderCore.DefaultFps; }
        mode.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Mode, RecorderCore.NormalizeMode(s.FrameRateMode));
        split.Value = RecorderCore.ClampSplitMinutes(s.SplitMinutes);
        quality.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Quality, RecorderCore.ClampQuality(s.Quality).ToString(System.Globalization.CultureInfo.InvariantCulture));
        backend.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Backend, RecorderCore.NormalizeBackend(s.CaptureBackend));
        encoder.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Encoder, RecorderCore.NormalizeEncoder(s.Encoder));
        chroma.SelectedIndex = SettingsCatalog.IndexOf(SettingsCatalog.Chroma, RecorderCore.NormalizeChroma(s.ChromaFormat));
        pointer.Checked = !s.ShowPointer;
        win.Checked = (s.HotkeyModifiers & 8) != 0;
        ctrl.Checked = (s.HotkeyModifiers & 2) != 0;
        alt.Checked = (s.HotkeyModifiers & 1) != 0;
        shift.Checked = (s.HotkeyModifiers & 4) != 0;
        string keyName = Settings.KeyName(s.HotkeyKey);
        if (!key.Items.Contains(keyName)) key.Items.Add(keyName);
        key.SelectedItem = keyName;
        crosshair.Checked = s.SelectorCrosshair;
        magnifier.Checked = s.SelectorMagnifier;
        position.Checked = s.SelectorPosition;
        copy.Checked = s.CopyToClipboard;
        audio.Checked = s.RecordAudio;
        startup.Checked = startsWithWindows;
        launch.Checked = s.RecordOnLaunch;
        sounds.Checked = s.PlaySounds;
    }

    // Reads the fields into a copy of the current settings (the fields that the window does not show stay as they
    // are). Returns null, or the problem text; then page and field tell where the problem is. No file and no
    // registry change here: the offscreen preview calls this method (test\ui_preview.cs).
    public string ReadFields(out Settings result, out bool startWithWindows, out SettingsPage page, out Control field)
    {
        result = null;
        startWithWindows = startup.Checked;
        page = SettingsPage.Recording;
        field = null;
        int mods = (win.Checked ? 8 : 0) | (ctrl.Checked ? 2 : 0) | (alt.Checked ? 1 : 0) | (shift.Checked ? 4 : 0);
        Keys hotkeyKey;
        if (!Settings.TryParseKey((string)key.SelectedItem ?? "", out hotkeyKey) || !Settings.IsValidHotkey(mods, hotkeyKey))
        {
            page = SettingsPage.Hotkey;
            field = win;
            return "For the hotkey, tick at least one of Win, Ctrl and Alt, and pick a key. Shift alone works only with F1 to F12.";
        }
        string path = folder.Box.Text.Trim().Trim('"');
        if (path.Length == 0 || !Path.IsPathRooted(path))
        {
            field = folder.Box;
            return "Give a full folder path, such as D:\\Recordings.";
        }
        result = current.Copy();
        result.Folder = path.TrimEnd('\\', '/').Length == 2 ? path : path.TrimEnd('\\', '/');
        result.Fps = (double)fps.Value;
        result.Quality = int.Parse(SettingsCatalog.ValueAt(SettingsCatalog.Quality, quality.SelectedIndex), System.Globalization.CultureInfo.InvariantCulture);
        result.SplitMinutes = (int)split.Value;
        result.RecordOnLaunch = launch.Checked;
        result.CopyToClipboard = copy.Checked;
        result.PlaySounds = sounds.Checked;
        result.ShowPointer = !pointer.Checked;
        result.RecordAudio = audio.Checked;
        result.HotkeyModifiers = mods;
        result.HotkeyKey = hotkeyKey;
        result.CaptureBackend = SettingsCatalog.ValueAt(SettingsCatalog.Backend, backend.SelectedIndex);
        result.Encoder = SettingsCatalog.ValueAt(SettingsCatalog.Encoder, encoder.SelectedIndex);
        result.ChromaFormat = SettingsCatalog.ValueAt(SettingsCatalog.Chroma, chroma.SelectedIndex);
        result.FrameRateMode = SettingsCatalog.ValueAt(SettingsCatalog.Mode, mode.SelectedIndex);
        result.OutputMode = SettingsCatalog.ValueAt(SettingsCatalog.Output, output.SelectedIndex);
        result.SelectorCrosshair = crosshair.Checked;
        result.SelectorMagnifier = magnifier.Checked;
        result.SelectorPosition = position.Checked;
        return null;
    }

    void OnSave()
    {
        Settings result;
        bool startWithWindows;
        SettingsPage page;
        Control field;
        string problem = ReadFields(out result, out startWithWindows, out page, out field);
        if (problem == null)
        {
            try { Directory.CreateDirectory(result.Folder); }
            catch (Exception e)
            {
                problem = "Cannot create that folder: " + e.Message;
                page = SettingsPage.Recording;
                field = folder.Box;
            }
        }
        if (problem != null)
        {
            ShowPage(page);
            ShowMessage(problem, true);
            if (field != null) field.Focus();
            return;
        }
        Result = result;
        StartWithWindows = startWithWindows;
        DialogResult = DialogResult.OK;
        Close();
    }

    void RestoreDefaults()
    {
        Fill(new Settings(), false);
        ShowMessage("The fields have the default values. Click Save to keep them.", false);
    }

    void ShowMessage(string text, bool problem)
    {
        message.Text = text;
        message.ForeColor = problem ? palette.Danger : palette.TextSecondary;
    }

    void Browse()
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.SelectedPath = folder.Box.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Box.Text = dialog.SelectedPath;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fonts.Dispose();
        base.Dispose(disposing);
    }

    // For the offscreen preview: show a page, restore the defaults, and set a message.
    public void PreviewPage(SettingsPage page) { ShowPage(page); }
    public void PreviewRestoreDefaults() { RestoreDefaults(); }
    public void PreviewMessage(string text, bool problem) { ShowMessage(text, problem); }
}

// The page list at the left, in the style of the Windows 11 Settings app: an icon and a name for each page, a
// light fill and an accent bar on the selected page. It stays a ListBox: arrow keys move, screen readers read it.
sealed class NavList : ListBox
{
    static readonly string[] Glyphs = { Ui.GlyphVideo, Ui.GlyphQuality, Ui.GlyphKeyboard, Ui.GlyphRobot, Ui.GlyphPower };
    readonly UiPalette palette;
    readonly UiFonts fonts;
    int hover = -1;

    public NavList(UiPalette palette, UiFonts fonts)
    {
        this.palette = palette;
        this.fonts = fonts;
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        Font = fonts.Body;
        ItemHeight = fonts.S(40);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Graphics g = e.Graphics;
        Rectangle r = e.Bounds;
        using (SolidBrush back = new SolidBrush(palette.Window)) g.FillRectangle(back, r);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Rectangle pill = new Rectangle(r.X + fonts.S(2), r.Y + fonts.S(2), r.Width - fonts.S(4), r.Height - fonts.S(4));
        if (selected || e.Index == hover)
            Ui.Box(g, pill, 4 * fonts.Scale, selected ? palette.Selection : palette.ControlHover, Color.Empty);
        if (selected)
        {
            int barH = fonts.S(16), barW = Math.Max(2, fonts.S(3));
            Rectangle bar = new Rectangle(pill.X, r.Y + (r.Height - barH) / 2, barW, barH);
            Ui.Box(g, bar, barW / 2f, palette.Accent, Color.Empty);
        }
        int x = r.X + fonts.S(16);
        if (fonts.Icons != null && e.Index < Glyphs.Length)
        {
            Rectangle icon = new Rectangle(x, r.Y, fonts.S(20), r.Height);
            TextRenderer.DrawText(g, Glyphs[e.Index], fonts.Icons, icon, palette.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += fonts.S(32);
        }
        Rectangle text = new Rectangle(x, r.Y, r.Right - x - fonts.S(4), r.Height);
        TextRenderer.DrawText(g, Items[e.Index].ToString(), fonts.Body, text, palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused && ShowFocusCues) Ui.FocusRing(g, pill, 4 * fonts.Scale, palette.Text, fonts.Scale);
    }
}
