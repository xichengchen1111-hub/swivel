using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class LargeFilesChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Synthetic metadata only. Do not enumerate a real folder, show the
        // window, invoke a button, read/write the clipboard or save preferences.
        var app = Application.Current as App ?? new App(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string longPath = string.Join("\\", Enumerable.Repeat(new string('a', 200), 4)) + "\\complete-tail-\u202E.bin";
        string issuePath = new string('b', 600) + "-issue-tail\u2028.bin";
        var scan = new FolderScanResult(new("recorded folder", true), 7, true, false);
        var entries = new[]
        {
            new LargeFile("largest.iso", 1_000_000_000, true),
            new LargeFile("middle.msi", 500_000_000, true),
            new LargeFile(longPath, 100_000_000, false),
            new LargeFile("below.exe", 99_999_999, true),
            new LargeFile("ten.txt", 10_000_000, false),
            new LargeFile("empty.txt", 0, false)
        };
        var result = new LargeFileResult(entries, new[] { new LargeFileIssue(issuePath, "Unavailable or unsupported"), new LargeFileIssue(".", "Scan entry limit") }, true, entries.Length, entries.Sum(x => x.Bytes));
        var dialog = new LargeFilesDialog(app, @"C:\recorded-only", scan, result);
        try
        {
            var nodes = Descendants(dialog).ToArray();
            var minimum = nodes.OfType<ComboBox>().Single();
            var onlyHints = nodes.OfType<CheckBox>().Single();
            var files = nodes.OfType<ListView>().Single(x => AutomationProperties.GetName(x) == app.L("Matching Files"));
            var issues = nodes.OfType<ListView>().Single(x => AutomationProperties.GetName(x) == app.L("Skipped Items"));
            var selected = nodes.OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == app.L("Selected File Details"));
            var selectedIssue = nodes.OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == app.L("Selected Issue Details"));
            var copySelected = nodes.OfType<Button>().Single(x => Equals(x.Content, app.L("Copy Selected File Details")));
            check(dialog.Content is DockPanel root && root.Children.OfType<WrapPanel>().Any(x => DockPanel.GetDock(x) == Dock.Bottom) && dialog.MinWidth == 540 && dialog.MinHeight == 480,
                "Large-file native result window retains a fixed footer and scrollable body at its minimum size");
            check(nodes.OfType<TabControl>().Single().Items.Count == 2 && files.Items.Count == 3 && minimum.SelectedIndex == 2 && onlyHints.IsChecked != true,
                "Native large-file results default to decimal 100 MB and retain a separate issues tab");
            check(files.SelectedIndex == -1 && !copySelected.IsEnabled && dialog.SelectedFileReport is null,
                "A file must be explicitly selected before its details can be copied");
            check(VirtualizingPanel.GetIsVirtualizing(files) && VirtualizingPanel.GetVirtualizationMode(files) == VirtualizationMode.Recycling && ScrollViewer.GetCanContentScroll(files)
                && VirtualizingPanel.GetIsVirtualizing(issues) && VirtualizingPanel.GetVirtualizationMode(issues) == VirtualizationMode.Recycling && ScrollViewer.GetCanContentScroll(issues),
                "Both native metadata lists keep recycling virtualization and logical scrolling enabled");
            files.SelectedIndex = 0;
            check(copySelected.IsEnabled && dialog.SelectedFileReport!.Contains("largest.iso") && !dialog.SelectedFileReport.Contains("middle.msi"),
                "The selected-file report includes just that file and scan scope, never unrelated matching paths");
            files.SelectedIndex = 2;
            check(selected.IsReadOnly && selected.Text.Contains(new string('a', 200)) && selected.Text.Contains("complete-tail-[U+202E].bin") && selected.Text.Length > 800 && !selected.Text.Contains('\u202E'),
                "Full selected relative paths beyond 512 characters remain available with direction characters made visible");
            string report = dialog.CurrentReport;
            check(report.Contains(@"C:\recorded-only") && report.Contains("complete-tail-[U+202E].bin") && report.Contains("-issue-tail[U+2028].bin") && report.Contains(app.L(LargeFileNotices.Snapshot))
                && report.Contains(app.L("Entry limit reached")) && !report.Contains("below.exe"),
                "Report preserves the root, full filtered paths, all issues, scope and metadata limits without silently copying excluded matches");
            check(!report.Contains('\u202E') && !report.Contains('\u2028'), "Metadata reports make directional and line-separator characters visible");
            issues.SelectedIndex = 0;
            check(selectedIssue.IsReadOnly && selectedIssue.Text.Contains("-issue-tail[U+2028].bin") && selectedIssue.Text.Contains(app.L("Unavailable or unsupported")),
                "Selecting an issue exposes its full sanitized path and reason in a native read-only text box");
            onlyHints.IsChecked = true;
            check(files.Items.Count == 2 && files.SelectedIndex == -1 && !copySelected.IsEnabled && dialog.SelectedFileReport is null
                && dialog.CurrentReport.Contains(app.L("Filter: installer or disk-image name hints only")),
                "Installer-name filtering uses the existing result and clears stale selected-file copying");
            minimum.SelectedIndex = 0;
            check(files.Items.Count == 3 && dialog.CurrentReport.Contains("below.exe") && !dialog.CurrentReport.Contains("empty.txt"),
                "All-size installer hints include smaller names without including ordinary files");
            onlyHints.IsChecked = false;
            check(files.Items.Count == 6 && dialog.CurrentReport.Contains("empty.txt"), "All sizes retains known zero-byte files");
            minimum.SelectedIndex = 1; check(files.Items.Count == 5, "Decimal 10 MB includes its exact boundary");
            minimum.SelectedIndex = 3; check(files.Items.Count == 2, "Decimal 500 MB includes its exact boundary");
            minimum.SelectedIndex = 4; check(files.Items.Count == 1, "Decimal 1 GB includes its exact boundary");
            check(entries.Length == 6 && result.Issues.Count == 2 && scan.Entries == 7, "Changing native filters leaves the input metadata unchanged");
            var buttons = nodes.OfType<Button>().ToArray();
            check(buttons.Length == 5 && buttons.Single(x => Equals(x.Content, app.L("Close"))).IsCancel
                && !buttons.Single(x => Equals(x.Content, app.L("Undo Move"))).IsEnabled && buttons.Any(x => Equals(x.Content, app.L("Move Selected to Recycle Bin…"))),
                "The result window offers copy actions, a confirmed Move to Recycle Bin, Undo (off until a move) and Close, with no file-open action");
        }
        finally { dialog.Close(); }

        var small = new LargeFileResult(new[] { new LargeFile("small.txt", 1, false) }, result.Issues, true, 1, 1);
        var emptyDialog = new LargeFilesDialog(app, @"C:\recorded-only", scan, small);
        try
        {
            check(!emptyDialog.CurrentReport.Contains("small.txt") && emptyDialog.CurrentReport.Contains(app.L("No files match these filters in the inspected metadata."))
                && emptyDialog.CurrentReport.Contains("-issue-tail[U+2028].bin"), "A zero-match filter still reports every recorded issue and the empty-result explanation");
        }
        finally { emptyDialog.Close(); }

        var manyFiles = Enumerable.Range(0, 300).Select(i => new LargeFile($"retained-{i:D3}.bin", 100_000_000, false)).ToArray();
        var manyResult = new LargeFileResult(manyFiles, Array.Empty<LargeFileIssue>(), false, manyFiles.Length, manyFiles.Sum(x => x.Bytes));
        var manyDialog = new LargeFilesDialog(app, @"C:\recorded-only", new(new("root", true), manyFiles.Length, false, false), manyResult);
        try
        {
            var list = Descendants(manyDialog).OfType<ListView>().Single(x => AutomationProperties.GetName(x) == app.L("Matching Files"));
            check(list.Items.Count == manyFiles.Length && manyDialog.CurrentReport.Contains("retained-000.bin") && manyDialog.CurrentReport.Contains("retained-299.bin"),
                "The native matching list and report do not truncate a larger recorded result");
        }
        finally { manyDialog.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
}
