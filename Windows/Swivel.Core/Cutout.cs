using System;
using System.Threading;

namespace Swivel.Core;

/// Subject masks from the pinned U²-Net model (ThirdParty/U2Net): the pixel work before and
/// after the model. The model itself runs in the Windows app through Windows ML.
public static class Cutout
{
    public const int Side = 320;
    public const string ModelFile = "u2net.onnx";
    public const long ModelBytes = 175_997_641;
    public const string ModelSha256 = "8d10d2f3bb75ae3b6d527c77944fc5e7dcd94b29809d47a739a7a728a912b491";
    public const string NoSubject = "No clear subject was found in this image.";
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Deviation = { 0.229f, 0.224f, 0.225f };

    /// The model input, 1×3×320×320 RGB: transparent pixels shown on white, the image
    /// stretched to a square, divided by its brightest value and normalised like ImageNet.
    public static float[] Input(PixelImage image, CancellationToken token)
    {
        image.Validate();
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            int channel = 2 - c; // BGRA → R, G, B
            planes[c] = Shrink(image.Width, image.Height, Side, Side, true, (y, row) =>
            {
                int i = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++, i += 4)
                {
                    float alpha = image.Bgra[i + 3] / 255f;
                    row[x] = image.Bgra[i + channel] * alpha + 255f * (1 - alpha);
                }
            }, token);
        }
        // Clamped and rounded to whole levels like the 8-bit image upstream resized.
        foreach (var plane in planes) for (int i = 0; i < plane.Length; i++) plane[i] = MathF.Round(Math.Clamp(plane[i], 0f, 255f));
        float max = 1e-6f;
        foreach (var plane in planes) foreach (float v in plane) max = Math.Max(max, v);
        var input = new float[3 * Side * Side];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < Side * Side; i++) input[c * Side * Side + i] = (planes[c][i] / max - Mean[c]) / Deviation[c];
        return input;
    }

    /// The first model output (320×320), min–max normalised and resized to the image as a
    /// 0–255 mask. Refused when the model found no subject.
    public static byte[] Mask(float[] prediction, int width, int height, CancellationToken token)
    {
        ImageRules.Validate(width, height);
        if (prediction is null || prediction.Length != Side * Side) throw new ArgumentException("Invalid model output.");
        float min = float.MaxValue, max = float.MinValue;
        foreach (float v in prediction)
        {
            if (!float.IsFinite(v)) throw new UserError(NoSubject);
            min = Math.Min(min, v); max = Math.Max(max, v);
        }
        if (max - min < 1e-6f) throw new UserError(NoSubject);
        var normalised = new float[prediction.Length];
        for (int i = 0; i < prediction.Length; i++) normalised[i] = (prediction[i] - min) / (max - min);
        var mask = new byte[(long)width * height];
        long solid = 0;
        Expand(normalised, Side, Side, width, height, true, (y, row) =>
        {
            int start = y * width;
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)Math.Round(Math.Clamp(row[x], 0f, 1f) * 255f);
                mask[start + x] = value;
                if (value >= 128) solid++;
            }
        }, token);
        if (solid * 1000 < (long)width * height) throw new UserError(NoSubject);
        return mask;
    }

    /// Mask values below this are background the model was only slightly unsure about; they
    /// become fully transparent rather than keeping faint, recoverable background colour.
    public const int Floor = 16;

    /// The subject on transparency. Faint mask values are dropped and fully transparent pixels
    /// keep no colour. Semi-transparent pixels (edges, areas the model is unsure of) keep
    /// their original colour, as straight alpha requires.
    public static PixelImage Apply(PixelImage image, byte[] mask)
    {
        image.Validate(); CheckMask(image, mask);
        var output = (byte[])image.Bgra.Clone();
        for (int p = 0, i = 0; p < mask.Length; p++, i += 4)
        {
            int m = mask[p] < Floor ? 0 : mask[p];
            int alpha = (output[i + 3] * m + 127) / 255;
            output[i + 3] = (byte)alpha;
            if (alpha == 0) { output[i] = 0; output[i + 1] = 0; output[i + 2] = 0; }
        }
        return new(image.Width, image.Height, output);
    }

    /// Everything behind the subject blurred. The blur only averages background pixels
    /// (weighted by 1 − mask), so the subject's colours never bleed into a halo around it.
    /// It runs on a copy at most 1024 pixels on the long edge and is scaled back up.
    public static PixelImage BlurBackground(PixelImage image, byte[] mask, CancellationToken token)
    {
        image.Validate(); CheckMask(image, mask);
        int w = image.Width, h = image.Height;
        double scale = Math.Min(1, 1024.0 / Math.Max(w, h));
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        // Premultiplied by the background weight: B, G, R and the weight itself.
        var small = new float[4][];
        for (int c = 0; c < 4; c++)
        {
            int channel = c;
            small[c] = Shrink(w, h, sw, sh, false, (y, row) =>
            {
                int p = y * w, i = p * 4;
                for (int x = 0; x < w; x++, p++, i += 4)
                {
                    // Hidden colour under transparency never mixes into visible pixels.
                    float weight = (255 - mask[p]) / 255f * (image.Bgra[i + 3] / 255f);
                    row[x] = channel == 3 ? weight : image.Bgra[i + channel] * weight;
                }
            }, token);
        }
        int radius = Math.Max(1, (int)Math.Round(Math.Max(sw, sh) * 0.04));
        foreach (var plane in small) { for (int pass = 0; pass < 3; pass++) BoxBlur(plane, sw, sh, radius, token); }
        var output = (byte[])image.Bgra.Clone();
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            var plane = new float[sw * sh];
            for (int i = 0; i < plane.Length; i++) plane[i] = small[3][i] > 1e-4f ? small[c][i] / small[3][i] : float.NaN;
            planes[c] = plane;
        }
        for (int c = 0; c < 3; c++)
        {
            int channel = c;
            Expand(planes[c], sw, sh, w, h, false, (y, row) =>
            {
                int p = y * w, i = p * 4;
                for (int x = 0; x < w; x++, p++, i += 4)
                {
                    float m = mask[p] / 255f, original = image.Bgra[i + channel];
                    // Deep inside the subject there is no background nearby; its own colour stands in.
                    float background = float.IsNaN(row[x]) ? original : row[x];
                    output[i + channel] = (byte)Math.Round(Math.Clamp(original * m + background * (1 - m), 0f, 255f));
                }
            }, token);
        }
        return new(w, h, output);
    }

    private static void CheckMask(PixelImage image, byte[] mask)
    {
        if (mask is null || mask.Length != (long)image.Width * image.Height) throw new ArgumentException("Invalid mask.");
    }

    /// Separable filter taps that widen with the reduction, so a shrink uses every source pixel.
    /// Lanczos-3 matches how the model was used upstream (sharper edges); the triangle filter
    /// (bilinear when enlarging) never overshoots, for the blurred background.
    private sealed class Taps
    {
        public readonly int[] Start, Count, Index; public readonly float[] Weight;
        public Taps(int source, int target, bool lanczos)
        {
            double scale = (double)source / target, width = Math.Max(1, scale), support = lanczos ? 3 * width : width;
            Start = new int[target]; Count = new int[target];
            var index = new System.Collections.Generic.List<int>(); var weight = new System.Collections.Generic.List<float>();
            for (int i = 0; i < target; i++)
            {
                double center = (i + 0.5) * scale - 0.5;
                int first = (int)Math.Floor(center - support) + 1, last = (int)Math.Floor(center + support);
                double total = 0; int begin = index.Count;
                for (int j = first; j <= last; j++)
                {
                    double distance = Math.Abs(j - center) / width;
                    double value = lanczos ? Lanczos(distance) : 1 - distance;
                    if (lanczos ? distance >= 3 : value <= 0) continue;
                    index.Add(Math.Clamp(j, 0, source - 1)); weight.Add((float)value); total += value;
                }
                if (index.Count == begin || Math.Abs(total) < 1e-9) { index.RemoveRange(begin, index.Count - begin); weight.RemoveRange(begin, weight.Count - begin); index.Add(Math.Clamp((int)Math.Round(center), 0, source - 1)); weight.Add(1); total = 1; }
                for (int k = begin; k < index.Count; k++) weight[k] = (float)(weight[k] / total);
                Start[i] = begin; Count[i] = index.Count - begin;
            }
            Index = index.ToArray(); Weight = weight.ToArray();
        }
        private static double Lanczos(double x)
        {
            if (x < 1e-9) return 1;
            double px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }
        public float Apply(float[] values, int offset, int stride, int i)
        {
            float sum = 0; int s = Start[i];
            for (int k = 0; k < Count[i]; k++) sum += values[offset + Index[s + k] * stride] * Weight[s + k];
            return sum;
        }
    }

    /// Resizes a plane read row by row (width first), keeping only target-width rows in memory.
    private static float[] Shrink(int sw, int sh, int dw, int dh, bool lanczos, Action<int, float[]> readRow, CancellationToken token)
    {
        var across = new Taps(sw, dw, lanczos); var down = new Taps(sh, dh, lanczos);
        var narrow = new float[dw * sh]; var row = new float[sw];
        for (int y = 0; y < sh; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            readRow(y, row);
            for (int x = 0; x < dw; x++) narrow[y * dw + x] = across.Apply(row, 0, 1, x);
        }
        var output = new float[dw * dh];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++) output[y * dw + x] = down.Apply(narrow, x, dw, y);
        return output;
    }

    /// Resizes a small plane to the target, handing over one finished row at a time.
    private static void Expand(float[] plane, int sw, int sh, int dw, int dh, bool lanczos, Action<int, float[]> writeRow, CancellationToken token)
    {
        var across = new Taps(sw, dw, lanczos); var down = new Taps(sh, dh, lanczos);
        var column = new float[sw]; var row = new float[dw];
        for (int y = 0; y < dh; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < sw; x++) column[x] = Mix(plane, x, sw, down, y);
            for (int x = 0; x < dw; x++) row[x] = Mix(column, 0, 1, across, x);
            writeRow(y, row);
        }
    }

    // NaN (no background nearby) must not spread into neighbours that have a value.
    private static float Mix(float[] values, int offset, int stride, Taps taps, int i)
    {
        float sum = 0, used = 0; int s = taps.Start[i];
        for (int k = 0; k < taps.Count[i]; k++)
        {
            float v = values[offset + taps.Index[s + k] * stride];
            if (float.IsNaN(v)) continue;
            sum += v * taps.Weight[s + k]; used += taps.Weight[s + k];
        }
        return used > 0 ? sum / used : float.NaN;
    }

    private static void BoxBlur(float[] plane, int w, int h, int radius, CancellationToken token)
    {
        var line = new float[Math.Max(w, h)];
        for (int y = 0; y < h; y++) { if ((y & 63) == 0) token.ThrowIfCancellationRequested(); Blur1D(plane, y * w, 1, w, radius, line); }
        for (int x = 0; x < w; x++) Blur1D(plane, x, w, h, radius, line);
    }

    private static void Blur1D(float[] plane, int offset, int stride, int length, int radius, float[] line)
    {
        for (int i = 0; i < length; i++) line[i] = plane[offset + i * stride];
        double sum = 0; int count = 0;
        for (int i = 0; i <= Math.Min(radius, length - 1); i++) { sum += line[i]; count++; }
        for (int i = 0; i < length; i++)
        {
            plane[offset + i * stride] = (float)(sum / count);
            int add = i + radius + 1, remove = i - radius;
            if (add < length) { sum += line[add]; count++; }
            if (remove >= 0) { sum -= line[remove]; count--; }
        }
    }
}
