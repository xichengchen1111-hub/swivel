using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WaveFormatDialog : Window
{
    public WaveFormat Format { get; private set; }
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public WaveFormatDialog(App app, WaveDocument document)
    {
        var info = document.Info; Format = new(info.Rate, info.Channels, info.Bits);
        Title = app.L("Convert WAV Format…"); Width = 720; Height = 650; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(app.L("Convert integer PCM WAV locally. Save creates a new WAV beside the original and removes metadata. Mono is duplicated for stereo; stereo is averaged for mono.")));
        ComboBox Choice(string key, int[] values, int initial, Func<int, string> label)
        {
            panel.Children.Add(UI.Text(app.L(key))); var box = new ComboBox { Margin = new Thickness(0, 4, 0, 12), MinHeight = 32 };
            var items = values.Select(x => new ComboBoxItem { Content = label(x), Tag = x }).ToArray();
            box.ItemsSource = items; box.SelectedItem = items.Single(x => (int)x.Tag == initial);
            AutomationProperties.SetName(box, app.L(key)); panel.Children.Add(box); return box;
        }
        var rate = Choice("Output Sample Rate", new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000, info.Rate }.Distinct().Order().ToArray(), info.Rate, x => string.Format(app.L("{0} Hz"), x));
        var channels = Choice("Output Channels", new[] { 1, 2 }, info.Channels, x => app.L(x == 1 ? "Mono" : "Stereo"));
        var bits = Choice("Output Bit Depth", new[] { 8, 16, 24, 32 }, info.Bits, x => string.Format(app.L("{0}-bit integer PCM"), x));
        panel.Children.Add(UI.Text(app.L("Resampling uses a finite low-pass filter and can change peaks. Lower rates remove high frequencies; higher rates and bit depths do not restore lost detail. Bit-depth reduction rounds without dither; 8-bit audio can have audible quantization noise. Peaks beyond full scale are clipped."), 12));
        panel.Children.Add(UI.Text(app.L("Preview plays up to 10 seconds with the chosen rate, channels and quantization, delivered as 16-bit PCM. Save is limited to 512 MB; long conversions may take time and can be cancelled."), 12));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        WaveFormat Read()
        {
            int Value(ComboBox box) => (int)((ComboBoxItem)box.SelectedItem).Tag;
            var result = new WaveFormat(Value(rate), Value(channels), Value(bits)); result.Frames(info); return result;
        }
        var preview = UI.Button(app.L("Play Selection Preview"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { try { Format = Read(); Stop(); DialogResult = true; } catch (UserError ex) { status.Text = app.L(ex.Message); } });
        preview.Click += (_, _) => { if (pending.IsCompleted) pending = Play(); };
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        foreach (var box in new[] { rate, channels, bits }) box.SelectionChanged += (_, _) => { Stop(); try { Read(); status.Text = ""; } catch (UserError ex) { status.Text = app.L(ex.Message); } };
        Closed += (_, _) => { closed = true; Stop(); };
        async Task Play()
        {
            WaveFormat format; try { format = Read(); } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            save.IsEnabled = preview.IsEnabled = false; status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(document.FormatPreview(format, token), token));
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
    private void Stop() { cancellation?.Cancel(); sound?.Dispose(); sound = null; }
    public async Task FinishPreviewAsync() { Stop(); await pending; }
}
