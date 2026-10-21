using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Swivel;

/// The monitor under a point, in physical pixels, with its scale (per-monitor DPI), and placement of borderless
/// windows on it in DIPs. Window.Left/Top are unreliable across monitors with different scales, so windows are moved
/// with SetWindowPos in pixels.
static partial class Screens
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(Native.POINT pt, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    /// A monitor: full bounds and work area (without the taskbar) in pixels, and DIP scale (1 = 96 DPI).
    public readonly record struct Monitor(RECT Bounds, RECT Work, double Scale)
    {
        /// The work area in DIPs relative to the monitor's top-left corner.
        public Rect WorkDips => new((Work.Left - Bounds.Left) / Scale, (Work.Top - Bounds.Top) / Scale, Work.Width / Scale, Work.Height / Scale);

        /// The whole monitor in DIPs relative to its top-left corner.
        public Rect BoundsDips => new(0, 0, Bounds.Width / Scale, Bounds.Height / Scale);

        /// A pixel point in DIPs relative to the monitor's top-left corner.
        public Point ToDips(Native.POINT p) => new((p.X - Bounds.Left) / Scale, (p.Y - Bounds.Top) / Scale);

        public Native.POINT ToPixels(Point dips) =>
            new() { X = Bounds.Left + (int)Math.Round(dips.X * Scale), Y = Bounds.Top + (int)Math.Round(dips.Y * Scale) };
    }

    public static Monitor At(Native.POINT point)
    {
        var handle = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
        {
            var w = (int)SystemParameters.PrimaryScreenWidth;
            var h = (int)SystemParameters.PrimaryScreenHeight;
            var all = new RECT { Right = w, Bottom = h };
            return new Monitor(all, all, 1);
        }
        double scale = GetDpiForMonitor(handle, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return new Monitor(info.rcMonitor, info.rcWork, scale);
    }

    /// Moves and sizes a window to a rect given in DIPs relative to the monitor's top-left corner, keeping it topmost
    /// and inactive. Moving onto a monitor with another scale changes the window's DPI on the first move, so it is
    /// placed again once WPF has adopted the new scale.
    public static void Place(Window window, Monitor monitor, Rect dips)
    {
        window.Width = dips.Width;
        window.Height = dips.Height;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var origin = monitor.ToPixels(dips.TopLeft);
        int cx = (int)Math.Round(dips.Width * monitor.Scale), cy = (int)Math.Round(dips.Height * monitor.Scale);
        const uint flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        bool rescale = Math.Abs(GetDpiForWindow(hwnd) / 96.0 - monitor.Scale) > 0.01;
        SetWindowPos(hwnd, HWND_TOPMOST, origin.X, origin.Y, cx, cy, flags);
        if (rescale) SetWindowPos(hwnd, HWND_TOPMOST, origin.X, origin.Y, cx, cy, flags);
    }

    /// Raises a window to the top of the topmost band without activating it.
    public static void BringToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | 0x2 /* SWP_NOMOVE */ | SWP_NOACTIVATE);
    }
}
