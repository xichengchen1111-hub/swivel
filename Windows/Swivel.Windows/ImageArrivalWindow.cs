using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ImageArrivalNotices
{
    internal const string Scope = "Choose a folder, then explicitly Start. After starting, Windows notifications about created or renamed image paths may cause their current contents to be read for thumbnails. This includes files copied into the folder, existing files renamed, and files replaced at the reported path before reading. These cards do not identify screenshots or prove which file caused an event.";
    internal const string Privacy = "There is no initial folder scan, subfolder monitoring, clipboard reading or screenshot takeover. Folder choices and cards are not saved after this window closes. Notifications can be missed; this is not a complete arrival history.";
    internal const string Limits = "One image is read at a time. A session allows up to 12 cards, 64 queued paths and 512 eligible notifications. The sum of file sizes reserved before preview attempts is limited to 512 MiB, including failed attempts and retries; actual disk reading can differ. A path has up to four read attempts and is considered once per session. Dismissing cards does not reset budgets or collect that path again. Limits or watcher failures stop monitoring; no catch-up scan is performed.";
    internal const string Stop = "Stop cancels pending work and waits for the current read and cleanup; system calls may take longer. Completed cards remain available. Starting again clears the current cards and begins a new session for future notifications only.";
    internal const string Cards = "Preview shows only the stored thumbnail. Hang uses the current path with the existing Clothesline action; a new print is read again and may differ from the thumbnail. An already hung path keeps its existing print until you remove it. Cards remain until Dismiss or Clear Cards, which never delete files.";
    internal const string Existing = "An image with this path is already on Clothesline. Remove that print before hanging it again to refresh.";
}

// All injected services are explicit UI requests. A validation host can exercise
// lifetime and dispatcher behavior without a watcher, file access or real Hang.
internal sealed record ImageArrivalUiServices(
    Func<string, CancellationToken, IImageArrivalSession> Create,
    Func<Window, string?> PickFolder,
    Func<string, Window, Action<string>, Task> Hang,
    Action<Window> CancelOwnedHang,
    Func<bool> Enabled,
    Func<bool> Busy,
    Func<string, bool> AlreadyHung);

internal sealed class ImageArrivalWindow : Window
{
    private readonly App app;
    private readonly ImageArrivalUiServices services;
    private readonly object notificationGate = new();
    private readonly TextBox directory = TextDetails(60), details = TextDetails(128), problems = TextDetails(110);
    private readonly TextBlock status = UI.Text(""), counts = UI.Text("", 12);
    private readonly ListView list = new() { Height = 235, SelectionMode = SelectionMode.Single };
    private readonly Button choose, start, stop, preview, hang, dismiss, clear;
    private volatile IImageArrivalSession? session;
    private IImageArrivalSession? queuedSource;
    private Action? sessionChanged;
    private CancellationTokenSource? runCancellation;
    private Task pendingStart = Task.CompletedTask, pendingStop = Task.CompletedTask, pendingHang = Task.CompletedTask;
    private Task? finishing;
    private Guid displayedSession;
    private long displayedRevision = -1;
    private string? chosenDirectory;
    private string localProblem = "";
    private bool notificationQueued, starting, stopping, hanging, closeRequested, allowClose, finished;
    private volatile bool isFinishing, closed;
    private ImageArrivalPreviewWindow? previewWindow;
    internal bool IsFinishing => isFinishing;

    internal ImageArrivalWindow(App app) : this(app, new ImageArrivalUiServices(
        ImageArrivalSession.Create,
        owner => { var picker = new OpenFolderDialog(); return picker.ShowDialog(owner) == true ? picker.FolderName : null; },
        (path, owner, report) => app.RunActionAsync(new("clothesline.add", "Hang on Clothesline", Feature.Clothesline, ImageOnly: true), new[] { path }, owner, report),
        owner => app.CancelWorkFrom(owner), () => app.Prefs.IsOn(Feature.Clothesline), () => app.Busy,
        path => app.Prints.Any(print => print.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))) { }

    internal ImageArrivalWindow(App app, ImageArrivalUiServices services)
    {
        this.app = app; this.services = services;
        Title = app.L("Image Arrival Cards"); Width = 900; Height = 800; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel();
        footer.Children.Add(status); footer.Children.Add(counts);
        var controls = new WrapPanel();
        choose = UI.Button(app.L("Choose Arrival Folder…"), ChooseFolder);
        start = UI.Button(app.L("Start Monitoring"), StartRequested);
        stop = UI.Button(app.L("Stop Monitoring"), () => StopRequested(ArrivalStopReason.User));
        preview = UI.Button(app.L("Preview Stored Thumbnail"), PreviewSelected);
        hang = UI.Button(app.L("Hang Selected Image"), HangRequested);
        dismiss = UI.Button(app.L("Dismiss Card"), DismissSelected);
        clear = UI.Button(app.L("Clear Cards"), ClearCards);
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true;
        foreach (var button in new[] { choose, start, stop, preview, hang, dismiss, clear, close }) controls.Children.Add(button);
        footer.Children.Add(controls); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        foreach (string note in new[] { ImageArrivalNotices.Scope, ImageArrivalNotices.Privacy, ImageArrivalNotices.Limits, ImageArrivalNotices.Stop, ImageArrivalNotices.Cards })
            body.Children.Add(UI.Text(app.L(note), 12));
        body.Children.Add(UI.Text(app.L("Selected Arrival Folder")));
        AutomationProperties.SetName(directory, app.L("Selected Arrival Folder")); body.Children.Add(directory);
        var columns = new GridView();
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding(nameof(Row.Thumbnail)));
        image.SetValue(FrameworkElement.WidthProperty, 88d); image.SetValue(FrameworkElement.HeightProperty, 64d); image.SetValue(Image.StretchProperty, Stretch.Uniform);
        columns.Columns.Add(new GridViewColumn { Header = app.L("Thumbnail"), CellTemplate = new DataTemplate { VisualTree = image }, Width = 110 });
        columns.Columns.Add(new GridViewColumn { Header = app.L("Reported image path"), DisplayMemberBinding = new Binding(nameof(Row.VisiblePath)), Width = 475 });
        columns.Columns.Add(new GridViewColumn { Header = app.L("Notification kind"), DisplayMemberBinding = new Binding(nameof(Row.Kind)), Width = 150 });
        list.View = columns; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, app.L("Image Arrival Cards")); body.Children.Add(list);
        AutomationProperties.SetName(details, app.L("Selected Arrival Details")); body.Children.Add(details);
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        AutomationProperties.SetName(problems, app.L("Arrival Problems")); body.Children.Add(problems);
        root.Children.Add(UI.Scroll(body)); Content = root;
        list.SelectionChanged += (_, _) => { UpdateSelection(); UpdateControls(); };
        Activated += (_, _) => UpdateControls();
        app.WorkStateChanged += WorkStateChanged;
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; app.WorkStateChanged -= WorkStateChanged; if (!isFinishing) _ = FinishAsync(); };
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        ShowState();
    }

    private void WorkStateChanged() { if (!closed && !isFinishing) UpdateControls(); }

    private static TextBox TextDetails(double height) => new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = height, Margin = new Thickness(0, 3, 0, 8) };
    private bool CanStart => !isFinishing && !closed && !starting && !stopping && services.Enabled() && chosenDirectory is not null &&
        (session is null || session.Snapshot.Status == ArrivalStatus.Stopped);
    private void ChooseFolder()
    {
        if (!CanChoose()) return;
        try
        {
            string? path = services.PickFolder(this);
            if (isFinishing || closed || !services.Enabled() || path is null) return;
            chosenDirectory = path; directory.Text = LibraryJson.Visible(path); localProblem = ""; ShowState();
        }
        catch (Exception error) { SetProblem(error); }
    }
    private bool CanChoose() => !isFinishing && !closed && !starting && !stopping && services.Enabled() &&
        (session is null || session.Snapshot.Status == ArrivalStatus.Stopped);
    private void StartRequested() { if (CanStart) pendingStart = StartCoreAsync(); }
    private async Task StartCoreAsync()
    {
        starting = true; localProblem = ""; ShowState();
        var previous = session;
        try
        {
            if (previous is not null) { Detach(previous); await JoinSession(previous, ArrivalStopReason.User); }
            session = null; displayedSession = Guid.Empty; displayedRevision = -1;
            list.ItemsSource = null; details.Text = ""; problems.Text = "";
            DisposeCancellation(); runCancellation = new CancellationTokenSource();
            CancellationToken token = runCancellation.Token; string path = chosenDirectory!;
            if (isFinishing || closed || stopping || !services.Enabled()) { runCancellation.Cancel(); return; }
            var created = await Task.Run(() => services.Create(path, token));
            // Even a factory that ignores cancellation must have its late resource
            // stopped and joined before this task (and FinishAsync) can complete.
            if (isFinishing || closed || stopping || token.IsCancellationRequested || !services.Enabled())
            { await JoinSession(created, isFinishing ? ArrivalStopReason.Closed : ArrivalStopReason.Cancelled); return; }
            session = created; displayedSession = created.Snapshot.SessionId;
            sessionChanged = () => QueueSnapshot(created); created.Changed += sessionChanged;
            created.Start(); ApplySnapshot(created); _ = ObserveCompletion(created);
        }
        catch (Exception error)
        {
            if (session is { } active) await JoinSession(active, ArrivalStopReason.ReadFailure);
            if (error is not OperationCanceledException) RecordProblem(error);
        }
        finally { starting = false; if (!closed) ShowState(); }
    }

    private async Task ObserveCompletion(IImageArrivalSession source)
    { try { await source.Completion; } catch (Exception error) { RecordProblem(error); } QueueSnapshot(source); }
    private void QueueSnapshot(IImageArrivalSession source)
    {
        lock (notificationGate)
        {
            if (isFinishing || closed || !ReferenceEquals(source, session)) return;
            queuedSource = source;
            if (notificationQueued) return;
            notificationQueued = true;
        }
        try { Dispatcher.BeginInvoke(new Action(DrainSnapshot)); }
        catch (InvalidOperationException) { lock (notificationGate) { notificationQueued = false; queuedSource = null; } }
    }
    private void DrainSnapshot()
    {
        IImageArrivalSession? source;
        lock (notificationGate) { source = queuedSource; queuedSource = null; notificationQueued = false; }
        // Clear pending before rendering. Events raised while rendering can queue
        // one further read of the newest snapshot instead of losing a revision.
        if (source is null || isFinishing || closed || !ReferenceEquals(source, session)) return;
        ApplySnapshot(source);
    }
    private void ApplySnapshot(IImageArrivalSession source)
    {
        if (closed || isFinishing || !ReferenceEquals(source, session)) return;
        var snapshot = source.Snapshot;
        if (snapshot.SessionId != displayedSession || snapshot.Revision <= displayedRevision) return;
        displayedRevision = snapshot.Revision;
        Guid? selected = (list.SelectedItem as Row)?.Id;
        var rows = snapshot.Cards.Select(card => new Row(card.Id, card.Path, LibraryJson.Visible(card.Path),
            card.Thumbnail.IsFrozen ? card.Thumbnail : null, app.L(card.Kind == ArrivalKind.Created ? "Created-path notification" : "Renamed-path notification"))).ToArray();
        list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
        problems.Text = SnapshotProblems(snapshot);
        UpdateSelection(); ShowState();
    }
    private string SnapshotProblems(ArrivalSnapshot<BitmapSource> snapshot)
    {
        string text = string.Join("\n\n", snapshot.Issues.Select(issue => LibraryJson.Visible(issue.Path) + "\n" +
            app.L(issue.Message) + (issue.Detail.Length == 0 ? "" : "\n" + LibraryJson.Visible(issue.Detail))));
        if (snapshot.OmittedIssues > 0) text += "\n\n" + string.Format(CultureInfo.CurrentCulture,
            app.L("Additional problems not retained: {0}."), snapshot.OmittedIssues);
        return text;
    }
    private void UpdateSelection()
    {
        details.Text = list.SelectedItem is Row row ? app.L("Reported image path") + ":\n" + row.VisiblePath + "\n\n" +
            row.Kind + "\n" + app.L("This is a stored thumbnail of a previously read path, not proof of the event's original file.") : "";
    }

    private void StopRequested(ArrivalStopReason reason)
    {
        if (closed || isFinishing || stopping || (!starting && session?.Snapshot.Status != ArrivalStatus.Running)) return;
        stopping = true; TryCancel(); ShowState(); pendingStop = StopCoreAsync(reason);
    }
    private async Task StopCoreAsync(ArrivalStopReason reason)
    {
        try { await AwaitAndRecord(pendingStart); if (session is { } active) await JoinSession(active, reason); }
        finally { stopping = false; if (session is { } active) ApplySnapshot(active); if (!closed) ShowState(); }
    }
    private async Task JoinSession(IImageArrivalSession source, ArrivalStopReason reason)
    {
        try { await source.StopAsync(reason); } catch (Exception error) { RecordProblem(error); }
        // A stop error must not abandon a still-running reader or resource cleanup.
        try { await source.Completion; } catch (Exception error) { RecordProblem(error); }
        // A factory may finish after Stop/Close. It was never the displayed
        // session, but successful Completion can still carry cleanup diagnostics.
        if (!ReferenceEquals(source, session))
        {
            try
            {
                var snapshot = source.Snapshot;
                string text = SnapshotProblems(snapshot);
                if (snapshot.StopReason is not ArrivalStopReason.None and not ArrivalStopReason.User)
                    text = app.L(Reason(snapshot.StopReason)) + (text.Length == 0 ? "" : "\n\n" + text);
                if (text.Length > 0) RecordVisibleProblem(text);
            }
            catch (Exception error) { RecordProblem(error); }
        }
    }
    private async Task AwaitAndRecord(Task task)
    { try { await task; } catch (Exception error) { if (error is not OperationCanceledException) RecordProblem(error); } }
    private void TryCancel()
    { try { runCancellation?.Cancel(); } catch (Exception error) { RecordProblem(error); } }
    private void DisposeCancellation()
    { var source = runCancellation; runCancellation = null; try { source?.Dispose(); } catch (Exception error) { RecordProblem(error); } }
    private void Detach(IImageArrivalSession source)
    { if (sessionChanged is not null) source.Changed -= sessionChanged; sessionChanged = null; }

    private void PreviewSelected()
    {
        if (isFinishing || closed || list.SelectedItem is not Row { Thumbnail: { } thumbnail } row) return;
        previewWindow?.Close(); var child = new ImageArrivalPreviewWindow(app, row.VisiblePath, thumbnail) { Owner = this };
        previewWindow = child; child.Closed += (_, _) => { if (ReferenceEquals(previewWindow, child)) previewWindow = null; }; child.Show();
    }
    private void HangRequested()
    {
        if (isFinishing || closed || hanging || !services.Enabled() || services.Busy() || list.SelectedItem is not Row row) return;
        if (services.AlreadyHung(row.Path)) { localProblem = app.L(ImageArrivalNotices.Existing); ShowState(); return; }
        hanging = true; pendingHang = HangCoreAsync(row.Path); UpdateControls();
    }
    private async Task HangCoreAsync(string path)
    {
        try { await services.Hang(path, this, message => { if (!isFinishing && !closed) { localProblem = LibraryJson.Visible(message); ShowState(); } }); }
        catch (Exception error) { RecordProblem(error); }
        finally { hanging = false; if (!isFinishing && !closed) ShowState(); }
    }
    private void DismissSelected()
    { if (!isFinishing && !closed && list.SelectedItem is Row row && session is { } active) { active.Dismiss(row.Id); ApplySnapshot(active); } }
    private void ClearCards()
    { if (!isFinishing && !closed && session is { } active) { active.ClearCards(); ApplySnapshot(active); } }

    private void SetProblem(Exception error) { RecordProblem(error); if (!closed && !isFinishing) ShowState(); }
    private void RecordProblem(Exception error)
    {
        string text = error is AggregateException aggregate ? string.Join("\n", aggregate.Flatten().InnerExceptions.Select(item => app.L(item.Message))) : app.L(error.Message);
        RecordVisibleProblem(LibraryJson.Visible(text));
    }
    private void RecordVisibleProblem(string text) =>
        localProblem = (isFinishing || stopping) && localProblem.Length > 0 ? localProblem + "\n\n" + text : text;
    private void ShowState()
    {
        if (closed) return;
        var snapshot = session?.Snapshot;
        string state = isFinishing || stopping || snapshot?.Status == ArrivalStatus.Stopping ? "Stopping image arrival monitoring…" : starting ? "Starting image arrival monitoring…" :
            snapshot?.Status == ArrivalStatus.Running ? "Monitoring future image-path notifications." : "Image arrival monitoring is stopped.";
        status.Text = app.L(state) + (snapshot is { StopReason: not ArrivalStopReason.None and not ArrivalStopReason.User } ? "\n" + app.L(Reason(snapshot.StopReason)) : "") +
            (localProblem.Length == 0 ? "" : "\n" + localProblem);
        counts.Text = snapshot is null ? app.L("No arrival session has started.") : string.Format(CultureInfo.CurrentCulture,
            app.L("Cards: {0}/12 · queued: {1}/64 · notifications: {2}/512 · read attempts: {3} · input-size budget used (bytes): {4:N0} · failed files: {5}"),
            snapshot.Cards.Count, snapshot.QueueCount, snapshot.EligibleNotifications, snapshot.Attempts, snapshot.ReservedBytes, snapshot.FailedFiles);
        if (chosenDirectory is null) directory.Text = app.L("No folder selected.");
        UpdateControls();
    }
    private void UpdateControls()
    {
        bool available = !isFinishing && !closed;
        choose.IsEnabled = CanChoose(); start.IsEnabled = CanStart;
        stop.IsEnabled = available && !stopping && (starting || session?.Snapshot.Status == ArrivalStatus.Running);
        preview.IsEnabled = available && list.SelectedItem is Row { Thumbnail: not null };
        hang.IsEnabled = available && !starting && !stopping && !hanging && services.Enabled() && !services.Busy() && list.SelectedItem is Row;
        dismiss.IsEnabled = available && list.SelectedItem is Row; clear.IsEnabled = available && list.Items.Count > 0;
    }
    private static string Reason(ArrivalStopReason reason) => reason switch
    {
        ArrivalStopReason.CardLimit => "The card limit was reached. Review or dismiss cards before explicitly starting a new session.",
        ArrivalStopReason.QueueLimit => "The queued-path limit was reached. Some notifications were not processed.",
        ArrivalStopReason.NotificationLimit => "The notification limit was reached. Some notifications were not processed.",
        ArrivalStopReason.ByteLimit => "The cumulative input-size budget was reached.",
        ArrivalStopReason.WatcherError => ImageArrivalRules.WatchFailed,
        ArrivalStopReason.ReadFailure => "Image reading stopped monitoring. Review the recorded problems.",
        ArrivalStopReason.CleanupError => ImageArrivalRules.CleanupFailed,
        ArrivalStopReason.FeatureDisabled => "Clothesline was turned off.",
        ArrivalStopReason.Closed => "The arrival window is closing.",
        _ => "Image arrival monitoring was cancelled."
    };
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || finished) return;
        e.Cancel = true; if (closeRequested) return; closeRequested = true; _ = CloseAfterFinish();
    }
    private async Task CloseAfterFinish()
    {
        await FinishAsync();
        // Even a completely idle session can finish synchronously while WPF is
        // still raising Closing. Defer the final Close until that event returns.
        if (!closed) _ = Dispatcher.BeginInvoke(new Action(() => { if (!closed) { allowClose = true; Close(); } }));
    }
    internal Task FinishAsync() => finishing ??= FinishCoreAsync();
    private async Task FinishCoreAsync()
    {
        isFinishing = true; TryCancel();
        try { services.CancelOwnedHang(this); } catch (Exception error) { RecordProblem(error); }
        try { previewWindow?.Close(); } catch (Exception error) { RecordProblem(error); }
        try { if (!closed) ShowState(); } catch (Exception error) { RecordProblem(error); }
        await AwaitAndRecord(pendingStart); await AwaitAndRecord(pendingStop);
        if (session is { } active)
        {
            try { Detach(active); } catch (Exception error) { RecordProblem(error); }
            await JoinSession(active, ArrivalStopReason.Closed);
        }
        await AwaitAndRecord(pendingHang);
        DisposeCancellation(); finished = true;
        lock (notificationGate) { queuedSource = null; }
        try { if (!closed) ShowState(); } catch (Exception error) { RecordProblem(error); }
    }
    private sealed record Row(Guid Id, string Path, string VisiblePath, BitmapSource? Thumbnail, string Kind)
    { public override string ToString() => VisiblePath + " · " + Kind; }
}

internal sealed class ImageArrivalPreviewWindow : Window
{
    internal ImageArrivalPreviewWindow(App app, string visiblePath, BitmapSource thumbnail)
    {
        Title = app.L("Stored Arrival Thumbnail"); Width = 620; Height = 460; MinWidth = 360; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) }; var footer = new StackPanel();
        footer.Children.Add(UI.Text(visiblePath, 12));
        footer.Children.Add(UI.Text(app.L("Only the stored thumbnail is shown. This preview does not read the current file."), 12));
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new Image { Source = thumbnail, Stretch = Stretch.Uniform }); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
