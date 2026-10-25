using System;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

public sealed record ImageRecipe(string Format, int LongEdge = 0, int Quality = 85, PhotoSettings? Adjustments = null, bool AutoPerImage = false)
{
    public static readonly string[] Formats = { "png", "jpg", "bmp", "tiff", "gif" };
    public const string GifNotice = "GIF palette and transparency limits apply after resizing, Auto and photo adjustments. These steps can add colors or partial transparency, so a recipe may be refused for some images. No fallback format or automatic color reduction is used. The quality field affects JPEG only.";
    public const string GifDowngrade = "Older Windows versions may reject GIF presets and fail to load all settings, then use defaults. Saving in an older version may replace prior settings. Keep pre-upgrade settings and preset backups before downgrading; a backup containing GIF presets also needs this version or later.";
    public void Validate()
    {
        if (!Formats.Contains(Format)) throw new UserError("Choose PNG, JPEG, BMP, TIFF or static GIF.");
        if (LongEdge != 0 && (LongEdge is < 16 or > 8192)) throw new UserError("Choose a long edge from 16 to 8192 pixels.");
        if (Quality is < 1 or > 100) throw new UserError("Choose JPEG quality from 1 to 100.");
        Adjustments?.Validate();
    }
    public PhotoSettings ResolvePhoto(PixelImage input)
    {
        Validate(); var settings = Adjustments ?? new PhotoSettings();
        if (!AutoPerImage) return settings;
        var automatic = PhotoAdjust.Auto(input);
        return settings with { Exposure = automatic.Exposure, Highlights = automatic.Highlights, Shadows = automatic.Shadows, Vibrance = automatic.Vibrance };
    }
}

public sealed record ImagePreset(string Id, string Name, ImageRecipe Recipe)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out var guid) || guid.ToString("N") != Id) throw new FormatException("Invalid preset identifier.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 48 || Name != Name.Trim() || Name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new UserError("Use a preset name of 1 to 48 characters without control characters.");
        if (Recipe is null) throw new FormatException("Invalid preset recipe.");
        Recipe.Validate();
    }
}

// Coordinates are pixels in the upright image, after EXIF orientation has been applied.
public readonly record struct PixelCrop(int X, int Y, int Width, int Height)
{
    public void Validate(int imageWidth, int imageHeight)
    {
        ImageRules.Validate(imageWidth, imageHeight);
        if (X < 0 || Y < 0 || Width < 1 || Height < 1 || (long)X + Width > imageWidth || (long)Y + Height > imageHeight)
            throw new UserError("The crop must stay inside the image and be at least one pixel wide and high.");
    }
    public static PixelCrop Center(int width, int height, double ratio)
    {
        ImageRules.Validate(width, height);
        if (!double.IsFinite(ratio) || ratio <= 0) throw new ArgumentOutOfRangeException(nameof(ratio));
        int w = Math.Clamp((int)Math.Min(width, height * ratio), 1, width);
        int h = Math.Clamp((int)Math.Min(height, w / ratio), 1, height);
        return new((width - w) / 2, (height - h) / 2, w, h);
    }
    public static PixelCrop Drag(int width, int height, double x1, double y1, double x2, double y2, double ratio = 0)
    {
        ImageRules.Validate(width, height);
        if (new[] { x1, y1, x2, y2, ratio }.Any(v => !double.IsFinite(v)) || ratio < 0) throw new ArgumentException("Invalid crop coordinates.");
        int a = Math.Clamp((int)Math.Clamp(x1, 0, width - 1), 0, width - 1), b = Math.Clamp((int)Math.Clamp(y1, 0, height - 1), 0, height - 1);
        int right = (int)Math.Clamp(x2, 0, width), bottom = (int)Math.Clamp(y2, 0, height);
        int w = Math.Max(1, Math.Abs(right - a)), h = Math.Max(1, Math.Abs(bottom - b));
        if (ratio > 0)
        {
            if (w / (double)h > ratio) w = Math.Max(1, (int)(h * ratio));
            else h = Math.Max(1, (int)(w / ratio));
        }
        int x = right < a ? a - w : a, y = bottom < b ? b - h : b;
        // A click at the top/left edge still selects one pixel inside the image.
        return new(Math.Max(0, x), Math.Max(0, y), w, h);
    }
}
