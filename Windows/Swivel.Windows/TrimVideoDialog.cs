using System;
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
/// window's FinishAsync waits for the last one before the video is closed.
internal sealed class TrimVideoDialog : Window
{
    public VideoTrimPlan? Plan { get; private set; }
    private readonly CancellationTokenSource frames = new();
    private Task pending = Task.CompletedTask;
    private bool dirty, closed;

    public TrimVideoDialog(App app, VideoTrimmer video)
    {
        Title = app.L("Trim Video…"); Width = 720; Height = 680; MinWidth = 460; MinHeight = 400;
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

        VideoTrimPlan? Check()
        {
            long? start = VideoTrim.Parse(startText.Text), end = VideoTrim.Parse(endText.Text);
            try
            {
                if (start is null || end is null) throw new UserError(VideoTrim.BadTime);
                var plan = VideoTrim.Plan(start.Value, end.Value, video.Duration, sound.IsChecked == true, video.HasSound);
                status.Text = string.Format(app.L("Keeps {0}, from {1} to {2}."), Time(plan.Length), Time(plan.Start), Time(plan.End))
                    + (plan.KeepSound || !video.HasSound ? "" : " " + app.L("The sound is removed."));
                trim.IsEnabled = true;
                return plan;
            }
            catch (UserError error) { status.Text = app.L(error.Message); trim.IsEnabled = false; return null; }
        }
        void Changed() { Check(); dirty = true; if (pending.IsCompleted && !closed) pending = Load(); }
        startText.TextChanged += (_, _) => Changed(); endText.TextChanged += (_, _) => Changed();
        sound.Checked += (_, _) => Check(); sound.Unchecked += (_, _) => Check();

        // Shows the frames for the times as typed once typing pauses; a time that isn't valid keeps its last frame.
        async Task Load()
        {
            var token = frames.Token;
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
                        var picture = await Task.Run(() => video.FrameAsync(s, 360, token), token);
                        if (closed) return; startFrame.Source = picture;
                    }
                    else startFrame.Source = null;
                    if (end is { } e && e > 0 && e <= video.Duration)
                    {
                        var picture = await Task.Run(() => video.FrameAsync(e - 1, 360, token), token);
                        if (closed) return; endFrame.Source = picture;
                    }
                    else endFrame.Source = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!closed) frameNote.Text = app.L("Windows could not show a frame from this video. You can still trim it."); }
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
        body.Children.Add(UI.Text(app.L("Type times as seconds, minutes:seconds or hours:minutes:seconds, with up to three decimals (for example 1:05.5). The frames shown are about the first and the last kept frame (exact for videos with a steady frame rate).")));
        body.Children.Add(UI.Text(app.L("Windows encodes the kept part as a new H.264/AAC MP4 at the original size and frame rate, cut at the exact times. It is re-encoded, so it takes time and loses a little quality; HDR becomes SDR, and extra sound tracks, chapters and metadata are not kept. Up to 3840 × 2160 (either way round) and two hours.")));
        body.Children.Add(UI.Text(app.L("The new file is saved beside the original as “name trimmed.mp4” without replacing existing files. The original stays unchanged. Cancelling saves no video.")));
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
