using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public readonly record struct MoveIdentity(ulong Volume, ulong Low, ulong High);
public readonly record struct MoveFileStamp(MoveIdentity Identity, ulong Length, long CreationTime, long WriteTime, long ChangeTime, uint Attributes, uint Links);
public sealed record MoveDirectoryEntry(string Path, MoveIdentity Identity);
public sealed record MoveDirectorySnapshot(string Path, IReadOnlyList<MoveDirectoryEntry> Ancestors)
{
    public MoveIdentity LeafIdentity => Ancestors[^1].Identity;
}
public sealed record MovePair(string Source, string Destination)
{
    public bool Changed => !string.Equals(Source, Destination, StringComparison.Ordinal);
}
public sealed record MoveLocation(string Original, string Current, MoveFileStamp? Stamp, MoveDirectorySnapshot OriginalDirectory, MoveDirectorySnapshot CurrentDirectory)
{
    public MoveFileStamp? BeforeStamp { get; init; }
}
public sealed record MoveOutcome(IReadOnlyList<MoveLocation> Locations, Exception? Error, bool Cancelled)
{
    public IReadOnlyList<MoveLocation> Remaining => Array.AsReadOnly(Locations.Where(x => !string.Equals(x.Original, x.Current, StringComparison.Ordinal)).ToArray());
}

public static class MoveRules
{
    public const int MaxFiles = 100;
    public static string DirectoryPath(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length > 3 && path.EndsWith('\\')) path = path[..^1];
        WindowsPathPolicy.Validate(path.Length == 3 ? path + "x" : path);
        return path;
    }
    public static string Parent(string path)
    {
        WindowsPathPolicy.Validate(path);
        int slash = path.LastIndexOf('\\');
        return slash == 2 ? path[..3] : path[..slash];
    }
    public static string FileName(string path)
    {
        WindowsPathPolicy.Validate(path); return path[(path.LastIndexOf('\\') + 1)..];
    }
    public static string Destination(string source, string directory)
        => WindowsPathPolicy.Validate(DirectoryPath(directory).TrimEnd('\\') + "\\" + FileName(source));

    public static void Validate(IReadOnlyList<MovePair> plan)
    {
        if (plan is null || plan.Count is < 1 or > MaxFiles || plan.Any(x => x is null))
            throw new UserError("Choose 1–100 distinct files to move.");
        foreach (var pair in plan)
        {
            WindowsPathPolicy.Validate(pair.Source); WindowsPathPolicy.Validate(pair.Destination);
            if (!string.Equals(FileName(pair.Source), FileName(pair.Destination), StringComparison.Ordinal))
                throw new UserError("Move Files keeps each original file name.");
        }
        if (plan.Select(x => x.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count)
            throw new UserError("Choose 1–100 distinct files to move.");
        if (plan.Select(x => x.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count)
            throw new UserError("Two selected files would have the same destination name. Nothing was moved.");
    }
    public static void ValidateDirectory(MoveDirectorySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Ancestors is null || snapshot.Ancestors.Count is < 1 or > 128 || snapshot.Ancestors.Any(x => x is null) || DirectoryPath(snapshot.Path) != snapshot.Path)
            throw new UserError("A move receipt is incomplete. Undo was refused.");
        string current = snapshot.Path[..3]; var paths = new List<string> { current };
        foreach (string component in snapshot.Path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        { current = current.TrimEnd('\\') + "\\" + component; paths.Add(current); }
        if (paths.Count != snapshot.Ancestors.Count) throw new UserError("A move receipt is incomplete. Undo was refused.");
        for (int i = 0; i < paths.Count; i++)
        {
            var entry = snapshot.Ancestors[i];
            if (entry is null || entry.Path != paths[i] || (entry.Identity.Low == 0 && entry.Identity.High == 0) || entry.Identity.Volume != snapshot.LeafIdentity.Volume)
                throw new UserError("A move receipt is incomplete. Undo was refused.");
        }
    }
    public static bool SameDirectory(MoveDirectorySnapshot left, MoveDirectorySnapshot right)
    {
        ValidateDirectory(left); ValidateDirectory(right);
        return string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) && left.Ancestors.Count == right.Ancestors.Count &&
            left.Ancestors.Zip(right.Ancestors).All(x => string.Equals(x.First.Path, x.Second.Path, StringComparison.OrdinalIgnoreCase) && x.First.Identity == x.Second.Identity);
    }
    public static MoveDirectorySnapshot FreezeDirectory(MoveDirectorySnapshot snapshot)
    {
        ValidateDirectory(snapshot); return new(snapshot.Path, Array.AsReadOnly(snapshot.Ancestors.ToArray()));
    }
    // A rename updates change time, and NTFS name tunnelling can replace creation time with
    // that of a recently vacated name; identity, length, write time, attributes and links remain.
    public static bool SameRenameMetadata(MoveFileStamp before, MoveFileStamp after)
        => before with { ChangeTime = after.ChangeTime, CreationTime = after.CreationTime } == after;
}

// A lease owns its source object and both complete directory chains. MoveTo updates
// CurrentPath immediately after a successful native rename, before any further query.
// Restore moves that same held object back; it must never overwrite or delete a path.
public interface IMoveLease
{
    string CurrentPath { get; }
    MoveFileStamp? VerifiedStamp { get; }
    MoveDirectorySnapshot CurrentDirectory { get; }
    void Validate();
    void EnsureDestinationFree(string destination);
    void MoveTo(string destination, bool restoring);
}

public static class MoveTransaction
{
    public static MoveOutcome Execute(IReadOnlyList<IMoveLease> leases, IReadOnlyList<MovePair> suppliedPlan, CancellationToken token)
    {
        var plan = suppliedPlan.ToArray(); MoveRules.Validate(plan);
        if (leases.Count != plan.Length || leases.Where((x, i) => x.CurrentPath != plan[i].Source).Any())
            throw new ArgumentException("Mismatched move leases.");
        var origins = leases.Select(x => x.CurrentDirectory).ToArray();
        var beforeStamps = leases.Select(x => x.VerifiedStamp).ToArray();
        var errors = new List<Exception>(leases.Count + 1); bool cancelled = false;
        try
        {
            // No first rename until every source and destination has been checked.
            foreach (var lease in leases) { token.ThrowIfCancellationRequested(); lease.Validate(); }
            for (int i = 0; i < plan.Length; i++)
            { token.ThrowIfCancellationRequested(); if (plan[i].Changed) leases[i].EnsureDestinationFree(plan[i].Destination); }
            for (int i = 0; i < plan.Length; i++)
            { token.ThrowIfCancellationRequested(); if (plan[i].Changed) leases[i].MoveTo(plan[i].Destination, false); }
            // Cancellation arriving during the final native call also requests rollback.
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            errors.Add(error); cancelled = error is OperationCanceledException || token.IsCancellationRequested;
            for (int i = leases.Count - 1; i >= 0; i--)
                if (!string.Equals(leases[i].CurrentPath, plan[i].Source, StringComparison.Ordinal))
                    try { leases[i].MoveTo(plan[i].Source, true); }
                    catch (Exception rollbackError) { errors.Add(rollbackError); }
        }
        // These are cached properties, not fallible I/O. Never lose the actual location
        // because the post-rename metadata query or a later rollback failed.
        var locations = Array.AsReadOnly(leases.Select((lease, i) => new MoveLocation(plan[i].Source, lease.CurrentPath,
            lease.VerifiedStamp, origins[i], lease.CurrentDirectory) { BeforeStamp = beforeStamps[i] }).ToArray());
        return new(locations, errors.Count switch { 0 => null, 1 => errors[0], _ => new AggregateException(errors) }, cancelled);
    }
}

public static class MoveReceipts
{
    public static IReadOnlyList<MoveLocation> ForUndo(IReadOnlyList<MoveLocation> supplied)
    {
        if (supplied is null || supplied.Count is < 1 or > MoveRules.MaxFiles || supplied.Any(x => x is null))
            throw new UserError("A move receipt is incomplete. Undo was refused.");
        var receipt = supplied.ToArray();
        foreach (var entry in receipt)
        {
            WindowsPathPolicy.Validate(entry.Original); WindowsPathPolicy.Validate(entry.Current);
            MoveRules.ValidateDirectory(entry.OriginalDirectory); MoveRules.ValidateDirectory(entry.CurrentDirectory);
            if (MoveRules.Parent(entry.Original) != entry.OriginalDirectory.Path || MoveRules.Parent(entry.Current) != entry.CurrentDirectory.Path ||
                entry.Stamp is not { } stamp || stamp.Links != 1 || (stamp.Identity.Low == 0 && stamp.Identity.High == 0) ||
                stamp.Identity.Volume != entry.OriginalDirectory.LeafIdentity.Volume || stamp.Identity.Volume != entry.CurrentDirectory.LeafIdentity.Volume)
                throw new UserError("A move receipt is incomplete. Undo was refused.");
        }
        if (receipt.Select(x => x.Stamp!.Value.Identity).Distinct().Count() != receipt.Length)
            throw new UserError("A move receipt is incomplete. Undo was refused.");
        MoveRules.Validate(receipt.Select(x => new MovePair(x.Current, x.Original)).ToArray());
        var remaining = receipt.Where(x => x.Original != x.Current).Select(x => x with {
            OriginalDirectory = MoveRules.FreezeDirectory(x.OriginalDirectory), CurrentDirectory = MoveRules.FreezeDirectory(x.CurrentDirectory)
        }).ToArray();
        if (remaining.Length == 0) throw new UserError("There are no remaining moves to undo.");
        return Array.AsReadOnly(remaining);
    }

    // Undo outcomes use the beginning of THAT operation as Original. Preserve the
    // first move's original directory while refreshing every remaining current stamp.
    public static IReadOnlyList<MoveLocation> AfterUndo(IReadOnlyList<MoveLocation> originalReceipt, MoveOutcome undoOutcome)
    {
        var original = ForUndo(originalReceipt);
        if (undoOutcome is null || undoOutcome.Locations.Count != original.Count)
            throw new ArgumentException("Mismatched undo outcome.");
        var result = new List<MoveLocation>(original.Count);
        var byStart = undoOutcome.Locations.ToDictionary(x => x.Original, StringComparer.Ordinal);
        foreach (var entry in original)
        {
            if (!byStart.TryGetValue(entry.Current, out var undone) ||
                !MoveRules.SameDirectory(entry.CurrentDirectory, undone.OriginalDirectory) ||
                (undone.Stamp is { } stamp && stamp.Identity != entry.Stamp!.Value.Identity) ||
                (undone.Current != entry.Current && undone.Current != entry.Original) ||
                !MoveRules.SameDirectory(undone.Current == entry.Current ? entry.CurrentDirectory : entry.OriginalDirectory, undone.CurrentDirectory))
                throw new ArgumentException("Mismatched undo outcome.");
            if (undone.Current != entry.Original)
                result.Add(entry with { Current = undone.Current, Stamp = undone.Stamp, CurrentDirectory = undone.CurrentDirectory });
        }
        return result.AsReadOnly();
    }

    // A rename changes ChangeTime. Only a verified before->after transition made by
    // this operation may refresh an older receipt. Identity/path alone would let a
    // later move of an externally edited file erase the older undo safety check.
    public static IReadOnlyList<MoveLocation> RefreshHistory(IReadOnlyList<MoveLocation> earlierReceipt, MoveOutcome outcome, IReadOnlyList<MoveLocation>? undoReceipt = null)
    {
        if (earlierReceipt is null || earlierReceipt.Count is < 1 or > MoveRules.MaxFiles || earlierReceipt.Any(x => x is null) || outcome is null)
            throw new ArgumentException("Invalid move history.");
        var bridges = new List<(string Path, MoveFileStamp Before, MoveFileStamp After, MoveDirectorySnapshot Directory)>();
        foreach (var row in outcome.Locations)
            if (row.Original == row.Current && row.BeforeStamp is { } before && row.Stamp is { } after && MoveRules.SameRenameMetadata(before, after) &&
                MoveRules.SameDirectory(row.OriginalDirectory, row.CurrentDirectory))
                bridges.Add((row.Current, before, after, row.CurrentDirectory));
        if (undoReceipt is not null)
        {
            // Validate the complete correspondence before accepting any bridge.
            _ = AfterUndo(undoReceipt, outcome);
            var byStart = outcome.Locations.ToDictionary(x => x.Original, StringComparer.Ordinal);
            foreach (var original in ForUndo(undoReceipt))
            {
                var row = byStart[original.Current];
                if (row.Current == original.Original && original.BeforeStamp is { } before && row.Stamp is { } after &&
                    MoveRules.SameRenameMetadata(before, after) && MoveRules.SameDirectory(original.OriginalDirectory, row.CurrentDirectory))
                    bridges.Add((row.Current, before, after, row.CurrentDirectory));
            }
        }
        return Array.AsReadOnly(earlierReceipt.Select(entry => {
            foreach (var bridge in bridges)
                if (entry.Current == bridge.Path && entry.Stamp == bridge.Before && MoveRules.SameDirectory(entry.CurrentDirectory, bridge.Directory))
                    return entry with { Stamp = bridge.After, CurrentDirectory = bridge.Directory };
            return entry;
        }).ToArray());
    }
}
