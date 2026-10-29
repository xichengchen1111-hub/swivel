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

internal static class PhotoSimilarityNotices
{
    internal const string Limits = "The chosen folder plus 4 levels: up to 20,000 metadata entries and about 30 seconds. Image comparison: up to 200 file attempts, 16 MiB per file, 128 MiB read and about 60 seconds including decoding and grid comparisons. Decoded images are limited to 16 megapixels and 16,384 pixels per side. Blocked system or codec calls may return later.";
    internal const string Rules = "Only single-frame SDR PNG, JPEG, BMP, TIFF, TGA, PSD and supported static GIF are decoded. Alpha is composited onto white, then integer brightness averages are calculated in a 9-by-8 grid. The 64 horizontal comparisons produce a signature. At most 6 differing bits qualify (at least 90.625% agreement). Different image formats may be compared. Displayed percentages are truncated to two decimals. This is not an object, face or semantic recognition score.";
    internal const string Detail = "Images smaller than 9-by-8 pixels, grid brightness ranges below 16, or signatures with fewer than 4 or more than 60 set bits are skipped. These heuristics can miss valid images, including flat pictures and directional gradients. Animation, HDR, high bit depth, CMYK and unsupported formats are refused. HEIC, WebP and AVIF remain pending.";
    internal const string Snapshot = "These are historical grid-similarity hints, not identical-image proof or cleanup advice. Even 100% agreement can describe different colors, details or aspect ratios. Each pair stands alone; matches do not form related groups. Rotation, crop and composition changes can be missed. The comparison itself does not preview, open, copy or delete images, and files may change afterward.";
    internal const string Privacy = TextSimilarityNotices.Privacy;
    internal const string PairLimit = TextSimilarityNotices.PairLimit;
    internal const string Downgrade = TextSimilarityNotices.Downgrade;
}

internal sealed class PhotoSimilarityConfirmation : Window
{
    public PhotoSimilarityConfirmation(App app, string path)
    {
        Title = app.L("Find Similar Images…"); Width = 760; Height = 650; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var start = UI.Button(app.L("Start Comparison"), () => DialogResult = true); start.IsDefault = true; footer.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Read image contents in this folder to find visually similar pairs?"), 18));
        body.Children.Add(UI.Text(DoctorFacts.VisibleBounded(path, 512)));
        foreach (string note in new[] { PhotoSimilarityNotices.Limits, PhotoSimilarityNotices.Rules, PhotoSimilarityNotices.Detail, DuplicateNotices.Scope, PhotoSimilarityNotices.Snapshot, PhotoSimilarityNotices.PairLimit, PhotoSimilarityNotices.Privacy, PhotoSimilarityNotices.Downgrade })
            body.Children.Add(UI.Text(app.L(note), 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

internal sealed class PhotoSimilarityDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult folder;
    private readonly PhotoSimilarityResult result;
    private readonly List<IssueRow> issues = new();
    private readonly ListView pairs = NativeList(290);
    private readonly TextBox selected;
    private readonly Button copySelected;
    private readonly SimilarPairCleanup cleanup;
    internal int Moved => cleanup.Moved; // also those Windows removed outright
    internal string CurrentReport
    {
        get
        {
            var text = Header();
            foreach (var pair in result.Pairs) AppendPair(text, pair);
            text.AppendLine(app.L("Skipped Items"));
            foreach (var issue in issues) text.AppendLine(issue.Path + " · " + issue.Reason);
            if (issues.Count == 0) text.AppendLine(app.L("No recorded skipped items."));
            cleanup.AppendReport(text, result.Pairs.SelectMany(pair => new[] { pair.First, pair.Second }));
            return text.ToString();
        }
    }
    internal string SelectedReport
    {
        get { var text = Header(); if (pairs.SelectedItem is PairRow row) AppendPair(text, row.Pair); return text.ToString(); }
    }
    public PhotoSimilarityDialog(App app, string path, FolderScanResult folder, PhotoSimilarityResult result)
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
        Title = app.L("Similar Images"); Width = 960; Height = 850; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        copySelected = UI.Button(app.L("Copy Selected Pair"), () => { if (pairs.SelectedItem is PairRow) app.Try(() => Clipboard.SetText(SelectedReport)); });
        copySelected.IsEnabled = false; footer.Children.Add(copySelected);
        cleanup = new SimilarPairCleanup(app, this, path, result.Stamps, pairs, () => pairs.SelectedItem is PairRow row ? (row.Pair.First, row.Pair.Second) : null);
        footer.Children.Add(cleanup.Move); footer.Children.Add(cleanup.Undo);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(UI.Text(Visible(path), 12)); body.Children.Add(UI.Text(Summary(), 14)); body.Children.Add(UI.Text(Status(), 12));
        if (result.Pairs.Count == 0) body.Children.Add(UI.Text(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")));
        body.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Snapshot), 12));
        body.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.PairLimit), 12));
        pairs.View = Columns(("Similarity", "Percent", 105), ("First File", "First", 330), ("Second File", "Second", 330));
        pairs.ItemsSource = result.Pairs.Select(pair => new PairRow(pair, pair.Percent.ToString("0.##") + "%", Visible(pair.First), Visible(pair.Second))).ToArray();
        AutomationProperties.SetName(pairs, app.L("Matching Image Pairs")); body.Children.Add(pairs);
        selected = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(selected, app.L("Selected Pair Details")); body.Children.Add(selected);
        body.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Privacy), 12));
        body.Children.Add(UI.Text(app.L(SimilarPairCleanup.Notice), 12)); body.Children.Add(cleanup.Files); body.Children.Add(cleanup.Status);
        void ShowSelected()
        {
            copySelected.IsEnabled = pairs.SelectedItem is PairRow;
            if (pairs.SelectedItem is not PairRow row) { selected.Text = ""; return; }
            var text = new StringBuilder(); AppendPair(text, row.Pair); selected.Text = text.ToString();
        }
        pairs.SelectionChanged += (_, _) => ShowSelected(); cleanup.Changed += ShowSelected;
        tabs.Items.Add(new TabItem { Header = app.L("Matching Image Pairs"), Content = UI.Scroll(body) });
        var skipped = new StackPanel { Margin = new Thickness(12) };
        skipped.Children.Add(UI.Text(Status(), 12)); skipped.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Limits), 12));
        skipped.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Rules), 12)); skipped.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Detail), 12)); skipped.Children.Add(UI.Text(app.L(DuplicateNotices.Scope), 12));
        skipped.Children.Add(UI.Text(app.L(PhotoSimilarityNotices.Downgrade), 12));
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
        if (result.FileLimit) parts.Add(app.L("Image file attempt limit"));
        if (result.ByteLimit) parts.Add(app.L("Image read budget limit"));
        if (result.TimeLimit) parts.Add(app.L("Image comparison time limit"));
        if (result.MatchingPairs > result.Pairs.Count) parts.Add(app.L("Image pair display limit"));
        return string.Join(" · ", parts);
    }
    private StringBuilder Header()
    {
        var text = new StringBuilder(); text.AppendLine(app.L("Similar Images")); text.AppendLine(Visible(path));
        text.AppendLine(Summary()); text.AppendLine(Status());
        foreach (string note in new[] { PhotoSimilarityNotices.Limits, PhotoSimilarityNotices.Rules, PhotoSimilarityNotices.Detail, PhotoSimilarityNotices.Snapshot, PhotoSimilarityNotices.PairLimit }) text.AppendLine(app.L(note));
        if (result.Pairs.Count == 0) text.AppendLine(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar."));
        return text;
    }
    private void AppendPair(StringBuilder text, SimilarPhotoPair pair)
    {
        text.AppendLine(string.Format(app.L("Grid comparison agreement: {0}% · different bits: {1} of 64"), pair.Percent, pair.DifferentBits));
        text.AppendLine(cleanup.Line(pair.First)); text.AppendLine(cleanup.Line(pair.Second));
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
    private sealed record PairRow(SimilarPhotoPair Pair, string Percent, string First, string Second);
    private sealed record IssueRow(string Path, string Reason);
}
