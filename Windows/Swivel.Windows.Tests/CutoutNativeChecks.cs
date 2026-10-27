using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class CutoutNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: the pinned model through Windows ML on a synthetic photo.
        string root = Path.Combine(directory, "cutout"); Directory.CreateDirectory(root);
        string real = CutoutModel.ModelPath;
        check(File.Exists(real), "The pinned background model is copied beside the app: " + real);
        // A missing or altered model is refused before Windows ML sees it (and is never cached).
        CutoutModel.ModelPath = Path.Combine(root, "none.onnx");
        reject(() => CutoutModel.Predict(new float[3 * Cutout.Side * Cutout.Side], default), "A missing background model is refused");
        string fake = Path.Combine(root, "fake.onnx");
        File.WriteAllBytes(fake, new byte[1024]);
        CutoutModel.ModelPath = fake;
        reject(() => CutoutModel.Predict(new float[3 * Cutout.Side * Cutout.Side], default), "A background model of the wrong size is refused");
        CutoutModel.ModelPath = real;
        if (!File.Exists(real)) return;

        // A dark red disc on a soft green gradient.
        int w = 640, h = 480;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool disc = (x - 320) * (x - 320) + (y - 240) * (y - 240) < 130 * 130;
                pixels[i] = disc ? (byte)30 : (byte)(120 + x / 8); pixels[i + 1] = disc ? (byte)30 : (byte)(170 + y / 8); pixels[i + 2] = disc ? (byte)190 : (byte)110; pixels[i + 3] = 255;
            }
        string photo = Path.Combine(root, "disc.png");
        using (var file = File.Create(photo)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4))); encoder.Save(file); }
        var clock = Stopwatch.StartNew();
        string cut = ImageTools.Run(photo, "image.removebg", 0, default);
        Console.WriteLine($"Remove Background 640×480 (first run, model load included): {clock.ElapsedMilliseconds} ms");
        check(cut == Path.Combine(root, "disc cutout.png") && File.Exists(cut), "Remove Background writes a PNG beside the original");
        var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(cut), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var output = new byte[w * h * 4]; decoded.CopyPixels(output, w * 4, 0);
        byte Alpha(int x, int y) => output[(y * w + x) * 4 + 3];
        check(Alpha(320, 240) > 200 && Alpha(10, 10) < 50 && Alpha(630, 470) < 50, $"Remove Background keeps the disc and clears the corners (centre {Alpha(320, 240)}, corners {Alpha(10, 10)}/{Alpha(630, 470)})");
        clock.Restart();
        string blurred = ImageTools.Run(photo, "image.blurbg", 0, default);
        Console.WriteLine($"Blur Background 640×480 (model loaded): {clock.ElapsedMilliseconds} ms");
        var blurredPixels = new byte[w * h * 4];
        new FormatConvertedBitmap(BitmapFrame.Create(new Uri(blurred), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0).CopyPixels(blurredPixels, w * 4, 0);
        int centre = (240 * w + 320) * 4;
        check(blurred == Path.Combine(root, "disc blurred.png") && Math.Abs(blurredPixels[centre + 2] - 190) <= 2 && Math.Abs(blurredPixels[centre] - 30) <= 2, "Blur Background keeps the subject's own pixels");
        check(File.ReadAllBytes(photo).Length > 0 && ImageTools.Run(photo, "image.removebg", 0, default) == Path.Combine(root, "disc cutout 2.png"), "Remove Background never overwrites an earlier result");
    }
}
