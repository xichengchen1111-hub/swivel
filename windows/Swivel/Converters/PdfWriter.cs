using System.Security.Cryptography;
using System.Text;

namespace Swivel;

/// A new PDF being built: numbered objects, then serialized with a cross-reference table (or, compactly, with
/// object streams and a cross-reference stream).
sealed class PdfOutput
{
    readonly List<PdfObj?> objects = [];

    public PdfRef Reserve()
    {
        objects.Add(null);
        return new PdfRef(objects.Count, 0);
    }

    public PdfRef Add(PdfObj obj)
    {
        objects.Add(obj);
        return new PdfRef(objects.Count, 0);
    }

    public void Set(PdfRef r, PdfObj obj) => objects[r.Num - 1] = obj;

    public PdfObj? Get(PdfRef r) => r.Num >= 1 && r.Num <= objects.Count ? objects[r.Num - 1] : null;

    public int Count => objects.Count;

    /// Writes the file. `root` and `info` go into the trailer.
    public void Write(Stream output, PdfRef root, PdfRef? info, bool compact = false)
    {
        var id = RandomNumberGenerator.GetBytes(16);
        var ids = new PdfArray([new PdfStr(id, hex: true), new PdfStr(id, hex: true)]);
        var s = new CountingStream(output);
        PdfSerializer.Ascii(s, compact ? "%PDF-1.7\n" : "%PDF-1.7\n");
        s.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        int count = objects.Count;
        var offsets = new long[count + 1];
        // (stream object number, index) for objects stored in object streams.
        var packed = new Dictionary<int, (int Stream, int Index)>();

        if (compact)
        {
            // Pack plain (non-stream) objects into object streams of up to 200 objects.
            var plain = Enumerable.Range(1, count).Where(n => objects[n - 1] is not (null or PdfStream)).ToList();
            for (int start = 0; start < plain.Count; start += 200)
            {
                var chunk = plain.Skip(start).Take(200).ToList();
                var header = new StringBuilder();
                var body = new MemoryStream();
                for (int i = 0; i < chunk.Count; i++)
                {
                    header.Append(chunk[i]).Append(' ').Append(body.Length).Append(' ');
                    PdfSerializer.Write(body, objects[chunk[i] - 1]!);
                    body.WriteByte((byte)'\n');
                }
                var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                var data = new byte[headerBytes.Length + body.Length];
                headerBytes.CopyTo(data, 0);
                body.ToArray().CopyTo(data, headerBytes.Length);
                var dict = new PdfDict();
                dict["Type"] = new PdfName("ObjStm");
                dict["N"] = new PdfNum(chunk.Count);
                dict["First"] = new PdfNum(headerBytes.Length);
                dict["Filter"] = new PdfName("FlateDecode");
                var streamRef = Add(new PdfStream(dict, PdfFilters.Deflate(data)));
                for (int i = 0; i < chunk.Count; i++) packed[chunk[i]] = (streamRef.Num, i);
            }
            Array.Resize(ref offsets, objects.Count + 2);
        }

        for (int n = 1; n <= objects.Count; n++)
        {
            if (packed.ContainsKey(n)) continue;
            var obj = objects[n - 1];
            if (obj == null) continue;
            offsets[n] = s.Position;
            PdfSerializer.Ascii(s, $"{n} 0 obj\n");
            PdfSerializer.Write(s, obj);
            PdfSerializer.Ascii(s, "\nendobj\n");
        }

        if (compact)
        {
            int xrefNum = objects.Count + 1;
            long xrefOffset = s.Position;
            offsets[xrefNum] = xrefOffset;
            int width = xrefOffset > 0xFFFFFF ? 4 : 3;
            var rows = new MemoryStream();
            for (int n = 0; n <= xrefNum; n++)
            {
                if (n == 0) { WriteRow(rows, 0, 0, 0xFFFF, width); continue; }
                if (packed.TryGetValue(n, out var p)) WriteRow(rows, 2, p.Stream, p.Index, width);
                else if (n == xrefNum || objects[n - 1] != null) WriteRow(rows, 1, offsets[n], 0, width);
                else WriteRow(rows, 0, 0, 0, width);
            }
            var dict = new PdfDict();
            dict["Type"] = new PdfName("XRef");
            dict["Size"] = new PdfNum(xrefNum + 1);
            dict["W"] = new PdfArray(1, width, 2);
            dict["Root"] = root;
            if (info != null) dict["Info"] = info;
            dict["ID"] = ids;
            dict["Filter"] = new PdfName("FlateDecode");
            PdfSerializer.Ascii(s, $"{xrefNum} 0 obj\n");
            PdfSerializer.Write(s, new PdfStream(dict, PdfFilters.Deflate(rows.ToArray())));
            PdfSerializer.Ascii(s, "\nendobj\n");
            PdfSerializer.Ascii(s, $"startxref\n{xrefOffset}\n%%EOF\n");
            return;
        }

        long xref = s.Position;
        var table = new StringBuilder();
        table.Append("xref\n0 ").Append(count + 1).Append('\n');
        table.Append("0000000000 65535 f\r\n");
        for (int n = 1; n <= count; n++)
            table.Append(objects[n - 1] == null ? "0000000000 00000 f\r\n" : $"{offsets[n]:D10} 00000 n\r\n");
        PdfSerializer.Ascii(s, table.ToString());
        var trailer = new PdfDict();
        trailer["Size"] = new PdfNum(count + 1);
        trailer["Root"] = root;
        if (info != null) trailer["Info"] = info;
        trailer["ID"] = ids;
        PdfSerializer.Ascii(s, "trailer\n");
        PdfSerializer.Write(s, trailer);
        PdfSerializer.Ascii(s, $"\nstartxref\n{xref}\n%%EOF\n");
    }

    static void WriteRow(Stream s, int type, long f2, int f3, int width)
    {
        s.WriteByte((byte)type);
        for (int b = width - 1; b >= 0; b--) s.WriteByte((byte)(f2 >> (8 * b)));
        s.WriteByte((byte)(f3 >> 8));
        s.WriteByte((byte)f3);
    }

    sealed class CountingStream(Stream inner) : Stream
    {
        long position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => position;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); position += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); position += buffer.Length; }
        public override void WriteByte(byte value) { inner.WriteByte(value); position++; }
    }

    /// Writes to a temporary file next to `path`, then moves it into place, so a failure never leaves half a file.
    public static void Save(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temp = Path.Combine(dir, ".swivel-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                write(stream);
            File.Move(temp, path, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw new ToolError(L("Couldn't write %@", Path.GetFileName(path)));
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }
}

/// Deep-copies objects from a source file into a PdfOutput, renumbering references. Every object reachable from what
/// is copied comes along exactly once; nothing unreachable does (so rewriting a file also drops unused objects).
sealed class PdfCopier(PdfFile source, PdfOutput output)
{
    readonly Dictionary<int, PdfRef> map = [];
    readonly Queue<(int Source, PdfRef Target)> pending = new();

    /// Source object numbers that must not be followed (pages that aren't part of the output, the old page tree):
    /// references to them become null.
    public HashSet<int> Excluded { get; } = [];

    /// Changes an object (by source number) before it's copied; return the object to copy instead.
    public Func<int, PdfObj, PdfObj>? Transform { get; init; }

    /// Maps a source object number to an object that is already in the output.
    public void MapTo(int sourceNum, PdfRef target) => map[sourceNum] = target;

    /// Copies a value (direct or indirect). Indirect objects are queued and copied by `Finish`.
    public PdfObj Copy(PdfObj? o)
    {
        switch (o)
        {
            case null: return PdfObj.Null;
            case PdfRef r:
                if (map.TryGetValue(r.Num, out var mapped)) return mapped;
                if (Excluded.Contains(r.Num)) return PdfObj.Null;
                var resolved = source.Get(r.Num);
                if (resolved is null or PdfNull) return PdfObj.Null;
                var target = output.Reserve();
                map[r.Num] = target;
                pending.Enqueue((r.Num, target));
                return target;
            case PdfArray a:
                return new PdfArray(a.Items.Select(Copy));
            case PdfDict d:
            {
                var copy = new PdfDict();
                foreach (var (k, v) in d.Items) copy.Items[k] = Copy(v);
                return copy;
            }
            case PdfStream s:
            {
                var dict = (PdfDict)Copy(s.Dict);
                return new PdfStream(dict, s.Data);
            }
            case PdfStr str:
                return new PdfStr(str.Bytes, str.Hex);
            default:
                return o;
        }
    }

    /// Copies everything still queued.
    public void Finish()
    {
        while (pending.TryDequeue(out var item))
        {
            var obj = source.Get(item.Source) ?? PdfObj.Null;
            if (Transform != null) obj = Transform(item.Source, obj);
            output.Set(item.Target, Copy(obj));
        }
    }

    /// Copies one page into a new page tree: the inherited attributes are written on the page itself.
    public PdfRef CopyPage(PdfPage page, PdfRef parent, Action<PdfDict>? change = null)
    {
        var dict = page.Dict.Clone();
        dict.Remove("Parent");
        // Logical structure and article threads belong to the old document.
        dict.Remove("StructParents");
        dict.Remove("B");
        dict.Remove("PieceInfo");
        if (page.Resources != null) dict["Resources"] = page.Resources;
        else if (!dict.Has("Resources")) dict["Resources"] = new PdfDict();
        if (page.MediaBox != null) dict["MediaBox"] = page.MediaBox;
        else dict["MediaBox"] = new PdfArray(0, 0, 612, 792);
        if (page.CropBox != null) dict["CropBox"] = page.CropBox;
        if (page.Rotate != 0) dict["Rotate"] = new PdfNum(page.Rotate);
        else dict.Remove("Rotate");
        change?.Invoke(dict);
        var target = map.TryGetValue(page.Ref.Num, out var reserved) ? reserved : output.Reserve();
        map[page.Ref.Num] = target;
        var copy = (PdfDict)Copy(dict);
        copy["Parent"] = parent;
        output.Set(target, copy);
        return target;
    }
}

/// Whole-file operations built on the reader, copier and writer.
static class PdfAssembler
{
    /// A new file with the given pages (from one or more source files) in order, like the Mac app's
    /// PDFDocument.insert(page). Annotations come along; links to pages that aren't included are dropped.
    public static void Pages(IReadOnlyList<(PdfFile File, IReadOnlyList<PdfPage> Pages)> parts, Stream stream, Action<PdfDict>? change = null)
    {
        var output = new PdfOutput();
        var catalogRef = output.Reserve();
        var pagesRef = output.Reserve();
        var kids = new PdfArray();
        foreach (var (file, pages) in parts)
        {
            var copier = new PdfCopier(file, output);
            // Every page-tree node of the source is off limits unless it's one of the pages being copied.
            foreach (var p in file.Pages) copier.Excluded.Add(p.Ref.Num);
            foreach (var num in PageTreeNodes(file)) copier.Excluded.Add(num);
            var targets = new List<PdfRef>();
            foreach (var page in pages)
            {
                var target = output.Reserve();
                copier.MapTo(page.Ref.Num, target);
                targets.Add(target);
            }
            for (int i = 0; i < pages.Count; i++)
            {
                copier.CopyPage(pages[i], pagesRef, change);
                kids.Add(targets[i]);
            }
            copier.Finish();
        }
        var pagesDict = new PdfDict();
        pagesDict["Type"] = new PdfName("Pages");
        pagesDict["Kids"] = kids;
        pagesDict["Count"] = new PdfNum(kids.Count);
        output.Set(pagesRef, pagesDict);
        var catalog = new PdfDict();
        catalog["Type"] = new PdfName("Catalog");
        catalog["Pages"] = pagesRef;
        output.Set(catalogRef, catalog);
        var info = new PdfDict();
        info["Producer"] = PdfStr.FromText("Swivel");
        info["CreationDate"] = PdfStr.FromText(PdfDate(DateTime.Now));
        output.Write(stream, catalogRef, output.Add(info));
    }

    static IEnumerable<int> PageTreeNodes(PdfFile file)
    {
        var result = new HashSet<int>();
        void Walk(PdfObj? node, int depth)
        {
            if (depth > 64 || node is not PdfRef r || !result.Add(r.Num)) return;
            if (file.Resolve(r) is PdfDict d && file.Resolve(d["Kids"]) is PdfArray kids)
                foreach (var k in kids.Items) Walk(k, depth + 1);
        }
        Walk(file.Catalog["Pages"], 0);
        return result;
    }

    /// Rewrites the whole file — outline, forms, metadata and all — changing pages and streams on the way. Only
    /// objects reachable from the catalog and document info are written, so unused objects are dropped.
    public static void Rewrite(PdfFile file, Stream stream, Action<PdfPage, PdfDict>? changePage = null,
                               Func<int, PdfStream, PdfStream>? changeStream = null, bool compact = false)
    {
        var output = new PdfOutput();
        var pagesByNum = file.Pages.GroupBy(p => p.Ref.Num).ToDictionary(g => g.Key, g => g.First());
        var copier = new PdfCopier(file, output)
        {
            Transform = (num, obj) =>
            {
                if (obj is PdfDict d && pagesByNum.TryGetValue(num, out var page))
                {
                    // Write the inherited attributes on each page, so changes to one page never affect another.
                    var copy = d.Clone();
                    if (page.Resources != null) copy["Resources"] = page.Resources;
                    if (page.MediaBox != null) copy["MediaBox"] = page.MediaBox;
                    if (page.CropBox != null) copy["CropBox"] = page.CropBox;
                    if (page.Rotate != 0) copy["Rotate"] = new PdfNum(page.Rotate);
                    else copy.Remove("Rotate");
                    changePage?.Invoke(page, copy);
                    return copy;
                }
                if (obj is PdfStream s && changeStream != null) return changeStream(num, s);
                return obj;
            },
        };
        var catalog = file.Catalog.Clone();
        // Pages tree nodes keep their inherited attributes; pages now carry their own.
        var root = output.Reserve();
        output.Set(root, (PdfDict)copier.Copy(catalog));
        PdfRef? info = null;
        if (file.Resolve(file.Trailer["Info"]) is PdfDict infoDict)
        {
            var copy = infoDict.Clone();
            copy["Producer"] = PdfStr.FromText("Swivel");
            info = output.Add(copier.Copy(copy));
        }
        copier.Finish();
        output.Write(stream, root, info, compact);
    }

    public static string PdfDate(DateTime t)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(t);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"D:{t:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
    }
}
