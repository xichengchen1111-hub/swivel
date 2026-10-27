using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ImagePdfDialog : Window
{
    private readonly ObservableCollection<PdfSource> pages;
    public int[] Order => pages.Select(x => x.Index).ToArray();
    public ImagePdfSettings Settings { get; private set; } = new();
    public ImagePdfDialog(App app,ImagePdfWork work)
    {
        pages = new(work.Pages); Title = app.L("Images to PDF…"); Width = 960; Height = 760; MinWidth = 660; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var top = new StackPanel();
        top.Children.Add(UI.Text(app.L("One image per page. Reorder the list, choose a page and review its layout. Save creates a new PDF beside the first page's source.")));
        top.Children.Add(UI.Text(app.L("Images remain raster images; transparency becomes white. This creates a new image-only PDF, without searchable text, original metadata or embedded files."),12));
        var options = new WrapPanel(); top.Children.Add(options);
        options.Children.Add(UI.Text(app.L("Page Size")));
        var paper = new ComboBox { ItemsSource = new[] { app.L("Image size at 96 DPI"),"A4","Letter" },SelectedIndex = 1,MinWidth = 170,Margin = new Thickness(8) }; options.Children.Add(paper); System.Windows.Automation.AutomationProperties.SetName(paper,app.L("Page Size"));
        options.Children.Add(UI.Text(app.L("Margin (pt)"))); var margin = new TextBox { Text = "36",Width = 65,Margin = new Thickness(8),VerticalContentAlignment = VerticalAlignment.Center }; options.Children.Add(margin); System.Windows.Automation.AutomationProperties.SetName(margin,app.L("Margin (pt)"));
        var landscape = new CheckBox { Content = app.L("Landscape for wide images"),IsChecked = true,VerticalAlignment = VerticalAlignment.Center }; options.Children.Add(landscape);
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel(); var error = UI.Text(""); bottom.Children.Add(error); var buttons = new WrapPanel(); bottom.Children.Add(buttons);
        var save = UI.Button(app.L("Save PDF"),() => DialogResult = true); buttons.Children.Add(save); var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel { Margin = new Thickness(0,0,16,0) }; var reorder = new WrapPanel(); DockPanel.SetDock(reorder,Dock.Bottom); left.Children.Add(reorder);
        var list = new ListBox { ItemsSource = pages,DisplayMemberPath = "Path",HorizontalContentAlignment = HorizontalAlignment.Stretch }; left.Children.Add(list); grid.Children.Add(left);
        System.Windows.Automation.AutomationProperties.SetName(list,app.L("PDF page order"));
        void Move(int change) { int i = list.SelectedIndex,j = i+change; if (i < 0 || j < 0 || j >= pages.Count) return; pages.Move(i,j); list.SelectedIndex = j; }
        reorder.Children.Add(UI.Button(app.L("Move Up"),() => Move(-1))); reorder.Children.Add(UI.Button(app.L("Move Down"),() => Move(1)));
        var display = new Viewbox { Stretch = Stretch.Uniform,StretchDirection = StretchDirection.DownOnly,Margin = new Thickness(8) }; Grid.SetColumn(display,1); grid.Children.Add(display); root.Children.Add(grid);
        void Preview()
        {
            try
            {
                if (list.SelectedItem is not PdfSource source) return;
                if (!int.TryParse(margin.Text,NumberStyles.Integer,CultureInfo.CurrentCulture,out int m)) throw new UserError("Choose a PDF margin from 0 to 72 points.");
                var settings = new ImagePdfSettings((PdfPaper)paper.SelectedIndex,m,landscape.IsChecked == true); var layout = settings.Layout(source.Size.Width,source.Size.Height); Settings = settings;
                var canvas = new Canvas { Width = layout.PageWidth,Height = layout.PageHeight,Background = Brushes.White };
                var image = new Image { Source = source.Thumbnail,Width = layout.Width,Height = layout.Height,Stretch = Stretch.Fill };
                Canvas.SetLeft(image,layout.X); Canvas.SetTop(image,layout.PageHeight-layout.Y-layout.Height); canvas.Children.Add(image); display.Child = canvas; error.Text = ""; save.IsEnabled = true;
            }
            catch (UserError ex) { error.Text = app.L(ex.Message); save.IsEnabled = false; }
        }
        paper.SelectionChanged += (_,_) => Preview(); margin.TextChanged += (_,_) => Preview(); landscape.Click += (_,_) => Preview(); list.SelectionChanged += (_,_) => Preview(); list.SelectedIndex = 0;
        Content = root;
    }
}
