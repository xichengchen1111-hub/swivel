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

internal static class ScheduledTaskNotices
{
    internal const string Intro = "Read-only local task metadata available with current permissions, not proof of task ownership or health. Task names remain visible and may contain private information.";
    internal const string Meaning = "These are historical, non-atomic values reported by Task Scheduler. A readable task is not necessarily owned by you. State and last-result numbers are shown as raw values, not as a health, safety or success verdict. Enabled does not prove that a task ran, will run or is currently running. Missing values are not treated as false or zero.";
    internal const string Privacy = "Task names are visible by default and can contain personal or application information. Task Scheduler namespace paths are hidden unless you choose to show them; they are not filesystem paths. The same choice applies to lists, details, issues and copied reports. Hiding paths does not anonymize task names. Nothing is sent automatically, and this snapshot is not included in assistant requests.";
    internal const string Missing = "An empty or partial result does not prove that no scheduled tasks exist. Hidden tasks, inaccessible locations, limits and changes during collection can omit tasks or fields. This window does not run, edit, enable, disable, delete or open any task.";
    internal const string ReportLimit = "This scheduled-task copy exceeds the complete-report limit. Copy one selected item instead.";
}

internal sealed class ScheduledTaskDialog : Window
{
    internal const int CopyLimit = 4 * 1024 * 1024;
    private readonly App app;
    private readonly ScheduledTaskSnapshot snapshot;
    private readonly ScheduledTaskRow[] tasks;
    private readonly ScheduledTaskFolder[] folders;
    private readonly ScheduledTaskIssue[] issues;
    private readonly ListView taskList = NativeList(), folderList = NativeList(), issueList = NativeList();
    private readonly CheckBox paths;
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 195,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 12) };
    private readonly Button copySelected;
    private VisibleRow? selected;
    private bool showPaths, rendering, changingSelection;
    internal string CurrentReport => Report(null);
    internal string? SelectedReport => selected is null ? null : Report(selected);

    internal ScheduledTaskDialog(App app, ScheduledTaskSnapshot snapshot)
    {
        this.app = app; this.snapshot = snapshot;
        // Only copied metadata from the finished reader is consumed. There are
        // no services, task objects, file handles or refresh callbacks here.
        tasks = snapshot.Tasks.ToArray(); folders = snapshot.Folders.ToArray(); issues = snapshot.Issues.ToArray();
        Title = app.L("Scheduled Task Metadata Snapshot"); Width = 1080; Height = 850; MinWidth = 560; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel();
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected Item"), () => app.Try(() => { if (SelectedReport is { } text) Clipboard.SetText(text); }));
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true; close.IsDefault = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L(ScheduledTaskNotices.Intro), 12));
        body.Children.Add(UI.Text(Header(), 13));
        paths = new CheckBox { Content = app.L("Show Task Scheduler Paths in This View and Copies"), IsChecked = false, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetName(paths, app.L("Show Task Scheduler Paths in This View and Copies"));
        paths.Checked += (_, _) => { showPaths = true; Render(); };
        paths.Unchecked += (_, _) => { showPaths = false; Render(); }; body.Children.Add(paths);
        var tabs = new TabControl { Height = 345 };
        taskList.View = Columns(("Record", nameof(TaskRow.Index), 70), ("Task Name", nameof(TaskRow.Name), 240),
            ("Task Scheduler Path", nameof(TaskRow.Path), 260), ("Raw Task State", nameof(TaskRow.State), 110),
            ("Reported Enabled Value", nameof(TaskRow.Enabled), 145), ("Raw Last Task Result", nameof(TaskRow.LastResult), 230));
        folderList.View = Columns(("Record", nameof(FolderRow.Index), 70), ("Task Scheduler Path", nameof(FolderRow.Path), 270),
            ("Folder Depth", nameof(FolderRow.Depth), 100), ("Reported Task Count", nameof(FolderRow.ReportedTasks), 140),
            ("Task Attempts", nameof(FolderRow.AttemptedTasks), 120), ("Reported Subfolder Count", nameof(FolderRow.ReportedFolders), 155),
            ("Subfolder Attempts", nameof(FolderRow.AttemptedChildren), 130), ("Folder Read Complete", nameof(FolderRow.Complete), 150));
        issueList.View = Columns(("Record", nameof(IssueRow.Index), 70), ("Task Record", nameof(IssueRow.TaskIndex), 90),
            ("Read Phase", nameof(IssueRow.Phase), 180), ("Task Scheduler Path", nameof(IssueRow.Path), 260),
            ("Raw Error Code", nameof(IssueRow.Error), 150), ("Reason", nameof(IssueRow.Reason), 370));
        foreach (var pair in new[] { ("Reported Scheduled Tasks", taskList), ("Task Folder Coverage", folderList), ("Scheduled Task Read Issues", issueList) })
        {
            AutomationProperties.SetName(pair.Item2, app.L(pair.Item1));
            tabs.Items.Add(new TabItem { Header = app.L(pair.Item1), Content = pair.Item2 });
            pair.Item2.SelectionChanged += (_, _) => Choose(pair.Item2);
        }
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) { selected = null; changingSelection = true; foreach (var list in Lists()) list.SelectedItem = null; changingSelection = false; UpdateDetails(); } };
        body.Children.Add(tabs);
        if (tasks.Length == 0) body.Children.Add(UI.Text(app.L("No scheduled-task rows were retained. Read issues and collection limits still apply."), 12));
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        AutomationProperties.SetName(details, app.L("Selected Scheduled Task Details")); body.Children.Add(details);
        var scope = new StackPanel(); foreach (string note in Notes()) scope.Children.Add(UI.Text(app.L(note), 12));
        body.Children.Add(new Expander { Header = app.L("Scope, Privacy and Collection Limits"), Content = scope, IsExpanded = false, Margin = new Thickness(0, 4, 0, 12) });
        root.Children.Add(UI.Scroll(body)); Content = root; Render();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
    private IEnumerable<string> Notes()
    {
        yield return ScheduledTasks.Scope; yield return ScheduledTasks.Limits; yield return ScheduledTaskNotices.Meaning;
        yield return ScheduledTaskNotices.Privacy; yield return ScheduledTaskNotices.Missing;
    }
    private ListView[] Lists() => new[] { taskList, folderList, issueList };
    private static ListView NativeList()
    {
        var list = new ListView { SelectionMode = SelectionMode.Single };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); return list;
    }
    private GridView Columns(params (string Label, string Property, double Width)[] values)
    {
        var grid = new GridView();
        foreach (var value in values) grid.Columns.Add(new GridViewColumn { Header = app.L(value.Label), DisplayMemberBinding = new Binding(value.Property), Width = value.Width });
        return grid;
    }
    private void Render()
    {
        string? keep = selected?.Key; rendering = true;
        try
        {
            taskList.ItemsSource = tasks.Select(task => new TaskRow(this, task)).ToArray();
            folderList.ItemsSource = folders.Select((folder, index) => new FolderRow(this, folder, index)).ToArray();
            issueList.ItemsSource = issues.Select((issue, index) => new IssueRow(this, issue, index)).ToArray();
            selected = null;
            foreach (var list in Lists())
            {
                var match = list.Items.OfType<VisibleRow>().FirstOrDefault(row => row.Key == keep);
                list.SelectedItem = match; if (match is not null) selected = match;
            }
        }
        finally { rendering = false; }
        UpdateDetails();
    }
    private void Choose(ListView source)
    {
        if (rendering || changingSelection) return;
        changingSelection = true;
        try { foreach (var list in Lists()) if (!ReferenceEquals(list, source)) list.SelectedItem = null; selected = source.SelectedItem as VisibleRow; }
        finally { changingSelection = false; }
        UpdateDetails();
    }
    private void UpdateDetails() { copySelected.IsEnabled = selected is not null; details.Text = selected?.Describe() ?? ""; }
    private string Header() => string.Join("\n", new[] {
        app.L("Snapshot Recorded") + ": " + snapshot.Captured.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        app.L(snapshot.Partial ? "Partial Scheduled Task Snapshot" : "No collection issue was recorded within the stated scope."),
        string.Format(CultureInfo.CurrentCulture, app.L("Retained tasks: {0} · folder attempts: {1}/128 · task attempts: {2}/1024 · name/path text budget used: {3}/262144 · retained issues: {4}/256 · additional issues not retained: {5}"),
            tasks.Length, snapshot.FolderAttempts, snapshot.TaskAttempts, snapshot.RetainedTextUnits, issues.Length, snapshot.OmittedIssues),
        app.L("Collection End Reason") + ": " + app.L(EndReason(snapshot.StopReason)),
        string.Format(CultureInfo.CurrentCulture, app.L("Elapsed collection time: {0:N1} seconds. The time budget is soft."), snapshot.Elapsed.TotalSeconds)
    });
    private static string EndReason(ScheduledTaskStopReason reason) => reason switch {
        ScheduledTaskStopReason.Completed => "Task collection ended within the stated scope.",
        ScheduledTaskStopReason.FolderLimit => "The folder-attempt limit was reached.",
        ScheduledTaskStopReason.TaskLimit => "The task-attempt limit was reached.",
        ScheduledTaskStopReason.TextLimit => "The shared name/path size limit was reached.",
        ScheduledTaskStopReason.TimeLimit => "The soft collection time limit was reached.",
        _ => "The collection end reason was not recognized."
    };
    private string Text(string? value) => value is null ? app.L("Not reported") : ServiceRecords.Visible(value);
    private string Namespace(string? value) => showPaths ? Text(value) : app.L("Hidden");
    private string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? app.L("Not reported");
    private string Boolean(bool? value) => value.HasValue ? app.L(value.Value ? "True" : "False") : app.L("Not reported");
    private string Bits(uint? value) => value.HasValue ? "0x" + value.Value.ToString("X8", CultureInfo.InvariantCulture) + " · " + value.Value.ToString(CultureInfo.InvariantCulture) : app.L("Not reported");
    private string Field(string label, string value) => app.L(label) + ": " + value;
    private string Report(VisibleRow? item)
    {
        var text = new StringBuilder();
        void Line(string value)
        {
            if (value.Length > CopyLimit - Environment.NewLine.Length - text.Length) throw new UserError(ScheduledTaskNotices.ReportLimit);
            text.Append(value).Append(Environment.NewLine);
        }
        Line(app.L("Windows Doctor") + " · " + Title); Line(Header());
        Line(app.L(showPaths ? "Task Scheduler paths are included in this report." : "Task Scheduler paths are hidden in this report."));
        foreach (string note in Notes()) Line(app.L(note));
        if (item is not null)
        {
            Line(""); Line(item.Describe());
            if (item is TaskRow task) foreach (var pair in issues.Select((issue, index) => (issue, index)).Where(pair => pair.issue.RowIndex == task.Source.RowIndex))
            { Line(""); Line(new IssueRow(this, pair.issue, pair.index).Describe()); }
        }
        else
        {
            Line(""); Line(app.L("Reported Scheduled Tasks")); foreach (var task in tasks) { Line(""); Line(new TaskRow(this, task).Describe()); }
            Line(""); Line(app.L("Task Folder Coverage")); foreach (var pair in folders.Select((folder, index) => (folder, index))) { Line(""); Line(new FolderRow(this, pair.folder, pair.index).Describe()); }
            Line(""); Line(app.L("Scheduled Task Read Issues")); foreach (var pair in issues.Select((issue, index) => (issue, index))) { Line(""); Line(new IssueRow(this, pair.issue, pair.index).Describe()); }
        }
        return text.ToString();
    }
    private abstract class VisibleRow
    {
        public abstract string Key { get; }
        public abstract string Describe();
        // WPF item fallbacks/accessibility must never use a record-generated
        // ToString that would expose the hidden source namespace path.
        public override string ToString() => Describe();
    }
    private sealed class TaskRow(ScheduledTaskDialog owner, ScheduledTaskRow source) : VisibleRow
    {
        internal ScheduledTaskRow Source => source;
        public override string Key => "task:" + source.RowIndex.ToString(CultureInfo.InvariantCulture);
        public string Index => source.RowIndex.ToString(CultureInfo.InvariantCulture);
        public string Name => owner.Text(source.Name);
        public string Path => owner.Namespace(source.Path);
        public string State => owner.Number(source.State);
        public string Enabled => owner.Boolean(source.Enabled);
        public string LastResult => owner.Bits(source.LastResult);
        public override string Describe() => string.Join("\n", new[] { owner.Field("Task Record", Index), owner.Field("Task Name", Name), owner.Field("Task Scheduler Path", Path),
            owner.Field("Raw Task State", State), owner.Field("Reported Enabled Value", Enabled), owner.Field("Raw Last Task Result", LastResult) });
    }
    private sealed class FolderRow(ScheduledTaskDialog owner, ScheduledTaskFolder source, int index) : VisibleRow
    {
        public override string Key => "folder:" + index.ToString(CultureInfo.InvariantCulture);
        public string Index => (index + 1).ToString(CultureInfo.InvariantCulture);
        public string Path => owner.Namespace(source.Path);
        public string Depth => owner.Number(source.Depth);
        public string ReportedTasks => owner.Number(source.ReportedTasks);
        public string AttemptedTasks => owner.Number(source.AttemptedTasks);
        public string ReportedFolders => owner.Number(source.ReportedFolders);
        public string AttemptedChildren => owner.Number(source.AttemptedChildren);
        public string Complete => owner.Boolean(source.Complete);
        public override string Describe() => string.Join("\n", new[] { owner.Field("Folder Record", Index), owner.Field("Task Scheduler Path", Path), owner.Field("Folder Depth", Depth),
            owner.Field("Reported Task Count", ReportedTasks), owner.Field("Task Attempts", AttemptedTasks), owner.Field("Reported Subfolder Count", ReportedFolders),
            owner.Field("Subfolder Attempts", AttemptedChildren), owner.Field("Folder Read Complete", Complete) });
    }
    private sealed class IssueRow(ScheduledTaskDialog owner, ScheduledTaskIssue source, int index) : VisibleRow
    {
        public override string Key => "issue:" + index.ToString(CultureInfo.InvariantCulture);
        public string Index => (index + 1).ToString(CultureInfo.InvariantCulture);
        public string TaskIndex => owner.Number(source.RowIndex);
        public string Phase => owner.app.L(ScheduledTasks.Phase(source.Phase));
        public string Path => owner.Namespace(source.NamespacePath);
        public string Error => owner.Bits(source.ErrorCode);
        public string Reason => ServiceRecords.Visible(owner.app.L(source.Reason));
        public override string Describe() => string.Join("\n", new[] { owner.Field("Issue Record", Index), owner.Field("Task Record", TaskIndex), owner.Field("Read Phase", Phase),
            owner.Field("Task Scheduler Path", Path), owner.Field("Raw Error Code", Error), owner.Field("Reason", Reason) });
    }
}
