// Frame that shows the recorded region during a recording.
// Four thin click-through windows make a colored border OUTSIDE the region, so that the border is not in the video.
// A small Stop button shows below the region.
// All windows ask Windows to keep them out of screen captures (WDA_EXCLUDEFROMCAPTURE).
// UNVERIFIED: that gdigrab and ddagrab respect WDA_EXCLUDEFROMCAPTURE.
//
// All coordinates are physical pixels in virtual-screen coordinates. The process must be per-monitor DPI aware.
// Make, show and dispose the frame on the UI thread.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

public sealed class RecordingFrame : IDisposable
{
    static readonly Color BorderColor = Color.FromArgb(255, 255, 64, 64);

    readonly Rectangle region;
    readonly List<Form> forms = new List<Form>();
    bool shown;
    bool disposed;
    bool excludedFromCapture;
    bool partlyInside;

    /// <summary>The user clicked the Stop button.</summary>
    public event EventHandler StopClicked;

    /// <param name="region">The recorded region, in physical pixels and virtual-screen coordinates.</param>
    public RecordingFrame(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) throw new ArgumentException("The region is empty.", "region");
        this.region = region;
    }

    /// <summary>The recorded region.</summary>
    public Rectangle Region { get { return region; } }

    /// <summary>
    /// True after Show when Windows accepted WDA_EXCLUDEFROMCAPTURE for all frame windows.
    /// False on Windows older than 10 2004, or when a call failed.
    /// </summary>
    public bool ExcludedFromCapture { get { return excludedFromCapture; } }

    /// <summary>
    /// True after Show when a side of the border or the Stop button is inside the region
    /// (the region touches the screen edge). Then they are in the video, unless ExcludedFromCapture is true.
    /// </summary>
    public bool PartlyInsideRegion { get { return partlyInside; } }

    /// <summary>Shows the border and the Stop button. A second call does nothing.</summary>
    public void Show()
    {
        if (disposed) throw new ObjectDisposedException("RecordingFrame");
        if (shown) return;
        shown = true;

        Rectangle[] monitors = NativeMethods.GetAllMonitorBounds();
        Point center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        float scale = NativeMethods.GetScaleForPoint(center);
        int thickness = Math.Max(2, (int)Math.Round(2f * scale));

        Rectangle outer, inner;
        Rectangle[] sides = ComputeBorderRects(region, thickness, monitors, out outer, out inner);

        bool allExcluded = true;
        foreach (Rectangle side in sides)
        {
            if (side.Width <= 0 || side.Height <= 0) continue;
            FrameBorderForm border = new FrameBorderForm(side, BorderColor);
            forms.Add(border);
            border.ShowFrame();
            allExcluded &= border.ExcludedFromCapture;
        }

        Rectangle monitor = NativeMethods.GetMonitorBounds(region);
        Rectangle workArea = NativeMethods.GetMonitorWorkArea(region);
        bool stopInside;
        Rectangle stopBounds = ComputeStopRect(region, outer, monitor, workArea, monitors, scale, out stopInside);
        partlyInside = stopInside
            || outer.Left == region.Left || outer.Top == region.Top
            || outer.Right == region.Right || outer.Bottom == region.Bottom;
        StopButtonForm stop = new StopButtonForm(stopBounds, scale);
        stop.Clicked += OnStopButtonClicked;
        forms.Add(stop);
        stop.ShowFrame();
        allExcluded &= stop.ExcludedFromCapture;

        excludedFromCapture = allExcluded;
    }

    void OnStopButtonClicked(object sender, EventArgs e)
    {
        EventHandler handler = StopClicked;
        if (handler != null) handler(this, EventArgs.Empty);
    }

    /// <summary>Closes all frame windows.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (Form form in forms)
        {
            if (!form.IsDisposed)
            {
                form.Close();
                form.Dispose();
            }
        }
        forms.Clear();
    }

    /// <summary>
    /// Computes the four border rectangles: top, bottom, left, right.
    /// Each side goes outside the region. When the strip outside a side is not on any monitor
    /// (the region touches the edge of the screen), that side goes just inside the region, at the screen edge.
    /// A side inside the region is in the video, unless Windows keeps it out of the capture (WDA_EXCLUDEFROMCAPTURE).
    /// </summary>
    internal static Rectangle[] ComputeBorderRects(Rectangle region, int thickness, Rectangle[] monitors,
        out Rectangle outer, out Rectangle inner)
    {
        int t = thickness;
        bool topOut = IsOnMonitors(new Rectangle(region.X, region.Y - t, region.Width, t), monitors);
        bool bottomOut = IsOnMonitors(new Rectangle(region.X, region.Bottom, region.Width, t), monitors);
        bool leftOut = IsOnMonitors(new Rectangle(region.X - t, region.Y, t, region.Height), monitors);
        bool rightOut = IsOnMonitors(new Rectangle(region.Right, region.Y, t, region.Height), monitors);

        // Outer edge of the border, and inner edge of the border. The border is the area between them.
        outer = Rectangle.FromLTRB(
            region.Left - (leftOut ? t : 0),
            region.Top - (topOut ? t : 0),
            region.Right + (rightOut ? t : 0),
            region.Bottom + (bottomOut ? t : 0));
        inner = Rectangle.FromLTRB(
            region.Left + (leftOut ? 0 : t),
            region.Top + (topOut ? 0 : t),
            region.Right - (rightOut ? 0 : t),
            region.Bottom - (bottomOut ? 0 : t));
        if (inner.Width < 0 || inner.Height < 0) inner = new Rectangle(outer.X + outer.Width / 2, outer.Y + outer.Height / 2, 0, 0);

        return new Rectangle[]
        {
            Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, inner.Top),
            Rectangle.FromLTRB(outer.Left, inner.Bottom, outer.Right, outer.Bottom),
            Rectangle.FromLTRB(outer.Left, inner.Top, inner.Left, inner.Bottom),
            Rectangle.FromLTRB(inner.Right, inner.Top, outer.Right, inner.Bottom),
        };
    }

    /// <summary>
    /// Computes the bounds of the Stop button, in this order:
    /// 1. Below the border, centered, inside the work area of the monitor (not on the taskbar).
    /// 2. Above the border.
    /// 3. At the top center of another monitor that the region does not touch.
    /// 4. Inside the region, at the top center (at the bottom center when the taskbar is at the top).
    ///    Then inside is true: the button is in the video, unless Windows keeps it out of the capture.
    /// The button never goes to the bottom center of a full monitor, where the Windows 11 taskbar icons are.
    /// </summary>
    internal static Rectangle ComputeStopRect(Rectangle region, Rectangle outer, Rectangle monitor, Rectangle workArea,
        Rectangle[] monitors, float scale, out bool inside)
    {
        int w = (int)Math.Round(84 * scale);
        int h = (int)Math.Round(28 * scale);
        int gap = (int)Math.Round(4 * scale);
        inside = false;
        if (workArea.Width <= 0 || workArea.Height <= 0) workArea = monitor;

        int x = region.X + (region.Width - w) / 2;
        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - w));
        int below = outer.Bottom + gap;
        if (below + h <= workArea.Bottom && below >= workArea.Top) return new Rectangle(x, below, w, h);
        int above = outer.Top - gap - h;
        if (above >= workArea.Top && above + h <= workArea.Bottom) return new Rectangle(x, above, w, h);

        if (monitors != null)
        {
            foreach (Rectangle m in monitors)
            {
                if (m.IntersectsWith(region) || m.Width < w || m.Height < h + gap) continue;
                return new Rectangle(m.X + (m.Width - w) / 2, m.Y + gap, w, h);
            }
        }

        inside = true;
        bool taskbarAtTop = workArea.Top > monitor.Top;
        int y = taskbarAtTop ? region.Bottom - gap - h : region.Top + gap;
        Rectangle area = Rectangle.Intersect(region, workArea);
        if (area.Width <= 0 || area.Height <= 0) area = region;
        x = Math.Max(area.Left, Math.Min(x, area.Right - w));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    /// <summary>True when monitors cover the full rectangle. Monitors do not overlap.</summary>
    static bool IsOnMonitors(Rectangle r, Rectangle[] monitors)
    {
        long area = (long)r.Width * r.Height;
        if (area <= 0) return false;
        long covered = 0;
        foreach (Rectangle m in monitors)
        {
            Rectangle part = Rectangle.Intersect(r, m);
            covered += (long)part.Width * part.Height;
        }
        return covered >= area;
    }
}

/// <summary>One side of the border: a layered, click-through, topmost tool window that never takes focus.</summary>
sealed class FrameBorderForm : Form
{
    readonly Rectangle target;
    bool excludedFromCapture;

    public FrameBorderForm(Rectangle bounds, Color color)
    {
        target = bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = color;
        Bounds = bounds;
        Text = "Recording frame";
    }

    public bool ExcludedFromCapture { get { return excludedFromCapture; } }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // A layered window is not visible until it has attributes.
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
        excludedFromCapture = NativeMethods.ExcludeFromCapture(Handle);
    }

    public void ShowFrame()
    {
        Show();
        // Set the exact physical bounds. WinForms can change very small sizes.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_NCHITTEST)
        {
            m.Result = new IntPtr(NativeMethods.HTTRANSPARENT);
            return;
        }
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>The Stop button: a small topmost tool window that takes clicks but does not take focus.</summary>
sealed class StopButtonForm : Form
{
    readonly Rectangle target;
    readonly Font font;
    bool hot;
    bool excludedFromCapture;

    public event EventHandler Clicked;

    public StopButtonForm(Rectangle bounds, float scale)
    {
        target = bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Cursor = Cursors.Hand;
        Bounds = bounds;
        Text = "Stop recording";
        // Font size in pixels, so that the WinForms system-DPI scaling does not apply.
        font = new Font("Segoe UI", 13f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public bool ExcludedFromCapture { get { return excludedFromCapture; } }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        excludedFromCapture = NativeMethods.ExcludeFromCapture(Handle);
    }

    public void ShowFrame()
    {
        Show();
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // Take the click, but do not take the focus from the recorded app.
            m.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
            return;
        }
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hot = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hot = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        EventHandler handler = Clicked;
        if (handler != null) handler(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle r = ClientRectangle;
        using (SolidBrush background = new SolidBrush(hot ? Color.FromArgb(64, 64, 64) : Color.FromArgb(32, 32, 32)))
            g.FillRectangle(background, r);
        using (Pen edge = new Pen(Color.FromArgb(255, 64, 64)))
            g.DrawRectangle(edge, 0, 0, r.Width - 1, r.Height - 1);

        int icon = Math.Max(6, r.Height / 3);
        int iconX = r.Height / 3;
        int iconY = (r.Height - icon) / 2;
        using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 64, 64)))
            g.FillRectangle(red, iconX, iconY, icon, icon);

        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Rectangle textArea = Rectangle.FromLTRB(iconX + icon, 0, r.Right, r.Bottom);
        TextRenderer.DrawText(g, "Stop", font, textArea, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) font.Dispose();
        base.Dispose(disposing);
    }
}
