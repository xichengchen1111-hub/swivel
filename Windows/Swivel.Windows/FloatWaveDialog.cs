using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class FloatWaveDialog : Window
{
    public FloatPcmOptions Options { get; private set; } = new();
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public FloatWaveDialog(App app, FloatWaveDocument document)
    {
        var info = document.Info; var stats = document.Stats;
        Title = app.L("Convert Float WAV to PCM…"); Width = 750; Height = 660; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit float"), info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(string.Format(app.L("Discrete sample peak: {0:G6} · Outside −1…+1: {1} of {2} sample values"), stats.Peak, stats.OutsideUnit, stats.Samples)));
        panel.Children.Add(UI.Text(app.L("The entire source has been checked for finite samples. This peak is not a true-peak or loudness measurement. No automatic normalization or gain is applied."), 12));
        panel.Children.Add(UI.Text(app.L("Integer PCM Bit Depth")));
        var depth = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 4, 0, 12) };
        foreach (int bits in new[] { 16, 24, 32 }) depth.Items.Add(bits); depth.SelectedItem = 24;
        AutomationProperties.SetName(depth, app.L("Integer PCM Bit Depth")); panel.Children.Add(depth);
        var clipping = new CheckBox { Content = app.L("Allow clipping samples outside −1…+1"), IsChecked = false, IsEnabled = stats.OutsideUnit > 0, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(clipping, app.L("Allow clipping samples outside −1…+1")); panel.Children.Add(clipping);
        panel.Children.Add(UI.Text(app.L("Values outside −1…+1 require explicit clipping permission. Positive full scale maps to the largest integer; negative full scale maps to the smallest. Fractional values round to the nearest integer, halfway away from zero; no dither is added. NaN and infinity are always refused."), 12));
        panel.Children.Add(UI.Text(app.L("Save preserves sample rate, channels and frame count, removes metadata, and creates a new integer PCM WAV beside the original. Float dynamic range and sub-integer detail are reduced. You can then open the result with the existing WAV tools."), 12));
        panel.Children.Add(UI.Text(app.L("Preview plays the first 10 seconds after the selected integer quantization, delivered as PCM16. The full source and complete output budget are checked before preview or save. Each is limited to 512 MiB and two hours; float64 input does not imply float64 device playback support."), 12));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        FloatPcmOptions Read()
        {
            if (depth.SelectedItem is not int bits) throw new UserError(FloatWave.Depth);
            var options = new FloatPcmOptions(bits, clipping.IsChecked == true); options.Validate(info, stats); return options;
        }
        var preview = UI.Button(app.L("Play PCM Conversion Preview"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { try { Options = Read(); Stop(); DialogResult = true; } catch (UserError ex) { status.Text = app.L(ex.Message); } });
        preview.Click += (_, _) => { if (pending.IsCompleted) pending = Play(); };
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        void Changed()
        {
            Stop();
            try { Read(); status.Text = ""; }
            catch (UserError ex) { status.Text = app.L(ex.Message); }
        }
        depth.SelectionChanged += (_, _) => Changed(); clipping.Checked += (_, _) => Changed(); clipping.Unchecked += (_, _) => Changed(); Changed();
        SourceInitialized += (_, _) => FitWorkArea(Owner ?? this); DpiChanged += (_, _) => FitWorkArea(this);
        Closed += (_, _) => { closed = true; Stop(); };
        async Task Play()
        {
            FloatPcmOptions options; try { options = Read(); } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            save.IsEnabled = preview.IsEnabled = false; status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(document.Preview(options, token), token));
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
