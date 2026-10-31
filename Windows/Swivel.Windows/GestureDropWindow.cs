using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

// A native invisible OLE probe hands off to the no-activate WPF action targets.
internal sealed class GestureDropWindow : Window, IGestureDropTarget
{
    private readonly Func<bool> available;
    private readonly App app;
    private readonly Func<bool>? primaryDown;
    private readonly Func<long>? elapsed;
    private readonly Func<bool>? oleEvidence;
    private WheelGestureRequest? request;
    private bool probing;
    private readonly Action<ToolAction, string[]> receive;
    private readonly WheelGestureFlow flow = new();
    private GestureDragProbe? probe;
    internal GestureDragProbe? Probe => probe;
    private readonly MenuMotion motion;
    // This HWND uses only WPF drag events; the separate native probe owns its IDropTarget.
    private readonly Grid root = new() { Background = Brushes.Transparent, AllowDrop = true };
    private readonly DispatcherTimer timer = new(DispatcherPriority.Input);
    private Canvas? canvas;
    private Button? highlighted;
    private Brush? previousBackground, previousForeground;
    private ToolAction[] actions = Array.Empty<ToolAction>(), slice = Array.Empty<ToolAction>();
    private string[]? files;
    private MenuTemplate template;
    private bool primaryIsRight;
    private readonly Action<Exception> report;
    private HwndSource? source;
    private Exception? cleanupFailure;
    private long shownAt;
    private bool accepted, closed, positioning, closeRequested, closeQueued;
    private int oleDepth;
    private long dragRevision;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pointerX, pointerY;
    private event Action? targetClosed;
    event Action? IGestureDropTarget.Closed { add => targetClosed += value; remove => targetClosed -= value; }
    public Task Completion => completion.Task;
    void IGestureDropTarget.Close() => RequestClose();

    internal GestureDropWindow(App app, Func<bool> available, Action<ToolAction, string[]> receive, Action<Exception> report, Func<bool>? primaryDown = null, Func<long>? elapsed = null, Func<bool>? oleEvidence = null)
    {
        this.oleEvidence = oleEvidence; this.elapsed = elapsed; this.primaryDown = primaryDown; this.app = app; this.available = available; this.receive = receive; this.report = report;
        motion = new MenuMotion(() => app.Prefs.AnimateMenus);
        Title = app.L("Wheel Drag Gesture"); Width = 24; Height = 24;
        WindowStartupLocation = WindowStartupLocation.Manual; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; Focusable = false;
        Background = Brushes.Transparent; Content = root;
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.None);
        // Preview handlers own the whole OLE operation; button helpers must not validate or run inside COM.
        root.PreviewDragEnter += OnDragEnter; root.PreviewDragOver += OnDragOver; root.PreviewDrop += OnDrop;
        root.PreviewDragLeave += OnDragLeave;
        timer.Interval = TimeSpan.FromMilliseconds(GestureOleEvidence.PollMilliseconds); timer.Tick += (_, _) => {
            try { PollDrag(); } catch (Exception error) { DeferFailure(error); }
        };
        SourceInitialized += (_, _) => ConfigureNative();
        DpiChanged += (_, _) => {
            if (closed || positioning) return;
            try { Place(); } catch (Exception error) { DeferFailure(error); }
        };
        Closing += (_, e) => { closeRequested = true; accepted = true; if (oleDepth > 0 || probe is not null) { e.Cancel = true; QueueClose(); } };
        Closed += (_, _) => {
            closed = true; timer.Stop(); motion.Dispose(); Exception? failure = cleanupFailure;
            try { source?.RemoveHook(Hook); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            source = null;
            try { targetClosed?.Invoke(); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        };
    }
    private bool Current => !closed && !closeRequested && !accepted && available();
    internal bool CanReceive => Current && (IsVisible || probing) &&
        flow.Phase is WheelGesturePhase.HiddenTarget or WheelGesturePhase.Wheel;
    private long Elapsed => elapsed?.Invoke() ?? (long)Stopwatch.GetElapsedTime(shownAt).TotalMilliseconds;
    private bool PrimaryDown => primaryDown?.Invoke() ?? ((GetAsyncKeyState(primaryIsRight ? 2 : 1) & 0x8000) != 0);
    private bool CheckEvidence() => flow.ObserveEvidence(Elapsed, PrimaryDown, oleEvidence?.Invoke() ?? GestureOleEvidence.IsActive(request!.PointerThreadId));
    public void ShowAt(WheelGestureRequest request)
    {
        if (closed || flow.Phase != WheelGesturePhase.Armed) throw new InvalidOperationException("Create a new drop target before showing it again.");
        this.request = request;
        pointerX = request.PhysicalX; pointerY = request.PhysicalY; primaryIsRight = request.PrimaryIsRight;
        shownAt = Stopwatch.GetTimestamp(); flow.Begin(0);
        if (!available()) { RequestClose(); return; }
        // No HWND is created while waiting for OLE's own capture window.
        timer.Start(); PollDrag();
    }
    private void PollDrag()
    {
        if (!Current || !CheckEvidence()) { RequestClose(); return; }
        if (flow.Phase != WheelGesturePhase.HiddenTarget || probe is not null) return;
        probing = true;
        probe = new GestureDragProbe(request!, () => CanReceive && CheckEvidence(),
            snapshot => { _ = Dispatcher.BeginInvoke(new Action(() => _ = ConfirmProbe(snapshot))); }, RequestClose, DeferFailure);
    }
    private void ConfigureNative()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("The drop target window could not be initialized.");
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x08000000L | 0x00000080L)); // NOACTIVATE | TOOLWINDOW
        if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        source.AddHook(Hook);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // This also prevents the optional Windows hover-to-activate setting from
        // activating the target. MA_NOACTIVATE preserves the original message.
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }
    private void Place(bool moveToMonitor = false)
    {
        if (closed || positioning) return;
        positioning = true;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle; if (hwnd == IntPtr.Zero) return;
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pointerX, pointerY)).WorkingArea;
            // Move while hidden first so GetDpiForWindow uses the target monitor.
            // All SetWindowPos coordinates remain physical pixels, including
            // monitors to the left/above the primary monitor.
            if (moveToMonitor && !SetWindowPos(hwnd, new IntPtr(-1), Math.Clamp(pointerX, area.Left, area.Right - 1), Math.Clamp(pointerY, area.Top, area.Bottom - 1),
                0, 0, 0x0001 | 0x0010 | 0x0200)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            uint dpi = GetDpiForWindow(hwnd); if (dpi == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            int width = Math.Min(Math.Max(1, area.Width), (int)Math.Ceiling((flow.Phase == WheelGesturePhase.Wheel ? 644d : 24d) * dpi / 96));
            int height = Math.Min(Math.Max(1, area.Height), (int)Math.Ceiling((flow.Phase == WheelGesturePhase.Wheel ? 510d : 24d) * dpi / 96));
            int x = (int)Math.Clamp((long)pointerX - width / 2, area.Left, (long)area.Right - width);
            int y = (int)Math.Clamp((long)pointerY - height / 2, area.Top, (long)area.Bottom - height);
            if (!SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0200)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { positioning = false; }
    }
    private async Task ConfirmProbe(string[] snapshot)
    {
        try
        {
            if (!CanReceive || !CheckEvidence()) { RequestClose(); return; }
            var old = probe;
            if (old is null) return;
            old.Close(); await old.Completion;
            if (!CanReceive || !CheckEvidence() || !flow.ConfirmFiles(Elapsed, true)) { RequestClose(); return; }
            probe = null; files = snapshot; template = app.Prefs.MenuTemplate;
            actions = Catalog.Available(app.Prefs, snapshot).ToArray();
            _ = new WindowInteropHelper(this).EnsureHandle();
            DrawPage(); SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
            Place(true); Show(); probing = false; Place(); root.UpdateLayout();
        }
        catch (Exception error) { DeferFailure(error); }
    }
    private void OnDragEnter(object sender, DragEventArgs e) => OnDragOver(sender, e);
    private void DrawPage()
    {
        Highlight(-1); motion.Reset(); root.Children.Clear();
        slice = actions.Skip(flow.Page * 8).Take(8).ToArray();
        canvas = MenuPresentation.Build(app, slice, _ => { }, (_, _) => { }, RequestClose, motion, gesture: true, templateOverride: template);
        var layout = new Grid { Width = 644, Height = 510 };
        var view = new Viewbox { Child = canvas, Width = 620, Height = 450, VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(view);
        var pages = new TextBlock { Text = app.L("Previous") + "    " + (flow.Page + 1) + "/" + Math.Max(1, (actions.Length + 7) / 8) + "    " + app.L("Next"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14) };
        pages.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey); layout.Children.Add(pages);
        root.Children.Add(new Viewbox { Child = layout, Stretch = Stretch.Uniform });
        root.UpdateLayout();
    }
    internal int HitItem(Point windowPoint)
    {
        if (canvas is null || flow.Phase != WheelGesturePhase.Wheel) return -1;
        Point local = root.TranslatePoint(windowPoint, canvas);
        return MenuGeometry.HitTest(template, slice.Length, local.X, local.Y);
    }
    private void Highlight(int index)
    {
        Button? next = index >= 0 && canvas is not null ? canvas.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, slice[index])) : null;
        if (ReferenceEquals(next, highlighted)) return;
        if (highlighted is not null) { highlighted.Background = previousBackground; highlighted.Foreground = previousForeground; }
        highlighted = next;
        if (next is not null)
        {
            previousBackground = next.Background; previousForeground = next.Foreground;
            next.SetResourceReference(Control.BackgroundProperty, SystemColors.HighlightBrushKey);
            next.SetResourceReference(Control.ForegroundProperty, SystemColors.HighlightTextBrushKey);
        }
        motion.Hover(next);
    }
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        oleDepth++;
        try
        {
            e.Handled = true; Highlight(-1); flow.HoverPage(Elapsed, 0, Math.Max(1, (actions.Length + 7) / 8));
            long revision = ++dragRevision;
            // Moving between child controls raises Leave followed by Enter in the same
            // OLE callback. An unmatched Leave means the drag left or was cancelled.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                if (CanReceive && dragRevision == revision) RequestClose();
            }));
        }
        finally { LeaveOle(); }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        dragRevision++; oleDepth++;
        try
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (!CanReceive || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop, false) || !CanReceive) return;
            if (flow.Phase != WheelGesturePhase.Wheel) return;
            Point point = e.GetPosition(root); int hit = HitItem(point); Highlight(hit);
            e.Effects = hit >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
            if (canvas is null) return;
            Point local = root.TranslatePoint(point, canvas);
            int direction = local.Y is >= 450 and <= 510 ? local.X < 310 ? -1 : 1 : 0;
            if (flow.HoverPage(Elapsed, direction, Math.Max(1, (actions.Length + 7) / 8))) DrawPage();
        }
        catch (Exception error) { DeferFailure(error); }
        finally { LeaveOle(); }
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        oleDepth++;
        try
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (!CanReceive || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop, false) || !CanReceive) return;
            if (e.Data.GetData(DataFormats.FileDrop, false) is not string[] paths || !CanReceive) return;
            var snapshot = GestureDropRules.Snapshot(paths);
            int hit = HitItem(e.GetPosition(root));
            if (!CanReceive || files is null || !snapshot.SequenceEqual(files, StringComparer.Ordinal) || !flow.TryDrop(Elapsed, hit)) { RequestClose(); return; }
            accepted = true; e.Effects = DragDropEffects.Copy; receive(slice[hit], snapshot); RequestClose();
        }
        catch (Exception error) { DeferFailure(error); }
        finally { LeaveOle(); }
    }
    private void DeferFailure(Exception error)
    {
        accepted = true;
        _ = Dispatcher.BeginInvoke(new Action(() => { if (closed) return; report(error); if (!closed) RequestClose(); }));
    }
    private void RequestClose()
    {
        closeRequested = true; accepted = true; flow.Cancel(); timer.Stop();
        probe?.Close();
        if (oleDepth == 0) QueueClose();
    }
    private void LeaveOle()
    {
        oleDepth--;
        if (oleDepth == 0 && closeRequested) QueueClose();
    }
    private void QueueClose()
    {
        if (closed || closeQueued) return;
        closeQueued = true;
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(async () => {
                if (oleDepth != 0) { closeQueued = false; return; }
                try
                {
                    var old = probe;
                    if (old is not null)
                    {
                        try { old.Close(); await old.Completion; }
                        catch (Exception error) { cleanupFailure = error; }
                        finally { if (ReferenceEquals(probe, old)) probe = null; }
                    }
                    closeQueued = false;
                    if (!closed && oleDepth == 0) Close();
                }
                catch (Exception error) { closeQueued = false; completion.TrySetException(error); }
            }));
        }
        catch (Exception error) { closeQueued = false; completion.TrySetException(error); }
    }
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
