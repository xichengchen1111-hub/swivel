using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

/// A number as written in the source: JSON keeps its exact text; plist integers and reals become
/// the shortest text that reads back as the same value.
public sealed record DataNumber(string Text, double Value, bool Integer)
{
    /// Only a plist real can be NaN or infinite; a JSON number too large for a double keeps its text.
    public bool NotFinite => Text is "nan" or "inf" or "-inf";
}

/// JSON, JSON Lines, property lists (XML or binary), CSV and TSV ↔ each other, plus YAML out, as
/// the Mac's Convert menu does (1.14.4). Values are never guessed: CSV cells stay text, and a value
/// a format can't hold stops the conversion. A value tree holds null, bool, DataNumber, string,
/// DateTime (UTC, from plists), byte[] (plist data), List&lt;object?&gt; and Dictionary&lt;string, object?&gt;.
public static class DataFileFormats
{
    public sealed record Format(string Name, string Extension)
    {
        public string Id => "data." + Extension;
    }

    public static readonly string[] InputExtensions = { "json", "jsonl", "ndjson", "plist", "csv", "tsv" };
    public static readonly Format[] Formats =
    {
        new("JSON", "json"), new("JSONL", "jsonl"), new("PLIST", "plist"), new("CSV", "csv"), new("TSV", "tsv"), new("YAML", "yaml"),
    };
    public const int MaxBytes = 32 * 1024 * 1024, MaxRows = 1_000_000, MaxColumns = 16_384, MaxDepth = 128;
    /// Every table cell becomes a .NET string; fewer than the Mac's 10 million, to keep memory in bounds.
    public const long MaxCells = 5_000_000;
    /// A binary plist can point at one object many times; written out, every reference is a full copy.
    /// The estimate counts text, indentation and a little per value.
    public const long MaxValues = 20_000_000, MaxOutputBytes = 128L * 1024 * 1024;
    /// Values read from JSON or JSON Lines: each becomes a .NET object of 50–100 bytes, so a file is
    /// counted before its tree is built.
    public const long MaxJsonValues = 4_000_000;

    public const string TooLarge = "Data files are limited to 32 MB.";
    public const string BadEncoding = "Swivel reads data files in UTF-8 or UTF-16 (with a byte order mark). Save this file as UTF-8 and try again.";
    public const string BadPlist = "This file is not a valid property list.";
    public const string BadJson = "This file is not valid JSON.";
    public const string BadJsonLine = "Line {0} is not valid JSON.";
    public const string DuplicateKey = "The key \"{0}\" appears twice in one object.";
    public const string TooManyRows = "This file has too many rows.";
    public const string TooManyColumns = "This file has too many columns.";
    public const string TooManyValues = "This file expands to too many values to convert.";
    public const string NotFinite = "This file contains NaN or infinite numbers, which this format can't hold.";
    public const string TsvCell = "TSV can't hold tabs or line breaks inside a cell. Choose CSV instead.";
    public const string Unclosed = "A quoted field in this file is never closed.";
    public const string NoRows = "This file has no rows.";
    public const string DuplicateColumn = "Column \"{0}\" appears twice in the header.";
    public const string LongRow = "Row {0} has more cells than the header.";
    public const string NotRecords = "Only a list of records can become a table.";
    public const string PlistNull = "Property lists can't hold empty (null) values. Remove them or choose another format.";
    public const string PlistNumber = "A number in this file is too large or precise for a property list.";
    public const string CantWrite = "Couldn't write this data. It may contain text the chosen format can't hold.";

    public static string ExtensionOf(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsInput(string path) => InputExtensions.Contains(ExtensionOf(path));
    public static Format? ForId(string id) => Formats.FirstOrDefault(f => f.Id == id);
    /// A file is not offered its own format (.ndjson counts as JSONL).
    public static bool IsSame(string path, Format format) => (ExtensionOf(path) is "ndjson" ? "jsonl" : ExtensionOf(path)) == format.Extension;

    /// Either a value tree or a table of text rows (from CSV/TSV).
    public sealed record Content(object? Tree, List<List<string>>? Table);

    public static byte[] Convert(byte[] source, string sourceExtension, Format target, CancellationToken token = default)
        => Write(Read(source, sourceExtension.TrimStart('.').ToLowerInvariant(), token), target.Extension, token);

    // MARK: Reading

    public static Content Read(byte[] data, string extension, CancellationToken token = default)
    {
        if (data.Length > MaxBytes) throw new UserError(TooLarge);
        switch (extension)
        {
            case "plist":
                return new Content(data.AsSpan().StartsWith("bplist00"u8) ? BinaryPlist.Read(data, token) : XmlPlist(data), null);
            case "csv": case "tsv":
                return new Content(null, Table(Decode(data, false), extension == "tsv" ? '\t' : ',', token));
            case "jsonl": case "ndjson":
            {
                var values = new List<object?>(); int number = 0; long counted = 0;
                foreach (string line in Decode(data, true).Split('\n'))
                {
                    number++;
                    if (number % 4096 == 0) token.ThrowIfCancellationRequested();
                    if (line.Trim().Length == 0) continue;
                    if (values.Count >= MaxRows) throw new UserError(TooManyRows);
                    try { values.Add(Json(line, ref counted)); }
                    catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new UserError(BadJsonLine, number); }
                }
                return new Content(values, null);
            }
            default:
                long count = 0;
                try { return new Content(Json(Decode(data, true), ref count), null); }
                catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new UserError(BadJson); }
        }
    }

    /// Strict UTF-8 (with or without a byte order mark) or UTF-16 with one. CSV keeps carriage
    /// returns: a CR LF inside a quoted cell is part of the cell.
    public static string Decode(byte[] bytes, bool normalizeNewlines)
    {
        string text;
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException) { throw new UserError(BadEncoding); }
        if (text.Contains('\0')) throw new UserError(BadEncoding);
        if (text.StartsWith('﻿')) text = text[1..];
        return normalizeNewlines ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = MaxDepth + 1, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    /// Any JSON value, including a bare number or string at the top. Numbers keep their exact text.
    public static object? Json(string text) { long count = 0; return Json(text, ref count); }

    /// A lone surrogate in an escape (\ud800) makes .NET throw InvalidOperationException; callers
    /// report it as invalid JSON.
    private static object? Json(string text, ref long count)
    {
        // Counted first with the forward-only reader, so a huge flat array is refused before its tree is built.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = MaxDepth + 1 });
        while (reader.Read())
            if (reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName) && ++count > MaxJsonValues)
                throw new UserError(TooManyValues);
        using var document = JsonDocument.Parse(text, JsonOptions);
        return Tree(document.RootElement);
    }

    private static object? Tree(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!map.TryAdd(property.Name, Tree(property.Value))) throw new UserError(DuplicateKey, Short(property.Name));
                return map;
            case JsonValueKind.Array: return element.EnumerateArray().Select(Tree).ToList();
            case JsonValueKind.String: return element.GetString();
            case JsonValueKind.Number:
                string raw = element.GetRawText();
                double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                return new DataNumber(raw, value, raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0);
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    /// XML property lists. The DOCTYPE every plist carries is skipped, never fetched.
    private static object? XmlPlist(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true, IgnoreProcessingInstructions = true, MaxCharactersInDocument = 4L * MaxBytes };
        try
        {
            // Decoded strictly first, so an encoding="ISO-8859-1" declaration can't bring in a legacy encoding.
            using var reader = XmlReader.Create(new StringReader(Decode(data, false)), settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "plist") throw new UserError(BadPlist);
            if (reader.IsEmptyElement || !reader.Read()) throw new UserError(BadPlist);
            var value = PlistValue(reader, 0);
            if (reader.NodeType != XmlNodeType.EndElement || reader.Name != "plist") throw new UserError(BadPlist);
            return value;
        }
        catch (XmlException) { throw new UserError(BadPlist); }
        catch (FormatException) { throw new UserError(BadPlist); }
        catch (OverflowException) { throw new UserError(BadPlist); }
    }

    // On entry the reader is on the value's start element; on return it is just after its end.
    private static object? PlistValue(XmlReader reader, int depth)
    {
        if (depth > MaxDepth) throw new UserError(TooManyValues);
        if (reader.NodeType != XmlNodeType.Element) throw new UserError(BadPlist);
        string name = reader.Name;
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return name switch
            {
                "true" => true, "false" => false, "string" => "", "data" => Array.Empty<byte>(),
                "dict" => new Dictionary<string, object?>(StringComparer.Ordinal), "array" => new List<object?>(),
                _ => throw new UserError(BadPlist),
            };
        }
        switch (name)
        {
            case "dict":
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "key") throw new UserError(BadPlist);
                    string key = reader.ReadElementContentAsString();
                    if (!map.TryAdd(key, PlistValue(reader, depth + 1))) throw new UserError(DuplicateKey, Short(key));
                }
                reader.Read();
                return map;
            }
            case "array":
            {
                var list = new List<object?>();
                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement) list.Add(PlistValue(reader, depth + 1));
                reader.Read();
                return list;
            }
            case "string": return reader.ReadElementContentAsString();
            case "integer":
            {
                string text = reader.ReadElementContentAsString().Trim();
                // Unsigned values above Int64 are written by some tools; both read exactly.
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed)) return Integer(signed.ToString(CultureInfo.InvariantCulture));
                if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong unsigned)) return Integer(unsigned.ToString(CultureInfo.InvariantCulture));
                throw new UserError(BadPlist);
            }
            case "real":
            {
                string text = reader.ReadElementContentAsString().Trim();
                double value = text.ToLowerInvariant() switch
                {
                    "nan" => double.NaN, "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity, "-inf" or "-infinity" => double.NegativeInfinity,
                    _ => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                };
                return Real(value);
            }
            case "date":
                return DateTime.ParseExact(reader.ReadElementContentAsString().Trim(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            case "data":
            {
                string text = new string(reader.ReadElementContentAsString().Where(c => !char.IsWhiteSpace(c)).ToArray());
                return System.Convert.FromBase64String(text);
            }
            default: throw new UserError(BadPlist);
        }
    }

    internal static DataNumber Integer(string text) => new(text, double.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), true);
    internal static DataNumber Real(double value) =>
        new(double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : double.IsNaN(value) ? "nan" : value > 0 ? "inf" : "-inf", value, false);

    // MARK: Tables

    /// RFC 4180 for CSV: quoted fields may contain commas, quotes ("") and line breaks (kept as
    /// they are). TSV has no quoting: a quote is an ordinary character. Read by Unicode scalar,
    /// so a combining mark after a separator can't hide it.
    public static List<List<string>> Table(string text, char separator, CancellationToken token = default)
    {
        bool quoting = separator != '\t';
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder();
        bool quoted = false, atStart = true; long cells = 0; int index = 0, steps = 0;
        void EndField()
        {
            row.Add(field.ToString()); field.Clear(); atStart = true;
            if (row.Count > MaxColumns) throw new UserError(TooManyColumns);
            if (++cells > MaxCells) throw new UserError(TooManyColumns);
        }
        void EndRow()
        {
            EndField();
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
            else cells--;
            if (rows.Count > MaxRows) throw new UserError(TooManyRows);
            row = new List<string>();
        }
        while (index < text.Length)
        {
            if (++steps % 65536 == 0) token.ThrowIfCancellationRequested();
            var rune = Rune.GetRuneAt(text, index); index += rune.Utf16SequenceLength;
            if (quoted)
            {
                if (rune.Value == '"')
                {
                    if (index < text.Length && text[index] == '"') { field.Append('"'); index++; }
                    else quoted = false;
                }
                else field.Append(text, index - rune.Utf16SequenceLength, rune.Utf16SequenceLength);
                continue;
            }
            if (quoting && rune.Value == '"' && atStart && field.Length == 0) quoted = true;
            else if (rune.Value == separator) { EndField(); continue; }
            else if (rune.Value == '\n') { EndRow(); continue; }
            else if (rune.Value == '\r')
            {
                if (index < text.Length && text[index] == '\n') index++;
                EndRow(); continue;
            }
            else field.Append(text, index - rune.Utf16SequenceLength, rune.Utf16SequenceLength);
            atStart = false;
        }
        if (quoted) throw new UserError(Unclosed);
        if (field.Length > 0 || row.Count > 0) EndRow();
        if (rows.Count == 0) throw new UserError(NoRows);
        return rows;
    }

    private static string Field(string value, char separator)
    {
        bool special = value.Any(c => c == separator || c == '\n' || c == '\r' || (c == '"' && separator == ','));
        if (!special) return value;
        if (separator != ',') throw new UserError(TsvCell);
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// The first row names the columns; each later row becomes an object with text values.
    private static object? TreeOf(Content content)
    {
        if (content.Table is not { } rows) return content.Tree;
        var names = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rows[0].Count; i++)
        {
            string name = rows[0][i].All(c => char.IsWhiteSpace(c) && c is not '\n' and not '\r') ? "column " + (i + 1).ToString(CultureInfo.InvariantCulture) : rows[0][i];
            if (!seen.Add(name)) throw new UserError(DuplicateColumn, Short(name));
            names.Add(name);
        }
        // Short rows are padded to the header's width, so a wide header times many rows is the real size.
        if ((long)(rows.Count - 1) * names.Count > MaxCells) throw new UserError(TooManyColumns);
        var records = new List<object?>(rows.Count - 1);
        for (int r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count > names.Count) throw new UserError(LongRow, r + 1);
            var record = new Dictionary<string, object?>(names.Count, StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++) record[names[i]] = i < rows[r].Count ? rows[r][i] : "";
            records.Add(record);
        }
        return records;
    }

    /// Arrays of objects (columns sorted within each record, first-seen across records) or arrays
    /// of arrays. Nested values are written as compact JSON text.
    private static List<List<string>> RowsOf(Content content)
    {
        if (content.Table is { } table) return table;
        var items = content.Tree switch
        {
            List<object?> list => list,
            Dictionary<string, object?> one => new List<object?> { one },
            _ => null,
        };
        if (items is null || items.Count == 0) throw new UserError(NotRecords);
        string Cell(object? value) => value switch
        {
            null => "",
            string text => text,
            bool flag => flag ? "true" : "false",
            DataNumber number => number.Text,
            DateTime or byte[] => Text(value)!,
            _ => Compact(value),
        };
        if (items.All(i => i is List<object?>)) return items.Select(i => ((List<object?>)i!).Select(Cell).ToList()).ToList();
        if (!items.All(i => i is Dictionary<string, object?>)) throw new UserError(NotRecords);
        var columns = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = items.Cast<Dictionary<string, object?>>().ToList();
        foreach (var record in records)
        {
            foreach (string key in record.Keys.OrderBy(k => k, StringComparer.Ordinal)) if (seen.Add(key)) columns.Add(key);
            if (columns.Count > MaxColumns || (long)columns.Count * items.Count > MaxCells) throw new UserError(TooManyColumns);
        }
        var rows = new List<List<string>> { columns };
        foreach (var record in records) rows.Add(columns.Select(c => record.TryGetValue(c, out var v) ? Cell(v) : "").ToList());
        return rows;
    }

    // MARK: Writing

    public static byte[] Write(Content content, string extension, CancellationToken token = default)
    {
        // A table becomes records that repeat every header name, so its tree is checked as well.
        if (content.Table is null) Check(content.Tree, extension is "json" or "jsonl" or "csv" or "tsv", token);
        else if (extension is not ("csv" or "tsv")) { content = new Content(TreeOf(content), null); Check(content.Tree, false, token); }
        var text = new StringBuilder();
        switch (extension)
        {
            case "csv": case "tsv":
            {
                char separator = extension == "tsv" ? '\t' : ',';
                foreach (var row in RowsOf(content)) text.AppendJoin(separator, row.Select(c => Field(c, separator))).Append("\r\n");
                // A byte order mark lets Excel read non-English text as UTF-8.
                return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(text.ToString())).ToArray();
            }
            case "plist":
                text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n");
                PlistXml(TreeOf(content), 0, text);
                text.Append("\n</plist>\n");
                break;
            case "jsonl":
            {
                var value = TreeOf(content);
                var items = value is List<object?> list ? list : new List<object?> { value };
                foreach (var item in items) { JsonText(item, -1, text); text.Append('\n'); }
                if (items.Count == 0) text.Append('\n');
                break;
            }
            case "yaml":
                text.Append("---\n"); Yaml(TreeOf(content), 0, text); text.Append('\n');
                break;
            default:
                JsonText(TreeOf(content), 0, text); text.Append('\n');
                break;
        }
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// Walks the whole value once before anything is written: how many values it expands to (shared
    /// plist objects count every time), how much text, how deep, and, for JSON, JSON Lines, CSV and
    /// TSV, no NaN or infinity. YAML writes them as .nan and .inf.
    public static void Check(object? root, bool json, CancellationToken token = default)
    {
        var stack = new Stack<(object? Value, int Depth)>(); stack.Push((root, 0));
        long count = 0, bytes = 0;
        while (stack.Count > 0)
        {
            var (value, depth) = stack.Pop();
            count++; bytes += 8 + 2 * depth;
            if (count % 65536 == 0) token.ThrowIfCancellationRequested();
            if (count > MaxValues || depth > MaxDepth) throw new UserError(TooManyValues);
            // Children are counted before they are pushed, so a list of millions of shared items is
            // refused without first filling the stack with them.
            long children = value switch { Dictionary<string, object?> m => m.Count, List<object?> l => l.Count, _ => 0 };
            if (count + stack.Count + children > MaxValues) throw new UserError(TooManyValues);
            switch (value)
            {
                case Dictionary<string, object?> map: foreach (var (key, item) in map) { bytes += Encoding.UTF8.GetByteCount(key); stack.Push((item, depth + 1)); } break;
                case List<object?> list: foreach (var item in list) stack.Push((item, depth + 1)); break;
                case string text: bytes += Encoding.UTF8.GetByteCount(text); break;
                case byte[] data: bytes += data.LongLength * 4 / 3; break;
                case DataNumber number:
                    bytes += number.Text.Length;
                    if (json && number.NotFinite) throw new UserError(NotFinite);
                    break;
            }
            if (bytes > MaxOutputBytes) throw new UserError(TooManyValues);
        }
    }

    /// Dates become ISO 8601 text and data Base64, since JSON has neither.
    private static string? Text(object? value) => value switch
    {
        DateTime date => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        byte[] data => System.Convert.ToBase64String(data),
        string text => text,
        _ => null,
    };

    private static string Compact(object? value) { var text = new StringBuilder(); JsonText(value, -1, text); return text.ToString(); }

    /// Pretty (indent ≥ 0, two spaces) or compact (indent -1) JSON with keys sorted and "/" and
    /// non-ASCII text left as they are.
    private static void JsonText(object? value, int indent, StringBuilder out_)
    {
        bool pretty = indent >= 0;
        void Pad(int level) { if (pretty) out_.Append('\n').Append(' ', 2 * level); }
        switch (value)
        {
            case null: out_.Append("null"); break;
            case bool flag: out_.Append(flag ? "true" : "false"); break;
            case DataNumber number: out_.Append(number.Text); break;
            case Dictionary<string, object?> map:
                if (map.Count == 0) { out_.Append("{}"); break; }
                out_.Append('{'); bool first = true;
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) out_.Append(','); first = false;
                    Pad(indent + 1); Quote(key, out_); out_.Append(pretty ? ": " : ":"); JsonText(map[key], pretty ? indent + 1 : -1, out_);
                }
                Pad(indent); out_.Append('}');
                break;
            case List<object?> list:
                if (list.Count == 0) { out_.Append("[]"); break; }
                out_.Append('[');
                for (int i = 0; i < list.Count; i++) { if (i > 0) out_.Append(','); Pad(indent + 1); JsonText(list[i], pretty ? indent + 1 : -1, out_); }
                Pad(indent); out_.Append(']');
                break;
            default: Quote(Text(value)!, out_); break;
        }
    }

    private static void Quote(string text, StringBuilder out_)
    {
        out_.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': out_.Append("\\\""); break;
                case '\\': out_.Append("\\\\"); break;
                case '\n': out_.Append("\\n"); break;
                case '\r': out_.Append("\\r"); break;
                case '\t': out_.Append("\\t"); break;
                case '\b': out_.Append("\\b"); break;
                case '\f': out_.Append("\\f"); break;
                default:
                    if (c < 0x20) out_.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else out_.Append(c);
                    break;
            }
        }
        out_.Append('"');
    }

    private static void PlistXml(object? value, int level, StringBuilder out_)
    {
        out_.Append('\t', level);
        switch (value)
        {
            case null: throw new UserError(PlistNull);
            case bool flag: out_.Append(flag ? "<true/>" : "<false/>"); break;
            case DataNumber number:
                // JSON numbers beyond 64-bit integers or double precision would be silently rounded.
                if (number.Integer && (long.TryParse(number.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) || ulong.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                    out_.Append("<integer>").Append(number.Text).Append("</integer>");
                else if (number.NotFinite) out_.Append("<real>").Append(number.Text).Append("</real>");
                else if (double.IsFinite(number.Value) && SameDecimal(number.Text, number.Value.ToString("R", CultureInfo.InvariantCulture)))
                    out_.Append("<real>").Append(number.Value.ToString("R", CultureInfo.InvariantCulture)).Append("</real>");
                else throw new UserError(PlistNumber);
                break;
            case DateTime date: out_.Append("<date>").Append(Text(date)).Append("</date>"); break;
            case byte[] data: out_.Append("<data>").Append(System.Convert.ToBase64String(data)).Append("</data>"); break;
            case string text: out_.Append("<string>").Append(Escape(text)).Append("</string>"); break;
            case Dictionary<string, object?> map:
                if (map.Count == 0) { out_.Append("<dict/>"); break; }
                out_.Append("<dict>");
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    out_.Append('\n').Append('\t', level + 1).Append("<key>").Append(Escape(key)).Append("</key>\n");
                    PlistXml(map[key], level + 1, out_);
                }
                out_.Append('\n').Append('\t', level).Append("</dict>");
                break;
            case List<object?> list:
                if (list.Count == 0) { out_.Append("<array/>"); break; }
                out_.Append("<array>");
                foreach (var item in list) { out_.Append('\n'); PlistXml(item, level + 1, out_); }
                out_.Append('\n').Append('\t', level).Append("</array>");
                break;
            default: throw new UserError(CantWrite);
        }
    }

    /// Text XML 1.0 can hold, escaped; control characters and lone surrogates can't be written.
    private static string Escape(string text)
    {
        var out_ = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { out_.Append(c).Append(text[++i]); continue; }
            if (char.IsSurrogate(c) || (c < 0x20 && c is not '\t' and not '\n' and not '\r') || c is '￾' or '￿') throw new UserError(CantWrite);
            out_.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '\r' => "&#13;", _ => c.ToString() });
        }
        return out_.ToString();
    }

    /// Whether two decimal number texts are the same value: digits without leading or trailing
    /// zeros, and the power of ten, compared exactly.
    public static bool SameDecimal(string a, string b) => Normalize(a) == Normalize(b);

    private static (bool Negative, string Digits, long Exponent)? Normalize(string text)
    {
        text = text.Trim();
        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        long exponent = 0;
        int e = text.IndexOfAny(new[] { 'e', 'E' });
        if (e >= 0)
        {
            if (!long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return null;
            text = text[..e];
        }
        int dot = text.IndexOf('.');
        string digits = dot < 0 ? text : text[..dot] + text[(dot + 1)..];
        if (dot >= 0) exponent -= text.Length - dot - 1;
        if (digits.Length == 0 || digits.Any(c => c is < '0' or > '9')) return null;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) return (false, "0", 0);
        int trailing = digits.Length - digits.TrimEnd('0').Length;
        return (negative, digits[..(digits.Length - trailing)], exponent + trailing);
    }

    /// Written into one buffer. Strings are always quoted: YAML reads bare yes, no, 1.0, ~ or dates
    /// as other types. Characters YAML forbids or folds are escaped.
    private static void Yaml(object? value, int indent, StringBuilder out_)
    {
        if (Scalar(value) is { } plain) { out_.Append(plain); return; }
        string pad = new(' ', 2 * indent); bool first = true;
        void Line(string prefix, object? child)
        {
            if (!first) out_.Append('\n'); first = false;
            if (Scalar(child) is { } s) { out_.Append(pad).Append(prefix).Append(' ').Append(s); return; }
            out_.Append(pad).Append(prefix).Append('\n');
            Yaml(child, indent + 1, out_);
        }
        if (value is Dictionary<string, object?> map) foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal)) Line(Scalar(key)! + ":", map[key]);
        else foreach (var item in (List<object?>)value!) Line("-", item);
    }

    /// YAML 1.1 readers take 1e+20 as text unless the mantissa has a dot and the exponent a sign:
    /// 1E+20 and 1e20 are written 1.0e+20.
    public static string YamlNumber(string text)
    {
        int e = text.IndexOfAny(new[] { 'e', 'E' });
        if (e < 0) return text;
        string mantissa = text[..e], exponent = text[(e + 1)..];
        if (!mantissa.Contains('.')) mantissa += ".0";
        if (!exponent.StartsWith('-') && !exponent.StartsWith('+')) exponent = "+" + exponent;
        return mantissa + "e" + exponent;
    }

    /// User text shown in a message: at most 80 characters, controls made visible.
    public static string Short(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length >= 80) return builder.Append('…').ToString();
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                builder.Append("U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture));
            else builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static string? Scalar(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case bool flag: return flag ? "true" : "false";
            case DataNumber number:
                if (number.Text == "nan") return ".nan";
                if (number.NotFinite) return number.Text == "inf" ? ".inf" : "-.inf";
                return YamlNumber(number.Text);
            case Dictionary<string, object?> { Count: 0 }: return "{}";
            case List<object?> { Count: 0 }: return "[]";
            case Dictionary<string, object?> or List<object?>: return null;
        }
        string text = Text(value)!;
        var out_ = new StringBuilder("\"");
        foreach (var rune in text.EnumerateRunes())
        {
            int u = rune.Value;
            if (u == '\\') out_.Append("\\\\");
            else if (u == '"') out_.Append("\\\"");
            else if (u == '\n') out_.Append("\\n");
            else if (u == '\t') out_.Append("\\t");
            else if (u < 0x20 || u is >= 0x7F and <= 0x9F) out_.Append("\\x").Append(u.ToString("X2", CultureInfo.InvariantCulture));
            else if (u is 0x2028 or 0x2029 or 0xFEFF or 0xFFFE or 0xFFFF) out_.Append("\\u").Append(u.ToString("X4", CultureInfo.InvariantCulture));
            else out_.Append(rune.ToString());
        }
        return out_.Append('"').ToString();
    }
}

/// Binary property lists ("bplist00"), read with every offset, count and reference checked
/// against the file. Objects referenced several times are decoded once and shared; a reference
/// cycle is refused.
internal static class BinaryPlist
{
    public static object? Read(byte[] data, CancellationToken token)
    {
        try { return new Reader(data, token).Top(); }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or ArgumentException or FormatException)
        { throw new UserError(DataFileFormats.BadPlist); }
    }

    private sealed class Reader
    {
        private readonly byte[] data; private readonly CancellationToken token;
        private readonly int offsetSize, refSize; private readonly long count, top, table;
        // Grown as objects are reached, not sized from the trailer's count.
        private readonly Dictionary<long, object?> decoded = new(); private readonly HashSet<long> inProgress = new();
        private long made;

        public Reader(byte[] data, CancellationToken token)
        {
            this.data = data; this.token = token;
            if (data.Length < 8 + 32) throw new UserError(DataFileFormats.BadPlist);
            var trailer = data.AsSpan(data.Length - 32);
            offsetSize = trailer[6]; refSize = trailer[7];
            count = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            table = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || count < 1 || count > data.Length || top < 0 || top >= count
                || table < 8 || table > data.Length - 32 || count * offsetSize > data.Length - 32 - table)
                throw new UserError(DataFileFormats.BadPlist);

        }

        public object? Top() => Object(top, 0);

        private long Unsigned(long at, int size)
        {
            if (at < 0 || size < 1 || size > 8 || at + size > data.Length - 32) throw new UserError(DataFileFormats.BadPlist);
            ulong value = 0;
            for (int i = 0; i < size; i++) value = value << 8 | data[at + i];
            if (value > long.MaxValue) throw new UserError(DataFileFormats.BadPlist);
            return (long)value;
        }

        private long Offset(long index)
        {
            long at = Unsigned(table + index * offsetSize, offsetSize);
            if (at < 8 || at >= table) throw new UserError(DataFileFormats.BadPlist);
            return at;
        }

        private object? Object(long index, int depth)
        {
            if (index < 0 || index >= count || depth > DataFileFormats.MaxDepth) throw new UserError(DataFileFormats.BadPlist);
            if (decoded.TryGetValue(index, out var known)) return known;
            if (!inProgress.Add(index)) throw new UserError(DataFileFormats.BadPlist); // a cycle
            if (++made % 4096 == 0) token.ThrowIfCancellationRequested();
            var value = Decode(Offset(index), depth);
            decoded[index] = value; inProgress.Remove(index);
            return value;
        }

        // The length after a marker: its low nibble, or an integer object that follows when that is 15.
        private (long Length, long Start) Length(long at)
        {
            int low = data[at] & 0x0F;
            if (low != 0x0F) return (low, at + 1);
            int marker = data[at + 1];
            if ((marker & 0xF0) != 0x10) throw new UserError(DataFileFormats.BadPlist);
            int size = 1 << (marker & 0x0F);
            if (size > 8) throw new UserError(DataFileFormats.BadPlist);
            return (Unsigned(at + 2, size), at + 2 + size);
        }

        private long Span(long start, long length, int unit)
        {
            if (length < 0 || length > (data.Length - 32 - start) / unit) throw new UserError(DataFileFormats.BadPlist);
            return length * unit;
        }

        private object? Decode(long at, int depth)
        {
            int marker = data[at];
            switch (marker >> 4)
            {
                case 0x0:
                    return marker switch { 0x00 => null, 0x08 => false, 0x09 => true, _ => throw new UserError(DataFileFormats.BadPlist) };
                case 0x1:
                {
                    int size = 1 << (marker & 0x0F);
                    if (at + 1 + size > data.Length - 32) throw new UserError(DataFileFormats.BadPlist);
                    var bytes = data.AsSpan((int)at + 1, size);
                    return size switch
                    {
                        1 => DataFileFormats.Integer(bytes[0].ToString(CultureInfo.InvariantCulture)),
                        2 => DataFileFormats.Integer(BinaryPrimitives.ReadUInt16BigEndian(bytes).ToString(CultureInfo.InvariantCulture)),
                        4 => DataFileFormats.Integer(BinaryPrimitives.ReadUInt32BigEndian(bytes).ToString(CultureInfo.InvariantCulture)),
                        8 => DataFileFormats.Integer(BinaryPrimitives.ReadInt64BigEndian(bytes).ToString(CultureInfo.InvariantCulture)),
                        // 16-byte integers hold unsigned 64-bit values above Int64 (high half zero).
                        16 when BinaryPrimitives.ReadUInt64BigEndian(bytes) == 0 => DataFileFormats.Integer(BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]).ToString(CultureInfo.InvariantCulture)),
                        _ => throw new UserError(DataFileFormats.BadPlist),
                    };
                }
                case 0x2:
                {
                    int size = 1 << (marker & 0x0F);
                    if (at + 1 + size > data.Length - 32) throw new UserError(DataFileFormats.BadPlist);
                    var bytes = data.AsSpan((int)at + 1, size);
                    return size switch
                    {
                        4 => DataFileFormats.Real(BinaryPrimitives.ReadSingleBigEndian(bytes)),
                        8 => DataFileFormats.Real(BinaryPrimitives.ReadDoubleBigEndian(bytes)),
                        _ => throw new UserError(DataFileFormats.BadPlist),
                    };
                }
                case 0x3:
                {
                    if (marker != 0x33 || at + 9 > data.Length - 32) throw new UserError(DataFileFormats.BadPlist);
                    double seconds = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan((int)at + 1, 8));
                    if (!double.IsFinite(seconds) || Math.Abs(seconds) > 3e11) throw new UserError(DataFileFormats.BadPlist);
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Floor(seconds));
                }
                case 0x4:
                {
                    var (length, start) = Length(at);
                    return data.AsSpan((int)start, (int)Span(start, length, 1)).ToArray();
                }
                case 0x5:
                {
                    var (length, start) = Length(at);
                    var bytes = data.AsSpan((int)start, (int)Span(start, length, 1));
                    foreach (byte b in bytes) if (b > 0x7F) throw new UserError(DataFileFormats.BadPlist);
                    return Encoding.ASCII.GetString(bytes);
                }
                case 0x6:
                {
                    var (length, start) = Length(at);
                    var bytes = data.AsSpan((int)start, (int)Span(start, length, 2));
                    try { return new UnicodeEncoding(true, false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { throw new UserError(DataFileFormats.BadPlist); }
                }
                case 0xA:
                {
                    var (length, start) = Length(at);
                    Span(start, length, refSize);
                    var list = new List<object?>((int)Math.Min(length, 65536));
                    for (long i = 0; i < length; i++) list.Add(Object(Unsigned(start + i * refSize, refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (length, start) = Length(at);
                    Span(start, length, 2 * refSize);
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (long i = 0; i < length; i++)
                    {
                        if (Object(Unsigned(start + i * refSize, refSize), depth + 1) is not string key) throw new UserError(DataFileFormats.BadPlist);
                        if (!map.TryAdd(key, Object(Unsigned(start + (length + i) * refSize, refSize), depth + 1))) throw new UserError(DataFileFormats.DuplicateKey, DataFileFormats.Short(key));
                    }
                    return map;
                }
                default: throw new UserError(DataFileFormats.BadPlist); // UIDs (keyed archives) and sets
            }
        }
    }
}
