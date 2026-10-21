using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Session-only references: staging and removing items never copies or deletes files.
sealed class ShelfContents
{
    readonly List<string> paths = [];

    public IReadOnlyList<string> Paths => paths;

    /// Full path without a trailing separator, so "C:\a\" and "c:\A" are the same reference.
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
    }

    public int IndexOf(string path)
    {
        string key;
        try { key = Normalize(path); } catch (Exception) { return -1; }
        return paths.FindIndex(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool Contains(string path) => IndexOf(path) >= 0;

    /// Returns the rows that were added and the rows of candidates that were already on the shelf. Only existing
    /// files and folders (rooted local or network paths) are taken.
    public (SortedSet<int> Added, SortedSet<int> Existing) Add(IEnumerable<string> candidates)
    {
        var added = new SortedSet<int>();
        var existing = new SortedSet<int>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !System.IO.Path.IsPathFullyQualified(candidate)) continue;
            string path;
            try { path = Normalize(candidate); } catch (Exception) { continue; }
            int index = IndexOf(path);
            if (index >= 0)
            {
                if (!added.Contains(index)) existing.Add(index);
            }
            else if (File.Exists(path) || Directory.Exists(path))
            {
                paths.Add(path);
                added.Add(paths.Count - 1);
            }
        }
        return (added, existing);
    }

    public void Remove(IEnumerable<int> indexes)
    {
        var drop = indexes.ToHashSet();
        var kept = paths.Where((_, i) => !drop.Contains(i)).ToList();
        paths.Clear();
        paths.AddRange(kept);
    }

    public void Clear() => paths.Clear();

    /// Puts removed references back where they were (for undo), skipping any that were added again meanwhile.
    /// Returns the rows they now occupy.
    public SortedSet<int> Restore(IEnumerable<(int Offset, string Path)> items)
    {
        var rows = new SortedSet<int>();
        foreach (var item in items.OrderBy(i => i.Offset))
        {
            if (Contains(item.Path)) continue;
            int index = Math.Min(item.Offset, paths.Count);
            rows = new SortedSet<int>(rows.Select(r => r >= index ? r + 1 : r));
            paths.Insert(index, item.Path);
            rows.Add(index);
        }
        return rows;
    }
}

/// The File Shelf (Shelf.swift): gather files from different places, then drag a selection out together.
/// A floating, non-activating panel (see `ToolPanel`); it lasts for the session and is empty after a restart.
sealed class Shelf
{
    public static Shelf Shared { get; } = new();

    ToolPanel? panel;
    ListBox table = null!;
    readonly ShelfContents contents = new();
    readonly UndoStack undo = new();
    readonly List<ShelfCell> cells = [];
    readonly Dictionary<string, bool> available = new(StringComparer.OrdinalIgnoreCase);
    TextBlock status = null!;
    FrameworkElement empty = null!;
    Button removeButton = null!, clearButton = null!, previewButton = null!;
    MenuItem previewItem = null!, revealItem = null!, removeItem = null!;
    DropRing ring = null!;
    /// A short message ("2 removed · Ctrl+Z to undo") shown in place of the counts for a few seconds.
    string? notice;
    int noticeGeneration;
    int availabilityGeneration;
    ShelfPreview? preview;
    // Dragging out
    Point? pressPoint;
    ListBoxItem? deferredSelect;
    bool draggingOut;

    /// The references on the shelf, in order (for tests and the settings page).
    public IReadOnlyList<string> Items => contents.Paths;

    public bool IsVisible => panel?.IsVisible == true;

    // Test hooks.
    internal ToolPanel? Panel => panel;
    internal string StatusText => status?.Text ?? "";
    internal void SelectRows(IEnumerable<int> rows)
    {
        table.SelectedItems.Clear();
        foreach (var row in rows) table.SelectedItems.Add(cells[row]);
    }
    internal void RemoveSelectedForTests() => RemoveSelected();
    internal void UndoForTests() => undo.Undo();

    /// Adds files without taking focus, so collecting from Explorer or the Wheel never interrupts the drag.
    public void Add(IReadOnlyList<string> paths)
    {
        if (!Feature.Shelf.IsOn()) return;
        panel ??= MakePanel();
        bool wasVisible = panel.IsVisible;
        var (added, existing) = contents.Add(paths);
        foreach (var row in added) available[contents.Paths[row]] = true;
        if (wasVisible)
        {
            InsertRows(added, animate: true);
        }
        else
        {
            Refresh();
            panel.Present(focus: false);
        }
        if (added.Count == 0 && existing.Count > 0) ShowNotice(L("Already on the shelf"));
        // Point at what arrived (or what was already there) once the rows exist.
        var highlight = added.Union(existing).ToList();
        panel.Dispatcher.BeginInvoke(() => Highlight(highlight), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Show()
    {
        if (!Feature.Shelf.IsOn()) return;
        panel ??= MakePanel();
        Refresh();
        panel.Present(focus: true);
        table.Focus();
    }

    public void Hide()
    {
        preview?.Close();
        panel?.Dismiss();
    }

    ToolPanel MakePanel()
    {
        var window = new ToolPanel
        {
            Title = L("File Shelf"),
            Width = 480, Height = 470, MinWidth = 420, MinHeight = 330,
            AllowDrop = true,
        };
        PlaceWindow(window);

        table = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(table, false);
        table.SelectionChanged += (_, _) => SelectionChanged();
        table.PreviewMouseLeftButtonDown += PressRow;
        table.PreviewMouseMove += DragRows;
        table.PreviewMouseLeftButtonUp += ReleaseRow;
        table.MouseDoubleClick += (_, e) =>
        {
            if (RowAt(e.OriginalSource) is { } cell && Available(cell.Path)) FileActions.Open(cell.Path);
        };

        var menu = new ContextMenu();
        previewItem = new MenuItem { Header = L("Preview"), InputGestureText = "Space" };
        previewItem.Click += (_, _) => Preview();
        revealItem = new MenuItem { Header = L("Show in File Explorer") };
        revealItem.Click += (_, _) => Reveal();
        removeItem = new MenuItem { Header = L("Remove from Shelf"), InputGestureText = "Del" };
        removeItem.Click += (_, _) => RemoveSelected();
        menu.Items.Add(previewItem);
        menu.Items.Add(revealItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);
        table.ContextMenu = menu;
        table.ContextMenuOpening += (_, e) =>
        {
            if (RowAt(e.OriginalSource) is { } cell && !table.SelectedItems.Contains(cell))
            {
                table.SelectedItems.Clear();
                table.SelectedItem = cell;
            }
            if (table.SelectedItems.Count == 0) { e.Handled = true; return; }
            removeItem.IsEnabled = SelectedPaths.Count > 0;
            previewItem.IsEnabled = revealItem.IsEnabled = AvailableSelection.Count > 0;
        };

        var listFrame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            MinHeight = 160,
            Child = table,
        };
        empty = ToolUI.EmptyState("\uE7B8", L("Drop files here, then select several and drag them out together."));
        ring = new DropRing();
        var area = new Grid();
        area.Children.Add(listFrame);
        area.Children.Add(empty);
        area.Children.Add(ring);

        status = ToolUI.Note("");
        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.VerticalAlignment = VerticalAlignment.Center;
        var addFiles = ToolUI.Button(L("Add Files…"), () => Add(ToolUI.ChooseFiles(imagesOnly: false, multiple: true, owner: panel)));
        var addFolder = ToolUI.Button(L("Add Folder…"), () => Add(ToolUI.ChooseFolders(multiple: true, owner: panel)));
        var top = ToolUI.Bar([status], [addFolder, addFiles]);

        previewButton = ToolUI.Button(L("Preview"), Preview);
        removeButton = ToolUI.Button(L("Remove from Shelf"), RemoveSelected);
        clearButton = ToolUI.Button(L("Clear"), ClearAll);
        var buttons = ToolUI.Bar([previewButton], [removeButton, clearButton]);
        var note = ToolUI.Note(L("Removing an item only clears its reference. Original files stay where they are. This shelf lasts until you quit."));

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        top.Margin = new Thickness(0, 0, 0, 12);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        note.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(buttons);
        root.Children.Add(area);
        window.Content = root;

        window.PreviewKeyDown += KeyDown;
        window.DragEnter += (_, e) => DragOver(e, entered: true);
        window.DragOver += (_, e) => DragOver(e, entered: false);
        window.DragLeave += (_, _) => ring.SetShown(false);
        window.Drop += (_, e) =>
        {
            ring.SetShown(false);
            var paths = ToolUI.DroppedFiles(e.Data);
            if (!Feature.Shelf.IsOn() || draggingOut || paths.Count == 0) { e.Effects = DragDropEffects.None; return; }
            e.Effects = ToolUI.DropEffect(e.AllowedEffects);
            e.Handled = true;
            Add(paths);
        };
        window.Activated += (_, _) => CheckAvailability();
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible) { preview?.Close(); SaveFrame(window); }
        };
        return window;
    }

    // MARK: Window position (remembered for the session's next show and across launches, like a frame autosave)

    static void PlaceWindow(Window window)
    {
        var frame = Prefs.Get<double[]>("shelf.frame");
        var area = SystemParameters.WorkArea;
        if (frame is { Length: 4 } && frame[2] >= 420 && frame[3] >= 330 &&
            frame[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 && frame[0] + frame[2] > SystemParameters.VirtualScreenLeft + 80 &&
            frame[1] >= SystemParameters.VirtualScreenTop && frame[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            (window.Left, window.Top, window.Width, window.Height) = (frame[0], frame[1], frame[2], frame[3]);
            return;
        }
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }

    static void SaveFrame(Window window)
    {
        if (double.IsNaN(window.Left) || window.WindowState != WindowState.Normal) return;
        Prefs.Set("shelf.frame", new[] { window.Left, window.Top, window.Width, window.Height }, notify: false);
    }

    // MARK: State

    List<string> SelectedPaths => table.SelectedItems.Cast<ShelfCell>().OrderBy(c => cells.IndexOf(c)).Select(c => c.Path).ToList();
    List<string> AvailableSelection => SelectedPaths.Where(Available).ToList();
    bool Available(string path) => !available.TryGetValue(path, out var exists) || exists;
    List<int> SelectedRows => table.SelectedItems.Cast<ShelfCell>().Select(c => cells.IndexOf(c)).Where(i => i >= 0).Order().ToList();

    void Refresh()
    {
        if (panel is null) return;
        panel.Title = L("File Shelf");
        var selected = SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        table.Items.Clear();
        cells.Clear();
        foreach (var path in contents.Paths)
        {
            var cell = MakeCell(path);
            cells.Add(cell);
            table.Items.Add(cell);
            if (selected.Contains(path)) table.SelectedItems.Add(cell);
        }
        UpdateEmpty();
        UpdateSelection();
        CheckAvailability();
    }

    ShelfCell MakeCell(string path)
    {
        var cell = new ShelfCell(path);
        cell.Configure(Available(path));
        cell.LoadIcon();
        return cell;
    }

    void UpdateEmpty() => empty.Visibility = contents.Paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateSelection()
    {
        if (panel is null) return;
        status.Text = notice ?? L("%d files · %d selected", contents.Paths.Count, table.SelectedItems.Count);
        removeButton.IsEnabled = SelectedPaths.Count > 0;
        clearButton.IsEnabled = contents.Paths.Count > 0;
        previewButton.IsEnabled = AvailableSelection.Count > 0;
    }

    void SelectionChanged()
    {
        UpdateSelection();
        // The open preview follows the selection.
        if (preview is { IsVisible: true })
        {
            var items = AvailableSelection;
            if (items.Count > 0) preview.Show(items, 0);
        }
    }

    void ShowNotice(string text)
    {
        int token = ++noticeGeneration;
        notice = text;
        UpdateSelection();
        ToolUI.After(4, () =>
        {
            if (noticeGeneration != token) return;
            notice = null;
            UpdateSelection();
        });
    }

    /// Re-checks which files still exist off the UI thread (network or sleeping drives can stall), then redraws only
    /// the rows that changed.
    async void CheckAvailability()
    {
        var paths = contents.Paths.ToList();
        if (panel?.IsVisible != true || paths.Count == 0) return;
        int token = ++availabilityGeneration;
        var found = await Task.Run(() => paths.Select(p => File.Exists(p) || Directory.Exists(p)).ToList());
        if (token != availabilityGeneration) return;
        bool changed = false;
        for (int i = 0; i < paths.Count; i++)
        {
            if (available.TryGetValue(paths[i], out var was) && was == found[i]) continue;
            bool known = available.ContainsKey(paths[i]);
            available[paths[i]] = found[i];
            if (!known && found[i]) continue;
            if (cells.FirstOrDefault(c => string.Equals(c.Path, paths[i], StringComparison.OrdinalIgnoreCase)) is { } cell) cell.Configure(found[i]);
            changed = true;
        }
        if (changed) UpdateSelection();
    }

    // MARK: Rows arriving and leaving

    void InsertRows(IEnumerable<int> rows, bool animate)
    {
        foreach (var row in rows.Order())
        {
            var cell = MakeCell(contents.Paths[row]);
            cells.Insert(row, cell);
            table.Items.Insert(row, cell);
            if (animate) cell.Arrive();
        }
        UpdateEmpty();
        UpdateSelection();
    }

    void RemoveRows(IEnumerable<int> rows)
    {
        foreach (var row in rows.OrderDescending())
        {
            if (row >= cells.Count) continue;
            table.Items.Remove(cells[row]);
            cells.RemoveAt(row);
        }
        UpdateEmpty();
        UpdateSelection();
    }

    /// Scrolls to the rows and gives them a brief accent glow, so it's clear what just landed.
    void Highlight(IReadOnlyCollection<int> rows)
    {
        if (rows.Count == 0) return;
        int last = rows.Max();
        if (last < cells.Count) table.ScrollIntoView(cells[last]);
        foreach (var row in rows) if (row < cells.Count) cells[row].Flash();
    }

    /// Takes rows off the shelf, with Ctrl+Z to bring them back; with undo, Clear doesn't need a confirmation.
    void RemoveReferences(IEnumerable<int> rows)
    {
        var removed = rows.Where(r => r >= 0 && r < contents.Paths.Count).Distinct().Order()
            .Select(r => (Offset: r, Path: contents.Paths[r])).ToList();
        if (removed.Count == 0) return;
        contents.Remove(removed.Select(r => r.Offset));
        table.SelectedItems.Clear();
        RemoveRows(removed.Select(r => r.Offset));
        undo.Register(() => RestoreReferences(removed));
        ShowNotice(L("%d removed · Ctrl+Z to undo", removed.Count));
    }

    void RestoreReferences(List<(int Offset, string Path)> items)
    {
        var rows = contents.Restore(items);
        foreach (var (_, path) in items) available[path] = true;
        InsertRows(rows, animate: true);
        table.SelectedItems.Clear();
        foreach (var row in rows) table.SelectedItems.Add(cells[row]);
        // Redo removes the same files again, wherever they are now.
        var paths = items.Select(i => i.Path).ToList();
        undo.Register(() => RemoveReferences(paths.Select(contents.IndexOf).Where(i => i >= 0)));
        noticeGeneration++;
        notice = null;
        UpdateSelection();
        table.Dispatcher.BeginInvoke(() => Highlight(rows.ToList()), System.Windows.Threading.DispatcherPriority.Loaded);
        CheckAvailability();
    }

    void RemoveSelected() => RemoveReferences(SelectedRows);
    void ClearAll() => RemoveReferences(Enumerable.Range(0, contents.Paths.Count));
    void Reveal() => FileActions.Reveal(AvailableSelection);

    /// Space toggles a preview of the selected files (Windows has no Quick Look: images and text show in a small
    /// window, anything else opens in its app).
    void Preview()
    {
        if (preview is { IsVisible: true })
        {
            preview.Close();
            return;
        }
        var items = AvailableSelection;
        if (items.Count == 0 || panel is null) return;
        if (items.Count == 1 && !ShelfPreview.CanPreview(items[0]))
        {
            try { FileActions.Open(items[0]); }
            catch (Exception ex) { Toast.Shared.Show(ex.Message, "error", tint: ToastTint.Red); }
            return;
        }
        preview = new ShelfPreview(panel, MoveSelection);
        preview.Closed += (_, _) => preview = null;
        preview.Show(items, 0);
    }

    /// ↑ and ↓ move through the shelf while the preview has the keyboard, as in Explorer's preview pane.
    void MoveSelection(int delta)
    {
        if (cells.Count == 0) return;
        int current = SelectedRows.LastOrDefault(-1);
        int row = Math.Clamp(current + delta, 0, cells.Count - 1);
        table.SelectedItems.Clear();
        table.SelectedItem = cells[row];
        table.ScrollIntoView(cells[row]);
    }

    void KeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Space when modifiers == ModifierKeys.None:
                Preview();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when modifiers == ModifierKeys.None:
                RemoveSelected();
                e.Handled = true;
                break;
            case Key.Z when modifiers == ModifierKeys.Control:
                undo.Undo();
                e.Handled = true;
                break;
            case Key.Y when modifiers == ModifierKeys.Control:
            case Key.Z when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                undo.Redo();
                e.Handled = true;
                break;
            case Key.Return when modifiers == ModifierKeys.None:
                foreach (var path in AvailableSelection.Take(8)) FileActions.Open(path);
                e.Handled = true;
                break;
        }
    }

    // MARK: Dragging out and in

    ShelfCell? RowAt(object source) =>
        source is DependencyObject element && ItemsControl.ContainerFromElement(table, element) is ListBoxItem item ? item.Content as ShelfCell : null;

    /// A press on a row that's already selected keeps the whole selection, so several files can be dragged together;
    /// a click without a drag then selects just that row, as in Explorer.
    void PressRow(object sender, MouseButtonEventArgs e)
    {
        pressPoint = e.GetPosition(table);
        deferredSelect = null;
        if (ItemsControl.ContainerFromElement(table, (DependencyObject)e.OriginalSource) is ListBoxItem item && item.IsSelected &&
            Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
        {
            deferredSelect = item;
            item.Focus();
            e.Handled = true;
        }
    }

    void ReleaseRow(object sender, MouseButtonEventArgs e)
    {
        if (deferredSelect is { } item && pressPoint != null)
        {
            table.SelectedItems.Clear();
            item.IsSelected = true;
        }
        deferredSelect = null;
        pressPoint = null;
    }

    void DragRows(object sender, MouseEventArgs e)
    {
        if (pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(table);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pressPoint = null;
        deferredSelect = null;
        if (!Feature.Shelf.IsOn()) return;
        var files = AvailableSelection.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, files);
        draggingOut = true;
        try
        {
            // Copy or Link only: never Move, which would take the originals away.
            DragDrop.DoDragDrop(table, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (System.Runtime.InteropServices.COMException) { }
        finally { draggingOut = false; }
    }

    void DragOver(DragEventArgs e, bool entered)
    {
        bool accept = Feature.Shelf.IsOn() && !draggingOut && ToolUI.DroppedFiles(e.Data).Count > 0;
        e.Effects = accept ? ToolUI.DropEffect(e.AllowedEffects) : DragDropEffects.None;
        if (accept && entered) Feedback.Hover(sound: false);
        ring.SetShown(accept);
        e.Handled = true;
    }
}

/// One file: icon (or thumbnail), name and folder, or a red note when the file has gone. It glides in and glows
/// briefly in the accent colour when it arrives.
sealed class ShelfCell : Grid
{
    public string Path { get; }
    readonly Image icon = new() { Width = 30, Height = 30, Stretch = Stretch.Uniform };
    readonly TextBlock glyph;
    readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock folder = ToolUI.Scaled(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top }, 11);
    readonly Border glow = new() { CornerRadius = new CornerRadius(6), Opacity = 0, Margin = new Thickness(-4, -1, -4, -1), IsHitTestVisible = false };
    readonly TranslateTransform shift = new();
    readonly FrameDriver driver = new();
    Spring slide = new(0, response: 0.32, damping: 0.8);
    Spring fade = new(1, response: 0.2, damping: 1);
    Spring glowLevel = new(0, response: 0.1, damping: 1);
    double glowAge = -1;

    public ShelfCell(string path)
    {
        Path = path;
        MinHeight = 44;   // grows with larger text instead of clipping it
        RenderTransform = shift;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        var accent = new SolidColorBrush(Color.FromArgb(0x38, ToolUI.AccentColor.R, ToolUI.AccentColor.G, ToolUI.AccentColor.B));
        accent.Freeze();
        glow.Background = accent;
        SetColumnSpan(glow, 2);
        SetRowSpan(glow, 2);
        Children.Add(glow);
        glyph = ToolUI.Glyph(FileKinds.Of(path).Glyph(), 24);
        glyph.Opacity = 0.7;
        foreach (var view in new FrameworkElement[] { glyph, icon })
        {
            SetRowSpan(view, 2);
            view.HorizontalAlignment = HorizontalAlignment.Left;
            Children.Add(view);
        }
        SetColumn(title, 1);
        SetColumn(folder, 1);
        SetRow(folder, 1);
        title.Margin = new Thickness(0, 0, 0, 1);
        folder.Margin = new Thickness(0, 2, 0, 0);
        Children.Add(title);
        Children.Add(folder);
        title.Text = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        ToolTip = path;
        ToolUI.AutomationName(this, title.Text);
        driver.OnFrame = Frame;
    }

    public void Configure(bool available)
    {
        folder.Text = available ? System.IO.Path.GetDirectoryName(Path) ?? Path : L("File is no longer available");
        if (available)
        {
            folder.ClearValue(TextBlock.ForegroundProperty);
            folder.Opacity = 0.68;
        }
        else
        {
            folder.Foreground = ToolUI.ErrorBrush;
            folder.Opacity = 1;
        }
    }

    /// Icons are looked up once per file (on a background thread); reloading rows never touches the disk again.
    public void LoadIcon()
    {
        if (IconCache.TryGetValue(Path, out var cached))
        {
            Apply(cached);
            return;
        }
        ShellIcons.Request(Path, 64, image =>
        {
            IconCache[Path] = image;
            Apply(image);
        });
    }

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    void Apply(ImageSource? image)
    {
        icon.Source = image;
        glyph.Visibility = image is null ? Visibility.Visible : Visibility.Hidden;
    }

    /// Glides down into place while fading in; with animations off it only fades.
    public void Arrive()
    {
        fade.Snap(0);
        fade.Target = 1;
        if (!Motion.Reduced)
        {
            slide.Snap(-12);
            slide.Target = 0;
        }
        Opacity = 0;
        shift.Y = slide.Value;
        driver.Start();
    }

    /// Rises fast, holds, then fades: a trace of what arrived rather than motion (the same with animations off).
    public void Flash()
    {
        glowAge = 0;
        glowLevel.Response = 0.1;
        glowLevel.Target = 1;
        driver.Start();
    }

    bool Frame(double dt)
    {
        slide.Step(dt, ToolUI.Tuning);
        fade.Step(dt, ToolUI.Tuning);
        shift.Y = slide.Value;
        Opacity = Math.Clamp(fade.Value, 0, 1);
        if (glowAge >= 0)
        {
            glowAge += dt;
            // Hold near full for about a third of the 0.9 s pulse, then let it go.
            if (glowAge > 0.32 && glowLevel.Target > 0)
            {
                glowLevel.Target = 0;
                glowLevel.Response = 0.55;
            }
            glowLevel.Step(dt, ToolUI.Tuning);
            glow.Opacity = Math.Clamp(glowLevel.Value, 0, 1);
            if (glowLevel.Target == 0 && glowLevel.IsSettled) { glowAge = -1; glow.Opacity = 0; }
        }
        bool running = !slide.IsSettled || !fade.IsSettled || glowAge >= 0;
        if (!running) { shift.Y = 0; Opacity = 1; }
        return running;
    }
}

/// A small preview for the shelf's Space key: images and text files, with ←/→ through the selected files and ↑/↓
/// through the shelf. Anything else shows its icon with an Open button.
sealed class ShelfPreview : Window
{
    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "csv", "tsv", "json", "xml", "yaml", "yml", "ini", "cfg", "conf", "toml", "html", "htm", "css",
        "js", "ts", "jsx", "tsx", "cs", "swift", "py", "rb", "go", "rs", "java", "kt", "c", "h", "cpp", "hpp", "m", "sh", "ps1", "bat",
        "cmd", "sql", "srt", "vtt", "gitignore", "editorconfig", "svg",
    };

    public static bool CanPreview(string path) =>
        File.Exists(path) && (FileKinds.Of(path) == FileKind.Image || TextExtensions.Contains(FileKinds.Ext(path)));

    readonly Action<int> moveShelf;
    readonly ContentControl body = new();
    readonly TextBlock counter = ToolUI.Note("");
    List<string> items = [];
    int index;
    int generation;

    public ShelfPreview(Window owner, Action<int> moveShelf)
    {
        this.moveShelf = moveShelf;
        Owner = owner;
        Width = 720;
        Height = 540;
        MinWidth = 320;
        MinHeight = 240;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppTypography.Apply(this);
        counter.VerticalAlignment = VerticalAlignment.Center;
        var open = ToolUI.Button(L("Open"), () => { if (Current is { } path) FileActions.Open(path); });
        var reveal = ToolUI.Button(L("Show in File Explorer"), () => { if (Current is { } path) FileActions.Reveal([path]); });
        var bar = ToolUI.Bar([counter], [reveal, open]);
        DockPanel.SetDock(bar, Dock.Bottom);
        bar.Margin = new Thickness(0, 12, 0, 0);
        var root = new DockPanel { Margin = new Thickness(16) };
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space or Key.Escape: Close(); e.Handled = true; break;
                case Key.Left: Step(-1); e.Handled = true; break;
                case Key.Right: Step(1); e.Handled = true; break;
                case Key.Up: moveShelf(-1); e.Handled = true; break;
                case Key.Down: moveShelf(1); e.Handled = true; break;
            }
        };
    }

    string? Current => index >= 0 && index < items.Count ? items[index] : null;

    public void Show(IReadOnlyList<string> files, int start)
    {
        items = [.. files];
        index = Math.Clamp(start, 0, Math.Max(0, items.Count - 1));
        Render();
        if (!IsVisible) Show();
        Activate();
    }

    void Step(int delta)
    {
        if (items.Count < 2) return;
        index = (index + delta + items.Count) % items.Count;
        Render();
    }

    async void Render()
    {
        if (Current is not { } path) return;
        int token = ++generation;
        Title = System.IO.Path.GetFileName(path);
        counter.Text = items.Count > 1 ? L("%d of %d", index + 1, items.Count) : System.IO.Path.GetDirectoryName(path) ?? "";
        if (FileKinds.Of(path) == FileKind.Image)
        {
            body.Content = ToolUI.EmptyState(FileKind.Image.Glyph(), Title);
            try
            {
                var image = await Task.Run(() =>
                {
                    BitmapSource loaded = Images.Load(path, 2000);
                    if (loaded.CanFreeze) loaded.Freeze();
                    return loaded;
                });
                if (token != generation) return;
                var view = new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
                body.Content = view;
                ToolUI.FadeIn(view);
            }
            catch (Exception)
            {
                if (token == generation) body.Content = Fallback(path);
            }
        }
        else if (TextExtensions.Contains(FileKinds.Ext(path)))
        {
            try
            {
                var text = await Task.Run(() => ReadStart(path));
                if (token != generation) return;
                body.Content = ToolUI.Scaled(new TextBox
                {
                    Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalContentAlignment = VerticalAlignment.Top,
                }, 12.5);
            }
            catch (Exception) { if (token == generation) body.Content = Fallback(path); }
        }
        else body.Content = Fallback(path);
    }

    /// The first 256 KB, so a huge log never stalls the preview.
    static string ReadStart(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[256 * 1024];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read) + (reader.EndOfStream ? "" : "\n…");
    }

    static FrameworkElement Fallback(string path)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Image { Width = 96, Height = 96, Source = ShellIcons.Load(path, 192) };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        return stack;
    }
}
