using System;
using System.IO;
using System.Linq;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderMoveNativeChecks
{
    // Move Folder to… and its Undo, and Undo of Copy Folder to…, on real NTFS folders.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(directory, "folder-move"); Directory.CreateDirectory(root);
        string folder = Path.Combine(root, "相册"), into = Path.Combine(root, "into");
        Directory.CreateDirectory(Path.Combine(folder, "sub")); Directory.CreateDirectory(into);
        File.WriteAllText(Path.Combine(folder, "sub", "a.txt"), "alpha");
        string? Error(Action action) { try { action(); return null; } catch (UserError error) { return error.Message; } }

        // Move and Undo.
        FolderMoveReceipt moved;
        using (var session = FolderMoveSession.Open(folder, into, default))
        {
            check(session.Planned == Path.Combine(into, "相册"), "The preview plans the folder's own name in the destination: " + session.Planned);
            moved = session.Execute(default);
        }
        check(moved.Current == Path.Combine(into, "相册") && !Directory.Exists(folder) && File.ReadAllText(Path.Combine(into, "相册", "sub", "a.txt")) == "alpha",
            "The whole folder moves with its contents");
        using (var session = FolderMoveSession.ForUndo(moved, default))
        {
            check(session.Planned == folder, "Undo plans the folder's original place");
            var back = session.Execute(default);
            check(back.Current == folder && back.Original == moved.Current, "Undo's result runs from the moved place back to the original");
        }
        check(Directory.Exists(folder) && !Directory.Exists(moved.Current) && File.ReadAllText(Path.Combine(folder, "sub", "a.txt")) == "alpha", "Undo puts the folder back with its contents");

        // A taken name moves on to the next number; nothing is overwritten.
        Directory.CreateDirectory(Path.Combine(into, "相册")); File.WriteAllText(Path.Combine(into, "相册", "keep.txt"), "keep");
        using (var session = FolderMoveSession.Open(folder, into, default)) moved = session.Execute(default);
        check(moved.Current == Path.Combine(into, "相册 2") && File.ReadAllText(Path.Combine(into, "相册", "keep.txt")) == "keep" && File.Exists(Path.Combine(moved.Current, "sub", "a.txt")),
            "A taken name gives the next free numbered name and leaves the other folder alone");
        using (var session = FolderMoveSession.Open(Path.Combine(into, "相册 2"), root, default))
            check(session.Planned == Path.Combine(root, "相册 2"), "A numbered folder keeps its numbered name when moved on");

        // A name taken between the preview and the rename is not overwritten either.
        using (var session = FolderMoveSession.ForUndo(moved, default))
        {
            using (var late = FolderMoveSession.Open(Path.Combine(into, "相册"), root, default)) late.Execute(default); // occupies the original name
            check(Error(() => session.Execute(default)) == FolderMove.OriginalTaken && Directory.Exists(moved.Current), "Undo refuses when the original name was taken after the preview");
        }
        check(File.ReadAllText(Path.Combine(folder, "keep.txt")) == "keep", "The folder that took the original name is untouched");
        check(Error(() => FolderMoveSession.ForUndo(moved, default).Dispose()) == FolderMove.OriginalTaken, "Undo refuses up front while the original name is taken");
        Directory.Move(folder, Path.Combine(root, "other"));
        using (var session = FolderMoveSession.ForUndo(moved, default)) session.Execute(default);
        check(Directory.Exists(Path.Combine(folder, "sub")) && !Directory.Exists(moved.Current), "Undo works once the original name is free again");

        // Inside itself and the place it already is.
        check(Error(() => FolderMoveSession.Open(folder, Path.Combine(folder, "sub"), default).Dispose()) == FolderMove.Inside, "Moving a folder into its own sub-folder is refused");
        check(Error(() => FolderMoveSession.Open(folder, folder, default).Dispose()) == FolderMove.Inside, "Moving a folder into itself is refused");
        check(Error(() => FolderMoveSession.Open(folder, root, default).Dispose()) == FolderMove.AlreadyThere, "Moving a folder into the folder it is in is refused");
        string shortName = ShortPath(Path.Combine(folder, "sub"));
        if (shortName != Path.Combine(folder, "sub") && !shortName.Contains("相册", StringComparison.Ordinal))
            check(Error(() => FolderMoveSession.Open(folder, shortName, default).Dispose()) == FolderMove.Inside, "Another spelling of a sub-folder (an 8.3 short name) is refused too: " + shortName);
        check(NativeFiles.FolderOnVolumeOf(folder, into) == true, "The volume probe finds the destination on the folder's drive");

        // A moved folder replaced (or renamed away) before Undo.
        using (var session = FolderMoveSession.Open(folder, into, default)) moved = session.Execute(default);
        Directory.Move(moved.Current, Path.Combine(into, "swapped"));
        Directory.CreateDirectory(moved.Current);
        check(Error(() => FolderMoveSession.ForUndo(moved, default).Dispose()) == FolderMove.Replaced && Directory.Exists(moved.Current) && !Directory.Exists(folder),
            "Undo refuses a different folder now at the moved place");
        Directory.Delete(moved.Current);
        check(Error(() => FolderMoveSession.ForUndo(moved, default).Dispose()) == FolderMove.Replaced, "Undo refuses when the moved folder is gone from its place");
        Directory.Move(Path.Combine(into, "swapped"), moved.Current);
        using (var session = FolderMoveSession.ForUndo(moved, default)) session.Execute(default);
        check(Directory.Exists(Path.Combine(folder, "sub")), "The same folder, put back at its moved place, can be undone");

        // Undo of Copy Folder to….
        string target = Path.Combine(root, "copies"); Directory.CreateDirectory(target);
        FolderCopyOutcome copy, second;
        using (var work = FolderCopyWork.Open(folder, target, default)) { copy = work.Execute(default, null); second = work.Execute(default, null); }
        check(copy.Error is null && copy.Recorded && copy.RootIdentity is not null && copy.Entries.Count == 3, "A copy records its new folder's identity and the 3 items it created");
        if (!RecycleBin.Available(copy.Folder!)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleCopiedFolder(copy.Folder!, copy.RootIdentity!, copy.Entries);
        check(!Directory.Exists(copy.Folder) && stored.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(folder, "sub")),
            "Undoing the copy moves the new folder to the Recycle Bin and leaves the original folder");
        NativeFiles.RestoreRecycled(copy.Folder!, stored, true);
        check(File.ReadAllText(Path.Combine(copy.Folder!, "sub", "a.txt")) == "alpha", "Undo Move puts the copied folder back");
        File.WriteAllText(Path.Combine(copy.Folder!, "added.txt"), "x");
        check(Error(() => NativeFiles.RecycleCopiedFolder(copy.Folder!, copy.RootIdentity!, copy.Entries)) == FolderCopyUndo.Changed && Directory.Exists(copy.Folder), "A file added to the copy refuses the undo");
        File.Delete(Path.Combine(copy.Folder!, "added.txt"));
        File.AppendAllText(Path.Combine(copy.Folder!, "sub", "a.txt"), "!");
        check(Error(() => NativeFiles.RecycleCopiedFolder(copy.Folder!, copy.RootIdentity!, copy.Entries)) == FolderCopyUndo.Changed, "An edited file in the copy refuses the undo");
        File.Move(Path.Combine(second.Folder!, "sub", "a.txt"), Path.Combine(second.Folder!, "sub", "b.txt"));
        check(Error(() => NativeFiles.RecycleCopiedFolder(second.Folder!, second.RootIdentity!, second.Entries)) == FolderCopyUndo.Changed, "A renamed file in the copy refuses the undo");
        File.Move(Path.Combine(second.Folder!, "sub", "b.txt"), Path.Combine(second.Folder!, "sub", "a.txt"));
        check(Error(() => NativeFiles.RecycleCopiedFolder(copy.Folder!, second.RootIdentity!, second.Entries)) == FolderCopyUndo.Replaced && Directory.Exists(copy.Folder), "Another folder at the copy's place is refused by identity");
        stored = NativeFiles.RecycleCopiedFolder(second.Folder!, second.RootIdentity!, second.Entries);
        check(!Directory.Exists(second.Folder), "A file renamed back restores the match, and the copy can be undone");
        Directory.CreateDirectory(second.Folder!);
        reject(() => NativeFiles.RestoreRecycled(second.Folder!, stored, true), "Undo Move refuses when something new is in the copy's place");
        Directory.Delete(second.Folder!);
        NativeFiles.RestoreRecycled(second.Folder!, stored, true);
        check(Directory.Exists(Path.Combine(second.Folder!, "sub")), "The copy is back once its place is free (nothing left in the Recycle Bin)");
    }

    private static string ShortPath(string path)
    {
        var buffer = new System.Text.StringBuilder(1024);
        return GetShortPathName(path, buffer, buffer.Capacity) is > 0 and < 1024 ? buffer.ToString() : path;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathName(string path, System.Text.StringBuilder shortPath, int length);
}
