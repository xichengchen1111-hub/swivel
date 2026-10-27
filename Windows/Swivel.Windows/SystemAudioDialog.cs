using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class SystemAudioDialog : Window
{
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public SystemAudioDialog(App app, SystemAudioDocument document)
    {
        var info = document.Info;
        Title = app.L("Decode Audio with Windows…"); Width = 760; Height = 740; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · PCM16 output"), info.Seconds, info.Rate, info.Channels), 18));
        foreach(string note in new[]{SystemAudioDocument.Scope,SystemAudioDocument.Limits})panel.Children.Add(UI.Text(app.L(note),12));
        panel.Children.Add(UI.Text(app.L("The complete file has been decoded and validated before this window opens. Preview plays at most 10 seconds of the same prepared PCM16 audio. Save creates a new WAV beside the source; the original stays unchanged."),12));
        panel.Children.Add(UI.Text(app.L("If a new tool or action is saved, Windows 0.49 and earlier may reject those settings and fall back to defaults. Keep a pre-upgrade backup before downgrading."),12));
        panel.Children.Add(UI.Text(app.L("The signature check is only an input gate. Windows validates the actual audio format. ID3 metadata before the audio is limited to 32 MiB."),12));
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
