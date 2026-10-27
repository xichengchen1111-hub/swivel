using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Swivel.Core;

internal static class DuplicateFileChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        DuplicateCandidate[] Pair(long length) => new[] { new DuplicateCandidate("b.bin", length), new DuplicateCandidate("a.bin", length) };
        foreach (int length in new[] { 1, 65535, 65536, 65537, 131071, 131072, 131073, 200003 })
        {
            byte[] data = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + i / 251)).ToArray();
            var source = new Source(c => new(data, c.RelativePath));
            var result = DuplicateFiles.Scan(Pair(length), source.Open, default);
            check(result.Groups.Count == 1 && result.Groups[0].Paths.SequenceEqual(new[] { "a.bin", "b.bin" }) && result.Groups[0].Sha256 == Convert.ToHexString(SHA256.HashData(data)), "Duplicate boundary files match independent full SHA256 with deterministic paths");
            check(result.BytesRead == 2 * (Math.Min(length, 131072) + length) && result.Compared == 2 && result.Candidates == 2 && result.Issues.Count == 0, "Sample boundary reads first/last 64KiB without overlap, then complete contents");
            check(source.Opened == 4 && source.Disposed == 4 && source.MaxActive == 1, "Both stages reopen; at most one file lease is active and all are released");
        }
        var shortSource = new Source(c => new(new byte[140001], c.RelativePath) { MaxRead = 7 });
        var scan = DuplicateFiles.Scan(Pair(140001), shortSource.Open, default);
        check(scan.Groups.Count == 1 && scan.BytesRead == 2 * (131072 + 140001) && shortSource.Disposed == 4, "Partial stream reads are accumulated without hashing stale buffer bytes");
        byte[] same = new byte[196608], middle = new byte[196608]; middle[90000] = 1;
        var collision = new Source(c => new(c.RelativePath == "middle" ? middle : same, c.RelativePath));
        scan = DuplicateFiles.Scan(new[] { new DuplicateCandidate("same-b", same.Length), new("middle", middle.Length), new("same-a", same.Length) }, collision.Open, default);
        check(scan.Groups.Count == 1 && scan.Groups[0].Paths.SequenceEqual(new[] { "same-a", "same-b" }) && scan.Compared == 3 && collision.Opened == 6, "Identical first/last samples still require full SHA256; changed middle is excluded");
        var distinctSamples = new Source(c => new(c.RelativePath == "a.bin" ? new byte[2] : new byte[] { 0, 1 }, c.RelativePath));
        scan = DuplicateFiles.Scan(Pair(2), distinctSamples.Open, default);
        check(scan.Groups.Count == 0 && scan.Compared == 0 && scan.BytesRead == 4 && distinctSamples.Opened == 2, "Different sample fingerprints avoid unnecessary full file reads");
        int never = 0;
        scan = DuplicateFiles.Scan(new[] { new DuplicateCandidate("zero", 0), new("huge", 512L * 1024 * 1024 + 1), new("invalid", -1), new("unique", 99) }, c => { never++; throw new Exception("Unexpected open"); }, default);
        check(never == 0 && scan.Candidates == 4 && scan.Issues.Count == 3 && scan.Issues.Any(x => x.Reason == "Empty file") && scan.Issues.Any(x => x.Reason == "File exceeds comparison size limit"), "Empty, oversized and invalid files have reasons; unique sizes do not open contents");
        var maximum = new Source(c => new(new byte[1], c.RelativePath) { ReportedLength = c.Length });
        scan = DuplicateFiles.Scan(Pair(512L * 1024 * 1024), maximum.Open, default, new(MaxReadBytes: 1));
        check(scan.ByteLimit && scan.BytesRead == 1 && !scan.Issues.Any(x => x.Reason == "File exceeds comparison size limit"), "Exactly 512MiB files remain eligible; the separate content-read budget still applies");
        var hardLinks = new Source(c => new(new byte[9], c.RelativePath is "a" or "b" ? "hard-link" : c.RelativePath));
        var three = new[] { new DuplicateCandidate("c", 9), new("b", 9), new("a", 9) };
        scan = DuplicateFiles.Scan(three, hardLinks.Open, default);
        check(scan.Groups.Count == 1 && scan.Groups[0].Paths.SequenceEqual(new[] { "a", "c" }) && scan.Issues.Single().RelativePath == "b" && scan.Issues[0].Reason == "Same file identity (hard link)" && scan.Compared == 2 && scan.BytesRead == 36, "Hard links are not counted as independent copies and retain a visible reason");
        var onlyLinks = new Source(c => new(new byte[9], "same-id"));
        scan = DuplicateFiles.Scan(Pair(9), onlyLinks.Open, default);
        check(scan.Groups.Count == 0 && scan.Compared == 0 && scan.Issues.Count == 1 && onlyLinks.Opened == 2, "A file and its hard link alone do not make a duplicate group");
        var changedAlias = new Source(c => new(new byte[9], c.RelativePath is "a" or "b" ? "changing-id" : c.RelativePath) { InitialWrite = c.RelativePath == "b" ? 2 : 1 });
        scan = DuplicateFiles.Scan(three, changedAlias.Open, default);
        check(scan.Groups.Count == 0 && scan.Issues.Count(x => x.Reason == "File changed during comparison") == 2 && scan.Compared == 0, "A changed identity seen through an alias invalidates its earlier sampled owner too");
        foreach (string changed in new[] { "identity", "length", "time" })
        {
            var source = new Source(c => new(new byte[9], c.RelativePath));
            source.Configure = (input, c, visit) =>
            {
                if (c.RelativePath == "a.bin" && visit == 2)
                    input.Initial = changed switch { "identity" => input.Initial with { Identity = "replacement" }, "length" => input.Initial with { Length = 8 }, _ => input.Initial with { LastWrite = 2 } };
            };
            scan = DuplicateFiles.Scan(Pair(9), source.Open, default);
            check(scan.Groups.Count == 0 && scan.Compared == 1 && scan.Issues.Single().Reason == "File changed during comparison" && source.Disposed == 4, "Identity/length/time change between sample and full hash refuses stale candidate: " + changed);
        }
        foreach (int refresh in new[] { 1, 2, 3, 4 })
        {
            var source = new Source(c => new(new byte[9], c.RelativePath));
            source.Configure = (input, c, visit) => { if (c.RelativePath == "a.bin") input.Refreshed = count => count + (visit - 1) * 2 == refresh ? input.Initial with { LastWrite = 7 } : input.Initial; };
            scan = DuplicateFiles.Scan(Pair(9), source.Open, default);
            check(scan.Groups.Count == 0 && scan.Issues.Any(x => x.RelativePath == "a.bin" && x.Reason == "File changed during comparison") && source.Active == 0, "Stamp changes are rejected at each before/after phase boundary: " + refresh);
        }
        var wrongLength = new Source(c => new(new byte[8], c.RelativePath) { ReportedLength = 9 });
        scan = DuplicateFiles.Scan(Pair(9), wrongLength.Open, default);
        check(scan.Issues.Count == 2 && scan.Issues.All(x => x.Reason == "File changed during comparison") && scan.Groups.Count == 0, "Early EOF with unchanged claimed length never produces a partial hash match");
        var zeroRead = new Source(c => new(new byte[9], c.RelativePath) { ZeroAtRead = 1 });
        scan = DuplicateFiles.Scan(Pair(9), zeroRead.Open, default);
        check(scan.Groups.Count == 0 && scan.Issues.Count == 2 && scan.BytesRead == 0, "Zero byte reads before expected EOF are not treated as completed files");
        var wrongSeek = new Source(c => new(new byte[9], c.RelativePath) { WrongSeek = true });
        scan = DuplicateFiles.Scan(Pair(9), wrongSeek.Open, default);
        check(scan.Groups.Count == 0 && scan.BytesRead == 0 && wrongSeek.Disposed == 2, "A stream that cannot seek to the requested offset cannot contribute a hash");
        foreach (int invalidCount in new[] { -1, 10, int.MaxValue })
        {
            var invalidRead = new Source(c => new(new byte[9], c.RelativePath) { InvalidReadCount = invalidCount });
            scan = DuplicateFiles.Scan(Pair(9), invalidRead.Open, default);
            check(scan.Groups.Count == 0 && scan.BytesRead == 18 && scan.Issues.All(x => x.Reason == "Unavailable or unsupported") && invalidRead.Active == 0, "Invalid native/foreign stream counts cannot corrupt accounting or hash stale data");
        }
        foreach (Exception error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new Win32Exception(5), new UserError("Unavailable or unsupported"), new NotSupportedException() })
        {
            var source = new Source(c => new(new byte[9], c.RelativePath));
            scan = DuplicateFiles.Scan(three, c => c.RelativePath == "b" ? throw error : source.Open(c), default);
            check(scan.Groups.Count == 1 && scan.Groups[0].Paths.SequenceEqual(new[] { "a", "c" }) && scan.Issues.Single().Reason == "Unavailable or unsupported" && source.Active == 0, "Expected individual open failure preserves other complete duplicate matches");
        }
        reject(() => DuplicateFiles.Scan(Pair(9), c => throw new InvalidOperationException("Programming error"), default), "Unexpected programming errors propagate instead of being shown as inaccessible files");
        var failure = new Source(c => new(new byte[9], c.RelativePath) { ErrorAtRead = 1 });
        scan = DuplicateFiles.Scan(Pair(9), failure.Open, default, new(MaxReadBytes: 10));
        check(scan.BytesRead == 10 && scan.Groups.Count == 0 && failure.Requests.Sum() == 10 && failure.Active == 0, "Failed reads conservatively consume reservations so repeated I/O failures cannot escape byte budget");
        foreach (long budget in new long[] { 1, 8, 9, 10, 17, 18, 19, 26, 27, 28, 35, 36, 37 })
        {
            var source = new Source(c => new(new byte[9], c.RelativePath) { MaxRead = 4 });
            scan = DuplicateFiles.Scan(Pair(9), source.Open, default, new(MaxReadBytes: budget));
            bool complete = budget >= 36;
            check(scan.BytesRead == Math.Min(36, budget) && scan.BytesRead <= budget && (scan.Groups.Count == 1) == complete && scan.ByteLimit == !complete && source.Active == 0, "Byte boundary is enforced without an off-by-one read or incomplete group: " + budget);
            if (!complete) check(scan.Issues.Any(x => x.Reason == "Comparison read limit"), "Every incomplete budget-limited comparison has a visible reason");
        }
        var preserved = new Source(c => new(new byte[c.Length], c.RelativePath));
        scan = DuplicateFiles.Scan(Pair(9).Concat(new[] { new DuplicateCandidate("c", 10), new("d", 10) }).ToArray(), preserved.Open, default, new(MaxReadBytes: 56));
        check(scan.Groups.Count == 1 && scan.Groups[0].Length == 9 && scan.Compared == 2 && scan.ByteLimit && scan.Issues.Count == 2, "A later full comparison budget failure preserves already fully checked groups");
        var noTime = new Source(c => new(new byte[9], c.RelativePath));
        scan = DuplicateFiles.Scan(Pair(9), noTime.Open, default, elapsed: () => TimeSpan.FromSeconds(60));
        check(scan.TimeLimit && scan.Issues.Count == 2 && noTime.Opened == 0 && scan.BytesRead == 0, "Expired soft deadline prevents native file opens");
        TimeSpan time = TimeSpan.Zero;
        var slowOpen = new Source(c => { time = TimeSpan.FromSeconds(60); return new(new byte[9], c.RelativePath); });
        scan = DuplicateFiles.Scan(Pair(9), slowOpen.Open, default, elapsed: () => time);
        check(scan.TimeLimit && slowOpen.Opened == 1 && slowOpen.Disposed == 1 && scan.BytesRead == 0, "Deadline is checked again after a blocked open returns");
        time = TimeSpan.Zero;
        var slowRead = new Source(c => new(new byte[9], c.RelativePath) { OnRead = () => time = TimeSpan.FromSeconds(60) });
        scan = DuplicateFiles.Scan(Pair(9), slowRead.Open, default, elapsed: () => time);
        check(scan.TimeLimit && scan.Groups.Count == 0 && scan.BytesRead == 9 && slowRead.Opened == 1 && slowRead.Disposed == 1, "A read returning after the deadline counts I/O but never publishes its incomplete hash");
        using (var cancel = new CancellationTokenSource())
        {
            var source = new Source(c => new(new byte[9], c.RelativePath) { OnRead = cancel.Cancel });
            bool cancelled = false;
            try { DuplicateFiles.Scan(Pair(9), source.Open, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && source.Disposed == 1 && source.Active == 0, "Cancellation after read propagates and closes the current source lease");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var source = new Source(c => { cancel.Cancel(); return new(new byte[9], c.RelativePath); });
            bool cancelled = false;
            try { DuplicateFiles.Scan(Pair(9), source.Open, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && source.Disposed == 1 && source.Active == 0, "Cancellation during native open closes the returned lease before propagating");
        }
        reject(() => DuplicateFiles.Scan(Pair(9), noTime.Open, new CancellationToken(true)), "Cancellation before comparison propagates");
        foreach (var limits in new[] { new DuplicateScanLimits(MaxFileBytes: 0), new(MaxFileBytes: 512L * 1024 * 1024 + 1), new(MaxReadBytes: 0), new(MaxReadBytes: 2L * 1024 * 1024 * 1024 + 1), new(Seconds: 0), new(Seconds: 61) })
            reject(() => DuplicateFiles.Scan(Pair(9), noTime.Open, default, limits), "Invalid duplicate comparison limits refused");
        reject(() => DuplicateFiles.Scan(Enumerable.Range(0, 20001).Select(i => new DuplicateCandidate("f" + i, i)).ToArray(), noTime.Open, default), "Comparison refuses more than 20000 candidate paths");
        scan = DuplicateFiles.Scan(Array.Empty<DuplicateCandidate>(), noTime.Open, default);
        check(scan.Candidates == 0 && scan.Compared == 0 && scan.BytesRead == 0 && scan.Groups.Count == 0 && scan.Issues.Count == 0, "Empty candidate input yields an empty explicit result");
        foreach (string path in new[] { "", "..\\file", ".\\file", "C:\\file", "\\file", "dir/file", "dir\\\\file", "file:stream", "CON", "hidden\u202efile", new string('x', 241) })
        {
            scan = DuplicateFiles.Scan(new[] { new DuplicateCandidate(path, 9), new("valid", 9) }, c => throw new Exception("Unsafe path must not open"), default);
            check(scan.Issues.Single().Reason == "System, link, cloud or invalid item", "Malformed relative candidate paths are rejected before opening: " + path);
        }
        scan = DuplicateFiles.Scan(new[] { new DuplicateCandidate("same", 9), new("same", 9) }, c => throw new Exception("Repeated path must not open"), default);
        check(scan.Groups.Count == 0 && scan.Issues.Single().Reason == "Repeated file path", "Repeated ordinal paths cannot count as independent copies");
        var prefs = new Prefs();
        check(!Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.duplicates"), "Duplicate file comparison is independently opt-in through PC Doctor");
        prefs.Set(Feature.Doctor, true);
        check(Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.duplicates" && !x.RequiresFiles && x.Feature == Feature.Doctor), "Explicit duplicate folder picker is available without supplied files");
    }

    private sealed class Source(Func<DuplicateCandidate, MemoryInput> create)
    {
        public int Active, MaxActive, Opened, Disposed;
        public List<int> Requests = new();
        private readonly Dictionary<string, int> visits = new(StringComparer.Ordinal);
        public Action<MemoryInput, DuplicateCandidate, int>? Configure;
        public IDuplicateInput Open(DuplicateCandidate candidate)
        {
            var input = create(candidate); int visit = visits.GetValueOrDefault(candidate.RelativePath) + 1; visits[candidate.RelativePath] = visit;
            input.Initial = new(input.Identity, input.ReportedLength ?? input.Data.Length, input.InitialWrite);
            Configure?.Invoke(input, candidate, visit);
            input.ReadRequested = request => Requests.Add(request);
            input.Closed = () => { Active--; Disposed++; };
            Opened++; Active++; MaxActive = Math.Max(MaxActive, Active); return input;
        }
    }
    private sealed class MemoryInput(byte[] data, string identity) : Stream, IDuplicateInput
    {
        public byte[] Data = data;
        public string Identity = identity;
        public long InitialWrite = 1;
        public long? ReportedLength;
        public DuplicateStamp Initial = new(identity, data.Length, 1);
        public Func<int, DuplicateStamp>? Refreshed;
        public int MaxRead = int.MaxValue, ZeroAtRead, ErrorAtRead;
        public int? InvalidReadCount;
        public bool WrongSeek;
        public Action? OnRead, Closed;
        public Action<int>? ReadRequested;
        private int reads, refreshes;
        private bool disposed;
        private long position;
        public Stream Stream => this;
        public DuplicateStamp Stamp => Initial;
        public DuplicateStamp Refresh() => Refreshed?.Invoke(++refreshes) ?? Initial;
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length => ReportedLength ?? Data.Length;
        public override long Position { get => position; set => position = value; }
        public override long Seek(long offset, SeekOrigin origin) { if (origin != SeekOrigin.Begin) throw new NotSupportedException(); position = offset; return WrongSeek ? offset + 1 : position; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this); reads++; ReadRequested?.Invoke(count); OnRead?.Invoke();
            if (reads == ErrorAtRead) throw new IOException("Injected read failure");
            if (InvalidReadCount is int invalid) return invalid;
            if (reads == ZeroAtRead) return 0;
            int read = (int)Math.Min(Math.Min(MaxRead, count), Math.Max(0, Data.Length - position));
            Array.Copy(Data, position, buffer, offset, read); position += read; return read;
        }
        protected override void Dispose(bool disposing) { if (!disposed) { disposed = true; Closed?.Invoke(); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
