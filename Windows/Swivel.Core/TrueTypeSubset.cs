using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// One face of a TrueType-outline font (a .ttf, or one face of a .ttc), read only as far as
/// the PDF text export needs: Unicode cmap (formats 4 and 12), metrics, glyph outlines and the
/// OS/2 embedding permission. Written from the OpenType specification; every offset is checked.
/// PostScript-outline (CFF) fonts are not used.
public sealed class TrueTypeFace
{
    public const int MaxFontBytes = 64 * 1024 * 1024, MaxComponentDepth = 16, MaxComponents = 1024;
    public const string Damaged = "A font Swivel tried to embed in the PDF is damaged or not a TrueType-outline font.";
    private const uint Head = 0x68656164, Hhea = 0x68686561, Maxp = 0x6D617870, Hmtx = 0x686D7478, Loca = 0x6C6F6361, Glyf = 0x676C7966,
        Cmap = 0x636D6170, Os2 = 0x4F532F32, Name = 0x6E616D65, Post = 0x706F7374, Cvt = 0x63767420, Fpgm = 0x6670676D, Prep = 0x70726570;

    private readonly byte[] data;
    private readonly Dictionary<uint, (int Offset, int Length)> tables = new();
    private readonly int cmapOffset, cmapFormat;
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public int FsType { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int CapHeight { get; }
    public (int XMin, int YMin, int XMax, int YMax) Box { get; }
    public string PostScriptName { get; }
    private readonly bool longLoca;
    private readonly int metricCount;

    /// The OpenType fsType rule: restricted-license (0x0002 alone), bitmap-only (0x0200) or
    /// no-subsetting (0x0100) fonts are never embedded. Installable, preview-and-print and
    /// editable fonts are.
    public bool MayEmbedSubset => ((FsType & 0x0002) == 0 || (FsType & 0x000C) != 0) && (FsType & 0x0300) == 0;

    private TrueTypeFace(byte[] data, int faceOffset)
    {
        this.data = data;
        uint flavor = U32(faceOffset);
        if (flavor is not (0x00010000u or 0x74727565u)) throw new UserError(Damaged);
        int count = U16(faceOffset + 4);
        if (count is 0 or > 1024) throw new UserError(Damaged);
        Need(faceOffset + 12, count * 16);
        for (int i = 0; i < count; i++)
        {
            int at = faceOffset + 12 + i * 16; uint tag = U32(at); long offset = U32(at + 8), length = U32(at + 12);
            if (offset > data.Length || length > data.Length - offset || !tables.TryAdd(tag, ((int)offset, (int)length))) throw new UserError(Damaged);
        }
        foreach (uint tag in new[] { Head, Hhea, Maxp, Hmtx, Loca, Glyf, Cmap, Os2 }) if (!tables.ContainsKey(tag)) throw new UserError(Damaged);
        var head = Table(Head, 54); var hhea = Table(Hhea, 36); var maxp = Table(Maxp, 6); var os2 = Table(Os2, 10);
        if (U32(head.Offset + 12) != 0x5F0F3CF5) throw new UserError(Damaged);
        UnitsPerEm = U16(head.Offset + 18);
        if (UnitsPerEm is < 16 or > 16384) throw new UserError(Damaged);
        Box = (S16(head.Offset + 36), S16(head.Offset + 38), S16(head.Offset + 40), S16(head.Offset + 42));
        int locFormat = S16(head.Offset + 50);
        if (locFormat is not (0 or 1)) throw new UserError(Damaged);
        longLoca = locFormat == 1;
        GlyphCount = U16(maxp.Offset + 4);
        metricCount = U16(hhea.Offset + 34);
        if (GlyphCount == 0 || metricCount == 0 || metricCount > GlyphCount) throw new UserError(Damaged);
        Ascender = S16(hhea.Offset + 4); Descender = S16(hhea.Offset + 6);
        Table(Hmtx, metricCount * 4 + (GlyphCount - metricCount) * 2);
        Table(Loca, (GlyphCount + 1) * (longLoca ? 4 : 2));
        FsType = U16(os2.Offset + 8);
        CapHeight = U16(os2.Offset) >= 2 && tables[Os2].Length >= 90 ? S16(os2.Offset + 88) : Ascender;
        PostScriptName = ReadPostScriptName();
        (cmapOffset, cmapFormat) = ChooseCmap();
    }

    /// `face` selects a face of a TrueType collection; it must be 0 for a single font.
    public static TrueTypeFace Parse(byte[] data, int face = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxFontBytes || data.Length < 12) throw new UserError(Damaged);
        int offset = 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) == 0x74746366) // 'ttcf'
        {
            uint faces = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            if (face < 0 || face >= faces || faces > 1024 || 12 + (long)faces * 4 > data.Length) throw new UserError(Damaged);
            long at = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + face * 4));
            if (at > data.Length - 12) throw new UserError(Damaged);
            offset = (int)at;
        }
        else if (face != 0) throw new UserError(Damaged);
        return new TrueTypeFace(data, offset);
    }

    // MARK: lookups

    /// The glyph for a Unicode scalar value, or 0 when the font has none.
    public int Glyph(int scalar)
    {
        if (scalar is < 0 or > 0x10FFFF) return 0;
        int t = cmapOffset;
        if (cmapFormat == 12)
        {
            long groups = U32(t + 12); int lo = 0, hi = (int)groups - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1, g = t + 16 + mid * 12; long start = U32(g), end = U32(g + 4);
                if (scalar < start) hi = mid - 1; else if (scalar > end) lo = mid + 1;
                else { long glyph = U32(g + 8) + (scalar - start); return glyph < GlyphCount ? (int)glyph : 0; }
            }
            return 0;
        }
        if (scalar > 0xFFFF) return 0;
        int segments = U16(t + 6) / 2, ends = t + 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        int a = 0, b = segments - 1;
        while (a < b) { int m = (a + b) >>> 1; if (U16(ends + m * 2) < scalar) a = m + 1; else b = m; }
        {
            int mid = a;
            if (U16(ends + mid * 2) < scalar) return 0;
            int start = U16(starts + mid * 2);
            if (scalar < start) return 0;
            int delta = U16(deltas + mid * 2), range = U16(ranges + mid * 2), glyph;
            if (range == 0) glyph = (scalar + delta) & 0xFFFF;
            else
            {
                int at = ranges + mid * 2 + range + (scalar - start) * 2;
                var cmap = tables[Cmap];
                if (at < cmap.Offset || at + 2 > cmap.Offset + cmap.Length) return 0;
                glyph = U16(at); if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
            }
            return glyph < GlyphCount ? glyph : 0;
        }
    }

    public int Advance(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount) throw new ArgumentOutOfRangeException(nameof(glyph));
        return U16(tables[Hmtx].Offset + Math.Min(glyph, metricCount - 1) * 4);
    }
    /// Whether the glyph has outline data (an empty glyph draws nothing).
    public bool HasOutline(int glyph) => glyph >= 0 && glyph < GlyphCount && GlyphData(glyph).Length > 0;
    private int SideBearing(int glyph) => glyph < metricCount ? S16(tables[Hmtx].Offset + glyph * 4 + 2) : S16(tables[Hmtx].Offset + metricCount * 4 + (glyph - metricCount) * 2);

    private (int Offset, int Length) GlyphData(int glyph)
    {
        var loca = tables[Loca]; var glyf = tables[Glyf];
        long start = longLoca ? U32(loca.Offset + glyph * 4) : U16(loca.Offset + glyph * 2) * 2L, end = longLoca ? U32(loca.Offset + glyph * 4 + 4) : U16(loca.Offset + glyph * 2 + 2) * 2L;
        if (start > end || end > glyf.Length) throw new UserError(Damaged);
        if (end > start && end - start < 10) throw new UserError(Damaged);
        return (glyf.Offset + (int)start, (int)(end - start));
    }

    /// Positions of the glyph-index fields of a composite glyph's components (empty for a simple glyph).
    private List<int> ComponentFields(int glyph)
    {
        var fields = new List<int>(); var (at, length) = GlyphData(glyph);
        if (length == 0 || S16(at) >= 0) return fields;
        int p = at + 10, end = at + length;
        for (;;)
        {
            if (p + 4 > end || fields.Count >= MaxComponents) throw new UserError(Damaged);
            int flags = U16(p); fields.Add(p + 2);
            if (U16(p + 2) >= GlyphCount) throw new UserError(Damaged);
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2) + ((flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0);
            if (p > end) throw new UserError(Damaged);
            if ((flags & 0x0020) == 0) break;
        }
        return fields;
    }

    // MARK: subsetting

    public sealed record Subset(byte[] Font, IReadOnlyDictionary<int, int> NewGlyphs, IReadOnlyList<int> Advances);

    /// A standalone TrueType font holding glyph 0, the requested glyphs and every glyph their
    /// composites use, renumbered in original order; `unicode` maps scalars to original glyphs
    /// for the minimal cmap. Hinting tables are copied unchanged; the OS/2 and name tables are
    /// copied so system rasterizers accept the font.
    public Subset Build(IEnumerable<int> glyphs, IReadOnlyDictionary<int, int> unicode, CancellationToken token)
    {
        if (!MayEmbedSubset) throw new UserError(Damaged);
        var state = new Dictionary<int, int>(); // 1 visiting, 2 done
        void Visit(int glyph, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (glyph < 0 || glyph >= GlyphCount) throw new UserError(Damaged);
            if (state.TryGetValue(glyph, out int s)) { if (s == 1) throw new UserError(Damaged); return; }
            if (depth > MaxComponentDepth) throw new UserError(Damaged);
            state[glyph] = 1;
            foreach (int field in ComponentFields(glyph)) Visit(U16(field), depth + 1);
            state[glyph] = 2;
        }
        Visit(0, 0);
        foreach (int g in glyphs) Visit(g, 0);
        var order = state.Keys.OrderBy(x => x).ToArray();
        if (order.Length > 65535) throw new UserError(Damaged);
        var map = new Dictionary<int, int>();
        for (int i = 0; i < order.Length; i++) map[order[i]] = i;

        var glyf = new List<byte>(); var loca = new byte[(order.Length + 1) * 4]; var hmtx = new byte[order.Length * 4]; var advances = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            int old = order[i]; var (at, length) = GlyphData(old);
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)glyf.Count);
            var body = data.AsSpan(at, length).ToArray();
            foreach (int field in ComponentFields(old)) BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(field - at), (ushort)map[U16(field)]);
            glyf.AddRange(body); while (glyf.Count % 4 != 0) glyf.Add(0);
            advances[i] = Advance(old);
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)advances[i]); BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2), (short)SideBearing(old));
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(order.Length * 4), (uint)glyf.Count);
        var head = Copy(Head); BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        var hhea = Copy(Hhea); BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)order.Length);
        var maxp = Copy(Maxp); BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)order.Length);
        var post = new byte[32];
        if (tables.TryGetValue(Post, out var original) && original.Length >= 32) Array.Copy(data, original.Offset, post, 0, 32);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        var mapped = unicode.Where(x => map.ContainsKey(x.Value)).ToDictionary(x => x.Key, x => map[x.Value]);
        var list = new List<FontFileFormats.Table>
        {
            new(Head, head), new(Hhea, hhea), new(Maxp, maxp), new(Hmtx, hmtx), new(Loca, loca), new(Glyf, glyf.ToArray()),
            new(Cmap, MinimalCmap(mapped)), new(Post, post), new(Os2, Copy(Os2)),
        };
        list.Add(new(Name, tables.TryGetValue(Name, out var name) && name.Length is > 6 and <= 1024 * 1024 ? Copy(Name) : MinimalName(PostScriptName.Length > 0 ? PostScriptName : "SwivelSubset")));
        foreach (uint tag in new[] { Cvt, Fpgm, Prep }) if (tables.ContainsKey(tag)) list.Add(new(tag, Copy(tag)));
        return new(FontFileFormats.Sfnt(new FontFileFormats.Font(0x00010000, list)), map, advances);
    }

    /// Windows-platform family, subfamily, full and PostScript names, for rasterizers that need a name table.
    private static byte[] MinimalName(string name)
    {
        var value = Encoding.BigEndianUnicode.GetBytes(name); var regular = Encoding.BigEndianUnicode.GetBytes("Regular");
        (int Id, byte[] Text)[] records = { (1, value), (2, regular), (4, value), (6, value) };
        var table = new byte[6 + records.Length * 12 + value.Length * 3 + regular.Length];
        Put16(table, 2, records.Length); Put16(table, 4, 6 + records.Length * 12);
        int offset = 0;
        for (int i = 0; i < records.Length; i++)
        {
            int at = 6 + i * 12; Put16(table, at, 3); Put16(table, at + 2, 1); Put16(table, at + 4, 0x409); Put16(table, at + 6, records[i].Id); Put16(table, at + 8, records[i].Text.Length); Put16(table, at + 10, offset);
            records[i].Text.CopyTo(table, 6 + records.Length * 12 + offset); offset += records[i].Text.Length;
        }
        return table;
    }

    /// Runs of consecutive codes with consecutive glyphs, as cmap groups.
    private static List<(int Start, int End, int Glyph)> Groups(IEnumerable<KeyValuePair<int, int>> map)
    {
        var groups = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in map.OrderBy(x => x.Key))
        {
            if (groups.Count > 0 && groups[^1].End == code - 1 && groups[^1].Glyph + (code - groups[^1].Start) == glyph) groups[^1] = (groups[^1].Start, code, groups[^1].Glyph);
            else groups.Add((code, code, glyph));
        }
        return groups;
    }

    /// (3,10) format 12 with every mapping, and (3,1) format 4 with the BMP ones when they fit
    /// its 16-bit sizes (otherwise just the required final segment). The PDF itself addresses
    /// glyphs through CIDToGIDMap, so this table only keeps the font well formed.
    private static byte[] MinimalCmap(IReadOnlyDictionary<int, int> map)
    {
        var bmp = Groups(map.Where(x => x.Key < 0xFFFF));
        if (16 + (bmp.Count + 1) * 8 > 0xFFFF) bmp.Clear();
        int segments = bmp.Count + 1;
        var f4 = new byte[16 + segments * 8];
        Put16(f4, 0, 4); Put16(f4, 2, f4.Length); Put16(f4, 6, segments * 2);
        int power = 1, log = 0; while (power * 2 <= segments) { power *= 2; log++; }
        Put16(f4, 8, power * 2); Put16(f4, 10, log); Put16(f4, 12, segments * 2 - power * 2);
        int ends = 14, starts = 16 + segments * 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        for (int i = 0; i < segments; i++)
        {
            var (start, end, glyph) = i < bmp.Count ? bmp[i] : (0xFFFF, 0xFFFF, 0);
            Put16(f4, ends + i * 2, end); Put16(f4, starts + i * 2, start); Put16(f4, deltas + i * 2, i < bmp.Count ? (glyph - start) & 0xFFFF : 1); Put16(f4, ranges + i * 2, 0);
        }
        var wide = Groups(map);
        var f12 = new byte[16 + wide.Count * 12];
        Put16(f12, 0, 12); Put32(f12, 4, (uint)f12.Length); Put32(f12, 12, (uint)wide.Count);
        for (int i = 0; i < wide.Count; i++) { Put32(f12, 16 + i * 12, (uint)wide[i].Start); Put32(f12, 20 + i * 12, (uint)wide[i].End); Put32(f12, 24 + i * 12, (uint)wide[i].Glyph); }
        const int header = 4 + 2 * 8;
        var cmap = new byte[header + f4.Length + f12.Length];
        Put16(cmap, 2, 2);
        Put16(cmap, 4, 3); Put16(cmap, 6, 1); Put32(cmap, 8, header);
        Put16(cmap, 12, 3); Put16(cmap, 14, 10); Put32(cmap, 16, (uint)(header + f4.Length));
        f4.CopyTo(cmap, header); f12.CopyTo(cmap, header + f4.Length);
        return cmap;
    }

    // MARK: table helpers

    private (int Offset, int Length) ChooseCmap()
    {
        var t = Table(Cmap, 4); int count = U16(t.Offset + 2); Need(t.Offset + 4, count * 8);
        (int Offset, int Format) best = (-1, 0); int rank = 0;
        for (int i = 0; i < count; i++)
        {
            int at = t.Offset + 4 + i * 8, platform = U16(at), encoding = U16(at + 2); long offset = U32(at + 4);
            if (offset > t.Length - 8) continue;
            int sub = t.Offset + (int)offset, format = U16(sub);
            int r = (platform, encoding, format) switch { (3, 10, 12) => 4, (0, 4 or 6, 12) => 3, (3, 1, 4) => 2, (0, 3, 4) => 1, _ => 0 };
            if (r <= rank) continue;
            if (format == 12)
            {
                long length = U32(sub + 4), groups = U32(sub + 12);
                if (length > t.Offset + t.Length - (long)sub || groups > (length - 16) / 12) continue;
                long previous = -1; bool ok = true;
                for (long g = 0; g < groups && ok; g++) { long start = U32(sub + 16 + (int)g * 12), end = U32(sub + 20 + (int)g * 12); ok = start <= end && start > previous && end <= 0x10FFFF; previous = end; }
                if (!ok) continue;
            }
            else
            {
                int length = U16(sub + 2), segments = U16(sub + 6) / 2;
                if (length > t.Offset + t.Length - sub || segments == 0 || 16 + segments * 8 > length) continue;
                bool ok = true; int previous = -1;
                for (int s = 0; s < segments && ok; s++) { int end = U16(sub + 14 + s * 2), start = U16(sub + 16 + segments * 2 + s * 2); ok = start <= end && end > previous; previous = end; }
                if (!ok) continue;
            }
            best = (sub, format); rank = r;
        }
        if (best.Offset < 0) throw new UserError(Damaged);
        return best;
    }

    private string ReadPostScriptName()
    {
        if (!tables.TryGetValue(Name, out var t) || t.Length < 6) return "";
        int count = U16(t.Offset + 2), strings = t.Offset + U16(t.Offset + 4);
        if (6 + count * 12 > t.Length) return "";
        for (int i = 0; i < count; i++)
        {
            int at = t.Offset + 6 + i * 12, platform = U16(at), name = U16(at + 6), length = U16(at + 8), offset = U16(at + 10);
            if (name != 6 || strings + offset + length > t.Offset + t.Length) continue;
            string value = platform switch { 3 or 0 => Encoding.BigEndianUnicode.GetString(data, strings + offset, length & ~1), 1 => Encoding.Latin1.GetString(data, strings + offset, length), _ => "" };
            value = new string(value.Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-').Take(63).ToArray());
            if (value.Length > 0) return value;
        }
        return "";
    }

    private (int Offset, int Length) Table(uint tag, int minimum) { var t = tables[tag]; if (t.Length < minimum) throw new UserError(Damaged); return t; }
    private byte[] Copy(uint tag) { var t = tables[tag]; return data.AsSpan(t.Offset, t.Length).ToArray(); }
    private void Need(int offset, long length) { if (offset < 0 || length < 0 || offset + length > data.Length) throw new UserError(Damaged); }
    private int U16(int at) { Need(at, 2); return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)); }
    private int S16(int at) { Need(at, 2); return BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)); }
    private uint U32(int at) { Need(at, 4); return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); }
    private static void Put16(byte[] b, int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), (ushort)v);
    private static void Put32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
}
