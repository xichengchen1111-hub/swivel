using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class WebPNativeChecks
{
    private static byte[] Decoded(string png, out int width, out int height)
    {
        using var stream = File.OpenRead(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth; height = bgra.PixelHeight;
        var bytes = new byte[width * height * 4]; bgra.CopyPixels(bytes, width * 4, 0); return bytes;
    }

    // WebP files go through the same image tools the wheel uses, read by Swivel's own decoder.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");
        string folder = Path.Combine(directory, "webp"); Directory.CreateDirectory(folder);
        string lossless = Path.Combine(folder, "图标 lossless.webp"), lossy = Path.Combine(folder, "photo lossy.webp");
        File.Copy(Path.Combine(fixtures, "ll-photo-61x33-alpha-m4.webp"), lossless);
        File.Copy(Path.Combine(fixtures, "ly-photo-63x47-q95-alpha.webp"), lossy);
        byte[] original = File.ReadAllBytes(lossless);

        string png = ImageTools.Run(lossless, "convert.png", 1920, default);
        var expected = File.ReadAllBytes(Path.Combine(fixtures, "ll-photo-61x33-alpha-m4.rgba"));
        var pixels = Decoded(png, out int width, out int height);
        bool same = width == 61 && height == 33;
        for (int i = 0; same && i < expected.Length; i += 4)
            same = pixels[i] == expected[i + 2] && pixels[i + 1] == expected[i + 1] && pixels[i + 2] == expected[i] && pixels[i + 3] == expected[i + 3];
        check(Path.GetFileName(png) == "图标 lossless converted.png" && same && File.ReadAllBytes(lossless).SequenceEqual(original),
            "A lossless WebP becomes a PNG with exactly libwebp's pixels; the original is unchanged");

        string jpg = ImageTools.Run(lossy, "convert.jpg", 1920, default);
        check(File.Exists(jpg) && jpg.EndsWith(".jpg", StringComparison.Ordinal), "A lossy WebP with transparency converts to JPEG (on white)");
        string rotated = ImageTools.Run(lossy, "image.rotate", 1920, default);
        Decoded(rotated, out width, out height);
        check(width == 47 && height == 63, "Other image tools take a WebP too (rotated to 47 × 63)");
        using (var document = ImageTools.Open(lossy, default))
        {
            var fields = ImageTools.Metadata(document, default);
            check(fields.Any(f => f.Label == "Pixel dimensions" && f.Value == "63 × 47"), "WebP metadata shows the pixel dimensions only");
        }

        // Animated and damaged files are refused with their messages and nothing is written.
        string animated = Path.Combine(folder, "moving.webp");
        File.Copy(Path.Combine(fixtures, "animated.webp"), animated);
        string? message = null;
        try { ImageTools.Run(animated, "convert.png", 1920, default); } catch (UserError error) { message = error.Message; }
        check(message == WebPImage.Animated && !Directory.EnumerateFiles(folder, "moving converted*").Any(), "An animated WebP is refused and nothing is written");
        string broken = Path.Combine(folder, "broken.webp");
        File.WriteAllBytes(broken, File.ReadAllBytes(lossy).Take(200).ToArray());
        message = null;
        try { ImageTools.Run(broken, "convert.png", 1920, default); } catch (UserError error) { message = error.Message; }
        check(message == WebPImage.Invalid && !Directory.EnumerateFiles(folder, "broken converted*").Any(), "A truncated WebP is refused and nothing is written");
    }
}
