using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class WheelGestureNotices
{
    internal const string Scope = "Start dragging files, then tap Ctrl, Shift or Alt. Wheel appears only after Windows confirms a file drag. Release on an action to run it; the hub or outside cancels. Hover Previous or Next to turn pages.";
    internal const string Privacy = "While listening, Swivel checks the selected modifier and primary mouse-button state. No typing is recorded, input is never blocked, and the clipboard is never read. Only the file list supplied by an OLE drag is read before a drop.";
    internal const string Drop = "The invisible file-drag check lasts less than half a second. A confirmed Wheel takes no focus. Drop 1–100 local files directly on an action; file checks run after the drop. Linked, cloud, virtual and duplicate paths are refused. No file action runs without dropping on an item.";
    internal const string Limits = "Windows can silently remove an input hook. A running status is not a health guarantee; a persistent modifier-state mismatch stops listening and reports a problem. Use Restart Gesture Monitoring if it stops responding. Elevated apps and remote sessions may limit drag-and-drop. Stop and Quit wait for cleanup. Drags from apps that do not use system drag-and-drop are not recognised.";

}

internal interface IGestureDropTarget
{
    event Action? Closed;
    Task Completion { get; }
    void ShowAt(WheelGestureRequest request);
    void Close();
}

internal sealed record WheelGestureUiServices(
    Func<WheelGestureModifier, IntPtr, CancellationToken, Task<IWheelGestureSession>> Start,
    Func<Window, IntPtr> NotificationHandle,
    Func<ToolAction, string[], CancellationToken, Func<bool>, Task> OpenFiles,
    Func<Func<bool>, Action<ToolAction, string[]>, Action<Exception>, IGestureDropTarget> CreateTarget,
    Func<bool> Enabled,
    Func<bool> Busy);

internal sealed class WheelGestureWindow : Window
{
    private readonly App app;
    private readonly WheelGestureUiServices services;
    private readonly ComboBox modifier = new() { MinWidth = 180, Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock status = UI.Text("");
    private readonly TextBox problems = new() { IsReadOnly = true, Height = 135, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 12) };
    private readonly Button start, stop;
    private readonly DispatcherTimer healthTimer = new(DispatcherPriority.Background);
    private bool backgroundListening;
    private readonly DispatcherTimer popupTimer = new(DispatcherPriority.Background);
    private IWheelGestureSession? session;
    private HwndSource? notificationSource;
    private IGestureDropTarget? popup;
    private CancellationTokenSource? runCancellation, acceptCancellation;
    private Task pendingStart = Task.CompletedTask, pendingStop = Task.CompletedTask, pendingAccept = Task.CompletedTask;
    private Task pendingPopupClose = Task.CompletedTask;
    private Task? finish;
    private long interaction, lastSequence;
    private bool starting, stopping, accepting, finishing, closed, finished, closeRequested, allowClose;
    private readonly List<string> localProblems = new();
    private int omittedProblems;
    private string lastResult = "";
    internal bool IsFinishing => finishing;

    internal WheelGestureWindow(App app) : this(app, new(
        WheelGestureSession.StartAsync, window => new WindowInteropHelper(window).EnsureHandle(),
        app.RunGestureActionAsync, (valid, receive, error) => new GestureDropWindow(app, valid, receive, error),
        () => app.Prefs.IsOn(Feature.Wheel), () => app.Busy)) { }

    internal WheelGestureWindow(App app, WheelGestureUiServices services)
    {
        this.app = app; this.services = services;
        Title = app.L("Wheel Drag Gesture"); Width = 720; Height = 650; MinWidth = 450; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(18) }; var footer = new StackPanel(); footer.Children.Add(status);
        var buttons = new WrapPanel(); start = UI.Button(app.L("Start Gesture Monitoring"), StartRequested);
        stop = UI.Button(app.L("Stop Gesture Monitoring"), StopRequested);
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true;
        var restart = UI.Button(app.L("Restart Gesture Monitoring"), async () => {
            StopRequested(); await pendingStop; if (CanStart) { StartRequested(); await pendingStart; }
        });
        buttons.Children.Add(restart);
        buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(close); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        foreach (string note in new[] { WheelGestureNotices.Scope, WheelGestureNotices.Privacy, WheelGestureNotices.Drop, WheelGestureNotices.Limits })
            body.Children.Add(UI.Text(app.L(note), 12));
        var listen = new CheckBox { Content = app.L("Listen for the gesture whenever Wheel is on"), IsChecked = app.Prefs.ListenForWheelGesture, Margin = new Thickness(0, 8, 0, 12) };
        listen.Click += (_, _) => app.Prefs.SetListenForWheelGesture(listen.IsChecked == true); body.Children.Add(listen);
        body.Children.Add(UI.Text(app.L("Gesture Modifier")));
        foreach (var value in Enum.GetValues<WheelGestureModifier>()) modifier.Items.Add(new ModifierChoice(value, value == WheelGestureModifier.Control ? "Ctrl" : value.ToString()));
        modifier.SelectedIndex = 0; body.Children.Add(modifier);
        System.Windows.Automation.AutomationProperties.SetName(modifier, app.L("Gesture Modifier"));
        body.Children.Add(UI.Text(app.L("Gesture Monitoring Problems")));
        System.Windows.Automation.AutomationProperties.SetName(problems, app.L("Gesture Monitoring Problems")); body.Children.Add(problems);
        root.Children.Add(UI.Scroll(body)); Content = root;
        healthTimer.Interval = TimeSpan.FromMilliseconds(250);
        healthTimer.Tick += (_, _) => { session?.CheckHealth(); RefreshSession(); };
        popupTimer.Interval = TimeSpan.FromSeconds(31); popupTimer.Tick += (_, _) => InvalidateInteraction();
        SourceInitialized += (_, _) => {
            notificationSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            notificationSource?.AddHook(NotificationHook); WebRouteLayout.Fit(this, this);
        };
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        app.WorkStateChanged += WorkStateChanged;
        Activated += (_, _) => RefreshSession();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; app.WorkStateChanged -= WorkStateChanged; if (!finishing) _ = FinishAsync(); };
        ShowState();
    }
    private sealed record ModifierChoice(WheelGestureModifier Value, string Label) { public override string ToString() => Label; }
    private bool CanStart => !finishing && !closed && !starting && !stopping && !accepting && services.Enabled() &&
        (session is null || session.Snapshot.Status is WheelGestureStatus.Stopped or WheelGestureStatus.Faulted);
    private bool Current(long version, IWheelGestureSession expected) => !finishing && !closed && !stopping && interaction == version &&
        ReferenceEquals(session, expected) && expected.Snapshot.Status == WheelGestureStatus.Running && services.Enabled() && !services.Busy();
    private IntPtr NotificationHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WheelGestureSession.NotificationMessage)
        {
            handled = true;
            try { RefreshSession(); }
            catch (Exception error)
            {
                Record(error); InvalidateInteraction();
                status.Text = app.L("The gesture display could not refresh. Stop monitoring before trying again.");
            }
        }
        return IntPtr.Zero;
    }
    private void WorkStateChanged()
    {
        if (closed || finishing) return;
        if ((services.Busy() && !accepting) || !services.Enabled()) InvalidateInteraction();
        RefreshSession();
    }
    private void StartRequested() { if (CanStart) pendingStart = StartCoreAsync(); }
    private async Task StartCoreAsync()
    {
        starting = true; localProblems.Clear(); omittedProblems = 0; lastResult = ""; InvalidateInteraction(); ShowState();
        try
        {
            if (session is { } previous) await Join(previous);
            session = null; Dispose(ref runCancellation); runCancellation = new CancellationTokenSource();
            if (finishing || closed || stopping || !services.Enabled()) { runCancellation.Cancel(); return; }
            var selected = ((ModifierChoice)modifier.SelectedItem).Value;
            IWheelGestureSession created = await services.Start(selected, services.NotificationHandle(this), runCancellation.Token);
            if (finishing || closed || stopping || runCancellation.IsCancellationRequested || !services.Enabled()) { await Join(created); return; }
            session = created; lastSequence = 0; healthTimer.Start(); RefreshSession(); _ = Observe(created);
        }
        catch (Exception error)
        {
            if (session is { } active) await Join(active);
            if (error is not OperationCanceledException)
            {
                Record(error);
                if (!app.IsValidationHost && !finishing) app.Home.SetStatus(app.L("Gesture monitoring stopped with a problem.") + " " + app.L("Restart Gesture Monitoring"));
            }
        }
        finally { starting = false; if (!closed) ShowState(); }
    }
    private async Task Observe(IWheelGestureSession source)
    {
        try { await source.Completion; } catch (Exception error) { Record(error); }
        if (ReferenceEquals(source, session) && !finishing && !closed)
        {
            RefreshSession();
            if (!app.IsValidationHost && source.Snapshot.Status == WheelGestureStatus.Faulted)
                app.Home.SetStatus(app.L("Gesture monitoring stopped with a problem.") + " " + app.L("Restart Gesture Monitoring"));
        }
    }
    private void RefreshSession()
    {
        if (closed || finishing) return;
        if (session is { } active)
        {
            var snapshot = active.Snapshot;
            if (snapshot.Status != WheelGestureStatus.Running) InvalidateInteraction();
            if (active.TryTakeRequest(out var request) && request is not null && Fresh(request, snapshot) && snapshot.Status == WheelGestureStatus.Running &&
                !starting && !stopping && !accepting && popup is null && pendingPopupClose.IsCompleted && services.Enabled() && !services.Busy())
            {
                long version = ++interaction;
                try
                {
                    var target = services.CreateTarget(() => Current(version, active), (action, paths) => QueueFiles(action, paths, version, active),
                        error => { if (Current(version, active)) Report(error); });
                    popup = target;
                    target.Closed += () => { if (ReferenceEquals(popup, target)) { popup = null; popupTimer.Stop(); } };
                    target.ShowAt(request); if (ReferenceEquals(popup, target)) popupTimer.Start();
                }
                catch (Exception error) { InvalidateInteraction(); Record(error); }
            }
        }
        ShowState();
    }
    private bool Fresh(WheelGestureRequest request, WheelGestureSnapshot snapshot)
    {
        if (request.SessionId != snapshot.SessionId || request.Sequence <= lastSequence) return false;
        lastSequence = request.Sequence; return true;
    }
    private void QueueFiles(ToolAction action, string[] paths, long version, IWheelGestureSession source)
    {
        if (!Current(version, source) || accepting) return;
        // The Drop callback only supplies the chosen action and a bounded reference snapshot.
        // The completion exists before the dispatcher post, so Stop also joins an
        // acceptance which has not started its asynchronous file checks yet.
        accepting = true; popupTimer.Stop(); acceptCancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingAccept = completion.Task; CancellationToken token = acceptCancellation.Token;
        try { _ = Dispatcher.BeginInvoke(new Action(() => _ = AcceptFiles(action, paths, version, source, token, completion))); }
        catch (Exception error) { Record(error); accepting = false; Dispose(ref acceptCancellation); completion.TrySetResult(); }
    }
    private async Task AcceptFiles(ToolAction action, string[] paths, long version, IWheelGestureSession source, CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            // Destroying a registered OLE drop window is deferred as well as the
            // checks/opening. The original Drop callback has returned by now.
            await ClosePopup(); ShowState();
            if (!Current(version, source)) return;
            token.ThrowIfCancellationRequested();
            await services.OpenFiles(action, paths, token, () => Current(version, source));
            if (Current(version, source) && !token.IsCancellationRequested) lastResult = app.L("The dropped action finished.");
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
            {
                Record(error);
                if (!app.IsValidationHost && !finishing) app.ShowError(error);
            }
        }
        finally { accepting = false; Dispose(ref acceptCancellation); completion.TrySetResult(); if (!closed && !finishing) ShowState(); }
    }
    private Task ClosePopup()
    {
        popupTimer.Stop(); var old = popup; popup = null;
        try { old?.Close(); } catch (Exception error) { Record(error); }
        if (old is not null) pendingPopupClose = pendingPopupClose.IsCompletedSuccessfully ? old.Completion : Task.WhenAll(pendingPopupClose, old.Completion);
        return pendingPopupClose;
    }
    private void InvalidateInteraction()
    {
        interaction++; Cancel(acceptCancellation); _ = ClosePopup();
    }
    private void StopRequested()
    {
        if (closed || finishing || stopping || (!starting && !accepting && session?.Snapshot.Status != WheelGestureStatus.Running)) return;
        healthTimer.Stop(); stopping = true; InvalidateInteraction(); Cancel(runCancellation); ShowState(); pendingStop = StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        try { await Await(pendingStart); if (session is { } active) await Join(active); await Await(pendingAccept); await Await(pendingPopupClose); }
        finally { stopping = false; if (!closed) ShowState(); }
    }
    private async Task Join(IWheelGestureSession source)
    {
        try { await source.StopAsync(); } catch (Exception error) { Record(error); }
        try { await source.Completion; } catch (Exception error) { Record(error); }
        if (!ReferenceEquals(source, session))
        {
            try
            {
                var snapshot = source.Snapshot;
                foreach (var issue in snapshot.Issues) Append(Problem(issue));
                if (snapshot.OmittedIssues > 0) Append(string.Format(CultureInfo.CurrentCulture, app.L("Additional problems not retained: {0}."), snapshot.OmittedIssues));
            }
            catch (Exception error) { Record(error); }
        }
    }
    private async Task Await(Task task) { try { await task; } catch (Exception error) { if (error is not OperationCanceledException) Record(error); } }
    private void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (Exception error) { Record(error); } }
    private void Dispose(ref CancellationTokenSource? source) { var old = source; source = null; try { old?.Dispose(); } catch (Exception error) { Record(error); } }
    private void Record(Exception error)
    {
        var messages = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.Select(item => item.Message) : new[] { error.Message };
        foreach (string message in messages)
        {
            string bounded = Bounded(message, out bool shortened);
            Append(app.L(bounded), shortened);
        }
    }
    private static string Bounded(string text, out bool shortened)
    {
        shortened = text.Length > 500; if (!shortened) return text;
        int length = char.IsHighSurrogate(text[499]) ? 499 : 500; return text[..length];
    }
    private void Append(string text, bool alreadyShortened = false)
    {
        if (localProblems.Count == 32) { if (omittedProblems < int.MaxValue) omittedProblems++; return; }
        string raw = Bounded(text, out bool rawShortened);
        string visible = Bounded(LibraryJson.Visible(raw), out bool visibleShortened);
        localProblems.Add(visible + (alreadyShortened || rawShortened || visibleShortened ? "\n" + app.L("Problem detail is truncated.") : ""));
    }
    private void Report(Exception error) { Record(error); if (!closed && !finishing) ShowState(); }
    private string Problems(WheelGestureSnapshot snapshot)
    {
        string text = string.Join("\n\n", snapshot.Issues.Select(Problem));
        if (snapshot.OmittedIssues > 0) text += "\n\n" + string.Format(CultureInfo.CurrentCulture, app.L("Additional problems not retained: {0}."), snapshot.OmittedIssues);
        return text;
    }
    private string Problem(WheelGestureIssue issue) => LibraryJson.Visible(app.L(issue.Message)) +
        (issue.NativeErrorCode is { } code ? "\n" + string.Format(CultureInfo.CurrentCulture, app.L("Windows error code: {0}."), code) : "");
    private void ShowState()
    {
        if (closed) return;
        var snapshot = session?.Snapshot;
        string state = finishing || stopping || snapshot?.Status == WheelGestureStatus.Stopping ? "Stopping gesture monitoring…" : starting ? "Starting gesture monitoring…" :
            accepting ? "Checking dropped file references…" : snapshot?.Status == WheelGestureStatus.Running ? "Gesture monitoring is running for this session." :
            snapshot?.Status == WheelGestureStatus.Faulted ? "Gesture monitoring stopped with a problem." : "Gesture monitoring is stopped.";
        status.Text = app.L(state) + (lastResult.Length == 0 ? "" : "\n" + lastResult);
        problems.Text = (snapshot is null ? "" : Problems(snapshot)) + (localProblems.Count == 0 ? "" : "\n\n" + string.Join("\n\n", localProblems));
        if (omittedProblems > 0) problems.Text += "\n\n" + string.Format(CultureInfo.CurrentCulture, app.L("Additional problems not retained: {0}."), omittedProblems);
        start.IsEnabled = CanStart; modifier.IsEnabled = CanStart;
        stop.IsEnabled = !closed && !finishing && !stopping && (starting || accepting || snapshot?.Status == WheelGestureStatus.Running);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || finished) return;
        if (backgroundListening && !finishing) { e.Cancel = true; Hide(); return; }
        e.Cancel = true;
        if (!closeRequested) { closeRequested = true; _ = CloseAfterFinish(); }
    }
    private async Task CloseAfterFinish()
    {
        await FinishAsync();
        if (!closed) _ = Dispatcher.BeginInvoke(new Action(() => { if (!closed) { allowClose = true; Close(); } }));
    }
    internal void ListenInBackground()
    {
        backgroundListening = true;
        if (CanStart) StartRequested();
    }
    internal Task FinishAsync() => finish ??= FinishCoreAsync();
    private async Task FinishCoreAsync()
    {
        healthTimer.Stop(); finishing = true; InvalidateInteraction(); Cancel(runCancellation);
        try { ShowState(); } catch (Exception error) { Record(error); }
        await Await(pendingStart); await Await(pendingStop);
        if (session is { } active) await Join(active);
        await Await(pendingAccept); await Await(pendingPopupClose); Dispose(ref runCancellation); Dispose(ref acceptCancellation);
        try { notificationSource?.RemoveHook(NotificationHook); } catch (Exception error) { Record(error); }
        notificationSource = null; finished = true;
        try { ShowState(); } catch (Exception error) { Record(error); }
    }
}
