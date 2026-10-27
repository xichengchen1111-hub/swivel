using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Swivel;

/// One page of the arrangement: which original page it is, and the extra turn it gets (0, 90, 180, 270).
sealed record PdfPageItem(string Source, int Page)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Rotation { get; init; }
}

static class PdfPageOrder
{
    /// "1, 3-5" → the 0-based pages 0, 2, 3, 4. A full-width comma works too. Throws on anything outside 1…count.
    public static SortedSet<int> Parse(string text, int count)
    {
        var result = new SortedSet<int>();
        string error = L("Use page numbers such as 1, 3-5 within this document.");
        foreach (var field in text.Replace('，', ',').Split(','))
        {
            var bounds = field.Trim().Split('-');
            if (bounds.Length is < 1 or > 2 || !Number(bounds[0], out long first) || first < 1 || first > count) throw new ToolError(error);
            long last = first;
            if (bounds.Length == 2 && !Number(bounds[1], out last)) throw new ToolError(error);
            if (last < first || last > count) throw new ToolError(error);
            for (long i = first - 1; i < last; i++) result.Add((int)i);
        }
        return result;
    }

    static bool Number(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// The selected pages moved together to `destination`, an index in the list *before* they are taken out (like a
    /// drop between two rows).
    public static List<PdfPageItem> Moving(IReadOnlyList<PdfPageItem> items, IEnumerable<int> indexes, int destination)
    {
        var valid = indexes.Where(i => i >= 0 && i < items.Count).ToHashSet();
        var selected = Enumerable.Range(0, items.Count).Where(valid.Contains).Select(i => items[i]).ToList();
        var rest = items.Where((_, i) => !valid.Contains(i)).ToList();
        int target = Math.Min(rest.Count, Math.Max(0, destination - valid.Count(i => i < destination)));
        rest.InsertRange(target, selected);
        return rest;
    }
}

/// The chosen PDFs' bytes as they were when the window opened: an edit to an original afterwards can't silently change
/// what the arrangement exports.
sealed class PdfPagesSnapshot
{
    public const int MaxPages = 2000;

    public IReadOnlyDictionary<string, byte[]> Data { get; }
    public IReadOnlyList<PdfPageItem> Items { get; }

    PdfPagesSnapshot(Dictionary<string, byte[]> data, List<PdfPageItem> items)
    {
        Data = data;
        Items = items;
    }

    public static PdfPagesSnapshot Load(IReadOnlyList<string> paths)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PdfPageItem>();
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (data.ContainsKey(path)) continue;
            var name = Path.GetFileName(path);
            byte[] bytes;
            PdfFile file;
            try
            {
                bytes = File.ReadAllBytes(path);
                file = PdfFile.Load(bytes, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolError)
            {
                throw new ToolError(L("Could not open %@. Unlock password-protected PDFs first.", name));
            }
            int count = file.Pages.Count;
            if (count == 0) throw new ToolError(L("Could not open %@. Unlock password-protected PDFs first.", name));
            if (items.Count + count > MaxPages) throw new ToolError(L("Choose at most 2,000 PDF pages at a time."));
            data[path] = bytes;
            for (int i = 0; i < count; i++) items.Add(new PdfPageItem(path, i));
        }
        if (items.Count == 0) throw new ToolError(L("Choose a PDF with at least one page."));
        return new PdfPagesSnapshot(data, items);
    }

    /// A new PDF of `pages` in this order, beside the first original ("name arranged.pdf", never replacing a file).
    /// Every page is copied object for object — content, fonts, images and annotations — never re-rendered; only its
    /// /Rotate changes. The result is read back before it's moved into place.
    public string Export(IReadOnlyList<PdfPageItem> pages)
    {
        if (Items.Count == 0 || pages.Count == 0) throw new ToolError(L("Select at least one page."));
        var bytes = Assemble(pages);
        PdfFile check;
        try { check = PdfFile.Load(bytes, "check.pdf"); }
        catch (ToolError) { throw new ToolError(L("Could not write PDF")); }
        if (check.Pages.Count != pages.Count) throw new ToolError(L("Could not write PDF"));
        var output = Paths.Output(Items[0].Source, "pdf", " arranged");
        PdfOutput.Save(output, s => s.Write(bytes));
        return output;
    }

    /// The new file's bytes. Pages from several sources can interleave; each source has one copier, so resources
    /// shared by its pages are written once.
    public byte[] Assemble(IReadOnlyList<PdfPageItem> pages)
    {
        var files = Data.ToDictionary(kv => kv.Key, kv => PdfFile.Load(kv.Value, Path.GetFileName(kv.Key)), StringComparer.OrdinalIgnoreCase);
        var output = new PdfOutput();
        var catalogRef = output.Reserve();
        var pagesRef = output.Reserve();
        var kids = new PdfArray();
        // A page can only be copied once per copier; the same page twice gets a second copier.
        var copiers = new Dictionary<string, List<(PdfCopier Copier, HashSet<int> Used)>>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(PdfCopier Copier, PdfPage Page, int Rotation, PdfRef Target)>();
        foreach (var item in pages)
        {
            if (!files.TryGetValue(item.Source, out var file) || item.Page < 0 || item.Page >= file.Pages.Count)
                throw new ToolError(L("A selected page is unavailable."));
            var page = file.Pages[item.Page];
            var list = copiers.TryGetValue(item.Source, out var existing) ? existing : copiers[item.Source] = [];
            var slot = list.FirstOrDefault(c => !c.Used.Contains(page.Ref.Num));
            if (slot.Copier is null)
            {
                var copier = new PdfCopier(file, output);
                // Pages and page-tree nodes that aren't being copied are off limits (links to them become null).
                foreach (var p in file.Pages) copier.Excluded.Add(p.Ref.Num);
                foreach (var num in PageTreeNodes(file)) copier.Excluded.Add(num);
                slot = (copier, []);
                list.Add(slot);
            }
            slot.Used.Add(page.Ref.Num);
            var target = output.Reserve();
            slot.Copier.MapTo(page.Ref.Num, target);
            plan.Add((slot.Copier, page, item.Rotation, target));
        }
        foreach (var (copier, page, rotation, target) in plan)
        {
            int turn = (((page.Rotate + rotation) % 360) + 360) % 360;
            copier.CopyPage(page, pagesRef, dict =>
            {
                if (turn == 0) dict.Remove("Rotate");
                else dict["Rotate"] = new PdfNum(turn);
            });
            kids.Add(target);
        }
        foreach (var list in copiers.Values)
            foreach (var (copier, _) in list) copier.Finish();
        var pagesDict = new PdfDict();
        pagesDict["Type"] = new PdfName("Pages");
        pagesDict["Kids"] = kids;
        pagesDict["Count"] = new PdfNum(kids.Count);
        output.Set(pagesRef, pagesDict);
        var catalog = new PdfDict();
        catalog["Type"] = new PdfName("Catalog");
        catalog["Pages"] = pagesRef;
        output.Set(catalogRef, catalog);
        var info = new PdfDict();
        info["Producer"] = PdfStr.FromText("Swivel");
        info["CreationDate"] = PdfStr.FromText(PdfAssembler.PdfDate(DateTime.Now));
        var buffer = new MemoryStream();
        output.Write(buffer, catalogRef, output.Add(info));
        return buffer.ToArray();
    }

    static HashSet<int> PageTreeNodes(PdfFile file)
    {
        var result = new HashSet<int>();
        void Walk(PdfObj? node, int depth)
        {
            if (depth > 64 || node is not PdfRef r || !result.Add(r.Num)) return;
            if (file.Resolve(r) is PdfDict d && file.Resolve(d["Kids"]) is PdfArray kids)
                foreach (var k in kids.Items) Walk(k, depth + 1);
        }
        Walk(file.Catalog["Pages"], 0);
        return result;
    }
}

/// Page thumbnails from the snapshot's bytes (Windows.Data.Pdf), rendered one at a time on demand and kept per
/// original page; a page's extra turn is applied when it's drawn, so rotating never renders again.
sealed class PdfThumbnails
{
    readonly PdfPagesSnapshot snapshot;
    readonly Dictionary<string, Task<(WinPdf.PdfDocument? Document, PdfFile? Parsed)>> documents = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, int), BitmapSource?> cache = [];
    readonly Dictionary<(string, int), List<Action<BitmapSource?>>> waiting = [];
    readonly Queue<(string Source, int Page)> queue = new();
    bool running;

    /// Pixels on the thumbnail's long side (about twice its size on screen, for high-DPI displays).
    public const double LongSide = 200;

    public PdfThumbnails(PdfPagesSnapshot snapshot) => this.snapshot = snapshot;

    public int Rendered => cache.Count(kv => kv.Value != null);

    public void Request(PdfPageItem item, Action<BitmapSource?> done)
    {
        var key = (item.Source, item.Page);
        if (cache.TryGetValue(key, out var image)) { done(image); return; }
        if (waiting.TryGetValue(key, out var list)) { list.Add(done); return; }
        waiting[key] = [done];
        queue.Enqueue(key);
        if (!running) _ = Drain();
    }

    async Task Drain()
    {
        running = true;
        try
        {
            while (queue.TryDequeue(out var key))
            {
                BitmapSource? image = null;
                try { image = await Render(key.Source, key.Page); }
                catch (Exception ex) when (ex is ToolError or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { }
                cache[key] = image;
                if (waiting.Remove(key, out var callbacks))
                    foreach (var callback in callbacks) callback(image);
            }
        }
        finally { running = false; }
    }

    async Task<BitmapSource?> Render(string source, int index)
    {
        if (!documents.TryGetValue(source, out var load)) documents[source] = load = Open(source);
        var (document, parsed) = await load;
        if (document is null || index >= document.PageCount) return null;
        using var page = document.GetPage((uint)index);
        double side = 612;
        if (parsed != null && index < parsed.Pages.Count)
        {
            var v = parsed.Pages[index].Visible;
            side = Math.Max(v.X1 - v.X0, v.Y1 - v.Y0);
        }
        return await PdfRender.Render(page, parsed, Math.Clamp(72 * LongSide / Math.Max(side, 1), 2, 300));
    }

    async Task<(WinPdf.PdfDocument?, PdfFile?)> Open(string source)
    {
        if (!snapshot.Data.TryGetValue(source, out var bytes)) return (null, null);
        var parsed = await Task.Run(() => PdfFile.Load(bytes, Path.GetFileName(source)));
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        try { return (await WinPdf.PdfDocument.LoadFromStreamAsync(stream), parsed); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException) { return (null, parsed); }
    }
}

/// Choose, reorder, rotate and remove PDF pages, then export a selection or the whole arrangement as a new PDF
/// (PDFPagesWindow in PDFPages.swift). The originals never change.
sealed class PdfPagesWindow : FileToolWindow
{
    public static void Present(IReadOnlyList<string> files)
    {
        if (!Feature.PdfPages.IsOn()) return;
        var window = new PdfPagesWindow(files);
        window.Show();
    }

    public static void CloseAll() => CloseAll(Feature.PdfPages);

    public PdfPagesSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<PdfPageItem> Items => items;
    public PdfThumbnails? Thumbnails { get; private set; }
    /// Where the last save went.
    public string? LastSaved { get; private set; }
    /// Reports a saved file. Tests replace it, so they leave the shared toast alone.
    public Action<string> ShowSaved { get; set; } = path =>
        Toast.Shared.Show(L("Saved %@", Path.GetFileName(path)), "ok", [path]);

    List<PdfPageItem> items = [];
    readonly Stack<List<PdfPageItem>> previous = new();
    public readonly PdfPageList List;
    public readonly TextBox Range = new() { MinWidth = 240 };
    readonly TextBlock status = ToolUI.Note(L("Loading pages…"));
    public readonly Button ExportButton, SaveButton, RemoveButton, RotateButton, UndoButton, UpButton, DownButton, SelectButton, AllButton;
    public string Status => status.Text;
    public Task Loading { get; }

    public PdfPagesWindow(IReadOnlyList<string> files) : base(Feature.PdfPages, L("PDF Pages"), 850, 720)
    {
        List = new PdfPageList(this);
        ToolUI.AutomationName(Range, L("Select pages"));
        Range.KeyDown += (_, e) =>
        {
            // Return picks the typed pages here; it must not reach the default Save All Pages.
            if (e.Key != Key.Return) return;
            e.Handled = true;
            SelectRange();
        };
        SelectButton = ToolUI.Button(L("Select Pages"), SelectRange);
        AllButton = ToolUI.Button(L("Select All"), SelectAll);
        RemoveButton = ToolUI.Button(L("Remove Pages"), RemovePages);
        RotateButton = ToolUI.Button(L("Rotate"), RotatePages);
        UndoButton = ToolUI.Button(L("Undo"), UndoEdit);
        UpButton = ToolUI.Button(L("Move Up"), MoveUp);
        DownButton = ToolUI.Button(L("Move Down"), MoveDown);
        ExportButton = ToolUI.Button(L("Export Selected…"), () => _ = ExportSelected());
        SaveButton = FileToolUI.Accent(ToolUI.Button(L("Save All Pages"), () => _ = SaveAll()));
        SaveButton.IsDefault = true;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.NoWrap;

        var top = ToolUI.Stack([
            ToolUI.Note(L("Select pages to export, drag rows to reorder, or remove unwanted pages. Save creates a new PDF beside the first original.")),
            ToolUI.Row([FileToolUI.WithPlaceholder(Range, L("Pages, e.g. 1, 3-5")), SelectButton, AllButton]),
            ToolUI.Row([UpButton, DownButton, RotateButton, RemoveButton, UndoButton], spacing: 8),
        ]);
        top.Margin = new Thickness(0, 0, 0, 12);
        var footer = ToolUI.Bar([status], [ExportButton, SaveButton]);
        footer.Margin = new Thickness(0, 12, 0, 0);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(List);
        Install(root);

        List.Box.SelectionChanged += (_, _) => UpdateButtons();
        List.Moved = (indexes, row) => Change(PdfPageOrder.Moving(items, indexes, row),
            indexes.Where(i => i >= 0 && i < items.Count).Select(i => items[i].Id).ToHashSet());
        List.Box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; RemovePages(); }
        };
        Window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => UndoEdit(), (_, e) => e.CanExecute = !IsBusy && previous.Count > 0));
        UpdateButtons();
        Loading = RunWork(async () =>
        {
            var value = await Task.Run(() => PdfPagesSnapshot.Load(files));
            Snapshot = value;
            Thumbnails = new PdfThumbnails(value);
            items = [.. value.Items];
            List.Show(items, new HashSet<Guid>());
            List.SelectIndexes([0]);
        });
    }

    IReadOnlyList<int> Selected => List.SelectedIndexes;

    void UpdateButtons()
    {
        bool ready = !IsBusy && Snapshot != null && Feature.PdfPages.IsOn();
        bool any = Selected.Count > 0;
        ExportButton.IsEnabled = ready && any;
        SaveButton.IsEnabled = ready && items.Count > 0;
        foreach (var button in new[] { RemoveButton, RotateButton, UpButton, DownButton }) button.IsEnabled = ready && any;
        UndoButton.IsEnabled = ready && previous.Count > 0;
        SelectButton.IsEnabled = AllButton.IsEnabled = Range.IsEnabled = ready;
        status.Text = Snapshot is null ? (IsBusy ? L("Loading pages…") : L("Choose a PDF to get started."))
                                       : L("%d pages · %d selected", items.Count, Selected.Count);
    }

    protected override void WorkFinished() => UpdateButtons();
    protected override void SettingsChanged() => UpdateButtons();

    public void SelectAll()
    {
        if (IsBusy) return;
        List.SelectIndexes(Enumerable.Range(0, items.Count));
    }

    public void SelectRange()
    {
        if (IsBusy) return;
        try { List.SelectIndexes(PdfPageOrder.Parse(Range.Text, items.Count)); }
        catch (ToolError error)
        {
            status.Text = error.Message;
            ToolUI.Refuse(Range);
        }
    }

    void Change(List<PdfPageItem> next, IReadOnlySet<Guid>? select = null)
    {
        if (!Feature.PdfPages.IsOn() || IsBusy || next.SequenceEqual(items)) return;
        previous.Push(items);
        items = next;
        List.Show(items, select ?? new HashSet<Guid>());
        UpdateButtons();
    }

    HashSet<Guid> SelectedIds => Selected.Select(i => items[i].Id).ToHashSet();

    public void RemovePages()
    {
        var indexes = Selected.ToHashSet();
        if (indexes.Count == 0) return;
        Change(items.Where((_, i) => !indexes.Contains(i)).ToList());
    }

    public void RotatePages()
    {
        var indexes = Selected.ToHashSet();
        if (indexes.Count == 0) return;
        Change(items.Select((item, i) => indexes.Contains(i) ? item with { Rotation = (item.Rotation + 90) % 360 } : item).ToList(), SelectedIds);
    }

    public void MoveUp()
    {
        var selected = Selected;
        if (selected.Count == 0 || selected[0] == 0) return;
        Change(PdfPageOrder.Moving(items, selected, selected[0] - 1), SelectedIds);
    }

    public void MoveDown()
    {
        var selected = Selected;
        if (selected.Count == 0 || selected[^1] >= items.Count - 1) return;
        Change(PdfPageOrder.Moving(items, selected, selected[^1] + 2), SelectedIds);
    }

    public void UndoEdit()
    {
        if (!Feature.PdfPages.IsOn() || IsBusy || previous.Count == 0) return;
        items = previous.Pop();
        List.Show(items, new HashSet<Guid>());
        UpdateButtons();
    }

    public Task ExportSelected() => Save(Selected.Select(i => items[i]).ToList());
    public Task SaveAll() => Save(items);

    Task Save(IReadOnlyList<PdfPageItem> pages)
    {
        if (Snapshot is not { } snapshot || pages.Count == 0) return Task.CompletedTask;
        return RunWork(async () =>
        {
            var output = await Task.Run(() => snapshot.Export(pages));
            LastSaved = output;
            ShowSaved(output);
        });
    }
}

/// The page list: a thumbnail, the page's place and where it comes from per row, with several rows selectable and
/// dragged together to a new place. An accent line shows where they will land.
sealed class PdfPageList : Grid
{
    const string Format = "Swivel.PdfPages.Rows";
    public const double RowHeight = 100;

    readonly PdfPagesWindow owner;
    public readonly ListBox Box = new();
    readonly Canvas overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly Border marker = new() { Height = 2, CornerRadius = new CornerRadius(1), Opacity = 0 };
    readonly FrameDriver driver = new();
    Spring markerFade = new(0, response: 0.12, damping: 1);
    Point? downPoint;
    ListBoxItem? downItem;
    bool deferSelection;
    bool dragging;
    int dropRow = -1;

    /// Called with the dragged rows and the drop row (an index before they are taken out).
    public Action<IReadOnlyList<int>, int>? Moved { get; set; }

    public PdfPageList(PdfPagesWindow owner)
    {
        this.owner = owner;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = PageRow.Columns();
        header.Margin = new Thickness(13, 0, 13, 6);
        foreach (var (column, title) in new[] { (0, L("Preview")), (1, L("Page")), (2, L("Source file")) })
        {
            var text = ToolUI.Scaled(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Opacity = 0.7 }, 12);
            SetColumn(text, column);
            header.Children.Add(text);
        }
        Children.Add(header);

        Box.SelectionMode = SelectionMode.Extended;
        Box.AllowDrop = true;
        Box.MinHeight = 240;
        Box.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(Box, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(Box, true);
        VirtualizingPanel.SetVirtualizationMode(Box, VirtualizationMode.Recycling);
        Box.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(PageRow)) };
        ToolUI.AutomationName(Box, L("PDF Pages"));
        SetRow(Box, 1);
        Children.Add(Box);

        ToolUI.UseAccent(marker, Border.BackgroundProperty);
        overlay.Children.Add(marker);
        SetRow(overlay, 1);
        Children.Add(overlay);
        driver.OnFrame = dt =>
        {
            markerFade.Step(dt, ToolUI.Tuning);
            marker.Opacity = Math.Clamp(markerFade.Value, 0, 1);
            return !markerFade.IsSettled;
        };

        Box.PreviewMouseLeftButtonDown += PressRow;
        Box.PreviewMouseMove += MoveRow;
        Box.PreviewMouseLeftButtonUp += ReleaseRow;
        Box.DragEnter += DragRows;
        Box.DragOver += DragRows;
        Box.DragLeave += (_, _) => ShowMarker(-1);
        Box.Drop += DropRows;
    }

    /// The rows for `items`, keeping the given pages selected.
    public void Show(IReadOnlyList<PdfPageItem> items, IReadOnlySet<Guid> selected)
    {
        var rows = items.Select((item, index) => new PageRow.Model(item, index, owner)).ToList();
        Box.ItemsSource = rows;
        Box.SelectedItems.Clear();
        foreach (var row in rows.Where(r => selected.Contains(r.Item.Id))) Box.SelectedItems.Add(row);
        if (rows.FirstOrDefault(r => selected.Contains(r.Item.Id)) is { } first) Box.ScrollIntoView(first);
    }

    public IReadOnlyList<int> SelectedIndexes =>
        Box.SelectedItems.OfType<PageRow.Model>().Select(m => m.Index).Order().ToList();

    public void SelectIndexes(IEnumerable<int> indexes)
    {
        if (Box.ItemsSource is not List<PageRow.Model> rows) return;
        Box.SelectedItems.Clear();
        PageRow.Model? first = null;
        foreach (var i in indexes.Where(i => i >= 0 && i < rows.Count))
        {
            Box.SelectedItems.Add(rows[i]);
            first ??= rows[i];
        }
        if (first != null) Box.ScrollIntoView(first);
    }

    static ListBoxItem? ItemAt(DependencyObject? element)
    {
        while (element != null && element is not ListBoxItem) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    // Pressing a row that is already part of a selection keeps the selection, so all of it can be dragged; a click
    // without a drag then selects just that row, as usual.
    void PressRow(object sender, MouseButtonEventArgs e)
    {
        downItem = ItemAt(e.OriginalSource as DependencyObject);
        downPoint = downItem is null ? null : e.GetPosition(Box);
        deferSelection = false;
        if (downItem is { IsSelected: true } && Keyboard.Modifiers == ModifierKeys.None && Box.SelectedItems.Count > 1)
        {
            deferSelection = true;
            downItem.Focus();
            e.Handled = true;
        }
    }

    void ReleaseRow(object sender, MouseButtonEventArgs e)
    {
        if (deferSelection && downItem?.DataContext is PageRow.Model model && !dragging)
        {
            Box.SelectedItems.Clear();
            Box.SelectedItems.Add(model);
        }
        deferSelection = false;
        downPoint = null;
        downItem = null;
    }

    void MoveRow(object sender, MouseEventArgs e)
    {
        if (downPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || owner.IsBusy || !Feature.PdfPages.IsOn()) return;
        var at = e.GetPosition(Box);
        if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (downItem?.DataContext is PageRow.Model pressed && !Box.SelectedItems.Contains(pressed))
        {
            Box.SelectedItems.Clear();
            Box.SelectedItems.Add(pressed);
        }
        var indexes = SelectedIndexes;
        downPoint = null;
        deferSelection = false;
        if (indexes.Count == 0) return;
        var data = new DataObject(Format, string.Join(",", indexes));
        dragging = true;
        try { DragDrop.DoDragDrop(Box, data, DragDropEffects.Move); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        finally
        {
            dragging = false;
            ShowMarker(-1);
        }
    }

    /// Only rows dragged inside this list are taken.
    bool Accepts(DragEventArgs e) => dragging && !owner.IsBusy && Feature.PdfPages.IsOn() && e.Data.GetDataPresent(Format);

    void DragRows(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!Accepts(e))
        {
            e.Effects = DragDropEffects.None;
            ShowMarker(-1);
            return;
        }
        e.Effects = DragDropEffects.Move;
        var at = e.GetPosition(Box);
        AutoScroll(at);
        ShowMarker(DropRow(at));
    }

    void DropRows(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int row = Accepts(e) ? DropRow(e.GetPosition(Box)) : -1;
        var text = e.Data.GetData(Format) as string;
        ShowMarker(-1);
        if (row < 0 || text is null) return;
        var indexes = text.Split(',').Select(s => int.TryParse(s, out var i) ? i : -1).Where(i => i >= 0).ToList();
        e.Effects = DragDropEffects.Move;
        Moved?.Invoke(indexes, row);
    }

    /// The row a drop at `at` lands before: the upper half of a row means before it, the lower half after it.
    int DropRow(Point at)
    {
        if (Box.ItemsSource is not List<PageRow.Model> rows) return -1;
        if (ItemAt(Box.InputHitTest(at) as DependencyObject) is { } item && item.DataContext is PageRow.Model model)
            return at.Y - item.TranslatePoint(new Point(), Box).Y > item.ActualHeight / 2 ? model.Index + 1 : model.Index;
        return at.Y > Box.ActualHeight / 2 ? rows.Count : Math.Max(dropRow, 0);
    }

    void ShowMarker(int row)
    {
        dropRow = row;
        double y = -1;
        if (row >= 0 && Box.ItemsSource is List<PageRow.Model> rows && rows.Count > 0)
        {
            bool after = row >= rows.Count;
            if (Box.ItemContainerGenerator.ContainerFromIndex(after ? rows.Count - 1 : row) is ListBoxItem item && item.IsVisible)
                y = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), overlay).Y;
        }
        if (y >= 0)
        {
            Canvas.SetLeft(marker, 10);
            Canvas.SetTop(marker, Math.Max(0, y - 1));
            marker.Width = Math.Max(0, overlay.ActualWidth - 20);
        }
        markerFade.Target = y >= 0 ? 1 : 0;
        driver.Start();
    }

    void AutoScroll(Point at)
    {
        if (FindScroller(Box) is not { } scroller) return;
        if (at.Y < 28) scroller.LineUp();
        else if (at.Y > Box.ActualHeight - 28) scroller.LineDown();
    }

    static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer s) return s;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }
}

/// One row of the page list; built from its DataContext (no bindings, code-only WPF). Recycled rows ask for their
/// new page's thumbnail.
sealed class PageRow : Grid
{
    public sealed record Model(PdfPageItem Item, int Index, PdfPagesWindow Owner);

    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Border sheet = new() { Background = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) };
    readonly Viewbox frame = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Width = 70, Height = 90 };
    readonly TextBlock page = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock source = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressBar spinner = new() { IsIndeterminate = true, Width = 40, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    Model? shown;

    /// The three columns: preview, page, source file (the Mac table's 105 / 100 / rest).
    public static Grid Columns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    public PageRow()
    {
        Height = PdfPageList.RowHeight;
        foreach (var column in Columns().ColumnDefinitions.ToList())
            ColumnDefinitions.Add(new ColumnDefinition { Width = column.Width });
        sheet.Child = image;
        sheet.Visibility = Visibility.Hidden;
        frame.Child = sheet;
        var paper = new Grid { Width = 70, Height = 90, HorizontalAlignment = HorizontalAlignment.Left };
        paper.Children.Add(spinner);
        paper.Children.Add(frame);
        Children.Add(paper);
        SetColumn(page, 1);
        SetColumn(source, 2);
        Children.Add(page);
        Children.Add(source);
        DataContextChanged += (_, _) => Update();
    }

    void Update()
    {
        if (DataContext is not Model model || model == shown) return;
        bool samePage = shown is { } old && old.Item.Source == model.Item.Source && old.Item.Page == model.Item.Page;
        shown = model;
        page.Text = L("Page %d", model.Index + 1);
        source.Text = L("%@ · original page %d", Path.GetFileName(model.Item.Source), model.Item.Page + 1);
        source.ToolTip = source.Text;
        ToolUI.AutomationName(this, page.Text);
        sheet.LayoutTransform = model.Item.Rotation == 0 ? Transform.Identity : new RotateTransform(model.Item.Rotation);
        if (samePage && image.Source != null) return;
        image.Source = null;
        sheet.Visibility = Visibility.Hidden;
        spinner.Visibility = Visibility.Visible;
        model.Owner.Thumbnails?.Request(model.Item, thumb =>
        {
            if (shown != model) return;
            spinner.Visibility = Visibility.Collapsed;
            if (thumb is null) return;
            // Sized in pixels (about twice the box; the bitmap says its render DPI, so its own size is no use) and scaled
            // down by the Viewbox.
            image.Source = thumb;
            image.Width = thumb.PixelWidth;
            image.Height = thumb.PixelHeight;
            sheet.Visibility = Visibility.Visible;
            ToolUI.FadeIn(sheet);
        });
    }
}

/// The settings page's content: how it works, Choose Files…, and a hint while the feature is off.
sealed class PdfPagesPane : Pane
{
    readonly Button choose;
    readonly TextBlock offHint;

    public PdfPagesPane()
    {
        Add(Ui.Text(L("Select page ranges, drag thumbnails to change order, rotate or remove pages, then export selected pages or save all. The original PDFs are kept."), tone: Tone.Secondary));
        choose = Ui.Button(L("Choose Files…"), () => FileTools.ChooseAndOpen(Feature.PdfPages), accent: true);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { choose } }, 16);
        Add(Ui.Text(L("Also available in the Wheel’s Tools menu and Quick Actions."), 12, tone: Tone.Secondary), 16);
        offHint = Ui.Text(L("Turn this feature on above to get started."), 12, tone: Tone.Secondary);
        Add(offHint, 10);
        Refresh();
    }

    internal Button Choose => choose;

    public override void Refresh()
    {
        bool on = Feature.PdfPages.IsOn();
        choose.IsEnabled = on;
        offHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
