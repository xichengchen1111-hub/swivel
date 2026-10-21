using Swivel;

namespace Swivel.Tests;

/// The 1.3 settings models (port of the model parts of the Mac InteractionSettingsTests).
[TestClass]
public sealed class InteractionModelTests
{
    [TestInitialize]
    public void Reset() => TestSetup.ResetPrefs();

    [TestMethod]
    public void TuningIsBoundedAndNeutralByDefault()
    {
        var tuning = new MotionTuning { Speed = 9, Bounce = -3 }.Sanitized;
        Assert.AreEqual(1.5, tuning.Speed);
        Assert.AreEqual(0, tuning.Bounce);
        Assert.AreEqual(0.75, new MotionTuning { Speed = 0.1 }.SafeSpeed);
        Assert.AreEqual(1, new MotionTuning { Speed = double.NaN }.SafeSpeed);
        // Bounce 1 keeps the original damping (floored at 0.6); bounce 0 is critically damped.
        Assert.AreEqual(0.8, MotionTuning.Default.Damping(0.8), 1e-9);
        Assert.AreEqual(0.6, MotionTuning.Default.Damping(0.4), 1e-9);
        Assert.AreEqual(1, new MotionTuning { Bounce = 0 }.Damping(0.7), 1e-9);
    }

    [TestMethod]
    public void DefaultTuningKeepsTheOriginalSpring()
    {
        var a = new Spring(0, 0.3, 0.7) { Target = 1 };
        var b = a;
        for (int i = 0; i < 30; i++) { a.Step(1 / 60.0); b.Step(1 / 60.0, MotionTuning.Default); }
        Assert.AreEqual(a.Value, b.Value, 1e-12);
        // Faster speed settles sooner.
        var fast = new Spring(0, 0.3, 1) { Target = 1 };
        var slow = fast;
        for (int i = 0; i < 12; i++) { fast.Step(1 / 60.0, new MotionTuning { Speed = 1.5 }); slow.Step(1 / 60.0); }
        Assert.IsGreaterThan(slow.Value, fast.Value);
    }

    [TestMethod]
    public void PresetsAreRecognisedAndKeepTextChoices()
    {
        var settings = new InteractionSettings { TextSize = TextSize.Large, BoldText = true };
        Assert.AreEqual(FeelPreset.Original, settings.MatchingPreset);
        foreach (var preset in FeelChoices.Presets)
        {
            var applied = settings.Applying(preset);
            Assert.AreEqual(preset, applied.MatchingPreset);
            Assert.AreEqual(TextSize.Large, applied.TextSize);
            Assert.IsTrue(applied.BoldText);
        }
        var custom = settings.With(FeelArea.Menu, new MotionTuning { Speed = 1.25 });
        Assert.IsNull(custom.MatchingPreset);
        Assert.IsFalse(settings.Applying(FeelPreset.Quiet).HoverSound);
        Assert.AreEqual(0.25, settings.Applying(FeelPreset.Quiet).Volume);
    }

    [TestMethod]
    public void SettingsPersistInTheMacShape()
    {
        Prefs.Interaction = new InteractionSettings().Applying(FeelPreset.Snappy) with { TextSize = TextSize.ExtraLarge };
        Prefs.ClotheslineColors = new ClotheslineColors { Rope = RopeColorPreset.Sage.Rgb()!, Peg = RopeColorPreset.Sand.Rgb()! };
        Prefs.TemplateColors = new TemplateColors().With(ColorTemplate.Notes, TemplateColorPreset.Lavender.Palette()!);
        var json = File.ReadAllText(Prefs.FilePath);
        StringAssert.Contains(json, "\"textSize\": \"extraLarge\"");
        StringAssert.Contains(json, "\"soundStyle\": \"classic\"");
        Prefs.Reload();
        Assert.AreEqual(FeelPreset.Snappy, (Prefs.Interaction with { TextSize = TextSize.Standard }).MatchingPreset);
        Prefs.Interaction = Prefs.Interaction with { Volume = 4 };
        Assert.AreEqual(1, Prefs.Interaction.Volume);
        Assert.AreEqual(TextSize.ExtraLarge, Prefs.Interaction.TextSize);
        Assert.AreEqual(RopeColorPreset.Sage, RopeColorPresets.Matching(Prefs.ClotheslineColors.Rope));
        Assert.AreEqual(RopeColorPreset.Sand, RopeColorPresets.Matching(Prefs.ClotheslineColors.Peg));
        Assert.AreEqual(TemplateColorPreset.Lavender, TemplateColorPresets.Matching(Prefs.TemplateColors.Notes));
        Assert.AreEqual(TemplateColorPreset.Native, TemplateColorPresets.Matching(Prefs.TemplateColors.Shelf));
    }

    [TestMethod]
    public void PalettesFillMissingColoursAndKeepTextReadable()
    {
        var partial = new TemplatePalette([new RGBColor(0.05, 0.05, 0.05)], new RGBColor(2, -1, 0.5)).Sanitized;
        Assert.HasCount(4, partial.Colors);
        Assert.AreEqual(new RGBColor(1, 0, 0.5), partial.Rail);
        foreach (var preset in TemplateColorPresets.All.Where(p => p != TemplateColorPreset.Custom))
        foreach (var dark in new[] { false, true })
        for (int i = 0; i < 4; i++)
        {
            var palette = preset.Palette()!;
            Assert.IsGreaterThanOrEqualTo(4.5, Contrast(palette.Card(i, dark), palette.Ink(i, dark)), $"{preset} {dark} {i}");
        }
        // A near-black custom card gets white text in light appearance.
        Assert.AreEqual(System.Windows.Media.Colors.White, partial.Ink(0, dark: false));
    }

    static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        static double lum(System.Windows.Media.Color c)
        {
            static double lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * lin(c.R / 255.0) + 0.7152 * lin(c.G / 255.0) + 0.0722 * lin(c.B / 255.0);
        }
        double x = lum(a), y = lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
