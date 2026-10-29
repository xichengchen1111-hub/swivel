using System;
using System.Linq;
using Swivel.Core;

internal static class FolderCopyChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string? Error(Action action) { try { action(); return null; } catch (UserError error) { return error.Message; } }
        // The destination may not be the folder or inside it; a sibling with a longer name is fine.
        check(Error(() => FolderCopy.CheckDestination(@"C:\Work\Photos", @"C:\Work\Photos")) == FolderCopy.Inside, "Copying a folder into itself is refused");
        check(Error(() => FolderCopy.CheckDestination(@"C:\Work\Photos", @"c:\work\photos\2024\")) == FolderCopy.Inside, "Copying a folder into one of its sub-folders is refused, whatever the letter case");
        check(Error(() => FolderCopy.CheckDestination(@"C:\Work\Photos", @"C:\Work\Photos 2")) is null && Error(() => FolderCopy.CheckDestination(@"C:\Work\Photos", @"C:\Work")) is null,
            "A sibling whose name starts the same, and the folder's own parent, are fine");
        check(FolderCopy.FolderName("Photos", 1) == "Photos" && FolderCopy.FolderName("Photos", 2) == "Photos 2" && FolderCopy.FolderName("Photos", 10000) == "Photos 10000", "New folder names count up from the folder's own name");
        // Every copy's path, with room for a numbered name, must fit 240 characters.
        var plan = new FolderZipPlan("Photos", new[] { new FolderZipItem("Photos/", "", true, 0, 0), new FolderZipItem("Photos/" + new string('a', 100), new string('a', 100), false, 1, 0) }, Array.Empty<FolderZipSkip>(), 1, 0, 1);
        // D:\ (3) + n + \ + Photos (6) + room for " 10000" (6) + \ + 100 ≤ 240, so n = 123 fits and 124 does not.
        check(Error(() => FolderCopy.CheckLengths(plan, @"D:\" + new string('x', 123))) is null, "A destination leaving exactly 240 characters is accepted");
        check(Error(() => FolderCopy.CheckLengths(plan, @"D:\" + new string('x', 124))) == FolderCopy.TooLong, "One character more is refused before anything is copied");
        check(Catalog.Actions.Any(a => a.Id == "file.copyfolder" && a.Feature == Feature.FileMoves && !a.RequiresFiles), "Copy Folder to is a Move Files action that picks its own folders");
    }
}
