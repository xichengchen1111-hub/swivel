using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Swivel.Core;

public sealed record SimilarPhotoPair(string First, string Second, int DifferentBits)
{
    public decimal Percent => ((64 - DifferentBits) * 10000 / 64) / 100m;
}
public sealed record PhotoSimilarityResult(IReadOnlyList<SimilarPhotoPair> Pairs, IReadOnlyList<DuplicateIssue> Issues,
    int Candidates, int Attempted, int Loaded, int ComparedPairs, int MatchingPairs, long BytesRead,
    bool FileLimit, bool ByteLimit, bool TimeLimit)
{
    /// The identity, length and last write recorded for each compared file, by relative path.
    public IReadOnlyDictionary<string, DuplicateStamp> Stamps { get; init; } = new Dictionary<string, DuplicateStamp>();
}
public sealed record PhotoSimilarityLimits(int MaxFiles = 200, int MaxFileBytes = 16 * 1024 * 1024,
    int MaxReadBytes = 128 * 1024 * 1024, int MaxPairs = 1000, int Seconds = 60);

public static class PhotoSimilarity
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    // Deterministic integer box averages, not a learned model or semantic score.
    // Each source pixel belongs to exactly one of 9 x 8 integer-partitioned cells.
    public static ulong? Fingerprint(PixelImage image, CancellationToken token, Action? checkpoint = null)
    {
        token.ThrowIfCancellationRequested(); image.Validate();
        if (image.Width < 9 || image.Height < 8) return null;
        var cells = new int[72]; int minimum = 255, maximum = 0;
        for (int gy = 0; gy < 8; gy++)
            for (int gx = 0; gx < 9; gx++)
            {
                int x0 = gx * image.Width / 9, x1 = (gx + 1) * image.Width / 9;
                int y0 = gy * image.Height / 8, y1 = (gy + 1) * image.Height / 8;
                long sum = 0, count = (long)(x1 - x0) * (y1 - y0);
                for (int y = y0; y < y1; y++)
                {
                    token.ThrowIfCancellationRequested(); checkpoint?.Invoke();
                    for (int x = x0; x < x1; x++)
                    {
                        if ((x & 1023) == 0) { token.ThrowIfCancellationRequested(); checkpoint?.Invoke(); }
                        int at = (y * image.Width + x) * 4, alpha = image.Bgra[at + 3];
                        int Blend(int value) => (value * alpha + 255 * (255 - alpha) + 127) / 255;
                        int blue = Blend(image.Bgra[at]), green = Blend(image.Bgra[at + 1]), red = Blend(image.Bgra[at + 2]);
                        sum += (77 * red + 150 * green + 29 * blue + 128) >> 8;
                    }
                }
                int mean = (int)((sum + count / 2) / count);
                cells[gy * 9 + gx] = mean; minimum = Math.Min(minimum, mean); maximum = Math.Max(maximum, mean);
            }
        ulong bits = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (cells[y * 9 + x] > cells[y * 9 + x + 1]) bits |= 1UL << (y * 8 + x);
        token.ThrowIfCancellationRequested(); checkpoint?.Invoke();
        int set = BitOperations.PopCount(bits);
        // Flat/low-range and almost entirely one-directional grids collide easily.
        // This deliberate heuristic can omit real pictures, including gradients.
        return maximum - minimum < 16 || set < 4 || set > 60 ? null : bits;
    }

    private sealed class Item(DuplicateCandidate candidate)
    {
        public DuplicateCandidate Candidate { get; } = candidate;
        public ulong? Hash;
        public string? Issue;
    }
    private sealed class Changed : Exception;
    private sealed class Stopped : Exception;

    public static PhotoSimilarityResult Scan(IReadOnlyList<DuplicateCandidate> candidates,
        Func<DuplicateCandidate, IDuplicateInput> open,
        Func<byte[], string, CancellationToken, Action, ulong?> fingerprint,
        CancellationToken token, PhotoSimilarityLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(open); ArgumentNullException.ThrowIfNull(fingerprint);
        limits ??= new();
        if (candidates.Count > 20000 || limits.MaxFiles is < 1 or > 200 || limits.MaxFileBytes is < 1 or > 16 * 1024 * 1024 ||
            limits.MaxReadBytes is < 1 or > 128 * 1024 * 1024 || limits.MaxPairs is < 1 or > 1000 || limits.Seconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested();
        var clock = System.Diagnostics.Stopwatch.StartNew(); elapsed ??= () => clock.Elapsed;
        var items = candidates.Select(c => new Item(c)).OrderBy(x => x.Candidate.RelativePath, StringComparer.Ordinal).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, (DuplicateStamp Stamp, Item Item)>(StringComparer.Ordinal);
        int attempted = 0, compared = 0, matching = 0; long bytesRead = 0;
        bool fileLimit = false, byteLimit = false, timeLimit = false;
        void CheckTime()
        {
            token.ThrowIfCancellationRequested();
            if (elapsed().TotalSeconds >= limits.Seconds) timeLimit = true;
            if (timeLimit) throw new Stopped();
        }
        void Verify(IDuplicateInput input, DuplicateStamp stamp)
        {
            CheckTime();
            if (input.Refresh() != stamp || !input.Stream.CanRead || !input.Stream.CanSeek || input.Stream.Length != stamp.Length) throw new Changed();
            CheckTime();
        }
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); var candidate = item.Candidate;
            try
            {
                if (string.IsNullOrEmpty(candidate.RelativePath) || candidate.RelativePath.Length > 240) throw new ArgumentException();
                foreach (string part in candidate.RelativePath.Split('\\')) WindowsPathPolicy.ValidateName(part);
            }
            catch (Exception ex) when (ex is UserError or ArgumentException) { item.Issue = "System, link, cloud or invalid item"; continue; }
            if (!paths.Add(candidate.RelativePath)) { item.Issue = "Repeated file path"; continue; }
            if (!Extensions.Contains(Path.GetExtension(candidate.RelativePath))) { item.Issue = "Not a supported image extension"; continue; }
            if (candidate.Length < 0) { item.Issue = "Unavailable or unsupported"; continue; }
            if (candidate.Length == 0) { item.Issue = "Empty file"; continue; }
            if (candidate.Length > limits.MaxFileBytes) { item.Issue = "Image file exceeds 16 MiB similarity limit"; continue; }
            try
            {
                CheckTime();
                if (attempted == limits.MaxFiles) { fileLimit = true; item.Issue = "Image file attempt limit"; continue; }
                if (candidate.Length > limits.MaxReadBytes - bytesRead) { byteLimit = true; item.Issue = "Image read budget limit"; continue; }
                attempted++; using var input = open(candidate);
                CheckTime(); var stamp = input.Stamp;
                if (string.IsNullOrEmpty(stamp.Identity) || stamp.Length != candidate.Length) throw new Changed();
                Verify(input, stamp);
                if (identities.TryGetValue(stamp.Identity, out var previous))
                {
                    if (previous.Stamp != stamp) { previous.Item.Hash = null; previous.Item.Issue = "File changed during comparison"; throw new Changed(); }
                    item.Issue = "Same file identity (hard link)"; continue;
                }
                identities.Add(stamp.Identity, (stamp, item));
                if (input.Stream.Seek(0, SeekOrigin.Begin) != 0) throw new Changed();
                byte[] bytes = new byte[(int)stamp.Length]; int at = 0;
                while (at < bytes.Length)
                {
                    CheckTime(); int requested = Math.Min(16384, bytes.Length - at); bytesRead += requested;
                    int count = input.Stream.Read(bytes, at, requested);
                    if (count < 0 || count > requested) throw new IOException("Invalid read count.");
                    bytesRead -= requested - count; CheckTime();
                    if (count == 0) throw new Changed(); at += count;
                }
                Verify(input, stamp);
                // Decoder can seek only in this memory snapshot. The source/root
                // leases remain held until decoding, hashing and final recheck end.
                var hash = fingerprint(bytes, candidate.RelativePath, token, CheckTime);
                Verify(input, stamp);
                if (hash is null) item.Issue = "Image is too small or lacks supported detail";
                else item.Hash = hash;
            }
            catch (Stopped) { item.Issue = "Image comparison time limit"; }
            catch (Changed) { item.Issue = "File changed during comparison"; }
            catch (UserError ex) { item.Issue = ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException or COMException or ArgumentException or FormatException)
            { item.Issue = "Unavailable or unsupported"; }
            if (item.Issue is not null) item.Hash = null;
        }
        var loaded = items.Where(x => x.Hash is not null && x.Issue is null).ToArray(); var pairs = new List<SimilarPhotoPair>();
        try
        {
            for (int a = 0; a < loaded.Length; a++)
                for (int b = a + 1; b < loaded.Length; b++)
                {
                    CheckTime(); int different = BitOperations.PopCount(loaded[a].Hash!.Value ^ loaded[b].Hash!.Value);
                    CheckTime(); compared++;
                    if (different > 6) continue;
                    matching++;
                    if (pairs.Count < limits.MaxPairs) pairs.Add(new(loaded[a].Candidate.RelativePath, loaded[b].Candidate.RelativePath, different));
                }
        }
        catch (Stopped) { }
        token.ThrowIfCancellationRequested();
        return new(pairs.AsReadOnly(), Array.AsReadOnly(items.Where(x => x.Issue is not null).Select(x => new DuplicateIssue(x.Candidate.RelativePath, x.Issue!)).ToArray()),
            items.Length, attempted, loaded.Length, compared, matching, bytesRead, fileLimit, byteLimit, timeLimit)
        { Stamps = identities.Values.Where(x => x.Item.Hash is not null && x.Item.Issue is null).ToDictionary(x => x.Item.Candidate.RelativePath, x => x.Stamp, StringComparer.Ordinal) };
    }
}
