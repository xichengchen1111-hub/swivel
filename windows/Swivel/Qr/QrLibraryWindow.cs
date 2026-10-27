using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Saved codes by collection, each shown as its scene (QRLibraryWindow.swift): names, collections, search, add / edit /
/// remove, undo of this window's own changes, import from images, and a plain QR PNG to copy or export.
sealed class QrLibraryWindow : QrToolWindow
{
    static readonly List<QrLibraryWindow> open = [];

    public QrLibraryStore Store { get; }
    public IReadOnlyList<SavedQrCode> Entries { get; private set; } = [];
    readonly List<(IReadOnlyList<SavedQrCode> Before, IReadOnlyList<SavedQrCode> After)> undoValues = [];
    public ListBox List { get; } = new();
    public TextBox Search { get; } = new() { Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
    public QrGardenView Garden { get; } = new();
    public FrameworkElement EmptyState { get; }
    readonly TextBlock titleLabel, emptyTitle, emptyHint;
    readonly TextBox content = new()
    {
        IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
        TextWrapping = TextWrapping.Wrap, MaxLines = 3, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
    };
    readonly TextBlock status = ToolUI.Note("");
    public SelectorBar Mode { get; } = new([L("Scene"), L("QR Code")]);
    public ComboBox Season { get; } = new() { MinWidth = 120 };
    public ComboBox Template { get; } = new() { MinWidth = 120 };
    public Button EditButton { get; }
    public Button RemoveButton { get; }
    public Button UndoButton { get; }
    public Button CopyButton { get; }
    public Button ExportButton { get; }
    /// Both "New Code…" buttons (beside the list and in the empty state).
    public IReadOnlyList<Button> NewButtons { get; }
    /// Where Copy QR Image puts the picture (tests leave the user's clipboard alone).
    public Action<BitmapSource> SetClipboardImage { get; set; } = image => FileActions.RetryClipboard(() => Clipboard.SetImage(image));
    readonly Action<string> libraryChanged;
    string? workMessage;
    bool syncing;

    public string Status => status.Text;
    internal string EmptyTitleText => emptyTitle.Text;
    internal string TitleText => titleLabel.Text;
    public bool CanUndo => undoValues.Count > 0;

    /// Shows the library (the window already open on it, if any) with a code selected.
    public static void Show(Guid? select = null, QrLibraryStore? store = null)
    {
        if (!Feature.QrCodes.IsOn()) return;
        store ??= new QrLibraryStore();
        var controller = open.FirstOrDefault(w => string.Equals(w.Store.FilePath, store.FilePath, StringComparison.OrdinalIgnoreCase));
        if (controller is null)
        {
            controller = new QrLibraryWindow(store, select);
            open.Add(controller);
        }
        else if (select is { } id && !controller.IsBusy) controller.Reload(id);
        ToolUI.Show(controller.Window);
    }

    public static void CloseAll()
    {
        foreach (var controller in open.ToList())
        {
            if (controller.IsBusy) continue;
            controller.Window.Close();
        }
    }

    public QrLibraryWindow(QrLibraryStore? store = null, Guid? select = null) : base(L("QR Library"), 1030, 750)
    {
        Store = store ?? new QrLibraryStore();
        Window.MinWidth = 940;
        Window.MinHeight = 670;

        // Left: search, the codes by collection, New / Import.
        ToolUI.AutomationName(Search, L("Search saved codes…"));
        Search.TextChanged += (_, _) => { if (!syncing) Rebuild(Selected?.Id); };
        var searchHint = new TextBlock { Text = L("Search saved codes…"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        searchHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Search.TextChanged += (_, _) => searchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchBox = new Grid { Children = { Search, searchHint } };
        ToolUI.AutomationName(List, L("QR Library"));
        List.SelectionChanged += (_, _) => { if (!syncing) ShowSelection(); };
        List.MinHeight = 430;
        ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        var add = ToolUI.Button(L("New Code…"), AddCode);
        var imported = ToolUI.Button(L("Import Images…"), ImportImages);
        var leftButtons = ToolUI.Row([add, imported], spacing: 8);
        var left = new DockPanel { Width = 245 };
        DockPanel.SetDock(searchBox, Dock.Top);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        leftButtons.Margin = new Thickness(0, 12, 0, 0);
        List.Margin = new Thickness(0, 12, 0, 0);
        left.Children.Add(searchBox);
        left.Children.Add(leftButtons);
        left.Children.Add(List);

        // Right: name, content, view controls, the scene (or the empty state), actions.
        titleLabel = ToolUI.Scaled(new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, 20);
        ToolUI.Scaled(content, 12);
        ToolUI.AutomationName(content, L("QR content"));
        Mode.SelectionChanged += index => { if (!syncing) Garden.SetShowsCode(index == 1); };
        foreach (var season in QrSeasons.All) Season.Items.Add(season.Title());
        foreach (var template in QrTemplates.All) Template.Items.Add(template.Title());
        ToolUI.AutomationName(Season, L("Season"));
        ToolUI.AutomationName(Template, L("Template"));
        Season.SelectionChanged += (_, _) =>
        {
            if (syncing || Selected is not { } entry || Season.SelectedIndex < 0) return;
            SaveEntry(entry with { Season = QrSeasons.All[Season.SelectedIndex] });
        };
        Template.SelectionChanged += (_, _) =>
        {
            if (syncing || Selected is not { } entry || Template.SelectedIndex < 0) return;
            SaveEntry(entry with { Template = QrTemplates.All[Template.SelectedIndex] });
        };
        Garden.OnToggle = code =>
        {
            syncing = true;
            Mode.Select(code ? 1 : 0, notify: false);
            syncing = false;
        };

        var icon = ToolUI.Glyph(Feature.QrCodes.Glyph(), 44);
        icon.Opacity = 0.5;
        emptyTitle = ToolUI.Scaled(new TextBlock { FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }, 19);
        emptyHint = ToolUI.Note("");
        emptyHint.TextAlignment = TextAlignment.Center;
        emptyHint.MaxWidth = 430;
        var emptyNew = ToolUI.Button(L("New Code…"), AddCode);
        emptyNew.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        var emptyActions = ToolUI.Row([emptyNew, ToolUI.Button(L("Import Images…"), ImportImages)], spacing: 8);
        emptyActions.HorizontalAlignment = HorizontalAlignment.Center;
        var emptyContent = ToolUI.Stack([icon, emptyTitle, emptyHint, emptyActions], spacing: 18);
        emptyContent.HorizontalAlignment = HorizontalAlignment.Center;
        emptyContent.VerticalAlignment = VerticalAlignment.Center;
        emptyContent.Margin = new Thickness(20);
        EmptyState = new Border { Child = emptyContent, CornerRadius = new CornerRadius(12) };
        EmptyState.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        NewButtons = [add, emptyNew];
        var preview = new Grid { MinHeight = 380, Children = { Garden, EmptyState } };

        EditButton = ToolUI.Button(L("Edit…"), EditCode);
        RemoveButton = ToolUI.Button(L("Remove"), RemoveCode);
        UndoButton = ToolUI.Button(L("Undo"), UndoChange);
        CopyButton = ToolUI.Button(L("Copy QR Image"), CopyImage);
        ExportButton = ToolUI.Button(L("Export PNG…"), ExportImage);
        var controls = ToolUI.Row([Mode, Template, Season], spacing: 12);
        Mode.Margin = new Thickness(-12, 0, 0, 0);
        var note = ToolUI.Note(L("Click the scene to look at it from above, where it becomes the QR code. Click again to look around it."));
        var actions = ToolUI.Row([EditButton, RemoveButton, UndoButton, CopyButton, ExportButton], spacing: 8);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.NoWrap;

        var detail = new DockPanel { MinWidth = 650 };
        var top = ToolUI.Stack([titleLabel, content, controls], spacing: 8);
        DockPanel.SetDock(top, Dock.Top);
        detail.Children.Add(top);
        var bottom = ToolUI.Stack([note, actions, status], spacing: 10);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);
        detail.Children.Add(bottom);
        preview.Margin = new Thickness(0, 12, 0, 0);
        detail.Children.Add(preview);

        var divider = new Border { Width = 1, Margin = new Thickness(18, 0, 18, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        if (divider.Background is null) divider.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        var body = new Grid { Margin = new Thickness(20) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(detail, 2);
        body.Children.Add(left);
        body.Children.Add(divider);
        body.Children.Add(detail);
        Window.Content = body;

        libraryChanged = path =>
        {
            if (!string.Equals(path, Store.FilePath, StringComparison.OrdinalIgnoreCase)) return;
            if (Window.Dispatcher.CheckAccess()) Reload(Selected?.Id);
            else Window.Dispatcher.BeginInvoke(() => Reload(Selected?.Id));
        };
        QrLibraryStore.FileChanged += libraryChanged;
        Window.Closed += (_, _) =>
        {
            QrLibraryStore.FileChanged -= libraryChanged;
            open.Remove(this);
        };
        Reload(select);
    }

    public SavedQrCode? Selected => (List.SelectedItem as ListBoxItem)?.Tag as SavedQrCode;

    void Reload(Guid? select = null)
    {
        try
        {
            Entries = Store.Load();
            Rebuild(select);
        }
        catch (ToolError e) { status.Text = e.Message; }
    }

    static bool Matches(SavedQrCode entry, string search)
    {
        if (search.Length == 0) return true;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
        return new[] { entry.Name, entry.Value, entry.Collection }.Any(text => compare.IndexOf(text, search, options) >= 0);
    }

    void Rebuild(Guid? select = null)
    {
        syncing = true;
        try
        {
            var matches = Entries.Where(e => Matches(e, Search.Text)).ToList();
            List.Items.Clear();
            ListBoxItem? chosen = null, first = null;
            foreach (var group in matches.GroupBy(e => e.Collection).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                List.Items.Add(Header(group.Key.Length == 0 ? L("Ungrouped") : group.Key));
                foreach (var entry in group)
                {
                    var item = Item(entry);
                    List.Items.Add(item);
                    first ??= item;
                    if (entry.Id == select) chosen = item;
                }
            }
            List.SelectedItem = chosen ?? first;
            (chosen ?? first)?.BringIntoView();
        }
        finally { syncing = false; }
        ShowSelection();
    }

    static ListBoxItem Header(string title)
    {
        var text = ToolUI.Note(title);
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        // Folder.
        var row = ToolUI.Row([ToolUI.Glyph("", 13), text], spacing: 6);
        row.Opacity = 0.85;
        return new ListBoxItem { Content = row, Focusable = false, IsHitTestVisible = false, Margin = new Thickness(0, 6, 0, 0) };
    }

    static ListBoxItem Item(SavedQrCode entry)
    {
        var glyph = ToolUI.Glyph(Feature.QrCodes.Glyph(), 14);
        glyph.Foreground = new SolidColorBrush(Native.DarkMode ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x10, 0x7C, 0x10));
        var text = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var item = new ListBoxItem { Content = ToolUI.Row([glyph, text], spacing: 8), Tag = entry, Padding = new Thickness(14, 6, 8, 6) };
        ToolUI.AutomationName(item, entry.Name);
        return item;
    }

    void ShowSelection()
    {
        var selected = Selected;
        EmptyState.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        Garden.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        emptyTitle.Text = Entries.Count == 0 ? L("Save your first QR code") : L("No matching codes");
        emptyHint.Text = Entries.Count == 0
            ? L("Create a code from a link or text, or import an image containing a QR code. Your saved codes will appear here as trees.")
            : L("Try another search, or add a new code.");
        titleLabel.Text = selected?.Name ?? L("Your QR collection");
        content.Text = selected?.Value ?? L("Add a code or import an image to plant your first tree.");
        status.Text = L("%d saved codes · stored on this PC", Entries.Count);
        syncing = true;
        try
        {
            // The same code shown again (another window saved something else) keeps its view.
            if (!Equals(Garden.Entry, selected))
            {
                try
                {
                    Garden.Display(selected);
                    Mode.Select(0, notify: false);
                }
                catch (ToolError e) { status.Text = e.Message; }
            }
            Season.SelectedIndex = Array.IndexOf(QrSeasons.All, selected?.Season ?? QrSeason.Summer);
            Template.SelectedIndex = Array.IndexOf(QrTemplates.All, selected?.Template ?? QrTemplate.Tree);
        }
        finally { syncing = false; }
        foreach (var button in new[] { EditButton, RemoveButton, CopyButton, ExportButton }) button.IsEnabled = selected != null && !IsBusy;
        Mode.IsEnabled = Season.IsEnabled = Template.IsEnabled = selected != null && !IsBusy;
        UndoButton.IsEnabled = undoValues.Count > 0 && !IsBusy;
    }

    protected override void WorkFinished()
    {
        ShowSelection();
        if (workMessage != null)
        {
            status.Text = workMessage;
            workMessage = null;
        }
    }

    protected override void ShowError(string message) => status.Text = message;

    void ClearSearch()
    {
        syncing = true;
        Search.Text = "";
        syncing = false;
    }

    void AddCode() => Edit(null);

    void EditCode()
    {
        if (Selected is { } selected) Edit(selected);
    }

    void Edit(SavedQrCode? value)
    {
        if (!Feature.QrCodes.IsOn()) return;
        SavedCodeEditor.Present(Window, value, SaveEntry);
    }

    /// Saves a new or edited code, clears the search so it shows, and selects it.
    public void SaveEntry(SavedQrCode entry)
    {
        if (!Feature.QrCodes.IsOn()) return;
        try
        {
            var before = Store.Load();
            var latest = before.ToList();
            int index = latest.FindIndex(e => e.Id == entry.Id);
            if (index >= 0) latest[index] = entry; else latest.Insert(0, entry);
            Store.Save(latest);
            undoValues.Add((before, latest));
            ClearSearch();
            Reload(entry.Id);
        }
        catch (ToolError e) { status.Text = e.Message; }
        catch (IOException e) { status.Text = e.Message; }
    }

    public void RemoveCode()
    {
        if (!Feature.QrCodes.IsOn() || Selected is not { } selected) return;
        try
        {
            var latest = Store.Load();
            var after = latest.Where(e => e.Id != selected.Id).ToList();
            Store.Save(after);
            undoValues.Add((latest, after));
            Reload();
        }
        catch (ToolError e) { status.Text = e.Message; }
        catch (IOException e) { status.Text = e.Message; }
    }

    /// Undoes this window's last change only; refuses when another window changed the same code since.
    public void UndoChange()
    {
        if (!Feature.QrCodes.IsOn() || undoValues.Count == 0) return;
        var last = undoValues[^1];
        try
        {
            Store.Revert(last.Before, last.After);
            undoValues.RemoveAt(undoValues.Count - 1);
            Reload();
        }
        catch (ToolError e) { status.Text = e.Message; }
        catch (IOException e) { status.Text = e.Message; }
    }

    void ImportImages()
    {
        var files = ToolUI.ChooseFiles(imagesOnly: true, multiple: true, owner: Window);
        _ = IngestImages(files);
    }

    /// Saves every code found in the images (new content only), as one undo step.
    public Task IngestImages(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return Task.CompletedTask;
        return RunWork(async () =>
        {
            var codes = await CodeRecognition.Recognize(files);
            if (codes.Count == 0)
            {
                workMessage = L("No readable QR codes or barcodes found.");
                return;
            }
            var before = Store.Load();
            var after = before.ToList();
            foreach (var code in codes)
            {
                var entry = SavedQrCode.New(Path.GetFileNameWithoutExtension(code.Source), code.Value);
                if (!entry.IsValid) throw new ToolError(L("Check the code name and content."));
                if (!after.Any(e => e.Value == entry.Value && e.Collection == entry.Collection)) after.Insert(0, entry);
            }
            if (after.SequenceEqual(before))
            {
                workMessage = L("These codes are already in your library.");
                return;
            }
            Store.Save(after);
            undoValues.Add((before, after));
            ClearSearch();
            Reload();
        });
    }

    public void CopyImage()
    {
        if (!Feature.QrCodes.IsOn() || Selected is not { } selected) return;
        try
        {
            SetClipboardImage(QrImage.Bitmap(selected.Value));
            Feedback.Press();
            status.Text = L("Copied");
        }
        catch (ToolError e) { status.Text = e.Message; }
    }

    void ExportImage()
    {
        if (!Feature.QrCodes.IsOn() || Selected is not { } selected) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = QrImage.FileName(selected.Name),
            // An existing file is never replaced, so the dialog doesn't offer to.
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(Window) != true || !Feature.QrCodes.IsOn()) return;
        ExportTo(selected, dialog.FileName);
    }

    public void ExportTo(SavedQrCode entry, string path)
    {
        try
        {
            QrImage.Export(entry.Value, path);
            status.Text = L("Saved %@", Path.GetFileName(path));
            Feedback.Done();
        }
        catch (ToolError e) { status.Text = e.Message; }
    }

    /// Selects a code (for tests and the Codes menu).
    public void Select(Guid id) => Reload(id);
}

/// The New / Edit QR Code dialog (the Mac's sheet).
static class SavedCodeEditor
{
    /// Checks a code before saving; the message to show, or null when it can be saved.
    public static string? Problem(SavedQrCode value)
    {
        if (!value.IsValid) return L("Check the code name and content.");
        try { QrImage.Raw(value.Value); }
        catch (ToolError e) { return e.Message; }
        return null;
    }

    public static void Present(Window owner, SavedQrCode? value, Action<SavedQrCode> saved)
    {
        var entry = value ?? SavedQrCode.New("", "");
        var dialog = new Window
        {
            Owner = owner.IsVisible ? owner : null,
            Title = L(value == null ? "New QR Code" : "Edit QR Code"),
            Width = 570,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        AppTypography.Apply(dialog);
        var name = new TextBox { Text = entry.Name, Padding = new Thickness(10, 6, 10, 6) };
        var collection = new TextBox { Text = entry.Collection, Padding = new Thickness(10, 6, 10, 6) };
        var text = new TextBox
        {
            Text = entry.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10),
        };
        ToolUI.AutomationName(name, L("Code name"));
        ToolUI.AutomationName(collection, L("Collection"));
        ToolUI.AutomationName(text, L("QR content"));
        var issue = ToolUI.Note("");
        issue.Opacity = 1;
        issue.Foreground = ToolUI.ErrorBrush;
        var cancel = ToolUI.Button(L("Cancel"), () => dialog.Close());
        cancel.IsCancel = true;
        var save = ToolUI.Button(L("Save"), () =>
        {
            if (!Feature.QrCodes.IsOn()) return;
            var edited = entry with { Name = name.Text.Trim(), Collection = collection.Text.Trim(), Value = text.Text };
            if (Problem(edited) is { } problem)
            {
                issue.Text = problem;
                return;
            }
            saved(edited);
            dialog.Close();
        });
        save.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        var bar = ToolUI.Row([cancel, save], spacing: 8);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        var root = ToolUI.Stack([
            WithHint(name, L("Code name")),
            WithHint(collection, L("Collection (optional)")),
            ToolUI.Note(L("Enter a URL or text. Saved codes stay on this PC until you remove them.")),
            text, issue, bar,
        ]);
        root.Margin = new Thickness(20);
        dialog.Content = root;
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        dialog.ShowDialog();
    }

    /// A text box with grey hint text while it's empty.
    static FrameworkElement WithHint(TextBox box, string hint)
    {
        var label = new TextBlock { Text = hint, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        void sync() => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => sync();
        sync();
        return new Grid { Children = { box, label } };
    }
}
