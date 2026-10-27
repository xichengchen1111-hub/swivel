using System;

namespace Swivel.Core;

public enum ResizeBasis { LongEdge, Width, Height, Percent }
public sealed record ResizeOptions(ResizeBasis Mode = ResizeBasis.LongEdge, double Value = 1920, bool Enlarge = false)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(Value) || (Mode == ResizeBasis.Percent ? Value is < .1 or > 400 : Value is < 16 or > 8192 || Value != Math.Truncate(Value)))
            throw new UserError("Choose 16–8192 whole pixels, or 0.1–400 percent.");
    }
    public (int Width, int Height) Size(int width, int height)
    {
        Validate(); ImageRules.Validate(width, height);
        double scale = Mode switch { ResizeBasis.Width => Value / width, ResizeBasis.Height => Value / height, ResizeBasis.Percent => Value / 100, _ => Value / Math.Max(width, height) };
        if (!Enlarge) scale = Math.Min(1, scale);
        int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
        ImageRules.Validate(w, h); return (w, h);
    }
}

/// A guaranteed byte ceiling, without assuming JPEG size is perfectly monotonic.
/// Chooses the highest fitting quality among at most nine measured candidates.
public static class JpegBudget
{
    public static void Validate(long bytes)
    {
        if (bytes is < 4096 or > 64L * 1024 * 1024) throw new UserError("Choose a target size from 4 to 65536 KB.");
    }
    public static byte[] Encode(long bytes, Func<int, byte[]> encode, System.Threading.CancellationToken token)
    {
        Validate(bytes); token.ThrowIfCancellationRequested();
        byte[] Run(int quality) { token.ThrowIfCancellationRequested(); var data = encode(quality); token.ThrowIfCancellationRequested(); return data; }
        var best = Run(1);
        if (best.LongLength > bytes) throw new UserError("This image cannot meet the target at its current dimensions. Resize it first or choose a larger target.");
        var full = Run(100); if (full.LongLength <= bytes) return full;
        int low = 1, high = 100;
        while (high - low > 1)
        {
            int quality = (low + high) / 2; var candidate = Run(quality);
            if (candidate.LongLength <= bytes) { low = quality; best = candidate; } else high = quality;
        }
        return best;
    }
}
