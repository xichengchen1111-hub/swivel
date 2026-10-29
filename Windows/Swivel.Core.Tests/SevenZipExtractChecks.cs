using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class SevenZipExtractChecks
{
    // The same deterministic contents as Fixtures/7z/make7z.py.
    internal static byte[] Lcg(int n, long seed = 12345)
    {
        var b = new byte[n]; long x = seed;
        for (int i = 0; i < n; i++) { x = (x * 1103515245 + 12345) & 0x7FFFFFFF; b[i] = (byte)((x >> 16) & 0xFF); }
        return b;
    }
    internal static byte[] Pattern(int n) => Enumerable.Range(0, n).Select(i => (byte)((i * 7 + (i >> 8)) & 0xFF)).ToArray();
    internal static byte[] Lines(int n) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, n).Select(i => $"line {i:000000} swivel\n")));
    internal static byte[] X86Like(int n, long seed = 777)
    {
        long x = seed; long Next() { x = (x * 1103515245 + 12345) & 0x7FFFFFFF; return x; }
        var b = new List<byte>();
        while (b.Count < n)
        {
            long r = Next();
            if (r % 5 == 0)
            {
                long v = Next();
                b.AddRange(new[] { (byte)(0xE8 | ((v >> 3) & 1)), (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0x0F), (byte)(((v >> 20) & 1) != 0 ? 0xFF : 0) });
            }
            else b.Add((byte)((r >> 16) % 24 + 0x40));
        }
        return b.Take(n).ToArray();
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "7z", name);

    internal static Dictionary<string, byte[]> ExtractAll(byte[] archive, CancellationToken token = default)
    {
        using var stream = new MemoryStream(archive, false);
        var reviewed = SevenZipExtract.Read(stream, token);
        using var source = SevenZipExtract.OpenSource(stream, reviewed);
        var result = new Dictionary<string, byte[]>();
        foreach (var member in reviewed.Plan.Files)
        {
            var output = new MemoryStream(); source.Extract(member, output, token); result[member.Path] = output.ToArray();
        }
        return result;
    }

    // ---- A small independent 7z writer for hostile cases: plain header, one Copy folder. ----
    internal static byte[] Num(ulong v)
    {
        for (int n = 0; n < 8; n++)
            if (v < 1UL << (7 * (n + 1)))
            {
                var bytes = new byte[n + 1];
                bytes[0] = (byte)(((0xFF << (8 - n)) & 0xFF) | (int)(v >> (8 * n)));
                for (int i = 0; i < n; i++) bytes[1 + i] = (byte)(v >> (8 * i));
                return bytes;
            }
        return new byte[] { 0xFF }.Concat(BitConverter.GetBytes(v)).ToArray();
    }
    private static byte[] Bits(IList<bool> flags)
    {
        var b = new byte[(flags.Count + 7) / 8];
        for (int i = 0; i < flags.Count; i++) if (flags[i]) b[i / 8] |= (byte)(0x80 >> (i % 8));
        return b;
    }
    internal static byte[] Wrap(byte[] packed, byte[] header, byte major = 0, byte minor = 4)
    {
        var start = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(start, (ulong)packed.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(8), (ulong)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(16), ZipExtract.Crc32(0xFFFFFFFF, header) ^ 0xFFFFFFFF);
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(crc, ZipExtract.Crc32(0xFFFFFFFF, start) ^ 0xFFFFFFFF);
        return new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, major, minor }.Concat(crc).Concat(start).Concat(packed).Concat(header).ToArray();
    }

    internal sealed record Spec(string Name, byte[]? Data, uint? Attributes = null, bool Anti = false, byte[]? RawName = null);
    internal sealed class Options
    {
        public byte[]? Coder; // full coder record (flags, id, properties) instead of Copy
        public int Coders = 1;
        public long? DeclaredUnpack;
        public bool OmitCrcs;
        public byte[]? ExtraFileProperty;
        public long PackSizeExtra;
    }

    internal static byte[] Build(IList<Spec> files, Options? options = null)
    {
        options ??= new();
        var datas = files.Where(f => f.Data is { Length: > 0 }).Select(f => f.Data!).ToList();
        byte[] packed = datas.SelectMany(d => d).ToArray();
        var h = new List<byte> { 0x01 };
        if (datas.Count > 0)
        {
            h.AddRange(new byte[] { 0x04, 0x06 }); h.AddRange(Num(0)); h.AddRange(Num(1)); h.Add(0x09); h.AddRange(Num((ulong)(packed.Length + options.PackSizeExtra))); h.Add(0x00);
            h.AddRange(new byte[] { 0x07, 0x0B }); h.AddRange(Num(1)); h.Add(0);
            h.AddRange(Num((ulong)options.Coders));
            for (int c = 0; c < options.Coders; c++) h.AddRange(options.Coder ?? new byte[] { 0x01, 0x00 });
            for (int c = 0; c < options.Coders - 1; c++) { h.AddRange(Num((ulong)c + 1)); h.AddRange(Num((ulong)c)); }
            h.Add(0x0C);
            for (int c = 0; c < options.Coders; c++) h.AddRange(Num((ulong)(options.DeclaredUnpack ?? packed.Length)));
            h.Add(0x00);
            h.AddRange(new byte[] { 0x08, 0x0D }); h.AddRange(Num((ulong)datas.Count));
            if (datas.Count > 1) { h.Add(0x09); foreach (var d in datas.Take(datas.Count - 1)) h.AddRange(Num((ulong)d.Length)); }
            if (!options.OmitCrcs)
            {
                h.AddRange(new byte[] { 0x0A, 0x01 });
                foreach (var d in datas) h.AddRange(BitConverter.GetBytes(ZipExtract.Crc32(0xFFFFFFFF, d) ^ 0xFFFFFFFF));
            }
            h.AddRange(new byte[] { 0x00, 0x00 });
        }
        h.Add(0x05); h.AddRange(Num((ulong)files.Count));
        var empty = files.Select(f => f.Data is not { Length: > 0 }).ToList();
        if (empty.Any(e => e))
        {
            var e = Bits(empty); h.Add(0x0E); h.AddRange(Num((ulong)e.Length)); h.AddRange(e);
            var emptyFiles = files.Where((f, i) => empty[i]).Select(f => f.Data is not null).ToList();
            var ef = Bits(emptyFiles); h.Add(0x0F); h.AddRange(Num((ulong)ef.Length)); h.AddRange(ef);
            if (files.Any(f => f.Anti))
            {
                var anti = Bits(files.Where((f, i) => empty[i]).Select(f => f.Anti).ToList()); h.Add(0x10); h.AddRange(Num((ulong)anti.Length)); h.AddRange(anti);
            }
        }
        var names = new List<byte> { 0 };
        foreach (var f in files) { names.AddRange(f.RawName ?? Encoding.Unicode.GetBytes(f.Name)); names.Add(0); names.Add(0); }
        h.Add(0x11); h.AddRange(Num((ulong)names.Count)); h.AddRange(names);
        if (files.Any(f => f.Attributes is not null))
        {
            var attributes = new List<byte> { 0 };
            attributes.AddRange(Bits(files.Select(f => f.Attributes is not null).ToList())); attributes.Add(0);
            foreach (var f in files) if (f.Attributes is uint a) attributes.AddRange(BitConverter.GetBytes(a));
            h.Add(0x15); h.AddRange(Num((ulong)attributes.Count)); h.AddRange(attributes);
        }
        if (options.ExtraFileProperty is not null) h.AddRange(options.ExtraFileProperty);
        h.AddRange(new byte[] { 0x00, 0x00 });
        return Wrap(packed, h.ToArray());
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        void Refuses(byte[] archive, string expected, string label)
        {
            try { ExtractAll(archive); check(false, label + " (accepted)"); }
            catch (UserError error) { check(error.Message == expected, label + ": " + error.Message); }
        }

        // ---- Independent fixtures: libarchive (bsdtar) writer, encoded headers ----
        var tree = new Dictionary<string, byte[]>
        {
            ["a.txt"] = Encoding.ASCII.GetBytes("hello 7z\n"),
            ["子目录\\数据 🦜.bin"] = Pattern(70000),
            ["empty.txt"] = Array.Empty<byte>(),
        };
        var large = new Dictionary<string, byte[]>(tree) { ["large.txt"] = Lines(150000), ["noise.bin"] = Lcg(30000) };
        foreach (var (name, expected) in new[] { ("libarchive-lzma2.7z", large), ("libarchive-lzma1.7z", large), ("libarchive-copy.7z", tree) })
        {
            byte[] bytes = File.ReadAllBytes(Fixture(name));
            var reviewed = SevenZipExtract.Read(new MemoryStream(bytes), default);
            check(reviewed.Plan.Files.Count == expected.Count && reviewed.Plan.Directories.SequenceEqual(new[] { "子目录", "空文件夹" }.OrderBy(d => d, StringComparer.Ordinal))
                && reviewed.Plan.TotalBytes == expected.Values.Sum(v => (long)v.Length), "7z plan from " + name + ": " + string.Join("|", reviewed.Plan.Directories));
            check(reviewed.Plan.Files.All(f => f.Modified == DateTime.UnixEpoch.AddSeconds(1600000000)), "7z modified times from " + name);
            var got = ExtractAll(bytes);
            check(got.Count == expected.Count && expected.All(pair => got.TryGetValue(pair.Key, out var data) && data.SequenceEqual(pair.Value)), "7z exact contents from " + name);
        }
        foreach (string method in new[] { "deflate", "bzip2", "ppmd" })
            reject(() => SevenZipExtract.Read(new MemoryStream(File.ReadAllBytes(Fixture("libarchive-" + method + ".7z"))), default), "7z refuses " + method);
        try { SevenZipExtract.Read(new MemoryStream(File.ReadAllBytes(Fixture("libarchive-deflate.7z"))), default); }
        catch (UserError error) { check(error.Message == "This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.", "7z names the unsupported method clearly: " + error.Message); }
        Refuses(File.ReadAllBytes(Fixture("libarchive-symlink.7z")), "7z archives containing links are not extracted.", "7z refuses a POSIX symlink (libarchive)");

        // ---- Hand-assembled from liblzma raw streams: BCJ x86 in both coder orders, LZMA lc/lp/pb ----
        byte[] code1 = X86Like(120000), code2 = X86Like(5000, 99);
        foreach (string name in new[] { "handmade-bcj.7z", "handmade-bcj-filter-first.7z" })
        {
            var got = ExtractAll(File.ReadAllBytes(Fixture(name)));
            check(got.Count == 2 && got["code\\one.exe"].SequenceEqual(code1) && got["code\\two.dll"].SequenceEqual(code2), "7z BCJ x86 + LZMA2, two files in one folder: " + name);
        }
        {
            var got = ExtractAll(File.ReadAllBytes(Fixture("handmade-lzma.7z")));
            var cases = new[] { (3, 0, 2), (0, 2, 0), (1, 3, 4), (4, 0, 0), (0, 0, 0) };
            bool all = true;
            for (int i = 0; i < cases.Length; i++)
            {
                var (lc, lp, pb) = cases[i];
                byte[] data = Lines(3000 + i * 500).Concat(Lcg(2000, i + 1)).Concat(Pattern(20000 + i)).ToArray();
                all &= got.TryGetValue($"lzma {lc}{lp}{pb}.dat", out var actual) && actual.SequenceEqual(data);
            }
            check(all && got["folder\\zero.txt"].Length == 0, "7z LZMA with every lc/lp/pb shape, small dictionaries and end markers");
        }

        {
            // An LZMA-encoded header whose range coder cannot even start is a user-facing refusal.
            byte[] bad = File.ReadAllBytes(Fixture("libarchive-lzma1.7z"));
            ulong nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(bad.AsSpan(12)), nextSize = BinaryPrimitives.ReadUInt64LittleEndian(bad.AsSpan(20));
            byte[] header = bad[(32 + (int)nextOffset)..(32 + (int)nextOffset + (int)nextSize)];
            check(header[0] == 0x17 && header[1] == 0x06, "7z LZMA header fixture has an encoded header");
            long headerPack = 32 + (long)new HeaderPosition(header).PackPosition;
            bad[headerPack] = 0x5A;
            Refuses(bad, "This 7z archive is damaged and was not extracted.", "7z refuses an encoded header whose LZMA stream cannot start");
        }

        // ---- LZMA2 chunk rules, stored chunks only ----
        static byte[] Lzma2(byte[] stream, long size)
        {
            using var s = new Lzma2Stream(new MemoryStream(stream), new byte[] { 0 }, size, SevenZipExtract.MaxWindowBytes, default);
            var output = new MemoryStream(); s.CopyTo(output); return output.ToArray();
        }
        byte[] good = { 0x01, 0x00, 0x04, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x02, 0x00, 0x00, (byte)'!', 0x00 };
        check(Encoding.ASCII.GetString(Lzma2(good, 6)) == "hello!", "LZMA2 stored chunks with and without dictionary reset");
        reject(() => Lzma2(new byte[] { 0x02, 0x00, 0x00, (byte)'x', 0x00 }, 1), "LZMA2 refuses a first chunk without dictionary reset");
        reject(() => Lzma2(good[..^1], 6), "LZMA2 refuses a missing end byte");
        reject(() => Lzma2(good.Concat(new byte[] { 0 }).ToArray()[..12].Concat(new byte[] { 0x03 }).ToArray(), 6), "LZMA2 refuses an unknown control byte at the end");
        reject(() => Lzma2(good, 7), "LZMA2 refuses a stream shorter than declared");
        reject(() => Lzma2(good, 5), "LZMA2 refuses a stream longer than declared");
        reject(() => Lzma2(new byte[] { 0x01, 0x00, 0x00, (byte)'x', 0x80, 0x00, 0x00, 0x00, 0x04, 0, 0, 0, 0, 0, 0x00 }, 2), "LZMA2 refuses an LZMA chunk without properties after a reset");
        reject(() => new Lzma2Stream(new MemoryStream(), new byte[] { 41 }, 1, SevenZipExtract.MaxWindowBytes, default), "LZMA2 refuses a dictionary property above 40");

        // ---- Hostile names, links, limits and methods (independent writer above) ----
        byte[] one = Encoding.ASCII.GetBytes("data");
        var copy = ExtractAll(Build(new[] { new Spec("dir\\back.txt", one), new Spec("./dot.txt", Encoding.ASCII.GetBytes("x")), new Spec("folder", null, 0x10) }));
        check(copy.Count == 2 && copy["dir\\back.txt"].SequenceEqual(one) && copy["dot.txt"].Length == 1, "7z backslash separators and a leading ./ map into the new folder");
        foreach (string name in new[] { "../x", "/abs", "a/../b", "a\\..\\b", "C:x", "CON", "a//b", "a ", "NAME~1.TXT", "" })
            Refuses(Build(new[] { new Spec(name, one) }), name switch
            {
                "C:x" or "CON" or "a " => "This archive contains names Windows cannot use safely and was not extracted.",
                "NAME~1.TXT" => "This archive contains names in the Windows short-name form (such as NAME~1) and was not extracted.",
                _ => "This archive contains unsafe paths and was not extracted.",
            }, "7z refuses name " + name);
        Refuses(Build(new[] { new Spec("A.txt", one), new Spec("a.txt", one) }), "This archive contains duplicate names (letter case is ignored on Windows) and was not extracted.", "7z refuses case duplicates");
        Refuses(Build(new[] { new Spec("é.txt", one), new Spec("é.txt", one) }), "This archive contains names that look identical and was not extracted.", "7z refuses NFC/NFD duplicates");
        Refuses(Build(new[] { new Spec("x", one), new Spec("x/y", one) }), "This archive uses one name for both a file and a folder and was not extracted.", "7z refuses a file used as a folder");
        Refuses(Build(new[] { new Spec(string.Join("/", Enumerable.Repeat("d", 33)), one) }), "This archive nests folders more than 32 levels deep.", "7z refuses 33 levels");
        Refuses(Build(Enumerable.Range(0, 10001).Select(i => new Spec("f" + i, Array.Empty<byte>())).ToList()), "Archive extraction is limited to 10,000 entries.", "7z refuses 10,001 entries");
        check(ExtractAll(Build(Enumerable.Range(0, 10000).Select(i => new Spec("f" + i, Array.Empty<byte>())).ToList())).Count == 10000, "7z accepts exactly 10,000 entries");
        Refuses(Build(new[] { new Spec("bad", new byte[] { 0x3D, 0xD8 }, RawName: new byte[] { 0x3D, 0xD8 }) }), "This 7z archive contains invalid Unicode names.", "7z refuses a lone surrogate name");
        Refuses(Build(new[] { new Spec("l", one, 0x400) }), "7z archives containing links are not extracted.", "7z refuses a Windows reparse point");
        Refuses(Build(new[] { new Spec("l", one, 0x8000 | 0xA1FFu << 16) }), "7z archives containing links are not extracted.", "7z refuses a POSIX symlink");
        Refuses(Build(new[] { new Spec("p", null, 0x8000 | 0x11B6u << 16) }), "This 7z archive contains special files and was not extracted.", "7z refuses a FIFO");
        Refuses(Build(new[] { new Spec("f", one, 0x10) }), "This 7z archive is damaged and was not extracted.", "7z refuses a file with data marked as a folder");
        Refuses(Build(new[] { new Spec("f", one), new Spec("gone", Array.Empty<byte>(), Anti: true) }), "This 7z archive contains deletion markers (anti-items) and was not extracted.", "7z refuses anti-items");
        Refuses(Build(new[] { new Spec("f", one) }, new() { OmitCrcs = true }), "This 7z archive has files without checksums and was not extracted.", "7z refuses files without CRCs");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x24, 0x06, 0xF1, 0x07, 0x01, 0x01, 0x00 } }), "Encrypted 7z archives are not supported.", "7z refuses AES");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x03, 0x03, 0x04, 0x01 } }), "This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.", "7z refuses PPMd");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x14, 0x03, 0x03, 0x01, 0x1B, 0x04, 0x01 } }), "This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.", "7z refuses BCJ2 (several inputs)");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coders = 3 }), "This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.", "7z refuses longer coder chains");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x23, 0x03, 0x01, 0x01, 0x05, 0x5D, 0x00, 0x00, 0x00, 0x40 }, DeclaredUnpack = 300L << 20 }),
            "This 7z archive needs more than 256 MB of decoder memory and was not extracted.", "7z refuses a 1 GB dictionary for 300 MB of output before extraction");
        check(SevenZipExtract.Read(new MemoryStream(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x23, 0x03, 0x01, 0x01, 0x05, 0x5D, 0x00, 0x00, 0x00, 0x40 }, DeclaredUnpack = 4 })), default).Plan.Files.Count == 1,
            "7z bounds the window by the output size, so a large dictionary for a small file is fine");
        Refuses(Build(new[] { new Spec("f", one) }, new() { Coder = new byte[] { 0x21, 0x21, 0x01, 0x28 }, DeclaredUnpack = 3L << 30 }), "Archive extraction is limited to 2 GB of extracted data.", "7z refuses 3 GB declared output");
        Refuses(Build(new[] { new Spec("f", one) }, new() { PackSizeExtra = 100 }), "This 7z archive is damaged and was not extracted.", "7z refuses packed data reaching into the header");
        Refuses(Build(new[] { new Spec("f", one) }, new() { ExtraFileProperty = new byte[] { 0x30, 0x01, 0x00 } }), "This 7z archive uses features this reader does not support and was not extracted.", "7z refuses unknown file properties");
        check(ExtractAll(Build(new[] { new Spec("f", one) }, new() { ExtraFileProperty = new byte[] { 0x19, 0x02, 0x00, 0x00 } })).Count == 1, "7z skips padding (kDummy)");

        // ---- Signature header, CRCs and file boundaries ----
        byte[] plain = Build(new[] { new Spec("a.txt", one), new Spec("b.txt", Encoding.ASCII.GetBytes("second")) });
        Refuses(plain.Take(plain.Length - 3).ToArray(), "This 7z archive is incomplete or split into parts and was not extracted.", "7z refuses a truncated archive");
        Refuses(plain.Concat(new byte[] { 0 }).ToArray(), "This 7z archive has unexpected data after its header and was not extracted.", "7z refuses trailing data");
        { var bad = (byte[])plain.Clone(); bad[^5] ^= 1; Refuses(bad, "This 7z archive is damaged and was not extracted.", "7z checks the header CRC"); }
        { var bad = (byte[])plain.Clone(); bad[13] ^= 1; Refuses(bad, "This 7z archive is damaged and was not extracted.", "7z checks the start header CRC"); }
        { var bad = (byte[])plain.Clone(); bad[32] ^= 1; Refuses(bad, "A 7z entry failed its CRC check and was not extracted.", "7z checks each file's CRC"); }
        Refuses(Wrap(Array.Empty<byte>(), new byte[] { 0x01, 0x00 }, major: 1), "This 7z archive needs a newer format version and was not extracted.", "7z refuses format version 1");
        Refuses(Wrap(Array.Empty<byte>(), Array.Empty<byte>()), "This 7z archive is empty.", "7z reports an empty archive");
        Refuses(Encoding.ASCII.GetBytes("PK\u0003\u0004 not a 7z archive at all, just text."), "This file is not a supported 7z archive.", "7z refuses other files");
        reject(() => SevenZipExtract.Read(new MemoryStream(plain), new CancellationToken(true)), "7z pre-cancel reads nothing");
        {
            using var stream = new MemoryStream(plain);
            var reviewed = SevenZipExtract.Read(stream, default);
            using var source = SevenZipExtract.OpenSource(stream, reviewed);
            bool ordered = false;
            try { source.Extract(reviewed.Plan.Files[1], new MemoryStream(), default); } catch (InvalidOperationException) { ordered = true; }
            check(ordered, "7z members of one folder are only extracted in order");
        }

        // ---- Cancellation inside a long LZMA2 decode ----
        {
            byte[] bytes = File.ReadAllBytes(Fixture("libarchive-lzma2.7z"));
            using var stream = new MemoryStream(bytes);
            var reviewed = SevenZipExtract.Read(stream, default);
            using var cts = new CancellationTokenSource();
            using var source = SevenZipExtract.OpenSource(stream, reviewed);
            bool cancelled = false;
            try
            {
                foreach (var member in reviewed.Plan.Files) source.Extract(member, new CancelAfter(cts, 200000), cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "7z extraction stops on cancellation inside a folder");
        }

        // ---- Fuzzing: damaged data, truncation and mutated headers only ever give user errors ----
        var random = new Random(7);
        int accepted = 0, refused = 0;
        void Fuzz(byte[] archive, string label)
        {
            try { ExtractAll(archive); accepted++; }
            catch (UserError) { refused++; }
            catch (Exception error) { check(false, label + " threw " + error.GetType().Name + ": " + error.Message); }
        }
        foreach (string name in new[] { "libarchive-lzma2.7z", "libarchive-lzma1.7z", "handmade-bcj.7z", "handmade-lzma.7z" })
        {
            byte[] original = File.ReadAllBytes(Fixture(name));
            int packedEnd = 32 + (int)BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(12));
            for (int i = 0; i < 150; i++)
            {
                var bad = (byte[])original.Clone();
                int at = 32 + random.Next(Math.Min(packedEnd - 32, 4096));
                bad[at] ^= (byte)(1 << random.Next(8));
                if (random.Next(3) == 0) bad[32 + random.Next(packedEnd - 32)] = (byte)random.Next(256);
                Fuzz(bad, name + " data mutation");
            }
            for (int i = 0; i < 20; i++) Fuzz(original.Take(random.Next(original.Length)).ToArray(), name + " truncation");
        }
        // Mutated plain headers with valid CRCs reach the parser itself.
        byte[] richHeader = Build(new[] { new Spec("a/b.txt", one), new Spec("c.txt", Encoding.ASCII.GetBytes("more")), new Spec("e", Array.Empty<byte>()), new Spec("d", null, 0x10) });
        byte[] packedPart = richHeader[32..(32 + 8)], headerPart = richHeader[(32 + 8)..];
        for (int i = 0; i < 3000; i++)
        {
            var header = (byte[])headerPart.Clone();
            for (int k = random.Next(1, 4); k > 0; k--) header[random.Next(header.Length)] = random.Next(4) == 0 ? (byte)random.Next(256) : (byte)(header[random.Next(header.Length)] ^ (1 << random.Next(8)));
            Fuzz(Wrap(packedPart, header), "7z header mutation");
        }
        check(refused > 1000, $"7z fuzzing: {refused} refused with user errors, {accepted} accepted, no other exceptions");
    }

    // Reads the pack position from "kEncodedHeader kPackInfo <number>".
    private sealed class HeaderPosition(byte[] header)
    {
        public ulong PackPosition
        {
            get
            {
                byte first = header[2]; ulong value = 0; int mask = 0x80, at = 3;
                for (int i = 0; i < 8; i++, mask >>= 1)
                {
                    if ((first & mask) == 0) return value | (ulong)(first & (mask - 1)) << (8 * i);
                    value |= (ulong)header[at++] << (8 * i);
                }
                return value;
            }
        }
    }

    private sealed class CancelAfter(CancellationTokenSource cts, long limit) : MemoryStream
    {
        private long written;
        public override void Write(byte[] buffer, int offset, int count) { written += count; if (written > limit) cts.Cancel(); }
    }
}
