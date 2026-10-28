using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Swivel.Windows;

/// One video held open for Trim Video…: Windows' media editing API shows frames for the chosen
/// start and end, then encodes the kept part (optionally without sound) as a new H.264/AAC MP4 in
/// a private temporary folder, which is published beside the original as `name trimmed.mp4`.
public sealed class VideoTrimmer : IDisposable
{
    public const string Failed = "Windows could not encode the trimmed video. No file was saved.";
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1"); // MF_MT_VIDEO_ROTATION
    private readonly NativeFiles.Input input;
    private readonly StorageFile file;
    private readonly MediaComposition frames;
    private readonly SemaphoreSlim frameLock = new(1, 1);
    private readonly string workspace;
    public string Path => input.Path;
    public int Width { get; }
    public int Height { get; }
    /// Whole milliseconds.
    public long Duration { get; }
    public bool HasSound { get; }
    public bool Hours => Duration >= 3_600_000;
    private readonly uint frameNumerator, frameDenominator, bitrate;

    private VideoTrimmer(NativeFiles.Input input, StorageFile file, MediaClip clip, string workspace, VideoEncodingProperties video, long duration)
    {
        this.input = input; this.file = file; this.workspace = workspace;
        frames = new MediaComposition(); frames.Clips.Add(clip);
        Width = (int)video.Width; Height = (int)video.Height; Duration = duration; HasSound = clip.EmbeddedAudioTracks.Count > 0;
        frameNumerator = video.FrameRate.Numerator; frameDenominator = video.FrameRate.Denominator; bitrate = video.Bitrate;
    }

    public static async Task<VideoTrimmer> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The held read handle denies writers and deleters, so the file Windows opens by path
        // below is the one checked here, until the window closes.
        var input = NativeFiles.OpenRead(path);
        string? workspace = null;
        try
        {
            StorageFile file; MediaClip clip; VideoEncodingProperties video;
            try
            {
                file = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
                clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
                video = clip.GetVideoEncodingProperties();
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(VideoSubtitles.Unreadable); }
            if (video is null || video.Width == 0 || video.Height == 0) throw new UserError(VideoSubtitles.Unreadable);
            if (video.Properties.TryGetValue(RotationKey, out var turn) && turn is uint degrees && degrees % 360 != 0) throw new UserError(VideoSubtitles.Rotated);
            double seconds = clip.OriginalDuration.TotalSeconds;
            SubtitleBurn.CheckVideo((int)video.Width, (int)video.Height, seconds);
            workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Swivel-trim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            return new VideoTrimmer(input, file, clip, workspace, video, VideoTrim.Duration(seconds));
        }
        catch
        {
            input.Dispose();
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// The frame Windows shows at a time (clamped into the video), at most `box` pixels on its
    /// longer side, frozen for the UI thread. One frame is made at a time.
    public async Task<BitmapSource> FrameAsync(long milliseconds, int box, CancellationToken token)
    {
        double scale = Math.Min(1, box / (double)Math.Max(Width, Height));
        int w = Math.Max(2, (int)Math.Round(Width * scale)), h = Math.Max(2, (int)Math.Round(Height * scale));
        long at = Math.Clamp(milliseconds, 0, Math.Max(0, Duration - 1));
        await frameLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var image = await NativeAsync.Join(frames.GetThumbnailAsync(TimeSpan.FromMilliseconds(at), w, h, VideoFramePrecision.NearestFrame), token).ConfigureAwait(false);
            using var stream = image.AsStreamForRead();
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        finally { frameLock.Release(); }
    }

    /// The last frame inside the kept part: just before its end (or its start when very short).
    public long LastFrameBefore(long end, long start)
    {
        double fps = frameNumerator > 0 && frameDenominator > 0 ? (double)frameNumerator / frameDenominator : 30;
        long step = Math.Max(1, (long)Math.Ceiling(1000 / Math.Max(1, fps)));
        return Math.Max(start, end - step);
    }

    /// Encodes the kept part and publishes `name trimmed.mp4`. Progress runs from 0 to 1.
    internal async Task<string> TrimAsync(VideoTrimPlan plan, Action<double>? progress, CancellationToken token)
    {
        if (plan.End > Duration) throw new UserError(VideoTrim.OutOfRange);
        string rendered = System.IO.Path.Combine(workspace, "trimmed.mp4");
        try
        {
            var clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
            clip.TrimTimeFromStart = TimeSpan.FromMilliseconds(plan.Start);
            var tail = clip.OriginalDuration - TimeSpan.FromMilliseconds(plan.End);
            clip.TrimTimeFromEnd = tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
            // Without sound the profile has no audio stream at all.
            bool sound = plan.KeepSound && HasSound;
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)Width; profile.Video.Height = (uint)Height;
            if (frameNumerator > 0 && frameDenominator > 0) { profile.Video.FrameRate.Numerator = frameNumerator; profile.Video.FrameRate.Denominator = frameDenominator; }
            double fps = frameNumerator > 0 && frameDenominator > 0 ? (double)frameNumerator / frameDenominator : 30;
            // At least the original's bit rate, so the kept part loses as little as re-encoding allows.
            profile.Video.Bitrate = (uint)Math.Clamp(Math.Max(bitrate, Width * (double)Height * fps * 0.1), 1_000_000, 100_000_000);
            if (!sound) profile.Audio = null;
            var output = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
            var target = await NativeAsync.Join(output.CreateFileAsync("trimmed.mp4", CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            TranscodeFailureReason reason;
            try { reason = await NativeAsync.Join(composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile), value => progress?.Invoke(value / 100), token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(Failed); }
            if (reason != TranscodeFailureReason.None) throw new UserError(Failed);
            token.ThrowIfCancellationRequested();
            return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " trimmed", "mp4", stream =>
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
        frameLock.Dispose();
        try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
