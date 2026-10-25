using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public static class ImageFrameTools
{
    public static BitmapSource Render(BitmapSource source, ImageFrame settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var layout = settings.Layout(source.PixelWidth, source.PixelHeight);
        var rect = new Rect(layout.X, layout.Y, layout.Width, layout.Height);
        Brush background = settings.Style switch {
            FrameStyle.White => Brushes.White, FrameStyle.Black => Brushes.Black,
            FrameStyle.Sky => new LinearGradientBrush(Color.FromRgb(215, 237, 249), Color.FromRgb(122, 168, 206), 60),
            FrameStyle.Rose => new LinearGradientBrush(Color.FromRgb(250, 226, 220), Color.FromRgb(208, 151, 169), 60),
            _ => new SolidColorBrush(Color.FromRgb(233, 234, 236))
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, settings.Width, settings.Height));
            if (settings.Shadow)
                for (int i = 16; i >= 1; i--)
                {
                    var shadow = new Rect(rect.X - i, rect.Y - i + 8, rect.Width + 2 * i, rect.Height + 2 * i);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(6, 0, 0, 0)), null, shadow, layout.Radius + i, layout.Radius + i);
                }
            dc.PushClip(new RectangleGeometry(rect, layout.Radius, layout.Radius));
            dc.DrawImage(source, rect); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(settings.Width, settings.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); token.ThrowIfCancellationRequested(); bitmap.Freeze(); return bitmap;
    }
    public static string Save(ImageTools.Document document, BitmapSource bitmap, CancellationToken token)
    {
        ImageRules.Validate(bitmap.PixelWidth, bitmap.PixelHeight);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return NativeFiles.WriteBeside(document.Input, Path.GetFileNameWithoutExtension(document.Input.Path) + " background", "png", encoder.Save, token);
    }
}
