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

internal static class LargeFileNotices
{
    internal const string Limits = "Read-only directory metadata in the chosen folder and 4 levels below, up to 20,000 entries and about 30 seconds. System items, links, cloud placeholders and unavailable subtrees are skipped. The time limit is soft; a blocked system call may return later.";
    internal const string Snapshot = "Sizes are recorded directory metadata, not a live or simultaneous snapshot. Logical bytes are not physical or reclaimable space; hard links may be counted more than once, and alternate streams and filesystem overhead are excluded. File contents are not read.";
    internal const string Hints = "Installer and disk-image hints use filename extensions only. They do not verify file contents, whether an application is installed, or whether a file is safe to delete.";
    internal const string Filtering = "Filters use this recorded metadata without scanning again. Every matching file is listed, largest first. MB and GB use decimal bytes. Skipped or unexamined items may contain other matches.";
    internal const string Privacy = "Copy Report includes the chosen folder, current filters, all matching files and all recorded issues. Copy Selected File Details includes only the selected file and scan scope. Nothing is opened, deleted, saved or sent automatically.";
}

internal sealed class LargeFilesDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult scan;
    private readonly LargeFileResult result;
    private readonly ListView files = NativeList(260);
    private readonly ComboBox minimum = new() { MinWidth = 200, Margin = new Thickness(0, 4, 14, 10) };
    private readonly CheckBox installerOnly = new() { Margin = new Thickness(0, 6, 0, 12), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock summary;
    private readonly TextBlock empty;
    private readonly TextBox details = DetailBox();
    private readonly Button copySelected;
    private IReadOnlyList<LargeFile> matches = Array.Empty<LargeFile>();

    internal string CurrentReport
    {
        get
        {
            var text = ReportHeader();
            if (matches.Count == 0) text.AppendLine(app.L("No files match these filters in the inspected metadata."));
            foreach (var file in matches) AppendFile(text, file);
            text.AppendLine(app.L("Skipped Items"));
            if (result.Issues.Count == 0) text.AppendLine(app.L("No recorded skipped items."));
            foreach (var issue in result.Issues) text.AppendLine(Visible(issue.RelativePath) + " · " + app.L(issue.Reason));
            return text.ToString();
        }
    }

    internal string? SelectedFileReport
    {
        get
        {
            if (files.SelectedItem is not FileRow row) return null;
            var text = ReportHeader(); AppendFile(text, row.File); return text.ToString();
        }
    }

    public LargeFilesDialog(App app, string rootPath, FolderScanResult scan, LargeFileResult result)
    {
        this.app = app; path = rootPath; this.scan = scan; this.result = result;
        Title = app.L("Large Files"); Width = 940; Height = 900; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel();
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected File Details"), () =>
        {
            if (SelectedFileReport is string report) app.Try(() => Clipboard.SetText(report));
        });
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var tabs = new TabControl();
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(UI.Text(Visible(path), 12));
        body.Children.Add(UI.Text(string.Format(app.L("Inspected files with known size: {0} · logical bytes: {1:N0}"), result.KnownFiles, result.KnownBytes), 14));
        body.Children.Add(UI.Text(ScopeStatus(), 12));
        body.Children.Add(UI.Text(app.L(LargeFileNotices.Limits), 12));
        body.Children.Add(UI.Text(app.L(LargeFileNotices.Snapshot), 12));
        body.Children.Add(UI.Text(app.L(LargeFileNotices.Hints), 12));
        body.Children.Add(UI.Text(app.L(LargeFileNotices.Filtering), 12));
        body.Children.Add(UI.Text(app.L(LargeFileNotices.Privacy), 12));

        body.Children.Add(UI.Text(app.L("Minimum Logical Size"), 13));
        foreach (var option in new[] { new SizeOption(0, "All sizes"), new SizeOption(10_000_000, "10 MB"), new SizeOption(100_000_000, "100 MB"), new SizeOption(500_000_000, "500 MB"), new SizeOption(1_000_000_000, "1 GB") })
            minimum.Items.Add(new ComboBoxItem { Content = app.L(option.Label), Tag = option });
        minimum.SelectedIndex = 2; AutomationProperties.SetName(minimum, app.L("Minimum Logical Size")); body.Children.Add(minimum);
        installerOnly.Content = app.L("Only installer or disk-image name hints");
        AutomationProperties.SetName(installerOnly, app.L("Only installer or disk-image name hints")); body.Children.Add(installerOnly);
        summary = UI.Text("", 14); body.Children.Add(summary);
        empty = UI.Text(app.L("No files match these filters in the inspected metadata."), 12); body.Children.Add(empty);
        files.View = Columns(("Relative Path", "Path", 470), ("Counted Logical Bytes", "Size", 170), ("Name Hint", "Hint", 200));
        AutomationProperties.SetName(files, app.L("Matching Files")); body.Children.Add(files);
        body.Children.Add(UI.Text(app.L("Selected File Details"), 13));
        AutomationProperties.SetName(details, app.L("Selected File Details")); body.Children.Add(details);
        files.SelectionChanged += (_, _) => SelectFile();
        minimum.SelectionChanged += (_, _) => ApplyFilter();
        installerOnly.Checked += (_, _) => ApplyFilter(); installerOnly.Unchecked += (_, _) => ApplyFilter();
        tabs.Items.Add(new TabItem { Header = app.L("Matching Files"), Content = UI.Scroll(body), Padding = new Thickness(12, 7, 12, 7) });

        var skipped = new StackPanel { Margin = new Thickness(12) };
        skipped.Children.Add(UI.Text(ScopeStatus(), 12)); skipped.Children.Add(UI.Text(app.L(LargeFileNotices.Limits), 12));
        skipped.Children.Add(UI.Text(string.Format(app.L("Recorded skipped items: {0}"), result.Issues.Count), 14));
        if (result.Issues.Count == 0) skipped.Children.Add(UI.Text(app.L("No recorded skipped items.")));
        var issues = NativeList(360); issues.View = Columns(("Relative Path", "Path", 440), ("Reason", "Reason", 350));
        issues.ItemsSource = result.Issues.Select(issue => new IssueRow(Visible(issue.RelativePath), app.L(issue.Reason))).ToArray();
        AutomationProperties.SetName(issues, app.L("Skipped Items")); skipped.Children.Add(issues);
        var issueDetails = DetailBox(); issueDetails.Text = app.L("Select a skipped item to see its full path and reason.");
        AutomationProperties.SetName(issueDetails, app.L("Selected Issue Details")); skipped.Children.Add(issueDetails);
        issues.SelectionChanged += (_, _) => issueDetails.Text = issues.SelectedItem is IssueRow issue
            ? app.L("Relative Path") + ": " + issue.Path + Environment.NewLine + app.L("Reason") + ": " + issue.Reason
            : app.L("Select a skipped item to see its full path and reason.");
        tabs.Items.Add(new TabItem { Header = app.L("Skipped Items"), Content = UI.Scroll(skipped), Padding = new Thickness(12, 7, 12, 7) });
        root.Children.Add(tabs); Content = root; ApplyFilter();
    }

    private SizeOption Minimum => minimum.SelectedItem is ComboBoxItem { Tag: SizeOption option } ? option : new(100_000_000, "100 MB");
    private void ApplyFilter()
    {
        matches = LargeFiles.Filter(result, Minimum.Bytes, installerOnly.IsChecked == true, default);
        files.SelectedItem = null;
        files.ItemsSource = matches.Select(file => new FileRow(file, Visible(file.RelativePath), file.Bytes.ToString("N0", CultureInfo.CurrentCulture), Hint(file))).ToArray();
        summary.Text = Summary(); empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectFile();
    }
    private string Summary() => string.Format(app.L("Matching files: {0} · matching logical bytes: {1:N0}"), matches.Count, matches.Sum(file => file.Bytes));
    private string Hint(LargeFile file) => app.L(file.InstallerHint ? "Installer or disk-image name hint" : "No installer name hint");
    private string ScopeStatus()
    {
        var parts = new List<string> { string.Format(app.L("Metadata entries: {0} · recorded skipped items/subtrees: {1}"), scan.Entries, scan.Root.Skipped) };
        if (result.Incomplete || scan.Root.Incomplete) parts.Add(app.L("Folder metadata scan is partial."));
        if (scan.EntryLimit) parts.Add(app.L("Entry limit reached"));
        if (scan.TimeLimit) parts.Add(app.L("Folder scan time limit reached"));
        return string.Join(" · ", parts);
    }
    private StringBuilder ReportHeader()
    {
        var text = new StringBuilder();
        text.AppendLine(app.L("Large Files")); text.AppendLine(Visible(path));
        text.AppendLine(string.Format(app.L("Inspected files with known size: {0} · logical bytes: {1:N0}"), result.KnownFiles, result.KnownBytes));
        text.AppendLine(app.L("Minimum Logical Size") + ": " + app.L(Minimum.Label));
        text.AppendLine(app.L(installerOnly.IsChecked == true ? "Filter: installer or disk-image name hints only" : "Filter: all file names"));
        text.AppendLine(Summary()); text.AppendLine(ScopeStatus());
        text.AppendLine(app.L(LargeFileNotices.Limits)); text.AppendLine(app.L(LargeFileNotices.Snapshot));
        text.AppendLine(app.L(LargeFileNotices.Hints)); text.AppendLine(app.L(LargeFileNotices.Filtering));
        return text;
    }
    private void AppendFile(StringBuilder text, LargeFile file)
    {
        text.AppendLine(app.L("Relative Path") + ": " + Visible(file.RelativePath));
        text.AppendLine(app.L("Counted Logical Bytes") + ": " + file.Bytes.ToString("N0", CultureInfo.CurrentCulture));
        text.AppendLine(app.L("Name Hint") + ": " + Hint(file));
    }
    private void SelectFile()
    {
        copySelected.IsEnabled = files.SelectedItem is FileRow;
        if (files.SelectedItem is not FileRow row) { details.Text = app.L("Select a file to see its full path and recorded size."); return; }
        var text = new StringBuilder(); AppendFile(text, row.File); details.Text = text.ToString();
    }
    // Reports and selected details preserve every path component. Escaping control
    // and direction-format characters makes metadata visible without truncation.
    private static string Visible(string value)
    {
        var text = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format)
                    text.Append("[U+").Append(char.ConvertToUtf32(c, value[i + 1]).ToString("X4", CultureInfo.InvariantCulture)).Append(']');
                else text.Append(c).Append(value[i + 1]);
                i++; continue;
            }
            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || char.IsSurrogate(c) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append("[U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append(']');
            else text.Append(c);
        }
        return text.ToString();
    }
    private static TextBox DetailBox() => new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 12) };
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
    private sealed record SizeOption(long Bytes, string Label);
    private sealed record FileRow(LargeFile File, string Path, string Size, string Hint);
    private sealed record IssueRow(string Path, string Reason);
}
