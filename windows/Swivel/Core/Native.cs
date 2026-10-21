using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Swivel;

/// Win32 calls shared by several modules.
static partial class Native
{
    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr ILCreateFromPathW(string path);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    /// Opens one Explorer window with several files of the same folder selected.
    public static void RevealMany(IReadOnlyList<string> files)
    {
        var folder = System.IO.Path.GetDirectoryName(files[0]);
        if (folder is null) return;
        var parent = ILCreateFromPathW(folder);
        var items = files.Select(ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
        try { SHOpenFolderAndSelectItems(parent, (uint)items.Length, items, 0); }
        finally
        {
            foreach (var item in items) ILFree(item);
            if (parent != IntPtr.Zero) ILFree(parent);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    /// The pointer in physical screen pixels.
    public static POINT CursorPixels() => GetCursorPos(out var p) ? p : default;

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// The left mouse button, honouring swapped buttons.
    public static bool PrimaryButtonDown() => IsKeyDown(GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowLongW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowLongW(IntPtr hwnd, int index, int value);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;

    /// Makes a WPF window a tool window that never takes focus (like an NSPanel with .nonactivatingPanel).
    public static void MakeNonActivating(Window window)
    {
        void apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongW(hwnd, GWL_EXSTYLE, GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) apply();
        else window.SourceInitialized += (_, _) => apply();
    }

    /// Lets clicks pass through a window (or stop passing through).
    public static void SetClickThrough(Window window, bool through)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, through ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT);
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// Windows 11 rounded corners for a borderless window (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2).
    public static void RoundCorners(Window window, bool small = false)
    {
        void apply()
        {
            int preference = small ? 3 : 2;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 33, ref preference, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) apply();
        else window.SourceInitialized += (_, _) => apply();
    }

    /// "Show animations in Windows" off (Settings › Accessibility › Visual effects): nothing should move.
    public static bool AnimationsOff => !SystemParameters.ClientAreaAnimation;

    /// "Transparency effects" off: menus get a solid backdrop.
    public static bool TransparencyOff
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }
    }

    /// Whether apps use the dark theme.
    public static bool DarkMode
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }
    }
}

/// Windows' share sheet for files, used for the "Share" action (Windows' counterpart of AirDrop).
static class ShareSheet
{
    static Window? anchor;

    public static void Show(IReadOnlyList<string> files)
    {
        anchor?.Close();
        var cursor = Native.CursorPixels();
        var window = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        anchor = window;
        window.Show();
        var source = PresentationSource.FromVisual(window);
        var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        window.Left = cursor.X * scale;
        window.Top = cursor.Y * scale;
        var hwnd = new WindowInteropHelper(window).Handle;
        var manager = DataTransferManagerInterop.GetForWindow(hwnd);
        manager.DataRequested += async (_, e) =>
        {
            var deferral = e.Request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var file in files)
                    items.Add(Directory.Exists(file)
                        ? await StorageFolder.GetFolderFromPathAsync(file)
                        : await StorageFile.GetFileFromPathAsync(file));
                e.Request.Data.Properties.Title = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : L("%d files", files.Count);
                e.Request.Data.SetStorageItems(items);
            }
            catch (Exception ex)
            {
                e.Request.FailWithDisplayText(ex.Message);
            }
            finally { deferral.Complete(); }
        };
        manager.TargetApplicationChosen += (_, _) => Close(window);
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        // The sheet has no "closed" event; take the anchor down once it's surely gone.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += (_, _) => { timer.Stop(); Close(window); };
        timer.Start();
    }

    static void Close(Window window)
    {
        if (anchor == window) anchor = null;
        window.Close();
    }
}
