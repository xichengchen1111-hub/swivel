using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

internal static class RotatedVideoNativeChecks
{
    // Phone videos are often stored sideways with a turn in the MP4 track header. Windows' media
    // editing API turns such a clip upright itself (reporting the upright size), so compressing
    // and burning subtitles must give upright videos with no turn left in the header. The test
    // videos carry a red top-left and green bottom-right quarter on blue and are turned by
    // rewriting that header, as a phone would write it.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "turned"); Directory.CreateDirectory(root);
        string plain = Path.Combine(root, "plain.mp4");
        MakeMarkerVideo(root, plain, 640, 360, 3);
        check(Quarters(plain, 640, 360) == "RBBG", "Rotated video fixture: the plain video has the marker quarters: " + Quarters(plain, 640, 360));
        foreach (var (degrees, upright) in new[] { (90, "BRGB"), (180, "GBBR"), (270, "BGRB") })
        {
            string turned = Path.Combine(root, $"turned {degrees}.mp4");
            var bytes = File.ReadAllBytes(plain); Turn(bytes, degrees, 640, 360); File.WriteAllBytes(turned, bytes);
            int width = degrees == 180 ? 640 : 360, height = degrees == 180 ? 360 : 640;
            check(Quarters(turned, width, height) == upright, $"Rotated video fixture: Windows shows the {degrees}° video turned: " + Quarters(turned, width, height));

            var probe = VideoCompress.ProbeAsync(turned, default).GetAwaiter().GetResult();
            check(probe.Width == width && probe.Height == height, $"Compress reads a {degrees}° video at its upright size: {probe}");
            FileStamp stamp; using (var held = NativeFiles.OpenRead(turned)) stamp = held.Stamp;
            string output = VideoCompress.CompressAsync(turned, 400_000, stamp, default).GetAwaiter().GetResult();
            var result = VideoCompress.ProbeAsync(output, default).GetAwaiter().GetResult();
            check(new FileInfo(output).Length <= 400_000 && result.Width == width && result.Height == height && Math.Abs(result.Seconds - 3) < 0.2,
                $"Compressing a {degrees}° video keeps its upright size and length within the target: {result} {new FileInfo(output).Length} bytes");
            check(Straight(output) && Quarters(output, width, height) == upright, $"The compressed {degrees}° video is stored upright with no turn: {Quarters(output, width, height)}");
        }

        string sideways = Path.Combine(root, "turned 90.mp4");
        using (var video = VideoSubtitles.OpenAsync(sideways, default).GetAwaiter().GetResult())
        {
            check(video.Width == 360 && video.Height == 640, $"Burn In Subtitles reads a 90° video at its upright size: {video.Width}×{video.Height}");
            var images = video.Draw(new[] { new SubtitleCue(500, 1500, "Upright") }, 1, default);
            check(images.All(i => i.X + i.Width <= 360 && i.Y + i.Height <= 640), "Burn In Subtitles fits the cue inside the upright picture");
            string burned = video.BurnAsync(images, null, default).GetAwaiter().GetResult();
            check(Straight(burned) && Quarters(burned, 360, 640, 2500) == "BRGB", "The subtitled 90° video is stored upright with no turn: " + Quarters(burned, 360, 640, 2500));
            double during = WhiteNearBottom(burned, 360, 640, 1000), after = WhiteNearBottom(burned, 360, 640, 2500);
            Console.WriteLine($"Turned video subtitles: white share near the bottom {during:0.000} during the cue, {after:0.000} after");
            check(during > 0.003 && after < 0.001, "The subtitle on the turned video is drawn near the bottom of the upright picture, only while its cue is on");
            using (var again = VideoSubtitles.OpenAsync(burned, default).GetAwaiter().GetResult())
                check(again.Width == 360 && again.Height == 640, $"The subtitled 90° video keeps the upright size: {again.Width}×{again.Height}");
        }

        // A 4K phone video stored 3840 × 2160 with a turn is 2160 × 3840 upright: within the limit
        // either way round, and Windows encodes it in portrait.
        string large = Path.Combine(root, "large.mp4"), portrait = Path.Combine(root, "portrait 4k.mp4");
        MakeMarkerVideo(root, large, 3840, 2160, 2);
        var stored = File.ReadAllBytes(large); Turn(stored, 90, 3840, 2160); File.WriteAllBytes(portrait, stored);
        using var tall = VideoSubtitles.OpenAsync(portrait, default).GetAwaiter().GetResult();
        check(tall.Width == 2160 && tall.Height == 3840, $"Burn In Subtitles accepts a turned 4K phone video at 2160×3840: {tall.Width}×{tall.Height}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string tallOut = tall.BurnAsync(tall.Draw(new[] { new SubtitleCue(200, 800, "Portrait 4K") }, 1, default), null, default).GetAwaiter().GetResult();
        Console.WriteLine($"Burn In Subtitles 2 s 2160×3840: {clock.ElapsedMilliseconds} ms, {new FileInfo(tallOut).Length} bytes");
        using (var tallAgain = VideoSubtitles.OpenAsync(tallOut, default).GetAwaiter().GetResult())
            check(Straight(tallOut) && tallAgain.Width == 2160 && tallAgain.Height == 3840 && Quarters(tallOut, 540, 960, 1500) == "BRGB",
                $"The subtitled 4K portrait video is stored upright at 2160×3840: {tallAgain.Width}×{tallAgain.Height} {Quarters(tallOut, 540, 960, 1500)}");
    }

    // The share of near-white pixels in the bottom third of the frame Windows shows at a time (ms).
    private static double WhiteNearBottom(string path, int width, int height, long ms)
    {
        var (w, h, data) = Frame(path, width, height, ms);
        int top = h * 2 / 3, white = 0;
        for (int y = top; y < h; y++) for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; if (data[i] > 200 && data[i + 1] > 200 && data[i + 2] > 200) white++; }
        return white / (double)(w * (h - top));
    }

    private static void MakeMarkerVideo(string root, string path, int width, int height, int seconds)
    {
        string png = Path.Combine(root, Path.GetFileNameWithoutExtension(path) + " marker.png");
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4; pixels[i + 3] = 255;
                if (x < width / 2 && y < height / 2) pixels[i + 2] = 255; else if (x >= width / 2 && y >= height / 2) pixels[i + 1] = 255; else pixels[i] = 255;
            }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using (var stream = File.Create(png)) encoder.Save(stream);
        var composition = new MediaComposition();
        var image = StorageFile.GetFileFromPathAsync(png).AsTask().GetAwaiter().GetResult();
        composition.Clips.Add(MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(seconds)).AsTask().GetAwaiter().GetResult());
        var profile = MediaEncodingProfile.CreateMp4(width > 1920 ? VideoEncodingQuality.Uhd2160p : VideoEncodingQuality.Vga);
        profile.Video.Width = (uint)width; profile.Video.Height = (uint)height; profile.Audio = null;
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var reason = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask().GetAwaiter().GetResult();
        if (reason != global::Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Could not make the test video: " + reason);
    }

    // Colour of each quarter (top-left, top-right, bottom-left, bottom-right) of the frame Windows shows.
    private static string Quarters(string path, int width, int height, long ms = 1000)
    {
        var (w, h, data) = Frame(path, width, height, ms);
        char At(int x, int y)
        {
            int i = (y * w + x) * 4;
            return data[i + 2] > 150 && data[i + 1] < 100 && data[i] < 100 ? 'R' : data[i + 1] > 150 && data[i + 2] < 100 && data[i] < 100 ? 'G' : data[i] > 150 && data[i + 1] < 100 && data[i + 2] < 100 ? 'B' : '?';
        }
        return new string(new[] { At(w / 4, h / 4), At(3 * w / 4, h / 4), At(w / 4, 3 * h / 4), At(3 * w / 4, 3 * h / 4) });
    }

    // The BGRA frame Windows shows at a time (ms), scaled to the given size.
    private static (int Width, int Height, byte[] Pixels) Frame(string path, int width, int height, long ms)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult());
        using var image = composition.GetThumbnailAsync(TimeSpan.FromMilliseconds(ms), width, height, VideoFramePrecision.NearestFrame).AsTask().GetAwaiter().GetResult();
        using var stream = image.AsStreamForRead();
        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        int w = frame.PixelWidth, h = frame.PixelHeight; var data = new byte[w * h * 4]; frame.CopyPixels(data, w * 4, 0);
        return (w, h, data);
    }

    // The identity matrix in the video track's header: stored upright.
    private static bool Straight(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int matrix = MatrixAt(bytes, VideoTrackHeader(bytes));
        int[] identity = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000 };
        return Enumerable.Range(0, 9).All(k => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(matrix + k * 4)) == identity[k]);
    }

    // Rewrites the video track's matrix as a phone stores a clockwise turn (ISO/IEC 14496-12 tkhd).
    private static void Turn(byte[] bytes, int degrees, int width, int height)
    {
        int matrix = MatrixAt(bytes, VideoTrackHeader(bytes)), one = 0x10000;
        int[] values = degrees switch
        {
            90 => new[] { 0, one, 0, -one, 0, 0, height * one, 0, 0x40000000 },
            180 => new[] { -one, 0, 0, 0, -one, 0, width * one, height * one, 0x40000000 },
            _ => new[] { 0, -one, 0, one, 0, 0, 0, width * one, 0x40000000 },
        };
        for (int k = 0; k < 9; k++) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(matrix + k * 4), values[k]);
    }

    // tkhd: box header, version/flags, times, track id, reserved, duration, reserved, layer,
    // group, volume, reserved, then the matrix and width/height.
    private static int MatrixAt(byte[] bytes, int header) => header + 12 + (bytes[header + 8] == 1 ? 32 : 20) + 16;

    // The tkhd of the first track with a picture (nonzero width).
    private static int VideoTrackHeader(byte[] bytes)
    {
        int Find(int start, int end, int depth)
        {
            string[] path = { "moov", "trak", "tkhd" };
            for (int at = start; at + 8 <= end;)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
                if (size < 8 || at + size > end) break;
                if (System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4) == path[depth])
                {
                    if (depth == 2) { if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(MatrixAt(bytes, at) + 36)) != 0) return at; }
                    else { int found = Find(at + 8, at + (int)size, depth + 1); if (found >= 0) return found; }
                }
                at += (int)size;
            }
            return -1;
        }
        int header = Find(0, bytes.Length, 0);
        if (header < 0) throw new InvalidDataException("The test video has no picture track header.");
        return header;
    }
}
