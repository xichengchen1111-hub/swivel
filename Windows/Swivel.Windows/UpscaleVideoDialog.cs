using System;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Confirms Upscale Video 2×… with the sizes, the frames and an estimate measured on this PC.
internal sealed class UpscaleVideoDialog : Window
{
    public UpscaleVideoDialog(App app, VideoUpscaler video, bool graphics, TimeSpan perBlock)
    {
        Title = app.L("Upscale Video 2×…"); Width = 660; Height = 600; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        string Time(TimeSpan time) { var (format, value) = VideoUpscale.Duration(time); return string.Format(app.L(format), value); }
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var start = UI.Button(app.L("Upscale"), () => DialogResult = true); start.IsDefault = true;
        buttons.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Upscale Video 2×…"), 22));
        var (ow, oh) = video.Output;
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} becomes {2} × {3}. {4} long, about {5} frames of {6} model block(s) each."),
            video.Width, video.Height, ow, oh, Time(TimeSpan.FromSeconds(video.Seconds)), video.Frames, Upscale.Tiles(video.Width, video.Height))));
        body.Children.Add(UI.Text(string.Format(app.L(graphics
            ? "One block of the first frame took {0} on this PC's graphics card."
            : "One block of the first frame took {0} on this PC's processor (Windows ML found no graphics card it can use)."), Time(perBlock))));
        body.Children.Add(UI.Text(string.Format(app.L("Estimated time: about {0}. Progress shows the time left as frames finish."),
            Time(VideoUpscale.Estimate(perBlock, video.Width, video.Height, video.Frames))), 16));
        body.Children.Add(UI.Text(app.L("Uses the Real-ESRGAN model on this PC (no upload). It sharpens and can invent small textures that were not in the original, so check faces, text and fine patterns.")));
        body.Children.Add(UI.Text(app.L("Saved as an H.264 MP4 with each frame at its original time; the sound is re-encoded as AAC. 8-bit SDR; metadata, chapters, subtitles and any other tracks are not kept.")));
        body.Children.Add(UI.Text(app.L("Takes videos up to 1920 pixels wide and 1080 tall, 10 minutes and 300,000 model blocks (4K output up to 64 frames per second). Videos stored with a rotation flag or with non-square pixels are not supported.")));
        body.Children.Add(UI.Text(app.L("The result is saved beside the original as “name 2x.mp4” without replacing anything. Cancel stops within one block and saves nothing.")));
        body.Children.Add(UI.Text(video.Path, 12));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => start.Focus();
    }
}
