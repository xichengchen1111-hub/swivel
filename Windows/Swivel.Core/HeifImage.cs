using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// What the box gate learned about a still HEIF (HEIC) or AVIF picture before any codec runs.
/// Width and Height are the coded size of the primary image (ispe, before rotation);
/// Orientation is the EXIF-style value (1–8) equivalent to its irot/imir properties.
/// WideGamut: nclx colour primaries other than BT.709/sRGB with no embedded ICC profile.
public sealed record HeifInfo(HeifCodec Codec, int Width, int Height, int Orientation, int BitDepth, bool HighDynamicRange, bool HasAlpha, bool IsGrid, bool WideGamut = false);

public enum HeifCodec { Hevc, Av1 }

/// Swivel's own bounded reader of the ISO base media file format boxes of a HEIF/AVIF file
/// (ISO/IEC 14496-12 and 23008-12, AV1 Image File Format). It checks the structure and the
/// primary image's properties; it never decodes pixels. The pixels themselves come only from
/// Microsoft's HEIF decoder through WIC, which needs the free HEIF Image Extensions plus HEVC
/// Video Extensions (HEIC) or AV1 Video Extension (AVIF) from the Microsoft Store.
public static class HeifImage
{
    public const string Invalid = "The HEIF or AVIF data is invalid or incomplete.";
    public const string Sequence = "HEIF and AVIF image sequences and animations are not supported. Save one frame as PNG, or use a still image.";
    public const string Unsupported = "This HEIF or AVIF file uses a feature Swivel doesn't check yet (external data, derived or cropped images, or an unknown required property). Save it again as JPEG or PNG.";
    public const string Ambiguous = "This HEIF or AVIF photo is mirrored or turned upside down. Swivel can't yet confirm how Windows' decoder applies that, so it is refused rather than risk a wrong picture. Rotate it in another app and save it as JPEG or PNG.";
    public const string AmbiguousSquare = "This square HEIF or AVIF photo is turned a quarter turn. Swivel can't yet tell whether Windows' decoder has already turned it, so it is refused rather than risk a wrong picture. Rotate it in another app and save it as JPEG or PNG.";
    public const string WideGamut = "This HEIF or AVIF picture uses a wide colour space (such as Display P3 or BT.2020) without an embedded colour profile, which Swivel can't convert to sRGB yet. Save it again as JPEG or PNG.";
    public const string HighDepth = "HDR and high-bit-depth HEIF or AVIF images are not supported for saving yet. Swivel works with 8-bit SDR pictures.";
    public const string AlphaUnsupported = "This HEIF or AVIF image has a transparency layer that Windows' decoder doesn't return. Save it again as PNG.";
    public const string HeicMissing = "Windows can't decode HEIC pictures yet. Install Microsoft's HEIF Image Extensions (free) and HEVC Video Extensions from the Microsoft Store, then try again. Swivel never installs anything itself.";
    public const string AvifMissing = "Windows can't decode AVIF pictures yet. Install Microsoft's free HEIF Image Extensions and AV1 Video Extension from the Microsoft Store, then try again. Swivel never installs anything itself.";
    public const string DecodeFailed = "Windows' HEIF decoder couldn't read this picture. The file may be damaged, or a codec extension may be missing or out of date.";
    public const string ReadNotice = "HEIC, HEIF and AVIF are opened only by Microsoft's own HEIF decoder in Windows; other installed HEIF codecs are not used. Windows chooses the HEVC or AV1 video decoder that decoder needs (Microsoft's Store extensions or the graphics hardware): HEIF Image Extensions with HEVC Video Extensions or AV1 Video Extension. Still pictures only; rotation and an embedded colour profile are applied, depth maps, gain maps and other metadata are not. Mirrored or upside-down photos, and wide-gamut pictures without a profile, are refused for now.";

    public const int MaxMetaBytes = 4 * 1024 * 1024, MaxBoxes = 8192, MaxDepth = 8, MaxItems = 4096, MaxProperties = 4096, MaxTopLevel = 1024;

    public static bool IsHeifExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".avif";

    private static readonly HashSet<string> StillBrands = new(StringComparer.Ordinal) { "heic", "heix", "heim", "heis", "mif1", "mif2", "miaf", "avif", "MA1B", "MA1A" };
    private static readonly HashSet<string> SequenceBrands = new(StringComparer.Ordinal) { "msf1", "hevc", "hevx", "hevm", "hevs", "avis" };
    // Properties whose meaning Swivel knows; an unknown one marked essential is refused.
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal) { "ispe", "irot", "imir", "pixi", "hvcC", "av1C", "colr", "auxC", "lsel", "a1op", "a1lx", "rloc", "pasp", "clli", "mdcv", "cclv", "amve" };

    public static HeifInfo Inspect(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.", nameof(input));
        long length = input.Length;
        if (length > ImageRules.MaxInputBytes) throw new UserError("Image files are limited to 128 MB.");
        byte[]? ftyp = null, meta = null; int topLevel = 0; long at = 0;
        Span<byte> header = stackalloc byte[16];
        while (at < length)
        {
            token.ThrowIfCancellationRequested();
            if (++topLevel > MaxTopLevel) throw new UserError(Invalid);
            if (length - at < 8) throw new UserError(Invalid);
            input.Position = at;
            int got = (int)Math.Min(16, length - at); input.ReadExactly(header[..got]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header); string type = Tag(header[4..8]); int headerLength = 8;
            if (size == 1)
            {
                if (got < 16) throw new UserError(Invalid);
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                if (large > (ulong)length) throw new UserError(Invalid);
                size = (long)large; headerLength = 16;
            }
            else if (size == 0) size = length - at;
            if (size < headerLength || size > length - at) throw new UserError(Invalid);
            if (topLevel == 1 && type != "ftyp") throw new UserError(Invalid);
            switch (type)
            {
                case "ftyp":
                    if (ftyp is not null || size > 4096) throw new UserError(Invalid);
                    ftyp = ReadBody(input, at + headerLength, size - headerLength); break;
                case "meta":
                    if (meta is not null) throw new UserError(Invalid);
                    if (size - headerLength > MaxMetaBytes) throw new UserError(Unsupported);
                    meta = ReadBody(input, at + headerLength, size - headerLength); break;
                case "moov": case "moof": throw new UserError(Sequence);
            }
            at += size;
        }
        if (ftyp is null || ftyp.Length < 8 || (ftyp.Length - 8) % 4 != 0) throw new UserError(Invalid);
        string major = Tag(ftyp.AsSpan(0, 4));
        var brands = new List<string> { major };
        for (int i = 8; i < ftyp.Length; i += 4) brands.Add(Tag(ftyp.AsSpan(i, 4)));
        if (SequenceBrands.Contains(major)) throw new UserError(Sequence);
        if (!brands.Any(StillBrands.Contains)) throw new UserError(brands.Any(SequenceBrands.Contains) ? Sequence : Invalid);
        if (meta is null) throw new UserError(Invalid);
        return Meta(meta, length, token);
    }

    public static HeifInfo Inspect(byte[] file, CancellationToken token)
    {
        using var stream = new MemoryStream(file, false);
        return Inspect(stream, token);
    }

    private static byte[] ReadBody(Stream input, long offset, long count)
    {
        var body = new byte[count]; input.Position = offset; input.ReadExactly(body); return body;
    }

    private readonly record struct Box(string Type, int Start, int End);

    private sealed class Walk
    {
        public int Boxes;
        public readonly CancellationToken Token;
        public Walk(CancellationToken token) => Token = token;
    }

    /// Children of a container, each fully inside its parent.
    private static List<Box> Children(byte[] data, int start, int end, int depth, Walk walk)
    {
        if (depth > MaxDepth) throw new UserError(Invalid);
        var boxes = new List<Box>();
        int at = start;
        while (at < end)
        {
            walk.Token.ThrowIfCancellationRequested();
            if (++walk.Boxes > MaxBoxes) throw new UserError(Unsupported);
            if (end - at < 8) throw new UserError(Invalid);
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); int headerLength = 8;
            if (size == 1)
            {
                if (end - at < 16) throw new UserError(Invalid);
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                if (large > (ulong)(end - at)) throw new UserError(Invalid);
                size = (long)large; headerLength = 16;
            }
            else if (size == 0) throw new UserError(Invalid);
            if (size < headerLength || size > end - at) throw new UserError(Invalid);
            boxes.Add(new Box(Tag(data.AsSpan(at + 4, 4)), at + headerLength, at + (int)size));
            at += (int)size;
        }
        return boxes;
    }

    private sealed class Reader
    {
        private readonly byte[] data; private int at; private readonly int end;
        public Reader(byte[] data, Box box) { this.data = data; at = box.Start; end = box.End; }
        public int Position => at;
        public int Remaining => end - at;
        private void Need(int count) { if (count < 0 || end - at < count) throw new UserError(Invalid); }
        public byte U8() { Need(1); return data[at++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)); at += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); at += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at)); at += 8; return v; }
        public ulong Sized(int bytes) => bytes switch { 0 => 0, 4 => U32(), 8 => U64(), _ => throw new UserError(Invalid) };
        public string Tag4() { Need(4); var t = Tag(data.AsSpan(at, 4)); at += 4; return t; }
        public (int Version, int Flags) Full() { uint v = U32(); return ((int)(v >> 24), (int)(v & 0xFFFFFF)); }
        public void Skip(int count) { Need(count); at += count; }
        public string CString()
        {
            int zero = Array.IndexOf(data, (byte)0, at, end - at);
            if (zero < 0) throw new UserError(Invalid);
            string text = Encoding.UTF8.GetString(data, at, zero - at); at = zero + 1; return text;
        }
    }

    private sealed record Item(uint Id, string Type);
    private sealed record Location(int Method, int DataReference, List<(ulong Offset, ulong Length)> Extents, ulong BaseOffset);

    private static HeifInfo Meta(byte[] meta, long fileLength, CancellationToken token)
    {
        var walk = new Walk(token);
        var whole = new Box("meta", 0, meta.Length);
        var top = new Reader(meta, whole); var (metaVersion, _) = top.Full();
        if (metaVersion != 0) throw new UserError(Invalid);
        var children = Children(meta, top.Position, meta.Length, 1, walk);
        Box? Single(string type)
        {
            var found = children.Where(b => b.Type == type).ToList();
            if (found.Count > 1) throw new UserError(Invalid);
            return found.Count == 1 ? found[0] : null;
        }
        // Handler: a picture ('pict') collection.
        var hdlr = new Reader(meta, Single("hdlr") ?? throw new UserError(Invalid));
        hdlr.Full(); hdlr.Skip(4);
        if (hdlr.Tag4() != "pict") throw new UserError(Invalid);
        if (Single("dinf") is { } dinf) Dinf(meta, dinf, walk);
        // Primary item.
        var pitm = new Reader(meta, Single("pitm") ?? throw new UserError(Invalid));
        var (pitmVersion, _) = pitm.Full();
        uint primary = pitmVersion == 0 ? pitm.U16() : pitmVersion == 1 ? pitm.U32() : throw new UserError(Invalid);
        // Items.
        var items = new Dictionary<uint, Item>();
        var iinfBox = Single("iinf") ?? throw new UserError(Invalid);
        var iinf = new Reader(meta, iinfBox); var (iinfVersion, _) = iinf.Full();
        long declared = iinfVersion == 0 ? iinf.U16() : iinfVersion == 1 ? iinf.U32() : throw new UserError(Invalid);
        if (declared > MaxItems) throw new UserError(Unsupported);
        var entries = Children(meta, iinf.Position, iinfBox.End, 2, walk).Where(b => b.Type == "infe").ToList();
        if (entries.Count != declared) throw new UserError(Invalid);
        foreach (var infe in entries)
        {
            var r = new Reader(meta, infe); var (version, _) = r.Full();
            if (version is not (2 or 3)) throw new UserError(Unsupported);
            uint id = version == 2 ? r.U16() : r.U32(); r.U16(); string type = r.Tag4();
            if (!items.TryAdd(id, new Item(id, type))) throw new UserError(Invalid);
        }
        if (!items.TryGetValue(primary, out var primaryItem)) throw new UserError(Invalid);
        // Locations, each within the file or the idat box.
        Box? idat = Single("idat");
        var locations = Iloc(meta, Single("iloc") ?? throw new UserError(Invalid), fileLength, idat, token);
        if (!locations.TryGetValue(primary, out var primaryLocation)) throw new UserError(Invalid);
        // References.
        var references = new List<(string Type, uint From, List<uint> To)>();
        if (Single("iref") is { } irefBox)
        {
            var r = new Reader(meta, irefBox); var (version, _) = r.Full();
            if (version > 1) throw new UserError(Invalid);
            foreach (var reference in Children(meta, r.Position, irefBox.End, 2, walk))
            {
                var rr = new Reader(meta, reference);
                uint from = version == 0 ? rr.U16() : rr.U32(); int count = rr.U16();
                if (count > MaxItems) throw new UserError(Unsupported);
                var to = new List<uint>(count);
                for (int i = 0; i < count; i++) to.Add(version == 0 ? rr.U16() : rr.U32());
                if (rr.Remaining != 0) throw new UserError(Invalid);
                references.Add((reference.Type, from, to));
            }
        }
        // Properties and their associations.
        var iprp = Single("iprp") ?? throw new UserError(Invalid);
        var iprpChildren = Children(meta, iprp.Start, iprp.End, 2, walk);
        var ipcos = iprpChildren.Where(b => b.Type == "ipco").ToList();
        if (ipcos.Count != 1) throw new UserError(Invalid);
        var ipco = ipcos[0];
        var properties = Children(meta, ipco.Start, ipco.End, 3, walk);
        if (properties.Count > MaxProperties) throw new UserError(Unsupported);
        var associations = new Dictionary<uint, List<(int Index, bool Essential)>>();
        var ipmas = iprpChildren.Where(b => b.Type == "ipma").ToList();
        if (ipmas.Count == 0) throw new UserError(Invalid);
        foreach (var ipmaBox in ipmas)
        {
            var r = new Reader(meta, ipmaBox); var (version, flags) = r.Full();
            if (version > 1) throw new UserError(Invalid);
            uint count = r.U32();
            if (count > MaxItems) throw new UserError(Unsupported);
            for (uint i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                uint id = version < 1 ? r.U16() : r.U32(); int n = r.U8();
                var list = new List<(int, bool)>(n);
                for (int j = 0; j < n; j++)
                {
                    int value = (flags & 1) != 0 ? r.U16() : r.U8();
                    int bits = (flags & 1) != 0 ? 15 : 7;
                    int index = value & ((1 << bits) - 1); bool essential = (value >> bits) != 0;
                    if (index > properties.Count) throw new UserError(Invalid);
                    if (index > 0) list.Add((index, essential));
                }
                if (!associations.TryAdd(id, list)) throw new UserError(Invalid);
            }
            if (r.Remaining != 0) throw new UserError(Invalid);
        }
        List<Box> PropertiesOf(uint id, bool checkEssential)
        {
            var found = new List<Box>();
            foreach (var (index, essential) in associations.GetValueOrDefault(id) ?? new())
            {
                var box = properties[index - 1];
                if (box.Type == "clap") throw new UserError(Unsupported);
                if (checkEssential && essential && !KnownProperties.Contains(box.Type)) throw new UserError(Unsupported);
                found.Add(box);
            }
            return found;
        }
        // The primary image: coded HEVC or AV1, or a grid of such tiles.
        var primaryProperties = PropertiesOf(primary, true);
        var coded = new List<uint>();
        bool grid = false; (int Rows, int Columns, uint Width, uint Height) layout = default;
        switch (primaryItem.Type)
        {
            case "hvc1": case "av01": coded.Add(primary); break;
            case "grid":
                grid = true;
                var dimg = references.Where(x => x.Type == "dimg" && x.From == primary).ToList();
                if (dimg.Count != 1 || dimg[0].To.Count == 0) throw new UserError(Invalid);
                layout = Grid(meta, primaryLocation, idat, dimg[0].To.Count);
                foreach (uint tile in dimg[0].To)
                {
                    if (!items.TryGetValue(tile, out var tileItem) || tileItem.Type is not ("hvc1" or "av01") || !locations.ContainsKey(tile)) throw new UserError(Invalid);
                    coded.Add(tile);
                }
                break;
            default: throw new UserError(Unsupported);
        }
        if (coded.Select(id => items[id].Type).Distinct().Count() != 1) throw new UserError(Invalid);
        var codec = items[coded[0]].Type == "hvc1" ? HeifCodec.Hevc : HeifCodec.Av1;
        // Size of the primary image before rotation.
        var ispe = primaryProperties.Where(b => b.Type == "ispe").ToList();
        if (ispe.Count != 1) throw new UserError(Invalid);
        var size = new Reader(meta, ispe[0]); size.Full();
        uint width = size.U32(), height = size.U32();
        if (width > int.MaxValue || height > int.MaxValue) throw new UserError("Images are limited to 16 megapixels and 16384 pixels per side.");
        ImageRules.Validate((int)width, (int)height);
        if (grid)
        {
            if (layout.Width != width || layout.Height != height) throw new UserError(Invalid);
            // Every tile is bounded too, so the decoder's work stays near the picture's own size.
            long tilePixels = 0;
            foreach (uint tile in coded)
            {
                var tileSize = PropertiesOf(tile, true).Where(b => b.Type == "ispe").ToList();
                if (tileSize.Count != 1) throw new UserError(Invalid);
                var t = new Reader(meta, tileSize[0]); t.Full();
                uint tileWidth = t.U32(), tileHeight = t.U32();
                if (tileWidth is 0 or > 16384 || tileHeight is 0 or > 16384) throw new UserError(Unsupported);
                tilePixels += (long)tileWidth * tileHeight;
                if (tilePixels > 4 * ImageRules.MaxPixels) throw new UserError(Unsupported);
            }
        }
        // Bit depth from the codec configuration of every coded item, and pixi when present.
        int depth = 8;
        foreach (uint id in coded)
        {
            var codedProperties = id == primary ? primaryProperties : PropertiesOf(id, true);
            string config = codec == HeifCodec.Hevc ? "hvcC" : "av1C";
            var box = codedProperties.FirstOrDefault(b => b.Type == config);
            if (box.Type is null) throw new UserError(Invalid);
            depth = Math.Max(depth, codec == HeifCodec.Hevc ? HevcDepth(meta, box) : Av1Depth(meta, box));
            foreach (var pixi in codedProperties.Where(b => b.Type == "pixi")) depth = Math.Max(depth, PixiDepth(meta, pixi));
        }
        foreach (var pixi in primaryProperties.Where(b => b.Type == "pixi")) depth = Math.Max(depth, PixiDepth(meta, pixi));
        // Colour, on the primary image and every coded item: a PQ or HLG transfer or HDR luminance
        // properties mark HDR; nclx primaries other than BT.709 (1) or unspecified (2) without an
        // ICC profile are a wide gamut Swivel can't convert. Non-square pixels are refused.
        bool hdr = false, wide = false, icc = false;
        foreach (var box in coded.Where(id => id != primary).SelectMany(id => PropertiesOf(id, true)).Concat(primaryProperties))
        {
            hdr |= box.Type is "clli" or "mdcv" or "cclv" or "amve";
            if (box.Type == "pasp")
            {
                var r = new Reader(meta, box); uint h = r.U32(), v = r.U32();
                if (h != v) throw new UserError(Unsupported);
            }
            if (box.Type != "colr") continue;
            var c = new Reader(meta, box); string kind = c.Tag4();
            if (kind == "nclx") { ushort primaries = c.U16(), transfer = c.U16(); hdr |= transfer is 16 or 18; wide |= primaries is not (1 or 2); }
            else if (kind is "prof" or "rICC") icc = true;
        }
        // Rotation and mirroring, applied in association order, as an EXIF orientation.
        var transforms = new List<(string Type, int Value)>();
        foreach (var box in primaryProperties)
        {
            if (box.Type == "irot") transforms.Add(("irot", new Reader(meta, box).U8() & 3));
            else if (box.Type == "imir") transforms.Add(("imir", new Reader(meta, box).U8() & 1));
        }
        // An auxiliary alpha image of the primary.
        bool alpha = false;
        foreach (var (type, from, to) in references)
        {
            if (type != "auxl" || !to.Contains(primary) || !items.ContainsKey(from)) continue;
            foreach (var box in PropertiesOf(from, false).Where(b => b.Type == "auxC"))
            {
                var r = new Reader(meta, box); r.Full();
                string urn = r.CString();
                alpha |= urn is "urn:mpeg:hevc:2015:auxid:1" or "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";
            }
        }
        return new HeifInfo(codec, (int)width, (int)height, Orientation(transforms), depth, hdr, alpha, grid, wide && !icc);
    }

    /// The EXIF orientation equivalent to HEIF transforms applied in order: irot turns the image
    /// anticlockwise by value × 90°, imir mirrors about a vertical (0: left-right) or horizontal
    /// (1: top-bottom) axis. Worked in y-up coordinates as 2 × 2 integer matrices.
    public static int Orientation(IEnumerable<(string Type, int Value)> transforms)
    {
        (int A, int B, int C, int D) m = (1, 0, 0, 1);
        static (int, int, int, int) Then((int A, int B, int C, int D) t, (int A, int B, int C, int D) m) =>
            (t.A * m.A + t.B * m.C, t.A * m.B + t.B * m.D, t.C * m.A + t.D * m.C, t.C * m.B + t.D * m.D);
        foreach (var (type, value) in transforms)
        {
            if (type == "irot") for (int i = 0; i < (value & 3); i++) m = Then((0, -1, 1, 0), m);
            else if (type == "imir") m = Then((value & 1) == 0 ? (-1, 0, 0, 1) : (1, 0, 0, -1), m);
        }
        return m switch {
            (1, 0, 0, 1) => 1, (-1, 0, 0, 1) => 2, (-1, 0, 0, -1) => 3, (1, 0, 0, -1) => 4,
            (0, -1, -1, 0) => 5, (0, 1, -1, 0) => 6, (0, 1, 1, 0) => 7, _ => 8
        };
    }

    /// Orientations whose rotation swaps width and height.
    public static bool SwapsSides(int orientation) => orientation is 5 or 6 or 7 or 8;

    private static void Dinf(byte[] meta, Box dinf, Walk walk)
    {
        // Only "this file" data references (self-contained flag) are accepted.
        foreach (var dref in Children(meta, dinf.Start, dinf.End, 2, walk).Where(b => b.Type == "dref"))
        {
            var r = new Reader(meta, dref); r.Full(); uint count = r.U32();
            var entries = Children(meta, r.Position, dref.End, 3, walk);
            if (entries.Count != count) throw new UserError(Invalid);
            foreach (var entry in entries)
            {
                var e = new Reader(meta, entry); var (_, flags) = e.Full();
                if ((flags & 1) == 0) throw new UserError(Unsupported);
            }
        }
    }

    private static Dictionary<uint, Location> Iloc(byte[] meta, Box box, long fileLength, Box? idat, CancellationToken token)
    {
        var r = new Reader(meta, box); var (version, _) = r.Full();
        if (version > 2) throw new UserError(Invalid);
        int sizes = r.U16();
        int offsetSize = sizes >> 12, lengthSize = (sizes >> 8) & 15, baseSize = (sizes >> 4) & 15, indexSize = version is 1 or 2 ? sizes & 15 : 0;
        foreach (int s in new[] { offsetSize, lengthSize, baseSize, indexSize }) if (s is not (0 or 4 or 8)) throw new UserError(Invalid);
        uint count = version < 2 ? r.U16() : r.U32();
        if (count > MaxItems) throw new UserError(Unsupported);
        var locations = new Dictionary<uint, Location>();
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            uint id = version < 2 ? r.U16() : r.U32();
            int method = version is 1 or 2 ? r.U16() & 15 : 0;
            int reference = r.U16();
            ulong baseOffset = r.Sized(baseSize);
            int extents = r.U16();
            if (extents is 0 or > 1024) throw new UserError(extents == 0 ? Invalid : Unsupported);
            var list = new List<(ulong, ulong)>(extents);
            for (int e = 0; e < extents; e++)
            {
                r.Sized(indexSize);
                ulong offset = r.Sized(offsetSize), length = r.Sized(lengthSize);
                list.Add((offset, length));
            }
            // External data, and data built from other items, are not followed.
            if (reference != 0 || method == 2) throw new UserError(Unsupported);
            if (method > 2) throw new UserError(Invalid);
            long limit = method == 0 ? fileLength : idat is { } d ? d.End - d.Start : -1;
            if (limit < 0) throw new UserError(Invalid);
            foreach (var (offset, length) in list)
            {
                // A zero length means "to the end", allowed only for a single extent.
                if (baseOffset > (ulong)limit || offset > (ulong)limit - baseOffset) throw new UserError(Invalid);
                ulong start = baseOffset + offset;
                if (length == 0 ? extents != 1 : length > (ulong)limit - start) throw new UserError(Invalid);
            }
            if (!locations.TryAdd(id, new Location(method, reference, list, baseOffset))) throw new UserError(Invalid);
        }
        if (r.Remaining != 0) throw new UserError(Invalid);
        return locations;
    }

    /// A grid description (ImageGrid) in idat: version 0, rows × columns equal to the tile count,
    /// output size in 16- or 32-bit fields. A grid description elsewhere is not read.
    private static (int, int, uint, uint) Grid(byte[] meta, Location location, Box? idat, int tiles)
    {
        if (location.Method != 1 || idat is not { } data || location.Extents.Count != 1) throw new UserError(Unsupported);
        var (offset, length) = location.Extents[0];
        long start = data.Start + (long)(location.BaseOffset + offset);
        long count = length == 0 ? data.End - start : (long)length;
        bool wide = count >= 2 && (meta[start + 1] & 1) != 0;
        if (count != (wide ? 12 : 8) || meta[start] != 0) throw new UserError(Invalid);
        int rows = meta[start + 2] + 1, columns = meta[start + 3] + 1;
        if (rows * columns != tiles) throw new UserError(Invalid);
        var field = meta.AsSpan((int)start + 4);
        return wide ? (rows, columns, BinaryPrimitives.ReadUInt32BigEndian(field), BinaryPrimitives.ReadUInt32BigEndian(field[4..]))
            : (rows, columns, BinaryPrimitives.ReadUInt16BigEndian(field), BinaryPrimitives.ReadUInt16BigEndian(field[2..]));
    }

    private static int HevcDepth(byte[] meta, Box box)
    {
        // HEVCDecoderConfigurationRecord: bitDepthLumaMinus8 and bitDepthChromaMinus8 at bytes 17 and 18.
        if (box.End - box.Start < 23) throw new UserError(Invalid);
        return 8 + Math.Max(meta[box.Start + 17] & 7, meta[box.Start + 18] & 7);
    }

    private static int Av1Depth(byte[] meta, Box box)
    {
        // AV1CodecConfigurationRecord: marker/version 0x81, then high_bitdepth and twelve_bit flags in byte 2.
        if (box.End - box.Start < 4 || meta[box.Start] != 0x81) throw new UserError(Invalid);
        int flags = meta[box.Start + 2];
        bool high = (flags & 0x40) != 0, twelve = (flags & 0x20) != 0;
        return high ? twelve ? 12 : 10 : 8;
    }

    private static int PixiDepth(byte[] meta, Box box)
    {
        var r = new Reader(meta, box); r.Full();
        int channels = r.U8(), depth = 0;
        if (channels == 0) throw new UserError(Invalid);
        for (int i = 0; i < channels; i++) depth = Math.Max(depth, r.U8());
        if (depth == 0) throw new UserError(Invalid);
        return depth;
    }

    private static string Tag(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++) chars[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '?';
        return new string(chars);
    }
}
