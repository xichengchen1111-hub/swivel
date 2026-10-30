using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;
using Swivel.Windows;

// Batch Rename, Move Files, Move Folder and Copy to Folder on real exFAT and FAT32 volumes. CI
// creates small virtual disks before the suite (Windows/Swivel.Windows.Tests/fat-volumes.ps1) and names them in
// SWIVEL_TEST_EXFAT / SWIVEL_TEST_FAT32; without them these cases are skipped and say so.
internal static class FatVolumeNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        bool any = false;
        foreach (var (variable, expected) in new[] { ("SWIVEL_TEST_EXFAT", "exFAT"), ("SWIVEL_TEST_FAT32", "FAT32") })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Console.WriteLine("SKIP: " + variable + " is not set; " + expected + " cases need a test volume."); continue; }
            check(new DriveInfo(root).DriveFormat == expected, expected + " test volume has the expected file system");
            any = true;
            string work = Path.Combine(root, "swivel-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            Rename(work, expected, check, reject);
            Move(work, directory, expected, check, reject);
            FolderMove(work, expected, check, reject);
            Copy(work, directory, expected, check, reject);
            NativeFiles.RetainFatHolds(Array.Empty<ulong>());
            check(NativeFiles.FatHoldCount == 0, expected + ": releasing the histories releases every kept reference");
        }
        if (!any) Console.WriteLine("SKIP: no exFAT/FAT32 test volume; FAT cases were not run.");
    }

    private static string Stored(string path) => NativeFiles.StoredName(path);
    private static RenameOutcome Undo(RenameOutcome done) { using var undo = RenameSession.ForUndo(done.Remaining, CancellationToken.None, true); return undo.Execute(done.Remaining.Select(x => new RenamePair(x.Current, x.Original)).ToArray(), CancellationToken.None); }

    private static void Rename(string work, string fs, Action<bool, string> check, Action<Action, string> reject)
    {
        string dir = Path.Combine(work, "rename"); Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "original A"); File.WriteAllText(b, "original B");
        var paths = new[] { a, b };
        reject(() => RenameSession.Open(paths, CancellationToken.None).Dispose(), fs + ": agents' rename (no allowFat) still refuses FAT");
        reject(() => RenameSession.Open(new[] { Path.Combine(dir, "A.TXT") }, CancellationToken.None, true).Dispose(), fs + ": a path in other letter case is refused (exact stored name required)");

        // Long names need more directory entries, so the FAT file index changes: identity is the held handle.
        var plan = new RenameRules(Base: "a-very-long-renamed-file-name-that-needs-several-entries", Number: true).Build(paths);
        RenameOutcome done;
        using (var session = RenameSession.Open(paths, CancellationToken.None, true))
        {
            check(session.IsFat, fs + ": rename session knows it is on FAT");
            reject(() => File.WriteAllText(a, "changed"), fs + ": rename lease blocks writes during the batch");
            done = session.Execute(plan, CancellationToken.None);
        }
        check(done.Error is null && done.Remaining.Count == 2 && File.ReadAllText(plan[0].Destination) == "original A" && File.ReadAllText(plan[1].Destination) == "original B", fs + ": batch rename to long names keeps contents");
        check(NativeFiles.FatHoldCount >= 2, fs + ": a reference is kept per renamed file");
        File.ReadAllText(plan[0].Destination); // other apps can still read while the reference is kept
        // Does a kept reference stop the folder holding it from being moved? Reported, not required.
        string aside = Path.Combine(work, "aside"); Directory.CreateDirectory(aside);
        try
        {
            FolderMoveReceipt parentMove;
            using (var session = FolderMoveSession.Open(dir, aside, CancellationToken.None, true)) parentMove = session.Execute(CancellationToken.None);
            using (var back = FolderMoveSession.ForUndo(parentMove, CancellationToken.None, true)) back.Execute(CancellationToken.None);
            Console.WriteLine("INFO: " + fs + ": the parent folder could be moved (and moved back) while references were kept.");
        }
        catch (Exception error) when (error is UserError or System.ComponentModel.Win32Exception) { Console.WriteLine("INFO: " + fs + ": moving the parent folder was refused while references were kept: " + error.Message); }
        check(Directory.Exists(dir) && File.Exists(plan[0].Destination), fs + ": the rename folder is back in place after the parent-move probe");
        var restored = Undo(done);
        check(restored.Error is null && File.ReadAllText(a) == "original A" && File.ReadAllText(b) == "original B" && !File.Exists(plan[0].Destination), fs + ": undo puts both names back through the kept references");

        // Case-only rename: FAT accepts it but changes nothing, so it must go through a temporary name.
        string upper = Path.Combine(dir, "A.txt");
        using (var session = RenameSession.Open(new[] { a }, CancellationToken.None, true))
            done = session.Execute(new[] { new RenamePair(a, upper) }, CancellationToken.None);
        check(done.Error is null && Stored(upper) == "A.txt" && File.ReadAllText(upper) == "original A", fs + ": case-only rename really changes the stored name");
        restored = Undo(done);
        check(restored.Error is null && Stored(a) == "a.txt", fs + ": case-only rename is undone");

        using (var session = RenameSession.Open(paths, CancellationToken.None, true))
            done = session.Execute(new[] { new RenamePair(a, b), new RenamePair(b, a) }, CancellationToken.None);
        check(done.Error is null && File.ReadAllText(a) == "original B" && File.ReadAllText(b) == "original A", fs + ": swap keeps both contents");
        check(Undo(done).Error is null && File.ReadAllText(a) == "original A", fs + ": swap is undone");

        // Refusals: an existing destination, then undo after another app renamed, edited or removed the file.
        File.WriteAllText(Path.Combine(dir, "new-b.txt"), "taken");
        using (var session = RenameSession.Open(paths, CancellationToken.None, true))
            reject(() => session.Execute(new RenameRules(Prefix: "new-").Build(paths), CancellationToken.None), fs + ": every destination is checked before the first rename");
        check(File.Exists(a) && File.Exists(b) && File.ReadAllText(Path.Combine(dir, "new-b.txt")) == "taken", fs + ": a conflict changes nothing");
        File.Delete(Path.Combine(dir, "new-b.txt"));

        plan = new RenameRules(Prefix: "x-").Build(paths);
        using (var session = RenameSession.Open(paths, CancellationToken.None, true)) done = session.Execute(plan, CancellationToken.None);
        File.Move(plan[0].Destination, Path.Combine(dir, "elsewhere.txt"));
        Refused(() => Undo(done), FatRules.MovedElsewhere, fs + ": undo refuses a file another app renamed", check);
        check(File.Exists(Path.Combine(dir, "elsewhere.txt")) && File.Exists(plan[1].Destination), fs + ": refused undo changes nothing");
        File.Move(Path.Combine(dir, "elsewhere.txt"), plan[0].Destination);
        File.AppendAllText(plan[1].Destination, " edited");
        reject(() => Undo(done), fs + ": undo refuses a file edited since");
        // A deleted file stays pending while its reference is kept, so no new file can take its name.
        File.Delete(plan[0].Destination);
        reject(() => File.WriteAllText(plan[0].Destination, "impostor"), fs + ": a deleted name cannot be taken while its reference is kept");
        reject(() => Undo(done), fs + ": undo refuses a deleted file");
        NativeFiles.RetainFatHolds(Array.Empty<ulong>());
        check(!File.Exists(plan[0].Destination), fs + ": the deleted file goes once the reference is released");
        Refused(() => Undo(done), FatRules.Released, fs + ": undo after the references were released is refused", check);
        check(File.Exists(plan[1].Destination) && File.ReadAllText(plan[1].Destination) == "original B edited", fs + ": refused undo leaves the remaining file alone");
    }

    private static void Move(string work, string ntfs, string fs, Action<bool, string> check, Action<Action, string> reject)
    {
        string source = Path.Combine(work, "move-src"), target = Path.Combine(work, "move-dst");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        string one = Path.Combine(source, "one.txt"), two = Path.Combine(source, "a long file name that needs more entries.txt");
        File.WriteAllText(one, "one"); File.WriteAllText(two, "two");
        reject(() => MoveSession.Open(new[] { one }, target, CancellationToken.None).Dispose(), fs + ": agents' move (no allowFat) still refuses FAT");
        check(NativeFiles.CountOnVolumeOf(new[] { one, two }, target) == 2, fs + ": files are counted on their own FAT volume");
        check(NativeFiles.CountOnVolumeOf(new[] { one }, ntfs) == 0, fs + ": an NTFS destination counts as another drive, so a copy is offered");
        reject(() => MoveSession.Open(new[] { one }, ntfs, CancellationToken.None, true).Dispose(), fs + ": moving from FAT to an NTFS folder is refused");
        MoveOutcome done;
        using (var session = MoveSession.Open(new[] { one, two }, target, CancellationToken.None, true))
        {
            check(session.IsFat, fs + ": move session knows it is on FAT");
            done = session.Execute(CancellationToken.None);
        }
        string movedOne = Path.Combine(target, "one.txt"), movedTwo = Path.Combine(target, Path.GetFileName(two));
        check(done.Error is null && done.Remaining.Count == 2 && File.ReadAllText(movedOne) == "one" && File.ReadAllText(movedTwo) == "two" && !File.Exists(one), fs + ": files move within the drive with their contents");
        MoveOutcome back;
        using (var undo = MoveSession.ForUndo(done.Remaining, CancellationToken.None, true)) back = undo.Execute(CancellationToken.None);
        check(back.Error is null && File.ReadAllText(one) == "one" && File.ReadAllText(two) == "two" && !File.Exists(movedOne), fs + ": move undo puts both back through the kept references");

        using (var session = MoveSession.Open(new[] { one }, target, CancellationToken.None, true)) done = session.Execute(CancellationToken.None);
        File.Move(movedOne, Path.Combine(target, "renamed.txt"));
        reject(() => MoveSession.ForUndo(done.Remaining, CancellationToken.None, true).Dispose(), fs + ": move undo refuses a file another app renamed");
        check(File.Exists(Path.Combine(target, "renamed.txt")) && !File.Exists(one), fs + ": refused move undo changes nothing");
        File.Move(Path.Combine(target, "renamed.txt"), one);
    }

    private static void FolderMove(string work, string fs, Action<bool, string> check, Action<Action, string> reject)
    {
        string source = Path.Combine(work, "fsrc"), target = Path.Combine(work, "fdst"), folder = Path.Combine(source, "Holiday photos");
        Directory.CreateDirectory(Path.Combine(folder, "inner")); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(folder, "inner", "p.txt"), "picture");
        reject(() => FolderMoveSession.Open(folder, target, CancellationToken.None).Dispose(), fs + ": agents' folder move (no allowFat) still refuses FAT");
        reject(() => FolderMoveSession.Open(folder, Path.Combine(folder, "inner"), CancellationToken.None, true).Dispose(), fs + ": a folder cannot move into itself");
        check(NativeFiles.FolderOnVolumeOf(folder, target) == true, fs + ": Move Folder sees the same drive");
        FolderMoveReceipt receipt; int holds = NativeFiles.FatHoldCount;
        using (var session = FolderMoveSession.Open(folder, target, CancellationToken.None, true))
        {
            check(session.IsFat, fs + ": folder move session knows it is on FAT");
            receipt = session.Execute(CancellationToken.None);
        }
        check(NativeFiles.FatHoldCount == holds + 1, fs + ": a reference is kept to the moved folder");
        string moved = Path.Combine(target, "Holiday photos");
        check(receipt.Current == moved && File.ReadAllText(Path.Combine(moved, "inner", "p.txt")) == "picture" && !Directory.Exists(folder), fs + ": the folder moves whole within the drive");
        using (var undo = FolderMoveSession.ForUndo(receipt, CancellationToken.None, true)) undo.Execute(CancellationToken.None);
        check(Directory.Exists(folder) && File.ReadAllText(Path.Combine(folder, "inner", "p.txt")) == "picture" && !Directory.Exists(moved), fs + ": folder move undo puts it back");

        using (var session = FolderMoveSession.Open(folder, target, CancellationToken.None, true)) receipt = session.Execute(CancellationToken.None);
        Directory.Move(moved, Path.Combine(target, "Renamed by someone"));
        Refused(() => FolderMoveSession.ForUndo(receipt, CancellationToken.None, true).Dispose(), FatRules.MovedElsewhere, fs + ": folder undo refuses a folder another app renamed", check);
        check(Directory.Exists(Path.Combine(target, "Renamed by someone")) && !Directory.Exists(folder), fs + ": refused folder undo changes nothing");
        Directory.Move(Path.Combine(target, "Renamed by someone"), folder);
    }

    private static void Copy(string work, string ntfs, string fs, Action<bool, string> check, Action<Action, string> reject)
    {
        string into = Path.Combine(work, "copies"); Directory.CreateDirectory(into);
        string plain = Path.Combine(ntfs, "fat-plain-" + fs + ".txt"), zoned = Path.Combine(ntfs, "fat-zoned-" + fs + ".txt"), extra = Path.Combine(ntfs, "fat-extra-" + fs + ".txt");
        File.WriteAllText(plain, "plain"); File.WriteAllText(zoned, "downloaded"); File.WriteAllText(extra, "extra");
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
        File.WriteAllText(zoned + ":Zone.Identifier", zone); File.WriteAllText(extra + ":thumb", "x");

        CopyOutcome outcome;
        using (var session = CopySession.Open(new[] { plain }, into, CancellationToken.None))
        {
            check(session.IsFat && session.ZoneMarked.Count == 0, fs + ": copy session knows the destination is FAT");
            outcome = session.Execute(CancellationToken.None);
        }
        check(outcome.Error is null && outcome.Copied.Count == 1 && File.ReadAllText(outcome.Copied[0].Destination) == "plain" && outcome.Copied[0].Verified && outcome.Copied[0].Stamp is null, fs + ": a plain file copies, is verified and is not offered for undo");
        check((File.GetLastWriteTimeUtc(outcome.Copied[0].Destination) - File.GetLastWriteTimeUtc(plain)).Duration() < TimeSpan.FromSeconds(2.1), fs + ": the copy keeps the modified time (FAT's 2-second steps)");

        using (var session = CopySession.Open(new[] { zoned }, into, CancellationToken.None))
        {
            check(session.ZoneMarked.SequenceEqual(new[] { zoned }), fs + ": a file with the download warning is listed for consent");
            outcome = session.Execute(CancellationToken.None);
        }
        check(outcome.Copied.Count == 0 && outcome.Error is UserError { Template: FatRules.ZoneRefused } && !File.Exists(Path.Combine(into, Path.GetFileName(zoned))), fs + ": without consent the download warning is never silently dropped");
        using (var session = CopySession.Open(new[] { zoned }, into, CancellationToken.None))
        {
            session.AcceptZoneLoss = true; outcome = session.Execute(CancellationToken.None);
        }
        check(outcome.Error is null && outcome.Copied.Count == 1 && File.ReadAllText(outcome.Copied[0].Destination) == "downloaded", fs + ": with consent the file copies without its download warning (CopyFile2: " + (outcome.Error?.Message ?? "ok") + ")");
        check(File.ReadAllText(zoned + ":Zone.Identifier") == zone, fs + ": the original keeps its download warning");
        Refused(() => CopySession.Open(new[] { extra }, into, CancellationToken.None).Dispose(), FatRules.OtherStreams, fs + ": another data stream refuses the copy up front", check);

        // FAT32's size limit is read from the length; the sparse file never writes 4 GB.
        string big = Path.Combine(ntfs, "fat-big-" + fs + ".bin");
        using (var stream = new FileStream(big, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            check(DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "Sparse test file marked sparse");
            stream.SetLength(4L << 30);
        }
        try
        {
            if (fs == "FAT32") Refused(() => CopySession.Open(new[] { big }, into, CancellationToken.None).Dispose(), FatRules.TooBig, fs + ": a 4 GB file is refused before copying", check);
            else Refused(() => CopySession.Open(new[] { big }, into, CancellationToken.None).Dispose(), "There is not enough free space in the destination folder.", fs + ": exFAT has no 4 GB limit (only the small test volume's free space refuses it)", check);
        }
        finally { File.Delete(big); }

        // From FAT to NTFS: the source has only its main stream; the NTFS copy is verified and offered for undo.
        string fatFile = Path.Combine(work, "from-fat.txt"); File.WriteAllText(fatFile, "on fat");
        using (var session = CopySession.Open(new[] { fatFile }, ntfs, CancellationToken.None)) outcome = session.Execute(CancellationToken.None);
        check(outcome.Error is null && outcome.Copied.Count == 1 && outcome.Copied[0].Stamp is not null && File.ReadAllText(outcome.Copied[0].Destination) == "on fat", fs + ": a file on FAT copies to NTFS and is verified");
        File.Delete(outcome.Copied[0].Destination);

        // Copy Folder to a FAT drive: refused names that differ only in case, consent for the warning.
        string tree = Path.Combine(ntfs, "fat-tree-" + fs); Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllText(Path.Combine(tree, "sub", "a.txt"), "a");
        File.WriteAllText(Path.Combine(tree, "dl.txt"), "dl"); File.WriteAllText(Path.Combine(tree, "dl.txt") + ":Zone.Identifier", zone);
        FolderCopyOutcome copied;
        using (var folderCopy = FolderCopyWork.Open(tree, into, CancellationToken.None))
        {
            check(folderCopy.IsFat && folderCopy.ZoneMarked == 1, fs + ": Copy Folder counts the files with the download warning");
            copied = folderCopy.Execute(CancellationToken.None, null);
        }
        check(copied.Error is UserError { Template: FatRules.ZoneRefused } && !copied.Recorded, fs + ": Copy Folder without consent stops at the marked file");
        using (var folderCopy = FolderCopyWork.Open(tree, into, CancellationToken.None))
        {
            folderCopy.AcceptZoneLoss = true; copied = folderCopy.Execute(CancellationToken.None, null);
        }
        check(copied.Error is null && copied.FilesCopied == 2 && !copied.Recorded && copied.RootIdentity is null && File.ReadAllText(Path.Combine(copied.Folder!, "sub", "a.txt")) == "a", fs + ": Copy Folder with consent copies the tree; undo is not offered");
        string sensitive = Path.Combine(ntfs, "fat-case-" + fs); Directory.CreateDirectory(sensitive);
        bool caseSensitive = Fsutil("file setCaseSensitiveInfo \"" + sensitive + "\" enable");
        if (caseSensitive)
        {
            File.WriteAllText(Path.Combine(sensitive, "read.txt"), "1"); File.WriteAllText(Path.Combine(sensitive, "READ.txt"), "2");
            caseSensitive = Directory.GetFiles(sensitive).Length == 2;
            if (!caseSensitive) Console.WriteLine("SKIP: fsutil reported success but the folder is not case-sensitive here; the case-clash rule is covered by core checks.");
        }
        if (caseSensitive)
        {
            // The folder walk already leaves the second name out and lists it, for every destination.
            using (var caseCopy = FolderCopyWork.Open(sensitive, into, CancellationToken.None))
                check(caseCopy.Plan.Items.Count(x => !x.Directory) == 1 && caseCopy.Plan.Skipped.Count == 1 && caseCopy.Plan.Skipped[0].Reason == "Same name as another item except for letter case",
                    fs + ": of two names differing only in case, one is copied and the other listed as skipped, never silently lost");
            Fsutil("file setCaseSensitiveInfo \"" + sensitive + "\" disable");
        }
        else if (!Directory.Exists(sensitive) || Directory.GetFiles(sensitive).Length == 0) Console.WriteLine("SKIP: case-sensitive NTFS folders are not available here; the case-clash rule is covered by core checks.");
    }

    private static void Refused(Action action, string template, string name, Action<bool, string> check)
    {
        try { action(); check(false, name + " (not refused)"); }
        catch (UserError error) { check(error.Template == template, name + " (got: " + error.Message + ")"); }
    }

    private static bool Fsutil(string arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "fsutil.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string part in System.Text.RegularExpressions.Regex.Matches(arguments, "\"[^\"]*\"|\\S+").Select(m => m.Value.Trim('"'))) start.ArgumentList.Add(part);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
