using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record RenameRules(string Base = "", string Find = "", string Replacement = "", string Prefix = "", string Suffix = "", bool Number = false, int Start = 1, int Digits = 3)
{
    public IReadOnlyList<RenamePair> Build(IReadOnlyList<string> paths)
    {
        CheckLimits(paths.Count);
        var pairs = paths.Select((path, index) => {
            WindowsPathPolicy.Validate(path);
            string destination = Destination(path, index);
            WindowsPathPolicy.Validate(destination);
            return new RenamePair(path, destination);
        }).ToArray();
        Validate(pairs);
        return Array.AsReadOnly(pairs);
    }
    public void CheckLimits(int count)
    {
        if (count is < 1 or > 100) throw new UserError("Choose up to 100 files at a time.");
        if (new[] { Base, Find, Replacement, Prefix, Suffix }.Any(x => x is null || x.Length > 120) || Start is < 0 or > 99999999 || Digits is < 1 or > 8 || (long)Start + count - 1 > 99999999)
            throw new UserError("Use text up to 120 characters, a start number from 0 to 99999999 and 1 to 8 digits.");
    }
    /// The new path of the file at this position, unchecked (Build checks it).
    public string Destination(string path, int index)
    {
        int slash = path.LastIndexOf('\\'); string name = path[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        string extension = dot > 0 ? name[dot..] : "", stem = dot > 0 ? name[..dot] : name;
        if (Base.Length > 0) stem = Base;
        if (Find.Length > 0) stem = stem.Replace(Find, Replacement, StringComparison.Ordinal);
        stem = Prefix + stem + Suffix;
        if (Number) stem += "-" + (Start + index).ToString("D" + Digits, CultureInfo.InvariantCulture);
        return path[..(slash + 1)] + stem + extension;
    }
    public static void Validate(IReadOnlyList<RenamePair> pairs)
    {
        if (pairs.Count is < 1 or > 100 || pairs.Select(x => x.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Count)
            throw new UserError("Choose distinct files for batch rename.");
        if (pairs.Select(x => x.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Count)
            throw new UserError("Two files would have the same name. Adjust the rules or add numbering.");
        foreach (var pair in pairs)
        {
            WindowsPathPolicy.Validate(pair.Source); WindowsPathPolicy.Validate(pair.Destination);
            if (pair.Source[..(pair.Source.LastIndexOf('\\') + 1)] != pair.Destination[..(pair.Destination.LastIndexOf('\\') + 1)])
                throw new UserError("Batch Rename keeps each file in its original folder.");
        }
    }
}
public sealed record RenamePair(string Source, string Destination)
{
    public bool Changed => Source != Destination;
}
public readonly record struct FileStamp(uint Volume, ulong Index, ulong Size, long WriteTime, long CreationTime)
{
    // NTFS name tunnelling gives a renamed file the creation time of a name vacated in the
    // last few seconds (a swap exchanges them), so creation time cannot show an edit. The
    // file ID still shows replacement; size and write time show edits.
    public bool SameObjectAndContent(FileStamp other) => this with { CreationTime = other.CreationTime } == other;
}
public interface IRenameLease
{
    string CurrentPath { get; }
    FileStamp Stamp { get; }
    void MoveTo(string destination);
}
public sealed record RenameLocation(string Original, string Current, FileStamp Stamp);
public sealed record RenameOutcome(IReadOnlyList<RenameLocation> Locations, Exception? Error, bool Cancelled)
{
    public IReadOnlyList<RenameLocation> Remaining => Locations.Where(x => x.Original != x.Current).ToArray();
}
public static class RenameTransaction
{
    public static bool NeedsStaging(IReadOnlyList<RenamePair> plan) => plan.Any(x => x.Changed &&
        plan.Any(p => p.Source.Equals(x.Destination, StringComparison.OrdinalIgnoreCase)));
    // Leases pin every source and its ancestors before the first move. The host must
    // preflight all destinations. Every move and rollback must refuse replacement.
    public static RenameOutcome Execute(IReadOnlyList<IRenameLease> leases, IReadOnlyList<RenamePair> plan, CancellationToken token)
    {
        RenameRules.Validate(plan);
        if (leases.Count != plan.Count || leases.Where((x, i) => x.CurrentPath != plan[i].Source).Any()) throw new ArgumentException("Mismatched rename leases.");
        var stamps = leases.Select(x => x.Stamp).ToArray();
        // Selected-name dependencies (including case-only changes) first vacate every
        // changed source into a unique same-directory name. No content copy or deletion.
        bool staged = NeedsStaging(plan);
        var temporary = staged ? plan.Select(x => x.Changed
            ? WindowsPathPolicy.Validate(x.Source[..(x.Source.LastIndexOf('\\') + 1)] + ".swivel-rename-" + Guid.NewGuid().ToString("N"))
            : x.Source).ToArray() : Array.Empty<string>();
        Exception? error = null; bool cancelled = false;
        try
        {
            if (staged)
                for (int i = 0; i < plan.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (plan[i].Changed) leases[i].MoveTo(temporary[i]);
                }
            for (int i = 0; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (plan[i].Changed) leases[i].MoveTo(plan[i].Destination);
            }
        }
        catch (Exception ex)
        {
            error = ex; cancelled = ex is OperationCanceledException;
            // Completed destinations can occupy another selected original. Re-vacate
            // them before restoring originals. Failed moves keep exact current paths.
            if (staged)
                for (int i = leases.Count - 1; i >= 0; i--)
                    if (leases[i].CurrentPath != plan[i].Source && leases[i].CurrentPath != temporary[i])
                        try { leases[i].MoveTo(temporary[i]); } catch { /* Recovery receipt retains the location. */ }
            // Never delete anything or overwrite a newly occupied original name.
            // On failure, CurrentPath still describes exactly where each object lives.
            for (int i = leases.Count - 1; i >= 0; i--)
                if (leases[i].CurrentPath != plan[i].Source)
                    try { leases[i].MoveTo(plan[i].Source); } catch { /* Preserve recovery mapping. */ }
        }
        return new RenameOutcome(Array.AsReadOnly(leases.Select((x, i) => new RenameLocation(plan[i].Source, x.CurrentPath, stamps[i])).ToArray()), error, cancelled);
    }
}
