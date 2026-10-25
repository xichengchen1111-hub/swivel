using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// One validated ZIP member. Path uses '\' and contains only names that passed
/// WindowsPathPolicy.ValidateName; it is always relative to the new extraction folder.
public sealed record ZipMember(string Path, bool IsDirectory, uint Crc, long CompressedSize, long Size, ushort Method,
    ushort Flags, long LocalOffset, byte[] RawName, DateTime? Modified);

public sealed class ZipPlan
{
    public required IReadOnlyList<ZipMember> Files { get; init; }
    /// Every folder to create, explicit or implied, parents before children.
    public required IReadOnlyList<string> Directories { get; init; }
    public long TotalBytes { get; init; }
    public int LongestPath { get; init; }
    /// macOS AppleDouble "._name" attribute files (TAR) that were recognised and left out.
    public int SkippedAppleDouble { get; init; }
}

/// Original ZIP32 reader for extraction: Store and Deflate only, central directory first,
/// every member validated before anything is written. No encryption, ZIP64, multi-disk,
/// links, devices or ambiguous names. Data integrity is the exact size plus CRC-32.
public static class ZipExtract
{
    public const int MaxEntries = 10000, MaxDepth = 32;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024, MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;
    private const int EndRecord = 22, CentralRecord = 46, LocalRecord = 30;

    public static ZipPlan Read(Stream archive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!archive.CanRead || !archive.CanSeek) throw new ArgumentException("A held seekable archive is required.");
        long length = archive.Length;
        if (length < EndRecord) throw new UserError("This file is not a supported ZIP archive.");
        if (length > MaxArchiveBytes) throw new UserError("ZIP extraction is limited to archives of 2 GB.");

        // The end record must be the only candidate whose comment reaches exactly the end of the file.
        int tail = (int)Math.Min(length, EndRecord + 65535);
        byte[] end = ReadAt(archive, length - tail, tail, token);
        int found = -1;
        for (int i = tail - EndRecord; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(i)) != 0x06054b50) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(i + 20)) != tail - i - EndRecord) continue;
            if (found >= 0) throw new UserError("This ZIP archive has an ambiguous end record and was not extracted.");
            found = i;
        }
        if (found < 0) throw new UserError("This file is not a supported ZIP archive.");
        var record = end.AsSpan(found);
        long endOffset = length - tail + found;
        ushort disk = U16(record, 4), cdDisk = U16(record, 6), diskEntries = U16(record, 8), entries = U16(record, 10);
        uint cdSize = U32(record, 12), cdOffset = U32(record, 16);
        if (entries == 0xFFFF || cdSize == uint.MaxValue || cdOffset == uint.MaxValue)
            throw new UserError("ZIP64 archives are not supported in the Windows preview yet.");
        if (disk != 0 || cdDisk != 0 || diskEntries != entries) throw new UserError("Split or multi-disk ZIP archives are not supported.");
        if (entries == 0) throw new UserError("This ZIP archive is empty.");
        if (entries > MaxEntries) throw new UserError("ZIP extraction is limited to 10,000 entries.");
        // The central directory must end exactly at the end record: no hidden data in between.
        // ZIP64 end records would sit here too, so this also refuses ZIP64 archives without markers.
        if ((long)cdOffset + cdSize != endOffset) throw new UserError("This ZIP archive has unexpected data around its directory and was not extracted.");
        if (cdSize > MaxDirectoryBytes) throw new UserError("This ZIP archive's directory is damaged.");

        byte[] directory = ReadAt(archive, cdOffset, checked((int)cdSize), token);
        var members = new List<ZipMember>(entries);
        var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // path → is directory
        var directories = new List<string>();
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // NFC form → name as stored
        long total = 0; int longest = 0, position = 0;
        for (int index = 0; index < entries; index++)
        {
            token.ThrowIfCancellationRequested();
            if (position + CentralRecord > directory.Length || U32(directory, position) != 0x02014b50)
                throw new UserError("This ZIP archive's directory is damaged.");
            var header = directory.AsSpan(position);
            ushort madeBy = U16(header, 4), needed = U16(header, 6), flags = U16(header, 8), method = U16(header, 10);
            ushort time = U16(header, 12), date = U16(header, 14);
            uint crc = U32(header, 16), compressed = U32(header, 20), size = U32(header, 24);
            ushort nameLength = U16(header, 28), extraLength = U16(header, 30), commentLength = U16(header, 32), startDisk = U16(header, 34);
            uint external = U32(header, 38), offset = U32(header, 42);
            int next = position + CentralRecord + nameLength + extraLength + commentLength;
            if (next > directory.Length) throw new UserError("This ZIP archive's directory is damaged.");
            byte[] raw = directory.AsSpan(position + CentralRecord, nameLength).ToArray();
            var extra = directory.AsSpan(position + CentralRecord + nameLength, extraLength);
            position = next;

            if ((flags & 0x0041) != 0 || (flags & 0x2000) != 0) throw new UserError("Encrypted ZIP archives are not supported.");
            if (method is not 0 and not 8) throw new UserError("This ZIP archive uses a compression method other than Store or Deflate.");
            if ((needed & 0xFF) > 20) throw new UserError("This ZIP archive needs features beyond ZIP 2.0 and was not extracted.");
            if (startDisk != 0) throw new UserError("Split or multi-disk ZIP archives are not supported.");
            if (compressed == uint.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
                throw new UserError("ZIP64 archives are not supported in the Windows preview yet.");
            if (method == 0 && compressed != size) throw new UserError("This ZIP archive's directory is damaged.");
            CheckExtra(extra, (flags & 0x0800) != 0);

            string name = DecodeName(raw, (flags & 0x0800) != 0);
            int host = madeBy >> 8;
            bool isDirectory = name.EndsWith('/');
            if (host is 3 or 19) // Unix and macOS store POSIX modes
            {
                uint type = (external >> 16) & 0xF000;
                if (type == 0xA000) throw new UserError("ZIP archives containing links are not extracted.");
                if (type is not 0 and not 0x8000 and not 0x4000) throw new UserError("This ZIP archive contains special files and was not extracted.");
                if ((type == 0x4000) != isDirectory && type != 0) throw new UserError("This ZIP archive's directory is damaged.");
            }
            else if (host is 0 or 10 or 11 or 14)
            {
                if ((external & 0x400) != 0) throw new UserError("ZIP archives containing links are not extracted.");
                if ((external & 0x10) != 0 && !isDirectory) throw new UserError("This ZIP archive's directory is damaged.");
            }
            string[] parts = SplitName(name, isDirectory);
            string path = string.Join('\\', parts);
            for (int depth = 1; depth < parts.Length; depth++) AddDirectory(string.Join('\\', parts.Take(depth)));
            if (isDirectory)
            {
                if (size != 0 || compressed > 2) throw new UserError("This ZIP archive's directory is damaged.");
                AddDirectory(path); continue;
            }
            CheckSpelling(path);
            if (kinds.ContainsKey(path)) throw new UserError("This ZIP archive contains duplicate names (letter case is ignored on Windows) and was not extracted.");
            kinds[path] = false;
            if (size > MaxTotalBytes - total) throw new UserError("ZIP extraction is limited to 2 GB of extracted data.");
            total += size; longest = Math.Max(longest, path.Length);
            members.Add(new(path, false, crc, compressed, size, method, flags, offset, raw, DosTime(date, time)));
        }
        if (position != directory.Length) throw new UserError("This ZIP archive has unexpected data around its directory and was not extracted.");
        CheckLayout(archive, members, cdOffset, token);
        directories.Sort((a, b) => a.Count(c => c == '\\').CompareTo(b.Count(c => c == '\\')) is int order and not 0 ? order : string.CompareOrdinal(a, b));
        return new() { Files = members, Directories = directories, TotalBytes = total, LongestPath = longest };

        // Names that differ only in Unicode composition look identical in Explorer; refuse them.
        void CheckSpelling(string path)
        {
            string key = path.Normalize(NormalizationForm.FormC);
            if (spellings.TryGetValue(key, out string? stored) && !string.Equals(stored, path, StringComparison.OrdinalIgnoreCase))
                throw new UserError("This ZIP archive contains names that look identical and was not extracted.");
            spellings[key] = path;
        }
        void AddDirectory(string path)
        {
            CheckSpelling(path);
            if (kinds.TryGetValue(path, out bool existing))
            {
                if (!existing) throw new UserError("This ZIP archive uses one name for both a file and a folder and was not extracted.");
                return;
            }
            if (directories.Count >= MaxEntries) throw new UserError("ZIP extraction is limited to 10,000 entries.");
            kinds[path] = true; directories.Add(path); longest = Math.Max(longest, path.Length);
        }
    }

    // Local headers are read now, so a plan never claims a member whose data cannot be located.
    // Member ranges may not overlap one another or the directory (no shared-data expansion).
    private static void CheckLayout(Stream archive, List<ZipMember> members, long cdOffset, CancellationToken token)
    {
        var ranges = new List<(long Start, long End)>(members.Count);
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            long dataStart = LocalDataStart(archive, member, token);
            long end = dataStart + member.CompressedSize + ((member.Flags & 8) != 0 ? 12 : 0);
            if (end > cdOffset) throw new UserError("This ZIP archive's directory is damaged.");
            ranges.Add((member.LocalOffset, end));
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < ranges.Count; i++)
            if (ranges[i].Start < ranges[i - 1].End) throw new UserError("This ZIP archive reuses data between entries and was not extracted.");
    }

    private static long LocalDataStart(Stream archive, ZipMember member, CancellationToken token)
    {
        if (member.LocalOffset + LocalRecord > archive.Length) throw new UserError("This ZIP archive's directory is damaged.");
        byte[] local = ReadAt(archive, member.LocalOffset, LocalRecord, token);
        if (U32(local, 0) != 0x04034b50) throw new UserError("This ZIP archive's directory is damaged.");
        ushort flags = U16(local, 6), method = U16(local, 8), nameLength = U16(local, 26), extraLength = U16(local, 28);
        if (method != member.Method || (flags & 0x0849) != (member.Flags & 0x0849)) throw new UserError("This ZIP archive's directory is damaged.");
        if ((flags & 8) == 0 && (U32(local, 14) != member.Crc || U32(local, 18) != member.CompressedSize || U32(local, 22) != member.Size))
            throw new UserError("This ZIP archive's directory is damaged.");
        if (member.LocalOffset + LocalRecord + nameLength + extraLength > archive.Length) throw new UserError("This ZIP archive's directory is damaged.");
        byte[] name = ReadAt(archive, member.LocalOffset + LocalRecord, nameLength, token);
        if (!name.AsSpan().SequenceEqual(member.RawName)) throw new UserError("This ZIP archive's directory is damaged.");
        CheckExtra(ReadAt(archive, member.LocalOffset + LocalRecord + nameLength, extraLength, token), (flags & 0x0800) != 0);
        return member.LocalOffset + LocalRecord + nameLength + extraLength;
    }

    /// Writes exactly one member's contents. Output that is too long, too short or fails
    /// CRC-32 throws; the caller discards its private staging file.
    public static void Extract(Stream archive, ZipMember member, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long start = LocalDataStart(archive, member, token);
        archive.Position = start;
        using var slice = new Slice(archive, member.CompressedSize, token);
        using Stream source = member.Method == 8 ? new DeflateStream(slice, CompressionMode.Decompress, true) : slice;
        byte[] buffer = new byte[65536]; long written = 0; uint crc = 0xFFFFFFFF;
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            int count;
            try { count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, member.Size - written + 1)); }
            catch (InvalidDataException) { throw new UserError("A ZIP entry's compressed data is damaged and was not extracted."); }
            if (count == 0) break;
            if (count > member.Size - written) throw new UserError("A ZIP entry is larger than its directory says and was not extracted.");
            crc = Crc32(crc, buffer.AsSpan(0, count)); output.Write(buffer, 0, count); written += count;
        }
        if (written != member.Size) throw new UserError("A ZIP entry is shorter than its directory says and was not extracted.");
        if ((crc ^ 0xFFFFFFFF) != member.Crc) throw new UserError("A ZIP entry failed its CRC check and was not extracted.");
    }

    /// Mark of the Web never weakens: at least Internet (3), and a Restricted (4) source stays 4.
    /// Only plain ZoneId=0–4 lines in the first 4 KiB count; anything else counts as Internet.
    public static int TargetZone(byte[]? sourceZoneIdentifier)
    {
        if (sourceZoneIdentifier is null) return 3;
        var bytes = sourceZoneIdentifier.AsSpan(0, Math.Min(sourceZoneIdentifier.Length, 4096));
        string text = bytes.StartsWith(new byte[] { 0xFF, 0xFE }) ? Encoding.Unicode.GetString(bytes[2..])
            : bytes.StartsWith(new byte[] { 0xFE, 0xFF }) ? Encoding.BigEndianUnicode.GetString(bytes[2..])
            : Encoding.Latin1.GetString(bytes);
        int zone = 3;
        foreach (string line in text.Split('\r', '\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) continue;
            string value = trimmed[7..].Trim();
            if (value.Length == 1 && value[0] is >= '0' and <= '4') zone = Math.Max(zone, value[0] - '0');
        }
        return zone;
    }
    public static byte[] ZoneIdentifier(int zone)
    {
        if (zone is < 3 or > 4) throw new ArgumentOutOfRangeException(nameof(zone));
        return Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=" + zone.ToString(CultureInfo.InvariantCulture) + "\r\n");
    }

    /// The name of the new folder, from the archive's name without its extension.
    public static string FolderStem(string archiveFileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(archiveFileName).TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "Extracted";
        try { WindowsPathPolicy.ValidateName(stem); } catch (UserError) { stem = "Extracted"; }
        return stem;
    }
    public static string FolderName(string stem, int ordinal)
    {
        if (ordinal is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(ordinal));
        string name = ordinal == 1 ? stem : stem + " " + ordinal.ToString(CultureInfo.InvariantCulture);
        WindowsPathPolicy.ValidateName(name); return name;
    }

    private static string DecodeName(byte[] raw, bool utf8)
    {
        if (raw.Length == 0) throw new UserError("This ZIP archive contains an entry without a name.");
        if (!utf8 && raw.Any(b => b >= 0x80))
            throw new UserError("This ZIP archive uses legacy non-Unicode names. Re-create it with Unicode (UTF-8) names.");
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { throw new UserError("This ZIP archive contains invalid Unicode names."); }
    }

    private static string[] SplitName(string name, bool isDirectory)
    {
        if (name.Contains('\\') || name.StartsWith('/')) throw new UserError("This ZIP archive contains unsafe paths and was not extracted.");
        string trimmed = isDirectory ? name[..^1] : name;
        string[] parts = trimmed.Split('/');
        if (parts.Length > MaxDepth) throw new UserError("This ZIP archive nests folders more than 32 levels deep.");
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 255) throw new UserError("This ZIP archive contains unsafe paths and was not extracted.");
            try { WindowsPathPolicy.ValidateName(part); }
            catch (UserError) { throw new UserError("This ZIP archive contains names Windows cannot use safely and was not extracted."); }
            // NAME~1-style names can collide with Windows short names of other entries.
            if (ShortName.IsMatch(part)) throw new UserError("This ZIP archive contains names in the Windows short-name form (such as NAME~1) and was not extracted.");
        }
        return parts;
    }

    internal static readonly System.Text.RegularExpressions.Regex ShortName = new(@"^[^.~]{0,7}~[0-9]+(\.[^.]{0,3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void CheckExtra(ReadOnlySpan<byte> extra, bool utf8)
    {
        int at = 0;
        while (at < extra.Length)
        {
            if (at + 4 > extra.Length) throw new UserError("This ZIP archive's directory is damaged.");
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]), size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 2)..]);
            if (at + 4 + size > extra.Length) throw new UserError("This ZIP archive's directory is damaged.");
            if (id == 0x0001) throw new UserError("ZIP64 archives are not supported in the Windows preview yet.");
            if (id is 0x0017 or 0x9901) throw new UserError("Encrypted ZIP archives are not supported.");
            // A second, possibly different Unicode name is ambiguous unless the header itself is UTF-8.
            if (id == 0x7075 && !utf8) throw new UserError("This ZIP archive uses legacy non-Unicode names. Re-create it with Unicode (UTF-8) names.");
            at += 4 + size;
        }
    }

    private static DateTime? DosTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31, hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    public static uint Crc32(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) state = CrcTable[(state ^ b) & 0xFF] ^ (state >> 8);
        return state;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || count < 0 || offset + count > stream.Length) throw new UserError("This ZIP archive's directory is damaged.");
        stream.Position = offset; byte[] data = new byte[count]; stream.ReadExactly(data); return data;
    }
    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    /// Exactly the member's compressed bytes; reads beyond them return end of stream.
    private sealed class Slice(Stream inner, long length, CancellationToken token) : Stream
    {
        private long left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (left == 0 || buffer.Length == 0) return 0;
            int read = inner.Read(buffer[..(int)Math.Min(Math.Min(buffer.Length, left), 65536)]);
            if (read == 0) throw new UserError("This ZIP archive ends early.");
            left -= read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
