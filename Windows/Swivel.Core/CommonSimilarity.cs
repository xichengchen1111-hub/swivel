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

public enum CommonSimilarityKind { Text, Images }
public sealed record CommonSimilarityLimits(int MetadataEntries = 20000, int Depth = 4, int MetadataSeconds = 30);
public sealed record CommonSimilarityResult(CommonSimilarityKind Kind, IReadOnlyList<CommonDuplicateRootResult> Roots,
    TextSimilarityResult? Text, PhotoSimilarityResult? Images, IReadOnlyDictionary<string, CommonDuplicateFileRef> Sources);

public static class CommonSimilarity
{
    public static CommonSimilarityResult ScanText(IReadOnlyList<CommonFolderChoice> selection,
        Func<CommonFolderChoice, ICommonDuplicateRoot> open, CancellationToken token,
        CommonSimilarityLimits? metadataLimits = null, TextSimilarityLimits? contentLimits = null,
        Func<TimeSpan>? metadataElapsed = null, Func<TimeSpan>? contentElapsed = null)
    {
        // Validate the unchanged content engine's closed limits before any root I/O.
        _ = TextSimilarity.Scan(Array.Empty<DuplicateCandidate>(), _ => throw new InvalidOperationException(), token, contentLimits);
        return Scan(CommonSimilarityKind.Text, selection, open, token, metadataLimits, metadataElapsed,
            (candidates, read) => (TextSimilarity.Scan(candidates, read, token, contentLimits, contentElapsed), null));
    }
    public static CommonSimilarityResult ScanImages(IReadOnlyList<CommonFolderChoice> selection,
        Func<CommonFolderChoice, ICommonDuplicateRoot> open,
        Func<byte[], string, CancellationToken, Action, ulong?> fingerprint, CancellationToken token,
        CommonSimilarityLimits? metadataLimits = null, PhotoSimilarityLimits? contentLimits = null,
        Func<TimeSpan>? metadataElapsed = null, Func<TimeSpan>? contentElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        _ = PhotoSimilarity.Scan(Array.Empty<DuplicateCandidate>(), _ => throw new InvalidOperationException(), fingerprint, token, contentLimits);
        return Scan(CommonSimilarityKind.Images, selection, open, token, metadataLimits, metadataElapsed,
            (candidates, read) => (null, PhotoSimilarity.Scan(candidates, read, fingerprint, token, contentLimits, contentElapsed)));
    }
    // Hold all successfully opened roots through the ONE content scan. This only
    // coordinates existing readers; it does not change their native safety policy.
    private static CommonSimilarityResult Scan(CommonSimilarityKind kind, IReadOnlyList<CommonFolderChoice> selection,
        Func<CommonFolderChoice, ICommonDuplicateRoot> open, CancellationToken token,
        CommonSimilarityLimits? limits, Func<TimeSpan>? metadataElapsed,
        Func<IReadOnlyList<DuplicateCandidate>, Func<DuplicateCandidate, IDuplicateInput>, (TextSimilarityResult? Text, PhotoSimilarityResult? Images)> compare)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(open);
        var choices = selection.ToArray(); limits ??= new();
        if (choices.Length is < 1 or > 6 || choices.Any(c => c is null || !c.CanAttempt) || choices.Select(c => c.Kind).Distinct().Count() != choices.Length)
            throw new ArgumentException("Invalid common-folder comparison selection.", nameof(selection));
        if (limits.MetadataEntries is < 1 or > 20000 || limits.Depth is < 0 or > 4 || limits.MetadataSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew(); metadataElapsed ??= () => watch.Elapsed;
        var held = new Dictionary<CommonFolderKind, ICommonDuplicateRoot>(); var leases = new List<ICommonDuplicateRoot>(6);
        var cleanupErrors = new List<Exception>(6);
        var roots = new List<CommonDuplicateRootResult>(); var candidates = new List<DuplicateCandidate>();
        var sources = new Dictionary<string, CommonDuplicateFileRef>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); int remainingRoots = choices.Select(c => c.Path!).Distinct(StringComparer.Ordinal).Count(), remainingEntries = limits.MetadataEntries;
        (TextSimilarityResult? Text, PhotoSimilarityResult? Images) comparison;
        try
        {
            foreach (var choice in choices)
            {
                token.ThrowIfCancellationRequested(); int quota = 0, deadline = 0;
                void Record(FolderScanResult? scan, string? issue = null, uint? error = null) => roots.Add(new(choice, scan, issue, error, quota, deadline, DateTimeOffset.Now));
                if (!seen.Add(choice.Path!)) { Record(null, CommonDuplicates.RepeatedRoot); continue; }
                double seconds = metadataElapsed().TotalSeconds;
                if (remainingEntries == 0) { remainingRoots--; Record(null, CommonDuplicates.NoEntries); continue; }
                if (seconds >= limits.MetadataSeconds) { remainingRoots--; Record(null, CommonDuplicates.NoTime); continue; }
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
                { Record(null, CommonDuplicates.Unavailable, error is Win32Exception native ? unchecked((uint)native.NativeErrorCode) : null); }
            }
            token.ThrowIfCancellationRequested();
            IDuplicateInput OpenFile(DuplicateCandidate candidate)
            {
                if (!sources.TryGetValue(candidate.RelativePath, out var source) || source.Length != candidate.Length || !held.TryGetValue(source.Kind, out var root))
                    throw new UserError("Unavailable or unsupported");
                return root.OpenDuplicate(new(source.RelativePath, source.Length));
            }
            comparison = compare(candidates, OpenFile);
        }
        finally
        {
            for (int i = leases.Count - 1; i >= 0; i--) try { leases[i].Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count > 0) { token.ThrowIfCancellationRequested(); throw new AggregateException("Common-folder readers could not all be released.", cleanupErrors); }
        }
        token.ThrowIfCancellationRequested();
        return new(kind, roots.AsReadOnly(), comparison.Text, comparison.Images, new ReadOnlyDictionary<string, CommonDuplicateFileRef>(sources));
    }
    private static bool Expected(Exception error) => error is UserError or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or Win32Exception;
}
