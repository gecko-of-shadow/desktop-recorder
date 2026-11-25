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

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    static extern bool CloseDesktop(IntPtr desktop);

    // False while Windows shows the secure desktop (a UAC prompt, the lock screen): no capture works then.
    public static bool InputDesktopAccessible()
    {
        try
        {
            IntPtr d = OpenInputDesktop(0, false, 0x0100);   // DESKTOP_SWITCHDESKTOP
            if (d == IntPtr.Zero) return false;
            CloseDesktop(d);
            return true;
        }
        catch (Exception) { return true; }
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

    /// <summary>The work area (the monitor without the taskbar) of the monitor that has the largest part of the rectangle.</summary>
    public static Rectangle GetMonitorWorkArea(Rectangle screenRect)
    {
        RECT r = RECT.FromRectangle(screenRect);
        IntPtr monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        MONITORINFO info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcWork.ToRectangle();
        return GetMonitorBounds(screenRect);
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

    // ---------------------------------------------------------------- window titles (AI capture index)

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    const uint GA_ROOT = 2, GA_ROOTOWNER = 3;

    /// <summary>
    /// The title of the top-level window at the screen point (physical px). When that window belongs to this
    /// process (the border or the Stop button), or has no title, the title of the foreground window.
    /// Returns "" when Windows gives no title.
    /// </summary>
    public static string WindowTitleAt(Point screenPoint)
    {
        try
        {
            POINT p = new POINT();
            p.X = screenPoint.X;
            p.Y = screenPoint.Y;
            IntPtr h = WindowFromPoint(p);
            if (h != IntPtr.Zero) h = GetAncestor(h, GA_ROOT);
            string title = h != IntPtr.Zero && !IsOwnWindow(h) ? TitleOf(h) : "";
            if (title.Length == 0)
            {
                IntPtr f = GetForegroundWindow();
                if (f != IntPtr.Zero && !IsOwnWindow(f)) title = TitleOf(f);
                // A popup without a title (a menu, a browser extension): the window that it belongs to.
                if (title.Length == 0 && h != IntPtr.Zero && !IsOwnWindow(h)) title = TitleOf(GetAncestor(h, GA_ROOTOWNER));
                if (title.Length == 0 && f != IntPtr.Zero && !IsOwnWindow(f)) title = TitleOf(GetAncestor(f, GA_ROOTOWNER));
            }
            return title;
        }
        catch (Exception) { return ""; }
    }

    static bool IsOwnWindow(IntPtr h)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    static string TitleOf(IntPtr h)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim();
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
    /// Returns false when Windows does not support it. gdigrab and ddagrab respect the flag (HANDOFF 6.7 and 7.22).
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hWnd)
    {
        // Test switch: with DESKTOP_RECORDER_SHOW_IN_CAPTURE=1 in the environment, the windows stay in screen captures,
        // so that a screenshot can show the note box, the popup and the frame (HANDOFF 7.22). PrintWindow gives a black
        // picture of an excluded window. Never use it for real recordings: these windows are then in the video.
        if (ShowInCapture) return false;
        try
        {
            return SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    static readonly bool ShowInCapture = Environment.GetEnvironmentVariable("DESKTOP_RECORDER_SHOW_IN_CAPTURE") == "1";

    // ---------------------------------------------------------------- thread DPI awareness

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    /// <summary>DPI_AWARENESS_CONTEXT_SYSTEM_AWARE. Windows scales the window as a bitmap on a monitor with a different DPI.</summary>
    public static readonly IntPtr DpiContextSystemAware = new IntPtr(-2);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. The process default (see the manifest).</summary>
    public static readonly IntPtr DpiContextPerMonitorAwareV2 = new IntPtr(-4);

    /// <summary>
    /// Sets the DPI awareness of the current thread. A top-level window keeps the awareness of the thread
    /// at the time Windows makes the window. Returns the old context, or IntPtr.Zero when Windows cannot do it
    /// (Windows older than 10 1607). Give the old context back to RestoreThreadDpiContext.
    /// </summary>
    public static IntPtr SetThreadDpiContext(IntPtr context)
    {
        try { return SetThreadDpiAwarenessContext(context); }
        catch (EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    public static void RestoreThreadDpiContext(IntPtr oldContext)
    {
        if (oldContext == IntPtr.Zero) return;
        try { SetThreadDpiAwarenessContext(oldContext); }
        catch (EntryPointNotFoundException) { }
    }

    // ---------------------------------------------------------------- DXGI outputs (for ddagrab output_idx)

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ComEnumDelegate(IntPtr self, uint index, out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ComGetDescDelegate(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ComReleaseDelegate(IntPtr self);

    static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    // Vtable slots. IUnknown = 0 to 2, IDXGIObject = 3 to 6.
    // IDXGIFactory::EnumAdapters, IDXGIAdapter::EnumOutputs and IDXGIOutput::GetDesc are all slot 7.
    const int SlotRelease = 2;
    const int SlotFirstOwn = 7;

    static T ComMethod<T>(IntPtr obj, int slot) where T : class
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
    }

    static void ComRelease(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try { ComMethod<ComReleaseDelegate>(obj, SlotRelease)(obj); }
        catch (Exception) { }
    }

    /// <summary>
    /// The outputs of DXGI adapter 0, in DXGI order. FFmpeg ddagrab makes its D3D11 device on the default
    /// adapter (adapter 0) and calls EnumOutputs(output_idx) on it, so DxgiOutputInfo.Index is the ddagrab output_idx.
    /// A monitor on a second adapter is not in the list. Returns null when DXGI fails.
    /// UNVERIFIED: a per-app GPU preference for ffmpeg.exe (Windows graphics settings) can change the adapter of FFmpeg.
    /// </summary>
    public static List<DxgiOutputInfo> GetDxgiOutputs()
    {
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        IntPtr desc = IntPtr.Zero;
        try
        {
            Guid iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return null;
            if (ComMethod<ComEnumDelegate>(factory, SlotFirstOwn)(factory, 0, out adapter) < 0 || adapter == IntPtr.Zero)
                return null;
            List<DxgiOutputInfo> list = new List<DxgiOutputInfo>();
            // DXGI_OUTPUT_DESC: WCHAR DeviceName[32] (64 bytes), RECT DesktopCoordinates (offset 64),
            // BOOL AttachedToDesktop (80), DXGI_MODE_ROTATION (84), HMONITOR (88). 128 bytes is enough.
            desc = Marshal.AllocHGlobal(128);
            for (uint i = 0; i < 16; i++)
            {
                IntPtr output;
                int hr = ComMethod<ComEnumDelegate>(adapter, SlotFirstOwn)(adapter, i, out output);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (hr < 0 || output == IntPtr.Zero) return null;
                try
                {
                    for (int k = 0; k < 128; k++) Marshal.WriteByte(desc, k, 0);
                    if (ComMethod<ComGetDescDelegate>(output, SlotFirstOwn)(output, desc) < 0) return null;
                    DxgiOutputInfo info = new DxgiOutputInfo();
                    info.Index = (int)i;
                    info.DeviceName = Marshal.PtrToStringUni(desc, 32).TrimEnd('\0');
                    int left = Marshal.ReadInt32(desc, 64), top = Marshal.ReadInt32(desc, 68);
                    int right = Marshal.ReadInt32(desc, 72), bottom = Marshal.ReadInt32(desc, 76);
                    info.X = left;
                    info.Y = top;
                    info.Width = right - left;
                    info.Height = bottom - top;
                    info.Attached = Marshal.ReadInt32(desc, 80) != 0;
                    list.Add(info);
                }
                finally { ComRelease(output); }
            }
            return list;
        }
        catch (Exception)
        {
            // DllNotFoundException, EntryPointNotFoundException or a bad vtable call: no DXGI data.
            return null;
        }
        finally
        {
            if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
            ComRelease(adapter);
            ComRelease(factory);
        }
    }
}
