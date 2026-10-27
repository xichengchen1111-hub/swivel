using System;
using System.Linq;
using System.IO;
using System.Text;
using Swivel.Core;

internal static class PresetLibraryChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var settings = new PhotoSettings(Exposure: 1.25, Contrast: -.3, Highlights: -.4, Shadows: .2, Whites: .1, Blacks: -.2,
            Saturation: .3, Vibrance: -.1, Warmth: .4, Tint: -.3, Clarity: .2, Sharpen: .8, Noise: .6, Vignette: .2, Look: PhotoLook.Film, LookStrength: .45);
        var original = new ImagePreset(Guid.NewGuid().ToString("N"), "照片 🦜", new("jpg", 1200, 92, settings, true));
        byte[] data = PresetLibrary.Encode(new[] { original });
        var decoded = PresetLibrary.Decode(data);
        check(!decoded.FromMac && decoded.Compatible.Single().Recipe == original.Recipe && decoded.Compatible[0].Name == original.Name, "Preset backup preserves all photo/Auto/Unicode values exactly");
        check(decoded.Compatible[0].Id != original.Id && !Encoding.UTF8.GetString(data).Contains(original.Id), "Portable preset backup excludes IDs and creates fresh local IDs");
        var prefs = new Prefs(); int events = 0; prefs.Changed += () => events++;
        prefs.ImportPresets(decoded.Compatible);
        check(events == 1 && !prefs.IsOn(Feature.ImagePresets) && prefs.Presets.Count == 1, "Preset import changes once and never enables tool");
        prefs.SetCustomActions(new[] { "preset." + prefs.Presets[0].Id }); int beforeEvents = events;
        string before = prefs.Serialize(); prefs.ImportPresets(PresetLibrary.Decode(data).Compatible);
        check(prefs.Serialize() == before && events == beforeEvents, "Identical re-import keeps existing ID, custom action and all preferences");
        var other = original with { Id = Guid.NewGuid().ToString("N"), Name = "Other" };
        var conflict = original with { Id = Guid.NewGuid().ToString("N"), Recipe = original.Recipe with { Quality = 80 } };
        reject(() => prefs.ImportPresets(new[] { other, conflict }), "Conflicting preset blocks whole merge");
        check(prefs.Serialize() == before && events == beforeEvents, "Conflict after earlier valid item cannot partially mutate preferences");
        var full = Enumerable.Range(0, 24).Select(i => original with { Id = Guid.NewGuid().ToString("N"), Name = "Preset " + i }).ToArray();
        check(PresetLibrary.Decode(PresetLibrary.Encode(full)).Compatible.Length == 24, "24 full photo recipes fit portable budget");
        reject(() => PresetLibrary.Merge(full, new[] { other }), "Merge capacity enforced");
        check(PresetLibrary.Decode(PresetLibrary.Encode(Array.Empty<ImagePreset>())).Compatible.Length == 0, "Empty backup inert");
        string json = Encoding.UTF8.GetString(data);
        foreach (string invalid in new[] { "null", "{}", json.Replace("Swivel.ImagePresets", "Other"), json.Replace("\"Version\": 1", "\"Version\": 2"),
            json.Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1"), json.Replace("\"Format\": \"jpg\"", "\"Format\": \"jpg\", \"Run\": \"evil\""),
            json.Replace("\"Exposure\": 1.25", "\"Exposure\": 1.25, \"Exposure\": 0"), json.Replace("\"Exposure\": 1.25,", ""),
            json.Replace("\"Recipe\": {", "\"Recipe\": null, \"Extra\": {"), json[..^1] })
            reject(() => PresetLibrary.Decode(Encoding.UTF8.GetBytes(invalid)), "Malformed, missing, duplicate or unknown backup fields refused");
        reject(() => PresetLibrary.Decode(new byte[65537]), "Preset byte budget before parsing");
        reject(() => PresetLibrary.Decode(new byte[] { 0xc0, 0xaf }), "Invalid UTF8 refused");
        byte[] boundary = Enumerable.Repeat((byte)' ', 65536).ToArray(); data.CopyTo(boundary, 0);
        check(PresetLibrary.Decode(boundary).Compatible.Length == 1, "Exactly 64KB accepted");
        string Mac(string id, string name, string format = "jpeg", bool strip = true, int edge = 1600) =>
            $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"nameKey\":null,\"longEdge\":{edge},\"format\":\"{format}\",\"quality\":80,\"stripMetadata\":{strip.ToString().ToLowerInvariant()}}}";
        var mac = PresetLibrary.Decode(Encoding.UTF8.GetBytes("[" + string.Join(',', new[] {
            Mac("web", "Web JPG"), Mac("heic", "HEIC", "heic"), Mac("keep", "Keep metadata", strip: false),
            Mac("large", "Large", edge: 12000), Mac("long", new string('x', 49)), Mac("png", "PNG", "png", edge: 0), Mac("tiff", "TIFF", "tiff") }) + "]"));
        check(mac.FromMac && mac.Rows.Count == 7 && mac.Compatible.Length == 3 && mac.Rows.Count(x => x.Issue is not null) == 4, "Mac supported and incompatible rows stay explicit without silent fallback");
        check(mac.Compatible[0].Recipe == new ImageRecipe("jpg", 1600, 80) && mac.Compatible[1].Recipe.LongEdge == 0 && mac.Compatible[2].Recipe.Format == "tiff", "Mac jpeg/PNG/TIFF map exactly and never enlarge");
        foreach (var issue in mac.Rows.Where(x => x.Issue is not null)) check(Strings.Table.ContainsKey(issue.Issue!), "Mac issue translated");
        reject(() => PresetLibrary.Decode(Encoding.UTF8.GetBytes("[" + Mac("x", "A") + "," + Mac("x", "B") + "]")), "Duplicate Mac IDs refused");
        reject(() => PresetLibrary.Decode(Encoding.UTF8.GetBytes("[" + Mac("x", "A").Replace("\"format\":\"jpeg\"", "\"format\":\"png\",\"format\":\"jpeg\"") + "]")), "Duplicate Mac fields refused");
        string longMacName = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 60));
        var longNameImport = PresetLibrary.Decode(Encoding.UTF8.GetBytes("[" + Mac("long-name", longMacName) + "," + Mac("valid", "Other") + "]"));
        check(longNameImport.Rows[0].Name == longMacName && longNameImport.Rows[0].Issue is not null && longNameImport.Compatible.Length == 1, "Mac grapheme-long name stays a per-row issue, not a whole-file failure");
        var swift = PresetLibrary.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mac-image-presets.json")));
        check(swift.FromMac && swift.Rows.Count == 4 && swift.Compatible.Length == 2 && swift.Compatible[1].Name == "图片 🦜", "Actual Swift JSONEncoder fixture accepts optional nameKey omission and Unicode");
        check(LibraryJson.Visible("a\u202e\n") == "a[U+202E][U+000A]", "Imported preview exposes invisible controls without truncation");
    }
}
