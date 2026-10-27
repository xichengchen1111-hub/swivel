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

/// The measured temporary folder's direct items unchanged for seven days, to review and move
/// to the Recycle Bin, with the size map one click away.
internal sealed class TempCleanupDialog : Window
{
    private readonly App app;
    private readonly string path;
    private readonly FolderScanResult scan;
    private readonly TempCleanupPlan plan;
    private readonly ListView items;
    private readonly RecycleBinSession<TempCandidate> bin;
    private readonly TextBlock summary;
    private readonly Button selectAll, showMap;
    // Items moved in this window at any time: once restored by Undo they count as recently
    // changed (moving and restoring are renames), so they are not offered again.
    private readonly HashSet<TempCandidate> moved = new();
    internal int Moved => bin.Gone.Count; // also those Windows removed outright

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Old Temporary Files")); text.AppendLine(Visible(path)); text.AppendLine(Summary());
            text.AppendLine(app.L(TempCleanup.Notice));
            if (plan.Old.Count == 0) text.AppendLine(app.L("No items in this folder were left unchanged for 7 days."));
            foreach (var item in plan.Old.Where(item => !moved.Contains(item))) text.AppendLine(Line(item));
            bin.AppendReport(text, plan.Old);
            return text.ToString();
        }
    }

    public TempCleanupDialog(App app, string path, FolderScanResult scan, DateTime nowUtc)
    {
        this.app = app; this.path = path; this.scan = scan; plan = TempCleanup.Plan(scan, nowUtc);
        Title = app.L("Old Temporary Files"); Width = 940; Height = 800; MinWidth = 540; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        items = new ListView { Height = 380, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 8, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(items, true); VirtualizingPanel.SetVirtualizationMode(items, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(items, true); ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Auto);
        bin = new RecycleBinSession<TempCandidate>(app, this, new(
            item => Visible(item.Name),
            item => Visible(Absolute(item)) + " · " + item.Bytes.ToString("N0", CultureInfo.CurrentCulture),
            (item, owner) => NativeFiles.RecycleTempItem(Absolute(item), item.Directory, item.LastWrite, item.Bytes, plan.Cutoff, owner),
            (item, stored) => NativeFiles.RestoreRecycled(Absolute(item), stored, item.Directory),
            "Move 1 temporary item to the Recycle Bin?", "Move {0} temporary items to the Recycle Bin?", "An app may still need an old temporary file. Items that changed or are in use may be left in place, and a program still running from an old file can be affected."),
            () => items.SelectedItems.OfType<Row>().Select(row => row.Item).Where(item => !bin!.Gone.Contains(item)).ToArray(), Refresh);
        selectAll = UI.Button(app.L("Select All Listed"), () => items.SelectAll());
        footer.Children.Add(selectAll); footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo);
        // The map shows the measurement taken before any moves in this window.
        showMap = UI.Button(app.L("Show Size Map…"), () =>
        {
            var map = new FolderMapDialog(app, path, scan, CacheLocations.ResultContext, CacheLocations.Name(CacheLocationKind.Temporary)) { Owner = this };
            map.ShowDialog();
        });
        footer.Children.Add(showMap);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(UI.Text(Visible(path), 12));
        summary = UI.Text(Summary(), 14); body.Children.Add(summary);
        body.Children.Add(UI.Text(app.L(TempCleanup.Notice), 12));
        if (plan.Old.Count == 0) body.Children.Add(UI.Text(app.L("No items in this folder were left unchanged for 7 days.")));
        var view = new GridView();
        foreach (var (title, property, width) in new[] { ("Name", "Name", 380.0), ("Kind", "Kind", 90.0), ("Size", "Size", 150.0), ("Last Changed", "Changed", 200.0) })
            view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
        items.View = view; AutomationProperties.SetName(items, app.L("Old Temporary Files"));
        items.SelectionChanged += (_, _) => bin.UpdateButtons();
        body.Children.Add(items); body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private string Summary()
    {
        var text = string.Format(app.L("Unchanged for 7 days: {0} items · {1:N0} bytes · changed recently: {2} · not listed (skipped, partial or undated): {3}"),
            plan.Old.Count(item => !moved.Contains(item)), plan.Old.Where(item => !moved.Contains(item)).Sum(item => item.Bytes), plan.Recent, plan.NotListed);
        if (scan.Root.Incomplete || scan.EntryLimit || scan.TimeLimit) text += " · " + app.L("Folder metadata scan is partial.");
        return text;
    }

    private void Refresh()
    {
        moved.UnionWith(bin.Gone);
        items.ItemsSource = plan.Old.Where(item => !moved.Contains(item)).Select(item => new Row(this, item)).ToArray();
        items.IsEnabled = selectAll.IsEnabled = showMap.IsEnabled = bin.Idle;
        summary.Text = Summary();
        bin.UpdateButtons();
    }

    private string Line(TempCandidate item) => Visible(Absolute(item)) + " · " + app.L(item.Directory ? "Folder" : "File") + " · " + item.Bytes.ToString("N0", CultureInfo.CurrentCulture) + " · " + Changed(item);
    private static string Changed(TempCandidate item) => DateTime.FromFileTimeUtc(item.Newest).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    private string Absolute(TempCandidate item) => System.IO.Path.Combine(path, item.Name);
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);

    private sealed record Row(TempCleanupDialog Owner, TempCandidate Item)
    {
        public string Name => Visible(Item.Name);
        public string Kind => Owner.app.L(Item.Directory ? "Folder" : "File");
        public string Size => Item.Bytes.ToString("N0", CultureInfo.CurrentCulture);
        public string Changed => TempCleanupDialog.Changed(Item);
    }
}
