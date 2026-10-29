using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WaveSpeedDialog : Window
{
    public WaveSpeed Speed { get; private set; } = new(KeepPitch: true);
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public WaveSpeedDialog(App app, WaveDocument document)
    {
        var info = document.Info;
        Title = app.L("Change WAV Speed…"); Width = 720; Height = 570; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(app.L("Change playback speed. With Keep Pitch on (25–400%), sounds keep their original pitch. With it off (50–200%), pitch changes with speed: faster is higher, slower is lower.")));
        var keep = new CheckBox { Content = app.L("Keep Pitch (time-stretch)"), IsChecked = true, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetName(keep, app.L("Keep Pitch (time-stretch)")); panel.Children.Add(keep);
        panel.Children.Add(UI.Text(app.L("Playback Speed (%)")));
        var percent = new TextBox { Text = "100", MinHeight = 32, MaxLength = 8, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(percent, app.L("Playback Speed (%)")); panel.Children.Add(percent);
        var duration = UI.Text(""); panel.Children.Add(duration);
        panel.Children.Add(UI.Text(app.L("Save keeps the original sample rate, channels and integer bit depth, removes metadata, and creates a new WAV beside the original. Duration rounds to the nearest output frame; 100% preserves sample values."), 12));
        panel.Children.Add(UI.Text(app.L("Keep Pitch repeats or skips short overlapping pieces of the sound (40 ms, lined up by similarity; an original WSOLA method). On steady test tones pitch stays within about 0.3%. Clicks and drum hits can repeat as echoes when slower, or soften and drop out when faster; chords, music and noisy recordings can sound rough or wavering; both stereo channels are lined up together. It does not separate voices or instruments."), 12));
        panel.Children.Add(UI.Text(app.L("Without Keep Pitch, speed changes use a finite low-pass filter. Faster playback removes frequencies that would exceed the output Nyquist limit; slower playback cannot restore detail. Either way, peaks may clip and reduction rounds without dither."), 12));
        panel.Children.Add(UI.Text(app.L("Preview plays the first 10 output seconds as 16-bit PCM using the same processing and target quantization as Save. Output is limited to 512 MB and two hours; long processing can be cancelled."), 12));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        WaveSpeed Read()
        {
            bool pitch = keep.IsChecked == true;
            if (!int.TryParse(percent.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
                throw new UserError(pitch ? "Choose a whole-number speed from 25–400%." : "Choose a whole-number speed from 50–200%.");
            var speed = new WaveSpeed(value, pitch); speed.Frames(info); return speed;
        }
        var preview = UI.Button(app.L("Play Speed Preview"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { try { Speed = Read(); Stop(); DialogResult = true; } catch (UserError ex) { status.Text = app.L(ex.Message); } });
        preview.Click += (_, _) => { if (pending.IsCompleted) pending = Play(); };
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        void Changed()
        {
            Stop();
            try
            {
                var speed = Read(); long frames = speed.Frames(info);
                duration.Text = string.Format(app.L("Output: {0:0.###} seconds · {1} sample frames"), (double)frames / info.Rate, frames);
                status.Text = "";
            }
            catch (UserError ex) { duration.Text = ""; status.Text = app.L(ex.Message); }
        }
        percent.TextChanged += (_, _) => Changed(); keep.Checked += (_, _) => Changed(); keep.Unchecked += (_, _) => Changed(); Changed();
        SourceInitialized += (_, _) => FitWorkArea(Owner ?? this); DpiChanged += (_, _) => FitWorkArea(this);
        Closed += (_, _) => { closed = true; Stop(); };
        async Task Play()
        {
            WaveSpeed speed; try { speed = Read(); } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            save.IsEnabled = preview.IsEnabled = false; status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(document.SpeedPreview(speed, token), token));
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
