using System;
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
        void UpdateSelection()
        {
            var row = list.SelectedItem as Row;
            details.Text = row is null ? app.L("Select a window to review its full title and executable path.") : CloseWindows.Details(session.Windows[row.Index], app.L);
            request.IsEnabled = !closed && pending.IsCompleted && row is not null && session.CanRequest(row.Index);
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
            request.IsEnabled = false; list.IsEnabled = false;
            pending = Request(row.Index);
        };
        async Task Request(int index)
        {
            status.Text = app.L("Requesting the selected window's Close action…");
            try
            {
                var result = await CloseWindowsReader.RequestAsync(session, index, cancellation.Token);
                if (!closed) status.Text = app.L(result switch { CloseWindowsResult.Processed => CloseWindows.Processed, CloseWindowsResult.Changed => CloseWindows.Changed, _ => CloseWindows.Unknown });
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception) { if (!closed) status.Text = app.L(CloseWindows.Unknown); }
            finally
            {
                if (!closed) { list.IsEnabled = true; request.IsEnabled = list.SelectedItem is Row row && session.CanRequest(row.Index); }
            }
        }
        Closed += (_, _) => { if (closed || finished) return; closed = true; cancellation.Cancel(); confirmation?.Close(); request.IsEnabled = false; };
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
