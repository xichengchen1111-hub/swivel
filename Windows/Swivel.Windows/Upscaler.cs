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
        "The upscaling model is not included in this copy of Swivel. Use a complete package.",
        "The upscaling model in this copy of Swivel is damaged. Use a complete package.",
        "Windows ML could not run the upscaling model on this PC.");

    internal static string ModelPath { get => Model.Path; set => Model.Path = value; }
    internal static PinnedModel Diagnostics => Model;

    /// Loads the model and reports whether Windows ML found a graphics card for it.
    public static bool UsesGraphics(CancellationToken token) => Model.UsesGraphics(token);

    public static float[] Predict(float[] input, CancellationToken token)
    {
        int side = (int)Math.Sqrt(input.Length / 3);
        return Model.Predict(input, new long[] { 1, 3, side, side }, 3 * side * Upscale.ModelScale * side * Upscale.ModelScale, token);
    }
}

/// Upscale 2× or 4×: a new file beside the original, JPEG stays JPEG (quality 95), everything else becomes PNG.
public static class Upscaler
{
    /// `progress` receives blocks done, blocks in this file and the estimated time left.
    public static string Run(string path, int factor, CancellationToken token, Action<int, int, TimeSpan?>? progress = null)
    {
        using var document = ImageTools.Open(path, token);
        Upscale.Dimensions(document.Bitmap.PixelWidth, document.Bitmap.PixelHeight, factor);
        var pixels = ImageTools.ReadPixels(document.Bitmap, 0, token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = Upscale.Run(pixels, factor, Upscale.Tile, tile => UpscaleModel.Predict(tile, token),
            (done, total) => progress?.Invoke(done, total, done < total ? clock.Elapsed / done * (total - done) : null), token);
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
