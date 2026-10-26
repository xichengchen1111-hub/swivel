using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Swivel.Windows;

/// One subtitle drawn as a picture: its file, when it shows, and where (video pixels).
internal sealed record SubtitleImage(string Path, long Start, long End, int X, int Y, int Width, int Height);

/// One video held open for Burn In Subtitles…: Windows' media editing API plays the original
/// through, lays each subtitle picture over it at its time and encodes a new H.264/AAC MP4,
/// which is then published beside the original. Pictures and the encoded file live in a private
/// temporary folder that is removed afterwards.
public sealed class VideoSubtitles : IDisposable
{
    public const string NoSound = "This video has no sound track that Windows can decode, so there is nothing to transcribe. Use an SRT file instead.";
    public const string Unreadable = "Windows could not open this video. Choose an MP4, MOV, M4V or WMV file that plays in the Windows Media Player or Films & TV app.";
    public const string Rotated = "This video is stored turned (as phones often record). Turned videos are not supported yet.";
    public const string Failed = "Windows could not encode the subtitled video. No file was saved.";
    public const string TooLong = "A subtitle is too long to fit in three lines at this text size. Choose a smaller text size or shorten it.";
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1"); // MF_MT_VIDEO_ROTATION
    private readonly NativeFiles.Input input;
    private readonly StorageFile file;
    private readonly string workspace;
    public string Path => input.Path;
    public int Width { get; }
    public int Height { get; }
    public double Seconds { get; }
    public bool HasSound { get; }
    private readonly uint frameNumerator, frameDenominator, bitrate;

    private VideoSubtitles(NativeFiles.Input input, StorageFile file, string workspace, VideoEncodingProperties video, double seconds, bool sound)
    {
        this.input = input; this.file = file; this.workspace = workspace;
        Width = (int)video.Width; Height = (int)video.Height; Seconds = seconds; HasSound = sound;
        frameNumerator = video.FrameRate.Numerator; frameDenominator = video.FrameRate.Denominator; bitrate = video.Bitrate;
    }

    public static async Task<VideoSubtitles> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The held read handle denies writers and deleters, so the file Windows opens by path
        // below is the one checked here.
        var input = NativeFiles.OpenRead(path);
        string? workspace = null;
        try
        {
            StorageFile file; MediaClip clip;
            try
            {
                file = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
                clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(Unreadable); }
            VideoEncodingProperties video;
            try { video = clip.GetVideoEncodingProperties(); } catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(Unreadable); }
            if (video is null || video.Width == 0 || video.Height == 0) throw new UserError(Unreadable);
            if (video.Properties.TryGetValue(RotationKey, out var turn) && turn is uint degrees && degrees % 360 != 0) throw new UserError(Rotated);
            double seconds = clip.OriginalDuration.TotalSeconds;
            SubtitleBurn.CheckVideo((int)video.Width, (int)video.Height, seconds);
            workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Swivel-subtitles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            return new VideoSubtitles(input, file, workspace, video, seconds, clip.EmbeddedAudioTracks.Count > 0);
        }
        catch
        {
            input.Dispose();
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// The video's sound, decoded by Windows to 16 kHz mono, for transcription. A second read
    /// handle is held by that document; this one keeps the original pinned meanwhile.
    public async Task<TranscribeDocument> SoundAsync(CancellationToken token)
    {
        if (!HasSound) throw new UserError(NoSound);
        var sound = NativeFiles.OpenRead(input.Path);
        try { return await TranscribeDocument.OpenAsync(sound, true, token).ConfigureAwait(false); }
        catch { sound.Dispose(); throw; }
    }

    /// Draws each cue as a white, centred, at most three-line caption on a translucent dark
    /// plate near the bottom. Runs on an STA thread (WPF text).
    internal IReadOnlyList<SubtitleImage> Draw(IReadOnlyList<SubtitleCue> cues, int size, CancellationToken token)
    {
        var plan = SubtitleBurn.Plan(cues, Seconds);
        var layout = SubtitleBurn.Layout(Width, Height, size);
        var face = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double line = Text("Ag", face, layout.FontPixels, double.PositiveInfinity).Height;
        var images = new List<SubtitleImage>();
        for (int i = 0; i < plan.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var text = Text(plan[i].Text, face, layout.FontPixels, layout.MaxWidth - 2 * layout.Padding);
            if (text.Height > line * (SubtitleBurn.MaxLines + 0.5) || text.Height + 2 * layout.Padding > layout.MaxHeight) throw new UserError(TooLong);
            int w = Math.Min(layout.MaxWidth, (int)Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2 * layout.Padding);
            int h = (int)Math.Ceiling(text.Height) + 2 * layout.Padding;
            w += w & 1; h += h & 1;
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(0, 0, w, h), layout.Padding * 0.6, layout.Padding * 0.6);
                text.MaxTextWidth = w - 2 * layout.Padding;
                draw.DrawText(text, new Point(layout.Padding, layout.Padding));
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            string png = System.IO.Path.Combine(workspace, $"cue {i + 1}.png");
            using (var stream = new FileStream(png, FileMode.CreateNew, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            }
            int x = (Width - w) / 2, y = Height - layout.BottomMargin - h;
            images.Add(new SubtitleImage(png, plan[i].Start, plan[i].End, x - (x & 1), Math.Max(0, y - (y & 1)), w, h));
        }
        return images;
    }

    private static FormattedText Text(string text, Typeface face, double size, double width) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0)
        { MaxTextWidth = width, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.None };

    /// Encodes the original with the pictures laid over it and publishes `name subtitled.mp4`.
    internal async Task<string> BurnAsync(IReadOnlyList<SubtitleImage> images, Action<double>? progress, CancellationToken token)
    {
        string rendered = System.IO.Path.Combine(workspace, "subtitled.mp4");
        try
        {
            var composition = new MediaComposition();
            composition.Clips.Add(await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false));
            var layer = new MediaOverlayLayer();
            foreach (var image in images)
            {
                token.ThrowIfCancellationRequested();
                var picture = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(image.Path), token).ConfigureAwait(false);
                var clip = await NativeAsync.Join(MediaClip.CreateFromImageFileAsync(picture, TimeSpan.FromMilliseconds(image.End - image.Start)), token).ConfigureAwait(false);
                layer.Overlays.Add(new MediaOverlay(clip) { Position = new global::Windows.Foundation.Rect(image.X, image.Y, image.Width, image.Height), Delay = TimeSpan.FromMilliseconds(image.Start), Opacity = 1 });
            }
            composition.OverlayLayers.Add(layer);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)Width; profile.Video.Height = (uint)Height;
            if (frameNumerator > 0 && frameDenominator > 0) { profile.Video.FrameRate.Numerator = frameNumerator; profile.Video.FrameRate.Denominator = frameDenominator; }
            double fps = frameNumerator > 0 && frameDenominator > 0 ? (double)frameNumerator / frameDenominator : 30;
            // At least the original's bit rate, and enough for sharp text (about 0.1 bit per pixel per frame).
            profile.Video.Bitrate = (uint)Math.Clamp(Math.Max(bitrate, Width * (double)Height * fps * 0.1), 1_000_000, 100_000_000);
            if (!HasSound) profile.Audio = null;
            var output = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
            var target = await NativeAsync.Join(output.CreateFileAsync("subtitled.mp4", CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            TranscodeFailureReason reason;
            try { reason = await NativeAsync.Join(composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile), value => progress?.Invoke(value / 100), token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(Failed); }
            if (reason != TranscodeFailureReason.None) throw new UserError(Failed);
            token.ThrowIfCancellationRequested();
            return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " subtitled", "mp4", stream =>
            {
                using var source = new FileStream(rendered, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                if (source.Length == 0) throw new UserError(Failed);
                var buffer = new byte[1 << 20]; int read;
                while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); stream.Write(buffer, 0, read); }
            }, token);
        }
        finally { try { File.Delete(rendered); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void Dispose()
    {
        input.Dispose();
        try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
