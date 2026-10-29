using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

/// One whole folder moved on its NTFS volume by renaming its held handle into another folder.
/// Original is where it was, Current where the rename put it (the same name, or the next free
/// numbered name); Identity is the folder's own 128-bit file ID with its volume.
public sealed record FolderMoveReceipt(string Original, string Current, MoveIdentity Identity, MoveDirectorySnapshot OriginalParent, MoveDirectorySnapshot CurrentParent);

/// Move Folder to… (as Mac's Move to… with a folder): one exclusive rename, never a copy and
/// delete. A different volume is refused here; the app offers Copy Folder instead.
public static class FolderMove
{
    public const int MaxHistory = 20;
    public const string Notice = "The folder and everything inside it move as one, by renaming it into the destination on the same NTFS drive: nothing is copied, and file contents, data streams, permissions and times stay as they are. If the destination already has an item with this name, the folder takes the next free numbered name; nothing is overwritten. Undo puts it back while it is still the same folder and its original name is free.";
    public const string Inside = "Choose a destination outside the folder being moved.";
    public const string AlreadyThere = "The folder is already in that folder.";
    public const string OtherVolume = "Move Folder requires a destination on the same NTFS drive. Nothing was moved.";
    public const string NotPlain = "Choose a local folder without links, system attributes or cloud placeholders.";
    public const string WholeDrive = "Choose a folder, not a whole drive.";
    public const string InUse = "Windows did not allow moving the folder; a file inside it may be open in another app. Nothing was moved.";
    public const string Replaced = "The moved folder was renamed, moved or replaced since. Undo was refused and nothing was moved.";
    public const string OriginalTaken = "Something now exists at the folder's original place. Undo was refused and nothing was replaced.";
    public const string ParentsChanged = "A source or destination folder changed. Undo was refused without moving the folder.";
    public const string Incomplete = "A folder move receipt is incomplete. Undo was refused.";
    public const string NoHistory = "There is no folder move to undo in this session.";
    public const string HistoryFull = "Undo or forget folder move history before moving another folder (20 moves maximum).";

    /// The destination may be neither the folder itself, anywhere inside it, nor the folder it
    /// is already in. Paths are compared without letter case, as NTFS does by default.
    public static void CheckDestination(string folder, string destination)
    {
        folder = MoveRules.DirectoryPath(folder); destination = MoveRules.DirectoryPath(destination);
        if (folder.Length == 3) throw new UserError(WholeDrive);
        string Clean(string path) => path.TrimEnd('\\') + "\\";
        if (Clean(destination).StartsWith(Clean(folder), StringComparison.OrdinalIgnoreCase)) throw new UserError(Inside);
        if (string.Equals(MoveRules.Parent(folder), destination, StringComparison.OrdinalIgnoreCase)) throw new UserError(AlreadyThere);
    }

    /// The folder's place in the destination for a numbered attempt (1 = its own name).
    public static string Candidate(string folder, string destination, int index)
        => WindowsPathPolicy.Validate(MoveRules.DirectoryPath(destination).TrimEnd('\\') + "\\" + FolderCopy.FolderName(MoveRules.FileName(MoveRules.DirectoryPath(folder)), index));

    /// Tries the folder's own name, then "name 2", "name 3"… until `claim` takes one (returns
    /// true) and returns that path. `claim` returns false only when the name is already taken;
    /// the claim itself is the exclusive rename, never a check followed by a write.
    public static string Claim(string folder, string destination, Func<string, bool> claim)
    {
        for (int i = 1; i <= 10000; i++)
        {
            string candidate = Candidate(folder, destination, i);
            if (claim(candidate)) return candidate;
        }
        throw new UserError("Too many files have this output name.");
    }

    /// Validates a receipt before Undo opens anything: both paths and parent chains agree,
    /// a complete identity on the parents' volume, and a real move.
    public static FolderMoveReceipt ForUndo(FolderMoveReceipt? receipt)
    {
        if (receipt is null || receipt.OriginalParent is null || receipt.CurrentParent is null) throw new UserError(Incomplete);
        try
        {
            string original = MoveRules.DirectoryPath(receipt.Original), current = MoveRules.DirectoryPath(receipt.Current);
            if (original != receipt.Original || current != receipt.Current || original.Length == 3 || current.Length == 3) throw new UserError(Incomplete);
            MoveRules.ValidateDirectory(receipt.OriginalParent); MoveRules.ValidateDirectory(receipt.CurrentParent);
            if (MoveRules.Parent(original) != receipt.OriginalParent.Path || MoveRules.Parent(current) != receipt.CurrentParent.Path ||
                string.Equals(original, current, StringComparison.OrdinalIgnoreCase) ||
                (receipt.Identity.Low == 0 && receipt.Identity.High == 0) ||
                receipt.Identity.Volume != receipt.OriginalParent.LeafIdentity.Volume || receipt.Identity.Volume != receipt.CurrentParent.LeafIdentity.Volume ||
                receipt.OriginalParent.Ancestors.Concat(receipt.CurrentParent.Ancestors).Any(entry => entry.Identity == receipt.Identity))
                throw new UserError(Incomplete);
        }
        catch (UserError) { throw new UserError(Incomplete); }
        return receipt with { OriginalParent = MoveRules.FreezeDirectory(receipt.OriginalParent), CurrentParent = MoveRules.FreezeDirectory(receipt.CurrentParent) };
    }

    /// A path at or below `from` now lives at the same place below `to`; others are unchanged.
    public static string UpdatePath(string path, string from, string to)
    {
        if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase)) return to;
        string prefix = from.TrimEnd('\\') + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? to.TrimEnd('\\') + "\\" + path[prefix.Length..] : path;
    }
}

/// One item Copy Folder created, as it was right after copying: its path below the new folder,
/// whether it is a folder, and for a file its length and modified time.
public sealed record FolderCopyEntry(string RelativePath, bool Directory, long Length, long LastWrite);

public static class FolderCopyUndo
{
    public const string Notice = "To undo the copy, Move Copy to Recycle Bin… moves the new folder to the Recycle Bin, after asking. It is moved only while it is still the folder Swivel created and holds exactly what was copied: the same names, file sizes and modified times, with nothing added, removed or renamed. Contents are not read again. The original folder is never opened or changed. Undo Move puts the copy back while this window is open, unless something now exists at its place; afterwards, restore it from the Recycle Bin.";
    public const string Changed = "The new folder changed since it was copied (an item was added, removed, renamed, resized or modified), so it was left alone.";
    public const string Replaced = "The new folder was renamed, moved or replaced since it was copied (or is now a link, system or cloud item), so it was left alone.";
    public const string NotOffered = "Some copies could not be verified, so undoing this copy is not offered. Check the new folder yourself.";

    /// Null when `now` (a fresh walk of the new folder) holds exactly the recorded items;
    /// otherwise the reason it was left alone. Folder times are not compared: adding a child
    /// changes them, and NTFS updates their listed copies lazily.
    public static string? Compare(IReadOnlyList<FolderCopyEntry> recorded, FolderZipPlan now)
    {
        if (recorded is null || recorded.Count == 0 || now is null) return Changed;
        if (now.Skipped.Count > 0 || now.Items.Count != recorded.Count) return Changed;
        var expected = new Dictionary<string, FolderCopyEntry>(StringComparer.Ordinal);
        foreach (var entry in recorded) if (entry is null || !expected.TryAdd(entry.RelativePath, entry)) return Changed;
        foreach (var item in now.Items)
        {
            if (!expected.TryGetValue(item.RelativePath, out var entry) || entry.Directory != item.Directory) return Changed;
            if (!item.Directory && (entry.Length != item.Length || entry.LastWrite != item.LastWrite)) return Changed;
        }
        return null;
    }
}
