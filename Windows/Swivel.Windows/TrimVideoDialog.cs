using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

/// Trim Video…: the start and end to keep, typed as times, each with the frame Windows shows
/// there, and whether to keep the sound. Frames are made one at a time after typing pauses; the
/// window's FinishAsync waits for the last one before the video is closed. The picture can be
/// turned, flipped and cropped to a shape, and the speed changed; the frames show those changes.
internal sealed class TrimVideoDialog : Window
{
    public VideoTrimPlan? Plan { get; private set; }
    private readonly CancellationTokenSource frames = new();
    private Task pending = Task.CompletedTask, noisePending = Task.CompletedTask, colorPending = Task.CompletedTask;
    private CancellationTokenSource? colorCancellation;
    private bool dirty, closed;

    public TrimVideoDialog(App app, VideoTrimmer video)
    {
        Title = app.L("Trim Video…"); Width = 720; Height = 760; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        string Time(long ms) => VideoTrim.Format(ms, video.Hours);

        Image Picture(string name)
        {
            var image = new Image { Height = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(image, name); RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        var startText = new TextBox { Text = Time(0), MinHeight = 28, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var endText = new TextBox { Text = Time(video.Duration), MinHeight = 28, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(startText, app.L("Start time")); AutomationProperties.SetName(endText, app.L("End time"));
        var startFrame = Picture(app.L("Frame at the start")); var endFrame = Picture(app.L("Last frame kept"));
        var sound = new CheckBox { Content = app.L("Keep the sound"), IsChecked = video.HasSound, IsEnabled = video.HasSound, Margin = new Thickness(0, 8, 0, 4) };
        var reduceNoise = new CheckBox { Content = app.L("Reduce noise in the sound"), IsChecked = false, IsEnabled = video.HasSound, Margin = new Thickness(0, 4, 0, 4) };
        var noiseSettingsButton = UI.Button(app.L("Noise sample and strength…"), () => { });
        WaveNoise? noiseSettings = null;
        bool noiseBusy = false;
        var turn = new ComboBox { ItemsSource = new[] { app.L("No turn"), app.L("90° clockwise"), app.L("180°"), app.L("90° counterclockwise") }, SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        var flipH = new CheckBox { Content = app.L("Flip left to right"), Margin = new Thickness(0, 4, 0, 2) };
        var flipV = new CheckBox { Content = app.L("Flip upside down"), Margin = new Thickness(0, 2, 0, 4) };
        var shape = new ComboBox { ItemsSource = new[] { app.L("Whole picture"), "16:9", "9:16", "1:1", "4:5", "4:3" }, SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        var position = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, SmallChange = 0.05, LargeChange = 0.25, Margin = new Thickness(0, 4, 0, 4), IsEnabled = false };
        var speed = new ComboBox { ItemsSource = VideoEdit.Speeds.Select(x => (x / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "×").ToArray(), SelectedIndex = Array.IndexOf(VideoEdit.Speeds, 100), MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetName(turn, app.L("Turn")); AutomationProperties.SetName(shape, app.L("Crop to shape"));
        AutomationProperties.SetName(position, app.L("Crop position")); AutomationProperties.SetName(speed, app.L("Speed"));
        var colorBody = new StackPanel(); var soundBody = new StackPanel();
        Slider Setting(StackPanel panel, string label, double min, double max, double value)
        {
            var text = UI.Text("");
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = (max - min) / 100, LargeChange = (max - min) / 10, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(slider, app.L(label));
            void Label() => text.Text = app.L(label) + ": " + slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
            slider.ValueChanged += (_, _) => Label(); Label(); panel.Children.Add(text); panel.Children.Add(slider); return slider;
        }
        var look = new ComboBox { ItemsSource = Enum.GetValues<PhotoLook>().Select(x => app.L(x.ToString())).ToArray(), SelectedIndex = 0, MinHeight = 28 };
        AutomationProperties.SetName(look, app.L("Look")); colorBody.Children.Add(UI.Text(app.L("Look"))); colorBody.Children.Add(look);
        var lookStrength = Setting(colorBody, "Look strength (%)", 0, 100, 100);
        var exposure = Setting(colorBody, "Exposure", -3, 3, 0);
        var contrast = Setting(colorBody, "Contrast", -1, 1, 0);
        var saturation = Setting(colorBody, "Saturation", -1, 1, 0);
        var warmth = Setting(colorBody, "Warmth", -1, 1, 0);
        var colorFrame = Picture(app.L("Color preview at the middle of the kept part"));
        colorBody.Children.Add(colorFrame);
        colorBody.Children.Add(UI.Text(app.L("Color uses the same SDR adjustments as photos. HDR becomes SDR as in other video exports. Color and picture fades add a frame-by-frame encoding pass; the still preview uses the same adjustments before MP4 compression."), 12));
        var volume = Setting(soundBody, "Original sound volume (%)", 0, 200, 100);
        var fadeIn = Setting(soundBody, "Picture and sound fade in (seconds)", 0, 5, 0);
        var fadeOut = Setting(soundBody, "Picture and sound fade out (seconds)", 0, 5, 0);
        soundBody.Children.Add(UI.Text(app.L("Fades share one envelope after cutting and changing speed, even when they overlap. Original volume above 100% and mixed peaks are clipped, never wrapped. Removing the original sound keeps the music."), 12));
        string? musicPath = null;
        var musicName = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(musicName, app.L("Background music"));
        var musicGain = Setting(soundBody, "Music volume (%)", 0, 100, 40);
        var musicLoop = new CheckBox { Content = app.L("Loop music to the end"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var musicDuck = new CheckBox { Content = app.L("Lower music only while someone speaks"), IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
        var chooseMusic = UI.Button(app.L("Choose Background Music…"), () => { });
        var removeMusic = UI.Button(app.L("Remove Music"), () => { });
        soundBody.Children.Add(chooseMusic); soundBody.Children.Add(removeMusic); soundBody.Children.Add(musicName); soundBody.Children.Add(musicLoop); soundBody.Children.Add(musicDuck);
        soundBody.Children.Add(UI.Text(app.L("Music starts at the cut's output start, loops or stops when it runs out, and is trimmed to the exact new length with an automatic final fade of up to 0.8 seconds. It is resampled to the video's sound rate. Input and prepared music are each limited to 128 MiB; processed video sound to 512 MiB and two hours. WAV, MP3, ADTS AAC, FLAC and audio-only M4A depend on installed Windows codecs."), 12));
        soundBody.Children.Add(UI.Text(app.L("Speech lowering uses the bundled local Whisper model on the kept, speed-adjusted original sound during export (up to one hour). Music falls to 25% during speech. It can take time and can misidentify speech; no model is downloaded. Removing or muting the original disables lowering. Missing models or audio failures stop the export."), 12));
        VideoColorSound ChosenEffects() => new(new PhotoSettings(Exposure: exposure.Value, Contrast: contrast.Value, Saturation: saturation.Value, Warmth: warmth.Value,
            Look: (PhotoLook)Math.Max(0, look.SelectedIndex), LookStrength: lookStrength.Value / 100), fadeIn.Value, fadeOut.Value, volume.Value / 100,
            musicPath is null ? null : new VideoMusic(musicPath, musicGain.Value / 100, musicLoop.IsChecked == true, musicDuck.IsChecked == true));
        var soundNote = UI.Text("", 12);
        // The frames as Windows returned them; shown through the chosen picture change.
        System.Windows.Media.Imaging.BitmapSource? startRaw = null, endRaw = null, colorRaw = null;
        bool colorDirty = false;
        var status = UI.Text("");
        var frameNote = UI.Text("", 12);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var buttons = new WrapPanel();
        Button trim = null!;
        trim = UI.Button(app.L("Trim"), () => { if (Check() is { } plan) { Plan = plan; DialogResult = true; } });
        trim.IsDefault = true; buttons.Children.Add(trim);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        VideoPicture Chosen() => new(turn.SelectedIndex, flipH.IsChecked == true, flipV.IsChecked == true, (VideoAspect)shape.SelectedIndex, Math.Clamp(position.Value, 0, 1));
        int Speed() => VideoEdit.Speeds[Math.Max(0, speed.SelectedIndex)];
        VideoTrimPlan? Check(bool requireNoise = true)
        {
            volume.IsEnabled = sound.IsChecked == true && video.HasSound;
            musicDuck.IsEnabled = musicPath is not null && sound.IsChecked == true && video.HasSound && volume.Value > 0;
            reduceNoise.IsEnabled = !noiseBusy && sound.IsChecked == true && video.HasSound;
            noiseSettingsButton.IsEnabled = !noiseBusy && reduceNoise.IsEnabled && reduceNoise.IsChecked == true;
            long? start = VideoTrim.Parse(startText.Text), end = VideoTrim.Parse(endText.Text);
            bool normal = Speed() == 100;
            soundNote.Text = video.HasSound && !normal && sound.IsChecked == true ? app.L(VideoTrimmer.StretchNote) : "";
            try
            {
                position.IsEnabled = shape.SelectedIndex > 0 && video.PictureChanges;
                var geometry = VideoEdit.Geometry(video.Width, video.Height, Chosen());
                position.IsEnabled = video.PictureChanges && geometry.Seen != VideoEdit.Crop(geometry.SeenWidth, geometry.SeenHeight, VideoAspect.Original, 0);
                if (!normal) VideoEdit.CheckRate(geometry.Width, geometry.Height, video.Fps, Speed());
                if (start is null || end is null) throw new UserError(VideoTrim.BadTime);
                var plan = VideoTrim.Plan(start.Value, end.Value, video.Duration, sound.IsChecked == true, video.HasSound, Chosen(), Speed(),
                    reduceNoise.IsEnabled && reduceNoise.IsChecked == true ? noiseSettings ?? new WaveNoise(0, Math.Min(.25, (end.Value - start.Value) / 1000.0)) : null, video.SoundRate, ChosenEffects());
                if (requireNoise && plan.Noise is not null && noiseSettings is null) throw new UserError("Choose the noise sample and strength before trimming.");
                video.CheckSound(plan);
                status.Text = string.Format(app.L("Keeps {0}, from {1} to {2}."), Time(plan.Length), Time(plan.Start), Time(plan.End))
                    + (plan.KeepSound || !video.HasSound ? "" : " " + app.L("The sound is removed."))
                    + (plan.Edited ? " " + string.Format(app.L("The new video is {0} × {1} pixels and {2} long."), geometry.Width, geometry.Height, VideoTrim.Format(plan.OutputLength, plan.OutputLength >= 3_600_000)) : "");
                trim.IsEnabled = !noiseBusy;
                return plan;
            }
            catch (UserError error) { status.Text = app.L(error.Message); trim.IsEnabled = false; return null; }
        }
        chooseMusic.Click += (_, _) =>
        {
            if (closed) return;
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = app.L("Audio (*.wav;*.mp3;*.aac;*.flac;*.m4a)|*.wav;*.mp3;*.aac;*.flac;*.m4a"), CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == true && !closed) { musicName.Text = musicPath = picker.FileName; Check(); }
        };
        removeMusic.Click += (_, _) => { musicPath = null; musicName.Text = ""; Check(); };
        void Changed() { noiseSettings = null; Check(); dirty = true; if (pending.IsCompleted && !closed) pending = Load(); }
        startText.TextChanged += (_, _) => Changed(); endText.TextChanged += (_, _) => Changed();
        reduceNoise.Checked += (_, _) => { Check(); if (noisePending.IsCompleted) noisePending = ConfigureNoise(); };
        reduceNoise.Unchecked += (_, _) => Check();
        noiseSettingsButton.Click += (_, _) => { if (noisePending.IsCompleted) noisePending = ConfigureNoise(); };
        async Task ConfigureNoise()
        {
            var plan = Check(false); if (plan is null) return;
            noiseBusy = true;
            startText.IsEnabled = endText.IsEnabled = sound.IsEnabled = reduceNoise.IsEnabled = noiseSettingsButton.IsEnabled = trim.IsEnabled = false;
            status.Text = app.L("Preparing audio preview…");
            WaveDocument? document = null;
            string? failure = null;
            try
            {
                document = await Task.Run(() => video.NoiseDocumentAsync(plan, frames.Token));
                frames.Token.ThrowIfCancellationRequested(); if (closed) return;
                var dialog = new WaveNoiseDialog(app, document, true, noiseSettings, video.Path) { Owner = this };
                using var registration = frames.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
                try { if (dialog.ShowDialog() == true) noiseSettings = dialog.Settings; }
                finally { await dialog.FinishPreviewAsync(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure = app.L(error is UserError && error.Message != VideoEdit.SoundFailed ? error.Message : VideoEdit.NoiseFailed); }
            finally
            {
                if (document is not null)
                {
                    string path = document.Path; document.Dispose();
                    try { System.IO.File.Delete(path); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
                }
                noiseBusy = false;
                if (!closed) { startText.IsEnabled = endText.IsEnabled = true; sound.IsEnabled = video.HasSound; Check(); if (failure is not null) status.Text = failure; }
            }
        }
        sound.Checked += (_, _) => Check(); sound.Unchecked += (_, _) => Check();
        void Show()
        {
            colorDirty = true; colorCancellation?.Cancel();
            if (colorPending.IsCompleted && !closed) colorPending = Paint();
        }
        async Task Paint()
        {
            while (colorDirty && !closed)
            {
                colorDirty = false;
                VideoPicture picture = Chosen(); var settings = ChosenEffects();
                var first = startRaw; var last = endRaw; var middle = colorRaw;
                long start = VideoTrim.Parse(startText.Text) ?? 0, end = VideoTrim.Parse(endText.Text) ?? start;
                int percent = Speed();
                double length = VideoEdit.OutputLength(Math.Max(0, end - start), percent) / 1000.0;
                using var owned = CancellationTokenSource.CreateLinkedTokenSource(frames.Token); colorCancellation = owned;
                try
                {
                    var results = await Task.Run(() =>
                    {
                        System.Windows.Media.Imaging.BitmapSource? Adjust(System.Windows.Media.Imaging.BitmapSource? raw, double time)
                        {
                            if (raw is null) return null;
                            var seen = VideoTrimmer.Preview(raw, video.Width, video.Height, picture);
                            return VideoTrimmer.ColorPreview(seen, settings, time, length, owned.Token);
                        }
                        return (Adjust(first, 0), Adjust(last, Math.Max(0, length - 1 / (video.Fps * percent / 100))), Adjust(middle, length / 2));
                    }, owned.Token);
                    if (!closed && !colorDirty) { startFrame.Source = results.Item1; endFrame.Source = results.Item2; colorFrame.Source = results.Item3; }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { if (!closed && !colorDirty) { startFrame.Source = endFrame.Source = colorFrame.Source = null; frameNote.Text = app.L("Windows could not show a frame from this video. You can still trim it."); } }
                finally { if (ReferenceEquals(colorCancellation, owned)) colorCancellation = null; }
            }
        }
        void PictureChanged() { Check(); Show(); }
        turn.SelectionChanged += (_, _) => PictureChanged(); shape.SelectionChanged += (_, _) => PictureChanged();
        flipH.Checked += (_, _) => PictureChanged(); flipH.Unchecked += (_, _) => PictureChanged();
        flipV.Checked += (_, _) => PictureChanged(); flipV.Unchecked += (_, _) => PictureChanged();
        position.ValueChanged += (_, _) => PictureChanged(); speed.SelectionChanged += (_, _) => PictureChanged();
        look.SelectionChanged += (_, _) => PictureChanged();
        foreach (var control in new[] { lookStrength, exposure, contrast, saturation, warmth, fadeIn, fadeOut }) control.ValueChanged += (_, _) => PictureChanged();
        volume.ValueChanged += (_, _) => Check(); musicGain.ValueChanged += (_, _) => Check();
        musicLoop.Checked += (_, _) => Check(); musicLoop.Unchecked += (_, _) => Check();
        musicDuck.Checked += (_, _) => Check(); musicDuck.Unchecked += (_, _) => Check();

        // Shows the frames for the times as typed once typing pauses; a time that isn't valid keeps its last frame.
        async Task Load()
        {
            var token = frames.Token;
            Image? loading = null;
            try
            {
                while (dirty && !closed)
                {
                    dirty = false;
                    await Task.Delay(250, token);
                    if (dirty) continue;
                    long? start = VideoTrim.Parse(startText.Text), end = VideoTrim.Parse(endText.Text);
                    frameNote.Text = "";
                    // A time that isn't valid shows no frame rather than an old one.
                    if (start is { } s && s < video.Duration)
                    {
                        loading = startFrame;
                        var picture = await Task.Run(() => video.FrameAsync(s, 360, token), token);
                        if (closed) return; startRaw = picture;
                    }
                    else startRaw = null;
                    Show();
                    if (end is { } e && e > 0 && e <= video.Duration)
                    {
                        loading = endFrame;
                        var picture = await Task.Run(() => video.FrameAsync(e - 1, 360, token), token);
                        if (closed) return; endRaw = picture;
                    }
                    else endRaw = null;
                    Show();
                    loading = colorFrame;
                    if (start is { } a && end is { } b && b > a && b <= video.Duration)
                    {
                        var middle = await Task.Run(() => video.FrameAsync(a + (b - a) / 2, 360, token), token);
                        if (closed) return; colorRaw = middle;
                    }
                    else colorRaw = null;
                    Show();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!closed && loading is not null) { if (loading == startFrame) startRaw = null; else if (loading == endFrame) endRaw = null; else colorRaw = null; Show(); } if (!closed) frameNote.Text = app.L("Windows could not show a frame from this video. You can still trim it."); }
        }

        var body = new StackPanel();
        body.Children.Add(new TextBox { Text = video.Path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels, {2}"), video.Width, video.Height, Time(video.Duration)), 14));
        var times = new Grid();
        times.ColumnDefinitions.Add(new ColumnDefinition()); times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); times.ColumnDefinitions.Add(new ColumnDefinition());
        StackPanel Column(string label, TextBox box, Image frame, int column)
        {
            var panel = new StackPanel(); panel.Children.Add(UI.Text(label)); panel.Children.Add(box); panel.Children.Add(frame);
            Grid.SetColumn(panel, column); times.Children.Add(panel); return panel;
        }
        Column(app.L("Start time"), startText, startFrame, 0); Column(app.L("End time"), endText, endFrame, 2);
        body.Children.Add(times);
        body.Children.Add(frameNote);
        soundBody.Children.Insert(0, sound);
        soundBody.Children.Insert(1, reduceNoise); soundBody.Children.Insert(2, noiseSettingsButton);
        soundBody.Children.Add(UI.Text(app.L("Noise reduction uses a noise-only sample within the kept part. It runs before speed changes; removing the sound disables it. Noise reduction itself adds no picture encoding."), 12));
        if (!video.HasSound) body.Children.Add(UI.Text(app.L("This video has no sound track that Windows can decode."), 12));
        body.Children.Add(UI.Text(app.L("Picture"), 16));
        if (!video.PictureChanges) { foreach (var control in new Control[] { turn, flipH, flipV, shape, position }) control.IsEnabled = false; body.Children.Add(UI.Text(app.L(VideoTrimmer.NoPictureChanges), 12)); }
        body.Children.Add(UI.Text(app.L("Turn"))); body.Children.Add(turn); body.Children.Add(flipH); body.Children.Add(flipV);
        body.Children.Add(UI.Text(app.L("Crop to shape"))); body.Children.Add(shape);
        body.Children.Add(UI.Text(app.L("Crop position (left to right, or top to bottom)"))); body.Children.Add(position);
        body.Children.Add(UI.Text(app.L("Speed"), 16)); body.Children.Add(speed); body.Children.Add(soundNote);
        var pages = new TabControl { Margin = new Thickness(0, 12, 0, 12) };
        pages.Items.Add(new TabItem { Header = app.L("Color"), Content = colorBody });
        pages.Items.Add(new TabItem { Header = app.L("Sound"), Content = soundBody });
        body.Children.Add(pages);
        body.Children.Add(UI.Text(app.L("Turns and flips apply to the picture as you see it, then the crop takes the largest part of that shape. Sizes are rounded down to even pixels. A crop or turn changes the size of the new video. Another speed keeps every frame and changes how long each one shows (so 4× has four times the frame rate, and 0.25× shows each frame four times as long); nothing is blended or dropped.")));
        body.Children.Add(UI.Text(app.L("Type times as seconds, minutes:seconds or hours:minutes:seconds, with up to three decimals (for example 1:05.5). The frames shown are about the first and the last kept frame; they can be a frame off.")));
        body.Children.Add(UI.Text(app.L("Windows encodes the kept part as a new H.264/AAC MP4 at the original size and frame rate, cut at the exact times. It is re-encoded, so it takes time and loses a little quality; HDR becomes SDR, and extra sound tracks, chapters and metadata are not kept. Up to 3840 × 2160 (either way round) and two hours.")));
        body.Children.Add(UI.Text(app.L("The new file is saved beside the original as “name trimmed.mp4” without replacing existing files. The original stays unchanged. Cancelling saves no video.")));
        body.Children.Add(UI.Text(app.L("When the picture, speed, color, fades, volume or music changes, it is saved as “name edited.mp4” instead.")));
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        Closed += (_, _) => { closed = true; frames.Cancel(); };
        Loaded += (_, _) => { Changed(); startText.Focus(); startText.SelectAll(); };
    }

    /// Stops making frames and waits for the one in progress, so the video can be closed.
    public async Task FinishAsync()
    {
        closed = true; frames.Cancel();
        try { await Task.WhenAll(pending, noisePending, colorPending); } catch (OperationCanceledException) { }
        frames.Dispose();
    }
}
