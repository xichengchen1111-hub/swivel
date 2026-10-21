using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Offscreen filmstrips of the rope and the screenshot card, for reviewing the motion without touching the
/// desktop (the Mac's `SWIVEL_ROPE_FRAMES`). Each strip stacks labelled frames from top to bottom. Runs on any STA
/// thread; nothing is shown on screen.
static class ClotheslineFrames
{
    /// Renders rope-1-drop.png, rope-2-hover-press-copy.png, rope-3-arrive.png, rope-4-lift.png, rope-5-discard.png,
    /// rope-chip.png and card.png into `dir`. Returns 0.
    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var fixtures = Fixtures(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swivel-rope-frames"));
        RenderRope(dir, fixtures);
        RenderCard(dir, fixtures[0]);
        return 0;
    }

    /// Four pictures of different shapes, each a colour with a darker top band (as the Mac tests draw them).
    public static List<string> Fixtures(string folder)
    {
        Directory.CreateDirectory(folder);
        (int W, int H, double Hue)[] specs = [(1600, 1000, 0.58), (900, 1200, 0.08), (1200, 1200, 0.33), (1800, 900, 0.85)];
        var result = new List<string>();
        for (int n = 0; n < specs.Length; n++)
        {
            var path = System.IO.Path.Combine(folder, $"print-{n}.png");
            result.Add(path);
            if (File.Exists(path)) continue;
            var (w, h, hue) = specs[n];
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Hsb(hue, 0.5, 0.9)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new SolidColorBrush(Hsb(hue, 0.7, 0.55)), null, new Rect(0, 0, w, h / 3.0));
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            WritePng(bitmap, path);
        }
        return result;
    }

    static Color Hsb(double hue, double saturation, double brightness)
    {
        double h = hue * 6, c = brightness * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = brightness - c;
        var (r, g, b) = (int)Math.Floor(h) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        byte B(double v) => (byte)Math.Round(255 * Math.Clamp(v + m, 0, 1));
        return Color.FromRgb(B(r), B(g), B(b));
    }

    static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var temp = path + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }

    static readonly Color Desktop = Color.FromRgb(92, 133, 184);   // sRGB 0.36, 0.52, 0.72

    /// The element drawn over a plain desktop colour at 2×.
    static BitmapSource Snapshot(FrameworkElement element, Size size)
    {
        var host = new Border { Background = new SolidColorBrush(Desktop), Width = size.Width, Height = size.Height, Child = element };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;
        bitmap.Freeze();
        return bitmap;
    }

    static void Strip(List<(string Label, BitmapSource Image)> frames, string path)
    {
        if (frames.Count == 0) return;
        int w = frames[0].Image.PixelWidth, h = frames[0].Image.PixelHeight, label = 36;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, (h + label) * frames.Count));
            for (int i = 0; i < frames.Count; i++)
            {
                double y = (h + label) * i;
                var text = new FormattedText(frames[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, Brushes.Black, 1);
                dc.DrawText(text, new Point(8, y + 6));
                dc.DrawImage(frames[i].Image, new Rect(0, y + label, w, h));
            }
        }
        var bitmap = new RenderTargetBitmap(w, (h + label) * frames.Count, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        WritePng(bitmap, path);
    }

    static void RenderRope(string dir, List<string> fixtures)
    {
        var view = new RopeView(900) { ReducedMotionOverride = false, RendersOffscreen = true };
        view.SetItems(fixtures.Take(3).ToList());
        var size = new Size(900, RopeView.PanelHeight);
        var frames = new List<(string, BitmapSource)>();
        double t = 0;
        void Capture(string name, double every, double seconds)
        {
            double next = 0, elapsed = 0;
            while (elapsed <= seconds + 1e-6)
            {
                if (elapsed >= next - 1e-6)
                {
                    frames.Add(($"{name} t={t:F3}", Snapshot(view, size)));
                    next += every;
                }
                view.Advance(1.0 / 120);
                elapsed += 1.0 / 120;
                t += 1.0 / 120;
            }
        }
        view.Lower(fromHidden: true);
        Capture("drop", 0.05, 0.6);
        Strip(frames, System.IO.Path.Combine(dir, "rope-1-drop.png")); frames.Clear();

        var first = view.PrintPosition(1);
        view.SimulatedPointer = new Point(first.X, first.Y + view.PrintSizeAt(1).Height / 2);
        Capture("hover", 0.05, 0.3);
        view.PreviewPress(fixtures[1], true);
        Capture("press", 0.033, 0.15);
        view.PreviewPress(fixtures[1], false);
        Capture("release", 0.05, 0.3);
        view.ConfirmCopy(fixtures[1]);
        Capture("copied", 0.1, 0.4);
        Strip(frames, System.IO.Path.Combine(dir, "rope-2-hover-press-copy.png")); frames.Clear();

        // The chip on its own, as it looks at full opacity.
        var chipView = new RopeView(300) { ReducedMotionOverride = true };
        chipView.SetItems([fixtures[1]]);
        chipView.Lower(fromHidden: true);
        chipView.Advance(1.0 / 60);
        chipView.ConfirmCopy(fixtures[1]);
        for (int i = 0; i < 40; i++) chipView.Advance(1.0 / 120);
        WritePng(Snapshot(chipView, new Size(300, RopeView.PanelHeight)), System.IO.Path.Combine(dir, "rope-chip.png"));

        view.SimulatedPointer = null;
        view.MarkNew(fixtures[3], RopeArrival.FromAbove);
        view.SetItems([fixtures[3], .. fixtures.Take(3)]);
        Capture("arrive", 0.06, 0.9);
        Strip(frames, System.IO.Path.Combine(dir, "rope-3-arrive.png")); frames.Clear();

        view.Remove(fixtures[0], falling: true);
        Capture("discard", 0.06, 0.5);
        Strip(frames, System.IO.Path.Combine(dir, "rope-5-discard.png")); frames.Clear();

        view.Lift(() => { });
        Capture("lift", 0.05, 0.35);
        Strip(frames, System.IO.Path.Combine(dir, "rope-4-lift.png"));
    }

    static void RenderCard(string dir, string fixture)
    {
        var view = new IncomingView { ReducedMotionOverride = false };
        view.SetThumbnail(RopeThumbnails.Image(fixture, 480));
        var preferred = view.PreferredSize;
        var size = new Size(preferred.Width + 60, preferred.Height);
        view.Width = preferred.Width;
        view.Height = preferred.Height;
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.LayoutCard();
        var frames = new List<(string, BitmapSource)>();
        void Capture(string name, double every, double seconds)
        {
            double next = 0, elapsed = 0;
            while (elapsed <= seconds + 1e-6)
            {
                if (elapsed >= next - 1e-6)
                {
                    frames.Add(($"{name} {elapsed:F2}", Snapshot(view, size)));
                    next += every;
                }
                view.Advance(1.0 / 120);
                elapsed += 1.0 / 120;
            }
        }
        view.Enter();
        Capture("enter", 0.05, 0.4);
        view.ResetInteraction(hovered: true);
        Capture("hover", 0.1, 0.3);
        view.PreviewPress(true);
        Capture("press", 0.05, 0.15);
        view.ResetInteraction(hovered: true);
        Capture("release", 0.1, 0.3);
        view.Exit(() => { });
        Capture("exit", 0.05, 0.25);
        Strip(frames, System.IO.Path.Combine(dir, "card.png"));
    }
}
