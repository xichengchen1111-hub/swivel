using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Media;
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
    private readonly CacheRing ring = new();
    private readonly StackPanel legend = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly DockPanel chart = new() { Margin = new Thickness(0, 8, 0, 0) };
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
        DockPanel.SetDock(ring, Dock.Left); chart.Children.Add(ring); chart.Children.Add(legend);
        AutomationProperties.SetName(ring, app.L("Cache sizes by app")); body.Children.Add(chart);
        var view = new GridView();
        foreach (var (title, property, width) in new[] { ("App", "App", 170.0), ("Profile", "Profile", 130.0), ("Folder", "Folder", 160.0), ("Size", "Size", 170.0), ("Status", "Status", 330.0) })
            view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
        items.View = view; AutomationProperties.SetName(items, app.L("Browser and App Caches"));
        items.SelectionChanged += (_, _) => { bin.UpdateButtons(); UpdateChart(); };
        body.Children.Add(items); body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        // High contrast changes the ring's and legend's colours.
        void SystemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(new Action(UpdateChart)); }
        // Subscribed once shown, so a dialog that never opens is not kept alive by the static event.
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= SystemChanged;
        SourceInitialized += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; WebRouteLayout.Fit(this, Owner ?? this); }; DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
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
        UpdateChart();
    }

    // One ring segment per app by measured bytes; the apps of the selected rows stay bright,
    // and the centre shows the selected or the total measured size.
    private void UpdateChart()
    {
        var shares = AppCaches.Shares(Listed());
        chart.Visibility = shares.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = items.SelectedItems.OfType<Row>().ToArray();
        var chosen = rows.Select(row => row.Item.Folder.App).ToHashSet();
        var counted = rows.Length > 0 ? rows.Select(row => row.Item).ToArray() : Listed().ToArray();
        string Amount(decimal bytes, bool partial) => partial ? string.Format(app.L("at least {0}"), AppCaches.ShortSize(bytes)) : AppCaches.ShortSize(bytes);
        string center = Amount(counted.Sum(item => item.Size?.Bytes ?? 0), counted.Any(item => item.Size is null || item.Size.Partial));
        string caption = app.L(rows.Length > 0 ? "Selected" : "Measured");
        bool[] bright = shares.Select(share => chosen.Count == 0 || chosen.Contains(share.App)).ToArray();
        ring.Update(shares.Select(share => share.Bytes).ToArray(), bright, center, caption);
        legend.Children.Clear();
        void Entry(int index, string text, bool lit)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Opacity = CacheRing.Strength(lit, 0.55) };
            line.Children.Add(new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = CacheRing.SegmentBrush(index, lit), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(UI.Text(text, 12)); legend.Children.Add(line);
        }
        for (int index = 0; index < Math.Min(CacheRing.Named, shares.Count); index++)
            Entry(index, shares[index].App.Name + " · " + Amount(shares[index].Bytes, shares[index].Partial), bright[index]);
        if (shares.Count > CacheRing.Named)
        {
            var rest = shares.Skip(CacheRing.Named).ToArray();
            Entry(CacheRing.Named, string.Format(app.L("{0} more · {1}"), rest.Length, Amount(rest.Sum(share => share.Bytes), rest.Any(share => share.Partial))),
                chosen.Count == 0 || rest.Any(share => chosen.Contains(share.App)));
        }
        AutomationProperties.SetHelpText(ring, caption + ": " + center + ". " + string.Join("; ", shares.Select(share => share.App.Name + " " + Amount(share.Bytes, share.Partial))));
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

/// A ring of measured cache sizes: one segment per app, in the legend's order and colours,
/// clockwise from the top. The first `Named` apps have their own colour; the rest share one.
/// Nothing moves. In high contrast every segment is opaque: the apps of the selected rows use
/// the highlight colour and the others the window text colour.
internal sealed class CacheRing : FrameworkElement
{
    internal const int Named = 6;
    private static readonly Brush[] Palette = new[] { "#0078D4", "#8764B8", "#00A3AE", "#C239B3", "#CA5010", "#498205" }
        .Select(code => { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(code)); brush.Freeze(); return (Brush)brush; }).ToArray();
    private decimal[] values = Array.Empty<decimal>();
    private bool[] bright = Array.Empty<bool>();
    private string center = "", caption = "";

    internal CacheRing() { Width = 140; Height = 140; }
    internal static Brush SegmentBrush(int index, bool lit) => SystemParameters.HighContrast ? (lit ? SystemColors.HighlightBrush : SystemColors.WindowTextBrush)
        : index < Named ? Palette[index] : SystemColors.GrayTextBrush;
    internal static double Strength(bool lit, double dim) => SystemParameters.HighContrast || lit ? 1 : dim;

    internal void Update(decimal[] values, bool[] bright, string center, string caption)
    {
        this.values = values; this.bright = bright; this.center = center; this.caption = caption; InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize) => new(140, 140);
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == System.Windows.Documents.TextElement.ForegroundProperty) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        const double thickness = 14;
        var middle = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 - thickness / 2 - 1;
        if (radius <= 0) return;
        double[] sweeps = AppCaches.Sweeps(values);
        if (sweeps.Sum() <= 0) dc.DrawEllipse(null, new Pen(SystemColors.GrayTextBrush, thickness), middle, radius, radius);
        double start = -90;
        for (int i = 0; i < sweeps.Length; i++)
        {
            double sweep = sweeps[i];
            if (sweep <= 0) continue;
            bool lit = i < bright.Length && bright[i];
            // A thin gap between segments, so neighbours stay apart also in one colour.
            double gap = sweeps.Count(value => value > 0) > 1 ? Math.Min(1.5, sweep / 3) : 0;
            var pen = new Pen(SegmentBrush(i, lit), thickness);
            dc.PushOpacity(Strength(lit, 0.3));
            if (sweep - gap >= 359.9) dc.DrawEllipse(null, pen, middle, radius, radius);
            else
            {
                Point At(double degrees) => new(middle.X + radius * Math.Cos(degrees * Math.PI / 180), middle.Y + radius * Math.Sin(degrees * Math.PI / 180));
                var figure = new PathFigure { StartPoint = At(start), IsClosed = false };
                figure.Segments.Add(new ArcSegment(At(start + sweep - gap), new Size(radius, radius), 0, sweep - gap > 180, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry(); geometry.Figures.Add(figure); geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            dc.Pop();
            start += sweep;
        }
        // The text follows the window's theme like the legend's TextBlocks.
        var foreground = GetValue(System.Windows.Documents.TextElement.ForegroundProperty) as Brush ?? SystemColors.WindowTextBrush;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip, width = Math.Max(1, 2 * (radius - thickness));
        var big = new FormattedText(center, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 15, foreground, dpi)
            { MaxTextWidth = width, TextAlignment = TextAlignment.Center, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var small = new FormattedText(caption, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, foreground, dpi)
            { MaxTextWidth = width, TextAlignment = TextAlignment.Center, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        double top = middle.Y - (big.Height + small.Height) / 2;
        dc.DrawText(big, new Point(middle.X - width / 2, top));
        dc.PushOpacity(SystemParameters.HighContrast ? 1 : 0.75); dc.DrawText(small, new Point(middle.X - width / 2, top + big.Height)); dc.Pop();
    }
    // Narrator reads the ring as an image, with the name and help text the window sets.
    private sealed class Peer(CacheRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(CacheRing);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
