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

internal sealed class FolderMapDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly string? context;
    private readonly string? contextLabel;
    private readonly string mapTitle;
    private readonly FolderScanResult result;
    private readonly Stack<FolderSizeNode> history = new();
    private FolderSizeNode current;
    private readonly Canvas map = new() { Height = 260, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 12) };
    private readonly ListView list = new() { MinHeight = 150 };
    private readonly TextBlock location, summary;
    private readonly Button back;
    private IReadOnlyList<FolderSizeNode> rows = Array.Empty<FolderSizeNode>();
    private string Label(FolderSizeNode node) => node.Aggregate ? app.L("Other Items") : context is null?DoctorFacts.VisibleBounded(node.Name, 512):CacheLocations.VisiblePath(node.Name);
    private string Status(FolderSizeNode node) => app.L(node.Issue ?? (node.Incomplete ? "Partial" : "Counted"));
    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder(app.L(mapTitle) + Environment.NewLine + (context is null?DoctorFacts.VisibleBounded(path, 512):CacheLocations.VisiblePath(path)) + Environment.NewLine);
            if(context is not null){if(contextLabel is not null)text.AppendLine(app.L(contextLabel));text.AppendLine(app.L(context));}
            text.AppendLine(location.Text); text.AppendLine(summary.Text);
            text.AppendLine(app.L("Logical file bytes are not physical disk usage or reclaimable space. Hard links can be counted more than once; alternate streams and filesystem overhead are not included. Sizes may change during the scan."));
            foreach (var node in rows) text.AppendLine(Label(node) + " · " + (node.Issue is null ? node.Bytes.ToString("N0", CultureInfo.CurrentCulture) : "—") + " · " + Status(node));
            return text.ToString();
        }
    }
    public FolderMapDialog(App app, string path, FolderScanResult result,string? context=null,string? contextLabel=null,string? contextTitle=null)
    {
        this.app = app; this.path = path; this.result = result;this.context=context;this.contextLabel=contextLabel;mapTitle=contextTitle??(context is null?"Folder Size Map":"User Cache Location Size Map"); current = result.Root;
        Title = app.L(mapTitle); Width = 900; Height = 850; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var body = new StackPanel();
        if(context is not null){if(contextLabel is not null)body.Children.Add(UI.Text(app.L(contextLabel),14));body.Children.Add(UI.Text(app.L(context),12));}
        body.Children.Add(UI.Text(context is null?DoctorFacts.VisibleBounded(path, 512):CacheLocations.VisiblePath(path), 12));
        body.Children.Add(UI.Text(app.L("Read-only folder metadata, up to 4 levels, 20,000 entries and about 30 seconds. System items, links, cloud placeholders and unavailable subtrees are skipped. Partial results are not the full folder size. A blocked system call may take longer to return."), 12));
        body.Children.Add(UI.Text(app.L("Logical file bytes are not physical disk usage or reclaimable space. Hard links can be counted more than once; alternate streams and filesystem overhead are not included. Sizes may change during the scan."), 12));
        body.Children.Add(UI.Text(app.L("Folder and file names are visible and included when you copy the current view. Nothing is saved or sent automatically. Double-click a folder or use Open Selected Folder to explore counted results; this does not scan again."), 12));
        location = UI.Text("", 18); summary = UI.Text("", 12); body.Children.Add(location); body.Children.Add(summary); body.Children.Add(map);
        var view = new GridView();
        foreach (var item in new[] { ("Name", "Name", 300d), ("Kind", "Kind", 90d), ("Logical Bytes", "Size", 150d), ("Status", "Status", 100d) })
            view.Columns.Add(new GridViewColumn { Header = app.L(item.Item1), DisplayMemberBinding = new Binding(item.Item2), Width = item.Item3 });
        list.View = view; AutomationProperties.SetName(list, app.L(mapTitle)); body.Children.Add(list);
        var footer = new WrapPanel();
        back = UI.Button(app.L("Back"), () => { if (history.Count > 0) { current = history.Pop(); Refresh(); } }); footer.Children.Add(back);
        footer.Children.Add(UI.Button(app.L("Open Selected Folder"), () => { if (list.SelectedItem is Row row) Enter(row.Node); }));
        footer.Children.Add(UI.Button(app.L("Copy Current View"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(body)); Content = root;
        if(context is not null){SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);}
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Row row) Enter(row.Node); };
        map.SizeChanged += (_, _) => Draw(); Refresh();
    }
    private void Enter(FolderSizeNode node)
    {
        if (!node.Directory || node.Children.Count == 0) return;
        history.Push(current); current = node; Refresh();
    }
    private void Refresh()
    {
        rows = FolderSizes.Display(current); location.Text = Label(current);
        summary.Text = string.Format(app.L("Logical bytes: {0:N0} · counted files: {1} · skipped items/subtrees: {2}"), current.Bytes, current.Files, current.Skipped)
            + " · " + app.L(current.Incomplete ? "Partial" : "Counted")
            + (result.EntryLimit ? " · " + app.L("Entry limit reached") : "") + (result.TimeLimit ? " · " + app.L("Time limit reached") : "");
        list.ItemsSource = rows.Select(x => new Row(x, Label(x), app.L(x.Directory ? "Folder" : "File"), x.Issue is null ? x.Bytes.ToString("N0", CultureInfo.CurrentCulture) : "—", Status(x))).ToArray();
        back.IsEnabled = history.Count > 0; Draw();
    }
    private void Draw()
    {
        map.Children.Clear();
        var tiles = FolderMapLayout.Build(rows, Math.Max(0, map.ActualWidth), map.Height);
        if (tiles.Count == 0) { map.Children.Add(UI.Text(app.L("No counted file bytes in this view."))); return; }
        foreach (var tile in tiles)
        {
            var node = rows[tile.Index]; string label = Label(node) + Environment.NewLine + node.Bytes.ToString("N0", CultureInfo.CurrentCulture);
            var button = new Button { Width = tile.Width, Height = tile.Height, MinWidth = 0, MinHeight = 0, Padding = new Thickness(4), BorderThickness = new Thickness(1), ClipToBounds = true, ToolTip = label,
                Content = new TextBlock { Text = tile.Width >= 60 && tile.Height >= 35 ? label : "", TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis } };
            AutomationProperties.SetName(button, label); button.Click += (_, _) => Enter(node);
            Canvas.SetLeft(button, tile.X); Canvas.SetTop(button, tile.Y); map.Children.Add(button);
        }
    }
    private sealed record Row(FolderSizeNode Node, string Name, string Kind, string Size, string Status);
}
