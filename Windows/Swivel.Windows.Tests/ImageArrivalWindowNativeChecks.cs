using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class ImageArrivalWindowNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) yield return target;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in Find<T>(child)) yield return descendant;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Arrival UI fixture did not settle."); Pump(); Thread.Sleep(1); }
        Pump();
    }
    private static void Finish(ImageArrivalWindow window) { Task task = window.FinishAsync(); Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); window.Close(); Pump(); }
    private static Button Button(ImageArrivalWindow window, App app, string key) => Find<Button>(window).Single(button => Equals(button.Content, app.L(key)));
    private static void Click(ImageArrivalWindow window, App app, string key) => Button(window, app, key).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

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
        // Every service below is synthetic. No directory, watcher, image file,
        // clipboard or Clothesline action is opened by this fixture.
        const string folder = @"C:\Fixture\arrivals";
        const string path = @"C:\Fixture\arrivals\complete-path.png";
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4); bitmap.Freeze();
        ArrivalCard<BitmapSource> Card(string value = path) => new(Guid.NewGuid(), value, bitmap, ArrivalKind.Renamed);
        var fake = new Session(folder); int creates = 0, hangs = 0, cancels = 0; bool enabled = true, busy = false, existing = false;
        var hangDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? hangOwner = null, cancelledOwner = null;
        var services = new ImageArrivalUiServices((_, _) => { Interlocked.Increment(ref creates); return fake; }, _ => folder,
            (_, owner, _) => { hangs++; hangOwner = owner; return hangDone.Task; }, owner => { cancels++; cancelledOwner = owner; },
            () => enabled, () => busy, _ => existing);
        var window = new ImageArrivalWindow(app, services);
        check(creates == 0 && !Button(window, app, "Start Monitoring").IsEnabled && !Button(window, app, "Stop Monitoring").IsEnabled,
            "Arrival UI starts stopped without a chosen folder or implicit factory call");
        check(!Find<Button>(window).Any(button => button.IsDefault) && Button(window, app, "Close").IsCancel,
            "Arrival Start and Hang are never implicit Enter defaults");
        var root = (DockPanel)window.Content;
        check(DockPanel.GetDock(root.Children[0]) == Dock.Bottom && root.Children[1] is ScrollViewer,
            "Arrival fixed footer remains outside the scrolling scope and cards");
        Click(window, app, "Choose Arrival Folder…");
        check(creates == 0 && Button(window, app, "Start Monitoring").IsEnabled && Find<TextBox>(window).Any(box => box.Text == folder && box.IsReadOnly),
            "Choosing a folder only displays its full path and enables explicit Start");
        Click(window, app, "Start Monitoring"); Until(() => fake.Starts == 1);
        check(creates == 1 && !Button(window, app, "Choose Arrival Folder…").IsEnabled && Button(window, app, "Stop Monitoring").IsEnabled,
            "Start owns exactly one session and prevents changing its chosen folder");
        fake.Cards(Card()); fake.Emit(); Pump();
        var list = Find<ListView>(window).Single(); list.SelectedIndex = 0;
        check(list.Items.Count == 1 && Find<TextBox>(window).Any(box => box.Text.Contains(path, StringComparison.Ordinal) && box.Text.Contains(app.L("Renamed-path notification"), StringComparison.Ordinal)),
            "Arrival details preserve the complete reported path and notification kind");
        check(Button(window, app, "Preview Stored Thumbnail").IsEnabled && Button(window, app, "Hang Selected Image").IsEnabled,
            "A frozen historical thumbnail can be previewed or explicitly hung");
        Click(window, app, "Stop Monitoring"); Until(() => fake.Stops > 0 && Button(window, app, "Start Monitoring").IsEnabled);
        check(list.Items.Count == 1 && Button(window, app, "Hang Selected Image").IsEnabled,
            "Stopping preserves completed cards and permits explicit Hang");
        existing = true; Click(window, app, "Hang Selected Image");
        check(hangs == 0 && Find<TextBlock>(window).Any(text => text.Text.Contains(app.L(ImageArrivalNotices.Existing), StringComparison.Ordinal)),
            "An already-hung path is explained without falsely claiming a reread or invoking Hang");
        existing = false; Click(window, app, "Hang Selected Image");
        check(hangs == 1 && ReferenceEquals(hangOwner, window) && !Button(window, app, "Hang Selected Image").IsEnabled,
            "Explicit Hang passes this window as action owner and prevents concurrent Hang");
        hangDone.SetException(new InvalidOperationException("synthetic hang failure"));
        Until(() => Button(window, app, "Hang Selected Image").IsEnabled);
        check(list.Items.Count == 1, "Completed or failed Hang keeps the arrival card until explicit Dismiss");
        busy = true; typeof(App).GetMethod("NotifyWorkStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, null);
        check(!Button(window, app, "Hang Selected Image").IsEnabled, "Other active work disables Hang without a polling timer");
        busy = false; typeof(App).GetMethod("NotifyWorkStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, null);
        check(Button(window, app, "Hang Selected Image").IsEnabled, "Other work finishing reenables Hang even after monitoring stopped");
        enabled = false; Click(window, app, "Hang Selected Image"); check(hangs == 1, "Feature-off rejects a stale Hang request"); enabled = true;
        Click(window, app, "Dismiss Card"); check(fake.Dismisses == 1 && list.Items.Count == 0, "Dismiss removes only the selected in-memory card");
        fake.Cards(Card(path + "\u202E")); fake.Emit(); Pump(); list.SelectedIndex = 0;
        check(Find<TextBox>(window).Any(box => box.Text.Contains("[U+202E]", StringComparison.Ordinal)), "Arrival path controls are visibly escaped without dropping the full path");
        Click(window, app, "Clear Cards"); check(fake.Clears == 1 && list.Items.Count == 0, "Clear requests only in-memory card removal");
        fake.Cards(Card()); fake.Emit(); Pump(); int readsBefore = fake.Reads;
        Task.Run(() => { for (int index = 0; index < 1000; index++) fake.Emit(); }).GetAwaiter().GetResult();
        check(fake.Reads == readsBefore, "Arbitrary-thread notifications do not synchronously access WPF or read snapshots");
        Pump(); check(fake.Reads - readsBefore < 20, "One thousand notifications coalesce into a bounded dispatcher refresh");
        Task finish = window.FinishAsync(); check(ReferenceEquals(finish, window.FinishAsync()), "Finish is idempotent"); Until(() => finish.IsCompleted);
        check(cancels == 1 && ReferenceEquals(cancelledOwner, window), "Finish cancels work through exactly this window's owner helper");
        int finalReads = fake.Reads; fake.Emit(); Pump(); check(fake.Reads == finalReads, "Late events after Finish cannot refresh closed-session UI"); window.Close(); Pump();

        // Stop and close during a factory which ignores cancellation must join
        // the late resource, even before the window is ever displayed.
        var created = new TaskCompletionSource<IImageArrivalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new ManualResetEventSlim(); var late = new Session(folder) { AutoComplete = false };
        var lateWindow = new ImageArrivalWindow(app, services with { Create = (_, _) => { entered.Set(); return created.Task.GetAwaiter().GetResult(); }, CancelOwnedHang = _ => { } });
        Click(lateWindow, app, "Choose Arrival Folder…"); Click(lateWindow, app, "Start Monitoring"); Until(() => entered.IsSet);
        Task lateFinish = lateWindow.FinishAsync(); check(!lateFinish.IsCompleted, "Unshown Finish waits for pending Create");
        created.SetResult(late); Until(() => late.Stops > 0);
        check(late.Starts == 0 && !lateFinish.IsCompleted, "A late cancelled session never starts and is joined before Finish");
        late.Complete(); Until(() => lateFinish.IsCompleted); lateFinish.GetAwaiter().GetResult(); lateWindow.Close(); Pump(); entered.Dispose();

        var stoppedCreate = new TaskCompletionSource<IImageArrivalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new ManualResetEventSlim(); var stoppedLate = new Session(folder) { AutoComplete = false };
        var stopWindow = new ImageArrivalWindow(app, services with { Create = (_, _) => { stopEntered.Set(); return stoppedCreate.Task.GetAwaiter().GetResult(); }, CancelOwnedHang = _ => { } });
        Click(stopWindow, app, "Choose Arrival Folder…"); Click(stopWindow, app, "Start Monitoring"); Until(() => stopEntered.IsSet);
        Click(stopWindow, app, "Stop Monitoring"); stoppedCreate.SetResult(stoppedLate); Until(() => stoppedLate.Stops > 0);
        stoppedLate.CleanupFailure(); stoppedLate.Complete(); Until(() => Button(stopWindow, app, "Start Monitoring").IsEnabled);
        check(stoppedLate.Starts == 0 && Find<TextBlock>(stopWindow).Any(text => text.Text.Contains(app.L(ImageArrivalRules.CleanupFailed), StringComparison.Ordinal) &&
            text.Text.Contains("late cleanup[U+202E]", StringComparison.Ordinal) && text.Text.Contains(string.Format(app.L("Additional problems not retained: {0}."), 2), StringComparison.Ordinal)),
            "Stop during Create preserves the late session's cleanup diagnostics and omitted count without displaying cards or starting it");
        Finish(stopWindow); stopEntered.Dispose();

        var automatic = new Session(folder) { AutoComplete = false };
        var autoWindow = new ImageArrivalWindow(app, services with { Create = (_, _) => automatic, CancelOwnedHang = _ => { } });
        Click(autoWindow, app, "Choose Arrival Folder…"); Click(autoWindow, app, "Start Monitoring"); Until(() => automatic.Starts == 1);
        automatic.Stopping(); automatic.Emit(); Pump();
        check(Find<TextBlock>(autoWindow).Any(text => text.Text.Contains(app.L("Stopping image arrival monitoring…"), StringComparison.Ordinal)),
            "Automatic-limit cleanup displays Stopping until the worker completes");
        Task autoFinish = autoWindow.FinishAsync(); check(!autoFinish.IsCompleted, "Finish joins automatic cleanup already in progress");
        automatic.Complete(); Until(() => autoFinish.IsCompleted); autoWindow.Close(); Pump();

        // A synchronous idle Finish must not recursively Close inside Closing.
        var idle = new ImageArrivalWindow(app, services with { CancelOwnedHang = _ => { } });
        int closed = 0; idle.Closed += (_, _) => closed++; idle.Show(); idle.Close(); Until(() => closed == 1);
        check(idle.IsFinishing && closed == 1, "Shown idle Close defers its final Close until the original Closing event returns");
        var closing = new Session(folder) { AutoComplete = false };
        var shown = new ImageArrivalWindow(app, services with { Create = (_, _) => closing, CancelOwnedHang = _ => { } });
        shown.Show(); Click(shown, app, "Choose Arrival Folder…"); Click(shown, app, "Start Monitoring"); Until(() => closing.Starts == 1);
        bool shownClosed = false; shown.Closed += (_, _) => shownClosed = true; shown.Close();
        check(!shownClosed && shown.IsFinishing, "Closing a running window waits rather than abandoning the reader");
        closing.Complete(); Until(() => shownClosed);
        check(closing.Stops == 1, "Asynchronous close stops and joins its session exactly once");

        var ownedHang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Window? actualOwner = null; int ownerCancellations = 0;
        var hanging = new Session(folder); ImageArrivalWindow? ownedWindow = null;
        ownedWindow = new ImageArrivalWindow(app, services with { Create = (_, _) => hanging,
            Hang = (_, owner, _) => { actualOwner = owner; return ownedHang.Task; },
            CancelOwnedHang = owner => { if (ReferenceEquals(actualOwner, owner)) { ownerCancellations++; ownedHang.TrySetCanceled(); } } });
        Click(ownedWindow, app, "Choose Arrival Folder…"); Click(ownedWindow, app, "Start Monitoring"); Until(() => hanging.Starts == 1);
        hanging.Cards(Card()); hanging.Emit(); Pump(); Find<ListView>(ownedWindow).Single().SelectedIndex = 0;
        Click(ownedWindow, app, "Hang Selected Image"); Finish(ownedWindow);
        check(ownerCancellations == 1 && ownedHang.Task.IsCompleted, "Finish cancels and awaits its own pending Hang");
        var otherOwner = new Window(); actualOwner = otherOwner; ownerCancellations = 0;
        var foreign = new ImageArrivalWindow(app, services with { CancelOwnedHang = owner => { if (ReferenceEquals(actualOwner, owner)) ownerCancellations++; } });
        Finish(foreign); otherOwner.Close(); check(ownerCancellations == 0, "Owner-scoped Finish does not request cancellation of a different window's operation");
    }

    private sealed class Session(string directory) : IImageArrivalSession
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ArrivalSnapshot<BitmapSource> snapshot = new(Guid.NewGuid(), 0, directory, ArrivalStatus.Stopped, ArrivalStopReason.None,
            Array.Empty<ArrivalCard<BitmapSource>>(), 0, 0, 0, 0, 0, Array.Empty<ArrivalIssue>(), 0);
        public bool AutoComplete = true;
        public int Starts, Stops, Dismisses, Clears, Reads;
        public ArrivalSnapshot<BitmapSource> Snapshot { get { Reads++; return snapshot; } }
        public event Action? Changed;
        public Task Completion => done.Task;
        public void Start() { Starts++; snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = ArrivalStatus.Running }; Emit(); }
        public Task StopAsync(ArrivalStopReason reason = ArrivalStopReason.User)
        { Stops++; snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = ArrivalStatus.Stopping, StopReason = reason }; if (AutoComplete) Complete(); return done.Task; }
        public void Complete() { snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = ArrivalStatus.Stopped }; done.TrySetResult(); Emit(); }
        public void Stopping() => snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = ArrivalStatus.Stopping, StopReason = ArrivalStopReason.CardLimit };
        public void CleanupFailure() => snapshot = snapshot with { Revision = snapshot.Revision + 1, StopReason = ArrivalStopReason.CleanupError,
            Issues = new[] { new ArrivalIssue(directory, ImageArrivalRules.CleanupFailed, "late cleanup\u202E") }, OmittedIssues = 2 };
        public void Cards(params ArrivalCard<BitmapSource>[] cards) => snapshot = snapshot with { Revision = snapshot.Revision + 1, Cards = cards };
        public void Dismiss(Guid id) { Dismisses++; Cards(snapshot.Cards.Where(card => card.Id != id).ToArray()); Emit(); }
        public void ClearCards() { Clears++; Cards(); Emit(); }
        public void Emit() => Changed?.Invoke();
    }
}
