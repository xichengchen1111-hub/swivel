using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// A reviewed 7z archive: the plan extraction publishes, plus where each file's data lives.
public sealed class SevenZipArchive
{
    public ZipPlan Plan { get; }
    internal IReadOnlyList<SevenZipExtract.Folder> Folders { get; }
    internal Dictionary<ZipMember, SevenZipExtract.Place> Places { get; }
    internal SevenZipArchive(ZipPlan plan, IReadOnlyList<SevenZipExtract.Folder> folders, Dictionary<ZipMember, SevenZipExtract.Place> places)
    { Plan = plan; Folders = folders; Places = places; }
}

/// Original bounded 7z reader, written from the published 7z format description. It reads
/// the signature header and both CRCs, a plain or LZMA/LZMA2-encoded header, folders made of
/// Copy, LZMA, LZMA2 and optionally the x86 BCJ filter, and file names, sizes, CRCs, times and
/// attributes. Encryption, other methods, split archives, anti-items, links, special files and
/// anything it cannot interpret are refused. Every name passes the same rules as ZIP and TAR
/// before anything is written, and every file's data is checked against its CRC-32.
public static class SevenZipExtract
{
    public const int MaxWindowBytes = 256 * 1024 * 1024;
    private const int SignatureBytes = 32, MaxHeaderBytes = 64 * 1024 * 1024, MaxCoders = 4;
    private static readonly byte[] Signature = { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C };
    private const ulong Copy = 0x00, Lzma = 0x030101, Lzma2 = 0x21, BcjX86 = 0x03030103, Aes = 0x06F10701;

    private const byte End = 0x00, Header = 0x01, ArchiveProperties = 0x02, AdditionalStreams = 0x03, MainStreams = 0x04,
        FilesInfo = 0x05, PackInfo = 0x06, UnpackInfo = 0x07, SubStreamsInfo = 0x08, Size = 0x09, Crc = 0x0A, FolderId = 0x0B,
        CodersUnpackSize = 0x0C, NumUnpackStream = 0x0D, EmptyStream = 0x0E, EmptyFile = 0x0F, Anti = 0x10, Name = 0x11,
        CTime = 0x12, ATime = 0x13, MTime = 0x14, WinAttributes = 0x15, Comment = 0x16, EncodedHeader = 0x17, StartPos = 0x18, Dummy = 0x19;

    internal const string Damaged = "This 7z archive is damaged and was not extracted.";
    internal const string DataDamaged = "A 7z entry's compressed data is damaged and was not extracted.";

    /// One coder in decoding order (packed side first). Unpacked is its own output size.
    internal sealed record Coder(ulong Method, byte[] Properties, long Unpacked);
    internal sealed record Folder(long PackOffset, long PackSize, IReadOnlyList<Coder> Chain, long Unpacked, uint? Crc);
    /// Folder < 0 means an empty file with no data stream.
    internal readonly record struct Place(int Folder, int Index, bool Last);

    private sealed record Streams(long PackPosition, long[] PackSizes, Folder[] Folders, int[] Substreams, long[] Sizes, uint?[] Crcs);

    public static SevenZipArchive Read(Stream archive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!archive.CanRead || !archive.CanSeek) throw new ArgumentException("A held seekable archive is required.");
        long length = archive.Length;
        if (length > ZipExtract.MaxArchiveBytes) throw new UserError("Archives larger than 2 GB are not extracted in the Windows preview.");
        if (length < SignatureBytes) throw new UserError("This file is not a supported 7z archive.");
        byte[] start = ReadAt(archive, 0, SignatureBytes);
        if (!start.AsSpan(0, 6).SequenceEqual(Signature)) throw new UserError("This file is not a supported 7z archive.");
        if (start[6] != 0 || start[7] > 4) throw new UserError("This 7z archive needs a newer format version and was not extracted.");
        if (ZipExtract.Crc32(0xFFFFFFFF, start.AsSpan(12, 20)) != ~BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(8))) throw new UserError(Damaged);
        ulong nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(start.AsSpan(12)), nextSize = BinaryPrimitives.ReadUInt64LittleEndian(start.AsSpan(20));
        uint nextCrc = BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(28));
        if (nextSize == 0 && nextOffset == 0) throw new UserError("This 7z archive is empty.");
        // The header must end exactly at the end of the file. A header beyond it means a
        // truncated download or the first part of a split archive.
        if (nextOffset > (ulong)length || nextSize > (ulong)length || SignatureBytes + nextOffset + nextSize > (ulong)length)
            throw new UserError("This 7z archive is incomplete or split into parts and was not extracted.");
        if (SignatureBytes + nextOffset + nextSize != (ulong)length) throw new UserError("This 7z archive has unexpected data after its header and was not extracted.");
        if (nextSize > MaxHeaderBytes) throw new UserError(Damaged);
        long headerStart = SignatureBytes + (long)nextOffset;
        byte[] header = ReadAt(archive, headerStart, (int)nextSize);
        if ((ZipExtract.Crc32(0xFFFFFFFF, header) ^ 0xFFFFFFFF) != nextCrc) throw new UserError(Damaged);

        var reader = new HeaderReader(header);
        byte kind = reader.Byte();
        if (kind == EncodedHeader)
        {
            var encoded = ReadStreams(reader, headerStart);
            reader.RequireEnd();
            if (encoded.Folders.Length != 1 || encoded.Folders[0].Crc is null || encoded.Folders[0].Unpacked > MaxHeaderBytes || encoded.Substreams[0] != 1)
                throw new UserError(Damaged);
            header = DecodeAll(archive, encoded.Folders[0], token);
            reader = new HeaderReader(header);
            kind = reader.Byte();
        }
        if (kind != Header) throw new UserError(Damaged);
        return ReadHeader(reader, headerStart, token);
    }

    private static SevenZipArchive ReadHeader(HeaderReader reader, long headerStart, CancellationToken token)
    {
        byte id = reader.Byte();
        if (id == ArchiveProperties)
        {
            for (byte property; (property = reader.Byte()) != End;) reader.Skip(reader.Number());
            id = reader.Byte();
        }
        if (id == AdditionalStreams) throw new UserError("This 7z archive uses features this reader does not support and was not extracted.");
        Streams? streams = null;
        if (id == MainStreams) { streams = ReadStreams(reader, headerStart); id = reader.Byte(); }
        if (id != FilesInfo) throw new UserError(id == End && streams is null ? "This 7z archive is empty." : Damaged);
        var files = ReadFiles(reader, token);
        if (reader.Byte() != End) throw new UserError(Damaged);
        reader.RequireEnd();
        return Assemble(files, streams ?? new Streams(0, Array.Empty<long>(), Array.Empty<Folder>(), Array.Empty<int>(), Array.Empty<long>(), Array.Empty<uint?>()), token);
    }

    private sealed record FileRecord(string Name, bool HasStream, bool IsDirectory, uint? Attributes, DateTime? Modified);

    private static List<FileRecord> ReadFiles(HeaderReader reader, CancellationToken token)
    {
        long count = reader.Number();
        if (count > ZipExtract.MaxEntries) throw new UserError("Archive extraction is limited to 10,000 entries.");
        int files = (int)count;
        bool[]? emptyStream = null, emptyFile = null;
        string[]? names = null; long?[]? times = null; uint?[]? attributes = null;
        var seen = new HashSet<byte>();
        int empties = 0;
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            byte type = reader.Byte();
            if (type == End) break;
            long size = reader.Number();
            int end = reader.Limit(size);
            if (type != Dummy && !seen.Add(type)) throw new UserError(Damaged);
            switch (type)
            {
                case EmptyStream:
                    emptyStream = reader.Bits(files); empties = emptyStream.Count(b => b); break;
                case EmptyFile:
                    if (emptyStream is null) throw new UserError(Damaged);
                    emptyFile = reader.Bits(empties); break;
                case Anti:
                    if (emptyStream is null) throw new UserError(Damaged);
                    if (reader.Bits(empties).Any(b => b)) throw new UserError("This 7z archive contains deletion markers (anti-items) and was not extracted.");
                    break;
                case Name:
                    if (reader.Byte() != 0) throw new UserError(Damaged);
                    names = reader.Names(files, end); break;
                case MTime:
                    times = new long?[files];
                    var timeDefined = reader.Defined(files);
                    if (reader.Byte() != 0) throw new UserError(Damaged);
                    for (int i = 0; i < files; i++) if (timeDefined[i]) times[i] = (long)reader.UInt64();
                    break;
                case WinAttributes:
                    attributes = new uint?[files];
                    var attributeDefined = reader.Defined(files);
                    if (reader.Byte() != 0) throw new UserError(Damaged);
                    for (int i = 0; i < files; i++) if (attributeDefined[i]) attributes[i] = reader.UInt32();
                    break;
                case CTime or ATime or Comment or StartPos or Dummy:
                    reader.Skip(size); break;
                default:
                    throw new UserError("This 7z archive uses features this reader does not support and was not extracted.");
            }
            if (reader.Position != end) throw new UserError(Damaged);
        }
        if (files > 0 && names is null) throw new UserError(Damaged);
        var result = new List<FileRecord>(files);
        for (int i = 0, e = 0; i < files; i++)
        {
            bool empty = emptyStream?[i] == true;
            bool isFileWithoutData = empty && emptyFile is not null && emptyFile[e];
            if (empty) e++;
            DateTime? modified = times?[i] is long ticks && ticks >= 0 && ticks <= DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(ticks) : null;
            result.Add(new(names![i], !empty, empty && !isFileWithoutData, attributes?[i], modified));
        }
        return result;
    }

    private static SevenZipArchive Assemble(List<FileRecord> files, Streams streams, CancellationToken token)
    {
        if (files.Count == 0) throw new UserError("This 7z archive is empty.");
        if (files.Count(f => f.HasStream) != streams.Sizes.Length) throw new UserError(Damaged);
        var names = new TarExtract.Names();
        var members = new List<ZipMember>();
        var places = new Dictionary<ZipMember, Place>(ReferenceEqualityComparer.Instance);
        // Stream k belongs to folder f at position j within it.
        var owner = new (int Folder, int Index, bool Last)[streams.Sizes.Length];
        for (int f = 0, k = 0; f < streams.Folders.Length; f++)
            for (int j = 0; j < streams.Substreams[f]; j++, k++) owner[k] = (f, j, j == streams.Substreams[f] - 1);
        long total = 0; int stream = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            CheckAttributes(file);
            string[] parts = TarExtract.SplitName(file.Name.Replace('\\', '/'));
            if (file.IsDirectory) { if (parts.Length > 0) names.AddDirectory(parts); continue; } // "." is the archive's own root
            if (parts.Length == 0) throw new UserError("This archive contains unsafe paths and was not extracted.");
            string path = names.AddFile(parts);
            long size = 0; uint crc = 0; Place place = new(-1, 0, false);
            if (file.HasStream)
            {
                size = streams.Sizes[stream];
                crc = streams.Crcs[stream] ?? throw new UserError("This 7z archive has files without checksums and was not extracted.");
                place = new(owner[stream].Folder, owner[stream].Index, owner[stream].Last);
                stream++;
            }
            if (size > ZipExtract.MaxTotalBytes - total) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
            total += size;
            var member = new ZipMember(path, false, crc, size, size, 0, 0, 0, Encoding.UTF8.GetBytes(path), file.Modified);
            members.Add(member); places[member] = place;
        }
        if (members.Count + names.Directories.Count > ZipExtract.MaxEntries) throw new UserError("Archive extraction is limited to 10,000 entries.");
        return new(new() { Files = members, Directories = names.Sorted(), TotalBytes = total, LongestPath = names.Longest }, streams.Folders, places);
    }

    private static void CheckAttributes(FileRecord file)
    {
        if (file.Attributes is not uint attributes) return;
        if ((attributes & 0x400) != 0) throw new UserError("7z archives containing links are not extracted.");
        if ((attributes & 0x8000) != 0) // POSIX mode in the high 16 bits
        {
            uint type = (attributes >> 16) & 0xF000;
            if (type == 0xA000) throw new UserError("7z archives containing links are not extracted.");
            if (type is not 0 and not 0x8000 and not 0x4000) throw new UserError("This 7z archive contains special files and was not extracted.");
            if (type == 0x4000 && !file.IsDirectory || type == 0x8000 && file.IsDirectory) throw new UserError(Damaged);
        }
        if ((attributes & 0x10) != 0 && !file.IsDirectory) throw new UserError(Damaged);
    }

    private static Streams ReadStreams(HeaderReader reader, long headerStart)
    {
        byte id = reader.Byte();
        long packPosition = 0; long[] packSizes = Array.Empty<long>(); Folder[]? folders = null;
        if (id == PackInfo)
        {
            packPosition = reader.Number();
            long count = reader.Number();
            if (count > ZipExtract.MaxEntries) throw new UserError(Damaged);
            packSizes = new long[count];
            id = reader.Byte();
            if (id != Size) throw new UserError(Damaged);
            for (int i = 0; i < count; i++) packSizes[i] = reader.Number();
            id = reader.Byte();
            if (id == Crc) { reader.Digests((int)count); id = reader.Byte(); }
            if (id != End) throw new UserError(Damaged);
            id = reader.Byte();
        }
        if (id == UnpackInfo) { folders = ReadFolders(reader, packPosition, packSizes, headerStart); id = reader.Byte(); }
        folders ??= Array.Empty<Folder>();
        if (folders.Length == 0 && packSizes.Length != 0) throw new UserError(Damaged);
        int[] substreams = Enumerable.Repeat(1, folders.Length).ToArray();
        long[] sizes = folders.Select(f => f.Unpacked).ToArray();
        uint?[] crcs = folders.Select(f => f.Crc).ToArray();
        if (id == SubStreamsInfo) { (substreams, sizes, crcs) = ReadSubstreams(reader, folders); id = reader.Byte(); }
        if (id != End) throw new UserError(Damaged);
        return new(packPosition, packSizes, folders, substreams, sizes, crcs);
    }

    private static Folder[] ReadFolders(HeaderReader reader, long packPosition, long[] packSizes, long headerStart)
    {
        if (reader.Byte() != FolderId) throw new UserError(Damaged);
        long count = reader.Number();
        if (count > ZipExtract.MaxEntries) throw new UserError(Damaged);
        if (reader.Byte() != 0) throw new UserError(Damaged); // folders stored elsewhere
        var parsed = new List<(ulong[] Methods, byte[][] Properties, int[] Order, int Outputs)>();
        int packIndex = 0;
        for (int f = 0; f < count; f++) parsed.Add(ReadFolder(reader));
        if (reader.Byte() != CodersUnpackSize) throw new UserError(Damaged);
        var unpacked = parsed.Select(p => Enumerable.Range(0, p.Outputs).Select(_ => reader.Number()).ToArray()).ToArray();
        uint?[] crcs = new uint?[count];
        byte id = reader.Byte();
        if (id == Crc) { crcs = reader.Digests((int)count); id = reader.Byte(); }
        if (id != End) throw new UserError(Damaged);
        if (packSizes.Length != count) throw new UserError(packSizes.Length > count ? Damaged : "This 7z archive uses features this reader does not support and was not extracted.");

        // Packed streams follow one another from the pack position and must end before the header.
        long offset = SignatureBytes;
        if (packPosition > headerStart - SignatureBytes) throw new UserError(Damaged);
        offset += packPosition;
        var folders = new Folder[count];
        for (int f = 0; f < count; f++)
        {
            var (methods, properties, order, _) = parsed[f];
            long packSize = packSizes[packIndex++];
            if (packSize > headerStart - offset) throw new UserError(Damaged);
            var chain = order.Select(i => new Coder(methods[i], properties[i], unpacked[f][i])).ToArray();
            CheckChain(chain, packSize);
            folders[f] = new(offset, packSize, chain, chain[^1].Unpacked, crcs[f]);
            offset += packSize;
        }
        return folders;
    }

    // A folder is accepted only as a single chain: one packed stream → Copy, LZMA or LZMA2 →
    // optionally the x86 BCJ filter → the folder's output. Order lists coders packed side first.
    private static (ulong[] Methods, byte[][] Properties, int[] Order, int Outputs) ReadFolder(HeaderReader reader)
    {
        long coders = reader.Number();
        if (coders == 0) throw new UserError(Damaged);
        if (coders > MaxCoders) throw new UserError("This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.");
        var methods = new ulong[coders]; var properties = new byte[coders][];
        bool complex = false;
        for (int i = 0; i < coders; i++)
        {
            byte flags = reader.Byte();
            int idSize = flags & 0x0F;
            if ((flags & 0xC0) != 0 || idSize > 8) throw new UserError(Damaged);
            ulong method = 0;
            for (int b = 0; b < idSize; b++) method = method << 8 | reader.Byte();
            methods[i] = method;
            if ((flags & 0x10) != 0)
            {
                long inputs = reader.Number(), outputs = reader.Number();
                if (inputs != 1 || outputs != 1) complex = true;
                if (inputs > MaxCoders || outputs > MaxCoders) throw new UserError(Damaged);
            }
            properties[i] = (flags & 0x20) != 0 ? reader.Bytes(reader.Number(), 64) : Array.Empty<byte>();
        }
        if (methods.Contains(Aes)) throw new UserError("Encrypted 7z archives are not supported.");
        if (complex || methods.Any(m => m is not (Copy or Lzma or Lzma2 or BcjX86)))
            throw new UserError("This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.");
        int n = (int)coders;
        // Simple coders: input i and output i both belong to coder i.
        var source = new int[n]; Array.Fill(source, -1); // coder i reads coder source[i]'s output
        var used = new bool[n];
        for (int p = 0; p < n - 1; p++)
        {
            long input = reader.Number(), output = reader.Number();
            if (input >= n || output >= n || source[input] >= 0 || used[output] || input == output) throw new UserError(Damaged);
            source[input] = (int)output; used[output] = true;
        }
        // With one packed stream its index is implied: the input no bind pair feeds.
        int packed = Array.IndexOf(source, -1);
        int final = Array.IndexOf(used, false);
        var chain = new List<int>();
        for (int at = final; at >= 0 && chain.Count <= n; at = source[at]) chain.Add(at);
        if (chain.Count != n || chain[^1] != packed) throw new UserError(Damaged);
        chain.Reverse();
        return (methods, properties, chain.ToArray(), n);
    }

    private static void CheckChain(Coder[] chain, long packSize)
    {
        if (chain.Length > 2 || chain[0].Method == BcjX86 || chain.Length == 2 && chain[1].Method != BcjX86)
            throw new UserError("This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy.");
        foreach (var coder in chain)
        {
            bool fits = coder.Method switch
            {
                Copy or BcjX86 => coder.Properties.Length == 0,
                Lzma => coder.Properties.Length == 5,
                _ => coder.Properties.Length == 1,
            };
            if (!fits) throw new UserError(coder.Method == BcjX86 ? "This 7z archive uses a compression method other than LZMA, LZMA2, BCJ (x86) or Copy." : Damaged);
            if (coder.Unpacked > ZipExtract.MaxTotalBytes) throw new UserError("Archive extraction is limited to 2 GB of extracted data.");
            // Decoder memory is known now, so an archive that needs too much is refused before the review.
            if (coder.Method == Lzma)
            {
                if (coder.Properties[0] >= 9 * 5 * 5) throw new UserError(Damaged);
                LzmaStream.WindowFor(BinaryPrimitives.ReadUInt32LittleEndian(coder.Properties.AsSpan(1)), coder.Unpacked, MaxWindowBytes);
            }
            if (coder.Method == Lzma2)
            {
                if (coder.Properties[0] > 40) throw new UserError(Damaged);
                LzmaStream.WindowFor(Lzma2Stream.Dictionary(coder.Properties[0]), coder.Unpacked, MaxWindowBytes);
            }
        }
        if (chain[0].Method == Copy && chain[0].Unpacked != packSize) throw new UserError(Damaged);
        if (chain.Length == 2 && chain[1].Unpacked != chain[0].Unpacked) throw new UserError(Damaged);
    }

    private static (int[] Substreams, long[] Sizes, uint?[] Crcs) ReadSubstreams(HeaderReader reader, Folder[] folders)
    {
        var substreams = Enumerable.Repeat(1, folders.Length).ToArray();
        byte id = reader.Byte();
        if (id == NumUnpackStream)
        {
            long total = 0;
            for (int f = 0; f < folders.Length; f++)
            {
                long n = reader.Number();
                total += n;
                if (n == 0 || total > ZipExtract.MaxEntries) throw new UserError(n == 0 ? Damaged : "Archive extraction is limited to 10,000 entries.");
                substreams[f] = (int)n;
            }
            id = reader.Byte();
        }
        var sizes = new List<long>();
        bool listed = id == Size;
        for (int f = 0; f < folders.Length; f++)
        {
            long sum = 0;
            if (substreams[f] > 1 && !listed) throw new UserError(Damaged);
            for (int j = 0; j < substreams[f] - 1; j++)
            {
                long size = reader.Number();
                if (size > folders[f].Unpacked - sum) throw new UserError(Damaged);
                sizes.Add(size); sum += size;
            }
            sizes.Add(folders[f].Unpacked - sum);
        }
        if (listed) id = reader.Byte();
        var crcs = new uint?[sizes.Count];
        int unknown = 0;
        for (int f = 0; f < folders.Length; f++) if (!(substreams[f] == 1 && folders[f].Crc is not null)) unknown += substreams[f];
        uint?[] digests = new uint?[unknown];
        if (id == Crc) { digests = reader.Digests(unknown); id = reader.Byte(); }
        if (id != End) throw new UserError(Damaged);
        for (int f = 0, k = 0, d = 0; f < folders.Length; f++)
        {
            if (substreams[f] == 1 && folders[f].Crc is not null) { crcs[k++] = folders[f].Crc; continue; }
            for (int j = 0; j < substreams[f]; j++) crcs[k++] = digests[d++];
        }
        return (substreams, sizes.ToArray(), crcs);
    }

    /// Decodes a whole folder into memory (only used for the encoded header, at most 64 MB).
    private static byte[] DecodeAll(Stream archive, Folder folder, CancellationToken token)
    {
        var data = new byte[folder.Unpacked];
        try
        {
            // Opening may already read: LZMA starts its range decoder in the constructor.
            using var stream = Open(archive, folder, token);
            int at = 0;
            while (at < data.Length)
            {
                int read = stream.Read(data, at, data.Length - at);
                if (read == 0) throw new UserError(Damaged);
                at += read;
            }
        }
        catch (LzmaDataException) { throw new UserError(Damaged); }
        if ((ZipExtract.Crc32(0xFFFFFFFF, data) ^ 0xFFFFFFFF) != folder.Crc) throw new UserError(Damaged);
        return data;
    }

    internal static Stream Open(Stream archive, Folder folder, CancellationToken token)
    {
        Stream stream = new PackSlice(archive, folder.PackOffset, folder.PackSize, token);
        foreach (var coder in folder.Chain)
            stream = coder.Method switch
            {
                Copy => stream,
                Lzma => new LzmaStream(stream, coder.Properties, coder.Unpacked, MaxWindowBytes, token),
                Lzma2 => new Lzma2Stream(stream, coder.Properties, coder.Unpacked, MaxWindowBytes, token),
                _ => new BcjX86Stream(stream, coder.Unpacked, token),
            };
        return stream;
    }

    /// Opens the forward reader that extraction copies members from, in plan order.
    public static SevenZipSource OpenSource(Stream archive, SevenZipArchive reviewed) => new(archive, reviewed);

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length) throw new UserError(Damaged);
        stream.Position = offset; byte[] data = new byte[count]; stream.ReadExactly(data); return data;
    }

    /// Exactly one packed stream, read by absolute position so nothing else moves it.
    private sealed class PackSlice(Stream archive, long offset, long length, CancellationToken token) : Stream
    {
        private long done;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int at, int count)
        {
            token.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(count, length - done);
            if (wanted <= 0) return 0;
            archive.Position = offset + done;
            int read = archive.Read(buffer, at, wanted);
            if (read == 0) throw new UserError("This archive ends early or is damaged and was not extracted.");
            done += read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// Bounds-checked reader for header bytes. Every failure is "damaged".
    private sealed class HeaderReader(byte[] data)
    {
        private int limit = data.Length;
        public int Position { get; private set; }
        public byte Byte() => Position < limit ? data[Position++] : throw new UserError(Damaged);
        public long Number()
        {
            byte first = Byte(); ulong value = 0; int mask = 0x80;
            for (int i = 0; i < 8; i++, mask >>= 1)
            {
                if ((first & mask) == 0) { value |= (ulong)(first & (mask - 1)) << (8 * i); return Checked(value); }
                value |= (ulong)Byte() << (8 * i);
            }
            return Checked(value);
        }
        private static long Checked(ulong value) => value <= long.MaxValue / 2 ? (long)value : throw new UserError(Damaged);
        public uint UInt32() { var bytes = Bytes(4, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes); }
        public ulong UInt64() { var bytes = Bytes(8, 8); return BinaryPrimitives.ReadUInt64LittleEndian(bytes); }
        public byte[] Bytes(long count, long max)
        {
            if (count > max || count > limit - Position) throw new UserError(Damaged);
            var bytes = data.AsSpan(Position, (int)count).ToArray(); Position += (int)count; return bytes;
        }
        public void Skip(long count) { if (count > limit - Position) throw new UserError(Damaged); Position += (int)count; }
        /// A property of `size` bytes must fit in the header; returns where it ends.
        public int Limit(long size) { if (size > limit - Position) throw new UserError(Damaged); return Position + (int)size; }
        public void RequireEnd() { if (Position != data.Length) throw new UserError(Damaged); }
        public bool[] Bits(int count)
        {
            var bits = new bool[count]; int current = 0;
            for (int i = 0; i < count; i++)
            {
                if (i % 8 == 0) current = Byte();
                bits[i] = (current & (0x80 >> (i % 8))) != 0;
            }
            return bits;
        }
        public bool[] Defined(int count)
        {
            byte all = Byte();
            if (all > 1) throw new UserError(Damaged);
            return all == 1 ? Enumerable.Repeat(true, count).ToArray() : Bits(count);
        }
        public uint?[] Digests(int count)
        {
            var defined = Defined(count); var result = new uint?[count];
            for (int i = 0; i < count; i++) if (defined[i]) result[i] = UInt32();
            return result;
        }
        public string[] Names(int count, int end)
        {
            var names = new string[count];
            var decoder = new UnicodeEncoding(false, false, true);
            for (int i = 0; i < count; i++)
            {
                int at = Position;
                while (at + 1 < end && (data[at] | data[at + 1]) != 0) at += 2;
                if (at + 1 >= end) throw new UserError(Damaged);
                try { names[i] = decoder.GetString(data, Position, at - Position); }
                catch (DecoderFallbackException) { throw new UserError("This 7z archive contains invalid Unicode names."); }
                Position = at + 2;
            }
            return names;
        }
    }
}

/// Copies 7z members in plan order from one folder decoder at a time. Each file's size and
/// CRC-32 are checked, and at the end of a folder the folder's own CRC-32 when it has one.
public sealed class SevenZipSource : IDisposable
{
    private readonly Stream archive;
    private readonly SevenZipArchive reviewed;
    private Stream? decoder;
    private int folder = -1, next;
    private uint folderCrc;

    internal SevenZipSource(Stream archive, SevenZipArchive reviewed) { this.archive = archive; this.reviewed = reviewed; }

    public void Extract(ZipMember member, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!reviewed.Places.TryGetValue(member, out var place)) throw new ArgumentException("The member is not part of this archive.");
        if (place.Folder < 0) return; // an empty file
        if (place.Folder != folder)
        {
            if (place.Index != 0) throw new InvalidOperationException("7z members must be extracted in archive order.");
            decoder?.Dispose();
            folder = place.Folder; next = 0; folderCrc = 0xFFFFFFFF;
            try { decoder = SevenZipExtract.Open(archive, reviewed.Folders[folder], token); }
            catch (LzmaDataException) { throw new UserError(SevenZipExtract.DataDamaged); }
        }
        if (place.Index != next) throw new InvalidOperationException("7z members must be extracted in archive order.");
        next++;
        byte[] buffer = new byte[65536]; long written = 0; uint crc = 0xFFFFFFFF;
        try
        {
            while (written < member.Size)
            {
                token.ThrowIfCancellationRequested();
                int count = decoder!.Read(buffer, 0, (int)Math.Min(buffer.Length, member.Size - written));
                if (count == 0) throw new UserError(SevenZipExtract.DataDamaged);
                crc = ZipExtract.Crc32(crc, buffer.AsSpan(0, count)); folderCrc = ZipExtract.Crc32(folderCrc, buffer.AsSpan(0, count));
                output.Write(buffer, 0, count); written += count;
            }
        }
        catch (LzmaDataException) { throw new UserError(SevenZipExtract.DataDamaged); }
        if ((crc ^ 0xFFFFFFFF) != member.Crc) throw new UserError("A 7z entry failed its CRC check and was not extracted.");
        if (place.Last && reviewed.Folders[folder].Crc is uint expected && (folderCrc ^ 0xFFFFFFFF) != expected)
            throw new UserError("A 7z entry failed its CRC check and was not extracted.");
    }

    public void Dispose() => decoder?.Dispose();
}
