using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swivel.Core;

public sealed record PresetImportRow(string Name, string Description, ImagePreset? Preset, string? Issue = null);
public sealed record PresetImport(IReadOnlyList<PresetImportRow> Rows, bool FromMac)
{
    public ImagePreset[] Compatible => Rows.Where(x => x.Preset is not null).Select(x => x.Preset!).ToArray();
}

public static class LibraryJson
{
    public static string Visible(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) result.Append($"[U+{(int)c:X4}]"); else result.Append(c);
        return result.ToString();
    }
    public static void Fields(JsonElement item, string[] required, params string[] optional)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
        string[] fields = item.EnumerateObject().Select(x => x.Name).ToArray();
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || required.Any(x => !fields.Contains(x)) ||
            fields.Any(x => !required.Contains(x) && !optional.Contains(x))) throw new JsonException();
    }
    public static string Text(JsonElement item, string key) => item.GetProperty(key).GetString() ?? throw new JsonException();
    public static string OptionalText(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetString() ?? throw new JsonException() : "";
}

public static class PresetLibrary
{
    public const int MaxBytes = 65536;
    private const string Invalid = "Choose a valid Swivel image preset JSON file (64 KB, up to 24 presets).";
    private sealed record Entry(string Name, ImageRecipe Recipe);
    private sealed record Backup(string Format, int Version, Entry[] Entries);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] PhotoFields = { "Exposure", "Contrast", "Highlights", "Shadows", "Whites", "Blacks", "Saturation", "Vibrance", "Warmth", "Tint", "Clarity", "Sharpen", "Noise", "Vignette", "Look", "LookStrength" };

    public static byte[] Encode(IReadOnlyList<ImagePreset> presets)
    {
        Validate(presets);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Backup("Swivel.ImagePresets", 1,
            presets.Select(x => new Entry(x.Name, x.Recipe)).ToArray()), Options);
        if (data.Length > MaxBytes) throw new UserError(Invalid);
        return data;
    }
    public static PresetImport Decode(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxBytes) throw new UserError(Invalid);
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return DecodeMac(root);
            LibraryJson.Fields(root, new[] { "Format", "Version", "Entries" });
            if (LibraryJson.Text(root, "Format") != "Swivel.ImagePresets" || root.GetProperty("Version").GetInt32() != 1) throw new JsonException();
            var entries = root.GetProperty("Entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 24) throw new JsonException();
            var presets = new List<ImagePreset>();
            foreach (var entry in entries.EnumerateArray())
            {
                LibraryJson.Fields(entry, new[] { "Name", "Recipe" });
                var recipe = entry.GetProperty("Recipe");
                LibraryJson.Fields(recipe, new[] { "Format", "LongEdge", "Quality", "Adjustments", "AutoPerImage" });
                if (recipe.GetProperty("Adjustments").ValueKind != JsonValueKind.Null)
                    LibraryJson.Fields(recipe.GetProperty("Adjustments"), PhotoFields);
                presets.Add(new(Guid.NewGuid().ToString("N"), LibraryJson.Text(entry, "Name"),
                    recipe.Deserialize<ImageRecipe>(Options) ?? throw new JsonException()));
            }
            Validate(presets);
            return new(presets.Select(x => new PresetImportRow(x.Name, Describe(x.Recipe), x)).ToArray(), false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NullReferenceException)
        { throw new UserError(Invalid); }
    }
    private static PresetImport DecodeMac(JsonElement array)
    {
        if (array.GetArrayLength() > 24) throw new JsonException();
        var rows = new List<PresetImportRow>(); var ids = new HashSet<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            LibraryJson.Fields(item, new[] { "id", "name", "longEdge", "format", "quality", "stripMetadata" }, "nameKey");
            string id = LibraryJson.Text(item, "id"), name = LibraryJson.Text(item, "name"), format = LibraryJson.Text(item, "format");
            _ = LibraryJson.OptionalText(item, "nameKey");
            int edge = item.GetProperty("longEdge").GetInt32(), quality = item.GetProperty("quality").GetInt32();
            bool strip = item.GetProperty("stripMetadata").GetBoolean();
            if (id.Length is < 1 or > 128 || !ids.Add(id) || !names.Add(name) || string.IsNullOrWhiteSpace(name) ||
                (edge != 0 && edge is < 64 or > 16384) || quality is < 1 or > 100 || format is not ("jpeg" or "png" or "heic" or "tiff")) throw new JsonException();
            string? issue = format == "heic" ? "HEIC presets need Windows codec validation." :
                !strip ? "Keeping source metadata is not supported by Windows image exports." :
                edge > 8192 ? "This long edge exceeds the Windows preset limit of 8192 pixels." : null;
            ImagePreset? preset = null;
            if (issue is null)
            {
                try { preset = new(Guid.NewGuid().ToString("N"), name, new(format == "jpeg" ? "jpg" : format, edge, quality)); preset.Validate(); }
                catch (UserError error) { preset = null; issue = error.Message; }
            }
            rows.Add(new(name, $"{format.ToUpperInvariant()} · {edge} px · {quality}%", preset, issue));
        }
        return new(rows.ToArray(), true);
    }
    public static string Describe(ImageRecipe recipe) => recipe.Format == "gif" ? $"GIF · {recipe.LongEdge} px" : $"{recipe.Format.ToUpperInvariant()} · {recipe.LongEdge} px · {recipe.Quality}%";
    public static ImagePreset[] Merge(IReadOnlyList<ImagePreset> current, IReadOnlyList<ImagePreset> incoming)
    {
        Validate(current); Validate(incoming); var result = current.ToList();
        foreach (var preset in incoming)
        {
            var existing = result.FirstOrDefault(x => x.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Recipe == preset.Recipe) continue;
                throw new UserError("A preset name already exists with different settings. Rename it before importing; nothing was changed.");
            }
            if (result.Any(x => x.Id == preset.Id)) throw new UserError(Invalid);
            result.Add(preset);
        }
        Validate(result); return result.ToArray();
    }
    private static void Validate(IReadOnlyList<ImagePreset> presets)
    {
        if (presets.Count > 24) throw new UserError("Save up to 24 image presets.");
        var ids = new HashSet<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            if (preset is null) throw new UserError(Invalid);
            preset.Validate(); if (!ids.Add(preset.Id) || !names.Add(preset.Name)) throw new UserError(Invalid);
        }
    }
}
