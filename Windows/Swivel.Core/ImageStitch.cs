using System;
using System.Threading;

namespace Swivel.Core;

/// Clothesline stitching (as Mac 1.13.0): two whole images, the target above the dragged one,
/// matched to the narrower width (never enlarged, cropped or distorted) and saved as a new PNG
/// beside the upper image. No overlap matching.
public static class ImageStitch
{
    // The result must open in every other image tool, so it keeps their 16 MP / 16,384 limit
    // (Mac allows 40 MP, 32,768 tall).
    public const int EdgeLimit = 16_384;
    public const long PixelLimit = ImageRules.MaxPixels;
    public const string Suffix = " stitched";
    public const string ChooseTwo = "Choose two different hung images to stitch.";
    public const string TooLarge = "These images are too large to stitch: the result may have at most 16 megapixels and 16384 pixels per side.";

    /// Output width and each image's height at that width.
    public static (int Width, int Top, int Bottom) Size(int topWidth, int topHeight, int bottomWidth, int bottomHeight)
    {
        foreach (int side in new[] { topWidth, topHeight, bottomWidth, bottomHeight })
            if (side is < 1 or > EdgeLimit) throw new UserError(TooLarge);
        int width = Math.Min(topWidth, bottomWidth);
        int Height(int w, int h) => (int)Math.Max(1, Math.Round((double)h * width / w, MidpointRounding.AwayFromZero));
        int top = Height(topWidth, topHeight), bottom = Height(bottomWidth, bottomHeight);
        if ((long)top + bottom > EdgeLimit || (long)width * (top + bottom) > PixelLimit) throw new UserError(TooLarge);
        return (width, top, bottom);
    }

    /// The upper image's rows, then the lower image's (both already at the same width).
    public static PixelImage Stack(PixelImage top, PixelImage bottom, CancellationToken token = default)
    {
        if (top.Width != bottom.Width || top.Bgra.Length != top.Width * top.Height * 4 || bottom.Bgra.Length != bottom.Width * bottom.Height * 4)
            throw new ArgumentException("Stitched images need the same width.");
        token.ThrowIfCancellationRequested();
        var joined = new byte[checked(top.Bgra.Length + bottom.Bgra.Length)];
        Buffer.BlockCopy(top.Bgra, 0, joined, 0, top.Bgra.Length);
        Buffer.BlockCopy(bottom.Bgra, 0, joined, top.Bgra.Length, bottom.Bgra.Length);
        return new PixelImage(top.Width, top.Height + bottom.Height, joined);
    }
}

public enum StitchHoverState { None, Holding, Ready }

/// A deliberate stitch rather than passing over a neighbour: the dragged print must rest over
/// the lower half of another print (the place it will go) for half a second. Moving off that
/// half, onto another print or away starts over.
public sealed class StitchHover
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(0.5);
    private TimeSpan since;
    public string? Target { get; private set; }

    public static bool InZone(double y, double height) => height > 0 && y >= height * 0.5 && y <= height;

    public StitchHoverState Update(string source, string target, double y, double height, TimeSpan now)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !InZone(y, height)) { Reset(); return StitchHoverState.None; }
        if (!string.Equals(Target, target, StringComparison.OrdinalIgnoreCase)) { Target = target; since = now; }
        return now - since >= Delay ? StitchHoverState.Ready : StitchHoverState.Holding;
    }

    public bool IsReady(TimeSpan now) => Target is not null && now - since >= Delay;

    public void Reset() => Target = null;
}
