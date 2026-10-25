using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WaveNoiseDialog : Window
{
    public WaveNoise Settings { get; private set; }
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public WaveNoiseDialog(App app, WaveDocument document)
    {
        var info = document.Info; Settings = new(0, Math.Min(.25, info.Seconds));
        Title = app.L("Reduce WAV Noise…"); Width = 760; Height = 730; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(document.Path, 12));
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(app.L("Choose 0.05–2 seconds containing only steady background noise. Speech or music in this sample can also be removed. Listen to the sample, then compare original and processed audio before saving.")));
        var waveform = new WaveEnvelope(document.Peaks); panel.Children.Add(waveform);
        TextBox Field(string key, double value)
        {
            var row = new DockPanel(); var box = new TextBox { Text = value.ToString("R", CultureInfo.CurrentCulture), Width = 180, MinHeight = 30, MaxLength = 32, Margin = new Thickness(0, 5, 0, 5) };
            AutomationProperties.SetName(box, app.L(key)); DockPanel.SetDock(box, Dock.Right); row.Children.Add(box); row.Children.Add(UI.Text(app.L(key))); panel.Children.Add(row); return box;
        }
        var start = Field("Noise Sample Start (seconds)", 0); var end = Field("Noise Sample End (seconds)", Settings.End);
        panel.Children.Add(UI.Text(app.L("Noise Reduction Strength (%)")));
        var strength = new Slider { Minimum = 0, Maximum = 100, Value = 75, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(strength, app.L("Noise Reduction Strength (%)")); panel.Children.Add(strength); var level = UI.Text("75%", 12); panel.Children.Add(level);
        panel.Children.Add(UI.Text(app.L("This local filter estimates steady noise; it does not recognize speech or reliably remove clicks, wind or changing noise. Strong settings can damage speech or create artifacts. Use a lower strength if needed. There is no automatic loudness boost; changed peaks can clip."), 12));
        panel.Children.Add(UI.Text(app.L("The shaded waveform range is the noise sample. Original and processed previews play the first 10 seconds; sample preview plays your chosen segment. All playback uses 16-bit PCM. Save keeps the full duration, rate, channels and bit depth, removes metadata, and creates a new WAV beside the original."), 12));
        panel.Children.Add(UI.Text(app.L("Find Quiet Sample checks fixed windows of the current sample length, plus a final window ending at the recording end. It suggests the lowest average energy across all channels. Quiet audio can contain speech or music; this is not a noise-only detector. Listen before explicitly choosing Use Suggested Sample. Finding does not change your sample or save audio."), 12));
        QuietSampleCandidate? candidate = null; bool applyingCandidate = false;
        var suggestion = UI.Text(""); panel.Children.Add(suggestion);
        var candidateButtons = new WrapPanel(); panel.Children.Add(candidateButtons);
        var find = UI.Button(app.L("Find Quiet Sample"), () => { });
        var hear = UI.Button(app.L("Play Suggested Sample"), () => { });
        var use = UI.Button(app.L("Use Suggested Sample"), () => { });
        hear.IsEnabled = use.IsEnabled = false;
        candidateButtons.Children.Add(find); candidateButtons.Children.Add(hear); candidateButtons.Children.Add(use);
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        var original = UI.Button(app.L("Play Original"), () => { }); var processed = UI.Button(app.L("Play Processed"), () => { }); var sample = UI.Button(app.L("Play Noise Sample"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { try { if (!pending.IsCompleted) return; Settings = Read(); Stop(); DialogResult = true; } catch (UserError ex) { status.Text = app.L(ex.Message); } });
        original.Click += (_, _) => { if (pending.IsCompleted) pending = Play(0); };
        processed.Click += (_, _) => { if (pending.IsCompleted) pending = Play(1); };
        sample.Click += (_, _) => { if (pending.IsCompleted) pending = Play(2); };
        find.Click += (_, _) => { if (pending.IsCompleted) pending = Find(); };
        hear.Click += (_, _) => { if (pending.IsCompleted && candidate is not null) pending = Play(3); };
        use.Click += (_, _) =>
        {
            if (!pending.IsCompleted || candidate is not { } chosen) return;
            Stop(); ClearCandidate(); applyingCandidate = true;
            try { start.Text = chosen.Start.ToString("R", CultureInfo.CurrentCulture); end.Text = chosen.End.ToString("R", CultureInfo.CurrentCulture); }
            finally { applyingCandidate = false; }
            Changed();
        };
        buttons.Children.Add(original); buttons.Children.Add(processed); buttons.Children.Add(sample);
        buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        WaveNoise Read()
        {
            double Value(TextBox field) => double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ? value : double.NaN;
            var value = new WaveNoise(Value(start), Value(end), strength.Value / 100); value.Range(info); return value;
        }
        void Changed()
        {
            if (applyingCandidate) return; Stop(); ClearCandidate(); level.Text = strength.Value.ToString("0", CultureInfo.CurrentCulture) + "%";
            try { var range = Read().Range(info); waveform.Select((double)range.First / info.Frames, (double)(range.First + range.Count) / info.Frames); status.Text = ""; }
            catch (UserError ex) { waveform.Select(0, 0); status.Text = app.L(ex.Message); }
        }
        start.TextChanged += (_, _) => Changed(); end.TextChanged += (_, _) => Changed(); strength.ValueChanged += (_, _) => Changed(); Changed();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        Closed += (_, _) => { closed = true; Stop(); };
        void ClearCandidate() { candidate = null; suggestion.Text = ""; hear.IsEnabled = use.IsEnabled = false; }
        void Controls(bool enabled)
        {
            save.IsEnabled = original.IsEnabled = processed.IsEnabled = sample.IsEnabled = find.IsEnabled = enabled;
            hear.IsEnabled = use.IsEnabled = enabled && candidate is not null;
        }
        async Task Find()
        {
            long frames; try { frames = Read().Range(info).Count; } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); ClearCandidate(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            Controls(false); status.Text = app.L("Finding a quiet sample…");
            try
            {
                var found = await Task.Run(() => document.FindQuietSample(frames, token));
                token.ThrowIfCancellationRequested(); if (closed) return;
                candidate = found;
                suggestion.Text = string.Format(CultureInfo.CurrentCulture, app.L("Suggested sample: {0:0.######}–{1:0.######} seconds · {2} windows checked. Listen before using it; speech or music may be present."), found.Start, found.End, found.Windows);
                status.Text = app.L("Suggestion ready. Your current sample has not changed.");
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("A quiet sample could not be found. You can still choose the sample manually."); }
            finally
            {
                if (ReferenceEquals(cancellation, owned)) cancellation = null; owned.Dispose();
                if (!closed) Controls(true);
            }
        }
        async Task Play(int mode)
        {
            WaveNoise settings = Settings; var chosen = candidate; if (mode == 3 && chosen is null) return;
            try { if (mode != 0) settings = Read(); } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            Controls(false);
            status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(mode == 1 ? document.NoisePreview(settings, token) : document.Preview(mode == 3 ? new(chosen!.Start, chosen.End) : mode == 2 ? new(settings.Start, settings.End) : new(0, info.Seconds), token), token));
                token.ThrowIfCancellationRequested(); if (closed) return;
                sound = prepared; prepared = null; sound.Play(); status.Text = app.L("Playing up to 10 seconds. Stop ends playback.");
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("Audio preview could not play. You can still save the edited WAV."); }
            finally
            {
                prepared?.Dispose(); if (ReferenceEquals(cancellation, owned)) cancellation = null; owned.Dispose();
                if (!closed) Controls(true);
            }
        }
    }
    private void Stop() { cancellation?.Cancel(); sound?.Dispose(); sound = null; }
    public async Task FinishPreviewAsync() { Stop(); await pending; }
}
