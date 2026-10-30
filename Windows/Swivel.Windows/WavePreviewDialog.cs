using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WavePreviewDialog:Window
{
    private Task pending=Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    internal WavePreviewDialog(App app,WaveDocument document)
    {
        Title=app.L("Preview Audio");Width=760;Height=540;MinWidth=480;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var body=new StackPanel();
        body.Children.Add(new TextBox{Text=document.Path,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var info=document.Info;
        body.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"),info.Seconds,info.Rate,info.Channels,info.Bits),18));
        body.Children.Add(new WaveEnvelope(document.Peaks));
        body.Children.Add(UI.Text(app.L("Read-only audio preview. Integer PCM WAV is read directly; supported float WAV, integer AIFF/AIFC and G.711 WAV are converted to PCM in memory without clipping. Play Original explicitly plays up to the first 10 seconds as 16-bit PCM. The waveform represents the complete source. No editing, file export or automatic playback is provided here. The source stays held until this window and audio preparation finish.")));
        if(document.Decoded)body.Children.Add(UI.Text(app.L(WaveDocument.DecodedPreviewNotice),12));
        var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var buttons=new WrapPanel();footer.Children.Add(buttons);
        var play=UI.Button(app.L("Play Original"),()=>{});play.Click+=(_,_)=>{if(pending.IsCompleted)pending=Play();};buttons.Children.Add(play);
        buttons.Children.Add(UI.Button(app.L("Stop"),Stop));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;buttons.Children.Add(close);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(UI.Scroll(body));Content=root;
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
        Closed+=(_,_)=>{closed=true;Stop();};
        async Task Play()
        {
            Stop();var owned=cancellation=new CancellationTokenSource();var token=owned.Token;play.IsEnabled=false;
            status.Text=app.L("Preparing audio preview…");WaveDialog.PreviewSound? prepared=null;
            try
            {
                prepared=await Task.Run(()=>WaveDialog.PreviewSound.Create(document.Preview(new(0,info.Seconds),token),token));
                token.ThrowIfCancellationRequested();if(closed)return;
                sound=prepared;prepared=null;sound.Play();status.Text=app.L("Playing up to 10 seconds. Stop ends playback.");
            }
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception){if(!closed)status.Text=app.L("Audio preview could not play.");}
            finally{prepared?.Dispose();if(ReferenceEquals(cancellation,owned))cancellation=null;owned.Dispose();if(!closed)play.IsEnabled=true;}
        }
    }
    private void Stop(){cancellation?.Cancel();sound?.Dispose();sound=null;}
    internal async Task FinishPreviewAsync(){Stop();await pending;}
}
