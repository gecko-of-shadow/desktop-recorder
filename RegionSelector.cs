// Region selector, based on the ShareX region capture.
// One borderless topmost form covers all monitors. It shows a frozen, dimmed screenshot.
// The selected area shows at full brightness.
//
// Mouse:  move = highlight the window under the cursor. Click = select that window. Drag = select a rectangle.
//         Shift while dragging keeps a square. Ctrl while dragging moves the new rectangle.
//         Drag a handle to resize the selection. Drag inside the selection to move it.
//         Double-click inside the selection = accept. Right-click = clear the selection, or cancel when there is none.
// Keys:   Enter = accept. Esc = cancel. Space = full virtual screen (accept). ~ = monitor under the cursor (accept).
//         Arrow keys move the selection 1 px (Shift = 10 px). Ctrl + arrow keys change the size.
//         With no selection, or while a mouse button is down, the arrow keys move the cursor.
//         C = crosshair on/off. M = magnifier on/off.
//
// All coordinates are physical pixels. The process must be per-monitor DPI aware (NativeMethods.EnableDpiAwareness).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

public static class RegionSelector
{
    static bool showCrosshair;
    static bool showMagnifier = true;
    static bool showPosition = true;

    /// <summary>Draw a crosshair across the full virtual screen at the cursor.</summary>
    public static bool ShowCrosshair { get { return showCrosshair; } set { showCrosshair = value; } }

    /// <summary>Draw the magnifier (10 x 10 px, 8 times) next to the cursor.</summary>
    public static bool ShowMagnifier { get { return showMagnifier; } set { showMagnifier = value; } }

    /// <summary>Show the position of the selection next to its size.</summary>
    public static bool ShowPosition { get { return showPosition; } set { showPosition = value; } }

    /// <summary>
    /// Shows the selector and waits for the user. Returns the selected region in physical pixels and
    /// virtual-screen coordinates, with an even width and an even height. Returns null when the user cancels.
    /// When lastRegion is set and is on the screen, the selector starts with it selected (Enter records it again).
    /// Call this on the UI thread.
    /// </summary>
    public static Rectangle? Show(Rectangle? lastRegion)
    {
        Rectangle virtualScreen = NativeMethods.GetVirtualScreen();
        if (virtualScreen.Width < 2 || virtualScreen.Height < 2) return null;

        using (Bitmap shot = CaptureScreen(virtualScreen))
        using (RegionSelectorForm form = new RegionSelectorForm(virtualScreen, shot, lastRegion))
        {
            form.ShowDialog();
            return form.Result;
        }
    }

    /// <summary>
    /// Clips the region to the virtual screen and makes the width and the height even (H.264 needs even sizes).
    /// Returns null when less than 2 x 2 px stays. Use this for "record last region" without the selector.
    /// </summary>
    public static Rectangle? NormalizeRegion(Rectangle region)
    {
        Rectangle r = Rectangle.Intersect(region, NativeMethods.GetVirtualScreen());
        r.Width -= r.Width % 2;
        r.Height -= r.Height % 2;
        if (r.Width < 2 || r.Height < 2) return null;
        return r;
    }

    static Bitmap CaptureScreen(Rectangle virtualScreen)
    {
        Bitmap bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            try
            {
                g.CopyFromScreen(virtualScreen.X, virtualScreen.Y, 0, 0, virtualScreen.Size,
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            }
            catch (Win32Exception)
            {
                // For example on the secure desktop. The selector still works on a black picture.
                g.Clear(Color.Black);
            }
        }
        return bitmap;
    }
}

/// <summary>Visible top-level windows in Z-order (top first), for the hover detection.</summary>
static class WindowList
{
    public static List<Rectangle> Get(IntPtr skip, Rectangle virtualScreen)
    {
        List<Rectangle> result = new List<Rectangle>();
        NativeMethods.EnumWindowsProc callback = delegate (IntPtr hWnd, IntPtr lParam)
        {
            if (hWnd == skip) return true;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.IsIconic(hWnd)) return true;
            int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (NativeMethods.IsCloaked(hWnd)) return true;
            Rectangle bounds = Rectangle.Intersect(NativeMethods.GetWindowBounds(hWnd), virtualScreen);
            if (bounds.Width < 2 || bounds.Height < 2) return true;
            result.Add(bounds);
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }
}

sealed class RegionSelectorForm : Form
{
    enum DragMode { None, Pending, Creating, Moving, Resizing }

    const int DragThreshold = 3;
    const int HandleSize = 8;
    const int MagnifierSource = 10;
    const int MagnifierZoom = 8;
    const int MagnifierOffset = 24;
    const int SlotSelection = 0;
    const int SlotHover = 1;
    const int SlotCount = 2;

    static readonly Color Accent = Color.FromArgb(255, 64, 158, 255);

    readonly Rectangle virtualScreen;
    readonly Rectangle clientArea;
    readonly Bitmap shot;
    readonly Bitmap dimmed;
    readonly Bitmap back;
    readonly Graphics backGraphics;
    readonly Pen selectionPen = new Pen(Accent, 1f);
    readonly Pen hoverPen = new Pen(Accent, 2f);
    readonly Pen crosshairPen = new Pen(Color.FromArgb(170, 255, 255, 255), 1f);
    readonly SolidBrush labelBrush = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
    readonly Rectangle? lastRegion;

    List<Rectangle> windows = new List<Rectangle>();
    Rectangle selection;
    bool hasSelection;
    Rectangle hover;
    bool hasHover;
    DragMode mode = DragMode.None;
    Point mouse;
    Point lastMouse;
    Point downPoint;
    Point anchor;
    Rectangle selectionAtDown;
    int activeHandle = -1;

    Font font;
    float fontScale;
    string labelText;
    Rectangle labelRect;
    Rectangle magnifierRect;
    readonly Rectangle[] paintedSlots = new Rectangle[SlotCount];
    Rectangle paintedLabel, paintedMagnifier, paintedCrossH, paintedCrossV;
    bool paintedHasSelection;

    Rectangle? result;

    public Rectangle? Result { get { return result; } }

    public RegionSelectorForm(Rectangle virtualScreen, Bitmap shot, Rectangle? lastRegion)
    {
        this.virtualScreen = virtualScreen;
        this.shot = shot;
        this.lastRegion = lastRegion;
        clientArea = new Rectangle(0, 0, virtualScreen.Width, virtualScreen.Height);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Text = "Select region";
        Cursor = Cursors.Cross;
        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        Bounds = virtualScreen;

        dimmed = new Bitmap(clientArea.Width, clientArea.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(dimmed))
        using (SolidBrush shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(shot, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            g.FillRectangle(shade, clientArea);
        }

        // Own back buffer at full size, made one time. The WinForms double buffer would allocate a full-size buffer on each paint.
        back = new Bitmap(clientArea.Width, clientArea.Height, PixelFormat.Format32bppPArgb);
        backGraphics = Graphics.FromImage(back);

        Point cursor = Cursor.Position;
        mouse = new Point(cursor.X - virtualScreen.X, cursor.Y - virtualScreen.Y);
        lastMouse = mouse;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // WinForms can clip a form to the maximum track size. Set the exact physical bounds with SetWindowPos.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, virtualScreen.X, virtualScreen.Y,
            virtualScreen.Width, virtualScreen.Height, NativeMethods.SWP_NOACTIVATE);

        // Get the windows now, so that the selector itself can be skipped by its handle.
        windows = WindowList.Get(Handle, virtualScreen);
        for (int i = 0; i < windows.Count; i++)
        {
            Rectangle w = windows[i];
            w.Offset(-virtualScreen.X, -virtualScreen.Y);
            windows[i] = w;
        }

        if (lastRegion.HasValue)
        {
            Rectangle r = lastRegion.Value;
            r.Offset(-virtualScreen.X, -virtualScreen.Y);
            r = Rectangle.Intersect(r, clientArea);
            if (r.Width >= 2 && r.Height >= 2)
            {
                selection = r;
                hasSelection = true;
            }
        }

        UpdateHover();
        ComputeLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        // The form spans monitors with different DPI. Ignore the DPI change so that Windows does not resize it.
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            backGraphics.Dispose();
            back.Dispose();
            dimmed.Dispose();
            selectionPen.Dispose();
            hoverPen.Dispose();
            crosshairPen.Dispose();
            labelBrush.Dispose();
            if (font != null) font.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- keyboard

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0;
        bool ctrl = (keyData & Keys.Control) != 0;

        switch (key)
        {
            case Keys.Escape:
                result = null;
                DialogResult = DialogResult.Cancel;
                return true;

            case Keys.Enter:
                Accept();
                return true;

            case Keys.Space:
                selection = clientArea;
                hasSelection = true;
                Accept();
                return true;

            case Keys.Oemtilde:
                Rectangle monitor = NativeMethods.GetMonitorBounds(ToScreen(mouse));
                monitor.Offset(-virtualScreen.X, -virtualScreen.Y);
                selection = Rectangle.Intersect(monitor, clientArea);
                hasSelection = true;
                Accept();
                return true;

            case Keys.C:
                RegionSelector.ShowCrosshair = !RegionSelector.ShowCrosshair;
                RefreshOverlay();
                return true;

            case Keys.M:
                RegionSelector.ShowMagnifier = !RegionSelector.ShowMagnifier;
                RefreshOverlay();
                return true;

            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                int step = shift ? 10 : 1;
                int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
                int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
                if (hasSelection && mode == DragMode.None)
                {
                    if (ctrl) ResizeSelectionBy(dx, dy);
                    else MoveSelectionBy(dx, dy);
                }
                else
                {
                    MoveCursorBy(dx, dy);
                }
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void MoveSelectionBy(int dx, int dy)
    {
        Rectangle r = selection;
        r.X = Math.Max(0, Math.Min(r.X + dx, clientArea.Width - r.Width));
        r.Y = Math.Max(0, Math.Min(r.Y + dy, clientArea.Height - r.Height));
        selection = r;
        UpdateHover();
        RefreshOverlay();
    }

    void ResizeSelectionBy(int dw, int dh)
    {
        Rectangle r = selection;
        r.Width = Math.Max(2, Math.Min(r.Width + dw, clientArea.Width - r.X));
        r.Height = Math.Max(2, Math.Min(r.Height + dh, clientArea.Height - r.Y));
        selection = r;
        UpdateHover();
        RefreshOverlay();
    }

    void MoveCursorBy(int dx, int dy)
    {
        Point target = new Point(
            Math.Max(0, Math.Min(mouse.X + dx, clientArea.Width - 1)),
            Math.Max(0, Math.Min(mouse.Y + dy, clientArea.Height - 1)));
        Cursor.Position = ToScreen(target);
        HandleMouseMove(target);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        mouse = e.Location;
        lastMouse = e.Location;

        if (e.Button == MouseButtons.Right)
        {
            if (mode != DragMode.None)
            {
                mode = DragMode.None;
            }
            else if (hasSelection)
            {
                hasSelection = false;
            }
            else
            {
                result = null;
                DialogResult = DialogResult.Cancel;
                return;
            }
            UpdateHover();
            UpdateCursor();
            RefreshOverlay();
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (e.Clicks >= 2 && hasSelection && selection.Contains(e.Location))
        {
            Accept();
            return;
        }

        downPoint = e.Location;
        selectionAtDown = selection;
        int handle = hasSelection ? HitHandle(e.Location) : -1;
        if (handle >= 0)
        {
            mode = DragMode.Resizing;
            activeHandle = handle;
        }
        else if (hasSelection && selection.Contains(e.Location))
        {
            mode = DragMode.Moving;
        }
        else
        {
            mode = DragMode.Pending;
        }
        RefreshOverlay();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HandleMouseMove(e.Location);
    }

    void HandleMouseMove(Point p)
    {
        mouse = p;
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        bool ctrl = (ModifierKeys & Keys.Control) != 0;

        switch (mode)
        {
            case DragMode.Pending:
                if (Math.Abs(p.X - downPoint.X) > DragThreshold || Math.Abs(p.Y - downPoint.Y) > DragThreshold)
                {
                    mode = DragMode.Creating;
                    anchor = downPoint;
                    hasHover = false;
                    UpdateCreating(shift, false);
                }
                break;

            case DragMode.Creating:
                UpdateCreating(shift, ctrl);
                break;

            case DragMode.Moving:
                Rectangle moved = selectionAtDown;
                moved.X = Math.Max(0, Math.Min(moved.X + p.X - downPoint.X, clientArea.Width - moved.Width));
                moved.Y = Math.Max(0, Math.Min(moved.Y + p.Y - downPoint.Y, clientArea.Height - moved.Height));
                selection = moved;
                break;

            case DragMode.Resizing:
                UpdateResizing();
                break;

            default:
                UpdateHover();
                UpdateCursor();
                break;
        }

        lastMouse = p;
        RefreshOverlay();
    }

    void UpdateCreating(bool shift, bool ctrl)
    {
        // Ctrl moves the rectangle that is being drawn: the start corner follows the mouse.
        if (ctrl) anchor.Offset(mouse.X - lastMouse.X, mouse.Y - lastMouse.Y);

        Point end = mouse;
        if (shift)
        {
            int dx = end.X - anchor.X;
            int dy = end.Y - anchor.Y;
            int size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            end = new Point(anchor.X + (dx < 0 ? -size : size), anchor.Y + (dy < 0 ? -size : size));
        }

        Rectangle r = Rectangle.FromLTRB(
            Math.Min(anchor.X, end.X), Math.Min(anchor.Y, end.Y),
            Math.Max(anchor.X, end.X) + 1, Math.Max(anchor.Y, end.Y) + 1);
        selection = Rectangle.Intersect(r, clientArea);
        hasSelection = !selection.IsEmpty;
    }

    void UpdateResizing()
    {
        int dx = mouse.X - downPoint.X;
        int dy = mouse.Y - downPoint.Y;
        int l = selectionAtDown.Left, t = selectionAtDown.Top, r = selectionAtDown.Right, b = selectionAtDown.Bottom;

        // Handles: 0 top-left, 1 top, 2 top-right, 3 right, 4 bottom-right, 5 bottom, 6 bottom-left, 7 left.
        if (activeHandle == 0 || activeHandle == 6 || activeHandle == 7) l += dx;
        if (activeHandle == 2 || activeHandle == 3 || activeHandle == 4) r += dx;
        if (activeHandle == 0 || activeHandle == 1 || activeHandle == 2) t += dy;
        if (activeHandle == 4 || activeHandle == 5 || activeHandle == 6) b += dy;

        if (l > r) { int s = l; l = r; r = s; }
        if (t > b) { int s = t; t = b; b = s; }
        if (r - l < 1) r = l + 1;
        if (b - t < 1) b = t + 1;

        Rectangle n = Rectangle.Intersect(Rectangle.FromLTRB(l, t, r, b), clientArea);
        if (!n.IsEmpty) selection = n;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        mouse = e.Location;

        if (mode == DragMode.Pending)
        {
            // A click without a drag selects the highlighted window.
            if (hasHover)
            {
                selection = hover;
                hasSelection = true;
            }
        }
        else if (mode == DragMode.Creating)
        {
            if (selection.Width < 2 || selection.Height < 2) hasSelection = false;
        }

        mode = DragMode.None;
        activeHandle = -1;
        UpdateHover();
        UpdateCursor();
        RefreshOverlay();
    }

    void UpdateHover()
    {
        hasHover = false;
        if (mode != DragMode.None && mode != DragMode.Pending) return;
        if (hasSelection && (selection.Contains(mouse) || HitHandle(mouse) >= 0)) return;
        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i].Contains(mouse))
            {
                hover = windows[i];
                hasHover = true;
                return;
            }
        }
    }

    void UpdateCursor()
    {
        Cursor c = Cursors.Cross;
        if (hasSelection && mode == DragMode.None)
        {
            int handle = HitHandle(mouse);
            if (handle == 0 || handle == 4) c = Cursors.SizeNWSE;
            else if (handle == 2 || handle == 6) c = Cursors.SizeNESW;
            else if (handle == 1 || handle == 5) c = Cursors.SizeNS;
            else if (handle == 3 || handle == 7) c = Cursors.SizeWE;
            else if (selection.Contains(mouse)) c = Cursors.SizeAll;
        }
        if (Cursor != c) Cursor = c;
    }

    Rectangle HandleRect(int index)
    {
        Rectangle s = selection;
        int left = s.Left - 1, midX = s.Left + s.Width / 2, right = s.Right;
        int top = s.Top - 1, midY = s.Top + s.Height / 2, bottom = s.Bottom;
        int x, y;
        switch (index)
        {
            case 0: x = left; y = top; break;
            case 1: x = midX; y = top; break;
            case 2: x = right; y = top; break;
            case 3: x = right; y = midY; break;
            case 4: x = right; y = bottom; break;
            case 5: x = midX; y = bottom; break;
            case 6: x = left; y = bottom; break;
            default: x = left; y = midY; break;
        }
        return new Rectangle(x - HandleSize / 2, y - HandleSize / 2, HandleSize, HandleSize);
    }

    int HitHandle(Point p)
    {
        if (!hasSelection) return -1;
        for (int i = 0; i < 8; i++)
        {
            Rectangle r = HandleRect(i);
            r.Inflate(2, 2);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------- accept

    void Accept()
    {
        Rectangle r;
        if (hasSelection) r = selection;
        else if (hasHover) r = hover;
        else return;

        r.Offset(virtualScreen.X, virtualScreen.Y);
        Rectangle? normalized = RegionSelector.NormalizeRegion(r);
        if (!normalized.HasValue) return;
        result = normalized;
        DialogResult = DialogResult.OK;
    }

    Point ToScreen(Point client)
    {
        return new Point(client.X + virtualScreen.X, client.Y + virtualScreen.Y);
    }

    static Rectangle EvenSize(Rectangle r)
    {
        r.Width -= r.Width % 2;
        r.Height -= r.Height % 2;
        return r;
    }

    // ---------------------------------------------------------------- layout and invalidation

    void EnsureFont()
    {
        float scale = NativeMethods.GetScaleForPoint(ToScreen(mouse));
        if (font != null && Math.Abs(scale - fontScale) < 0.01f) return;
        if (font != null) font.Dispose();
        font = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        fontScale = scale;
    }

    Rectangle MonitorUnderMouse()
    {
        Rectangle m = NativeMethods.GetMonitorBounds(ToScreen(mouse));
        m.Offset(-virtualScreen.X, -virtualScreen.Y);
        return Rectangle.Intersect(m, clientArea);
    }

    void ComputeLayout()
    {
        EnsureFont();
        Rectangle monitor = MonitorUnderMouse();

        // Size label: the size that the recording will have (even numbers).
        Rectangle target = hasSelection ? selection : (hasHover ? hover : Rectangle.Empty);
        labelText = null;
        labelRect = Rectangle.Empty;
        if (!target.IsEmpty)
        {
            Rectangle even = EvenSize(target);
            labelText = even.Width + " x " + even.Height;
            if (RegionSelector.ShowPosition)
                labelText += "   at " + (even.X + virtualScreen.X) + ", " + (even.Y + virtualScreen.Y);
            SizeF size = backGraphics.MeasureString(labelText, font);
            int w = (int)Math.Ceiling(size.Width) + 10;
            int h = (int)Math.Ceiling(size.Height) + 4;
            int x = target.X;
            int y = target.Y - h - HandleSize;
            if (y < 0) y = target.Y + HandleSize;
            x = Math.Max(0, Math.Min(x, clientArea.Width - w));
            y = Math.Max(0, Math.Min(y, clientArea.Height - h));
            labelRect = new Rectangle(x, y, w, h);
        }

        // Magnifier next to the cursor, on the same monitor.
        magnifierRect = Rectangle.Empty;
        if (RegionSelector.ShowMagnifier)
        {
            int zoomSize = MagnifierSource * MagnifierZoom;
            int caption = font.Height + 4;
            int captionWidth = (int)Math.Ceiling(backGraphics.MeasureString("-00000, -00000", font).Width) + 8;
            int w = Math.Max(zoomSize, captionWidth);
            int h = zoomSize + caption;
            int x = mouse.X + MagnifierOffset;
            int y = mouse.Y + MagnifierOffset;
            if (x + w > monitor.Right) x = mouse.X - MagnifierOffset - w;
            if (y + h > monitor.Bottom) y = mouse.Y - MagnifierOffset - h;
            x = Math.Max(monitor.Left, Math.Min(x, monitor.Right - w));
            y = Math.Max(monitor.Top, Math.Min(y, monitor.Bottom - h));
            magnifierRect = new Rectangle(x, y, w, h);
        }
    }

    void RefreshOverlay()
    {
        ComputeLayout();

        if (hasSelection != paintedHasSelection)
        {
            // The hover area changes between bright and dimmed. Paint all.
            paintedHasSelection = hasSelection;
            Invalidate();
        }

        Rectangle[] slots = new Rectangle[SlotCount];
        if (hasSelection)
        {
            Rectangle s = selection;
            s.Inflate(HandleSize, HandleSize);
            slots[SlotSelection] = s;
        }
        if (hasHover)
        {
            Rectangle h = hover;
            h.Inflate(3, 3);
            slots[SlotHover] = h;
        }

        // Large areas: paint again only when they change.
        for (int i = 0; i < SlotCount; i++)
        {
            if (slots[i] == paintedSlots[i]) continue;
            InvalidateArea(paintedSlots[i]);
            InvalidateArea(slots[i]);
            paintedSlots[i] = slots[i];
        }

        // Small areas: paint again each time.
        InvalidateArea(paintedLabel);
        InvalidateArea(paintedMagnifier);
        InvalidateArea(paintedCrossH);
        InvalidateArea(paintedCrossV);

        paintedLabel = Inflated(labelRect, 1);
        paintedMagnifier = Inflated(magnifierRect, 1);
        if (RegionSelector.ShowCrosshair)
        {
            paintedCrossH = new Rectangle(0, mouse.Y - 1, clientArea.Width, 3);
            paintedCrossV = new Rectangle(mouse.X - 1, 0, 3, clientArea.Height);
        }
        else
        {
            paintedCrossH = Rectangle.Empty;
            paintedCrossV = Rectangle.Empty;
        }

        InvalidateArea(paintedLabel);
        InvalidateArea(paintedMagnifier);
        InvalidateArea(paintedCrossH);
        InvalidateArea(paintedCrossV);
    }

    static Rectangle Inflated(Rectangle r, int by)
    {
        if (r.IsEmpty) return r;
        r.Inflate(by, by);
        return r;
    }

    void InvalidateArea(Rectangle r)
    {
        // Paint each area at once. Otherwise Windows joins all invalid areas, and the clip box of
        // two far-apart areas (for example the crosshair lines) becomes the full virtual screen.
        if (r.Width > 0 && r.Height > 0)
        {
            Invalidate(r);
            Update();
        }
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint covers the full area. Nothing to do.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle clip = Rectangle.Intersect(e.ClipRectangle, clientArea);
        if (clip.Width <= 0 || clip.Height <= 0) return;

        backGraphics.SetClip(clip);
        RenderScene(backGraphics, clip);
        backGraphics.ResetClip();

        Graphics g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.DrawImage(back, clip, clip, GraphicsUnit.Pixel);
    }

    void RenderScene(Graphics g, Rectangle clip)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);

        Rectangle bright = hasSelection ? selection : (hasHover ? hover : Rectangle.Empty);
        Rectangle part = Rectangle.Intersect(bright, clip);
        if (part.Width > 0 && part.Height > 0) g.DrawImage(shot, part, part, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;

        if (hasHover) DrawFrame(g, hover, hoverPen);

        if (hasSelection)
        {
            DrawFrame(g, selection, selectionPen);
            if (mode != DragMode.Creating)
            {
                for (int i = 0; i < 8; i++)
                {
                    Rectangle h = HandleRect(i);
                    g.FillRectangle(Brushes.White, h);
                    g.DrawRectangle(selectionPen, h.X, h.Y, h.Width - 1, h.Height - 1);
                }
            }
        }

        if (RegionSelector.ShowCrosshair)
        {
            g.DrawLine(crosshairPen, 0, mouse.Y, clientArea.Width, mouse.Y);
            g.DrawLine(crosshairPen, mouse.X, 0, mouse.X, clientArea.Height);
        }

        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (labelText != null && labelRect.IntersectsWith(clip))
        {
            g.FillRectangle(labelBrush, labelRect);
            g.DrawString(labelText, font, Brushes.White, labelRect.X + 5, labelRect.Y + 2);
        }

        if (!magnifierRect.IsEmpty && Inflated(magnifierRect, 1).IntersectsWith(clip)) DrawMagnifier(g);
    }

    static void DrawFrame(Graphics g, Rectangle r, Pen pen)
    {
        // The frame is drawn just outside the rectangle, so that it does not cover the selected pixels.
        if (pen.Width <= 1f) g.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
        else g.DrawRectangle(pen, r.X - 1.5f, r.Y - 1.5f, r.Width + 2f, r.Height + 2f);
    }

    void DrawMagnifier(Graphics g)
    {
        int zoomSize = MagnifierSource * MagnifierZoom;
        Rectangle zoomArea = new Rectangle(magnifierRect.X + (magnifierRect.Width - zoomSize) / 2, magnifierRect.Y, zoomSize, zoomSize);
        g.FillRectangle(Brushes.Black, magnifierRect);

        int half = MagnifierSource / 2;
        Rectangle source = new Rectangle(mouse.X - half, mouse.Y - half, MagnifierSource, MagnifierSource);
        Rectangle visible = Rectangle.Intersect(source, clientArea);
        if (visible.Width > 0 && visible.Height > 0)
        {
            Rectangle dest = new Rectangle(
                zoomArea.X + (visible.X - source.X) * MagnifierZoom,
                zoomArea.Y + (visible.Y - source.Y) * MagnifierZoom,
                visible.Width * MagnifierZoom,
                visible.Height * MagnifierZoom);
            CompositingMode oldMode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, dest, visible, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.CompositingMode = oldMode;
        }

        // Mark the pixel under the cursor.
        Rectangle center = new Rectangle(zoomArea.X + half * MagnifierZoom, zoomArea.Y + half * MagnifierZoom, MagnifierZoom, MagnifierZoom);
        g.DrawRectangle(Pens.Black, center.X - 1, center.Y - 1, center.Width + 1, center.Height + 1);
        g.DrawRectangle(Pens.White, center.X, center.Y, center.Width - 1, center.Height - 1);

        Point screen = ToScreen(mouse);
        string caption = screen.X + ", " + screen.Y;
        g.DrawString(caption, font, Brushes.White, magnifierRect.X + 4, zoomArea.Bottom + 2);
        g.DrawRectangle(selectionPen, magnifierRect.X, magnifierRect.Y, magnifierRect.Width - 1, magnifierRect.Height - 1);
    }
}
