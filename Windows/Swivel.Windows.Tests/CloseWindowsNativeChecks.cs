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
    private sealed class Lease : ICloseWindowLease
    {
        public CloseWindowInfo Info { get; } = new(123, 9999, 9998, 100, 1, "Fixture only", "FixtureClass", @"C:\Apps\Fixture.exe");
        public int Sends, Disposals; public bool Mta, Dedicated;
        public CloseWindowInfo? Inspect(CancellationToken token) { token.ThrowIfCancellationRequested(); return Info; }
        public CloseWindowsResult Send(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sends++; Mta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA; Dedicated = !Thread.CurrentThread.IsThreadPoolThread; return CloseWindowsResult.Processed; }
        public void Dispose() => Disposals++;
    }
    private sealed class Source(Lease lease) : ICloseWindowsSource
    {
        public CloseWindowsEnumeration Enumerate(CancellationToken token) => new(new nint[] { 123 });
        public ICloseWindowLease? Open(nint window, CancellationToken token) => lease;
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
        check(list.SelectedIndex == -1 && !review.IsEnabled && lease.Sends == 0, "Close-window UI has no preselected target or automatic action");
        list.SelectedIndex = 0; check(review.IsEnabled && lease.Sends == 0, "Selecting a close-window row only enables explicit review");
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
    }
}
