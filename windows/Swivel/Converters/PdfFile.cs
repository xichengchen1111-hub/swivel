using System.Security.Cryptography;
using System.Text;

namespace Swivel;

/// One page as found in the page tree, with the inheritable attributes resolved.
sealed class PdfPage
{
    public required PdfRef Ref;
    public required PdfDict Dict;
    public PdfObj? Resources, MediaBox, CropBox;
    public int Rotate;

    /// The media box in default user space.
    public (double X0, double Y0, double X1, double Y1) Media = (0, 0, 612, 792);

    /// The visible area: the crop box clipped to the media box.
    public (double X0, double Y0, double X1, double Y1) Visible = (0, 0, 612, 792);
}

/// A PDF file opened for reading. Handles classic cross-reference tables, cross-reference streams, object streams,
/// incremental updates (newest definition wins), broken offsets (rebuilds the table by scanning), and the standard
/// security handler with an empty user password (files that open without asking, like on the Mac). Files that need a
/// password are refused.
sealed class PdfFile
{
    readonly struct XrefEntry(int type, long offset, int gen)
    {
        /// 1 = at a byte offset, 2 = inside an object stream (offset = stream number, gen = index).
        public readonly int Type = type;
        public readonly long Offset = offset;
        public readonly int Gen = gen;
    }

    public readonly byte[] Data;
    public readonly string Name;
    readonly Dictionary<int, XrefEntry> xref = [];
    readonly Dictionary<int, PdfObj> cache = [];
    readonly Dictionary<int, (PdfLexer Lexer, Dictionary<int, int> Offsets, int First)> objectStreams = [];
    readonly HashSet<int> resolving = [];
    PdfCrypt? crypt;
    int encryptNum = -1;
    public PdfDict Trailer = new();
    List<PdfPage>? pages;

    PdfFile(byte[] data, string name)
    {
        Data = data;
        Name = name;
    }

    /// Opens a file; throws ToolError "Couldn't open name" or "name is password protected".
    public static PdfFile Open(string path) => Load(ReadAll(path), Path.GetFileName(path));

    static byte[] ReadAll(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolError(L("Couldn't open %@", Path.GetFileName(path)));
        }
    }

    public static PdfFile Load(byte[] data, string name)
    {
        var file = new PdfFile(data, name);
        try
        {
            file.ReadStructure();
            _ = file.Pages;
        }
        catch (ToolError) { throw; }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidCastException or NullReferenceException or OverflowException)
        {
            throw new ToolError(L("Couldn't open %@", name));
        }
        return file;
    }

    public bool WasEncrypted => crypt != null;

    // ─────────────────────────────── structure ───────────────────────────────

    void ReadStructure()
    {
        if (PdfLexer.Find(Data, "%PDF-", 0, Math.Min(Data.Length, 1024)) < 0 && PdfLexer.Find(Data, "obj", 0, Math.Min(Data.Length, 4096)) < 0)
            throw new ToolError(L("Couldn't open %@", Name));
        bool ok = false;
        try { ok = ReadXrefChain(); }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidCastException) { ok = false; }
        if (!ok || Trailer["Root"] is not PdfRef || Resolve(Trailer["Root"]) is not PdfDict)
        {
            xref.Clear();
            cache.Clear();
            Rebuild();
        }
        if (Trailer["Root"] == null) throw new ToolError(L("Couldn't open %@", Name));
        if (Trailer["Encrypt"] is { } encrypt)
        {
            if (encrypt is PdfRef er) encryptNum = er.Num;
            var dict = Resolve(encrypt) as PdfDict ?? throw new ToolError(L("%@ is password protected", Name));
            var id = (Resolve(Trailer["ID"]) as PdfArray)?.Items.FirstOrDefault() is PdfStr s ? s.Bytes : [];
            crypt = PdfCrypt.Open(dict, id) ?? throw new ToolError(L("%@ is password protected", Name));
            // Objects read so far (only the trailer's) were read before decryption was known.
            cache.Clear();
        }
    }

    long StartXref()
    {
        int at = PdfLexer.FindLast(Data, "startxref", Data.Length);
        if (at < 0) return -1;
        var lexer = new PdfLexer(Data, at + 9);
        return lexer.Next() is PdfNum n ? n.Long : -1;
    }

    bool ReadXrefChain()
    {
        long offset = StartXref();
        if (offset <= 0 || offset >= Data.Length) return false;
        var seen = new HashSet<long>();
        bool first = true;
        while (offset > 0 && offset < Data.Length && seen.Add(offset))
        {
            PdfDict? trailer = ReadXrefSection(offset);
            if (trailer == null) return false;
            if (first)
            {
                Trailer = trailer;
                first = false;
            }
            else
                foreach (var (k, v) in trailer.Items)
                    if (!Trailer.Has(k)) Trailer.Items[k] = v;
            // Hybrid files: the table points at an extra cross-reference stream for compressed objects.
            if (trailer["XRefStm"] is PdfNum stm && seen.Add(stm.Long)) ReadXrefSection(stm.Long);
            offset = trailer["Prev"] is PdfNum prev ? prev.Long : -1;
        }
        Trailer.Remove("Prev");
        Trailer.Remove("XRefStm");
        return xref.Count > 0;
    }

    PdfDict? ReadXrefSection(long offset)
    {
        var lexer = new PdfLexer(Data, (int)offset);
        lexer.SkipWhite();
        if (lexer.Pos + 4 <= Data.Length && Encoding.ASCII.GetString(Data, lexer.Pos, 4) == "xref")
        {
            lexer.Pos += 4;
            while (true)
            {
                var o = lexer.Next(resolveRefs: false);
                if (o is PdfKeyword { Value: "trailer" }) return lexer.Next() as PdfDict;
                if (o is not PdfNum start) return null;
                if (lexer.Next(resolveRefs: false) is not PdfNum count) return null;
                for (int i = 0; i < count.Int; i++)
                {
                    lexer.SkipWhite();
                    // "nnnnnnnnnn ggggg n" — parse tolerantly (some writers use single-space separators or short lines).
                    if (lexer.Next(resolveRefs: false) is not PdfNum off || lexer.Next(resolveRefs: false) is not PdfNum gen ||
                        lexer.Next(resolveRefs: false) is not PdfKeyword kind) return null;
                    int num = start.Int + i;
                    if (num == 0 && i == 0 && kind.Value == "f") continue;
                    if (!xref.ContainsKey(num))
                        xref[num] = kind.Value == "n" ? new XrefEntry(1, off.Long, gen.Int) : new XrefEntry(0, 0, gen.Int);
                }
            }
        }
        // A cross-reference stream: "n g obj << /Type /XRef … >> stream".
        var (_, obj) = ParseIndirectAt((int)offset, decrypt: false);
        if (obj is not PdfStream stream || stream.Dict.Name("Type") != "XRef") return null;
        var data = Decoded(stream) ?? throw new PdfFormatException("xref stream");
        if (stream.Dict["W"] is not PdfArray w || w.Count < 3) return null;
        int[] widths = [.. w.Items.Select(x => (x as PdfNum)?.Int ?? 0)];
        int size = (stream.Dict["Size"] as PdfNum)?.Int ?? 0;
        var index = stream.Dict["Index"] is PdfArray ia ? ia.Items.Select(x => (x as PdfNum)?.Int ?? 0).ToList() : [0, size];
        int rowLength = widths.Sum();
        int pos = 0;
        for (int k = 0; k + 1 < index.Count; k += 2)
        {
            for (int i = 0; i < index[k + 1]; i++)
            {
                if (pos + rowLength > data.Length) break;
                long Field(int f, long fallback)
                {
                    if (widths[f] == 0) return fallback;
                    long v = 0;
                    for (int b = 0; b < widths[f]; b++) v = (v << 8) | data[pos++];
                    return v;
                }
                int type = (int)Field(0, 1);
                long f2 = Field(1, 0);
                long f3 = Field(2, 0);
                int num = index[k] + i;
                if (!xref.ContainsKey(num))
                    xref[num] = type switch
                    {
                        1 => new XrefEntry(1, f2, (int)f3),
                        2 => new XrefEntry(2, f2, (int)f3),
                        _ => new XrefEntry(0, 0, 0),
                    };
            }
        }
        return stream.Dict;
    }

    /// Rebuilds the cross-reference table by scanning for "n g obj" (for files with broken offsets).
    void Rebuild()
    {
        Trailer = new PdfDict();
        var span = Data.AsSpan();
        var objToken = "obj"u8;
        int i = 0;
        var trailers = new List<PdfDict>();
        while (i < Data.Length)
        {
            int found = span[i..].IndexOf(objToken);
            if (found < 0) break;
            int at = i + found;
            i = at + 3;
            if (at > 0 && !PdfLexer.IsWhite(Data[at - 1])) continue;
            // Walk back over "n g ".
            int p = at - 1;
            while (p >= 0 && PdfLexer.IsWhite(Data[p])) p--;
            int genEnd = p;
            while (p >= 0 && Data[p] is >= (byte)'0' and <= (byte)'9') p--;
            if (p == genEnd) continue;
            if (!int.TryParse(Encoding.ASCII.GetString(Data, p + 1, genEnd - p), out var gen)) continue;
            while (p >= 0 && PdfLexer.IsWhite(Data[p])) p--;
            int numEnd = p;
            while (p >= 0 && Data[p] is >= (byte)'0' and <= (byte)'9') p--;
            if (p == numEnd || numEnd - p > 10) continue;
            if (!int.TryParse(Encoding.ASCII.GetString(Data, p + 1, numEnd - p), out var num)) continue;
            // Later definitions win (incremental updates append).
            xref[num] = new XrefEntry(1, p + 1, gen);
        }
        // Objects inside object streams.
        foreach (var (num, entry) in xref.ToList())
        {
            try
            {
                var (_, obj) = ParseIndirectAt((int)entry.Offset, decrypt: false);
                if (obj is PdfStream s && s.Dict.Name("Type") == "ObjStm" && LoadObjectStream(num, s) is { } os)
                    foreach (var (n, _) in os.Offsets)
                        if (!xref.ContainsKey(n)) xref[n] = new XrefEntry(2, num, -1);
                if (obj is PdfStream xs && xs.Dict.Name("Type") == "XRef") trailers.Add(xs.Dict);
            }
            catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException) { }
        }
        int t = 0;
        while ((t = PdfLexer.Find(Data, "trailer", t)) >= 0)
        {
            var lexer = new PdfLexer(Data, t + 7);
            if (lexer.Next() is PdfDict d) trailers.Add(d);
            t += 7;
        }
        foreach (var d in trailers)
            foreach (var key in new[] { "Root", "Info", "ID", "Encrypt" })
                if (d[key] is { } v) Trailer[key] = v;
        if (Trailer["Root"] == null)
        {
            // Find the catalog by type.
            foreach (var (num, _) in xref)
                if (Get(num) is PdfDict d && d.Name("Type") == "Catalog") { Trailer["Root"] = new PdfRef(num, 0); break; }
        }
        cache.Clear();
    }

    // ─────────────────────────────── objects ───────────────────────────────

    public IEnumerable<int> ObjectNumbers => xref.Where(e => e.Value.Type != 0).Select(e => e.Key);

    public PdfObj? Resolve(PdfObj? o)
    {
        for (int depth = 0; o is PdfRef r && depth < 32; depth++) o = Get(r.Num);
        return o is PdfRef ? null : o;
    }

    public PdfDict? Dict(PdfObj? o) => Resolve(o) as PdfDict ?? (Resolve(o) as PdfStream)?.Dict;

    public PdfObj? Get(int num)
    {
        if (cache.TryGetValue(num, out var cached)) return cached;
        if (!xref.TryGetValue(num, out var entry) || entry.Type == 0) return null;
        if (!resolving.Add(num)) return null;
        PdfObj? obj = null;
        try
        {
            if (entry.Type == 1)
            {
                var (parsedNum, parsed) = ParseIndirectAt((int)entry.Offset, decrypt: true, expected: num);
                if (parsedNum == num) obj = parsed;
            }
            else obj = FromObjectStream((int)entry.Offset, num, entry.Gen);
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidCastException)
        {
            obj = null;
        }
        finally { resolving.Remove(num); }
        obj ??= PdfObj.Null;
        cache[num] = obj;
        return obj;
    }

    (int Num, PdfObj Obj) ParseIndirectAt(int offset, bool decrypt, int expected = -1)
    {
        if (offset < 0 || offset >= Data.Length) throw new PdfFormatException("offset");
        var lexer = new PdfLexer(Data, offset);
        if (lexer.Next(resolveRefs: false) is not PdfNum num || lexer.Next(resolveRefs: false) is not PdfNum gen ||
            lexer.Next(resolveRefs: false) is not PdfKeyword { Value: "obj" })
            throw new PdfFormatException("not an object");
        var obj = lexer.Next() ?? PdfObj.Null;
        if (obj is PdfKeyword { Value: "endobj" }) obj = PdfObj.Null;
        if (obj is PdfDict dict)
        {
            int save = lexer.Pos;
            var next = lexer.Next(resolveRefs: false);
            if (next is PdfKeyword { Value: "stream" })
            {
                int start = lexer.Pos;
                if (start < Data.Length && Data[start] == '\r') start++;
                if (start < Data.Length && Data[start] == '\n') start++;
                int length = -1;
                var lengthObj = dict["Length"];
                if (lengthObj is PdfNum ln) length = ln.Int;
                else if (lengthObj is PdfRef lr && lr.Num != num.Int && Get(lr.Num) is PdfNum lv) length = lv.Int;
                int end = start + length;
                bool valid = length >= 0 && end <= Data.Length && EndstreamNear(end);
                if (!valid)
                {
                    int es = PdfLexer.Find(Data, "endstream", start);
                    if (es < 0) es = Data.Length;
                    end = es;
                    while (end > start && Data[end - 1] is (byte)'\n' or (byte)'\r') end--;
                }
                var bytes = Data[start..end];
                var stream = new PdfStream(dict, bytes);
                if (decrypt && crypt != null && num.Int != encryptNum && dict.Name("Type") != "XRef")
                {
                    DecryptStrings(dict, num.Int, gen.Int);
                    if (!(dict.Name("Type") == "Metadata" && !crypt.EncryptMetadata))
                        stream.Data = crypt.DecryptStream(bytes, num.Int, gen.Int, dict);
                }
                return (num.Int, stream);
            }
            lexer.Pos = save;
        }
        if (decrypt && crypt != null && num.Int != encryptNum) obj = DecryptStrings(obj, num.Int, gen.Int);
        return (num.Int, obj);
    }

    bool EndstreamNear(int end)
    {
        int p = end;
        while (p < Data.Length && PdfLexer.IsWhite(Data[p])) p++;
        return p + 9 <= Data.Length && Encoding.ASCII.GetString(Data, p, 9) == "endstream";
    }

    PdfObj DecryptStrings(PdfObj o, int num, int gen)
    {
        switch (o)
        {
            case PdfStr s:
                s.Bytes = crypt!.DecryptString(s.Bytes, num, gen);
                return s;
            case PdfArray a:
                for (int i = 0; i < a.Count; i++) a[i] = DecryptStrings(a[i], num, gen);
                return a;
            case PdfDict d:
                foreach (var key in d.Items.Keys.ToList()) d.Items[key] = DecryptStrings(d.Items[key], num, gen);
                return d;
            default:
                return o;
        }
    }

    (PdfLexer Lexer, Dictionary<int, int> Offsets, int First)? LoadObjectStream(int streamNum, PdfStream? stream = null)
    {
        if (objectStreams.TryGetValue(streamNum, out var loaded)) return loaded;
        stream ??= Get(streamNum) as PdfStream;
        if (stream == null) return null;
        var data = Decoded(stream);
        if (data == null) return null;
        int n = (Resolve(stream.Dict["N"]) as PdfNum)?.Int ?? 0;
        int first = (Resolve(stream.Dict["First"]) as PdfNum)?.Int ?? 0;
        var lexer = new PdfLexer(data, 0, Math.Min(first, data.Length));
        var offsets = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (lexer.Next(resolveRefs: false) is not PdfNum objNum || lexer.Next(resolveRefs: false) is not PdfNum off) break;
            offsets.TryAdd(objNum.Int, off.Int);
        }
        var result = (new PdfLexer(data), offsets, first);
        objectStreams[streamNum] = result;
        return result;
    }

    PdfObj? FromObjectStream(int streamNum, int num, int index)
    {
        var os = LoadObjectStream(streamNum);
        if (os is not { } s || !s.Offsets.TryGetValue(num, out var offset)) return null;
        var lexer = new PdfLexer(s.Lexer.Data, s.First + offset);
        var obj = lexer.Next();
        return obj is PdfKeyword ? null : obj;
    }

    /// The stream's data with all general filters undone; null if a filter isn't supported.
    public byte[]? Decoded(PdfStream stream, bool stopAtImage = false) =>
        PdfFilters.Decode(stream.Data, PdfFilters.Chain(stream.Dict, Resolve), stopAtImage);

    // ─────────────────────────────── pages ───────────────────────────────

    public PdfDict Catalog => Resolve(Trailer["Root"]) as PdfDict ?? throw new ToolError(L("Couldn't open %@", Name));

    public List<PdfPage> Pages => pages ??= LoadPages();

    List<PdfPage> LoadPages()
    {
        var result = new List<PdfPage>();
        var visited = new HashSet<int>();
        void Walk(PdfObj? node, PdfObj? resources, PdfObj? media, PdfObj? crop, PdfObj? rotate, int depth)
        {
            if (depth > 64 || node is not PdfRef r || !visited.Add(r.Num)) return;
            if (Resolve(r) is not PdfDict dict) return;
            resources = dict["Resources"] ?? resources;
            media = dict["MediaBox"] ?? media;
            crop = dict["CropBox"] ?? crop;
            rotate = dict["Rotate"] ?? rotate;
            var type = dict.Name("Type");
            if (type == "Pages" || (type == null && dict.Has("Kids")))
            {
                if (Resolve(dict["Kids"]) is PdfArray kids)
                    foreach (var kid in kids.Items) Walk(kid, resources, media, crop, rotate, depth + 1);
                return;
            }
            var page = new PdfPage
            {
                Ref = r,
                Dict = dict,
                Resources = resources,
                MediaBox = media,
                CropBox = crop,
                Rotate = ((((Resolve(rotate) as PdfNum)?.Int ?? 0) % 360) + 360) % 360 / 90 * 90,
            };
            page.Media = Box(media) ?? (0, 0, 612, 792);
            var c = Box(crop) ?? page.Media;
            page.Visible = (Math.Max(c.X0, page.Media.X0), Math.Max(c.Y0, page.Media.Y0), Math.Min(c.X1, page.Media.X1), Math.Min(c.Y1, page.Media.Y1));
            if (page.Visible.X1 <= page.Visible.X0 || page.Visible.Y1 <= page.Visible.Y0) page.Visible = page.Media;
            result.Add(page);
        }
        Walk(Catalog["Pages"], null, null, null, null, 0);
        return result;
    }

    /// A rectangle array, normalized so X0 ≤ X1 and Y0 ≤ Y1.
    public (double X0, double Y0, double X1, double Y1)? Box(PdfObj? o)
    {
        if (Resolve(o) is not PdfArray a || a.Count < 4) return null;
        var v = a.Items.Take(4).Select(x => (Resolve(x) as PdfNum)?.Value ?? 0).ToArray();
        var box = (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        return box.Item3 - box.Item1 <= 0 || box.Item4 - box.Item2 <= 0 ? null : box;
    }

    /// The page's content streams, decoded and joined.
    public byte[] PageContent(PdfPage page)
    {
        var contents = Resolve(page.Dict["Contents"]);
        var parts = new List<byte[]>();
        if (contents is PdfStream s) { if (Decoded(s) is { } d) parts.Add(d); }
        else if (contents is PdfArray a)
            foreach (var item in a.Items)
                if (Resolve(item) is PdfStream ps && Decoded(ps) is { } d) parts.Add(d);
        if (parts.Count == 1) return parts[0];
        var ms = new MemoryStream();
        foreach (var p in parts) { ms.Write(p); ms.WriteByte((byte)'\n'); }
        return ms.ToArray();
    }
}

/// The standard security handler (ISO 32000-2 §7.6.4), for files whose user password is empty.
sealed class PdfCrypt
{
    static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    enum Method { None, Rc4, AesV2, AesV3 }

    readonly byte[] key;
    readonly Method strings, streams;
    public bool EncryptMetadata { get; private init; } = true;

    PdfCrypt(byte[] key, Method strings, Method streams)
    {
        this.key = key;
        this.strings = strings;
        this.streams = streams;
    }

    /// Returns null when the empty password doesn't open the file (it needs a password) or the handler is unknown.
    public static PdfCrypt? Open(PdfDict dict, byte[] id)
    {
        if (dict.Name("Filter") != "Standard") return null;
        int v = (dict["V"] as PdfNum)?.Int ?? 0;
        int r = (dict["R"] as PdfNum)?.Int ?? 0;
        var o = (dict["O"] as PdfStr)?.Bytes ?? [];
        var u = (dict["U"] as PdfStr)?.Bytes ?? [];
        int p = (dict["P"] as PdfNum)?.Int ?? 0;
        bool encryptMetadata = dict["EncryptMetadata"] is not PdfBool { Value: false };
        int length = (dict["Length"] as PdfNum)?.Int ?? 40;

        Method stmMethod = Method.Rc4, strMethod = Method.Rc4;
        if (v >= 4)
        {
            var filters = dict["CF"] as PdfDict;
            Method MethodOf(string? name)
            {
                if (name is null or "Identity") return Method.None;
                var cf = filters?[name] as PdfDict;
                return cf?.Name("CFM") switch
                {
                    "AESV2" => Method.AesV2,
                    "AESV3" => Method.AesV3,
                    "V2" => Method.Rc4,
                    "None" => Method.None,
                    _ => Method.Rc4,
                };
            }
            stmMethod = MethodOf(dict.Name("StmF") ?? "Identity");
            strMethod = MethodOf(dict.Name("StrF") ?? "Identity");
            if (filters?[dict.Name("StmF") ?? ""] is PdfDict scf && scf["Length"] is PdfNum sl) length = sl.Int <= 32 ? sl.Int * 8 : sl.Int;
        }

        if (r >= 5)
        {
            if (u.Length < 48) return null;
            var password = Array.Empty<byte>();
            var check = Hash(r, password, u.AsSpan(32, 8).ToArray(), []);
            if (!check.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32))) return null;
            var intermediate = Hash(r, password, u.AsSpan(40, 8).ToArray(), []);
            var ue = (dict["UE"] as PdfStr)?.Bytes;
            if (ue == null || ue.Length < 32) return null;
            using var aes = Aes.Create();
            aes.Key = intermediate[..32];
            var fileKey = aes.DecryptCbc(ue.AsSpan(0, 32), new byte[16], PaddingMode.None);
            return new PdfCrypt(fileKey, strMethod, stmMethod) { EncryptMetadata = encryptMetadata };
        }

        // Algorithm 2: the file key from the (empty, padded) user password.
        int n = r == 2 ? 5 : Math.Clamp(length / 8, 5, 16);
        var input = new List<byte>(Padding);
        input.AddRange(o.Take(32));
        input.AddRange(BitConverter.GetBytes(p));
        input.AddRange(id);
        if (r >= 4 && !encryptMetadata) input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        var hash = MD5.HashData(input.ToArray());
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, n));
        var k = hash[..n];

        // Algorithms 4 and 5: check the U entry.
        bool valid;
        if (r == 2) valid = Rc4(k, Padding).AsSpan().SequenceEqual(u.AsSpan(0, Math.Min(32, u.Length)));
        else
        {
            var x = MD5.HashData([.. Padding, .. id]);
            x = Rc4(k, x);
            for (int i = 1; i <= 19; i++) x = Rc4([.. k.Select(b => (byte)(b ^ i))], x);
            valid = u.Length >= 16 && x.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
        }
        if (!valid) return null;
        return new PdfCrypt(k, strMethod, stmMethod) { EncryptMetadata = encryptMetadata };
    }

    /// Algorithm 2.B (R6) or plain SHA-256 (R5).
    static byte[] Hash(int r, byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (r == 5) return k;
        using var aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            var k1Unit = new List<byte>(password.Length + k.Length + userKey.Length);
            k1Unit.AddRange(password);
            k1Unit.AddRange(k);
            k1Unit.AddRange(userKey);
            var k1 = new byte[k1Unit.Count * 64];
            for (int i = 0; i < 64; i++) k1Unit.CopyTo(k1, i * k1Unit.Count);
            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            if (round + 1 >= 64 && e[^1] <= round + 1 - 32) break;
        }
        return k[..32];
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return output;
    }

    byte[] ObjectKey(int num, int gen, Method method)
    {
        if (method == Method.AesV3) return key;
        var input = new List<byte>(key) { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8) };
        if (method == Method.AesV2) input.AddRange("sAlT"u8.ToArray());
        var hash = MD5.HashData(input.ToArray());
        return hash[..Math.Min(key.Length + 5, 16)];
    }

    byte[] Decrypt(byte[] data, int num, int gen, Method method)
    {
        switch (method)
        {
            case Method.None:
                return data;
            case Method.Rc4:
                return Rc4(ObjectKey(num, gen, method), data);
            default:
                if (data.Length < 32 || data.Length % 16 != 0)
                {
                    if (data.Length < 16) return data;
                }
                try
                {
                    using var aes = Aes.Create();
                    aes.Key = ObjectKey(num, gen, method);
                    int body = (data.Length - 16) / 16 * 16;
                    return aes.DecryptCbc(data.AsSpan(16, body), data.AsSpan(0, 16), PaddingMode.PKCS7);
                }
                catch (CryptographicException) { return data; }
        }
    }

    public byte[] DecryptString(byte[] data, int num, int gen) => Decrypt(data, num, gen, strings);

    public byte[] DecryptStream(byte[] data, int num, int gen, PdfDict dict)
    {
        // A stream may choose its own crypt filter (only Identity is common).
        if (dict["Filter"] is PdfName { Value: "Crypt" } && (dict["DecodeParms"] as PdfDict)?.Name("Name") is null or "Identity") return data;
        return Decrypt(data, num, gen, streams);
    }

    /// For tests: the U and O entries of an RC4 (R3) handler with an empty user password and the given owner password.
    public static (byte[] O, byte[] U, byte[] Key) MakeRc4(byte[] ownerPassword, byte[] id, int p, int lengthBits = 128)
    {
        int n = lengthBits / 8;
        byte[] Pad(byte[] pw) => [.. pw.Take(32), .. Padding.Take(32 - Math.Min(32, pw.Length))];
        // Algorithm 3: O.
        var hash = MD5.HashData(Pad(ownerPassword));
        for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
        var ok = hash[..n];
        var o = Rc4(ok, Pad([]));
        for (int i = 1; i <= 19; i++) o = Rc4([.. ok.Select(b => (byte)(b ^ i))], o);
        // Algorithm 2: file key.
        var input = new List<byte>(Padding);
        input.AddRange(o);
        input.AddRange(BitConverter.GetBytes(p));
        input.AddRange(id);
        var k = MD5.HashData(input.ToArray());
        for (int i = 0; i < 50; i++) k = MD5.HashData(k.AsSpan(0, n));
        k = k[..n];
        // Algorithm 5: U.
        var x = Rc4(k, MD5.HashData([.. Padding, .. id]));
        for (int i = 1; i <= 19; i++) x = Rc4([.. k.Select(b => (byte)(b ^ i))], x);
        var u = new byte[32];
        x.CopyTo(u, 0);
        return (o, u, k);
    }

    /// For tests: encrypts like `DecryptStream` decrypts (RC4 is symmetric).
    public static byte[] Rc4Object(byte[] fileKey, int num, int gen, byte[] data)
    {
        var input = new List<byte>(fileKey) { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8) };
        var hash = MD5.HashData(input.ToArray());
        return Rc4(hash[..Math.Min(fileKey.Length + 5, 16)], data);
    }
}
