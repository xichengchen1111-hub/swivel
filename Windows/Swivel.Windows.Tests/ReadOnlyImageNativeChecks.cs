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
    private static readonly string SrgbProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");

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
            ("cmyk.tif", PixelFormats.Cmyk32, new TiffBitmapEncoder()), ("profiled.png", PixelFormats.Rgba64, new PngBitmapEncoder()), ("profiled.tif", PixelFormats.Rgb48, new TiffBitmapEncoder()),
        })
        {
            string path = Path.Combine(folder, name);
            // The two "profiled" images carry Windows' own sRGB profile, when this PC has it.
            var profile = name.StartsWith("profiled") && File.Exists(SrgbProfile) ? new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(new[] { new ColorContext(new Uri(SrgbProfile)) }) : null;
            if (name.StartsWith("profiled")) check(profile is not null, "Windows' sRGB colour profile is available for the profiled fixtures");
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, format, null, 0), (BitmapSource?)null, (BitmapMetadata?)null, profile));
            using (var file = File.Create(path)) encoder.Save(file);
            using (var probe = File.OpenRead(path))
            {
                var stored = BitmapFrame.Create(probe, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                check(!ImageTools.IsSdrFormat(stored.Format) && (!name.StartsWith("profiled") || stored.ColorContexts is { Count: > 0 }), $"{name} is stored as {stored.Format}{(stored.ColorContexts is { Count: > 0 } ? " with a colour profile" : "")}");
            }
            using (var document = QrReadDocument.Open(path, default))
                check(document.Result.Text == text, $"Reading codes accepts a 16-bit, CMYK or profiled image ({name})");
            string? refused = null;
            try { ImageTools.Open(path, default).Dispose(); } catch (UserError error) { refused = error.Message; }
            check(refused == "HDR, high-bit-depth and CMYK images are not supported yet.", $"Tools that save images still refuse it ({name})");
        }
    }
}
