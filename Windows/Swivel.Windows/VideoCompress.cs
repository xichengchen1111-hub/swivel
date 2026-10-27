using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Swivel.Windows;

/// Compress to a size, for agents: Windows' media editing API re-encodes a video as H.264/AAC
/// MP4 at the rates VideoBudget works out, in a private temporary folder. A result that comes out
/// too big is tried once more at a rate scaled by the overshoot; only a file that fits is
/// published beside the original.
internal static class VideoCompress
{
    public const string Failed = "Windows could not encode the compressed video. No file was saved.";
    public const string Changed = "The file changed since planning. No file was made for it; plan again.";
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1"); // MF_MT_VIDEO_ROTATION

    public static async Task<VideoProbe> ProbeAsync(string path, CancellationToken token)
    {
        using var input = NativeFiles.OpenRead(path);
        var (_, _, probe) = await OpenAsync(path, token).ConfigureAwait(false);
        return probe;
    }

    public static async Task<string> CompressAsync(string path, long maxBytes, FileStamp expected, CancellationToken token)
    {
        // The held read handle denies writers and deleters, so the file Windows opens by path is
        // the one checked, from planning to publishing.
        using var input = NativeFiles.OpenRead(path);
        if (!input.Stamp.SameObjectAndContent(expected)) throw new McpToolError(Changed);
        var (file, video, probe) = await OpenAsync(path, token).ConfigureAwait(false);
        var attempt = VideoBudget.Plan(maxBytes, probe);
        SweepStale();
        string workspace = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        FileStream? result = null;
        try
        {
            // Measured and copied through one handle that denies writers.
            result = await RenderAsync(file, video, attempt, workspace, "first.mp4", token).ConfigureAwait(false);
            if (result.Length > maxBytes)
            {
                long first = result.Length;
                var retry = VideoBudget.Retry(attempt, maxBytes, first, probe);
                if (retry == attempt) throw TooBig(first); // already the lowest rate and size
                result.Dispose(); result = null;
                result = await RenderAsync(file, video, retry, workspace, "second.mp4", token).ConfigureAwait(false);
                if (result.Length > maxBytes) throw TooBig(Math.Min(first, result.Length));
            }
            if (result.Length == 0) throw new McpToolError(Failed);
            token.ThrowIfCancellationRequested();
            var source = result;
            return NativeFiles.WriteBeside(input, Path.GetFileNameWithoutExtension(path) + " compressed", "mp4", stream =>
            {
                var buffer = new byte[1 << 20]; int read;
                while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); stream.Write(buffer, 0, read); }
            }, token);
        }
        finally
        {
            result?.Dispose();
            try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private const string Prefix = "Swivel-compress-";
    private static McpToolError TooBig(long size) => new("The target size can't be reached: the smallest video Windows made was " + size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes. No file was saved.");

    /// Workspaces a killed server left behind: removed once they are a day old. Directory.Delete
    /// removes links inside without following them.
    private static void SweepStale()
    {
        try
        {
            foreach (var stale in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(Prefix + "*"))
                if ((stale.Attributes & System.IO.FileAttributes.ReparsePoint) == 0 && stale.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    try { stale.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<(StorageFile File, VideoEncodingProperties Video, VideoProbe Probe)> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
        var probe = new VideoProbe((int)Math.Min(video.Width, int.MaxValue), (int)Math.Min(video.Height, int.MaxValue), clip.OriginalDuration.TotalSeconds, clip.EmbeddedAudioTracks.Count > 0, video.Bitrate);
        VideoBudget.Check(probe);
        return (file, video, probe);
    }

    private static async Task<FileStream> RenderAsync(StorageFile file, VideoEncodingProperties video, VideoTarget target, string workspace, string name, CancellationToken token)
    {
        var composition = new MediaComposition();
        composition.Clips.Add(await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false));
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)target.Width; profile.Video.Height = (uint)target.Height; profile.Video.Bitrate = target.VideoBitrate;
        uint numerator = video.FrameRate.Numerator, denominator = video.FrameRate.Denominator;
        if (numerator > 0 && denominator > 0)
        {
            // The original's frame rate, at most 60 a second.
            if ((double)numerator / denominator > 60) { numerator = 60; denominator = 1; }
            profile.Video.FrameRate.Numerator = numerator; profile.Video.FrameRate.Denominator = denominator;
        }
        if (target.AudioBitrate == 0) profile.Audio = null; else profile.Audio.Bitrate = target.AudioBitrate;
        var folder = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
        var output = await NativeAsync.Join(folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
        TranscodeFailureReason reason;
        try { reason = await NativeAsync.Join(composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile), _ => { }, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new McpToolError(Failed); }
        if (reason != TranscodeFailureReason.None) throw new McpToolError(Failed);
        return new FileStream(Path.Combine(workspace, name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    }
}
