using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

/// A face box from the system face detector, in source pixels (top-left origin).
public readonly record struct IdFace(double X, double Y, double Width, double Height);

public sealed record IdPhotoSettings(int Width = 295, int Height = 413, int Dpi = 300, uint Background = 0xFFFFFF, double Zoom = 1, double Vertical = 0);

/// A source-pixel crop rectangle; parts outside the photo are filled with the background.
public readonly record struct IdCrop(double X, double Y, double Width, double Height);

/// ID photos like the Mac's: preset sizes, head at about 70% of the height, a plain
/// background. Windows' face detector reports only a face box, so the top of the head comes
/// from the subject mask and the chin from the box; the preview and Zoom/Vertical controls
/// let the user correct both.
public static class IdPhoto
{
    public static readonly string[] PresetNames = { "One inch (295 × 413)", "Two inch (413 × 579)", "Passport (33 × 48 mm)", "US Visa (600 × 600)", "Custom" };
    public static readonly (string Name, uint Rgb)[] Backgrounds = { ("White", 0xFFFFFF), ("Blue", 0x438EDB), ("Red", 0xD9001B) };
    public const int MaxSide = 4000;
    public const long MaxPixels = 16_000_000;
    public const double HeadShare = 0.7, TopMargin = 0.13;
    private const string SizeRule = "Choose a DPI from 72 to 1200 and a size from 1 to 4000 pixels per side.";

    public static (int Width, int Height) Dimensions(int preset, double width, double height, bool millimeters, int dpi)
    {
        if (dpi is < 72 or > 1200 || !double.IsFinite(width) || !double.IsFinite(height)) throw new UserError(SizeRule);
        (double w, double h) = preset switch
        {
            0 => (295d, 413d), 1 => (413d, 579d),
            2 => (33 / 25.4 * dpi, 48 / 25.4 * dpi),
            3 => (600d, 600d),
            4 => (width * (millimeters ? dpi / 25.4 : 1), height * (millimeters ? dpi / 25.4 : 1)),
            _ => throw new UserError(SizeRule),
        };
        int rw = (int)Math.Round(w), rh = (int)Math.Round(h);
        if (!(w >= 1 && h >= 1) || rw > MaxSide || rh > MaxSide || (long)rw * rh > MaxPixels) throw new UserError(SizeRule);
        return (rw, rh);
    }

    public static IdFace SingleFace(IReadOnlyList<IdFace> faces)
    {
        if (faces.Count == 0) throw new UserError("No face found. Choose a clear portrait facing the camera.");
        if (faces.Count > 1) throw new UserError("More than one face found. Choose a photo with just one person.");
        var face = faces[0];
        if (!(face.Width >= 4 && face.Height >= 4) || !double.IsFinite(face.X) || !double.IsFinite(face.Y)) throw new UserError("No face found. Choose a clear portrait facing the camera.");
        return face;
    }

    /// The top of the head: walking up from the face box through rows where the subject mask
    /// covers the middle of the face's width (hair counts). Without a usable mask, an estimate
    /// from the box.
    public static double HeadTop(byte[] mask, int width, int height, IdFace face)
    {
        ImageRules.Validate(width, height);
        if (mask is null || mask.Length != (long)width * height) throw new ArgumentException("Invalid mask.");
        double estimate = face.Y - 0.4 * face.Height;
        int left = Math.Clamp((int)Math.Round(face.X + face.Width * 0.3), 0, width - 1);
        int right = Math.Clamp((int)Math.Round(face.X + face.Width * 0.7), left, width - 1);
        int start = Math.Clamp((int)Math.Round(face.Y + face.Height * 0.1), 0, height - 1);
        int limit = Math.Max(0, (int)Math.Floor(face.Y - 1.5 * face.Height));
        bool Covered(int y)
        {
            int solid = 0;
            for (int x = left; x <= right; x++) if (mask[(long)y * width + x] >= 128) solid++;
            return solid * 10 >= (right - left + 1) * 3;
        }
        if (!Covered(start)) return estimate;
        int top = start;
        while (top - 1 >= limit && Covered(top - 1)) top--;
        // A mask that runs to the photo's top edge or the search limit says nothing about the head.
        if (top == 0) return Math.Min(estimate, 0);
        return top <= limit ? estimate : top;
    }

    /// The source rectangle that becomes the output: head (top to chin) at 70% of the height,
    /// its top 13% down, centred on the face. Zoom 0.5–2 enlarges the head around its centre;
    /// Vertical −0.3–0.3 (share of the height) moves the person up (positive) or down.
    public static IdCrop Crop(int outWidth, int outHeight, IdFace face, double headTop, double zoom, double vertical)
    {
        double chin = face.Y + face.Height;
        if (!double.IsFinite(headTop) || !double.IsFinite(zoom) || !double.IsFinite(vertical) || chin - headTop < 4 || outWidth < 1 || outHeight < 1) throw new UserError("No face found. Choose a clear portrait facing the camera.");
        double scale = outHeight * HeadShare / (chin - headTop) * Math.Clamp(zoom, 0.5, 2);
        double w = outWidth / scale, h = outHeight / scale;
        double x = face.X + face.Width / 2 - w / 2;
        double y = (headTop + chin) / 2 - h * (TopMargin + HeadShare / 2 - Math.Clamp(vertical, -0.3, 0.3));
        return new IdCrop(x, y, w, h);
    }

    /// The source with its background estimated once, ready for any crop and colour.
    public sealed class Prepared
    {
        internal PixelImage Image { get; init; } = null!;
        internal byte[] Alpha { get; init; } = null!;
        internal float[][] Behind { get; init; } = null!;
        internal int SmallWidth { get; init; }
        internal int SmallHeight { get; init; }
        public int Width => Image.Width;
        public int Height => Image.Height;
    }

    public static Prepared Prepare(PixelImage image, byte[] mask, CancellationToken token)
    {
        image.Validate(); Cutout.CheckMask(image, mask);
        var alpha = new byte[mask.Length];
        for (int p = 0; p < mask.Length; p++)
        {
            int m = mask[p] < Cutout.Floor ? 0 : mask[p];
            alpha[p] = (byte)((m * image.Bgra[p * 4 + 3] + 127) / 255);
        }
        var (planes, sw, sh) = Cutout.BackgroundPlanes(image, alpha, 0.02, token);
        return new Prepared { Image = image, Alpha = alpha, Behind = planes, SmallWidth = sw, SmallHeight = sh };
    }

    /// The finished, opaque photo. Each pixel's old background is swapped for the new colour
    /// (pixel + (1 − α)(new − old)), which keeps hair edges free of the old colour without
    /// dividing by small alpha values; the subject's own pixels stay exact.
    public static PixelImage Render(Prepared prepared, IdCrop crop, int outWidth, int outHeight, uint background, CancellationToken token)
    {
        if (outWidth is < 1 or > MaxSide || outHeight is < 1 or > MaxSide || (long)outWidth * outHeight > MaxPixels || background > 0xFFFFFF) throw new UserError(SizeRule);
        if (!(crop.Width > 0 && crop.Height > 0) || !double.IsFinite(crop.X) || !double.IsFinite(crop.Y)) throw new ArgumentException("Invalid crop.");
        var image = prepared.Image; int w = image.Width, h = image.Height;
        float[] bg = { background & 0xFF, (background >> 8) & 0xFF, (background >> 16) & 0xFF }; // B, G, R
        var across = Axis(crop.X, crop.Width, outWidth, w);
        var down = Axis(crop.Y, crop.Height, outHeight, h);
        // Rows are filtered across once each and kept only while some output row still needs them.
        var rows = new Dictionary<int, float[]>();
        var recoloured = new float[w * 3];
        var (lo, hi) = across.Range();
        float[] Row(int y)
        {
            if (rows.TryGetValue(y, out var cached)) return cached;
            for (int x = lo; x <= hi; x++)
            {
                int p = y * w + x;
                float a = prepared.Alpha[p] / 255f;
                for (int c = 0; c < 3; c++)
                {
                    float v = image.Bgra[p * 4 + c];
                    if (a < 1)
                    {
                        float old = Cutout.Sample(prepared.Behind[c], prepared.SmallWidth, prepared.SmallHeight, w, h, x, y);
                        // Transparent source pixels have no trustworthy colour of their own.
                        v = image.Bgra[p * 4 + 3] == 0 ? bg[c] : float.IsNaN(old) ? v * a + bg[c] * (1 - a) : v + (1 - a) * (bg[c] - old);
                    }
                    recoloured[x * 3 + c] = v;
                }
            }
            var result = new float[outWidth * 3];
            for (int ox = 0; ox < outWidth; ox++)
                for (int k = across.Start[ox]; k < across.Start[ox] + across.Count[ox]; k++)
                {
                    int j = across.Index[k]; float weight = across.Weight[k];
                    for (int c = 0; c < 3; c++) result[ox * 3 + c] += (j < 0 ? bg[c] : recoloured[j * 3 + c]) * weight;
                }
            rows[y] = result;
            return result;
        }
        var output = new byte[(long)outWidth * outHeight * 4];
        var line = new float[outWidth * 3];
        for (int oy = 0; oy < outHeight; oy++)
        {
            if ((oy & 15) == 0) token.ThrowIfCancellationRequested();
            int first = down.FirstInside(oy);
            foreach (int stale in new List<int>(rows.Keys)) if (stale < first) rows.Remove(stale);
            Array.Clear(line);
            for (int k = down.Start[oy]; k < down.Start[oy] + down.Count[oy]; k++)
            {
                int y = down.Index[k]; float weight = down.Weight[k];
                var source = y < 0 ? null : Row(y);
                for (int column = 0; column < line.Length; column++) line[column] += (source is null ? bg[column % 3] : source[column]) * weight;
            }
            for (int ox = 0; ox < outWidth; ox++)
            {
                long i = ((long)oy * outWidth + ox) * 4;
                for (int c = 0; c < 3; c++) output[i + c] = (byte)Math.Round(Math.Clamp(line[ox * 3 + c], 0f, 255f));
                output[i + 3] = 255;
            }
        }
        return new(outWidth, outHeight, output);
    }

    /// Triangle-filter taps from output pixels into a source span that may reach past the
    /// photo; taps outside it take the background colour.
    private sealed class Span
    {
        // Output pixel i reads taps Start[i] .. Start[i] + Count[i]; Index −1 is outside the photo.
        public readonly int[] Start, Count, Index; public readonly float[] Weight;
        private readonly int[] start, index;
        public Span(int[] start, int[] count, int[] index, float[] weight) { Start = this.start = start; Count = count; Index = this.index = index; Weight = weight; }
        /// The source columns any output pixel reads (empty when the crop misses the photo).
        public (int Low, int High) Range()
        {
            int low = int.MaxValue, high = -1;
            foreach (int j in index) if (j >= 0) { low = Math.Min(low, j); high = Math.Max(high, j); }
            return (low, high);
        }
        public int FirstInside(int i)
        {
            for (int k = start[i]; k < start[i] + Count[i]; k++) if (index[k] >= 0) return index[k];
            return int.MaxValue;
        }
    }

    private static Span Axis(double origin, double length, int target, int source)
    {
        double scale = length / target, support = Math.Max(1, scale);
        var start = new int[target]; var count = new int[target];
        var index = new List<int>(); var weight = new List<float>();
        for (int i = 0; i < target; i++)
        {
            double center = origin + (i + 0.5) * scale - 0.5;
            int first = (int)Math.Floor(center - support) + 1, last = (int)Math.Floor(center + support);
            int begin = index.Count; double total = 0;
            for (int j = first; j <= last; j++)
            {
                double value = 1 - Math.Abs(j - center) / support;
                if (value <= 0) continue;
                index.Add(j >= 0 && j < source ? j : -1); weight.Add((float)value); total += value;
            }
            if (index.Count == begin) { index.Add(-1); weight.Add(1); total = 1; }
            for (int k = begin; k < index.Count; k++) weight[k] = (float)(weight[k] / total);
            start[i] = begin; count[i] = index.Count - begin;
        }
        return new Span(start, count, index.ToArray(), weight.ToArray());
    }
}
