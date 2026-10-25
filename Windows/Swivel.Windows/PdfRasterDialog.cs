using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PdfRasterDialog:Window
{
    private Task pending=Task.CompletedTask;private CancellationTokenSource? cancellation;private bool closed;
    public PdfRasterPage[] Plan{get;private set;}=Array.Empty<PdfRasterPage>();public PdfRasterOutput Output{get;private set;}
    internal PdfRasterDialog(App app,PdfRasterDocument document)
    {
        Title=app.L("Render PDF Pages…");Width=920;Height=860;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var buttons=new WrapPanel();footer.Children.Add(buttons);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L(PdfRasterDocument.Fidelity)));body.Children.Add(UI.Text(app.L(PdfRasterPlan.Limit),12));body.Children.Add(UI.Text(app.L("Select pages across the files below. Exports go beside the first input. Completed separate outputs are kept if a later page fails or is cancelled. Physical PDF page dimensions are retained; PNG uses the selected pixel edge."),12));
        var list=new ListView{Height=160};list.View=new GridView();foreach(var row in document.Pages)list.Items.Add(row.Number+" · "+AppMemory.Visible(row.Path)+" · "+app.L("Page")+" "+row.Page);VirtualizingPanel.SetIsVirtualizing(list,true);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("PDF Source Pages"));body.Children.Add(list);
        body.Children.Add(UI.Text(app.L(PdfRasterPlan.Selection),12));var selection=new TextBox{Text="1",MaxLength=512,Margin=new Thickness(0,6,0,8)};AutomationProperties.SetName(selection,app.L("Page Selection"));body.Children.Add(selection);
        var options=new WrapPanel();body.Children.Add(options);options.Children.Add(UI.Text(app.L("Longest Edge")));var edge=new ComboBox{Width=100,ItemsSource=new[]{800,1200,1600,2400,3200,4000},SelectedItem=1600,Margin=new Thickness(8)};AutomationProperties.SetName(edge,app.L("Longest Edge"));options.Children.Add(edge);
        options.Children.Add(UI.Text(app.L("Rotation")));var turn=new ComboBox{Width=100,ItemsSource=new[]{0,90,180,270},SelectedItem=0,Margin=new Thickness(8)};AutomationProperties.SetName(turn,app.L("Rotation"));options.Children.Add(turn);
        var format=new ComboBox{ItemsSource=new[]{app.L("One Rendered PDF"),app.L("Separate Rendered PDFs"),app.L("PNG Pages")},SelectedIndex=0,Margin=new Thickness(0,6,0,8)};AutomationProperties.SetName(format,app.L("Output Format"));body.Children.Add(format);
        var image=new Image{MaxHeight=340,Stretch=Stretch.Uniform};body.Children.Add(UI.Text(app.L("Preview shows only the first selected page at a maximum 800-pixel edge. Export uses the selected full pixel edge."),12));body.Children.Add(image);
        void Stop(){cancellation?.Cancel();}
        var preview=UI.Button(app.L("Preview First Selected Page"),()=>{});var save=UI.Button(app.L("Export Pages"),()=>{});buttons.Children.Add(preview);buttons.Children.Add(UI.Button(app.L("Stop"),Stop));buttons.Children.Add(save);var cancel=UI.Button(app.L("Cancel"),()=>{Stop();DialogResult=false;});cancel.IsCancel=true;buttons.Children.Add(cancel);
        PdfRasterPage[] Read()=>document.Plan(selection.Text,(int)edge.SelectedItem,(int)turn.SelectedItem);
        void Changed(){Stop();image.Source=null;status.Text="";}
        selection.TextChanged+=(_,_)=>Changed();edge.SelectionChanged+=(_,_)=>Changed();turn.SelectionChanged+=(_,_)=>Changed();format.SelectionChanged+=(_,_)=>Changed();
        save.Click+=(_,_)=>{try{Plan=Read();Output=(PdfRasterOutput)format.SelectedIndex;Stop();DialogResult=true;}catch(UserError error){status.Text=app.L(error.Message);}};
        preview.Click+=(_,_)=>{if(pending.IsCompleted)pending=Preview();};
        async Task Preview()
        {
            Stop();var owned=cancellation=new CancellationTokenSource();var token=owned.Token;preview.IsEnabled=save.IsEnabled=false;
            try
            {
                var plan=Read();var first=document.Plan((plan[0].Index+1).ToString(CultureInfo.InvariantCulture),800,(int)turn.SelectedItem)[0];var pixels=await Task.Run(()=>document.RenderAsync(first,token),token);token.ThrowIfCancellationRequested();if(closed)return;
                var bitmap=BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4);bitmap.Freeze();image.Source=bitmap;
            }
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception error){if(!closed)status.Text=error is UserError user?app.L(user.Message):app.L("PDF rendering failed. No output was published.");}
            finally{if(ReferenceEquals(cancellation,owned))cancellation=null;owned.Dispose();if(!closed)preview.IsEnabled=save.IsEnabled=true;}
        }
        Closed+=(_,_)=>{closed=true;Stop();};SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    public async Task FinishAsync(){cancellation?.Cancel();await pending;}
}
