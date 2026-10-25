using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using Swivel.Core;

internal static class MacQrLibraryChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string Row(string name, string text, string group = "Work") => JsonSerializer.Serialize(new {
            id = Guid.NewGuid().ToString("D").ToUpperInvariant(), name, value = text, collection = group,
            season = "summer", template = "tree", created = 800000000.25
        });
        MacQrImport Decode(params string[] rows) => MacQrLibrary.Decode(Encoding.UTF8.GetBytes("[" + string.Join(',', rows) + "]"));
        string value = "https://example.com/中文?x=🦜\r\nline";
        var source = Decode(Row("照片QR", value)); var entry = source.Compatible.Single();
        check(entry.Name == "照片QR" && entry.Text == value && entry.Group == "Work" && entry.Correction==QrCorrection.Medium, "Mac QR exact Unicode/CRLF/name/collection preserved");
        check(Guid.TryParseExact(entry.Id, "N", out _), "Imported Mac QR uses new local ID format");
        var mixed = Decode(Row("valid", "ok"), Row("long", new string('a', 271)), Row(new string('n', 49), "ok"), Row("group", "ok", new string('g', 33)), Row("hidden", "secret\u0001"));
        check(mixed.Rows.Count == 5 && mixed.Compatible.Length == 2 && mixed.Rows.Count(x => x.Issue is not null) == 3, "Over-limit and hidden fields are marked per-row and never shortened");
        check(mixed.Rows[1].Text.Length == 271 && mixed.Rows[2].Name.Length == 49 && mixed.Rows[3].Group.Length == 33, "Unsupported Mac values retained for full preview");
        var expanded=Decode(Row("former max",new string('a',105)),Row("newly compatible",new string('b',106)),Row("new max",new string('c',270)),Row("CJK max",new string('中',90)),Row("too much",new string('中',91)));
        check(expanded.Compatible.Length==5 && expanded.Rows[4].Text==new string('中',91) && expanded.Rows[4].Issue is null,"Expanded capacity applies to Mac import without truncating former incompatible rows");
        check(Decode(Row("Mac maximum",new string('x',2000)),Row("Mac Unicode maximum",new string('中',666)+"é")).Compatible.Length==2,"Full Mac source payload byte capacity can now import while retaining row constraints");
        foreach (var row in mixed.Rows.Where(x => x.Issue is not null)) check(Strings.Table.ContainsKey(row.Issue!), "Mac QR compatibility reason translated");
        var formatted = Decode(Row("Visible content", "secret\u202e"));
        check(formatted.Compatible.Single().Text == "secret\u202e" && LibraryJson.Visible(formatted.Rows[0].Text).EndsWith("[U+202E]"), "Existing QR payload contract preserves format characters and exposes them in preview");
        var duplicate = Decode(Row("Same", "a", "A"), Row("same", "b", "B"), Row("Other", "c"));
        check(duplicate.Compatible.Length == 1 && duplicate.Rows.Take(2).All(x => x.Issue == "Duplicate QR names must be renamed before importing into Windows."), "Duplicate Mac names all marked without arbitrarily choosing a collection");
        string minimal = "{\"id\":\"" + Guid.NewGuid().ToString("D") + "\",\"name\":\"Old\",\"value\":\"x\"}";
        check(Decode(minimal).Compatible.Single().Group == "", "Legacy Mac optional fields default to empty group");
        check(Decode(minimal[..^1] + ",\"collection\":null,\"season\":null,\"template\":null,\"created\":null}").Compatible.Length == 1, "Mac decodeIfPresent null defaults supported");
        string json = "[" + Row("entry", "data") + "]";
        foreach (string invalid in new[] { "null", "{}", json[..^1], json.Replace("\"name\":", "\"name\":\"first\",\"name\":"), json.Replace("\"name\":", "\"exec\":\"bad\",\"name\":"),
            json.Replace("\"value\":\"data\"", "\"value\":null"), json.Replace("\"created\":800000000.25", "\"created\":\"wrong type\""), json.Replace("\"created\":800000000.25", "\"created\":1e999") })
            reject(() => MacQrLibrary.Decode(Encoding.UTF8.GetBytes(invalid)), "Invalid/duplicate/unknown Mac QR JSON refused");
        string sameId = Row("same", "data"); reject(() => Decode(sameId, sameId.Replace("same", "other")), "Duplicate source QR IDs refused");
        reject(() => Decode(Row("too long", new string('x', 2001))), "Invalid Mac payload beyond source format bound refused");
        reject(() => MacQrLibrary.Decode(new byte[MacQrLibrary.MaxBytes + 1]), "Mac QR byte budget checked before parse");
        reject(() => MacQrLibrary.Decode(new byte[] { 0xc0, 0xaf }), "Malformed Mac QR UTF8 refused");
        reject(() => Decode(Enumerable.Range(0, 129).Select(i => Row("Row " + i, "x")).ToArray()), "Source entry count bounded");
        string longMacName = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 100));
        string longMacGroup = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 80));
        var graphemes = Decode(Row(longMacName, "x"), Row("group", "x", longMacGroup), Row("plain", "valid"));
        check(graphemes.Rows.Count == 3 && graphemes.Compatible.Length == 1 && graphemes.Rows[0].Name == longMacName && graphemes.Rows[1].Group == longMacGroup, "Long legal Mac grapheme fields are per-row Windows issues and never reject compatible peers");
        var swift = MacQrLibrary.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mac-qr-library.json")));
        check(swift.Rows.Count == 2 && swift.Compatible.Length == 2 && swift.Compatible[0].Text == value && swift.Compatible[0].Group == "工作" && swift.Compatible[1].Text==new string('a',106), "Actual Swift JSONEncoder fixture preserves Unicode/CRLF/reference dates and now imports the previously over-limit 106-byte row");
        check(Decode().Rows.Count == 0, "Empty Mac QR library inert");
        check(Decode(Enumerable.Range(0, 128).Select(i => Row("Row " + i, "x")).ToArray()).Rows.Count == 128, "128-row source can be fully reviewed");
        var legacyLevel=new Prefs();legacyLevel.SaveQr(entry with{Correction=QrCorrection.Low});string oldLibrary=legacyLevel.Serialize();
        reject(()=>legacyLevel.ImportQr(source.Compatible),"Importing Mac M over identical saved L is a conflict requiring explicit user edit");
        check(legacyLevel.Serialize()==oldLibrary,"Mac correction difference never silently replaces old L entry");
        var prefs = new Prefs(); prefs.ImportQr(source.Compatible); string before = prefs.Serialize();
        prefs.ImportQr(Decode(Row("照片QR", value)).Compatible);
        check(prefs.Serialize() == before && !prefs.IsOn(Feature.QrCodes), "Re-import preserves existing IDs and opt-in switch");
        reject(() => prefs.ImportQr(Decode(Row("new", "x"), Row("照片QR", "conflict")).Compatible), "Conflicting Mac import blocks all changes");
        check(prefs.Serialize() == before, "Mac conflict never partly imports earlier entry");
    }
}
