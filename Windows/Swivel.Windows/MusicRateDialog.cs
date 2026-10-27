using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal static class MusicRateNotices
{
    internal const string Scope="The music has a different sample rate. Prepare a complete local copy in memory at the main audio rate before mixing? This changes only the music rate, keeping its channels and bit depth. Duration rounds to the nearest target frame; filtering and quantization can change sound or clip peaks, with no dither or loudness boost. The complete prepared WAV is limited to 128 MiB, in addition to the converter's working memory. No original file is written. Both source files stay held until preview and save finish. Cancel leaves the editor unopened.";
    internal const string Output="The output keeps the main audio duration, rate, channels and bit depth. The music was explicitly prepared in memory at the main rate; its original channels and bit depth are retained. Preview and save mix the same prepared samples. Resampling can change sound or clip peaks; loud combinations can also clip. Compressed music and video mixing are not supported here.";
    internal const string Loop="This uses linear amplitude weights, not equal-power mixing or beat matching. Opposite-phase sounds can cancel and loudness or rhythm can change; even a two-frame crossfade is not guaranteed click-free. Music timing is relative to its start in the trimmed output. Overall music fades, activity lowering, main duration and format are retained. The prepared integer PCM music matches the main rate. Compressed music and video remain planned.";
    internal const string Duck="Activity lowering supports a main selection up to 10 minutes and 128 MiB of PCM data. Every preview and save first reads the whole selection for approximately 20 ms RMS windows, which adds preparation time. Threshold equality counts as active. A window may anticipate audio within it; full-depth lowering takes approximately 20 ms and full recovery approximately 250 ms; partial changes take less time. This is not Mac's speech classifier, transcription or pitch-preserving processing. The prepared integer PCM music matches the main rate.";
}

internal sealed class MusicRateDialog:Window
{
    internal MusicRateDialog(App app,WaveDocument main,WaveDocument music,WaveMusicRatePlan plan)
    {
        Title=app.L("Prepare Music Sample Rate…");Width=700;Height=610;MinWidth=460;MinHeight=340;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var body=new StackPanel();
        body.Children.Add(UI.Text(app.L("Main Audio"),18));body.Children.Add(UI.Text(main.Path,12));
        body.Children.Add(UI.Text(app.L("Background Music"),18));body.Children.Add(UI.Text(music.Path,12));
        body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"),music.Info.Seconds,music.Info.Rate,music.Info.Channels,music.Info.Bits)));
        body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,app.L("Prepared music: {0} Hz · {1} frames · {2} bytes including WAV header and padding."),plan.Format.Rate,plan.Frames,plan.Bytes),18));
        body.Children.Add(UI.Text(app.L(MusicRateNotices.Scope)));
        var footer=new WrapPanel();var prepare=UI.Button(app.L("Prepare Music"),()=>DialogResult=true);footer.Children.Add(prepare);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(UI.Scroll(body));Content=root;
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
