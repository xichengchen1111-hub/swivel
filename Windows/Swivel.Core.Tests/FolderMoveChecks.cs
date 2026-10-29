using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class FolderMoveChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string? Error(Action action) { try { action(); return null; } catch (UserError error) { return error.Message; } }
        // Destination rules: not the folder, not inside it, not where it already is.
        check(Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"C:\Work\Photos")) == FolderMove.Inside, "Moving a folder into itself is refused");
        check(Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"c:\work\photos\2024\")) == FolderMove.Inside, "Moving a folder into one of its sub-folders is refused, whatever the letter case");
        check(Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"c:\WORK")) == FolderMove.AlreadyThere, "Moving a folder into the folder it is already in is refused");
        check(Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"C:\Work\Photos 2")) is null && Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"C:\")) is null
            && Error(() => FolderMove.CheckDestination(@"C:\Work\Photos", @"D:\Work")) is null, "A sibling whose name starts the same, a higher folder and another drive pass the path rules");
        check(Error(() => FolderMove.CheckDestination(@"C:\", @"D:\Work")) == FolderMove.WholeDrive, "A whole drive is not a folder to move");

        // Numbered names: the claim is the rename itself, the next number on "name taken".
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Into\Photos", @"D:\Into\Photos 2" };
        var tried = new List<string>();
        string claimed = FolderMove.Claim(@"C:\Work\Photos", @"D:\Into\", path => { tried.Add(path); return !taken.Contains(path); });
        check(claimed == @"D:\Into\Photos 3" && tried.SequenceEqual(new[] { @"D:\Into\Photos", @"D:\Into\Photos 2", @"D:\Into\Photos 3" }), "A taken name moves on to the next free numbered name: " + claimed);
        check(FolderMove.Claim(@"C:\Work\Photos", @"D:\Into", _ => true) == @"D:\Into\Photos", "A free destination keeps the folder's own name");
        check(Error(() => FolderMove.Claim(@"C:\Work\Photos", @"D:\Into", _ => false)) == "Too many files have this output name.", "The numbered names stop after 10,000");
        bool stopped = false;
        try { FolderMove.Claim(@"C:\Work\Photos", @"D:\Into", _ => throw new UnauthorizedAccessException()); } catch (UnauthorizedAccessException) { stopped = true; }
        check(stopped, "Any other failure stops at once instead of trying the next number");

        // Undo receipts.
        var original = Directory(@"C:\Work"); var current = Directory(@"C:\Into");
        var identity = new MoveIdentity(1, 900, 9);
        var receipt = new FolderMoveReceipt(@"C:\Work\Photos", @"C:\Into\Photos 2", identity, original, current);
        check(Error(() => FolderMove.ForUndo(receipt)) is null, "A complete receipt is accepted for Undo");
        check(Error(() => FolderMove.ForUndo(receipt with { Current = @"C:\Other\Photos 2" })) == FolderMove.Incomplete, "A current path outside its recorded parent is refused");
        check(Error(() => FolderMove.ForUndo(receipt with { Identity = new(1, 0, 0) })) == FolderMove.Incomplete, "A receipt without a folder identity is refused");
        check(Error(() => FolderMove.ForUndo(receipt with { Identity = new(2, 900, 9) })) == FolderMove.Incomplete, "A folder identity on another volume is refused");
        check(Error(() => FolderMove.ForUndo(receipt with { Current = @"C:\Work\Photos", CurrentParent = original })) == FolderMove.Incomplete, "A receipt that moved nothing is refused");
        check(Error(() => FolderMove.ForUndo(receipt with { Identity = original.LeafIdentity })) == FolderMove.Incomplete, "The moved folder cannot be one of the recorded parents");
        check(Error(() => FolderMove.ForUndo(receipt with { OriginalParent = new(@"C:\Work", original.Ancestors.Take(1).ToArray()) })) == FolderMove.Incomplete, "A parent chain that does not match its path is refused");
        check(Error(() => FolderMove.ForUndo(null)) == FolderMove.Incomplete, "A missing receipt is refused");

        // Paths that point into the moved folder follow it; others stay.
        check(FolderMove.UpdatePath(@"C:\Work\Photos", @"C:\Work\Photos", @"C:\Into\Photos 2") == @"C:\Into\Photos 2"
            && FolderMove.UpdatePath(@"c:\work\photos\a\b.jpg", @"C:\Work\Photos", @"C:\Into\Photos 2") == @"C:\Into\Photos 2\a\b.jpg"
            && FolderMove.UpdatePath(@"C:\Work\Photos 2\b.jpg", @"C:\Work\Photos", @"C:\Into\Photos 2") == @"C:\Work\Photos 2\b.jpg", "Only paths at or below the moved folder follow it");

        // Copy Folder undo: exactly the recorded items, files by length and modified time.
        var recorded = new[] { new FolderCopyEntry("", true, 0, 0), new FolderCopyEntry("sub", true, 0, 0), new FolderCopyEntry(@"sub\a.txt", false, 5, 100) };
        FolderZipPlan Plan(params FolderZipItem[] items) => new("Photos", items, Array.Empty<FolderZipSkip>(), items.Count(i => !i.Directory), items.Count(i => i.Directory) - 1, items.Sum(i => i.Length));
        var same = Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 777), new("Photos/sub/a.txt", @"sub\a.txt", false, 5, 100));
        check(FolderCopyUndo.Compare(recorded, same) is null, "The untouched copy matches what was copied, whatever its folders' times say");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 0), new("Photos/sub/a.txt", @"sub\a.txt", false, 6, 100))) == FolderCopyUndo.Changed, "A resized file refuses the undo");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 0), new("Photos/sub/a.txt", @"sub\a.txt", false, 5, 101))) == FolderCopyUndo.Changed, "A modified file refuses the undo");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 0), new("Photos/sub/A.txt", @"sub\A.txt", false, 5, 100))) == FolderCopyUndo.Changed, "A renamed file (even only its letter case) refuses the undo");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 0), new("Photos/sub/a.txt", @"sub\a.txt", false, 5, 100), new("Photos/b.txt", "b.txt", false, 0, 1))) == FolderCopyUndo.Changed, "An added file refuses the undo");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub/", "sub", true, 0, 0))) == FolderCopyUndo.Changed, "A removed file refuses the undo");
        check(FolderCopyUndo.Compare(recorded, Plan(new("Photos/", "", true, 0, 0), new("Photos/sub", "sub", false, 0, 0), new("Photos/sub/a.txt", @"sub\a.txt", false, 5, 100))) == FolderCopyUndo.Changed, "A folder replaced by a file refuses the undo");
        check(FolderCopyUndo.Compare(recorded, same with { Skipped = new[] { new FolderZipSkip("link", "Link or junction (not followed)") } }) == FolderCopyUndo.Changed, "A link added inside refuses the undo");
        check(FolderCopyUndo.Compare(Array.Empty<FolderCopyEntry>(), same) == FolderCopyUndo.Changed, "Nothing recorded means nothing to compare, so the undo is refused");
        check(Catalog.Actions.Any(a => a.Id == "file.movefolder" && a.Feature == Feature.FileMoves && !a.RequiresFiles), "Move Folder to is a Move Files action that picks its own folders");
    }

    private static MoveDirectorySnapshot Directory(string path)
    {
        var parts = new List<MoveDirectoryEntry> { new(@"C:\", new(1, 500, 0)) }; string current = @"C:\";
        foreach (string component in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        { current = current.TrimEnd('\\') + "\\" + component; ulong id = 600; foreach (char c in current) id = unchecked(id * 31 + c); parts.Add(new(current, new(1, id, 7))); }
        return new(path, parts.AsReadOnly());
    }
}
