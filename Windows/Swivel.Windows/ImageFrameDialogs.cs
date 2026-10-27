using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ImageFrameDialog : Window
{
    public ImageFrame Settings { get; private set; } = new();
    public ImageFrameDialog(App app, BitmapSource source)
    {
        Title = app.L("Add Background…"); Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(app.L("Place the whole image on a new canvas. This adds a background; it does not remove the existing background or enlarge the image.")));
        TextBox Field(string label, string initial) { panel.Children.Add(UI.Text(app.L(label))); var box = new TextBox { Text = initial, MinHeight = 28 }; AutomationProperties.SetName(box, app.L(label)); panel.Children.Add(box); return box; }
        var width = Field("Width (px)", "2400"); var height = Field("Height (px)", "1800"); var padding = Field("Padding (px)", "100"); var radius = Field("Corner radius (px)", "24");
        panel.Children.Add(UI.Text(app.L("Background")));
        var style = new ComboBox { ItemsSource = new[] { app.L("White"), app.L("Black"), app.L("Soft Gray"), app.L("Sky"), app.L("Rose") }, SelectedIndex = 2 }; panel.Children.Add(style); AutomationProperties.SetName(style, app.L("Background"));
        var shadow = new CheckBox { Content = app.L("Soft Shadow"), IsChecked = true, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(shadow);
        var error = UI.Text(""); panel.Children.Add(error); var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Preview"), () => { try {
            if (!int.TryParse(width.Text, out int w) || !int.TryParse(height.Text, out int h) || !int.TryParse(padding.Text, out int p) || !int.TryParse(radius.Text, out int r)) throw new UserError("Enter whole-pixel dimensions.");
            var settings = new ImageFrame(w, h, p, r, shadow.IsChecked == true, (FrameStyle)style.SelectedIndex); settings.Layout(source.PixelWidth, source.PixelHeight); Settings = settings; DialogResult = true;
        } catch (UserError ex) { error.Text = app.L(ex.Message); } }));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
internal sealed class ImageOutputDialog : Window
{
    public ImageOutputDialog(App app, BitmapSource bitmap)
    {
        Title = app.L("Preview"); Width = 900; Height = 720; MinWidth = 500; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new StackPanel();
        footer.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels. Save creates a new PNG beside the original."), bitmap.PixelWidth, bitmap.PixelHeight)));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Save PNG"), () => DialogResult = true)); var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); footer.Children.Add(row);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform }); Content = root;
    }
}
internal sealed class MetadataDialog : Window
{
    public MetadataDialog(App app, IReadOnlyList<(string Label, string Value)> fields)
    {
        Title = app.L("Inspect Metadata…"); Width = 700; Height = 600; MinWidth = 480; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) }; var footer = new StackPanel();
        footer.Children.Add(UI.Text(app.L("This lists common tags only; other metadata may exist. Save creates a new SDR PNG with embedded metadata removed and orientation applied. The original stays unchanged.")));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Save Metadata-free PNG"), () => DialogResult = true)); var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; row.Children.Add(close); footer.Children.Add(row);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); var list = new StackPanel();
        foreach (var field in fields) { list.Children.Add(UI.Text(app.L(field.Label), 17)); list.Children.Add(new TextBox { Text = field.Value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) }); }
        root.Children.Add(UI.Scroll(list)); Content = root;
    }
}
