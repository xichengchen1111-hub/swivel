using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Full review before copying: every source path and the destination folder. Move Files
/// opens it too when every file is on another drive than the destination (`crossDrive`).
internal sealed class CopyFilesDialog : Window
{
    internal const string CrossDrive = "The destination is on another drive. Swivel does not move files between drives: it copies them and keeps every original, so a stopped copy never loses a file. After checking the copies you can move the originals to the Recycle Bin yourself.";

    public CopyFilesDialog(App app, CopySession session, bool crossDrive = false)
    {
        Title = app.L(crossDrive ? "Copy Instead of Move" : "Copy to Folder…");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        CheckBox? agree = null;
        // On exFAT/FAT32 a copy of a file with the download warning loses it: only with explicit consent.
        var copy = UI.Button(app.L("Copy Files"), () => { session.AcceptZoneLoss = agree?.IsChecked == true; DialogResult = true; });
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; buttons.Children.Add(copy); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var top = new StackPanel();
        if (crossDrive) top.Children.Add(UI.Text(app.L(CrossDrive), 14));
        top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("{0} file(s), {1} MB, into:"), session.Sources.Count,
            (session.TotalBytes / 1_000_000.0).ToString("0.##", CultureInfo.CurrentCulture)), 18));
        top.Children.Add(UI.Text(session.Directory));
        top.Children.Add(UI.Text(app.L("Originals stay where they are. Each copy keeps its name; if that name is already taken, Swivel adds a number (photo 2.jpg). Nothing is overwritten."), 12));
        if (!session.IsFat)
            top.Children.Add(UI.Text(app.L("Copies keep every data stream, including the download warning (Mark of the Web), plus attributes and the modified time; the creation time is new. Permissions follow the destination folder. Each copy's stream names and sizes are checked against the original (not every byte). If copying stops, completed copies stay."), 12));
        else
        {
            top.Children.Add(UI.Text(app.L(FatRules.CopyNote) + " " + app.L(FatRules.NoCopyUndo), 12));
            if (session.ZoneMarked.Count > 0)
            {
                top.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L(FatRules.ZoneWillBeLost), session.ZoneMarked.Count), 14));
                agree = new CheckBox { Content = app.L(FatRules.ZoneConsent), Margin = new Thickness(0, 6, 0, 4) };
                copy.IsEnabled = false; agree.Checked += (_, _) => copy.IsEnabled = true; agree.Unchecked += (_, _) => copy.IsEnabled = false;
                top.Children.Add(agree);
            }
        }
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 12), ItemsSource = session.Sources.ToArray() };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(list, app.L("Files to copy"));
        root.Children.Add(list); Content = root;
        Loaded += (_, _) => cancel.Focus();
    }
}
