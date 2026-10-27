using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

public sealed record MacQrRow(string Name, string Text, string Group, QrEntry? Entry, string? Issue,string Season="",string Template="");
public sealed record MacQrImport(IReadOnlyList<MacQrRow> Rows)
{
    public QrEntry[] Compatible => Rows.Where(x => x.Entry is not null).Select(x => x.Entry!).ToArray();
}

// Explicit, one-way import of Mac's SavedQRCode array. Never reads Mac settings,
// scans directories, opens URLs or writes back to the selected source library.
public static class MacQrLibrary
{
    public const int MaxBytes = 262144;
    private const string Invalid = "Choose a valid Mac Swivel QR library JSON (256 KB, up to 128 entries).";
    public static MacQrImport Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new UserError(Invalid);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            var array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 128) throw new JsonException();
            var rows = new List<MacQrRow>(); var ids = new HashSet<Guid>();
            foreach (var item in array.EnumerateArray())
            {
                LibraryJson.Fields(item, new[] { "id", "name", "value" }, "collection", "season", "template", "created");
                if (!Guid.TryParseExact(LibraryJson.Text(item, "id"), "D", out var id) || !ids.Add(id)) throw new JsonException();
                string name = LibraryJson.Text(item, "name"), text = LibraryJson.Text(item, "value"), group = LibraryJson.OptionalText(item, "collection");
                if (string.IsNullOrWhiteSpace(name) || text.Length == 0 ||
                    new UTF8Encoding(false, true).GetByteCount(text) > 2000) throw new JsonException();
                string season=LibraryJson.OptionalText(item,"season"),template=LibraryJson.OptionalText(item,"template");
                foreach (string key in new[] { "season", "template" })
                    if (LibraryJson.OptionalText(item, key).Length > 64) throw new JsonException();
                if (item.TryGetProperty("created", out var created) && created.ValueKind != JsonValueKind.Null && !double.IsFinite(created.GetDouble())) throw new JsonException();
                QrEntry? entry = null; string? issue = null;
                try { entry = new(Guid.NewGuid().ToString("N"), name, text, group, QrCorrection.Medium,template.Length==0?QrGardenTemplate.Tree:QrGarden.Template(template),season.Length==0?QrGardenSeason.Summer:QrGarden.Season(season)); entry.Validate(); }
                catch (UserError error) { entry = null; issue = error.Message; }
                rows.Add(new(name, text, group, entry, issue,season,template));
            }
            // Mac collections may contain equal names; never choose one silently.
            var duplicates = rows.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new(rows.Select(x => duplicates.Contains(x.Name) ? x with { Entry = null, Issue = "Duplicate QR names must be renamed before importing into Windows." } : x).ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NullReferenceException)
        { throw new UserError(Invalid); }
    }
}
