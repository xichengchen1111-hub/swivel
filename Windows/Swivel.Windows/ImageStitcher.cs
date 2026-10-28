using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

/// Stitch one hung image below another: both held and decoded upright as 8-bit SDR (the usual
/// image checks), the wider scaled down to the narrower one's width, then saved as a PNG named
/// "<upper> stitched.png" beside the upper image (numbered when taken, never overwriting).
internal static class ImageStitcher
{
    public static string Save(string top, string bottom, CancellationToken token)
    {
        if (string.Equals(Path.GetFullPath(top), Path.GetFullPath(bottom), StringComparison.OrdinalIgnoreCase)) throw new UserError(ImageStitch.ChooseTwo);
        using var upper = ImageTools.Open(top, token);
        using var lower = ImageTools.Open(bottom, token);
        BitmapSource a = upper.Bitmap, b = lower.Bitmap;
        var size = ImageStitch.Size(a.PixelWidth, a.PixelHeight, b.PixelWidth, b.PixelHeight);
        var joined = ImageStitch.Stack(Scale(a, size.Width, size.Top, token), Scale(b, size.Width, size.Bottom, token), token);
        token.ThrowIfCancellationRequested();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(joined.Width, joined.Height, 96, 96, PixelFormats.Bgra32, null, joined.Bgra, joined.Width * 4)));
        return NativeFiles.WriteBeside(upper.Input, Path.GetFileNameWithoutExtension(top) + ImageStitch.Suffix, "png", png.Save, token);
    }

    /// Scaled premultiplied, so transparent edges don't darken; unchanged when already that size.
    private static PixelImage Scale(BitmapSource source, int width, int height, CancellationToken token)
    {
        if (source.PixelWidth == width && source.PixelHeight == height) return ImageTools.ReadPixels(source, 0, token);
        var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var scaled = ImageTools.ReadPixels(new TransformedBitmap(premultiplied, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight)), 0, token);
        if (scaled.Width == width && scaled.Height == height) return scaled;
        // WPF rounded the size differently: centre what it made on the exact size.
        var exact = new byte[width * height * 4];
        int dx = (width - scaled.Width) / 2, dy = (height - scaled.Height) / 2;
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp(y - dy, 0, scaled.Height - 1);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp(x - dx, 0, scaled.Width - 1);
                Buffer.BlockCopy(scaled.Bgra, (sy * scaled.Width + sx) * 4, exact, (y * width + x) * 4, 4);
            }
        }
        return new PixelImage(width, height, exact);
    }
}
