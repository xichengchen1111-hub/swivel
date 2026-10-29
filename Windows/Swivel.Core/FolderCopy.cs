using System;
using System.IO;

namespace Swivel.Core;

/// Copy Folder to… (as Mac's Copy to… with a folder): the chosen folder's whole tree as a new
/// folder of the same name inside another folder. Planning reuses Compress Folder to ZIP's walk,
/// so the same items are skipped and the same limits apply.
public static class FolderCopy
{
    public const string Notice = "The copy is a new folder with the same name (or the next free numbered name) inside the destination. Files keep every data stream, including the download warning, their attributes and modified times; folders get new times and the destination's permissions. Links, junctions, cloud-only files and system files are skipped and listed. Nothing is overwritten.";
    public const string Inside = "Choose a destination outside the folder being copied.";
    public const string NeedsNtfs = "Copy Folder currently requires an NTFS destination, so every data stream and the download warning can be kept.";
    public const string Changed = "A file changed after the folder was read. Copying stopped; the copies already made were kept.";
    public const string TooLong = "Some copies would have paths longer than 240 characters. Choose a destination with a shorter path.";
    public const string TooMany = "Copy Folder is limited to 10,000 files and folders.";
    public const string TooLarge = "Copy Folder is limited to 2 GB of files.";
    public const string TooDeep = "Copy Folder is limited to 32 levels of folders.";
    public const string Unreadable = "\"{0}\" is in use by another app or could not be read. Copying stopped; the copies already made were kept.";
    public const string Interfered = "Something else changed the new folder while copying. Nothing was overwritten.";

    /// The destination may not be the folder itself or anywhere inside it (a copy would copy itself).
    public static void CheckDestination(string source, string destination)
    {
        string Clean(string path) => path.TrimEnd('\\') + "\\";
        if (Clean(destination).StartsWith(Clean(source), StringComparison.OrdinalIgnoreCase)) throw new UserError(Inside);
    }

    /// Every copy's full path, with room for a numbered folder name ("name 10000"), must fit
    /// Windows Preview's 240-character limit.
    public static void CheckLengths(FolderZipPlan plan, string destination)
    {
        int longest = 0;
        foreach (var item in plan.Items) longest = Math.Max(longest, item.RelativePath.Length);
        if (destination.TrimEnd('\\').Length + 1 + plan.Name.Length + 6 + 1 + longest > 240) throw new UserError(TooLong);
    }

    /// The new folder's name: the source's own name, then "name 2", "name 3"…
    public static string FolderName(string name, int index) => index <= 1 ? name : name + " " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
