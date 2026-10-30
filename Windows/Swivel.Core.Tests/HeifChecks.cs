using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

/// HEIF/AVIF box gate: files are built here box by box (ISO/IEC 14496-12 / 23008-12 layouts);
/// no codec is involved, so the "pixels" in mdat are placeholder bytes.
internal static class HeifChecks
{
    internal static byte[] Box(string type, params byte[][] parts)
    {
        int length = 8 + parts.Sum(p => p.Length);
        var box = new List<byte>(length);
        box.AddRange(U32((uint)length)); box.AddRange(Encoding.ASCII.GetBytes(type));
        foreach (var part in parts) box.AddRange(part);
        return box.ToArray();
    }
    internal static byte[] Full(string type, int version, int flags, params byte[][] parts) =>
        Box(type, new[] { U32((uint)(version << 24 | flags)) }.Concat(parts).ToArray());
    internal static byte[] U32(uint value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    internal static byte[] U16(int value) => new[] { (byte)(value >> 8), (byte)value };
    internal static byte[] B(params int[] values) => values.Select(v => (byte)v).ToArray();
    internal static byte[] A(string text) => Encoding.ASCII.GetBytes(text);

    internal sealed class Options
    {
        public string Major = "heic"; public string[] Compatible = { "mif1", "heic" };
        public string Codec = "hvc1"; public int Width = 4032, Height = 3024, Depth = 8;
        public List<byte[]> Extra = new();            // extra properties, associated with the primary item
        public bool ExtraEssential;
        public bool Alpha, Grid, Moov, Clap;
        public int DataReference; public int Method;
        public int Transfer = 13, Primaries = 1;
        public bool GridInFile; public int GridWidth = -1, TileWidth = -1;
        public List<byte[]> TileExtra = new();
        public byte[]? Dinf;
        public string Handler = "pict";
        public uint? Primary;
    }

    private static byte[] Config(string codec, int depth) => codec == "hvc1"
        // HEVCDecoderConfigurationRecord with no parameter set arrays.
        ? Box("hvcC", B(1, 1, 0x60, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 90, 0xF0, 0, 0xFC, 0xFD, 0xF8 | (depth - 8), 0xF8 | (depth - 8), 0, 0, 0x0F, 0))
        : Box("av1C", B(0x81, 0x08, depth == 8 ? 0x0C : depth == 10 ? 0x4C : 0x6C, 0));

    /// A still picture: item 1 (coded or a 2-tile grid of items 2 and 3), an optional alpha item 9.
    internal static byte[] File(Options o)
    {
        var ftyp = Box("ftyp", A(o.Major), U32(0), o.Compatible.SelectMany(A).ToArray());
        var items = new List<(uint Id, string Type)> { (1, o.Grid ? "grid" : o.Codec) };
        if (o.Grid) { items.Add((2, o.Codec)); items.Add((3, o.Codec)); }
        if (o.Alpha) items.Add((9, o.Codec));
        var infe = items.Select(i => Full("infe", 2, 0, U16((int)i.Id), U16(0), A(i.Type), B(0))).ToArray();
        var iinf = Full("iinf", 0, 0, new[] { U16(items.Count) }.Concat(infe).ToArray());
        // Properties: 1 config, 2 ispe, 3 colr, 4 tile ispe, 5 alpha auxC, 6.. extra.
        var properties = new List<byte[]> {
            Config(o.Codec, o.Depth),
            Full("ispe", 0, 0, U32((uint)o.Width), U32((uint)o.Height)),
            Box("colr", A("nclx"), U16(o.Primaries), U16(o.Transfer), U16(1), B(0x80)),
            Full("ispe", 0, 0, U32((uint)(o.TileWidth > 0 ? o.TileWidth : Math.Max(1, o.Width / 2))), U32((uint)o.Height)),
            Full("auxC", 0, 0, A("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"), B(0)),
        };
        if (o.Clap) properties.Add(Box("clap", new byte[32]));
        properties.AddRange(o.Extra);
        int tileExtraStart = properties.Count + 1;
        properties.AddRange(o.TileExtra);
        var ipco = Box("ipco", properties.ToArray());
        var associations = new List<(uint Id, int[] Indexes)>();
        var primaryIndexes = new List<int> { 2, 3 };
        if (!o.Grid) primaryIndexes.Insert(0, 0x81);
        for (int i = 6; i < tileExtraStart; i++) primaryIndexes.Add(o.ExtraEssential ? 0x80 | i : i);
        associations.Add((1, primaryIndexes.ToArray()));
        int[] tileIndexes = new[] { 0x81, 4 }.Concat(Enumerable.Range(tileExtraStart, o.TileExtra.Count)).ToArray();
        if (o.Grid) { associations.Add((2, tileIndexes)); associations.Add((3, tileIndexes)); }
        if (o.Alpha) associations.Add((9, new[] { 0x81, 2, 5 }));
        var ipma = Full("ipma", 0, 0, new[] { U32((uint)associations.Count) }.Concat(associations.Select(a => U16((int)a.Id).Concat(B(a.Indexes.Length)).Concat(a.Indexes.Select(x => (byte)x)).ToArray())).ToArray());
        var iprp = Box("iprp", ipco, ipma);
        var references = new List<byte[]>();
        if (o.Grid) references.Add(Box("dimg", U16(1), U16(2), U16(2), U16(3)));
        if (o.Alpha) references.Add(Box("auxl", U16(9), U16(1), U16(1)));
        var hdlr = Full("hdlr", 0, 0, U32(0), A(o.Handler), new byte[12], B(0));
        var pitm = Full("pitm", 0, 0, U16((int)(o.Primary ?? 1)));
        int gridWidth = o.GridWidth < 0 ? o.Width : o.GridWidth;
        byte[] grid = B(0, 0, 0, 1, (gridWidth >> 8) & 255, gridWidth & 255, (o.Height >> 8) & 255, o.Height & 255);
        byte[]? idat = o.Grid ? Box("idat", grid) : null;
        // iloc version 1: offset/length 4 bytes, base 0, index 0; offsets patched once sizes are known.
        byte[] Iloc(uint mdatData) => Full("iloc", 1, 0, U16(0x4400), U16(items.Count), items.SelectMany((item, n) =>
        {
            bool inIdat = o.Grid && item.Id == 1 && !o.GridInFile;
            int method = inIdat ? 1 : o.Method;
            return U16((int)item.Id).Concat(U16(method)).Concat(U16(o.DataReference)).Concat(U16(1))
                .Concat(U32(inIdat ? 0u : mdatData + (uint)n * 16)).Concat(U32(inIdat ? (uint)grid.Length : 16u));
        }).ToArray());
        byte[] Meta(uint mdatData)
        {
            var parts = new List<byte[]> { hdlr };
            if (o.Dinf is not null) parts.Add(o.Dinf);
            parts.Add(pitm); parts.Add(Iloc(mdatData)); parts.Add(iinf);
            if (references.Count > 0) parts.Add(Full("iref", 0, 0, references.ToArray()));
            parts.Add(iprp);
            if (idat is not null) parts.Add(idat);
            return Full("meta", 0, 0, parts.ToArray());
        }
        var moov = o.Moov ? Box("moov", Box("mvhd", new byte[16])) : Array.Empty<byte>();
        uint dataStart = (uint)(ftyp.Length + Meta(0).Length + moov.Length + 8);
        var mdat = Box("mdat", new byte[16 * items.Count]);
        return ftyp.Concat(Meta(dataStart)).Concat(moov).Concat(mdat).ToArray();
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        void Refused(byte[] file, string expected, string name)
        {
            string? message = null;
            try { HeifImage.Inspect(file, default); } catch (UserError error) { message = error.Message; }
            check(message == expected, name + " (" + (message ?? "accepted") + ")");
        }
        var heic = HeifImage.Inspect(File(new Options()), default);
        check(heic is { Codec: HeifCodec.Hevc, Width: 4032, Height: 3024, Orientation: 1, BitDepth: 8, HighDynamicRange: false, HasAlpha: false, IsGrid: false }, "A still HEIC header is read: HEVC, 4032 × 3024, upright, 8-bit");
        var avif = HeifImage.Inspect(File(new Options { Major = "avif", Compatible = new[] { "avif", "mif1", "miaf" }, Codec = "av01", Width = 640, Height = 480 }), default);
        check(avif is { Codec: HeifCodec.Av1, Width: 640, Height: 480, BitDepth: 8 }, "A still AVIF header is read: AV1, 640 × 480");
        check(HeifImage.Inspect(File(new Options { Major = "mif1", Compatible = new[] { "heic" } }), default).Codec == HeifCodec.Hevc, "A mif1 major brand with heic compatible is a still image");
        var grid = HeifImage.Inspect(File(new Options { Grid = true }), default);
        check(grid.IsGrid && grid.Width == 4032, "An iPhone-style grid of HEVC tiles is accepted");
        Refused(Patch(File(new Options { Grid = true }), "idat", 11, 2), HeifImage.Invalid, "A grid whose rows × columns differ from its tiles is refused");
        check(HeifImage.Inspect(File(new Options { Alpha = true, Codec = "av01", Major = "avif", Compatible = new[] { "avif" } }), default).HasAlpha, "An auxiliary alpha image of the primary is found");
        check(HeifImage.Inspect(File(new Options { Depth = 10 }), default).BitDepth == 10, "A 10-bit HEVC configuration is seen");
        check(HeifImage.Inspect(File(new Options { Codec = "av01", Major = "avif", Compatible = new[] { "avif" }, Depth = 10 }), default).BitDepth == 10, "A 10-bit AV1 configuration is seen");
        check(HeifImage.Inspect(File(new Options { Codec = "av01", Major = "avif", Compatible = new[] { "avif" }, Depth = 12 }), default).BitDepth == 12, "A 12-bit AV1 configuration is seen");
        check(HeifImage.Inspect(File(new Options { Extra = { Full("pixi", 0, 0, B(3, 10, 10, 10)) } }), default).BitDepth == 10, "pixi with 10 bits per channel is seen");
        check(HeifImage.Inspect(File(new Options { Transfer = 16 }), default).HighDynamicRange && HeifImage.Inspect(File(new Options { Transfer = 18 }), default).HighDynamicRange, "PQ and HLG transfers are HDR");
        check(HeifImage.Inspect(File(new Options { Extra = { Box("clli", new byte[4]) } }), default).HighDynamicRange, "A content light level property marks HDR");
        // Review follow-ups: colour on tiles, wide gamut, pixel aspect, grid layout and tile sizes.
        check(HeifImage.Inspect(File(new Options { Grid = true, TileExtra = { Box("colr", A("nclx"), U16(9), U16(16), U16(9), B(0)) } }), default).HighDynamicRange, "A PQ transfer on grid tiles marks HDR");
        check(HeifImage.Inspect(File(new Options { Primaries = 12 }), default).WideGamut && HeifImage.Inspect(File(new Options { Primaries = 9 }), default).WideGamut, "P3 or BT.2020 nclx primaries without a profile are wide gamut");
        check(!HeifImage.Inspect(File(new Options { Primaries = 12, Extra = { Box("colr", A("prof"), new byte[16]) } }), default).WideGamut && !HeifImage.Inspect(File(new Options { Primaries = 2 }), default).WideGamut, "An ICC profile, or unspecified primaries, is not a wide-gamut refusal");
        Refused(File(new Options { Extra = { Box("pasp", U32(4), U32(3)) } }), HeifImage.Unsupported, "Non-square pixels are refused");
        check(HeifImage.Inspect(File(new Options { Extra = { Box("pasp", U32(1), U32(1)) } }), default).Width == 4032, "Square pixels (pasp 1:1) are accepted");
        Refused(File(new Options { Grid = true, GridWidth = 4000 }), HeifImage.Invalid, "A grid whose output size differs from its ispe is refused");
        Refused(File(new Options { Grid = true, GridInFile = true }), HeifImage.Unsupported, "A grid description outside idat is refused");
        Refused(File(new Options { Grid = true, TileExtra = { Full("ispe", 0, 0, U32(2016), U32(3024)) } }), HeifImage.Invalid, "A tile with two sizes is refused");
        Refused(File(new Options { Grid = true, TileWidth = 16385 }), HeifImage.Unsupported, "A tile wider than 16384 is refused");
        Refused(File(new Options { Grid = true, TileWidth = 16384 }), HeifImage.Unsupported, "Tiles adding up to more than 4 × 16 MP are refused");
        check(HeifImage.Inspect(File(new Options { Grid = true, TileWidth = 2048 }), default).IsGrid, "Tiles slightly larger than the output (coding padding) are accepted");

        // Orientation: irot (anticlockwise quarter turns) and imir, in association order.
        int Orient(params byte[][] extra) { var o = new Options(); o.Extra.AddRange(extra); return HeifImage.Inspect(File(o), default).Orientation; }
        byte[] Irot(int a) => Box("irot", B(a)); byte[] Imir(int axis) => Box("imir", B(axis));
        check(Orient(Irot(0)) == 1 && Orient(Irot(1)) == 8 && Orient(Irot(2)) == 3 && Orient(Irot(3)) == 6, "irot 0/90/180/270° anticlockwise is EXIF 1/8/3/6");
        check(Orient(Imir(0)) == 2 && Orient(Imir(1)) == 4, "imir about the vertical/horizontal axis is EXIF 2/4");
        check(Orient(Irot(1), Imir(0)) == 7 && Orient(Irot(3), Imir(0)) == 5 && Orient(Irot(2), Imir(0)) == 4 && Orient(Irot(1), Imir(1)) == 5, "Rotation then mirroring combine to EXIF 7/5/4/5");
        check(Orient(Imir(0), Irot(1)) == 5, "Mirroring then rotation follows the association order");
        check(new[] { 5, 6, 7, 8 }.All(HeifImage.SwapsSides) && !new[] { 1, 2, 3, 4 }.Any(HeifImage.SwapsSides), "Quarter-turn orientations swap the sides");

        // Sequences and animation are refused.
        Refused(File(new Options { Major = "msf1", Compatible = new[] { "msf1", "heic" } }), HeifImage.Sequence, "An msf1 image sequence is refused");
        Refused(File(new Options { Major = "avis", Compatible = new[] { "avis", "avif" }, Codec = "av01" }), HeifImage.Sequence, "An avis AVIF sequence is refused");
        Refused(File(new Options { Major = "hevc", Compatible = new[] { "hevc" } }), HeifImage.Sequence, "An hevc image sequence is refused");
        Refused(File(new Options { Moov = true }), HeifImage.Sequence, "A file with a movie box is refused as a sequence");
        Refused(File(new Options { Major = "isom", Compatible = new[] { "msf1" } }), HeifImage.Sequence, "Only sequence brands: refused as a sequence");
        Refused(File(new Options { Major = "isom", Compatible = new[] { "mp41" } }), HeifImage.Invalid, "No HEIF or AVIF brand: refused");

        // Size limits before any decoding.
        Refused(File(new Options { Width = 16385, Height = 10 }), "Images are limited to 16 megapixels and 16384 pixels per side.", "An ispe wider than 16384 is refused");
        Refused(File(new Options { Width = 5000, Height = 4000 }), "Images are limited to 16 megapixels and 16384 pixels per side.", "An ispe over 16 MP is refused");
        Refused(File(new Options { Width = 0, Height = 10 }), "Images are limited to 16 megapixels and 16384 pixels per side.", "A zero ispe is refused");
        check(HeifImage.Inspect(File(new Options { Width = 4000, Height = 4000 }), default).Width == 4000, "Exactly 16 MP is accepted");

        // Structure.
        Refused(File(new Options { Handler = "vide" }), HeifImage.Invalid, "A non-picture handler is refused");
        Refused(File(new Options { Primary = 7 }), HeifImage.Invalid, "A primary item that isn't listed is refused");
        Refused(File(new Options { DataReference = 1 }), HeifImage.Unsupported, "Data in another file (data reference) is refused");
        Refused(File(new Options { Method = 2 }), HeifImage.Unsupported, "Data built from another item is refused");
        Refused(File(new Options { Dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 0, A("http://example.invalid/x"), B(0)))) }), HeifImage.Unsupported, "An external URL data entry is refused");
        check(HeifImage.Inspect(File(new Options { Dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1))) }), default).Width == 4032, "A self-contained data entry is accepted");
        Refused(File(new Options { Clap = true }), HeifImage.Unsupported, "A cropped (clean aperture) image is refused");
        Refused(File(new Options { Extra = { Box("zzzz", B(1)) }, ExtraEssential = true }), HeifImage.Unsupported, "An unknown essential property is refused");
        check(HeifImage.Inspect(File(new Options { Extra = { Box("zzzz", B(1)) } }), default).Width == 4032, "An unknown optional property is ignored");
        Refused(Box("mdat", new byte[8]).Concat(File(new Options())).ToArray(), HeifImage.Invalid, "A file not starting with ftyp is refused");
        var valid = File(new Options());
        Refused(ReplaceMdat(valid), HeifImage.Invalid, "A data extent beyond the end of the file is refused");
        // Every truncation, and an oversized box length, is refused with a user message.
        for (int cut = 0; cut < valid.Length; cut++)
        {
            string? message = null;
            try { HeifImage.Inspect(valid[..cut], default); } catch (UserError error) { message = error.Message; }
            check(message is not null, "A truncated HEIF is refused at " + cut);
        }
        var oversized = (byte[])valid.Clone(); int metaAt = IndexOf(oversized, "meta") - 4;
        oversized[metaAt] = 0x7F; Refused(oversized, HeifImage.Invalid, "A meta box longer than the file is refused");
        var large = (byte[])valid.Clone(); large[metaAt] = 0; large[metaAt + 1] = 0; large[metaAt + 2] = 0; large[metaAt + 3] = 3; Refused(large, HeifImage.Invalid, "A box shorter than its header is refused");
        Refused(Box("ftyp", A("heic"), U32(0), A("mif1")).Concat(Box("meta", new byte[HeifImage.MaxMetaBytes + 8])).ToArray(), HeifImage.Unsupported, "A meta box over 4 MB is refused before it is read");
        // Box counts and nesting are bounded; unknown containers are never descended.
        var deep = Box("free"); for (int i = 0; i < 2000; i++) deep = Box("zzzz", deep);
        check(HeifImage.Inspect(File(new Options { Extra = { deep } }), default).Width == 4032, "2000 nested unknown boxes are skipped without recursion");
        var many = new Options(); for (int i = 0; i < HeifImage.MaxBoxes; i++) many.Extra.Add(Box("free"));
        Refused(File(many), HeifImage.Unsupported, "More than " + HeifImage.MaxBoxes + " boxes in meta are refused");
        var manyTop = File(new Options()).Concat(Enumerable.Range(0, HeifImage.MaxTopLevel).SelectMany(_ => Box("free"))).ToArray();
        Refused(manyTop, HeifImage.Invalid, "More than " + HeifImage.MaxTopLevel + " top-level boxes are refused");
        var largesize = Box("ftyp", A("heic"), U32(0), A("mif1")).Concat(U32(1)).Concat(A("free")).Concat(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }).ToArray();
        Refused(largesize, HeifImage.Invalid, "A 64-bit box length past the end is refused");
        bool cancelled = false;
        try { HeifImage.Inspect(valid, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "A cancelled inspection stops");

        // Mutations: only acceptance or a user message, never another exception.
        var random = new Random(20261030); int accepted = 0, refused = 0;
        var seeds = new[] { valid, File(new Options { Grid = true, Alpha = true }), File(new Options { Codec = "av01", Major = "avif", Compatible = new[] { "avif" }, Extra = { Irot(1), Imir(0), Full("pixi", 0, 0, B(1, 8)) } }) };
        for (int i = 0; i < 30000; i++)
        {
            var file = (byte[])seeds[i % seeds.Length].Clone();
            int changes = 1 + random.Next(4);
            for (int c = 0; c < changes; c++) file[random.Next(Math.Min(file.Length, 600))] = (byte)random.Next(256);
            try { HeifImage.Inspect(file, default); accepted++; }
            catch (UserError) { refused++; }
            catch (Exception error) { check(false, "HEIF mutation " + i + " threw " + error.GetType().Name + ": " + error.Message); }
        }
        check(accepted + refused == 30000 && refused > 0, $"30000 HEIF mutations: {accepted} accepted, {refused} refused, none threw");

        check(HeifImage.IsHeifExtension("a.HEIC") && HeifImage.IsHeifExtension("b.avif") && HeifImage.IsHeifExtension("c.heif") && !HeifImage.IsHeifExtension("d.png"), "HEIF extensions");
        check(Catalog.IsImage("x.heic") && Catalog.IsImage("x.avif") && Catalog.IsImage("x.heif"), "HEIC, HEIF and AVIF are image inputs");
        check(PhotoSimilarity.SupportedExtensions.Contains(".heic") && PhotoSimilarity.SupportedExtensions.Contains(".avif"), "Photo similarity takes HEIC and AVIF");
        check(FilePreview.ImageExtensions.Contains("heic") && FilePreview.ImageExtensions.Contains("avif"), "Preview File takes HEIC and AVIF");
        foreach (string text in new[] { HeifImage.Invalid, HeifImage.Sequence, HeifImage.Unsupported, HeifImage.Ambiguous, HeifImage.HighDepth, HeifImage.AlphaUnsupported, HeifImage.AmbiguousSquare, HeifImage.WideGamut, HeifImage.HeicMissing, HeifImage.AvifMissing, HeifImage.ReadNotice, HeifImage.DecodeFailed })
            check(Strings.Table.TryGetValue(text, out var translations) && translations.Length == 3 && translations.All(t => !string.IsNullOrWhiteSpace(t)), "HEIF text translated: " + text);
    }

    private static int IndexOf(byte[] data, string tag)
    {
        var needle = Encoding.ASCII.GetBytes(tag);
        for (int i = 0; i + 4 <= data.Length; i++) if (data.AsSpan(i, 4).SequenceEqual(needle)) return i;
        return -1;
    }
    /// Sets one byte at an offset inside the first box of a type (past its 8-byte header).
    private static byte[] Patch(byte[] file, string tag, int offset, byte value)
    {
        var copy = (byte[])file.Clone(); copy[IndexOf(copy, tag) - 4 + offset] = value; return copy;
    }
    /// The same file with its mdat shortened so the item extents run past the end.
    private static byte[] ReplaceMdat(byte[] file)
    {
        int at = IndexOf(file, "mdat") - 4;
        var copy = file[..(at + 12)];
        copy[at] = 0; copy[at + 1] = 0; copy[at + 2] = 0; copy[at + 3] = 12;
        return copy;
    }
}
