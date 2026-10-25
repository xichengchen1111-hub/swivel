using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swivel.Core;

public static class QrLibrary
{
    // Accommodate 32 maximum-length payloads even when JSON escapes every
    // character, together with full-length names and groups.
    public const int MaxBytes = 1048576;
    public const string Invalid = "Choose a Swivel QR library JSON file with up to 32 valid entries (1 MiB maximum).";
    private sealed record Entry(string Name,string Text,string Group,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Correction=null,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Template=null,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Season=null);
    private sealed record Backup(string Format,int Version,Entry[] Entries);
    public static byte[] Encode(IReadOnlyList<QrEntry> entries)
    {
        Validate(entries);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new Backup("Swivel.QR",1,entries.Select(x=>new Entry(x.Name,x.Text,x.Group,x.Correction==QrCorrection.Low?null:QrCode.Level(x.Correction),x.Template==QrGardenTemplate.Tree?null:QrGarden.Key(x.Template),x.Season==QrGardenSeason.Summer?null:QrGarden.Key(x.Season))).ToArray()),new JsonSerializerOptions { WriteIndented=true });
        if(bytes.Length>MaxBytes)throw new UserError(Invalid);return bytes;
    }
    public static QrEntry[] Decode(ReadOnlyMemory<byte> bytes)
    {
        if(bytes.Length>MaxBytes)throw new UserError(Invalid);
        try
        {
            // Closed, shallow portable schema. IDs, switches, paths and executable
            // configuration cannot be imported through this file.
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=4 });var root=document.RootElement;
            Properties(root,"Format","Version","Entries");
            if(root.GetProperty("Format").GetString()!="Swivel.QR" || root.GetProperty("Version").GetInt32()!=1)throw new UserError(Invalid);
            var array=root.GetProperty("Entries");if(array.ValueKind!=JsonValueKind.Array || array.GetArrayLength()>32)throw new UserError(Invalid);
            var result=new List<QrEntry>();
            foreach(var item in array.EnumerateArray())
            {
                EntryProperties(item);
                var correction=QrCorrection.Low;
                if(item.TryGetProperty("Correction",out var level))
                    correction=level.GetString() switch { "L"=>QrCorrection.Low,"M"=>QrCorrection.Medium,"Q"=>QrCorrection.Quartile,"H"=>QrCorrection.High,_=>throw new UserError(Invalid) };
                var template=item.TryGetProperty("Template",out var t)?QrGarden.Template(t.GetString()!):QrGardenTemplate.Tree;
                var season=item.TryGetProperty("Season",out var s)?QrGarden.Season(s.GetString()!):QrGardenSeason.Summer;
                result.Add(new(Guid.NewGuid().ToString("N"),item.GetProperty("Name").GetString()!,item.GetProperty("Text").GetString()!,item.GetProperty("Group").GetString()!,correction,template,season));
            }
            Validate(result);return result.ToArray();
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NullReferenceException)
        { throw new UserError(Invalid); }
    }
    private static void EntryProperties(JsonElement item)
    {
        if(item.ValueKind!=JsonValueKind.Object)throw new UserError(Invalid);
        var keys=item.EnumerateObject().Select(p=>p.Name).ToArray();
        if(keys.Length is <3 or >6 || keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length ||
            !new[]{"Name","Text","Group"}.All(keys.Contains) || keys.Any(k=>k is not "Name" and not "Text" and not "Group" and not "Correction" and not "Template" and not "Season"))throw new UserError(Invalid);
    }
    private static void Properties(JsonElement item,params string[] allowed)
    {
        if(item.ValueKind!=JsonValueKind.Object)throw new UserError(Invalid);
        var keys=item.EnumerateObject().Select(p=>p.Name).ToArray();
        if(keys.Length!=allowed.Length || keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length || keys.Any(k=>!allowed.Contains(k,StringComparer.Ordinal)))throw new UserError(Invalid);
    }
    public static QrEntry[] Merge(IReadOnlyList<QrEntry> current,IReadOnlyList<QrEntry> incoming)
    {
        Validate(current);Validate(incoming);var result=current.ToList();
        foreach(var entry in incoming)
        {
            var same=result.FirstOrDefault(x=>x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase));
            if(same is not null)
            {
                if(same.Text==entry.Text && same.Group==entry.Group && same.Correction==entry.Correction && same.Template==entry.Template && same.Season==entry.Season)continue;
                throw new UserError("A QR name already exists with different content. Rename it in the library before importing; nothing was changed.");
            }
            if(result.Any(x=>x.Id==entry.Id))throw new UserError(Invalid);
            result.Add(entry);
        }
        Validate(result);return result.ToArray();
    }
    private static void Validate(IReadOnlyList<QrEntry> entries)
    {
        if(entries.Count>32)throw new UserError("Save up to 32 QR codes.");
        var ids=new HashSet<string>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in entries)
        {
            if(entry is null)throw new UserError(Invalid);entry.Validate();
            if(!ids.Add(entry.Id) || !names.Add(entry.Name))throw new UserError(Invalid);
        }
    }
}
