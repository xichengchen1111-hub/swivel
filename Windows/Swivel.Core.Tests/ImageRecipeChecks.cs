using System;
using System.Linq;
using Swivel.Core;

internal static class ImageRecipeChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var migrated = Prefs.Parse("{\"Version\":1,\"Language\":\"ja\",\"Enabled\":[\"Shelf\",\"QuickActions\"]}");
        check(migrated.Language == "ja" && migrated.IsOn(Feature.Shelf) && migrated.IsOn(Feature.QuickActions) && !migrated.IsOn(Feature.Wheel) && !migrated.IsOn(Feature.ImagePresets) && migrated.Presets.Count == 0, "v1 migration preserves choices, new tool off");
        string id = Guid.NewGuid().ToString("N");
        var initial = new ImagePreset(id, "照片 / Preview", new ImageRecipe("jpg", 1920, 77));
        migrated.SavePreset(initial);
        check(!Catalog.Offered(migrated).Any(a => a.Id.StartsWith("preset.")), "Disabled presets stay hidden despite saved recipes");
        migrated.Set(Feature.ImagePresets, true);
        var action = Catalog.Available(migrated, new[] { "first.png", "second.jpg" }).Single(a => a.Id == "preset." + id);
        check(action.Feature == Feature.ImagePresets && action.ImageOnly, "Preset independently offered for image batches");
        migrated.SavePreset(initial with { Name = "Clear", Recipe = new ImageRecipe("png") });
        var restored = Prefs.Parse(migrated.Serialize());
        check(restored.Presets.Single().Id == id && restored.Presets.Single().Recipe == new ImageRecipe("png") && restored.Presets.Single().Name == "Clear", "Rename/edit/reload preserves stable ID and recipe");
        check(Catalog.DisplayTitle(Catalog.Offered(restored).Single(a => a.Id == action.Id), "zh-Hans").EndsWith("Clear"), "User preset name is literal, not translated as a built-in string");
        reject(() => restored.SavePreset(initial with { Id = Guid.NewGuid().ToString("N"), Name = "clear" }), "Duplicate name rejected ignoring case");
        reject(() => Prefs.Parse(restored.Serialize().Replace("\"Presets\":[", "\"Presets\":[" + System.Text.Json.JsonSerializer.Serialize(restored.Presets[0]) + ",")), "Duplicate saved ID rejected");
        restored.RemovePreset(id);
        check(!Catalog.Offered(restored).Any(a => a.Id == action.Id), "Deleted action no longer resolves");
        check(!Catalog.Available(migrated, new[] { "a.png", "a.txt" }).Any(a => a.Id.StartsWith("preset.")), "Mixed batch excludes preset");
        foreach (string format in ImageRecipe.Formats) new ImageRecipe(format, 0, 100).Validate();
        var photographic = new PhotoSettings(Exposure:.5,Contrast:.2,Saturation:-.3,Sharpen:.4,Look:PhotoLook.Film,LookStrength:.65);
        var photoPreset = new ImagePreset(Guid.NewGuid().ToString("N"),"Photo values",new("png",1200,85,photographic)); migrated.SavePreset(photoPreset);
        var photoReloaded = Prefs.Parse(migrated.Serialize()).Presets.Single(x => x.Id == photoPreset.Id);
        check(photoReloaded.Recipe.Adjustments == photographic,"All fixed photo recipe values and look strength round-trip");
        var old = Prefs.Parse("{\"Version\":2,\"Language\":\"en\",\"Enabled\":[],\"Presets\":[{\"Id\":\"00000000000000000000000000000001\",\"Name\":\"Old\",\"Recipe\":{\"Format\":\"jpg\",\"LongEdge\":1920,\"Quality\":85}}]}");
        check(old.Presets[0].Recipe.Adjustments is null,"Older recipes acquire no unrequested photo changes");
        reject(() => new ImageRecipe("png",Adjustments:new(Exposure:double.NaN)).Validate(),"Photo recipe refuses nonfinite values");
        reject(() => new ImageRecipe("png",Adjustments:new(Look:(PhotoLook)99)).Validate(),"Photo recipe refuses unknown look");
        foreach (var invalid in new[] { new ImageRecipe("heic"), new ImageRecipe("../jpg"), new ImageRecipe("png", -1), new ImageRecipe("jpg", 15), new ImageRecipe("jpg", 8193), new ImageRecipe("jpg", 0, 0), new ImageRecipe("jpg", 0, 101) })
            reject(invalid.Validate, "Unsafe recipe rejected: " + invalid);
        foreach (string name in new[] { "", "  ", " name", "name ", new string('n', 49), "name\nsecond", "a\u202eb" })
            reject(() => new ImagePreset(id, name, new ImageRecipe("png")).Validate(), "Unsafe preset name rejected");
        reject(() => new ImagePreset("../bad", "Name", new ImageRecipe("png")).Validate(), "Preset ID is never a path");
        for (int i = 0; i < 24; i++) restored.SavePreset(new(Guid.NewGuid().ToString("N"), "Recipe " + i, new ImageRecipe("png")));
        check(Prefs.Parse(restored.Serialize()).Presets.Count == 24, "Maximum preset collection round trips");
        reject(() => restored.SavePreset(initial), "Preset limit enforced");
        check(Catalog.Available(migrated, new[] { "a.png" }).Any(a => a.Id == "image.crop") && !Catalog.Available(migrated, new[] { "a.png", "b.png" }).Any(a => a.Id == "image.crop"), "Crop limited to exactly one supplied image");
        check(Catalog.Available(migrated, new[] { "a.png", "b.jpg" }).Any(a => a.Id == "image.compress"), "JPEG compression supports batches");
        check(PixelCrop.Center(4000, 3000, 1) == new PixelCrop(500, 0, 3000, 3000), "Centered square on landscape");
        check(PixelCrop.Drag(100, 80, 90, 70, 10, 5) == new PixelCrop(10, 5, 80, 65), "Reverse drag has correct origin");
        check(PixelCrop.Drag(100, 80, 10, 5, 500, 500) == new PixelCrop(10, 5, 90, 75), "Outside drag clamps to image");
        check(PixelCrop.Drag(100, 80, 99, 79, 99, 79) == new PixelCrop(99, 79, 1, 1), "Bottom-right click selects one pixel");
        foreach (var rect in new[] { new PixelCrop(-1, 0, 1, 1), new PixelCrop(0, 0, 0, 1), new PixelCrop(90, 70, 11, 10), new PixelCrop(int.MaxValue, 1, int.MaxValue, 1) })
            reject(() => rect.Validate(100, 80), "Invalid/overflow crop rejected");
        reject(() => PixelCrop.Drag(100, 80, double.NaN, 0, 1, 1), "Nonfinite crop rejected");
        reject(() => PixelCrop.Center(100, 80, double.PositiveInfinity), "Infinite ratio rejected");
        var random = new Random(1023);
        foreach (var size in new[] { (1, 1), (1, 16384), (16384, 1), (640, 480), (3000, 4000) })
        {
            bool valid = true;
            for (int i = 0; i < 200; i++)
            {
                double ratio = new[] { 0d, 1, 16d / 9, 9d / 16 }[i % 4];
                var crop = PixelCrop.Drag(size.Item1, size.Item2, random.Next(-100, size.Item1 + 100), random.Next(-100, size.Item2 + 100), random.Next(-100, size.Item1 + 100), random.Next(-100, size.Item2 + 100), ratio);
                valid &= crop.X >= 0 && crop.Y >= 0 && crop.Width >= 1 && crop.Height >= 1 && (long)crop.X + crop.Width <= size.Item1 && (long)crop.Y + crop.Height <= size.Item2;
            }
            check(valid, "All randomized/edge crop selections stay inside: " + size);
        }
    }
}
