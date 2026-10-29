using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class MoveFilesNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // This fixture is Windows/NTFS-only. Cross-compiling it is not execution.
        string root = Path.Combine(directory, "move-files"), a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), c = Path.Combine(root, "c");
        foreach (string path in new[] { a, b, c }) Directory.CreateDirectory(path);
        string first = Path.Combine(a, "first.dat"), second = Path.Combine(a, "noextension"), third = Path.Combine(a, ".profile");
        byte[] body = Encoding.UTF8.GetBytes("main file contents\n世界"), zone = Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/source\r\n"), extra = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(first, body); File.WriteAllBytes(first + ":Zone.Identifier", zone); File.WriteAllBytes(first + ":binary metadata", extra); File.WriteAllBytes(first + ":empty", Array.Empty<byte>());
        File.WriteAllText(second, "second"); File.WriteAllText(third, "third");
        string[] selected = { first, second, third }; MoveOutcome moved;
        reject(() => MoveSession.Open(selected, b, new CancellationToken(true)), "Native pre-cancel acquires no move session");
        using (var session = MoveSession.Open(selected, b, default))
        {
            check(session.Plan.Select(p => p.Source).SequenceEqual(selected) && session.Plan.All(p => p.Changed && Path.GetFileName(p.Source) == Path.GetFileName(p.Destination)), "Native move preview keeps all original leaf names");
            reject(() => Directory.Move(a, a + " displaced"), "Native source parent held throughout preview");
            reject(() => Directory.Move(b, b + " displaced"), "Native destination parent held throughout preview");
            reject(() => Directory.Move(root, root + " displaced"), "Native shared ancestor held throughout preview");
            reject(() => File.AppendAllText(first, "changed"), "Native move source rejects default-stream writers during preview");
            moved = session.Execute(default);
            check(moved.Error is null && moved.Remaining.Count == 3 && moved.Locations.All(x => x.Stamp is { } after && x.BeforeStamp is { } before && after.Identity == before.Identity), "Native same-volume move preserves full object identities: remaining " + moved.Remaining.Count + " " + moved.Error);
            check(moved.Locations.All(x => x.CurrentDirectory.Ancestors.Count >= 3 && x.OriginalDirectory.Ancestors.Count >= 3), "Native receipt records both full ancestor identity chains");
            reject(() => session.Execute(default), "Native move session executes at most once");
        }
        string movedFirst = Path.Combine(b, "first.dat");
        check(!File.Exists(first) && File.ReadAllBytes(movedFirst).SequenceEqual(body) && File.ReadAllBytes(movedFirst + ":Zone.Identifier").SequenceEqual(zone) && File.ReadAllBytes(movedFirst + ":binary metadata").SequenceEqual(extra) && File.ReadAllBytes(movedFirst + ":empty").Length == 0, "Native move preserves unnamed, named binary, empty, and Zone.Identifier data streams byte-for-byte");
        MoveOutcome undone;
        using (var session = MoveSession.ForUndo(moved.Remaining, default)) undone = session.Execute(default);
        check(undone.Error is null && MoveReceipts.AfterUndo(moved.Remaining, undone).Count == 0 && File.ReadAllBytes(first + ":Zone.Identifier").SequenceEqual(zone), "Native undo restores original object and Zone.Identifier");
        using (var session = MoveSession.Open(selected, a, default))
        { var same = session.Execute(default); check(session.Plan.All(p => !p.Changed) && same.Error is null && same.Remaining.Count == 0, "Native same-directory identity produces visible noops"); }
        string single = Path.Combine(a, "x"); File.WriteAllText(single, "one");
        MoveOutcome one;
        using (var session = MoveSession.Open(new[] { single }, c, default)) one = session.Execute(default); // the session holds the file until disposed
        check(one.Error is null && File.ReadAllText(Path.Combine(c, "x")) == "one" && !File.Exists(single), "Native move of a one-character name meets the minimum rename buffer: " + one.Error);
        File.Delete(Path.Combine(c, "x"));
        string alias = a[..^1] + "A";
        if (Directory.Exists(alias))
            using (var session = MoveSession.Open(selected, alias, default)) check(session.Plan.All(p => !p.Changed), "Native case alias of the same held directory also produces noops");

        // A read-only file moves and stays read-only; undo brings it back.
        File.SetAttributes(third, File.GetAttributes(third) | FileAttributes.ReadOnly);
        try
        {
            MoveOutcome ro;
            using (var session = MoveSession.Open(new[] { third }, b, default)) ro = session.Execute(default);
            string movedThird = Path.Combine(b, Path.GetFileName(third));
            check(ro.Error is null && File.Exists(movedThird) && !File.Exists(third) && (File.GetAttributes(movedThird) & FileAttributes.ReadOnly) != 0, "Native move of a read-only file keeps it read-only: " + ro.Error?.Message);
            using (var session = MoveSession.ForUndo(ro.Remaining, default)) check(session.Execute(default).Error is null && File.Exists(third), "Native undo brings the read-only file back");
        }
        finally { File.SetAttributes(third, File.GetAttributes(third) & ~FileAttributes.ReadOnly); }
        check(selected.All(File.Exists) && !Directory.EnumerateFiles(b).Any(), "Native read-only move and undo leave the destination empty again");
        string other = Path.Combine(c, "first.dat"); File.WriteAllText(other, "other original");
        reject(() => MoveSession.Open(new[] { first, other }, b, default), "Duplicate target basenames refuse the entire native batch");
        File.Delete(other);
        reject(() => MoveSession.Open(new[] { first, first }, b, default), "Repeated source is refused without deduplication");
        using (var session = MoveSession.Open(selected, b, default))
        {
            string occupied = Path.Combine(b, ".profile"); File.WriteAllText(occupied, "unrelated");
            var failed = session.Execute(default);
            check(failed.Error is not null && failed.Remaining.Count == 0 && selected.All(File.Exists) && File.ReadAllText(occupied) == "unrelated", "Destination created after preview blocks all moves and is never replaced");
            File.Delete(occupied);
        }
        using (var session = MoveSession.Open(selected, b, default))
        { var cancelled = session.Execute(new CancellationToken(true)); check(cancelled.Cancelled && cancelled.Remaining.Count == 0 && selected.All(File.Exists), "Native cancellation after preview leaves originals intact"); }

        // Open stream leases participate in deletion sharing across the entire file.
        using (var adsReader = new FileStream(first + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read))
            reject(() => MoveSession.Open(new[] { first }, b, default), "An ADS handle denying delete sharing blocks move before mutation");
        string hardlink = Path.Combine(c, "hardlink.dat");
        check(CreateHardLink(hardlink, first, IntPtr.Zero), "Native NTFS hardlink fixture created");
        try
        {
            // Only the chosen name moves; the other name stays. Both names of one file in a batch are refused.
            MoveOutcome linked;
            using (var session = MoveSession.Open(new[] { hardlink }, b, default)) linked = session.Execute(default);
            string movedLink = Path.Combine(b, "hardlink.dat");
            check(linked.Error is null && File.Exists(movedLink) && !File.Exists(hardlink) && File.Exists(first) && File.ReadAllBytes(movedLink).SequenceEqual(File.ReadAllBytes(first)), "Native move of one hard-link name leaves the other: " + linked.Error?.Message);
            using (var session = MoveSession.ForUndo(linked.Remaining, default)) check(session.Execute(default).Error is null && File.Exists(hardlink), "Native undo brings the moved link name back");
            reject(() => MoveSession.Open(new[] { first, hardlink }, b, default), "Two names of one file in a move batch are refused");
        }
        finally { File.Delete(hardlink); }

        // Directory replacement at the same spelling must not redirect undo.
        MoveOutcome directoryMove;
        using (var session = MoveSession.Open(new[] { first }, b, default)) directoryMove = session.Execute(default);
        string savedA = a + " saved"; Directory.Move(a, savedA); Directory.CreateDirectory(a);
        reject(() => MoveSession.ForUndo(directoryMove.Remaining, default), "Undo refuses replaced original directory despite identical path");
        check(File.Exists(movedFirst) && !File.Exists(first), "Refused directory-identity undo leaves moved file intact");
        Directory.Delete(a); Directory.Move(savedA, a);
        using (var session = MoveSession.ForUndo(directoryMove.Remaining, default)) check(session.Execute(default).Error is null, "Restoring the exact original directory permits identity-checked undo");

        MoveOutcome batch1, batch2, undo2;
        using (var session = MoveSession.Open(new[] { first }, b, default)) batch1 = session.Execute(default);
        using (var session = MoveSession.Open(new[] { movedFirst }, c, default)) batch2 = session.Execute(default);
        using (var session = MoveSession.ForUndo(batch2.Remaining, default)) undo2 = session.Execute(default);
        var earlier = MoveReceipts.RefreshHistory(batch1.Remaining, undo2, batch2.Remaining);
        using (var session = MoveSession.ForUndo(earlier, default)) check(session.Execute(default).Error is null && File.Exists(first), "Actual ChangeTime refresh supports consecutive native undo batches");

        using (var session = MoveSession.Open(new[] { first }, b, default)) batch1 = session.Execute(default);
        File.AppendAllText(movedFirst, "external edit");
        reject(() => MoveSession.ForUndo(batch1.Remaining, default), "Native undo refuses edited source metadata");
        using (var session = MoveSession.Open(new[] { movedFirst }, c, default)) batch2 = session.Execute(default);
        using (var session = MoveSession.ForUndo(batch2.Remaining, default)) undo2 = session.Execute(default);
        var unwashed = MoveReceipts.RefreshHistory(batch1.Remaining, undo2, batch2.Remaining);
        reject(() => MoveSession.ForUndo(unwashed, default), "Later move and undo do not wash native external edits out of earlier history");
        check(File.ReadAllBytes(movedFirst + ":Zone.Identifier").SequenceEqual(zone) && File.ReadAllBytes(movedFirst + ":binary metadata").SequenceEqual(extra), "Repeated moves preserve all original ADS including MOTW");

        foreach (var drive in DriveInfo.GetDrives())
            if ((drive.DriveType is DriveType.Fixed or DriveType.Removable) && drive.IsReady && !string.Equals(drive.RootDirectory.FullName, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            {
                reject(() => MoveSession.Open(new[] { movedFirst }, drive.RootDirectory.FullName, default), "Native cross-volume move refused without copy/delete");
                if (drive.DriveFormat != "NTFS") Console.WriteLine("SKIP: the other drive is not NTFS, so the volume count is not checked.");
                else check(NativeFiles.CountOnVolumeOf(new[] { movedFirst }, drive.RootDirectory.FullName) == 0, "A file on another NTFS drive is counted as not on the destination volume, so Move Files offers a copy instead");
                break;
            }
        check(!Directory.EnumerateFiles(root, ".swivel-*", SearchOption.AllDirectories).Any(), "Move never creates a temporary copy or staging file");
        Directory.Move(root, root + " released"); check(Directory.Exists(root + " released"), "All native file and directory leases are released after every failure and success");
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
