using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class FloatingShelf : Window
{
    private readonly App app;
    private ListBox list = null!;
    private TextBlock status = null!;
    public string[] SelectedPaths => list.SelectedItems.Cast<string>().ToArray();
    public FloatingShelf(App app)
    {
        this.app = app; Width = 620; Height = 620; MinWidth = 500; MinHeight = 560;
        Topmost = true; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PreviewKeyDown += (_,e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Refresh();
    }
    private string[] Chosen() => list.SelectedItems.Count == 0 ? app.Shelf.ToArray() : SelectedPaths;
    public void RestoreSelection(IEnumerable<string> paths)
    {
        list.SelectedItems.Clear(); foreach (string path in paths) if (app.Shelf.Contains(path)) list.SelectedItems.Add(path);
    }
    public void Refresh()
    {
        string[] selection = list is null ? Array.Empty<string>() : SelectedPaths;
        string previousStatus = status?.Text ?? "";
        status = UI.Text(previousStatus);
        Title = app.L("File Shelf"); var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); top.Children.Add(UI.Text(app.L("Drop files or folders here. Online-only items stay online. References last until Swivel quits.")));
        top.Children.Add(UI.Text(app.L("Actions use selected files, or all listed files when none are selected."),12));
        var topmost = new CheckBox { Content = app.L("Stay on top"), IsChecked = Topmost, Margin = new Thickness(0,4,0,10) }; topmost.Click += (_,_) => Topmost = topmost.IsChecked == true; top.Children.Add(topmost);
        var buttons = new WrapPanel(); top.Children.Add(buttons);
        buttons.Children.Add(UI.Button(app.L("Choose Files…"), () => app.Try(() => app.AddShelf(UI.Pick(this)))));
        buttons.Children.Add(UI.Button(app.L("Choose Folders…"), () => app.Try(() => app.AddShelf(UI.PickShelfFolders(this)))));
        buttons.Children.Add(UI.Button(app.L("Remove Selected"), () => { foreach (string path in SelectedPaths) app.Shelf.Remove(path); }));
        buttons.Children.Add(UI.Button(app.L("Clear"), () => app.Shelf.Clear()));
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var footer = new StackPanel(); var actions = new WrapPanel(); footer.Children.Add(actions);
        void Action(string id,string title) => actions.Children.Add(UI.Button(app.L(title),async () => await app.RunActionAsync(new(id,title),Chosen(),this,text => status.Text = text)));
        Action("file.copy","Copy Files"); Action("file.paths","Copy Path"); Action("file.reveal","Show in Explorer");
        actions.Children.Add(UI.Button(app.L("Preview File"), async () => await app.PreviewShelfFileAsync(Chosen(), this, text => status.Text = text)));
        actions.Children.Add(UI.Button(app.L("System Preview…"), async () => await app.PreviewShelfSystemAsync(Chosen(), this, text => status.Text = text)));
        if (app.Prefs.IsOn(Feature.Wheel)) actions.Children.Add(UI.Button(app.L("Open Wheel"),() => app.OpenPanel(true,Chosen())));
        if (app.Prefs.IsOn(Feature.QuickActions)) actions.Children.Add(UI.Button(app.L("Quick Actions"),() => app.OpenPanel(false,Chosen())));
        if (app.Prefs.IsOn(Feature.MyWheel)) actions.Children.Add(UI.Button(app.L("My Wheel"),() => app.OpenPanel(true,Chosen(),true)));
        actions.Children.Add(UI.Button(app.L("Cancel Operation"),app.CancelWork)); footer.Children.Add(status);
        DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        list = UI.ShelfList(app); list.MaxHeight = double.PositiveInfinity; root.Children.Add(list); UI.CopyDrag(list,app,shelfReferences: true); UI.DropFiles(root,app,app.AddShelf,shelfReferences: true);
        Content = root; RestoreSelection(selection);
    }
}

internal sealed class ImagePreviewDialog : Window
{
    public ImagePreviewDialog(App app,BitmapSource bitmap)
    {
        Title = app.L("Preview Image"); Width = 900; Height = 720; MinWidth = 450; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) }; var footer = new StackPanel();
        footer.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels. Preview does not create or change files."),bitmap.PixelWidth,bitmap.PixelHeight)));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Copy Image"),() => app.Try(() => Clipboard.SetImage(bitmap))));
        var close = UI.Button(app.L("Close"),() => DialogResult = false); close.IsCancel = true; row.Children.Add(close); footer.Children.Add(row);
        DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer); root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform }); Content = root;
    }
}
