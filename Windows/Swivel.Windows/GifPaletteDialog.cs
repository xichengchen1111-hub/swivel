using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal static class GifPaletteNotices
{
    internal const string Scope="Explicit lossy color reduction for one supported static SDR image. Choose a maximum palette size, including one transparent entry when needed, then Generate GIF Preview before saving. The complete original-size normalized pixels are processed; the saved GIF uses exactly the prepared result. Images already within the chosen limit keep their opaque RGB values.";
    internal const string Algorithm="Color reduction can change colors and create bands in gradients. Optional dithering can soften these bands but may add grain; compare the previews before saving. Transparency is kept. Images already within the color limit stay unchanged. The actual palette may contain fewer entries than the limit. Changed-pixel counts include only opaque normalized RGB pixels.";
    internal const string Limits="Partial transparency is refused, not flattened. Fully transparent pixels keep transparency and their hidden RGB becomes zero. Existing supported-image limits apply: 16 MP, 16,384 pixels per side and a 128 MiB source-file budget, not a total memory limit. Animation, HDR, high bit depth, CMYK and other unsupported inputs remain refused. GIF does not save metadata or color profiles. Existing direct GIF conversion and GIF presets still refuse excess colors and do not quantize.";
    internal const string Preview="Previews are reduced to at most 960 pixels per side using nearest-neighbor sampling and may hide detail or banding. The full-size prepared result is saved beside the original as a new GIF without replacing any file. No network, clipboard access or persistent image history is used.";
    internal const string Downgrade="If this action is saved in My Wheel, Windows 0.46 and earlier may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading.";
}
internal sealed class GifPaletteDialog : Window
{
    internal GifPaletteDocument.Preparation? Selection{get;private set;}
    private Task pending=Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private bool closed;
    private int revision;
    internal GifPaletteDialog(App app,GifPaletteDocument document)
    {
        Title=app.L("Reduce Colors to GIF…");Width=880;Height=800;MinWidth=460;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var status=UI.Text(app.L("Generate a GIF preview before saving."));footer.Children.Add(status);var buttons=new WrapPanel();footer.Children.Add(buttons);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Source (JSON path)")));var path=new TextBox{Text=JsonSerializer.Serialize(document.Path),IsReadOnly=true,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(path,app.L("Source (JSON path)"));body.Children.Add(path);
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels"),document.Width,document.Height)));
        body.Children.Add(UI.Text(app.L(GifPaletteNotices.Scope),12));body.Children.Add(UI.Text(app.L("Maximum Palette Entries")));
        var limit=new ComboBox{ItemsSource=new[]{16,32,64,128,256},SelectedIndex=4,MinHeight=32,Margin=new Thickness(0,4,0,8)};AutomationProperties.SetName(limit,app.L("Maximum Palette Entries"));body.Children.Add(limit);
        var dither=new CheckBox{Content=app.L("Dither gradients (Floyd–Steinberg)"),IsChecked=false,Margin=new Thickness(0,4,0,12)};AutomationProperties.SetName(dither,app.L("Dither gradients (Floyd–Steinberg)"));body.Children.Add(dither);
        var tabs=new TabControl{Height=380};body.Children.Add(tabs);
        var original=new Image{Stretch=Stretch.Uniform};var reduced=new Image{Stretch=Stretch.Uniform};RenderOptions.SetBitmapScalingMode(original,BitmapScalingMode.NearestNeighbor);RenderOptions.SetBitmapScalingMode(reduced,BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(original,app.L("Original Image Preview"));AutomationProperties.SetName(reduced,app.L("Reduced Colors Preview"));
        tabs.Items.Add(new TabItem{Header=app.L("Original Image Preview"),Content=original});tabs.Items.Add(new TabItem{Header=app.L("Reduced Colors Preview"),Content=reduced});
        foreach(string note in new[]{GifPaletteNotices.Algorithm,GifPaletteNotices.Limits,GifPaletteNotices.Preview,GifPaletteNotices.Downgrade})body.Children.Add(UI.Text(app.L(note),12));
        GifPaletteDocument.Preparation? prepared=null;
        var generate=UI.Button(app.L("Generate GIF Preview"),()=>{});buttons.Children.Add(generate);
        var save=UI.Button(app.L("Save GIF Copy"),()=>{if(prepared is null)return;Selection=prepared;DialogResult=true;});save.IsEnabled=false;buttons.Children.Add(save);
        void Invalidate(){revision++;cancellation?.Cancel();prepared=null;save.IsEnabled=false;reduced.Source=null;status.Text=app.L("Generate a GIF preview before saving.");}
        buttons.Children.Add(UI.Button(app.L("Stop"),Invalidate));var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        limit.SelectionChanged+=(_,_)=>Invalidate();dither.Checked+=(_,_)=>Invalidate();dither.Unchecked+=(_,_)=>Invalidate();generate.Click+=(_,_)=>{if(pending.IsCompleted)pending=Render();};
        Closed+=(_,_)=>{closed=true;revision++;cancellation?.Cancel();prepared=null;save.IsEnabled=false;generate.IsEnabled=false;};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
        async Task Render()
        {
            Invalidate();if(limit.SelectedItem is not int maximum){status.Text=app.L(GifPalette.Invalid);return;}GifDither mode=dither.IsChecked==true?GifDither.FloydSteinberg:GifDither.None;int request=revision;var owned=cancellation=new CancellationTokenSource();var token=owned.Token;
            generate.IsEnabled=false;status.Text=app.L("Preparing full-size GIF colors…");
            try
            {
                var result=await Task.Run(()=>{var p=document.Prepare(maximum,mode,token);return(Prepared:p,Before:document.OriginalPreview(token),After:p.Result.Preview(960,token));},token);
                token.ThrowIfCancellationRequested();if(closed||request!=revision)return;
                original.Source=ToBitmap(result.Before);reduced.Source=ToBitmap(result.After);prepared=result.Prepared;
                status.Text=string.Format(app.L("Output palette: {0} / {1} entries · changed opaque pixels: {2:N0}"),prepared.Result.Colors,prepared.Result.Limit,prepared.Result.ChangedOpaquePixels);tabs.SelectedIndex=1;save.IsEnabled=true;
            }
            catch(OperationCanceledException){if(!closed&&request==revision)status.Text=app.L("Cancelled.");}
            catch(Exception ex){if(!closed&&request==revision)status.Text=ex is UserError error?app.L(error.Message):app.L("The GIF preview could not finish. Try a smaller image.");}
            finally{if(ReferenceEquals(cancellation,owned))cancellation=null;owned.Dispose();if(!closed)generate.IsEnabled=true;}
        }
    }
    internal async Task FinishAsync(){cancellation?.Cancel();await pending;}
    private static BitmapSource ToBitmap(PixelImage pixels){var b=BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4);b.Freeze();return b;}
}
