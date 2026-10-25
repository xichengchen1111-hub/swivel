using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Full review before anything is written: every folder and file the archive will create.
internal sealed class ZipExtractDialog : Window
{
    public ZipExtractDialog(App app, ZipExtractWork work)
    {
        Title = app.L("Extract Archive…");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var extract = UI.Button(app.L("Extract"), () => DialogResult = true);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; extract.IsDefault = true;
        buttons.Children.Add(extract); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var top = new StackPanel();
        top.Children.Add(UI.Text(work.Path));
        top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} file(s) and {1} folder(s), {2} MB after extraction."),
            work.Plan.Files.Count, work.Plan.Directories.Count, (work.Plan.TotalBytes / 1_000_000.0).ToString("0.##", CultureInfo.CurrentCulture)), 18));
        top.Children.Add(UI.Text(app.L(work.Tar is null
            ? "Files are extracted into a new folder beside the archive, named after it. Existing files and folders are never overwritten. Each file is checked against its size and CRC-32 before it appears."
            : "Files are extracted into a new folder beside the archive, named after it. Existing files and folders are never overwritten. TAR stores no file checksums, so each file is checked against its size and the CRC-32 Swivel recorded while reviewing the archive; a compressed archive's own checksum is checked too."), 12));
        top.Children.Add(UI.Text(app.L(work.Zone == 4
            ? "The archive is marked as Restricted, so every extracted file is marked Restricted too."
            : "Every extracted file is marked as downloaded from the Internet (Mark of the Web), even if the archive itself was not."), 12));
        top.Children.Add(UI.Text(app.L(work.Tar is null
            ? "Modified times from the archive are kept. Permissions, attributes, comments and other data stored in the archive are not restored. If extraction stops, files already extracted stay in the new folder; it is not removed automatically."
            : "Modified times from the archive are kept. Owners, permissions, extended attributes and other data stored in the archive are not restored. If extraction stops, files already extracted stay in the new folder; it is not removed automatically."), 12));
        if (work.Plan.SkippedAppleDouble > 0)
            top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} macOS attribute file(s) (._ names) are left out."), work.Plan.SkippedAppleDouble), 12));
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var list = new ListBox { Margin = new Thickness(0, 8, 0, 12) };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(list, app.L("Paths in the new folder"));
        list.ItemsSource = work.Plan.Directories.Select(path => path + "\\")
            .Concat(work.Plan.Files.Select(file => file.Path)).ToArray();
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => cancel.Focus();
    }
}
