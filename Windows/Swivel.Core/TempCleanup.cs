using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

/// A direct item of the temporary folder that nothing in it changed for TempCleanup.Days.
public sealed record TempCandidate(string Name, bool Directory, decimal Bytes, int Files, long LastWrite, long Touched, long Newest);
public sealed record TempCleanupPlan(IReadOnlyList<TempCandidate> Old, int Recent, int NotListed, decimal OldBytes, long Cutoff);

/// Picks the direct items of a measured temporary folder that were created, written and changed
/// more than seven days ago, with everything counted inside them (seven days is the age Windows
/// Disk Cleanup uses for temporary files). Creation and change times matter because a file
/// copied or extracted into the folder today keeps its old last write. Partial, skipped or
/// undated items, or folders with anything undated inside, are never listed.
public static class TempCleanup
{
    public const int Days = 7;
    public const string Notice = "Only direct items of the configured temporary folder are listed, and only when the item and everything measured inside it were created, written and changed more than 7 days ago (a file copied or unpacked here recently counts as recent, even with an old modified date). Skipped, partial or undated items, and anything changed recently, are not listed. Nothing is selected for you. An app may still need an old file, so review the list first. Before each move the item is checked again: a file must have the same size and modified time and no other hard link; a folder must have the same modified time and be measured again completely with nothing inside changed in the last 7 days. Items in use may be left in place. If Windows removes only part of a folder (for example after asking to delete it permanently while a file inside is in use), the result says so; check the Recycle Bin. Only fixed drives with a Recycle Bin are used; if an item is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move restores the last moved items while this window is open, unless something now exists at their place; restored items count as recently changed and leave the list. Afterwards, restore them from the Recycle Bin.";

    public static long CutoffFor(DateTime nowUtc) => nowUtc.ToUniversalTime().AddDays(-Days).ToFileTimeUtc();

    /// True when a FILETIME is known and older than the cutoff.
    public static bool IsOld(long fileTime, long cutoff) => fileTime > 0 && fileTime < cutoff;

    public static TempCleanupPlan Plan(FolderScanResult scan, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(scan);
        long cutoff = CutoffFor(nowUtc);
        var old = new List<TempCandidate>(); int recent = 0, notListed = 0;
        foreach (var child in scan.Root.Children)
        {
            if (child.Issue is not null || child.Incomplete || child.Aggregate || child.LastWrite <= 0 || child.Touched <= 0 || child.Newest <= 0) { notListed++; continue; }
            if (!IsOld(child.Touched, cutoff) || !IsOld(child.Newest, cutoff)) { recent++; continue; }
            old.Add(new(child.Name, child.Directory, child.Bytes, child.Files, child.LastWrite, child.Touched, child.Newest));
        }
        var sorted = old.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return new(Array.AsReadOnly(sorted), recent, notListed, sorted.Sum(x => x.Bytes), cutoff);
    }
}
