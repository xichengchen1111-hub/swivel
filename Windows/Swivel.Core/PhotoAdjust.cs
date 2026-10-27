using System;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum PhotoLook { Original, Vivid, Soft, Warm, Cool, Mono, Film, Fade }
public sealed record PhotoSettings(double Exposure = 0, double Contrast = 0, double Highlights = 0, double Shadows = 0,
    double Whites = 0, double Blacks = 0, double Saturation = 0, double Vibrance = 0, double Warmth = 0, double Tint = 0,
    double Clarity = 0, double Sharpen = 0, double Noise = 0, double Vignette = 0, PhotoLook Look = PhotoLook.Original, double LookStrength = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(Exposure) || Exposure is < -3 or > 3 ||
            new[] { Contrast, Highlights, Shadows, Whites, Blacks, Saturation, Vibrance, Warmth, Tint, Clarity }.Any(x => !double.IsFinite(x) || x is < -1 or > 1) ||
            new[] { Sharpen, Noise, Vignette, LookStrength }.Any(x => !double.IsFinite(x) || x is < 0 or > 1) || !Enum.IsDefined(Look))
            throw new UserError("Photo adjustment values are outside their supported range.");
    }
    public PhotoSettings Effective()
    {
        Validate(); double amount = LookStrength;
        double contrast = 0, saturation = 0, warmth = 0, blacks = 0, highlights = 0;
        switch (Look)
        {
            case PhotoLook.Vivid: contrast = .2; saturation = .2; break;
            case PhotoLook.Soft: contrast = -.2; highlights = -.15; break;
            case PhotoLook.Warm: warmth = .35; break;
            case PhotoLook.Cool: warmth = -.35; break;
            case PhotoLook.Mono: saturation = -1; break;
            case PhotoLook.Film: contrast = .12; saturation = -.2; warmth = .12; blacks = .1; break;
            case PhotoLook.Fade: contrast = -.15; blacks = .3; saturation = -.1; break;
        }
        return this with { Contrast = Math.Clamp(Contrast + contrast * amount, -1, 1), Saturation = Math.Clamp(Saturation + saturation * amount, -1, 1), Warmth = Math.Clamp(Warmth + warmth * amount, -1, 1), Blacks = Math.Clamp(Blacks + blacks * amount, -1, 1), Highlights = Math.Clamp(Highlights + highlights * amount, -1, 1), Look = PhotoLook.Original, LookStrength = 1 };
    }
}
public sealed record PixelImage(int Width, int Height, byte[] Bgra)
{
    public void Validate()
    {
        ImageRules.Validate(Width, Height);
        if (Bgra is null || Bgra.Length != (long)Width * Height * 4) throw new ArgumentException("Invalid pixel buffer.");
    }
}
public static class PhotoAdjust
{
    public static PixelImage Apply(PixelImage input, PhotoSettings settings, CancellationToken token)
    {
        input.Validate(); var p = settings.Effective(); token.ThrowIfCancellationRequested();
        byte[] output = (byte[])input.Bgra.Clone();
        if (p == new PhotoSettings()) return new(input.Width, input.Height, output);
        double gain = Math.Pow(2, p.Exposure), redGain = Math.Pow(2, p.Warmth * .35), blueGain = 1 / redGain, greenGain = Math.Pow(2, -p.Tint * .25);
        for (int y = 0; y < input.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < input.Width; x++)
            {
                int i = (y * input.Width + x) * 4;
                if (input.Bgra[i + 3] == 0) continue; // Preserve hidden RGB as well as alpha.
                double b = input.Bgra[i] / 255d, g = input.Bgra[i + 1] / 255d, r = input.Bgra[i + 2] / 255d;
                if (p.Noise > 0 || p.Sharpen > 0 || p.Clarity != 0)
                {
                    double sb = 0, sg = 0, sr = 0, weight = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int at = (Math.Clamp(y + dy, 0, input.Height - 1) * input.Width + Math.Clamp(x + dx, 0, input.Width - 1)) * 4;
                        double alpha = input.Bgra[at + 3] / 255d;
                        sb += input.Bgra[at] / 255d * alpha; sg += input.Bgra[at + 1] / 255d * alpha; sr += input.Bgra[at + 2] / 255d * alpha; weight += alpha;
                    }
                    if (weight > 0)
                    {
                        sb /= weight; sg /= weight; sr /= weight;
                        double detail = p.Sharpen * 1.3 + p.Clarity * .65 - p.Noise * .8;
                        b = Math.Clamp(b + (b - sb) * detail, 0, 1); g = Math.Clamp(g + (g - sg) * detail, 0, 1); r = Math.Clamp(r + (r - sr) * detail, 0, 1);
                    }
                }
                // sRGB transfer functions keep exposure in linear light. Tonal/color controls
                // operate on display-referred SDR; these are original Windows algorithms.
                r = FromLinear(ToLinear(r) * gain * redGain); g = FromLinear(ToLinear(g) * gain * greenGain); b = FromLinear(ToLinear(b) * gain * blueGain);
                double luma = .2126 * r + .7152 * g + .0722 * b;
                double shadow = 1 - Math.Clamp(luma, 0, 1), highlight = Math.Clamp(luma, 0, 1);
                double lift = p.Shadows * shadow * shadow * .3 + p.Highlights * highlight * highlight * .3 + p.Blacks * Math.Pow(shadow, 4) * .2 + p.Whites * Math.Pow(highlight, 4) * .2;
                r = (r - .5) * (1 + p.Contrast * .8) + .5 + lift;
                g = (g - .5) * (1 + p.Contrast * .8) + .5 + lift;
                b = (b - .5) * (1 + p.Contrast * .8) + .5 + lift;
                luma = .2126 * r + .7152 * g + .0722 * b;
                double chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                double saturation = Math.Max(0, 1 + p.Saturation + p.Vibrance * (1 - Math.Clamp(chroma, 0, 1)) * .6);
                r = luma + (r - luma) * saturation; g = luma + (g - luma) * saturation; b = luma + (b - luma) * saturation;
                double nx = (x + .5) / input.Width * 2 - 1, ny = (y + .5) / input.Height * 2 - 1;
                double vignette = 1 - p.Vignette * .75 * Math.Pow(Math.Clamp((nx * nx + ny * ny) / 2, 0, 1), 1.5);
                output[i] = Byte(b * vignette); output[i + 1] = Byte(g * vignette); output[i + 2] = Byte(r * vignette);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(input.Width, input.Height, output);
    }
    public static PhotoSettings Auto(PixelImage input)
    {
        input.Validate(); double total = 0; int samples = 0, bright = 0;
        int step = Math.Max(1, input.Width * input.Height / 16384);
        for (int pixel = 0; pixel < input.Width * input.Height; pixel += step)
        {
            int i = pixel * 4; if (input.Bgra[i + 3] < 128) continue;
            double y = (.2126 * input.Bgra[i + 2] + .7152 * input.Bgra[i + 1] + .0722 * input.Bgra[i]) / 255;
            total += y; samples++; if (y > .92) bright++;
        }
        if (samples == 0) return new();
        double average = total / samples;
        return new(Exposure: Math.Clamp(Math.Log2(.45 / Math.Max(.05, average)), -1, 1), Highlights: bright > samples / 50 ? -.25 : 0, Shadows: average < .3 ? .12 : 0, Vibrance: .1);
    }
    private static double ToLinear(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
    private static double FromLinear(double v) => v <= .0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - .055;
    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
