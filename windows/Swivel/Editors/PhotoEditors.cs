using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

// MARK: - Adjust

/// The Edit sliders' pixel maths, on straight BGRA bytes. A port of the Core Image chain the Mac editor uses
/// (CIExposureAdjust → CIColorControls → CITemperatureAndTint → CIVignette → CISharpenLuminance), approximated where
/// Core Image's kernels aren't documented: exposure, warmth and vignette work on linear light like Core Image's working
/// space; contrast and saturation pivot on perceptual (sRGB) values, which is how CIColorControls looks in practice.
/// Every step is skipped at its neutral value, so neutral settings return the pixels unchanged.
static class AdjustMath
{
    /// Exposure (EV), contrast, saturation, warmth, vignette, sharpen.
    public static readonly double[] Neutral = [0, 1, 1, 0, 0, 0];

    static readonly float[] toLinear = Enumerable.Range(0, 256).Select(i => (float)Decode(i / 255.0)).ToArray();

    static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    static float Encode(float v)
    {
        if (v <= 0) return v * 12.92f;
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
    }

    static float DecodeF(float v)
    {
        if (v <= 0) return v / 12.92f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    public static bool IsNeutral(double[] v) => v.Zip(Neutral).All(p => p.First == p.Second);

    /// The image with the adjustments applied (a new frozen bitmap; the image itself at neutral).
    public static BitmapSource Render(BitmapSource image, double[] v)
    {
        if (IsNeutral(v)) return image;
        var pixels = Pixels.Bgra(image, out int w, out int h);
        return Pixels.FromBgra(Apply(pixels, w, h, v), w, h);
    }

    public static byte[] Apply(byte[] source, int w, int h, double[] v)
    {
        var output = (byte[])source.Clone();
        double exposure = v[0], contrast = v[1], saturation = v[2], warmth = v[3], vignette = v[4], sharpen = v[5];
        bool color = exposure != 0 || contrast != 1 || saturation != 1 || warmth != 0 || vignette > 0;
        if (color)
        {
            float gain = (float)Math.Pow(2, exposure);
            var (mr, mg, mb) = warmth != 0 ? WhiteBalance(6500 + warmth * 2500, 6500) : (1f, 1f, 1f);
            bool tone = contrast != 1 || saturation != 1;
            float c = (float)contrast, s = (float)saturation;
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, halfDiagonal = Math.Max(1, Math.Sqrt(cx * cx + cy * cy));
            float strength = (float)(vignette * 0.45);
            Parallel.For(0, h, y =>
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    float b = toLinear[source[i]] * gain, g = toLinear[source[i + 1]] * gain, r = toLinear[source[i + 2]] * gain;
                    if (tone)
                    {
                        float pr = Encode(r), pg = Encode(g), pb = Encode(b);
                        float gray = 0.2125f * pr + 0.7154f * pg + 0.0721f * pb;
                        pr = gray + s * (pr - gray);
                        pg = gray + s * (pg - gray);
                        pb = gray + s * (pb - gray);
                        pr = (pr - 0.5f) * c + 0.5f;
                        pg = (pg - 0.5f) * c + 0.5f;
                        pb = (pb - 0.5f) * c + 0.5f;
                        r = DecodeF(pr);
                        g = DecodeF(pg);
                        b = DecodeF(pb);
                    }
                    r *= mr;
                    g *= mg;
                    b *= mb;
                    if (strength > 0)
                    {
                        double dx = x - cx, dy = y - cy;
                        float d = (float)(Math.Sqrt(dx * dx + dy * dy) / halfDiagonal);
                        float t = Math.Clamp((d - 0.25f) / 0.75f, 0, 1);
                        float f = 1 - strength * t * t * (3 - 2 * t);
                        r *= f;
                        g *= f;
                        b *= f;
                    }
                    output[i] = ToByte(Encode(b));
                    output[i + 1] = ToByte(Encode(g));
                    output[i + 2] = ToByte(Encode(r));
                }
            });
        }
        if (sharpen > 0) Sharpen(output, w, h, (float)sharpen);
        return output;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    /// Channel gains that take white light of `neutral` kelvin to `target` (CITemperatureAndTint's inputNeutral and
    /// inputTargetNeutral), in linear light, normalised to keep the luminance.
    public static (float R, float G, float B) WhiteBalance(double neutral, double target)
    {
        var n = Blackbody(neutral);
        var t = Blackbody(target);
        double r = t.R / n.R, g = t.G / n.G, b = t.B / n.B;
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return ((float)(r / lum), (float)(g / lum), (float)(b / lum));
    }

    /// The linear-light colour of a black body (Tanner Helland's fit of the CIE data, 1000–40000 K).
    static (double R, double G, double B) Blackbody(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        static double Lin(double v) => Decode(Math.Clamp(v, 1, 255) / 255);
        return (Lin(r), Lin(g), Lin(b));
    }

    /// Unsharp mask on luminance (CISharpenLuminance): each channel gets `amount` × (Y − blurred Y), so colours don't
    /// fringe. The blur is a small Gaussian (σ ≈ 1 px, Core Image's default radius of about 1.7 px).
    static void Sharpen(byte[] px, int w, int h, float amount)
    {
        var luma = new float[w * h];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4) luma[i] = 0.0722f * px[p] + 0.7152f * px[p + 1] + 0.2126f * px[p + 2];
        float[] kernel = [0.054f, 0.244f, 0.404f, 0.244f, 0.054f];
        var temp = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++) sum += kernel[k + 2] * luma[row + Math.Clamp(x + k, 0, w - 1)];
                temp[row + x] = sum;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++) sum += kernel[k + 2] * temp[Math.Clamp(y + k, 0, h - 1) * w + x];
                int i = y * w + x;
                float delta = amount * (luma[i] - sum);
                int p = i * 4;
                px[p] = (byte)Math.Clamp((int)MathF.Round(px[p] + delta), 0, 255);
                px[p + 1] = (byte)Math.Clamp((int)MathF.Round(px[p + 1] + delta), 0, 255);
                px[p + 2] = (byte)Math.Clamp((int)MathF.Round(px[p + 2] + delta), 0, 255);
            }
        });
    }
}

/// Exposure, contrast, saturation, warmth, vignette and sharpness with a live preview.
sealed class AdjustEditor : Editor
{
    sealed record Adjustment(DetentSlider Slider, TextBlock Label, double Neutral, Func<double, string> Describe);

    readonly ImagePreview preview;
    BitmapSource? small;
    readonly List<Adjustment> adjustments = [];
    readonly LiveRender<BitmapSource?> renderer;

    public AdjustEditor(string path) : this(path, new ImagePreview()) { }

    AdjustEditor(string path, ImagePreview preview) : base("Edit Photo", path, preview)
    {
        this.preview = preview;
        renderer = new LiveRender<BitmapSource?>(image => { if (image != null) preview.Image = image; });
        Func<double, string> percent = v => EditorFormat.Signed((v - 1) * 100, 0) + "%";
        Func<double, string> share = v => $"{(int)Math.Round(v / 2 * 100, MidpointRounding.AwayFromZero)}%";
        (string Name, double Min, double Max, double Neutral, Func<double, string> Describe)[] specs =
        [
            ("Exposure", -2, 2, 0, v => EditorFormat.Signed(v, 1) + " EV"),
            ("Contrast", 0.5, 1.5, 1, percent),
            ("Saturation", 0, 2, 1, percent),
            ("Warmth", -1, 1, 0, v => EditorFormat.Signed(v * 100, 0)),
            ("Vignette", 0, 2, 0, share),
            ("Sharpen", 0, 2, 0, share),
        ];
        foreach (var spec in specs)
        {
            var control = Slider(spec.Min, spec.Max, spec.Neutral);
            // The value beside each slider greys out at neutral: with the detent, that's how you find it again.
            var label = ToolUI.Scaled(new TextBlock { MinWidth = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 11);
            adjustments.Add(new Adjustment(control, label, spec.Neutral, spec.Describe));
            AddRow(spec.Name, control, label);
        }
    }

    /// The sliders in order: exposure, contrast, saturation, warmth, vignette, sharpen (for tests).
    public IReadOnlyList<DetentSlider> Sliders => adjustments.Select(a => a.Slider).ToList();

    protected override async Task LoadAsync()
    {
        var source = Source;
        var image = await Task.Run(() => Images.Load(source, 1400));
        small = image;
        preview.Image = image;
        preview.Original = image;
    }

    double[] Values => adjustments.Select(a => a.Slider.Value).ToArray();

    protected override void Update()
    {
        foreach (var adjustment in adjustments)
        {
            double value = adjustment.Slider.Value;
            adjustment.Label.Text = adjustment.Describe(value);
            // Grey at neutral, so the detent reads at a glance too.
            adjustment.Label.SetResourceReference(TextBlock.ForegroundProperty,
                value == adjustment.Neutral ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
        }
        if (small is not { } image) return;
        var values = Values;
        renderer.Request(() => AdjustMath.Render(image, values));
    }

    protected override void Reset()
    {
        foreach (var adjustment in adjustments) adjustment.Slider.Value = adjustment.Neutral;
    }

    public override void DidClose()
    {
        renderer.Cancel();
        preview.Stop();
    }

    protected override Task<List<string>> ApplyAsync()
    {
        var values = Values;
        var source = Source;
        return Task.Run(() =>
        {
            var result = AdjustMath.Render(Images.Load(source), values);
            var ext = Images.WritableExt(source);
            var output = Paths.Output(source, ext, " edited");
            Images.Write(result, output, ext, 0.95);
            return new List<string> { output };
        });
    }
}

// MARK: - Blur helpers

static class Blur
{
    /// Box widths whose three passes approximate a Gaussian of `sigma` (Kutskir's method).
    public static int[] BoxesForGauss(double sigma, int n = 3)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        return Enumerable.Range(0, n).Select(i => i < m ? wl : wu).ToArray();
    }

    /// Blurs interleaved float channels in place (edges clamped, as Core Image's clampedToExtent).
    public static void Gaussian(float[] data, int w, int h, int channels, double sigma)
    {
        if (sigma < 0.3 || w < 1 || h < 1) return;
        var temp = new float[data.Length];
        foreach (var box in BoxesForGauss(sigma))
        {
            int r = (box - 1) / 2;
            if (r < 1) continue;
            BoxHorizontal(data, temp, w, h, channels, r);
            BoxVertical(temp, data, w, h, channels, r);
        }
    }

    static void BoxHorizontal(float[] src, float[] dst, int w, int h, int ch, int r)
    {
        float scale = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w * ch;
            for (int c = 0; c < ch; c++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1) * ch + c];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * ch + c] = sum * scale;
                    sum += src[row + Math.Min(x + r + 1, w - 1) * ch + c] - src[row + Math.Max(x - r, 0) * ch + c];
                }
            }
        });
    }

    static void BoxVertical(float[] src, float[] dst, int w, int h, int ch, int r)
    {
        float scale = 1f / (2 * r + 1);
        int stride = w * ch;
        Parallel.For(0, w, x =>
        {
            for (int c = 0; c < ch; c++)
            {
                int col = x * ch + c;
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Math.Clamp(k, 0, h - 1) * stride + col];
                for (int y = 0; y < h; y++)
                {
                    dst[y * stride + col] = sum * scale;
                    sum += src[Math.Min(y + r + 1, h - 1) * stride + col] - src[Math.Max(y - r, 0) * stride + col];
                }
            }
        });
    }

    /// An alpha mask the size of the canvas: a rounded rectangle blurred by `sigma`, drawn at a reduced scale (a
    /// shadow is smooth, so it can be) and returned as a black, partly transparent bitmap to stretch over the canvas.
    public static BitmapSource Shadow(int width, int height, Rect frame, double radius, double sigma, double opacity)
    {
        double k = Math.Min(1, 512.0 / Math.Max(width, height));
        int sw = Math.Max(1, (int)Math.Ceiling(width * k)), sh = Math.Max(1, (int)Math.Ceiling(height * k));
        var mask = new float[sw * sh];
        double x0 = frame.Left * k, x1 = frame.Right * k, y0 = frame.Top * k, y1 = frame.Bottom * k, r = radius * k;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double dx = Math.Max(Math.Max(x0 + r - px, px - (x1 - r)), 0), dy = Math.Max(Math.Max(y0 + r - py, py - (y1 - r)), 0);
                bool inside = px >= x0 && px <= x1 && py >= y0 && py <= y1 && (r <= 0 || dx * dx + dy * dy <= r * r);
                mask[y * sw + x] = inside ? 1 : 0;
            }
        Gaussian(mask, sw, sh, 1, sigma * k);
        var pixels = new byte[sw * sh * 4];
        for (int i = 0; i < mask.Length; i++) pixels[i * 4 + 3] = (byte)Math.Clamp((int)Math.Round(mask[i] * opacity * 255), 0, 255);
        return Pixels.FromBgra(pixels, sw, sh);
    }
}

// MARK: - Add Background

/// Places the photo on a colour or gradient backdrop with padding, rounded corners and a shadow. (The Mac editor does
/// the same: it composes, it doesn't cut the subject out, so nothing here depends on subject-lifting APIs.)
sealed class BackgroundEditor : Editor
{
    public readonly record struct Look(double Padding, double Radius, double? Ratio, Color[] Colors, bool Shadow);

    readonly ImagePreview preview;
    BitmapSource? small;
    static readonly (string Name, Color[] Colors)[] styles =
    [
        ("Sunset", [Rgb(1, 0.55, 0.3), Rgb(0.93, 0.3, 0.45)]),
        ("Ocean", [Rgb(0.25, 0.6, 0.95), Rgb(0.35, 0.3, 0.85)]),
        ("Mint", [Rgb(0.6, 0.95, 0.8), Rgb(0.2, 0.7, 0.65)]),
        ("Candy", [Rgb(1, 0.75, 0.85), Rgb(0.75, 0.6, 1)]),
        ("White", [System.Windows.Media.Colors.White]),
        ("Black", [System.Windows.Media.Colors.Black]),
        ("Custom", []),
    ];
    static readonly (string Name, double? Ratio)[] aspects = [("Auto", null), ("1:1", 1), ("4:5", 0.8), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:3", 4.0 / 3)];
    readonly ComboBox style, aspect;
    readonly ColorWell colorWell;
    readonly DetentSlider padding, radius;
    readonly CheckBox shadow;
    int lastAspect;
    bool fadeNext;
    readonly LiveRender<BitmapSource?> renderer;

    static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    public BackgroundEditor(string path) : this(path, new ImagePreview()) { }

    BackgroundEditor(string path, ImagePreview preview) : base("Add Background", path, preview)
    {
        this.preview = preview;
        renderer = new LiveRender<BitmapSource?>(Show);
        style = Popup(styles.Select(s => L(s.Name)));
        colorWell = ColorWell(Color.FromRgb(0x30, 0xB0, 0xC7));
        AddRow("Background", style, colorWell);
        padding = Slider(0, 0.4, 0.12);
        AddRow("Spacing", padding);
        radius = Slider(0, 0.2, 0.04);
        AddRow("Corners", radius);
        aspect = Popup(aspects.Select(a => L(a.Name)));
        AddRow("Aspect ratio", aspect);
        shadow = Checkbox("Drop shadow", true);
        AddRow("", shadow);
        colorWell.Visibility = Visibility.Collapsed;
    }

    Look CurrentLook
    {
        get
        {
            var colors = styles[Math.Max(0, style.SelectedIndex)].Colors;
            if (colors.Length == 0) colors = [colorWell.Color];
            return new Look(padding.Value, radius.Value, aspects[Math.Max(0, aspect.SelectedIndex)].Ratio, colors, shadow.IsChecked == true);
        }
    }

    protected override async Task LoadAsync()
    {
        // Decode and compose the first preview together, so the window opens on the finished picture.
        var source = Source;
        var look = CurrentLook;
        var (image, first) = await Sta.Run(() =>
        {
            var image = Images.Load(source, 1000);
            return (image, Render(image, look));
        });
        small = image;
        preview.Image = first;
    }

    /// The canvas size for an image of w × h with this look.
    public static Size CanvasSize(double w, double h, Look look)
    {
        double pad = look.Padding * Math.Max(w, h);
        double cw = w + pad * 2, ch = h + pad * 2;
        if (look.Ratio is double ratio)
        {
            if (cw / ch > ratio) ch = cw / ratio;
            else cw = ch * ratio;
        }
        return new Size(Math.Round(cw), Math.Round(ch));
    }

    /// Call on an STA thread.
    public static BitmapSource Render(BitmapSource image, Look look)
    {
        double w = image.PixelWidth, h = image.PixelHeight;
        var canvas = CanvasSize(w, h, look);
        int cw = (int)canvas.Width, ch = (int)canvas.Height;
        var frame = new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        double corner = look.Radius * Math.Min(w, h);
        double side = Math.Max(w, h);
        // The Mac shadow: offset down by 1.2 % of the long side, blurred by 4 % (a Gaussian of about half that), 40 % black.
        var shadowMask = look.Shadow
            ? Blur.Shadow(cw, ch, new Rect(frame.X, frame.Y + side * 0.012, w, h), corner, side * 0.02, 0.4)
            : null;
        return Pixels.Render(cw, ch, dc =>
        {
            var full = new Rect(0, 0, cw, ch);
            Brush background = look.Colors.Length == 1
                ? new SolidColorBrush(look.Colors[0])
                : new LinearGradientBrush(look.Colors[0], look.Colors[^1], new Point(0, 0), new Point(cw, ch)) { MappingMode = BrushMappingMode.Absolute };
            dc.DrawRectangle(background, null, full);
            if (shadowMask != null)
            {
                dc.DrawImage(shadowMask, full);
                dc.DrawRoundedRectangle(Brushes.White, null, frame, corner, corner);
            }
            dc.PushClip(new RectangleGeometry(frame, corner, corner));
            dc.DrawImage(image, frame);
            dc.Pop();
        });
    }

    void Show(BitmapSource? image)
    {
        if (image is null) return;
        preview.SetImage(image, fadeNext);
        fadeNext = false;
    }

    protected override void Update()
    {
        colorWell.Visibility = style.SelectedIndex == styles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        // A new aspect reshapes the whole picture: crossfade it rather than cut.
        if (aspect.SelectedIndex != lastAspect)
        {
            lastAspect = aspect.SelectedIndex;
            fadeNext = true;
        }
        if (small is not { } image) return;
        var look = CurrentLook;
        renderer.Request(() => Sta.Invoke(() => Render(image, look)));
    }

    protected override void Reset()
    {
        style.SelectedIndex = 0;
        padding.Value = 0.12;
        radius.Value = 0.04;
        aspect.SelectedIndex = 0;
        shadow.IsChecked = true;
    }

    public override void DidClose()
    {
        renderer.Cancel();
        preview.Stop();
    }

    protected override Task<List<string>> ApplyAsync()
    {
        var look = CurrentLook;
        var source = Source;
        return Sta.Run(() =>
        {
            var result = Render(Images.Load(source), look);
            var output = Paths.Output(source, "png", " background");
            Images.Write(result, output, "png", 1);
            return new List<string> { output };
        });
    }
}

// MARK: - Redact

/// The covering styles, on straight BGRA bytes. Rects are normalised (0–1), top-left origin.
static class RedactMath
{
    public enum Style { Blur, Pixelate, Solid }

    /// Pixel bounds of a normalised rect, rounded outward so the cover never falls short of the box.
    public static Int32Rect PixelRect(Rect unit, int w, int h)
    {
        int x0 = Math.Clamp((int)Math.Floor(unit.Left * w), 0, w), y0 = Math.Clamp((int)Math.Floor(unit.Top * h), 0, h);
        int x1 = Math.Clamp((int)Math.Ceiling(unit.Right * w), 0, w), y1 = Math.Clamp((int)Math.Ceiling(unit.Bottom * h), 0, h);
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public static BitmapSource Render(BitmapSource image, IReadOnlyList<Rect> rects, Style style)
    {
        if (rects.Count == 0) return image;
        var pixels = Pixels.Bgra(image, out int w, out int h);
        Apply(pixels, w, h, rects, style);
        return Pixels.FromBgra(pixels, w, h);
    }

    public static void Apply(byte[] px, int w, int h, IReadOnlyList<Rect> rects, Style style)
    {
        var boxes = rects.Select(r => PixelRect(r, w, h)).Where(r => r.Width > 0 && r.Height > 0).ToList();
        if (boxes.Count == 0) return;
        switch (style)
        {
            case Style.Solid:
                foreach (var r in boxes)
                    for (int y = r.Y; y < r.Y + r.Height; y++)
                        for (int x = r.X; x < r.X + r.Width; x++)
                        {
                            int i = (y * w + x) * 4;
                            px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255;
                        }
                break;
            case Style.Pixelate:
                Pixelate(px, w, h, boxes, Math.Max(w, h) / 70.0);
                break;
            default:
                BlurBoxes(px, w, h, boxes, Math.Max(w, h) * 0.02);
                break;
        }
    }

    /// CIPixellate: one colour per square of the image-wide grid (the cell's average), shown inside the boxes.
    static void Pixelate(byte[] px, int w, int h, List<Int32Rect> boxes, double cell)
    {
        cell = Math.Max(1, cell);
        var averages = new Dictionary<(int, int), (byte B, byte G, byte R, byte A)>();
        (byte, byte, byte, byte) Average(int cx, int cy)
        {
            if (averages.TryGetValue((cx, cy), out var known)) return known;
            int x0 = (int)Math.Floor(cx * cell), x1 = Math.Min(w, (int)Math.Floor((cx + 1) * cell));
            int y0 = (int)Math.Floor(cy * cell), y1 = Math.Min(h, (int)Math.Floor((cy + 1) * cell));
            x1 = Math.Max(x1, x0 + 1);
            y1 = Math.Max(y1, y0 + 1);
            double b = 0, g = 0, r = 0, a = 0;
            int n = 0;
            for (int y = y0; y < y1 && y < h; y++)
                for (int x = x0; x < x1 && x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    double alpha = px[i + 3] / 255.0;
                    b += px[i] * alpha; g += px[i + 1] * alpha; r += px[i + 2] * alpha; a += alpha;
                    n++;
                }
            var value = a > 0
                ? ((byte)Math.Round(b / a), (byte)Math.Round(g / a), (byte)Math.Round(r / a), (byte)Math.Round(a / Math.Max(1, n) * 255))
                : ((byte)0, (byte)0, (byte)0, (byte)0);
            averages[(cx, cy)] = value;
            return value;
        }
        // Averages first (from the untouched pixels), then paint.
        var paint = new List<(Int32Rect Box, Dictionary<(int, int), (byte, byte, byte, byte)> Cells)>();
        foreach (var box in boxes)
        {
            var cells = new Dictionary<(int, int), (byte, byte, byte, byte)>();
            for (int cy = (int)(box.Y / cell); cy * cell < box.Y + box.Height; cy++)
                for (int cx = (int)(box.X / cell); cx * cell < box.X + box.Width; cx++)
                    cells[(cx, cy)] = Average(cx, cy);
            paint.Add((box, cells));
        }
        foreach (var (box, cells) in paint)
            for (int y = box.Y; y < box.Y + box.Height; y++)
                for (int x = box.X; x < box.X + box.Width; x++)
                {
                    var (b, g, r, a) = cells[((int)(x / cell), (int)(y / cell))];
                    int i = (y * w + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
                }
    }

    /// A Gaussian blur of the whole image (edges clamped), shown inside the boxes. A blur this heavy is smooth, so it
    /// is worked out on a copy shrunk by up to σ / 4 and sampled back up — the same picture, a fraction of the work.
    static void BlurBoxes(byte[] px, int w, int h, List<Int32Rect> boxes, double sigma)
    {
        int f = Math.Max(1, (int)Math.Floor(sigma / 4));
        int sw = (w + f - 1) / f, sh = (h + f - 1) / f;
        var small = new float[sw * sh * 4];
        // Area average, premultiplied, so transparent pixels don't bleed colour.
        Parallel.For(0, sh, sy =>
        {
            for (int sx = 0; sx < sw; sx++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                int n = 0;
                for (int y = sy * f; y < Math.Min(h, sy * f + f); y++)
                    for (int x = sx * f; x < Math.Min(w, sx * f + f); x++)
                    {
                        int i = (y * w + x) * 4;
                        float alpha = px[i + 3] / 255f;
                        b += px[i] * alpha; g += px[i + 1] * alpha; r += px[i + 2] * alpha; a += alpha;
                        n++;
                    }
                int o = (sy * sw + sx) * 4;
                small[o] = b / n; small[o + 1] = g / n; small[o + 2] = r / n; small[o + 3] = a / n;
            }
        });
        Blur.Gaussian(small, sw, sh, 4, sigma / f);
        foreach (var box in boxes)
            Parallel.For(box.Y, box.Y + box.Height, y =>
            {
                double fy = Math.Clamp((y + 0.5) / f - 0.5, 0, sh - 1);
                int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
                float ty = (float)(fy - y0);
                for (int x = box.X; x < box.X + box.Width; x++)
                {
                    double fx = Math.Clamp((x + 0.5) / f - 0.5, 0, sw - 1);
                    int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                    float tx = (float)(fx - x0);
                    int i = (y * w + x) * 4;
                    float a = Sample(small, sw, x0, x1, y0, y1, tx, ty, 3);
                    if (a <= 0.0001f)
                    {
                        px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
                        continue;
                    }
                    for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Clamp((int)MathF.Round(Sample(small, sw, x0, x1, y0, y1, tx, ty, c) / a), 0, 255);
                    px[i + 3] = (byte)Math.Clamp((int)MathF.Round(a * 255), 0, 255);
                }
            });
    }

    static float Sample(float[] s, int sw, int x0, int x1, int y0, int y1, float tx, float ty, int c)
    {
        float top = s[(y0 * sw + x0) * 4 + c] * (1 - tx) + s[(y0 * sw + x1) * 4 + c] * tx;
        float bottom = s[(y1 * sw + x0) * 4 + c] * (1 - tx) + s[(y1 * sw + x1) * 4 + c] * tx;
        return top * (1 - ty) + bottom * ty;
    }
}

/// Finds faces and text on-device and covers them; drag on the photo to add a box, click a box to remove it. Every
/// change can be undone (Ctrl+Z), Reset included.
///
/// Apply waits for detection to finish (under its Saving… state), so a quick Enter can't save the photo before the
/// faces and text are found. It never writes a "redacted" file that covers nothing, and a detection that was cancelled
/// saves nothing at all.
///
/// Faces: Windows.Media.FaceAnalysis.FaceDetector. Text: Windows.Media.Ocr line boxes in each installed OCR language
/// among English, Chinese (Simplified / Traditional) and Japanese (else the user's profile languages).
sealed class RedactEditor : Editor
{
    public enum Kind { Face, Text, Manual }
    public readonly record struct Box(Rect Rect, Kind Kind);  // normalised, top-left origin
    sealed record Snapshot(List<Box> Boxes, bool Faces, bool Text, int Style);
    public enum Detection { Running, Finished, Failed }

    readonly RedactCanvas preview;
    BitmapSource? small;
    List<Box> boxes = [];
    /// What detection found, which Reset returns to.
    List<Box> detected = [];
    readonly CheckBox faces, text;
    readonly Segments style;
    readonly TextBlock status;
    readonly ProgressBar spinner = new() { IsIndeterminate = true, Width = 40 };
    Task? detection;
    CancellationTokenSource? detectionCancel;
    /// Stays `Running` if the detection task was cancelled before it could add what it found.
    public Detection DetectionState { get; private set; } = Detection.Running;
    readonly LiveRender<BitmapSource?> renderer;

    public RedactEditor(string path) : this(path, new RedactCanvas()) { }

    RedactEditor(string path, RedactCanvas preview) : base("Redact Photo", path, preview)
    {
        this.preview = preview;
        renderer = new LiveRender<BitmapSource?>(image => { if (image != null) preview.Picture = image; });
        faces = Checkbox("Faces", true);
        text = Checkbox("Text", true);
        AddRow("Detect", faces, text);
        style = Segments(["Blur", "Pixelate", "Solid"]);
        AddRow("Style", style);
        status = Label(L("Looking for faces and text…"));
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.None;
        AddRow("", spinner, Stretch(status));

        preview.OnAdd = rect => Change(() => boxes.Add(new Box(rect, Kind.Manual)));
        preview.OnRemove = index =>
        {
            var active = Active;
            if (index < 0 || index >= active.Count) return;
            var target = active[index];
            Change(() => boxes.Remove(target));
        };
    }

    public string Status => status.Text;

    protected override async Task LoadAsync()
    {
        var source = Source;
        var image = await Task.Run(() => Images.Load(source, 1400));
        small = image;
        detectionCancel = new CancellationTokenSource();
        detection = RunDetection(image, detectionCancel.Token);
    }

    async Task RunDetection(BitmapSource image, CancellationToken token)
    {
        DetectionResult? found;
        try { found = await Task.Run(() => Detect(image)); }
        catch (Exception ex)
        {
            Log.Write($"Redact detection failed: {ex.Message}");
            found = null;
        }
        if (token.IsCancellationRequested) return;
        spinner.Visibility = Visibility.Collapsed;
        if (found is null)
        {
            // Say so rather than "found 0": the photo may well have faces, and nothing covers them yet.
            DetectionState = Detection.Failed;
            status.Text = L("Couldn't look for faces and text. Drag on the photo to cover areas yourself.");
            status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            return;
        }
        detected = found.Boxes;
        boxes.AddRange(found.Boxes);
        DetectionState = Detection.Finished;
        // Undoing an edit made before detection finished would also drop what it found: start the history here.
        History.RemoveAll();
        int f = found.Boxes.Count(b => b.Kind == Kind.Face), t = found.Boxes.Count(b => b.Kind == Kind.Text);
        var message = L("Found %d faces and %d text areas. Drag to add a box, click one to remove it.", f, t);
        // Keep the UI honest about what this PC could look for.
        if (!found.FacesAvailable) message += " " + L("Face detection isn't available on this PC.");
        if (!found.TextAvailable) message += " " + L("Text recognition isn't available on this PC.");
        status.Text = message;
        preview.RevealOutlines();
        if (IsLoaded) Update();
    }

    sealed record DetectionResult(List<Box> Boxes, bool FacesAvailable, bool TextAvailable);

    /// Faces and text in normalised space, or null if both detectors failed (as opposed to finding nothing).
    static async Task<DetectionResult?> Detect(BitmapSource image)
    {
        var bgra = Pixels.Bgra(image, out int w, out int h);
        // Flatten onto white, so transparent areas read as paper rather than black.
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)((bgra[i + c] * a + 255 * (255 - a)) / 255);
            bgra[i + 3] = 255;
        }
        using var bitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(),
            global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var found = new List<Box>();
        bool facesOk = false, textOk = false, facesAvailable = false, textAvailable = false;

        try
        {
            if (global::Windows.Media.FaceAnalysis.FaceDetector.IsSupported)
            {
                facesAvailable = true;
                var detector = await global::Windows.Media.FaceAnalysis.FaceDetector.CreateAsync();
                var format = global::Windows.Graphics.Imaging.BitmapPixelFormat.Gray8;
                if (!global::Windows.Media.FaceAnalysis.FaceDetector.IsBitmapPixelFormatSupported(format))
                    format = global::Windows.Media.FaceAnalysis.FaceDetector.GetSupportedBitmapPixelFormats().First();
                using var gray = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(bitmap, format);
                foreach (var face in await detector.DetectFacesAsync(gray))
                {
                    var b = face.FaceBox;
                    found.Add(new Box(Grow(new Rect((double)b.X / w, (double)b.Y / h, (double)b.Width / w, (double)b.Height / h), 0.01, 0.01), Kind.Face));
                }
            }
            facesOk = true;
        }
        catch (Exception ex) { Log.Write($"Face detection failed: {ex.Message}"); }

        try
        {
            var engines = OcrEngines();
            textAvailable = engines.Count > 0;
            var lines = new List<Rect>();
            foreach (var engine in engines)
            {
                if (w > global::Windows.Media.Ocr.OcrEngine.MaxImageDimension || h > global::Windows.Media.Ocr.OcrEngine.MaxImageDimension) break;
                var result = await engine.RecognizeAsync(bitmap);
                foreach (var line in result.Lines)
                {
                    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                        x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
                    }
                    if (x1 <= x0 || y1 <= y0) continue;
                    var unit = Grow(new Rect(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h), 0.003, 0.004);
                    // Two languages often read the same line: keep one box for it.
                    if (lines.Any(existing => Overlap(existing, unit) > 0.5)) continue;
                    lines.Add(unit);
                }
            }
            found.AddRange(lines.Select(r => new Box(r, Kind.Text)));
            textOk = true;
        }
        catch (Exception ex) { Log.Write($"Text detection failed: {ex.Message}"); }

        if (!facesOk && !textOk) return null;
        return new DetectionResult(found, facesAvailable && facesOk, textAvailable && textOk);
    }

    /// OCR engines for the installed recognizer languages among the app's languages, or the profile languages.
    static List<global::Windows.Media.Ocr.OcrEngine> OcrEngines()
    {
        var wanted = new[] { "en", "zh-Hans", "zh-CN", "zh-Hant", "zh-TW", "zh-HK", "ja" };
        var engines = new List<global::Windows.Media.Ocr.OcrEngine>();
        foreach (var language in global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
        {
            var tag = language.LanguageTag;
            if (!wanted.Any(w => tag.Equals(w, StringComparison.OrdinalIgnoreCase) || tag.StartsWith(w + "-", StringComparison.OrdinalIgnoreCase))) continue;
            if (global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(language) is { } engine) engines.Add(engine);
        }
        if (engines.Count == 0 && global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback) engines.Add(fallback);
        return engines;
    }

    static Rect Grow(Rect r, double dx, double dy)
    {
        double x0 = Math.Max(0, r.Left - dx), y0 = Math.Max(0, r.Top - dy), x1 = Math.Min(1, r.Right + dx), y1 = Math.Min(1, r.Bottom + dy);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// Intersection over the smaller area.
    static double Overlap(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : i.Width * i.Height / smaller;
    }

    List<Box> Active => boxes.Where(b => b.Kind switch
    {
        Kind.Face => faces.IsChecked == true,
        Kind.Text => text.IsChecked == true,
        _ => true,
    }).ToList();

    /// The boxes that will be covered (for tests).
    public IReadOnlyList<Box> ActiveBoxes => Active;

    /// Adds a box as a drag on the photo would (for tests).
    public void AddBox(Rect unit) => preview.OnAdd?.Invoke(unit);

    // MARK: Undo

    Snapshot Current => new([.. boxes], faces.IsChecked == true, text.IsChecked == true, style.SelectedIndex);

    /// Makes a change that Ctrl+Z can take back.
    void Change(Action mutate)
    {
        var before = Current;
        mutate();
        History.Register(() => Restore(before));
        Update();
        NoteEdited();
    }

    void Restore(Snapshot state)
    {
        var current = Current;
        boxes = [.. state.Boxes];
        faces.IsChecked = state.Faces;
        text.IsChecked = state.Text;
        style.SelectedIndex = state.Style;
        History.Register(() => Restore(current));  // registered while undoing, so it becomes the redo
        Update();
        NoteEdited();
    }

    public override bool HasUnsavedWork => !boxes.SequenceEqual(detected);

    // MARK: Rendering

    protected override void Update()
    {
        if (small is not { } image) return;
        var rects = Active.Select(b => b.Rect).ToList();
        var chosen = (RedactMath.Style)style.SelectedIndex;
        preview.Outlines = rects;
        if (preview.Picture is null) preview.Picture = image;
        renderer.Request(() => RedactMath.Render(image, rects, chosen));
    }

    protected override void Reset()
    {
        Change(() =>
        {
            boxes = [.. detected];
            faces.IsChecked = true;
            text.IsChecked = true;
            style.SelectedIndex = 0;
        });
    }

    public override void DidClose()
    {
        detectionCancel?.Cancel();
        renderer.Cancel();
        preview.Stop();
    }

    protected override async Task<List<string>> ApplyAsync()
    {
        // Detection may still be running (Enter pressed straight away): what it finds has to be in the file, so wait
        // for it here, under Apply's Saving… state, before reading the boxes.
        if (detection != null) await detection;
        // Cancelled before it added its boxes (the window is closing): never write a file that may show the faces.
        if (DetectionState == Detection.Running) throw new Cancelled();
        var rects = Active.Select(b => b.Rect).ToList();
        var chosen = (RedactMath.Style)style.SelectedIndex;
        var source = Source;
        // Nothing would be covered, so a "… redacted" copy would be the original under a name that says otherwise.
        if (rects.Count == 0) throw new ToolError(L("Nothing to redact. Drag on the photo to cover an area."));
        return await Task.Run(() =>
        {
            var result = RedactMath.Render(Images.Load(source), rects, chosen);
            var ext = Images.WritableExt(source);
            var output = Paths.Output(source, ext, " redacted");
            Images.Write(result, output, ext, 0.95);
            return new List<string> { output };
        });
    }
}

/// Image preview that outlines redaction boxes and lets you draw or remove them. Rects are normalised, top-left origin.
///
/// The box under the pointer lights up with a × badge (and a pointing hand); elsewhere on the photo the crosshair says
/// "drag to add". A box is removed only by a click that starts and ends on it without moving, so a shaky press meant
/// to draw never un-redacts anything.
sealed class RedactCanvas : PictureCanvas
{
    List<Rect> outlines = [];
    public Action<Rect>? OnAdd { get; set; }
    /// Index into `Outlines`.
    public Action<int>? OnRemove { get; set; }

    Point? dragStart;
    Rect? dragRect;
    int? pressedBox;
    int? hovered;
    Spring hoverAmount = new(0, 0.12, 1);
    Spring pressAmount = new(0, 0.1, 1);
    Spring reveal = new(1, 0.2, 1);
    readonly FrameDriver driver = new();
    const double BadgeRadius = 8;

    public RedactCanvas()
    {
        driver.OnFrame = Advance;
    }

    public List<Rect> Outlines
    {
        get => outlines;
        set
        {
            outlines = value;
            if (hovered is int h && h >= outlines.Count) hovered = null;
            RedrawOverlay();
        }
    }

    Rect ToView(Rect r)
    {
        var i = ImageRect;
        if (i.IsEmpty) return Rect.Empty;
        return new Rect(i.X + r.X * i.Width, i.Y + r.Y * i.Height, r.Width * i.Width, r.Height * i.Height);
    }

    Point ToNormalized(Point p)
    {
        var i = ImageRect;
        return new Point((p.X - i.X) / i.Width, (p.Y - i.Y) / i.Height);
    }

    /// The hovered box is lit (not just fading out), so its × badge is live.
    bool BadgeLive => hovered != null && hoverAmount.Target > 0.5;

    /// The badge sits on the box's top-right corner.
    static Point BadgeCenter(Rect r) => new(r.Right, r.Top);

    /// The topmost box at a point, counting the hovered box's × badge as part of it.
    int? BoxAt(Point p)
    {
        if (BadgeLive && hovered is int h && h < outlines.Count)
        {
            var c = BadgeCenter(ToView(outlines[h]));
            if (Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y)) <= BadgeRadius + 2) return h;
        }
        for (int i = outlines.Count - 1; i >= 0; i--)
            if (ToView(outlines[i]).Contains(p)) return i;
        return null;
    }

    /// Detection finished: the outlines fade in rather than appear in one frame.
    public void RevealOutlines()
    {
        reveal.Snap(Motion.Reduced ? 1 : 0);
        reveal.Target = 1;
        Kick();
    }

    void Kick()
    {
        RedrawOverlay();
        if (PresentationSource.FromVisual(this) != null) driver.Start();
        else
        {
            hoverAmount.Snap(hoverAmount.Target);
            pressAmount.Snap(pressAmount.Target);
            reveal.Snap(reveal.Target);
        }
    }

    bool Advance(double dt)
    {
        hoverAmount.Step(dt, ToolUI.Tuning);
        pressAmount.Step(dt, ToolUI.Tuning);
        reveal.Step(dt, ToolUI.Tuning);
        RedrawOverlay();
        return !(hoverAmount.IsSettled && pressAmount.IsSettled && reveal.IsSettled);
    }

    public void Stop() => driver.Stop();

    // MARK: Pointer

    void UpdateHover(Point p)
    {
        var box = BoxAt(p);
        double target = box is null ? 0 : 1;
        if (box is int b && (b != hovered || hoverAmount.Target == 0))
        {
            // A newly hovered box lights up from nothing; one left behind keeps `hovered` while it fades out.
            if (b != hovered) hoverAmount.Snap(0);
            hovered = b;
        }
        if (hoverAmount.Target != target)
        {
            hoverAmount.Target = target;
            Kick();
        }
        Cursor = box != null ? Cursors.Hand : ImageRect.Contains(p) ? Cursors.Cross : Cursors.Arrow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (dragStart is Point start)
        {
            // Past a few points it's a drag: draw a new box, even if it started on an existing one.
            if (dragRect is null && Math.Sqrt((p.X - start.X) * (p.X - start.X) + (p.Y - start.Y) * (p.Y - start.Y)) <= 3) return;
            if (pressedBox != null)
            {
                pressedBox = null;
                pressAmount.Target = 0;
                Kick();
            }
            Cursor = Cursors.Cross;
            var r = EditorGeometry.FromEdges(start.X, start.Y, p.X, p.Y);
            r.Intersect(ImageRect);
            dragRect = r.IsEmpty ? new Rect(start, new Size(0, 0)) : r;
            RedrawOverlay();
            return;
        }
        UpdateHover(p);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragStart != null) return;
        if (hoverAmount.Target != 0)
        {
            hoverAmount.Target = 0;
            Kick();
        }
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Picture is null) return;
        Focus();
        var p = e.GetPosition(this);
        dragStart = p;
        dragRect = null;
        pressedBox = BoxAt(p);
        pressAmount.Target = pressedBox is null ? 0 : 1;
        CaptureMouse();
        Kick();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragStart is null) return;
        var p = e.GetPosition(this);
        var rect = dragRect;
        var pressed = pressedBox;
        dragStart = null;
        dragRect = null;
        pressedBox = null;
        pressAmount.Target = 0;
        ReleaseMouseCapture();
        if (rect is Rect r && r.Width > 4 && r.Height > 4)
        {
            var a = ToNormalized(r.TopLeft);
            var b = ToNormalized(r.BottomRight);
            OnAdd?.Invoke(new Rect(a, b));
        }
        else if (rect is null && pressed is int index && BoxAt(p) == index)
        {
            OnRemove?.Invoke(index);
        }
        Kick();
        UpdateHover(p);
    }

    // MARK: Drawing

    protected override void DrawOverlay(DrawingContext dc)
    {
        if (Picture is null || ImageRect.IsEmpty) return;
        var accent = Accent;
        for (int index = 0; index < outlines.Count; index++)
        {
            var frame = ToView(outlines[index]);
            double lit = index == hovered ? hoverAmount.Value : 0;
            double pressed = index == pressedBox ? pressAmount.Value : 0;
            if (lit + pressed > 0.01) dc.DrawRectangle(Brush(WithAlpha(accent, 0.15 * lit + 0.15 * pressed)), null, frame);
            dc.DrawRectangle(null, Pen(WithAlpha(accent, reveal.Value), 1.5 + lit), frame);
        }
        if (hovered is int h && h < outlines.Count && hoverAmount.Value > 0.01 && dragRect is null)
        {
            // The × badge says what a click on this box does.
            var c = BadgeCenter(ToView(outlines[h]));
            double radius = BadgeRadius * (Motion.Reduced ? 1 : 0.7 + 0.3 * hoverAmount.Value);
            dc.DrawEllipse(Brush(WithAlpha(accent, hoverAmount.Value)), null, c, radius, radius);
            var cross = Pen(WithAlpha(Colors.White, hoverAmount.Value), 1.5, PenLineCap.Round);
            double d = radius * 0.38;
            dc.DrawLine(cross, new Point(c.X - d, c.Y - d), new Point(c.X + d, c.Y + d));
            dc.DrawLine(cross, new Point(c.X - d, c.Y + d), new Point(c.X + d, c.Y - d));
        }
        if (dragRect is Rect drag)
        {
            var pen = new Pen(Brush(accent), 1) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            dc.DrawRectangle(null, pen, drag);
        }
    }
}

// MARK: - Metadata

/// Shows camera, date and location tags and removes the chosen groups, without re-encoding where Windows can rewrite
/// the tags in place (JPEG, TIFF, JPEG XR); other files are re-encoded with the remaining tags.
sealed class MetadataEditor : Editor
{
    public readonly record struct Removal(bool Location, bool Camera, bool Date, bool Author, bool Everything);

    readonly ImagePreview preview;
    readonly CheckBox location, camera, date, author, everything;

    public MetadataEditor(string path) : this(path, new ImagePreview()) { }

    MetadataEditor(string path, ImagePreview preview) : base("Edit Metadata", path, preview)
    {
        this.preview = preview;
        foreach (var (name, value) in Describe(path))
        {
            var field = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                MinHeight = 0,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            field.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
            AddRow(name, Stretch(field));
        }
        location = Checkbox("Location", true);
        camera = Checkbox("Camera & lens", false);
        date = Checkbox("Dates", false);
        author = Checkbox("Author", false);
        var groups = new WrapPanel();
        foreach (var box in new[] { location, camera, date, author })
        {
            box.Margin = new Thickness(0, 0, 12, 0);
            groups.Children.Add(box);
        }
        AddRow("Remove", Stretch(groups));
        everything = Checkbox("Remove all metadata", false);
        AddRow("", everything);
    }

    protected override async Task LoadAsync()
    {
        var source = Source;
        preview.Image = await Task.Run(() => Images.Load(source, 900));
    }

    protected override void Update()
    {
        foreach (var box in new[] { location, camera, date, author }) box.IsEnabled = everything.IsChecked != true;
    }

    protected override void Reset()
    {
        location.IsChecked = true;
        foreach (var box in new[] { camera, date, author, everything }) box.IsChecked = false;
    }

    public override void DidClose() => preview.Stop();

    /// Sets the checkboxes (for tests).
    public void Choose(Removal removal)
    {
        location.IsChecked = removal.Location;
        camera.IsChecked = removal.Camera;
        date.IsChecked = removal.Date;
        author.IsChecked = removal.Author;
        everything.IsChecked = removal.Everything;
    }

    // MARK: Reading

    /// The rows shown above the checkboxes: (English label, value).
    public static List<(string Name, string Value)> Describe(string path)
    {
        BitmapFrame? frame = null;
        BitmapMetadata? meta = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            frame = decoder.Frames[0];
            meta = frame.Metadata as BitmapMetadata;
        }
        catch (Exception) { /* Shown as unknowns. */ }

        string? Text(params string[] queries)
        {
            foreach (var q in queries)
                if (Query(meta, q) is { } value && Stringify(value) is { Length: > 0 } s) return s;
            return null;
        }
        double? Number(params string[] queries)
        {
            foreach (var q in queries)
                if (Query(meta, q) is { } value && ToDouble(value) is double d) return d;
            return null;
        }

        var cameraName = string.Join(" ", new[] { Text("System.Photo.CameraManufacturer"), Text("System.Photo.CameraModel") }.Where(s => s != null));
        var shot = new List<string>();
        if (Number("System.Photo.FNumber") is double f) shot.Add("ƒ/" + f.ToString("0.#", CultureInfo.InvariantCulture));
        if (Number("System.Photo.ExposureTime") is double t)
            shot.Add(t < 1 ? $"1/{(int)Math.Round(1 / t)}s" : t.ToString("0.#", CultureInfo.InvariantCulture) + "s");
        if (Number("System.Photo.ISOSpeed") is double iso) shot.Add($"ISO {iso:0}");
        string place = "—";
        // The photo policy names (System.GPS.Latitude) don't read every file's GPS block; the EXIF paths do.
        double? Coord(params string[] queries) => queries.Select(q => Coordinate(Query(meta, q))).FirstOrDefault(v => v != null);
        if (Coord("System.GPS.Latitude", "/app1/ifd/gps/{ushort=2}", "/ifd/gps/{ushort=2}") is double lat
            && Coord("System.GPS.Longitude", "/app1/ifd/gps/{ushort=4}", "/ifd/gps/{ushort=4}") is double lon)
        {
            var ns = Text("System.GPS.LatitudeRef", "/app1/ifd/gps/{ushort=1}", "/ifd/gps/{ushort=1}") ?? "N";
            var ew = Text("System.GPS.LongitudeRef", "/app1/ifd/gps/{ushort=3}", "/ifd/gps/{ushort=3}") ?? "E";
            place = string.Format(CultureInfo.InvariantCulture, "{0:F5}° {1}, {2:F5}° {3}", lat, ns, lon, ew);
        }
        var taken = Text("/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}", "/app1/ifd/{ushort=306}", "/ifd/{ushort=306}");
        if (taken is null && Query(meta, "System.Photo.DateTaken") is { } when)
            taken = when switch
            {
                System.Runtime.InteropServices.ComTypes.FILETIME ft => DateTime.FromFileTime(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime).ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture),
                DateTime d => d.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => Stringify(when),
            };
        string? artist = null;
        try { artist = meta?.Author is { Count: > 0 } authors ? string.Join("; ", authors) : null; } catch (Exception) { }
        artist ??= Text("System.Author", "/app1/ifd/{ushort=315}", "/ifd/{ushort=315}");

        string dims = frame is null ? "? × ? px" : $"{frame.PixelWidth} × {frame.PixelHeight} px";
        return
        [
            ("Dimensions", dims),
            ("Camera", cameraName.Length == 0 ? "—" : cameraName),
            ("Lens", Text("System.Photo.LensModel") ?? "—"),
            ("Exposure", shot.Count == 0 ? "—" : string.Join("  ", shot)),
            ("Taken", taken ?? "—"),
            ("Location", place),
            ("Author", artist ?? "—"),
        ];
    }

    static object? Query(BitmapMetadata? meta, string query)
    {
        if (meta is null) return null;
        try { return meta.GetQuery(query); }
        catch (Exception) { return null; }
    }

    static string? Stringify(object value) => value switch
    {
        string s => s.Trim('\0', ' '),
        string[] list => string.Join("; ", list.Select(s => s.Trim('\0', ' ')).Where(s => s.Length > 0)),
        BitmapMetadata => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    static double? ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        ushort u => u,
        uint u => u,
        int i => i,
        short s => s,
        ushort[] list when list.Length > 0 => list[0],
        // EXIF rationals come back as 64-bit numbers: numerator in the low half, denominator in the high half.
        ulong r when (r >> 32) != 0 => (double)(uint)r / (r >> 32),
        _ => null,
    };

    /// System.GPS.Latitude / Longitude: degrees, minutes, seconds.
    static double? Coordinate(object? value) => value switch
    {
        double[] { Length: >= 3 } dms => dms[0] + dms[1] / 60 + dms[2] / 3600,
        double[] { Length: >= 1 } d => d[0],
        ulong[] { Length: >= 3 } r => Rational(r[0]) + Rational(r[1]) / 60 + Rational(r[2]) / 3600,
        _ => null,
    };

    static double Rational(ulong r) => (r >> 32) == 0 ? 0 : (double)(uint)r / (r >> 32);

    // MARK: Removing

    static readonly HashSet<string> CameraTags = new(StringComparer.Ordinal)
    {
        "Make", "Model", "LensModel", "LensMake", "LensSpecification", "BodySerialNumber", "SerialNumber", "LensSerialNumber",
        "FNumber", "ExposureTime", "ISOSpeedRatings", "PhotographicSensitivity", "FocalLength", "FocalLenIn35mmFilm",
        "Flash", "ExposureProgram", "MeteringMode", "WhiteBalance", "ApertureValue", "ShutterSpeedValue",
        "ExposureBiasValue", "BrightnessValue", "Software", "HostComputer",
        // XMP aux: names for the same things (the Mac reads them through ImageIO's XMP mapping).
        "Lens", "LensID", "LensInfo", "FocalLengthIn35mmFilm", "ISOSpeed",
    };

    static readonly HashSet<string> AuthorTags = new(StringComparer.Ordinal)
    {
        "Artist", "Copyright", "creator", "Byline", "rights", "CameraOwnerName", "CopyrightNotice", "Author", "XPAuthor",
    };

    /// Metadata blocks that hold the picture's own data (JPEG tables, colour), never walked or removed.
    static readonly HashSet<string> Structural = new(StringComparer.OrdinalIgnoreCase)
    {
        "luminance", "chrominance", "app0", "iCCP", "sRGB", "gAMA", "cHRM", "pHYs", "bKGD", "hIST", "sBIT", "IHDR", "PLTE", "tRNS",
    };

    static readonly Dictionary<int, string> ExifNames = new()
    {
        [270] = "ImageDescription", [271] = "Make", [272] = "Model", [274] = "Orientation", [305] = "Software", [306] = "DateTime",
        [315] = "Artist", [316] = "HostComputer", [33432] = "Copyright", [33434] = "ExposureTime", [33437] = "FNumber",
        [34850] = "ExposureProgram", [34855] = "ISOSpeedRatings", [36867] = "DateTimeOriginal", [36868] = "DateTimeDigitized",
        [36880] = "OffsetTime", [36881] = "OffsetTimeOriginal", [36882] = "OffsetTimeDigitized", [37377] = "ShutterSpeedValue",
        [37378] = "ApertureValue", [37379] = "BrightnessValue", [37380] = "ExposureBiasValue", [37383] = "MeteringMode",
        [37385] = "Flash", [37386] = "FocalLength", [37520] = "SubsecTime", [37521] = "SubsecTimeOriginal",
        [37522] = "SubsecTimeDigitized", [40093] = "XPAuthor", [41987] = "WhiteBalance", [41989] = "FocalLenIn35mmFilm",
        [42032] = "CameraOwnerName", [42033] = "BodySerialNumber", [42034] = "LensSpecification", [42035] = "LensMake",
        [42036] = "LensModel", [42037] = "LensSerialNumber",
    };

    /// The tag name a query segment stands for: EXIF ids by number, XMP and IPTC by name without namespace or spaces.
    public static string TagName(string segment, bool inGps)
    {
        var s = segment.TrimStart('/');
        if (s.StartsWith('[') && s.IndexOf(']') is int close and > 0) s = s[(close + 1)..];
        if (s.StartsWith("{ushort=", StringComparison.Ordinal) || s.StartsWith("{uint=", StringComparison.Ordinal))
        {
            var number = s[(s.IndexOf('=') + 1)..].TrimEnd('}');
            if (inGps) return "GPS" + number;
            return int.TryParse(number, out var id) && ExifNames.TryGetValue(id, out var name) ? name : "Tag" + number;
        }
        if (s.StartsWith("{str=", StringComparison.Ordinal) || s.StartsWith("{wstr=", StringComparison.Ordinal))
            s = s[(s.IndexOf('=') + 1)..].TrimEnd('}');
        if (s.Equals("gps", StringComparison.OrdinalIgnoreCase)) return "GPSInfo";
        if (s.Equals("tIME", StringComparison.Ordinal)) return "DateTime";
        if (s.IndexOf(':') is int colon and >= 0) s = s[(colon + 1)..];
        return s.Replace(" ", "").Replace("-", "");
    }

    public static bool ShouldRemove(string name, Removal removal)
    {
        if (name == "Orientation") return false;  // keeps photos upright
        if (removal.Everything) return true;
        if (removal.Location && name.StartsWith("GPS", StringComparison.Ordinal)) return true;
        if (removal.Camera && CameraTags.Contains(name)) return true;
        if (removal.Date && (name.Contains("Date") || name.StartsWith("OffsetTime") || name.StartsWith("SubsecTime")
                             || name is "TimeCreated" or "DigitalCreationTime")) return true;
        if (removal.Author && AuthorTags.Contains(name)) return true;
        return false;
    }

    /// The queries to remove from this metadata (containers whole when the container itself goes).
    public static List<string> Doomed(BitmapMetadata meta, Removal removal)
    {
        var doomed = new List<string>();
        Walk(meta, "", inGps: false, removal, doomed, depth: 0);
        return doomed;
    }

    static void Walk(BitmapMetadata meta, string prefix, bool inGps, Removal removal, List<string> doomed, int depth)
    {
        if (depth > 12) return;
        List<string> children;
        try { children = meta.ToList(); }
        catch (Exception) { return; }
        foreach (var raw in children)
        {
            var child = Friendly(raw, prefix);
            var path = prefix + child;
            var segment = child.TrimStart('/');
            var bare = segment.StartsWith('[') && segment.IndexOf(']') is int close and > 0 ? segment[(close + 1)..] : segment;
            if (Structural.Contains(bare)) continue;
            object? value;
            try { value = meta.GetQuery(raw); }
            catch (Exception) { continue; }
            var name = TagName(child, inGps);
            if (value is BitmapMetadata nested)
            {
                // "Everything" keeps walking so Orientation survives; the embedded preview goes whole (it shows the
                // original picture, and its own tags).
                if (removal.Everything && bare.Equals("thumb", StringComparison.OrdinalIgnoreCase)) { doomed.Add(path); continue; }
                bool isGps = bare.Equals("gps", StringComparison.OrdinalIgnoreCase);
                if (!removal.Everything && ShouldRemove(name, removal) && (isGps || IsTagContainer(bare))) { doomed.Add(path); continue; }
                Walk(nested, path, inGps || isGps, removal, doomed, depth + 1);
                continue;
            }
            if (ShouldRemove(name, removal)) doomed.Add(path);
        }
    }

    /// WIC lists EXIF blocks by number; the query language also knows them by name, which reads better and is what
    /// the photo metadata paths use: /app1/{ushort=0} is /app1/ifd, {ushort=34853} the GPS block, {ushort=34665}
    /// the EXIF block, /app1/{ushort=1} the embedded thumbnail.
    static string Friendly(string child, string prefix)
    {
        bool underApp1 = prefix.EndsWith("/app1", StringComparison.OrdinalIgnoreCase);
        return child switch
        {
            "/{ushort=0}" when underApp1 => "/ifd",
            "/{ushort=1}" when underApp1 => "/thumb",
            "/{ushort=34853}" => "/gps",
            "/{ushort=34665}" => "/exif",
            "/{ushort=40965}" => "/interop",
            _ => child,
        };
    }

    /// An XMP property that holds a list or structure (dc:creator, dc:rights), as opposed to a block like /app1.
    static bool IsTagContainer(string segment) => segment.Contains(':');

    protected override Task<List<string>> ApplyAsync()
    {
        var removal = new Removal(location.IsChecked == true, camera.IsChecked == true, date.IsChecked == true,
                                  author.IsChecked == true, everything.IsChecked == true);
        var source = Source;
        return Task.Run(() => new List<string> { Clean(source, removal) });
    }

    /// Writes "name clean.ext" without the removed tags and returns its path.
    public static string Clean(string source, Removal removal)
    {
        var ext = System.IO.Path.GetExtension(source).TrimStart('.');
        var output = Paths.Output(source, ext, " clean");
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output) ?? "", $".{System.IO.Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (!CleanInPlace(source, temp, removal)) Reencode(source, temp, removal);
            File.Move(temp, output, overwrite: false);
            return output;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// Copies the file and rewrites its tags where they are (the pixels are never decoded or re-encoded).
    static bool CleanInPlace(string source, string temp, Removal removal)
    {
        try
        {
            File.Copy(source, temp);
            bool saved;
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.Metadata is not BitmapMetadata current) return Done(true);
                var doomed = Doomed(current, removal);
                if (doomed.Count == 0) return Done(true);
                var writer = frame.CreateInPlaceBitmapMetadataWriter();
                foreach (var query in doomed) writer.RemoveQuery(query);
                saved = writer.TrySave();
            }
            if (!saved) File.Delete(temp);
            return saved;
        }
        catch (Exception ex)
        {
            Log.Write($"In-place metadata edit failed: {ex.Message}");
            if (File.Exists(temp)) File.Delete(temp);
            return false;
        }

        static bool Done(bool value) => value;
    }

    /// Re-encodes the picture with the remaining tags, for formats whose tags can't be rewritten in place: lossless
    /// for PNG / TIFF / BMP, quality 95 for lossy formats (as the Mac's fallback does).
    static void Reencode(string source, string temp, Removal removal)
    {
        BitmapDecoder decoder;
        using (var stream = File.OpenRead(source))
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapMetadata? metadata = null;
        if (frame.Metadata is BitmapMetadata current)
        {
            metadata = current.Clone();
            foreach (var query in Doomed(current, removal))
                try { metadata.RemoveQuery(query); } catch (Exception) { }
        }
        BitmapEncoder encoder;
        try { encoder = BitmapEncoder.Create(decoder.CodecInfo.ContainerFormat); }
        catch (Exception) { throw new ToolError(L("Can't write this format")); }
        if (encoder is JpegBitmapEncoder jpeg) jpeg.QualityLevel = 95;
        BitmapSource? thumbnail = removal.Everything ? null : frame.Thumbnail;
        encoder.Frames.Add(BitmapFrame.Create(frame, thumbnail, metadata, frame.ColorContexts));
        using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
        try { encoder.Save(output); }
        catch (Exception ex) { throw new ToolError(L("Couldn't save") + ": " + ex.Message); }
    }
}
