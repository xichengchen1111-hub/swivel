using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

public sealed record MacPresetExportRow(string Id, string Name, string Description, bool Kept, IReadOnlyList<string> Issues)
{
    public bool Exported => Issues.Count == 0;
}

public sealed class MacPresetExportPlan
{
    private readonly byte[] data;
    public IReadOnlyList<MacPresetExportRow> Rows { get; }
    public int Count { get; }
    internal MacPresetExportPlan(MacPresetExportRow[] rows, byte[] data, int count)
    { Rows = Array.AsReadOnly(rows); this.data = data; Count = count; }
    public byte[] Encode()
    {
        if (Count == 0) throw new UserError(MacPresetExport.Empty);
        return (byte[])data.Clone();
    }
}

// Mac ImagePreset / Prefs.imagePresets: a Data-backed JSON array, never a Windows backup wrapper.
public static class MacPresetExport
{
    public const string Title = "Export for Mac…";
    public const string Scope = "Review every row before exporting. This writes a Mac image preset JSON array, not a Windows backup. Windows settings and feature switches are unchanged. Only the rows marked for export will be written, with stable IDs.";
    public const string Replace = "Installing this file REPLACES ALL image presets on the Mac. To keep the current Mac list, first export its imagePresets.v1 Data as the same JSON array and choose that file here. Kept Mac rows stay unchanged and first; conflicting Windows rows are not added. An omitted Mac list means only the compatible Windows rows will remain after installation.";
    public const string Equivalence = "Matching recipe values do not promise identical images: codecs, JPEG quality, resizing, color and input/pixel limits can differ. Windows removes metadata, so new Mac rows use stripMetadata=true. JPG maps to Mac jpeg. Photo adjustments and Auto, BMP, GIF and GIF post-processing limits cannot be transferred.";
    public const string Bounds = "Export and optional Mac lists are limited to 64 KB and 24 total presets. Names must be non-blank and at most 60 UTF-16 units, a conservative bound for Swift's character count. Mac IDs must be non-blank and at most 128 UTF-16 units. IDs and names must be unique, including equivalent Unicode spellings; name conflicts ignore case.";
    public const string InstallStart = "1. Quit Swivel on the Mac. Keep a backup of the Mac's current imagePresets.v1 Data before replacing it.\n2. Transfer the exported JSON to the Mac and name it swivel-mac-presets.json. In Terminal, change to the folder containing that file.\n3. Run this fixed command. It REPLACES ALL Mac image presets:";
    public const string Command = "defaults write local.swivel.app imagePresets.v1 -data \"$(xxd -p -c 0 ./swivel-mac-presets.json)\"";
    public const string InstallEnd = "4. Reopen Swivel and check Image Presets. Mac menu choices are not merged or rewritten. Swivel on Windows never executes this command.";
    public const string Invalid = "Choose a valid Mac image preset JSON array (64 KB, up to 24 unique presets, names up to 60 UTF-16 units). Nothing was exported.";
    public const string Empty = "No presets can be exported. An empty replacement file will not be written.";
    public const string Format = "Mac supports JPEG, PNG, HEIC and TIFF presets. This Windows format is not exported.";
    public const string Photo = "Mac image presets cannot store photo adjustments or per-image Auto. This recipe is not exported.";
    public const string Edge = "Mac requires a long edge of 0 or 64–16384 pixels. This recipe is not exported.";
    public const string Name = "The name contains invalid Unicode and cannot be transferred without changing it. This row is not exported.";
    public const string Conflict = "The ID or case-insensitive name conflicts with a kept Mac row or an earlier Windows row. This row is not added.";
    public const string Limit = "Adding this row would exceed the 24-preset or 64 KB export limit. This row is not added.";

    private static string Key(string text) => text.Normalize(NormalizationForm.FormC);
    private static byte[] ArrayBytes(IReadOnlyList<JsonElement> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items) writer.WriteRawValue(item.GetRawText());
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }
    public static MacPresetExportPlan Prepare(IReadOnlyList<ImagePreset> presets, ReadOnlyMemory<byte>? currentMac = null)
    {
        var snapshot = presets.ToArray();
        _ = PresetLibrary.Encode(snapshot); // Reuse Windows validation; do not reinterpret unknown fields.
        var rows = new List<MacPresetExportRow>(); var items = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentMac is { } source)
        {
            if (source.Length > PresetLibrary.MaxBytes) throw new UserError(Invalid);
            try
            {
                using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 6 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 24) throw new JsonException();
                foreach (var item in root.EnumerateArray())
                {
                    LibraryJson.Fields(item, new[] { "id", "name", "longEdge", "format", "quality", "stripMetadata" }, "nameKey");
                    string id = LibraryJson.Text(item, "id"), name = LibraryJson.Text(item, "name"), format = LibraryJson.Text(item, "format");
                    _ = LibraryJson.OptionalText(item, "nameKey");
                    int edge = item.GetProperty("longEdge").GetInt32(), quality = item.GetProperty("quality").GetInt32();
                    bool strip = item.GetProperty("stripMetadata").GetBoolean();
                    // UTF-16 length is an upper bound on Swift grapheme count, independent of Unicode version.
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || string.IsNullOrWhiteSpace(name) || name.Length > 60 ||
                        !ids.Add(Key(id)) || !names.Add(Key(name)) || (edge != 0 && edge is < 64 or > 16384) ||
                        quality is < 1 or > 100 || format is not ("jpeg" or "png" or "heic" or "tiff")) throw new JsonException();
                    items.Add(item.Clone());
                    rows.Add(new(id, name, $"{format.ToUpperInvariant()} · {edge} px · {quality}% · stripMetadata={strip.ToString().ToLowerInvariant()}", true, Array.Empty<string>()));
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            { throw new UserError(Invalid); }
        }
        foreach (var preset in snapshot)
        {
            var recipe = preset.Recipe; var issues = new List<string>();
            if (recipe.Format is not ("jpg" or "png" or "tiff")) issues.Add(Format);
            if (recipe.Adjustments is not null || recipe.AutoPerImage) issues.Add(Photo);
            if (recipe.LongEdge != 0 && recipe.LongEdge < 64) issues.Add(Edge);
            string nameKey;
            try { nameKey = Key(preset.Name); }
            catch (ArgumentException) { nameKey = ""; issues.Add(Name); }
            if (ids.Contains(Key(preset.Id)) || names.Contains(nameKey)) issues.Add(Conflict);
            if (issues.Count == 0)
            {
                var item = JsonSerializer.SerializeToElement(new { id = preset.Id, name = preset.Name, longEdge = recipe.LongEdge,
                    format = recipe.Format == "jpg" ? "jpeg" : recipe.Format, quality = recipe.Quality, stripMetadata = true });
                items.Add(item);
                if (items.Count > 24 || ArrayBytes(items).Length > PresetLibrary.MaxBytes)
                { items.RemoveAt(items.Count - 1); issues.Add(Limit); }
                else { ids.Add(Key(preset.Id)); names.Add(nameKey); }
            }
            rows.Add(new(preset.Id, preset.Name, PresetLibrary.Describe(recipe), false, issues.AsReadOnly()));
        }
        byte[] data = ArrayBytes(items);
        if (data.Length > PresetLibrary.MaxBytes) throw new UserError(Invalid);
        return new(rows.ToArray(), data, items.Count);
    }
}
