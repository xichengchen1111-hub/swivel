using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DuplicateNotices
{
    internal const string Limits = "Metadata scan: the chosen folder plus 4 levels, up to 20,000 entries and about 30 seconds. Content comparison: files up to 512 MiB each, at most 2 GiB read and about 60 seconds. These are soft time limits; a blocked system call may return later.";
    internal const string Scope = "Only ordinary local files on the chosen volume are compared. Empty files are skipped; repeated hard-link identities are counted once. System items, links, cloud placeholders and unavailable files or folders are skipped. The comparison itself changes nothing.";
    internal const string Snapshot = "Matching size and SHA-256 describe main file contents when each file was read, not one simultaneous snapshot. Files may change afterward. Alternate streams, permissions and metadata are not compared; logical repeat bytes are not physical or reclaimable space.";
    internal const string Recycle = "To clean up a group, select the files to remove in its file list and choose Move Selected to Recycle Bin…; at least one file of the group must stay unselected. Before each move, another file of the group is read again and must still have the group's SHA-256, and is held open until the move ends so other programs cannot newly open it for writing, rename or delete it; the selected file must still be a plain local file with one link and the same SHA-256. Otherwise it is left alone. Only fixed drives with a Recycle Bin are used; if a file is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move restores the last moved files while this window is open, unless something now exists at their place; afterwards, restore them from the Recycle Bin.";
    internal const string Privacy = "Names and paths are visible. Copy Report includes the chosen folder, every matching group and all recorded skip reasons; Copy Selected Group includes that group and the chosen folder. Nothing is saved or sent automatically.";
}

internal sealed class DuplicateScanConfirmation : Window
{
    public DuplicateScanConfirmation(App app, string path)
    {
        Title = app.L("Find Duplicate Files…"); Width = 720; Height = 560; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel();
        var start = UI.Button(app.L("Start Comparison"), () => DialogResult = true); start.IsDefault = true; footer.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Read file contents in the chosen folder to find duplicates?"), 18));
        body.Children.Add(UI.Text(DoctorFacts.VisibleBounded(path, 512)));
        body.Children.Add(UI.Text(app.L(DuplicateNotices.Limits)));
        body.Children.Add(UI.Text(app.L(DuplicateNotices.Scope)));
        body.Children.Add(UI.Text(app.L(DuplicateNotices.Snapshot)));
        root.Children.Add(UI.Scroll(body)); Content = root;
    }
}

internal sealed class DuplicateFilesDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult folder;
    private readonly DuplicateScanResult result;
    private readonly IReadOnlyList<IssueRow> issues;
    private readonly ListView groups = NativeList(230);
    private readonly ListView paths = NativeList(190);
    private readonly TextBlock selectedSummary, selectedHash;
    private readonly Button copySelected;
    private readonly RecycleBinSession<Member> bin;
    internal int Moved => bin.Gone.Count; // also those Windows removed outright

    internal string CurrentReport
    {
        get
        {
            var text = ReportHeader();
            if (result.Groups.Count == 0) text.AppendLine(app.L("No duplicate groups in the compared files. Skipped or unexamined files may still contain duplicates."));
            for (int i = 0; i < result.Groups.Count; i++) AppendGroup(text, result.Groups[i], i + 1);
            text.AppendLine(app.L("Skipped Items"));
            if (issues.Count == 0) text.AppendLine(app.L("No recorded skipped items."));
            foreach (var issue in issues) text.AppendLine(issue.Path + " · " + issue.Reason);
            bin.AppendReport(text, result.Groups.SelectMany(group => group.Paths.Select(member => new Member(group, member))));
            return text.ToString();
        }
    }

    public DuplicateFilesDialog(App app, string path, FolderScanResult folder, DuplicateScanResult result)
    {
        this.app = app; this.path = path; this.folder = folder; this.result = result;
        issues = BuildIssues();
        Title = app.L("Duplicate Files"); Width = 940; Height = 840; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel();
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected Group"), () =>
        {
            if (groups.SelectedItem is not GroupRow row) return;
            app.Try(() => { var text = ReportHeader(); AppendGroup(text, row.Group, row.Index); Clipboard.SetText(text.ToString()); });
        });
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        bin = new RecycleBinSession<Member>(app, this, new(
            member => Visible(member.Relative),
            member => Visible(Absolute(member.Relative)) + " · " + member.Group.Length.ToString("N0", CultureInfo.CurrentCulture),
            (member, owner) => NativeFiles.RecycleDuplicate(Absolute(member.Relative), member.Group.Length, member.Group.Sha256, Keep(member), owner),
            (member, stored) => NativeFiles.RestoreRecycled(Absolute(member.Relative), stored, false),
            "Move 1 duplicate file to the Recycle Bin?", "Move {0} duplicate files to the Recycle Bin?", "At least one other file of the group is kept. Check which copy you want to keep before moving."),
            SelectedMembers, Refresh);
        footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var tabs = new TabControl();
        var matches = new StackPanel { Margin = new Thickness(12) };
        matches.Children.Add(UI.Text(Visible(path), 12));
        matches.Children.Add(UI.Text(Summary(), 14));
        matches.Children.Add(UI.Text(ScopeStatus(), 12));
        matches.Children.Add(UI.Text(app.L(DuplicateNotices.Snapshot), 12));
        matches.Children.Add(UI.Text(app.L(DuplicateNotices.Privacy), 12));
        matches.Children.Add(UI.Text(app.L(DuplicateNotices.Recycle), 12));
        if (result.Groups.Count == 0) matches.Children.Add(UI.Text(app.L("No duplicate groups in the compared files. Skipped or unexamined files may still contain duplicates.")));
        groups.View = Columns(("Group", "Label", 85), ("Files", "Count", 65), ("Bytes Per File", "Size", 150), ("Logical Repeat Bytes", "Repeated", 180), ("First File", "First", 350));
        groups.ItemsSource = result.Groups.Select((group, index) => new GroupRow(this, group, index + 1)).ToArray();
        AutomationProperties.SetName(groups, app.L("Matching Groups")); matches.Children.Add(groups);
        selectedSummary = UI.Text(app.L("Select a group to see all matching file paths."), 14);
        selectedHash = UI.Text("", 12); matches.Children.Add(selectedSummary); matches.Children.Add(selectedHash);
        paths.SelectionMode = SelectionMode.Extended;
        paths.SelectionChanged += (_, _) => bin.UpdateButtons();
        paths.View = Columns(("Relative Path", "Path", 780));
        AutomationProperties.SetName(paths, app.L("Files in Selected Group")); matches.Children.Add(paths);
        groups.SelectionChanged += (_, _) => SelectGroup();
        matches.Children.Add(bin.Status);
        tabs.Items.Add(new TabItem { Header = app.L("Matching Groups"), Content = UI.Scroll(matches), Padding = new Thickness(12, 7, 12, 7) });

        var skipped = new StackPanel { Margin = new Thickness(12) };
        skipped.Children.Add(UI.Text(ScopeStatus()));
        skipped.Children.Add(UI.Text(app.L(DuplicateNotices.Limits), 12));
        skipped.Children.Add(UI.Text(app.L(DuplicateNotices.Scope), 12));
        skipped.Children.Add(UI.Text(string.Format(app.L("Recorded skipped items: {0}"), issues.Count), 14));
        if (issues.Count == 0) skipped.Children.Add(UI.Text(app.L("No recorded skipped items.")));
        var skippedList = NativeList(370); skippedList.View = Columns(("Relative Path", "Path", 460), ("Reason", "Reason", 330));
        skippedList.ItemsSource = issues; AutomationProperties.SetName(skippedList, app.L("Skipped Items")); skipped.Children.Add(skippedList);
        tabs.Items.Add(new TabItem { Header = app.L("Skipped Items"), Content = UI.Scroll(skipped), Padding = new Thickness(12, 7, 12, 7) });
        root.Children.Add(tabs); Content = root;
        if (result.Groups.Count > 0) groups.SelectedIndex = 0;
    }

    private string Summary() => string.Format(app.L("Matching groups: {0} · candidates: {1} · fully hashed files: {2} · read budget used: {3:N0} bytes"), result.Groups.Count, result.Candidates, result.Compared, result.BytesRead);

    private string ScopeStatus()
    {
        var parts = new List<string> { string.Format(app.L("Metadata entries: {0} · recorded skipped items/subtrees: {1}"), folder.Entries, folder.Root.Skipped) };
        if (folder.Root.Incomplete) parts.Add(app.L("Folder metadata scan is partial."));
        if (folder.EntryLimit) parts.Add(app.L("Entry limit reached"));
        if (folder.TimeLimit) parts.Add(app.L("Folder scan time limit reached"));
        if (result.ByteLimit) parts.Add(app.L("Comparison read limit"));
        if (result.TimeLimit) parts.Add(app.L("Comparison time limit"));
        return string.Join(" · ", parts);
    }

    private StringBuilder ReportHeader()
    {
        var text = new StringBuilder();
        text.AppendLine(app.L("Duplicate Files")); text.AppendLine(Visible(path));
        text.AppendLine(Summary()); text.AppendLine(ScopeStatus());
        text.AppendLine(app.L(DuplicateNotices.Limits)); text.AppendLine(app.L(DuplicateNotices.Scope));
        text.AppendLine(app.L(DuplicateNotices.Snapshot));
        return text;
    }

    private void AppendGroup(StringBuilder text, DuplicateGroup group, int index)
    {
        text.AppendLine(string.Format(app.L("Group {0}: {1} files · {2:N0} bytes each · {3:N0} logical repeat bytes"), index, group.Paths.Count, group.Length, RepeatedBytes(group)));
        text.AppendLine(app.L("SHA-256") + ": " + group.Sha256);
        foreach (var member in group.Paths)
            text.AppendLine(bin.Gone.Contains(new Member(group, member)) ? Visible(member) + " · " + app.L("moved or removed in this window") : Visible(member));
    }

    private void SelectGroup()
    {
        copySelected.IsEnabled = groups.SelectedItem is GroupRow;
        if (groups.SelectedItem is not GroupRow row)
        {
            selectedSummary.Text = app.L("Select a group to see all matching file paths."); selectedHash.Text = ""; paths.ItemsSource = null; return;
        }
        selectedSummary.Text = string.Format(app.L("Group {0}: {1} files · {2:N0} bytes each · {3:N0} logical repeat bytes"), row.Index, Remaining(row.Group).Count(), row.Group.Length, (decimal)row.Group.Length * Math.Max(0, Remaining(row.Group).Count() - 1));
        selectedHash.Text = app.L("SHA-256") + ": " + row.Group.Sha256;
        paths.ItemsSource = Remaining(row.Group).Select(member => new PathRow(new Member(row.Group, member), Visible(member))).ToArray();
        bin.UpdateButtons();
    }

    // Files of a group still in place, in the group's order.
    private IEnumerable<string> Remaining(DuplicateGroup group) => group.Paths.Where(member => !bin.Gone.Contains(new Member(group, member)));

    // Only a selection that leaves at least one file of its group in place can be moved.
    private IReadOnlyList<Member> SelectedMembers()
    {
        var chosen = paths.SelectedItems.OfType<PathRow>().Select(row => row.Member).Where(member => !bin.Gone.Contains(member)).Distinct().ToArray();
        if (chosen.Length == 0 || chosen.Select(member => member.Group).Distinct().Count() != 1) return Array.Empty<Member>();
        return chosen.Length < Remaining(chosen[0].Group).Count() ? chosen : Array.Empty<Member>();
    }

    // Files that may serve as the kept copy: in place, not in the batch being moved.
    private IEnumerable<string> Keep(Member member)
        => member.Group.Paths.Where(other => other != member.Relative && !bin.Gone.Contains(new Member(member.Group, other)) && !bin.Batch.Contains(new Member(member.Group, other))).Select(Absolute).ToArray();

    private void Refresh()
    {
        int index = groups.SelectedIndex;
        groups.Items.Refresh();
        if (groups.SelectedIndex != index) groups.SelectedIndex = index; else SelectGroup();
        groups.IsEnabled = paths.IsEnabled = bin.Idle;
    }

    private IReadOnlyList<IssueRow> BuildIssues()
    {
        var rows = new List<IssueRow>();
        void Visit(FolderSizeNode node, string relative)
        {
            if (node.Issue is string issue) rows.Add(new(Visible(relative), app.L(issue)));
            else if (node.Skipped > node.Children.Sum(child => child.Skipped)) rows.Add(new(Visible(relative), app.L("Folder enumeration was incomplete")));
            foreach (var child in node.Children) Visit(child, relative == "." ? child.Name : relative + "\\" + child.Name);
        }
        Visit(folder.Root, ".");
        rows.AddRange(result.Issues.Select(issue => new IssueRow(Visible(issue.RelativePath), app.L(issue.Reason))));
        return rows.AsReadOnly();
    }

    private string Absolute(string relative) => System.IO.Path.Combine(path, relative);
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);
    private static decimal RepeatedBytes(DuplicateGroup group) => (decimal)group.Length * Math.Max(0, group.Paths.Count - 1);
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
    private sealed record Member(DuplicateGroup Group, string Relative);
    private sealed record GroupRow(DuplicateFilesDialog Owner, DuplicateGroup Group, int Index)
    {
        private int Left => Owner.Remaining(Group).Count();
        public string Label => string.Format(Owner.app.L("Group {0}"), Index);
        public string Count => Left.ToString("N0", CultureInfo.CurrentCulture);
        public string Size => Group.Length.ToString("N0", CultureInfo.CurrentCulture);
        public string Repeated => ((decimal)Group.Length * Math.Max(0, Left - 1)).ToString("N0", CultureInfo.CurrentCulture);
        public string First => Owner.Remaining(Group).FirstOrDefault() is string first ? Visible(first) : "";
    }
    private sealed record PathRow(Member Member, string Path);
    private sealed record IssueRow(string Path, string Reason);
}
