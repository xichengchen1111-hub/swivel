using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class WheelGestureWindowNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) yield return target;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var item in Find<T>(child)) yield return item;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Gesture UI fixture did not settle."); Pump(); Thread.Sleep(1); }
        Pump();
    }
    private static Button Button(WheelGestureWindow window, App app, string key) => Find<Button>(window).Single(button => Equals(button.Content, app.L(key)));
    private static void Click(WheelGestureWindow window, App app, string key) => Button(window, app, key).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static void Refresh(WheelGestureWindow window) => typeof(WheelGestureWindow).GetMethod("RefreshSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Finish(WheelGestureWindow window) { Task done = window.FinishAsync(); Until(() => done.IsCompleted); done.GetAwaiter().GetResult(); window.Close(); Pump(); }

    // Clicks raised directly from the test thread resume awaits on the dispatcher, as a real click does.
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { RunCore(check, reject); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<bool, string> check, Action<Action, string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        // Fakes test all monitor lifecycle paths without installing a real hook,
        // reading any path, opening a Wheel or touching the clipboard.
        var session = new Session(); Target? target = null; int starts = 0, opens = 0; bool enabled = true, busy = false;
        string[]? received = null; CancellationToken openToken = default; Func<bool>? stillCurrent = null;
        var openDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new WheelGestureUiServices((_, _, _) => { starts++; return Task.FromResult<IWheelGestureSession>(session); }, _ => new IntPtr(42),
            (paths, token, current) => { opens++; received = paths; openToken = token; stillCurrent = current; return openDone.Task; },
            (current, receive, report) => target = new Target(current, receive, report), () => enabled, () => busy);
        var window = new WheelGestureWindow(app, services);
        check(starts == 0 && Button(window, app, "Start Gesture Monitoring").IsEnabled && !Button(window, app, "Stop Gesture Monitoring").IsEnabled,
            "Opening the gesture window leaves input monitoring stopped");
        check(!Find<Button>(window).Any(button => button.IsDefault) && Button(window, app, "Close").IsCancel,
            "Starting input monitoring is never an implicit Enter default");
        check(Find<ComboBox>(window).Single().Items.Count == 3, "Only Ctrl, Shift and Alt are offered for this session");
        var root = (DockPanel)window.Content;
        check(DockPanel.GetDock(root.Children[0]) == Dock.Bottom && root.Children[1] is ScrollViewer, "Gesture controls remain outside the scrolling scope and diagnostics");
        Click(window, app, "Start Gesture Monitoring");
        check(starts == 1 && !Find<ComboBox>(window).Single().IsEnabled && Button(window, app, "Stop Gesture Monitoring").IsEnabled,
            "Explicit Start owns one listener and fixes its selected modifier");
        session.Offer(); Refresh(window); var firstTarget = target!;
        check(firstTarget.Shows == 1 && firstTarget.Available() && opens == 0, "A gesture only shows a drop target and does not open or execute a file action");
        session.Offer(); Refresh(window); check(ReferenceEquals(target, firstTarget), "Further gesture signals cannot create a second target");
        string[] paths = { @"C:\Fixture\One.png", @"C:\Fixture\two.jpg" }; firstTarget.Drop(paths); paths[0] = @"C:\Changed.png";
        check(opens == 0 && firstTarget.Closes == 0, "The Drop callback neither checks files nor destroys its registered drop window");
        Pump(); check(opens == 1 && firstTarget.Closes == 1 && received![0] == @"C:\Fixture\One.png" && stillCurrent!(),
            "Deferred acceptance closes the target and delivers an independent case-preserving reference snapshot");
        Click(window, app, "Stop Gesture Monitoring"); check(openToken.IsCancellationRequested && !stillCurrent!(), "Stop immediately invalidates and cancels pending file checks");
        check(!Button(window, app, "Start Gesture Monitoring").IsEnabled, "Stop does not permit a new session until pending acceptance joins");
        openDone.SetCanceled(); Until(() => Button(window, app, "Start Gesture Monitoring").IsEnabled);
        check(session.Stops == 1, "Stop joins its listener before returning to the stopped state"); Finish(window);

        // A queued but not-yet-started dispatcher acceptance must also be joined.
        var queuedSession = new Session(); Target? queuedTarget = null; int queuedOpens = 0;
        var queuedWindow = new WheelGestureWindow(app, services with { Start = (_, _, _) => Task.FromResult<IWheelGestureSession>(queuedSession),
            CreateTarget = (current, receive, report) => queuedTarget = new Target(current, receive, report),
            OpenFiles = (_, _, _) => { queuedOpens++; return Task.CompletedTask; } });
        Click(queuedWindow, app, "Start Gesture Monitoring"); queuedSession.Offer(); Refresh(queuedWindow); queuedTarget!.Drop(new[] { @"C:\Fixture\one.png" });
        Task queuedFinish = queuedWindow.FinishAsync(); check(!queuedFinish.IsCompleted, "Finish waits even when acceptance is still queued on the dispatcher");
        Until(() => queuedFinish.IsCompleted); check(queuedOpens == 0, "Closing before the queued callback prevents any file check or late Wheel opening"); queuedWindow.Close(); Pump();

        var lateReady = new TaskCompletionSource<IWheelGestureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new Session { AutoComplete = false };
        var lateWindow = new WheelGestureWindow(app, services with { Start = (_, _, _) => lateReady.Task });
        Click(lateWindow, app, "Start Gesture Monitoring"); Task lateFinish = lateWindow.FinishAsync();
        check(!lateFinish.IsCompleted, "Unshown Finish waits for a pending listener startup"); lateReady.SetResult(late); Until(() => late.Stops == 1);
        check(!lateFinish.IsCompleted, "A listener returned after cancellation must finish its cleanup before the window finishes");
        late.Complete(); Until(() => lateFinish.IsCompleted); lateWindow.Close(); Pump();

        var live = new Session(); Target? liveTarget = null; int liveOpens = 0;
        var liveWindow = new WheelGestureWindow(app, services with { Start = (_, _, _) => Task.FromResult<IWheelGestureSession>(live),
            CreateTarget = (current, receive, report) => liveTarget = new Target(current, receive, report),
            OpenFiles = (_, _, _) => { liveOpens++; return Task.CompletedTask; } });
        Click(liveWindow, app, "Start Gesture Monitoring"); live.Offer(); Refresh(liveWindow);
        var stale = liveTarget!; busy = true; typeof(App).GetMethod("NotifyWorkStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
        check(stale.Closes == 1 && !stale.Available(), "Other app work immediately hides and invalidates the target");
        stale.Drop(new[] { @"C:\Fixture\stale.png" }); Pump(); check(liveOpens == 0, "A late drop from a hidden target cannot open Wheel");
        busy = false; live.Offer(); Refresh(liveWindow); enabled = false;
        liveTarget!.Drop(new[] { @"C:\Fixture\disabled.png" }); Pump(); check(liveOpens == 0, "Feature-off blocks a drop even before the window's close notification"); enabled = true;
        Finish(liveWindow);

        var idle = new WheelGestureWindow(app, services); bool closed = false; idle.Closed += (_, _) => closed = true; idle.Show(); idle.Close(); Until(() => closed);
        check(idle.IsFinishing, "Synchronous idle cleanup defers final Close until WPF Closing returns");
        var fault = new Session { AutoComplete = false };
        var failureWindow = new WheelGestureWindow(app, services with { Start = (_, _, _) => Task.FromResult<IWheelGestureSession>(fault) });
        Click(failureWindow, app, "Start Gesture Monitoring"); fault.Fail(); Refresh(failureWindow);
        check(Find<TextBox>(failureWindow).Single().Text.Contains("[U+202E]", StringComparison.Ordinal), "Native gesture diagnostic control characters are visibly escaped");
        Task failureFinish = failureWindow.FinishAsync(); check(!failureFinish.IsCompleted, "Faulted listener cleanup is still joined on Finish");
        fault.Complete(); Until(() => failureFinish.IsCompleted); failureWindow.Close(); Pump();

        // The real popup creates only its own window, never a real drag or hook.
        // Its native checks run only on Windows, not in cross-compilation.
        IntPtr foreground = GetForegroundWindow();
        var drop = new GestureDropWindow(app, () => true, _ => { }, _ => { });
        check(!drop.ShowActivated && !drop.ShowInTaskbar && !drop.Focusable && drop.WindowStyle == WindowStyle.None,
            "The native gesture target requests no activation, taskbar item or keyboard focus");
        drop.ShowAt(new WheelGestureRequest(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 100, 100, false));
        long style = GetWindowLongPtr(new WindowInteropHelper(drop).Handle, -20).ToInt64();
        check((style & 0x08000000L) != 0 && (style & 0x80L) != 0, "The drop HWND has NOACTIVATE and TOOLWINDOW styles");
        check(GetForegroundWindow() == foreground, "Showing and placing the drop target does not change the foreground window");
        object[] hookArguments = { IntPtr.Zero, 0x0021, IntPtr.Zero, IntPtr.Zero, false };
        object? hookResult = typeof(GestureDropWindow).GetMethod("Hook", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(drop, hookArguments);
        check(Equals(hookResult, new IntPtr(3)) && Equals(hookArguments[4], true), "Mouse and hover activation return MA_NOACTIVATE without consuming mouse input");
        drop.Close(); Pump(); check(!drop.CanReceive, "A closed target can never receive a late drop");
        foreach (bool formatProbe in new[] { true, false })
        {
            var reentrantSession = new Session(); GestureDropWindow? actualTarget = null; int fileCalls = 0;
            var control = new WheelGestureWindow(app, services with { Start = (_, _, _) => Task.FromResult<IWheelGestureSession>(reentrantSession),
                CreateTarget = (current, receive, error) => actualTarget = new GestureDropWindow(app, current, receive, error),
                OpenFiles = (_, _, _) => { fileCalls++; return Task.CompletedTask; } });
            Click(control, app, "Start Gesture Monitoring"); reentrantSession.Offer(); Refresh(control);
            Task? joined = null;
            void Reenter()
            {
                if (formatProbe)
                {
                    Click(control, app, "Stop Gesture Monitoring");
                    joined = (Task)typeof(WheelGestureWindow).GetField("pendingStop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
                }
                else joined = control.FinishAsync();
                Pump();
                check(!actualTarget!.Completion.IsCompleted && !joined.IsCompleted && !actualTarget.CanReceive,
                    "Provider reentry invalidates immediately but cannot destroy or join the target inside an OLE callback, even with a nested dispatcher pump");
            }
            var data = new Provider(formatProbe ? Reenter : () => { }, formatProbe ? () => { } : Reenter);
            var args = (DragEventArgs)typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point) }, null)!
                .Invoke(new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, actualTarget!, new Point(1, 1) });
            typeof(GestureDropWindow).GetMethod(formatProbe ? "OnDragOver" : "OnDrop", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(object), typeof(DragEventArgs) })!
                .Invoke(actualTarget, new object[] { actualTarget!, args });
            check(args.Effects == DragDropEffects.None && fileCalls == 0 && !actualTarget!.Completion.IsCompleted,
                "A cancelled provider callback returns None without opening Wheel or synchronously closing its HWND");
            Until(() => joined!.IsCompleted);
            check(actualTarget!.Completion.IsCompleted && fileCalls == 0 && data.ConversionRequests == 0,
                "Stop or Finish completes only after the outermost OLE callback exits and the target closes, without automatic format conversion");
            Finish(control);
        }
        reject(() => GestureDropRules.Snapshot(new[] { @"C:\Fixture\a.png", @"C:\Fixture\A.png" }), "Case-only paths are explicitly rejected, never silently collapsed");
    }
    private sealed class Target(Func<bool> current, Action<string[]> receive, Action<Exception> report) : IGestureDropTarget
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Shows, Closes;
        public Task Completion => done.Task;
        public event Action? Closed;
        public bool Available() => current();
        public void ShowAt(WheelGestureRequest request) => Shows++;
        public void Close() { Closes++; Closed?.Invoke(); done.TrySetResult(); }
        public void Drop(string[] paths) { if (current()) receive(GestureDropRules.Snapshot(paths)); }
        public void Error(Exception error) => report(error);
    }
    private sealed class Session : IWheelGestureSession
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WheelGestureSnapshot snapshot = new(Guid.NewGuid(), 0, WheelGestureStatus.Running, Array.Empty<WheelGestureIssue>(), 0);
        private WheelGestureRequest? request;
        public bool AutoComplete = true;
        public int Stops;
        public WheelGestureSnapshot Snapshot => snapshot;
        public Task Completion => done.Task;
        public void Offer() => request = new(snapshot.SessionId, 1, Stopwatch.GetTimestamp(), 100, 100, false);
        public bool TryTakeRequest(out WheelGestureRequest? result) { result = request; request = null; return result is not null; }
        public Task StopAsync() { Stops++; snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = WheelGestureStatus.Stopping }; if (AutoComplete) Complete(); return done.Task; }
        public void Complete() { snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = WheelGestureStatus.Stopped }; done.TrySetResult(); }
        public void Fail() => snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = WheelGestureStatus.Faulted, Issues = new[] { new WheelGestureIssue("fixture\u202E", 5) } };
    }
    private sealed class Provider(Action beforePresent, Action beforeRead) : IDataObject
    {
        public int ConversionRequests;
        public object GetData(string format, bool autoConvert) { if (autoConvert) ConversionRequests++; if (format != DataFormats.FileDrop) throw new InvalidOperationException("Unexpected format request."); beforeRead(); return new[] { @"C:\Fixture\one.png" }; }
        public bool GetDataPresent(string format, bool autoConvert) { if (autoConvert) ConversionRequests++; if (format != DataFormats.FileDrop) throw new InvalidOperationException("Unexpected format request."); beforePresent(); return true; }
        public object GetData(string format) => throw new NotSupportedException();
        public object GetData(Type format) => throw new NotSupportedException();
        public bool GetDataPresent(string format) => throw new NotSupportedException();
        public bool GetDataPresent(Type format) => throw new NotSupportedException();
        public string[] GetFormats() => throw new NotSupportedException();
        public string[] GetFormats(bool autoConvert) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    }
    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
