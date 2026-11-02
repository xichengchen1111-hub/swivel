using System;
using System.IO;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class MacPresetExportChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var presets = new[] {
            new ImagePreset("11111111111111111111111111111111", "Web JPG", new("jpg", 1600, 80)),
            new ImagePreset("22222222222222222222222222222222", "PNG", new("png", 0, 100)),
            new ImagePreset("33333333333333333333333333333333", "TIFF", new("tiff", 64, 1)) };
        var plan = MacPresetExport.Prepare(presets);
        byte[] data = plan.Encode();
        check(data.SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "windows-presets-for-mac.json"))), "Mac export exact UTF8 bytes match golden Mac array shape");
        check(plan.Rows.Count == 3 && plan.Rows.All(row => row.Exported && !row.Kept && row.Issues.Count == 0), "Mac export previews all exact recipe values");
        var imported = PresetLibrary.Decode(data);
        check(imported.FromMac && imported.Compatible.Select(x => x.Recipe).SequenceEqual(presets.Select(x => x.Recipe)) && imported.Compatible.Select(x => x.Name).SequenceEqual(presets.Select(x => x.Name)), "Mac export existing importer roundtrip preserves every recipe value and name");
        check(MacPresetExport.Prepare(presets).Encode().SequenceEqual(data), "Mac export IDs and bytes stable across preparations");
        byte[] mutable = plan.Encode(); mutable[0] = 0;
        check(plan.Encode().SequenceEqual(data), "Mac export payload cannot be changed after preview");
        var rejected = presets.Select((preset, i) => preset with { Id = (i + 10).ToString("x32"), Name = "Refused " + i }).ToArray();
        var unsupported = new[] {
            rejected[0] with { Recipe = new("gif") }, rejected[1] with { Recipe = new("bmp") },
            rejected[2] with { Recipe = new("png", 16) },
            new ImagePreset("44444444444444444444444444444444", "Auto", new("jpg", AutoPerImage: true)),
            new ImagePreset("55555555555555555555555555555555", "Photo", new("jpg", Adjustments: new PhotoSettings())),
            presets[1] };
        var partial = MacPresetExport.Prepare(unsupported);
        check(partial.Count == 1 && partial.Rows.Count == 6 && partial.Rows.Take(5).All(row => !row.Exported) && partial.Rows.Last().Exported, "Mac export refuses GIF/BMP/short edge/Auto/even identity photo settings without losing compatible row");
        foreach (var row in partial.Rows)
            foreach (string issue in row.Issues) check(Strings.Table.ContainsKey(issue), "Mac export refusal reason has all translations");
        check(PresetLibrary.Decode(partial.Encode()).Compatible.Single().Recipe == presets[1].Recipe, "Partial Mac export roundtrips without fallback or photo loss");
        reject(() => MacPresetExport.Prepare(unsupported[..5]).Encode(), "All refused cannot create an empty replacement");
        reject(() => MacPresetExport.Prepare(Array.Empty<ImagePreset>()).Encode(), "Empty Windows library cannot create replacement");
        byte[] mac = Encoding.UTF8.GetBytes("[{\"id\":\"old\",\"name\":\"Mac HEIC\",\"nameKey\":null,\"longEdge\":16384,\"format\":\"heic\",\"quality\":77,\"stripMetadata\":false}]");
        var merge = MacPresetExport.Prepare(presets, mac);
        string merged = Encoding.UTF8.GetString(merge.Encode());
        check(merge.Count == 4 && merge.Rows[0].Kept && merged.StartsWith("[" + Encoding.UTF8.GetString(mac)[1..^1] + ",", StringComparison.Ordinal), "Mac merge preserves raw HEIC/metadata/long edge/nameKey-null row unchanged and first");
        var conflicts = new[] { presets[0] with { Name = "mac heic" }, presets[1], presets[2] };
        var nameConflict = MacPresetExport.Prepare(conflicts, mac);
        check(!nameConflict.Rows[1].Exported && nameConflict.Rows[1].Issues.Contains(MacPresetExport.Conflict) && nameConflict.Count == 3, "Mac merge case-insensitive name conflict omitted per row, other rows continue");
        byte[] idMac = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(mac).Replace("\"old\"", "\"" + presets[1].Id + "\""));
        check(MacPresetExport.Prepare(presets, idMac).Rows[2].Issues.Contains(MacPresetExport.Conflict), "Mac merge ID conflict omitted without generating a new ID");
        var full = Enumerable.Range(1, 24).Select(i => presets[1] with { Id = i.ToString("x32"), Name = "Row " + i }).ToArray();
        var fullPlan = MacPresetExport.Prepare(full, mac);
        check(fullPlan.Count == 24 && fullPlan.Rows.Last().Issues.Contains(MacPresetExport.Limit), "Mac merge capacity refuses last addition, never drops kept row");
        check(PresetLibrary.Decode(MacPresetExport.Prepare(full).Encode()).Compatible.Length == 24, "24 compatible Mac exports fit budget and roundtrip");
        foreach (string malformed in new[] { "{}", "null", "[null]", "[{}]", "[" + Encoding.UTF8.GetString(mac)[1..^1] + "," + Encoding.UTF8.GetString(mac)[1..^1] + "]",
            Encoding.UTF8.GetString(mac).Replace("\"old\"", "\"\""), Encoding.UTF8.GetString(mac).Replace("16384", "63"),
            Encoding.UTF8.GetString(mac).Replace("77", "101"), Encoding.UTF8.GetString(mac).Replace("heic", "gif"),
            Encoding.UTF8.GetString(mac).Replace("\"nameKey\":null", "\"nameKey\":7"), Encoding.UTF8.GetString(mac).Replace("\"quality\":77", "\"quality\":77,\"quality\":77"),
            Encoding.UTF8.GetString(mac).Replace("Mac HEIC", new string('x',61)), Encoding.UTF8.GetString(mac).Replace("Mac HEIC", "   "),
            Encoding.UTF8.GetString(mac).Replace("\"nameKey\":null", "\"unknown\":null") })
            reject(() => MacPresetExport.Prepare(presets, Encoding.UTF8.GetBytes(malformed)), "Invalid optional Mac list refused whole; no kept row silently filtered");
        reject(() => MacPresetExport.Prepare(presets, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(mac).Replace("\"old\"", "\"" + new string('i',129) + "\""))), "Mac merge refuses ID beyond importer 128-unit bound");
        string idBoundary = Encoding.UTF8.GetString(mac).Replace("\"old\"", "\"" + new string('i',128) + "\"");
        check(MacPresetExport.Prepare(presets, Encoding.UTF8.GetBytes(idBoundary)).Count == 4, "Mac merge accepts exact 128-unit ID bound");
        reject(() => MacPresetExport.Prepare(presets, new byte[65537]), "Mac merge source byte bound checked first");
        reject(() => MacPresetExport.Prepare(presets, new byte[] {0xff}), "Mac merge invalid UTF8 refused");
        byte[] boundary = Enumerable.Repeat((byte)' ', 65536).ToArray(); mac.CopyTo(boundary, 0);
        check(MacPresetExport.Prepare(presets, boundary).Count == 4, "Mac merge accepts exact 64KB bound");
        string sixty = Encoding.UTF8.GetString(mac).Replace("Mac HEIC", new string('x',60));
        check(MacPresetExport.Prepare(presets, Encoding.UTF8.GetBytes(sixty)).Count == 4, "Mac merge accepts exact conservative 60-unit name bound");
        string unicode = Encoding.UTF8.GetString(mac).Replace("Mac HEIC", "Café");
        var equivalent = MacPresetExport.Prepare(new[] { presets[1] with { Name = "Cafe\u0301" } }, Encoding.UTF8.GetBytes(unicode));
        check(equivalent.Count == 1 && equivalent.Rows[1].Issues.Contains(MacPresetExport.Conflict), "Mac merge catches canonically equivalent names using Swift-compatible normalization");
        check(MacPresetExport.Command == "defaults write local.swivel.app imagePresets.v1 -data \"$(xxd -p -c 0 ./swivel-mac-presets.json)\"" && !MacPresetExport.Command.Contains(presets[0].Name), "Mac install command fixed, quoted and independent of user data");
        var unicodePreset = presets[1] with { Name = "图片 🦜 e\u0301" };
        check(PresetLibrary.Decode(MacPresetExport.Prepare(new[] { unicodePreset }).Encode()).Compatible.Single().Name == unicodePreset.Name, "Mac export Unicode scalar and decomposed name roundtrips unchanged");
        check(MacPresetExport.Prepare(new[] { presets[1] with { Name = "\ud800" } }).Rows.Single().Issues.Contains(MacPresetExport.Name), "Mac export refuses unpaired surrogate rather than silently replacing name");
        foreach (string format in new[] { "png", "jpg", "tiff" })
            foreach (int edge in new[] { 0, 64, 8192 })
                foreach (int quality in Enumerable.Range(1,100))
                {
                    var sample = presets[1] with { Recipe = new(format, edge, quality) };
                    check(PresetLibrary.Decode(MacPresetExport.Prepare(new[] { sample }).Encode()).Compatible.Single().Recipe == sample.Recipe, "Mac export all quality values and size/format boundaries roundtrip");
                }
        foreach (var note in new[] { MacPresetExport.Scope, MacPresetExport.Replace, MacPresetExport.Equivalence, MacPresetExport.Bounds, MacPresetExport.InstallStart, MacPresetExport.InstallEnd, MacPresetExport.Name, MacPresetExport.Invalid, MacPresetExport.Empty, MacPresetExport.Limit, MacPresetExport.Conflict })
            check(Strings.Table.ContainsKey(note) && Strings.Table[note].Length == 3, "Mac export note/error has all three translations");
        var prefs = new Prefs();
        var action = Catalog.Actions.Single(x => x.Id == "presets.exportMac");
        check(!prefs.IsOn(Feature.ImagePresets) && !action.RequiresFiles && action.Feature == Feature.ImagePresets, "Mac export catalog action needs no files and uses existing default-off switch");
        string settingsBefore = prefs.Serialize(); _ = MacPresetExport.Prepare(presets, mac);
        check(prefs.Serialize() == settingsBefore, "Mac export never writes preferences or feature/menu choices");
        presets[0] = presets[0] with { Name = "Later edit" };
        check(plan.Encode().SequenceEqual(data), "Mac export freezes source collection before review");
    }
}
