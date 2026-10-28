using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class DataFileFormatsChecks
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static string Out(string source, string from, string to) => Encoding.UTF8.GetString(DataFileFormats.Convert(Utf8(source), from, DataFileFormats.Formats.Single(f => f.Extension == to)));
    private static string Out(byte[] source, string from, string to) => Encoding.UTF8.GetString(DataFileFormats.Convert(source, from, DataFileFormats.Formats.Single(f => f.Extension == to)));

    /// A binary plist from hand-written objects: one-byte offsets and references.
    private static byte[] Bplist(params byte[][] objects) => Bplist(0, objects);
    private static byte[] Bplist(int top, params byte[][] objects)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("bplist00")); var offsets = new List<byte>();
        foreach (var o in objects) { offsets.Add((byte)bytes.Count); bytes.AddRange(o); }
        int table = bytes.Count; bytes.AddRange(offsets);
        var trailer = new byte[32]; trailer[6] = 1; trailer[7] = 1; trailer[15] = (byte)objects.Length; trailer[23] = (byte)top; trailer[31] = (byte)table;
        bytes.AddRange(trailer);
        return bytes.ToArray();
    }
    private static byte[] Ascii(string s) => new[] { (byte)(0x50 | s.Length) }.Concat(Encoding.ASCII.GetBytes(s)).ToArray();

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string json = "{\"name\": \"Ann / \\\"A\\\"\", \"tags\": [\"x\", 2, true, null], \"big\": 12345678901234567890, \"pi\": 3.14, \"zh\": \"中文\", \"empty\": {}}";
        check(Out(json, "json", "json") == "{\n  \"big\": 12345678901234567890,\n  \"empty\": {},\n  \"name\": \"Ann / \\\"A\\\"\",\n  \"pi\": 3.14,\n  \"tags\": [\n    \"x\",\n    2,\n    true,\n    null\n  ],\n  \"zh\": \"中文\"\n}\n",
            "JSON is written with sorted keys, exact numbers and unescaped slashes and text");
        check(Out(json, "json", "yaml") == "---\n\"big\": 12345678901234567890\n\"empty\": {}\n\"name\": \"Ann / \\\"A\\\"\"\n\"pi\": 3.14\n\"tags\":\n  - \"x\"\n  - 2\n  - true\n  - null\n\"zh\": \"中文\"\n",
            "JSON becomes YAML with every string, keys included, quoted");
        check(Out("[{\"a\":1},{\"b\":[1,2]}]", "json", "jsonl") == "{\"a\":1}\n{\"b\":[1,2]}\n" && Out("5", "json", "jsonl") == "5\n", "JSON becomes one compact JSON value per line");
        check(Out("{\"x\": \"yes\", \"n\": \"\\u0001\\u2028\\t\"}", "json", "yaml") == "---\n\"n\": \"\\x01\\u2028\\t\"\n\"x\": \"yes\"\n", "YAML escapes controls and line separators and quotes yes");

        // CSV and TSV.
        string csv = "id,note,\r\n1,\"a, \"\"b\"\"\r\nnext\",x\r\n2,plain\r\n";
        var table = DataFileFormats.Table(DataFileFormats.Decode(Utf8(csv), false), ',');
        check(table.Count == 3 && table[1].SequenceEqual(new[] { "1", "a, \"b\"\r\nnext", "x" }) && table[2].SequenceEqual(new[] { "2", "plain" }), "CSV quoted cells keep commas, doubled quotes and CR LF");
        check(Out(csv, "csv", "json") == "[\n  {\n    \"column 3\": \"x\",\n    \"id\": \"1\",\n    \"note\": \"a, \\\"b\\\"\\r\\nnext\"\n  },\n  {\n    \"column 3\": \"\",\n    \"id\": \"2\",\n    \"note\": \"plain\"\n  }\n]\n",
            "CSV becomes records of text, short rows padded, a blank header named column 3");
        check(DataFileFormats.Table("a\t\"b\tc\n", '\t').Single().SequenceEqual(new[] { "a", "\"b", "c" }), "TSV has no quoting: a quote is ordinary text");
        check(DataFileFormats.Table("a,\u0301b\n", ',').Single().SequenceEqual(new[] { "a", "\u0301b" }), "A combining mark after a separator can't hide it");
        byte[] csvOut = DataFileFormats.Convert(Utf8("[{\"b\":\"2\",\"a\":\"x,y\"},{\"c\":{\"k\":[1]},\"a\":true}]"), "json", DataFileFormats.Formats.Single(f => f.Extension == "csv"));
        check(csvOut.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && Encoding.UTF8.GetString(csvOut[3..]) == "a,b,c\r\n\"x,y\",2,\r\ntrue,,\"{\"\"k\"\":[1]}\"\r\n",
            "Records become CSV with a BOM, columns in first-seen order and nested values as JSON text");
        check(Out("[[1,\"a\"],[2,null]]", "json", "tsv").TrimStart('\uFEFF') == "1\ta\r\n2\t\r\n", "Arrays of arrays become rows as they are");
        reject(() => Out("[{\"a\":\"x\\ty\"}]", "json", "tsv"), "TSV refuses a tab inside a cell");
        reject(() => Out("[1, 2]", "json", "csv"), "Only a list of records becomes a table");
        reject(() => Out("a,a\n1,2\n", "csv", "json"), "A header with the same column twice is refused");
        var longRow = Assert(() => Out("a\n1,2\n", "csv", "json"));
        check(longRow is UserError { Template: DataFileFormats.LongRow } e && e.Message == "Row 2 has more cells than the header." && e.Localized(t => t == DataFileFormats.LongRow ? "第 {0} 行太长" : t) == "第 2 行太长",
            "A row longer than the header is refused, naming the row in every language");
        reject(() => DataFileFormats.Table("a,\"b\n", ','), "An unclosed quote is refused");
        reject(() => DataFileFormats.Table("\n\n", ','), "A table with no rows is refused");
        check(Out("a,b\n1,2\n", "csv", "tsv").TrimStart('\uFEFF') == "a\tb\r\n1\t2\r\n", "CSV becomes TSV row by row");

        // JSON Lines.
        check(Out("{\"a\":1}\n\n  \n[2]\n", "ndjson", "json") == "[\n  {\n    \"a\": 1\n  },\n  [\n    2\n  ]\n]\n", "JSON Lines become an array, blank lines skipped");
        var badLine = Assert(() => Out("{}\n{oops}\n", "jsonl", "json"));
        check(badLine is UserError { Message: "Line 2 is not valid JSON." }, "A bad JSON line is named");
        reject(() => Out("{\"a\":1,\"a\":2}", "json", "yaml"), "A key appearing twice in one object is refused");
        reject(() => Out("{\"a\":1,}", "json", "yaml"), "Trailing commas are not JSON");
        reject(() => Out(string.Concat(Enumerable.Repeat("[", 130)) + string.Concat(Enumerable.Repeat("]", 130)), "json", "yaml"), "JSON nested deeper than 128 is refused");
        check(Out(string.Concat(Enumerable.Repeat("[", 128)) + string.Concat(Enumerable.Repeat("]", 128)), "json", "jsonl").Length > 0, "JSON nested 128 deep is converted");

        // XML property lists.
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>"
            + "<key>when</key><date>2024-02-03T04:05:06Z</date><key>blob</key><data>\n  AAEC\n</data><key>n</key><integer>-7</integer><key>u</key><integer>18446744073709551615</integer>"
            + "<key>r</key><real>0.5</real><key>ok</key><true/><key>s</key><string>a &amp; b</string><key>list</key><array><string/><false/></array></dict></plist>";
        check(Out(xml, "plist", "json") == "{\n  \"blob\": \"AAEC\",\n  \"list\": [\n    \"\",\n    false\n  ],\n  \"n\": -7,\n  \"ok\": true,\n  \"r\": 0.5,\n  \"s\": \"a & b\",\n  \"u\": 18446744073709551615,\n  \"when\": \"2024-02-03T04:05:06Z\"\n}\n",
            "An XML plist becomes JSON: dates as ISO 8601, data as Base64");
        string plistOut = Out(xml, "plist", "plist");
        check(plistOut.Contains("\t<key>blob</key>\n\t<data>AAEC</data>") && plistOut.Contains("<date>2024-02-03T04:05:06Z</date>") && plistOut.Contains("<integer>18446744073709551615</integer>") && plistOut.Contains("<string>a &amp; b</string>") && plistOut.EndsWith("</dict>\n</plist>\n"),
            "A plist written again keeps dates, data, unsigned integers and escapes text");
        check(Out(plistOut, "plist", "json") == Out(xml, "plist", "json"), "A written plist reads back to the same values");
        reject(() => Out("<plist><dict><key>a</key></dict></plist>", "plist", "json"), "A plist key without a value is refused");
        reject(() => Out("<!DOCTYPE plist [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><plist><string>&x;</string></plist>", "plist", "json"), "A plist can't pull in files through entities");
        reject(() => Out("<root/>", "plist", "json"), "A file that isn't a plist is refused");

        // Binary property lists.
        byte[] binary = Bplist(
            new byte[] { 0xD3, 1, 2, 3, 4, 5, 6 },           // 0: dict of 3 (keys 1-3, values 4-6)
            Ascii("arr"), Ascii("num"), Ascii("t"),          // 1-3
            new byte[] { 0xA2, 7, 7 },                       // 4: array referencing object 7 twice
            new byte[] { 0x11, 0x01, 0x00 },                 // 5: 256
            new byte[] { 0x09 },                             // 6: true
            new byte[] { 0x61, 0x4E, 0x2D });                // 7: "中" as UTF-16
        check(Out(binary, "plist", "json") == "{\n  \"arr\": [\n    \"中\",\n    \"中\"\n  ],\n  \"num\": 256,\n  \"t\": true\n}\n", "A binary plist is read, shared objects copied where referenced");
        reject(() => Out(Bplist(new byte[] { 0xA1, 0 }), "plist", "json"), "A binary plist that refers to itself is refused");
        reject(() => Out(Bplist(5, new byte[] { 0x09 }), "plist", "json"), "A binary plist whose top object is outside the table is refused");
        reject(() => Out(Bplist(new byte[] { 0x5F, 0x10, 0xFF, 0x41 }), "plist", "json"), "A binary plist string longer than the file is refused");
        reject(() => Out(Bplist(new byte[] { 0x80, 0x01 }), "plist", "json"), "A keyed archive UID is refused");
        // 2^20 copies: each array holds the next one twice.
        var doubling = Enumerable.Range(0, 24).Select(i => i < 23 ? new byte[] { 0xA2, (byte)(i + 1), (byte)(i + 1) } : new byte[] { 0x09 }).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        reject(() => Out(Bplist(doubling), "plist", "json"), "A binary plist that expands to millions of copies is refused");
        check(clock.ElapsedMilliseconds < 10_000, $"The expanding plist is refused quickly ({clock.ElapsedMilliseconds} ms)");

        // Review fixes: a table whose header repeats into a huge output is refused before writing.
        var wideHeader = new StringBuilder();
        for (int c = 0; c < 2000; c++) wideHeader.Append(c > 0 ? "," : "").Append(c).Append(new string('h', 7000));
        wideHeader.Append('\n');
        for (int r = 0; r < 2400; r++) wideHeader.Append(new string(',', 1999)).Append("x\n");
        clock.Restart();
        reject(() => Out(wideHeader.ToString(), "csv", "jsonl"), "A CSV whose header names repeat into gigabytes of JSON is refused");
        check(clock.ElapsedMilliseconds < 3_000, $"The repeating header is refused before records are built ({clock.ElapsedMilliseconds} ms)");
        check(Assert(() => Out("[\"\\ud800\"]", "json", "yaml")) is UserError { Message: DataFileFormats.BadJson } && Assert(() => Out("{}\n{\"\\udc00\":1}\n", "jsonl", "yaml")) is UserError { Message: "Line 2 is not valid JSON." },
            "A lone surrogate escape is reported as invalid JSON");
        string huge = "[1e400, " + "1" + new string('0', 400) + "]";
        check(Out(huge, "json", "json") == "[\n  1e400,\n  1" + new string('0', 400) + "\n]\n" && Out(huge, "json", "yaml") == "---\n- 1.0e+400\n- 1" + new string('0', 400) + "\n",
            "JSON numbers beyond a double keep their exact text in JSON and YAML");
        reject(() => Out(huge, "json", "plist"), "A plist refuses numbers beyond a double");
        check(Out("[1E+20, 2e-3, 3.5E2]", "json", "yaml") == "---\n- 1.0e+20\n- 2.0e-3\n- 3.5e+2\n", "YAML exponents get a dot and a sign so YAML 1.1 reads them as numbers");
        clock.Restart();
        reject(() => Out("[" + string.Join(",", Enumerable.Repeat("0", (int)DataFileFormats.MaxJsonValues)) + "]", "json", "yaml"), "JSON with more than 4 million values is refused before its tree is built");
        check(clock.ElapsedMilliseconds < 10_000, $"A huge flat JSON array is refused quickly ({clock.ElapsedMilliseconds} ms)");
        check(Out("[]", "json", "jsonl") == "\n", "An empty list becomes an empty line of JSON Lines, as on the Mac");
        check(Out("\"\r\n\",a\n1,2\n", "csv", "json").Contains("\"\\r\\n\": \"1\""), "A header of only a line break keeps its name, as on the Mac");
        reject(() => DataFileFormats.Convert(Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><plist><string>caf\u00e9</string></plist>"), "plist", DataFileFormats.Formats[0]), "An XML plist in a legacy encoding is refused");
        check(DataFileFormats.Short(new string('k', 200)).Length == 81 && DataFileFormats.Short("a\nb\u202E") == "aU+000AbU+202E", "Keys shown in messages are shortened and controls made visible");

        // Values formats can't hold.
        reject(() => Out("{\"a\": null}", "json", "plist"), "A plist can't hold null");
        reject(() => Out("[12345678901234567890123]", "json", "plist"), "A plist refuses an integer beyond 64 bits that a double would round");
        reject(() => Out("[0.1000000000000000000001]", "json", "plist"), "A plist refuses a number more precise than a double");
        check(Out("[1e20, 1.5, -0, 18446744073709551615]", "json", "plist").Contains("<real>1E+20</real>\n\t<real>1.5</real>\n\t<integer>-0</integer>\n\t<integer>18446744073709551615</integer>"), "Exact numbers become plist reals and integers");
        reject(() => Out("[1000000000000000000000000000000]", "json", "plist"), "A plist refuses an integer beyond 64 bits even when a double is close");
        string nan = "<plist><array><real>nan</real><real>-infinity</real></array></plist>";
        reject(() => Out(nan, "plist", "json"), "JSON refuses NaN and infinity");
        reject(() => Out(nan, "plist", "csv"), "CSV refuses NaN and infinity");
        check(Out(nan, "plist", "yaml") == "---\n- .nan\n- -.inf\n", "YAML writes NaN and infinity");
        reject(() => Out("{\"a\": \"\\u0001\"}", "json", "plist"), "A plist can't hold control characters");
        check(DataFileFormats.SameDecimal("1.50", "15e-1") && DataFileFormats.SameDecimal("-0.0", "0") && !DataFileFormats.SameDecimal("1", "10"), "Decimal texts compare by value");

        // Encodings and size.
        check(Out(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("{\"k\":\"值\"}")).ToArray(), "json", "jsonl") == "{\"k\":\"值\"}\n", "UTF-16 with a BOM is read");
        reject(() => Out(new byte[] { 0x7B, 0xC4, 0xE3, 0x7D }, "json", "yaml"), "GBK bytes are refused, not guessed");
        reject(() => DataFileFormats.Read(new byte[DataFileFormats.MaxBytes + 1], "json"), "Data files over 32 MB are refused");

        // Catalog: every other format, never the file's own; .ndjson counts as JSONL.
        var prefs = new Prefs();
        string[] Ids(params string[] files) => Catalog.Available(prefs, files).Select(a => a.Id).Where(id => id.StartsWith("data.")).ToArray();
        check(Ids(@"C:\a\x.json").SequenceEqual(new[] { "data.jsonl", "data.plist", "data.csv", "data.tsv", "data.yaml" }) && Ids(@"C:\a\x.ndjson").SequenceEqual(new[] { "data.json", "data.plist", "data.csv", "data.tsv", "data.yaml" })
            && Ids(@"C:\a\x.csv", @"C:\a\y.json").Length == 6 && Ids(@"C:\a\x.yaml").Length == 0 && Ids(@"C:\a\x.txt").Length == 0, "Data files are offered every other format");
    }

    private static Exception? Assert(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
}
