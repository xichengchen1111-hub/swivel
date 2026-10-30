using System;

namespace Swivel.Core;

/// Upscale Video 2×: every frame goes through the same Real-ESRGAN tiles as a still (Upscale),
/// 2× like the Mac's video upscaling. Input up to 1920 wide and 1080 tall, so the result stays
/// within 3840 × 2160, which Windows' H.264 encoder takes (it stops at 2304 lines, so a 1080 × 1920
/// portrait video, 3840 lines once doubled, is refused); its smallest frame is 64 × 64, hence 32 in. Each frame costs `Upscale.Tiles` model blocks; without a graphics card a
/// block takes 10–20 s (measured on the CI runner), so the length is limited by blocks as well as
/// by time, and the dialog shows an estimate from a block measured on the video's first frame.
public static class VideoUpscale
{
    public const int Factor = 2, MaxWidth = 1920, MaxHeight = 1080, MinSide = 32;
    /// H.264 level 5.2's macroblocks per second (Windows' encoder goes up to level 5.2): 4K up to 64 fps.
    public const long MaxMacroblocksPerSecond = 2_073_600;
    public const double MaxFps = 120;
    /// Ten minutes keeps the decoded PCM16 sound (48 kHz stereo: 115 MB) well inside the WAV limits.
    public const double MaxSeconds = 600;
    /// 300,000 blocks is about a day at 0.3 s a block (a graphics card); on a processor the
    /// estimate shown before starting says how long it would take.
    public const long MaxBlocks = 300_000;
    public const string TooLarge = "Upscale Video 2× takes videos from 32 × 32 up to 1920 pixels wide and 1080 pixels tall, so the result stays within 3840 × 2160 (the most Windows' H.264 encoder takes).";
    public const string TooFast = "This video has too many frames per second for Windows' H.264 encoder at twice its size. Use a video with a lower frame rate (4K output takes up to 64 frames per second).";
    public const string TooLong = "Upscale Video 2× takes videos up to 10 minutes and 300,000 model blocks (frames × blocks per frame). Trim the video first or use a smaller one.";
    public const string Unreadable = "Windows could not read this video's frames. Choose an MP4, MOV or M4V file that plays in the Windows Media Player or Films & TV app.";

    /// The output size; refuses sizes outside the limits.
    public static (int Width, int Height) Dimensions(int width, int height)
    {
        if (width < MinSide || height < MinSide || width > MaxWidth || height > MaxHeight) throw new UserError(TooLarge);
        return (width * Factor, height * Factor);
    }

    /// Frames in `seconds` at an average `fps`, counting a last partial frame.
    public static long Frames(double seconds, double fps)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(fps) || fps <= 0 || fps > MaxFps) throw new UserError(Unreadable);
        if (seconds > MaxSeconds) throw new UserError(TooLong);
        // A millionth of a frame of slack, so 1.000 s at 10 fps is 10 frames, not 11.
        return Math.Max(1, (long)Math.Ceiling(seconds * fps - 1e-6));
    }

    /// Model blocks for `frames` frames of this size.
    public static long Blocks(int width, int height, long frames) => checked(Upscale.Tiles(width, height) * frames);

    /// Refuses a frame rate whose doubled frames pass H.264 level 5.2 (16 × 16 macroblocks, rounded up).
    public static void CheckRate(int width, int height, double fps)
    {
        var (w, h) = Dimensions(width, height);
        if (!double.IsFinite(fps) || fps <= 0 || fps > MaxFps) throw new UserError(Unreadable);
        if ((long)((w + 15) / 16) * ((h + 15) / 16) * fps > MaxMacroblocksPerSecond) throw new UserError(TooFast);
    }

    /// Refuses a video outside the size, rate, length or block limits; returns its estimated frames.
    public static long Check(int width, int height, double seconds, double fps)
    {
        CheckRate(width, height, fps);
        long frames = Frames(seconds, fps);
        if (Blocks(width, height, frames) > MaxBlocks) throw new UserError(TooLong);
        return frames;
    }

    /// Whether `frames` decoded frames still stay within the block limit (a variable frame rate
    /// can hold more frames than the average suggests).
    public static bool WithinBlocks(int width, int height, long frames) => frames <= MaxBlocks / Upscale.Tiles(width, height);

    /// The time for the whole video from one measured block.
    public static TimeSpan Estimate(TimeSpan perBlock, int width, int height, long frames)
    {
        if (perBlock < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perBlock));
        double ticks = (double)perBlock.Ticks * Blocks(width, height, frames);
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    /// The time left after `done` of about `total` frames took `elapsed`; null before the first frame.
    public static TimeSpan? Left(TimeSpan elapsed, long done, long total)
    {
        if (done <= 0) return null;
        double ticks = (double)elapsed.Ticks / done * Math.Max(0, total - done);
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    /// H.264 bit rate: about 0.1 bit per output pixel per frame and at least four times the
    /// source's (four times the pixels), between 1 and 100 Mbit/s.
    public static uint Bitrate(int outputWidth, int outputHeight, double fps, uint source)
        => (uint)Math.Clamp(Math.Max(source * 4.0, outputWidth * (double)outputHeight * fps * 0.1), 1_000_000, 100_000_000);

    /// Whether two lengths are within one frame (with a millisecond for rounding).
    public static bool WithinFrame(double seconds, double expected, double fps)
        => fps > 0 && Math.Abs(seconds - expected) <= 1 / fps + 0.001;

    /// Time text for the estimate and progress: seconds under a minute, minutes under two hours, then hours.
    public static (string Format, long Value) Duration(TimeSpan time)
    {
        double s = Math.Ceiling(Math.Max(0, time.TotalSeconds));
        if (s < 60) return ("{0} s", (long)s);
        if (s < 7200) return ("{0} min", (long)Math.Ceiling(s / 60));
        return ("{0} h", (long)Math.Min(long.MaxValue, Math.Ceiling(s / 3600)));
    }
}
