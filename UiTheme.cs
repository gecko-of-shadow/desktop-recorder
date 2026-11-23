// Look of the note box, the Settings window and the tray menu: colors, fonts, scale and small custom controls.
// Every size is in effective pixels (epx, 96 DPI) and goes through Ui.S(scale, epx). The windows use
// AutoScaleMode.None and lay out their controls with an explicit scale, so a test can render them at any scale.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

sealed class UiPalette
{
    public bool Dark;
    public Color Window, Surface, SurfaceBorder, Divider;
    public Color Text, TextSecondary, TextDisabled;
    public Color Accent, AccentHover, AccentPressed, AccentText;
    public Color ControlFill, ControlHover, ControlPressed, ControlBorder;
    public Color Selection, Danger, Caution;

    // Colors after the Windows 11 (Fluent) light and dark themes, with the default Windows blue accent.
    public static UiPalette Light()
    {
        UiPalette p = new UiPalette();
        p.Window = Rgb(0xF3F3F3); p.Surface = Rgb(0xFBFBFB); p.SurfaceBorder = Rgb(0xE5E5E5); p.Divider = Rgb(0xE5E5E5);
        p.Text = Rgb(0x1B1B1B); p.TextSecondary = Rgb(0x5D5D5D); p.TextDisabled = Rgb(0x9E9E9E);
        p.Accent = Rgb(0x005FB8); p.AccentHover = Rgb(0x1A6FC0); p.AccentPressed = Rgb(0x3380C8); p.AccentText = Color.White;
        p.ControlFill = Rgb(0xFEFEFE); p.ControlHover = Rgb(0xF5F5F5); p.ControlPressed = Rgb(0xEDEDED); p.ControlBorder = Rgb(0xCFCFCF);
        p.Selection = Rgb(0xE8E8E8); p.Danger = Rgb(0xC42B1C); p.Caution = Rgb(0x9D5D00);
        return p;
    }

    public static UiPalette DarkPalette()
    {
        UiPalette p = new UiPalette();
        p.Dark = true;
        p.Window = Rgb(0x202020); p.Surface = Rgb(0x2B2B2B); p.SurfaceBorder = Rgb(0x3A3A3A); p.Divider = Rgb(0x3A3A3A);
        p.Text = Color.White; p.TextSecondary = Rgb(0xC8C8C8); p.TextDisabled = Rgb(0x7A7A7A);
        p.Accent = Rgb(0x60CDFF); p.AccentHover = Rgb(0x5AB8E6); p.AccentPressed = Rgb(0x4FA3CC); p.AccentText = Color.Black;
        p.ControlFill = Rgb(0x2D2D2D); p.ControlHover = Rgb(0x333333); p.ControlPressed = Rgb(0x282828); p.ControlBorder = Rgb(0x454545);
        p.Selection = Rgb(0x383838); p.Danger = Rgb(0xFF99A4); p.Caution = Rgb(0xFCE100);
        return p;
    }

    static Color Rgb(int rgb)
    {
        return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}

// Fonts in pixels for one scale: Segoe UI, as in Windows 11 (14 epx body text, 12 epx captions, 20 epx titles).
// Pixel units make the size independent of the DPI of the process, so the offscreen preview is exact.
sealed class UiFonts : IDisposable
{
    public readonly float Scale;
    public readonly Font Body, BodyStrong, Caption, Subtitle;
    public readonly Font Icons;   // Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10), or null

    public UiFonts(float scale)
    {
        Scale = scale;
        Body = Make("Segoe UI", 14, FontStyle.Regular);
        Caption = Make("Segoe UI", 12, FontStyle.Regular);
        BodyStrong = Has("Segoe UI Semibold") ? Make("Segoe UI Semibold", 14, FontStyle.Regular) : Make("Segoe UI", 14, FontStyle.Bold);
        Subtitle = Has("Segoe UI Semibold") ? Make("Segoe UI Semibold", 20, FontStyle.Regular) : Make("Segoe UI", 20, FontStyle.Bold);
        if (Has("Segoe Fluent Icons")) Icons = Make("Segoe Fluent Icons", 16, FontStyle.Regular);
        else if (Has("Segoe MDL2 Assets")) Icons = Make("Segoe MDL2 Assets", 16, FontStyle.Regular);
    }

    public Font Make(string family, float epx, FontStyle style)
    {
        string name = Has(family) ? family : SystemFonts.MessageBoxFont.FontFamily.Name;
        return new Font(name, Math.Max(1f, epx * Scale), style, GraphicsUnit.Pixel);
    }

    // True when the font is installed. GDI+ puts another font in place of a missing one without an error.
    public static bool Has(string family)
    {
        try
        {
            using (FontFamily f = new FontFamily(family)) return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public int S(int epx)
    {
        return Ui.S(Scale, epx);
    }

    public void Dispose()
    {
        Body.Dispose(); BodyStrong.Dispose(); Caption.Dispose(); Subtitle.Dispose();
        if (Icons != null) Icons.Dispose();
    }
}

static class Ui
{
    // Glyphs of Segoe Fluent Icons and Segoe MDL2 Assets (same code points in both fonts).
    public const string GlyphRecord = "";
    public const string GlyphStop = "";
    public const string GlyphFolder = "";
    public const string GlyphShowFile = "";
    public const string GlyphCopy = "";
    public const string GlyphSettings = "";
    public const string GlyphExit = "";
    public const string GlyphVideo = "";
    public const string GlyphQuality = "";
    public const string GlyphKeyboard = "";
    public const string GlyphRobot = "";
    public const string GlyphPower = "";

    public static int S(float scale, int epx)
    {
        return (int)Math.Round(epx * scale);
    }

    // The app theme (Settings > Personalization > Colors > "Choose your default app mode"). One switch for all
    // three UI parts. Bran's PC has app mode light and Windows mode dark (2026-11-22), so the tray menu is light
    // there. To follow the Windows mode (the taskbar) instead, read "SystemUsesLightTheme" here.
    // Microsoft documents the WinRT UISettings check for this; .NET Framework 4 with csc.exe cannot call WinRT
    // easily, so the app reads the registry value that that setting writes. A missing value means light.
    public const string ThemeValue = "AppsUseLightTheme";

    public static bool DarkMode()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = key == null ? null : key.GetValue(ThemeValue);
                return v is int && (int)v == 0;
            }
        }
        catch (Exception) { return false; }
    }

    public static UiPalette CurrentPalette()
    {
        return DarkMode() ? UiPalette.DarkPalette() : UiPalette.Light();
    }

    // ------------------------------------------------------------------ native calls (Microsoft Learn: DwmSetWindowAttribute, FlashWindowEx)

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWCP_ROUNDSMALL = 3;
    const uint FLASHW_ALL = 3;
    const uint FLASHW_TIMERNOFG = 0x0C;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // A dark title bar in dark mode (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 11; older builds ignore it).
    public static void TitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, 4); }
        catch (Exception) { }
    }

    // Small rounded corners and the border color for a custom menu (Windows 11; Windows 10 ignores it).
    public static void MenuCorners(IntPtr hwnd, Color border)
    {
        try
        {
            int corner = DWMWCP_ROUNDSMALL;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            int colorRef = border.R | (border.G << 8) | (border.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, 4);
        }
        catch (Exception) { }
    }

    public static bool IsForeground(IntPtr hwnd)
    {
        try { return GetForegroundWindow() == hwnd; }
        catch (Exception) { return true; }
    }

    // Flashes the caption and the taskbar button until the window comes to the foreground.
    public static void FlashUntilForeground(IntPtr hwnd)
    {
        try
        {
            FLASHWINFO info = new FLASHWINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            info.hwnd = hwnd;
            info.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
            FlashWindowEx(ref info);
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ drawing helpers

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Fills a rounded rectangle and draws its 1 px border inside the bounds.
    public static void Box(Graphics g, Rectangle bounds, float radius, Color fill, Color border)
    {
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF r = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1f, bounds.Height - 1f);
        using (GraphicsPath path = RoundRect(r, radius))
        {
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border != Color.Empty) using (Pen p = new Pen(border)) g.DrawPath(p, path);
        }
        g.SmoothingMode = old;
    }

    // The keyboard focus mark: a 2 px ring in the text color (Fluent focus visual).
    public static void FocusRing(Graphics g, Rectangle bounds, float radius, Color color, float scale)
    {
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Math.Max(1.5f, 2f * scale);
        RectangleF r = new RectangleF(bounds.X + w / 2, bounds.Y + w / 2, bounds.Width - w, bounds.Height - w);
        using (GraphicsPath path = RoundRect(r, radius))
        using (Pen p = new Pen(color, w)) g.DrawPath(p, path);
        g.SmoothingMode = old;
    }

    public static Size Measure(string text, Font font, int width)
    {
        TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
        return TextRenderer.MeasureText(text ?? "", font, new Size(Math.Max(1, width), int.MaxValue), flags);
    }

    // A label that wraps at the width and is exactly as high as its text.
    public static Label Text(Control parent, string text, Font font, Color color, Color back, int x, int y, int width)
    {
        Label label = new Label();
        label.AutoSize = false;
        label.UseMnemonic = false;
        label.Font = font;
        label.ForeColor = color;
        label.BackColor = back;
        label.Text = text;
        Size size = Measure(text, font, width);
        label.SetBounds(x, y, width, size.Height);
        parent.Controls.Add(label);
        return label;
    }
}

// A push button in the Windows 11 style: an accent button (the main command) or a standard button.
// It stays a Button, so Enter, Space, AcceptButton, CancelButton, mnemonics and accessibility work as before.
sealed class FluentButton : Button
{
    public bool Accent;
    UiPalette palette = UiPalette.Light();
    float scale = 1f;
    bool hover, pressed;

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void Style(UiPalette p, float s, Font font)
    {
        palette = p;
        scale = s;
        Font = font;
        BackColor = p.Window;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : palette.Window);
        Color fill, border, text;
        if (!Enabled)
        {
            fill = Accent ? palette.TextDisabled : palette.ControlFill;
            border = Accent ? fill : palette.ControlBorder;
            text = Accent ? palette.Window : palette.TextDisabled;
        }
        else if (Accent)
        {
            fill = pressed ? palette.AccentPressed : hover ? palette.AccentHover : palette.Accent;
            border = fill;
            text = palette.AccentText;
        }
        else
        {
            fill = pressed ? palette.ControlPressed : hover ? palette.ControlHover : palette.ControlFill;
            border = palette.ControlBorder;
            text = palette.Text;
        }
        Rectangle r = ClientRectangle;
        Ui.Box(g, r, 4 * scale, fill, border);
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(g, Text, Font, r, text, flags);
        if (Focused && ShowFocusCues) Ui.FocusRing(g, r, 4 * scale, palette.Text, scale);
    }
}

// An On/Off switch in the Windows 11 style. It stays a CheckBox: Space switches it, and screen readers read it
// as a check box with its AccessibleName. The text "On" or "Off" is at the left of the switch.
sealed class ToggleSwitch : CheckBox
{
    UiPalette palette = UiPalette.Light();
    float scale = 1f;
    bool hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Text = "";
    }

    public void Style(UiPalette p, float s, Font font, Color back)
    {
        palette = p;
        scale = s;
        Font = font;
        BackColor = back;
        Size = new Size(Ui.S(s, 88), Ui.S(s, 32));
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        int trackW = Ui.S(scale, 40), trackH = Ui.S(scale, 20);
        Rectangle track = new Rectangle(Width - trackW - Ui.S(scale, 4), (Height - trackH) / 2, trackW, trackH);
        Color text = Enabled ? palette.Text : palette.TextDisabled;
        Rectangle label = new Rectangle(0, 0, track.X - Ui.S(scale, 10), Height);
        TextRenderer.DrawText(g, Checked ? "On" : "Off", Font, label, text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF t = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f);
        float knob = (Checked ? 12f : 10f) * scale * (hover && Enabled ? 1.15f : 1f);
        float cy = track.Y + track.Height / 2f;
        float cx = Checked ? track.Right - track.Height / 2f : track.X + track.Height / 2f;
        using (GraphicsPath path = Ui.RoundRect(t, t.Height / 2f))
        {
            if (Checked)
            {
                Color fill = Enabled ? (hover ? palette.AccentHover : palette.Accent) : palette.TextDisabled;
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (SolidBrush k = new SolidBrush(Enabled ? palette.AccentText : palette.Window))
                    g.FillEllipse(k, cx - knob / 2f, cy - knob / 2f, knob, knob);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(hover && Enabled ? palette.ControlHover : palette.ControlFill)) g.FillPath(b, path);
                Color edge = Enabled ? palette.TextSecondary : palette.TextDisabled;
                using (Pen p = new Pen(edge, Math.Max(1f, scale))) g.DrawPath(p, path);
                using (SolidBrush k = new SolidBrush(edge)) g.FillEllipse(k, cx - knob / 2f, cy - knob / 2f, knob, knob);
            }
        }
        g.SmoothingMode = old;
        if (Focused && ShowFocusCues)
        {
            Rectangle ring = Rectangle.Inflate(track, Ui.S(scale, 3), Ui.S(scale, 3));
            Ui.FocusRing(g, ring, ring.Height / 2f, palette.Text, scale);
        }
    }
}

// A panel that paints a card: a rounded rectangle with a 1 px border, on the color of its parent.
sealed class CardPanel : Panel
{
    public UiPalette Palette = UiPalette.Light();
    public float UiScale = 1f;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Palette.Window);
        Ui.Box(e.Graphics, ClientRectangle, 4 * UiScale, Palette.Surface, Palette.SurfaceBorder);
    }
}

// A text box in the Windows 11 style: a borderless TextBox inside a rounded frame, with an accent line at the
// bottom while it has the focus.
sealed class TextFrame : Panel
{
    public readonly TextBox Box = new TextBox();
    UiPalette palette = UiPalette.Light();
    float scale = 1f;

    public TextFrame()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Box.BorderStyle = BorderStyle.None;
        Controls.Add(Box);
        Box.GotFocus += delegate { Invalidate(); };
        Box.LostFocus += delegate { Invalidate(); };
    }

    public void Style(UiPalette p, float s, Font font)
    {
        palette = p;
        scale = s;
        Box.Font = font;
        Box.BackColor = p.ControlFill;
        Box.ForeColor = p.Text;
        Invalidate();
    }

    // Places the frame and the text box in it. For one line, the frame is 32 epx high.
    public void Place(int x, int y, int width, int height)
    {
        SetBounds(x, y, width, height);
        int padX = Ui.S(scale, 10), padY = Ui.S(scale, 6);
        int boxH = Box.Multiline ? height - 2 * padY : Box.PreferredHeight;
        Box.SetBounds(padX, Box.Multiline ? padY : (height - boxH) / 2, width - 2 * padX, boxH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : palette.Window);
        Ui.Box(g, ClientRectangle, 4 * scale, palette.ControlFill, palette.ControlBorder);
        if (Box.Focused)
        {
            int h = Math.Max(2, Ui.S(scale, 2));
            using (SolidBrush b = new SolidBrush(palette.Accent))
                g.FillRectangle(b, Ui.S(scale, 2), Height - h, Width - 2 * Ui.S(scale, 2), h);
        }
    }
}
