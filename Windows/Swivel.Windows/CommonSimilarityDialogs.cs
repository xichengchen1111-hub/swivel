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

internal static class CommonSimilarityNotices
{
    internal const string Scope = "Select one to six Windows-reported common folders for one similarity comparison across them. Nothing is selected initially. Reviewing locations does not read child files; the next confirmation lists every selected path before reading contents. Existing local path, system, link and cloud protections remain in force. No files are opened in another app, changed, deleted or sent to an assistant.";
    internal const string Metadata = "All selected folders share 20,000 metadata entries and about 30 soft seconds, up to four levels below each root. Remaining entry and time shares are divided among remaining distinct paths; unused shares pass to later roots. Earlier partial roots are not rescanned. A scan that fails without a count consumes its reserved share. Partial, failed, repeated and unattempted roots remain visible. Blocked system or codec calls may return later; cancellation waits for cleanup.";
    internal const string TextLimits = "All selected folders share one text-content budget: 200 file attempts, 1 MiB per file, 16 MiB of requested reads, 20,000 lines per file and 200,000 retained per-file distinct lines in total. Shared lines count again in each file. Reading, decoding and comparisons share about 60 soft seconds. These limits are not multiplied by the number of folders.";
    internal const string ImageLimits = "All selected folders share one image-content budget: 200 file attempts, 16 MiB per file and 128 MiB of requested reads. Reading, decoding and comparisons share about 60 soft seconds. Decoded images are limited to 16 megapixels and 16,384 pixels per side. These limits are not multiplied by the number of folders.";
    internal const string Roots = "Roots may overlap or be on different local volumes. Exact repeated paths are skipped; case variants can be examined separately. Full volume/file identities deduplicate overlapping files and hard links. Results are historical and partial, not a simultaneous snapshot. Do not add root logical sizes as profile usage, physical usage or reclaimable space.";
    internal const string Privacy = "Names and full paths are visible. Copy Report includes all roots, displayed pairs and recorded issues. Copy Selected Pair includes both paths and every root's limits. Copies exceeding 4 Mi UTF-16 units are refused in full. File contents and image pixels are never copied, saved or sent automatically.";
    internal const string CopyLimit = "This copy exceeds the complete-report limit. Select one pair instead.";
    internal static string Title(CommonSimilarityKind kind) => kind switch { CommonSimilarityKind.Text => "Compare Text in Common Folders…", CommonSimilarityKind.Images => "Compare Images in Common Folders…", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    internal static IEnumerable<string> Notes(CommonSimilarityKind kind)
    {
        yield return Scope; yield return Metadata; yield return kind == CommonSimilarityKind.Text ? TextLimits : ImageLimits;
        yield return kind == CommonSimilarityKind.Text ? TextSimilarityNotices.Rules : PhotoSimilarityNotices.Rules;
        if (kind == CommonSimilarityKind.Images) yield return PhotoSimilarityNotices.Detail;
        yield return kind == CommonSimilarityKind.Text ? TextSimilarityNotices.Snapshot : PhotoSimilarityNotices.Snapshot;
        yield return TextSimilarityNotices.PairLimit; yield return Roots; yield return Privacy; yield return TextSimilarityNotices.Downgrade;
    }
}

internal sealed class CommonSimilarityChooser : Window
{
    private readonly CommonFolderChoice[] choices;
    private readonly HashSet<CommonFolderKind> selected = new();
    internal CommonFolderChoice[] Selected => choices.Where(c => c.CanAttempt && selected.Contains(c.Kind)).ToArray();
    internal CommonSimilarityChooser(App app, IReadOnlyList<CommonFolderChoice> source, CommonSimilarityKind kind)
    {
        Title = app.L(CommonSimilarityNotices.Title(kind)); choices = source.ToArray();
        if (choices.Length != 6 || choices.Any(c => c is null) || choices.Select(c => c.Kind).Distinct().Count() != 6) throw new ArgumentException("Invalid common folder choices.");
        Width = 880; Height = 800; MinWidth = 500; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var review = UI.Button(app.L("Review Selected Comparison…"), () => { if (Selected.Length > 0) DialogResult = true; }); review.IsEnabled = false; footer.Children.Add(review);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        foreach (string note in new[] { CommonSimilarityNotices.Scope, CommonSimilarityNotices.Metadata, kind == CommonSimilarityKind.Text ? CommonSimilarityNotices.TextLimits : CommonSimilarityNotices.ImageLimits }) body.Children.Add(UI.Text(app.L(note), 12));
        foreach (var choice in choices)
        {
            var box = new CheckBox { Content = app.L(CommonFolders.Name(choice.Kind)), IsEnabled = choice.CanAttempt, IsChecked = false, Margin = new Thickness(0, 16, 0, 4) };
            AutomationProperties.SetName(box, app.L(CommonFolders.Name(choice.Kind)));
            box.Checked += (_, _) => { if (choice.CanAttempt) selected.Add(choice.Kind); review.IsEnabled = Selected.Length > 0; };
            box.Unchecked += (_, _) => { selected.Remove(choice.Kind); review.IsEnabled = Selected.Length > 0; }; body.Children.Add(box);
            body.Children.Add(new TextBox { Text = CacheLocations.VisiblePath(choice.ReportedPath), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            string status = app.L(choice.Issue ?? "Path passed basic checks; access and safety are checked only after you confirm content comparison.");
            if (choice.NativeError is uint error) status += " · " + app.L("Folder Lookup HRESULT") + " 0x" + error.ToString("X8", CultureInfo.InvariantCulture);
            if (choice.Truncated) status += " · " + app.L("Reported path preview was truncated; this location cannot be compared.");
            body.Children.Add(UI.Text(status, 12));
        }
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

internal sealed class CommonSimilarityConfirmation : Window
{
    internal CommonSimilarityConfirmation(App app, IReadOnlyList<CommonFolderChoice> selection, CommonSimilarityKind kind)
    {
        Title = app.L(CommonSimilarityNotices.Title(kind)); Width = 860; Height = 740; MinWidth = 500; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Start Comparison"), () => DialogResult = true)); var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        body.Children.Add(UI.Text(app.L("Read file contents across these selected common folders?"), 18));
        foreach (var choice in selection) { body.Children.Add(UI.Text(app.L(CommonFolders.Name(choice.Kind)), 14)); body.Children.Add(new TextBox { Text = CacheLocations.VisiblePath(choice.Path!), IsReadOnly = true, TextWrapping = TextWrapping.Wrap }); }
        foreach (string note in CommonSimilarityNotices.Notes(kind)) body.Children.Add(UI.Text(app.L(note), 12));
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

internal sealed class CommonSimilarityDialog : Window
{
    internal const int CopyLimit = 4 * 1024 * 1024;
    private readonly App app;
    private readonly CommonSimilarityResult result;
    private readonly Dictionary<CommonFolderKind, CommonDuplicateRootResult> roots;
    private readonly List<IssueRow> issues = new();
    private readonly PairRow[] matches;
    private readonly ListView pairs = NativeList(300);
    internal string CurrentReport => Report(null);
    internal string SelectedReport => Report(pairs.SelectedItem as PairRow);
    internal CommonSimilarityDialog(App app, CommonSimilarityResult result)
    {
        this.app = app; this.result = result;
        if (result.Kind == CommonSimilarityKind.Text ? result.Text is null || result.Images is not null : result.Kind != CommonSimilarityKind.Images || result.Images is null || result.Text is not null) throw new ArgumentException("Invalid similarity result.");
        roots = result.Roots.ToDictionary(r => r.Choice.Kind);
        Title = app.L(CommonSimilarityNotices.Title(result.Kind)); Width = 1000; Height = 830; MinWidth = 540; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var root in result.Roots)
        {
            if (root.Issue is string issue) issues.Add(new(DisplayPath(root.Choice.Kind, ""), app.L(issue)));
            if (root.Scan is not { } scan) continue;
            void Visit(FolderSizeNode node, string relative)
            {
                if (node.Issue is string reason) issues.Add(new(DisplayPath(root.Choice.Kind, relative), app.L(reason)));
                else if (node.Skipped > node.Children.Sum(c => c.Skipped)) issues.Add(new(DisplayPath(root.Choice.Kind, relative), app.L("Folder enumeration was incomplete")));
                foreach (var child in node.Children) Visit(child, relative.Length == 0 ? child.Name : relative + "\\" + child.Name);
            }
            Visit(scan.Root, "");
        }
        foreach (var issue in result.Text?.Issues ?? result.Images!.Issues) issues.Add(new(FullPath(issue.RelativePath), app.L(issue.Reason)));
        matches = result.Text is { } text ? text.Pairs.Select(p => new PairRow(FullPath(p.First), FullPath(p.Second), p.Percent, p.CommonLines, p.UnionLines, null)).ToArray()
            : result.Images!.Pairs.Select(p => new PairRow(FullPath(p.First), FullPath(p.Second), p.Percent, null, null, p.DifferentBits)).ToArray();
        var rootPanel = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); rootPanel.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var copy = UI.Button(app.L("Copy Selected Pair"), () => { if (pairs.SelectedItem is PairRow) app.Try(() => Clipboard.SetText(SelectedReport)); }); copy.IsEnabled = false; footer.Children.Add(copy);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var tabs = new TabControl(); rootPanel.Children.Add(tabs); Content = rootPanel;
        var rootBody = new StackPanel { Margin = new Thickness(12) };
        foreach (string note in new[] { CommonSimilarityNotices.Scope, CommonSimilarityNotices.Metadata, CommonSimilarityNotices.Roots }) rootBody.Children.Add(UI.Text(app.L(note), 12));
        var rootList = NativeList(230); rootList.View = Columns(("Folder", "Name", 150), ("Status", "Status", 290), ("Metadata Entries", "Entries", 130)); rootList.ItemsSource = result.Roots.Select(r => new RootRow(this, r)).ToArray(); AutomationProperties.SetName(rootList, app.L("Selected Roots")); rootBody.Children.Add(rootList);
        var rootDetail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        rootList.SelectionChanged += (_, _) => rootDetail.Text = rootList.SelectedItem is RootRow row ? RootDetail(row.Result) : ""; rootBody.Children.Add(rootDetail); AddTab(tabs, "Selected Roots", rootBody);
        var matchBody = new StackPanel { Margin = new Thickness(12) }; matchBody.Children.Add(UI.Text(Summary(), 14)); matchBody.Children.Add(UI.Text(Status(), 12));
        if (matches.Length == 0) matchBody.Children.Add(UI.Text(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")));
        matchBody.Children.Add(UI.Text(app.L(result.Kind == CommonSimilarityKind.Text ? TextSimilarityNotices.Snapshot : PhotoSimilarityNotices.Snapshot), 12)); matchBody.Children.Add(UI.Text(app.L(TextSimilarityNotices.PairLimit), 12));
        pairs.View = Columns(("Similarity", "Percent", 105), ("First File", "First", 365), ("Second File", "Second", 365)); pairs.ItemsSource = matches;
        string matchTitle = result.Kind == CommonSimilarityKind.Text ? "Matching Text Pairs" : "Matching Image Pairs"; AutomationProperties.SetName(pairs, app.L(matchTitle)); matchBody.Children.Add(pairs);
        var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; AutomationProperties.SetName(detail, app.L("Selected Pair Details")); matchBody.Children.Add(detail);
        pairs.SelectionChanged += (_, _) => { copy.IsEnabled = pairs.SelectedItem is PairRow; detail.Text = pairs.SelectedItem is PairRow row ? PairText(row) : ""; }; matchBody.Children.Add(UI.Text(app.L(CommonSimilarityNotices.Privacy), 12)); AddTab(tabs, matchTitle, matchBody);
        var skipped = new StackPanel { Margin = new Thickness(12) }; skipped.Children.Add(UI.Text(Summary(), 14)); skipped.Children.Add(UI.Text(Status(), 12));
        foreach (string note in CommonSimilarityNotices.Notes(result.Kind)) skipped.Children.Add(UI.Text(app.L(note), 12));
        skipped.Children.Add(UI.Text(string.Format(app.L("Recorded skipped items: {0}"), issues.Count), 14)); if (issues.Count == 0) skipped.Children.Add(UI.Text(app.L("No recorded skipped items.")));
        var issueList = NativeList(420); issueList.View = Columns(("Full Path", "Path", 540), ("Reason", "Reason", 350)); issueList.ItemsSource = issues; AutomationProperties.SetName(issueList, app.L("Skipped Items")); skipped.Children.Add(issueList); AddTab(tabs, "Skipped Items", skipped);
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
    private string Summary()
    {
        var t = result.Text; var p = result.Images;
        return string.Format(app.L("Displayed pairs: {0} · matching pairs: {1} · compared pairs: {2} · loaded files: {3} · attempts: {4} · candidates: {5} · read bytes: {6:N0}"), matches.Length, t?.MatchingPairs ?? p!.MatchingPairs, t?.ComparedPairs ?? p!.ComparedPairs, t?.Loaded ?? p!.Loaded, t?.Attempted ?? p!.Attempted, t?.Candidates ?? p!.Candidates, t?.BytesRead ?? p!.BytesRead);
    }
    private string Status()
    {
        var status = new List<string>(); if (result.Roots.Any(r => r.Issue is not null || r.Scan?.Root.Incomplete == true)) status.Add(app.L("Folder metadata scan is partial."));
        if (result.Roots.Any(r => r.Scan?.EntryLimit == true)) status.Add(app.L("Entry limit reached")); if (result.Roots.Any(r => r.Scan?.TimeLimit == true)) status.Add(app.L("Folder scan time limit reached"));
        if (result.Text is { } t) { if (t.FileLimit) status.Add(app.L("Text file attempt limit")); if (t.ByteLimit) status.Add(app.L("Text read budget limit")); if (t.LineLimit) status.Add(app.L("Retained text line budget limit")); if (t.TimeLimit) status.Add(app.L("Text comparison time limit")); if (t.MatchingPairs > t.Pairs.Count) status.Add(app.L("Text pair display limit")); }
        if (result.Images is { } p) { if (p.FileLimit) status.Add(app.L("Image file attempt limit")); if (p.ByteLimit) status.Add(app.L("Image read budget limit")); if (p.TimeLimit) status.Add(app.L("Image comparison time limit")); if (p.MatchingPairs > p.Pairs.Count) status.Add(app.L("Image pair display limit")); }
        return string.Join(" · ", status);
    }
    private string RootStatus(CommonDuplicateRootResult root) => app.L(root.Issue ?? (root.Scan!.Root.Incomplete ? "Partial" : "Counted")) + (root.Error is uint code ? " · " + app.L("Windows Error Code") + " " + code.ToString(CultureInfo.InvariantCulture) : "");
    private string RootDetail(CommonDuplicateRootResult root)
    {
        var text = new StringBuilder(app.L(CommonFolders.Name(root.Choice.Kind)) + Environment.NewLine + DisplayPath(root.Choice.Kind, "") + Environment.NewLine + RootStatus(root) + Environment.NewLine);
        text.AppendLine(string.Format(app.L("Allocated metadata entries: {0} · shared-clock deadline: {1} seconds"), root.EntryQuota, root.DeadlineSeconds));
        if (root.Scan is { } scan) { text.AppendLine(string.Format(app.L("Logical bytes: {0:N0} · counted files: {1} · skipped items/subtrees: {2}"), scan.Root.Bytes, scan.Root.Files, scan.Root.Skipped)); text.AppendLine(app.L("Metadata Entries") + ": " + scan.Entries.ToString(CultureInfo.CurrentCulture)); if (scan.EntryLimit) text.AppendLine(app.L("Entry limit reached")); if (scan.TimeLimit) text.AppendLine(app.L("Folder scan time limit reached")); }
        text.Append(app.L("Result Recorded")).Append(": ").Append(root.Recorded.ToString("g", CultureInfo.CurrentCulture)); return text.ToString();
    }
    private string FullPath(string key) { if (!result.Sources.TryGetValue(key, out var source)) throw new UserError("Unavailable or unsupported"); return DisplayPath(source.Kind, source.RelativePath); }
    private string DisplayPath(CommonFolderKind kind, string relative)
    {
        var choice = roots[kind].Choice; string path = choice.Path ?? choice.ReportedPath; // a refused location is still shown as reported
        return relative.Length == 0 ? CacheLocations.VisiblePath(path) : CacheLocations.VisiblePath(path.TrimEnd('\\')) + "\\" + string.Join("\\", relative.Split('\\').Select(CacheLocations.VisiblePath));
    }
    private string PairText(PairRow row) => (row.Bits is int bits ? string.Format(app.L("Grid comparison agreement: {0}% · different bits: {1} of 64"), row.Score, bits) : string.Format(app.L("Similarity: {0}% · shared distinct lines: {1} · union: {2}"), row.Score, row.Common, row.Union)) + Environment.NewLine + row.First + Environment.NewLine + row.Second;
    private string Report(PairRow? selected)
    {
        var text = new StringBuilder(); void Add(string line) { if (line.Length > CopyLimit - Environment.NewLine.Length - text.Length) throw new UserError(CommonSimilarityNotices.CopyLimit); text.AppendLine(line); }
        Add(app.L("Windows Doctor") + " · " + Title); Add(Summary()); Add(Status()); foreach (string note in CommonSimilarityNotices.Notes(result.Kind)) Add(app.L(note)); foreach (var root in result.Roots) { Add(""); Add(RootDetail(root)); }
        if (selected is not null) Add(PairText(selected)); else { if (matches.Length == 0) Add(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")); foreach (var pair in matches) Add(PairText(pair)); Add(app.L("Skipped Items")); if (issues.Count == 0) Add(app.L("No recorded skipped items.")); foreach (var issue in issues) Add(issue.Path + " · " + issue.Reason); } return text.ToString();
    }
    private void AddTab(TabControl tabs, string title, StackPanel body) => tabs.Items.Add(new TabItem { Header = app.L(title), Content = UI.Scroll(body), Padding = new Thickness(12, 7, 12, 7) });
    private GridView Columns(params (string Title, string Property, double Width)[] columns) { var view = new GridView(); foreach (var c in columns) view.Columns.Add(new GridViewColumn { Header = app.L(c.Title), DisplayMemberBinding = new Binding(c.Property), Width = c.Width }); return view; }
    private static ListView NativeList(double height) { var list = new ListView { Height = height, SelectionMode = SelectionMode.Single }; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true); return list; }
    private sealed record RootRow(CommonSimilarityDialog Owner, CommonDuplicateRootResult Result) { public string Name => Owner.app.L(CommonFolders.Name(Result.Choice.Kind)); public string Status => Owner.RootStatus(Result); public string Entries => Result.Scan?.Entries.ToString(CultureInfo.CurrentCulture) ?? "—"; }
    private sealed record PairRow(string First, string Second, decimal Score, int? Common, int? Union, int? Bits) { public string Percent => Score.ToString("0.##", CultureInfo.CurrentCulture) + "%"; }
    private sealed record IssueRow(string Path, string Reason);
}
