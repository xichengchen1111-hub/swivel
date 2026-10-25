using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

// A single reference drop target, never a menu which executes an action. The
// normal Wheel is opened separately, after the OLE callback has returned.
internal sealed class GestureDropWindow : Window, IGestureDropTarget
{
    private readonly Func<bool> available;
    private readonly Action<string[]> receive;
    private readonly Action<Exception> report;
    private HwndSource? source;
    private long shownAt;
    private bool accepted, closed, positioning, closeRequested, closeQueued;
    private int oleDepth;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pointerX, pointerY;
    private event Action? targetClosed;
    event Action? IGestureDropTarget.Closed { add => targetClosed += value; remove => targetClosed -= value; }
    public Task Completion => completion.Task;
    void IGestureDropTarget.Close() => RequestClose();

    internal GestureDropWindow(App app, Func<bool> available, Action<string[]> receive, Action<Exception> report)
    {
        this.available = available; this.receive = receive; this.report = report;
        Title = app.L("Drop Files to Open Wheel"); Width = 340; Height = 145;
        WindowStartupLocation = WindowStartupLocation.Manual; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; Focusable = false;
        SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);
        var body = new StackPanel(); body.Children.Add(UI.Text(app.L("Drop Files to Open Wheel"), 17));
        body.Children.Add(UI.Text(app.L("Release files here, then choose an action in Wheel. No file action runs automatically."), 12));
        var border = new Border {
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = body, AllowDrop = true };
        border.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        border.SetResourceReference(Border.BorderBrushProperty, SystemColors.HighlightBrushKey);
        foreach (var text in body.Children) if (text is TextBlock block) block.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);
        KeyboardNavigation.SetTabNavigation(border, KeyboardNavigationMode.None);
        System.Windows.Automation.AutomationProperties.SetName(border, app.L("Drop Files to Open Wheel")); Content = border;
        border.DragOver += OnDragOver; border.Drop += OnDrop;
        SourceInitialized += (_, _) => ConfigureNative();
        DpiChanged += (_, _) => {
            if (closed || positioning) return;
            try { Place(); } catch (Exception error) { DeferFailure(error); }
        };
        Closing += (_, e) => { closeRequested = true; accepted = true; if (oleDepth > 0) e.Cancel = true; };
        Closed += (_, _) => {
            closed = true; Exception? failure = null;
            try { source?.RemoveHook(Hook); } catch (Exception error) { failure = error; }
            source = null;
            try { targetClosed?.Invoke(); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        };
    }
    internal bool CanReceive => !closed && !closeRequested && !accepted && IsVisible && available() &&
        Stopwatch.GetElapsedTime(shownAt) <= TimeSpan.FromSeconds(5);
    public void ShowAt(WheelGestureRequest request)
    {
        if (closed || IsVisible) throw new InvalidOperationException("Create a new drop target before showing it again.");
        pointerX = request.PhysicalX; pointerY = request.PhysicalY;
        _ = new WindowInteropHelper(this).EnsureHandle(); Place(true);
        if (!available()) { RequestClose(); return; }
        shownAt = Stopwatch.GetTimestamp(); Show(); Place();
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
            int width = Math.Min(Math.Max(1, area.Width), (int)Math.Ceiling(340d * dpi / 96));
            int height = Math.Min(Math.Max(1, area.Height), (int)Math.Ceiling(145d * dpi / 96));
            int x = (int)Math.Clamp((long)pointerX + 18, area.Left, (long)area.Right - width);
            int y = (int)Math.Clamp((long)pointerY + 18, area.Top, (long)area.Bottom - height);
            if (!SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0200)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { positioning = false; }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        oleDepth++;
        try
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (!CanReceive || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
            // Never request streams, virtual files, text or automatic conversion.
            try { if (e.Data.GetDataPresent(DataFormats.FileDrop, false) && CanReceive) e.Effects = DragDropEffects.Copy; }
            catch { /* A provider's format probe can fail. Do not query more data. */ }
        }
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
            if (!CanReceive) return;
            accepted = true; e.Effects = DragDropEffects.Copy; receive(snapshot);
        }
        catch (Exception error)
        {
            accepted = true; e.Effects = DragDropEffects.None;
            // Keep destruction and diagnostic UI outside the OLE callback too.
            DeferFailure(error);
        }
        finally { LeaveOle(); }
    }
    private void DeferFailure(Exception error)
    {
        accepted = true;
        _ = Dispatcher.BeginInvoke(new Action(() => { if (closed) return; report(error); if (!closed) RequestClose(); }));
    }
    private void RequestClose()
    {
        closeRequested = true; accepted = true;
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
            _ = Dispatcher.BeginInvoke(new Action(() => {
                closeQueued = false;
                // A COM provider can pump messages and reenter drag callbacks.
                // Never destroy its target until the outermost callback exits.
                if (!closed && oleDepth == 0)
                {
                    try { Close(); } catch (Exception error) { completion.TrySetException(error); }
                }
            }));
        }
        catch (Exception error) { closeQueued = false; completion.TrySetException(error); }
    }
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
