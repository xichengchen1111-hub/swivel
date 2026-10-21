using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Swivel;

/// The system colour picker: Win32's ChooseColor through WinForms' ColorDialog, the Windows counterpart of the Mac's
/// colour panel. It is modal, so a choice arrives at once (the Mac panel streams changes while it is open). The custom
/// colours row starts with the rope presets and the template palettes and remembers what the user adds while Swivel
/// runs. Tests replace `Chooser`.
static class ColorChooser
{
    internal static Func<FrameworkElement, Color, Color?> Chooser { get; set; } = ShowDialog;

    static int[]? custom;

    public static Color? Pick(FrameworkElement from, Color current) => Chooser(from, current);

    static Color? ShowDialog(FrameworkElement from, Color current)
    {
        custom ??= [.. RopeColorPresets.All.Select(p => p.Rgb()).OfType<RGBColor>()
            .Concat(TemplateColorPresets.All.Skip(1).Select(p => p.Palette()).OfType<TemplatePalette>().Select(p => p.Colors[0]))
            .Select(rgb => Bgr(rgb.Color))];
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            CustomColors = custom,
        };
        var handle = Window.GetWindow(from) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        var result = handle != IntPtr.Zero ? dialog.ShowDialog(new Owner(handle)) : dialog.ShowDialog();
        if (result != System.Windows.Forms.DialogResult.OK) return null;
        custom = dialog.CustomColors;
        return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
    }

    /// ChooseColor's COLORREF: 0x00BBGGRR.
    static int Bgr(Color c) => c.R | c.G << 8 | c.B << 16;

    sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }
}

/// A colour well for the settings window (NSColorWell): a button showing its colour; clicking it opens the colour picker.
sealed class PaletteWell : Button
{
    readonly Border swatch = new() { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    Color color = Colors.Gray;

    /// The user chose a colour in the picker.
    public event Action<Color>? Picked;

    public PaletteWell(string name, double width = 48, double height = 32)
    {
        Width = width;
        Height = height;
        Padding = new Thickness(5);
        MinWidth = 0;
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
        swatch.HorizontalAlignment = HorizontalAlignment.Stretch;
        swatch.VerticalAlignment = VerticalAlignment.Stretch;
        Content = swatch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, name);
        ToolTip = name;
        Click += (_, _) => Choose();
        Color = color;
    }

    public Color Color
    {
        get => color;
        set
        {
            color = value;
            swatch.Background = Ui.Brush(value);
            AutomationProperties.SetItemStatus(this, $"#{value.R:X2}{value.G:X2}{value.B:X2}");
        }
    }

    /// Opens the picker on the current colour (also when "Custom" is chosen from a list).
    internal void Choose()
    {
        if (ColorChooser.Pick(this, color) is not { } chosen) return;
        Color = chosen;
        Picked?.Invoke(chosen);
    }

    /// A small rounded swatch for a list item.
    internal static Border Swatch(Color color, double size = 12) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        Background = Ui.Brush(color),
        BorderThickness = new Thickness(1),
        BorderBrush = Ui.Brush(Color.FromArgb(0x30, 0, 0, 0)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// A list item: swatches (none for "Custom", which shows the palette glyph instead) and a title.
    internal static ComboBoxItem Item(string title, IReadOnlyList<Color>? colors)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (colors is null) row.Children.Add(new Border { Width = 12, Child = Ui.Glyph("", 12, Tone.Secondary) }); // Color
        else
            for (int i = 0; i < colors.Count; i++)
            {
                var swatch = Swatch(colors[i]);
                if (i > 0) swatch.Margin = new Thickness(2, 0, 0, 0);
                row.Children.Add(swatch);
            }
        var label = Ui.Text(title, 14, wrap: false);
        label.Margin = new Thickness(8, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var item = new ComboBoxItem { Content = row };
        AutomationProperties.SetName(item, title);
        return item;
    }
}

/// Rope and peg colours on the Clothesline page (RopeColorsPane.swift), chosen separately and shared by the real
/// clothesline and the Clothesline menu template.
sealed class RopeColorsSection : StackPanel
{
    static readonly string[] Titles = ["Copy", "Move", "Share", "Copy Path", "Terminal", "Hang"];

    readonly ComboBox rope = new() { MinWidth = 180 };
    readonly ComboBox peg = new() { MinWidth = 180 };
    readonly PaletteWell ropeWell = new(L("Custom rope color"));
    readonly PaletteWell pegWell = new(L("Custom peg color"));
    readonly PreviewFrame preview = new() { Height = 190 };
    (ClotheslineColors Colors, double Scale, bool Bold)? displayed;
    bool refreshing;

    public RopeColorsSection()
    {
        Children.Add(Ui.Section(L("Rope & peg colors")));
        var note = Ui.Text(L("Choose rope and peg colors separately. Changes apply to the clothesline and its pop-up menu template."), 12, tone: Tone.Secondary);
        note.Margin = new Thickness(1, 0, 0, 8);
        Children.Add(note);
        foreach (var (box, well, name, isRope) in new[] { (rope, ropeWell, "Rope color", true), (peg, pegWell, "Peg color", false) })
        {
            foreach (var preset in RopeColorPresets.All)
                box.Items.Add(PaletteWell.Item(preset.Title(), preset.Rgb() is { } rgb ? [rgb.Color] : null));
            AutomationProperties.SetName(box, L(name));
            box.SelectionChanged += (_, _) => Picked(isRope);
            well.Picked += color => Save(isRope, new RGBColor(color));
            well.Margin = new Thickness(8, 0, 0, 0);
        }
        Children.Add(new CardGroup(
            new SettingsCard("", L("Rope color"), null, Row(rope, ropeWell)), // Color
            new SettingsCard("", L("Peg color"), null, Row(peg, pegWell)))); // Pin
        preview.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(preview);
        var reset = Ui.Button(L("Reset rope colors"), Reset);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { reset } });
        PrefsWatch.Attach(this, Refresh);
        Refresh();
    }

    static StackPanel Row(ComboBox box, PaletteWell well) => new() { Orientation = Orientation.Horizontal, Children = { box, well } };

    internal ComboBox Rope => rope;
    internal ComboBox Peg => peg;
    internal PaletteWell RopeWell => ropeWell;
    internal PaletteWell PegWell => pegWell;
    internal PreviewFrame Preview => preview;

    public void Refresh()
    {
        refreshing = true;
        var colors = Prefs.ClotheslineColors;
        rope.SelectedIndex = Array.IndexOf(RopeColorPresets.All, RopeColorPresets.Matching(colors.Rope));
        peg.SelectedIndex = Array.IndexOf(RopeColorPresets.All, RopeColorPresets.Matching(colors.Peg));
        ropeWell.Color = colors.Rope.Color;
        pegWell.Color = colors.Peg.Color;
        refreshing = false;
        var key = (colors, AppTypography.Scale, Prefs.Interaction.BoldText);
        if (displayed != key)
        {
            bool crossfade = displayed != null;
            displayed = key;
            preview.Show(MenuPreview.Create(MenuStyle.Clothesline, Titles, WheelMode.Actions, interactive: false), crossfade);
        }
    }

    void Picked(bool isRope)
    {
        if (refreshing) return;
        var box = isRope ? rope : peg;
        if (RopeColorPresets.All[Math.Max(box.SelectedIndex, 0)].Rgb() is { } rgb)
        {
            Save(isRope, rgb);
            return;
        }
        // Custom: the picker opens once the list has closed. Cancelling it keeps the stored colour selected.
        Dispatcher.Later(() =>
        {
            (isRope ? ropeWell : pegWell).Choose();
            Refresh();
        });
    }

    static void Save(bool isRope, RGBColor rgb)
    {
        var colors = Prefs.ClotheslineColors;
        Prefs.ClotheslineColors = isRope ? colors with { Rope = rgb } : colors with { Peg = rgb };
    }

    internal void Reset()
    {
        Prefs.ClotheslineColors = new ClotheslineColors();
        Refresh();
    }
}

/// Wheel › Colors (TemplateColorsPane.swift): a palette for Bookshelf and one for Sticky Notes, each a preset or four
/// colours of the user's own, plus Bookshelf's shelf colour. Neither choosing colours nor trying the preview changes
/// which template the menu uses.
sealed class TemplateColorsPane : Pane
{
    static readonly string[] Titles = ["JPG", "PNG", "HEIC", "TIFF", "PDF", "GIF", "AVIF", "BMP"];
    const double ControlsWidth = 340, PreviewWidth = 410, PreviewHeight = 300, ColumnGap = 24;

    readonly ComboBox template = new() { MinWidth = 200 };
    readonly ComboBox palette = new() { MinWidth = 220 };
    readonly PaletteWell[] wells;
    readonly StackPanel railRow;
    readonly PreviewFrame preview = new() { Width = PreviewWidth, Height = PreviewHeight };
    (ColorTemplate Template, TemplatePalette Palette, double Scale, bool Bold)? displayed;
    bool refreshing;

    public TemplateColorsPane()
    {
        Add(Ui.Text(L("Template colors"), 20, FontWeights.SemiBold), 4);
        Add(Ui.Text(L("Choose a soft palette or pick your own colors. Bookshelf and Sticky Notes save their colors separately."), tone: Tone.Secondary), 6);

        foreach (var t in ColorTemplates.All) template.Items.Add(t.Title());
        template.SelectedIndex = 0;
        AutomationProperties.SetName(template, L("Color template"));
        template.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            Refresh();
        };
        foreach (var preset in TemplateColorPresets.All)
            palette.Items.Add(PaletteWell.Item(preset.Title(), preset.Palette()?.Colors.Select(c => c.Color).ToList()));
        AutomationProperties.SetName(palette, L("Template palette"));
        palette.SelectionChanged += (_, _) => PresetChosen();

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (row, label, box) in new[] { (0, L("Template"), template), (1, L("Palette"), palette) })
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Ui.Text(label, 14, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, row == 0 ? 0 : 10, 16, 0);
            box.Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 0);
            box.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            form.Children.Add(text);
            form.Children.Add(box);
        }

        wells = [.. Enumerable.Range(0, 5).Select(i => new PaletteWell(i == 4 ? L("Shelf color") : L("Palette color %d", i + 1)))];
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < 5; i++)
        {
            int index = i;
            wells[i].Picked += color => ColorChanged(index, color);
            if (i == 4) continue;
            var name = Ui.Text(L("Color %d", i + 1), 12, tone: Tone.Secondary, wrap: false);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 4, 0, 0);
            wells[i].HorizontalAlignment = HorizontalAlignment.Center;
            swatches.Children.Add(new StackPanel { Width = 72, Children = { wells[i], name } });
        }
        var railLabel = Ui.Text(L("Shelf color"), 14, wrap: false);
        railLabel.VerticalAlignment = VerticalAlignment.Center;
        wells[4].Margin = new Thickness(12, 0, 0, 0);
        railRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { railLabel, wells[4] } };
        var hint = Ui.Text(L("Click a swatch to choose any color. The four colors repeat across items; dark appearance adjusts them for readability."), 12, tone: Tone.Secondary);
        hint.Margin = new Thickness(0, 16, 0, 0);
        var reset = Ui.Button(L("Reset this palette"), Reset);
        var controls = new StackPanel
        {
            Width = ControlsWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { form, swatches, railRow, hint, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { reset } } },
        };

        var previewColumn = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Children = { preview, Ui.TryItHint() } };
        // Side by side when there is room; the preview goes under the controls in a narrow window.
        var split = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.Children.Add(controls);
        split.Children.Add(previewColumn);
        void layout(double width)
        {
            bool side = width >= ControlsWidth + ColumnGap + 320;
            Grid.SetColumn(previewColumn, side ? 1 : 0);
            Grid.SetRow(previewColumn, side ? 0 : 1);
            Grid.SetColumnSpan(previewColumn, side ? 1 : 2);
            previewColumn.Margin = side ? new Thickness(ColumnGap, 0, 0, 0) : new Thickness(0, 20, 0, 0);
            preview.Width = Math.Clamp(side ? width - ControlsWidth - ColumnGap : width, 300, PreviewWidth);
            preview.Height = preview.Width * PreviewHeight / PreviewWidth;
        }
        split.SizeChanged += (_, e) => layout(e.NewSize.Width);
        layout(ControlsWidth + ColumnGap + PreviewWidth);
        Add(split);
        Refresh();
    }

    internal ColorTemplate Area => ColorTemplates.All[Math.Max(template.SelectedIndex, 0)];
    internal ComboBox TemplatePicker => template;
    internal ComboBox PalettePicker => palette;
    internal IReadOnlyList<PaletteWell> Wells => wells;
    internal FrameworkElement RailRow => railRow;
    internal PreviewFrame Preview => preview;

    public override void Refresh()
    {
        refreshing = true;
        var current = Prefs.TemplateColors[Area];
        palette.SelectedIndex = Array.IndexOf(TemplateColorPresets.All, TemplateColorPresets.Matching(current));
        for (int i = 0; i < 4; i++) wells[i].Color = current.Colors[i].Color;
        wells[4].Color = current.Rail.Color;
        railRow.Visibility = Area == ColorTemplate.Shelf ? Visibility.Visible : Visibility.Collapsed;
        refreshing = false;
        var key = (Area, current, AppTypography.Scale, Prefs.Interaction.BoldText);
        if (displayed != key)
        {
            bool crossfade = displayed != null;
            displayed = key;
            preview.Show(MenuPreview.Create(Area.Style(), Titles, WheelMode.Convert), crossfade);
        }
    }

    void PresetChosen()
    {
        if (refreshing) return;
        if (TemplateColorPresets.All[Math.Max(palette.SelectedIndex, 0)].Palette() is { } chosen)
        {
            Prefs.TemplateColors = Prefs.TemplateColors.With(Area, chosen);
            return;
        }
        // Custom: start from the first colour. Cancelling the picker keeps the stored palette selected.
        Dispatcher.Later(() =>
        {
            wells[0].Choose();
            Refresh();
        });
    }

    void ColorChanged(int index, Color color)
    {
        var current = Prefs.TemplateColors[Area];
        var colors = current.Colors.ToList();
        var rail = current.Rail;
        if (index == 4) rail = new RGBColor(color);
        else colors[index] = new RGBColor(color);
        Prefs.TemplateColors = Prefs.TemplateColors.With(Area, new TemplatePalette(colors, rail));
    }

    internal void Reset()
    {
        Prefs.TemplateColors = Prefs.TemplateColors.With(Area, TemplatePalette.Native);
        Refresh();
    }
}
