using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

/// The pinned Real-ESRGAN x4plus model, converted from the official checkpoint at build time.
internal static class UpscaleModel
{
    private static readonly PinnedModel Model = new(Upscale.ModelFile, Upscale.ModelBytes, Upscale.ModelSha256,
        new long[] { 1, 3, Upscale.Tile, Upscale.Tile }, 3 * Upscale.Tile * Upscale.ModelScale * Upscale.Tile * Upscale.ModelScale,
        "The upscaling model is not included in this copy of Swivel. Use a complete package.",
        "The upscaling model in this copy of Swivel is damaged. Use a complete package.",
        "Windows ML could not run the upscaling model on this PC.");

    internal static string ModelPath { get => Model.Path; set => Model.Path = value; }
    internal static PinnedModel Diagnostics => Model;

    public static float[] Predict(float[] input, CancellationToken token) => Model.Predict(input, token);
}

/// Upscale 2× or 4×: a new file beside the original, JPEG stays JPEG (quality 95), everything else becomes PNG.
public static class Upscaler
{
    public static string Run(string path, int factor, CancellationToken token, Action<int, int>? progress = null)
    {
        using var document = ImageTools.Open(path, token);
        Upscale.Dimensions(document.Bitmap.PixelWidth, document.Bitmap.PixelHeight, factor);
        var pixels = ImageTools.ReadPixels(document.Bitmap, 0, token);
        var result = Upscale.Run(pixels, factor, tile => UpscaleModel.Predict(tile, token), progress, token);
        token.ThrowIfCancellationRequested();
        var source = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, result.Bgra, result.Width * 4);
        // A JPEG name with transparent content (a renamed PNG) still keeps its transparency as PNG.
        bool opaque = true;
        for (int i = 3; i < result.Bgra.Length && opaque; i += 4) opaque = result.Bgra[i] == 255;
        string extension = opaque && System.IO.Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? "jpg" : "png";
        BitmapEncoder encoder = extension == "jpg"
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(extension == "jpg" ? new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0) : source));
        return NativeFiles.WriteBeside(document.Input, System.IO.Path.GetFileNameWithoutExtension(path) + $" {factor}x", extension, stream => encoder.Save(stream), token);
    }
}
