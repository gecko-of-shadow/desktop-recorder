// The tray menu (right-click on the tray icon). Bran, 2026-11-22: a clear status at the top, groups, a bold
// default command with the hotkey next to it, small icons, and the app theme (light or dark).
// The texts and the enabled state come from TrayModel (UiModel.cs, unit tested).
// Position: NotifyIcon shows the menu with ContextMenuStrip.ShowInTaskbar, which computes the place from the size
// of the menu BEFORE the Opening event (dotnet/winforms source). So this class never changes the menu in Opening:
// TrayApp calls Update at each state change and once a second while recording, and ApplyLook on the right
// mouse button down (before the button up opens the menu).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

sealed class TrayMenu : IDisposable
{
    public readonly ContextMenuStrip Strip = new ContextMenuStrip();
    public event Action<TrayCommand> Command;

    readonly TrayStatusItem status = new TrayStatusItem();
    readonly Dictionary<TrayCommand, ToolStripMenuItem> items = new Dictionary<TrayCommand, ToolStripMenuItem>();
    readonly List<ToolStripSeparator> separators = new List<ToolStripSeparator>();
    readonly TrayMenuRenderer renderer = new TrayMenuRenderer();
    UiPalette palette;
    UiFonts fonts;
    Font bold;
    Bitmap placeholder;
    TrayPhase phase;

    public TrayMenu()
    {
        Strip.ShowImageMargin = true;
        Strip.ShowCheckMargin = false;
        Strip.Renderer = renderer;
        Strip.DropShadowEnabled = true;
        Strip.Items.Add(status);
        int group = 0;
        foreach (TrayEntry e in TrayModel.Entries(new TrayState()))
        {
            if (e.Group != group)
            {
                ToolStripSeparator line = new ToolStripSeparator();
                separators.Add(line);
                Strip.Items.Add(line);
                group = e.Group;
            }
            ToolStripMenuItem item = new ToolStripMenuItem(e.Text);
            item.ImageScaling = ToolStripItemImageScaling.None;
            TrayCommand command = e.Command;
            item.Click += delegate
            {
                Action<TrayCommand> handler = Command;
                if (handler != null) handler(command);
            };
            items[command] = item;
            Strip.Items.Add(item);
        }
        ApplyLook(1f);
    }

    // Sets the scale (the DPI of the monitor where the menu opens) and the theme. Call it before the menu opens.
    public void ApplyLook(float scale)
    {
        ApplyLook(scale, Ui.CurrentPalette());
    }

    // The offscreen preview gives the theme here; the app reads it from Windows (the overload above).
    public void ApplyLook(float scale, UiPalette p)
    {
        if (fonts != null && Math.Abs(fonts.Scale - scale) < 0.01f && palette != null && palette.Dark == p.Dark) return;
        UiFonts old = fonts;
        Font oldBold = bold;
        Bitmap oldPlaceholder = placeholder;
        palette = p;
        fonts = new UiFonts(scale);
        bold = new Font(fonts.Body, FontStyle.Bold);
        int icon = fonts.S(16);
        placeholder = new Bitmap(icon, icon);
        renderer.Set(palette, fonts);
        Strip.SuspendLayout();
        Strip.Font = fonts.Body;
        Strip.BackColor = palette.Surface;
        Strip.ForeColor = palette.Text;
        Strip.Padding = new Padding(fonts.S(4));
        Strip.ImageScalingSize = new Size(icon, icon);
        status.SetLook(palette, fonts);
        foreach (ToolStripMenuItem item in items.Values)
        {
            item.Image = placeholder;
            item.Padding = new Padding(fonts.S(4), fonts.S(5), fonts.S(4), fonts.S(5));
            item.Font = item == items[TrayCommand.Record] ? bold : fonts.Body;
        }
        foreach (ToolStripSeparator line in separators)
        {
            line.AutoSize = false;
            line.Height = fonts.S(9);
        }
        Strip.ResumeLayout(true);
        if (Strip.IsHandleCreated) Ui.MenuCorners(Strip.Handle, palette.SurfaceBorder);
        if (old != null) old.Dispose();
        if (oldBold != null) oldBold.Dispose();
        if (oldPlaceholder != null) oldPlaceholder.Dispose();
    }

    // Makes the menu window now, so that its size is known before the first open, and rounds its corners.
    public void CreateWindow()
    {
        IntPtr handle = Strip.Handle;
        Ui.MenuCorners(handle, palette.SurfaceBorder);
        Strip.PerformLayout();
    }

    public void Update(TrayState state)
    {
        phase = state.Phase;
        status.Set(TrayModel.StatusTitle(state), TrayModel.StatusDetail(state), state.LastResult, state.Phase);
        foreach (TrayEntry e in TrayModel.Entries(state))
        {
            ToolStripMenuItem item = items[e.Command];
            item.Text = e.Text;
            item.ShortcutKeyDisplayString = e.Shortcut;
            item.ShowShortcutKeys = e.Shortcut != null;
            item.Enabled = e.Enabled;
            item.Tag = Glyph(e.Command);
        }
        Strip.PerformLayout();
    }

    // Only the time in the status changes (once a second while recording); the menu keeps its size.
    public void UpdateClock(TrayState state)
    {
        status.SetTitle(TrayModel.StatusTitle(state));
    }

    string Glyph(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Record: return phase == TrayPhase.Recording ? Ui.GlyphStop : Ui.GlyphRecord;
            case TrayCommand.OpenFolder: return Ui.GlyphFolder;
            case TrayCommand.ShowLastZip: return Ui.GlyphShowFile;
            case TrayCommand.CopyLastZip: return Ui.GlyphCopy;
            case TrayCommand.Settings: return Ui.GlyphSettings;
            default: return Ui.GlyphExit;
        }
    }

    public void Dispose()
    {
        Strip.Dispose();
        if (fonts != null) fonts.Dispose();
        if (bold != null) bold.Dispose();
        if (placeholder != null) placeholder.Dispose();
    }
}

// The status at the top of the menu: a colored dot, the state in bold, and the details in gray. It is not a
// command, so it does not look disabled and cannot be selected. Its width is fixed, so the time can change while
// the menu is open. ToolStripDropDownMenu puts it to the right of the icon column, so the dot is in line with
// the text of the commands.
sealed class TrayStatusItem : ToolStripItem
{
    string title = "", detail = "", last;
    TrayPhase phase;
    UiPalette palette = UiPalette.Light();
    UiFonts fonts;
    const int StatusWidth = 300;

    public override bool CanSelect { get { return false; } }

    public void SetLook(UiPalette p, UiFonts f)
    {
        palette = p;
        fonts = f;
        Font = f.Body;
    }

    public void Set(string title, string detail, string last, TrayPhase phase)
    {
        this.title = title ?? "";
        this.detail = detail ?? "";
        this.last = last;
        this.phase = phase;
        AccessibleName = this.title + ". " + this.detail + (last != null ? " " + last : "");
        Invalidate();
    }

    public void SetTitle(string title)
    {
        this.title = title ?? "";
        Invalidate();
    }

    int TextWidth() { return fonts.S(StatusWidth) - fonts.S(20) - fonts.S(12); }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        if (fonts == null) return new Size(StatusWidth, 40);
        int w = TextWidth();
        int h = fonts.S(10) + Ui.Measure(title, fonts.BodyStrong, w).Height + fonts.S(2) + Ui.Measure(detail, fonts.Caption, w).Height;
        if (!string.IsNullOrEmpty(last)) h += fonts.S(6) + Ui.Measure(last, fonts.Caption, w).Height;
        return new Size(fonts.S(StatusWidth), h + fonts.S(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (fonts == null) return;
        Graphics g = e.Graphics;
        int x = fonts.S(20), w = TextWidth(), y = fonts.S(10);
        int titleH = Ui.Measure(title, fonts.BodyStrong, w).Height;
        Color dot = phase == TrayPhase.Recording ? palette.Danger
            : phase == TrayPhase.Idle ? palette.TextSecondary : palette.Caution;
        int d = fonts.S(10);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, fonts.S(2), y + (titleH - d) / 2, d, d);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, title, fonts.BodyStrong, new Rectangle(x, y, w, titleH), palette.Text, flags);
        y += titleH + fonts.S(2);
        int detailH = Ui.Measure(detail, fonts.Caption, w).Height;
        TextRenderer.DrawText(g, detail, fonts.Caption, new Rectangle(x, y, w, detailH), palette.TextSecondary, flags);
        y += detailH;
        if (!string.IsNullOrEmpty(last))
        {
            y += fonts.S(6);
            TextRenderer.DrawText(g, last, fonts.Caption, new Rectangle(x, y, w, Ui.Measure(last, fonts.Caption, w).Height), palette.TextSecondary, flags);
        }
    }
}

// Draws the menu in the Windows 11 style: a flat surface, rounded hover marks, gray shortcut text, and icons from
// the Segoe Fluent Icons font in the text color.
sealed class TrayMenuRenderer : ToolStripRenderer
{
    UiPalette palette = UiPalette.Light();
    UiFonts fonts;

    public void Set(UiPalette p, UiFonts f)
    {
        palette = p;
        fonts = f;
    }

    float Scale { get { return fonts != null ? fonts.Scale : 1f; } }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(palette.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using (Pen p = new Pen(palette.SurfaceBorder)) e.Graphics.DrawRectangle(p, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        int inset = (int)Math.Round(2 * Scale);
        Rectangle r = new Rectangle(inset, 1, e.Item.Width - 2 * inset, e.Item.Height - 2);
        Ui.Box(e.Graphics, r, 4 * Scale, palette.Selection, Color.Empty);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        ToolStripMenuItem item = e.Item as ToolStripMenuItem;
        bool shortcut = item != null && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString;
        Color color = !e.Item.Enabled ? palette.TextDisabled : shortcut ? palette.TextSecondary : palette.Text;
        Font font = shortcut && fonts != null ? fonts.Body : e.TextFont;
        TextRenderer.DrawText(e.Graphics, e.Text, font, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        string glyph = e.Item.Tag as string;
        if (glyph == null || fonts == null || fonts.Icons == null) return;
        Color color = !e.Item.Enabled ? palette.TextDisabled
            : glyph == Ui.GlyphRecord || glyph == Ui.GlyphStop ? palette.Danger : palette.Text;
        // The image column of ToolStripDropDownMenu starts close to the edge: move the icon a little to the right.
        Rectangle r = e.ImageRectangle;
        r.Offset((int)Math.Round(4 * Scale), 0);
        TextRenderer.DrawText(e.Graphics, glyph, fonts.Icons, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        int inset = (int)Math.Round(4 * Scale);
        using (Pen p = new Pen(palette.Divider)) e.Graphics.DrawLine(p, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
}
