using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Swivel.Windows;

internal static class VideoColorSoundNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "video-color-sound"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "colors.mp4"), music = Path.Combine(root, "music.wav");
        VideoTrimNativeChecks.MakeColourVideo(root, source, 320, 240);
        File.WriteAllBytes(music, VideoTrimNativeChecks.Tone(48000, 1));
        byte[] original = SHA256.HashData(File.ReadAllBytes(source)), originalMusic = SHA256.HashData(File.ReadAllBytes(music));
        using (var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            var settings = new VideoColorSound(new(Look: PhotoLook.Mono), .2, .2, 1.8, new VideoMusic(music, .4, true));
            var plan = VideoTrim.Plan(500, 2500, video.Duration, true, speed: 200, effects: settings);
            string output = video.TrimAsync(plan, null, default).GetAwaiter().GetResult();
            var (seconds, sound) = VideoTrimNativeChecks.Probe(output);
            check(Path.GetFileName(output) == "colors edited.mp4" && Math.Abs(seconds - 1) < .1 && sound,
                $"Look/fades/180% volume/music export follows the cut and speed: {seconds:0.000}s, sound {sound}");
            var first = RotatedVideoNativeChecks.Frame(output, 320, 240, 0);
            var middle = RotatedVideoNativeChecks.Frame(output, 320, 240, 500);
            var before = RotatedVideoNativeChecks.Frame(source, 320, 240, 1500);
            double Bright(byte[] pixels) => pixels.Where((_, i) => i % 4 != 3).Average(x => (double)x);
            double Chroma(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Average(i =>
                (double)Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])) - Math.Min(pixels[i * 4], Math.Min(pixels[i * 4 + 1], pixels[i * 4 + 2])));
            check(Bright(first.Pixels) < 15 && Bright(middle.Pixels) > 80, "The exported first frame is dark during picture fade, and the middle is visible");
            check(Chroma(before.Pixels) > 150 && Chroma(middle.Pixels) < 15, "The exported Mono look changes the measured source colors");
            var raw = video.FrameAsync(1500, 320, default).GetAwaiter().GetResult();
            var preview = VideoTrimmer.ColorPreview(raw, settings, .5, 1, default);
            var pixels = new byte[preview.PixelWidth * preview.PixelHeight * 4]; preview.CopyPixels(pixels, preview.PixelWidth * 4, 0);
            check(preview.IsFrozen && Math.Abs(Bright(pixels) - Bright(middle.Pixels)) < 15, "Still preview and exported middle frame agree within codec precision");

            // Removing the original must keep the new track, including when it is shorter than the output.
            var musicOnly = VideoTrim.Plan(0, 2500, video.Duration, false, effects: new(Music: new(music, Loop: false)));
            string only = video.TrimAsync(musicOnly, null, default).GetAwaiter().GetResult();
            var probe = VideoTrimNativeChecks.Probe(only);
            check(probe.Sound && Math.Abs(probe.Seconds - 2.5) < .1, "Nonlooped music retains a full-length sound track with a silent tail after removing the original");

            // Cancellation during the added frame pass (after the native cut has finished).
            int files = Directory.GetFiles(root).Length;
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            var run = Task.Run(() => video.TrimAsync(plan, done =>
            {
                if (done > .4 && done < .8) cancel.Cancel();
            }, cancel.Token));
            if (Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2))).GetAwaiter().GetResult() != run)
                throw new InvalidOperationException("A cancelled frame edit did not finish within two minutes.");
            try { run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && Directory.GetFiles(root).Length == files, "Cancelling the color frame pass publishes no output");
        }
        // Exercise tall storage beyond the frame encoder's 2304-row limit, using the same
        // sideways pixels and display matrix as the added color/fade pass.
        string tall = Path.Combine(root, "tall.mp4");
        var portrait = new byte[64 * 2400 * 4];
        for (int y = 0; y < 2400; y++) for (int x = 0; x < 64; x++)
        {
            int i = (y * 64 + x) * 4;
            portrait[i] = (byte)(y < 1200 ? 20 : 220); portrait[i + 1] = 20; portrait[i + 2] = (byte)(y < 1200 ? 220 : 20); portrait[i + 3] = 255;
        }
        var stored = VideoColorSound.StoreCounterclockwise(new(64, 2400, portrait), default);
        using (var writer = new VideoFrameWriter(tall, 2400, 64, 10, 1, 1_000_000))
        {
            for (int i = 0; i < 3; i++) writer.Write(stored.Bgra, i * 1_000_000, 1_000_000);
            writer.Finish();
        }
        using (var movie = new FileStream(tall, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Mp4Retime.DisplayClockwise(movie, 2400, 64);
        using var tallVideo = VideoTrimmer.OpenAsync(tall, default).GetAwaiter().GetResult();
        var tallPlan = VideoTrim.Plan(0, tallVideo.Duration, tallVideo.Duration, false, tallVideo.HasSound, effects: new(Color: new(Look: PhotoLook.Warm)));
        string tallOutput = tallVideo.TrimAsync(tallPlan, null, default).GetAwaiter().GetResult();
        var tallFrame = RotatedVideoNativeChecks.Frame(tallOutput, 64, 2400, 100);
        int top = (600 * 64 + 32) * 4, bottom = (1800 * 64 + 32) * 4;
        check(tallFrame.Width == 64 && tallFrame.Height == 2400 && tallFrame.Pixels[top + 2] > 150 && tallFrame.Pixels[top] < 80 && tallFrame.Pixels[bottom] > 150 && tallFrame.Pixels[bottom + 2] < 80,
            "A color-pass portrait taller than 2304 rows decodes upright, with its top and bottom intact");
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original) && SHA256.HashData(File.ReadAllBytes(music)).SequenceEqual(originalMusic),
            "Color, fades, volume and music leave both source files byte-for-byte unchanged");
    }
}
