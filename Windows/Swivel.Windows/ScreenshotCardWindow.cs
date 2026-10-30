using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// The card a new screenshot waits on in the bottom-right corner of the monitor with the
/// pointer. Never activated, not in the taskbar or Alt+Tab. Click it or drag it up to hang
/// the file on the Clothesline; drag it anywhere else to hand the file out (copy or link,
/// never move). Leaves by itself after 2.5 s unattended. Only refers to the file.
internal sealed class ScreenshotCardWindow : Window
{
    internal const double CardWidth = 260, CornerMargin = 16;
    private readonly App app;
    private readonly Func<string,Task> hang;
    private readonly Action<Guid> done;
    private readonly CardDismissClock clock = new();
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Image picture = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,6,0,0) };
    private readonly Border plate = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10), BorderThickness = new Thickness(1) };
    private readonly TranslateTransform shift = new();
    private readonly MotionSpring slide = new(0,.42,.86), fade = new(0,.3,1), lift = new(0,.38,.8);
    private ArrivalCard<BitmapSource>? card, waiting;
    private ScreenRect work;
    private nint monitor;
    private uint dpi = 96;
    private int cornerLeft, cornerTop, pixelWidth, pixelHeight;
    private bool leaving, listening, pressing;
    private CardGesture gesture;
    private Point pressAt;
    private long last;

    internal ScreenshotCardWindow(App app,Func<string,Task> hang,Action<Guid> done)
    {
        this.app = app; this.hang = hang; this.done = done;
        Title = app.L("Screenshot card");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Width = CardWidth; Height = 200;
        var stack = new StackPanel(); stack.Children.Add(picture); stack.Children.Add(caption);
        plate.Child = stack; plate.RenderTransform = shift; Content = plate;
        caption.Text = app.L("Click or drag up to hang on the Clothesline. Drag elsewhere to use the file.");
        AutomationProperties.SetName(this,app.L("Screenshot card"));
        AutomationProperties.SetHelpText(this,caption.Text);
        SourceInitialized += (_,_) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle,-20,(nint)RopeWindow.RopeStyle(GetWindowLongPtrW(handle,-20)));
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        };
        // WPF applies the system's suggested size on a DPI change; put the card back to its own place after.
        DpiChanged += (_,_) => Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(Place));
        MouseEnter += (_,_) => clock.SetHover(true,Now);
        MouseLeave += (_,_) => { if (!pressing) clock.SetHover(false,Now); };
        MouseLeftButtonDown += Pressed;
        MouseMove += Moved;
        MouseLeftButtonUp += Released;
        LostMouseCapture += (_,_) => { if (pressing && gesture != CardGesture.HandOut) { Cancel(); ShowWaiting(); } };
        MouseRightButtonUp += (_,_) => Leave(false);
        timer.Tick += (_,_) => { if (IsVisible && !leaving && clock.Due(Now)) Leave(false); };
        Closed += (_,_) => { timer.Stop(); StopFrames(); };
    }

    private TimeSpan Now => watch.Elapsed;
    private bool Reduced => !app.Prefs.AnimateMenus || !SystemParameters.ClientAreaAnimation;
    internal Guid? Current => leaving ? null : card?.Id;
    internal CardDismissClock Clock => clock;

    /// Shows `next`, replacing any card still waiting.
    internal void Present(ArrivalCard<BitmapSource> next)
    {
        // Never swap the card the person is holding or dragging out; show the newer one after.
        if (pressing && card is { } held && held.Id != next.Id && !leaving) { if (waiting is { } skipped && skipped.Id != next.Id) done(skipped.Id); waiting = next; return; }
        waiting = null;
        // Replacing a card, even one already on its way out, frees the old one now.
        StopFrames();
        if (card is { } previous && previous.Id != next.Id) done(previous.Id);
        if (IsMouseCaptured) ReleaseMouseCapture();
        card = next; leaving = false; pressing = false; gesture = CardGesture.Pending;
        picture.Source = next.Thumbnail;
        caption.Text = app.L("Click or drag up to hang on the Clothesline. Drag elsewhere to use the file.");
        double scale = Math.Min(1,Math.Min((CardWidth - 20) / Math.Max(1,next.Thumbnail.PixelWidth),170.0 / Math.Max(1,next.Thumbnail.PixelHeight)));
        picture.Width = next.Thumbnail.PixelWidth * scale; picture.Height = next.Thumbnail.PixelHeight * scale;
        plate.Background = SystemColors.WindowBrush; plate.BorderBrush = SystemColors.ActiveBorderBrush; caption.Foreground = SystemColors.WindowTextBrush;
        plate.Measure(new Size(CardWidth,double.PositiveInfinity));
        Height = Math.Ceiling(plate.DesiredSize.Height);
        Locate();
        new WindowInteropHelper(this).EnsureHandle();
        lift.Target = 0; lift.Snap();
        slide.Target = CardWidth; slide.Snap(); slide.Target = 0;
        fade.Target = 0; fade.Snap(); fade.Target = 1;
        if (Reduced) { slide.Snap(); fade.Snap(); }
        Apply();
        Place();
        if (!IsVisible) Show();
        Place();
        clock.Show(Now); clock.SetHover(IsMouseOver,Now); timer.Start(); Wake();
    }

    /// The monitor with the pointer, or the primary one.
    private void Locate()
    {
        GetCursorPos(out var point);
        monitor = MonitorFromPoint(point,1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        work = monitor != 0 && GetMonitorInfoW(monitor,ref info) ? new(info.Work.Left,info.Work.Top,info.Work.Right,info.Work.Bottom) : default;
        dpi = monitor != 0 && GetDpiForMonitor(monitor,0,out uint x,out _) == 0 && x > 0 ? x : 96;
        double s = dpi / 96.0;
        if (monitor == 0 || work.Width <= 0) work = new(0,0,(int)(SystemParameters.WorkArea.Width * s),(int)(SystemParameters.WorkArea.Height * s));
        pixelWidth = (int)Math.Ceiling(CardWidth * s); pixelHeight = (int)Math.Ceiling(Height * s);
        var (left,top) = ScreenshotCards.Corner(work.Left,work.Top,work.Right,work.Bottom,pixelWidth,pixelHeight,Math.Round(CornerMargin * s));
        cornerLeft = (int)left; cornerTop = (int)top;
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || pixelWidth <= 0) return;
        int top = cornerTop + (int)Math.Round(lift.Value);
        SetWindowPos(handle,-1,cornerLeft,top,pixelWidth,pixelHeight,0x10 | 0x200); // NOACTIVATE | NOOWNERZORDER
    }

    private nint Hook(nint window,int message,nint wParam,nint lParam,ref bool handled)
    {
        // A click must not take focus from the app the person is working in.
        if (message == RopeWindow.MouseActivate) { handled = true; return RopeWindow.NoActivate; }
        // Keep the card's own rectangle when it lands on a monitor with another DPI.
        if (message == 0x02E0 && lParam != 0 && pixelWidth > 0)
            Marshal.StructureToPtr(new RopeWindow.NativeRect { Left = cornerLeft,Top = cornerTop + (int)Math.Round(lift.Value),Right = cornerLeft + pixelWidth,Bottom = cornerTop + (int)Math.Round(lift.Value) + pixelHeight },lParam,false);
        return 0;
    }

    private static Point PointerAt() { GetCursorPos(out var p); return new(p.X,p.Y); }

    private void Pressed(object sender,MouseButtonEventArgs e)
    {
        if (leaving || card is null) return;
        e.Handled = true; pressing = true; gesture = CardGesture.Pending; pressAt = PointerAt();
        clock.SetPressed(true,Now); CaptureMouse();
    }

    private void Moved(object sender,MouseEventArgs e)
    {
        if (!pressing || leaving || card is not { } current) return;
        Point now = PointerAt(); double s = dpi / 96.0, dx = (now.X - pressAt.X) / s, dy = (now.Y - pressAt.Y) / s;
        if (gesture == CardGesture.Pending) gesture = CardGestures.Classify(dx,dy,Math.Max(SystemParameters.MinimumVerticalDragDistance,4));
        if (gesture == CardGesture.Lift)
        {
            // The card follows the pointer upward; it never goes below its corner.
            lift.Target = Math.Min(0,now.Y - pressAt.Y); lift.Snap(); Place();
        }
        else if (gesture == CardGesture.HandOut) HandOut(current);
    }

    private void HandOut(ArrivalCard<BitmapSource> current)
    {
        ReleaseMouseCapture();
        var data = new DataObject(DataFormats.FileDrop,new[] { current.Path });
        DragDropEffects result = DragDropEffects.None;
        // Copy or link only: the receiving app may never move the screenshot away.
        try { result = DragDrop.DoDragDrop(this,data,DragDropEffects.Copy | DragDropEffects.Link); }
        catch (Exception) { result = DragDropEffects.None; }
        pressing = false; gesture = CardGesture.Pending;
        clock.SetPressed(false,Now);
        if (!IsMouseOver) clock.SetHover(false,Now);
        if (result != DragDropEffects.None && ReferenceEquals(card,current)) Leave(false);
        ShowWaiting();
    }

    private void ShowWaiting() { if (!pressing && waiting is { } next) { waiting = null; Present(next); } }

    private void Released(object sender,MouseButtonEventArgs e)
    {
        if (!pressing) return;
        e.Handled = true;
        var was = gesture; pressing = false; gesture = CardGesture.Pending;
        double dy = lift.Value / (dpi / 96.0);
        ReleaseMouseCapture();
        if (was == CardGesture.Pending || (was == CardGesture.Lift && CardGestures.Hangs(dy))) { Hang(); ShowWaiting(); return; }
        Cancel(); ShowWaiting();
    }

    private void Cancel()
    {
        pressing = false; gesture = CardGesture.Pending;
        clock.SetPressed(false,Now); if (!IsMouseOver) clock.SetHover(false,Now);
        lift.Target = 0; if (Reduced) lift.Snap();
        Place(); Wake();
    }

    private void Hang()
    {
        if (card is not { } current || leaving) return;
        // Another operation holds Swivel's work slot: keep the card and say so, rather than lose the print.
        if (app.Busy || !app.Prefs.IsOn(Feature.Clothesline))
        { caption.Text = app.L("Swivel is busy. Try again in a moment."); Cancel(); clock.Show(Now); clock.SetHover(IsMouseOver,Now); ShowWaiting(); return; }
        Leave(true);
        _ = HangAsync(current.Path);
    }

    private async Task HangAsync(string path)
    {
        try { await hang(path); } catch (Exception error) { app.ShowError(error); }
    }

    /// Fades out (up toward the rope when hung, back to the edge otherwise), then frees the card.
    internal void Leave(bool hanging)
    {
        if (leaving || card is null) return;
        leaving = true; timer.Stop();
        fade.Target = 0;
        if (hanging) lift.Target = Math.Min(lift.Value,-Math.Max(0,cornerTop - work.Top) * .35);
        else slide.Target = CardWidth;
        if (Reduced) { fade.Snap(); slide.Snap(); lift.Snap(); }
        Apply(); Wake();
        if (!listening) Finish();
    }

    private void Finish()
    {
        StopFrames();
        if (card is { } gone) { card = null; done(gone.Id); }
        Hide(); slide.Target = 0; slide.Snap(); lift.Target = 0; lift.Snap(); leaving = false;
    }

    private void Apply() { shift.X = slide.Value; plate.Opacity = Math.Clamp(fade.Value,0,1); }

    private void Wake()
    {
        if (listening || (slide.Settled && fade.Settled && lift.Settled)) return;
        listening = true; last = 0; CompositionTarget.Rendering += Frame;
    }

    private void Frame(object? sender,EventArgs args)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = last == 0 ? 0 : Math.Min(.05,(now - last) / (double)Stopwatch.Frequency); last = now;
        if (Reduced) { slide.Snap(); fade.Snap(); if (!pressing) lift.Snap(); }
        slide.Advance(dt); fade.Advance(dt); if (!pressing) lift.Advance(dt);
        bool still = slide.SettleWithin(.5,5) & fade.SettleWithin(.01,.05) & (pressing || lift.SettleWithin(.5,5));
        Apply(); Place();
        if (!still) return;
        StopFrames();
        if (leaving) Finish();
    }

    private void StopFrames() { if (!listening) return; CompositionTarget.Rendering -= Frame; listening = false; }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RopeWindow.NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",ExactSpelling = true,CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("shcore.dll",ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDpiForMonitor(nint monitor,int type,out uint dpiX,out uint dpiY);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
    [DllImport("user32.dll",ExactSpelling = true,SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
}
