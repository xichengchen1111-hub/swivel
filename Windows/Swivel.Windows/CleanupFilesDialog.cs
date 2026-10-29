using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

/// Clean Up Files: everything the search found, grouped by kind with a tick box per item and per
/// group, folded until opened; one move to the Recycle Bin for all ticked items, with Undo.
internal sealed class CleanupFilesDialog : Window
{
    private readonly App app;
    private readonly CleanupReader.Result result;
    private readonly RecycleBinSession<CleanupEntry> bin;
    // Keyed by reference: an entry's value hash would hash its whole source record every lookup.
    private readonly Dictionary<CleanupEntry, Row> rows = new(ReferenceEqualityComparer.Instance);
    // Moved in this window at any time (also when Undo brought them back).
    private readonly HashSet<CleanupEntry> moved = new(ReferenceEqualityComparer.Instance);
    private IReadOnlySet<string>? processes;
    private bool bulk;
    private readonly List<(CleanupGroup Group, CheckBox Box, TextBlock Header, ListView List)> groups = new();
    private readonly TextBlock summary;
    internal int Moved => bin.Gone.Count;

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Clean Up Files")); text.AppendLine(Summary());
            text.AppendLine(app.L(CleanupFiles.Notice)); text.AppendLine(app.L(CleanupFiles.Limits));
            foreach (var part in result.Parts) text.AppendLine(PartLine(part));
            foreach (var entry in Listed())
                text.AppendLine((rows[entry].Checked ? "[x] " : "[ ] ") + app.L(CleanupFiles.Name(entry.Group)) + " · " + Visible(entry.Path) + " · " + Size(entry));
            bin.AppendReport(text, result.Entries);
            return text.ToString();
        }
    }

    public CleanupFilesDialog(App app, CleanupReader.Result result)
    {
        this.app = app; this.result = result;
        Title = app.L("Clean Up Files"); Width = 1040; Height = 820; MinWidth = 560; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var entry in result.Entries) rows[entry] = new Row(this, entry, entry.Preselected);
        bin = new RecycleBinSession<CleanupEntry>(app, this, new(
            entry => Visible(entry.Path),
            entry => app.L(CleanupFiles.Name(entry.Group)) + " · " + Visible(entry.Path) + " · " + Size(entry),
            Recycle, Restore,
            "Move 1 item to the Recycle Bin?", "Move {0} items to the Recycle Bin?",
            "Each item is checked again first, under the rules of the tool that found it: cache folders only while their app is closed, temporary items only if still unchanged for 7 days, project folders only next to their project file. Installers and project folders may still be wanted; check each one. If an item is too large for the Recycle Bin, Windows asks before deleting it permanently."),
            () => Listed().Where(entry => rows[entry].Checked).ToArray(), Refresh);

        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo);
        if (result.Entries.Any(e => e.Group == CleanupGroup.AppCaches)) footer.Children.Add(UI.Button(app.L("Check Running Apps Again"), () => { if (bin.Idle) Refresh(); }));
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        summary = UI.Text("", 14); body.Children.Add(summary);
        body.Children.Add(UI.Text(app.L(CleanupFiles.Notice), 12));
        body.Children.Add(UI.Text(app.L(CleanupFiles.Limits), 12));
        foreach (var part in result.Parts) body.Children.Add(UI.Text(PartLine(part), 12));
        if (result.Entries.Count == 0) body.Children.Add(UI.Text(app.L("Nothing to clean was found in these places.")));
        foreach (var group in Enum.GetValues<CleanupGroup>())
        {
            if (!result.Entries.Any(e => e.Group == group)) continue;
            var box = new CheckBox { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(box, string.Format(app.L("Select all: {0}"), app.L(CleanupFiles.Name(group))));
            var header = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal }; headerPanel.Children.Add(box); headerPanel.Children.Add(header);
            var list = new ListView { MaxHeight = 300, SelectionMode = SelectionMode.Extended, Margin = new Thickness(24, 4, 0, 8) };
            VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            var view = new GridView();
            var tick = new FrameworkElementFactory(typeof(CheckBox));
            tick.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(Row.Checked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            tick.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Row.Path)));
            view.Columns.Add(new GridViewColumn { Header = "", CellTemplate = new DataTemplate { VisualTree = tick }, Width = 36 });
            var columns = new List<(string, string, double)> { ("Path", nameof(Row.Path), 520.0), ("Kind", nameof(Row.Kind), 140.0), ("Size", nameof(Row.Size), 150.0) };
            if (group == CleanupGroup.AppCaches) columns.Add(("Status", nameof(Row.Status), 260.0));
            foreach (var (title, property, width) in columns)
                view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
            list.View = view; AutomationProperties.SetName(list, app.L(CleanupFiles.Name(group)));
            // Space ticks the selected rows, so the list works without a mouse.
            // (Before the list uses Space to select; a focused tick box toggles itself.)
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Space || e.OriginalSource is CheckBox || list.SelectedItems.Count == 0 || !bin.Idle) return;
                var chosen = list.SelectedItems.OfType<Row>().ToArray(); bool value = !chosen.All(r => r.Checked);
                Bulk(() => { foreach (var row in chosen) row.Checked = value; }); e.Handled = true;
            };
            box.Click += (_, _) =>
            {
                bool value = CleanupFiles.GroupClick(CleanupFiles.GroupState(Listed().Where(e => e.Group == group).Select(e => rows[e].Checked)));
                Bulk(() => { foreach (var entry in Listed().Where(e => e.Group == group)) rows[entry].Checked = value; });
            };
            body.Children.Add(new Expander { Header = headerPanel, Content = list, IsExpanded = false, Margin = new Thickness(0, 6, 0, 0) });
            groups.Add((group, box, header, list));
        }
        body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    // The Recycle Bin session asks for the ticked items while it is being built, before `bin` is set.
    // Temporary items restored by Undo count as recently changed, so they leave the list (as in Old
    // Temporary Files).
    private IEnumerable<CleanupEntry> Listed() => result.Entries.Where(entry => bin?.Gone.Contains(entry) != true && !(entry.Group == CleanupGroup.OldTemporary && moved.Contains(entry)));

    /// Many ticks at once, then one update.
    private void Bulk(Action change) { bulk = true; try { change(); } finally { bulk = false; } Ticked(); }

    private string Summary()
    {
        var all = CleanupFiles.Total(Listed()); var ticked = CleanupFiles.Total(Listed().Where(e => rows[e].Checked));
        return string.Format(app.L("Found: {0} items · {1} · selected: {2} items · {3}"), all.Count, AppCaches.ShortSize(all.Bytes), ticked.Count, AppCaches.ShortSize(ticked.Bytes));
    }

    private string PartLine(CleanupPart part)
    {
        string line = app.L(CleanupFiles.Name(part.Group)) + ": " + string.Format(app.L("{0} items · {1}"), part.Count, AppCaches.ShortSize(part.Bytes));
        if (part.Issue is not null) line += " · " + app.L(part.Issue);
        else if (part.Partial) line += " · " + app.L("The search stopped at a limit or skipped some items; the list may be incomplete.");
        return line;
    }

    private void Ticked()
    {
        if (bulk) return;
        foreach (var (group, box, header, _) in groups)
        {
            var listed = Listed().Where(e => e.Group == group).ToArray(); var total = CleanupFiles.Total(listed);
            box.IsChecked = CleanupFiles.GroupState(listed.Select(e => rows[e].Checked));
            box.IsEnabled = bin.Idle && listed.Length > 0;
            header.Text = app.L(CleanupFiles.Name(group)) + " · " + string.Format(app.L("{0} items · {1}"), total.Count, AppCaches.ShortSize(total.Bytes));
        }
        summary.Text = Summary(); bin.UpdateButtons();
    }

    private void Refresh()
    {
        if (bin.Idle) processes = AppCacheReader.RunningNames();
        // Items Undo brought back return unticked.
        foreach (var entry in result.Entries)
            if (bin.Gone.Contains(entry)) moved.Add(entry);
            else if (moved.Contains(entry)) rows[entry].Untick();
        foreach (var (group, _, _, list) in groups)
        {
            list.ItemsSource = Listed().Where(e => e.Group == group).Select(e => rows[e]).ToArray();
            list.IsEnabled = bin.Idle;
        }
        Ticked();
    }

    // Each kind moves and comes back under its own tool's rules and rechecks.
    internal static string Recycle(CleanupEntry entry, IntPtr owner) => entry.Source switch
    {
        AppCacheItem cache => NativeFiles.RecycleAppCache(entry.Path, () => AppCacheReader.IsRunning(cache.Folder.App), owner),
        CleanupTemp temp => NativeFiles.RecycleTempItem(entry.Path, temp.Item.Directory, temp.Item.LastWrite, temp.Item.Bytes, temp.Cutoff, owner),
        LargeFile file => NativeFiles.RecycleFile(entry.Path, file.Bytes, owner),
        CleanupBuild build => NativeFiles.RecycleProjectFolder(entry.Path, build.Folder.Evidence.Distinct(StringComparer.Ordinal).Select(marker => System.IO.Path.Combine(build.Root, marker)), owner),
        _ => throw new ArgumentException("Unknown cleanup item."),
    };

    internal static void Restore(CleanupEntry entry, string stored)
    {
        switch (entry.Source)
        {
            case AppCacheItem cache: NativeFiles.RestoreAppCache(entry.Path, stored, () => AppCacheReader.IsRunning(cache.Folder.App)); break;
            case CleanupTemp temp: NativeFiles.RestoreRecycled(entry.Path, stored, temp.Item.Directory); break;
            case LargeFile: NativeFiles.RestoreRecycled(entry.Path, stored, false); break;
            case CleanupBuild: NativeFiles.RestoreRecycled(entry.Path, stored, true); break;
            default: throw new ArgumentException("Unknown cleanup item.");
        }
    }

    private string Size(CleanupEntry entry)
    {
        string bytes = entry.Bytes.ToString("N0", CultureInfo.CurrentCulture);
        return entry.Partial ? string.Format(app.L("at least {0}"), bytes) : bytes;
    }
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);

    private sealed class Row(CleanupFilesDialog owner, CleanupEntry entry, bool preselected) : INotifyPropertyChanged
    {
        private bool ticked = preselected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool Checked
        {
            get => ticked;
            set { if (ticked == value) return; ticked = value; PropertyChanged?.Invoke(this, new(nameof(Checked))); owner.Ticked(); }
        }
        public void Untick() { if (!ticked) return; ticked = false; PropertyChanged?.Invoke(this, new(nameof(Checked))); }
        public string Path => Visible(entry.Path);
        public string Status => entry.Source is AppCacheItem cache
            ? owner.processes is null ? owner.app.L("Swivel could not list the running programs, so no folder can be moved or put back now.")
            : AppCaches.IsRunning(cache.Folder.App, owner.processes) ? owner.app.L("Its app is running; close it first") : owner.app.L("Can be moved to the Recycle Bin")
            : "";
        public string Kind => owner.app.L(entry.Label);
        public string Size => owner.Size(entry);
    }
}
