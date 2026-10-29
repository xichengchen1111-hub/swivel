using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

internal static class VideoEditNativeChecks
{
    // A 2-second 320×240 test video in four coloured quarters (red, green / blue, white) with a
    // tone. Each edit is checked by its size, length, sound and where the quarters end up: every
    // sample point of the source is placed by the core geometry and its colour read back.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "video-edit"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "quads.mp4");
        MakeQuarterVideo(root, source, 320, 240, 2);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        using (var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            check(video.Width == 320 && video.Height == 240 && Math.Abs(video.Duration - 2000) <= 50 && video.HasSound && video.PictureChanges, $"The quarter test video opens: {video.Width}×{video.Height}, {video.Duration} ms");

            // The window's preview draws the same picture change as the saved video.
            var frame = video.FrameAsync(1000, 320, default).GetAwaiter().GetResult();
            foreach (var picture in new[] { new VideoPicture(1), new VideoPicture(3, true, false, VideoAspect.Square, 1), new VideoPicture(0, false, true, VideoAspect.Tall, 0), new VideoPicture(2, true, true, VideoAspect.Portrait, 0.5) })
            {
                var g = VideoEdit.Geometry(320, 240, picture);
                var preview = VideoTrimmer.Preview(frame, 320, 240, picture);
                var pixels = Pixels(preview);
                bool agrees = Agrees(g, pixels, preview.PixelWidth, preview.PixelHeight, preview.PixelWidth / (double)g.Width, out var found);
                check(preview.IsFrozen && Math.Abs(preview.PixelWidth - g.Width) <= 1 && Math.Abs(preview.PixelHeight - g.Height) <= 1 && agrees,
                    $"The preview of {picture} shows the quarters where the saved video will ({preview.PixelWidth}×{preview.PixelHeight}): {found}");
            }

            int number = 0;
            foreach (var (picture, speed, start, end, keep) in new (VideoPicture?, int, long, long, bool)[]
            {
                (new VideoPicture(1), 100, 0, 2000, true),
                (new VideoPicture(FlipHorizontal: true), 100, 0, 2000, true),
                (new VideoPicture(FlipVertical: true), 100, 500, 1500, false),
                (new VideoPicture(0, true, false, VideoAspect.Square, 0), 200, 0, 2000, true),
                (new VideoPicture(3, false, true, VideoAspect.Wide, 1), 50, 0, 1000, true),
                (new VideoPicture(1, true, false, VideoAspect.Portrait, 0.3), 150, 0, 2000, false),
                (new VideoPicture(2, false, false, VideoAspect.Tall, 0.5), 400, 0, 2000, true),
                (null, 25, 0, 1000, true),
            })
            {
                number++;
                var plan = VideoTrim.Plan(start, end, video.Duration, keep, video.HasSound, picture, speed);
                var g = VideoEdit.Geometry(320, 240, picture ?? new VideoPicture());
                double progress = -1;
                string output = video.TrimAsync(plan, done => progress = Math.Max(progress, done), default).GetAwaiter().GetResult();
                string name = number == 1 ? "quads edited.mp4" : $"quads edited {number}.mp4";
                var (seconds, sound) = VideoTrimNativeChecks.Probe(output);
                var (w, h) = Size(output);
                double expected = plan.OutputLength / 1000.0;
                check(Path.GetFileName(output) == name && w == g.Width && h == g.Height && Math.Abs(seconds - expected) <= 0.1 + expected * 0.03 && sound == (keep && speed == 100) && progress is > 0 and <= 1.0001,
                    $"{picture?.ToString() ?? "No picture change"} at {speed}% is saved as “{Path.GetFileName(output)}”, {w}×{h} (expected {g.Width}×{g.Height}), {seconds:0.000} s (expected {expected:0.000}), sound {sound}");
                foreach (double at in new[] { 0.25, 0.75 })
                {
                    var (fw, fh, data) = RotatedVideoNativeChecks.Frame(output, g.Width, g.Height, (long)(seconds * 1000 * at));
                    bool agrees = Agrees(g, data, fw, fh, 1, out var found);
                    check(fw == g.Width && fh == g.Height && agrees, $"The saved {picture?.ToString() ?? "video"} at {speed}% shows the quarters turned, flipped and cropped as planned ({at:0%} through): {found}");
                }
                check(RotatedVideoNativeChecks.Straight(output), "The edited video is stored upright with no turn in its header");
                if (speed != 100)
                {
                    Console.WriteLine("INFO: " + Timescales(output));
                    using var again = VideoTrimmer.OpenAsync(output, default).GetAwaiter().GetResult();
                    check(Math.Abs(again.Duration - plan.OutputLength) <= 100 + plan.OutputLength * 0.03, $"Windows reads the {speed}% video back at its new length: {again.Duration} ms, {again.Fps:0.##} fps reported (source {video.Fps:0.##})");
                }
            }
        }
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Editing leaves the original byte for byte the same");

        // Upside down keeps its size, but the track header gives it away.
        string upside = Path.Combine(root, "upside.mp4");
        var flipped = File.ReadAllBytes(source); RotatedVideoNativeChecks.Turn(flipped, 180, 320, 240); File.WriteAllBytes(upside, flipped);
        using (var video = VideoTrimmer.OpenAsync(upside, default).GetAwaiter().GetResult())
            check(video.Width == 320 && video.Height == 240 && !video.PictureChanges, $"A 180° phone video offers no picture changes either ({video.Width}×{video.Height}, {video.PictureChanges})");

        // A phone video stored sideways: Windows' transcoder doesn't turn it upright, so picture
        // changes are refused (with the reason); a speed change alone still works, upright.
        string turned = Path.Combine(root, "sideways.mp4");
        var bytes = File.ReadAllBytes(source); RotatedVideoNativeChecks.Turn(bytes, 90, 320, 240); File.WriteAllBytes(turned, bytes);
        using (var video = VideoTrimmer.OpenAsync(turned, default).GetAwaiter().GetResult())
        {
            check(video.Width == 240 && video.Height == 320 && !video.PictureChanges, $"A 90° phone video reads upright ({video.Width}×{video.Height}) and offers no picture changes ({video.PictureChanges})");
            string? message = null;
            try { video.TrimAsync(VideoTrim.Plan(0, video.Duration, video.Duration, true, video.HasSound, new VideoPicture(3)), null, default).GetAwaiter().GetResult(); }
            catch (UserError error) { message = error.Message; }
            check(message == VideoTrimmer.NoPictureChanges, "A picture change on it is refused with the reason: " + message);
            string output = video.TrimAsync(VideoTrim.Plan(0, video.Duration, video.Duration, true, video.HasSound, null, 200), null, default).GetAwaiter().GetResult();
            var (w, h) = Size(output);
            var (fw, fh, data) = RotatedVideoNativeChecks.Frame(output, w, h, 500);
            bool agrees = Agrees(VideoEdit.Geometry(320, 240, new VideoPicture(1)), data, fw, fh, 1, out var found);
            check(w == 240 && h == 320 && RotatedVideoNativeChecks.Straight(output) && agrees && Math.Abs(VideoTrimNativeChecks.Probe(output).Seconds - 1) < 0.1,
                $"Its 2× speed change is stored upright at 240×320 and one second: {w}×{h}, {found}");
        }
    }

    private static readonly (double X, double Y, char Colour)[] Points =
    {
        (80, 60, 'R'), (240, 60, 'G'), (80, 180, 'B'), (240, 180, 'W'),
        (20, 20, 'R'), (300, 20, 'G'), (20, 220, 'B'), (300, 220, 'W'), (140, 100, 'R'), (180, 140, 'W'),
    };

    // Each source point inside the crop (away from its edges and the quarter borders) shows its quarter's colour.
    private static bool Agrees(VideoGeometry g, byte[] pixels, int width, int height, double scale, out string found)
    {
        var text = new StringBuilder(); bool ok = true; int used = 0;
        var c = g.SourceCrop;
        foreach (var (x, y, colour) in Points)
        {
            if (x < c.X + 8 || y < c.Y + 8 || x > c.X + c.Width - 8 || y > c.Y + c.Height - 8) continue;
            var (u, v) = VideoEdit.Place(g, x, y);
            int px = Math.Clamp((int)(u * scale), 0, width - 1), py = Math.Clamp((int)(v * scale), 0, height - 1);
            char seen = Colour(pixels, width, px, py);
            text.Append($"({x},{y})→({px},{py}) {colour}/{seen} ");
            ok &= seen == colour; used++;
        }
        text.Append("grid ").Append(Grid(pixels, width, height));
        found = text.ToString();
        return ok && used >= 2;
    }

    private static string Grid(byte[] pixels, int width, int height)
    {
        var text = new StringBuilder();
        for (int j = 1; j <= 3; j++) { for (int i = 1; i <= 3; i++) text.Append(Colour(pixels, width, width * i / 4, height * j / 4)); text.Append('/'); }
        return text.ToString();
    }

    private static char Colour(byte[] d, int width, int x, int y)
    {
        int i = (y * width + x) * 4; int b = d[i], g = d[i + 1], r = d[i + 2];
        return r > 180 && g > 180 && b > 180 ? 'W' : r > 150 && g < 100 && b < 100 ? 'R' : g > 150 && r < 100 && b < 100 ? 'G' : b > 150 && r < 100 && g < 100 ? 'B' : '?';
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var data = new byte[bgra.PixelWidth * bgra.PixelHeight * 4]; bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
        return data;
    }

    // Versions and timescales of the movie and media headers Windows wrote, for knowing the retime's limits.
    private static string Timescales(string path)
    {
        var b = File.ReadAllBytes(path); var text = new StringBuilder();
        foreach (var type in new[] { "mvhd", "tkhd", "mdhd", "elst", "ctts", "cslg" })
            for (int i = 4; i + 24 <= b.Length; i++)
                if (b[i] == type[0] && b[i + 1] == type[1] && b[i + 2] == type[2] && b[i + 3] == type[3])
                {
                    int v = b[i + 4]; uint R(int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
                    text.Append($"{type} v{v}");
                    if (type is "mvhd" or "mdhd") text.Append($" timescale {R(i + 4 + (v == 1 ? 20 : 12))}");
                    text.Append("; ");
                }
        return text.ToString();
    }

    private static (int Width, int Height) Size(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var size = clip.GetVideoEncodingProperties();
        return ((int)size.Width, (int)size.Height);
    }

    private static void MakeQuarterVideo(string root, string path, int width, int height, int seconds)
    {
        string png = Path.Combine(root, "quarters.png");
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool right = x >= width / 2, bottom = y >= height / 2;
                var (b, g, r) = (right, bottom) switch { (false, false) => (0, 0, 255), (true, false) => (0, 255, 0), (false, true) => (255, 0, 0), _ => (255, 255, 255) };
                int i = (y * width + x) * 4; pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using (var stream = File.Create(png)) encoder.Save(stream);
        var composition = new MediaComposition();
        var image = StorageFile.GetFileFromPathAsync(png).AsTask().GetAwaiter().GetResult();
        composition.Clips.Add(MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(seconds)).AsTask().GetAwaiter().GetResult());
        string wav = Path.Combine(root, "tone.wav");
        File.WriteAllBytes(wav, VideoTrimNativeChecks.Tone(44100, seconds));
        var tone = StorageFile.GetFileFromPathAsync(wav).AsTask().GetAwaiter().GetResult();
        composition.BackgroundAudioTracks.Add(BackgroundAudioTrack.CreateFromFileAsync(tone).AsTask().GetAwaiter().GetResult());
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var reason = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask().GetAwaiter().GetResult();
        if (reason != global::Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Could not make the test video: " + reason);
    }
}
