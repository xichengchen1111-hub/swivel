using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class AiffDialog : Window
{
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public AiffDialog(App app, AiffDocument document)
    {
        var info = document.Info;
        Title = app.L("Convert AIFF to WAV…"); Width = 720; Height = 570; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(info.CompressedForm ? app.L("AIFC container · uncompressed integer PCM only") : app.L("AIFF container · uncompressed integer PCM")));
        panel.Children.Add(UI.Text(app.L("Convert AIFF or uncompressed AIFC NONE/sowt/twos/in24/in32 to WAV. Save preserves every integer sample value, sample rate, channel order, frame count and bit depth. 8-bit signed samples become unsigned WAV; larger samples become little-endian.")));
        panel.Children.Add(UI.Text(app.L("Metadata, markers, instrument loops and codec names are removed. This is an audio conversion, not an archival copy. No gain, normalization, resampling or dither is applied. The result can be opened with the existing WAV tools."), 12));
        panel.Children.Add(UI.Text(app.L("Input and complete output are each limited to 512 MiB and two hours. Only mono/stereo 8/16/24/32-bit integer PCM at exact integer-Hz rates in the 8–192 kHz range is supported. twos/in24/in32 require 16/24/32-bit respectively. Compressed/float AIFC, other encoding labels, fractional rates and nonzero SSND block sizes are refused without rounding or silent substitution."), 12));
        panel.Children.Add(UI.Text(app.L("Preview plays the first 10 seconds as PCM16, matching the saved WAV converted to PCM16. Complete output limits are checked even for preview. Save creates a new WAV beside the original without replacing it; processing can be cancelled."), 12));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        var preview = UI.Button(app.L("Play WAV Preview"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { Stop(); DialogResult = true; });
        preview.Click += (_, _) => { if (pending.IsCompleted) pending = Play(); };
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        SourceInitialized += (_, _) => FitWorkArea(Owner ?? this); DpiChanged += (_, _) => FitWorkArea(this);
        Closed += (_, _) => { closed = true; Stop(); };
        async Task Play()
        {
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            save.IsEnabled = preview.IsEnabled = false; status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(document.Preview(token), token));
                token.ThrowIfCancellationRequested(); if (closed) return;
                sound = prepared; prepared = null; sound.Play(); status.Text = app.L("Playing up to 10 seconds. Stop ends playback.");
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("Audio preview could not play. You can still save the edited WAV."); }
            finally
            {
                prepared?.Dispose(); if (ReferenceEquals(cancellation, owned)) cancellation = null; owned.Dispose();
                if (!closed) save.IsEnabled = preview.IsEnabled = true;
            }
        }
    }
    private void FitWorkArea(Window reference)
    {
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(reference);
        double height = Math.Max(1, area.Height / dpi.DpiScaleY - 16), width = Math.Max(1, area.Width / dpi.DpiScaleX - 16);
        MinHeight = Math.Min(400, height); MinWidth = Math.Min(500, width); MaxHeight = height; MaxWidth = width;
        Height = Math.Min(Height, height); Width = Math.Min(Width, width);
    }
    private void Stop() { cancellation?.Cancel(); sound?.Dispose(); sound = null; }
    public async Task FinishPreviewAsync() { Stop(); await pending; }
}
