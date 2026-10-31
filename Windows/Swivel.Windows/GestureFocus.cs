using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Swivel.Windows;

internal static class GestureFocus
{
    internal const string Refused = "Windows did not allow Swivel to take focus. Select its flashing window, or open Wheel from the tray and choose the action again.";
    internal static bool BringForward(Window window)
    {
        bool topmost = window.Topmost;
        try
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Topmost = true;
            if (window.Activate()) return true;
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = new WindowInteropHelper(window).Handle, Flags = 3, Count = 3 };
            FlashWindowEx(ref info); return false;
        }
        finally { window.Topmost = topmost; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
}
