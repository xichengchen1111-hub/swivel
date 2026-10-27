using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class TarExtractChecks
{
    // A hand-built POSIX ustar header, so every field can be made wrong on purpose.
    private static byte[] Header(string name, byte type = (byte)'0', long size = 0, string magic = "ustar\u000000", string prefix = "", byte[]? rawName = null, bool damageChecksum = false, long mtime = 1700000000)
    {
        var h = new byte[512];
        (rawName ?? Encoding.UTF8.GetBytes(name)).CopyTo(h, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
        Encoding.ASCII.GetBytes(Convert.ToString(mtime, 8).PadLeft(11, '0') + "\0").CopyTo(h, 136);
        h[156] = type;
        Encoding.ASCII.GetBytes(magic).CopyTo(h, 257);
        Encoding.UTF8.GetBytes(prefix).CopyTo(h, 345);
        for (int i = 148; i < 156; i++) h[i] = (byte)' ';
        int sum = h.Sum(b => b) + (damageChecksum ? 1 : 0);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
        return h;
    }
    private static byte[] Data(byte[] data) => data.Concat(new byte[(512 - data.Length % 512) % 512]).ToArray();
    private static byte[] File(string name, byte[] data) => Header(name, size: data.Length).Concat(Data(data)).ToArray();
    private static byte[] Pax(params (string Key, string Value)[] records)
    {
        var body = new MemoryStream();
        foreach (var (key, value) in records)
        {
            string tail = " " + key + "=" + value + "\n"; int length = Encoding.UTF8.GetByteCount(tail);
            int total = length + length.ToString().Length; if ((total).ToString().Length != length.ToString().Length) total++;
            body.Write(Encoding.UTF8.GetBytes(total + tail));
        }
        byte[] bytes = body.ToArray();
        return Header("PaxHeader", (byte)'x', bytes.Length).Concat(Data(bytes)).ToArray();
    }
    private static byte[] Tar(params byte[][] parts) => parts.SelectMany(p => p).Concat(new byte[1024]).ToArray();
    private static byte[] Gzip(byte[] data) { var output = new MemoryStream(); using (var z = new GZipStream(output, CompressionLevel.Optimal, true)) z.Write(data); return output.ToArray(); }
    private static ZipPlan Read(byte[] archive, TarKind kind = TarKind.Tar, string name = "a.tar") => TarExtract.Read(new MemoryStream(archive), kind, name, default);
    private static byte[] Contents(byte[] archive, TarKind kind, ZipMember member, TarSource source)
    {
        var output = new MemoryStream(); source.Extract(member, output, default); return output.ToArray();
    }

    private static byte[] Written(TarEntryFormat format, Action<TarWriter> write)
    {
        var output = new MemoryStream();
        using (var writer = new TarWriter(output, format, leaveOpen: true)) write(writer);
        return output.ToArray();
    }
    private static TarEntry Entry(TarEntryFormat format, TarEntryType type, string name, byte[]? data = null)
    {
        TarEntry entry = format switch
        {
            TarEntryFormat.Pax => new PaxTarEntry(type, name),
            TarEntryFormat.Gnu => new GnuTarEntry(type, name),
            _ => new UstarTarEntry(type, name),
        };
        entry.ModificationTime = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        if (data is not null) entry.DataStream = new MemoryStream(data);
        return entry;
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Archives written by the .NET TarWriter (an independent producer) in all three formats.
        string longName = "deep/" + new string('长', 40) + "/" + new string('n', 120) + ".txt";
        byte[] photo = Enumerable.Range(0, 70000).Select(i => (byte)(i * 7)).ToArray(), text = Encoding.UTF8.GetBytes("你好 🦜\n");
        foreach (var format in new[] { TarEntryFormat.Pax, TarEntryFormat.Gnu, TarEntryFormat.Ustar })
        {
            bool longNames = format != TarEntryFormat.Ustar;
            byte[] tar = Written(format, w =>
            {
                w.WriteEntry(Entry(format, format == TarEntryFormat.Gnu ? TarEntryType.Directory : TarEntryType.Directory, "photos/"));
                w.WriteEntry(Entry(format, format == TarEntryFormat.Ustar ? TarEntryType.RegularFile : TarEntryType.RegularFile, "photos/海 🦜.dat", photo));
                w.WriteEntry(Entry(format, TarEntryType.RegularFile, "empty", Array.Empty<byte>()));
                w.WriteEntry(Entry(format, TarEntryType.Directory, "photos/empty folder/"));
                if (longNames) w.WriteEntry(Entry(format, TarEntryType.RegularFile, longName, text));
                else w.WriteEntry(Entry(format, TarEntryType.RegularFile, new string('p', 120) + "/note.txt", text)); // ustar prefix field
            });
            foreach (var (bytes, kind) in new[] { (tar, TarKind.Tar), (Gzip(tar), TarKind.TarGzip) })
            {
                var plan = Read(bytes, kind, kind == TarKind.Tar ? "x.tar" : "x.tgz");
                string last = longNames ? longName.Replace('/', '\\') : new string('p', 120) + "\\note.txt";
                check(plan.Files.Select(f => f.Path).SequenceEqual(new[] { "photos\\海 🦜.dat", "empty", last }), "TAR plan keeps every file in archive order: " + format + " " + kind);
                check(plan.Directories.Contains("photos") && plan.Directories.Contains("photos\\empty folder") && plan.TotalBytes == photo.Length + text.Length, "TAR plan has explicit, implied and empty folders and exact size: " + format + " " + kind);
                check(plan.Files[0].Modified == new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), "TAR modified time is read as UTC: " + format);
                using var source = TarExtract.OpenSource(new MemoryStream(bytes), kind, default);
                check(Contents(bytes, kind, plan.Files[0], source).SequenceEqual(photo) && Contents(bytes, kind, plan.Files[1], source).Length == 0 && Contents(bytes, kind, plan.Files[2], source).SequenceEqual(text),
                    "TAR extraction returns exact contents in order: " + format + " " + kind);
            }
        }

        // Hand-built archives: "./" roots, the ustar prefix field, pax path/size/mtime and GNU long names.
        var dotted = Read(Tar(Header("./", (byte)'5'), File("./a/b.txt", text)));
        check(dotted.Files.Single().Path == "a\\b.txt" && dotted.Directories.SequenceEqual(new[] { "a" }), "Leading ./ is the archive root and is dropped");
        var prefixed = Read(Tar(Header("n.txt", size: 2, prefix: "pre/fix").Concat(Data(new byte[] { 1, 2 })).ToArray()));
        check(prefixed.Files.Single().Path == "pre\\fix\\n.txt", "POSIX prefix field joins the name");
        var paxed = Read(Tar(Pax(("path", "pax/名.bin"), ("mtime", "1700000000.25"), ("SCHILY.xattr.user.x", "ÿ")), File("ignored", text)));
        check(paxed.Files.Single().Path == "pax\\名.bin" && paxed.Files.Single().Modified == DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, "Pax path and mtime override the header; other keys are ignored");
        byte[] longBody = Encoding.UTF8.GetBytes("gnu/long/name.txt\0");
        var gnu = Read(Tar(Header("././@LongLink", (byte)'L', longBody.Length, "ustar  \0").Concat(Data(longBody)).ToArray(), File("short", text)));
        check(gnu.Files.Single().Path == "gnu\\long\\name.txt", "GNU long name applies to the next entry");

        // Single-file gzip.
        var single = Read(Gzip(photo), TarKind.Gzip, "report.pdf.gz");
        check(single.Files.Single().Path == "report.pdf" && single.Files.Single().Size == photo.Length && TarExtract.FolderStem("report.pdf.gz", TarKind.Gzip) == "report", "Single-file gzip extracts one file named without .gz");
        using (var source = TarExtract.OpenSource(new MemoryStream(Gzip(photo)), TarKind.Gzip, default))
            check(Contents(Gzip(photo), TarKind.Gzip, single.Files[0], source).SequenceEqual(photo), "Single-file gzip contents are exact");
        check(TarExtract.FolderStem("photos.tar.gz", TarKind.TarGzip) == "photos" && TarExtract.FolderStem("a.b.TGZ", TarKind.TarGzip) == "a.b" && TarExtract.FolderStem("x.tar", TarKind.Tar) == "x" && TarExtract.FolderStem(".tar", TarKind.Tar) == "Extracted",
            "Folder names drop .tar/.tar.gz/.tgz");
        check(TarExtract.KindFor("a.TAR.GZ") == TarKind.TarGzip && TarExtract.KindFor("a.tgz") == TarKind.TarGzip && TarExtract.KindFor("a.tar") == TarKind.Tar && TarExtract.KindFor("a.gz") == TarKind.Gzip && TarExtract.KindFor("a.zip") is null, "Archive kinds come from the name");

        // Refused before anything is written.
        byte[] links = Written(TarEntryFormat.Pax, w => { var link = Entry(TarEntryFormat.Pax, TarEntryType.SymbolicLink, "link"); link.LinkName = "../outside"; w.WriteEntry(link); });
        reject(() => Read(links), "TAR symbolic links refused");
        reject(() => Read(Tar(Header("hard", (byte)'1'))), "TAR hard links refused");
        foreach (byte type in new[] { (byte)'3', (byte)'4', (byte)'6' }) reject(() => Read(Tar(Header("dev", type))), "TAR devices and FIFOs refused: " + (char)type);
        foreach (byte type in new[] { (byte)'S', (byte)'M', (byte)'V', (byte)'Z', (byte)'K' }) reject(() => Read(Tar(Header("odd", type))), "Sparse, multi-volume, label, unknown and long-link entries refused: " + (char)type);
        reject(() => Read(Tar(Pax(("linkpath", "x")), File("a", text))), "Pax link path refused");
        reject(() => Read(Tar(Pax(("GNU.sparse.map", "0,1")), File("a", text))), "Pax sparse map refused");
        reject(() => Read(Tar(Header("g", (byte)'g', 0).Concat(Pax(("path", "x")).Skip(512)).ToArray(), File("a", text))), "Global pax path defaults refused");
        foreach (string unsafeName in new[] { "/etc/passwd", "../x", "a/../b", "a//b", "a\\b", "CON", "a/NUL.txt", "PROGRA~1/x", "name.", "a/./b" })
            reject(() => Read(Tar(File(unsafeName, text))), "Unsafe TAR name refused: " + unsafeName);
        reject(() => Read(Tar(File("A.txt", text), File("a.TXT", text))), "Case-insensitive duplicate refused");
        reject(() => Read(Tar(File("café", text), File("café", text))), "Names identical after NFC refused");
        reject(() => Read(Tar(File("x", text), File("x/y", text))), "One name for a file and a folder refused");
        reject(() => Read(Tar(Header("d", (byte)'5', 3).Concat(Data(new byte[3])).ToArray())), "Folder with data refused");
        reject(() => Read(Tar(File(string.Join("/", Enumerable.Repeat("d", 33)), text))), "More than 32 levels refused");
        reject(() => Read(Tar(Header("bad", rawName: new byte[] { 0x62, 0xFF }))), "Non-UTF-8 names refused");
        reject(() => Read(Tar(Header("a", damageChecksum: true))), "Damaged header checksum refused");
        reject(() => Read(Tar(Header("a", magic: "\0\0\0\0\0\0\0\0"))), "Old V7 headers refused");
        reject(() => Read(File("a", text)), "Archive without an end marker refused");
        reject(() => Read(File("a", text).Take(600).ToArray()), "Truncated data refused");
        reject(() => Read(Tar(File("a", text)).Concat(new byte[] { 0, 1 }).ToArray()), "Non-zero data after the end refused");
        check(Read(Tar(File("a", text)).Concat(new byte[9216]).ToArray()).Files.Count == 1, "Zero record padding after the end accepted");
        reject(() => Read(new byte[1024]), "Empty TAR refused");
        var huge = Header("big", size: 0); var field = new byte[12]; field[0] = 0x80; field[4] = 1; field.CopyTo(huge, 124);
        for (int i = 148; i < 156; i++) huge[i] = (byte)' '; Encoding.ASCII.GetBytes(Convert.ToString(huge.Sum(b => b), 8).PadLeft(6, '0') + "\0 ").CopyTo(huge, 148);
        reject(() => Read(Tar(huge)), "Base-256 size beyond 2 GB refused before reading");

        // Gzip integrity: the stream's own CRC-32 and length close the file.
        byte[] compressed = Gzip(Tar(File("a", text)));
        var badCrc = (byte[])compressed.Clone(); badCrc[^8] ^= 1;
        reject(() => Read(badCrc, TarKind.TarGzip, "a.tgz"), "Gzip CRC mismatch refused");
        reject(() => Read(compressed.Concat(Gzip(Tar(File("b", text)))).ToArray(), TarKind.TarGzip, "a.tgz"), "Multi-part gzip refused");
        reject(() => Read(compressed[..^10], TarKind.TarGzip, "a.tgz"), "Truncated gzip refused");
        reject(() => Read(compressed.Concat(new byte[] { 0 }).ToArray(), TarKind.TarGzip, "a.tgz"), "Data appended after gzip refused");
        reject(() => Read(Tar(File("a", text)), TarKind.TarGzip, "a.tgz"), "A plain TAR named .tgz refused");
        reject(() => Read(Gzip(text), TarKind.Gzip, "CON.gz"), "Unusable single-file gzip name refused");
        reject(() => TarExtract.Read(new MemoryStream(Tar(File("a", text))), TarKind.Tar, "a.tar", new CancellationToken(true)), "Pre-cancelled TAR review refused");

        // Written by macOS tar (bsdtar 3.x, default pax format, an extended attribute and a
        // 110-character folder name): Unicode names, pax paths and ignored LIBARCHIVE.xattr keys.
        byte[] macArchive = Convert.FromBase64String("H4sIAH0W3moAA+2ZPW/TQBjH3UoIEcTAAPN9Avde7XrIEBCilVr1lQomZDUHiXBeSF0a2BFiYmVAAgkBXwCEGCrEwLdgBLow8BE4E5fYlzhOKp+Dm/tJVhL3bJ/7+P/8/dyz7naXuFvlnQXz9tHjd0dPnxiZAyG0GQN/P63ep+D4U8BsgBi2bIoIshmAyBJfDNDNfiqD7O/5bkdM5WDfqz+q1RPHHdQ490acJ35TIPN5KgLZoO36tfJx9EsEgoZfb/Aysh3sEMeizMQoCB0iuDTt6WoyRp3q+6TpH1Mi6Z8ixgwAVU7qmBnXv3Hm8llj3jBW3V2wtgVugpBgn3FObFhs98UW/H493ikr29ub4dfgiOdiOy8Nmevvv7Tbaphuu+1xs91pPeBNt7nLjbl5Y+PbJ/+rB39lcJOaJNb/+b+6PJCqf+E5cf0zGzLt/3mArJ7/96K/kGL/4kVtZflKZfPq0vLONbPr+n7HHCbfcmVjuXKj+pBVV3f2VtcOStQBW+KglVujDopoXr9o5EQYd6XXSH3/h7L+KUTEAEzprEJmXP9h/NcjZeDPDx/B75dvX5h+18/kGuL/YVE6Iv42jccfi186/+eCyPfR/B+PfqIbsEUHE52jTwFD457xNdL0TyCS8r/NqK7/8mGc+u+Z0av/vox3ykj9FxzxXmwXpCHz4f7E+k/wuTfuYvB33nUbwYhmy4/VhiMCphmHAf9XkAdS9W8h2f8JtLX/5wFejPn/BO5P0fBaMCrVsnv9XolYg7VfbFCtrgvLaTEk7plfI/X9HzHZ/wnB2v/zoMY9rwW+H7768eZQa2P2iBb+XRUY46z/Sv1fiqFV9P6v+rZaJiDsSAsASp6CRJJeMsSzsGg7OiOpRn28T9D/pZgVvv9bEP3r/u9s0/d/ZfJP1T9FUNY/RkVf/y+I/pG0AKDoKUggsd8c2r9eFlCN+hCfoP9LsQWL3v8tiP6j9Z/Xat4FTbfBwZ26UEpmS0Fp6z/YwvH4M0IwPhX5P+d8OvntIzrV/D/soUv0BMfGTHeds0Wd6vuk6l+u/xgiDJ6K+u+/17+u/2abvv+rywNp+qdkwP8RpNr/89A/otY0/X9i99cVYbaod/8x+r+Qyf6PsK39f3Imv/0q5+0ZedQ1Go1GE+EPBxcO6ABAAAA=");
        var mac = Read(macArchive, TarKind.TarGzip, "照片.tgz"); string deepFolder = "照片\\" + new string('x', 110);
        check(mac.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { "照片\\海 🦜.txt", deepFolder + "\\long name file.txt" }.OrderBy(p => p, StringComparer.Ordinal)) && mac.Directories.Contains(deepFolder),
            "macOS bsdtar archive with Unicode names, a long path and an extended attribute is planned exactly: " + string.Join(" | ", mac.Files.Select(f => f.Path)) + " dirs " + string.Join(" | ", mac.Directories));
        check(mac.SkippedAppleDouble == 4, "macOS AppleDouble ._ attribute files are recognised by signature and left out: " + mac.SkippedAppleDouble);
        // A decompressor may hand back one byte at a time: the signature is still recognised.
        byte[] appleBody = new byte[] { 0, 5, 0x16, 7 }.Concat(new byte[40]).ToArray();
        var trickled = TarExtract.Read(new Trickle(Tar(File("._x", appleBody), File("x", text))), TarKind.Tar, "a.tar", default);
        check(trickled.SkippedAppleDouble == 1 && trickled.Files.Single().Path == "x", "AppleDouble signature read across one-byte reads");
        // A pax size belongs to the entry, not to a GNU long-name header between them.
        byte[] paxBody = new byte[600]; paxBody[599] = 9; byte[] longBytes = Encoding.UTF8.GetBytes("long/name.txt\0");
        var paxThenLong = Read(Tar(Pax(("size", "600")), Header("././@LongLink", (byte)'L', longBytes.Length, "ustar  \0").Concat(Data(longBytes)).ToArray(),
            Header("short", size: 0, magic: "ustar  \0").Concat(Data(paxBody)).ToArray()));
        check(paxThenLong.Files.Single().Path == "long\\name.txt" && paxThenLong.Files.Single().Size == 600, "Pax size applies past a GNU long-name header");
        // A pre-1970 (negative base-256) time is dropped, not refused.
        var old = Header("old.txt", size: 2); var time = new byte[12]; Array.Fill(time, (byte)0xFF); time.CopyTo(old, 136);
        for (int i = 148; i < 156; i++) old[i] = (byte)' '; Encoding.ASCII.GetBytes(Convert.ToString(old.Sum(b => b), 8).PadLeft(6, '0') + "\0 ").CopyTo(old, 148);
        var oldPlan = Read(Tar(old.Concat(Data(new byte[] { 1, 2 })).ToArray()));
        check(oldPlan.Files.Single().Modified is null, "Negative base-256 times are kept as no time");
        var notApple = Read(Tar(File("._real", Enumerable.Repeat((byte)7, 40).ToArray())));
        check(notApple.Files.Single().Path == "._real" && notApple.SkippedAppleDouble == 0, "A ._ file without the AppleDouble signature is extracted normally");
        using (var source = TarExtract.OpenSource(new MemoryStream(macArchive), TarKind.TarGzip, default))
            check(mac.Files.Select(f => Encoding.UTF8.GetString(Contents(macArchive, TarKind.TarGzip, f, source))).OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(new[] { "deep\n", "hello 你好\n" }), "macOS bsdtar contents are exact");

        // A member whose bytes change between review and extraction is refused.
        byte[] original = Tar(File("a", text)); var reviewed = Read(original);
        var changed = (byte[])original.Clone(); changed[512] ^= 1;
        using (var source = TarExtract.OpenSource(new MemoryStream(changed), TarKind.Tar, default))
            reject(() => source.Extract(reviewed.Files[0], new MemoryStream(), default), "Changed TAR content after review refused by CRC-32");
    }
}

internal sealed class Trickle : MemoryStream
{
    public Trickle(byte[] data) : base(data) { }
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
}
