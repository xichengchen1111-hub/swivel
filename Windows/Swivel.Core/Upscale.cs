using System;
using System.Threading;

namespace Swivel.Core;

/// Real-ESRGAN x4plus upscaling like the Mac's: square model tiles whose useful centre has 64
/// pixels of real neighbouring context on each side (the photo is mirrored past its edges), so
/// tiles join without seams. The app uses the Mac's 256 × 256 tiles (128 × 128 useful); less
/// context shows seams (32 pixels: up to 30 levels apart on test photos) and larger tiles need
/// several gigabytes on the processor. 2× is each 4× tile averaged 2 × 2.
/// Colour and alpha are handled apart: fully transparent pixels take nearby colour before the
/// model sees them, and alpha is enlarged separately with Lanczos.
public static class Upscale
{
    public const string ModelFile = "realesrgan-x4plus.onnx";
    public const long ModelBytes = 66_903_282;
    public const string ModelSha256 = "043A257B04A3F1B54D9D7D1AC17B5BECA43FFDD457D55278FB944F96E0BE5994";
    public const int Tile = 256, Halo = 64, ModelScale = 4;
    public const long MaxInputPixels = 16_000_000, MaxOutputPixels = 64_000_000;
    public const int MaxOutputSide = 16384;
    private const string TooLarge = "The enlarged image would be too large. Use an image whose result stays within 64 megapixels and 16384 pixels per side.";

    public static (int Width, int Height) Dimensions(int width, int height, int factor)
    {
        if (factor is not (2 or 4)) throw new ArgumentException("Choose 2× or 4×.");
        if (width < 1 || height < 1 || (long)width * height > MaxInputPixels || (long)width * factor > MaxOutputSide || (long)height * factor > MaxOutputSide
            || (long)width * height * factor * factor > MaxOutputPixels) throw new UserError(TooLarge);
        return (width * factor, height * factor);
    }

    public static int Tiles(int width, int height, int tile = Tile)
    {
        int centre = Centre(tile);
        return checked(((width + centre - 1) / centre) * ((height + centre - 1) / centre));
    }

    private static int Centre(int tile) => tile is >= 3 * Halo and <= 512 ? tile - 2 * Halo : throw new ArgumentException("Invalid tile size.");

    /// `predict` takes one 3 × tile × tile RGB tile in 0–1 (channel planes) and returns 3 × 4tile × 4tile.
    public static PixelImage Run(PixelImage image, int factor, int tile, Func<float[], float[]> predict, Action<int, int>? progress, CancellationToken token)
    {
        image.Validate();
        int Tile = tile, Centre = Upscale.Centre(tile);
        var (ow, oh) = Dimensions(image.Width, image.Height, factor);
        int w = image.Width, h = image.Height;
        var colour = OpaqueColours(image, token, out bool transparent);
        var output = new byte[(long)ow * oh * 4];
        int across = (w + Centre - 1) / Centre, down = (h + Centre - 1) / Centre, total = across * down, done = 0;
        var input = new float[3 * Tile * Tile];
        int outTile = Tile * ModelScale, plane = outTile * outTile, skip = Halo * ModelScale, step = ModelScale / factor;
        for (int ty = 0; ty < down; ty++)
            for (int tx = 0; tx < across; tx++)
            {
                token.ThrowIfCancellationRequested();
                int cx = tx * Centre, cy = ty * Centre;
                for (int y = 0; y < Tile; y++)
                {
                    int sy = Mirror(cy - Halo + y, h);
                    for (int x = 0; x < Tile; x++)
                    {
                        int p = (sy * w + Mirror(cx - Halo + x, w)) * 4, q = y * Tile + x;
                        input[q] = colour[p + 2] / 255f; input[Tile * Tile + q] = colour[p + 1] / 255f; input[2 * Tile * Tile + q] = colour[p] / 255f;
                    }
                }
                var result = predict(input);
                if (result is null || result.Length != 3 * plane) throw new InvalidOperationException("Unexpected model output.");
                token.ThrowIfCancellationRequested();
                // The tile's centre, cut at the photo's right and bottom edges.
                int cw = Math.Min(Centre, w - cx) * factor, ch = Math.Min(Centre, h - cy) * factor;
                for (int y = 0; y < ch; y++)
                {
                    long row = ((long)(cy * factor + y) * ow + cx * factor) * 4;
                    for (int x = 0; x < cw; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            float sum = 0;
                            for (int dy = 0; dy < step; dy++)
                                for (int dx = 0; dx < step; dx++) sum += result[c * plane + (skip + y * step + dy) * outTile + skip + x * step + dx];
                            float v = sum / (step * step);
                            // Channel planes are R, G, B; the image is BGRA.
                            output[row + x * 4 + 2 - c] = (byte)Math.Round(Math.Clamp(float.IsNaN(v) ? 0 : v, 0f, 1f) * 255f);
                        }
                }
                progress?.Invoke(++done, total);
            }
        if (!transparent) { for (long i = 3; i < output.Length; i += 4) output[i] = 255; }
        else
        {
            var alpha = new float[w * h];
            for (int p = 0; p < alpha.Length; p++) alpha[p] = image.Bgra[p * 4 + 3];
            Cutout.Expand(alpha, w, h, ow, oh, true, (y, values) =>
            {
                long row = (long)y * ow * 4;
                for (int x = 0; x < ow; x++) output[row + x * 4 + 3] = (byte)Math.Round(Math.Clamp(values[x], 0f, 255f));
            }, token);
        }
        return new(ow, oh, output);
    }

    /// Mirrors an index past the edges without repeating the edge pixel (like PyTorch's reflect).
    public static int Mirror(int i, int n)
    {
        if (n == 1) return 0;
        int period = 2 * (n - 1);
        i %= period; if (i < 0) i += period;
        return i < n ? i : period - i;
    }

    // Fully transparent pixels have no trustworthy colour; give them the colour of visible neighbours.
    private static byte[] OpaqueColours(PixelImage image, CancellationToken token, out bool transparent)
    {
        transparent = false;
        for (int p = 3; p < image.Bgra.Length; p += 4) if (image.Bgra[p] != 255) { transparent = true; break; }
        if (!transparent) return image.Bgra;
        var colour = (byte[])image.Bgra.Clone();
        var (planes, sw, sh) = Cutout.BackgroundPlanes(image, new byte[image.Width * image.Height], 0.02, token);
        for (int y = 0; y < image.Height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++)
            {
                int p = (y * image.Width + x) * 4;
                if (image.Bgra[p + 3] != 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    float v = Cutout.Sample(planes[c], sw, sh, image.Width, image.Height, x, y);
                    colour[p + c] = float.IsNaN(v) ? (byte)0 : (byte)Math.Round(Math.Clamp(v, 0f, 255f));
                }
            }
        }
        return colour;
    }
}
