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

public enum TarKind { Tar, TarGzip, Gzip }

/// Original bounded reader for TAR (POSIX ustar/pax, GNU long names), gzip-compressed TAR
/// and single-file gzip. A complete review pass validates every header and name before
/// anything is written; the result is the same plan ZIP extraction publishes. TAR stores
/// no content checksum, so each file's CRC-32 is recorded while reviewing and checked
/// again while extracting. A gzip stream's own CRC-32 and length are verified.
public static class TarExtract
{
    public const long MaxDecompressedBytes = ZipExtract.MaxTotalBytes + 256L * 1024 * 1024;
    private const int Block = 512, MaxHeaders = 5 * ZipExtract.MaxEntries + 8, MaxPaxBytes = 1024 * 1024, MaxLongName = 65536;

    public static TarKind? KindFor(string fileName)
    {
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return TarKind.TarGzip;
        if (fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return TarKind.Tar;
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return TarKind.Gzip;
        return null;
    }

    /// The new folder's name: the archive name without .tar/.tar.gz/.tgz; a single-file .gz
    /// also drops the inner extension ("report.pdf.gz" → folder "report").
    public static string FolderStem(string fileName, TarKind kind)
    {
        string name = Inner(fileName, kind);
        return kind == TarKind.Gzip ? ZipExtract.FolderStem(name) : ZipExtract.FolderStem(name + ".x");
    }
    private static string Inner(string fileName, TarKind kind) => kind switch
    {
        TarKind.TarGzip => fileName[..^(fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? 4 : 7)],
        TarKind.Tar => fileName[..^4],
        _ => fileName[..^3],
    };

    public static ZipPlan Read(Stream archive, TarKind kind, string fileName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!archive.CanRead || !archive.CanSeek) throw new ArgumentException("A held seekable archive is required.");
        if (archive.Length > ZipExtract.MaxArchiveBytes) throw new UserError("Archives larger than 2 GB are not extracted in the Windows preview.");
        if (kind != TarKind.Tar) CheckGzipHeader(archive, token);
        archive.Position = 0;
        using var source = new Reader(archive, kind, token);
        ZipPlan plan = kind == TarKind.Gzip ? ReadGzipFile(source, fileName, token) : ReadTar(source, token);
        if (kind != TarKind.Tar) source.VerifyGzipTrailer(archive);
        return plan;
    }

    /// Opens the forward reader that extraction copies members from, in plan order.
    public static TarSource OpenSource(Stream archive, TarKind kind, CancellationToken token)
    {
        archive.Position = 0;
        return new TarSource(new Reader(archive, kind, token), kind == TarKind.Tar ? archive : null);
    }

    private static ZipPlan ReadGzipFile(Reader source, string fileName, CancellationToken token)
    {
        string name = Inner(fileName, TarKind.Gzip);
        string[] parts;
        try { parts = SplitName(name); }
        catch (UserError) { throw new UserError("This gzip file's name does not give a usable output name."); }
        if (parts.Length != 1) throw new UserError("This gzip file's name does not give a usable output name.");
        var (size, crc) = source.Measure(long.MaxValue, MaxDecompressedBytes);
        if (size > ZipExtract.MaxTotalBytes) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
        var member = new ZipMember(parts[0], false, crc, size, size, 0, 0, 0, Encoding.UTF8.GetBytes(parts[0]), source.GzipModified);
        return new() { Files = new[] { member }, Directories = Array.Empty<string>(), TotalBytes = size, LongestPath = parts[0].Length };
    }

    private static ZipPlan ReadTar(Reader source, CancellationToken token)
    {
        var names = new Names();
        var members = new List<ZipMember>();
        long total = 0; int headers = 0, skipped = 0;
        Dictionary<string, string>? pax = null; string? longName = null;
        var header = new byte[Block];
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            if (!source.ReadBlock(header)) throw new UserError("This archive ends early or is damaged and was not extracted.");
            if (header.All(b => b == 0))
            {
                // End of archive: everything after it, whatever its length, must be zero padding.
                if (pax is not null || longName is not null) throw new UserError("This TAR archive has damaged headers and was not extracted.");
                source.RequireZeros();
                break;
            }
            if (++headers > MaxHeaders) throw new UserError("Archive extraction is limited to 10,000 entries.");
            CheckHeader(header);
            byte type = header[156];
            long size = Number(header, 124, 12);
            switch (type)
            {
                case (byte)'x':
                    if (pax is not null) throw new UserError("This TAR archive has damaged headers and was not extracted.");
                    pax = ParsePax(source.ReadData(size, MaxPaxBytes));
                    if (pax.ContainsKey("linkpath")) throw new UserError("TAR archives containing links are not extracted.");
                    continue;
                case (byte)'g':
                    var global = ParsePax(source.ReadData(size, MaxPaxBytes));
                    // Global defaults that would rename, resize or link later entries are refused.
                    if (global.Keys.Any(k => k is "path" or "linkpath" or "size" || k.StartsWith("GNU.sparse", StringComparison.Ordinal)))
                        throw new UserError("Sparse, multi-volume or unknown TAR entries are not supported.");
                    continue;
                case (byte)'L':
                    if (longName is not null) throw new UserError("This TAR archive has damaged headers and was not extracted.");
                    longName = Utf8(TrimNul(source.ReadData(size, MaxLongName)));
                    continue;
                case (byte)'1' or (byte)'2' or (byte)'K':
                    throw new UserError("TAR archives containing links are not extracted.");
                case (byte)'3' or (byte)'4' or (byte)'6':
                    throw new UserError("This TAR archive contains special files and was not extracted.");
                case (byte)'0' or 0 or (byte)'7' or (byte)'5':
                    break;
                default:
                    throw new UserError("Sparse, multi-volume or unknown TAR entries are not supported.");
            }
            if (pax is not null && pax.Keys.Any(k => k.StartsWith("GNU.sparse", StringComparison.Ordinal)))
                throw new UserError("Sparse, multi-volume or unknown TAR entries are not supported.");
            // A pax size describes the entry it precedes, never a long-name or other metadata header.
            if (pax is not null && pax.TryGetValue("size", out string? paxSize)) size = Decimal(paxSize);
            string name = pax is not null && pax.TryGetValue("path", out string? paxPath) ? paxPath : longName ?? HeaderName(header);
            DateTime? modified = pax is not null && pax.TryGetValue("mtime", out string? paxTime) ? PaxTime(paxTime) : HeaderTime(header);
            pax = null; longName = null;
            bool directory = type == (byte)'5' || name.EndsWith('/');
            if (directory && type != (byte)'5' && type != (byte)'0' && type != 0) throw new UserError("This TAR archive has damaged headers and was not extracted.");
            string[] parts = SplitName(name);
            if (directory)
            {
                if (size != 0) throw new UserError("This TAR archive has damaged headers and was not extracted.");
                if (parts.Length > 0) names.AddDirectory(parts);
                continue;
            }
            if (parts.Length == 0) throw new UserError("This archive contains unsafe paths and was not extracted.");
            if (size > ZipExtract.MaxTotalBytes - total) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
            long offset = source.Position;
            var (read, crc) = source.Measure(size, MaxDecompressedBytes);
            if (read != size) throw new UserError("This archive ends early or is damaged and was not extracted.");
            source.SkipPadding(size);
            // macOS tar stores each item's attributes as a "._name" AppleDouble file. They mean
            // nothing on Windows; only files whose contents carry the AppleDouble signature are left out.
            if (parts[^1].StartsWith("._", StringComparison.Ordinal) && size >= 26 && source.LastStart == 0x00051607u) { skipped++; continue; }
            string path = names.AddFile(parts);
            total += size;
            members.Add(new(path, false, crc, size, size, 0, 0, offset, Encoding.UTF8.GetBytes(path), modified));
            if (members.Count + names.Directories.Count > ZipExtract.MaxEntries) throw new UserError("Archive extraction is limited to 10,000 entries.");
        }
        if (members.Count == 0 && names.Directories.Count == 0) throw new UserError("This archive is empty.");
        return new() { Files = members, Directories = names.Sorted(), TotalBytes = total, LongestPath = names.Longest, SkippedAppleDouble = skipped };
    }

    // POSIX ustar ("ustar\0" "00") and GNU ("ustar  \0") only; the checksum counts its own
    // field as spaces and may be summed as unsigned or (historically) signed bytes.
    private static void CheckHeader(byte[] header)
    {
        bool posix = IsPosix(header);
        bool gnu = header.AsSpan(257, 8).SequenceEqual("ustar  \0"u8);
        if (!posix && !gnu) throw new UserError("This TAR format is not supported. Use a POSIX (ustar or pax) or GNU TAR archive.");
        long expected = Number(header, 148, 8);
        long unsigned = 0, signed = 0;
        for (int i = 0; i < Block; i++)
        {
            byte b = i is >= 148 and < 156 ? (byte)' ' : header[i];
            unsigned += b; signed += (sbyte)b;
        }
        if (expected != unsigned && expected != signed) throw new UserError("This TAR archive has damaged headers and was not extracted.");
    }

    private static bool IsPosix(byte[] header) => header.AsSpan(257, 8).SequenceEqual("ustar\u000000"u8);

    private static string HeaderName(byte[] header)
    {
        string name = Utf8(TrimNul(header.AsSpan(0, 100).ToArray()));
        if (IsPosix(header))
        {
            string prefix = Utf8(TrimNul(header.AsSpan(345, 155).ToArray()));
            if (prefix.Length > 0) name = prefix + "/" + name;
        }
        return name;
    }

    // A negative (pre-1970) base-256 time is kept as "no time" rather than refusing the archive.
    private static DateTime? HeaderTime(byte[] header) => header[136] == 0xFF ? null : UnixTime(Number(header, 136, 12));

    /// Octal digits (leading spaces allowed, then NUL/space padding) or the base-256 form.
    private static long Number(byte[] header, int at, int length)
    {
        var field = header.AsSpan(at, length);
        if ((field[0] & 0x80) != 0)
        {
            if (field[0] != 0x80) throw new UserError("This TAR archive has damaged headers and was not extracted.");
            long value = 0;
            foreach (byte b in field[1..])
            {
                if (value > (long.MaxValue >> 8)) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
                value = (value << 8) | b;
            }
            return value;
        }
        int i = 0; while (i < field.Length && field[i] == ' ') i++;
        long result = 0; int digits = 0;
        for (; i < field.Length && field[i] is >= (byte)'0' and <= (byte)'7'; i++, digits++) result = result * 8 + (field[i] - '0');
        for (; i < field.Length; i++) if (field[i] is not 0 and not (byte)' ') throw new UserError("This TAR archive has damaged headers and was not extracted.");
        if (digits == 0) throw new UserError("This TAR archive has damaged headers and was not extracted.");
        return result;
    }

    private static long Decimal(string text)
    {
        if (text.Length is 0 or > 18 || !text.All(c => c is >= '0' and <= '9')) throw new UserError("This TAR archive has damaged headers and was not extracted.");
        return long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static DateTime? UnixTime(long seconds) =>
        seconds is >= -11644473600 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    private static DateTime? PaxTime(string text)
    {
        string whole = text.Split('.')[0];
        bool negative = whole.StartsWith('-');
        string digits = negative ? whole[1..] : whole;
        if (digits.Length is 0 or > 15 || !digits.All(c => c is >= '0' and <= '9')) throw new UserError("This TAR archive has damaged headers and was not extracted.");
        long value = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return UnixTime(negative ? -value : value);
    }

    /// "length key=value\n" records. Only keys this reader acts on are decoded as text;
    /// others (owners, extended attributes) may hold binary values and are ignored.
    private static Dictionary<string, string> ParsePax(byte[] data)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int at = 0;
        while (at < data.Length)
        {
            int space = Array.IndexOf(data, (byte)' ', at);
            if (space <= at || space - at > 9) throw new UserError("This TAR archive has damaged headers and was not extracted.");
            int length = 0;
            for (int i = at; i < space; i++)
            {
                if (data[i] is < (byte)'0' or > (byte)'9') throw new UserError("This TAR archive has damaged headers and was not extracted.");
                length = length * 10 + (data[i] - '0');
            }
            if (length < space - at + 3 || length > data.Length - at || data[at + length - 1] != (byte)'\n')
                throw new UserError("This TAR archive has damaged headers and was not extracted.");
            var record = data.AsSpan(space + 1, at + length - 1 - (space + 1));
            int equals = record.IndexOf((byte)'=');
            if (equals <= 0) throw new UserError("This TAR archive has damaged headers and was not extracted.");
            string key = Utf8(record[..equals].ToArray());
            if (key is "path" or "linkpath" or "size" or "mtime") map[key] = Utf8(record[(equals + 1)..].ToArray());
            else map[key] = "";
            at += length;
        }
        return map;
    }

    private static byte[] TrimNul(byte[] data)
    {
        int end = Array.IndexOf(data, (byte)0);
        return end < 0 ? data : data[..end];
    }
    private static string Utf8(byte[] data)
    {
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { throw new UserError("This archive uses legacy non-Unicode names. Re-create it with Unicode (UTF-8) names."); }
    }

    /// A leading "./" is the archive's own root and is dropped; ".", "..", empty segments,
    /// backslashes and absolute paths are refused.
    internal static string[] SplitName(string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.StartsWith('/') || name.Contains('\0')) throw new UserError("This archive contains unsafe paths and was not extracted.");
        var parts = name.TrimEnd('/').Split('/').ToList();
        while (parts.Count > 0 && parts[0] == ".") parts.RemoveAt(0);
        if (parts.Count > ZipExtract.MaxDepth) throw new UserError("This archive nests folders more than 32 levels deep.");
        foreach (string part in parts)
        {
            if (part.Length is 0 or > 255 || part is "." or "..") throw new UserError("This archive contains unsafe paths and was not extracted.");
            try { WindowsPathPolicy.ValidateName(part); }
            catch (UserError) { throw new UserError("This archive contains names Windows cannot use safely and was not extracted."); }
            if (ZipExtract.ShortName.IsMatch(part)) throw new UserError("This archive contains names in the Windows short-name form (such as NAME~1) and was not extracted.");
        }
        return parts.ToArray();
    }

    private static void CheckGzipHeader(Stream archive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (archive.Length < 18) throw new UserError("This gzip file is damaged, uses more than one part, or failed its CRC check, and was not extracted.");
        archive.Position = 0; var head = new byte[10]; archive.ReadExactly(head);
        if (head[0] != 0x1F || head[1] != 0x8B || head[2] != 8 || (head[3] & 0xE0) != 0)
            throw new UserError("This gzip file is damaged, uses more than one part, or failed its CRC check, and was not extracted.");
    }

    // Names, duplicates (case-insensitive and NFC), and file/folder conflicts, like ZIP.
    internal sealed class Names
    {
        private readonly Dictionary<string, bool> kinds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> spellings = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Directories { get; } = new();
        public int Longest { get; private set; }
        public void AddDirectory(string[] parts)
        {
            for (int depth = 1; depth <= parts.Length; depth++) Directory(string.Join('\\', parts.Take(depth)));
        }
        public string AddFile(string[] parts)
        {
            for (int depth = 1; depth < parts.Length; depth++) Directory(string.Join('\\', parts.Take(depth)));
            string path = string.Join('\\', parts);
            Spelling(path);
            if (kinds.ContainsKey(path)) throw new UserError("This archive contains duplicate names (letter case is ignored on Windows) and was not extracted.");
            kinds[path] = false; Longest = Math.Max(Longest, path.Length);
            return path;
        }
        private void Directory(string path)
        {
            Spelling(path);
            if (kinds.TryGetValue(path, out bool existing))
            {
                if (!existing) throw new UserError("This archive uses one name for both a file and a folder and was not extracted.");
                return;
            }
            if (Directories.Count >= ZipExtract.MaxEntries) throw new UserError("Archive extraction is limited to 10,000 entries.");
            kinds[path] = true; Directories.Add(path); Longest = Math.Max(Longest, path.Length);
        }
        private void Spelling(string path)
        {
            string key = path.Normalize(NormalizationForm.FormC);
            if (spellings.TryGetValue(key, out string? stored) && !string.Equals(stored, path, StringComparison.OrdinalIgnoreCase))
                throw new UserError("This archive contains names that look identical and was not extracted.");
            spellings[key] = path;
        }
        public IReadOnlyList<string> Sorted()
        {
            var sorted = Directories.ToList();
            sorted.Sort((a, b) => a.Count(c => c == '\\').CompareTo(b.Count(c => c == '\\')) is int order and not 0 ? order : string.CompareOrdinal(a, b));
            return sorted;
        }
    }

    /// The archive's TAR bytes in order: the file itself, or its gzip decompression with a
    /// running CRC-32 and length, capped against decompression bombs.
    internal sealed class Reader : IDisposable
    {
        private readonly Stream stream;
        private readonly GZipStream? gzip;
        private readonly CancellationToken token;
        private readonly byte[] buffer = new byte[65536];
        private uint crc = 0xFFFFFFFF;
        public long Position { get; private set; }
        public DateTime? GzipModified { get; }
        public Reader(Stream archive, TarKind kind, CancellationToken token)
        {
            this.token = token;
            if (kind == TarKind.Tar) { stream = archive; return; }
            var head = new byte[8]; archive.Position = 4; archive.ReadExactly(head.AsSpan(0, 4)); archive.Position = 0;
            uint time = BinaryPrimitives.ReadUInt32LittleEndian(head);
            GzipModified = time == 0 ? null : UnixTime(time);
            gzip = new GZipStream(archive, CompressionMode.Decompress, true);
            stream = gzip;
        }
        private int Read(Span<byte> span)
        {
            token.ThrowIfCancellationRequested();
            int read;
            try { read = stream.Read(span); }
            catch (InvalidDataException) { throw new UserError("This gzip file is damaged, uses more than one part, or failed its CRC check, and was not extracted."); }
            if (read > MaxDecompressedBytes - Position) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
            if (gzip is not null) crc = ZipExtract.Crc32(crc, span[..read]);
            Position += read; return read;
        }
        private int Fill(Span<byte> span)
        {
            int total = 0;
            while (total < span.Length) { int read = Read(span[total..]); if (read == 0) break; total += read; }
            return total;
        }
        public bool ReadBlock(byte[] block)
        {
            int read = Fill(block);
            if (read == 0) return false;
            if (read != block.Length) throw new UserError("This archive ends early or is damaged and was not extracted.");
            return true;
        }
        public byte[] ReadData(long size, int limit)
        {
            if (size > limit) throw new UserError("This TAR archive has damaged headers and was not extracted.");
            var data = new byte[size];
            if (Fill(data) != size) throw new UserError("This archive ends early or is damaged and was not extracted.");
            SkipPadding(size);
            return data;
        }
        public void SkipPadding(long size)
        {
            int padding = (int)((Block - size % Block) % Block);
            if (padding > 0 && Fill(buffer.AsSpan(0, padding)) != padding) throw new UserError("This archive ends early or is damaged and was not extracted.");
        }
        /// Reads up to size bytes (stopping early only at the end), returning the count and CRC-32.
        /// The first four bytes (big-endian) of the last measured data, when it had four.
        public uint LastStart { get; private set; }
        public (long Read, uint Crc) Measure(long size, long cap)
        {
            uint state = 0xFFFFFFFF; long total = 0; LastStart = 0;
            while (total < size)
            {
                int read = Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - total)));
                if (read == 0) break;
                // Decompressors may return fewer than four bytes at a time; collect the first four.
                for (int i = 0; i < read && total + i < 4; i++) LastStart = (LastStart << 8) | buffer[i];
                state = ZipExtract.Crc32(state, buffer.AsSpan(0, read)); total += read;
                if (total > cap) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
            }
            return (total, state ^ 0xFFFFFFFF);
        }
        public void Copy(long size, Stream output, out uint memberCrc)
        {
            uint state = 0xFFFFFFFF; long total = 0;
            while (total < size)
            {
                int read = Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - total)));
                if (read == 0) throw new UserError("This archive ends early or is damaged and was not extracted.");
                state = ZipExtract.Crc32(state, buffer.AsSpan(0, read)); output.Write(buffer, 0, read); total += read;
            }
            memberCrc = state ^ 0xFFFFFFFF;
        }
        public void Skip(long count)
        {
            while (count > 0)
            {
                int read = Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, count)));
                if (read == 0) throw new UserError("This archive ends early or is damaged and was not extracted.");
                count -= read;
            }
        }
        public void Seek(long position) { stream.Position = position; Position = position; }
        public void RequireZeros()
        {
            for (int read; (read = Read(buffer)) > 0;)
                if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0) throw new UserError("This archive has unexpected data after its end and was not extracted.");
        }
        /// The last 8 bytes of a single-part gzip file are the CRC-32 and length (mod 2^32)
        /// of everything decompressed. A truncated stream, a second part or appended data fails.
        public void VerifyGzipTrailer(Stream archive)
        {
            if (gzip is null) return;
            if (Read(buffer) != 0) throw new UserError("This archive has unexpected data after its end and was not extracted.");
            var trailer = new byte[8]; archive.Position = archive.Length - 8; archive.ReadExactly(trailer);
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != (crc ^ 0xFFFFFFFF) || BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4)) != (uint)Position)
                throw new UserError("This gzip file is damaged, uses more than one part, or failed its CRC check, and was not extracted.");
        }
        public void Dispose() => gzip?.Dispose();
    }
}

/// Copies reviewed members in plan order. A plain TAR is read by position; a gzip stream
/// is decompressed once more, forward only. Every member must match its reviewed CRC-32.
public sealed class TarSource : IDisposable
{
    private readonly TarExtract.Reader reader;
    private readonly Stream? seekable;
    internal TarSource(TarExtract.Reader reader, Stream? seekable) { this.reader = reader; this.seekable = seekable; }
    public void Extract(ZipMember member, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (seekable is not null) reader.Seek(member.LocalOffset);
        else if (member.LocalOffset < reader.Position) throw new InvalidOperationException("Members are extracted in archive order.");
        else reader.Skip(member.LocalOffset - reader.Position);
        reader.Copy(member.Size, output, out uint crc);
        if (crc != member.Crc) throw new UserError("An archive entry changed after it was reviewed and was not extracted.");
    }
    public void Dispose() => reader.Dispose();
}
