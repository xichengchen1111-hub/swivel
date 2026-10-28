using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ReadOnlyImageNativeChecks
{
    // As on the Mac since 1.14.6: tools that only look at a picture (code and text recognition)
    // accept 16-bit images, while tools that save a new image still refuse them.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string folder = Path.Combine(directory, "read-only images"); Directory.CreateDirectory(folder);
        string text = "Swivel 16-bit 中文";
        string flat = QrTools.Save(text, QrCorrection.Medium, folder, default);
        BitmapSource source;
        using (var stream = File.OpenRead(flat)) { source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
        foreach (var (name, format, encoder) in new (string, PixelFormat, BitmapEncoder)[]
        {
            ("deep.png", PixelFormats.Rgba64, new PngBitmapEncoder()), ("gray16.png", PixelFormats.Gray16, new PngBitmapEncoder()), ("deep.tif", PixelFormats.Rgb48, new TiffBitmapEncoder()),
        })
        {
            string path = Path.Combine(folder, name);
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, format, null, 0)));
            using (var file = File.Create(path)) encoder.Save(file);
            using (var probe = File.OpenRead(path))
                check(BitmapFrame.Create(probe, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Format.BitsPerPixel >= 16, $"{name} is stored with 16 bits per channel");
            using (var document = QrReadDocument.Open(path, default))
                check(document.Result.Text == text, $"Reading codes accepts a 16-bit image ({name})");
            string? refused = null;
            try { ImageTools.Open(path, default).Dispose(); } catch (UserError error) { refused = error.Message; }
            check(refused == "HDR, high-bit-depth and CMYK images are not supported yet.", $"Tools that save images still refuse a 16-bit image ({name})");
        }
    }
}
