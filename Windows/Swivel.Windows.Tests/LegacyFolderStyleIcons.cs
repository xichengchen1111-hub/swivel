// Frozen pre-B09-extension renderer, kept only for byte-for-byte compatibility checks.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

internal static class LegacyFolderStyleIcons
{
    public static byte[] Icon(FolderStyle style)
        => FolderStyles.Icon(FolderStyles.IconSizes.Select(size => (size, Png(style, size))).ToArray());

    public static BitmapSource Bitmap(FolderStyle style, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) Draw(context, style, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(FolderStyle style, int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(style, size)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static Color Shade(uint rgb, double factor)
    {
        byte Channel(int shift) => (byte)Math.Clamp(Math.Round(((rgb >> shift) & 0xFF) * factor), 0, 255);
        return Color.FromRgb(Channel(16), Channel(8), Channel(0));
    }

    private static void Draw(DrawingContext context, FolderStyle style, double s)
    {
        uint rgb = FolderStyles.Colors[style.Color].Rgb;
        double corner = Math.Max(1, s * 0.06);
        // Back panel with its tab, then the front panel slightly lower and lighter.
        var back = new StreamGeometry();
        using (var g = back.Open())
        {
            g.BeginFigure(new Point(s * 0.06, s * 0.20), true, true);
            g.LineTo(new Point(s * 0.40, s * 0.20), true, false);
            g.LineTo(new Point(s * 0.48, s * 0.28), true, false);
            g.LineTo(new Point(s * 0.94, s * 0.28), true, false);
            g.LineTo(new Point(s * 0.94, s * 0.84), true, false);
            g.LineTo(new Point(s * 0.06, s * 0.84), true, false);
        }
        back.Freeze();
        context.DrawGeometry(new SolidColorBrush(Shade(rgb, 0.78)), null, back);
        var front = new RectangleGeometry(new Rect(s * 0.06, s * 0.36, s * 0.88, s * 0.48), corner, corner);
        var fill = new LinearGradientBrush(Shade(rgb, 1.12), Shade(rgb, 0.96), 90);
        context.DrawGeometry(fill, null, front);
        if (style.Badge.Length == 0 || s < 24) return;
        var text = new FormattedText(style.Badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Emoji, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            s * 0.26, Brushes.White, 1.0);
        double scale = Math.Min(1, s * 0.76 / Math.Max(1, text.Width));
        context.PushTransform(new ScaleTransform(scale, scale, s / 2, s * 0.60));
        context.DrawText(text, new Point((s - text.Width) / 2, s * 0.60 - text.Height / 2));
        context.Pop();
    }
}

