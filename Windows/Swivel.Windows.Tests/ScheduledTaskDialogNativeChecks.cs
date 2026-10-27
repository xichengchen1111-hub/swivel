using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class ScheduledTaskDialogNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in Find<T>(child)) yield return descendant;
    }
    private static string Property(object row, string name) => (string)row.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(row)!;
    private static ScheduledTaskSnapshot Snapshot(ScheduledTaskRow[] tasks, ScheduledTaskFolder[] folders, ScheduledTaskIssue[] issues,
        int omitted = 0, ScheduledTaskStopReason stop = ScheduledTaskStopReason.Completed) => new(tasks, folders, issues,
            DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(15001), folders.Length, tasks.Length, 1024, omitted, stop);

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        // Synthetic immutable metadata only: no Task Scheduler connection,
        // native task object, clipboard operation or file access is performed.
        string languageBefore = app.Prefs.Language;
        try
        {
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            {
                app.Prefs.SetLanguage(language);
                const string taskPath = "\\TASK_PRIVATE\\name\u202e", folderPath = "\\FOLDER_PRIVATE\u0009", issuePath = "\\ISSUE_PRIVATE\u000a";
                var inputTasks = new[] { new ScheduledTaskRow(1, "Visible name\r\n\u202e😀\ud800\u2028\U000e0001", taskPath, int.MinValue, false, uint.MaxValue),
                    new ScheduledTaskRow(2, null, "\\MISSING_NAME_PRIVATE", null, null, null), new ScheduledTaskRow(3, "Zero fields", "\\ZERO_PRIVATE", 0, true, 0) };
                var inputFolders = new[] { new ScheduledTaskFolder(folderPath, 4, null, 3, 0, 0, false) };
                var inputIssues = new[] { new ScheduledTaskIssue(1, issuePath, ScheduledTaskPhase.Name, 0x80070005, ScheduledTasks.ReadFailed),
                    new ScheduledTaskIssue(null, "\\CLEANUP_PRIVATE", ScheduledTaskPhase.Cleanup, 0, ScheduledTasks.CleanupFailed) };
                var snapshot = Snapshot(inputTasks, inputFolders, inputIssues, 7, ScheduledTaskStopReason.TimeLimit);
                var dialog = new ScheduledTaskDialog(app, snapshot);
                try
                {
                    var root = (DockPanel)dialog.Content;
                    var footer = root.Children.OfType<WrapPanel>().Single();
                    var buttons = footer.Children.OfType<Button>().ToArray();
                    var scroll = root.Children.OfType<ScrollViewer>().Single();
                    var body = (StackPanel)scroll.Content;
                    var tabs = body.Children.OfType<TabControl>().Single();
                    var lists = tabs.Items.OfType<TabItem>().Select(tab => (ListView)tab.Content).ToArray();
                    var pathChoice = body.Children.OfType<CheckBox>().Single();
                    var details = body.Children.OfType<TextBox>().Single();
                    var copy = buttons.Single(button => Equals(button.Content, app.L("Copy Selected Item")));
                    check(buttons.Length == 3 && buttons.All(button => new[] { app.L("Copy Report"), app.L("Copy Selected Item"), app.L("Close") }.Contains(button.Content)),
                        "Scheduled-task UI only offers explicit copies and Close, with no task mutation or launch");
                    check(Equals(buttons.Single(button => button.IsDefault).Content, app.L("Close")) && Equals(buttons.Single(button => button.IsCancel).Content, app.L("Close")) && !copy.IsEnabled,
                        "Return and Escape close; clipboard copy has no default action or preselection");
                    check(DockPanel.GetDock(footer) == Dock.Bottom && root.Children.IndexOf(footer) < root.Children.IndexOf(scroll),
                        "Copy and Close footer remains outside the scrolling metadata content");
                    check(body.Children.OfType<Expander>().Single().IsExpanded == false && body.Children.IndexOf(tabs) < body.Children.IndexOf(body.Children.OfType<Expander>().Single()),
                        "Full scope and limits are available in a collapsed native expander after the results");
                    check(lists.Length == 3 && lists.All(list => VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling && ScrollViewer.GetCanContentScroll(list)),
                        "Tasks, folder coverage and issues use virtualized recycling native lists");
                    check(lists.All(list => !string.IsNullOrEmpty(AutomationProperties.GetName(list))) && !string.IsNullOrEmpty(AutomationProperties.GetName(details)),
                        "Task lists and selected details have explicit accessibility names");
                    check(pathChoice.IsChecked == false && !dialog.CurrentReport.Contains("PRIVATE", StringComparison.Ordinal),
                        "Namespace paths in all rows, folders and issues are hidden by default in report copies");
                    check(dialog.CurrentReport.Contains("Visible name[U+000D][U+000A][U+202E]😀[U+D800][U+2028][U+E0001]", StringComparison.Ordinal) && dialog.CurrentReport.Contains(app.L(ScheduledTaskNotices.Privacy), StringComparison.Ordinal),
                        "Task names remain visible with control escaping and a clear privacy limitation");
                    check(dialog.CurrentReport.Contains(app.L("Partial Scheduled Task Snapshot"), StringComparison.Ordinal) && dialog.CurrentReport.Contains(app.L(ScheduledTasks.Limits), StringComparison.Ordinal) &&
                        dialog.CurrentReport.Contains(app.L("The soft collection time limit was reached."), StringComparison.Ordinal) && dialog.CurrentReport.Contains("7", StringComparison.Ordinal),
                        "Partial results retain collection stop reason, omitted count and full soft-limit explanation");
                    foreach (var list in lists)
                        foreach (object row in list.Items)
                        {
                            check(!row.ToString()!.Contains("PRIVATE", StringComparison.Ordinal), "Accessible item ToString does not leak a hidden namespace path");
                            check(Property(row, "Path") == app.L("Hidden"), "Every path binding shares the same default-hidden state");
                        }
                    var first = lists[0].Items[0]; var missing = lists[0].Items[1]; var zero = lists[0].Items[2];
                    check(Property(first, "State") == "-2147483648" && Property(first, "Enabled") == app.L("False") && Property(first, "LastResult") == "0xFFFFFFFF · 4294967295",
                        "Unknown state integers and raw 32-bit result patterns are retained without health conversion");
                    check(new[] { "Name", "State", "Enabled", "LastResult" }.All(property => Property(missing, property) == app.L("Not reported")),
                        "Unavailable name does not fall back to private Path, and missing fields do not become false or zero");
                    check(Property(zero, "State") == "0" && Property(zero, "Enabled") == app.L("True") && Property(zero, "LastResult") == "0x00000000 · 0",
                        "Actual zero and true remain distinguishable from unavailable fields");
                    tabs.SelectedIndex = 0; lists[0].SelectedIndex = 0;
                    check(copy.IsEnabled && dialog.SelectedReport is { } chosen && !chosen.Contains("PRIVATE", StringComparison.Ordinal) && chosen.Contains(app.L(ScheduledTasks.ReadFailed), StringComparison.Ordinal) &&
                        !chosen.Contains(app.L(ScheduledTasks.CleanupFailed), StringComparison.Ordinal),
                        "Selected task copy follows the path choice and includes its recorded row issues");
                    pathChoice.IsChecked = true;
                    check(lists[0].SelectedIndex == 0 && details.Text.Contains(LibraryJson.Visible(taskPath), StringComparison.Ordinal) && dialog.SelectedReport!.Contains(LibraryJson.Visible(issuePath), StringComparison.Ordinal),
                        "Showing paths preserves selected task identity and updates details plus associated issues");
                    check(dialog.CurrentReport.Contains(LibraryJson.Visible(folderPath), StringComparison.Ordinal) && dialog.CurrentReport.Contains(LibraryJson.Visible(issuePath), StringComparison.Ordinal) &&
                        dialog.CurrentReport.Contains("0x80070005 · 2147942405", StringComparison.Ordinal), "Full path-enabled report includes folder and issue paths and raw HRESULT bits");
                    for (int tab = 0; tab < lists.Length; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        if (tab != 0) check(!copy.IsEnabled && dialog.SelectedReport is null && details.Text == "", "Switching tabs clears selected-copy context and details");
                        lists[tab].SelectedIndex = 0;
                        check(dialog.SelectedReport!.Contains("PRIVATE", StringComparison.Ordinal) && lists[tab].Items[0].ToString()!.Contains("PRIVATE", StringComparison.Ordinal),
                            "Selected task, folder and issue surfaces all follow the explicit show-path choice");
                        pathChoice.IsChecked = false;
                        check(!dialog.SelectedReport!.Contains("PRIVATE", StringComparison.Ordinal) && !details.Text.Contains("PRIVATE", StringComparison.Ordinal) &&
                            !lists[tab].Items[0].ToString()!.Contains("PRIVATE", StringComparison.Ordinal), "Hiding paths removes them from every selected surface and accessibility fallback");
                        pathChoice.IsChecked = true;
                    }
                    inputTasks[0] = new(1, "MUTATED_AFTER_CAPTURE", "\\MUTATED_PRIVATE", 1, true, 1);
                    inputFolders[0] = new("\\MUTATED_PRIVATE", 0, 0, 0, 0, 0, true);
                    inputIssues[0] = new(1, "\\MUTATED_PRIVATE", ScheduledTaskPhase.Name, 1, "MUTATED_AFTER_CAPTURE");
                    check(!dialog.CurrentReport.Contains("MUTATED", StringComparison.Ordinal), "Subsequent caller-array replacement cannot mutate captured display or reports");
                }
                finally { dialog.Close(); }
                var empty = new ScheduledTaskDialog(app, Snapshot(Array.Empty<ScheduledTaskRow>(), Array.Empty<ScheduledTaskFolder>(),
                    new[] { new ScheduledTaskIssue(null, "\\EMPTY_PRIVATE", ScheduledTaskPhase.OpenService, 0x80070005, ScheduledTasks.ReadFailed) }, 11));
                try
                {
                    check(empty.CurrentReport.Contains(app.L("Partial Scheduled Task Snapshot"), StringComparison.Ordinal) && empty.CurrentReport.Contains(app.L(ScheduledTasks.ReadFailed), StringComparison.Ordinal) &&
                        empty.CurrentReport.Contains(app.L(ScheduledTaskNotices.Missing), StringComparison.Ordinal) && !empty.CurrentReport.Contains("EMPTY_PRIVATE", StringComparison.Ordinal),
                        "An empty task list retains all recorded issues and absence-of-evidence limitations without exposing paths");
                }
                finally { empty.Close(); }
            }
            app.Prefs.SetLanguage("en");
            var maximum = new ScheduledTaskDialog(app, Snapshot(Enumerable.Range(1, ScheduledTasks.MaxTasks).Select(index => new ScheduledTaskRow(index, "Task " + index, "\\path" + index, index, null, null)).ToArray(),
                Array.Empty<ScheduledTaskFolder>(), Array.Empty<ScheduledTaskIssue>()));
            try { check(maximum.CurrentReport.Contains("Task 1024", StringComparison.Ordinal) && Find<TabControl>(maximum).Single().Items.OfType<TabItem>().Sum(tab => ((ListView)tab.Content).Items.Count) == 1024,
                "The last retained task remains reachable and copied at the native 1,024-task limit"); }
            finally { maximum.Close(); }
            // The real reader only supplies fixed issue keys. This intentionally
            // oversized synthetic issue also exercises the final copy bound.
            var oversized = new ScheduledTaskDialog(app, Snapshot(new[] { new ScheduledTaskRow(1, "Small selected row", "\\SMALL_PRIVATE", 0, false, 0) },
                Array.Empty<ScheduledTaskFolder>(), new[] { new ScheduledTaskIssue(null, null, ScheduledTaskPhase.Validation, null, new string('x', ScheduledTaskDialog.CopyLimit)) }));
            try
            {
                reject(() => _ = oversized.CurrentReport, "An oversized complete copy is refused without silently truncating diagnostics");
                var tabs = Find<TabControl>(oversized).Single(); tabs.SelectedIndex = 0; ((ListView)((TabItem)tabs.Items[0]).Content).SelectedIndex = 0;
                string selected = oversized.SelectedReport!;
                check(selected.Contains("Small selected row", StringComparison.Ordinal) && selected.Length < ScheduledTaskDialog.CopyLimit,
                    "A bounded selected-row copy remains available when the whole report exceeds its copy budget");
            }
            finally { oversized.Close(); }
        }
        finally { app.Prefs.SetLanguage(languageBefore); }
    }
}
