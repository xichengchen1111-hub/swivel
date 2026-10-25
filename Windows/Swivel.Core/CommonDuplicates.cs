using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace Swivel.Core;

public interface ICommonDuplicateRoot : IFolderMetadata
{
    IDuplicateInput OpenDuplicate(DuplicateCandidate candidate);
}
public sealed record CommonDuplicateLimits(int MetadataEntries = 20000, int Depth = 4, int MetadataSeconds = 30, DuplicateScanLimits? Content = null);
public sealed record CommonDuplicateFileRef(CommonFolderKind Kind, string RelativePath, long Length);
public sealed record CommonDuplicateRootResult(CommonFolderChoice Choice, FolderScanResult? Scan, string? Issue, uint? Error,
    int EntryQuota, int DeadlineSeconds, DateTimeOffset Recorded);
public sealed record CommonDuplicateResult(IReadOnlyList<CommonDuplicateRootResult> Roots, DuplicateScanResult Duplicates,
    IReadOnlyDictionary<string, CommonDuplicateFileRef> Sources);

public static class CommonDuplicates
{
    public const string Scope = "Select one to six Windows-reported common folders for one exact-content comparison across them. Nothing is selected initially. Reviewing locations does not read child files; the next confirmation lists every selected path before content reading. Existing local path, system, link and cloud protections remain in force. No deletion, opening files, permission change or assistant upload is performed.";
    public const string Limits = "One shared metadata budget: 20,000 entries and about 30 soft seconds, at most four levels below each root. Remaining entry/time shares are divided among remaining distinct paths; unused shares pass to later roots, without rescanning earlier partial roots. A scan that fails before returning a count conservatively consumes its reserved entry share. One shared content budget: 512 MiB per file, 2 GiB requested reads including samples and full hashes, and about 60 soft seconds. Blocked system calls may return later; cancellation waits for cleanup. Partial, failed, repeated and unattempted roots remain visible.";
    public const string Snapshot = "Roots can overlap or be on different local volumes. Exact repeated paths are skipped; case variants may be scanned separately. The original full volume/file identity deduplicates hard links and overlapping roots. Matching SHA-256 describes historical main-stream contents, not a simultaneous snapshot, current proof or deletion advice. Alternate streams, permissions and metadata are excluded. Root logical bytes must not be added as profile usage, physical usage or reclaimable space.";
    public const string Privacy = "Names and full paths are visible. Copy Report includes all roots, matching groups and recorded issues. Copy Selected Group includes its paths and all root limits. Copy Selected Path includes only that path. Copies exceeding 4 Mi UTF-16 units are refused in full; choose a smaller group or one path. Nothing is copied, saved or sent automatically.";
    public const string RepeatedRoot = "Same reported path as an earlier selected folder; not compared again.";
    public const string NoEntries = "Shared metadata entry budget exhausted; this root was not opened.";
    public const string NoTime = "Shared metadata time budget exhausted; this root was not opened.";
    public const string Unavailable = "This reported folder could not be compared.";

    // Hold all successfully opened roots through the ONE content scan. This only
    // coordinates existing readers; it does not change their native safety policy.
    public static CommonDuplicateResult Scan(IReadOnlyList<CommonFolderChoice> selection,
        Func<CommonFolderChoice, ICommonDuplicateRoot> open, CancellationToken token,
        CommonDuplicateLimits? limits = null, Func<TimeSpan>? metadataElapsed = null, Func<TimeSpan>? contentElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(open);
        var choices = selection.ToArray(); limits ??= new(); var contentLimits = limits.Content ?? new();
        if (choices.Length is < 1 or > 6 || choices.Any(c => c is null || !c.CanAttempt) || choices.Select(c => c.Kind).Distinct().Count() != choices.Length)
            throw new ArgumentException("Invalid common-folder comparison selection.", nameof(selection));
        if (limits.MetadataEntries is < 1 or > 20000 || limits.Depth is < 0 or > 4 || limits.MetadataSeconds is < 1 or > 30 ||
            contentLimits.MaxFileBytes is < 1 or > 512L * 1024 * 1024 || contentLimits.MaxReadBytes is < 1 or > 2L * 1024 * 1024 * 1024 || contentLimits.Seconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew(); metadataElapsed ??= () => watch.Elapsed;
        var held = new Dictionary<CommonFolderKind, ICommonDuplicateRoot>(); var leases = new List<ICommonDuplicateRoot>(6);
        var cleanupErrors = new List<Exception>(6);
        var roots = new List<CommonDuplicateRootResult>(); var candidates = new List<DuplicateCandidate>();
        var sources = new Dictionary<string, CommonDuplicateFileRef>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); int remainingRoots = choices.Select(c => c.Path!).Distinct(StringComparer.Ordinal).Count(), remainingEntries = limits.MetadataEntries;
        DuplicateScanResult duplicates;
        try
        {
            foreach (var choice in choices)
            {
                token.ThrowIfCancellationRequested(); int quota = 0, deadline = 0;
                void Record(FolderScanResult? scan, string? issue = null, uint? error = null) => roots.Add(new(choice, scan, issue, error, quota, deadline, DateTimeOffset.Now));
                if (!seen.Add(choice.Path!)) { Record(null, RepeatedRoot); continue; }
                double seconds = metadataElapsed().TotalSeconds;
                if (remainingEntries == 0) { remainingRoots--; Record(null, NoEntries); continue; }
                if (seconds >= limits.MetadataSeconds) { remainingRoots--; Record(null, NoTime); continue; }
                quota = Math.Max(1, remainingEntries / remainingRoots);
                deadline = Math.Clamp((int)Math.Ceiling(seconds + (limits.MetadataSeconds - seconds) / remainingRoots), 1, limits.MetadataSeconds); remainingRoots--;
                try
                {
                    var root = open(choice); leases.Add(root); held.Add(choice.Kind, root); token.ThrowIfCancellationRequested();
                    // An expected exception can escape before FolderSizes returns
                    // its count. Reserve first; only a completed scan refunds its
                    // unused share. Failed opening has consumed no entry quota.
                    remainingEntries -= quota;
                    var scan = FolderSizes.Scan(root, token, new(quota, limits.Depth, deadline), metadataElapsed);
                    remainingEntries += quota - scan.Entries; Record(scan);
                    void Add(FolderSizeNode node, string prefix)
                    {
                        foreach (var child in node.Children)
                        {
                            token.ThrowIfCancellationRequested(); string relative = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                            if (child.Directory) Add(child, relative);
                            else if (child.Issue is null && child.Files == 1)
                            {
                                // A two-character opaque namespace, never a localized
                                // folder label or a path interpreted by native I/O.
                                string key = ((char)('0' + (int)choice.Kind)).ToString() + "\\" + relative;
                                long length = (long)child.Bytes; sources.TryAdd(key, new(choice.Kind, relative, length)); candidates.Add(new(key, length));
                            }
                        }
                    }
                    Add(scan.Root, "");
                }
                catch (Exception error) when (!token.IsCancellationRequested && Expected(error))
                { Record(null, Unavailable, error is Win32Exception native ? unchecked((uint)native.NativeErrorCode) : null); }
            }
            token.ThrowIfCancellationRequested();
            IDuplicateInput OpenFile(DuplicateCandidate candidate)
            {
                if (!sources.TryGetValue(candidate.RelativePath, out var source) || source.Length != candidate.Length || !held.TryGetValue(source.Kind, out var root))
                    throw new UserError("Unavailable or unsupported");
                return root.OpenDuplicate(new(source.RelativePath, source.Length));
            }
            duplicates = DuplicateFiles.Scan(candidates, OpenFile, token, contentLimits, contentElapsed);
        }
        finally
        {
            for (int i = leases.Count - 1; i >= 0; i--) try { leases[i].Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count > 0) { token.ThrowIfCancellationRequested(); throw new AggregateException("Common-folder readers could not all be released.", cleanupErrors); }
        }
        token.ThrowIfCancellationRequested();
        return new(roots.AsReadOnly(), duplicates, new ReadOnlyDictionary<string, CommonDuplicateFileRef>(sources));
    }
    private static bool Expected(Exception error) => error is UserError or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or Win32Exception;
}
