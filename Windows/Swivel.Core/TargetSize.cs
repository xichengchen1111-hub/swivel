using System;
using System.Globalization;
using System.Threading;

namespace Swivel.Core;

/// What Windows reports about a video before it is compressed to a size. Bitrate is the
/// video stream's, 0 when unknown.
public sealed record VideoProbe(int Width, int Height, double Seconds, bool HasSound, uint Bitrate);

/// The bit rates and picture size of one encoding attempt.
public readonly record struct VideoTarget(uint VideoBitrate, uint AudioBitrate, int Width, int Height);

/// Fits a video under a byte ceiling: a total bit rate from its length with 5% and 4 KB left for
/// the container, AAC sound at 96 or 128 kbit/s (Windows' AAC encoder takes only 96, 128, 160 or
/// 192), and a smaller picture when the rate the target allows is low (the steps Swivel for Mac
/// uses). The video rate never goes above the original's own.
public static class VideoBudget
{
    public const double MaxSeconds = 2 * 3600;
    public const uint MinVideoBitrate = 150_000, MaxVideoBitrate = 100_000_000, LowAudioBitrate = 96_000, AudioBitrate = 128_000;
    public const int MaxSide = 8192;
    public const long ContainerBytes = 4096;
    public static readonly string[] Extensions = { "mp4", "mov", "m4v", "wmv" };

    public static bool IsVideo(string path) => Array.IndexOf(Extensions, System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) >= 0;

    public static void Check(VideoProbe video)
    {
        if (video.Width is < 2 or > MaxSide || video.Height is < 2 or > MaxSide || !double.IsFinite(video.Seconds) || video.Seconds <= 0 || video.Seconds > MaxSeconds)
            throw new McpToolError("Swivel compresses videos up to 2 hours long and 8192 pixels per side.");
    }

    /// The smallest ceiling that still leaves the lowest video rate (and 96 kbit/s sound).
    public static long MinimumBytes(VideoProbe video)
    {
        Check(video);
        return (long)Math.Ceiling((MinVideoBitrate + (video.HasSound ? LowAudioBitrate : 0)) * video.Seconds / 8 / 0.95) + ContainerBytes;
    }

    /// The first attempt for a byte ceiling.
    public static VideoTarget Plan(long maxBytes, VideoProbe video)
    {
        Check(video);
        // Only this check refuses, so the smallest target it names is always accepted; For
        // clamps the rate to the lowest one.
        if (maxBytes < MinimumBytes(video))
            throw new McpToolError("The target size is too small for this video's length: it needs at least " + MinimumBytes(video).ToString("N0", CultureInfo.InvariantCulture) + " bytes.");
        double total = (maxBytes - ContainerBytes) * 8.0 / video.Seconds * 0.95;
        uint audio = video.HasSound ? (total < 1_000_000 ? LowAudioBitrate : AudioBitrate) : 0;
        return For(total - audio, audio, video);
    }

    /// A second attempt after the first came out `size` bytes: the total rate scaled down by
    /// how far it overshot, never below the lowest video rate; sound drops to 96 kbit/s once the
    /// total is under 1 Mbit/s. The same target back means there is nothing smaller to try.
    public static VideoTarget Retry(VideoTarget first, long maxBytes, long size, VideoProbe video)
    {
        if (size <= 0 || maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        double total = (double)(first.VideoBitrate + first.AudioBitrate) * maxBytes / size * 0.95;
        uint audio = first.AudioBitrate > 0 && total < 1_000_000 ? LowAudioBitrate : first.AudioBitrate;
        return For(Math.Max(MinVideoBitrate, total - audio), audio, video);
    }

    private static VideoTarget For(double videoRate, uint audio, VideoProbe video)
    {
        // The picture size follows the rate the target allows; the original's rate (when known)
        // only caps the rate, so a low-rate original keeps its size when there is room.
        double ceiling = video.Bitrate > 0 ? Math.Max(MinVideoBitrate, Math.Min(video.Bitrate, MaxVideoBitrate)) : MaxVideoBitrate;
        double allowed = Math.Max(MinVideoBitrate, Math.Floor(videoRate));
        uint rate = (uint)Math.Min(allowed, ceiling);
        int cap = allowed < 500_000 ? 640 : allowed < 1_000_000 ? 854 : allowed < 2_500_000 ? 1280 : 1920;
        double scale = Math.Min(1, (double)cap / Math.Max(video.Width, video.Height));
        // H.264 needs even sides.
        int width = Math.Max(2, (int)(video.Width * scale) / 2 * 2), height = Math.Max(2, (int)(video.Height * scale) / 2 * 2);
        return new VideoTarget(rate, audio, width, height);
    }
}
