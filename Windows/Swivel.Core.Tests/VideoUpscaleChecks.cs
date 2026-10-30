using System;
using System.Linq;
using Swivel.Core;

internal static class VideoUpscaleChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Sizes: 2× of 32 × 32 up to 1920 wide and 1080 tall, never past 3840 × 2160 (Windows' H.264 encoder stops at 2304 lines).
        check(VideoUpscale.Dimensions(64, 48) == (128, 96), "Upscale Video doubles 64 × 48");
        check(VideoUpscale.Dimensions(1920, 1080) == (3840, 2160) && VideoUpscale.Dimensions(608, 1080) == (1216, 2160), "Upscale Video takes 1920 × 1080 and portrait videos up to 1080 tall");
        check(VideoUpscale.Dimensions(32, 32) == (64, 64) && VideoUpscale.Dimensions(33, 35) == (66, 70), "Odd and minimum sizes still give even outputs of at least 64 × 64");
        foreach (var (w, h) in new[] { (31, 100), (100, 31), (16, 16), (1921, 1080), (1920, 1081), (1080, 1920), (1200, 1200), (0, 0), (-2, 64), (3840, 2160) })
            reject(() => VideoUpscale.Dimensions(w, h), $"Upscale Video refuses {w} × {h}");
        for (int w = 16; w <= 1960; w += 37)
            for (int h = 16; h <= 1960; h += 41)
            {
                bool fits = w is >= 32 and <= 1920 && h is >= 32 and <= 1080;
                bool ok = true; try { var (ow, oh) = VideoUpscale.Dimensions(w, h); ok = fits && ow == 2 * w && oh == 2 * h && ow <= 3840 && oh <= 2160 && ow >= 64 && oh >= 64; } catch (UserError) { ok = !fits; }
                if (!ok) { check(false, $"Upscale Video size rule at {w} × {h}"); return; }
            }
        check(true, "Upscale Video size rule over a grid of sizes");

        // Frame rate: H.264 level 5.2 at the doubled size (4K: 32,400 macroblocks, up to 64 fps).
        VideoUpscale.CheckRate(1920, 1080, 64); VideoUpscale.CheckRate(1280, 720, 120); VideoUpscale.CheckRate(64, 48, 120);
        check(true, "4K output up to 64 fps and 1440p up to 120 fps pass");
        reject(() => VideoUpscale.CheckRate(1920, 1080, 64.01), "4K output over 64 fps is refused");
        reject(() => VideoUpscale.Check(1920, 1080, 1, 120), "Check refuses 1080p at 120 fps");
        reject(() => VideoUpscale.CheckRate(64, 48, 0), "A zero frame rate is refused");

        // Frame counts from length and average rate.
        check(VideoUpscale.Frames(1.0, 10) == 10 && VideoUpscale.Frames(1.0, 30) == 30 && VideoUpscale.Frames(1.0001, 10) == 11 && VideoUpscale.Frames(0.01, 30) == 1, "Frames counts whole and partial frames");
        check(VideoUpscale.Frames(600, 30000.0 / 1001) == 17983, "Ten minutes at 29.97 fps is 17,983 frames");
        foreach (var (s, f) in new[] { (0.0, 30.0), (-1.0, 30.0), (double.NaN, 30.0), (double.PositiveInfinity, 30.0), (1.0, 0.0), (1.0, double.NaN), (1.0, 121.0), (600.001, 1.0) })
            reject(() => VideoUpscale.Frames(s, f), $"Frames refuses {s} s at {f} fps");

        // Blocks and the block limit: 1080p is 15 × 9 blocks a frame.
        check(VideoUpscale.Blocks(1920, 1080, 1) == 135 && VideoUpscale.Blocks(64, 48, 10) == 10 && VideoUpscale.Blocks(129, 128, 2) == 4, "Blocks per frame follow the 128-pixel tile centres");
        check(VideoUpscale.Check(64, 48, 1, 10) == 10, "A small one-second video passes");
        check(VideoUpscale.Check(1920, 1080, 60, 30) == 1800, "A minute of 1080p at 30 fps passes (243,000 blocks)");
        reject(() => VideoUpscale.Check(1920, 1080, 75, 30), "75 s of 1080p at 30 fps passes the block limit (303,750)");
        check(VideoUpscale.Check(640, 360, 600, 30) == 18000, "Ten minutes of 640 × 360 at 30 fps passes (270,000 blocks)");
        reject(() => VideoUpscale.Check(64, 48, 600.5, 30), "Over ten minutes is refused");
        reject(() => VideoUpscale.Check(4000, 48, 1, 30), "Check refuses sizes too");
        check(VideoUpscale.WithinBlocks(1920, 1080, 2222) && !VideoUpscale.WithinBlocks(1920, 1080, 2223) && VideoUpscale.WithinBlocks(64, 48, 300_000) && !VideoUpscale.WithinBlocks(64, 48, 300_001), "The decoded-frame limit is the block limit");

        // Estimate: one measured block × blocks per frame × frames.
        check(VideoUpscale.Estimate(TimeSpan.FromSeconds(15), 64, 48, 10) == TimeSpan.FromSeconds(150), "Ten one-block frames at 15 s a block take 150 s");
        check(VideoUpscale.Estimate(TimeSpan.FromSeconds(0.2), 1920, 1080, 1800) == TimeSpan.FromSeconds(0.2 * 243_000), "A 1080p minute at 0.2 s a block");
        check(VideoUpscale.Estimate(TimeSpan.Zero, 64, 48, 10) == TimeSpan.Zero && VideoUpscale.Estimate(TimeSpan.MaxValue, 1920, 1080, 1800) == TimeSpan.MaxValue, "Estimate saturates instead of overflowing");
        reject(() => VideoUpscale.Estimate(TimeSpan.FromSeconds(-1), 64, 48, 1), "A negative block time is refused");
        check(VideoUpscale.Left(TimeSpan.FromSeconds(30), 0, 10) is null && VideoUpscale.Left(TimeSpan.FromSeconds(30), 2, 10) == TimeSpan.FromSeconds(120) && VideoUpscale.Left(TimeSpan.FromSeconds(30), 12, 10) == TimeSpan.Zero && VideoUpscale.Left(TimeSpan.MaxValue, 1, long.MaxValue) == TimeSpan.MaxValue, "Time left from the frames done so far");
        check(VideoUpscale.Duration(TimeSpan.FromSeconds(0.2)) == ("{0} s", 1) && VideoUpscale.Duration(TimeSpan.FromSeconds(59)) == ("{0} s", 59) && VideoUpscale.Duration(TimeSpan.FromSeconds(60)) == ("{0} min", 1)
            && VideoUpscale.Duration(TimeSpan.FromSeconds(61)) == ("{0} min", 2) && VideoUpscale.Duration(TimeSpan.FromHours(2)) == ("{0} h", 2) && VideoUpscale.Duration(TimeSpan.MaxValue).Value > 0, "Durations read as seconds, minutes or hours");

        // Bit rate and length tolerance.
        check(VideoUpscale.Bitrate(128, 96, 10, 0) == 1_000_000 && VideoUpscale.Bitrate(3840, 2160, 60, 0) == 49_766_400 && VideoUpscale.Bitrate(3840, 2160, 30, 40_000_000) == 100_000_000 && VideoUpscale.Bitrate(1280, 720, 30, 2_000_000) == 8_000_000, "Bit rate: 0.1 bit per pixel, at least four times the source, 1–100 Mbit/s");
        check(VideoUpscale.WithinFrame(1.1, 1.0, 10) && !VideoUpscale.WithinFrame(1.102, 1.0, 10) && VideoUpscale.WithinFrame(0.967, 1.0, 30) && !VideoUpscale.WithinFrame(1.0, 1.0, 0), "Lengths match within one frame");
        check(Catalog.Actions.Any(a => a.Id == "video.upscale" && a.SingleFile && a.Extensions is { } e && e.SequenceEqual(new[] { "mp4", "mov", "m4v" })), "Upscale Video 2×… is offered for one MP4, MOV or M4V");
    }
}
