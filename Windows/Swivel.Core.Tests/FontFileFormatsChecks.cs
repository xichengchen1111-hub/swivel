using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Swivel.Core;

internal static class FontFileFormatsChecks
{
    // Synthetic fonts made with fontTools' FontBuilder for these checks (no third-party font data):
    // a TrueType font with an empty glyph, a two-contour glyph with a quadratic curve and
    // instructions, a triangle with large coordinates, a scaled composite and an empty space; a
    // CFF font; and fontTools' WOFF2 of each, the TrueType one with the glyf, loca and hmtx transforms.
    private const string Ttf = "AAEAAAAKAIAAAwAgT1MvMkEXQfMAAAEoAAAAYGNtYXAAeQBbAAABnAAAADxnbHlmq+P0xAAAAeQAAABiaGVhZKWnhBYAAACsAAAANmhoZWF7pIuAAAAA5AAAACRobXR4DB0AggAAAYgAAAAUbG9jYQBIAFUAAAHYAAAADG1heHAADQAWAAABCAAAACBuYW1ld2HxOgAAAkgAAABscG9zdEHUrfIAAAK0AAAAPAABAAAAAQAA/MbNzV8PPPUAAwPoAAAAAOcHXlkAAAAA5wdeWf/OgwB4tAOEAAAAAwACAAAAAAAAAAEAAAMg/zgAAAKA/86Jwni0AAEAAAAAAAAAAAAAAAAAAAAFAAEAAAAFAAgAAgALAAMAAgAAAAAAAAAAAAAAAAACAAEAAwJsAZAABQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAPz8/PwAAACAAQwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAACWAAAAmIAZAJs/84CdgBQAoAAAAAAAAIAAAADAAAAFAADAAEAAAAUAAQAKAAAAAYABAABAAIAIABD//8AAAAgAEH////k/8AAAQAAAAAAAAAAAAAAFwAkADEAMQACAGT+1AK8A4QABAAHAAOwBSwzESQFESUhEWQBLAEs/gwBkAK8yMj9RGT+cAAAAf/OgwB1MAADAAIAACMlATITumGoA4L9AP//AFCDAHi0AZoALgABHtggAAAHAAIDhAAAAAAAAAAEADYAAQAAAAAAAQALAAAAAQAAAAAAAgAHAAsAAwABBAkAAQAWABIAAwABBAkAAgAOAChTd2l2ZWwgVGVzdFJlZ3VsYXIAUwB3AGkAdgBlAGwAIABUAGUAcwB0AFIAZQBnAHUAbABhAHIAAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFAAABAgEDAQQAAwZzcXVhcmUDdHJpBGNvbXA=";
    private const string Otf = "T1RUTwAJAIAAAwAQQ0ZGIMYmrAUAAAIsAAAAWE9TLzJBOEF5AAABAAAAAGBjbWFwAAwAlAAAAdgAAAA0aGVhZC8Y/+oAAACcAAAANmhoZWEFFgFgAAAA1AAAACRobXR4AiYAAAAAAoQAAAAGbWF4cAACUAAAAAD4AAAABm5hbWW+HzTyAAABYAAAAHhwb3N0AAMAAAAAAgwAAAAgAAEAAAABAADvEvexXw889QADA+gAAAAA5wdeWQAAAADnB15ZADIAAAHCAlgAAAADAAIAAAAAAAAAAQAAAyD/OAAAAfQAMgAyAcIAAQAAAAAAAAAAAAAAAAAAAAEAAFAAAAIAAAADAfQBkAAFAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAA/Pz8/AAAAQQBBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAQANgABAAAAAAABAA8AAAABAAAAAAACAAcADwADAAEECQABAB4AFgADAAEECQACAA4ANFN3aXZlbCBUZXN0IENGRlJlZ3VsYXIAUwB3AGkAdgBlAGwAIABUAGUAcwB0ACAAQwBGAEYAUgBlAGcAdQBsAGEAcgAAAAIAAAADAAAAFAADAAEAAAAUAAQAIAAAAAQABAABAAAAQf//AAAAQf///8AAAQAAAAAAAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEABAEAAQEBDlN3aXZlbFRlc3RDRkYAAQEBEvgbAr2L+Fb47AXND4vjEtARAAEBARBTd2l2ZWwgVGVzdCBDRkYAAAAAXQACAQEEDviIDviIvRb4JPjsBg4B9AAAADIAAA==";
    private const string TransformedWoff2 = "d09GMgABAAAAAAGcAAoAAAAAAvQAAAFUAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAABmAAPApocQE2AiRDFAsLDAAEIAVsBzwbOQIAPg1ocVhOPbYVhu9ULRrjJHQQo+Ra+/Tss38JCBEPxdrr2927IMnMybCM4lYYYBkCVQYfI2tElahnYao/MuEWAAcUgJ3fLDKEmprgwKtYDhRANsDX3bEG+KcdXMLzCbLCiypcwXUuF7p4GgFnDJhOjm9nzCDQQAKxRFvzUMIIA9Pcxq5TFPS1q40qL+NKqwEZsK2FznWv6ECiMKMEZjQW0KcRUud6orMb7wKAFgyhMIw5TGAEhRQ9aauG259jfzSH3r89/L58v684/Wrv21usifkf0C/rCxNwY148vhAIvq6Ob/xH/YO/9lB77yrtCiVBUF1P8kOCM0UAANrKB7mfl/ksPwi0BqRhJQFACjTWBQjDCJD6DSvRDApzpnyWxiy0s2dHl3eubf3UFdz23Jlnjlzq3HHksSduOXLiqUt7HklUtkRGRaP6RjLWk0dnzd8JAA==";
    private const string OtfWoff2 = "d09GMk9UVE8AAAFMAAkAAAAAAowAAAEKAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAADVgGYAA0ATYCJAMGBAYFeAcgG+kBCJ4FdtMHbY8t26vwKMzSQgjjL57Hlu39mblO6ZKs0HRXhaE7nEciJUEohENC/DW31jZPTeVDNltEeLpajAvHED5Npx+cgRLW2lByXKkwZWXvl7AbHIe8yAaDLgHhYgsNkuiCg+0NtjA5ht+Z6aNFkq3nOw+tc+2iCpfsrb3o9dv6cbf77GdXkp2dAgpa8M4nbWv9kRc6Wsf6SdXM8wmooQ9cR+FWputAQcWBKjjQdGia4LbvbfovgWn4teW0nRZS6z/8cQ1tiuS7JKAqINSuvyJzJ06yAACSOU54gILmQkBsI6AY2ValTcSxo1YVW87uzR+p76O5poOMeEHLJu4pVdHrZx6fIAA=";

    private static byte[] B(string base64) => Convert.FromBase64String(base64);
    private static FontFileFormats.Table T(FontFileFormats.Font font, string tag) => font.Tables.Single(t => t.Tag == BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(tag)));

    /// A glyph as its outline: contour ends, points with on-curve flags, instructions; composites as their component bytes.
    private static string Glyph(byte[] glyf, int start, int end)
    {
        if (start == end) return "empty";
        var d = glyf[start..end];
        short contours = BinaryPrimitives.ReadInt16BigEndian(d);
        string box = string.Join(",", Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16BigEndian(d[(2 + 2 * i)..])));
        if (contours < 0)
        {
            // Components to the end, without instructions: their bytes are copied unchanged.
            int at = 10; var parts = new List<string>(); bool more = true;
            while (more)
            {
                int f = BinaryPrimitives.ReadUInt16BigEndian(d[at..]); more = (f & 0x20) != 0;
                int size = 4 + ((f & 1) != 0 ? 4 : 2) + ((f & 8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0);
                parts.Add(Convert.ToHexString(d.AsSpan(at, size))); at += size;
            }
            return "composite " + box + " " + string.Join("|", parts);
        }
        int pos = 10; var ends = new List<int>();
        for (int c = 0; c < contours; c++) { ends.Add(BinaryPrimitives.ReadUInt16BigEndian(d[pos..])); pos += 2; }
        int il = BinaryPrimitives.ReadUInt16BigEndian(d[pos..]); pos += 2;
        string instructions = Convert.ToHexString(d.AsSpan(pos, il)); pos += il;
        int n = ends[^1] + 1; var flags = new List<int>();
        while (flags.Count < n) { int f = d[pos++]; flags.Add(f); if ((f & 8) != 0) { int r = d[pos++]; for (int k = 0; k < r; k++) flags.Add(f); } }
        int[] Coords(int shortBit, int sameBit)
        {
            var values = new int[n]; int v = 0;
            for (int p = 0; p < n; p++)
            {
                int f = flags[p];
                if ((f & shortBit) != 0) { int delta = d[pos++]; v += (f & sameBit) != 0 ? delta : -delta; }
                else if ((f & sameBit) == 0) { v += BinaryPrimitives.ReadInt16BigEndian(d[pos..]); pos += 2; }
                values[p] = v;
            }
            return values;
        }
        var xs = Coords(2, 16); var ys = Coords(4, 32);
        return $"simple {box} ends {string.Join(",", ends)} instr {instructions} points " + string.Join(" ", Enumerable.Range(0, n).Select(p => $"{xs[p]},{ys[p]}{((flags[p] & 1) != 0 ? "" : "~")}"));
    }

    private static List<string> Glyphs(FontFileFormats.Font font)
    {
        var glyf = T(font, "glyf").Data; var loca = T(font, "loca").Data; var head = T(font, "head").Data;
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) == 1;
        int count = loca.Length / (longLoca ? 4 : 2) - 1;
        int Offset(int g) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(4 * g)) : 2 * BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(2 * g));
        return Enumerable.Range(0, count).Select(g => Glyph(glyf, Offset(g), Offset(g + 1))).ToList();
    }

    private static Exception? Assert(Action action) { try { action(); return null; } catch (Exception error) { return error; } }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var ttf = FontFileFormats.Read(B(Ttf)); var otf = FontFileFormats.Read(B(Otf));
        check(!ttf.IsCff && otf.IsCff && ttf.Tables.Count == 10 && otf.Tables.Count == 9, $"The fixtures read as TrueType ({ttf.Tables.Count} tables) and CFF ({otf.Tables.Count} tables)");
        var expected = Glyphs(ttf);
        check(expected.Count == 5 && expected[0] == "empty" && expected[1] == "simple 100,-300,700,900 ends 4,7 instr B0052C points 100,0 100,700 400,900~ 700,700 700,0 200,100 600,100 600,-300"
            && expected[2] == "simple -50,-32000,30000,3 ends 2 instr  points -50,0 5000,3 30000,-32000" && expected[3].StartsWith("composite ") && expected[4] == "empty",
            "The TrueType fixture's glyphs read as drawn: " + string.Join(" / ", expected));

        // fontTools' transformed WOFF2: glyphs, loca and hmtx rebuilt to the same outlines and metrics.
        var unpacked = FontFileFormats.Read(B(TransformedWoff2));
        check(Glyphs(unpacked).SequenceEqual(expected), "A WOFF2 with transformed glyf/loca unpacks to the same outlines: " + string.Join(" / ", Glyphs(unpacked)));
        check(T(unpacked, "hmtx").Data.SequenceEqual(T(ttf, "hmtx").Data), "A WOFF2 with transformed hmtx unpacks to the same metrics");
        foreach (var tag in new[] { "cmap", "head", "hhea", "maxp", "name", "OS/2", "post" })
            // WOFF2 encoders set head.flags bit 11 after transforming glyphs, and the checksum adjustment is recomputed.
            check(tag == "head" ? T(unpacked, tag).Data.AsSpan(18).SequenceEqual(T(ttf, tag).Data.AsSpan(18)) && T(unpacked, tag).Data.AsSpan(12, 4).SequenceEqual(T(ttf, tag).Data.AsSpan(12, 4))
                : T(unpacked, tag).Data.SequenceEqual(T(ttf, tag).Data), $"The {tag} table is the same after unpacking");
        var (desktop, extension) = FontFileFormats.Convert(B(TransformedWoff2), FontFileFormats.Desktop);
        check(extension == "ttf" && FontFileFormats.Checksum(desktop) == 0xB1B0AFBA && Glyphs(FontFileFormats.Read(desktop)).SequenceEqual(expected), "Unpacking a TrueType web font gives a TTF whose whole-file checksum is the sfnt constant");
        var (cff, cffExtension) = FontFileFormats.Convert(B(OtfWoff2), FontFileFormats.Desktop);
        check(cffExtension == "otf" && T(FontFileFormats.Read(cff), "CFF ").Data.SequenceEqual(T(otf, "CFF ").Data), "Unpacking a CFF web font gives an OTF with the same CFF table");

        // Desktop → WOFF and WOFF2 → back: every table but head (whose checksum adjustment is recomputed) byte for byte.
        foreach (var (source, name) in new[] { (B(Ttf), "TTF"), (B(Otf), "OTF") })
            foreach (var format in new[] { FontFileFormats.Woff, FontFileFormats.Woff2 })
            {
                var (web, webExtension) = FontFileFormats.Convert(source, format);
                var original = FontFileFormats.Read(source); var back = FontFileFormats.Read(web);
                bool same = original.Tables.All(t => back.Tables.Single(b => b.Tag == t.Tag).Data.AsSpan(t.Tag == 0x68656164 ? 12 : 0).SequenceEqual(t.Data.AsSpan(t.Tag == 0x68656164 ? 12 : 0)));
                check(webExtension == format.Name.ToLowerInvariant() && back.Flavor == original.Flavor && same, $"{name} → {format.Name} keeps every table");
            }
        var woff = FontFileFormats.Convert(B(Ttf), FontFileFormats.Woff).Bytes;
        check(BinaryPrimitives.ReadUInt32BigEndian(woff) == 0x774F4646 && BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(8)) == woff.Length && woff.Length % 4 == 0, "WOFF output has the signature and its own length");
        var woff2 = FontFileFormats.Convert(B(Ttf), FontFileFormats.Woff2).Bytes;
        check(BinaryPrimitives.ReadUInt32BigEndian(woff2) == 0x774F4632 && BinaryPrimitives.ReadUInt32BigEndian(woff2.AsSpan(8)) == woff2.Length && woff2.Length % 4 == 0, "WOFF2 output has the signature and its own length");

        // Refusals.
        reject(() => FontFileFormats.Read(new byte[] { 0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 0 }), "A font collection (TTC) is refused");
        reject(() => FontFileFormats.Read(new byte[12]), "Bytes that aren't a font are refused");
        reject(() => FontFileFormats.Read(new byte[FontFileFormats.MaxBytes + 1]), "Fonts over 64 MB are refused");
        var bomb = (byte[])woff.Clone(); BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(44 + 12), 0x7FFFFFF0);
        reject(() => FontFileFormats.Read(bomb), "A WOFF table declaring a huge unpacked size is refused before inflating");
        var wrongLength = (byte[])woff.Clone(); int first = 44; uint stored = BinaryPrimitives.ReadUInt32BigEndian(wrongLength.AsSpan(first + 8)), orig = BinaryPrimitives.ReadUInt32BigEndian(wrongLength.AsSpan(first + 12));
        if (stored < orig) { BinaryPrimitives.WriteUInt32BigEndian(wrongLength.AsSpan(first + 12), orig + 1); reject(() => FontFileFormats.Read(wrongLength), "A WOFF table that inflates to another length is refused"); }
        var wrap = (byte[])woff.Clone(); BinaryPrimitives.WriteUInt32BigEndian(wrap.AsSpan(44 + 12), 0xFFFFFFFE);
        check(Assert(() => FontFileFormats.Read(wrap)) is UserError { Message: FontFileFormats.Damaged }, "A WOFF table declaring a length near 4 GB is refused as damaged, not with an overflow");
        var flavorless = (byte[])woff.Clone(); BinaryPrimitives.WriteUInt32BigEndian(flavorless.AsSpan(4), 0x41424344);
        reject(() => FontFileFormats.Read(flavorless), "A web font with an unknown flavor is refused");
        var big2 = B(TransformedWoff2); BinaryPrimitives.WriteUInt32BigEndian(big2.AsSpan(16), FontFileFormats.MaxBytes + 1u);
        reject(() => FontFileFormats.Read(big2), "A WOFF2 declaring more than 64 MB unpacked is refused");
        var cut = B(TransformedWoff2)[..200];
        reject(() => FontFileFormats.Read(cut), "A truncated WOFF2 is refused");
        var flipped = B(TransformedWoff2); flipped[^20] ^= 0xFF;
        reject(() => FontFileFormats.Read(flipped), "A WOFF2 with damaged compressed data is refused");
        var sfnt = B(Ttf); BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(12 + 8), 0xFFFFFF00);
        reject(() => FontFileFormats.Read(sfnt), "A table pointing outside the file is refused");
        var noHead = B(Ttf); for (int i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(noHead.AsSpan(4)); i++) if (BinaryPrimitives.ReadUInt32BigEndian(noHead.AsSpan(12 + 16 * i)) == 0x68656164) noHead[12 + 16 * i] = (byte)'x';
        reject(() => FontFileFormats.Read(noHead), "A font without a head table is refused");
        int Fuzz(int seed, byte[] source)
        {
            var random = new Random(seed); int refused = 0;
            for (int i = 0; i < 300; i++)
            {
                var copy = (byte[])source.Clone();
                for (int k = 0; k < 1 + random.Next(4); k++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                try { FontFileFormats.Convert(copy, FontFileFormats.Desktop); } catch (UserError) { refused++; }
            }
            return refused;
        }
        // Damaged inputs either convert or are refused with a user message, never another exception.
        Fuzz(1, B(TransformedWoff2)); Fuzz(2, woff); Fuzz(3, B(Ttf));
        check(true, "Randomly damaged WOFF2, WOFF and TTF inputs give only user-facing refusals");

        // Catalog: WOFF/WOFF2 for any font but its own format; unpacking only for web fonts.
        var prefs = new Prefs();
        string[] Ids(params string[] files) => Catalog.Available(prefs, files).Select(a => a.Id).Where(id => id.StartsWith("font.")).ToArray();
        check(Ids(@"C:\a\x.ttf").SequenceEqual(new[] { "font.woff", "font.woff2" }) && Ids(@"C:\a\x.woff2").SequenceEqual(new[] { "font.desktop", "font.woff" })
            && Ids(@"C:\a\x.woff", @"C:\a\y.woff2").Length == 3 && Ids(@"C:\a\x.ttf", @"C:\a\y.woff").SequenceEqual(new[] { "font.woff", "font.woff2" }) && Ids(@"C:\a\x.ttc").Length == 0,
            "Fonts are offered every other format; only web fonts are unpacked");
    }
}
