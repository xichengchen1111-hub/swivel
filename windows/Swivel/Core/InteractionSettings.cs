using System.Text.Json.Serialization;

namespace Swivel;

// Port of Sources/Swivel/InteractionSettings.swift (1.3.0 personal tuning). The JSON shape matches the Mac app's
// `interaction.v1` value. Typography for WPF lives in `AppTypography` below.

/// Bounded, independent controls. A value of 1 preserves the 1.2 motion; colour/opacity never overshoot.
sealed record MotionTuning
{
    [JsonPropertyName("speed")] public double Speed { get; init; } = 1;
    [JsonPropertyName("bounce")] public double Bounce { get; init; } = 1;

    [JsonIgnore] public double SafeSpeed => double.IsFinite(Speed) ? Math.Clamp(Speed, 0.75, 1.5) : 1;
    [JsonIgnore] public double SafeBounce => double.IsFinite(Bounce) ? Math.Clamp(Bounce, 0, 1) : 1;

    public double Damping(double original) => 1 - (1 - Math.Max(original, 0.6)) * SafeBounce;

    [JsonIgnore] public MotionTuning Sanitized => new() { Speed = SafeSpeed, Bounce = SafeBounce };

    public static readonly MotionTuning Default = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<FeelArea>))]
enum FeelArea { [JsonStringEnumMemberName("menu")] Menu, [JsonStringEnumMemberName("clothesline")] Clothesline,
    [JsonStringEnumMemberName("tools")] Tools }

[JsonConverter(typeof(JsonStringEnumConverter<SoundStyle>))]
enum SoundStyle { [JsonStringEnumMemberName("classic")] Classic, [JsonStringEnumMemberName("soft")] Soft,
    [JsonStringEnumMemberName("bright")] Bright }

[JsonConverter(typeof(JsonStringEnumConverter<TextSize>))]
enum TextSize { [JsonStringEnumMemberName("small")] Small, [JsonStringEnumMemberName("standard")] Standard,
    [JsonStringEnumMemberName("large")] Large, [JsonStringEnumMemberName("extraLarge")] ExtraLarge }

enum FeelPreset { Original, Calm, Snappy, Playful, Quiet }

static class FeelChoices
{
    public static readonly FeelArea[] Areas = [FeelArea.Menu, FeelArea.Clothesline, FeelArea.Tools];
    public static readonly SoundStyle[] SoundStyles = [SoundStyle.Classic, SoundStyle.Soft, SoundStyle.Bright];
    public static readonly TextSize[] TextSizes = [TextSize.Small, TextSize.Standard, TextSize.Large, TextSize.ExtraLarge];
    public static readonly FeelPreset[] Presets = [FeelPreset.Original, FeelPreset.Calm, FeelPreset.Snappy, FeelPreset.Playful, FeelPreset.Quiet];

    public static string Title(this FeelArea area) => area switch
    {
        FeelArea.Menu => L("Pop-up menus"),
        FeelArea.Clothesline => L("Clothesline"),
        _ => L("Tools & settings"),
    };

    public static string Title(this SoundStyle style) => style switch
    {
        SoundStyle.Classic => L("Classic clicks"),
        SoundStyle.Soft => L("Soft & low"),
        _ => L("Bright chimes"),
    };

    public static double Pitch(this SoundStyle style) => style == SoundStyle.Soft ? 0.72 : style == SoundStyle.Bright ? 1.3 : 1;
    public static double Gain(this SoundStyle style) => style == SoundStyle.Soft ? 0.65 : style == SoundStyle.Bright ? 0.85 : 1;

    public static string Title(this TextSize size) => size switch
    {
        TextSize.Small => L("Small"),
        TextSize.Standard => L("Standard"),
        TextSize.Large => L("Large"),
        _ => L("Extra Large"),
    };

    public static double Scale(this TextSize size) => size switch
    {
        TextSize.Small => 0.9,
        TextSize.Standard => 1,
        TextSize.Large => 1.15,
        _ => 1.3,
    };

    public static string Title(this FeelPreset preset) => preset switch
    {
        FeelPreset.Original => L("Original"),
        FeelPreset.Calm => L("Calm"),
        FeelPreset.Snappy => L("Snappy"),
        FeelPreset.Playful => L("Playful"),
        _ => L("Quiet"),
    };
}

sealed record InteractionSettings
{
    [JsonPropertyName("menu")] public MotionTuning Menu { get; init; } = new();
    [JsonPropertyName("clothesline")] public MotionTuning Clothesline { get; init; } = new();
    [JsonPropertyName("tools")] public MotionTuning Tools { get; init; } = new();
    [JsonPropertyName("soundStyle")] public SoundStyle SoundStyle { get; init; } = SoundStyle.Classic;
    [JsonPropertyName("volume")] public double Volume { get; init; } = 1;
    [JsonPropertyName("hoverSound")] public bool HoverSound { get; init; } = true;
    /// Kept for the shared settings shape; Windows has no trackpad haptics.
    [JsonPropertyName("gentleHaptics")] public bool GentleHaptics { get; init; }
    [JsonPropertyName("textSize")] public TextSize TextSize { get; init; } = TextSize.Standard;
    [JsonPropertyName("boldText")] public bool BoldText { get; init; }

    public MotionTuning this[FeelArea area] => area switch
    {
        FeelArea.Menu => Menu,
        FeelArea.Clothesline => Clothesline,
        _ => Tools,
    };

    public InteractionSettings With(FeelArea area, MotionTuning tuning) => area switch
    {
        FeelArea.Menu => this with { Menu = tuning.Sanitized },
        FeelArea.Clothesline => this with { Clothesline = tuning.Sanitized },
        _ => this with { Tools = tuning.Sanitized },
    };

    [JsonIgnore]
    public InteractionSettings Sanitized => this with
    {
        Menu = (Menu ?? new()).Sanitized,
        Clothesline = (Clothesline ?? new()).Sanitized,
        Tools = (Tools ?? new()).Sanitized,
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 1,
    };

    /// Presets change feel and sound, leaving reading preferences and the channel switches alone.
    public InteractionSettings Applying(FeelPreset preset)
    {
        var motion = preset switch
        {
            FeelPreset.Calm => new MotionTuning { Speed = 1, Bounce = 0 },
            FeelPreset.Snappy => new MotionTuning { Speed = 1.5, Bounce = 0.15 },
            FeelPreset.Playful => new MotionTuning { Speed = 1.15, Bounce = 1 },
            FeelPreset.Quiet => new MotionTuning { Speed = 1.15, Bounce = 0 },
            _ => new MotionTuning(),
        };
        return this with
        {
            Menu = motion, Clothesline = motion, Tools = motion,
            SoundStyle = preset is FeelPreset.Calm or FeelPreset.Quiet ? SoundStyle.Soft
                : preset == FeelPreset.Playful ? SoundStyle.Bright : SoundStyle.Classic,
            Volume = preset == FeelPreset.Quiet ? 0.25 : preset == FeelPreset.Calm ? 0.5 : preset == FeelPreset.Original ? 1 : 0.75,
            HoverSound = preset != FeelPreset.Quiet,
            GentleHaptics = preset is FeelPreset.Calm or FeelPreset.Quiet,
        };
    }

    [JsonIgnore]
    public FeelPreset? MatchingPreset =>
        FeelChoices.Presets.Cast<FeelPreset?>().FirstOrDefault(p => Applying(p!.Value) == this);
}

/// Text size and bold text for WPF windows. Unlike the Mac (which rescales each control in place), WPF inherits
/// FontSize/FontWeight down the visual tree, so a window applies them once at its root with `Apply(window)` and they
/// update live on `Prefs.Changed`. Custom-drawn menus scale their own fonts with `Size(...)` / `Weight(...)`.
/// Exported image lettering (Annotate) deliberately ignores these settings.
static class AppTypography
{
    /// Windows' standard UI text size (Segoe UI Variable / Segoe UI, 14 px in WPF's Fluent theme).
    public const double BaseSize = 14;

    public static double Scale => Prefs.Interaction.TextSize.Scale();

    public static double Size(double size) => size * Scale;

    public static System.Windows.FontWeight Weight(System.Windows.FontWeight weight) =>
        Prefs.Interaction.BoldText && weight.ToOpenTypeWeight() < 600 ? System.Windows.FontWeights.SemiBold : weight;

    /// Sets the window's root font size and weight now and whenever the settings change.
    public static void Apply(System.Windows.Window window)
    {
        void update()
        {
            window.FontSize = Size(BaseSize);
            window.FontWeight = Weight(System.Windows.FontWeights.Normal);
        }
        update();
        void changed() => window.Dispatcher.BeginInvoke(update);
        Prefs.Changed += changed;
        window.Closed += (_, _) => Prefs.Changed -= changed;
    }
}
