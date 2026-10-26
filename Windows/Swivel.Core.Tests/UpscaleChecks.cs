using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class UpscaleChecks
{
    const int T = Upscale.Tile, O = Upscale.Tile * Upscale.ModelScale;

    // A stand-in model: optional 3 × 3 mean inside the tile, then nearest-neighbour 4×.
    static float[] Fake(float[] input, bool mean)
    {
        int T = (int)Math.Sqrt(input.Length / 3), O = T * 4;
        var output = new float[3 * O * O];
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < O; y++)
                for (int x = 0; x < O; x++)
                {
                    int sx = x / 4, sy = y / 4; float v;
                    if (!mean) v = input[c * T * T + sy * T + sx];
                    else
                    {
                        float sum = 0;
                        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                                sum += input[c * T * T + Math.Clamp(sy + dy, 0, T - 1) * T + Math.Clamp(sx + dx, 0, T - 1)];
                        v = sum / 9;
                    }
                    output[c * O * O + y * O + x] = v;
                }
        return output;
    }

    static PixelImage Pattern(int w, int h, bool alpha = false)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = (byte)(x * 7 + y * 3); bgra[i + 1] = (byte)(x * x + y); bgra[i + 2] = (byte)(y * 11 ^ x); bgra[i + 3] = alpha && x < w / 2 ? (byte)0 : (byte)255;
            }
        return new PixelImage(w, h, bgra);
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(Upscale.Mirror(-1, 5) == 1 && Upscale.Mirror(5, 5) == 3 && Upscale.Mirror(-64, 3) == 0 && Upscale.Mirror(70, 1) == 0 && Upscale.Mirror(2, 5) == 2 && Upscale.Mirror(9, 5) == 1, "Upscale mirrors past the edges like PyTorch's reflect");
        check(Upscale.Dimensions(2000, 2000, 4) == (8000, 8000) && Upscale.Dimensions(4000, 4000, 2) == (8000, 8000) && Upscale.Dimensions(8192, 1000, 2) == (16384, 2000), "Upscale output sizes at the limits");
        reject(() => Upscale.Dimensions(2001, 2000, 4), "Upscale refuses more than 64 megapixels out");
        reject(() => Upscale.Dimensions(8193, 100, 2), "Upscale refuses more than 16384 pixels per side");
        reject(() => Upscale.Dimensions(4001, 4000, 2), "Upscale refuses more than 16 megapixels in");
        reject(() => Upscale.Dimensions(100, 100, 3), "Upscale accepts only 2× and 4×");
        check(Upscale.Tiles(128, 128) == 1 && Upscale.Tiles(129, 128) == 2 && Upscale.Tiles(300, 70) == 3 && Upscale.Tiles(512, 257, Upscale.ProcessorTile) == 4, "Upscale block count");
        reject(() => Upscale.Tiles(10, 10, 128), "Upscale refuses a tile without room for context");

        foreach (int size in new[] { Upscale.Tile, Upscale.ProcessorTile })
        foreach (var (w, h) in new[] { (1, 1), (130, 129), (300, 70), (257, 260) })
        {
            var image = Pattern(w, h); int calls = 0, last = 0;
            var four = Upscale.Run(image, 4, size, tile => { calls++; return Fake(tile, false); }, (done, total) => last = done == total ? total : -1, default);
            bool exact = true;
            for (int y = 0; y < h * 4 && exact; y++) for (int x = 0; x < w * 4; x++) for (int c = 0; c < 4; c++)
                        if (four.Bgra[(y * w * 4 + x) * 4 + c] != image.Bgra[(y / 4 * w + x / 4) * 4 + c]) { exact = false; break; }
            check(four.Width == w * 4 && four.Height == h * 4 && exact && calls == Upscale.Tiles(w, h, size) && last == calls, $"Upscale 4× stitches blocks exactly ({w}×{h}, tile {size})");
            var two = Upscale.Run(image, 2, size, tile => Fake(tile, false), null, default);
            bool exact2 = true;
            for (int y = 0; y < h * 2 && exact2; y++) for (int x = 0; x < w * 2; x++) for (int c = 0; c < 3; c++)
                        if (two.Bgra[(y * w * 2 + x) * 4 + c] != image.Bgra[(y / 2 * w + x / 2) * 4 + c]) { exact2 = false; break; }
            check(two.Width == w * 2 && exact2, $"Upscale 2× averages each 4× block ({w}×{h}, tile {size})");
        }

        // A model that looks at neighbours: blocks must see real context, so the joins are invisible.
        foreach (int size in new[] { Upscale.Tile, Upscale.ProcessorTile })
        {
            int w = 300, h = 290; var image = Pattern(w, h);
            var four = Upscale.Run(image, 4, size, tile => Fake(tile, true), null, default);
            int worst = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++)
                    {
                        float sum = 0;
                        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) sum += image.Bgra[(Upscale.Mirror(y + dy, h) * w + Upscale.Mirror(x + dx, w)) * 4 + c] / 255f;
                        int expected = (int)Math.Round(Math.Clamp(sum / 9, 0f, 1f) * 255f);
                        worst = Math.Max(worst, Math.Abs(four.Bgra[((y * 4 + 2) * w * 4 + x * 4 + 1) * 4 + c] - expected));
                    }
            check(worst <= 1, $"Upscale blocks carry real neighbouring context (worst {worst}, tile {size})");
        }

        // Transparency: alpha is enlarged on its own; hidden colour never reaches the model.
        {
            int w = 40, h = 20; var image = Pattern(w, h, alpha: true);
            for (int p = 0; p < w * h; p++) if (image.Bgra[p * 4 + 3] == 0) { image.Bgra[p * 4] = 255; image.Bgra[p * 4 + 1] = 0; image.Bgra[p * 4 + 2] = 255; }
            bool magenta = false;
            var result = Upscale.Run(image, 4, Upscale.Tile, tile =>
            {
                int n = tile.Length / 3;
                for (int q = 0; q < n; q++) if (tile[q] == 1f && tile[n + q] == 0f && tile[2 * n + q] == 1f) magenta = true;
                return Fake(tile, false);
            }, null, default);
            check(result.Bgra[(10 * 160 + 4) * 4 + 3] == 0 && result.Bgra[(10 * 160 + 150) * 4 + 3] == 255, "Upscale keeps transparent and opaque areas");
            check(!magenta, "Upscale never shows the model colour hidden under transparency");
            check(Upscale.Run(Pattern(8, 8), 2, Upscale.Tile, tile => Fake(tile, false), null, default).Bgra.Where((_, i) => i % 4 == 3).All(a => a == 255), "Upscale keeps opaque images opaque");
        }

        reject(() => Upscale.Run(Pattern(8, 8), 4, Upscale.Tile, _ => new float[10], null, default), "Upscale refuses a model output of the wrong size");
        var nan = Upscale.Run(Pattern(4, 4), 4, Upscale.Tile, _ => Enumerable.Repeat(float.NaN, 3 * O * O).ToArray(), null, default);
        check(nan.Bgra.Where((_, i) => i % 4 != 3).All(v => v == 0), "Upscale turns a non-finite model output into black, never garbage");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            reject(() => Upscale.Run(Pattern(8, 8), 4, Upscale.Tile, tile => Fake(tile, false), null, cancelled.Token), "Upscale honours cancellation");
        }
        check(Catalog.Actions.Any(a => a.Id == "image.upscale" && a.ImageOnly && !a.SingleFile), "Upscale catalog entry");
        check(Upscale.ModelBytes == 66_903_282 && Upscale.ModelSha256.Length == 64, "Upscale model pin");
    }
}
