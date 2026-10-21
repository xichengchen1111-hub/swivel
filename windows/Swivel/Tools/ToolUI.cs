using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Swivel;

/// Shared building blocks of the tool windows (a port of ToolUI.swift), also used by the settings window.
///
/// Text passed to these helpers is shown as given: wrap it in `L("…")` at the call site, so the strings check in
/// the tests sees every literal.
static class ToolUI
{
    /// The Segoe Fluent Icons font (Segoe MDL2 Assets on Windows 10).
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// A standard push button (Fluent style from the app theme).
    public static Button Button(string title, Action? click = null)
    {
        var button = new Button { Content = title, MinWidth = 80, Padding = new Thickness(12, 5, 12, 6) };
        if (click != null) button.Click += (_, _) => click();
        return button;
    }

    /// A button with a glyph before its title.
    public static Button Button(string glyph, string title, Action? click = null)
    {
        var button = Button(title, click);
        button.Content = Row([Glyph(glyph, 14), new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }], spacing: 8);
        AutomationName(button, title);
        return button;
    }

    /// A secondary, wrapping explanation in small type (the Mac app's `ToolUI.note`).
    public static TextBlock Note(string text) => Scaled(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.68,
    }, 12);

    /// An explicit font size that follows Feel & Text's text size (1.3). Everything that inherits its size gets it
    /// from the window root (`AppTypography.Apply`); text with a size of its own goes through here, and follows a
    /// change live while it is on screen. Icon glyphs keep their size, like the Mac's symbols.
    public static T Scaled<T>(T element, double size) where T : FrameworkElement
    {
        void update() => element.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, AppTypography.Size(size));
        void changed() => element.Dispatcher.BeginInvoke(new Action(update));
        update();
        element.Loaded += (_, _) =>
        {
            Prefs.Changed -= changed;
            Prefs.Changed += changed;
            update();
        };
        element.Unloaded += (_, _) => Prefs.Changed -= changed;
        return element;
    }

    /// A Segoe Fluent Icons glyph.
    public static TextBlock Glyph(string glyph, double size = 16) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    /// A large icon and two lines of text, centred: what an empty list shows. It never takes the mouse, so drops and
    /// clicks reach the list under it.
    public static FrameworkElement EmptyState(string glyph, string title, string? detail = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340 };
        var icon = Glyph(glyph, 32);
        icon.Opacity = 0.45;
        icon.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7 });
        if (detail != null)
        {
            var note = Note(detail);
            note.TextAlignment = TextAlignment.Center;
            note.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(note);
        }
        stack.IsHitTestVisible = false;
        return stack;
    }

    /// A vertical stack with even spacing (NSStackView, vertical).
    public static StackPanel Stack(IEnumerable<UIElement> views, double spacing = 12) => Spaced(new StackPanel(), views, spacing, vertical: true);

    /// A horizontal row with even spacing, vertically centred.
    public static StackPanel Row(IEnumerable<UIElement> views, double spacing = 12) =>
        Spaced(new StackPanel { Orientation = Orientation.Horizontal }, views, spacing, vertical: false);

    static StackPanel Spaced(StackPanel panel, IEnumerable<UIElement> views, double spacing, bool vertical)
    {
        bool first = true;
        foreach (var view in views)
        {
            if (view is FrameworkElement element)
            {
                if (!first) element.Margin = vertical ? new Thickness(element.Margin.Left, spacing, element.Margin.Right, element.Margin.Bottom)
                                                      : new Thickness(spacing, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
                if (!vertical) element.VerticalAlignment = VerticalAlignment.Center;
            }
            panel.Children.Add(view);
            first = false;
        }
        return panel;
    }

    /// A bar with views on the leading and trailing side (NSStackView with gravity areas).
    public static DockPanel Bar(IEnumerable<UIElement> leading, IEnumerable<UIElement> trailing, double spacing = 8)
    {
        var bar = new DockPanel { LastChildFill = true };
        var right = Row(trailing, spacing);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var left = Row(leading, spacing);
        left.Margin = new Thickness(0, 0, spacing, 0);
        bar.Children.Add(left);
        return bar;
    }

    /// Sets the name a screen reader says for an element.
    public static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// The accent brush that follows Windows' accent colour.
    public static void UseAccent(FrameworkElement element, DependencyProperty property) =>
        element.SetResourceReference(property, SystemColors.AccentColorBrushKey);

    public static Color AccentColor => SystemColors.AccentColor;

    /// A red that reads on both themes (Fluent's critical text colours).
    public static Brush ErrorBrush => new SolidColorBrush(Native.DarkMode ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C));

    /// Shows a normal window and brings it to the front.
    public static void Show(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// Runs `body` on the UI thread after `delay`. DispatcherTimers keep firing inside WPF's modal loops (an Open
    /// dialog, a message box), like the Mac app's run-loop timers in the modal-panel mode. For timing a step (closing
    /// a panel, taking a notice down), never for motion: that's a Spring.
    public static DispatcherTimer After(double seconds, Action body)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            body();
        };
        timer.Start();
        return timer;
    }

    /// An Open dialog; empty when cancelled. `imagesOnly` filters to the image types Swivel reads. Windows' file
    /// dialog can't pick files and folders together, so folders arrive by dropping them, or through `ChooseFolders`.
    public static List<string> ChooseFiles(bool imagesOnly, bool multiple) => ChooseFiles(imagesOnly, multiple, owner: null);

    public static List<string> ChooseFiles(bool imagesOnly, bool multiple, Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = multiple,
            Filter = imagesOnly ? ImageFilter : L("All files") + "|*.*",
            CheckFileExists = true,
            Title = imagesOnly ? L("Choose an Image…") : L("Choose Files…"),
        };
        bool chosen = owner is { IsVisible: true } ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
        return chosen ? [.. dialog.FileNames] : [];
    }

    /// A folder picker; empty when cancelled.
    public static List<string> ChooseFolders(bool multiple, Window? owner = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = multiple };
        bool chosen = owner is { IsVisible: true } ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
        return chosen ? [.. dialog.FolderNames] : [];
    }

    /// "Images|*.jpg;*.png;…|All files|*.*", built from the image extensions Swivel knows.
    public static string ImageFilter =>
        L("Images") + "|" + string.Join(";", FileKinds.ImageExtensions.Order().Select(e => "*." + e)) + "|" + L("All files") + "|*.*";

    /// The files and folders in a drag from Explorer (or any app that offers file paths). Only paths that exist.
    public static List<string> DroppedFiles(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return [];
        try
        {
            return data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList()
                : [];
        }
        catch (COMException) { return []; }
    }

    /// The effect to report for a drop of files: Copy (or Link), never Move — Explorer deletes the source after a move.
    public static DragDropEffects DropEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy :
        allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;

    /// The Tools & settings area of Feel & Text (1.3): every spring in the tool panels and editors steps with it.
    public static MotionTuning Tuning => Prefs.Interaction.Tools;

    /// Fades an element up to fully opaque. Opacity only (critically damped), so it stays the same with animations off.
    public static void FadeIn(UIElement element, double from = 0, double duration = 0.15)
    {
        var spring = new Spring(from, response: Math.Max(duration, 0.05) * 1.6, damping: 1) { Target = 1 };
        element.Opacity = from;
        Animate(element, OpacityKey, dt =>
        {
            spring.Step(dt, Tuning);
            element.Opacity = Math.Clamp(spring.Value, 0, 1);
            if (spring.IsSettled) element.Opacity = 1;
            return !spring.IsSettled;
        });
    }

    static readonly DependencyProperty OpacityKey = DependencyProperty.RegisterAttached("SwivelFade", typeof(FrameDriver), typeof(ToolUI));
    static readonly DependencyProperty ShakeKey = DependencyProperty.RegisterAttached("SwivelShake", typeof(FrameDriver), typeof(ToolUI));

    /// Drives one animation per element and slot; a new one replaces the one still running.
    static void Animate(UIElement element, DependencyProperty slot, Func<double, bool> frame)
    {
        (element.GetValue(slot) as FrameDriver)?.Stop();
        var driver = new FrameDriver { OnFrame = frame };
        element.SetValue(slot, driver);
        driver.Start();
    }

    /// "That didn't work": a short sideways shake of the offending control plus the refuse sound. With animations off
    /// the control stays still and the refuse sound alone marks it (sounds go through Feedback only, so the Mac's
    /// extra system beep is not repeated here).
    public static void Refuse(FrameworkElement element)
    {
        Feedback.Refuse();
        if (Motion.Reduced) return;
        var shift = element.RenderTransform as TranslateTransform;
        if (shift is null)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }
        // A kick to the left that a lively spring brings back: [0, -6, 5, -3, 2, 0] over ~0.28 s, as on the Mac.
        var spring = new Spring(0, response: 0.13, damping: 0.6) { Velocity = -260 };
        // Speed only: the Mac shortens the shake's keyframes (duration / speed) but keeps their shape, so a calm
        // (bounce 0) setting still shakes.
        var tuning = Tuning with { Bounce = 1 };
        Animate(element, ShakeKey, dt =>
        {
            spring.Step(dt, tuning);
            shift.X = spring.IsSettled ? 0 : spring.Value;
            return !spring.IsSettled;
        });
    }

    /// Keeps a button as wide as its widest title, so swapping titles (Copied, Processing 3 of 12…) never shifts the
    /// buttons beside it.
    public static void ReserveWidth(Button button, IEnumerable<object> contents)
    {
        var current = button.Content;
        double widest = 0;
        foreach (var content in contents)
        {
            button.Content = content;
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, button.DesiredSize.Width);
        }
        button.Content = current;
        button.MinWidth = Math.Ceiling(widest);
    }

    /// The shell's icon or thumbnail for a file, as a frozen image (null when the shell has none).
    public static ImageSource? FileIcon(string path, int pixels = 64) => ShellIcons.Load(path, pixels);
}

/// The window the user was in when a tool panel took the keyboard, so activation can go back to it afterwards
/// (the Mac app's `ActivationReturn`).
struct ActivationReturn
{
    public IntPtr Window { get; private set; }

    /// Call just before activating a Swivel window. Nothing is remembered when Swivel itself (or nothing) was in front.
    public void Remember(IntPtr foreground)
    {
        Window = foreground == IntPtr.Zero || ToolNative.IsOwnWindow(foreground) ? IntPtr.Zero : foreground;
    }

    /// The window to give activation back to, at most once: only while Swivel still has the foreground (the user
    /// hasn't clicked into another app meanwhile) and that window still exists.
    public IntPtr Take(bool swivelIsActive)
    {
        var window = Window;
        Window = IntPtr.Zero;
        return swivelIsActive && window != IntPtr.Zero && ToolNative.IsWindow(window) ? window : IntPtr.Zero;
    }
}

/// An accent outline over a drop target while files hover over it. It never takes clicks or drops itself.
sealed class DropRing : Border
{
    readonly FrameDriver driver = new();
    Spring fade = new(0, response: 0.12, damping: 1);

    public DropRing()
    {
        IsHitTestVisible = false;
        BorderThickness = new Thickness(2);
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(6);
        Opacity = 0;
        ToolUI.UseAccent(this, BorderBrushProperty);
        driver.OnFrame = dt =>
        {
            fade.Step(dt, ToolUI.Tuning);
            Opacity = Math.Clamp(fade.Value, 0, 1);
            return !fade.IsSettled;
        };
    }

    /// Fades in quickly and out a little faster (opacity only, so the same with animations off).
    public void SetShown(bool shown)
    {
        fade.Target = shown ? 1 : 0;
        fade.Response = shown ? 0.12 : 0.1;
        driver.Start();
    }
}

/// Undo and redo for a tool window (NSUndoManager's role): an action registered while undoing goes on the redo stack.
sealed class UndoStack
{
    readonly Stack<Action> undo = new(), redo = new();
    bool undoing, redoing;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Register(Action action)
    {
        if (undoing) redo.Push(action);
        else
        {
            undo.Push(action);
            if (!redoing) redo.Clear();
        }
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        undoing = true;
        try { undo.Pop()(); } finally { undoing = false; }
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        redoing = true;
        try { redo.Pop()(); } finally { redoing = false; }
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}

/// Win32 calls used only by the tools.
static partial class ToolNative
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetShellWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// MONITOR_DEFAULTTONEAREST = 2.
    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(Native.POINT point, uint flags);

    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001;

    static readonly uint ownProcess = (uint)Environment.ProcessId;

    /// Whether a window belongs to this process.
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == ownProcess;
    }

    /// Whether this process has the foreground window.
    public static bool SwivelIsActive
    {
        get
        {
            var foreground = GetForegroundWindow();
            return foreground != IntPtr.Zero && IsOwnWindow(foreground);
        }
    }

    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;
}

/// File icons and thumbnails from the shell (IShellItemImageFactory: the same pictures Explorer shows), loaded on a
/// background STA thread so a slow network folder never stalls the UI.
static unsafe partial class ShellIcons
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid riid, out IntPtr item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(IntPtr handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int Cx, Cy; }

    static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    static readonly System.Collections.Concurrent.BlockingCollection<Action> queue = [];
    static Thread? worker;

    /// Loads on the worker thread and hands the image to `done` on the caller's dispatcher.
    public static void Request(string path, int pixels, Action<ImageSource?> done)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        EnsureWorker();
        queue.Add(() =>
        {
            ImageSource? image = null;
            try { image = Load(path, pixels); } catch (Exception) { }
            dispatcher.BeginInvoke(() => done(image));
        });
    }

    static void EnsureWorker()
    {
        if (worker != null) return;
        lock (queue)
        {
            if (worker != null) return;
            worker = new Thread(() => { foreach (var job in queue.GetConsumingEnumerable()) job(); })
            { IsBackground = true, Name = "Swivel shell icons" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
    }

    /// The thumbnail (or icon, when there is no thumbnail) at up to `pixels` square. Call from an STA thread.
    public static ImageSource? Load(string path, int pixels)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ImageFactoryId, out var factory) < 0 || factory == IntPtr.Zero) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            // IShellItemImageFactory::GetImage is the first method after IUnknown's three.
            var getImage = (delegate* unmanaged[Stdcall]<IntPtr, SIZE, int, IntPtr*, int>)(*(*(void***)factory + 3));
            if (getImage(factory, new SIZE { Cx = pixels, Cy = pixels }, 0 /* SIIGBF_RESIZETOFIT */, &bitmap) < 0 || bitmap == IntPtr.Zero)
                return null;
            return FromHBitmap(bitmap);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.Release(factory);
        }
    }

    /// Copies a 32-bit shell bitmap top-down, keeping its alpha (Imaging.CreateBitmapSourceFromHBitmap drops it).
    static BitmapSource? FromHBitmap(IntPtr bitmap)
    {
        BITMAP info;
        if (GetObjectW(bitmap, sizeof(BITMAP), &info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        int width = info.Width, height = Math.Abs(info.Height);
        var pixels = new byte[width * height * 4];
        var header = new BITMAPINFOHEADER
        {
            Size = (uint)sizeof(BITMAPINFOHEADER), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0,
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* bits = pixels)
                if (GetDIBits(dc, bitmap, 0, (uint)height, bits, &header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // Photos come back with no alpha at all (every byte 0): draw them opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        var source = BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}
