using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class IcoNativeChecks
{
    private static string Png(string path, int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { var p = pixel(x, y); int i = (y * width + x) * 4; bytes[i] = p.B; bytes[i + 1] = p.G; bytes[i + 2] = p.R; bytes[i + 3] = p.A; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4)));
        using var output = File.Create(path); encoder.Save(output); return path;
    }

    private static byte[] Pixels(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4]; bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0); return bytes;
    }

    // Convert to ICO on real files, read back by Windows' own two icon readers (WPF's icon decoder and GDI+).
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "ico-export"); Directory.CreateDirectory(folder);
        // A red bar twice as wide as tall on transparency.
        string bar = Png(Path.Combine(folder, "徽标 bar.png"), 300, 150, (x, y) => (0, 0, 255, 255));
        byte[] original = File.ReadAllBytes(bar);
        string ico = ImageTools.Run(bar, "convert.ico", 1920, default);
        check(Path.GetFileName(ico) == "徽标 bar converted.ico" && File.ReadAllBytes(bar).SequenceEqual(original), "A PNG becomes 'name converted.ico' beside it; the original is unchanged");
        var entries = IcoFile.Read(File.ReadAllBytes(ico));
        check(entries.Select(e => e.Size).SequenceEqual(IcoFile.Sizes) && entries.All(e => e.Png == (e.Size == 256)), "A 300-pixel picture gets all six sizes, 256 as PNG");
        using (var stream = File.OpenRead(ico))
        {
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            check(decoder.Frames.Select(f => f.PixelWidth).OrderBy(s => s).SequenceEqual(IcoFile.Sizes) && decoder.Frames.All(f => f.PixelHeight == f.PixelWidth),
                "WPF's icon decoder reads every size as a square: " + string.Join(",", decoder.Frames.Select(f => f.PixelWidth)));
            foreach (int size in new[] { 32, 256 })
            {
                var frame = decoder.Frames.Single(f => f.PixelWidth == size); var px = Pixels(frame);
                int Alpha(int x, int y) => px[(y * size + x) * 4 + 3];
                bool redCentre = px[(size / 2 * size + size / 2) * 4 + 2] == 255 && Alpha(size / 2, size / 2) == 255;
                check(redCentre && Alpha(size / 2, size / 8) == 0 && Alpha(size / 2, size - 1 - size / 8) == 0 && Alpha(0, size / 2) == 255 && Alpha(size - 1, size / 2) == 255,
                    $"At {size} pixels the bar fills the width and sits centred between transparent bands");
            }
        }
        using (var icon = new System.Drawing.Icon(ico, new System.Drawing.Size(48, 48)))
        using (var image = icon.ToBitmap())
        {
            var centre = image.GetPixel(24, 24); var top = image.GetPixel(24, 2);
            check(icon.Width == 48 && centre.R == 255 && centre.A == 255 && top.A == 0, $"GDI+ loads the 48-pixel icon: centre {centre}, top {top}");
        }

        // Scaling averages fine detail instead of picking pixels, and transparent edges don't darken.
        string checker = Png(Path.Combine(folder, "checker.png"), 512, 512, (x, y) => ((x + y) % 2 == 0 ? (byte)0 : (byte)255, (x + y) % 2 == 0 ? (byte)0 : (byte)255, (x + y) % 2 == 0 ? (byte)0 : (byte)255, 255));
        var small = IcoFile.Read(File.ReadAllBytes(ImageTools.Run(checker, "convert.ico", 1920, default))).Single(e => e.Size == 16).Data;
        var greys = Enumerable.Range(0, 256).Select(i => (int)small[40 + i * 4]).ToArray();
        Console.WriteLine($"512-pixel checkerboard at 16: grey {greys.Min()}–{greys.Max()}");
        check(greys.All(g => g is > 80 and < 175), $"A 1-pixel checkerboard averages to grey at 16 pixels: {greys.Min()}–{greys.Max()}");
        // Edges that don't line up with the reduction, so the smaller sizes have partly transparent pixels.
        string dot = Png(Path.Combine(folder, "dot.png"), 100, 100, (x, y) => x is >= 17 and < 83 && y is >= 17 and < 83 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var dotIcon = IcoFile.Read(File.ReadAllBytes(ImageTools.Run(dot, "convert.ico", 1920, default)));
        check(dotIcon.Select(e => e.Size).SequenceEqual(new[] { 16, 32, 48, 64 }), "A 100-pixel picture stops at 64");
        int darkest = 255, partial = 0;
        foreach (var entry in dotIcon)
            for (int i = 0; i < entry.Size * entry.Size; i++)
            {
                byte alpha = entry.Data[40 + i * 4 + 3];
                if (alpha is > 0 and < 255) partial++;
                if (alpha > 0) darkest = Math.Min(darkest, Math.Min(entry.Data[40 + i * 4], Math.Min(entry.Data[40 + i * 4 + 1], entry.Data[40 + i * 4 + 2])));
            }
        check(partial > 0 && darkest >= 250, $"A white square on transparency keeps white edges when scaled down: {partial} partly transparent pixels, darkest visible channel {darkest}");

        // A tiny JPEG still gets 16 pixels; cancelling first writes nothing.
        string tiny = Path.Combine(folder, "tiny.jpg");
        var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(BitmapSource.Create(10, 6, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat((byte)200, 180).ToArray(), 30)));
        using (var output = File.Create(tiny)) jpeg.Save(output);
        var tinyIcon = IcoFile.Read(File.ReadAllBytes(ImageTools.Run(tiny, "convert.ico", 1920, default)));
        check(tinyIcon.Count == 1 && tinyIcon[0].Size == 16 && tinyIcon[0].Data[40 + 8 * 16 * 4 + 8 * 4 + 3] == 255, "A 10×6 JPEG becomes a single, opaque-centred 16-pixel icon");
        int before = Directory.GetFiles(folder).Length;
        reject(() => ImageTools.Run(bar, "convert.ico", 1920, new CancellationToken(true)), "Convert to ICO cancelled before it starts");
        check(Directory.GetFiles(folder).Length == before && !Directory.GetFiles(folder, ".swivel-*.tmp").Any(), "Cancelling before the conversion starts writes no file or staging copy (the writer's own cancellation is a core check)");
        check(Path.GetFileName(ImageTools.Run(bar, "convert.ico", 1920, default)) == "徽标 bar converted 2.ico", "A second conversion takes the next numbered name");
    }
}
