using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class ZipExtractChecks
{
    // A hand-built ZIP so every header field can be made wrong on purpose.
    private sealed record Entry(string Name, byte[] Data, ushort Method = 0, ushort Flags = 0x0800, ushort MadeBy = 20,
        uint External = 0, uint? Crc = null, uint? Size = null, byte[]? Extra = null, uint? Offset = null, ushort Needed = 20, byte[]? RawName = null);

    private static byte[] Build(IReadOnlyList<Entry> entries, byte[]? between = null, byte[]? after = null, ushort? count = null)
    {
        var output = new MemoryStream(); var central = new MemoryStream(); var w = new BinaryWriter(output); var c = new BinaryWriter(central);
        foreach (var entry in entries)
        {
            byte[] stored = entry.Method == 8 ? Deflate(entry.Data) : entry.Data;
            byte[] name = entry.RawName ?? Encoding.UTF8.GetBytes(entry.Name);
            uint crc = entry.Crc ?? ZipExtract.Crc32(0xFFFFFFFF, entry.Data) ^ 0xFFFFFFFF, size = entry.Size ?? (uint)entry.Data.Length;
            uint offset = (uint)output.Position; byte[] extra = entry.Extra ?? Array.Empty<byte>();
            w.Write(0x04034b50u); w.Write(entry.Needed); w.Write(entry.Flags); w.Write(entry.Method); w.Write((ushort)0x6000); w.Write((ushort)0x5A21);
            w.Write(crc); w.Write((uint)stored.Length); w.Write(size); w.Write((ushort)name.Length); w.Write((ushort)extra.Length); w.Write(name); w.Write(extra); w.Write(stored);
            if ((entry.Flags & 8) != 0) { w.Write(0x08074b50u); w.Write(crc); w.Write((uint)stored.Length); w.Write(size); }
            c.Write(0x02014b50u); c.Write(entry.MadeBy); c.Write(entry.Needed); c.Write(entry.Flags); c.Write(entry.Method); c.Write((ushort)0x6000); c.Write((ushort)0x5A21);
            c.Write(crc); c.Write((uint)stored.Length); c.Write(size); c.Write((ushort)name.Length); c.Write((ushort)extra.Length); c.Write((ushort)0);
            c.Write((ushort)0); c.Write((ushort)0); c.Write(entry.External); c.Write(entry.Offset ?? offset); c.Write(name); c.Write(extra);
        }
        uint cdOffset = (uint)output.Position; w.Write(central.ToArray()); if (between is not null) w.Write(between);
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write(count ?? (ushort)entries.Count); w.Write(count ?? (ushort)entries.Count);
        w.Write((uint)central.Length); w.Write(cdOffset); w.Write((ushort)0); if (after is not null) w.Write(after);
        return output.ToArray();
    }
    private static byte[] Deflate(byte[] data) { var m = new MemoryStream(); using (var d = new DeflateStream(m, CompressionLevel.Optimal, true)) d.Write(data); return m.ToArray(); }
    private static ZipPlan Plan(byte[] zip) => ZipExtract.Read(new MemoryStream(zip), CancellationToken.None);
    private static byte[] Contents(byte[] zip, ZipMember member)
    {
        var output = new MemoryStream(); ZipExtract.Extract(new MemoryStream(zip), member, output, CancellationToken.None); return output.ToArray();
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check((ZipExtract.Crc32(0xFFFFFFFF, Encoding.ASCII.GetBytes("123456789")) ^ 0xFFFFFFFF) == 0xCBF43926, "ZIP CRC-32 standard check value");
        byte[] big = Enumerable.Range(0, 300000).Select(i => (byte)(i * 7 % 251)).ToArray();

        // Real archive from the BCL writer: nested folders, Unicode, empty files and folders.
        var system = new MemoryStream();
        using (var zip = new ZipArchive(system, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            zip.CreateEntry("照片/假期 🦜/").ToString();
            using (var s = zip.CreateEntry("照片/假期 🦜/海.dat", CompressionLevel.Optimal).Open()) s.Write(big);
            using (var s = zip.CreateEntry("readme.txt", CompressionLevel.NoCompression).Open()) s.Write(Encoding.UTF8.GetBytes("hello"));
            zip.CreateEntry("empty.txt").Open().Dispose();
            zip.CreateEntry("docs/a/b/").ToString();
        }
        byte[] systemZip = system.ToArray(); var plan = Plan(systemZip);
        check(plan.Files.Count == 3 && plan.TotalBytes == big.Length + 5, "ZIP plan counts files and extracted bytes");
        check(plan.Directories.SequenceEqual(new[] { "docs", "照片", "docs\\a", "照片\\假期 🦜", "docs\\a\\b" }), "ZIP folders listed parents first, implied and explicit");
        check(Contents(systemZip, plan.Files.Single(f => f.Path == "照片\\假期 🦜\\海.dat")).SequenceEqual(big), "ZIP Deflate member exact contents");
        check(Encoding.UTF8.GetString(Contents(systemZip, plan.Files.Single(f => f.Path == "readme.txt"))) == "hello", "ZIP Store member exact contents");
        check(Contents(systemZip, plan.Files.Single(f => f.Path == "empty.txt")).Length == 0, "ZIP empty member");
        check(plan.Files.All(f => f.Modified is not null), "ZIP modified times are read");

        // The app's own archives extract.
        var own = new MemoryStream();
        using (var source = new MemoryStream(big)) Archives.Create(new[] { new ArchiveSource("data.bin", source) }, ArchiveFormat.Zip, own, CancellationToken.None);
        var ownPlan = Plan(own.ToArray());
        check(ownPlan.Files.Single().Path == "data.bin" && Contents(own.ToArray(), ownPlan.Files[0]).SequenceEqual(big), "Swivel ZIP output extracts");

        byte[] ok = Build(new[] { new Entry("a/b.txt", big, 8), new Entry("c.txt", Encoding.ASCII.GetBytes("x")) });
        check(Plan(ok).Files.Count == 2 && Plan(ok).LongestPath == 7, "Hand-built ZIP accepted");
        check(Plan(Build(new[] { new Entry("x.txt", big, 8, Flags: 0x0808) })).Files.Count == 1, "Data descriptor flag uses directory sizes");
        check(Plan(Build(new[] { new Entry("ascii.txt", big, Flags: 0) })).Files.Count == 1, "ASCII names without UTF-8 flag");
        check(Plan(Build(new[] { new Entry("u.txt", big, MadeBy: 0x031E, External: 0x81A40000) })).Files.Count == 1, "Unix regular file mode accepted");
        check(Plan(Build(new[] { new Entry("d/", Array.Empty<byte>(), MadeBy: 0x031E, External: 0x41ED0010) })).Directories.Single() == "d", "Unix folder mode accepted");
        check(Plan(Build(new[] { new Entry("c.txt", big) }, after: null)).Files[0].Size == big.Length, "Store size");
        check(Plan(Build(new[] { new Entry("t.txt", big, Extra: new byte[] { 0x55, 0x54, 5, 0, 1, 0, 0, 0, 0 }) })).Files.Count == 1, "Ordinary extra fields ignored");

        foreach (string name in new[] { "../evil.txt", "a/../../evil.txt", "/abs.txt", "a\\b.txt", "C:/x.txt", "C:x.txt", "CON.txt", "a/nul", "bad.", "bad /x.txt", "a//b.txt", "./x.txt",
            "x\u202e.txt", "x\u0001.txt", "x:ads.txt", "a*b.txt", "q?.txt", "x|y.txt", "<a>.txt", "\"a\".txt", string.Join("/", Enumerable.Repeat("d", 33)) + ".txt", new string('n', 256) + ".txt" })
            reject(() => Plan(Build(new[] { new Entry(name, big) })), "ZIP rejects unsafe name: " + name);
        reject(() => Plan(Build(new[] { new Entry("A.txt", big), new Entry("a.TXT", big) })), "ZIP rejects case-only duplicates");
        reject(() => Plan(Build(new[] { new Entry("same.txt", big), new Entry("same.txt", big) })), "ZIP rejects exact duplicates");
        reject(() => Plan(Build(new[] { new Entry("a", big), new Entry("a/b.txt", big) })), "ZIP rejects file then folder of same name");
        reject(() => Plan(Build(new[] { new Entry("a/b.txt", big), new Entry("A", big) })), "ZIP rejects folder then file of same name");
        reject(() => Plan(Build(new[] { new Entry("caf\u00e9.txt", big), new Entry("cafe\u0301.txt", big) })), "ZIP rejects names identical after normalization");
        check(Plan(Build(new[] { new Entry("D/x.txt", big), new Entry("d/y.txt", big) })).Directories.Single() == "D", "ZIP folders differing in case share one folder");
        reject(() => Plan(Build(new[] { new Entry("e.txt", big, Flags: 0x0801) })), "ZIP rejects encryption flag");
        reject(() => Plan(Build(new[] { new Entry("e.txt", big, Flags: 0x0841) })), "ZIP rejects strong encryption");
        reject(() => Plan(Build(new[] { new Entry("e.txt", big, Extra: new byte[] { 0x01, 0x99, 0, 0 }) })), "ZIP rejects AES extra");
        reject(() => Plan(Build(new[] { new Entry("m.txt", big, 12) })), "ZIP rejects other compression methods");
        reject(() => Plan(Build(new[] { new Entry("n.txt", big, Needed: 45) })), "ZIP rejects newer version needed");
        reject(() => Plan(Build(new[] { new Entry("z.txt", big, Extra: new byte[] { 1, 0, 0, 0 }) })), "ZIP rejects ZIP64 extra");
        reject(() => Plan(Build(new[] { new Entry("z.txt", big, Size: uint.MaxValue) })), "ZIP rejects ZIP64 size marker");
        reject(() => Plan(Build(new[] { new Entry("l", Encoding.ASCII.GetBytes("/etc"), MadeBy: 0x031E, External: 0xA1FF0000) })), "ZIP rejects Unix symlink");
        reject(() => Plan(Build(new[] { new Entry("f", Array.Empty<byte>(), MadeBy: 0x031E, External: 0x11B60000) })), "ZIP rejects Unix FIFO");
        reject(() => Plan(Build(new[] { new Entry("r.txt", big, External: 0x400) })), "ZIP rejects DOS reparse attribute");
        reject(() => Plan(Build(new[] { new Entry("n\u00e9.txt", big, Flags: 0, RawName: new byte[] { 0x6E, 0xE9, 0x2E, 0x74 }) })), "ZIP rejects legacy code-page names");
        reject(() => Plan(Build(new[] { new Entry("bad.txt", big, RawName: new byte[] { 0x61, 0xC0, 0x80 }) })), "ZIP rejects invalid UTF-8 names");
        reject(() => Plan(Build(new[] { new Entry("u.txt", big, Flags: 0, Extra: new byte[] { 0x75, 0x70, 0, 0 }) })), "ZIP rejects ambiguous Unicode path extra");
        reject(() => Plan(Build(new[] { new Entry("d/", big) })), "ZIP rejects folder entries with data");
        reject(() => Plan(Build(new[] { new Entry("s.txt", big, Size: 5) })), "ZIP rejects Store size mismatch");
        reject(() => Plan(Build(new[] { new Entry("a.txt", big) }, after: new byte[] { 1 })), "ZIP rejects bytes after the end record");
        reject(() => Plan(Build(new[] { new Entry("a.txt", big) }, between: new byte[] { 1, 2 })), "ZIP rejects data between directory and end record");
        reject(() => Plan(Build(Array.Empty<Entry>())), "ZIP rejects empty archive");
        reject(() => Plan(Build(new[] { new Entry("a.txt", big) }, count: 2)), "ZIP rejects wrong entry count");
        reject(() => Plan(Build(new[] { new Entry("a.txt", big), new Entry("b.txt", big, Offset: 0) })), "ZIP rejects members sharing data");
        reject(() => Plan(new byte[30]), "ZIP rejects non-ZIP data");
        reject(() => Plan(Array.Empty<byte>()), "ZIP rejects empty file");
        reject(() => Plan(Build(Enumerable.Range(0, 10001).Select(i => new Entry("f" + i, Array.Empty<byte>())).ToArray())), "ZIP limits entries");
        check(Plan(Build(Enumerable.Range(0, 10000).Select(i => new Entry("f" + i, Array.Empty<byte>())).ToArray())).Files.Count == 10000, "ZIP accepts exactly 10,000 entries");
        // Comments that contain an end-record signature are ambiguous.
        byte[] trap = Build(new[] { new Entry("a.txt", big) });
        trap[^2] = 22; trap[^1] = 0;
        var trapped = trap.Concat(new byte[] { 0x50, 0x4b, 5, 6 }).Concat(new byte[18]).ToArray();
        reject(() => Plan(trapped), "ZIP rejects ambiguous end record");

        // Data checks happen while extracting.
        var badCrc = Build(new[] { new Entry("c.txt", big, 8, Crc: 1234) });
        reject(() => Contents(badCrc, Plan(badCrc).Files[0]), "ZIP rejects CRC mismatch");
        var shortSize = Build(new[] { new Entry("c.txt", big, 8, Size: (uint)big.Length - 1) });
        reject(() => Contents(shortSize, Plan(shortSize).Files[0]), "ZIP rejects longer-than-declared Deflate data");
        var longSize = Build(new[] { new Entry("c.txt", big, 8, Size: (uint)big.Length + 1) });
        reject(() => Contents(longSize, Plan(longSize).Files[0]), "ZIP rejects shorter-than-declared Deflate data");
        var damaged = Build(new[] { new Entry("c.txt", big, 8) }); damaged[60] ^= 0xFF; damaged[61] ^= 0xFF;
        reject(() => Contents(damaged, Plan(damaged).Files[0]), "ZIP rejects damaged Deflate data");
        var renamed = Build(new[] { new Entry("c.txt", big) }); renamed[30] = (byte)'d';
        reject(() => Plan(renamed), "ZIP rejects local name differing from directory");
        // A bomb cannot write more than the declared size.
        var bomb = Build(new[] { new Entry("b.txt", new byte[50_000_000], 8, Size: 1000) });
        reject(() => Contents(bomb, Plan(bomb).Files[0]), "ZIP stops at the declared size");
        reject(() => ZipExtract.Read(new MemoryStream(ok), new CancellationToken(true)), "ZIP plan cancellation");
        reject(() => ZipExtract.Extract(new MemoryStream(ok), Plan(ok).Files[0], new MemoryStream(), new CancellationToken(true)), "ZIP extract cancellation");

        // Review regressions (0.82).
        reject(() => Plan(Build(new[] { new Entry("l", Encoding.ASCII.GetBytes("/etc"), MadeBy: 0x1314, External: 0xA1ED0000) })), "ZIP rejects macOS-host symlink");
        foreach (string name in new[] { "LONGDI~1/x.txt", "longdi~1", "PROGRA~12.TXT", "a~1" })
            reject(() => Plan(Build(new[] { new Entry(name, big) })), "ZIP rejects short-name form: " + name);
        check(Plan(Build(new[] { new Entry("report~v2.txt", big), new Entry("my~notes~1 long name.txt", big) })).Files.Count == 2, "ZIP keeps ordinary names with tildes");
        byte[] commented = Build(new[] { new Entry("a.txt", big) }); commented[^2] = 4;
        check(Plan(commented.Concat(new byte[] { 0x50, 0x4b, 6, 7 }).ToArray()).Files.Count == 1, "ZIP end comment accepted");
        check(ZipExtract.TargetZone(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[ZoneTransfer]\r\nZoneId=4\r\n")).ToArray()) == 4, "Zone UTF-16 Restricted kept");
        check(ZipExtract.TargetZone(Encoding.ASCII.GetBytes("[ZoneTransfer]\rZoneId=4\r")) == 4, "Zone CR-only Restricted kept");
        check(ZipExtract.TargetZone(null) == 3, "Zone default Internet");
        check(ZipExtract.TargetZone(Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=4\r\n")) == 4, "Zone Restricted kept");
        check(ZipExtract.TargetZone(Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=2\r\nReferrerUrl=x")) == 3, "Zone never lowered below Internet");
        check(ZipExtract.TargetZone(Encoding.ASCII.GetBytes("ZoneId=9")) == 3 && ZipExtract.TargetZone(Encoding.ASCII.GetBytes("ZoneId=44")) == 3, "Zone unknown values count as Internet");
        check(ZipExtract.TargetZone(new byte[] { 0xFF, 0xFE, 0, 0 }) == 3, "Zone undecodable data counts as Internet");
        check(Encoding.ASCII.GetString(ZipExtract.ZoneIdentifier(3)) == "[ZoneTransfer]\r\nZoneId=3\r\n", "Zone file contents");
        reject(() => ZipExtract.ZoneIdentifier(2), "Zone writer refuses weaker zones");
        check(ZipExtract.FolderStem("photos.zip") == "photos" && ZipExtract.FolderStem(".zip") == "Extracted" && ZipExtract.FolderStem("CON.zip") == "Extracted", "Folder names from archive name");
        check(ZipExtract.FolderName("photos", 2) == "photos 2", "Folder collision naming");
    }
}
