using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Swivel.Windows;

/// Chooses 2× or 4× for Upscale… and states what the model does and costs.
internal sealed class UpscaleDialog : Window
{
    public int Factor { get; private set; } = 2;

    public UpscaleDialog(App app, IReadOnlyList<string> files)
    {
        Title = app.L("Upscale…"); Width = 640; Height = 560; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var two = new RadioButton { Content = app.L("2× (twice the width and height)"), IsChecked = true, GroupName = "factor", Margin = new Thickness(0, 4, 0, 4) };
        var four = new RadioButton { Content = app.L("4× (four times the width and height)"), GroupName = "factor", Margin = new Thickness(0, 4, 0, 8) };
        var buttons = new WrapPanel();
        var start = UI.Button(app.L("Upscale"), () => { Factor = four.IsChecked == true ? 4 : 2; DialogResult = true; }); start.IsDefault = true;
        buttons.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Upscale…"), 22));
        var group = new StackPanel(); AutomationProperties.SetName(group, app.L("Size"));
        group.Children.Add(two); group.Children.Add(four); body.Children.Add(group);
        body.Children.Add(UI.Text(app.L("Uses the Real-ESRGAN model on this PC (no upload). It sharpens and can invent small textures that were not in the original, so check faces, text and fine patterns.")));
        body.Children.Add(UI.Text(app.L("The image is processed in 128 × 128 blocks. Without a graphics card each block can take several seconds, so large images may take many minutes. Cancel keeps finished files.")));
        body.Children.Add(UI.Text(app.L("Up to 16 megapixels in; the result stays within 64 megapixels and 16384 pixels per side (so 4× takes images up to 4 megapixels).")));
        body.Children.Add(UI.Text(app.L("JPEG stays JPEG (quality 95); other formats are saved as PNG. Transparency is kept. Saved as 8-bit SDR without source metadata or color profiles.")));
        body.Children.Add(UI.Text(app.L("New files are saved beside each original without replacing existing files. Originals stay unchanged. The batch stops at the first error; completed files are kept.")));
        foreach (string file in files) body.Children.Add(UI.Text(file, 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => two.Focus();
    }
}
