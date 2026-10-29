using System;
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

internal static class VideoTrimNativeChecks
{
    // A 3-second test video, one second each of red, green and blue, with a tone as its sound.
    // Trims are checked by the colours of their first and last frames, their length and whether
    // they carry sound; the original must stay byte for byte the same.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "trim"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "colours.mp4");
        MakeColourVideo(root, source, 320, 240);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));

        using (var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            check(video.Width == 320 && video.Height == 240 && Math.Abs(video.Duration - 3000) <= 50 && video.HasSound,
                $"Trim Video opens the test video: {video.Width}×{video.Height}, {video.Duration} ms, sound {video.HasSound}");
            var frame = video.FrameAsync(1500, 160, default).GetAwaiter().GetResult();
            check(frame.PixelWidth == 160 && frame.PixelHeight == 120 && frame.IsFrozen && Colour(frame) == 'G', $"Trim Video shows a frozen, scaled frame from the middle: {frame.PixelWidth}×{frame.PixelHeight} {Colour(frame)}");
            check(Colour(video.FrameAsync(video.Duration - 1, 160, default).GetAwaiter().GetResult()) == 'B', "Trim Video shows the last frame before the end");
            // Windows reports an average frame rate (30.659 for this 30 fps video on the CI runner),
            // so frames are found only approximately; these times are a few frames from a colour change.
            check(Colour(video.FrameAsync(880, 160, default).GetAwaiter().GetResult()) == 'R' && Colour(video.FrameAsync(1120, 160, default).GetAwaiter().GetResult()) == 'G',
                $"Trim Video shows frames on either side of a colour change ({video.Fps:0.###} fps reported)");
            check(Colour(video.FrameAsync(999_999, 160, default).GetAwaiter().GetResult()) == 'B', "A frame asked past the end shows the last one");

            string middle = video.TrimAsync(new VideoTrimPlan(1100, 1900, true), null, default).GetAwaiter().GetResult();
            check(Path.GetFileName(middle) == "colours trimmed.mp4", "The trimmed video is saved beside the original as “name trimmed.mp4”: " + Path.GetFileName(middle));
            var (seconds, sound) = Probe(middle);
            check(Math.Abs(seconds - 0.8) < 0.1 && sound, $"Trimming 1.1–1.9 s keeps 0.8 seconds with its sound: {seconds:0.000} s, sound {sound}");
            check(Colours(middle, seconds) == "GG", "The kept part is all green, first frame to last: " + Colours(middle, seconds));

            double progress = -1;
            string wide = video.TrimAsync(new VideoTrimPlan(500, 2500, false), done => progress = Math.Max(progress, done), default).GetAwaiter().GetResult();
            check(Path.GetFileName(wide) == "colours trimmed 2.mp4", "A second trim doesn't replace the first: " + Path.GetFileName(wide));
            (seconds, sound) = Probe(wide);
            check(Math.Abs(seconds - 2) < 0.1 && !sound, $"Trimming 0.5–2.5 s without sound keeps two silent seconds: {seconds:0.000} s, sound {sound}");
            check(Colours(wide, seconds) == "RB", "The kept part runs from red to blue: " + Colours(wide, seconds));
            check(progress is > 0 and <= 1.0001, $"Trim Video reports progress up to 1: {progress}");

            string silent = video.TrimAsync(VideoTrim.Plan(0, video.Duration, video.Duration, false, video.HasSound), null, default).GetAwaiter().GetResult();
            (seconds, sound) = Probe(silent);
            check(Math.Abs(seconds - 3) < 0.1 && !sound && Colours(silent, seconds) == "RB", $"Removing only the sound keeps the whole picture: {seconds:0.000} s, sound {sound}");

            // A picture change is encoded by another Windows path; its cut must be as exact.
            foreach (var (start, end, picture, colours) in new[] { (1100L, 1900L, new VideoPicture(FlipHorizontal: true), "GG"), (500L, 2500L, new VideoPicture(QuarterTurns: 1), "RB"), (2100L, 2900L, new VideoPicture(Aspect: VideoAspect.Square), "BB") })
            {
                string edited = video.TrimAsync(VideoTrim.Plan(start, end, video.Duration, true, video.HasSound, picture), null, default).GetAwaiter().GetResult();
                var (s2, sound2) = Probe(edited);
                check(Path.GetFileName(edited).StartsWith("colours edited", StringComparison.Ordinal) && Math.Abs(s2 - (end - start) / 1000.0) < 0.1 && sound2 && Colours(edited, s2) == colours,
                    $"A picture change cut {start}–{end} ms keeps exactly that part with sound: {Path.GetFileName(edited)}, {s2:0.000} s, {Colours(edited, s2)} (expected {colours})");
            }

            var before = Directory.GetFiles(root).Length;
            foreach (bool midway in new[] { false, true })
            {
                // Cancelled before starting, and once encoding has reported progress.
                using var cancel = new CancellationTokenSource(); if (!midway) cancel.Cancel();
                // The cancel comes from another thread, as the window's Cancel does, never from inside
                // Windows' progress callback. A trim that doesn't end within two minutes fails here
                // rather than hanging the suite.
                bool cancelled = false; double at = -1;
                var trim = Task.Run(() => video.TrimAsync(new VideoTrimPlan(0, 2900, true), done => { if (done > 0 && at < 0) { at = done; Task.Run(cancel.Cancel); } }, cancel.Token));
                bool ended = Task.WhenAny(trim, Task.Delay(TimeSpan.FromMinutes(2))).GetAwaiter().GetResult() == trim;
                if (!ended) { check(false, "A trim cancelled while encoding ends within two minutes"); return; }
                try { trim.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                check(cancelled && Directory.GetFiles(root).Length == before, $"A trim cancelled {(midway ? $"once encoding reports progress (at {at:0.###})" : "before starting")} saves nothing");
            }
        }
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Trimming leaves the original byte for byte the same");

        // A phone video stored sideways is trimmed upright, like compression and subtitles.
        string turned = Path.Combine(root, "turned.mp4");
        var bytes = File.ReadAllBytes(source); RotatedVideoNativeChecks.Turn(bytes, 90, 320, 240); File.WriteAllBytes(turned, bytes);
        using (var video = VideoTrimmer.OpenAsync(turned, default).GetAwaiter().GetResult())
        {
            check(video.Width == 240 && video.Height == 320, $"Trim Video reads a 90° video at its upright size: {video.Width}×{video.Height}");
            string output = video.TrimAsync(new VideoTrimPlan(1100, 1900, true), null, default).GetAwaiter().GetResult();
            var (w, h, _) = RotatedVideoNativeChecks.Frame(output, 240, 320, 500);
            check(RotatedVideoNativeChecks.Straight(output) && w == 240 && h == 320 && Colours(output, Probe(output).Seconds) == "GG", "The trimmed 90° video is stored upright with no turn");
        }
    }

    internal static (double Seconds, bool Sound) Probe(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        return (clip.OriginalDuration.TotalSeconds, clip.EmbeddedAudioTracks.Count > 0);
    }

    // The colours of the first and last frame of a video.
    private static string Colours(string path, double seconds)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var size = clip.GetVideoEncodingProperties();
        int w = (int)size.Width, h = (int)size.Height;
        char At(long ms) => Middle(RotatedVideoNativeChecks.Frame(path, w, h, ms));
        return new string(new[] { At(0), At(Math.Max(0, (long)(seconds * 1000) - 60)) });
    }

    private static char Colour(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight; var data = new byte[w * h * 4]; bgra.CopyPixels(data, w * 4, 0);
        return Middle((w, h, data));
    }

    private static char Middle((int Width, int Height, byte[] Pixels) frame)
    {
        int i = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4; var d = frame.Pixels;
        return d[i + 2] > 150 && d[i + 1] < 100 && d[i] < 100 ? 'R' : d[i + 1] > 150 && d[i + 2] < 100 && d[i] < 100 ? 'G' : d[i] > 150 && d[i + 1] < 100 && d[i + 2] < 100 ? 'B' : '?';
    }

    internal static void MakeColourVideo(string root, string path, int width, int height)
    {
        var composition = new MediaComposition();
        foreach (var (name, b, g, r) in new[] { ("red", 0, 0, 255), ("green", 0, 255, 0), ("blue", 255, 0, 0) })
        {
            string png = Path.Combine(root, name + ".png");
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255; }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
            using (var stream = File.Create(png)) encoder.Save(stream);
            var image = StorageFile.GetFileFromPathAsync(png).AsTask().GetAwaiter().GetResult();
            composition.Clips.Add(MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(1)).AsTask().GetAwaiter().GetResult());
        }
        string wav = Path.Combine(root, "tone.wav");
        File.WriteAllBytes(wav, Tone(44100, 3));
        var tone = StorageFile.GetFileFromPathAsync(wav).AsTask().GetAwaiter().GetResult();
        composition.BackgroundAudioTracks.Add(BackgroundAudioTrack.CreateFromFileAsync(tone).AsTask().GetAwaiter().GetResult());
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var reason = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask().GetAwaiter().GetResult();
        if (reason != global::Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Could not make the test video: " + reason);
    }

    // 16-bit mono PCM WAV with a 440 Hz tone.
    internal static byte[] Tone(int rate, int seconds)
    {
        int frames = rate * seconds, data = frames * 2;
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(data);
        for (int i = 0; i < frames; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
        w.Flush(); return stream.ToArray();
    }
}
