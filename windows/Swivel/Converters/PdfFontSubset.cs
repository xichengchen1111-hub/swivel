using System.Buffers.Binary;
using System.Text;

namespace Swivel;

/// A TrueType subsetter for embedding fonts in PDFs: keeps the outlines of the glyphs a document uses (plus the
/// components of composite glyphs), empties the rest and keeps glyph numbers unchanged, so text can be written with
/// glyph IDs directly (Identity encoding). Works on .ttf files and faces of .ttc collections. WPF's own
/// GlyphTypeface.ComputeSubset rejects some fonts (Calibri) and keeps large tables, so it isn't used.
sealed class PdfFontSubset
{
    readonly byte[] file;
    readonly Dictionary<string, (int Offset, int Length)> tables = [];
    readonly int numGlyphs;
    readonly bool longLoca;

    PdfFontSubset(byte[] file, int faceOffset)
    {
        this.file = file;
        int count = U16(faceOffset + 4);
        for (int i = 0; i < count; i++)
        {
            int rec = faceOffset + 12 + 16 * i;
            var tag = Encoding.ASCII.GetString(file, rec, 4);
            int offset = (int)U32(rec + 8), length = (int)U32(rec + 12);
            if (offset >= 0 && length >= 0 && (long)offset + length <= file.Length) tables[tag] = (offset, length);
        }
        foreach (var required in new[] { "head", "hhea", "maxp", "loca", "glyf", "hmtx" })
            if (!tables.ContainsKey(required)) throw new FormatException("missing " + required);
        numGlyphs = U16(tables["maxp"].Offset + 4);
        longLoca = (short)U16(tables["head"].Offset + 50) == 1;
    }

    int U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(o, 2));
    uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(o, 4));

    /// Opens the face matching the family and style names (for collections), or null when the font isn't TrueType
    /// (CFF-based OpenType, bitmap-only) or can't be read.
    public static PdfFontSubset? Open(byte[] file, IEnumerable<string> families, IEnumerable<string> faces, int glyphCount)
    {
        try
        {
            if (file.Length < 12) return null;
            var tag = Encoding.ASCII.GetString(file, 0, 4);
            var offsets = new List<int>();
            if (tag == "ttcf")
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8, 4));
                for (int i = 0; i < count && i < 64; i++) offsets.Add((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(12 + 4 * i, 4)));
            }
            else offsets.Add(0);
            var familySet = families.Select(f => f.ToLowerInvariant()).ToHashSet();
            var faceSet = faces.Select(f => f.ToLowerInvariant()).ToHashSet();
            PdfFontSubset? byCount = null;
            foreach (var offset in offsets)
            {
                uint version = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
                if (version != 0x00010000 && version != 0x74727565) continue; // 'OTTO' (CFF) can't be subset here
                var face = new PdfFontSubset(file, offset);
                if (offsets.Count == 1) return face;
                var (names, styles) = face.Names();
                if (names.Any(familySet.Contains) && (faceSet.Count == 0 || styles.Any(faceSet.Contains))) return face;
                if (byCount == null && face.numGlyphs == glyphCount && names.Any(familySet.Contains)) byCount = face;
            }
            return byCount;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// Family names (IDs 1 and 16) and style names (IDs 2 and 17) from the name table, lower-cased.
    (HashSet<string> Families, HashSet<string> Styles) Names()
    {
        var families = new HashSet<string>();
        var styles = new HashSet<string>();
        if (!tables.TryGetValue("name", out var t)) return (families, styles);
        int count = U16(t.Offset + 2), strings = t.Offset + U16(t.Offset + 4);
        for (int i = 0; i < count; i++)
        {
            int rec = t.Offset + 6 + 12 * i;
            int platform = U16(rec), nameId = U16(rec + 6), length = U16(rec + 8), offset = U16(rec + 10);
            if (nameId is not (1 or 2 or 16 or 17) || strings + offset + length > file.Length) continue;
            string value = platform is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(file, strings + offset, length)
                : Encoding.Latin1.GetString(file, strings + offset, length);
            (nameId is 1 or 16 ? families : styles).Add(value.ToLowerInvariant());
        }
        return (families, styles);
    }

    (int Offset, int Length) Glyph(int g)
    {
        var loca = tables["loca"].Offset;
        int start, end;
        if (longLoca)
        {
            start = (int)U32(loca + 4 * g);
            end = (int)U32(loca + 4 * g + 4);
        }
        else
        {
            start = U16(loca + 2 * g) * 2;
            end = U16(loca + 2 * g + 2) * 2;
        }
        if (end < start || start > tables["glyf"].Length) return (0, 0);
        end = Math.Min(end, tables["glyf"].Length);
        return (tables["glyf"].Offset + start, end - start);
    }

    /// Builds the subset. `unicode` gives a small cmap so viewers that want one are happy.
    public byte[] Build(IEnumerable<ushort> glyphs, IReadOnlyDictionary<ushort, string> unicode)
    {
        // The glyphs and, for composite glyphs, their components.
        var keep = new SortedSet<int> { 0 };
        var queue = new Queue<int>(glyphs.Select(g => (int)g).Where(g => g < numGlyphs));
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            if (!keep.Add(g) && g != 0) continue;
            var (offset, length) = Glyph(g);
            if (length < 10 || (short)U16(offset) >= 0) continue;
            int p = offset + 10;
            while (p + 4 <= offset + length)
            {
                int flags = U16(p), component = U16(p + 2);
                if (component < numGlyphs && !keep.Contains(component)) queue.Enqueue(component);
                p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) p += 2;
                else if ((flags & 0x0040) != 0) p += 4;
                else if ((flags & 0x0080) != 0) p += 8;
                if ((flags & 0x0020) == 0) break;
            }
        }

        var glyf = new MemoryStream();
        var loca = new byte[(numGlyphs + 1) * 4];
        for (int g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * g), (uint)glyf.Length);
            if (!keep.Contains(g)) continue;
            var (offset, length) = Glyph(g);
            glyf.Write(file, offset, length);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * numGlyphs), (uint)glyf.Length);

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] Copy(string tag) => file.AsSpan(tables[tag].Offset, tables[tag].Length).ToArray();
        var head = Copy("head");
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        output["head"] = head;
        output["hhea"] = Copy("hhea");
        output["maxp"] = Copy("maxp");
        output["hmtx"] = Copy("hmtx");
        output["loca"] = loca;
        output["glyf"] = glyf.ToArray();
        foreach (var optional in new[] { "cvt ", "fpgm", "prep", "OS/2", "name" })
            if (tables.ContainsKey(optional)) output[optional] = Copy(optional);
        if (tables.TryGetValue("post", out var post) && post.Length >= 32)
        {
            var p = file.AsSpan(post.Offset, 32).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(p, 0x00030000);
            output["post"] = p;
        }
        output["cmap"] = Cmap(unicode);
        return Assemble(output);
    }

    /// A format 4 (BMP) cmap with one segment per character.
    static byte[] Cmap(IReadOnlyDictionary<ushort, string> unicode)
    {
        var map = new SortedDictionary<ushort, ushort>();
        foreach (var (glyph, text) in unicode)
            if (text.Length == 1 && !char.IsSurrogate(text[0]) && text[0] != 0xFFFF) map.TryAdd(text[0], glyph);
        int segments = map.Count + 1;
        int length = 16 + segments * 8;
        var b = new byte[12 + length];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1);           // one subtable
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 3);           // Windows
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 1);           // Unicode BMP
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 12);
        var t = s[12..];
        BinaryPrimitives.WriteUInt16BigEndian(t, 4);
        BinaryPrimitives.WriteUInt16BigEndian(t[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(t[6..], (ushort)(segments * 2));
        int pow = 1, log = 0;
        while (pow * 2 <= segments) { pow *= 2; log++; }
        BinaryPrimitives.WriteUInt16BigEndian(t[8..], (ushort)(pow * 2));
        BinaryPrimitives.WriteUInt16BigEndian(t[10..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(t[12..], (ushort)(segments * 2 - pow * 2));
        int ends = 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        int i = 0;
        foreach (var (c, g) in map)
        {
            BinaryPrimitives.WriteUInt16BigEndian(t[(ends + 2 * i)..], c);
            BinaryPrimitives.WriteUInt16BigEndian(t[(starts + 2 * i)..], c);
            BinaryPrimitives.WriteUInt16BigEndian(t[(deltas + 2 * i)..], (ushort)(g - c));
            i++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(t[(ends + 2 * i)..], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(t[(starts + 2 * i)..], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(t[(deltas + 2 * i)..], 1);
        _ = ranges;
        return b;
    }

    static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }
        return sum;
    }

    static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int pow = 1, log = 0;
        while (pow * 2 <= count) { pow *= 2; log++; }
        int headerLength = 12 + 16 * count;
        int total = headerLength + tables.Values.Sum(t => (t.Length + 3) / 4 * 4);
        var font = new byte[total];
        var s = font.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(pow * 16));
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)(count * 16 - pow * 16));
        int offset = headerLength, index = 0, headOffset = -1;
        foreach (var (tag, data) in tables)
        {
            int rec = 12 + 16 * index++;
            Encoding.ASCII.GetBytes(tag).CopyTo(font, rec);
            BinaryPrimitives.WriteUInt32BigEndian(s[(rec + 4)..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(s[(rec + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(s[(rec + 12)..], (uint)data.Length);
            data.CopyTo(font, offset);
            if (tag == "head") headOffset = offset;
            offset += (data.Length + 3) / 4 * 4;
        }
        if (headOffset >= 0) BinaryPrimitives.WriteUInt32BigEndian(s[(headOffset + 8)..], 0xB1B0AFBA - Checksum(font));
        return font;
    }
}
