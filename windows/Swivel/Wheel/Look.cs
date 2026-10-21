using System.Windows;
using System.Windows.Media;

namespace Swivel;

/// Fonts and system colours shared by the menu, its previews and the toast.
static class Look
{
    /// The Windows 11 UI font, falling back to Segoe UI on Windows 10.
    public static readonly FontFamily UI = new("Segoe UI Variable Text, Segoe UI");

    /// Segoe Fluent Icons (Windows 11), falling back to Segoe MDL2 Assets (Windows 10).
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// The system accent colour (Settings › Personalisation › Colours).
    public static Color Accent
    {
        get
        {
            try { return SystemColors.AccentColor; } catch (Exception) { return Color.FromRgb(0x00, 0x67, 0xC0); }
        }
    }

    /// The accent as Windows 11 uses it for text and icons: darker on light backgrounds, lighter on dark ones.
    public static Color AccentText(bool dark)
    {
        try { return dark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1; }
        catch (Exception) { return Accent; }
    }

    public static Color WithAlpha(this Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    /// The colour's own alpha scaled by `alpha`.
    public static Color Faded(this Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B);

    public static Color Mix(this Color a, Color b, double t) => Swivel.Mix.Lerp(a, b, t);

    public static Color Gray(double white, double alpha = 1)
    {
        byte v = (byte)Math.Round(Math.Clamp(white, 0, 1) * 255);
        return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), v, v, v);
    }

    public static Color Rgb(double r, double g, double b, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    public static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
