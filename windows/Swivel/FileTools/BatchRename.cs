using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Swivel;

/// The rename rules, combined in this order: base name (or the original name), find/replace, prefix and suffix, then
/// a number. Extensions are kept; folders have none. A port of BatchRename.swift.
sealed record RenameRules
{
    public string Base { get; init; } = "";
    public string Find { get; init; } = "";
    public string Replacement { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public bool Number { get; init; }
    public long Start { get; init; } = 1;
    public int Digits { get; init; } = 3;

    public string Name(string path, int index)
    {
        var fileName = Path.GetFileName(path);
        var ext = Directory.Exists(path) ? "" : RenamePlan.Extension(fileName);
        var stem = Base.Length == 0 ? (ext.Length == 0 ? fileName : fileName[..^(ext.Length + 1)]) : Base;
        if (Find.Length > 0) stem = stem.Replace(Find, Replacement, StringComparison.Ordinal);
        stem = Prefix + stem + Suffix;
        if (Number)
        {
            long value;
            try { value = checked(Start + index); }
            catch (OverflowException) { return ""; }
            stem += "-" + value.ToString("D" + Math.Clamp(Digits, 1, 8), CultureInfo.InvariantCulture);
        }
        return ext.Length == 0 ? stem : stem + "." + ext;
    }
}

sealed record RenameEntry(string Source, string Destination, FileIdentity? Identity, string? Issue = null)
{
    /// A case-only change counts: Windows keeps the new spelling.
    public bool Changes => !string.Equals(Source, Destination, StringComparison.Ordinal);
}

sealed record RenamePlan(IReadOnlyList<RenameEntry> Entries)
{
    public bool CanApply => Entries.Count > 0 && Entries.All(e => e.Issue is null) && Entries.Any(e => e.Changes);
    public int ChangedCount => Entries.Count(e => e.Changes);

    public static RenamePlan Preview(IEnumerable<string> paths, RenameRules rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = paths.Select(Normalize).Where(p => p != null).Select(p => p!).Where(seen.Add).ToList();
        return Validate(files.Select((path, index) =>
        {
            var name = rules.Name(path, index);
            string? issue = null;
            if (!ValidName(name) || rules.Start < 0 || rules.Digits is < 1 or > 8) issue = L("Invalid name or numbering");
            return new RenameEntry(path, Path.Join(Parent(path), name), FileIdentity.TryRead(path), issue);
        }).ToList());
    }

    /// A full path without a trailing separator (null for empty input).
    static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd('\\', '/');
            // A drive root keeps its separator ("C:\"); it can't be renamed and fails validation.
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    static string Parent(string path) => Path.GetDirectoryName(path) ?? "";

    /// The extension as the Mac reads it: after the last dot, never a leading one (".hidden" has none).
    public static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..] : "";
    }

    static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// The Mac's rules (no empty name, ".", "..", leading dot, separators, NUL or line breaks), plus what Windows
    /// refuses: \ / : * ? " < > | and control characters, a trailing space or dot, device names such as CON or NUL
    /// (with any extension), and more than 255 UTF-16 units. ':' would otherwise name an alternate data stream.
    public static bool ValidName(string name)
    {
        if (name.Length == 0 || name == "." || name == ".." || name.StartsWith('.')) return false;
        if (name.Length > 255 || name.IndexOfAny(Invalid) >= 0 || name.Any(c => c is '\u2028' or '\u2029' or '\u0085')) return false;
        if (name.EndsWith(' ') || name.EndsWith('.')) return false;
        int dot = name.IndexOf('.');
        return !Reserved.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }

    /// Compares names the way Windows does: case-insensitively. Canonically equivalent spellings also count as the
    /// same, which is conservative (NTFS itself would allow both).
    public static string Key(string path) => path.TrimEnd('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static RenamePlan Validate(IReadOnlyList<RenameEntry> original)
    {
        var rows = original.ToList();
        var sources = rows.Select(r => Key(r.Source)).ToHashSet(StringComparer.Ordinal);
        var destinations = rows.GroupBy(r => Key(r.Destination), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var issue = row.Issue;
            if (!ValidName(Path.GetFileName(row.Destination)) ||
                !string.Equals(Parent(row.Source), Parent(row.Destination), StringComparison.OrdinalIgnoreCase))
                issue = L("Invalid name or numbering");
            if (row.Identity is null) issue = L("File is missing or unavailable");
            if (destinations.GetValueOrDefault(Key(row.Destination)) > 1) issue = L("Duplicate destination name");
            if (ValidName(Path.GetFileName(row.Destination)) && FileToolsNative.Exists(row.Destination) && !sources.Contains(Key(row.Destination)))
                issue = L("A file with this name already exists");
            if (rows.Any(other => row.Source.StartsWith(other.Source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                issue = L("Choose a folder or its contents, not both");
            rows[i] = row with { Issue = issue };
        }
        return new RenamePlan(rows);
    }
}

sealed record RenameReceipt(IReadOnlyList<RenameEntry> Entries)
{
    public RenameReceipt Undo() => BatchRenamer.Execute(RenamePlan.Validate(Entries.Select(e =>
        new RenameEntry(e.Destination, e.Source, e.Identity)).ToList()));
}

static class BatchRenamer
{
    /// MoveFileEx without MOVEFILE_REPLACE_EXISTING is an atomic no-replace rename on the same volume (the Mac's
    /// renamex_np with RENAME_EXCL), so a file created after the preview is never overwritten. Works for folders too.
    public static void Move(string source, string destination)
    {
        if (!FileToolsNative.MoveFileExW(FileToolsNative.Extended(source), FileToolsNative.Extended(destination), 0))
            throw new ToolError(L("Could not rename %@ to %@", source, destination));
    }

    static void Check(string path, FileIdentity? identity)
    {
        if (FileIdentity.Read(path) != identity) throw new ToolError(L("Files changed since preview. Refresh and try again."));
    }

    public static RenameReceipt Execute(RenamePlan plan, Action<string, string>? moveFile = null)
    {
        moveFile ??= Move;
        if (!plan.CanApply || !RenamePlan.Validate(plan.Entries).CanApply) throw new ToolError(L("Resolve the highlighted conflicts first."));
        var rows = plan.Entries.Where(e => e.Changes).ToList();
        foreach (var row in rows) Check(row.Source, row.Identity);
        // Stage beside each source, so case-only changes and name swaps work on the same volume.
        var staging = rows.Select(r => Path.Join(Path.GetDirectoryName(r.Source) ?? "", ".swivel-rename-" + Guid.NewGuid().ToString().ToUpperInvariant())).ToList();
        var current = rows.Select(r => r.Source).ToList();
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Check(current[i], rows[i].Identity);
                moveFile(current[i], staging[i]);
                current[i] = staging[i];
            }
            for (int i = 0; i < rows.Count; i++)
            {
                Check(current[i], rows[i].Identity);
                moveFile(current[i], rows[i].Destination);
                current[i] = rows[i].Destination;
            }
        }
        catch (Exception)
        {
            // First free any completed destinations, then restore originals. Never remove a conflicting file.
            for (int i = 0; i < rows.Count; i++)
            {
                if (current[i] == rows[i].Source || current[i] == staging[i]) continue;
                if (FileIdentity.TryRead(current[i]) == rows[i].Identity && TryMove(moveFile, current[i], staging[i])) current[i] = staging[i];
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (current[i] == rows[i].Source) continue;
                if (FileIdentity.TryRead(current[i]) == rows[i].Identity && TryMove(moveFile, current[i], rows[i].Source)) current[i] = rows[i].Source;
            }
            var remaining = rows.Select((r, i) => (r, i)).Where(x => current[x.i] != x.r.Source).Select(x => current[x.i]).ToList();
            if (remaining.Count > 0)
                throw new ToolError(L("Some files could not be restored. They remain at:\n%@", string.Join("\n", remaining)));
            throw;
        }
        return new RenameReceipt(rows);
    }

    static bool TryMove(Action<string, string> move, string from, string to)
    {
        try { move(from, to); return true; }
        catch (Exception) { return false; }
    }
}

/// Preview and rename a batch of files (BatchRenameWindow in BatchRename.swift). The full old → new list, with
/// every conflict, is shown before anything changes; Undo Rename stays available while the window is open.
sealed class BatchRenameWindow : FileToolWindow
{
    public static void Present(IReadOnlyList<string> files)
    {
        if (!Feature.BatchRename.IsOn()) return;
        new BatchRenameWindow(files).Show();
    }

    public static void CloseAll() => CloseAll(Feature.BatchRename);

    List<string> paths;
    public readonly TextBox BaseName = new(), FindText = new(), Replacement = new(), Prefix = new(), Suffix = new();
    public readonly CheckBox Numbering = new() { Content = L("Add a number"), VerticalAlignment = VerticalAlignment.Center };
    public readonly TextBox StartAt = new() { Text = "1", Width = 80 };
    public readonly ComboBox DigitsBox = new() { MinWidth = 70 };
    public readonly ListView Table = new();
    readonly TextBlock status = ToolUI.Note("");
    public readonly Button ApplyButton, UndoButton, ChooseButton;
    readonly DropRing ring = new();
    RenameReceipt? receipt;
    string? resultMessage;

    public RenamePlan Plan { get; private set; } = new([]);
    public string Status => status.Text;

    public BatchRenameWindow(IReadOnlyList<string> files) : base(Feature.BatchRename, L("Batch Rename"), 850, 690)
    {
        paths = [.. files];
        Window.AllowDrop = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        var hint = ToolUI.Note(L("Keep the original name when empty"));
        hint.VerticalAlignment = VerticalAlignment.Center;
        AddRow(grid, 0, L("Base name"), FileToolUI.WithPlaceholder(BaseName, L("Original name")), hint);
        AddRow(grid, 1, L("Find"), FindText, FileToolUI.WithPlaceholder(Replacement, L("Replace with")));
        AddRow(grid, 2, L("Prefix"), Prefix, FileToolUI.WithPlaceholder(Suffix, L("Suffix")));
        foreach (var (field, name) in new[] { (BaseName, L("Base name")), (FindText, L("Find")), (Replacement, L("Replace with")),
                                              (Prefix, L("Prefix")), (Suffix, L("Suffix")), (StartAt, L("Start at")) })
        {
            ToolUI.AutomationName(field, name);
            field.TextChanged += (_, _) => Changed();
        }

        for (int i = 1; i <= 8; i++) DigitsBox.Items.Add(i.ToString(CultureInfo.InvariantCulture));
        DigitsBox.SelectedIndex = 2;
        ToolUI.AutomationName(DigitsBox, L("Digits"));
        DigitsBox.SelectionChanged += (_, _) => Changed();
        Numbering.Checked += (_, _) => Changed();
        Numbering.Unchecked += (_, _) => Changed();
        var numbers = ToolUI.Row([Numbering, FileToolUI.Label(L("Start at")), StartAt, FileToolUI.Label(L("Digits")), DigitsBox]);

        var view = new GridView();
        foreach (var (column, title, width) in new[] { (0, L("Current name"), 230.0), (1, L("New name"), 260.0), (2, L("Status"), 240.0) })
        {
            var cell = new FrameworkElementFactory(typeof(RenameCell));
            cell.SetValue(RenameCell.ColumnProperty, column);
            view.Columns.Add(new GridViewColumn { Header = title, Width = width, CellTemplate = new DataTemplate { VisualTree = cell } });
        }
        Table.View = view;
        Table.MinHeight = 180;
        Table.SelectionMode = SelectionMode.Extended;
        ScrollViewer.SetHorizontalScrollBarVisibility(Table, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(Table, true);
        ToolUI.AutomationName(Table, L("Batch Rename"));

        ApplyButton = FileToolUI.Accent(ToolUI.Button(L("Rename Files"), () => _ = ApplyRename()));
        ApplyButton.IsDefault = true;
        UndoButton = ToolUI.Button(L("Undo Rename"), () => _ = UndoRename());
        UndoButton.IsEnabled = false;
        ChooseButton = ToolUI.Button(L("Choose Files…"), ChooseFiles);
        ChooseButton.ToolTip = L("To rename folders, drop them on this window.");
        var footer = ToolUI.Bar([ChooseButton], [UndoButton, ApplyButton]);
        status.TextWrapping = TextWrapping.Wrap;

        var top = ToolUI.Stack([ToolUI.Note(L("This changes original file names. Review every new name before renaming. Extensions are kept.")), grid, numbers]);
        var bottom = ToolUI.Stack([status, footer]);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        top.Margin = new Thickness(0, 0, 0, 12);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(Table);
        Install(root, ring);
        FocusManager.SetFocusedElement(Window, BaseName);

        Window.DragEnter += (_, e) => DragOver(e);
        Window.DragOver += (_, e) => DragOver(e);
        Window.DragLeave += (_, _) => ring.SetShown(false);
        Window.Drop += (_, e) =>
        {
            ring.SetShown(false);
            var dropped = ToolUI.DroppedFiles(e.Data);
            if (dropped.Count == 0 || IsBusy) return;
            e.Effects = ToolUI.DropEffect(e.AllowedEffects);
            Replace(dropped);
        };
        RefreshPreview();
    }

    static void AddRow(Grid grid, int row, string label, FrameworkElement field, FrameworkElement third)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = FileToolUI.Label(label);
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.Margin = new Thickness(0, row == 0 ? 0 : 10, 10, 0);
        field.Margin = new Thickness(0, row == 0 ? 0 : 10, 10, 0);
        third.Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 0);
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetRow(third, row);
        Grid.SetColumn(field, 1);
        Grid.SetColumn(third, 2);
        grid.Children.Add(text);
        grid.Children.Add(field);
        grid.Children.Add(third);
    }

    void DragOver(DragEventArgs e)
    {
        bool files = Feature.BatchRename.IsOn() && !IsBusy && ToolUI.DroppedFiles(e.Data).Count > 0;
        e.Effects = files ? ToolUI.DropEffect(e.AllowedEffects) : DragDropEffects.None;
        ring.SetShown(files);
        e.Handled = true;
    }

    public RenameRules Rules => new()
    {
        Base = BaseName.Text,
        Find = FindText.Text,
        Replacement = Replacement.Text,
        Prefix = Prefix.Text,
        Suffix = Suffix.Text,
        Number = Numbering.IsChecked == true,
        Start = long.TryParse(StartAt.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : -1,
        Digits = DigitsBox.SelectedIndex + 1,
    };

    void Changed()
    {
        resultMessage = null;
        RefreshPreview();
    }

    public void RefreshPreview()
    {
        Plan = RenamePlan.Preview(paths, Rules);
        Table.ItemsSource = Plan.Entries.Select(e => new RenameRow(e)).ToList();
        ApplyButton.IsEnabled = !IsBusy && Plan.CanApply && Feature.BatchRename.IsOn();
        bool numbered = Numbering.IsChecked == true;
        StartAt.IsEnabled = numbered;
        DigitsBox.IsEnabled = numbered;
        status.Text = resultMessage ?? (Plan.Entries.Any(e => e.Issue != null)
            ? L("Resolve the highlighted conflicts first.")
            : L("%d files will be renamed", Plan.ChangedCount));
    }

    void ChooseFiles()
    {
        if (IsBusy) return;
        var chosen = ToolUI.ChooseFiles(imagesOnly: false, multiple: true, owner: Window);
        if (chosen.Count > 0) Replace(chosen);
    }

    /// A new set of files: the old undo no longer applies.
    public void Replace(IReadOnlyList<string> files)
    {
        paths = [.. files];
        receipt = null;
        resultMessage = null;
        UndoButton.IsEnabled = false;
        RefreshPreview();
    }

    public Task ApplyRename()
    {
        if (!Feature.BatchRename.IsOn() || IsBusy || !Plan.CanApply) return Task.CompletedTask;
        var snapshot = Plan;
        return RunWork(async () =>
        {
            var value = await Task.Run(() => BatchRenamer.Execute(snapshot));
            receipt = value;
            paths = snapshot.Entries.Select(e => e.Destination).ToList();
            resultMessage = L("Renamed %d files. Undo is available in this window.", value.Entries.Count);
        });
    }

    public Task UndoRename()
    {
        if (!Feature.BatchRename.IsOn() || IsBusy || receipt is not { } done) return Task.CompletedTask;
        return RunWork(async () =>
        {
            await Task.Run(done.Undo);
            paths = paths.Select(p => done.Entries.FirstOrDefault(e => string.Equals(e.Destination, p, StringComparison.OrdinalIgnoreCase))?.Source ?? p).ToList();
            receipt = null;
            resultMessage = L("Original names restored");
        });
    }

    protected override void WorkFinished()
    {
        RefreshPreview();
        UndoButton.IsEnabled = receipt != null;
    }

    protected override void SettingsChanged() => RefreshPreview();

    sealed class RenameRow(RenameEntry entry)
    {
        public RenameEntry Entry { get; } = entry;
    }

    /// One cell of the preview table; reads its row from the DataContext (no bindings, code-only WPF).
    sealed class RenameCell : TextBlock
    {
        public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(nameof(Column), typeof(int), typeof(RenameCell),
            new PropertyMetadata(0, (d, _) => ((RenameCell)d).Update()));

        public int Column { get => (int)GetValue(ColumnProperty); set => SetValue(ColumnProperty, value); }

        public RenameCell()
        {
            TextTrimming = TextTrimming.CharacterEllipsis;
            VerticalAlignment = VerticalAlignment.Center;
            DataContextChanged += (_, _) => Update();
        }

        void Update()
        {
            if (DataContext is not RenameRow { Entry: var entry }) return;
            Text = Column switch
            {
                0 => Path.GetFileName(entry.Source),
                1 => Path.GetFileName(entry.Destination),
                _ => entry.Issue ?? (entry.Changes ? L("Ready") : L("Unchanged")),
            };
            ToolTip = Text;
            if (entry.Issue != null) Foreground = ToolUI.ErrorBrush;
            else ClearValue(ForegroundProperty);
        }
    }
}

/// The settings page's content: how it works, Choose Files…, and a hint while the feature is off.
sealed class BatchRenamePane : Pane
{
    readonly Button choose;
    readonly TextBlock offHint;

    public BatchRenamePane()
    {
        Add(Ui.Text(L("Combine a base name, text replacement, prefix, suffix and numbering. Review the full preview and conflicts before changing names. Undo remains available while the rename window is open."), tone: Tone.Secondary));
        choose = Ui.Button(L("Choose Files…"), () => FileTools.ChooseAndOpen(Feature.BatchRename), accent: true);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { choose } }, 16);
        Add(Ui.Text(L("Also available in the Wheel’s Actions menu and Quick Actions."), 12, tone: Tone.Secondary), 16);
        offHint = Ui.Text(L("Turn this feature on above to get started."), 12, tone: Tone.Secondary);
        Add(offHint, 10);
        Refresh();
    }

    internal Button Choose => choose;

    public override void Refresh()
    {
        bool on = Feature.BatchRename.IsOn();
        choose.IsEnabled = on;
        offHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
