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
    private Task pending = Task.CompletedTask;
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
        var turn = new ComboBox { ItemsSource = new[] { app.L("No turn"), app.L("90° clockwise"), app.L("180°"), app.L("90° counterclockwise") }, SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        var flipH = new CheckBox { Content = app.L("Flip left to right"), Margin = new Thickness(0, 4, 0, 2) };
        var flipV = new CheckBox { Content = app.L("Flip upside down"), Margin = new Thickness(0, 2, 0, 4) };
        var shape = new ComboBox { ItemsSource = new[] { app.L("Whole picture"), "16:9", "9:16", "1:1", "4:5", "4:3" }, SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        var position = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, SmallChange = 0.05, LargeChange = 0.25, Margin = new Thickness(0, 4, 0, 4), IsEnabled = false };
        var speed = new ComboBox { ItemsSource = VideoEdit.Speeds.Select(x => (x / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "×").ToArray(), SelectedIndex = Array.IndexOf(VideoEdit.Speeds, 100), MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetName(turn, app.L("Turn")); AutomationProperties.SetName(shape, app.L("Crop to shape"));
        AutomationProperties.SetName(position, app.L("Crop position")); AutomationProperties.SetName(speed, app.L("Speed"));
        var soundNote = UI.Text("", 12);
        // The frames as Windows returned them; shown through the chosen picture change.
        System.Windows.Media.Imaging.BitmapSource? startRaw = null, endRaw = null;
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
        VideoTrimPlan? Check()
        {
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
                var plan = VideoTrim.Plan(start.Value, end.Value, video.Duration, sound.IsChecked == true, video.HasSound, Chosen(), Speed());
                video.CheckSound(plan);
                status.Text = string.Format(app.L("Keeps {0}, from {1} to {2}."), Time(plan.Length), Time(plan.Start), Time(plan.End))
                    + (plan.KeepSound || !video.HasSound ? "" : " " + app.L("The sound is removed."))
                    + (plan.Edited ? " " + string.Format(app.L("The new video is {0} × {1} pixels and {2} long."), geometry.Width, geometry.Height, VideoTrim.Format(plan.OutputLength, plan.OutputLength >= 3_600_000)) : "");
                trim.IsEnabled = true;
                return plan;
            }
            catch (UserError error) { status.Text = app.L(error.Message); trim.IsEnabled = false; return null; }
        }
        void Changed() { Check(); dirty = true; if (pending.IsCompleted && !closed) pending = Load(); }
        startText.TextChanged += (_, _) => Changed(); endText.TextChanged += (_, _) => Changed();
        sound.Checked += (_, _) => Check(); sound.Unchecked += (_, _) => Check();
        void Show()
        {
            VideoPicture? picture;
            try { picture = Chosen(); VideoEdit.Geometry(video.Width, video.Height, picture); } catch (UserError) { picture = null; }
            startFrame.Source = startRaw is null ? null : VideoTrimmer.Preview(startRaw, video.Width, video.Height, picture);
            endFrame.Source = endRaw is null ? null : VideoTrimmer.Preview(endRaw, video.Width, video.Height, picture);
        }
        void PictureChanged() { Check(); Show(); }
        turn.SelectionChanged += (_, _) => PictureChanged(); shape.SelectionChanged += (_, _) => PictureChanged();
        flipH.Checked += (_, _) => PictureChanged(); flipH.Unchecked += (_, _) => PictureChanged();
        flipV.Checked += (_, _) => PictureChanged(); flipV.Unchecked += (_, _) => PictureChanged();
        position.ValueChanged += (_, _) => PictureChanged(); speed.SelectionChanged += (_, _) => Check();

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
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!closed && loading is not null) { if (loading == startFrame) startRaw = null; else endRaw = null; Show(); } if (!closed) frameNote.Text = app.L("Windows could not show a frame from this video. You can still trim it."); }
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
        body.Children.Add(sound);
        if (!video.HasSound) body.Children.Add(UI.Text(app.L("This video has no sound track that Windows can decode."), 12));
        body.Children.Add(UI.Text(app.L("Picture"), 16));
        if (!video.PictureChanges) { foreach (var control in new Control[] { turn, flipH, flipV, shape, position }) control.IsEnabled = false; body.Children.Add(UI.Text(app.L(VideoTrimmer.NoPictureChanges), 12)); }
        body.Children.Add(UI.Text(app.L("Turn"))); body.Children.Add(turn); body.Children.Add(flipH); body.Children.Add(flipV);
        body.Children.Add(UI.Text(app.L("Crop to shape"))); body.Children.Add(shape);
        body.Children.Add(UI.Text(app.L("Crop position (left to right, or top to bottom)"))); body.Children.Add(position);
        body.Children.Add(UI.Text(app.L("Speed"), 16)); body.Children.Add(speed); body.Children.Add(soundNote);
        body.Children.Add(UI.Text(app.L("Turns and flips apply to the picture as you see it, then the crop takes the largest part of that shape. Sizes are rounded down to even pixels. A crop or turn changes the size of the new video. Another speed keeps every frame and changes how long each one shows (so 4× has four times the frame rate, and 0.25× shows each frame four times as long); nothing is blended or dropped.")));
        body.Children.Add(UI.Text(app.L("Type times as seconds, minutes:seconds or hours:minutes:seconds, with up to three decimals (for example 1:05.5). The frames shown are about the first and the last kept frame; they can be a frame off.")));
        body.Children.Add(UI.Text(app.L("Windows encodes the kept part as a new H.264/AAC MP4 at the original size and frame rate, cut at the exact times. It is re-encoded, so it takes time and loses a little quality; HDR becomes SDR, and extra sound tracks, chapters and metadata are not kept. Up to 3840 × 2160 (either way round) and two hours.")));
        body.Children.Add(UI.Text(app.L("The new file is saved beside the original as “name trimmed.mp4” without replacing existing files. The original stays unchanged. Cancelling saves no video.")));
        body.Children.Add(UI.Text(app.L("When the picture or speed changes, it is saved as “name edited.mp4” instead.")));
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        Closed += (_, _) => { closed = true; frames.Cancel(); };
        Loaded += (_, _) => { Changed(); startText.Focus(); startText.SelectAll(); };
    }

    /// Stops making frames and waits for the one in progress, so the video can be closed.
    public async Task FinishAsync()
    {
        closed = true; frames.Cancel();
        try { await pending; } catch (OperationCanceledException) { }
        frames.Dispose();
    }
}
