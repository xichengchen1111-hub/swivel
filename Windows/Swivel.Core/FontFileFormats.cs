using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

/// Desktop fonts (TTF, OTF) ↔ web fonts (WOFF, WOFF2), as the Mac's Convert menu does (1.14.4).
/// Outlines are never changed: tables are copied as they are, so TTF stays TrueType and OTF stays
/// PostScript. WOFF2 is written with its "null" transforms; reading WOFF2 also undoes the glyf/loca
/// and hmtx transforms most web fonts use (W3C WOFF2 §5), which the Mac leaves to CoreText.
public static class FontFileFormats
{
    public sealed record Format(string Name, string Id);
    public static readonly string[] InputExtensions = { "ttf", "otf", "woff", "woff2" };
    public static readonly Format Woff = new("WOFF", "font.woff"), Woff2 = new("WOFF2", "font.woff2"), Desktop = new("TTF/OTF", "font.desktop");
    public static readonly Format[] Formats = { Desktop, Woff, Woff2 };
    public const int MaxBytes = 64 * 1024 * 1024, MaxTables = 1024;

    public const string NotFont = "This is not a font file Swivel can read.";
    public const string Collection = "This is not a font file Swivel can read. Font collections (TTC) aren't supported.";
    public const string Damaged = "This font file is damaged.";
    public const string TooLarge = "Font files are limited to 64 MB.";
    public const string CantCompress = "Couldn't compress the font.";

    public static string ExtensionOf(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsInput(string path) => InputExtensions.Contains(ExtensionOf(path));
    public static bool IsWeb(string path) => ExtensionOf(path) is "woff" or "woff2";
    public static Format? ForId(string id) => Formats.FirstOrDefault(f => f.Id == id);
    /// Offered for a file: unpacking only for web fonts, and never the file's own format.
    public static bool Offers(string path, Format format) => IsInput(path) && (format == Desktop ? IsWeb(path) : ExtensionOf(path) != format.Id["font.".Length..]);

    public sealed record Table(uint Tag, byte[] Data);
    public sealed record Font(uint Flavor, IReadOnlyList<Table> Tables)
    {
        public bool IsCff => Flavor == 0x4F54544F; // 'OTTO'
    }

    /// The converted bytes and their extension (ttf or otf when unpacking).
    public static (byte[] Bytes, string Extension) Convert(byte[] source, Format target, CancellationToken token = default)
    {
        var font = Read(source, token);
        // Laid out afresh first, so head.checkSumAdjustment matches the font a decoder rebuilds.
        var laidOut = Sfnt(font);
        var result = target == Woff ? (EncodeWoff(ParseSfnt(laidOut)), "woff") : target == Woff2 ? (EncodeWoff2(ParseSfnt(laidOut)), "woff2") : (laidOut, font.IsCff ? "otf" : "ttf");
        token.ThrowIfCancellationRequested();
        // Read back, as the Mac checks its output with CoreText.
        if (Read(result.Item1, token).Tables.Count != font.Tables.Count) throw new UserError(Damaged);
        return result;
    }

    public static Font Read(byte[] data, CancellationToken token = default)
    {
        if (data.Length > MaxBytes) throw new UserError(TooLarge);
        if (data.Length < 12) throw new UserError(NotFont);
        return U32(data, 0) switch
        {
            0x774F4646 => DecodeWoff(data),              // 'wOFF'
            0x774F4632 => DecodeWoff2(data, token),      // 'wOF2'
            0x00010000 or 0x4F54544F or 0x74727565 => ParseSfnt(data), // TrueType, 'OTTO', 'true'
            0x74746366 => throw new UserError(Collection), // 'ttcf'
            _ => throw new UserError(NotFont),
        };
    }

    // MARK: sfnt

    public static Font ParseSfnt(byte[] data)
    {
        int count = U16(data, 4);
        if (count == 0 || count > MaxTables || data.Length < 12 + count * 16) throw new UserError(Damaged);
        var tables = new List<Table>(); long total = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 12 + i * 16;
            long offset = U32(data, at + 8), length = U32(data, at + 12);
            if (offset > data.Length || length > data.Length - offset) throw new UserError(Damaged);
            // Entries may point at the same bytes; copied out, they must still fit the budget.
            total += length;
            if (total > MaxBytes) throw new UserError(Damaged);
            tables.Add(new Table(U32(data, at), data.AsSpan((int)offset, (int)length).ToArray()));
        }
        uint flavor = U32(data, 0);
        return Validated(new Font(flavor == 0x74727565 ? 0x00010000 : flavor, tables));
    }

    private const uint HeadTag = 0x68656164, GlyfTag = 0x676C7966, LocaTag = 0x6C6F6361, HmtxTag = 0x686D7478, HheaTag = 0x68686561, MaxpTag = 0x6D617870;

    private static Font Validated(Font font)
    {
        var tags = font.Tables.Select(t => t.Tag).ToList();
        if (tags.Distinct().Count() != tags.Count || font.Tables.FirstOrDefault(t => t.Tag == HeadTag) is not { Data.Length: >= 54 } || SfntSize(font) > MaxBytes)
            throw new UserError(Damaged);
        return font;
    }

    /// Tables in tag order, 4-byte aligned, with fresh checksums and head.checkSumAdjustment.
    public static byte[] Sfnt(Font font)
    {
        var tables = font.Tables.OrderBy(t => t.Tag).ToList();
        int n = tables.Count, power = 1, selector = 0;
        while (power * 2 <= n) { power *= 2; selector++; }
        var out_ = new byte[SfntSize(font)];
        Put32(out_, 0, font.Flavor); Put16(out_, 4, n); Put16(out_, 6, power * 16); Put16(out_, 8, selector); Put16(out_, 10, n * 16 - power * 16);
        int offset = 12 + n * 16, headOffset = -1;
        for (int i = 0; i < n; i++)
        {
            var body = tables[i].Data;
            if (tables[i].Tag == HeadTag) headOffset = offset;
            body.CopyTo(out_, offset);
            if (tables[i].Tag == HeadTag) Put32(out_, offset + 8, 0);
            int at = 12 + i * 16;
            Put32(out_, at, tables[i].Tag); Put32(out_, at + 4, Checksum(out_.AsSpan(offset, body.Length))); Put32(out_, at + 8, (uint)offset); Put32(out_, at + 12, (uint)body.Length);
            offset += (body.Length + 3) & ~3;
        }
        Put32(out_, headOffset + 8, unchecked(0xB1B0AFBA - Checksum(out_)));
        return out_;
    }

    /// head's own checksum is taken with its checkSumAdjustment field zeroed.
    public static uint TableChecksum(uint tag, byte[] data)
    {
        if (tag != HeadTag || data.Length < 12) return Checksum(data);
        var body = (byte[])data.Clone(); Put32(body, 8, 0);
        return Checksum(body);
    }

    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++) word = word << 8 | (i + k < data.Length ? data[i + k] : 0u);
            unchecked { sum += word; }
        }
        return sum;
    }

    private static long SfntSize(Font font) => 12 + 16L * font.Tables.Count + font.Tables.Sum(t => (long)((t.Data.Length + 3) & ~3));

    // MARK: WOFF 1.0 (zlib per table)

    public static byte[] EncodeWoff(Font font)
    {
        var tables = font.Tables.OrderBy(t => t.Tag).ToList();
        var directory = new MemoryStream(); var body = new MemoryStream();
        long offset = 44 + 20 * tables.Count;
        foreach (var table in tables)
        {
            var compressed = Zlib(table.Data);
            var stored = compressed.Length < table.Data.Length ? compressed : table.Data;
            var entry = new byte[20];
            Put32(entry, 0, table.Tag); Put32(entry, 4, (uint)offset); Put32(entry, 8, (uint)stored.Length); Put32(entry, 12, (uint)table.Data.Length); Put32(entry, 16, TableChecksum(table.Tag, table.Data));
            directory.Write(entry); body.Write(stored); body.Write(new byte[(4 - stored.Length % 4) % 4]);
            offset += (stored.Length + 3) & ~3;
        }
        var header = new byte[44];
        Put32(header, 0, 0x774F4646); Put32(header, 4, font.Flavor); Put32(header, 8, (uint)(44 + directory.Length + body.Length));
        Put16(header, 12, tables.Count); Put32(header, 16, (uint)SfntSize(font)); Put16(header, 20, 1);
        return header.Concat(directory.ToArray()).Concat(body.ToArray()).ToArray();
    }

    public static Font DecodeWoff(byte[] data)
    {
        int count = U16(data, 12);
        if (count == 0 || count > MaxTables || data.Length < 44 + 20 * count) throw new UserError(Damaged);
        // Every table's unpacked size is declared up front: refuse a decompression bomb before inflating anything.
        long declared = Enumerable.Range(0, count).Sum(i => (long)((U32(data, 44 + 20 * i + 12) + 3) & ~3u));
        if (declared > MaxBytes) throw new UserError(Damaged);
        var tables = new List<Table>();
        for (int i = 0; i < count; i++)
        {
            int at = 44 + 20 * i;
            long offset = U32(data, at + 4), stored = U32(data, at + 8), length = U32(data, at + 12);
            if (offset > data.Length || stored > data.Length - offset || stored > length) throw new UserError(Damaged);
            var chunk = data.AsSpan((int)offset, (int)stored).ToArray();
            tables.Add(new Table(U32(data, at), stored == length ? chunk : Unzlib(chunk, (int)length)));
        }
        return Validated(new Font(U32(data, 4), tables));
    }

    private static byte[] Zlib(byte[] data)
    {
        if (data.Length == 0) return data;
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, true)) zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] Unzlib(byte[] data, int length)
    {
        if (data.Length < 6 || (data[0] & 0x0F) != 8 || (data[0] << 8 | data[1]) % 31 != 0) throw new UserError(Damaged);
        var out_ = new byte[length + 1]; int filled = 0;
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data, false), CompressionMode.Decompress);
            int read;
            while (filled < out_.Length && (read = zlib.Read(out_, filled, out_.Length - filled)) > 0) filled += read;
        }
        catch (InvalidDataException) { throw new UserError(Damaged); }
        if (filled != length) throw new UserError(Damaged);
        return out_[..length];
    }

    // MARK: WOFF 2.0

    // The 63 tags with a one-byte index in the WOFF2 table directory (W3C WOFF2 §5.2, Table 1).
    private static readonly string[] KnownTags =
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    };
    private static uint TagOf(string tag) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(tag));

    public static byte[] EncodeWoff2(Font font)
    {
        var tables = font.Tables.OrderBy(t => t.Tag).ToList();
        // loca follows glyf in the directory, as WOFF2 decoders expect.
        int loca = tables.FindIndex(t => t.Tag == LocaTag);
        if (loca >= 0 && tables.Any(t => t.Tag == GlyfTag))
        {
            var entry = tables[loca]; tables.RemoveAt(loca);
            tables.Insert(tables.FindIndex(t => t.Tag == GlyfTag) + 1, entry);
        }
        var directory = new MemoryStream(); var stream = new MemoryStream();
        foreach (var table in tables)
        {
            // Tag index 63 means "the tag follows"; transform version 3 is the null transform for glyf and loca.
            byte nullTransform = table.Tag is GlyfTag or LocaTag ? (byte)(3 << 6) : (byte)0;
            directory.WriteByte((byte)(nullTransform | 63));
            var tag = new byte[4]; Put32(tag, 0, table.Tag); directory.Write(tag);
            directory.Write(Base128((uint)table.Data.Length));
            stream.Write(table.Data);
        }
        var raw = stream.ToArray();
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, compressed, out int n, 11, 22)) throw new UserError(CantCompress);
        int padding = (int)((4 - (48 + directory.Length + n) % 4) % 4);
        var header = new byte[48];
        Put32(header, 0, 0x774F4632); Put32(header, 4, font.Flavor); Put32(header, 8, (uint)(48 + directory.Length + n + padding));
        Put16(header, 12, tables.Count); Put32(header, 16, (uint)SfntSize(font)); Put32(header, 20, (uint)n); Put16(header, 24, 1);
        return header.Concat(directory.ToArray()).Concat(compressed.Take(n)).Concat(new byte[padding]).ToArray();
    }

    private static byte[] Base128(uint value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (uint v = value >> 7; v > 0; v >>= 7) bytes.Insert(0, (byte)((v & 0x7F) | 0x80));
        return bytes.ToArray();
    }

    private sealed record Entry(uint Tag, int Transform, long Original, long Stored);

    public static Font DecodeWoff2(byte[] data, CancellationToken token = default)
    {
        if (data.Length < 48) throw new UserError(Damaged);
        uint flavor = U32(data, 4);
        if (flavor == 0x74746366) throw new UserError(Collection);
        int count = U16(data, 12);
        long totalSfnt = U32(data, 16), compressedLength = U32(data, 20);
        // The unpacked size is declared in the header: refuse a bomb before inflating anything.
        if (count == 0 || count > MaxTables || totalSfnt > MaxBytes) throw new UserError(Damaged);
        int at = 48; var entries = new List<Entry>(); long streamLength = 0;
        for (int i = 0; i < count; i++)
        {
            if (at >= data.Length) throw new UserError(Damaged);
            int flags = data[at++], index = flags & 0x3F, version = flags >> 6;
            uint tag;
            if (index == 63) { if (at + 4 > data.Length) throw new UserError(Damaged); tag = U32(data, at); at += 4; }
            else tag = TagOf(KnownTags[index]);
            long original = Base128(data, ref at);
            // glyf and loca use version 0 for their transform and 3 for none; every other table the reverse.
            bool transformed = tag is GlyfTag or LocaTag ? version != 3 : version != 0;
            if (transformed && !(tag is GlyfTag or LocaTag && version == 0) && !(tag == HmtxTag && version == 1)) throw new UserError(Damaged);
            long stored = transformed ? Base128(data, ref at) : original;
            if (tag == LocaTag && transformed && stored != 0) throw new UserError(Damaged);
            streamLength += stored;
            if (original > MaxBytes || streamLength > MaxBytes) throw new UserError(Damaged);
            entries.Add(new Entry(tag, transformed ? version : -1, original, stored));
        }
        if (entries.Select(e => e.Tag).Distinct().Count() != entries.Count) throw new UserError(Damaged);
        if (compressedLength > data.Length - at) throw new UserError(Damaged);
        var stream = new byte[streamLength + 1];
        if (!BrotliDecoder.TryDecompress(data.AsSpan(at, (int)compressedLength), stream, out int written) || written != streamLength) throw new UserError(Damaged);
        token.ThrowIfCancellationRequested();

        var slices = new Dictionary<uint, byte[]>(); long position = 0;
        foreach (var e in entries) { slices[e.Tag] = stream.AsSpan((int)position, (int)e.Stored).ToArray(); position += e.Stored; }
        var tables = new Dictionary<uint, byte[]>();
        foreach (var e in entries) if (e.Transform < 0) tables[e.Tag] = slices[e.Tag];
        var glyf = entries.FirstOrDefault(e => e.Tag == GlyfTag); var loca = entries.FirstOrDefault(e => e.Tag == LocaTag);
        if ((glyf?.Transform == 0) != (loca?.Transform == 0)) throw new UserError(Damaged);
        short[]? xMins = null;
        if (glyf?.Transform == 0)
        {
            if (!tables.TryGetValue(HeadTag, out var head) || head.Length < 54) throw new UserError(Damaged);
            var (glyfTable, locaTable, mins) = GlyfTransform.Reconstruct(slices[GlyfTag], BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)), token);
            tables[GlyfTag] = glyfTable; tables[LocaTag] = locaTable; xMins = mins;
            // The rebuilt glyf may differ in size from the original (flags are written uncompressed); loca may not.
            if (locaTable.Length != loca!.Original) throw new UserError(Damaged);
        }
        if (entries.FirstOrDefault(e => e.Tag == HmtxTag) is { Transform: 1 } hmtx)
        {
            if (xMins is null || !tables.TryGetValue(HheaTag, out var hhea) || hhea.Length < 36) throw new UserError(Damaged);
            tables[HmtxTag] = HmtxTransform(slices[HmtxTag], U16(hhea, 34), xMins);
            if (tables[HmtxTag].Length != hmtx.Original) throw new UserError(Damaged);
        }
        return Validated(new Font(flavor, entries.Select(e => new Table(e.Tag, tables[e.Tag])).ToList()));
    }

    private static long Base128(byte[] data, ref int at)
    {
        long value = 0;
        for (int i = 0; i < 5; i++)
        {
            if (at >= data.Length) throw new UserError(Damaged);
            byte b = data[at++];
            if (i == 0 && b == 0x80) throw new UserError(Damaged); // no leading zeros
            value = value << 7 | (uint)(b & 0x7F);
            if (value > uint.MaxValue) throw new UserError(Damaged);
            if ((b & 0x80) == 0) return value;
        }
        throw new UserError(Damaged);
    }

    /// hmtx transform version 1 (§5.4): left side bearings left out are the glyphs' xMin.
    private static byte[] HmtxTransform(byte[] data, int metrics, short[] xMins)
    {
        int glyphs = xMins.Length;
        if (data.Length < 1 || metrics < 1 || metrics > glyphs) throw new UserError(Damaged);
        int flags = data[0]; bool proportional = (flags & 1) == 0, monospace = (flags & 2) == 0;
        if ((flags & 0xFC) != 0 || (proportional && monospace)) throw new UserError(Damaged);
        int at = 1;
        short Next() { if (at + 2 > data.Length) throw new UserError(Damaged); short v = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)); at += 2; return v; }
        var advances = new short[metrics]; for (int i = 0; i < metrics; i++) advances[i] = Next();
        var bearings = new short[glyphs];
        for (int i = 0; i < metrics; i++) bearings[i] = proportional ? Next() : xMins[i];
        for (int i = metrics; i < glyphs; i++) bearings[i] = monospace ? Next() : xMins[i];
        if (at != data.Length) throw new UserError(Damaged);
        var out_ = new byte[4 * metrics + 2 * (glyphs - metrics)]; int o = 0;
        for (int i = 0; i < glyphs; i++)
        {
            if (i < metrics) { BinaryPrimitives.WriteInt16BigEndian(out_.AsSpan(o), advances[i]); o += 2; }
            BinaryPrimitives.WriteInt16BigEndian(out_.AsSpan(o), bearings[i]); o += 2;
        }
        return out_;
    }

    // MARK: bytes

    internal static int U16(byte[] data, int at) => at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) : 0;
    internal static uint U32(byte[] data, int at) => at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : 0;
    private static void Put16(byte[] data, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)value);
    private static void Put32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), value);
}

/// The WOFF2 glyf/loca transform undone (W3C WOFF2 §5.1–5.3): glyphs rebuilt from the separated
/// contour, point, flag, coordinate, composite, bounding-box and instruction streams. Simple
/// glyphs are written with uncompressed flags and 16-bit coordinates; outlines are the same.
internal static class GlyfTransform
{
    private const string Damaged = FontFileFormats.Damaged;

    private sealed class Reader(byte[] data, int start, int length)
    {
        private int at = start; private readonly int end = start + length;
        public bool Done => at == end;
        public byte U8() { if (at >= end) throw new UserError(Damaged); return data[at++]; }
        public int U16() => U8() << 8 | U8();
        public short I16() => (short)U16();
        public uint U32() => (uint)(U16() << 16 | U16());
        public ReadOnlySpan<byte> Bytes(int count) { if (count < 0 || count > end - at) throw new UserError(Damaged); var span = data.AsSpan(at, count); at += count; return span; }
        public int U255()
        {
            int code = U8();
            return code switch { 253 => U16(), 255 => U8() + 253, 254 => U8() + 506, _ => code };
        }
    }

    public static (byte[] Glyf, byte[] Loca, short[] XMins) Reconstruct(byte[] data, short headIndexFormat, CancellationToken token)
    {
        var header = new Reader(data, 0, data.Length);
        header.U16(); // reserved
        int options = header.U16(), glyphs = header.U16(), indexFormat = header.U16();
        if (indexFormat != headIndexFormat || indexFormat is not (0 or 1)) throw new UserError(Damaged);
        var sizes = new long[7]; for (int i = 0; i < 7; i++) sizes[i] = header.U32();
        int at = 36; var streams = new Reader[7];
        for (int i = 0; i < 7; i++)
        {
            if (sizes[i] > data.Length - at) throw new UserError(Damaged);
            streams[i] = new Reader(data, at, (int)sizes[i]); at += (int)sizes[i];
        }
        var (contours, points, flags, glyphStream, composites, bboxes, instructions) = (streams[0], streams[1], streams[2], streams[3], streams[4], streams[5], streams[6]);
        int bitmapLength = 4 * ((glyphs + 31) / 32);
        var bboxBitmap = bboxes.Bytes(bitmapLength).ToArray();
        // The overlap bitmap (one bit per glyph) follows the seven streams when option bit 0 is set.
        byte[]? overlap = (options & 1) != 0 ? new Reader(data, at, data.Length - at).Bytes((glyphs + 7) / 8).ToArray() : null;

        var glyf = new MemoryStream(); var offsets = new long[glyphs + 1]; var xMins = new short[glyphs];
        for (int g = 0; g < glyphs; g++)
        {
            if (g % 1024 == 0) token.ThrowIfCancellationRequested();
            offsets[g] = glyf.Position;
            bool hasBox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
            int count = contours.I16();
            var glyph = new MemoryStream();
            void W16(int v) { glyph.WriteByte((byte)(v >> 8)); glyph.WriteByte((byte)v); }
            if (count == 0)
            {
                if (hasBox) throw new UserError(Damaged);
            }
            else if (count == -1)
            {
                // Composite: its bounding box is always stored.
                if (!hasBox) throw new UserError(Damaged);
                short xMin = bboxes.I16(), yMin = bboxes.I16(), xMax = bboxes.I16(), yMax = bboxes.I16();
                xMins[g] = xMin;
                W16(-1); W16(xMin); W16(yMin); W16(xMax); W16(yMax);
                bool more = true, instructed = false;
                while (more)
                {
                    int f = composites.U16();
                    more = (f & 0x20) != 0; instructed |= (f & 0x100) != 0;
                    int size = 2 + ((f & 1) != 0 ? 4 : 2) + ((f & 0x8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0);
                    W16(f); glyph.Write(composites.Bytes(size));
                }
                if (instructed)
                {
                    int length = glyphStream.U255();
                    W16(length); glyph.Write(instructions.Bytes(length));
                }
            }
            else if (count > 0)
            {
                var ends = new int[count]; int total = 0;
                for (int c = 0; c < count; c++)
                {
                    total += points.U255();
                    if (total > 65535) throw new UserError(Damaged);
                    ends[c] = total - 1;
                }
                var xs = new int[total]; var ys = new int[total]; var on = new bool[total];
                int x = 0, y = 0;
                for (int p = 0; p < total; p++)
                {
                    int flag = flags.U8(); on[p] = (flag & 0x80) == 0; flag &= 0x7F;
                    int dx, dy;
                    static int Sign(int f, int value) => (f & 1) != 0 ? value : -value;
                    if (flag < 10) { dx = 0; dy = Sign(flag, ((flag & 14) << 7) + glyphStream.U8()); }
                    else if (flag < 20) { dx = Sign(flag, (((flag - 10) & 14) << 7) + glyphStream.U8()); dy = 0; }
                    else if (flag < 84) { int b0 = flag - 20, b1 = glyphStream.U8(); dx = Sign(flag, 1 + (b0 & 0x30) + (b1 >> 4)); dy = Sign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)); }
                    else if (flag < 120) { int b0 = flag - 84, b1 = glyphStream.U8(), b2 = glyphStream.U8(); dx = Sign(flag, 1 + ((b0 / 12) << 8) + b1); dy = Sign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2); }
                    else if (flag < 124) { int b1 = glyphStream.U8(), b2 = glyphStream.U8(), b3 = glyphStream.U8(); dx = Sign(flag, (b1 << 4) + (b2 >> 4)); dy = Sign(flag >> 1, ((b2 & 0x0F) << 8) + b3); }
                    else { int b1 = glyphStream.U8(), b2 = glyphStream.U8(), b3 = glyphStream.U8(), b4 = glyphStream.U8(); dx = Sign(flag, (b1 << 8) + b2); dy = Sign(flag >> 1, (b3 << 8) + b4); }
                    x += dx; y += dy;
                    if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue) throw new UserError(Damaged);
                    xs[p] = x; ys[p] = y;
                }
                int instructionLength = glyphStream.U255();
                short xMin, yMin, xMax, yMax;
                if (hasBox) { xMin = bboxes.I16(); yMin = bboxes.I16(); xMax = bboxes.I16(); yMax = bboxes.I16(); }
                else if (total == 0) { xMin = yMin = xMax = yMax = 0; }
                else { xMin = (short)xs.Min(); yMin = (short)ys.Min(); xMax = (short)xs.Max(); yMax = (short)ys.Max(); }
                xMins[g] = xMin;
                W16(count); W16(xMin); W16(yMin); W16(xMax); W16(yMax);
                foreach (int e in ends) W16(e);
                W16(instructionLength); glyph.Write(instructions.Bytes(instructionLength));
                // Uncompressed flags: on-curve bit only, so both coordinates follow as 16-bit deltas.
                bool overlapping = overlap is not null && (overlap[g >> 3] & (0x80 >> (g & 7))) != 0;
                for (int p = 0; p < total; p++) glyph.WriteByte((byte)((on[p] ? 1 : 0) | (p == 0 && overlapping ? 0x40 : 0)));
                int previous = 0;
                for (int p = 0; p < total; p++) { W16(xs[p] - previous); previous = xs[p]; }
                previous = 0;
                for (int p = 0; p < total; p++) { W16(ys[p] - previous); previous = ys[p]; }
            }
            else throw new UserError(Damaged);
            glyph.WriteTo(glyf);
            // Short loca stores offsets divided by two; long loca is 4-byte aligned.
            int align = indexFormat == 0 ? 2 : 4;
            while (glyf.Position % align != 0) glyf.WriteByte(0);
            if (glyf.Position > FontFileFormats.MaxBytes) throw new UserError(Damaged);
        }
        offsets[glyphs] = glyf.Position;
        if (!contours.Done || !points.Done || !flags.Done || !glyphStream.Done || !composites.Done || !bboxes.Done || !instructions.Done) throw new UserError(Damaged);
        if (indexFormat == 0 && offsets[glyphs] / 2 > ushort.MaxValue) throw new UserError(Damaged);
        var loca = new byte[(glyphs + 1) * (indexFormat == 0 ? 2 : 4)];
        for (int g = 0; g <= glyphs; g++)
        {
            if (indexFormat == 0) BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(2 * g), (ushort)(offsets[g] / 2));
            else BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * g), (uint)offsets[g]);
        }
        return (glyf.ToArray(), loca, xMins);
    }
}
