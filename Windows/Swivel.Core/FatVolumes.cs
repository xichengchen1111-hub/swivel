using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

/// The file systems of USB sticks and memory cards. Windows reports "FAT32", "FAT" (FAT12/16)
/// or "exFAT"; anything else that is not NTFS stays unsupported.
public enum VolumeFormat { Other, Ntfs, Fat, ExFat }

/// Rules for exFAT and FAT32 drives. They keep no lasting file identity (a file's index is the
/// position of its directory entry, which a rename or move changes, and FILE_ID_INFO is not
/// supported), no alternate data streams (so no download warning), and on FAT32 no file of
/// 4 GB or more. Names follow the same Windows rules as on NTFS, but every folder ignores
/// letter case, and asking for a case-only rename succeeds without changing the stored name.
public static class FatRules
{
    public const ulong Fat32MaxFile = uint.MaxValue; // 4 GiB − 1 byte
    public const string MainStream = "::$DATA", ZoneStream = ":Zone.Identifier:$DATA";

    public const string UndoNote = "On exFAT and FAT32 drives (USB sticks, memory cards) Swivel cannot recognise a file again later, so Undo works only while Swivel keeps a reference to each moved or renamed item until you undo or forget the history. Meanwhile Windows may say the drive is in use, the folders holding those items may not be renamed or moved, and a deleted item's name stays taken: forget the history first, for example before ejecting the drive.";
    public const string ExactName = "On exFAT and FAT32 drives, choose each item by its exact stored name (same letter case, not a short 8.3 name).";
    public const string NameNotTaken = "The drive did not store the new name exactly as asked. Nothing was overwritten; check the current names.";
    public const string Released = "Undo is no longer available for this item: on exFAT and FAT32 drives it needs the reference Swivel kept, which was released. Nothing was changed.";
    public const string MovedElsewhere = "An item was moved, renamed or removed by another app since. Undo was refused without changing it.";
    public const string TooBig = "\"{0}\" is 4 GB or larger, and FAT32 drives cannot hold files that large. Nothing was copied. Choose an NTFS or exFAT destination.";
    public const string OtherStreams = "\"{0}\" has extra data streams that exFAT and FAT32 drives cannot hold. Nothing was copied. Choose an NTFS destination.";
    public const string BadName = "\"{0}\" has a name exFAT and FAT32 drives cannot store. Nothing was copied.";
    public const string CaseClash = "\"{0}\" and \"{1}\" differ only in letter case; exFAT and FAT32 drives cannot hold both in one folder. Nothing was copied.";
    public const string ZoneWillBeLost = "{0} file(s) carry the download warning (Mark of the Web). exFAT and FAT32 drives cannot store it, so their copies would open without it.";
    public const string ZoneConsent = "Copy them without the download warning";
    public const string ZoneRefused = "\"{0}\" carries the download warning, which exFAT and FAT32 drives cannot store. Nothing more was copied; confirm losing the warning to copy it.";
    public const string FolderNotice = "The folder and everything inside it move as one, by renaming it into the destination on the same drive: nothing is copied. If the destination already has an item with this name, the folder takes the next free numbered name; nothing is overwritten. Undo puts it back while it is still the same folder and its original name is free.";
    public const string CopyNote = "The destination is an exFAT or FAT32 drive: copies keep file contents, attributes and modified times, but such drives cannot store extra data streams, and FAT32 cannot hold files of 4 GB or more.";
    public const string NoCopyUndo = "Undo is not offered for copies on exFAT and FAT32 drives: they cannot recognise a copy again, and removable drives usually have no Recycle Bin.";

    public static VolumeFormat Parse(string? fileSystem) => fileSystem switch
    {
        "NTFS" => VolumeFormat.Ntfs,
        "FAT32" or "FAT" => VolumeFormat.Fat,
        "exFAT" => VolumeFormat.ExFat,
        _ => VolumeFormat.Other
    };
    public static bool IsFat(VolumeFormat format) => format is VolumeFormat.Fat or VolumeFormat.ExFat;

    /// Null when this single name can be stored on exFAT and FAT32, otherwise why not. The
    /// same characters as on NTFS are forbidden; long file name entries hold up to 255 units.
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.'))
            return BadName;
        foreach (char c in name)
            if (c < 32 || c == 127 || "<>:\"/\\|?*".Contains(c)) return BadName;
        for (int i = 0; i < name.Length; i++)
        {
            // UTF-16 code units are stored as they are; a lone surrogate has no letter case to fold.
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1])) { i++; continue; }
            if (char.IsSurrogate(name[i])) return BadName;
        }
        return null;
    }

    public static void CheckName(string name)
    {
        if (NameProblem(name) is string problem) throw new UserError(problem, name);
    }

    /// Every relative path in a folder copy: each part storable, and no two paths in one folder
    /// that differ only in letter case (a case-sensitive NTFS folder can hold both).
    public static void CheckTree(IEnumerable<string> relativePaths)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in relativePaths)
        {
            if (path.Length == 0) continue;
            foreach (string part in path.Split('\\')) CheckName(part);
            if (seen.TryGetValue(path, out string? other) && !string.Equals(other, path, StringComparison.Ordinal))
                throw new UserError(CaseClash, other, path);
            seen.TryAdd(path, path);
        }
    }

    public static void CheckSize(VolumeFormat format, ulong length, string name)
    {
        if (format == VolumeFormat.Fat && length > Fat32MaxFile) throw new UserError(TooBig, name);
    }

    /// Whether a file's streams can go to exFAT or FAT32: only its main stream can. A download
    /// warning may be left behind only with the user's explicit consent; any other named
    /// stream refuses the copy. Returns whether the file carries the download warning.
    public static bool CheckStreams(IReadOnlyList<(string Name, long Size)> streams, string name)
    {
        bool zone = false;
        foreach (var (stream, _) in streams)
        {
            if (string.Equals(stream, MainStream, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, ZoneStream, StringComparison.OrdinalIgnoreCase)) { zone = true; continue; }
            throw new UserError(OtherStreams, name);
        }
        return zone;
    }

    /// The streams an exFAT or FAT32 copy must have: the main stream alone, with the source's size.
    public static IReadOnlyList<(string Name, long Size)> CopyStreams(IReadOnlyList<(string Name, long Size)> streams, bool acceptZoneLoss, string name)
    {
        if (CheckStreams(streams, name) && !acceptZoneLoss) throw new UserError(ZoneRefused, name);
        return streams.Where(x => string.Equals(x.Name, MainStream, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
