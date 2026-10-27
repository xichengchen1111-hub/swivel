using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class CollageDialog:Window
{
    public CollageSettings Settings {get;private set;}=new();
    public CollageDialog(App app,int count)
    {
        Title=app.L("Collage…");Width=480;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(UI.Text(app.L("Arrange images in their displayed order. The new PNG will be saved beside the first image.")));
        TextBox Field(string label,string value){panel.Children.Add(UI.Text(app.L(label)));var box=new TextBox{Text=value,MinHeight=28};AutomationProperties.SetName(box,app.L(label));panel.Children.Add(box);return box;}
        var width=Field("Width (px)","2400");var height=Field("Height (px)","1800");var gap=Field("Gap (px)","24");
        panel.Children.Add(UI.Text(app.L("Layout")));
        var style=new ComboBox{ItemsSource=new[]{app.L("Grid"),app.L("Row"),app.L("Column"),app.L("Featured")},SelectedIndex=0};panel.Children.Add(style);AutomationProperties.SetName(style,app.L("Layout"));
        panel.Children.Add(UI.Text(app.L("Featured gives the first image the large area on the left."),12));
        var fill=new CheckBox{Content=app.L("Fill each cell (crop image edges)"),Margin=new Thickness(0,10,0,10)};panel.Children.Add(fill);
        var dark=new CheckBox{Content=app.L("Black background"),Margin=new Thickness(0,0,0,10)};panel.Children.Add(dark);
        var error=UI.Text("");panel.Children.Add(error);var row=new WrapPanel();
        row.Children.Add(UI.Button(app.L("Preview Collage"),()=>{
            try{if(!int.TryParse(width.Text,out int w)||!int.TryParse(height.Text,out int h)||!int.TryParse(gap.Text,out int g))throw new UserError("Collage dimensions must be 256–4000 pixels, with a gap of 0–100 pixels.");
                Settings=new(w,h,g,(CollageStyle)style.SelectedIndex,fill.IsChecked==true,dark.IsChecked==true?0u:0xffffffu);Settings.Layout(count);DialogResult=true;
            }catch(UserError ex){error.Text=app.L(ex.Message);}
        }));
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;row.Children.Add(cancel);panel.Children.Add(row);Content=UI.Scroll(panel);
    }
}
internal sealed class CollagePreview:Window
{
    public CollagePreview(App app,BitmapSource bitmap)
    {
        Title=app.L("Preview Collage");Width=900;Height=700;MinWidth=500;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(18)};var footer=new StackPanel();
        footer.Children.Add(UI.Text(string.Format(app.L("Output: {0} × {1} pixels. Save creates a new PNG beside the first image."),bitmap.PixelWidth,bitmap.PixelHeight)));
        var row=new WrapPanel();row.Children.Add(UI.Button(app.L("Save Collage"),()=>DialogResult=true));var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;row.Children.Add(cancel);
        footer.Children.Add(row);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(new Image{Source=bitmap,Stretch=Stretch.Uniform});Content=root;
    }
}
