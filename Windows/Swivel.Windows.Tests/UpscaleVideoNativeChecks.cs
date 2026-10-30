using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

internal static class UpscaleVideoNativeChecks
{
    const int W = 64, H = 48, Fps = 10;

    // A one-second 64 × 48 video at 10 fps with a tone; its picture has four different quarters
    // (red, green, blue, white from the top left), so a turned or mirrored frame is caught.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(directory, "upscale-video"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "quarters.mp4");
        MakeVideo(root, source);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var sourceFrames = Frames(source, W, H, out var sourceTimes);
        check(sourceFrames.Count is >= 9 and <= 11 && Quarters(sourceFrames[sourceFrames.Count / 2], W, H) == "RGBW",
            $"The frame reader decodes the test video upright: {sourceFrames.Count} frames, quarters {Quarters(sourceFrames[sourceFrames.Count / 2], W, H)}");

        using (var video = VideoUpscaler.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            check(video.Width == W && video.Height == H && video.Output == (2 * W, 2 * H) && video.HasSound && video.Frames is >= 9 and <= 11,
                $"Upscale Video opens the test video: {video.Width}×{video.Height}, {video.Seconds:0.###} s at {video.Fps:0.###} fps, about {video.Frames} frames, sound {video.HasSound}");
            var (graphics, perBlock) = Task.Run(() => video.Measure(default)).GetAwaiter().GetResult();
            Console.WriteLine($"Upscale Video: one block of the first frame took {perBlock.TotalSeconds:0.00} s on the {(graphics ? "graphics card" : "processor")} ({Environment.ProcessorCount} logical processors); estimate {VideoUpscale.Estimate(perBlock, W, H, video.Frames).TotalSeconds:0} s");
            check(perBlock > TimeSpan.Zero, "Upscale Video measures one block before starting");

            var before = Directory.GetFiles(root).Length;
            foreach (bool midway in new[] { false, true })
            {
                using var cancel = new CancellationTokenSource(); if (!midway) cancel.Cancel();
                long at = 0; bool cancelled = false;
                var run = Task.Run(() => video.Run((done, _, _) => { if (at == 0) { at = done; cancel.Cancel(); } }, null, cancel.Token));
                if (Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(5))).GetAwaiter().GetResult() != run) { check(false, "A cancelled upscale ends within five minutes"); return; }
                try { run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                check(cancelled && Directory.GetFiles(root).Length == before && Directory.GetFiles(video.Workspace).Length == 0,
                    $"An upscale cancelled {(midway ? $"after frame {at}" : "before starting")} saves nothing and leaves no temporary files");
            }

            long frames = 0, total = 0; TimeSpan? lastLeft = null;
            var clock = Stopwatch.StartNew();
            string output = Task.Run(() => video.Run((done, all, left) => { frames = done; total = all; lastLeft = left; }, null, default)).GetAwaiter().GetResult();
            Console.WriteLine($"Upscale Video 2× of {frames} frames of 64×48: {clock.Elapsed.TotalSeconds:0.0} s, {clock.Elapsed.TotalSeconds / Math.Max(1, frames):0.00} s per frame");
            check(output == Path.Combine(root, "quarters 2x.mp4"), "The upscaled video is saved beside the original as “name 2x.mp4”: " + Path.GetFileName(output));
            check(frames == sourceFrames.Count && total == frames && lastLeft == TimeSpan.Zero, $"Progress counts every frame: {frames} of {total}, last time left {lastLeft}");
            check(Directory.GetFiles(video.Workspace).Length == 0, "No temporary video remains after saving (only the prepared sound, removed on close)");

            var outputFrames = Frames(output, 2 * W, 2 * H, out var outputTimes);
            var (ow, oh, seconds, sound) = Probe(output);
            var (_, _, sourceSeconds, _) = Probe(source);
            check(ow == 2 * W && oh == 2 * H, $"The upscaled video is 128 × 96: {ow}×{oh}");
            // Windows' writer starts the video at 0 (the reader gave this source's first frame at 0.1 s), so times are compared from the first frame.
            check(outputFrames.Count == sourceFrames.Count && outputTimes.Zip(sourceTimes).All(p => Math.Abs(p.First - outputTimes[0] - (p.Second - sourceTimes[0])) <= 10_000),
                $"Every frame is kept at its time: {outputFrames.Count} frames (source {sourceFrames.Count}), first times {string.Join(",", outputTimes.Take(3))} vs {string.Join(",", sourceTimes.Take(3))}");
            check(VideoUpscale.WithinFrame(seconds, sourceSeconds, Fps) && sound, $"The upscaled video lasts as long as the original within a frame and has sound: {seconds:0.000} s vs {sourceSeconds:0.000} s, sound {sound}");

            int middle = outputFrames.Count / 2;
            var reduced = Halve(outputFrames[middle], 2 * W, 2 * H);
            double rmse = Math.Sqrt(Enumerable.Range(0, W * H * 4).Where(i => i % 4 != 3).Average(i => Math.Pow(reduced[i] - sourceFrames[middle][i], 2)));
            check(rmse < 20 && Quarters(outputFrames[middle], 2 * W, 2 * H) == "RGBW", $"A sampled upscaled frame, halved, matches the source frame: RMSE {rmse:0.0}, quarters {Quarters(outputFrames[middle], 2 * W, 2 * H)}");
        }
        check(!Directory.GetDirectories(Path.GetTempPath(), "Swivel-upscale-*").Any(d => Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Any()), "Closing removes the upscaling workspace and its prepared sound");
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Upscaling leaves the original byte for byte the same");

        // Windows' H.264 encoder takes the extremes the limits allow: 64 × 64 and 3840 × 2160 at 64 fps (level 5.2).
        foreach (var (w, h, rate) in new[] { (64, 64, 120u), (3840, 2160, 64u), (2160, 2160, 30u) })
        {
            string probe = Path.Combine(root, $"writer {w}x{h}.mp4");
            var frame = new byte[w * h * 4];
            using (var writer = new VideoFrameWriter(probe, w, h, rate, 1, VideoUpscale.Bitrate(w, h, rate, 0)))
            {
                writer.Write(frame, 0, 10_000_000 / rate); writer.Write(frame, 10_000_000 / rate, 10_000_000 / rate); writer.Finish();
            }
            check(Probe(probe) is var (pw, ph, _, _) && pw == w && ph == h, $"Windows' H.264 writer takes {w} × {h} at {rate} fps");
            File.Delete(probe);
        }

        // A phone video stored sideways is refused (its stored frames would come out turned).
        string turned = Path.Combine(root, "turned.mp4");
        var bytes = File.ReadAllBytes(source); RotatedVideoNativeChecks.Turn(bytes, 90, W, H); File.WriteAllBytes(turned, bytes);
        reject(() => VideoUpscaler.OpenAsync(turned, default).GetAwaiter().GetResult().Dispose(), "Upscale Video refuses a video stored with a 90° turn");
    }

    // Every frame (BGRA) and its time in 100 ns units.
    internal static System.Collections.Generic.List<byte[]> Frames(string path, int width, int height, out System.Collections.Generic.List<long> times)
    {
        var frames = new System.Collections.Generic.List<byte[]>(); times = new();
        using var reader = new VideoFrameReader(path, width, height);
        while (true)
        {
            var frame = new byte[width * height * 4];
            if (!reader.Read(frame, out long time, out _, default)) break;
            frames.Add(frame); times.Add(time);
        }
        return frames;
    }

    private static (int Width, int Height, double Seconds, bool Sound) Probe(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var size = clip.GetVideoEncodingProperties();
        return ((int)size.Width, (int)size.Height, clip.OriginalDuration.TotalSeconds, clip.EmbeddedAudioTracks.Count > 0);
    }

    // Each 2 × 2 square averaged.
    private static byte[] Halve(byte[] bgra, int width, int height)
    {
        int w = width / 2, h = height / 2; var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) sum += bgra[((2 * y + dy) * width + 2 * x + dx) * 4 + c];
                    result[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
        return result;
    }

    // The colour at the middle of each quarter: R, G, B, W or ?.
    private static string Quarters(byte[] bgra, int width, int height)
    {
        char At(int x, int y)
        {
            int i = (y * width + x) * 4; byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            return r > 180 && g > 180 && b > 180 ? 'W' : r > 150 && g < 100 && b < 100 ? 'R' : g > 150 && r < 100 && b < 100 ? 'G' : b > 150 && r < 100 && g < 100 ? 'B' : '?';
        }
        return new string(new[] { At(width / 4, height / 4), At(3 * width / 4, height / 4), At(width / 4, 3 * height / 4), At(3 * width / 4, 3 * height / 4) });
    }

    private static void MakeVideo(string root, string path)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                var (b, g, r) = (x < W / 2, y < H / 2) switch { (true, true) => (0, 0, 255), (false, true) => (0, 255, 0), (true, false) => (255, 0, 0), _ => (255, 255, 255) };
                pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
        string png = Path.Combine(root, "quarters.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, pixels, W * 4)));
        using (var stream = File.Create(png)) encoder.Save(stream);
        var composition = new MediaComposition();
        var image = StorageFile.GetFileFromPathAsync(png).AsTask().GetAwaiter().GetResult();
        composition.Clips.Add(MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(1)).AsTask().GetAwaiter().GetResult());
        string wav = Path.Combine(root, "tone.wav");
        File.WriteAllBytes(wav, VideoTrimNativeChecks.Tone(44100, 1));
        var tone = StorageFile.GetFileFromPathAsync(wav).AsTask().GetAwaiter().GetResult();
        composition.BackgroundAudioTracks.Add(BackgroundAudioTrack.CreateFromFileAsync(tone).AsTask().GetAwaiter().GetResult());
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        profile.Video.Width = W; profile.Video.Height = H; profile.Video.FrameRate.Numerator = Fps; profile.Video.FrameRate.Denominator = 1;
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var reason = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask().GetAwaiter().GetResult();
        if (reason != global::Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Could not make the test video: " + reason);
        foreach (var file in new[] { png, wav }) File.Delete(file);
    }
}
