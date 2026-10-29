using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ConversionDialog : Window
{
    public ConversionDialog(App app, ToolAction action, IReadOnlyList<string> files, string format)
    {
        Title = app.L("About This Conversion"); Width = 640; Height = 520; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        buttons.Children.Add(UI.Button(app.L("Continue"), () => DialogResult = true));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(UI.Text(Catalog.DisplayTitle(action, app.Prefs.Language), 22));
        body.Children.Add(UI.Text(string.Format(app.L("Output: {0} · {1} file(s)"), format.ToUpperInvariant(), files.Count)));
        body.Children.Add(UI.Text(app.L("Images are saved as 8-bit SDR without source metadata or embedded color profiles. This is not an archival copy.")));
        if (format == "gif") body.Children.Add(UI.Text(app.L(GifExport.Notice)));
        if (format == "gif" && action.Id.StartsWith("preset.", System.StringComparison.Ordinal)) body.Children.Add(UI.Text(app.L(ImageRecipe.GifNotice)));
        if (format == "ico") body.Children.Add(UI.Text(app.L(IcoFile.Notice)));
        if (format == "tga") body.Children.Add(UI.Text(app.L(TgaImage.Notice)));
        if (format == "psd") body.Children.Add(UI.Text(app.L(PsdImage.Notice)));
        if (files.Any(f => System.IO.Path.GetExtension(f).Equals(".psd", System.StringComparison.OrdinalIgnoreCase))) body.Children.Add(UI.Text(app.L(PsdImage.ReadNotice)));
        if (files.Any(f => System.IO.Path.GetExtension(f).Equals(".webp", System.StringComparison.OrdinalIgnoreCase))) body.Children.Add(UI.Text(app.L(WebPImage.ReadNotice)));
        if (format == "jpg") body.Children.Add(UI.Text(app.L("JPEG uses lossy compression. Fine detail may change, even at high quality.")));
        if (format is "jpg" or "bmp") body.Children.Add(UI.Text(app.L("Transparent areas become white in this output format.")));
        body.Children.Add(UI.Text(app.L("New files are saved beside each original without replacing existing files. Originals stay unchanged. The batch stops at the first error; completed files are kept.")));
        foreach (string file in files) body.Children.Add(UI.Text(file, 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
    }
}

internal sealed class BatchDetailsDialog : Window
{
    public BatchDetailsDialog(App app, string details)
    {
        Title = app.L("Last Batch Details…"); Width = 720; Height = 520; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var close = UI.Button(app.L("Close"), Close); close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var text = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
        AutomationProperties.SetName(text, app.L("Last Batch Details…"));
        root.Children.Add(text); Content = root;
    }
}
