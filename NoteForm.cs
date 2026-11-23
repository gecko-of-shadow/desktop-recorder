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
