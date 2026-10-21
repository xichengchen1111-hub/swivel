using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Swivel;

// Port of Sources/Swivel/MenuAppearance.swift (1.3.1–1.3.3): quiet template surfaces, rope/peg colours and the
// Bookshelf / Sticky Notes palettes. JSON shapes match the Mac app's `clothesline.colors.v1` and `templates.colors.v1`.

/// Quiet, opaque card surfaces. Only a small selection tint uses the user's accent colour.
readonly record struct MenuAppearance(bool Dark)
{
    static Color Gray(double white, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(white * 255), (byte)Math.Round(white * 255), (byte)Math.Round(white * 255));

    public Color Surface => Gray(Dark ? 0.19 : 0.98);
    public Color Edge => Dark ? Gray(1, 0.16) : Gray(0, 0.10);
    public Color Trim => Gray(Dark ? 0.55 : 0.72);
    public Color Rail => Gray(Dark ? 0.27 : 0.87);
    public Color Ink => Gray(Dark ? 0.94 : 0.16);

    /// macOS systemBlue / systemGray / systemTeal / systemOrange, as on the Mac.
    static readonly Color[] Hints =
        [Color.FromRgb(0, 122, 255), Color.FromRgb(142, 142, 147), Color.FromRgb(48, 176, 199), Color.FromRgb(255, 149, 0)];

    public static Color Accent => System.Windows.SystemColors.AccentColor;

    public Color Card(int index, double selection)
    {
        var hint = Hints[((index % 4) + 4) % 4];
        return Mix.Lerp(Mix.Lerp(Surface, hint, Dark ? 0.035 : 0.025), Accent, Math.Clamp(selection, 0, 1) * (Dark ? 0.14 : 0.07));
    }
}

/// An opaque sRGB value, persisted as numbers rather than a platform colour object.
sealed record RGBColor
{
    [JsonPropertyName("red")] public double Red { get; init; }
    [JsonPropertyName("green")] public double Green { get; init; }
    [JsonPropertyName("blue")] public double Blue { get; init; }

    public RGBColor() : this(0.5, 0.5, 0.5) { }

    public RGBColor(double red, double green, double blue)
    {
        Red = Bounded(red);
        Green = Bounded(green);
        Blue = Bounded(blue);
    }

    public RGBColor(Color color) : this(color.R / 255.0, color.G / 255.0, color.B / 255.0) { }

    static double Bounded(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;

    [JsonIgnore] public RGBColor Sanitized => new(Red, Green, Blue);
    [JsonIgnore] public Color Color => Color.FromRgb((byte)Math.Round(Red * 255), (byte)Math.Round(Green * 255), (byte)Math.Round(Blue * 255));
}

sealed record ClotheslineColors
{
    [JsonPropertyName("rope")] public RGBColor Rope { get; init; } = RopeColorPreset.Silver.Rgb()!;
    [JsonPropertyName("peg")] public RGBColor Peg { get; init; } = RopeColorPreset.Silver.Rgb()!;

    [JsonIgnore] public ClotheslineColors Sanitized => new() { Rope = (Rope ?? new()).Sanitized, Peg = (Peg ?? new()).Sanitized };
}

enum RopeColorPreset { Silver, Graphite, Sand, Sage, Mist, Rose, Custom }

static class RopeColorPresets
{
    public static readonly RopeColorPreset[] All =
        [RopeColorPreset.Silver, RopeColorPreset.Graphite, RopeColorPreset.Sand, RopeColorPreset.Sage, RopeColorPreset.Mist, RopeColorPreset.Rose, RopeColorPreset.Custom];

    public static string Title(this RopeColorPreset preset) => preset switch
    {
        RopeColorPreset.Silver => L("Silver"),
        RopeColorPreset.Graphite => L("Graphite"),
        RopeColorPreset.Sand => L("Sand"),
        RopeColorPreset.Sage => L("Sage"),
        RopeColorPreset.Mist => L("Mist Blue"),
        RopeColorPreset.Rose => L("Dusty Rose"),
        _ => L("Custom"),
    };

    public static RGBColor? Rgb(this RopeColorPreset preset) => preset switch
    {
        RopeColorPreset.Silver => new(0.76, 0.78, 0.80),
        RopeColorPreset.Graphite => new(0.40, 0.43, 0.47),
        RopeColorPreset.Sand => new(0.77, 0.72, 0.64),
        RopeColorPreset.Sage => new(0.65, 0.73, 0.69),
        RopeColorPreset.Mist => new(0.64, 0.72, 0.79),
        RopeColorPreset.Rose => new(0.79, 0.68, 0.70),
        _ => null,
    };

    public static RopeColorPreset Matching(RGBColor rgb) => All.FirstOrDefault(p => p.Rgb() == rgb, RopeColorPreset.Custom);
}

/// Each template owns a four-colour palette and a supporting shelf-rail colour.
sealed record TemplatePalette
{
    static readonly RGBColor[] NativeColors =
        [new(0.96, 0.97, 0.98), new(0.98, 0.98, 0.98), new(0.96, 0.98, 0.97), new(0.99, 0.98, 0.96)];
    static readonly RGBColor NativeRail = new(0.87, 0.87, 0.87);

    [JsonPropertyName("colors")] public IReadOnlyList<RGBColor> Colors { get; init; } = NativeColors;
    [JsonPropertyName("rail")] public RGBColor Rail { get; init; } = NativeRail;

    public static readonly TemplatePalette Native = new(NativeColors, NativeRail);

    public TemplatePalette() { }

    public TemplatePalette(IReadOnlyList<RGBColor> colors, RGBColor rail)
    {
        Colors = colors;
        Rail = rail;
    }

    // Records compare lists by reference; palettes compare their colours.
    public bool Equals(TemplatePalette? other) =>
        other is not null && Rail == other.Rail && (Colors ?? []).SequenceEqual(other.Colors ?? []);

    public override int GetHashCode() => (Colors ?? []).Aggregate(Rail.GetHashCode(), (h, c) => HashCode.Combine(h, c));

    [JsonIgnore]
    public TemplatePalette Sanitized
    {
        get
        {
            var safe = (Colors ?? []).Take(4).Select(c => (c ?? new()).Sanitized).ToList();
            return new([.. safe, .. Native.Colors.Skip(safe.Count)], (Rail ?? Native.Rail).Sanitized);
        }
    }

    bool IsNativeColors => (Colors ?? []).SequenceEqual(Native.Colors);

    public Color Card(int index, bool dark, double selection = 0)
    {
        var theme = new MenuAppearance(dark);
        if (IsNativeColors) return theme.Card(index, selection);
        var list = Colors is { Count: > 0 } ? Colors : Native.Colors;
        var color = list[((index % list.Count) + list.Count) % list.Count].Color;
        var baseColor = dark ? Mix.Lerp(theme.Surface, color, 0.24) : color;
        return Mix.Lerp(baseColor, MenuAppearance.Accent, Math.Clamp(selection, 0, 1) * (dark ? 0.14 : 0.07));
    }

    public Color RailColor(bool dark)
    {
        if (Rail == Native.Rail) return new MenuAppearance(dark).Rail;
        return dark ? Mix.Lerp(new MenuAppearance(dark).Rail, Rail.Color, 0.28) : Rail.Color;
    }

    /// Chooses text by contrast, including custom near-black colours in light appearance.
    public Color Ink(int index, bool dark, double selection = 0)
    {
        var c = Card(index, dark, selection);
        static double linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double luminance = 0.2126 * linear(c.R / 255.0) + 0.7152 * linear(c.G / 255.0) + 0.0722 * linear(c.B / 255.0);
        if (luminance < 0.179) return System.Windows.Media.Colors.White;
        return luminance >= 0.221 ? Color.FromRgb(26, 26, 26) : System.Windows.Media.Colors.Black;
    }
}

enum ColorTemplate { Shelf, Notes }

static class ColorTemplates
{
    public static readonly ColorTemplate[] All = [ColorTemplate.Shelf, ColorTemplate.Notes];
    public static MenuStyle Style(this ColorTemplate t) => t == ColorTemplate.Shelf ? MenuStyle.Shelf : MenuStyle.Notes;
    public static string Title(this ColorTemplate t) => t.Style().Title();
}

sealed record TemplateColors
{
    [JsonPropertyName("shelf")] public TemplatePalette Shelf { get; init; } = TemplatePalette.Native;
    [JsonPropertyName("notes")] public TemplatePalette Notes { get; init; } = TemplatePalette.Native;

    public TemplatePalette this[ColorTemplate template] => template == ColorTemplate.Shelf ? Shelf : Notes;

    public TemplateColors With(ColorTemplate template, TemplatePalette palette) =>
        template == ColorTemplate.Shelf ? this with { Shelf = palette.Sanitized } : this with { Notes = palette.Sanitized };

    [JsonIgnore]
    public TemplateColors Sanitized => new() { Shelf = (Shelf ?? TemplatePalette.Native).Sanitized, Notes = (Notes ?? TemplatePalette.Native).Sanitized };
}

enum TemplateColorPreset { Native, Mist, Sage, Sand, Rose, Lavender, Custom }

static class TemplateColorPresets
{
    public static readonly TemplateColorPreset[] All =
        [TemplateColorPreset.Native, TemplateColorPreset.Mist, TemplateColorPreset.Sage, TemplateColorPreset.Sand,
         TemplateColorPreset.Rose, TemplateColorPreset.Lavender, TemplateColorPreset.Custom];

    public static string Title(this TemplateColorPreset preset) => preset switch
    {
        TemplateColorPreset.Native => L("Soft Neutrals"),
        TemplateColorPreset.Mist => L("Misty Coast"),
        TemplateColorPreset.Sage => L("Sage Garden"),
        TemplateColorPreset.Sand => L("Warm Linen"),
        TemplateColorPreset.Rose => L("Rose Clay"),
        TemplateColorPreset.Lavender => L("Lavender Haze"),
        _ => L("Custom"),
    };

    public static TemplatePalette? Palette(this TemplateColorPreset preset) => preset switch
    {
        TemplateColorPreset.Native => TemplatePalette.Native,
        TemplateColorPreset.Mist => new([new(0.83, 0.89, 0.93), new(0.89, 0.93, 0.95), new(0.84, 0.90, 0.90), new(0.91, 0.92, 0.94)], new(0.71, 0.77, 0.81)),
        TemplateColorPreset.Sage => new([new(0.84, 0.89, 0.83), new(0.91, 0.93, 0.87), new(0.85, 0.91, 0.88), new(0.92, 0.91, 0.85)], new(0.72, 0.77, 0.70)),
        TemplateColorPreset.Sand => new([new(0.94, 0.89, 0.80), new(0.96, 0.93, 0.87), new(0.90, 0.87, 0.81), new(0.93, 0.89, 0.85)], new(0.79, 0.73, 0.64)),
        TemplateColorPreset.Rose => new([new(0.93, 0.85, 0.85), new(0.95, 0.90, 0.88), new(0.90, 0.85, 0.87), new(0.93, 0.88, 0.82)], new(0.79, 0.70, 0.70)),
        TemplateColorPreset.Lavender => new([new(0.88, 0.85, 0.93), new(0.93, 0.90, 0.96), new(0.85, 0.88, 0.93), new(0.92, 0.88, 0.91)], new(0.74, 0.71, 0.80)),
        _ => null,
    };

    public static TemplateColorPreset Matching(TemplatePalette palette) =>
        All.FirstOrDefault(p => p.Palette() == palette, TemplateColorPreset.Custom);
}
