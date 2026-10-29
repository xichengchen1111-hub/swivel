using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

internal static class UI
{
    public static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9) };
    public static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, ToolTip = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), MinHeight = 36 };
        button.Click += (_, _) => click();
        return button;
    }
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public static string[] Pick(Window owner)
    {
        var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true };
        return picker.ShowDialog(owner) == true ? picker.FileNames : Array.Empty<string>();
    }
    public static void DropFiles(UIElement element, App app, Action<string[]> receive)
    {
        // WPF panels with null backgrounds do not hit-test their empty area.
        if (element is Panel panel && panel.Background is null) panel.Background = Brushes.Transparent;
        element.AllowDrop = true;
        element.DragOver += (_, e) => {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop, false) && (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        element.Drop += (_, e) => {
            e.Handled = true; e.Effects = DragDropEffects.None;
            if ((e.AllowedEffects & DragDropEffects.Copy) == 0) return;
            app.Try(() => {
                if (e.Data.GetData(DataFormats.FileDrop, false) is string[] files) { receive(app.Validate(files)); e.Effects = DragDropEffects.Copy; }
            });
        };
    }
    public static ListBox FileList(ObservableCollection<string> files) => new() {
        ItemsSource = files, SelectionMode = SelectionMode.Extended, MinHeight = 120, MaxHeight = 240,
        Margin = new Thickness(0, 4, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    public static void CopyDrag(ListBox list, App app)
    {
        Point start = new();
        bool armed = false;
        string? pendingSingle = null;
        list.PreviewMouseLeftButtonDown += (_, e) => {
            start = e.GetPosition(list);
            var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            armed = item is not null;
            pendingSingle = null;
            if (item is { IsSelected: true } && list.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                pendingSingle = item.Content as string;
                e.Handled = true; // Keep the selection until this becomes a click or a drag.
                list.Focus();
            }
        };
        list.MouseMove += (_, e) => {
            if (!armed || e.LeftButton != MouseButtonState.Pressed || list.SelectedItems.Count == 0) return;
            Point point = e.GetPosition(list);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            armed = false;
            pendingSingle = null;
            app.Try(() => {
                var paths = app.Validate(list.SelectedItems.Cast<string>());
                var data = new DataObject(DataFormats.FileDrop, paths);
                DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
            });
        };
        list.PreviewMouseLeftButtonUp += (_, e) => {
            armed = false;
            if (pendingSingle is not null) { list.SelectedItem = pendingSingle; pendingSingle = null; e.Handled = true; }
        };
    }
}

public sealed class MainWindow : Window
{
    private readonly App app;
    private readonly ObservableCollection<string> files = new(), outputs = new();
    private NavigationShell navigation = null!;
    private TextBlock status = null!, awakeStatus = null!;
    private Button cancel = null!;
    private ListBox fileList = null!;
    private ListBox? shelfList;
    public string[] ShelfSelection => shelfList?.SelectedItems.Cast<string>().ToArray() ?? Array.Empty<string>();
    public void RestoreShelfSelection(IEnumerable<string> paths) { if (shelfList is null) return; shelfList.SelectedItems.Clear(); foreach (string path in paths) if (app.Shelf.Contains(path)) shelfList.SelectedItems.Add(path); }
    private string lastStatus = "";
    private string lastBatch = "";
    private Button? batchDetails;
    private string? selectedCustomAction;
    public MainWindow(App app)
    {
        this.app = app;
        Title = "Swivel · Windows Preview " + typeof(App).Assembly.GetName().Version?.ToString(2);
        Width = 940; Height = 740; MinWidth = 660; MinHeight = 480;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Refresh();
    }
    public void Refresh()
    {
        int selected = navigation?.SelectedIndex ?? 0;
        var selectedShelf = ShelfSelection;
        var selectedFiles = fileList?.SelectedItems.Cast<string>().ToArray() ?? Array.Empty<string>();
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(UI.Text("Swivel", 30));
        header.Children.Add(UI.Text(app.L("Windows Preview · Local file tools, built one step at a time.")));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel();
        status = UI.Text(lastStatus);
        footer.Children.Add(status);
        footer.Children.Add(UI.Text(app.L("Closing this window keeps Swivel in the notification area. Choose Quit Swivel there to exit."), 12));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        navigation = new NavigationShell(app.L("Tools"));
        AddPage("Files", FilesPage());
        // Editing a recipe rebuilds the page. Keep the explicit file selection so the
        // next action cannot silently expand from selected files to the whole list.
        if (fileList is not null)
            foreach (string path in selectedFiles) fileList.SelectedItems.Add(path);
        AddPage("File Shelf", ShelfPage());
        RestoreShelfSelection(selectedShelf);
        AddPage("Image Presets", PresetsPage());
        AddPage("Batch Rename", RenamePage());
        AddPage("Move Files", MovePage());
        AddPage("My Wheel", CustomizePage());
        AddPage("Windows Doctor", DoctorPage());
        AddPage("Applications", ApplicationsPage());
        AddPage("QR & Barcodes", QrPage());
        AddPage("Settings", SettingsPage());
        AddPage("Compatibility", CompatibilityPage());
        AddPage("Text Capture", TextCapturePage());
        AddPage("Assistant", AssistantPage());
        navigation.SelectedIndex = selected;
        root.Children.Add(navigation); Content = root;
        UpdateBusy(); UpdateAwake();
    }
    private void AddPage(string title, UIElement body) => navigation.Add(app.L(title), UI.Scroll(body));
    private UIElement AssistantPage()
    {
        var page=Page();page.Children.Add(UI.Text(app.L("Assistant"),22));page.Children.Add(UI.Text(app.L(AssistantSettingsDialog.Scope),12));page.Children.Add(UI.Text(app.L("Enable Assistant for reviewed requests. Quick Actions suggests actions; custom labels require per-request permission. Windows Doctor explains redacted basic/static snapshots or one frozen monitor sample. Doctor labels are off by default and require individual selection and a new review each request. Nothing executes automatically; monitoring stays paused until explicit Resume.")));
        page.Children.Add(UI.Text(app.Prefs.IsOn(Feature.Assistant)?app.L("Enabled"):app.L("Disabled")));
        page.Children.Add(UI.Button(app.L("Assistant Settings"),()=>app.Try(()=>new AssistantSettingsDialog(app){Owner=this}.ShowDialog())));return page;
    }
    private StackPanel Page() => new() { Margin = new Thickness(18) };
    private string[] Chosen() => fileList.SelectedItems.Count == 0 ? files.ToArray() : fileList.SelectedItems.Cast<string>().ToArray();
    private void AddFiles(IEnumerable<string> paths)
    {
        var accepted = app.Validate(paths);
        if (files.Count + accepted.Count(x => !files.Contains(x, StringComparer.OrdinalIgnoreCase)) > 100)
            throw new UserError("Choose up to 100 files at a time.");
        foreach (var path in accepted) if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
    }
    private UIElement FilesPage()
    {
        var page = Page();
        page.Children.Add(UI.Text(app.L("Drop local files here or choose them below."), 19));
        page.Children.Add(UI.Text(app.L("Actions use selected files, or all listed files when none are selected.")));
        var buttons = new WrapPanel();
        buttons.Children.Add(UI.Button(app.L("Choose Files…"), () => app.Try(() => AddFiles(UI.Pick(this)))));
        buttons.Children.Add(UI.Button(app.L("Remove Selected"), () => { foreach (string path in fileList.SelectedItems.Cast<string>().ToArray()) files.Remove(path); }));
        buttons.Children.Add(UI.Button(app.L("Clear"), () => files.Clear()));
        page.Children.Add(buttons);
        fileList = UI.FileList(files); page.Children.Add(fileList);
        UI.DropFiles(page, app, AddFiles);
        var tools = new WrapPanel();
        foreach (var action in Catalog.Offered(app.Prefs))
            tools.Children.Add(UI.Button(Catalog.DisplayTitle(action, app.Prefs.Language), async () => await app.RunActionAsync(action, Chosen(), this, SetStatus)));
        page.Children.Add(tools);
        page.Children.Add(UI.Text(app.L("Images: single-frame PNG, JPEG, BMP, TIFF, TGA, PSD (flattened picture), still WebP or supported full-canvas static GIF, up to 16 MP / 128 MB. Outputs are SDR and metadata-free. Animated GIF and WebP and partial-canvas GIFs are refused. Enlargement requires an explicit choice."), 12));
        var launch = new WrapPanel();
        if (app.Prefs.IsOn(Feature.Wheel)) launch.Children.Add(UI.Button(app.L("Open Wheel") + " · " + app.Prefs.Shortcuts.Wheel.Label, () => app.OpenPanel(true, Chosen())));
        if (app.Prefs.IsOn(Feature.QuickActions)) launch.Children.Add(UI.Button(app.L("Quick Actions") + " · " + app.Prefs.Shortcuts.Quick.Label, () => app.OpenPanel(false, Chosen())));
        if (app.Prefs.IsOn(Feature.MyWheel)) launch.Children.Add(UI.Button(app.L("My Wheel") + " · " + app.Prefs.Shortcuts.Custom.Label, () => app.OpenPanel(true, Chosen(), true)));
        cancel = UI.Button(app.L("Cancel Operation"), app.CancelWork); launch.Children.Add(cancel);
        page.Children.Add(launch);
        batchDetails = UI.Button(app.L("Last Batch Details…"), () => new BatchDetailsDialog(app, lastBatch) { Owner = this }.ShowDialog());
        batchDetails.IsEnabled = lastBatch.Length > 0;
        page.Children.Add(batchDetails);
        page.Children.Add(UI.Text(app.L("Recent Outputs"), 18));
        var resultList = UI.FileList(outputs); page.Children.Add(resultList);
        UI.CopyDrag(resultList, app);
        page.Children.Add(UI.Button(app.L("Show in Explorer"), () => { if (resultList.SelectedItem is string path) app.Try(() => Explorer.Reveal(path)); }));
        return page;
    }
    private UIElement ShelfPage()
    {
        var page = Page();
        page.Children.Add(UI.Text(app.L("File Shelf"), 22));
        page.Children.Add(UI.Text(app.L("References only. Select several files and drag to copy them out. Removing a reference never deletes a file.")));
        if (!app.Prefs.IsOn(Feature.Shelf)) { shelfList = null; page.Children.Add(UI.Text(app.L("Enable File Shelf in Settings first."))); return page; }
        var list = shelfList = UI.FileList(app.Shelf); page.Children.Add(list);
        UI.DropFiles(page, app, app.AddShelf); UI.CopyDrag(list, app);
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Open Floating Shelf"),app.OpenShelf));
        row.Children.Add(UI.Button(app.L("Preview File"), async () => await app.PreviewShelfFileAsync(list.SelectedItems.Count == 0 ? app.Shelf.ToArray() : list.SelectedItems.Cast<string>().ToArray(), this, SetStatus)));
        row.Children.Add(UI.Button(app.L("Choose Files…"), () => app.Try(() => app.AddShelf(UI.Pick(this)))));
        row.Children.Add(UI.Button(app.L("Remove Selected"), () => { foreach (string path in list.SelectedItems.Cast<string>().ToArray()) app.Shelf.Remove(path); }));
        row.Children.Add(UI.Button(app.L("Clear"), () => app.Shelf.Clear()));
        row.Children.Add(UI.Button(app.L("Show in Explorer"), () => { if (list.SelectedItem is string path) app.Try(() => Explorer.Reveal(path)); }));
        page.Children.Add(row);
        return page;
    }
    private UIElement PresetsPage()
    {
        var page = Page();
        page.Children.Add(UI.Text(app.L("Image Presets"), 22));
        page.Children.Add(UI.Text(app.L("Save a recipe, then use it from Files, Wheel or Quick Actions. Renaming a preset keeps its action identity.")));
        if (!app.Prefs.IsOn(Feature.ImagePresets)) { page.Children.Add(UI.Text(app.L("Enable Image Presets in Settings first."))); return page; }
        var backups = new WrapPanel();
        backups.Children.Add(UI.Button(app.L("Import Image Presets…"), async () => await app.RunActionAsync(new("presets.import", "Import Image Presets…"), Array.Empty<string>(), this, SetStatus)));
        backups.Children.Add(UI.Button(app.L("Export Image Presets…"), async () => await app.RunActionAsync(new("presets.export", "Export Image Presets…"), Array.Empty<string>(), this, SetStatus)));
        page.Children.Add(backups);
        page.Children.Add(UI.Button(app.L("New Preset…"), () => new PresetDialog(app) { Owner = this }.ShowDialog()));
        foreach (var preset in app.Prefs.Presets)
        {
            var row = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            row.Children.Add(UI.Text(preset.Name, 18));
            string size = preset.Recipe.LongEdge == 0 ? app.L("Keep original dimensions") : string.Format(app.L("Long edge: {0} px"), preset.Recipe.LongEdge);
            string summary = preset.Recipe.Format.ToUpperInvariant() + " · " + size;
            if (preset.Recipe.Format == "jpg") summary += " · " + string.Format(app.L("JPEG quality: {0}"), preset.Recipe.Quality);
            if (preset.Recipe.Adjustments is not null) summary += " · " + app.L("Photo adjustments");
            if (preset.Recipe.AutoPerImage) summary += " · " + app.L("Per-image Auto");
            row.Children.Add(UI.Text(summary));
            var buttons = new WrapPanel();
            buttons.Children.Add(UI.Button(app.L("Edit Preset…"), () => new PresetDialog(app, preset) { Owner = this }.ShowDialog()));
            buttons.Children.Add(UI.Button(app.L("Delete Preset"), () => app.Prefs.RemovePreset(preset.Id)));
            row.Children.Add(buttons); page.Children.Add(row);
        }
        return page;
    }
    private UIElement MovePage()
    {
        var page = Page(); page.Children.Add(UI.Text(app.L("Move Files"), 22));
        page.Children.Add(UI.Text(app.L("Move original files to another folder on the same NTFS volume, or copy files to any local NTFS folder. Review every source and destination before confirming. Moves across volumes are not available yet.")));
        page.Children.Add(UI.Text(app.L("Move history lasts only for this session, with up to 20 batches. If recovery was incomplete, use the recorded current paths. Closing Swivel or forgetting history removes its Undo records.")));
        bool enabled = app.Prefs.IsOn(Feature.FileMoves);
        if (!enabled) page.Children.Add(UI.Text(app.L("Enable Move Files in Settings first.")));
        var row = new WrapPanel();
        var choose = UI.Button(app.L("Choose Files to Move…"), async () =>
        {
            try { var paths = UI.Pick(this); if (paths.Length > 0) await app.RunActionAsync(new("file.move", "Move to Folder…", Feature.FileMoves), paths, this, SetStatus); }
            catch (Exception error) { app.ShowError(error); }
        });
        choose.IsEnabled = enabled; row.Children.Add(choose);
        var copy = UI.Button(app.L("Choose Files to Copy…"), async () =>
        {
            try { var paths = UI.Pick(this); if (paths.Length > 0) await app.RunActionAsync(new("file.copyto", "Copy to Folder…", Feature.FileMoves), paths, this, SetStatus); }
            catch (Exception error) { app.ShowError(error); }
        });
        copy.IsEnabled = enabled; row.Children.Add(copy);
        var undo = UI.Button(app.L("Undo File Move"), async () => await app.RunActionAsync(new("file.undoMove", "Undo File Move", Feature.FileMoves), Array.Empty<string>(), this, SetStatus));
        undo.IsEnabled = enabled && app.MoveHistory.Count > 0; row.Children.Add(undo);
        row.Children.Add(UI.Button(app.L("Forget Move History"), () => { if (app.Busy) return; app.MoveHistory.Clear(); Refresh(); })); page.Children.Add(row);
        var results = UI.Button(app.L("Review Last Move Result…"), () =>
        {
            if (app.Busy || app.LastMoveOutcome is not { } outcome) return;
            new MoveFilesResultDialog(app, outcome, app.LastMoveWasUndo) { Owner = this }.ShowDialog();
        });
        results.IsEnabled = app.LastMoveOutcome is not null; page.Children.Add(results);
        // Keep recovery paths visible even if the feature was disabled while a move was running.
        foreach (var batch in app.MoveHistory.AsEnumerable().Reverse())
        {
            page.Children.Add(UI.Text(app.L("Original → Current"), 18));
            foreach (var entry in batch) page.Children.Add(UI.Text(LibraryJson.Visible(entry.Original) + "\n→ " + LibraryJson.Visible(entry.Current)));
        }
        return page;
    }
    public void ApplyMoves(IReadOnlyDictionary<string, string> changes)
    {
        var selected = fileList.SelectedItems.Cast<string>().ToArray();
        string Updated(string path) => changes.TryGetValue(path, out string? current) ? current : path;
        foreach (var collection in new[] { files, outputs })
        {
            var before = collection.ToArray();
            for (int i = 0; i < before.Length; i++) collection[i] = Updated(before[i]);
        }
        fileList.SelectedItems.Clear(); foreach (var path in selected) fileList.SelectedItems.Add(Updated(path));
    }
    private UIElement RenamePage()
    {
        var page = Page(); page.Children.Add(UI.Text(app.L("Batch Rename"), 22));
        page.Children.Add(UI.Text(app.L("This changes original file names. Review every new name before renaming. Extensions are kept.")));
        page.Children.Add(UI.Text(app.L("NTFS regular files only. Case-only changes, swaps and chains use temporary names in the same folder. Conflicts never overwrite files. If rollback fails, review the recovery paths. A crash may leave temporary names; undo history lasts only for this session.")));
        if (!app.Prefs.IsOn(Feature.BatchRename)) { page.Children.Add(UI.Text(app.L("Enable Batch Rename in Settings first."))); return page; }
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Choose Files…"), async () => { try { var paths = UI.Pick(this); if (paths.Length > 0) await app.RunActionAsync(new("rename.files", "Batch Rename…"), paths, this, SetStatus); } catch (Exception ex) { app.ShowError(ex); } }));
        var undo = UI.Button(app.L("Undo Rename"), async () => await app.RunActionAsync(new("rename.undo", "Undo Rename"), Array.Empty<string>(), this, SetStatus));
        undo.IsEnabled = app.RenameHistory.Count > 0; row.Children.Add(undo);
        row.Children.Add(UI.Button(app.L("Forget Undo History"), () => { if (app.Busy) return; app.RenameHistory.Clear(); Refresh(); }));
        page.Children.Add(row);
        foreach (var batch in app.RenameHistory.AsEnumerable().Reverse())
        {
            page.Children.Add(UI.Text(app.L("Original → Current"), 18));
            foreach (var entry in batch) page.Children.Add(UI.Text(entry.Original + "\n→ " + entry.Current));
        }
        return page;
    }
    public void ApplyRenames(IReadOnlyList<RenameLocation> changes)
    {
        var selectedPaths = fileList.SelectedItems.Cast<string>().ToArray();
        foreach (var collection in new[] { files, outputs })
        {
            var before = collection.ToArray();
            for (int i = 0; i < before.Length; i++)
                collection[i] = changes.FirstOrDefault(x => x.Original.Equals(before[i], StringComparison.OrdinalIgnoreCase))?.Current ?? before[i];
        }
        // WPF removes replaced items from the selection. Restore it from the pre-change
        // snapshot before Refresh can interpret an empty selection as "all files".
        fileList.SelectedItems.Clear();
        foreach (var path in selectedPaths)
            fileList.SelectedItems.Add(changes.FirstOrDefault(x => x.Original.Equals(path, StringComparison.OrdinalIgnoreCase))?.Current ?? path);
    }
    private UIElement CustomizePage()
    {
        var page = Page(); page.Children.Add(UI.Text(app.L("My Wheel"), 22));
        page.Children.Add(UI.Text(app.L("Choose and order your actions. My Wheel works independently; disabled tools are omitted until enabled. Preset renaming keeps the action in place.")));
        var all = Catalog.All(app.Prefs).ToDictionary(x => x.Id);
        var list = new ListBox { MinHeight = 150, MaxHeight = 300, DisplayMemberPath = "Title" };
        var rows = app.Prefs.CustomActionIds.Where(all.ContainsKey).Select(id => new CustomRow(id, Catalog.DisplayTitle(all[id], app.Prefs.Language))).ToArray();
        list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(x => x.Id == selectedCustomAction);
        list.SelectionChanged += (_, _) => selectedCustomAction = (list.SelectedItem as CustomRow)?.Id; page.Children.Add(list);
        var choices = Catalog.All(app.Prefs).Where(x => !app.Prefs.CustomActionIds.Contains(x.Id)).Select(x => new CustomRow(x.Id, Catalog.DisplayTitle(x, app.Prefs.Language))).ToArray();
        var select = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Title", SelectedIndex = choices.Length > 0 ? 0 : -1, MinHeight = 30 }; page.Children.Add(select);
        System.Windows.Automation.AutomationProperties.SetName(select, app.L("Choose an action"));
        var buttons = new WrapPanel();
        buttons.Children.Add(UI.Button(app.L("Add Action"), () => app.Try(() => { if (select.SelectedItem is CustomRow row) app.Prefs.SetCustomActions(app.Prefs.CustomActionIds.Append(row.Id)); })));
        void Move(int offset) { if (list.SelectedIndex < 0) return; var ids = app.Prefs.CustomActionIds.ToArray(); int i = list.SelectedIndex, j = i + offset; if (j < 0 || j >= ids.Length) return; (ids[i],ids[j]) = (ids[j],ids[i]); app.Prefs.SetCustomActions(ids); }
        buttons.Children.Add(UI.Button(app.L("Move Up"), () => Move(-1))); buttons.Children.Add(UI.Button(app.L("Move Down"), () => Move(1)));
        buttons.Children.Add(UI.Button(app.L("Remove Action"), () => { if (list.SelectedItem is CustomRow row) app.Prefs.SetCustomActions(app.Prefs.CustomActionIds.Where(x => x != row.Id)); }));
        buttons.Children.Add(UI.Button(app.L("Open My Wheel"), () => app.OpenPanel(true, custom: true)));
        page.Children.Add(buttons);
        if (!app.Prefs.IsOn(Feature.MyWheel)) page.Children.Add(UI.Text(app.L("Enable My Wheel in Settings to open it. You can configure it while it is off.")));
        return page;
    }
    private sealed record CustomRow(string Id, string Title);
    private UIElement DoctorPage() => WindowsDoctorPage.Build(app,this,SetStatus);
    private UIElement ApplicationsPage()
    {
        var page = Page(); page.Children.Add(UI.Text(app.L("Applications"),22));
        page.Children.Add(UI.Text(app.L("Choose trusted applications or saved web pages. Actions are also available in My Wheel and Quick Actions. Nothing opens when you save an entry.")));
        var draft = UI.Button(app.L("Prepare Assistant Draft…"),async () => {
            var files = UI.Pick(this); if(files.Length>0) await app.RunActionAsync(new("file.assistantdraft","Prepare Assistant Draft…"),files,this,SetStatus);
        }); draft.IsEnabled=app.Prefs.IsOn(Feature.AppRouting);page.Children.Add(draft);
        page.Children.Add(UI.Text(app.L("Prepare a complete file-reference draft for Codex or Claude Code, review it, then copy it or open a registered desktop app. No file contents or workspace permission are supplied.")));
        page.Children.Add(UI.Button(app.L("Add Application…"),() => new AppRouteDialog(app) { Owner = this }.ShowDialog()));
        page.Children.Add(UI.Button(app.L("Add Web Page…"),() => new WebRouteDialog(app) { Owner = this }.ShowDialog()));
        if (!app.Prefs.IsOn(Feature.AppRouting)) page.Children.Add(UI.Text(app.L("Enable App Routing in Settings to use these actions.")));
        foreach (var route in app.Prefs.Apps)
        {
            page.Children.Add(UI.Text(route.Name,18)); page.Children.Add(UI.Text(route.IsWebPage ? route.WebAddress! : route.Executable));
            page.Children.Add(UI.Text(route.IsWebPage && !route.CopyToClipboard ? app.L("Open page only; no files or clipboard") : route.Types.Length == 0 ? app.L("Any file type") : route.Extensions));
            var row = new WrapPanel(); row.Children.Add(UI.Button(app.L(route.IsWebPage ? "Edit Web Page…" : "Edit Application…"),() => {
                Window dialog = route.IsWebPage ? new WebRouteDialog(app,route) : new AppRouteDialog(app,route); dialog.Owner = this; dialog.ShowDialog();
            }));
            row.Children.Add(UI.Button(app.L(route.IsWebPage ? "Remove Web Page" : "Remove Application"),() => app.Prefs.RemoveApp(route.Id))); page.Children.Add(row);
        }
        return page;
    }
    private UIElement QrPage()
    {
        var page = Page(); page.Children.Add(UI.Text(app.L("QR & Barcodes"),22));
        page.Children.Add(UI.Text(app.L("Create QR codes from text and keep up to 32 in a local library. Read QR codes from images, screenshots and photos with the separate reader.")));
        page.Children.Add(UI.Text(app.L("Open a QR code to choose a garden template and season, then explore the original Windows 3D relief. Flat QR copying and PNG export stay available.")));
        if (!app.Prefs.IsOn(Feature.QrCodes)) { page.Children.Add(UI.Text(app.L("Enable QR & Barcodes in Settings first."))); return page; }
        page.Children.Add(UI.Button(app.L("Create QR Code"),async () => await app.RunActionAsync(new("qr.create","Create QR Code"),Array.Empty<string>(),this,SetStatus)));
        page.Children.Add(UI.Button(app.L("Read QR Image…"),async()=>await app.RunActionAsync(new("qr.readflat","Read QR Image…"),Array.Empty<string>(),this,SetStatus)));
        var backups=new WrapPanel();backups.Children.Add(UI.Button(app.L("Import QR Library…"),async()=>await app.RunActionAsync(new("qr.import","Import QR Library…"),Array.Empty<string>(),this,SetStatus)));backups.Children.Add(UI.Button(app.L("Export QR Library…"),async()=>await app.RunActionAsync(new("qr.export","Export QR Library…"),Array.Empty<string>(),this,SetStatus)));page.Children.Add(backups);
        page.Children.Add(UI.Button(app.L("Import Mac QR Library…"), async () => await app.RunActionAsync(new("qr.importMac", "Import Mac QR Library…"), Array.Empty<string>(), this, SetStatus)));
        page.Children.Add(UI.Button(app.L("Export QR Images for Mac…"), async () => await app.RunActionAsync(new("qr.exportMacImages", "Export QR Images for Mac…"), Array.Empty<string>(), this, SetStatus)));
        foreach(var group in app.Prefs.QrCodes.GroupBy(x=>x.Group).OrderBy(x=>x.Key,StringComparer.CurrentCulture))
        {
            page.Children.Add(UI.Text(group.Key.Length==0?app.L("Ungrouped QR Codes"):group.Key,20));
            foreach (var entry in group)
            {
            page.Children.Add(UI.Text(entry.Name,18));
            page.Children.Add(UI.Text(string.Format(app.L("Error correction: {0}"),QrCode.Level(entry.Correction)),12));
            page.Children.Add(UI.Text(app.L(QrGarden.TemplateName(entry.Template))+" · "+app.L(QrGarden.SeasonName(entry.Season)),12));
            var row = new WrapPanel();
            row.Children.Add(UI.Button(app.L("Edit QR Code…"),async () => await app.RunActionAsync(new("qr.create","Create QR Code"),Array.Empty<string>(),this,SetStatus,entry)));
            row.Children.Add(UI.Button(app.L("Remove from Library"),() => app.Prefs.RemoveQr(entry.Id))); page.Children.Add(row);
            }
        }
        return page;
    }
    private UIElement TextCapturePage()
    {
        var page=Page();page.Children.Add(UI.Text(app.L(TextCaptureDocument.Scope)));page.Children.Add(UI.Text(app.L(NativeIdentity.Required),12));
        if(!app.Prefs.IsOn(Feature.TextCapture)){page.Children.Add(UI.Text(app.L("Enable Text Capture in Settings first.")));return page;}
        page.Children.Add(UI.Button(app.L("Choose Image for Text Capture…"),async()=>{var paths=UI.Pick(this);if(paths.Length==1)await app.RunActionAsync(new("text.ocr","Recognize Text…",Feature.TextCapture,ImageOnly:true,SingleFile:true),app.Validate(paths),this,SetStatus);}));return page;
    }
    private UIElement SettingsPage()
    {
        var page = Page();
        page.Children.Add(UI.Text(app.L("Language"), 18));
        var language = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), ItemsSource = new[] { app.L("System Default"), "English", "简体中文", "繁體中文", "日本語" }, SelectedIndex = Array.IndexOf(Prefs.Languages, app.Prefs.Language) };
        language.SelectionChanged += (_, _) => { if (language.SelectedIndex >= 0) app.Prefs.SetLanguage(Prefs.Languages[language.SelectedIndex]); };
        page.Children.Add(language);
        page.Children.Add(UI.Text(app.L("Optional tools start off. Enable each tool independently.")));
        foreach (var feature in Enum.GetValues<Feature>())
        {
            string label = feature switch { Feature.Shelf => "File Shelf", Feature.QuickActions => "Quick Actions", Feature.KeepAwake => "Keep Awake", Feature.ImagePresets => "Image Presets", Feature.BatchRename => "Batch Rename", Feature.Archives => "Archives", Feature.SubtitleFiles => "Subtitles", Feature.MyWheel => "My Wheel", Feature.Doctor => "Windows Doctor", Feature.AppRouting => "App Routing", Feature.Clothesline => "Clothesline", Feature.PdfPages => "PDF Pages", Feature.QrCodes => "QR & Barcodes", Feature.TextCapture => "Text Capture", Feature.Assistant=>"Assistant", Feature.FileMoves=>"Move Files", Feature.FolderStyle=>"Folder Style", Feature.AgentTools=>"Agent Tools (MCP)", _ => "Wheel" };
            var check = new CheckBox { Content = app.L(label), IsChecked = app.Prefs.IsOn(feature), Margin = new Thickness(0, 7, 0, 7) };
            check.Click += (_, _) => app.Prefs.Set(feature, check.IsChecked == true);
            page.Children.Add(check);
        }
        page.Children.Add(UI.Text(app.L("Wheel") + ": " + app.Prefs.Shortcuts.Wheel.Label + " · " + app.L("Quick Actions") + ": " + app.Prefs.Shortcuts.Quick.Label + " · " + app.L("My Wheel") + ": " + app.Prefs.Shortcuts.Custom.Label));
        page.Children.Add(UI.Button(app.L("Change Shortcuts…"), () => new ShortcutsDialog(app) { Owner = this }.ShowDialog()));
        page.Children.Add(UI.Text(app.L("Agent Tools (MCP)"), 18));
        page.Children.Add(UI.Text(app.L("With Agent Tools on, an agent host such as Claude Code, Claude Desktop or Codex can start Swivel with --mcp and talk to it over stdin and stdout only. Agents can inspect files, read the text of documents and (in the MSIX package) images, read QR codes in images and photos, convert, rotate, flip or grayscale images or remove or blur their background, save a document's text as TXT, RTF, DOCX, HTML or ODT, compress images to JPEG or videos to MP4 under a size, rename files in their folders, and move files into another folder on the same drive. Reading never changes a file. Every write is planned first, applied once and never overwrites anything; new files can be undone to the Recycle Bin, renames by renaming the files back, and moves by moving the files back."), 12));
        page.Children.Add(UI.Button(app.L("Copy Agent Tools Setup"), () =>
        {
            string command = NativeIdentity.HasPackage ? McpSetup.Alias : Environment.ProcessPath ?? "Swivel.exe";
            try { Clipboard.SetText(McpSetup.Text(command)); SetStatus(app.L("Setup copied. Paste it into your agent host's configuration.")); }
            catch (System.Runtime.InteropServices.ExternalException) { SetStatus(app.L("The clipboard is in use. Try again.")); }
        }));
        page.Children.Add(UI.Button(app.L("Install in Claude Desktop…"), () =>
        {
            try { ClaudeExtension.Open(); SetStatus(NativeIdentity.HasPackage ? app.L("The extension was handed to the app that opens .mcpb files (normally Claude Desktop): choose Install there.") : app.L("The extension was handed to the app that opens .mcpb files (normally Claude Desktop): choose Install there, then choose this Swivel.exe in the extension's settings.")); }
            catch (UserError error) { SetStatus(app.L(error.Message)); }
        }));
        page.Children.Add(UI.Button(app.L("Wheel Drag Gesture…"), app.OpenWheelGesture));
        page.Children.Add(UI.Button(app.L("Open Clothesline"),app.OpenClothesline));
        page.Children.Add(UI.Button(app.L("Image Arrival Cards…"),app.OpenImageArrivalCards));
        page.Children.Add(UI.Text(app.L("Menu Style"), 18));
        var style = new ComboBox { ItemsSource = new[] { app.L("Wheel"), app.L("List"), app.L("Bookshelf"), app.L("Sticky Notes"), app.L("Clothesline") }, SelectedIndex = (int)app.Prefs.MenuTemplate, MinHeight = 30 };
        System.Windows.Automation.AutomationProperties.SetName(style, app.L("Menu Style"));
        style.SelectionChanged += (_,_) => { if (style.SelectedIndex >= 0) app.Prefs.SetMenuPresentation((MenuTemplate)style.SelectedIndex, app.Prefs.AnimateMenus); }; page.Children.Add(style);
        var animate = new CheckBox { Content = app.L("Gentle menu animation"), IsChecked = app.Prefs.AnimateMenus, Margin = new Thickness(0,10,0,10) };
        animate.Click += (_,_) => app.Prefs.SetMenuPresentation(app.Prefs.MenuTemplate, animate.IsChecked == true); page.Children.Add(animate);
        page.Children.Add(UI.Text(app.L("The style applies to Wheel and My Wheel. Animation also follows Windows accessibility settings.")));
        page.Children.Add(UI.Button(app.L("Menu Colors…"),() => new MenuColorsDialog(app) { Owner = this }.ShowDialog()));
        page.Children.Add(UI.Text(app.L("Keep Awake"), 18));
        awakeStatus = UI.Text(""); page.Children.Add(awakeStatus);
        var durations = new ComboBox { ItemsSource = new[] { app.L("30 minutes"), app.L("1 hour"), app.L("2 hours"), app.L("5 hours"), app.L("Until turned off") }, SelectedIndex = 0, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(durations);
        var display = new CheckBox { Content = app.L("Keep the display on too"), Margin = new Thickness(0, 10, 0, 10) }; page.Children.Add(display);
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Start"), () => app.Try(() => {
            if (!app.Prefs.IsOn(Feature.KeepAwake)) throw new UserError("Enable Keep Awake first.");
            TimeSpan? duration = durations.SelectedIndex switch { 0 => TimeSpan.FromMinutes(30), 1 => TimeSpan.FromHours(1), 2 => TimeSpan.FromHours(2), 3 => TimeSpan.FromHours(5), _ => null };
            app.Awake.Start(duration, display.IsChecked == true);
        })));
        row.Children.Add(UI.Button(app.L("Stop"), () => app.Try(() => app.Awake.Stop()))); page.Children.Add(row);
        page.Children.Add(UI.Text(app.L("No automatic startup, screenshot takeover or clipboard reading. Folder watching and assistant requests start only after your explicit choices."), 12));
        return page;
    }
    private UIElement CompatibilityPage()
    {
        var page = Page();
        page.Children.Add(UI.Text(app.L("Compatibility"), 22));
        page.Children.Add(UI.Text(app.L("Preview features are implemented but still need Windows hardware validation. Planned features cannot be enabled.")));
        foreach (var item in Compatibility.Items)
        {
            page.Children.Add(UI.Text(app.L(item.Title) + " · " + app.L(item.Status), 17));
            page.Children.Add(UI.Text(app.L(item.Detail)));
        }
        return page;
    }
    public void SetStatus(string text) { lastStatus = text; status.Text = text; }
    public void ShowResults(IEnumerable<string> paths, string text) { foreach (var path in paths) { outputs.Add(path); if (outputs.Count > 100) outputs.RemoveAt(0); } SetStatus(text); }
    public void UpdateBusy() { if (cancel is not null) cancel.IsEnabled = app.Busy; }
    public void UpdateAwake() { if (awakeStatus is not null) awakeStatus.Text = app.L(app.Awake.Active ? "Keep Awake is active." : "Keep Awake is off."); }
    public void ShowBatchResults(IReadOnlyList<ImageBatchEntry> entries)
    {
        lastBatch = string.Join(Environment.NewLine + Environment.NewLine, entries.Select(entry => {
            string state = app.L(entry.State switch { BatchState.Saved => "Saved", BatchState.Failed => "Failed", BatchState.Cancelled => "Cancelled.", _ => "Not started" });
            string detail = entry.Output ?? (entry.Error is UserError error ? app.L(error.Message) : entry.Error is not null ? app.L("The operation could not finish. Check the file format, permissions, free space and whether another app is using the file.") : "");
            return state + Environment.NewLine + entry.Input + (detail.Length == 0 ? "" : Environment.NewLine + detail);
        }));
        if (batchDetails is not null) batchDetails.IsEnabled = true;
    }
    public void ShowShelf() => navigation.SelectedIndex = 1;
}

public sealed class ActionWindow : Window
{
    private readonly App app;
    private readonly MenuMotion motion;
    public bool IsWheel { get; }
    public bool IsCustom { get; }
    public Feature PanelFeature => IsCustom ? Feature.MyWheel : IsWheel ? Feature.Wheel : Feature.QuickActions;
    private readonly ObservableCollection<string> files = new();
    private readonly TextBlock status;
    private readonly ContentControl actionArea = new();
    private readonly TextBox search = new();
    private KeyEventHandler? searchHandler;
    private int page;
    private string? suggestedId;
    public ActionWindow(App app, bool wheel, bool custom = false)
    {
        this.app = app; IsWheel = wheel; IsCustom = custom;
        motion = new MenuMotion(() => app.Prefs.AnimateMenus); Closed += (_,_) => motion.Dispose();
        Title = app.L(custom ? "My Wheel" : wheel ? "Wheel" : "Quick Actions");
        Width = wheel ? 680 : 620; Height = wheel ? 730 : 620; MinWidth = 480; MinHeight = 430;
        FontSize = 14; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel();
        header.Children.Add(UI.Text(app.L("Drop files here, then choose an action. Escape closes this panel.")));
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Choose Files…"), () => app.Try(() => SetFiles(UI.Pick(this)))));
        row.Children.Add(UI.Button(app.L("Clear"), () => SetFiles(Array.Empty<string>())));
        row.Children.Add(UI.Button(app.L("Cancel Operation"), app.CancelWork));
        header.Children.Add(row);
        var list = UI.FileList(files); list.MinHeight = 55; list.MaxHeight = 100; header.Children.Add(list);
        if (!wheel)
        {
            header.Children.Add(UI.Text(app.L("Search Actions")));
            search.Margin = new Thickness(0, 0, 0, 12); search.MinHeight = 32;
            search.TextChanged += (_, _) => {suggestedId=null;DrawActions();}; header.Children.Add(search);
        }
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        status = UI.Text(""); DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        if(!wheel)row.Children.Add(UI.Button(app.L("Ask Assistant…"),async()=>await app.RunActionAsync(new("assistant.choose","Ask Assistant…"),files.ToArray(),this,text=>status.Text=text,assistantRequest:search.Text)));
        root.Children.Add(actionArea); Content = root;
        UI.DropFiles(root, app, SetFiles);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += (_, _) => { if (!wheel) search.Focus(); };
        DrawActions();
    }
    public void SetFiles(IEnumerable<string> paths)
    {
        SetValidatedFiles(app.Validate(paths));
    }
    // Only the explicit gesture receiver calls this after complete metadata
    // preflight; normal pick/drop paths retain their existing validation.
    internal void SetValidatedFiles(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        suggestedId=null;
        files.Clear(); foreach (var path in paths) files.Add(path);
        page = 0; DrawActions();
    }
    private ToolAction[] Actions() => (IsCustom ? Catalog.ForCustomWheel(app.Prefs, files) : files.Count == 0 ? Catalog.Offered(app.Prefs) : Catalog.Available(app.Prefs, files)).ToArray();
    private async void Run(ToolAction action)
    {
        if (action.RequiresFiles && files.Count == 0) { try { SetFiles(UI.Pick(this)); } catch (Exception error) { app.ShowError(error); return; } }
        if (action.RequiresFiles && files.Count == 0) return;
        await app.RunActionAsync(action, files.ToArray(), this, text => status.Text = text);
    }
    public void ApplyRenames(IReadOnlyList<RenameLocation> changes)
    {
        var before = files.ToArray();
        for (int i = 0; i < before.Length; i++)
            files[i] = changes.FirstOrDefault(x => x.Original.Equals(before[i], StringComparison.OrdinalIgnoreCase))?.Current ?? before[i];
        DrawActions();
    }
    public void ApplyMoves(IReadOnlyDictionary<string, string> changes)
    {
        var before = files.ToArray();
        for (int i = 0; i < before.Length; i++)
            if (changes.TryGetValue(before[i], out string? current)) files[i] = current;
        DrawActions();
    }
    public void RefreshActions() { suggestedId=null;page = 0; Title = app.L(IsCustom ? "My Wheel" : IsWheel ? "Wheel" : "Quick Actions"); DrawActions(); }
    internal void ApplyAssistantSuggestion(string id,string[] snapshot,bool includeCustom=false)
    {
        if(IsWheel||!app.Prefs.IsOn(Feature.Assistant)||!snapshot.SequenceEqual(files,StringComparer.OrdinalIgnoreCase)||!AssistantPlan.Offered(app.Prefs,files,includeCustom).Any(x=>x.Id==id))return;
        search.Text="";suggestedId=id;DrawActions();status.Text=app.L("Suggested action selected. Review it and press Return or Run Selected to execute.");search.Focus();
    }
    private void DrawActions()
    {
        motion.Reset();
        var actions = Actions();
        if (IsCustom && actions.Length == 0) { actionArea.Content = UI.Text(app.L("Choose actions in My Wheel settings. Disabled tools and actions that do not match these files are hidden.")); return; }
        if (!IsWheel)
        {
            var list = new ListBox { DisplayMemberPath = "Title" };
            var matches = actions.Where(x => Catalog.DisplayTitle(x, app.Prefs.Language).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || x.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.Id==suggestedId).Select(x => new ActionRow(x, Catalog.DisplayTitle(x, app.Prefs.Language))).ToArray();
            list.ItemsSource = matches; if (matches.Length > 0) list.SelectedIndex = 0;
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ActionRow row) Run(row.Action); };
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is ActionRow row) { Run(row.Action); e.Handled = true; } };
            if (searchHandler is not null) search.PreviewKeyDown -= searchHandler;
            searchHandler = (_, e) =>
            {
                if (e.Key == Key.Down) { list.Focus(); e.Handled = true; }
                if (e.Key == Key.Enter && list.SelectedItem is ActionRow row) { Run(row.Action); e.Handled = true; }
            };
            search.PreviewKeyDown += searchHandler;
            var dock = new DockPanel();
            var run = UI.Button(app.L("Run Selected"), () => { if (list.SelectedItem is ActionRow row) Run(row.Action); });
            DockPanel.SetDock(run, Dock.Bottom); dock.Children.Add(run); dock.Children.Add(list);
            actionArea.Content = dock;
            return;
        }
        var slice = actions.Skip(page * 8).Take(8).ToArray();
        var canvas = MenuPresentation.Build(app,slice,Run,(action,paths) => { SetFiles(paths); Run(action); },Close,motion);
        var container = new DockPanel();
        var pages = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var previous = UI.Button(app.L("Previous"), () => { page--; DrawActions(); }); previous.IsEnabled = page > 0;
        var next = UI.Button(app.L("Next"), () => { page++; DrawActions(); }); next.IsEnabled = (page + 1) * 8 < actions.Length;
        pages.Children.Add(previous); pages.Children.Add(next);
        if (app.Prefs.MenuTemplate != MenuTemplate.Wheel) pages.Children.Add(UI.Button(app.L("Cancel"),Close));
        DockPanel.SetDock(pages, Dock.Bottom); container.Children.Add(pages);
        container.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        actionArea.Content = container;
    }
    private sealed record ActionRow(ToolAction Action, string Title);
}

internal sealed class ResizeDialog : Window
{
    public ResizeOptions Options { get; private set; } = new();
    public ResizeDialog(App app)
    {
        Title = app.L("Resize…"); Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = System.Windows.ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(app.L("Resize by"), 18));
        var mode = new ComboBox { ItemsSource = new[] { app.L("Long Edge"), app.L("Width"), app.L("Height"), app.L("Percent") }, SelectedIndex = 0, MinHeight = 30 }; panel.Children.Add(mode);
        System.Windows.Automation.AutomationProperties.SetName(mode, app.L("Resize by"));
        var field = new TextBox { Text = "1920", FontSize = 18, Margin = new Thickness(0, 8, 0, 12) }; panel.Children.Add(field);
        System.Windows.Automation.AutomationProperties.SetName(field, app.L("Pixels or percent"));
        mode.SelectionChanged += (_, _) => field.Text = mode.SelectedIndex == 3 ? "50" : "1920";
        var enlarge = new CheckBox { Content = app.L("Allow enlargement"), Margin = new Thickness(0, 5, 0, 12) }; panel.Children.Add(enlarge);
        panel.Children.Add(UI.Text(app.L("Aspect ratio is kept. Images only shrink unless enlargement is enabled. Results are PNG files; the 16 MP limit still applies.")));
        var error = UI.Text(""); panel.Children.Add(error); var row = new WrapPanel();
        var apply = UI.Button(app.L("Apply"), () => {
            try {
                if (!double.TryParse(field.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double value)) throw new UserError("Choose 16–8192 whole pixels, or 0.1–400 percent.");
                var options = new ResizeOptions((ResizeBasis)mode.SelectedIndex, value, enlarge.IsChecked == true); options.Validate(); Options = options; DialogResult = true;
            } catch (UserError ex) { error.Text = app.L(ex.Message); }
        }); apply.IsDefault = true; row.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        panel.Children.Add(row); Content = panel;
    }
}
