using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class MoveFilesNotices
{
    internal const string Scope = "Move the listed original files to the displayed destinations. This is a move of the same file objects, not a copy. A move requires an unoccupied destination; files are never overwritten or automatically renamed to avoid a conflict.";
    internal const string Limits = "Choose 1–100 ordinary files on the same NTFS volume as the destination. When every selected file is on another drive, Swivel offers to copy them instead and keeps the originals; a mix of drives is refused. Hard links, read-only files, cloud placeholders and linked paths are not supported. Files already in the destination folder stay in place and are marked as unchanged.";
    internal const string Streams = "File contents, download-origin markings and other attached file data stay with each file. Contents are not scanned or copied. Same-volume moves retain the original permissions; do not expect the files to inherit the destination folder's permissions.";
    internal const string Recovery = "If a move fails or is cancelled, Swivel attempts to restore earlier moves without replacing occupied names. Recovery can be partial: review the exact current paths in the results. A crash or power loss can leave files at their new locations; there is no automatic crash recovery.";
    internal const string History = "Undo holds up to 20 batches in this session only. Quitting loses that history. Undo rechecks file and directory identities and recorded metadata, and refuses detected changes or occupied original paths. These checks are not a content snapshot and may miss changes to contents or attached file data. Restoration is not guaranteed.";
    internal const string UndoScope = "Review the current and original paths before undoing this move. Only the listed files are requested to return. Changed file or directory identities and occupied original paths are refused; no file is replaced.";
    internal const string Selection = "Select a row to inspect both complete paths. The operation applies to every listed file, regardless of the selected row.";
    internal const string Results = "These are the paths recorded when the operation ended. Files may subsequently change outside Swivel. Review every path before trying session Undo; this result does not perform another move or scan.";
}

// Display-only immutable snapshots. Native leases and execution remain in the
// caller so closing or confirming this dialog never performs filesystem I/O.
internal sealed class MoveFilesDialog : Window
{
    internal IReadOnlyList<MovePair> Plan { get; }

    internal MoveFilesDialog(App app, IReadOnlyList<MovePair> plan, bool undo = false)
    {
        Plan = MoveFilesLayout.Snapshot(plan);
        Title = app.L(undo ? "Undo File Move" : "Review File Move");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; cancel.IsDefault = true; buttons.Children.Add(cancel);
        var apply = UI.Button(app.L(undo ? "Undo File Move" : "Move Files"), () => DialogResult = true);
        apply.IsEnabled = Plan.Any(pair => pair.Changed); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L(undo ? MoveFilesNotices.UndoScope : MoveFilesNotices.Scope)));
        body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,
            app.L("Files listed: {0}. To move: {1}. Unchanged: {2}."), Plan.Count, Plan.Count(pair => pair.Changed), Plan.Count(pair => !pair.Changed)), 18));
        body.Children.Add(UI.Text(app.L(MoveFilesNotices.Selection), 12));
        MoveFilesLayout.Paths(app, body, Plan, undo ? "Current path" : "Source path",
            undo ? "Original path to restore" : "Destination path", false);
        foreach (string notice in new[] { MoveFilesNotices.Limits, MoveFilesNotices.Streams, MoveFilesNotices.Recovery, MoveFilesNotices.History })
            body.Children.Add(UI.Text(app.L(notice), 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => cancel.Focus();
    }
}

internal sealed class MoveFilesResultDialog : Window
{
    internal MoveFilesResultDialog(App app, MoveOutcome outcome, bool undo = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var paths = MoveFilesLayout.Snapshot(outcome.Locations.Select(item => new MovePair(item.Original, item.Current)).ToArray());
        Exception? error = outcome.Error; bool cancelled = outcome.Cancelled;
        Title = app.L("File Move Results"); MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true; close.IsDefault = true;
        buttons.Children.Add(close); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        int changed = paths.Count(path => !StringComparer.Ordinal.Equals(path.Source, path.Destination));
        string summary = error is null && !cancelled
            ? changed == 0 ? "No files moved." : undo ? "File move undone." : "Files moved. Session Undo is available."
            : changed > 0 ? "The operation stopped. Some files remain at changed paths; review every current path."
            : "The operation stopped. All recorded paths were restored to their state before this operation.";
        body.Children.Add(UI.Text(app.L(summary), 18));
        if (cancelled) body.Children.Add(UI.Text(app.L("The operation was cancelled.")));
        if (error is not null)
        {
            var problems = MoveFilesLayout.Problems(error);
            body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,
                app.L("The following problems were reported ({0})."), problems.Count), 14));
            var problem = MoveFilesLayout.Details(app, "Reported problem", 100);
            problem.Text = MoveFilesLayout.ProblemText(problems, app.L); body.Children.Add(problem);
        }
        body.Children.Add(UI.Text(app.L(MoveFilesNotices.Results), 12));
        if (outcome.Locations.Any(location => location.Stamp is null))
            body.Children.Add(UI.Text(app.L("Some file identities could not be verified after the operation. Their paths remain listed, but session Undo may refuse these entries."), 12));
        body.Children.Add(UI.Text(app.L("Select a row to inspect the complete recorded paths."), 12));
        MoveFilesLayout.Paths(app, body, paths, "Path before this operation", "Recorded current path", true);
        body.Children.Add(UI.Text(app.L(MoveFilesNotices.History), 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => close.Focus();
    }
}

internal static class MoveFilesLayout
{
    internal static IReadOnlyList<Exception> Problems(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // Native transactions already bound the number of files and failures.
        // Keep every diagnostic, even if additional cleanup failures were joined.
        if (error is AggregateException aggregate && aggregate.Flatten().InnerExceptions is { Count: > 0 } inner)
            return inner;
        return Array.AsReadOnly(new[] { error });
    }

    internal static string ProblemText(IReadOnlyList<Exception> problems, Func<string, string> localize) =>
        string.Join("\n\n", problems.Select((problem, index) =>
            (problems.Count > 1 ? (index + 1).ToString(CultureInfo.CurrentCulture) + ". " : "") +
            LibraryJson.Visible(localize(problem.Message))));

    internal static IReadOnlyList<MovePair> Snapshot(IReadOnlyList<MovePair> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 100) throw new UserError("Choose up to 100 files at a time.");
        var snapshot = paths.Select(pair => pair is null ? throw new ArgumentException("A file path pair is required.")
            : new MovePair(WindowsPathPolicy.Validate(pair.Source), WindowsPathPolicy.Validate(pair.Destination))).ToArray();
        return Array.AsReadOnly(snapshot);
    }

    internal static void Configure(Window window)
    {
        window.Width = 860; window.Height = 740; window.MinWidth = 420; window.MinHeight = 330;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.SourceInitialized += (_, _) => WebRouteLayout.Fit(window, window.Owner ?? window);
        window.DpiChanged += (_, _) => WebRouteLayout.Fit(window, window);
    }

    internal static TextBox Details(App app, string label, double height) => Named(new TextBox
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = height,
        Margin = new Thickness(0, 3, 0, 12)
    }, app.L(label));

    private static T Named<T>(T element, string name) where T : DependencyObject
    { AutomationProperties.SetName(element, name); return element; }

    internal static void Paths(App app, Panel body, IReadOnlyList<MovePair> paths, string sourceLabel, string destinationLabel, bool result)
    {
        // Keep the list bounded inside the scrollable page so it remains useful
        // on small work areas and can virtualize independently of the page.
        var list = Named(new ListView { Height = 235, SelectionMode = SelectionMode.Single,
            ItemsSource = paths.Select((pair, index) => new Row((index + 1).ToString(CultureInfo.CurrentCulture),
                LibraryJson.Visible(pair.Source), LibraryJson.Visible(pair.Destination),
                app.L(pair.Changed ? result ? "Changed path" : "Will move" : "Unchanged"))).ToArray() }, app.L("File Move Paths"));
        var columns = new GridView();
        columns.Columns.Add(new GridViewColumn { Header = app.L("No."), DisplayMemberBinding = new Binding(nameof(Row.Number)), Width = 50 });
        columns.Columns.Add(new GridViewColumn { Header = app.L("Status"), DisplayMemberBinding = new Binding(nameof(Row.Status)), Width = 130 });
        columns.Columns.Add(new GridViewColumn { Header = app.L(sourceLabel), DisplayMemberBinding = new Binding(nameof(Row.Source)), Width = 335 });
        columns.Columns.Add(new GridViewColumn { Header = app.L(destinationLabel), DisplayMemberBinding = new Binding(nameof(Row.Destination)), Width = 335 });
        list.View = columns; VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); body.Children.Add(list);
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        var details = Details(app, "Selected File Paths", 132); body.Children.Add(details);
        list.SelectionChanged += (_, _) => details.Text = list.SelectedItem is Row row
            ? app.L(sourceLabel) + ":\n" + row.Source + "\n\n" + app.L(destinationLabel) + ":\n" + row.Destination + "\n\n" + app.L("Status") + ": " + row.Status : "";
    }

    private sealed record Row(string Number, string Source, string Destination, string Status)
    {
        public override string ToString() => Number + " · " + Source + " → " + Destination + " · " + Status;
    }
}
