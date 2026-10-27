using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Swivel;

/// App Actions settings (FileActionsPanes.swift's AppActionsPane): the rules of the Apps menu, each with its own
/// switch, delivery and extension filter, plus Add / Edit / Remove.
sealed class AppRoutingPane : Pane
{
    readonly ListBox table = new() { Height = 268, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
    readonly Button editButton, removeButton;
    readonly AppLauncher launcher;
    List<FileAppTarget> targets = [];

    public AppRoutingPane() : this(null) { }

    internal AppRoutingPane(AppLauncher? launcher)
    {
        this.launcher = launcher ?? AppLauncher.Default;
        Add(Ui.Text(L("Your files, your apps"), 20, FontWeights.SemiBold));
        Add(Ui.Text(L("Choose which apps appear in the Apps wheel. Add an app, choose a delivery method and optionally limit its file extensions."), tone: Tone.Secondary), 6);

        table.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        table.SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(table, Feature.AppRouting.Title());
        table.SelectionChanged += (_, _) => UpdateButtons();
        table.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source || IsInsideCheckBox(source)) return;
            EditTarget();
        };
        table.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { RemoveTarget(); e.Handled = true; }
            else if (e.Key == Key.Enter) { EditTarget(); e.Handled = true; }
        };
        Add(table, 16);

        var add = Ui.Button(L("Add Application…"), () => ShowEditor(null));
        editButton = Ui.Button(L("Edit…"), EditTarget);
        removeButton = Ui.Button(L("Remove"), RemoveTarget);
        Add(new WrapPanel { Children = { add, Spaced(editButton), Spaced(removeButton) } }, 12);
        Add(Ui.Text(L("Codex and Claude Code open a new draft containing local file references. They do not upload attachments or send a message automatically. Review and send in the target app."), 12, tone: Tone.Secondary), 16);
        Add(Ui.Text(L("Open files starts the app with the selected files. Copy files and open app prepares attachments for you to paste. This only changes Swivel’s actions; your default apps stay the same."), 12, tone: Tone.Secondary), 8);
        Refresh();
    }

    static Button Spaced(Button button)
    {
        button.Margin = new Thickness(8, 0, 0, 0);
        return button;
    }

    static bool IsInsideCheckBox(DependencyObject node)
    {
        for (DependencyObject? at = node; at != null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is CheckBox) return true;
        return false;
    }

    internal ListBox Table => table;
    internal IReadOnlyList<FileAppTarget> Targets => targets;
    internal Button EditButton => editButton;
    internal Button RemoveButton => removeButton;

    FileAppTarget? Selected => table.SelectedItem is ListBoxItem { Tag: string id } ? targets.FirstOrDefault(t => t.Id == id) : null;

    /// Reloads only when the saved rules differ from the rows (a switch flipped here doesn't rebuild the list).
    public override void Refresh()
    {
        var latest = AppTargets.All.ToList();
        if (!latest.SequenceEqual(targets))
        {
            var selectedId = Selected?.Id;
            targets = latest;
            table.Items.Clear();
            foreach (var target in targets) table.Items.Add(MakeRow(target));
            if (targets.Count > 0) table.SelectedIndex = Math.Max(targets.FindIndex(t => t.Id == selectedId), 0);
        }
        UpdateButtons();
    }

    ListBoxItem MakeRow(FileAppTarget target)
    {
        var toggle = new CheckBox { IsChecked = target.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(toggle, target.Name);
        var id = target.Id;
        toggle.Click += (_, _) => SetEnabled(id, toggle.IsChecked == true);

        bool installed = FileAppRouting.Locate(target, launcher) != null;
        var name = Ui.Text(target.Name, 14, FontWeights.Medium, wrap: false);
        var status = Ui.Text(installed ? L("Installed") : L("Choose the app to finish setup"), 12, tone: installed ? Tone.Secondary : Tone.Caution, wrap: false);
        status.Margin = new Thickness(0, 2, 0, 0);
        var delivery = Ui.Text(target.Delivery.Title(), 13, wrap: false);
        var types = Ui.Text(target.Extensions.Count == 0 ? L("All files") : string.Join(", ", target.Extensions), 12, tone: Tone.Secondary, wrap: false);
        types.Margin = new Thickness(0, 2, 0, 0);
        types.ToolTip = types.Text;

        var grid = new Grid { Margin = new Thickness(4, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var left = new StackPanel { Children = { name, status }, Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel { Children = { delivery, types } };
        Grid.SetColumn(left, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(toggle);
        grid.Children.Add(left);
        grid.Children.Add(right);
        var item = new ListBoxItem { Content = grid, Tag = target.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, target.Name);
        AutomationProperties.SetHelpText(item, $"{target.Delivery.Title()}, {types.Text}");
        return item;
    }

    internal void SetEnabled(string id, bool enabled)
    {
        int index = targets.FindIndex(t => t.Id == id);
        if (index < 0) return;
        targets[index] = targets[index] with { Enabled = enabled };
        AppTargets.All = targets;
    }

    void UpdateButtons()
    {
        editButton.IsEnabled = Selected != null;
        removeButton.IsEnabled = Selected != null;
    }

    void EditTarget()
    {
        if (Selected is { } selected) ShowEditor(selected);
    }

    void ShowEditor(FileAppTarget? target) => AppTargetForm.Present(this, target, Saved, launcher);

    internal void Saved(FileAppTarget value)
    {
        AppTargets.Save(value);
        Refresh();
        if (table.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == value.Id) is { } row)
        {
            table.SelectedItem = row;
            table.ScrollIntoView(row);
        }
    }

    internal void RemoveTarget()
    {
        if (Selected is not { } selected) return;
        int index = targets.FindIndex(t => t.Id == selected.Id);
        AppTargets.All = targets.Where(t => t.Id != selected.Id).ToList();
        Refresh();
        if (targets.Count > 0) table.SelectedIndex = Math.Min(index, targets.Count - 1);
    }
}

/// Add Application / Edit Application (the Mac's AppTargetEditor): an installed app or a chosen .exe, the name in the
/// menu, the delivery and an optional extension filter. Save checks the name, the extensions and that the app can be
/// found, and says what's wrong.
sealed class AppTargetForm : Window
{
    FileAppTarget value;
    readonly Action<FileAppTarget> saved;
    readonly AppLauncher launcher;
    readonly TextBox name = new() { MinWidth = 320 };
    readonly TextBox extensions = new() { MinWidth = 320 };
    readonly ComboBox delivery = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox installed = new() { MinWidth = 320, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Button browse;
    readonly TextBlock appLabel = Ui.Text("", 12, tone: Tone.Secondary, wrap: false);
    readonly TextBlock issue = Ui.Text("", 13, tone: Tone.Critical);
    List<InstalledApp> apps = [];
    bool filling;

    public AppTargetForm(FileAppTarget? target, Action<FileAppTarget> saved, AppLauncher? launcher = null, Func<Task<List<InstalledApp>>>? loadApps = null)
    {
        value = target ?? new FileAppTarget { Id = Guid.NewGuid().ToString().ToUpperInvariant(), Delivery = AppDelivery.OpenFiles };
        this.saved = saved;
        this.launcher = launcher ?? AppLauncher.Default;
        Title = L(target == null ? "Add Application" : "Edit Application");
        if (Application.Current is null || Application.Current.ThemeMode == ThemeMode.None) ThemeMode = ThemeMode.System;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppTypography.Apply(this);

        name.Text = value.Name;
        extensions.Text = string.Join(", ", value.Extensions);
        AutomationProperties.SetName(name, L("Name in the Wheel"));
        AutomationProperties.SetName(extensions, L("File extensions"));
        AutomationProperties.SetName(delivery, L("Delivery"));
        AutomationProperties.SetName(installed, L("Application"));
        foreach (var d in AppDeliveries.All) delivery.Items.Add(d.Title());
        delivery.SelectedIndex = Array.IndexOf(AppDeliveries.All, value.Delivery);
        delivery.SelectionChanged += (_, _) =>
        {
            if (delivery.SelectedIndex < 0) return;
            value = value with { Delivery = AppDeliveries.All[delivery.SelectedIndex] };
            UpdateAppLabel();
        };
        installed.Items.Add(L("Choose an installed app…"));
        installed.SelectedIndex = 0;
        installed.SelectionChanged += (_, _) =>
        {
            int index = installed.SelectedIndex - 1;
            if (!filling && index >= 0 && index < apps.Count) Select(apps[index]);
        };
        browse = Ui.Button(L("Choose Application…"), Browse);
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        _ = LoadApps(loadApps ?? InstalledApps.Load);

        name.TextChanged += (_, _) => { if (issue.Text.Length > 0) issue.Text = ""; };
        extensions.TextChanged += (_, _) => { if (issue.Text.Length > 0) issue.Text = ""; };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var extensionHint = Ui.Text(L("Empty means all files, e.g. png, jpg, pdf"), 12, tone: Tone.Secondary);
        extensionHint.Margin = new Thickness(0, 4, 0, 0);
        appLabel.MaxWidth = 420;
        appLabel.Margin = new Thickness(0, 4, 0, 0);
        var appColumn = new StackPanel { Children = { installed, appLabel } };
        UIElement?[][] rows =
        [
            [Label("Application"), appColumn],
            [null, browse],
            [Label("Name"), name],
            [Label("Delivery"), delivery],
            [Label("File extensions"), new StackPanel { Children = { extensions, extensionHint } }],
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < 2; c++)
            {
                if (rows[i][c] is not FrameworkElement cell) continue;
                cell.Margin = new Thickness(c == 0 ? 0 : 16, i == 0 ? 0 : i == 1 ? 8 : 12, 0, 0);
                cell.VerticalAlignment = c == 0 ? VerticalAlignment.Top : VerticalAlignment.Center;
                if (c == 0) cell.Margin = new Thickness(0, cell.Margin.Top + 6, 0, 0);
                Grid.SetRow(cell, i);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        issue.MaxWidth = 480;
        issue.Margin = new Thickness(0, 12, 0, 0);
        var cancel = new Button { Content = L("Cancel"), IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        var save = new Button { Content = L("Save"), IsDefault = true, MinWidth = 96 };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        save.Click += (_, _) => TrySave();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Children = { save, cancel } };
        Content = new StackPanel { Margin = new Thickness(24), Children = { grid, issue, buttons } };
        Loaded += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        UpdateAppLabel();
    }

    /// Opens the form over the window that holds `owner`.
    public static void Present(FrameworkElement owner, FileAppTarget? target, Action<FileAppTarget> saved, AppLauncher? launcher = null)
    {
        var form = new AppTargetForm(target, saved, launcher) { Owner = Window.GetWindow(owner) };
        if (form.Owner is null) form.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        form.ShowDialog();
    }

    static TextBlock Label(string text)
    {
        var label = Ui.Text(L(text), 14, wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        return label;
    }

    internal TextBox NameBox => name;
    internal TextBox ExtensionsBox => extensions;
    internal ComboBox DeliveryBox => delivery;
    internal ComboBox InstalledBox => installed;
    internal string AppLabelText => appLabel.Text;
    internal string IssueText => issue.Text;
    internal FileAppTarget Value => value;

    async Task LoadApps(Func<Task<List<InstalledApp>>> load)
    {
        List<InstalledApp> found;
        try { found = await load(); }
        catch (Exception ex)
        {
            Log.Write($"Installed apps: {ex.Message}");
            return;
        }
        filling = true;
        apps = found;
        foreach (var app in apps) installed.Items.Add(app.Name);
        int match = apps.FindIndex(a => (value.BundleId.Length > 0 && a.BundleId == value.BundleId)
            || (value.AppPath.Length > 0 && string.Equals(a.AppPath, value.AppPath, StringComparison.OrdinalIgnoreCase)));
        installed.SelectedIndex = match + 1;
        filling = false;
        UpdateAppLabel();
    }

    /// Uses an installed app (or a chosen .exe); the name follows it while it's empty.
    internal void Select(InstalledApp app)
    {
        value = value with { AppPath = app.AppPath, BundleId = app.BundleId };
        if (name.Text.Trim().Length == 0) name.Text = app.Name;
        issue.Text = "";
        UpdateAppLabel();
    }

    void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = L("Applications") + " (*.exe)|*.exe",
            CheckFileExists = true,
            Title = L("Choose Application…"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        UseExecutable(dialog.FileName);
    }

    /// A browsed .exe: anything but an existing application (or Swivel itself) is refused.
    internal bool UseExecutable(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || FileAppRouting.IsSwivel(path))
        {
            issue.Text = L("Choose an installed application first.");
            return false;
        }
        Select(new InstalledApp(System.IO.Path.GetFileNameWithoutExtension(path), path, ""));
        installed.SelectedIndex = Math.Max(apps.FindIndex(a => string.Equals(a.AppPath, path, StringComparison.OrdinalIgnoreCase)) + 1, 0);
        return true;
    }

    void UpdateAppLabel()
    {
        bool draft = value.Delivery.IsDraft();
        // A draft goes to the app registered for its link, so there's no app to pick.
        installed.IsEnabled = !draft;
        browse.IsEnabled = !draft;
        var location = FileAppRouting.Locate(value, launcher);
        appLabel.Text = location?.Kind switch
        {
            AppLocation.Kinds.Executable => System.IO.Path.GetFileName(location.Value),
            AppLocation.Kinds.Packaged => apps.FirstOrDefault(a => a.BundleId == location.Value)?.Name ?? location.Value,
            AppLocation.Kinds.LinkHandler => location.DisplayName,
            _ => L("No application selected"),
        };
        appLabel.ToolTip = location?.Value;
    }

    /// Saves and closes, or says what's wrong. True when saved.
    internal bool TrySave()
    {
        var typed = extensions.Text.Trim();
        var candidate = value with { Name = name.Text.Trim(), Extensions = FileAppTarget.NormalizedExtensions(typed) };
        if (!candidate.IsValid) return Refuse(name, L("Enter a name of 1–80 characters."));
        if (typed.Length > 0 && candidate.Extensions.Count == 0) return Refuse(extensions, L("Enter file extensions separated by commas."));
        if (FileAppRouting.Locate(candidate, launcher) == null)
        {
            return Refuse(null, candidate.Delivery.Scheme() is { } scheme
                ? L("No app on this PC opens %@ links. Install %@, then try again.", scheme + "://", candidate.Name)
                : L("Choose an installed application first."));
        }
        value = candidate;
        saved(candidate);
        if (IsLoaded)
        {
            if (IsVisible && Owner != null) DialogResult = true;
            Close();
        }
        return true;
    }

    bool Refuse(TextBox? field, string message)
    {
        issue.Text = message;
        if (!IsLoaded || field is null) return false;
        field.Focus();
        field.SelectAll();
        Ui.Shake(field);
        return false;
    }
}
