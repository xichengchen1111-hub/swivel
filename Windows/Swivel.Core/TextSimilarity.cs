using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record SimilarTextPair(string First, string Second, int CommonLines, int UnionLines)
{
    // Truncate, rather than round a near match up to 100%.
    public decimal Percent => (CommonLines * 10000L / UnionLines) / 100m;
}
public sealed record TextSimilarityResult(IReadOnlyList<SimilarTextPair> Pairs, IReadOnlyList<DuplicateIssue> Issues,
    int Candidates, int Attempted, int Loaded, int ComparedPairs, int MatchingPairs, long BytesRead,
    bool FileLimit, bool ByteLimit, bool LineLimit, bool TimeLimit);
public sealed record TextSimilarityLimits(int MaxFiles = 200, int MaxFileBytes = 1024 * 1024,
    int MaxReadBytes = 16 * 1024 * 1024, int MaxLines = 20000, int MaxRetainedLines = 200000, int MaxPairs = 1000, int Seconds = 60);

/// Read-only historical hints, never byte equality, transitive groups or deletion advice.
public static class TextSimilarity
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".log", ".srt" };
    private sealed class Item(DuplicateCandidate candidate)
    {
        public DuplicateCandidate Candidate { get; } = candidate;
        public HashSet<string>? Lines;
        public string? Issue;
    }
    private sealed class Changed : Exception;
    private sealed class Stopped : Exception;
    public static TextSimilarityResult Scan(IReadOnlyList<DuplicateCandidate> candidates,
        Func<DuplicateCandidate, IDuplicateInput> open, CancellationToken token,
        TextSimilarityLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(open);
        limits ??= new();
        if (candidates.Count > 20000 || limits.MaxFiles is < 1 or > 200 || limits.MaxFileBytes is < 1 or > 1024 * 1024 ||
            limits.MaxReadBytes is < 1 or > 16 * 1024 * 1024 || limits.MaxLines is < 5 or > 20000 ||
            limits.MaxRetainedLines is < 5 or > 200000 || limits.MaxPairs is < 1 or > 1000 || limits.Seconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested();
        var clock = System.Diagnostics.Stopwatch.StartNew(); elapsed ??= () => clock.Elapsed;
        var items = candidates.Select(c => new Item(c)).OrderBy(x => x.Candidate.RelativePath, StringComparer.Ordinal).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, (DuplicateStamp Stamp, Item Item)>(StringComparer.Ordinal);
        int attempted = 0, retained = 0, compared = 0, matching = 0;
        long readBytes = 0;
        bool fileLimit = false, byteLimit = false, lineLimit = false, timeLimit = false;
        void CheckTime()
        {
            token.ThrowIfCancellationRequested();
            if (elapsed().TotalSeconds >= limits.Seconds) timeLimit = true;
            if (timeLimit) throw new Stopped();
        }
        void Verify(IDuplicateInput input, DuplicateStamp stamp)
        {
            CheckTime();
            if (input.Refresh() != stamp || !input.Stream.CanRead || !input.Stream.CanSeek || input.Stream.Length != stamp.Length)
                throw new Changed();
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
            catch (Exception ex) when (ex is UserError or ArgumentException)
            { item.Issue = "System, link, cloud or invalid item"; continue; }
            if (!paths.Add(candidate.RelativePath)) { item.Issue = "Repeated file path"; continue; }
            if (!Extensions.Contains(Path.GetExtension(candidate.RelativePath))) { item.Issue = "Not a supported plain-text extension"; continue; }
            if (candidate.Length < 0) { item.Issue = "Unavailable or unsupported"; continue; }
            if (candidate.Length == 0) { item.Issue = "Empty file"; continue; }
            if (candidate.Length > limits.MaxFileBytes) { item.Issue = "Text file exceeds 1 MiB limit"; continue; }
            try
            {
                CheckTime();
                if (attempted == limits.MaxFiles) { fileLimit = true; item.Issue = "Text file attempt limit"; continue; }
                if (candidate.Length > limits.MaxReadBytes - readBytes) { byteLimit = true; item.Issue = "Text read budget limit"; continue; }
                attempted++;
                using var input = open(candidate);
                CheckTime(); var stamp = input.Stamp;
                if (string.IsNullOrEmpty(stamp.Identity) || stamp.Length != candidate.Length) throw new Changed();
                Verify(input, stamp);
                if (identities.TryGetValue(stamp.Identity, out var previous))
                {
                    if (previous.Stamp != stamp)
                    {
                        if (previous.Item.Lines is { } old) { retained -= old.Count; previous.Item.Lines = null; }
                        previous.Item.Issue = "File changed during comparison";
                        throw new Changed();
                    }
                    item.Issue = "Same file identity (hard link)"; continue;
                }
                identities.Add(stamp.Identity, (stamp, item));
                if (input.Stream.Seek(0, SeekOrigin.Begin) != 0) throw new Changed();
                byte[] bytes = new byte[(int)stamp.Length]; int at = 0;
                while (at < bytes.Length)
                {
                    CheckTime(); int requested = Math.Min(16384, bytes.Length - at);
                    readBytes += requested; // A throwing read keeps its conservative reservation.
                    int count = input.Stream.Read(bytes, at, requested);
                    if (count < 0 || count > requested) throw new IOException("Invalid read count.");
                    readBytes -= requested - count; CheckTime();
                    if (count == 0) throw new Changed(); at += count;
                }
                Verify(input, stamp);
                string text;
                try { using var data = new MemoryStream(bytes, false); text = DocumentText.Read(data, "txt", token).Text; }
                catch (UserError) { item.Issue = "Invalid UTF-8 or BOM-marked UTF-16 plain text"; continue; }
                CheckTime();
                var lines = new HashSet<string>(StringComparer.Ordinal);
                int start = 0, lineCount = 0;
                for (int end = 0; end <= text.Length; end++)
                {
                    if ((end & 1023) == 0) CheckTime();
                    if (end < text.Length && text[end] is not '\r' and not '\n') continue;
                    if (++lineCount > limits.MaxLines) { item.Issue = "Text line limit"; break; }
                    var line = text.AsSpan(start, end - start).Trim();
                    if (!line.IsEmpty) lines.Add(line.ToString());
                    if (lines.Count > limits.MaxRetainedLines - retained) { lineLimit = true; item.Issue = "Retained text line budget limit"; break; }
                    if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end++;
                    start = end + 1;
                }
                Verify(input, stamp);
                if (item.Issue is not null) continue;
                if (lines.Count < 5) { item.Issue = "Fewer than 5 distinct nonempty lines"; continue; }
                item.Lines = lines; retained += lines.Count;
            }
            catch (Stopped) { item.Issue = "Text comparison time limit"; }
            catch (Changed) { item.Issue = "File changed during comparison"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or UserError or NotSupportedException)
            { item.Issue = "Unavailable or unsupported"; }
            if (item.Issue is not null && item.Lines is { } rejected)
            { retained -= rejected.Count; item.Lines = null; }
        }
        var loaded = items.Where(x => x.Lines is not null && x.Issue is null).ToArray();
        var pairs = new List<SimilarTextPair>();
        try
        {
            for (int a = 0; a < loaded.Length; a++)
                for (int b = a + 1; b < loaded.Length; b++)
                {
                    CheckTime();
                    if (!Path.GetExtension(loaded[a].Candidate.RelativePath).Equals(Path.GetExtension(loaded[b].Candidate.RelativePath), StringComparison.OrdinalIgnoreCase)) continue;
                    var left = loaded[a].Lines!; var right = loaded[b].Lines!;
                    if (left.Count > right.Count) (left, right) = (right, left);
                    if (left.Count * 5 < right.Count * 4) { compared++; continue; }
                    int common = 0, visited = 0;
                    foreach (string line in left)
                    { if ((visited++ & 127) == 0) CheckTime(); if (right.Contains(line)) common++; }
                    CheckTime(); compared++;
                    int union = left.Count + right.Count - common;
                    if (common * 5 < union * 4) continue;
                    matching++;
                    if (pairs.Count < limits.MaxPairs) pairs.Add(new(loaded[a].Candidate.RelativePath, loaded[b].Candidate.RelativePath, common, union));
                }
        }
        catch (Stopped) { }
        token.ThrowIfCancellationRequested();
        return new(pairs.AsReadOnly(), Array.AsReadOnly(items.Where(x => x.Issue is not null).Select(x => new DuplicateIssue(x.Candidate.RelativePath, x.Issue!)).ToArray()),
            items.Length, attempted, loaded.Length, compared, matching, readBytes, fileLimit, byteLimit, lineLimit, timeLimit);
    }
}
