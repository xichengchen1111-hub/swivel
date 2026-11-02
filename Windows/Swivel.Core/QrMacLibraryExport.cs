using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

public sealed record MacQrSaved(Guid Id, string Name, string Value, string Collection, string Season, string Template, double Created);
public sealed record QrMacLibraryRow(string Name, string Value, string Collection, bool FromMac, MacQrSaved? Output, IReadOnlyList<string> Reasons);
public sealed class QrMacLibraryPlan
{
    public IReadOnlyList<QrMacLibraryRow> Rows { get; }
    private readonly byte[] bytes;
    internal QrMacLibraryPlan(List<QrMacLibraryRow> rows)
    {
        Rows=rows.AsReadOnly();
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions {Indented=true}))
        {
            writer.WriteStartArray();
            foreach(var row in rows.Where(x=>x.Output is not null))
            {
                var item=row.Output!; writer.WriteStartObject();
                writer.WriteString("id",item.Id.ToString("D").ToUpperInvariant());
                writer.WriteString("name",item.Name); writer.WriteString("value",item.Value);
                writer.WriteString("collection",item.Collection); writer.WriteString("season",item.Season);
                writer.WriteString("template",item.Template); writer.WriteNumber("created",item.Created);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        bytes=stream.ToArray();
    }
    public byte[] Encode()=>(byte[])bytes.Clone();
}

// Foundation Date uses seconds since 2001-01-01 UTC. Windows QrEntry has no date.
// Keep a fixed epoch rather than inventing a historical date on every export.
public static class QrMacLibraryExport
{
    public const int MaxMergeBytes=262144, MaxMergeRows=128;
    public const string Title="Export full library for Mac";
    public const string Scope="Review every row before exporting a full Mac library. Names, exact content, groups, templates and seasons are retained. Mac renders all codes at M correction; Windows scene artwork differs. Refused and duplicate rows are listed and left out, never shortened.";
    public const string Install="Quit Swivel on the Mac. Back up the current library first. Put the exported file at ~/Library/Application Support/Swivel/QRCodes/library-v1.json (create the QRCodes folder if needed). Rename a numbered export to library-v1.json. This replaces the Mac library file, including entries not in this export. Reopen Swivel and check the library.";
    public const string Privacy="This JSON contains complete QR text in plaintext. Anyone who can read it can see private content. Nothing is uploaded. Existing export files are never replaced.";
    public const string MissingDate="No creation date is stored; created is set to 2001-01-01 00:00:00 UTC (Foundation seconds: 0).";
    public const string CorrectionChanged="Mac renders this code at M correction instead of its Windows L, Q or H level.";
    public const string GeneratedId="The Windows identifier is not a UUID; a stable deterministic UUID is generated.";
    public const string IdCollision="This UUID is already in the output; a new deterministic UUID is used.";
    public const string Duplicate="Not added: the same value and collection already exist (Mac Unicode-equivalent duplicate rule).";
    public const string InvalidRow="Refused: invalid Unicode, empty content, blank name, unsupported scene or correction level, or missing identifier.";
    public const string NameLimit="Refused: the name exceeds the conservative Mac limit of 120 Unicode scalars.";
    public const string GroupLimit="Refused: the collection exceeds the conservative Mac limit of 80 Unicode scalars.";
    public const string ValueLimit="Refused: content exceeds 2,000 UTF-8 bytes.";
    public const string InvalidMerge="The Mac merge source cannot be read safely (valid library JSON, up to 256 KiB and 128 rows required). Nothing was exported. Unknown fields or scenes, duplicate IDs and invalid rows are refused; conservative name and collection scalar limits apply.";
    public const string DefaultScene="Missing Mac scene fields use Summer and Tree defaults.";
    private static readonly UTF8Encoding Utf8=new(false,true);

    public static QrMacLibraryPlan Prepare(IReadOnlyList<QrEntry> entries, ReadOnlyMemory<byte>? merge=null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if(entries.Count>32) throw new UserError("Save up to 32 QR codes.");
        var rows=new List<QrMacLibraryRow>(); var ids=new HashSet<Guid>();
        var values=new HashSet<(string,string)>();
        if(merge.HasValue)
        {
            foreach(var (item,reasons) in ReadMerge(merge.Value))
            {
                ids.Add(item.Id); values.Add(Key(item.Value,item.Collection));
                rows.Add(new(item.Name,item.Value,item.Collection,true,item,Array.AsReadOnly(reasons)));
            }
        }
        foreach(var entry in entries.ToArray())
        {
            var reasons=new List<string>(); MacQrSaved? output=null;
            string? issue=entry is null?InvalidRow:Validate(entry.Name,entry.Text,entry.Group);
            if(entry is null || !ValidIdText(entry.Id) || !Enum.IsDefined(entry.Correction) || !Enum.IsDefined(entry.Template) || !Enum.IsDefined(entry.Season)) issue=InvalidRow;
            if(issue is not null) reasons.Add(issue);
            else
            {
                var value=entry!;
                if(values.Contains(Key(value.Text,value.Group))) reasons.Add(Duplicate);
                else
                {
                    Guid id;
                    if(!Guid.TryParseExact(value.Id,"N",out id) && !Guid.TryParseExact(value.Id,"D",out id))
                    { id=StableId(value.Id,0); reasons.Add(GeneratedId); }
                    int attempt=0;
                    if(ids.Contains(id))
                    {
                        reasons.Add(IdCollision);
                        do { id=StableId(value.Id,++attempt); } while(ids.Contains(id));
                    }
                    ids.Add(id); values.Add(Key(value.Text,value.Group));
                    reasons.Add(MissingDate);
                    if(value.Correction!=QrCorrection.Medium) reasons.Add(CorrectionChanged);
                    output=new(id,value.Name,value.Text,value.Group,QrGarden.Key(value.Season),QrGarden.Key(value.Template),0);
                }
            }
            rows.Add(new(entry?.Name??"",entry?.Text??"",entry?.Group??"",false,output,reasons.AsReadOnly()));
        }
        return new(rows);
    }
    private static (string,string) Key(string value,string collection)=>
        (value.Normalize(NormalizationForm.FormD),collection.Normalize(NormalizationForm.FormD));
    private static bool ValidIdText(string id)
    {
        try { return !string.IsNullOrEmpty(id) && Utf8.GetByteCount(id)<=4096; }
        catch(EncoderFallbackException) { return false; }
    }
    private static Guid StableId(string id,int attempt)
    {
        // Domain-separated SHA-256, UUID version 8 / RFC variant, big-endian bytes.
        byte[] hash=SHA256.HashData(Utf8.GetBytes("Swivel.MacQrLibrary.v1\n"+id+"\n"+attempt.ToString(CultureInfo.InvariantCulture)));
        hash[6]=(byte)((hash[6]&15)|128); hash[8]=(byte)((hash[8]&63)|128);
        return new Guid(hash.AsSpan(0,16),bigEndian:true);
    }
    private static string? Validate(string name,string value,string collection)
    {
        try
        {
            if(name is null || value is null || collection is null || string.IsNullOrWhiteSpace(name) || value.Length==0) return InvalidRow;
            _=Utf8.GetByteCount(name); _=Utf8.GetByteCount(collection);
            int bytes=Utf8.GetByteCount(value);
            // Scalar counts are an upper bound on Swift's grapheme-cluster count,
            // avoiding dependence on different OS Unicode segmentation versions.
            if(name.EnumerateRunes().Count()>120) return NameLimit;
            if(collection.EnumerateRunes().Count()>80) return GroupLimit;
            return bytes>2000?ValueLimit:null;
        }
        catch(EncoderFallbackException) { return InvalidRow; }
    }
    private static List<(MacQrSaved Item,string[] Reasons)> ReadMerge(ReadOnlyMemory<byte> bytes)
    {
        if(bytes.Length>MaxMergeBytes) throw new UserError(InvalidMerge);
        try
        {
            // Reject malformed UTF-8 before JSON parsing; never replace payload bytes.
            _=Utf8.GetString(bytes.Span);
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=3});
            var array=document.RootElement;
            if(array.ValueKind!=JsonValueKind.Array || array.GetArrayLength()>MaxMergeRows) throw new JsonException();
            var result=new List<(MacQrSaved,string[])>(); var ids=new HashSet<Guid>();
            foreach(var item in array.EnumerateArray())
            {
                LibraryJson.Fields(item,new[]{"id","name","value"},"collection","season","template","created");
                if(!Guid.TryParseExact(LibraryJson.Text(item,"id"),"D",out var id) || !ids.Add(id)) throw new JsonException();
                string name=LibraryJson.Text(item,"name"),value=LibraryJson.Text(item,"value"),collection=LibraryJson.OptionalText(item,"collection");
                if(Validate(name,value,collection) is not null) throw new JsonException();
                var reasons=new List<string>();
                string season=LibraryJson.OptionalText(item,"season"),template=LibraryJson.OptionalText(item,"template");
                if(season.Length==0 || template.Length==0) reasons.Add(DefaultScene);
                if(season.Length==0) season="summer";
                if(template.Length==0) template="tree";
                _=QrGarden.Season(season); _=QrGarden.Template(template);
                double created=0;
                if(item.TryGetProperty("created",out var date) && date.ValueKind!=JsonValueKind.Null)
                { created=date.GetDouble(); if(!double.IsFinite(created)) throw new JsonException(); }
                else reasons.Add(MissingDate);
                result.Add((new(id,name,value,collection,season,template,created),reasons.ToArray()));
            }
            return result;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or UserError)
        { throw new UserError(InvalidMerge); }
    }
}
