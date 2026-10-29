using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class TgaPsdNativeChecks
{
    private static (string Path, byte[] Bgra) Png(string path, int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { var p = pixel(x, y); int i = (y * width + x) * 4; bytes[i] = p.B; bytes[i + 1] = p.G; bytes[i + 2] = p.R; bytes[i + 3] = p.A; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4)));
        using (var output = File.Create(path)) encoder.Save(output);
        return (path, bytes);
    }

    private static byte[] Decoded(string png)
    {
        using var stream = File.OpenRead(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4]; bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0); return bytes;
    }

    // Convert to TGA / PSD and back on real files through the same image tools the wheel uses.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "tga-psd"); Directory.CreateDirectory(folder);
        // Soft-edged picture: opaque centre, partly transparent ring, clear corners.
        var (source, pixels) = Png(Path.Combine(folder, "图标 soft.png"), 61, 33, (x, y) =>
        {
            int d = Math.Max(Math.Abs(x - 30), Math.Abs(y - 16) * 2);
            return ((byte)(x * 4), (byte)(y * 7), 200, (byte)(d < 20 ? 255 : d < 28 ? 255 - (d - 19) * 28 : 0));
        });
        byte[] original = File.ReadAllBytes(source);

        string tga = ImageTools.Run(source, "convert.tga", 1920, default);
        check(Path.GetFileName(tga) == "图标 soft converted.tga" && File.ReadAllBytes(source).SequenceEqual(original), "A PNG becomes 'name converted.tga' beside it; the original is unchanged");
        var fromTga = TgaImage.Read(File.ReadAllBytes(tga), default);
        check(fromTga.Width == 61 && fromTga.Height == 33 && fromTga.Bgra.SequenceEqual(pixels), "The TGA holds the PNG's pixels exactly, transparency included");
        string psd = ImageTools.Run(source, "convert.psd", 1920, default);
        check(Path.GetFileName(psd) == "图标 soft converted.psd", "A PNG becomes 'name converted.psd'");
        var fromPsd = PsdImage.Read(File.ReadAllBytes(psd), default);
        bool same = true;
        for (int i = 0; i < pixels.Length; i += 4) same &= pixels[i + 3] == 0 ? fromPsd.Bgra[i + 3] == 0 : fromPsd.Bgra.AsSpan(i, 4).SequenceEqual(pixels.AsSpan(i, 4));
        check(same, "The PSD holds the PNG's visible pixels exactly");

        // Back to PNG through the wheel: Windows' own PNG decoder sees the same pixels.
        string back = ImageTools.Run(tga, "convert.png", 1920, default);
        check(Path.GetFileName(back) == "图标 soft converted converted.png" && Decoded(back).SequenceEqual(pixels), "TGA → PNG gives back the original pixels");
        string fromPsdPng = ImageTools.Run(psd, "convert.png", 1920, default);
        var decoded = Decoded(fromPsdPng); same = true;
        for (int i = 0; i < pixels.Length; i += 4) same &= pixels[i + 3] == 0 ? decoded[i + 3] == 0 : decoded.AsSpan(i, 4).SequenceEqual(pixels.AsSpan(i, 4));
        check(same, "PSD → PNG gives back the original visible pixels");
        string jpg = ImageTools.Run(psd, "convert.jpg", 1920, default);
        check(File.Exists(jpg) && jpg.EndsWith(".jpg", StringComparison.Ordinal), "A PSD converts to JPEG (on white)");
        string rotated = ImageTools.Run(tga, "image.rotate", 1920, default);
        using (var stream = File.OpenRead(rotated))
        {
            var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            check(frame.PixelWidth == 33 && frame.PixelHeight == 61, "Other image tools take a TGA too (rotated to 33 × 61)");
        }

        // A damaged TGA is refused with its message and nothing is written.
        string broken = Path.Combine(folder, "broken.tga");
        File.WriteAllBytes(broken, File.ReadAllBytes(tga).Take(100).ToArray());
        string? message = null;
        try { ImageTools.Run(broken, "convert.png", 1920, default); } catch (UserError error) { message = error.Message; }
        check(message == TgaImage.Invalid && !Directory.EnumerateFiles(folder, "broken converted*").Any(), "A truncated TGA is refused and nothing is written");
        // A 16-bit PSD gets the unsupported message.
        string deep = Path.Combine(folder, "deep.psd");
        var deepBytes = File.ReadAllBytes(psd); deepBytes[23] = 16; File.WriteAllBytes(deep, deepBytes);
        message = null;
        try { ImageTools.Run(deep, "convert.png", 1920, default); } catch (UserError error) { message = error.Message; }
        check(message == PsdImage.Unsupported, "A 16-bit PSD is refused as unsupported");
    }
}
