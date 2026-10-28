using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ImageStitchNativeChecks
{
    private static string Png(string path, int width, int height, byte b, byte g, byte r, byte a)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++) { bytes[i * 4] = b; bytes[i * 4 + 1] = g; bytes[i * 4 + 2] = r; bytes[i * 4 + 3] = a; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4)));
        using var output = File.Create(path); encoder.Save(output); return path;
    }

    // Stitching two hung images on real files: the dragged one below the target, at the narrower width.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "stitch"); Directory.CreateDirectory(folder);
        string top = Png(Path.Combine(folder, "截图 top.png"), 300, 100, 0, 0, 255, 255);
        string bottom = Png(Path.Combine(folder, "bottom.png"), 150, 150, 255, 0, 0, 128);
        byte[] a = File.ReadAllBytes(top), b = File.ReadAllBytes(bottom);
        string made = ImageStitcher.Save(top, bottom, default);
        check(Path.GetFileName(made) == "截图 top stitched.png" && File.ReadAllBytes(top).SequenceEqual(a) && File.ReadAllBytes(bottom).SequenceEqual(b),
            "Stitching saves '<upper> stitched.png' beside the upper image and leaves both originals unchanged");
        var frame = BitmapDecoder.Create(new Uri(made), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
        byte[] At(int x, int y) => pixels.Skip((y * frame.PixelWidth + x) * 4).Take(4).ToArray();
        check(frame.PixelWidth == 150 && frame.PixelHeight == 200, $"The result is the narrower width with both heights in proportion: {frame.PixelWidth}×{frame.PixelHeight}");
        check(At(75, 0).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && At(0, 49).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && At(149, 25).SequenceEqual(new byte[] { 0, 0, 255, 255 }),
            "The upper image is scaled down whole, edge to edge, without darkened borders");
        check(At(75, 50).SequenceEqual(new byte[] { 255, 0, 0, 128 }) && At(149, 199).SequenceEqual(new byte[] { 255, 0, 0, 128 }), "The lower image follows directly below, keeping its transparency");
        check(Path.GetFileName(ImageStitcher.Save(top, bottom, default)) == "截图 top stitched 2.png", "Stitching again takes the next numbered name");

        // A JPEG on top of the PNG: names follow the upper image.
        string jpeg = Path.Combine(folder, "photo.jpg");
        var encoderJ = new JpegBitmapEncoder(); encoderJ.Frames.Add(BitmapFrame.Create(BitmapSource.Create(100, 40, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat((byte)90, 100 * 40 * 3).ToArray(), 300)));
        using (var output = File.Create(jpeg)) encoderJ.Save(output);
        string mixed = ImageStitcher.Save(jpeg, bottom, default);
        var mixedFrame = BitmapDecoder.Create(new Uri(mixed), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        check(Path.GetFileName(mixed) == "photo stitched.png" && mixedFrame.PixelWidth == 100 && mixedFrame.PixelHeight == 140, $"A JPEG above a PNG gives a 100×140 PNG named after the JPEG: {mixedFrame.PixelWidth}×{mixedFrame.PixelHeight}");

        int before = Directory.GetFiles(folder).Length;
        string? same = null; try { ImageStitcher.Save(top, top.ToUpperInvariant(), default); } catch (UserError error) { same = error.Message; }
        check(same == ImageStitch.ChooseTwo, "An image is never stitched to itself");
        reject(() => ImageStitcher.Save(top, bottom, new CancellationToken(true)), "Stitching cancelled before it starts");
        string damaged = Path.Combine(folder, "damaged.png"); File.WriteAllBytes(damaged, new byte[] { 137, 80, 78, 71, 1, 2, 3 });
        reject(() => ImageStitcher.Save(top, damaged, default), "A damaged image is refused");
        check(Directory.GetFiles(folder).Length == before + 1 && !Directory.GetFiles(folder, "*.tmp").Any() && !Directory.GetFiles(folder, ".swivel-*").Any(),
            "Refused and cancelled stitches write nothing (only the damaged fixture was added)");
    }
}
