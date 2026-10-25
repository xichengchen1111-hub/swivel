using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class TextSimilarityNotices
{
    internal const string Limits = "The chosen folder plus 4 levels: up to 20,000 metadata entries and about 30 seconds. Text reading and comparison: up to 200 file attempts, 1 MiB per file, 16 MiB read, 20,000 lines per file, 200,000 retained per-file distinct lines in total (shared lines count again in each file) and about 60 seconds. Blocked system calls may return later.";
    internal const string Rules = "Only TXT, TEXT, MD, MARKDOWN, CSV, TSV, LOG and SRT with valid UTF-8 or BOM-marked UTF-16 are read. Compare the same extension only, ignoring extension case. At least 5 distinct nonempty lines are required. Leading and trailing whitespace and repeated lines are ignored; line text is case-sensitive. Similarity is shared distinct lines divided by their union, at least 80%. Line order and frequency are not compared.";
    internal const string Snapshot = "These are historical text-similarity hints, not identical-file proof, related groups or cleanup advice. Even 100% can have different bytes, order or repeated lines. Each pair stands alone; one match does not imply another. Files may change afterward. No image similarity, content preview, deletion or opening is performed.";
    internal const string Privacy = "File names and paths are visible. Copy Report includes the chosen folder, all displayed pairs and every recorded skip reason; Copy Selected Pair includes both safely displayed paths. Hidden path characters use visible escapes; paths over 512 source characters end in an ellipsis. File contents are never copied. Nothing is saved or sent automatically.";
    internal const string PairLimit = "At most the first 1,000 matching pairs in ordinal path order are displayed, not the best 1,000. Counts cover comparisons completed before any time limit. Missing pairs and unexamined files may still be similar.";
    internal const string Downgrade = "Older Windows previews cannot read a My Wheel configuration containing this action. Keep a pre-upgrade settings backup before downgrading; an older preview may reset settings and overwrite them on the next save.";
}

internal sealed class TextSimilarityConfirmation : Window
{
    public TextSimilarityConfirmation(App app, string path)
    {
        Title = app.L("Find Similar Text Files…"); Width = 760; Height = 650; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var start = UI.Button(app.L("Start Comparison"), () => DialogResult = true); start.IsDefault = true; footer.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Read plain-text contents in this folder to find similar file pairs?"), 18));
        body.Children.Add(UI.Text(DoctorFacts.VisibleBounded(path, 512)));
        foreach (string note in new[] { TextSimilarityNotices.Limits, TextSimilarityNotices.Rules, DuplicateNotices.Scope, TextSimilarityNotices.Snapshot, TextSimilarityNotices.PairLimit, TextSimilarityNotices.Privacy, TextSimilarityNotices.Downgrade })
            body.Children.Add(UI.Text(app.L(note), 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

internal sealed class TextSimilarityDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult folder;
    private readonly TextSimilarityResult result;
    private readonly List<IssueRow> issues = new();
    private readonly ListView pairs = NativeList(290);
    private readonly TextBox selected;
    private readonly Button copySelected;
    internal string CurrentReport
    {
        get
        {
            var text = Header();
            foreach (var pair in result.Pairs) AppendPair(text, pair);
            text.AppendLine(app.L("Skipped Items"));
            foreach (var issue in issues) text.AppendLine(issue.Path + " · " + issue.Reason);
            if (issues.Count == 0) text.AppendLine(app.L("No recorded skipped items."));
            return text.ToString();
        }
    }
    internal string SelectedReport
    {
        get { var text = Header(); if (pairs.SelectedItem is PairRow row) AppendPair(text, row.Pair); return text.ToString(); }
    }
    public TextSimilarityDialog(App app, string path, FolderScanResult folder, TextSimilarityResult result)
    {
        this.app = app; this.path = path; this.folder = folder; this.result = result;
        void Visit(FolderSizeNode node, string relative)
        {
            if (node.Issue is string issue) issues.Add(new(Visible(relative), app.L(issue)));
            else if (node.Skipped > node.Children.Sum(child => child.Skipped)) issues.Add(new(Visible(relative), app.L("Folder enumeration was incomplete")));
            foreach (var child in node.Children) Visit(child, relative == "." ? child.Name : relative + "\\" + child.Name);
        }
        Visit(folder.Root, ".");
        issues.AddRange(result.Issues.Select(issue => new IssueRow(Visible(issue.RelativePath), app.L(issue.Reason))));
        Title = app.L("Similar Text Files"); Width = 960; Height = 850; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected Pair"), () => { if (pairs.SelectedItem is PairRow) app.Try(() => Clipboard.SetText(SelectedReport)); });
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(UI.Text(Visible(path), 12)); body.Children.Add(UI.Text(Summary(), 14)); body.Children.Add(UI.Text(Status(), 12));
        if (result.Pairs.Count == 0) body.Children.Add(UI.Text(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")));
        body.Children.Add(UI.Text(app.L(TextSimilarityNotices.Snapshot), 12));
        body.Children.Add(UI.Text(app.L(TextSimilarityNotices.PairLimit), 12));
        pairs.View = Columns(("Similarity", "Percent", 105), ("First File", "First", 330), ("Second File", "Second", 330));
        pairs.ItemsSource = result.Pairs.Select(pair => new PairRow(pair, pair.Percent.ToString("0.##") + "%", Visible(pair.First), Visible(pair.Second))).ToArray();
        AutomationProperties.SetName(pairs, app.L("Matching Text Pairs")); body.Children.Add(pairs);
        selected = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(selected, app.L("Selected Pair Details")); body.Children.Add(selected);
        body.Children.Add(UI.Text(app.L(TextSimilarityNotices.Privacy), 12));
        pairs.SelectionChanged += (_, _) => {
            copySelected.IsEnabled = pairs.SelectedItem is PairRow;
            if (pairs.SelectedItem is not PairRow row) { selected.Text = ""; return; }
            var text = new StringBuilder(); AppendPair(text, row.Pair); selected.Text = text.ToString();
        };
        tabs.Items.Add(new TabItem { Header = app.L("Matching Text Pairs"), Content = UI.Scroll(body) });
        var skipped = new StackPanel { Margin = new Thickness(12) };
        skipped.Children.Add(UI.Text(Status(), 12)); skipped.Children.Add(UI.Text(app.L(TextSimilarityNotices.Limits), 12));
        skipped.Children.Add(UI.Text(app.L(TextSimilarityNotices.Rules), 12)); skipped.Children.Add(UI.Text(app.L(DuplicateNotices.Scope), 12));
        skipped.Children.Add(UI.Text(app.L(TextSimilarityNotices.Downgrade), 12));
        skipped.Children.Add(UI.Text(string.Format(app.L("Recorded skipped items: {0}"), issues.Count)));
        var list = NativeList(340); list.View = Columns(("Relative Path", "Path", 440), ("Reason", "Reason", 360)); list.ItemsSource = issues;
        AutomationProperties.SetName(list, app.L("Skipped Items")); skipped.Children.Add(list);
        if (issues.Count == 0) skipped.Children.Add(UI.Text(app.L("No recorded skipped items.")));
        tabs.Items.Add(new TabItem { Header = app.L("Skipped Items"), Content = UI.Scroll(skipped) }); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
    private string Summary() => string.Format(app.L("Displayed pairs: {0} · matching pairs: {1} · compared pairs: {2} · loaded files: {3} · attempts: {4} · candidates: {5} · read bytes: {6:N0}"),
        result.Pairs.Count, result.MatchingPairs, result.ComparedPairs, result.Loaded, result.Attempted, result.Candidates, result.BytesRead);
    private string Status()
    {
        var parts = new List<string> { string.Format(app.L("Metadata entries: {0} · recorded skipped items/subtrees: {1}"), folder.Entries, folder.Root.Skipped) };
        if (folder.Root.Incomplete) parts.Add(app.L("Folder metadata scan is partial."));
        if (folder.EntryLimit) parts.Add(app.L("Entry limit reached"));
        if (folder.TimeLimit) parts.Add(app.L("Folder scan time limit reached"));
        if (result.FileLimit) parts.Add(app.L("Text file attempt limit"));
        if (result.ByteLimit) parts.Add(app.L("Text read budget limit"));
        if (result.LineLimit) parts.Add(app.L("Retained text line budget limit"));
        if (result.TimeLimit) parts.Add(app.L("Text comparison time limit"));
        if (result.MatchingPairs > result.Pairs.Count) parts.Add(app.L("Text pair display limit"));
        return string.Join(" · ", parts);
    }
    private StringBuilder Header()
    {
        var text = new StringBuilder(); text.AppendLine(app.L("Similar Text Files")); text.AppendLine(Visible(path));
        text.AppendLine(Summary()); text.AppendLine(Status());
        foreach (string note in new[] { TextSimilarityNotices.Limits, TextSimilarityNotices.Rules, TextSimilarityNotices.Snapshot, TextSimilarityNotices.PairLimit }) text.AppendLine(app.L(note));
        if (result.Pairs.Count == 0) text.AppendLine(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar."));
        return text;
    }
    private void AppendPair(StringBuilder text, SimilarTextPair pair)
    {
        text.AppendLine(string.Format(app.L("Similarity: {0}% · shared distinct lines: {1} · union: {2}"), pair.Percent, pair.CommonLines, pair.UnionLines));
        text.AppendLine(Visible(path.TrimEnd('\\') + "\\" + pair.First)); text.AppendLine(Visible(path.TrimEnd('\\') + "\\" + pair.Second));
    }
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);
    private GridView Columns(params (string Title, string Property, double Width)[] columns)
    {
        var view = new GridView(); foreach (var column in columns) view.Columns.Add(new GridViewColumn { Header = app.L(column.Title), DisplayMemberBinding = new Binding(column.Property), Width = column.Width }); return view;
    }
    private static ListView NativeList(double height)
    {
        var list = new ListView { Height = height, SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 8, 0, 12) };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto); return list;
    }
    private sealed record PairRow(SimilarTextPair Pair, string Percent, string First, string Second);
    private sealed record IssueRow(string Path, string Reason);
}
