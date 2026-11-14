// Win32 calls for the region selector and the recording frame.
// All rectangles are in physical pixels and in virtual-screen coordinates.
// This is true only when the process is per-monitor DPI aware (see EnableDpiAwareness and DesktopRecorder.manifest).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DPICHANGED = 0x02E0;
    public const int HTTRANSPARENT = -1;
    public const int MA_NOACTIVATE = 3;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    public const uint WDA_NONE = 0x00;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const uint LWA_ALPHA = 0x2;

    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    static readonly IntPtr DpiContextPerMonitorV2 = new IntPtr(-4);
    const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        public static RECT FromRectangle(Rectangle r)
        {
            RECT result;
            result.Left = r.Left;
            result.Top = r.Top;
            result.Right = r.Right;
            result.Bottom = r.Bottom;
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern bool IsProcessDPIAware();

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Makes the process per-monitor DPI aware. Call this in Program.Main before the first window is made.
    /// Order: SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2), then SetProcessDpiAwareness(PER_MONITOR),
    /// then SetProcessDPIAware (system aware). When the manifest already set the mode, all calls fail and the
    /// manifest mode stays. Returns true when the process is DPI aware after the calls.
    /// </summary>
    public static bool EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DpiContextPerMonitorV2)) return true;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows older than 10 1703.
        }

        try
        {
            if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0) return true;
        }
        catch (DllNotFoundException)
        {
            // Windows older than 8.1.
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            if (SetProcessDPIAware()) return true;
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            return IsProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The bounds of all monitors together.</summary>
    public static Rectangle GetVirtualScreen()
    {
        return new Rectangle(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    /// <summary>The bounds of the monitor that is nearest to the point.</summary>
    public static Rectangle GetMonitorBounds(Point screenPoint)
    {
        IntPtr monitor = MonitorFromPoint(new POINT(screenPoint.X, screenPoint.Y), MONITOR_DEFAULTTONEAREST);
        return GetMonitorBounds(monitor);
    }

    /// <summary>The bounds of the monitor that has the largest part of the rectangle.</summary>
    public static Rectangle GetMonitorBounds(Rectangle screenRect)
    {
        RECT r = RECT.FromRectangle(screenRect);
        IntPtr monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        return GetMonitorBounds(monitor);
    }

    static Rectangle GetMonitorBounds(IntPtr monitor)
    {
        MONITORINFO info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcMonitor.ToRectangle();
        return GetVirtualScreen();
    }

    /// <summary>The bounds of each monitor.</summary>
    public static Rectangle[] GetAllMonitorBounds()
    {
        List<Rectangle> list = new List<Rectangle>();
        MonitorEnumProc callback = delegate (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            list.Add(rect.ToRectangle());
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (list.Count == 0) list.Add(GetVirtualScreen());
        return list.ToArray();
    }

    /// <summary>The DPI scale (1.0 = 96 DPI) of the monitor nearest to the point. Returns 1.0 when Windows cannot tell.</summary>
    public static float GetScaleForPoint(Point screenPoint)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT(screenPoint.X, screenPoint.Y), MONITOR_DEFAULTTONEAREST);
            uint dpiX, dpiY;
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 && dpiX > 0)
                return dpiX / 96f;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        return 1f;
    }

    /// <summary>The visible bounds of a top-level window, without the invisible shadow border.</summary>
    public static Rectangle GetWindowBounds(IntPtr hWnd)
    {
        RECT r;
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0)
                return r.ToRectangle();
        }
        catch (DllNotFoundException)
        {
        }
        if (GetWindowRect(hWnd, out r)) return r.ToRectangle();
        return Rectangle.Empty;
    }

    /// <summary>True when DWM hides the window (for example a window on another virtual desktop or a suspended UWP app).</summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            int cloaked;
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks Windows to keep the window out of screen captures (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004 and later).
    /// Returns false when Windows does not support it. UNVERIFIED: that gdigrab and ddagrab respect this flag.
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hWnd)
    {
        try
        {
            return SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
