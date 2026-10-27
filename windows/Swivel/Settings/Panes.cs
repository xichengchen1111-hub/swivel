using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Swivel's icon (Swivel.ico, a WPF resource), at the frame closest to the size; a glyph if it can't be loaded.
static class AppIcon
{
    public static FrameworkElement Element(double size)
    {
        try
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // registers pack:// outside a running Application (tests)
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Swivel;component/Swivel.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderBy(f => f.PixelWidth >= size * 2 ? f.PixelWidth - size * 2 : 1000 + size * 2 - f.PixelWidth).First();
            var image = new Image { Source = frame, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch (Exception)
        {
            return Ui.Glyph("", size, Tone.Accent);
        }
    }
}

// MARK: - Overview

/// Overview: one card per feature with its switch and a link to its settings.
sealed class OverviewPage : StackPanel
{
    public OverviewPage(Action<SettingsView.Page> open)
    {
        Children.Add(Ui.Title("Swivel"));
        var intro = Ui.Text(L("A bundle of small tools for Windows. Turn on the ones you want — each works on its own."), tone: Tone.Secondary);
        intro.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(intro);
        var group = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        foreach (var (feature, page) in new[]
                 {
                     (Feature.Wheel, SettingsView.Page.Wheel), (Feature.Clothesline, SettingsView.Page.Clothesline),
                     (Feature.TextCapture, SettingsView.Page.TextCapture), (Feature.Shelf, SettingsView.Page.Shelf),
                     (Feature.ImagePresets, SettingsView.Page.ImagePresets), (Feature.QuickActions, SettingsView.Page.QuickActions),
                     (Feature.BatchRename, SettingsView.Page.BatchRename), (Feature.PdfPages, SettingsView.Page.PdfPages),
                     (Feature.QrCodes, SettingsView.Page.QrCodes), (Feature.AppRouting, SettingsView.Page.AppRouting),
                 })
        {
            var card = new FeatureHeader(feature, compact: true, open: () => open(page));
            if (group.Children.Count > 0) card.Margin = new Thickness(0, 8, 0, 0);
            group.Children.Add(card);
        }
        Children.Add(group);
        var foot = Ui.Text(L("You can also switch features on and off from the Swivel icon in the notification area."), 12, tone: Tone.Secondary);
        foot.Margin = new Thickness(0, 16, 0, 0);
        Children.Add(foot);
    }

    internal IEnumerable<FeatureHeader> Cards => ((StackPanel)Children[2]).Children.OfType<FeatureHeader>();
}

/// A feature's page: title, header with the switch, then that feature's settings.
sealed class FeaturePage : StackPanel
{
    public Feature Feature { get; }
    public Pane Content { get; }
    public FeatureHeader Header { get; }

    public FeaturePage(Feature feature, Pane content)
    {
        Feature = feature;
        Content = content;
        Children.Add(Ui.Title(feature.Title()));
        Header = new FeatureHeader(feature) { Margin = new Thickness(0, 20, 0, 0) };
        Children.Add(Header);
        content.Margin = new Thickness(0, 24, 0, 0);
        Children.Add(content);
    }
}

// MARK: - General

sealed class GeneralPage : Pane
{
    readonly ToggleSwitch login = new();
    readonly ToggleSwitch sounds = new();
    readonly ComboBox languages = new() { MinWidth = 180 };
    bool refreshing;

    public GeneralPage()
    {
        Add(Ui.Title(L("General")));

        // About: the icon, the name and what Swivel is.
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        titles.Children.Add(Ui.Text("Swivel", 20, FontWeights.SemiBold));
        titles.Children.Add(Ui.Text(L("A bundle of small tools for Windows. Turn on the ones you want."), tone: Tone.Secondary));
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        titles.Children.Add(Ui.Text(version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}", 12, tone: Tone.Tertiary));
        var about = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0), Children = { AppIcon.Element(48), titles } };
        Add(about);

        Add(Ui.Section(L("Startup")));
        login.Toggled += on =>
        {
            try
            {
                LoginItem.IsEnabled = on;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Swivel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Dispatcher.Later(Refresh);
        };
        Add(new SettingsCard("", L("Open Swivel at login"), L("Starts in the notification area when you sign in to Windows."), login));

        // Haptics are not offered: Windows has no public trackpad haptics API, so feedback is sound only.
        Add(Ui.Section(L("Feedback")));
        sounds.Toggled += on =>
        {
            Prefs.SoundEffects = on;
            // Let the user hear what they turned on.
            if (on)
            {
                Feedback.Toggle(true);
                Dispatcher.After(0.45, Feedback.Done);
            }
        };
        Add(new SettingsCard("", L("Sound effects"), L("Short sounds when a menu opens, an option is chosen or a task finishes."), sounds));

        // The Mac's "Personalize: Feel & Text…" row; on Windows a card that opens the page, like Settings' links.
        Add(Ui.Section(L("Personalize")));
        Add(new SettingsCard(SettingsView.Glyph(SettingsView.Page.Feel), L("Feel & Text"),
            L("Too bouncy, too slow or too noisy? Make Swivel comfortable for you. Changes are saved as you go."),
            onClick: () => (Window.GetWindow(this) as SettingsWindow)?.Select(AppTab.Feel)));

        Add(Ui.Section(L("Language")));
        foreach (var language in Languages.All) languages.Items.Add(language.Title());
        languages.SelectionChanged += (_, _) =>
        {
            if (refreshing || languages.SelectedIndex < 0) return;
            var chosen = Languages.All[languages.SelectedIndex];
            if (chosen == Languages.Chosen) return;
            Prefs.LanguageId = chosen.Id();
            // Rebuild everything that shows text: this window (the tray menu follows Prefs.Changed).
            Dispatcher.Later(() =>
            {
                if (Window.GetWindow(this) is SettingsWindow window) window.Rebuild(AppTab.General);
                else AppWindow.Shared.ReloadForLanguage();
            });
        };
        Add(new SettingsCard("", L("Language"), L("The language of Swivel's menus and windows."), languages));

        Add(Ui.Section(Feature.Wheel.Title()));
        var steps = Ui.Text(L("1.  Start dragging any file in File Explorer and keep holding the mouse button.\n2.  Tap a key once — the menu opens under the pointer.\n3.  Drop the file on an option. Results are saved next to the original.\nTap the same key again for the next page. Drop on empty space to cancel."), tone: Tone.Secondary);
        steps.LineHeight = 22;
        Add(new SettingsCard(Feature.Wheel.Glyph(), null, null, null) { Child = Steps(steps) });

        var buttons = Ui.Row(
            Ui.Button(L("Show Shelf"), () =>
            {
                if (Feature.Shelf.IsOn()) Shelf.Shared.Show();
                else if (Window.GetWindow(this) is SettingsWindow window) window.Select(AppTab.Shelf);
            }),
            Ui.Button(L("Show Clothesline"), () => Clothesline.Shared.Reveal()));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        Add(buttons);
        Refresh();
    }

    static Grid Steps(TextBlock steps)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Glyph(Feature.Wheel.Glyph(), 20);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(2, 2, 18, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(steps, 1);
        grid.Children.Add(steps);
        return grid;
    }

    internal ComboBox LanguagePicker => languages;
    internal ToggleSwitch Sounds => sounds;

    public override void Refresh()
    {
        refreshing = true;
        bool loginOn;
        try { loginOn = LoginItem.IsEnabled; } catch (Exception) { loginOn = false; }
        login.SetQuietly(loginOn);
        sounds.SetQuietly(Prefs.SoundEffects);
        languages.SelectedIndex = Array.IndexOf(Languages.All, Languages.Chosen);
        refreshing = false;
    }
}

// MARK: - Clothesline

sealed class ClotheslinePane : Pane
{
    readonly ToggleSwitch takeover = new();
    readonly ToggleSwitch copied = new();

    public ClotheslinePane()
    {
        Add(Ui.Text(L("A rope hidden along the top of the screen. Screenshots hang on it instead of piling up in a folder; take one down when you need it."), tone: Tone.Secondary));

        Add(Ui.Section(L("Screenshots")));
        takeover.Toggled += on => Prefs.ClotheslineTakeover = on;
        Add(new SettingsCard("", L("Hang New Screenshots"),
            L("New screenshots in your Screenshots folder (Print Screen, Win + Print Screen, or the Snipping Tool with automatic saving) appear as a card, ready to hang on the rope. The files stay in the Screenshots folder."),
            takeover));

        Add(Ui.Section(L("Clipboard")));
        copied.Toggled += on => Prefs.ClotheslineHangCopied = on;
        Add(new SettingsCard("", L("Hang Copied Images"), L("Also hang images you copy (for example with Win + Shift + S, or Copy in Photos)."), copied));

        Ropes = new RopeColorsSection();
        Add(Ropes);

        Add(Ui.Section(L("Gestures")));
        var gestures = new Grid();
        gestures.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        gestures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Key, string Value)[] pairs =
        [
            (L("Click"), L("Copy the image")),
            (L("Double-click"), L("Open it in its default app")),
            (L("Press and hold"), L("Preview")),
            (L("Drag into an app"), L("Send a copy — the original stays on the rope")),
            (L("Drag into a folder"), L("Move it there — it comes off the rope")),
            (L("Drag to the Recycle Bin, or click ×"), L("Throw it away")),
            (L("Move to the top edge"), L("Lower the rope")),
            ("Ctrl + Alt + T", L("Show or hide the rope")),
        ];
        for (int i = 0; i < pairs.Length; i++)
        {
            gestures.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Ui.Text(pairs[i].Key, 13, FontWeights.SemiBold, wrap: false);
            key.Margin = new Thickness(0, i == 0 ? 0 : 8, 24, 0);
            var value = Ui.Text(pairs[i].Value, 13, tone: Tone.Secondary);
            value.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            gestures.Children.Add(key);
            gestures.Children.Add(value);
        }
        Add(new SettingsCard(null, null, null) { Child = gestures, Padding = new Thickness(20, 16, 20, 16) });

        var buttons = Ui.Row(Ui.Button(L("Show Rope"), () => Clothesline.Shared.Reveal()), Ui.Button(L("Open Folder"), OpenFolder));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        Add(buttons);
        Refresh();
    }

    internal RopeColorsSection Ropes { get; }

    /// Where screenshots are saved (and copied images are put away), as the Mac app opens the rope's folder.
    static string Folder() => Clothesline.Shared.ScreenshotsFolder;

    static void OpenFolder()
    {
        var folder = Folder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Toast.Shared.Show(ex.Message, "error", tint: ToastTint.Red); }
    }

    public override void Refresh()
    {
        takeover.SetQuietly(Prefs.ClotheslineTakeover);
        copied.SetQuietly(Prefs.ClotheslineHangCopied);
    }
}

// MARK: - Text Capture, File Shelf

sealed class TextCapturePane : Pane
{
    readonly Button choose;
    readonly TextBlock offHint;

    public TextCapturePane()
    {
        Add(Ui.Text(L("From picture to text"), 20, FontWeights.SemiBold));
        Add(Ui.Text(L("Choose an image, review the recognized text, then copy it. English, Chinese and Japanese are detected automatically."), tone: Tone.Secondary), 6);
        choose = Ui.Button(L("Choose an Image…"), () =>
        {
            if (ToolUI.ChooseFiles(imagesOnly: true, multiple: false).FirstOrDefault() is { } file) TextCaptureWindow.Present(file);
        }, accent: true);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { choose } }, 16);
        Add(Ui.Text(L("You can also drop one image on Extract Text in the Wheel, or right-click a photo on the Clothesline."), tone: Tone.Secondary), 16);
        offHint = Ui.Text(L("Turn this feature on above to get started."), 12, tone: Tone.Secondary);
        Add(offHint, 10);
        Refresh();
    }

    internal Button Choose => choose;

    public override void Refresh()
    {
        bool on = Feature.TextCapture.IsOn();
        choose.IsEnabled = on;
        offHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class ShelfPane : Pane
{
    readonly Button open;
    readonly TextBlock offHint;

    public ShelfPane()
    {
        Add(Ui.Text(L("A place to gather your files"), 20, FontWeights.SemiBold));
        Add(Ui.Text(L("Drop files or folders onto the shelf. Use Ctrl-click or Shift-click to select several, then drag them out together. Press Space to preview."), tone: Tone.Secondary), 6);
        open = Ui.Button(L("Show Shelf"), () => Shelf.Shared.Show(), accent: true);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { open } }, 16);
        Add(Ui.Text(L("Removing an item only clears its reference. Original files stay where they are. This shelf lasts until you quit."), tone: Tone.Secondary), 16);
        offHint = Ui.Text(L("Turn this feature on above to get started."), 12, tone: Tone.Secondary);
        Add(offHint, 10);
        Refresh();
    }

    public override void Refresh()
    {
        bool on = Feature.Shelf.IsOn();
        open.IsEnabled = on;
        offHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}

// MARK: - Image Presets

sealed class ImagePresetsPane : Pane
{
    readonly ListBox table = new() { Height = 268, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
    readonly Button editButton, removeButton, applyButton;
    readonly ProgressBar progress = new() { Width = 80, Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    readonly Stack<Action> undo = new();
    List<ImagePreset> presets = [];
    bool working;

    public ImagePresetsPane()
    {
        Add(Ui.Text(L("Your everyday image recipes"), 20, FontWeights.SemiBold));
        Add(Ui.Text(L("Save a size, format and metadata preference. Apply a preset to several images, or find it in the Wheel’s Tools menu."), tone: Tone.Secondary), 6);

        table.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        table.SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(table, Feature.ImagePresets.Title());
        table.SelectionChanged += (_, _) => UpdateButtons();
        table.MouseDoubleClick += (_, e) =>
        {
            if (ItemUnder(e.OriginalSource as DependencyObject) != null) EditPreset();
        };
        table.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { RemovePreset(); e.Handled = true; }
            else if (e.Key == Key.Enter) { EditPreset(); e.Handled = true; }
        };
        Add(table, 16);

        var add = Ui.Button(L("New Preset…"), () => ShowEditor(null));
        editButton = Ui.Button(L("Edit…"), EditPreset);
        removeButton = Ui.Button(L("Remove"), RemovePreset);
        applyButton = Ui.Button(L("Apply to Images…"), () => _ = ApplyPreset(), accent: true);
        // Room for "Processing…" so the button doesn't change size while it works.
        applyButton.MinWidth = 150;
        var left = Ui.Row(add, editButton, removeButton);
        var right = Ui.Row(progress, applyButton);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        progress.Margin = new Thickness(0, 0, 12, 0);
        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(left);
        buttons.Children.Add(right);
        Add(buttons);
        Add(Ui.Text(L("Results are saved beside each original with a unique name. Original files are kept. Presets remain saved when this feature is off."), 12, tone: Tone.Secondary), 16);

        // Ctrl+Z puts back the last preset removed (or takes out the last one added), as Edit ▸ Undo does on the Mac.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) =>
        {
            if (undo.Count > 0) undo.Pop()();
        }, (_, e) =>
        {
            e.CanExecute = undo.Count > 0;
            e.Handled = true;
        }));
        Refresh();
    }

    internal ListBox Table => table;
    internal IReadOnlyList<ImagePreset> Presets => presets;
    internal Button ApplyButton => applyButton;

    ImagePreset? Selected => table.SelectedItem is ListBoxItem { Tag: string id } ? presets.FirstOrDefault(p => p.Id == id) : null;

    ListBoxItem? ItemUnder(DependencyObject? node)
    {
        for (; node != null && node != table; node = VisualTreeHelper.GetParent(node))
            if (node is ListBoxItem item) return item;
        return null;
    }

    /// Reloads only when the saved presets differ from the rows, so this pane's own animated edits aren't redrawn by
    /// the Prefs change they cause.
    public override void Refresh()
    {
        var latest = Prefs.ImagePresets;
        if (!latest.SequenceEqual(presets))
        {
            var selectedId = Selected?.Id;
            presets = latest;
            table.Items.Clear();
            foreach (var preset in presets) table.Items.Add(MakeRow(preset));
            if (presets.Count > 0) table.SelectedIndex = Math.Max(presets.FindIndex(p => p.Id == selectedId), 0);
        }
        UpdateButtons();
    }

    static ListBoxItem MakeRow(ImagePreset preset)
    {
        var name = Ui.Text(preset.DisplayName, 14, FontWeights.Medium, wrap: false);
        var summary = Ui.Text(preset.Summary, 12, tone: Tone.Secondary, wrap: false);
        summary.Margin = new Thickness(0, 2, 0, 0);
        var item = new ListBoxItem
        {
            Content = new StackPanel { Children = { name, summary }, Margin = new Thickness(4, 6, 4, 6) },
            Tag = preset.Id,
        };
        AutomationProperties.SetName(item, preset.DisplayName);
        AutomationProperties.SetHelpText(item, preset.Summary);
        return item;
    }

    void UpdateButtons()
    {
        editButton.IsEnabled = Selected != null && !working;
        removeButton.IsEnabled = Selected != null && !working;
        applyButton.IsEnabled = Selected != null && Feature.ImagePresets.IsOn() && !working;
        if (!working) applyButton.Content = L("Apply to Images…");
    }

    void EditPreset()
    {
        if (Selected is { } selected && !working) ShowEditor(selected);
    }

    void ShowEditor(ImagePreset? preset)
    {
        var form = new PresetForm(preset, Saved) { Owner = Window.GetWindow(this) };
        if (form.Owner is null) form.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        form.ShowDialog();
    }

    internal void Saved(ImagePreset value)
    {
        int index = presets.FindIndex(p => p.Id == value.Id);
        if (index >= 0)
        {
            presets[index] = value;
            var row = MakeRow(value);
            if (RowFor(value.Id) is { } old) table.Items[table.Items.IndexOf(old)] = row;
            table.SelectedItem = row;
            Prefs.ImagePresets = presets;
        }
        else Insert(value, presets.Count);
    }

    internal void RemovePreset()
    {
        if (Selected is not { } selected || working) return;
        Remove(presets.FindIndex(p => p.Id == selected.Id));
    }

    /// Fades the row out and selects its neighbour. Ctrl+Z puts it back where it was.
    void Remove(int index)
    {
        if (index < 0 || index >= presets.Count) return;
        var preset = presets[index];
        presets.RemoveAt(index);
        if (RowFor(preset.Id) is { } item)
        {
            // A ghost without an id fades out in the row's place; the list treats it as gone already.
            var ghost = new ListBoxItem { Content = item.Content, IsHitTestVisible = false, Focusable = false };
            item.Content = null;
            table.Items[table.Items.IndexOf(item)] = ghost;
            Fade(ghost, appear: false, () => table.Items.Remove(ghost));
        }
        if (presets.Count > 0)
        {
            int next = Math.Min(index, presets.Count - 1);
            table.SelectedItem = RowFor(presets[next].Id);
        }
        Prefs.ImagePresets = presets;
        undo.Push(() => Insert(preset, index));
        UpdateButtons();
    }

    /// Slides a new (or restored) preset in and selects it; Ctrl+Z removes it again.
    void Insert(ImagePreset preset, int index)
    {
        if (presets.Any(p => p.Id == preset.Id)) return;
        index = Math.Min(index, presets.Count);
        presets.Insert(index, preset);
        var row = MakeRow(preset);
        int at = 0, seen = 0;
        // Ghost rows fading out still occupy the list; count only real ones.
        for (; at < table.Items.Count && seen < index; at++)
            if (table.Items[at] is ListBoxItem { Tag: string }) seen++;
        table.Items.Insert(at, row);
        Fade(row, appear: true, null);
        table.SelectedItem = row;
        table.ScrollIntoView(row);
        Prefs.ImagePresets = presets;
        var id = preset.Id;
        undo.Push(() =>
        {
            int i = presets.FindIndex(p => p.Id == id);
            if (i >= 0) Remove(i);
        });
        UpdateButtons();
    }

    ListBoxItem? RowFor(string id) => table.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == id);

    /// Opacity critically damped, plus a short slide down for rows that arrive. Animation effects off: a plain fade
    /// is skipped too and the row just appears or goes.
    static void Fade(ListBoxItem item, bool appear, Action? done)
    {
        if (Motion.Reduced)
        {
            done?.Invoke();
            return;
        }
        var shift = new TranslateTransform(0, appear ? -8 : 0);
        item.RenderTransform = shift;
        var opacity = new Spring(appear ? 0 : 1, response: 0.18, damping: 1) { Target = appear ? 1 : 0 };
        var slide = new Spring(shift.Y, response: 0.22, damping: 0.9) { Target = 0 };
        item.Opacity = opacity.Value;
        var driver = new FrameDriver();
        driver.OnFrame = dt =>
        {
            var tuning = Prefs.Interaction.Tools;
            opacity.Step(dt, tuning);
            slide.Step(dt, tuning);
            item.Opacity = Math.Clamp(opacity.Value, 0, 1);
            shift.Y = slide.Value;
            bool moving = !opacity.IsSettled || !slide.IsSettled;
            if (!moving) done?.Invoke();
            return moving;
        };
        driver.Start();
    }

    async Task ApplyPreset()
    {
        if (Selected is not { } preset || !Feature.ImagePresets.IsOn() || working) return;
        var files = ToolUI.ChooseFiles(imagesOnly: true, multiple: true);
        if (files.Count == 0) return;
        working = true;
        UpdateButtons();
        // PresetActions.Run reports no progress, so this spins until it's done (the Mac app counts "3 of 12").
        applyButton.Content = L("Processing…");
        progress.Visibility = Visibility.Visible;
        try
        {
            var result = await PresetActions.Run(preset, files);
            if (result.Files.Count > 0) Feedback.Done();
            Toast.Shared.Show(result.Message, "ok", result.Files);
        }
        catch (Cancelled) { }
        catch (Exception ex)
        {
            Feedback.Refuse();
            var owner = Window.GetWindow(this);
            if (owner != null) MessageBox.Show(owner, ex.Message, "Swivel", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(ex.Message, "Swivel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            working = false;
            progress.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }
}

/// New Preset / Edit Preset: name, long edge, format, quality and metadata, checked with ImagePreset.FormIssueFor.
/// A refused field says why, gets its text selected for retyping, and shakes.
sealed class PresetForm : Window
{
    readonly ImagePreset original;
    readonly Action<ImagePreset> saved;
    readonly ImagePreset.PresetFormat[] formats = Enum.GetValues<ImagePreset.PresetFormat>();
    readonly TextBox name = new() { Width = 290 };
    readonly TextBox edge = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox format = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Slider quality = new() { Minimum = 1, Maximum = 100, SmallChange = 1, LargeChange = 10, Width = 210, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock qualityLabel = Ui.Text("", 14, wrap: false);
    readonly CheckBox strip = new() { Content = L("Remove metadata") };
    readonly TextBlock error = Ui.Text("", 13, tone: Tone.Critical);
    /// The round quality value the slider is resting in, if any.
    double? detent;
    bool snapping;

    public PresetForm(ImagePreset? preset, Action<ImagePreset> saved)
    {
        original = preset ?? new ImagePreset
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = L("My Preset"),
            LongEdge = 1600,
            Format = ImagePreset.PresetFormat.Jpeg,
            Quality = 80,
            StripMetadata = true,
        };
        this.saved = saved;
        Title = L(preset == null ? "New Preset" : "Edit Preset");
        if (Application.Current is null || Application.Current.ThemeMode == ThemeMode.None) ThemeMode = ThemeMode.System;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        name.Text = original.DisplayName;
        edge.Text = original.LongEdge.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutomationProperties.SetName(name, L("Name"));
        AutomationProperties.SetName(edge, L("Long edge (px)"));
        AutomationProperties.SetName(quality, L("Quality"));
        foreach (var f in formats) format.Items.Add(ImagePreset.FormatTitle(f));
        format.SelectedIndex = Math.Max(Array.IndexOf(formats, original.Format), 0);
        quality.Value = original.Quality;
        strip.IsChecked = original.StripMetadata;
        name.TextChanged += (_, _) => FollowTyping();
        edge.TextChanged += (_, _) => FollowTyping();
        format.SelectionChanged += (_, _) => Changed();
        quality.ValueChanged += (_, _) => Changed(fromSlider: true);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qualityLabel.Width = 52;
        qualityLabel.Margin = new Thickness(10, 0, 0, 0);
        qualityLabel.VerticalAlignment = VerticalAlignment.Center;
        var qualityRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { quality, qualityLabel } };
        UIElement?[][] rows =
        [
            [Label("Name"), name], [Label("Long edge (px)"), edge], [Label("Format"), format], [Label("Quality"), qualityRow], [null, strip],
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < 2; c++)
            {
                if (rows[i][c] is not FrameworkElement cell) continue;
                cell.Margin = new Thickness(c == 0 ? 0 : 16, i == 0 ? 0 : 12, 0, 0);
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cell, i);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        var hint = Ui.Text(L("Use 0 for original size, or 64–16384 px. Small images are never enlarged. JPG uses a white background for transparency."), 12, tone: Tone.Secondary);
        hint.MaxWidth = 440;
        hint.Margin = new Thickness(0, 16, 0, 0);
        error.MaxWidth = 440;
        error.Margin = new Thickness(0, 8, 0, 0);
        var cancel = new Button { Content = L("Cancel"), IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        var save = new Button { Content = L("Save"), IsDefault = true, MinWidth = 96 };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        save.Click += (_, _) => TrySave();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Children = { save, cancel } };
        Content = new StackPanel { Margin = new Thickness(24), Children = { grid, hint, error, buttons } };
        Loaded += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        Changed();
    }

    static TextBlock Label(string text)
    {
        var label = Ui.Text(L(text), 14, wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        return label;
    }

    internal TextBox NameBox => name;
    internal TextBox EdgeBox => edge;
    internal ComboBox FormatBox => format;
    internal Slider QualitySlider => quality;
    internal CheckBox StripBox => strip;
    internal string ErrorText => error.Text;

    void Changed(bool fromSlider = false)
    {
        // Only while dragging: arrow keys must still be able to step through 9, 10, 11.
        if (fromSlider && !snapping && Mouse.LeftButton == MouseButtonState.Pressed && quality.IsMouseCaptureWithin) SettleOnDetent();
        qualityLabel.Text = L("%d%%", (int)Math.Round(quality.Value));
        quality.IsEnabled = ImagePreset.FormatIsLossy(formats[Math.Max(format.SelectedIndex, 0)]);
    }

    /// Round qualities (10, 20 … 100) hold the thumb for most of a point either side while dragging.
    void SettleOnDetent()
    {
        double snapped = QualitySnap(quality.Value);
        if (!(snapped >= 10 && snapped % 10 == 0)) { detent = null; return; }
        snapping = true;
        quality.Value = snapped;
        snapping = false;
        if (detent != snapped) Feedback.Snap();
        detent = snapped;
    }

    /// Values within 0.75 of a multiple of ten (10…100) snap to it; anything else is left alone.
    internal static double QualitySnap(double value)
    {
        double nearest = Math.Round(value / 10) * 10;
        return nearest >= 10 && Math.Abs(value - nearest) <= 0.75 ? nearest : value;
    }

    static string Message(ImagePreset.FormIssue? issue) => issue switch
    {
        ImagePreset.FormIssue.Name => L("Give the preset a name of up to 60 characters."),
        ImagePreset.FormIssue.Size => L("Enter 0, or a size from 64 to 16384 px."),
        _ => "",
    };

    /// Once an error is showing, it follows the typing and clears as soon as the field is fixed.
    void FollowTyping()
    {
        if (error.Text.Length == 0) return;
        error.Text = Message(ImagePreset.FormIssueFor(name.Text, edge.Text));
    }

    /// Saves and closes, or points at the field that's wrong. True when saved.
    internal bool TrySave()
    {
        if (ImagePreset.FormIssueFor(name.Text, edge.Text) is { } issue)
        {
            Refuse(issue == ImagePreset.FormIssue.Name ? name : edge, Message(issue));
            return false;
        }
        var trimmed = name.Text.Trim();
        var value = original with
        {
            Name = trimmed,
            NameKey = trimmed != original.DisplayName ? null : original.NameKey,
            LongEdge = ImagePreset.LongEdgeFrom(edge.Text) ?? original.LongEdge,
            Format = formats[Math.Max(format.SelectedIndex, 0)],
            Quality = (int)Math.Round(quality.Value),
            StripMetadata = strip.IsChecked == true,
        };
        if (!value.IsValid)
        {
            Refuse(name, L("Check the preset name, size and quality."));
            return false;
        }
        saved(value);
        if (IsLoaded)
        {
            if (IsVisible && Owner != null) DialogResult = true;
            Close();
        }
        return true;
    }

    void Refuse(TextBox field, string message)
    {
        error.Text = message;
        if (!IsLoaded) return;
        field.Focus();
        field.SelectAll();
        Ui.Shake(field);
    }
}

// MARK: - Quick Actions

sealed class QuickActionsPane : Pane
{
    readonly ComboBox shortcuts = new() { MinWidth = 200 };
    readonly TextBlock availability = Ui.Text("", 13, tone: Tone.Secondary);
    readonly TextBlock availabilityIcon = Ui.Glyph("", 14, Tone.Secondary);
    readonly Button open;
    bool refreshing;

    public QuickActionsPane()
    {
        Add(Ui.Text(L("A shortcut to everything"), 20, FontWeights.SemiBold));
        Add(Ui.Text(L("Search enabled tools, choose files and press Enter. The panel also accepts files dragged from File Explorer."), tone: Tone.Secondary), 6);
        foreach (var shortcut in QuickShortcuts.All) shortcuts.Items.Add(shortcut.Title());
        shortcuts.SelectionChanged += (_, _) =>
        {
            if (refreshing || shortcuts.SelectedIndex < 0) return;
            Prefs.QuickShortcut = QuickShortcuts.All[shortcuts.SelectedIndex];
        };
        var card = new SettingsCard("", L("Shortcut"), null, shortcuts) { Margin = new Thickness(0, 16, 0, 0) };
        Add(card);
        availabilityIcon.Margin = new Thickness(2, 0, 8, 0);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { availabilityIcon, availability } };
        Add(status);
        open = Ui.Button(L("Show Quick Actions"), () => QuickActions.Shared.Show(), accent: true);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { open } }, 16);
        Add(Ui.Text(L("If another app uses the same shortcut, choose a different one. You can always reopen Swivel from the Start menu or the notification area to reach Settings."), 12, tone: Tone.Secondary), 16);
        // The hotkey registers after Prefs change (AppHost syncs it); its result arrives through ShortcutChanged.
        void registered() => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) =>
        {
            QuickActions.Shared.ShortcutChanged -= registered;
            QuickActions.Shared.ShortcutChanged += registered;
        };
        Unloaded += (_, _) => QuickActions.Shared.ShortcutChanged -= registered;
        Refresh();
    }

    internal TextBlock Availability => availability;
    internal ComboBox Shortcuts => shortcuts;


    public override void Refresh()
    {
        refreshing = true;
        shortcuts.SelectedIndex = Array.IndexOf(QuickShortcuts.All, Prefs.QuickShortcut);
        refreshing = false;
        bool on = Feature.QuickActions.IsOn();
        open.IsEnabled = on;
        if (!on)
        {
            availability.Text = L("Turn this feature on above to get started.");
            availabilityIcon.Text = ""; // Info
            Ui.SetTone(availability, Tone.Secondary);
            Ui.SetTone(availabilityIcon, Tone.Secondary);
        }
        else if (!QuickActions.Shared.ShortcutAvailable)
        {
            availability.Text = L("This shortcut is unavailable. Choose another shortcut or open the panel below.");
            availabilityIcon.Text = ""; // Warning
            Ui.SetTone(availability, Tone.Caution);
            Ui.SetTone(availabilityIcon, Tone.Caution);
        }
        else
        {
            availability.Text = L("Shortcut is ready");
            availabilityIcon.Text = ""; // CheckMark
            Ui.SetTone(availability, Tone.Secondary);
            Ui.SetTone(availabilityIcon, Tone.Success);
        }
    }
}
