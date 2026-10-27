using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Swivel.Core;

public sealed record DuplicateCandidate(string RelativePath, long Length);
public sealed record DuplicateStamp(string Identity, long Length, long LastWrite);
public interface IDuplicateInput : IDisposable
{
    Stream Stream { get; }
    DuplicateStamp Stamp { get; }
    DuplicateStamp Refresh();
}
public sealed record DuplicateIssue(string RelativePath, string Reason);
public sealed record DuplicateGroup(long Length, IReadOnlyList<string> Paths, string Sha256);
public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<DuplicateIssue> Issues,
    int Candidates, int Compared, long BytesRead, bool ByteLimit, bool TimeLimit);
public sealed record DuplicateScanLimits(long MaxFileBytes = 512L * 1024 * 1024, long MaxReadBytes = 2L * 1024 * 1024 * 1024, int Seconds = 60);

public static class DuplicateFiles
{
    private const int Block = 64 * 1024;
    private sealed class Item(DuplicateCandidate candidate)
    {
        public DuplicateCandidate Candidate { get; } = candidate;
        public DuplicateStamp? Stamp;
        public string? Sample, Hash, Issue;
    }
    private sealed class Stopped(string reason) : Exception { public string Reason { get; } = reason; }
    private sealed class Changed : Exception;

    // Results describe bounded, read-only snapshots. Even a full matching hash is
    // not a deletion authorization: files may change after their lease is closed.
    public static DuplicateScanResult Scan(IReadOnlyList<DuplicateCandidate> candidates,
        Func<DuplicateCandidate, IDuplicateInput> open, CancellationToken token,
        DuplicateScanLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(open);
        limits ??= new();
        if (candidates.Count > 20000 || limits.MaxFileBytes is < 1 or > 512L * 1024 * 1024 ||
            limits.MaxReadBytes is < 1 or > 2L * 1024 * 1024 * 1024 || limits.Seconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var watch = System.Diagnostics.Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed;
        long bytesRead = 0; bool byteLimit = false, timeLimit = false;
        var items = candidates.Select(x => new Item(x)).OrderBy(x => x.Candidate.RelativePath, StringComparer.Ordinal).ToArray();
        var identities = new Dictionary<string, Item>(StringComparer.Ordinal);
        byte[] buffer = new byte[Block];
        void CheckTime()
        {
            token.ThrowIfCancellationRequested();
            if (elapsed().TotalSeconds >= limits.Seconds) timeLimit = true;
            if (timeLimit) throw new Stopped("Comparison time limit");
        }
        void Verify(IDuplicateInput input, DuplicateStamp stamp)
        {
            CheckTime();
            if (input.Refresh() != stamp || !input.Stream.CanRead || !input.Stream.CanSeek || input.Stream.Length != stamp.Length)
                throw new Changed();
            CheckTime();
        }
        void ReadRange(Stream stream, IncrementalHash hash, long offset, long count)
        {
            CheckTime();
            if (stream.Seek(offset, SeekOrigin.Begin) != offset) throw new Changed();
            CheckTime();
            while (count > 0)
            {
                CheckTime();
                long available = limits.MaxReadBytes - bytesRead;
                if (available == 0) { byteLimit = true; throw new Stopped("Comparison read limit"); }
                int requested = (int)Math.Min(Math.Min(Block, count), available);
                // Reserve the request before calling foreign/native stream code.
                // Short reads refund unused bytes. A throwing read conservatively
                // consumes its reservation because its actual I/O is unknown.
                bytesRead += requested;
                int read = stream.Read(buffer, 0, requested);
                if (read < 0 || read > requested) throw new IOException("Invalid stream read count.");
                bytesRead -= requested - read;
                CheckTime();
                if (read == 0) throw new Changed();
                hash.AppendData(buffer, 0, read); count -= read;
            }
        }
        void Read(Item item, bool full)
        {
            try
            {
                CheckTime();
                if (bytesRead == limits.MaxReadBytes) { byteLimit = true; throw new Stopped("Comparison read limit"); }
                using var input = open(item.Candidate);
                CheckTime();
                var stamp = input.Stamp;
                if (string.IsNullOrEmpty(stamp.Identity) || stamp.Length != item.Candidate.Length || (full && stamp != item.Stamp)) throw new Changed();
                Verify(input, stamp);
                if (!full)
                {
                    if (identities.TryGetValue(stamp.Identity, out var previous))
                    {
                        if (previous.Stamp != stamp)
                        {
                            previous.Issue = "File changed during comparison"; previous.Hash = previous.Sample = null;
                            throw new Changed();
                        }
                        item.Issue = "Same file identity (hard link)"; return;
                    }
                    item.Stamp = stamp; identities.Add(stamp.Identity, item);
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                if (full) ReadRange(input.Stream, hash, 0, stamp.Length);
                else
                {
                    long first = Math.Min(Block, stamp.Length), last = Math.Min(Block, stamp.Length - first);
                    ReadRange(input.Stream, hash, 0, first);
                    if (last > 0) ReadRange(input.Stream, hash, stamp.Length - last, last);
                }
                Verify(input, stamp);
                string digest = Convert.ToHexString(hash.GetHashAndReset());
                if (full) item.Hash = digest; else item.Sample = digest;
            }
            catch (Stopped ex) { item.Issue = ex.Reason; }
            catch (Changed) { item.Issue = "File changed during comparison"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or UserError or NotSupportedException)
            { item.Issue = "Unavailable or unsupported"; }
        }
        token.ThrowIfCancellationRequested();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            try
            {
                if (string.IsNullOrEmpty(item.Candidate.RelativePath) || item.Candidate.RelativePath.Length > 240) throw new ArgumentException("Invalid relative path.");
                foreach (string part in item.Candidate.RelativePath.Split('\\')) WindowsPathPolicy.ValidateName(part);
            }
            catch (Exception ex) when (ex is UserError or ArgumentException) { item.Issue = "System, link, cloud or invalid item"; continue; }
            if (!paths.Add(item.Candidate.RelativePath)) item.Issue = "Repeated file path";
            else if (item.Candidate.Length < 0) item.Issue = "Unavailable or unsupported";
            else if (item.Candidate.Length == 0) item.Issue = "Empty file";
            else if (item.Candidate.Length > limits.MaxFileBytes) item.Issue = "File exceeds comparison size limit";
        }
        foreach (var sameSize in items.Where(x => x.Issue is null).GroupBy(x => x.Candidate.Length).OrderBy(x => x.Key))
        {
            if (sameSize.Count() < 2) continue;
            foreach (var item in sameSize) Read(item, false);
        }
        foreach (var sample in items.Where(x => x.Issue is null && x.Sample is not null).GroupBy(x => (x.Candidate.Length, x.Sample)).OrderBy(x => x.Key.Length).ThenBy(x => x.Key.Sample, StringComparer.Ordinal))
        {
            if (sample.Count() < 2) continue;
            foreach (var item in sample) Read(item, true);
        }
        token.ThrowIfCancellationRequested();
        var groups = items.Where(x => x.Issue is null && x.Hash is not null)
            .GroupBy(x => (x.Candidate.Length, x.Hash)).Where(x => x.Count() >= 2)
            .Select(x => new DuplicateGroup(x.Key.Length, Array.AsReadOnly(x.Select(i => i.Candidate.RelativePath).ToArray()), x.Key.Hash!))
            .OrderByDescending(x => x.Length).ThenBy(x => x.Paths[0], StringComparer.Ordinal).ToArray();
        var issues = items.Where(x => x.Issue is not null).Select(x => new DuplicateIssue(x.Candidate.RelativePath, x.Issue!)).ToArray();
        return new(Array.AsReadOnly(groups), Array.AsReadOnly(issues), items.Length,
            items.Count(x => x.Issue is null && x.Hash is not null), bytesRead, byteLimit, timeLimit);
    }
}
