using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ProjectFolderNotices
{
    internal const string Limits = "Read-only directory metadata in the chosen folder and 4 levels below, up to 20,000 entries and about 30 seconds. System items, links, cloud placeholders and unavailable subtrees are skipped. The time limit is soft; a blocked system call may return later.";
    internal const string Heuristic = "Candidates are identified by folder names and nearby project file names, ignoring letter case. File contents are not read or validated. These folders may contain user files; matching a name does not mean a folder is safe to delete.";
    internal const string Boundary = "The chosen folder itself is not classified. Choose its project folder to inspect a build folder; parents outside the chosen folder are not inspected.";
    internal const string Counting = "Only outermost matching folders are listed to avoid nested totals. Partial counts are not the full folder size. Logical bytes are not physical or reclaimable space; hard links may be counted more than once, and alternate streams and filesystem overhead are excluded. Names and sizes may change during the scan.";
    internal const string Privacy = "Copy Report includes the chosen folder, all candidate details and recorded issues. Copy Selected Folder Details includes that candidate and the chosen folder. Names and paths are visible. Nothing is saved or sent automatically.";
    internal const string Recycle = "Move Selected to Recycle Bin… first lists the selected folders with their full paths. Each folder is then checked again: it must still be a plain local folder (not a link, system or cloud item), and every listed project file must still be there. Its contents are not compared with the scan, so files added since then move too. Only fixed drives with a Recycle Bin are used; if a folder is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move restores the last moved folders while this window is open, unless something now exists at their place; afterwards, restore them from the Recycle Bin.";
}

internal sealed class ProjectFoldersDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult scan;
    private readonly ProjectFolderResult result;
    private readonly ListView folders = NativeList(240);
    private readonly TextBlock details;
    private readonly Button copySelected, recycle, undo;
    private readonly TextBlock moveStatus;
    // Folders gone from their place in this window (moved, or removed by Windows), how many
    // reached the Recycle Bin, and where it keeps the last batch (for Undo).
    private readonly HashSet<ProjectFolder> moved = new(), removed = new();
    private int recycled;
    private List<(ProjectFolder Folder, string Stored)> lastMoved = new();
    private bool busy, closeRequested;
    internal int Moved => recycled;

    internal string CurrentReport
    {
        get
        {
            var text = ReportHeader();
            if (result.Folders.Count == 0) text.AppendLine(app.L("No matching project folders in the inspected metadata. Skipped or unexamined items may contain other matches."));
            foreach (var folder in result.Folders.Where(folder => !moved.Contains(folder))) AppendFolder(text, folder);
            text.AppendLine(app.L("Skipped or Unclassified"));
            if (result.Issues.Count == 0) text.AppendLine(app.L("No recorded issues. Unrecognized folder names are not classified."));
            foreach (var issue in result.Issues) text.AppendLine(Visible(issue.RelativePath) + " · " + app.L(issue.Reason));
            if (recycled > 0)
            {
                text.AppendLine(string.Format(app.L("Moved to the Recycle Bin in this window: {0}"), recycled));
                foreach (var folder in result.Folders.Where(folder => moved.Contains(folder) && !removed.Contains(folder))) text.AppendLine("  " + Visible(folder.RelativePath));
            }
            if (removed.Count > 0)
            {
                text.AppendLine(string.Format(app.L("Removed without the Recycle Bin, or not found there, in this window: {0}"), removed.Count));
                foreach (var folder in result.Folders.Where(removed.Contains)) text.AppendLine("  " + Visible(folder.RelativePath));
            }
            return text.ToString();
        }
    }

    public ProjectFoldersDialog(App app, string path, FolderScanResult scan, ProjectFolderResult result)
    {
        this.app = app; this.path = path; this.scan = scan; this.result = result;
        Title = app.L("Project Build Folders"); Width = 940; Height = 850; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel();
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected Folder Details"), () =>
        {
            if (folders.SelectedItem is not FolderRow row) return;
            app.Try(() => { var text = ReportHeader(); AppendFolder(text, row.Folder); Clipboard.SetText(text.ToString()); });
        });
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        recycle = UI.Button(app.L("Move Selected to Recycle Bin…"), () => _ = RecycleAsync()); recycle.IsEnabled = false; footer.Children.Add(recycle);
        undo = UI.Button(app.L("Undo Move"), () => _ = UndoAsync()); undo.IsEnabled = false; footer.Children.Add(undo);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        // A Recycle Bin move or restore cannot be stopped halfway; the window stays until it ends.
        // Quit or turning Doctor off still closes it once the batch ends.
        Closing += (_, e) => { if (busy) { closeRequested = true; e.Cancel = true; } };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var tabs = new TabControl();
        var candidates = new StackPanel { Margin = new Thickness(12) };
        candidates.Children.Add(UI.Text(Visible(path), 12));
        candidates.Children.Add(UI.Text(Summary(), 14));
        candidates.Children.Add(UI.Text(ScopeStatus(), 12));
        candidates.Children.Add(UI.Text(app.L(ProjectFolderNotices.Heuristic), 12));
        candidates.Children.Add(UI.Text(app.L(ProjectFolderNotices.Boundary), 12));
        candidates.Children.Add(UI.Text(app.L(ProjectFolderNotices.Counting), 12));
        candidates.Children.Add(UI.Text(app.L(ProjectFolderNotices.Privacy), 12));
        candidates.Children.Add(UI.Text(app.L(ProjectFolderNotices.Recycle), 12));
        if (result.Folders.Count == 0) candidates.Children.Add(UI.Text(app.L("No matching project folders in the inspected metadata. Skipped or unexamined items may contain other matches.")));
        folders.View = Columns(("Relative Path", "Path", 330), ("Kind", "Kind", 185), ("Counted Logical Bytes", "Size", 165), ("Status", "Status", 200));
        folders.SelectionMode = SelectionMode.Extended; Refresh();
        AutomationProperties.SetName(folders, app.L("Candidate Folders")); candidates.Children.Add(folders);
        moveStatus = UI.Text("", 13); AutomationProperties.SetName(moveStatus, app.L("Recycle Bin")); candidates.Children.Add(moveStatus);
        details = UI.Text(app.L("Select a candidate to see its project markers and counted size."), 13);
        AutomationProperties.SetName(details, app.L("Selected Folder Details")); candidates.Children.Add(details);
        folders.SelectionChanged += (_, _) => SelectFolder();
        tabs.Items.Add(new TabItem { Header = app.L("Candidate Folders"), Content = UI.Scroll(candidates), Padding = new Thickness(12, 7, 12, 7) });

        var skipped = new StackPanel { Margin = new Thickness(12) };
        skipped.Children.Add(UI.Text(ScopeStatus(), 12));
        skipped.Children.Add(UI.Text(app.L(ProjectFolderNotices.Limits), 12));
        skipped.Children.Add(UI.Text(app.L("Recorded issues include skipped items and known output folder names without clear project markers. Other unrecognized names are not classified."), 12));
        skipped.Children.Add(UI.Text(string.Format(app.L("Recorded issues: {0}"), result.Issues.Count), 14));
        if (result.Issues.Count == 0) skipped.Children.Add(UI.Text(app.L("No recorded issues. Unrecognized folder names are not classified.")));
        var issues = NativeList(390); issues.View = Columns(("Relative Path", "Path", 430), ("Reason", "Reason", 350));
        issues.ItemsSource = result.Issues.Select(issue => new IssueRow(Visible(issue.RelativePath), app.L(issue.Reason))).ToArray();
        AutomationProperties.SetName(issues, app.L("Skipped or Unclassified")); skipped.Children.Add(issues);
        tabs.Items.Add(new TabItem { Header = app.L("Skipped or Unclassified"), Content = UI.Scroll(skipped), Padding = new Thickness(12, 7, 12, 7) });
        root.Children.Add(tabs); Content = root;
        if (result.Folders.Count > 0) folders.SelectedIndex = 0;
    }

    private void Refresh() => folders.ItemsSource = result.Folders.Where(folder => !moved.Contains(folder))
        .Select(folder => new FolderRow(folder, Visible(folder.RelativePath), app.L(folder.Kind), Size(folder), Status(folder))).ToArray();
    private string Absolute(string relative) => System.IO.Path.Combine(path, relative);

    private async Task RecycleAsync()
    {
        var chosen = folders.SelectedItems.OfType<FolderRow>().Select(row => row.Folder).ToArray();
        if (busy || chosen.Length == 0) return;
        var list = new StringBuilder();
        foreach (var folder in chosen.Take(20)) list.AppendLine(Visible(Absolute(folder.RelativePath)) + " · " + Size(folder) + (folder.Partial ? " · " + app.L("Partial; full size unknown") : ""));
        if (chosen.Length > 20) list.AppendLine(string.Format(app.L("and {0} more"), chosen.Length - 20));
        string question = (chosen.Length == 1 ? app.L("Move 1 folder to the Recycle Bin?") : string.Format(app.L("Move {0} folders to the Recycle Bin?"), chosen.Length)) + "\n\n" + list + "\n" + app.L("These folders may contain your own files. Check each one before moving it.");
        if (MessageBox.Show(this, question, app.L("Move Selected to Recycle Bin…"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK || closeRequested || !IsVisible) return;
        var done = new List<(ProjectFolder Folder, string Stored)>();
        var gone = new List<ProjectFolder>(); // removed, or moved where Undo cannot find it
        var problems = new List<string>();
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        string? failure = await Work(() =>
        {
            foreach (var folder in chosen)
            {
                string? problem = null;
                try { done.Add((folder, NativeFiles.RecycleProjectFolder(Absolute(folder.RelativePath), folder.Evidence.Distinct(StringComparer.Ordinal).Select(Absolute), owner))); }
                catch (UserError error)
                {
                    if (error.Message is NativeFiles.RecycledUntracked or NativeFiles.RemovedNotRecycled) gone.Add(folder);
                    problem = error.Message;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException) { problem = NativeFiles.NotRecycled; }
                if (problem is not null) problems.Add(Visible(folder.RelativePath) + " · " + app.L(problem));
            }
        });
        foreach (var item in done) moved.Add(item.Folder);
        foreach (var folder in gone) { moved.Add(folder); removed.Add(folder); }
        recycled += done.Count;
        // Undo always means the batch just run, even when none of it can be restored.
        if (done.Count + gone.Count > 0) lastMoved = done;
        Refresh(); UpdateButtons();
        if (failure is not null) problems.Add(failure);
        Report(string.Format(app.L("Moved to the Recycle Bin: {0} of {1}"), done.Count, chosen.Length), problems);
        CloseIfRequested();
    }

    private async Task UndoAsync()
    {
        if (busy || lastMoved.Count == 0) return;
        var batch = lastMoved; lastMoved = new();
        var restored = new List<ProjectFolder>();
        var problems = new List<string>();
        string? failure = await Work(() =>
        {
            foreach (var (folder, stored) in batch)
            {
                try { NativeFiles.RestoreProjectFolder(Absolute(folder.RelativePath), stored); restored.Add(folder); }
                catch (UserError error) { problems.Add(Visible(folder.RelativePath) + " · " + app.L(error.Message)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException or System.Reflection.TargetInvocationException or System.Runtime.InteropServices.COMException)
                { problems.Add(Visible(folder.RelativePath) + " · " + app.L(NativeFiles.NotRestored)); }
            }
        });
        foreach (var folder in restored) moved.Remove(folder);
        recycled -= restored.Count;
        // Folders that were not restored stay in the batch, so Undo can be tried again.
        lastMoved = batch.Where(item => !restored.Contains(item.Folder)).ToList();
        if (failure is not null) problems.Add(failure);
        Refresh(); UpdateButtons();
        Report(string.Format(app.L("Restored from the Recycle Bin: {0} of {1}"), restored.Count, batch.Count), problems);
        CloseIfRequested();
    }

    // Runs one Recycle Bin batch on its own STA thread (the shell's Restore needs one) while the
    // window waits with its buttons off.
    private async Task<string?> Work(Action action)
    {
        busy = true; UpdateButtons();
        moveStatus.Text = app.L("Working with the Recycle Bin…");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } })
        { IsBackground = true, Name = "Swivel Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task; return null; }
        catch (Exception error) { return app.L(error is UserError ? error.Message : "Windows stopped the Recycle Bin work. Some folders may not have been handled."); }
        finally { busy = false; UpdateButtons(); }
    }
    // After the caller's bookkeeping, so the app reads the final counts once the window closes.
    private void CloseIfRequested() { if (closeRequested) Close(); }
    private void Report(string summary, List<string> problems)
    {
        var text = new StringBuilder(summary);
        foreach (string problem in problems) text.AppendLine().Append(problem);
        moveStatus.Text = text.ToString();
    }
    private void UpdateButtons()
    {
        bool idle = !busy && !closeRequested;
        recycle.IsEnabled = idle && folders.SelectedItems.Count > 0;
        undo.IsEnabled = idle && lastMoved.Count > 0;
        copySelected.IsEnabled = idle && folders.SelectedItems.Count == 1;
    }

    private string Summary() => string.Format(app.L("Candidate folders: {0} · partial counts: {1}"), result.Folders.Count, result.Folders.Count(folder => folder.Partial));
    private string ScopeStatus()
    {
        var parts = new List<string> { string.Format(app.L("Metadata entries: {0} · recorded skipped items/subtrees: {1}"), scan.Entries, scan.Root.Skipped) };
        if (scan.Root.Incomplete) parts.Add(app.L("Folder metadata scan is partial."));
        if (scan.EntryLimit) parts.Add(app.L("Entry limit reached"));
        if (scan.TimeLimit) parts.Add(app.L("Folder scan time limit reached"));
        return string.Join(" · ", parts);
    }
    private StringBuilder ReportHeader()
    {
        var text = new StringBuilder();
        text.AppendLine(app.L("Project Build Folders")); text.AppendLine(Visible(path));
        text.AppendLine(Summary()); text.AppendLine(ScopeStatus());
        text.AppendLine(app.L(ProjectFolderNotices.Limits)); text.AppendLine(app.L(ProjectFolderNotices.Heuristic));
        text.AppendLine(app.L(ProjectFolderNotices.Boundary));
        text.AppendLine(app.L(ProjectFolderNotices.Counting));
        return text;
    }
    private void AppendFolder(StringBuilder text, ProjectFolder folder)
    {
        text.AppendLine(app.L("Relative Path") + ": " + Visible(folder.RelativePath));
        text.AppendLine(app.L("Project Folder") + ": " + Visible(folder.ProjectPath));
        text.AppendLine(app.L("Kind") + ": " + app.L(folder.Kind));
        text.AppendLine(app.L("Counted Logical Bytes") + ": " + Size(folder));
        text.AppendLine(app.L("Counted Files") + ": " + folder.Files.ToString("N0", CultureInfo.CurrentCulture));
        text.AppendLine(app.L("Status") + ": " + Status(folder));
        text.AppendLine(string.Format(app.L("Project marker names shown: {0} of {1}"), Math.Min(8, folder.Evidence.Count), folder.EvidenceCount));
        foreach (string marker in folder.Evidence.Take(8)) text.AppendLine("  " + Visible(marker));
    }
    private void SelectFolder()
    {
        UpdateButtons();
        if (folders.SelectedItems.Count != 1 || folders.SelectedItem is not FolderRow row) { details.Text = app.L("Select a candidate to see its project markers and counted size."); return; }
        var text = new StringBuilder(); AppendFolder(text, row.Folder); details.Text = text.ToString();
    }
    private string Size(ProjectFolder folder) => folder.Partial && folder.Files == 0 ? app.L("Unknown") : folder.Bytes.ToString("N0", CultureInfo.CurrentCulture);
    private string Status(ProjectFolder folder) => app.L(folder.Partial ? "Partial; full size unknown" : "Metadata counted");
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);
    private static ListView NativeList(double height)
    {
        var list = new ListView { Height = height, Margin = new Thickness(0, 6, 0, 12), SelectionMode = SelectionMode.Single };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto); return list;
    }
    private GridView Columns(params (string Title, string Property, double Width)[] columns)
    {
        var view = new GridView();
        foreach (var column in columns) view.Columns.Add(new GridViewColumn { Header = app.L(column.Title), DisplayMemberBinding = new Binding(column.Property), Width = column.Width });
        return view;
    }
    private sealed record FolderRow(ProjectFolder Folder, string Path, string Kind, string Size, string Status);
    private sealed record IssueRow(string Path, string Reason);
}
