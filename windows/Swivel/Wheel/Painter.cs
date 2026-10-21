using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Swivel;

/// Draws in the Mac app's y-up view coordinates onto a stack of DrawingVisual layers, so that a block of drawing can
/// carry its own soft shadow (the AppKit code's `withShadow`) or text glow. WPF's DrawingContext has no shadows; a
/// layer is a DrawingVisual with a DropShadowEffect, which the GPU renders. Plain drawing that follows plain drawing
/// shares one layer, so a menu frame is a few dozen visuals at most.
sealed class Painter
{
    readonly Action<Visual> add;
    readonly List<DrawingVisual> pool = [];
    readonly List<DropShadowEffect> effects = [];
    readonly List<bool> drawn = [];
    int used;
    DrawingContext? dc;
    Kind kind;
    Color glowColor;
    bool exclusive;
    double height;
    double alpha = 1;
    Geometry? clip;
    readonly Stack<(double Alpha, Geometry? Clip)> saved = new();
    public double PixelsPerDip { get; set; } = 1;

    /// Mac NSShadow blur radius → WPF DropShadowEffect blur radius (Quartz's blur is about a third wider than its sigma).
    public const double BlurScale = 1.5;

    enum Kind { None, Plain, Shadow, Glow }

    public Painter(Action<Visual> addVisual) => add = addVisual;

    public void Begin(double viewHeight)
    {
        height = viewHeight;
        used = 0;
        alpha = 1;
        clip = null;
        saved.Clear();
        kind = Kind.None;
        dc = null;
    }

    public void End()
    {
        Close();
        for (int i = used; i < pool.Count; i++)
        {
            if (!drawn[i]) continue;
            using (pool[i].RenderOpen()) { }
            pool[i].Effect = null;
            drawn[i] = false;
        }
    }

    // MARK: State (alpha and clip carry over into every following layer, like a CGContext's graphics state)

    public void Save() => saved.Push((alpha, clip));

    public void Restore()
    {
        if (saved.Count == 0) return;
        var (a, c) = saved.Pop();
        if (a != alpha || c != clip) Close();
        alpha = a;
        clip = c;
    }

    /// Sets the alpha for what follows (CGContext.setAlpha: replaces, doesn't multiply).
    public void SetAlpha(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (value == alpha) return;
        Close();
        alpha = value;
    }

    public void SetClip(Geometry? geometry)
    {
        Close();
        clip = geometry;
    }

    // MARK: Layers

    /// The context for plain drawing (no shadow).
    public DrawingContext Plain
    {
        get
        {
            if (dc != null && kind == Kind.Plain && !exclusive) return dc;
            return Open(Kind.Plain, null);
        }
    }

    /// The context for text with a soft glow of `color` around it (labels over glass).
    public DrawingContext Glow(Color color)
    {
        if (dc != null && kind == Kind.Glow && glowColor == color && !exclusive) return dc;
        glowColor = color;
        return Open(Kind.Glow, e =>
        {
            e.Color = Color.FromRgb(color.R, color.G, color.B);
            e.Opacity = color.A / 255.0;
            e.ShadowDepth = 0;
            e.BlurRadius = 2.5 * BlurScale;
            e.Direction = 270;
        });
    }

    /// Drawing in the returned context, until it is disposed, casts one shadow (blur and offset in Mac points; the
    /// shadow falls downwards).
    public ShadowScope Shadow(double blur, double alpha, double offset = 1)
    {
        if (alpha <= 0.002) return new ShadowScope(this, Open(Kind.Plain, null, exclusiveLayer: true));
        var context = Open(Kind.Shadow, e =>
        {
            e.Color = Colors.Black;
            e.Opacity = Math.Clamp(alpha, 0, 1);
            e.BlurRadius = Math.Max(blur, 0) * BlurScale;
            e.ShadowDepth = Math.Max(offset, 0);
            e.Direction = 270;
        }, exclusiveLayer: true);
        return new ShadowScope(this, context);
    }

    public readonly struct ShadowScope(Painter painter, DrawingContext context) : IDisposable
    {
        public DrawingContext Context { get; } = context;
        public void Dispose() => painter.Close();
    }

    DrawingContext Open(Kind newKind, Action<DropShadowEffect>? effect, bool exclusiveLayer = false)
    {
        Close();
        if (used == pool.Count)
        {
            var created = new DrawingVisual();
            pool.Add(created);
            effects.Add(new DropShadowEffect { RenderingBias = RenderingBias.Performance });
            drawn.Add(false);
            add(created);
        }
        int index = used++;
        var visual = pool[index];
        if (effect != null)
        {
            effect(effects[index]);
            visual.Effect = effects[index];
        }
        else visual.Effect = null;
        drawn[index] = true;
        kind = newKind;
        exclusive = exclusiveLayer;
        dc = visual.RenderOpen();
        // Content-level flip, so shadows still fall down the screen.
        dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, height));
        if (clip != null) dc.PushClip(clip);
        if (alpha < 1) dc.PushOpacity(alpha);
        return dc;
    }

    void Close()
    {
        dc?.Close();
        dc = null;
        kind = Kind.None;
        exclusive = false;
    }

    // MARK: Text

    readonly Dictionary<(string, FontFamily, FontWeight, double), FormattedText> texts = [];

    public FormattedText Format(string text, FontFamily family, FontWeight weight, double size)
    {
        size = Math.Round(size * 4) / 4;
        var key = (text, family, weight, size);
        if (texts.TryGetValue(key, out var cached)) return cached;
        if (texts.Count > 600) texts.Clear();
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, PixelsPerDip);
        texts[key] = formatted;
        return formatted;
    }

    public double Width(string text, FontFamily family, FontWeight weight, double size) =>
        Format(text, family, weight, size).WidthIncludingTrailingWhitespace;

    readonly Dictionary<(string, FontFamily, FontWeight, double, double), string> truncated = [];

    /// Shortens the middle of a line that doesn't fit ("a very lo…ame.png").
    public string TruncateMiddle(string text, FontFamily family, FontWeight weight, double size, double maxWidth)
    {
        if (Width(text, family, weight, size) <= maxWidth || text.Length < 3) return text;
        var key = (text, family, weight, size, Math.Round(maxWidth));
        if (truncated.TryGetValue(key, out var done)) return done;
        int lo = 0, hi = text.Length;
        string Candidate(int keep) => text[..((keep + 1) / 2)] + "…" + text[(text.Length - keep / 2)..];
        while (lo < hi)
        {
            int keep = (lo + hi + 1) / 2;
            if (Width(Candidate(keep), family, weight, size) <= maxWidth) lo = keep; else hi = keep - 1;
        }
        if (truncated.Count > 200) truncated.Clear();
        return truncated[key] = Candidate(lo);
    }

    /// Draws one line centred on `centre` (y-up), rotated by `angle` degrees (counter-clockwise, like AppKit) and
    /// scaled. Long text shrinks to fit `maxWidth` (not below 7 pt), then loses its middle.
    public void Text(DrawingContext context, string text, Point centre, FontFamily family, FontWeight weight, double size,
                     Color color, double maxWidth, double angle = 0, double scale = 1)
    {
        if (color.A < 3 || string.IsNullOrEmpty(text)) return;
        var formatted = Format(text, family, weight, size);
        double width = formatted.WidthIncludingTrailingWhitespace;
        if (maxWidth > 0 && width > maxWidth)
        {
            double fitted = Math.Max(7, size * maxWidth / width);
            formatted = Format(text, family, weight, fitted);
            width = formatted.WidthIncludingTrailingWhitespace;
            if (width > maxWidth + 0.5)
            {
                formatted = Format(TruncateMiddle(text, family, weight, fitted, maxWidth), family, weight, fitted);
                width = formatted.WidthIncludingTrailingWhitespace;
            }
        }
        formatted.SetForegroundBrush(Look.Brush(color));
        var m = Matrix.Identity;
        m.Scale(1, -1);
        if (scale != 1) m.Scale(scale, scale);
        if (angle != 0) m.Rotate(angle);
        m.Translate(centre.X, centre.Y);
        context.PushTransform(new MatrixTransform(m));
        // Centre on the caps rather than the line box (Segoe's line box sits a little high).
        context.DrawText(formatted, new Point(-width / 2, -formatted.Height / 2 + 0.05 * formatted.Height));
        context.Pop();
    }

    /// One line starting at `left`, vertically centred on `y` (y-up), cut with an ellipsis at `maxWidth`.
    public void TextLeft(DrawingContext context, string text, double left, double y, FontFamily family, FontWeight weight,
                         double size, Color color, double maxWidth)
    {
        if (color.A < 3 || string.IsNullOrEmpty(text)) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Look.Brush(color), PixelsPerDip)
        {
            MaxTextWidth = Math.Max(maxWidth, 1), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        context.PushTransform(new MatrixTransform(1, 0, 0, -1, left, y));
        context.DrawText(formatted, new Point(0, -formatted.Height / 2 + 0.05 * formatted.Height));
        context.Pop();
    }

    // MARK: Gradients (NSGradient semantics, y-up)

    /// A linear gradient that spans `rect` along `angle` degrees (0 = left to right, 90 = bottom to top, −90 = top to
    /// bottom), with evenly spaced colours.
    public static LinearGradientBrush Gradient(Rect rect, double angle, params Color[] colors)
    {
        double rad = angle * Math.PI / 180;
        var d = new Vector(Math.Cos(rad), Math.Sin(rad));
        double extent = Math.Abs(rect.Width / 2 * d.X) + Math.Abs(rect.Height / 2 * d.Y);
        var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = c - d * extent,
            EndPoint = c + d * extent,
        };
        for (int i = 0; i < colors.Length; i++)
            brush.GradientStops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : i / (double)(colors.Length - 1)));
        brush.Freeze();
        return brush;
    }

    public static RadialGradientBrush Radial(Point centre, double radius, Color inner, Color outer)
    {
        var brush = new RadialGradientBrush(inner, outer)
        {
            MappingMode = BrushMappingMode.Absolute, Center = centre, GradientOrigin = centre, RadiusX = radius, RadiusY = radius,
        };
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(Color color, double width, bool round = false)
    {
        var pen = new Pen(Look.Brush(color), width);
        if (round) { pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round; pen.LineJoin = PenLineJoin.Round; }
        pen.Freeze();
        return pen;
    }
}
