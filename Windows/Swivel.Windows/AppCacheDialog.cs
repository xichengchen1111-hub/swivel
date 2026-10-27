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

/// The browser and app cache folders found at their default places, to review and move to
/// the Recycle Bin while their app is closed.
internal sealed class AppCacheDialog : Window
{
    private readonly App app;
    private readonly AppCacheResult result;
    private readonly Func<IReadOnlySet<string>?> runningNames;
    private readonly ListView items;
    private readonly RecycleBinSession<AppCacheItem> bin;
    private readonly TextBlock summary, running;
    private readonly Button selectAll, checkRunning;
    private IReadOnlySet<string>? processes;
    private Dictionary<string, bool> fixedDrives = new(StringComparer.OrdinalIgnoreCase);
    internal int Moved => bin.Gone.Count; // also those Windows removed outright

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Browser and App Caches")); text.AppendLine(Summary());
            text.AppendLine(app.L(AppCaches.Notice)); text.AppendLine(app.L(AppCaches.Limits));
            foreach (string line in AppLines()) text.AppendLine(line);
            foreach (var item in Listed()) text.AppendLine(Line(item));
            bin.AppendReport(text, result.Items);
            return text.ToString();
        }
    }

    public AppCacheDialog(App app, AppCacheResult result, Func<IReadOnlySet<string>?> runningNames)
    {
        this.app = app; this.result = result; this.runningNames = runningNames;
        Title = app.L("Browser and App Caches"); Width = 1040; Height = 820; MinWidth = 560; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        items = new ListView { Height = 360, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 8, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(items, true); VirtualizingPanel.SetVirtualizationMode(items, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(items, true); ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Auto);
        bin = new RecycleBinSession<AppCacheItem>(app, this, new(
            item => Visible(item.Folder.Path),
            item => item.Folder.App.Name + " · " + Visible(item.Folder.Path) + " · " + AppCaches.Size(item.Size, app.L),
            (item, owner) => NativeFiles.RecycleAppCache(item.Folder.Path, () => AppCacheReader.IsRunning(item.Folder.App), owner),
            (item, stored) => NativeFiles.RestoreAppCache(item.Folder.Path, stored, () => AppCacheReader.IsRunning(item.Folder.App)),
            "Move 1 cache folder to the Recycle Bin?", "Move {0} cache folders to the Recycle Bin?", "Close each app first, including any copy running in the background. Its next start may be slower while it builds the cache again."),
            () => items.SelectedItems.OfType<Row>().Select(row => row.Item).Where(item => !bin!.Gone.Contains(item)).ToArray(), Refresh);
        selectAll = UI.Button(app.L("Select All Listed"), () => items.SelectAll());
        checkRunning = UI.Button(app.L("Check Running Apps Again"), Refresh);
        footer.Children.Add(selectAll); footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo); footer.Children.Add(checkRunning);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        summary = UI.Text("", 14); body.Children.Add(summary);
        running = UI.Text("", 13); body.Children.Add(running);
        body.Children.Add(UI.Text(app.L(AppCaches.Notice), 12));
        body.Children.Add(UI.Text(app.L(AppCaches.Limits), 12));
        foreach (string line in AppLines()) body.Children.Add(UI.Text(line, 12));
        if (result.Items.Count == 0) body.Children.Add(UI.Text(app.L("No cache folders of the listed apps were found at their default places.")));
        var view = new GridView();
        foreach (var (title, property, width) in new[] { ("App", "App", 170.0), ("Profile", "Profile", 130.0), ("Folder", "Folder", 160.0), ("Size", "Size", 170.0), ("Status", "Status", 330.0) })
            view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
        items.View = view; AutomationProperties.SetName(items, app.L("Browser and App Caches"));
        items.SelectionChanged += (_, _) => bin.UpdateButtons();
        body.Children.Add(items); body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private IEnumerable<AppCacheItem> Listed() => result.Items.Where(item => !bin.Gone.Contains(item));

    private string Summary()
    {
        var listed = Listed().ToArray();
        var text = string.Format(app.L("Listed: {0} cache folders from {1} apps · measured {2:N0} bytes"), listed.Length,
            listed.Select(item => item.Folder.App.Id).Distinct().Count(), listed.Sum(item => item.Size?.Bytes ?? 0));
        if (listed.Any(item => item.Size is null || item.Size.Partial) || result.MeasureEntryLimit || result.MeasureTimeLimit) text += " · " + app.L("Some sizes are partial or not measured.");
        if (result.FolderLimit || result.DiscoveryLimit || result.Apps.Any(a => a.Partial)) text += " · " + app.L("The search stopped at a limit; some cache folders may not be listed.");
        if (result.Apps.Any(a => a.Unreadable)) text += " · " + app.L("Some app folders could not be read; some cache folders may not be listed.");
        return text;
    }

    // Every app looked for, then those not found, found without a cache folder or not readable,
    // so a missing app is never mistaken for an empty cache.
    private IEnumerable<string> AppLines()
    {
        yield return string.Format(app.L("Apps looked for, and the programs checked before a move or Undo: {0}"),
            string.Join("; ", AppCaches.Apps.Select(a => a.Name + " (" + AppCaches.Programs(a) + ")")));
        var missing = result.Apps.Where(a => a.State == AppCacheAppState.NotFound).Select(a => a.App.Name).ToArray();
        if (missing.Length > 0) yield return string.Format(app.L("Not found at the default place: {0}"), string.Join(", ", missing));
        var empty = result.Apps.Where(a => a.State == AppCacheAppState.Found && a.Issue is null && !a.Unreadable && !a.Partial && !result.Items.Any(i => i.Folder.App == a.App)).Select(a => a.App.Name).ToArray();
        if (empty.Length > 0) yield return string.Format(app.L("Found without a listed cache folder: {0}"), string.Join(", ", empty));
        foreach (var unavailable in result.Apps.Where(a => a.State == AppCacheAppState.Unavailable || a.Issue is not null || a.Unreadable))
            yield return unavailable.App.Name + " · " + Visible(unavailable.Container ?? "") + " · " + app.L(unavailable.Issue ?? (unavailable.Unreadable ? AppCaches.SomeUnreadable : AppCaches.ContainerUnreadable));
    }

    private void Refresh()
    {
        if (bin.Idle)
        {
            processes = runningNames();
            // Only the drive type here; the Recycle Bin itself is queried on the worker before each move.
            fixedDrives = result.Items.Select(item => System.IO.Path.GetPathRoot(item.Folder.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(root => root, Fixed, StringComparer.OrdinalIgnoreCase);
        }
        running.Text = processes is null ? app.L("Swivel could not list the running programs, so no folder can be moved or put back now.")
            : Listed().Select(item => item.Folder.App).Distinct().Where(Running).Select(a => a.Name + " (" + AppCaches.Programs(a, processes) + ")").ToArray() is { Length: > 0 } open
                ? string.Format(app.L("Running now (close them first): {0}"), string.Join(", ", open)) : app.L("None of the listed apps is running now.");
        items.ItemsSource = Listed().Select(item => new Row(this, item)).ToArray();
        items.IsEnabled = selectAll.IsEnabled = checkRunning.IsEnabled = bin.Idle;
        summary.Text = Summary();
        bin.UpdateButtons();
    }

    private static bool Fixed(string root)
    {
        try { return root.Length > 0 && new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Fixed; }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    private bool Running(AppCacheApp cacheApp) => AppCaches.IsRunning(cacheApp, processes);
    private string Status(AppCacheItem item) => item.Size?.Issue is { } issue ? app.L(issue)
        : !fixedDrives.GetValueOrDefault(System.IO.Path.GetPathRoot(item.Folder.Path) ?? "") ? app.L("Not on a fixed drive; it cannot be moved")
        : Running(item.Folder.App) ? app.L("Its app is running; close it first") : app.L("Can be moved to the Recycle Bin");
    private string Line(AppCacheItem item) => item.Folder.App.Name + " · " + Visible(item.Folder.Path) + " · " + AppCaches.Size(item.Size, app.L) + " · " + Status(item);
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);

    private sealed record Row(AppCacheDialog Owner, AppCacheItem Item)
    {
        public string App => Item.Folder.App.Name;
        public string Profile => Item.Folder.Profile is { } profile ? Visible(profile) : "";
        public string Folder => Visible(Item.Folder.Folder);
        public string Size => AppCaches.Size(Item.Size, Owner.app.L);
        public string Status => Owner.Status(Item);
    }
}
