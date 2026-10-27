using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;
using Forms = System.Windows.Forms;

namespace Swivel.Windows;

/// Size, DPI, background colour and framing for one ID photo, with a live preview.
internal sealed class IdPhotoDialog : Window
{
    public IdPhotoSettings Settings { get; private set; } = new();
    private uint custom = 0xFFFFFF;

    public IdPhotoDialog(App app, IdPhotoDocument document)
    {
        Title = app.L("ID Photo…");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var error = UI.Text("", 12);
        var preview = new Image { Width = 300, Height = 420, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(preview, app.L("Preview"));

        var form = new StackPanel { MinWidth = 300 };
        ComboBox Choice(string label, string[] items, int selected)
        {
            form.Children.Add(UI.Text(app.L(label)));
            var box = new ComboBox { ItemsSource = items.Select(app.L).ToArray(), SelectedIndex = selected, MinHeight = 28 };
            AutomationProperties.SetName(box, app.L(label)); form.Children.Add(box); return box;
        }
        TextBox Field(string label, string text, Panel parent)
        {
            var box = new TextBox { Text = text, MinHeight = 28, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(box, app.L(label)); parent.Children.Add(box); return box;
        }
        Slider Slide(string label, double min, double max)
        {
            form.Children.Add(UI.Text(app.L(label)));
            var slider = new Slider { Minimum = min, Maximum = max, SmallChange = 0.01, LargeChange = 0.05 };
            AutomationProperties.SetName(slider, app.L(label)); form.Children.Add(slider); return slider;
        }
        Slider zoom = null!, vertical = null!; Button save = null!; // created below; Update runs only after Loaded
        var preset = Choice("Size", IdPhoto.PresetNames, 0);
        form.Children.Add(UI.Text(app.L("Custom size")));
        var sizeRow = new WrapPanel();
        var width = Field("Width", "295", sizeRow); var height = Field("Height", "413", sizeRow);
        var units = new ComboBox { ItemsSource = new[] { app.L("Pixels"), app.L("Millimeters") }, SelectedIndex = 0, MinHeight = 28 };
        AutomationProperties.SetName(units, app.L("Units")); sizeRow.Children.Add(units); form.Children.Add(sizeRow);
        form.Children.Add(UI.Text(app.L("DPI")));
        var dpiRow = new WrapPanel(); var dpi = Field("DPI", "300", dpiRow); form.Children.Add(dpiRow);
        var background = Choice("Background", IdPhoto.Backgrounds.Select(b => b.Name).Append("Custom").ToArray(), 0);
        var pick = UI.Button(app.L("Choose Colour…"), () =>
        {
            using var picker = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb((int)(0xff000000u | custom)) };
            if (picker.ShowDialog(new OwnerHandle(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
            custom = (uint)picker.Color.ToArgb() & 0xFFFFFF; background.SelectedIndex = IdPhoto.Backgrounds.Length; Update();
        });
        form.Children.Add(pick);
        zoom = Slide("Zoom", 0.7, 1.5); zoom.Value = 1;
        vertical = Slide("Vertical Position", -0.2, 0.2); vertical.Value = 0;
        form.Children.Add(UI.Text(app.L("Windows finds only the face's outline, so the head size and chin are estimated. Check the preview and adjust Zoom and Vertical Position."), 12));
        form.Children.Add(UI.Text(app.L("For convenience only. Follow the issuing authority's requirements for official use."), 12));
        form.Children.Add(error);

        var buttons = new WrapPanel();
        save = UI.Button(app.L("Save JPEG"), () => { if (Read() is { } settings) { Settings = settings; DialogResult = true; } });
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; save.IsDefault = true;
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(preview); body.Children.Add(form);
        root.Children.Add(UI.Scroll(body));
        Content = root;

        IdPhotoSettings? Read()
        {
            try
            {
                bool customSize = preset.SelectedIndex == 4;
                width.IsEnabled = height.IsEnabled = units.IsEnabled = customSize;
                // Pixel presets are defined at 300 DPI (One inch 295 × 413 is 25 × 35 mm there).
                bool fixedPixels = preset.SelectedIndex is 0 or 1 or 3;
                dpi.IsEnabled = !fixedPixels;
                if (fixedPixels) dpi.Text = "300";
                if (!int.TryParse(dpi.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int d)) throw new UserError("Choose a DPI from 72 to 1200 and a size from 1 to 4000 pixels per side.");
                double w = 0, h = 0;
                if (customSize && (!double.TryParse(width.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out w) || !double.TryParse(height.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out h)))
                    throw new UserError("Choose a DPI from 72 to 1200 and a size from 1 to 4000 pixels per side.");
                var (pw, ph) = IdPhoto.Dimensions(preset.SelectedIndex, w, h, units.SelectedIndex == 1, d);
                uint color = background.SelectedIndex < IdPhoto.Backgrounds.Length ? IdPhoto.Backgrounds[background.SelectedIndex].Rgb : custom;
                error.Text = "";
                return new IdPhotoSettings(pw, ph, d, color, zoom.Value, vertical.Value);
            }
            catch (UserError ex) { error.Text = app.L(ex.Message); return null; }
        }
        void Update()
        {
            if (Read() is not { } settings) { preview.Source = null; save.IsEnabled = false; return; }
            try { preview.Source = document.Preview(settings, default); save.IsEnabled = true; }
            catch (UserError ex) { error.Text = app.L(ex.Message); preview.Source = null; save.IsEnabled = false; }
        }
        preset.SelectionChanged += (_, _) => Update(); units.SelectionChanged += (_, _) => Update(); background.SelectionChanged += (_, _) => Update();
        foreach (var box in new[] { width, height, dpi }) box.TextChanged += (_, _) => Update();
        zoom.ValueChanged += (_, _) => Update(); vertical.ValueChanged += (_, _) => Update();
        Loaded += (_, _) => { Update(); preset.Focus(); };
    }

    private sealed class OwnerHandle(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle => handle; }
}
