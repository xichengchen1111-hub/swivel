using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum ArchiveFormat { Zip, Tar, GZip }
public sealed record ArchiveSource(string Name, Stream Stream);

/// Creation only. No source paths reach an archive API, and no archive is extracted.
public static class Archives
{
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;
    public static void Validate(IReadOnlyList<ArchiveSource> sources, ArchiveFormat format)
    {
        if (!Enum.IsDefined(format) || sources.Count is < 1 or > 100 || (format == ArchiveFormat.GZip && sources.Count != 1))
            throw new UserError("Choose 1–100 files for ZIP or TAR, or one file for GZip.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var source in sources)
        {
            WindowsPathPolicy.ValidateName(source.Name);
            if (!names.Add(source.Name)) throw new UserError("Archive entries must have different file names. Rename duplicate names first.");
            if (!source.Stream.CanRead || !source.Stream.CanSeek) throw new ArgumentException("A held seekable input is required.");
            long length = source.Stream.Length;
            if (length < 0 || length > MaxBytes - total) throw new UserError("An archive batch is limited to 2 GB of source data.");
            total += length;
        }
    }
    public static void Create(IReadOnlyList<ArchiveSource> sources, ArchiveFormat format, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Validate(sources, format);
        // Cancellation is checked on every bounded read, including TAR's internal copy.
        var streams = sources.Select(x => new CancelReadStream(x.Stream, token)).ToArray();
        foreach (var source in streams) source.Position = 0;
        if (format == ArchiveFormat.Zip)
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
            for (int i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(sources[i].Name, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var destination = entry.Open(); streams[i].CopyTo(destination, 65536);
            }
        }
        else if (format == ArchiveFormat.Tar)
        {
            using var tar = new TarWriter(output, TarEntryFormat.Pax, true);
            for (int i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = new PaxTarEntry(TarEntryType.RegularFile, sources[i].Name) { DataStream = streams[i], ModificationTime = DateTimeOffset.UnixEpoch };
                tar.WriteEntry(entry);
            }
        }
        else
        {
            using var gzip = new GZipStream(output, CompressionLevel.Fastest, true);
            streams[0].CopyTo(gzip, 65536);
        }
        token.ThrowIfCancellationRequested();
    }
    private sealed class CancelReadStream(Stream inner, CancellationToken token) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, Math.Min(count, 65536)); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return inner.Read(buffer[..Math.Min(buffer.Length, 65536)]); }
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // The surrounding operation owns and releases the input leases.
        protected override void Dispose(bool disposing) { }
    }
}
