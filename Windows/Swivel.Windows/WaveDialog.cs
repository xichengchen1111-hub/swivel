using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal static class WaveDuckingNotices
{
    internal const string Scope="Off by default. This detects audio volume activity, not speech: noise, tones and existing music can lower the background, while quiet speech can be missed. RMS uses all main-audio channels after the selected gain and fades, before clipping or quantization. Music During Activity is a percentage of the configured music volume; the main audio itself is unchanged by this control.";
    internal const string Speech="With Lower Only While Someone Speaks, Find Speech transcribes the main audio on this PC with the Whisper base model (no upload, no download; up to one hour, and on a processor it can take about as long as the audio). The music is lowered wherever speech was found, with pauses under half a second bridged; the threshold is not used and the 10-minute limit does not apply. Whisper can miss quiet or overlapping speech and can mistake singing or noise for speech, so preview before saving. The speech found is kept only while this window is open.";
    internal const string Limits="Activity lowering supports a main selection up to 10 minutes and 128 MiB of PCM data. Every preview and save first reads the whole selection for approximately 20 ms RMS windows, which adds preparation time. Threshold equality counts as active. A window may anticipate audio within it; full-depth lowering takes approximately 20 ms and full recovery approximately 250 ms; partial changes take less time. This is not Mac's speech classifier, transcription or pitch-preserving processing. Matching-rate integer PCM WAV inputs are still required.";
}

internal static class WaveLoopNotices
{
    internal const string Scope="Loop crossfade is off at 0 ms and is available only with Loop Background Music. Choose 0–1000 ms; a nonzero value rounds to at least two sample frames and must fit within half the music file. The initial pass plays up to the overlap, then each repeat overlaps the tail with the head, shortening its period by the crossfade length. A short overlay may end before the first overlap.";
    internal const string Limits="This uses linear amplitude weights, not equal-power mixing or beat matching. Opposite-phase sounds can cancel and loudness or rhythm can change; even a two-frame crossfade is not guaranteed click-free. Music timing is relative to its start in the trimmed output. Overall music fades, activity lowering, main duration and format are retained. Matching-rate integer PCM WAV is still required; automatic resampling, compressed audio and video remain planned.";
}

internal sealed class WaveDialog : Window
{
    public AudioEdit Settings { get; private set; }
    public WaveMusic MusicSettings { get; private set; } = new();
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    // Find Speech has its own task and cancellation, so editing a field does not discard it.
    private Task finding = Task.CompletedTask;
    private CancellationTokenSource? findCancellation;
    private bool playing, searching;
    private PreviewSound? sound;
    private bool closed;
    private IReadOnlyList<TranscriptSegment>? speech;
    public WaveDialog(App app,WaveDocument document,WaveDocument? music = null)
    {
        var info = document.Info; Settings = new(0,info.Seconds); bool prepared = music?.PreparedMusicClippedSamples is not null;
        Title = app.L(music is null ? "Edit WAV Audio…" : "Add WAV Background Music…"); Width = 760; Height = 730; MinWidth = 500; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var panel = new StackPanel();
        var fields = new List<TextBox>();
        if (music is not null)
        {
            panel.Children.Add(UI.Text(app.L("Main Audio"), 18)); panel.Children.Add(UI.Text(document.Path, 12));
            panel.Children.Add(UI.Text(app.L("Background Music"), 18)); panel.Children.Add(UI.Text(music.Path, 12));
            panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), music.Info.Seconds, music.Info.Rate, music.Info.Channels, music.Info.Bits), 12));
            if (prepared) panel.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("Prepared music: {0} Hz · {1} frames · {2} sample(s) clipped during rate conversion. The source above is unchanged."), music.MusicInfo.Rate, music.MusicInfo.Frames, music.PreparedMusicClippedSamples), 12));
            panel.Children.Add(UI.Text(app.L("Music starts relative to the trimmed output. Mono music is copied to both stereo channels; stereo music is averaged for mono output. Loops repeat directly unless you enable a crossfade below. Optional lowering below follows the main audio's volume, or speech found on this PC.")));
        }
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"),info.Seconds,info.Rate,info.Channels,info.Bits),20));
        panel.Children.Add(UI.Text(app.L("Trim, adjust volume and add fades locally. Save keeps the sample rate, channels and bit depth, creates a new WAV beside the original, and removes metadata. Volume above 100% can clip.")));
        var waveform = new WaveEnvelope(document.Peaks); panel.Children.Add(waveform);
        TextBox Field(string key,double initial)
        {
            var row = new DockPanel(); var box = new TextBox { Text = initial.ToString("R",CultureInfo.CurrentCulture),Width = 180,MinHeight = 30,MaxLength = 32,Margin = new Thickness(0,5,0,5) };
            AutomationProperties.SetName(box,app.L(key)); DockPanel.SetDock(box,Dock.Right); row.Children.Add(box); row.Children.Add(UI.Text(app.L(key))); panel.Children.Add(row); fields.Add(box); return box;
        }
        var start = Field("Start (seconds)",0); var end = Field("End (seconds)",info.Seconds); var volume = Field("Volume (%)",100);
        var fadeIn = Field("Fade In (seconds)",0); var fadeOut = Field("Fade Out (seconds)",0);
        TextBox? musicStart = null, musicVolume = null, musicFadeIn = null, musicFadeOut = null, duckThreshold = null, duckLevel = null, crossfade = null; CheckBox? loop = null, duck = null, speechOnly = null; Button? findSpeech = null;
        if (music is not null)
        {
            panel.Children.Add(UI.Text(app.L("Music Mix"), 20));
            musicStart = Field("Music Start (seconds)", 0); musicVolume = Field("Music Volume (%)", 15);
            musicFadeIn = Field("Music Fade In (seconds)", 0); musicFadeOut = Field("Music Fade Out (seconds)", 0);
            loop = new CheckBox { Content = app.L("Loop Background Music"), Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(loop);
            crossfade = Field("Loop Crossfade (ms)",0); crossfade.IsEnabled = false;
            panel.Children.Add(UI.Text(app.L(WaveLoopNotices.Scope),12));
            panel.Children.Add(UI.Text(app.L(prepared ? MusicRateNotices.Loop : WaveLoopNotices.Limits),12));
            duck = new CheckBox { Content = app.L("Lower Music During Active Main Audio"), Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(duck);
            duckThreshold = Field("Activity Threshold (dBFS)", -35); duckLevel = Field("Music During Activity (%)", 20);
            duckThreshold.IsEnabled = duckLevel.IsEnabled = false;
            speechOnly = new CheckBox { Content = app.L("Lower Only While Someone Speaks"), Margin = new Thickness(0, 8, 0, 4), IsEnabled = false }; panel.Children.Add(speechOnly);
            findSpeech = UI.Button(app.L("Find Speech"), () => { }); findSpeech.IsEnabled = false; findSpeech.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(findSpeech);
            panel.Children.Add(UI.Text(app.L(WaveDuckingNotices.Speech),12));
            if (info.Seconds > Transcribe.MaxSeconds) panel.Children.Add(UI.Text(app.L("Lower Only While Someone Speaks needs main audio of one hour or less."),12));
            panel.Children.Add(UI.Text(app.L("When Lower Only While Someone Speaks is off:"),12));
            panel.Children.Add(UI.Text(app.L(WaveDuckingNotices.Scope),12));
            panel.Children.Add(UI.Text(app.L(prepared ? MusicRateNotices.Duck : WaveDuckingNotices.Limits),12));
            panel.Children.Add(UI.Text(app.L(prepared ? MusicRateNotices.Output : "The output keeps the main audio duration, rate, channels and bit depth. Loud combinations can clip. Music inputs must already have matching sample rates; use Convert WAV Format first if needed. Compressed music is not supported yet; loop crossfade is an explicit option above."), 12));
        }
        panel.Children.Add(UI.Text(app.L("Preview plays the first 10 seconds of the selection as 16-bit PCM. The waveform shows the original; the shaded range is selected. Playback stops when values change or this window closes."),12));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        var preview = UI.Button(app.L("Play Selection Preview"),() => { });
        var save = UI.Button(app.L("Save WAV"),() => { try { Settings = Read(); MusicSettings = ReadMusic(Settings); Stop(); DialogResult = true; } catch (UserError ex) { status.Text = app.L(ex.Message); } });
        preview.Click += (_,_) => { if (!playing && !searching && pending.IsCompleted) pending = Play(); };
        if (findSpeech is not null) findSpeech.Click += (_,_) => { if (!playing && !searching && finding.IsCompleted) finding = FindSpeech(); };
        // One place for the buttons that depend on a running preview or Find Speech.
        void UpdateButtons()
        {
            if (closed) return;
            save.IsEnabled = preview.IsEnabled = !playing && !searching;
            if (findSpeech is not null)
                findSpeech.IsEnabled = duck!.IsChecked == true && speechOnly!.IsChecked == true && !playing && !searching && info.Seconds <= Transcribe.MaxSeconds;
        }
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"),Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"),() => { Stop(); StopFind(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        AudioEdit Read()
        {
            double Value(TextBox box) => double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value) ? value : double.NaN;
            var edit = new AudioEdit(Value(start),Value(end),Value(volume)/100,Value(fadeIn),Value(fadeOut)); edit.Range(info); return edit;
        }
        WaveMusic ReadMusic(AudioEdit edit)
        {
            if (music is null) return new();
            double Value(TextBox box) => double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value) ? value : double.NaN;
            var settings = new WaveMusic(Value(musicStart!), Value(musicVolume!) / 100, loop!.IsChecked == true, Value(musicFadeIn!), Value(musicFadeOut!), duck?.IsChecked == true ? (speechOnly!.IsChecked == true ? new WaveDucking(-35, Value(duckLevel!) / 100, speech ?? throw new UserError("Choose Find Speech first, or turn off Lower Only While Someone Speaks.")) : new WaveDucking(Value(duckThreshold!),Value(duckLevel!)/100)) : null, loop.IsChecked == true ? Value(crossfade!) : 0);
            settings.Range(info, music.MusicInfo, edit); return settings;
        }
        void RefreshSelection()
        {
            Stop();
            try { var edit = Read(); ReadMusic(edit); var range = edit.Range(info); waveform.Select((double)range.First/info.Frames,(double)(range.First+range.Count)/info.Frames); status.Text = ""; }
            catch (UserError ex) { status.Text = app.L(ex.Message); }
        }
        foreach (var field in fields) field.TextChanged += (_,_) => RefreshSelection();
        if(loop is not null)
        {
            void ToggleLoop(){crossfade!.IsEnabled=loop.IsChecked==true;RefreshSelection();}
            loop.Checked+=(_,_)=>ToggleLoop();loop.Unchecked+=(_,_)=>ToggleLoop();
        }
        if(duck is not null)
        {
            void ToggleDucking()
            {
                bool on=duck.IsChecked==true,bySpeech=on&&speechOnly!.IsChecked==true;
                duckLevel!.IsEnabled=on;speechOnly!.IsEnabled=on&&info.Seconds<=Transcribe.MaxSeconds;duckThreshold!.IsEnabled=on&&!bySpeech;
                if(!bySpeech)StopFind();
                UpdateButtons();RefreshSelection();
            }
            duck.Checked+=(_,_)=>ToggleDucking();duck.Unchecked+=(_,_)=>ToggleDucking();
            speechOnly!.Checked+=(_,_)=>ToggleDucking();speechOnly.Unchecked+=(_,_)=>ToggleDucking();
            SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
        }
        Closed += (_,_) => { closed = true; Stop(); StopFind(); };
        async Task Play()
        {
            AudioEdit edit; WaveMusic mix;
            try { edit = Read(); mix = ReadMusic(edit); } catch (UserError ex) { status.Text = app.L(ex.Message); return; }
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            playing = true; UpdateButtons(); status.Text = app.L("Preparing audio preview…"); PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => PreviewSound.Create(music is null ? document.Preview(edit,token) : document.MixPreview(music,edit,mix,token),token));
                token.ThrowIfCancellationRequested();
                if (closed) return;
                sound = prepared; prepared = null; sound.Play(); status.Text = app.L("Playing up to 10 seconds. Stop ends playback.");
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("Audio preview could not play. You can still save the edited WAV."); }
            finally
            {
                prepared?.Dispose(); if (ReferenceEquals(cancellation,owned)) cancellation = null; owned.Dispose();
                playing = false; UpdateButtons();
            }
        }
        // Whisper on the main audio, off the UI thread; cancelled by Stop (any field change, Close,
        // Quit) and joined by FinishPreviewAsync like a preview.
        async Task FindSpeech()
        {
            Stop(); var owned = findCancellation = new CancellationTokenSource(); var token = owned.Token;
            searching = true; UpdateButtons(); status.Text = app.L("Loading the speech recognition model…");
            try
            {
                string path = document.Path;
                var found = await Task.Run(async () =>
                {
                    using var source = await TranscribeDocument.OpenAsync(path, token).ConfigureAwait(false);
                    SpeechModel.UsesGraphics(token);
                    return source.Run(null, (done, total, left) =>
                    {
                        int percent = total > 0 ? (int)(100L * done / total) : 0;
                        Dispatcher.BeginInvoke(() => { if (!closed && ReferenceEquals(findCancellation, owned)) status.Text = string.Format(app.L("Finding speech: {0}% done…"), percent); });
                    }, token).Segments;
                }, token);
                token.ThrowIfCancellationRequested();
                if (closed) return;
                speech = found;
                status.Text = found.Count == 0 ? app.L("No speech was found in the main audio, so the music keeps its normal volume.")
                    : string.Format(CultureInfo.CurrentCulture, app.L("Found {0} speech segment(s), {1:0.#} seconds in all. Preview before saving."), found.Count, found.Sum(x => x.End - x.Start));
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("Speech could not be found in this audio."); }
            finally
            {
                if (ReferenceEquals(findCancellation, owned)) findCancellation = null; owned.Dispose();
                searching = false; UpdateButtons();
            }
        }
    }
    private void StopFind() => findCancellation?.Cancel();
    private void Stop() { cancellation?.Cancel(); sound?.Dispose(); sound = null; }
    // App awaits this before exporting or releasing the source lease, including
    // when an owner closes, the app quits or the user cancels during generation.
    public async Task FinishPreviewAsync() { Stop(); StopFind(); await pending; await finding; }

    internal sealed class PreviewSound : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly SoundPlayer player;
        private PreviewSound(byte[] bytes) { stream = new(bytes,false); player = new(stream) { LoadTimeout = 2000 }; }
        public static PreviewSound Create(byte[] bytes,CancellationToken token)
        {
            var sound = new PreviewSound(bytes);
            try { token.ThrowIfCancellationRequested(); sound.player.Load(); token.ThrowIfCancellationRequested(); return sound; }
            catch { sound.Dispose(); throw; }
        }
        public void Play() => player.Play();
        public void Dispose() { player.Stop(); player.Dispose(); stream.Dispose(); }
    }
}

internal sealed class WaveEnvelope(double[] peaks) : FrameworkElement
{
    private double start, end = 1;
    public void Select(double first,double last) { start = first; end = last; InvalidateVisual(); }
    protected override Size MeasureOverride(Size available) => new(double.IsFinite(available.Width) ? available.Width : 600,110);
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(SystemColors.WindowBrush,new Pen(SystemColors.WindowTextBrush,1),new Rect(0,0,width,height));
        dc.PushOpacity(.2); dc.DrawRectangle(SystemColors.HighlightBrush,null,new Rect(start*width,0,(end-start)*width,height)); dc.Pop();
        var pen = new Pen(SystemColors.WindowTextBrush,1);
        for (int i = 0; i < peaks.Length; i++) { double x = (i+.5)*width/peaks.Length, amplitude = peaks[i]*height*.45; dc.DrawLine(pen,new Point(x,height/2-amplitude),new Point(x,height/2+amplitude)); }
    }
}
