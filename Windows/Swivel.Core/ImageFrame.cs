using System;

namespace Swivel.Core;

public enum FrameStyle { White, Black, Gray, Sky, Rose }
public sealed record ImageFrame(int Width = 2400, int Height = 1800, int Padding = 100, int Radius = 24, bool Shadow = true, FrameStyle Style = FrameStyle.Gray)
{
    public (double X, double Y, double Width, double Height, double Radius) Layout(int sourceWidth, int sourceHeight)
    {
        ImageRules.Validate(sourceWidth, sourceHeight);
        if (Width is < 64 or > 4000 || Height is < 64 or > 4000 || (long)Width * Height > ImageRules.MaxPixels || Padding is < 0 or > 500 || Width - 2 * Padding < 16 || Height - 2 * Padding < 16 || Radius is < 0 or > 500 || !Enum.IsDefined(Style))
            throw new UserError("Choose a 64–4000 pixel canvas, padding and corner radius from 0–500, leaving at least 16 pixels for the image.");
        double scale = Math.Min(1, Math.Min((Width - 2.0 * Padding) / sourceWidth, (Height - 2.0 * Padding) / sourceHeight));
        double w = sourceWidth * scale, h = sourceHeight * scale;
        return ((Width - w) / 2, (Height - h) / 2, w, h, Math.Min(Radius, Math.Min(w, h) / 2));
    }
}
