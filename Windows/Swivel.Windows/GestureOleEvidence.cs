using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Swivel.Windows;

internal static class GestureOleEvidence
{
    internal const int PollMilliseconds = 40;
    // Undocumented OLE implementation detail: real DoDragDrop CI checks must confirm it.
    private const string DragCaptureClass = "CLIPBRDWNDCLASS";
    internal static uint ThreadAt(int x, int y) => GetWindowThreadProcessId(WindowFromPoint(new Point { X = x, Y = y }), out _);
    internal static bool IsActive(uint pointerThreadId)
    {
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        return HasOleCapture(foreground) || (pointerThreadId != foreground && HasOleCapture(pointerThreadId));
    }
    private static bool HasOleCapture(uint thread)
    {
        if (thread == 0) return false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Capture == IntPtr.Zero) return false;
        var name = new StringBuilder(256);
        return GetClassName(info.Capture, name, name.Capacity) == DragCaptureClass.Length &&
            string.Equals(name.ToString(), DragCaptureClass, StringComparison.Ordinal);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
}
