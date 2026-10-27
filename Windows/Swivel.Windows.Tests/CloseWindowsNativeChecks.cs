using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class CloseWindowsNativeChecks
{
    private sealed class Lease(CloseWindowInfo? info = null) : ICloseWindowLease
    {
        public CloseWindowInfo Info { get; } = info ?? new(123, 9999, 9998, 100, 1, "Fixture only", "FixtureClass", @"C:\Apps\Fixture.exe");
        public int Sends, Disposals; public bool Mta, Dedicated;
        public CloseWindowInfo? Inspect(CancellationToken token) { token.ThrowIfCancellationRequested(); return Info; }
        public CloseWindowsResult Send(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sends++; Mta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA; Dedicated = !Thread.CurrentThread.IsThreadPoolThread; return CloseWindowsResult.Processed; }
        public void Dispose() => Disposals++;
    }
    private sealed class Source(params Lease[] leases) : ICloseWindowsSource
    {
        public CloseWindowsEnumeration Enumerate(CancellationToken token) => new(leases.Select(lease => lease.Info.Handle).ToArray());
        public ICloseWindowLease? Open(nint window, CancellationToken token) => leases.Single(lease => lease.Info.Handle == window);
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) yield return target;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in Find<T>(child)) yield return descendant;
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        // These fixtures never enumerate or send a close message to real apps.
        // A fake process lease is injected into the production UI and worker.
        var lease = new Lease(); using var session = CloseWindows.Scan(new Source(lease), default);
        var dialog = new CloseWindowsDialog(app, session, default);
        var list = Find<ListView>(dialog).Single(); var review = Find<Button>(dialog).Single(button => Equals(button.Content, app.L("Review Selected Window…")));
        var reviewAll = Find<Button>(dialog).Single(button => Equals(button.Content, app.L("Review All Listed Windows of This App…")));
        check(list.SelectedIndex == -1 && !review.IsEnabled && lease.Sends == 0, "Close-window UI has no preselected target or automatic action");
        list.SelectedIndex = 0; check(review.IsEnabled && lease.Sends == 0, "Selecting a close-window row only enables explicit review");
        check(!reviewAll.IsEnabled, "An app with one listed window has only the single-window review");
        var confirmation = new CloseWindowsConfirmDialog(app, lease.Info);
        var cancel = Find<Button>(confirmation).Single(button => Equals(button.Content, app.L("Cancel")));
        var submit = Find<Button>(confirmation).Single(button => Equals(button.Content, app.L("Request Normal Close")));
        check(cancel.IsDefault && cancel.IsCancel && !submit.IsDefault, "Close-window confirmation defaults Enter and Escape to cancellation");
        check(Find<TextBox>(confirmation).Single().Text.Contains(lease.Info.ExecutablePath, StringComparison.Ordinal), "Close-window final confirmation includes complete recorded executable path");
        confirmation.Close(); dialog.Close(); dialog.FinishAsync().GetAwaiter().GetResult(); Drain();
        check(lease.Sends == 0, "Closing the window list never requests a target close");
        var result = CloseWindowsReader.RequestAsync(session, 0, default).GetAwaiter().GetResult();
        check(result == CloseWindowsResult.Processed && lease.Sends == 1 && lease.Mta && lease.Dedicated, "Normal close worker is a dedicated MTA thread and returns the single fake request result");
        reject(() => CloseWindowsReader.RequestAsync(session, 0, default).GetAwaiter().GetResult(), "Window native worker refuses automatic retry");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var pendingLease = new Lease(); using var pendingSession = CloseWindows.Scan(new Source(pendingLease), default);
        var neverShown = new CloseWindowsDialog(app, pendingSession, canceled.Token);
        // Cancel queued its Dispatcher callback during construction. Finishing
        // first must make that callback harmless after the source is disposed.
        neverShown.FinishAsync().GetAwaiter().GetResult(); Drain(); neverShown.Close();
        check(pendingLease.Sends == 0, "Late cancellation callback after unshown dialog Finish cannot touch a disposed token or send");
        neverShown.FinishAsync().GetAwaiter().GetResult();

        // Every listed window of one app: only explicit review, then one fake request each on the worker.
        CloseWindowInfo Window(int handle, string path) => new(handle, 9000u + (uint)handle, 8000u + (uint)handle, 100, 1, "Fixture window " + handle, "FixtureClass", path);
        var appLeases = new[] { new Lease(Window(201, @"C:\Apps\Fixture.exe")), new Lease(Window(202, @"C:\Apps\Helper.exe")), new Lease(Window(203, @"C:\Apps\Fixture.exe")) };
        using var appSession = CloseWindows.Scan(new Source(appLeases), default);
        var appDialog = new CloseWindowsDialog(app, appSession, default);
        var appList = Find<ListView>(appDialog).Single(); var appAll = Find<Button>(appDialog).Single(button => Equals(button.Content, app.L("Review All Listed Windows of This App…")));
        check(!appAll.IsEnabled, "Close-all review is disabled until a window is chosen");
        var outcomes = Find<TextBox>(appDialog).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == app.L("Close Request Results"));
        check(outcomes.Visibility == Visibility.Collapsed && outcomes.Text.Length == 0, "Close request results stay hidden until a request for all windows finishes");
        appList.SelectedIndex = 1; check(!appAll.IsEnabled, "A helper program with one listed window has no close-all review");
        appList.SelectedIndex = 2; check(appAll.IsEnabled && appLeases.All(l => l.Sends == 0), "Choosing a window of an app with several listed windows only enables the close-all review");
        var appConfirm = new CloseAppWindowsConfirmDialog(app, appSession.SameApp(2).Select(index => appSession.Windows[index]).ToArray());
        var appCancel = Find<Button>(appConfirm).Single(button => Equals(button.Content, app.L("Cancel")));
        var appSubmit = Find<Button>(appConfirm).Single(button => Equals(button.Content, app.L("Request Normal Close for All")));
        string appText = Find<TextBox>(appConfirm).Single().Text;
        check(appCancel.IsDefault && appCancel.IsCancel && !appSubmit.IsDefault, "Close-all confirmation defaults Enter and Escape to cancellation");
        check(appText.Contains(@"C:\Apps\Fixture.exe", StringComparison.Ordinal) && appText.Contains("Fixture window 201", StringComparison.Ordinal) && appText.Contains("Fixture window 203", StringComparison.Ordinal)
            && !appText.Contains("Fixture window 202", StringComparison.Ordinal), "Close-all confirmation lists every window of that exact path and no helper window");
        appConfirm.Close(); appDialog.Close(); appDialog.FinishAsync().GetAwaiter().GetResult(); Drain();
        check(appLeases.All(l => l.Sends == 0), "Reviewing all windows of an app and closing the list sends nothing");
        var appResults = CloseWindowsReader.RequestEachAsync(appSession, appSession.SameApp(0), default).GetAwaiter().GetResult();
        check(appResults.SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.Processed, CloseWindowsResult.Processed }) && appLeases[0].Sends == 1 && appLeases[2].Sends == 1
            && appLeases[1].Sends == 0 && appLeases[0].Mta && appLeases[0].Dedicated && appLeases[2].Mta && appLeases[2].Dedicated,
            "Close-all requests run once per window of the app on a dedicated MTA thread and leave the helper alone");
        check(CloseWindowsReader.RequestEachAsync(appSession, new[] { 0, 2 }, default).GetAwaiter().GetResult().All(r => r is null) && appLeases[0].Sends == 1,
            "Close-all never repeats a window already requested");
    }
}
