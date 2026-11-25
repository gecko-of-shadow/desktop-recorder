// The note box after Stop (folder mode). Bran, 2026-11-22: it opens in the middle of the monitor of the
// recording, it is large and easy to read, and it never loses typed text without a clear answer.
// The rules (which close asks, which close saves, the Enter keys) are in NoteRules (UiModel.cs, unit tested).
// The question "Save this note?" shows inside the box, so there is no second window to keep out of the video.
using System;
using System.Drawing;
using System.Windows.Forms;

sealed class NoteForm : Form
{
    // Raised with the clean note text (never empty) when the user saves, or when Windows shuts down.
    public event Action<string> Saved;
    // Raised when the user answers Back to the question of the tray Exit: the app must not close.
    public event Action QuitCancelled;

    readonly UiPalette palette;
    readonly UiFonts fonts;
    readonly Label title, intro, recording, hint, question, questionDetail;
    // The "busy, then ready" line of the ZIP (Bran, 2026-11-22): a busy bar, or a mark, and the text.
    readonly Label zipGlyph, zipText;
    readonly BusyBar zipBar = new BusyBar();
    int padX, innerWidth;
    readonly TextFrame frame = new TextFrame();
    readonly FluentButton save = new FluentButton(), noNote = new FluentButton();
    readonly FluentButton answerSave = new FluentButton(), answerDiscard = new FluentButton(), answerBack = new FluentButton();
    readonly Rectangle workArea;
    readonly int cascade;
    bool asking, decided;
    readonly bool hasRecordingLine;   // Visible is false for every control until the form shows, so keep the fact here
    NoteCloseTrigger askedFor;

    // workArea: physical px of the monitor of the recording (the box is per-monitor DPI aware, see
    // TrayApp.ShowNoteBox). scale: the DPI scale of that monitor. cascade: the number of note boxes already open.
    public NoteForm(string recordingName, double seconds, Rectangle workArea, float scale, int cascade, UiPalette palette)
    {
        this.palette = palette;
        this.workArea = workArea;
        this.cascade = cascade;
        fonts = new UiFonts(scale);
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = NoteRules.WindowTitle;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        Font = fonts.Body;

        title = Ui.Text(this, NoteRules.Title, fonts.Subtitle, palette.Text, palette.Window, 0, 0, 10);
        intro = Ui.Text(this, NoteRules.Intro, fonts.Body, palette.TextSecondary, palette.Window, 0, 0, 10);
        recording = Ui.Text(this, NoteRules.RecordingLine(recordingName, seconds) ?? "", fonts.Caption, palette.TextSecondary, palette.Window, 0, 0, 10);
        zipGlyph = Ui.Text(this, Ui.GlyphCheck, fonts.Icons ?? fonts.Body, palette.Success, palette.Window, 0, 0, 10);
        zipText = Ui.Text(this, ZipStatus.BusyText(ZipStep.Stopping), fonts.Body, palette.TextSecondary, palette.Window, 0, 0, 10);
        zipText.AutoEllipsis = true;
        zipBar.Style(palette, palette.Window);
        Controls.Add(zipBar);
        zipGlyph.Visible = false;
        hint = Ui.Text(this, NoteRules.Hint, fonts.Caption, palette.TextSecondary, palette.Window, 0, 0, 10);
        question = Ui.Text(this, NoteRules.Question, fonts.BodyStrong, palette.Text, palette.Window, 0, 0, 10);
        questionDetail = Ui.Text(this, "", fonts.Body, palette.Text, palette.Window, 0, 0, 10);
        hasRecordingLine = recording.Text.Length > 0;
        recording.Visible = hasRecordingLine;

        frame.Box.Multiline = true;
        frame.Box.AcceptsReturn = true;   // Enter reaches KeyDown below: Enter saves, Shift+Enter adds a line
        frame.Box.WordWrap = true;
        // Set once: a change of ScrollBars makes the edit window again (the undo and an IME input are lost).
        frame.Box.ScrollBars = ScrollBars.Vertical;
        frame.Box.MaxLength = NoteRules.MaxLength;
        frame.Box.AccessibleName = NoteRules.Title;
        frame.Box.AccessibleDescription = NoteRules.Intro + " " + NoteRules.Hint;
        frame.Style(palette, scale, fonts.Body);
        frame.Box.KeyDown += OnBoxKeyDown;
        Controls.Add(frame);

        SetUpButton(save, "&" + NoteRules.SaveText, true, delegate { TryClose(NoteCloseTrigger.SaveButton); });
        SetUpButton(noNote, "&" + NoteRules.NoNoteText, false, delegate { TryClose(NoteCloseTrigger.NoNoteButton); });
        SetUpButton(answerSave, "&" + NoteRules.AnswerSave, true, delegate { Answer(NoteAnswer.Save); });
        SetUpButton(answerDiscard, "&" + NoteRules.AnswerDiscard, false, delegate { Answer(NoteAnswer.Discard); });
        SetUpButton(answerBack, "&" + NoteRules.AnswerBack, false, delegate { Answer(NoteAnswer.Back); });

        // Tab order: the text, then the buttons from left to right.
        frame.TabIndex = 0;
        save.TabIndex = 1; noNote.TabIndex = 2;
        answerSave.TabIndex = 3; answerDiscard.TabIndex = 4; answerBack.TabIndex = 5;
        SetMode(false);
        DoLayout();
        ActiveControl = frame.Box;
        ResumeLayout(false);
    }

    // Shows the state of the ZIP work. text comes from ZipStatus.NoteText.
    public void SetZipStatus(ZipStep step, string text)
    {
        bool busy = ZipStatus.Busy(step);
        bool failed = step == ZipStep.Failed;
        zipBar.Visible = busy;
        zipBar.Running = busy;
        bool mark = !busy && fonts.Icons != null;
        zipGlyph.Visible = mark;
        zipGlyph.Text = failed ? Ui.GlyphWarning : Ui.GlyphCheck;
        zipGlyph.ForeColor = failed ? palette.Danger : palette.Success;
        zipText.Font = busy ? fonts.Body : fonts.BodyStrong;
        zipText.ForeColor = failed ? palette.Danger : busy ? palette.TextSecondary : palette.Text;
        zipText.Text = text ?? "";
        zipText.AccessibleName = zipText.Text;
        int x = padX + (busy ? S(68) : mark ? S(34) : 0);
        zipText.SetBounds(x, zipText.Top, innerWidth - (x - padX), zipText.Height);
    }

    void SetUpButton(FluentButton b, string text, bool accent, EventHandler click)
    {
        b.Text = text;
        b.Accent = accent;
        b.Style(palette, fonts.Scale, fonts.Body);
        b.Click += click;
        Controls.Add(b);
    }

    int S(int epx) { return fonts.S(epx); }

    // All positions come from the scale. The box keeps one size in both modes, so it does not jump.
    void DoLayout()
    {
        int pad = S(24), width = S(560), inner = width - 2 * pad;
        int y = pad;
        Place(title, pad, ref y, inner, S(8));
        Place(intro, pad, ref y, inner, S(12));
        if (hasRecordingLine) Place(recording, pad, ref y, inner, S(8));
        // The ZIP line: room for two lines of text, so the box does not grow when the text changes.
        padX = pad;
        innerWidth = inner;
        int lineH = Ui.Measure("Ag", fonts.Body, inner).Height;
        int rowH = Ui.Measure("Ag\nAg", fonts.BodyStrong, inner).Height;
        zipBar.SetBounds(pad, y + (lineH - S(4)) / 2, S(56), S(4));
        zipGlyph.SetBounds(pad, y, S(28), lineH);
        zipText.SetBounds(pad + S(68), y, inner - S(68), rowH);
        y += rowH + S(8);
        frame.Place(pad, y, inner, S(132));
        y += S(132) + S(10);
        int hintTop = y;
        int hintH = Ui.Measure(hint.Text, hint.Font, inner).Height;
        hint.SetBounds(pad, y, inner, hintH);
        // The question and its detail take the place of the hint.
        int qH = Ui.Measure(question.Text, question.Font, inner).Height;
        int dH = Math.Max(Ui.Measure(NoteRules.QuestionDetail(NoteCloseTrigger.EscapeKey), questionDetail.Font, inner).Height,
            Ui.Measure(NoteRules.QuestionDetail(NoteCloseTrigger.QuitApp), questionDetail.Font, inner).Height);
        question.SetBounds(pad, hintTop, inner, qH);
        questionDetail.SetBounds(pad, hintTop + qH + S(2), inner, dH);
        y = hintTop + Math.Max(hintH, qH + S(2) + dH) + S(24);
        int bh = S(32), gap = S(8);
        int right = width - pad;
        right = PlaceButtons(right, y, bh, gap, noNote, save);
        right = width - pad;
        PlaceButtons(right, y, bh, gap, answerBack, answerDiscard, answerSave);
        ClientSize = new Size(width, y + bh + pad);
        Point p = new Point(workArea.X + (workArea.Width - Width) / 2 + cascade * S(28),
            workArea.Y + (workArea.Height - Height) / 2 + cascade * S(28));
        p.X = Math.Max(workArea.X, Math.Min(p.X, workArea.Right - Width));
        p.Y = Math.Max(workArea.Y, Math.Min(p.Y, workArea.Bottom - Height));
        Location = p;
    }

    static void Place(Label label, int x, ref int y, int width, int gapAfter)
    {
        int h = Ui.Measure(label.Text, label.Font, width).Height;
        label.SetBounds(x, y, width, h);
        y += h + gapAfter;
    }

    // Right-aligned, from the right edge to the left: the first button in the list is the rightmost.
    int PlaceButtons(int right, int y, int height, int gap, params FluentButton[] buttons)
    {
        foreach (FluentButton b in buttons)
        {
            int w = Math.Max(S(120), TextRenderer.MeasureText(b.Text.Replace("&", ""), b.Font).Width + S(32));
            right -= w;
            b.SetBounds(right, y, w, height);
            right -= gap;
        }
        return right;
    }

    void SetMode(bool ask)
    {
        asking = ask;
        hint.Visible = !ask;
        save.Visible = !ask;
        noNote.Visible = !ask;
        question.Visible = ask;
        questionDetail.Visible = ask;
        answerSave.Visible = ask;
        answerDiscard.Visible = ask;
        answerBack.Visible = ask;
        AcceptButton = ask ? answerSave : save;
    }

    void ShowQuestion(NoteCloseTrigger trigger)
    {
        askedFor = trigger;
        questionDetail.Text = NoteRules.QuestionDetail(trigger);
        // A screen reader reads the question with the button that gets the focus.
        answerSave.AccessibleDescription = NoteRules.Question + " " + questionDetail.Text;
        SetMode(true);
        answerSave.Focus();
        if (!Ui.IsForeground(Handle)) Ui.FlashUntilForeground(Handle);
    }

    void TryClose(NoteCloseTrigger trigger)
    {
        Act(NoteRules.Decide(trigger, frame.Box.Text), trigger);
    }

    void Answer(NoteAnswer answer)
    {
        NoteCloseTrigger trigger = askedFor;
        NoteCloseAction action = NoteRules.AfterAnswer(answer);
        if (action == NoteCloseAction.StayOpen)
        {
            SetMode(false);
            frame.Box.Focus();
            if (trigger == NoteCloseTrigger.QuitApp)
            {
                Action handler = QuitCancelled;
                if (handler != null) handler();
            }
            return;
        }
        Act(action, trigger);
    }

    void Act(NoteCloseAction action, NoteCloseTrigger trigger)
    {
        if (action == NoteCloseAction.Ask) { ShowQuestion(trigger); return; }
        if (action == NoteCloseAction.Save) RaiseSaved();
        if (action == NoteCloseAction.Save || action == NoteCloseAction.CloseWithoutNote)
        {
            decided = true;
            Close();
        }
    }

    string savedNote;   // the text that Saved got last: the same text is not saved two times

    void RaiseSaved()
    {
        string note = NoteRules.Clean(frame.Box.Text);
        Action<string> handler = Saved;
        if (note == null || note == savedNote || handler == null) return;
        savedNote = note;
        handler(note);
    }

    // Exit in the tray menu. True when the box closed at once (no text). False when it asks: the answer then
    // closes the box (Save, Discard) or raises QuitCancelled (Back).
    public bool CloseForQuit()
    {
        if (IsDisposed) return true;
        NoteCloseAction action = NoteRules.Decide(NoteCloseTrigger.QuitApp, frame.Box.Text);
        if (action == NoteCloseAction.CloseWithoutNote)
        {
            decided = true;
            Close();
            return true;
        }
        ShowQuestion(NoteCloseTrigger.QuitApp);
        Activate();
        return false;
    }

    void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.A && e.Control && !e.Alt)
        {
            frame.Box.SelectAll();
            e.SuppressKeyPress = true;
            return;
        }
        NoteKeyAction action = NoteRules.KeyAction(e.KeyCode == Keys.Enter, e.Shift, e.Control, e.Alt);
        if (action == NoteKeyAction.None) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (action == NoteKeyAction.Save) { TryClose(NoteCloseTrigger.SaveButton); return; }
        TextBox box = frame.Box;
        if (box.TextLength - box.SelectionLength + 2 <= box.MaxLength) box.SelectedText = "\r\n";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (asking) Answer(NoteAnswer.Back);
            else TryClose(NoteCloseTrigger.EscapeKey);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // The X button, Alt+F4, the taskbar "Close window", and Windows shutdown come here.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!decided)
        {
            bool shutdown = e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing
                || e.CloseReason == CloseReason.ApplicationExitCall;
            NoteCloseTrigger trigger = shutdown ? NoteCloseTrigger.Shutdown : NoteCloseTrigger.CloseBox;
            NoteCloseAction action = NoteRules.Decide(trigger, frame.Box.Text);
            if (action == NoteCloseAction.Ask)
            {
                e.Cancel = true;
                if (!asking) ShowQuestion(trigger);
            }
            else if (trigger == NoteCloseTrigger.Shutdown)
            {
                // Save, but stay undecided: when another program refuses the sign-out, the box stays open and
                // must ask again for new text (review 2026-11-22).
                if (action == NoteCloseAction.Save) RaiseSaved();
            }
            else decided = true;
        }
        base.OnFormClosing(e);
    }

    // The note box opens while FFmpeg still captures the last frames. Keep it out of the video.
    // Real recording on 2026-11-19: the box was in the last 2 frames and made 2 false "scroll" rows.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
        Ui.TitleBar(Handle, palette.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        frame.Box.Focus();
        // The box takes the focus when Windows lets it (the hotkey or the Stop click is input to this app).
        // When it does not have the focus, the taskbar button flashes until the user opens the box.
        if (!Ui.IsForeground(Handle)) Ui.FlashUntilForeground(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fonts.Dispose();
        base.Dispose(disposing);
    }

    // For the offscreen preview (test\ui_preview.cs): the question as it shows after Esc.
    public void PreviewQuestion(string text, NoteCloseTrigger trigger)
    {
        frame.Box.Text = text;
        askedFor = trigger;
        questionDetail.Text = NoteRules.QuestionDetail(trigger);
        SetMode(true);
    }
}

// The small popup at the bottom center of the monitor of the recording (Bran, 2026-11-22). While the work runs and
// the note box is closed: "Making the ZIP... (step: ...)". When the ZIP is on the clipboard: "ZIP ready: Ctrl+V to
// paste (33.7 MB)" for 4 s. It does not take the focus (WS_EX_NOACTIVATE), it stays on top, it is not in the video
// (ExcludeFromCapture), and a click closes it. Tray balloons do not show on Bran's PC (HANDOFF 6.7), so the app does
// not use them for this.
sealed class ZipToast : Form
{
    public readonly Rectangle WorkArea;
    public bool ClosedByUser;    // a click closed it
    readonly UiPalette palette;
    readonly UiFonts fonts;
    readonly Label glyph, line, detail;
    readonly BusyBar bar = new BusyBar();
    readonly Timer closeTimer = new Timer();
    const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;

    // workArea: physical px (the popup is per-monitor DPI aware, like the note box). scale: the DPI scale there.
    public ZipToast(Rectangle workArea, float scale, UiPalette palette)
    {
        WorkArea = workArea;
        this.palette = palette;
        fonts = new UiFonts(scale);
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Desktop Recorder";
        BackColor = palette.Surface;
        Font = fonts.Body;
        glyph = Ui.Text(this, Ui.GlyphCheck, fonts.Icons ?? fonts.Body, palette.Success, palette.Surface, 0, 0, 10);
        line = Ui.Text(this, "", fonts.BodyStrong, palette.Text, palette.Surface, 0, 0, 10);
        detail = Ui.Text(this, "", fonts.Caption, palette.TextSecondary, palette.Surface, 0, 0, 10);
        bar.Style(palette, palette.Surface);
        Controls.Add(bar);
        foreach (Control c in new Control[] { this, glyph, line, detail, bar })
            c.MouseUp += delegate
            {
                ClosedByUser = true;
                Close();
            };
        closeTimer.Tick += delegate
        {
            closeTimer.Stop();
            Close();
        };
        ResumeLayout(false);
    }

    // Shown without the focus. TopMost is set with the window style, not with the TopMost property: the property
    // calls SetWindowPos without SWP_NOACTIVATE and can take the focus.
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ExcludeFromCapture(Handle);
        Ui.PopupCorners(Handle, palette.SurfaceBorder);
    }

    int S(int epx) { return fonts.S(epx); }

    // text and detailText come from ZipStatus.PopupText and PopupDetail. The popup closes by itself after
    // ZipStatus.PopupSeconds (ready, failed), or stays (busy).
    public void Set(ZipStep step, string text, string detailText)
    {
        Set(step, text, detailText, 0);
    }

    // minSeconds: the popup stays at least this long (a ready popup with a warning).
    public void Set(ZipStep step, string text, string detailText, int minSeconds)
    {
        bool busy = ZipStatus.Busy(step);
        bool failed = step == ZipStep.Failed;
        bar.Visible = busy;
        bar.Running = busy;
        bool mark = !busy && fonts.Icons != null;
        glyph.Visible = mark;
        glyph.Text = failed ? Ui.GlyphWarning : Ui.GlyphCheck;
        glyph.ForeColor = failed ? palette.Danger : palette.Success;
        line.Text = text ?? "";
        line.ForeColor = failed ? palette.Danger : palette.Text;
        detail.Text = detailText ?? "";
        bool hasDetail = detail.Text.Length > 0;
        detail.Visible = hasDetail;
        AccessibleName = line.Text + (hasDetail ? ". " + detail.Text : "");
        int pad = S(16), lead = busy ? S(56) + S(12) : mark ? S(28) + S(8) : 0;
        int textW = Math.Max(TextRenderer.MeasureText(line.Text, line.Font).Width, S(220));
        if (hasDetail) textW = Math.Max(textW, Math.Min(S(420), TextRenderer.MeasureText(detail.Text, detail.Font).Width));
        textW = Math.Min(textW + S(4), Math.Max(S(220), WorkArea.Width - 2 * pad - lead - S(48)));
        int lineH = Ui.Measure(line.Text, line.Font, textW).Height;
        int detailH = hasDetail ? Ui.Measure(detail.Text, detail.Font, textW).Height : 0;
        int y = S(12);
        line.SetBounds(pad + lead, y, textW, lineH);
        if (hasDetail) detail.SetBounds(pad + lead, y + lineH + S(2), textW, detailH);
        int height = y + lineH + (hasDetail ? S(2) + detailH : 0) + S(12);
        int firstLine = Ui.Measure("Ag", line.Font, textW).Height;
        bar.SetBounds(pad, y + (firstLine - S(4)) / 2, S(56), S(4));
        glyph.SetBounds(pad, y, S(28), firstLine);
        ClientSize = new Size(pad + lead + textW + pad, height);
        Location = new Point(WorkArea.X + (WorkArea.Width - Width) / 2, WorkArea.Bottom - Height - S(24));
        int seconds = ZipStatus.PopupSeconds(step);
        closeTimer.Stop();
        if (seconds > 0) seconds = Math.Max(seconds, minSeconds);
        if (seconds > 0)
        {
            closeTimer.Interval = seconds * 1000;
            closeTimer.Start();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // A 1 px border, also where Windows does not round the corners (Windows 10).
        using (Pen p = new Pen(palette.SurfaceBorder)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closeTimer.Dispose();
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }
}
