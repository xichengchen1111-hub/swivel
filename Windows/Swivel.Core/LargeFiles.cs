using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record LargeFile(string RelativePath, decimal Bytes, bool InstallerHint);
public sealed record LargeFileIssue(string RelativePath, string Reason);
public sealed record LargeFileResult(IReadOnlyList<LargeFile> Files, IReadOnlyList<LargeFileIssue> Issues,
    bool Incomplete, int KnownFiles, decimal KnownBytes);

// Metadata-only projection of the original bounded FolderSizes snapshot. These
// logical lengths are neither physical usage nor safe-to-delete/reclaimable bytes.
// An installer hint is only a filename suffix; no content or signature is read.
public static class LargeFiles
{
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".cab", ".iso", ".img"
    };

    public static LargeFileResult Find(FolderScanResult scan, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(scan);
        token.ThrowIfCancellationRequested();
        var files = new List<LargeFile>();
        var issues = new HashSet<LargeFileIssue>();
        bool incomplete = scan.EntryLimit || scan.TimeLimit;
        decimal knownBytes = 0;
        string Join(string parent, string name) => parent == "." ? name : parent + "\\" + name;
        void Visit(FolderSizeNode node, string path)
        {
            token.ThrowIfCancellationRequested();
            incomplete |= node.Incomplete;
            if (node.Issue is not null) issues.Add(new(path, node.Issue));
            // Display aggregates repeat the underlying children and do not have
            // real paths. Only the scanner's original tree is an input snapshot.
            if (node.Aggregate) return;
            if (!node.Directory)
            {
                if (node.Files == 1 && node.Issue is null && !node.Incomplete)
                {
                    int dot = node.Name.LastIndexOf('.');
                    bool hint = dot >= 0 && InstallerExtensions.Contains(node.Name[dot..]);
                    files.Add(new(path, node.Bytes, hint));
                    knownBytes += node.Bytes;
                }
                return;
            }
            int childSkipped = 0;
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                childSkipped += child.Skipped;
                Visit(child, Join(path, child.Name));
            }
            // Skipped also includes all descendant skips. Only a local excess
            // means this directory's enumeration failed after a useful prefix.
            if (node.Issue is null && node.Skipped > childSkipped)
                issues.Add(new(path, "Folder enumeration incomplete"));
        }
        Visit(scan.Root, ".");
        if (scan.EntryLimit) issues.Add(new(".", "Scan entry limit"));
        if (scan.TimeLimit) issues.Add(new(".", "Scan time limit"));
        token.ThrowIfCancellationRequested();
        var orderedFiles = files.OrderByDescending(x => x.Bytes).ThenBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        var orderedIssues = issues.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray();
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(orderedFiles), Array.AsReadOnly(orderedIssues),
            incomplete || issues.Count > 0, files.Count, knownBytes);
    }

    // Filter the saved result without rescanning or losing the unfiltered totals
    // and omissions. Keep every match in the original deterministic size order.
    public static IReadOnlyList<LargeFile> Filter(LargeFileResult result, long minimumBytes, bool installerOnly, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (minimumBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        token.ThrowIfCancellationRequested();
        var matches = new List<LargeFile>();
        foreach (var file in result.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Bytes >= minimumBytes && (!installerOnly || file.InstallerHint)) matches.Add(file);
        }
        token.ThrowIfCancellationRequested();
        return matches.AsReadOnly();
    }
}
