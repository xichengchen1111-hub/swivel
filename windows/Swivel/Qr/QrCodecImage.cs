namespace Swivel;

// Pixels for the QR and barcode readers: 8-bit luminance, resampling, local (block-adaptive) and global
// binarisation, and the projective transform between module space and image space.

/// An 8-bit luminance image (0 = black), row-major. Transparent pixels count as white.
sealed class QrLuma(byte[] pixels, int width, int height)
{
    public readonly byte[] Pixels = pixels;
    public readonly int Width = width, Height = height;

    public static QrLuma FromArgb(int[] data, int width, int height)
    {
        var pixels = new byte[(long)width * height];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int c = data[row + x];
                int a = (c >> 24) & 255, r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                int l = (r * 77 + g * 150 + b * 29) >> 8;
                if (a != 255) l = (l * a + 255 * (255 - a)) / 255;
                pixels[row + x] = (byte)l;
            }
        });
        return new QrLuma(pixels, width, height);
    }

    /// The darkest colour channel (transparency over white), or null when the image has almost no colour. Light
    /// coloured modules (orange, pink, light green on white) differ little from white in luminance but a lot in
    /// their weakest channel.
    public static QrLuma? ColourContrast(int[] data, int width, int height)
    {
        int coloured = 0, sampled = 0;
        for (int i = 0; i < data.Length; i += 97, sampled++)
        {
            int c = data[i], r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40) coloured++;
        }
        if (coloured * 100 < sampled) return null;
        var pixels = new byte[(long)width * height];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int c = data[row + x];
                int a = (c >> 24) & 255, l = Math.Min((c >> 16) & 255, Math.Min((c >> 8) & 255, c & 255));
                if (a != 255) l = (l * a + 255 * (255 - a)) / 255;
                pixels[row + x] = (byte)l;
            }
        });
        return new QrLuma(pixels, width, height);
    }

    /// Box-filtered reduction by an integer factor.
    public QrLuma Downscale(int factor)
    {
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var result = new byte[w * h];
        int area = factor * factor, srcW = Width;
        var src = Pixels;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * srcW + x * factor;
                    for (int dx = 0; dx < factor; dx++) sum += src[row + dx];
                }
                result[y * w + x] = (byte)(sum / area);
            }
        });
        return new QrLuma(result, w, h);
    }

    /// Bilinear enlargement by an integer factor.
    public QrLuma Upscale(int factor)
    {
        int w = Width * factor, h = Height * factor;
        var result = new byte[w * h];
        var src = Pixels;
        int sw = Width, sh = Height;
        Parallel.For(0, h, y =>
        {
            double sy = (y + 0.5) / factor - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
            double fy = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < w; x++)
            {
                double sx = (x + 0.5) / factor - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                double fx = Math.Clamp(sx - x0, 0, 1);
                double top = src[y0 * sw + x0] * (1 - fx) + src[y0 * sw + x1] * fx;
                double bottom = src[y1 * sw + x0] * (1 - fx) + src[y1 * sw + x1] * fx;
                result[y * w + x] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
            }
        });
        return new QrLuma(result, w, h);
    }

    /// 3×3 box blur, which removes single-pixel noise before binarisation.
    public QrLuma Smooth()
    {
        int w = Width, h = Height;
        var result = new byte[w * h];
        var src = Pixels;
        Parallel.For(0, h, y =>
        {
            int ya = Math.Max(0, y - 1), yb = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(0, x - 1), xb = Math.Min(w - 1, x + 1), sum = 0, n = 0;
                for (int yy = ya; yy <= yb; yy++)
                    for (int xx = xa; xx <= xb; xx++) { sum += src[yy * w + xx]; n++; }
                result[y * w + x] = (byte)(sum / n);
            }
        });
        return new QrLuma(result, w, h);
    }

    /// Morphological closing of the dark parts (minimum filter, then maximum filter, square of side 2r + 1): light
    /// seams and specks narrower than about 2r pixels inside dark areas disappear, dark areas keep their size.
    public QrLuma Closed(int radius)
    {
        var eroded = Filter(Pixels, Width, Height, radius, min: true);
        return new QrLuma(Filter(eroded, Width, Height, radius, min: false), Width, Height);
    }

    /// Dark areas grown by `radius` pixels (minimum filter): rebalances blocks drawn smaller than their modules.
    public QrLuma Darkened(int radius) => new(Filter(Pixels, Width, Height, radius, min: true), Width, Height);

    static byte[] Filter(byte[] src, int w, int h, int r, bool min)
    {
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int v = src[row + x];
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) v = min ? Math.Min(v, src[row + k]) : Math.Max(v, src[row + k]);
                tmp[row + x] = (byte)v;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int v = tmp[y * w + x];
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) v = min ? Math.Min(v, tmp[k * w + x]) : Math.Max(v, tmp[k * w + x]);
                dst[y * w + x] = (byte)v;
            }
        });
        return dst;
    }
}

/// A binarised image: Bits[i] = 1 for dark. `Flip` = 1 reads it inverted (light-on-dark codes).
sealed class QrBinary(byte[] bits, int width, int height, byte flip = 0)
{
    public readonly byte[] Bits = bits;
    public readonly int Width = width, Height = height;
    public readonly byte Flip = flip;

    public bool Dark(int x, int y) => (Bits[y * Width + x] ^ Flip) != 0;
    public QrBinary Inverted() => new(Bits, Width, Height, (byte)(Flip ^ 1));
}

static class QrBinarizer
{
    const int Block = 8, MinRange = 24;

    /// Block-adaptive threshold: each 8×8 block gets a black point from its own range (or its neighbours' when it
    /// is flat), and each pixel is compared with the mean black point of the surrounding 5×5 blocks. Handles uneven
    /// lighting and shadows in photos.
    public static QrBinary Hybrid(QrLuma luma)
    {
        int w = luma.Width, h = luma.Height;
        if (w < Block * 5 || h < Block * 5) return Global(luma);
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var src = luma.Pixels;
        var black = new int[bw * bh];
        for (int by = 0; by < bh; by++)
        {
            int y0 = Math.Min(by * Block, h - Block);
            for (int bx = 0; bx < bw; bx++)
            {
                int x0 = Math.Min(bx * Block, w - Block), sum = 0, min = 255, max = 0;
                for (int y = y0; y < y0 + Block; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x0 + Block; x++)
                    {
                        int v = src[row + x];
                        sum += v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
                int average = sum / (Block * Block);
                if (max - min <= MinRange)
                {
                    // A flat block: assume background (light) unless the neighbours say it is inside a dark area.
                    average = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        int neighbours = (black[(by - 1) * bw + bx] + 2 * black[by * bw + bx - 1] + black[(by - 1) * bw + bx - 1]) / 4;
                        if (min < neighbours) average = neighbours;
                    }
                }
                black[by * bw + bx] = average;
            }
        }
        var bits = new byte[w * h];
        Parallel.For(0, bh, by =>
        {
            int cy = Math.Clamp(by, 2, bh - 3), y0 = Math.Min(by * Block, h - Block);
            for (int bx = 0; bx < bw; bx++)
            {
                int cx = Math.Clamp(bx, 2, bw - 3), sum = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int row = (cy + dy) * bw + cx;
                    sum += black[row - 2] + black[row - 1] + black[row] + black[row + 1] + black[row + 2];
                }
                int threshold = sum / 25, x0 = Math.Min(bx * Block, w - Block);
                for (int y = y0; y < y0 + Block; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x0 + Block; x++) bits[row + x] = src[row + x] <= threshold ? (byte)1 : (byte)0;
                }
            }
        });
        return new QrBinary(bits, w, h);
    }

    /// One threshold for the whole image (Otsu's method).
    public static QrBinary Global(QrLuma luma)
    {
        var histogram = new int[256];
        foreach (byte v in luma.Pixels) histogram[v]++;
        int threshold = Otsu(histogram, luma.Pixels.Length);
        var bits = new byte[luma.Pixels.Length];
        for (int i = 0; i < bits.Length; i++) bits[i] = luma.Pixels[i] <= threshold ? (byte)1 : (byte)0;
        return new QrBinary(bits, luma.Width, luma.Height);
    }

    public static int Otsu(ReadOnlySpan<int> histogram, int total)
    {
        if (total == 0) return 127;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)histogram[i];
        double sumBack = 0, best = -1;
        int weightBack = 0, threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) continue;
            int weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += t * (double)histogram[t];
            double meanBack = sumBack / weightBack, meanFore = (sumAll - sumBack) / weightFore;
            double between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }
}

/// A projective (homography) transform: (x, y) = ((a·u + b·v + c) / (g·u + h·v + i), (d·u + e·v + f) / (…)).
readonly struct QrPerspective(double a, double b, double c, double d, double e, double f, double g, double h, double i)
{
    readonly double a = a, b = b, c = c, d = d, e = e, f = f, g = g, h = h, i = i;

    public (double X, double Y) Map(double u, double v)
    {
        double w = g * u + h * v + i;
        return ((a * u + b * v + c) / w, (d * u + e * v + f) / w);
    }

    /// The transform taking the unit square's corners (0,0), (1,0), (1,1), (0,1) to the four points (Heckbert).
    public static QrPerspective SquareToQuad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        double dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
        if (Math.Abs(dx3) < 1e-10 && Math.Abs(dy3) < 1e-10)
            return new QrPerspective(x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0, 1);
        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        double den = dx1 * dy2 - dx2 * dy1;
        double g = (dx3 * dy2 - dx2 * dy3) / den, h = (dx1 * dy3 - dx3 * dy1) / den;
        return new QrPerspective(x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h, 1);
    }

    /// The adjugate, which is the inverse up to scale (all that matters for a homography).
    public QrPerspective Inverse() => new(
        e * i - f * h, c * h - b * i, b * f - c * e,
        f * g - d * i, a * i - c * g, c * d - a * f,
        d * h - e * g, b * g - a * h, a * e - b * d);

    /// this ∘ other: apply `other` first.
    public QrPerspective After(QrPerspective o) => new(
        a * o.a + b * o.d + c * o.g, a * o.b + b * o.e + c * o.h, a * o.c + b * o.f + c * o.i,
        d * o.a + e * o.d + f * o.g, d * o.b + e * o.e + f * o.h, d * o.c + e * o.f + f * o.i,
        g * o.a + h * o.d + i * o.g, g * o.b + h * o.e + i * o.h, g * o.c + h * o.f + i * o.i);

    /// Maps the quadrilateral (u0,v0)…(u3,v3) onto (x0,y0)…(x3,y3), corresponding corners in order.
    public static QrPerspective QuadToQuad(
        double u0, double v0, double u1, double v1, double u2, double v2, double u3, double v3,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        SquareToQuad(x0, y0, x1, y1, x2, y2, x3, y3).After(SquareToQuad(u0, v0, u1, v1, u2, v2, u3, v3).Inverse());
}
