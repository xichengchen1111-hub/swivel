using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class UpscaleNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: the converted Real-ESRGAN model through Windows ML on small images.
        string root = Path.Combine(directory, "upscale"); Directory.CreateDirectory(root);
        string real = UpscaleModel.ModelPath;
        check(File.Exists(real), "The pinned upscaling model is copied beside the app: " + real);
        UpscaleModel.ModelPath = Path.Combine(root, "none.onnx");
        reject(() => UpscaleModel.Predict(new float[3 * Upscale.Tile * Upscale.Tile], default), "A missing upscaling model is refused");
        string fake = Path.Combine(root, "fake.onnx"); File.WriteAllBytes(fake, new byte[4096]);
        UpscaleModel.ModelPath = fake;
        reject(() => UpscaleModel.Predict(new float[3 * Upscale.Tile * Upscale.Tile], default), "An upscaling model of the wrong size is refused");
        UpscaleModel.ModelPath = real;
        if (!File.Exists(real)) return;

        // Red on the left, blue on the right, a soft grey stripe between; half transparent at the bottom.
        int w = 96, h = 64;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool left = x < 40, right = x >= 56;
                pixels[i] = right ? (byte)200 : left ? (byte)20 : (byte)128; pixels[i + 1] = left || right ? (byte)20 : (byte)128; pixels[i + 2] = left ? (byte)200 : right ? (byte)20 : (byte)128;
                pixels[i + 3] = y >= 56 ? (byte)0 : (byte)255;
            }
        string photo = Path.Combine(root, "split.png");
        using (var file = File.Create(photo)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4))); encoder.Save(file); }
        var clock = Stopwatch.StartNew(); int blocks = 0;
        string four = Upscaler.Run(photo, 4, default, (done, total) => blocks = total);
        Console.WriteLine($"Upscale 4× 96×64, {blocks} block(s) (first run, model load included): {clock.ElapsedMilliseconds} ms");
        check(four == Path.Combine(root, "split 4x.png") && blocks == 1, "Upscale 4× writes a PNG beside the original");
        var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(four), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        check(decoded.PixelWidth == w * 4 && decoded.PixelHeight == h * 4, "Upscale 4× output size");
        var output = new byte[w * 4 * h * 4 * 4]; decoded.CopyPixels(output, w * 16, 0);
        int At(int x, int y, int c) => output[(y * w * 4 + x) * 4 + c];
        check(At(40, 100, 2) > 150 && At(40, 100, 0) < 70 && At(340, 100, 0) > 150 && At(340, 100, 2) < 70, $"Upscale keeps colours in place (left {At(40, 100, 2)}/{At(40, 100, 0)}, right {At(340, 100, 0)}/{At(340, 100, 2)})");
        check(At(200, 250, 3) == 0 && At(200, 100, 3) == 255, "Upscale keeps transparency");

        string jpeg = Path.Combine(root, "split.jpg");
        using (var file = File.Create(jpeg)) { var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4), PixelFormats.Bgr24, null, 0))); encoder.Save(file); }
        clock.Restart();
        string two = Upscaler.Run(jpeg, 2, default);
        Console.WriteLine($"Upscale 2× 96×64 (model loaded): {clock.ElapsedMilliseconds} ms");
        var twoFrame = BitmapFrame.Create(new Uri(two), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        check(two == Path.Combine(root, "split 2x.jpg") && twoFrame.PixelWidth == w * 2 && twoFrame.PixelHeight == h * 2, "Upscale 2× keeps JPEG as JPEG");
        check(Upscaler.Run(photo, 4, default) == Path.Combine(root, "split 4x 2.png"), "Upscale never overwrites an earlier result");
    }
}
