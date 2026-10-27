using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// The file tools' shared window (FileToolWindow.swift): focus, lifetime, feature gating and busy/error handling.
/// A normal top-level window like the editors: Topmost until it is first activated (opened from a drop while
/// Explorer is in front, Windows may refuse to bring it forward — the Mac's `.floating` → `.normal`).
abstract class FileToolWindow
{
    static readonly List<FileToolWindow> open = [];

    public Feature Feature { get; }
    public Window Window { get; }
    public bool IsBusy { get; private set; }
    /// The work that is running (or the last one), for tests to await.
    public Task Work { get; private set; } = Task.CompletedTask;
    /// The message of the last failed work, also shown in an alert.
    public string? LastError { get; private set; }
    /// Shows a failure. Tests replace it so no modal dialog blocks them.
    public Action<Window, string> ShowError { get; set; } = (owner, message) =>
    {
        if (owner.IsVisible && owner.IsLoaded) { EditorDialog.Alert(owner, message); return; }
        // Failed before the window was on screen (loading the files): tell once it is.
        void shown(object? sender, EventArgs e)
        {
            owner.ContentRendered -= shown;
            EditorDialog.Alert(owner, message);
        }
        owner.ContentRendered += shown;
    };

    readonly Grid layers = new();
    FrameworkElement? root;

    protected FileToolWindow(Feature feature, string title, double width, double height)
    {
        Feature = feature;
        Window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(740, width),
            MinHeight = Math.Min(560, height),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
        };
        try { Window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Swivel;component/Swivel.ico")); }
        catch (Exception) { /* No pack resources (tests): the default icon. */ }
        AppTypography.Apply(Window);
        Window.Content = layers;
        Window.Activated += (_, _) => { if (Window.Topmost) Window.Topmost = false; };
        // Closing waits for the work to finish (the Mac's windowShouldClose), so nothing is left half done.
        Window.Closing += (_, e) => { if (IsBusy) e.Cancel = true; };
        Window.Closed += (_, _) =>
        {
            open.Remove(this);
            Prefs.Changed -= OnPrefsChanged;
            Closed();
        };
        Prefs.Changed += OnPrefsChanged;
    }

    /// The feature turned off elsewhere (settings, the tray menu, another window): the window goes, unless it is busy;
    /// then it goes as soon as the work ends.
    void OnPrefsChanged()
    {
        if (Window.Dispatcher.HasShutdownStarted) return;
        Window.Dispatcher.BeginInvoke(() =>
        {
            if (!Feature.IsOn() && !IsBusy) Window.Close();
            else SettingsChanged();
        });
    }

    protected virtual void SettingsChanged() { }
    protected virtual void Closed() { }
    protected virtual void WorkFinished() { }

    /// Puts the content in the window with the standard margins; extra layers (a drop ring) go over it.
    protected void Install(FrameworkElement content, params UIElement[] overlays)
    {
        root = content;
        content.Margin = new Thickness(20, 16, 20, 18);
        layers.Children.Clear();
        layers.Children.Add(content);
        foreach (var overlay in overlays) layers.Children.Add(overlay);
    }

    /// The window's content without the margins (for offscreen renders in tests).
    public FrameworkElement Content => layers;

    public void Show()
    {
        if (!Feature.IsOn()) return;
        if (!open.Contains(this)) open.Add(this);
        ToolUI.Show(Window);
    }

    public static void CloseAll(Feature feature)
    {
        foreach (var window in open.Where(w => w.Feature == feature).ToList()) window.Window.Close();
    }

    /// Runs work with every control locked, then reports success (sound) or the failure (alert). The real work runs
    /// off the UI thread inside `body` (Task.Run); what `body` does after awaiting it runs back on the UI thread.
    protected Task RunWork(Func<Task> body)
    {
        if (!Feature.IsOn() || IsBusy) return Work;
        IsBusy = true;
        LastError = null;
        if (root != null) root.IsEnabled = false;
        Window.Cursor = Cursors.AppStarting;
        return Work = Run(body);
    }

    async Task Run(Func<Task> body)
    {
        try
        {
            await body();
            Feedback.Done();
        }
        catch (Exception ex)
        {
            Feedback.Refuse();
            LastError = ex.Message;
            IsBusy = false;
            Unlock();
            ShowError(Window, ex.Message);
        }
        IsBusy = false;
        Unlock();
        WorkFinished();
        if (!Feature.IsOn()) Window.Close();
    }

    void Unlock()
    {
        if (root != null) root.IsEnabled = true;
        Window.Cursor = null;
    }
}

/// Small shared pieces of the file tools' windows.
static class FileToolUI
{
    /// A text box with grey hint text while it's empty (an NSTextField's placeholder).
    public static FrameworkElement WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        if (hint.Foreground is null) hint.Foreground = Brushes.Gray;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    public static Button Accent(Button button)
    {
        button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        return button;
    }

    public static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
}

/// Which directory entry a path names, so a rename only ever moves the file that was previewed. The Mac app uses
/// lstat's device and inode. Here: the volume serial and the file ID of the entry itself (a symbolic link or junction
/// is not followed). NTFS and ReFS keep a file's ID across renames; FAT and exFAT derive it from the directory entry,
/// which a rename changes, so there the creation time, last write time and size stand in for it.
readonly record struct FileIdentity(ulong Volume, ulong High, ulong Low, long Created, long Written, long Size)
{
    public static FileIdentity Read(string path)
    {
        using var handle = FileToolsNative.CreateFileW(FileToolsNative.Extended(path), FileToolsNative.FILE_READ_ATTRIBUTES,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, (uint)FileMode.Open,
            FileToolsNative.FILE_FLAG_BACKUP_SEMANTICS | FileToolsNative.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) throw new ToolError(L("Could not access %@", path));
        if (!FileToolsNative.GetFileInformationByHandle(handle, out var info)) throw new ToolError(L("Could not access %@", path));
        bool stable = FileToolsNative.HasStableIds(handle);
        if (stable && FileToolsNative.GetFileInformationByHandleEx(handle, FileToolsNative.FileIdInfo, out FileToolsNative.FILE_ID_INFO id, (uint)Marshal.SizeOf<FileToolsNative.FILE_ID_INFO>()))
            return new FileIdentity(id.VolumeSerialNumber, id.High, id.Low, 0, 0, 0);
        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        if (stable) return new FileIdentity(info.VolumeSerialNumber, 0, index, 0, 0, 0);
        long created = ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow;
        long written = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
        long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        return new FileIdentity(info.VolumeSerialNumber, 0, 0, created, written, size);
    }

    public static FileIdentity? TryRead(string path)
    {
        try { return Read(path); }
        catch (ToolError) { return null; }
    }
}

static partial class FileToolsNative
{
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const int FileIdInfo = 18;
    const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_INVALID_NAME = 123, ERROR_BAD_PATHNAME = 161;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        // FILE_ID_128, as two halves.
        public ulong Low;
        public ulong High;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, out FILE_ID_INFO info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformationByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, char* volumeName, uint volumeNameSize,
        out uint serial, out uint maxComponent, out uint flags, char* fileSystemName, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileExW(string from, string to, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributesW(string name);

    /// NTFS and ReFS keep a file's ID when it is renamed.
    public static unsafe bool HasStableIds(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        char* name = stackalloc char[64];
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, name, 64)) return false;
        var fs = new string(name);
        return fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
    }

    /// Whether anything (a file, folder or link, even a broken one) has this name. Unreadable entries count as there.
    public static bool Exists(string path)
    {
        if (GetFileAttributesW(Extended(path)) != INVALID_FILE_ATTRIBUTES) return true;
        int error = Marshal.GetLastPInvokeError();
        return error is not (ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_NAME or ERROR_BAD_PATHNAME);
    }

    /// Paths longer than MAX_PATH need the \\?\ form for the raw Win32 calls.
    public static string Extended(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
