using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class CloseWindowsDialog : Window
{
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationTokenRegistration registration;
    private Task pending = Task.CompletedTask;
    private Task? finishing;
    private Window? confirmation;
    private bool closed, finished;
    internal CloseWindowsDialog(App app, CloseWindowsSession session, CancellationToken operationToken)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        Title = app.L(CloseWindows.Title); Width = 900; Height = 800; MinWidth = 500; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var status = UI.Text(""); footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        var request = UI.Button(app.L("Review Selected Window…"), () => { }); request.IsEnabled = false; actions.Children.Add(request);
        var all = UI.Button(app.L("Review All Listed Windows of This App…"), () => { }); all.IsEnabled = false; actions.Children.Add(all);
        var close = UI.Button(app.L("Close"), Close); close.IsCancel = true; actions.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        body.Children.Add(UI.Text(session.RecordedUtc.ToString("O", CultureInfo.InvariantCulture), 12));
        body.Children.Add(UI.Text(string.Format(app.L("Eligible windows: {0}. Inspected entries omitted: {1}."), session.Windows.Count, session.Omitted), 16));
        if (session.Limited || session.TimeLimited || session.Unavailable) body.Children.Add(UI.Text(app.L(CloseWindows.Partial)));
        foreach (string notice in new[] { CloseWindows.Scope, CloseWindows.Filter, CloseWindows.Bounds, CloseWindows.IdentityLimit, CloseWindows.RequestLimit })
            body.Children.Add(UI.Text(app.L(notice), 12));
        var list = new ListView { Height = 240, SelectionMode = SelectionMode.Single, SelectedIndex = -1 };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var view = new GridView();
        foreach (var column in new[] { ("Window Title", "Title", 370d), ("Executable Name", "Executable", 180d), ("Process Identifiers", "Pid", 100d) })
            view.Columns.Add(new GridViewColumn { Header = app.L(column.Item1), DisplayMemberBinding = new Binding(column.Item2), Width = column.Item3 });
        list.View = view;
        list.ItemsSource = session.Windows.Select((window, index) => new Row(index, LibraryJson.Visible(window.Title),
            LibraryJson.Visible(System.IO.Path.GetFileName(window.ExecutablePath)), window.ProcessId.ToString(CultureInfo.InvariantCulture))).ToArray();
        AutomationProperties.SetName(list, app.L("Eligible Desktop Windows")); body.Children.Add(list);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130,
            MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(details, app.L("Selected Window Details")); body.Children.Add(details);
        // Filled after a request for all windows of an app: each window's result.
        var outcomes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(outcomes, app.L("Close Request Results")); body.Children.Add(outcomes);
        bool requesting = false; // `pending` is not complete yet in its own finally
        void UpdateSelection()
        {
            var row = list.SelectedItem as Row;
            details.Text = row is null ? app.L("Select a window to review its full title and executable path.") : CloseWindows.Details(session.Windows[row.Index], app.L);
            request.IsEnabled = !closed && !requesting && row is not null && session.CanRequest(row.Index);
            // Only when the app has more than one requestable window; one window is the single review.
            all.IsEnabled = !closed && !requesting && row is not null && session.SameApp(row.Index).Count > 1;
        }
        list.SelectionChanged += (_, _) => UpdateSelection(); UpdateSelection();
        request.Click += (_, _) =>
        {
            if (closed || !pending.IsCompleted || cancellation.IsCancellationRequested || list.SelectedItem is not Row row || !session.CanRequest(row.Index)) return;
            var confirm = new CloseWindowsConfirmDialog(app, session.Windows[row.Index]) { Owner = this }; confirmation = confirm;
            bool accepted;
            try { accepted = confirm.ShowDialog() == true; }
            finally { confirmation = null; }
            if (!accepted || closed || cancellation.IsCancellationRequested) return;
            requesting = true; request.IsEnabled = false; all.IsEnabled = false; list.IsEnabled = false;
            pending = Request(row.Index);
        };
        all.Click += (_, _) =>
        {
            if (closed || !pending.IsCompleted || cancellation.IsCancellationRequested || list.SelectedItem is not Row row) return;
            var indices = session.SameApp(row.Index);
            if (indices.Count < 2) return;
            var confirm = new CloseAppWindowsConfirmDialog(app, indices.Select(index => session.Windows[index]).ToArray()) { Owner = this }; confirmation = confirm;
            bool accepted;
            try { accepted = confirm.ShowDialog() == true; }
            finally { confirmation = null; }
            if (!accepted || closed || cancellation.IsCancellationRequested) return;
            requesting = true; request.IsEnabled = false; all.IsEnabled = false; list.IsEnabled = false;
            pending = RequestAll(indices);
        };
        async Task Request(int index)
        {
            status.Text = app.L("Requesting the selected window's Close action…");
            outcomes.Visibility = Visibility.Collapsed; outcomes.Text = "";
            try
            {
                var result = await CloseWindowsReader.RequestAsync(session, index, cancellation.Token);
                if (!closed) status.Text = app.L(result switch { CloseWindowsResult.Processed => CloseWindows.Processed, CloseWindowsResult.Changed => CloseWindows.Changed, _ => CloseWindows.Unknown });
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception) { if (!closed) status.Text = app.L(CloseWindows.Unknown); }
            finally
            {
                requesting = false;
                if (!closed) { list.IsEnabled = true; UpdateSelection(); }
            }
        }
        async Task RequestAll(IReadOnlyList<int> indices)
        {
            status.Text = string.Format(app.L("Requesting the Close action of {0} windows, one after another…"), indices.Count);
            outcomes.Visibility = Visibility.Collapsed; outcomes.Text = "";
            try
            {
                var results = await CloseWindowsReader.RequestEachAsync(session, indices, cancellation.Token);
                if (!closed)
                {
                    status.Text = CloseWindows.Summary(results, app.L);
                    outcomes.Text = CloseWindows.ResultLines(indices.Select(index => session.Windows[index]).ToArray(), results, app.L);
                    outcomes.Visibility = Visibility.Visible;
                }
            }
            catch (Exception) { if (!closed) status.Text = app.L(CloseWindows.Unknown); }
            finally
            {
                requesting = false;
                if (!closed) { list.IsEnabled = true; UpdateSelection(); }
            }
        }
        Closed += (_, _) => { if (closed || finished) return; closed = true; cancellation.Cancel(); confirmation?.Close(); request.IsEnabled = false; all.IsEnabled = false; };
        registration = operationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Cancellation may already be queued when FinishAsync disposes its
            // source (including a dialog that was never shown).
            if (finished || closed) return;
            confirmation?.Close(); if (!closed && !finished) Close();
        })));
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
    internal Task FinishAsync() => finishing ??= FinishCoreAsync();
    private async Task FinishCoreAsync()
    {
        cancellation.Cancel();
        try { await pending; }
        finally { finished = true; closed = true; registration.Dispose(); cancellation.Dispose(); }
    }
    private sealed record Row(int Index, string Title, string Executable, string Pid)
    { public override string ToString() => Title + " · " + Executable + " · " + Pid; }
}

internal sealed class CloseWindowsConfirmDialog : Window
{
    internal CloseWindowsConfirmDialog(App app, CloseWindowInfo window)
    {
        Title = app.L("Confirm Window Close Request"); Width = 640; Height = 500; MinWidth = 380; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; cancel.IsDefault = true; buttons.Children.Add(cancel);
        // Enter defaults to Cancel. The destructive-in-the-target normal Close
        // request always requires its own explicitly selected button.
        buttons.Children.Add(UI.Button(app.L("Request Normal Close"), () => DialogResult = true));
        var body = new StackPanel(); body.Children.Add(UI.Text(app.L(CloseWindows.Confirmation)));
        var details = new TextBox { Text = CloseWindows.Details(window, app.L), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 150 };
        AutomationProperties.SetName(details, app.L("Selected Window Details")); body.Children.Add(details);
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

/// The confirmation for requesting every listed window of one app to close, one after another.
internal sealed class CloseAppWindowsConfirmDialog : Window
{
    internal CloseAppWindowsConfirmDialog(App app, IReadOnlyList<CloseWindowInfo> windows)
    {
        Title = app.L("Confirm Close Requests for One App"); Width = 680; Height = 560; MinWidth = 380; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; cancel.IsDefault = true; buttons.Children.Add(cancel);
        // As for one window: Enter cancels, and the requests need their own button.
        buttons.Children.Add(UI.Button(app.L("Request Normal Close for All"), () => DialogResult = true));
        var body = new StackPanel(); body.Children.Add(UI.Text(app.L(CloseWindows.AppConfirmation)));
        var details = new TextBox { Text = CloseWindows.AppDetails(windows, app.L), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 150, MaxHeight = 320 };
        AutomationProperties.SetName(details, app.L("Selected Window Details")); body.Children.Add(details);
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
