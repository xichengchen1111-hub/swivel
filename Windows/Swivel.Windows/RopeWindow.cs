using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// The Clothesline rope at the top of the screen under the pointer. Borderless,
/// transparent (empty pixels pass clicks through), topmost, never activated and
/// not in the taskbar or Alt+Tab. The prints are the same buttons as in the
/// Clothesline window. Moves only while its springs move.
internal sealed class RopeWindow : Window
{
    internal const int ExNoActivate = 0x08000000, ExToolWindow = 0x80, ExTopmost = 0x8, ExAppWindow = 0x40000;
    internal const int MouseActivate = 0x21, NoActivate = 3;
    /// Extended styles for the rope: no activation, tool window (no taskbar or Alt+Tab), topmost.
    internal static long RopeStyle(long current) => (current | ExNoActivate | ExToolWindow | ExTopmost) & ~(long)ExAppWindow;

    private sealed record Hanger(ClotheslinePrintButton Button,double Peg,double Tilt,RotateTransform Rotate,TranslateTransform Lower);
    private readonly App app;
    private readonly ClotheslinePrints prints;
    private readonly RopeMotion motion = new();
    private readonly Canvas canvas = new() { ClipToBounds = false };
    private readonly TranslateTransform slide = new();
    private readonly Path cord = new() { IsHitTestVisible = false, StrokeThickness = 2.5 };
    private readonly Path halo = new() { IsHitTestVisible = false, StrokeThickness = 6, Opacity = .55 };
    private readonly List<Hanger> hangers = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Padding = new Thickness(8,4,8,4) };
    private readonly Border statusPlate = new() { CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed };
    private readonly StackPanel statusRow = new() { Orientation = Orientation.Horizontal };
    private ScreenRect work;
    private nint monitor;
    private double spanLeft, spanRight;
    private bool listening, dirty = true;
    private long last;

    internal RopeWindow(App app)
    {
        this.app = app;
        prints = new ClotheslinePrints(app,this,() => IsVisible && app.Prefs.IsOn(Feature.Clothesline) && !app.Busy,Report) { PreviewWidth = 40 };
        Title = app.L("Clothesline");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = 0; Top = 0; Width = 400; Height = RopeLayout.Height;
        canvas.RenderTransform = slide;
        statusPlate.Child = status; statusRow.Children.Add(statusPlate); statusRow.Children.Add(prints.StitchPreview);
        Content = canvas;
        SourceInitialized += (_,_) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle,-20,(nint)RopeStyle(GetWindowLongPtrW(handle,-20)));
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        };
        // WPF applies the system's suggested size on a DPI change; put the rope back to its own size after.
        DpiChanged += (_,_) => Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(Place));
        SizeChanged += (_,_) => { dirty = true; if (IsVisible) Rebuild(); };
        app.Prints.CollectionChanged += Changed;
        Closed += (_,_) => { app.Prints.CollectionChanged -= Changed; StopFrames(); prints.Dispose(); };
    }

    private static nint Hook(nint window,int message,nint wParam,nint lParam,ref bool handled)
    {
        // A click on a print must not take focus from the app the person is working in.
        if (message == MouseActivate) { handled = true; return NoActivate; }
        return 0;
    }

    internal bool Engaged => IsVisible && prints.Engaged;
    internal bool Lowered => IsVisible && motion.Lowered;
    private bool Reduced => !app.Prefs.AnimateMenus || !SystemParameters.ClientAreaAnimation;

    /// The part of the screen that counts as "on the rope", in physical pixels: the prints' span, full height.
    internal ScreenRect? ActiveArea()
    {
        if (!IsVisible || !GetWindowRect(new WindowInteropHelper(this).Handle,out var rect)) return null;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new ScreenRect(rect.Left + (int)Math.Floor(spanLeft * scale),rect.Top,rect.Left + (int)Math.Ceiling(spanRight * scale),rect.Bottom);
    }

    /// Lowers the rope along the top of `work` (a monitor's work area). Caught while lifting, it turns around.
    internal void ShowOn(ScreenRect work,nint monitor)
    {
        // Following the pointer to another monitor: drop in afresh there rather than jump across.
        if (IsVisible && (work != this.work || monitor != this.monitor)) motion.Raise();
        this.work = work; this.monitor = monitor;
        new WindowInteropHelper(this).EnsureHandle();
        Place();
        if (!IsVisible) Show();
        Place();
        if (dirty) Rebuild();
        motion.Reduced = Reduced;
        motion.Lower();
        Apply(); Wake();
    }

    internal void Lift()
    {
        prints.CancelGestures();
        motion.Reduced = Reduced;
        motion.Lift();
        if (motion.Hidden) { HideNow(); return; }
        Wake();
    }

    private void HideNow() { StopFrames(); prints.ClearHover(); prints.CancelGestures(); Report(""); Hide(); }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || work.Width <= 0) return;
        uint dpi = GetDpiForMonitor(monitor,0,out uint x,out _) == 0 && x > 0 ? x : 96;
        int height = (int)Math.Ceiling(RopeLayout.Height * dpi / 96.0);
        if (GetWindowRect(handle,out var now) && now.Left == work.Left && now.Top == work.Top && now.Right == work.Right && now.Bottom == work.Top + height) return;
        SetWindowPos(handle,-1,work.Left,work.Top,work.Width,height,0x10 | 0x200); // NOACTIVATE | NOOWNERZORDER
    }

    private void Changed(object? sender,NotifyCollectionChangedEventArgs e)
    {
        dirty = true;
        if (!IsVisible) return;
        Rebuild();
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && e.NewStartingIndex >= 0 && e.NewStartingIndex < motion.Count && motion.Lowered)
        { motion.Reduced = Reduced; motion.Arrive(e.NewStartingIndex); Wake(); }
    }

    private void Rebuild()
    {
        dirty = false;
        prints.Clear(); hangers.Clear(); canvas.Children.Clear();
        double width = ActualWidth > 0 ? ActualWidth : Width;
        var palette = MenuColors.Active(app,MenuTemplate.Clothesline);
        Brush rope = palette is null ? SystemColors.ControlDarkDarkBrush : MenuColors.Brush(palette.Rope), peg = palette is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(palette.Peg);
        halo.Stroke = SystemColors.WindowBrush; cord.Stroke = rope;
        canvas.Children.Add(halo); canvas.Children.Add(cord);
        statusPlate.Background = SystemColors.WindowBrush; status.Foreground = SystemColors.WindowTextBrush;
        int count = app.Prints.Count;
        motion.SetPrints(count);
        var (size,pegs) = RopeLayout.Pegs(count,width);
        for (int i = 0; i < count; i++)
        {
            var hanger = new Canvas();
            var button = prints.Create(app.Prints[i],palette,size,size * 1.12,size - 16);
            Canvas.SetLeft(button,-size / 2); Canvas.SetTop(button,6); hanger.Children.Add(button);
            var clip = new Rectangle { Width = 8,Height = 20,Fill = peg,IsHitTestVisible = false,RadiusX = 2,RadiusY = 2 }; Canvas.SetLeft(clip,-4); Canvas.SetTop(clip,-8); hanger.Children.Add(clip);
            var rotate = new RotateTransform(); var lower = new TranslateTransform(); var group = new TransformGroup(); group.Children.Add(rotate); group.Children.Add(lower);
            hanger.RenderTransform = group; Canvas.SetLeft(hanger,pegs[i]); Canvas.SetTop(hanger,RopeLayout.RopeY);
            canvas.Children.Add(hanger);
            hangers.Add(new Hanger(button,pegs[i],i % 2 == 0 ? -1 : 1,rotate,lower));
        }
        if (count == 0)
        {
            var hint = new Border { Background = SystemColors.WindowBrush,CornerRadius = new CornerRadius(6),Child = new TextBlock { Text = app.L("No prints yet. Hang images from the Clothesline window."),Foreground = SystemColors.WindowTextBrush,Padding = new Thickness(10,6,10,6) } };
            hint.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            Canvas.SetLeft(hint,(width - hint.DesiredSize.Width) / 2); Canvas.SetTop(hint,RopeLayout.RopeY + RopeTuning.RestSag + 16); canvas.Children.Add(hint);
            spanLeft = Math.Max(0,width / 2 - 240); spanRight = Math.Min(width,width / 2 + 240);
        }
        else { spanLeft = Math.Max(0,pegs[0] - size / 2 - RopeLayout.Margin); spanRight = Math.Min(width,pegs[^1] + size / 2 + RopeLayout.Margin); }
        Canvas.SetLeft(statusRow,spanLeft); Canvas.SetTop(statusRow,RopeLayout.RopeY + 6 + RopeLayout.Print * 1.12 + 20);
        if (statusRow.Parent is Panel old) old.Children.Remove(statusRow);
        canvas.Children.Add(statusRow);
        Apply();
    }

    private void Report(string text) { status.Text = text; statusPlate.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; }

    private void Apply()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width, sag = motion.Sag;
        slide.Y = -(1 - motion.Drop) * RopeLayout.Height;
        // A quadratic Bézier through both ends with its control at twice the dip is exactly RopeLayout.Dip's parabola.
        var figure = new PathFigure { StartPoint = new Point(0,RopeLayout.RopeY) };
        figure.Segments.Add(new QuadraticBezierSegment(new Point(width / 2,RopeLayout.RopeY + 2 * sag),new Point(width,RopeLayout.RopeY),true));
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); geometry.Freeze();
        cord.Data = geometry; halo.Data = geometry;
        for (int i = 0; i < hangers.Count && i < motion.Count; i++)
        {
            var h = hangers[i];
            h.Rotate.Angle = h.Tilt + motion.Swing(i);
            h.Lower.Y = RopeLayout.Dip(h.Peg,width,sag);
        }
    }

    private void Wake()
    {
        if (listening || !motion.Moving) return;
        listening = true; last = 0; CompositionTarget.Rendering += Frame;
    }

    private void Frame(object? sender,EventArgs args)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = last == 0 ? 0 : (now - last) / (double)Stopwatch.Frequency; last = now;
        motion.Reduced = Reduced;
        bool moving = motion.Advance(dt);
        Apply();
        if (moving) return;
        StopFrames();
        if (motion.Hidden) HideNow();
    }

    private void StopFrames() { if (!listening) return; CompositionTarget.Rendering -= Frame; listening = false; }

    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("shcore.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDpiForMonitor(nint monitor,int type,out uint dpiX,out uint dpiY);
}

/// One cursor sample: where the pointer is, its monitor and work area, and whether a full-screen app is in front there.
internal readonly record struct RopeSample(int X,int Y,ScreenRect Monitor,ScreenRect Work,nint Handle,bool FullScreen);

/// Polls the cursor every 50 ms while the top-edge rope is on (no hooks, no permissions)
/// and feeds RopeEdgeTrigger. The sampler and callbacks are injectable for checks.
internal sealed class RopeEdgeWatcher : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = Interval };
    private readonly RopeEdgeTrigger trigger = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Func<bool,RopeSample?> sample;
    private readonly Action<RopeSample> show;
    private readonly Action hide;
    private readonly Func<ScreenRect?> area;
    private readonly Func<bool> engaged;

    internal RopeEdgeWatcher(Func<bool,RopeSample?> sample,Action<RopeSample> show,Action hide,Func<ScreenRect?> area,Func<bool> engaged)
    {
        this.sample = sample; this.show = show; this.hide = hide; this.area = area; this.engaged = engaged;
        timer.Tick += (_,_) => Step(clock.Elapsed);
    }

    internal bool Running => timer.IsEnabled;
    internal bool Showing => trigger.Showing;
    internal void Start() { if (!timer.IsEnabled) timer.Start(); }
    /// Stops polling and forgets the rope's state; the caller hides or closes the window.
    internal void Stop() { timer.Stop(); trigger.Reset(); }

    internal RopeEdgeAction Step(TimeSpan now)
    {
        // No cursor (secure desktop, locked session): nothing changes.
        if (sample(trigger.Showing) is not { } s) return RopeEdgeAction.None;
        var action = trigger.Update(now,s.X,s.Y,s.Monitor,s.FullScreen,trigger.Showing ? area() : null,engaged());
        if (action == RopeEdgeAction.Show) show(s);
        else if (action == RopeEdgeAction.Hide) hide();
        return action;
    }

    public void Dispose() => Stop();

    /// The real sampler. The full-screen check runs only at the top edge or while the rope is down.
    internal static RopeSample? Native(bool showing)
    {
        if (!GetCursorPos(out var point)) return null;
        nint monitor = MonitorFromPoint(point,2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor,ref info)) return null;
        var rect = Rect(info.Monitor);
        bool full = (showing || RopeEdgeTrigger.AtEdge(point.X,point.Y,rect)) && ForegroundFullScreen(monitor,rect);
        return new RopeSample(point.X,point.Y,rect,Rect(info.Work),monitor,full);
    }

    /// The desktop and shell windows are never "full screen", even though they cover the monitor.
    internal static bool IsDesktopClass(string name) => name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static bool ForegroundFullScreen(nint monitor,ScreenRect rect)
    {
        nint window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window,out uint process);
        var name = new StringBuilder(64);
        bool desktopOrOwn = process == (uint)Environment.ProcessId || window == GetShellWindow() || (GetClassNameW(window,name,name.Capacity) > 0 && IsDesktopClass(name.ToString()));
        ScreenRect? bounds = RopeWindow.GetWindowRect(window,out var r) ? Rect(r) : null;
        return RopeEdgeTrigger.IsFullScreen(bounds,rect,MonitorFromWindow(window,2) == monitor,desktopOrOwn);
    }

    private static ScreenRect Rect(RopeWindow.NativeRect r) => new(r.Left,r.Top,r.Right,r.Bottom);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RopeWindow.NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",ExactSpelling = true,CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",ExactSpelling = true,CharSet = CharSet.Unicode,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetClassNameW(nint window,StringBuilder name,int capacity);
}
