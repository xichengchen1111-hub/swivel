using System;
using System.IO;
using System.Linq;
using System.Text;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderCopyNativeChecks
{
    // Copy Folder to… on real NTFS folders: the tree, data streams and times arrive; nothing is overwritten.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(directory, "folder-copy"); Directory.CreateDirectory(root);
        string folder = Path.Combine(root, "相册 2024");
        Directory.CreateDirectory(Path.Combine(folder, "sub", "empty"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "alpha", Encoding.UTF8);
        var big = new byte[2_000_000]; new Random(4).NextBytes(big);
        File.WriteAllBytes(Path.Combine(folder, "sub", "big.bin"), big);
        File.WriteAllText(Path.Combine(folder, "a.txt:Zone.Identifier"), "[ZoneTransfer]\r\nZoneId=3\r\n");
        var stamp = new DateTime(2022, 2, 2, 9, 0, 0, DateTimeKind.Local); File.SetLastWriteTime(Path.Combine(folder, "a.txt"), stamp);
        string ini = Path.Combine(folder, "desktop.ini"); File.WriteAllText(ini, "x"); File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        string target = Path.Combine(root, "target"); Directory.CreateDirectory(target);

        FolderCopyOutcome outcome;
        using (var work = FolderCopyWork.Open(folder, target, default))
        {
            check(work.Plan.Files == 2 && work.Plan.Folders == 2 && work.Plan.Skipped.Count == 1, "The plan has 2 files, 2 sub-folders and skips desktop.ini");
            int last = 0; outcome = work.Execute(default, (done, total) => last = done);
            check(outcome.Error is null && !outcome.Cancelled && outcome.FilesCopied == 2 && last == 2 && outcome.Folder == Path.Combine(target, "相册 2024"), "The folder is copied as a new folder of the same name: " + outcome.Folder + " " + outcome.Error?.Message);
            var again = work.Execute(default, null);
            check(again.Error is null && again.Folder == Path.Combine(target, "相册 2024 2"), "A second copy takes the next numbered name: " + again.Folder);
        }
        string copy = outcome.Folder!;
        check(File.ReadAllBytes(Path.Combine(copy, "sub", "big.bin")).SequenceEqual(big) && File.ReadAllText(Path.Combine(copy, "a.txt")) == "alpha", "File contents arrive exactly");
        check(Directory.Exists(Path.Combine(copy, "sub", "empty")) && !File.Exists(Path.Combine(copy, "desktop.ini")), "The empty folder is created and the system file is not copied");
        check(File.Exists(Path.Combine(copy, "a.txt:Zone.Identifier")) && File.ReadAllText(Path.Combine(copy, "a.txt:Zone.Identifier")).Contains("ZoneId=3"), "The download warning stream is copied");
        check(Math.Abs((File.GetLastWriteTime(Path.Combine(copy, "a.txt")) - stamp).TotalSeconds) < 1, "The file's modified time is kept");
        check(File.ReadAllText(Path.Combine(folder, "a.txt")) == "alpha" && File.GetAttributes(ini).HasFlag(FileAttributes.System), "The source folder is unchanged");

        string? message = null;
        try { FolderCopyWork.Open(folder, Path.Combine(folder, "sub"), default); } catch (UserError error) { message = error.Message; }
        check(message == FolderCopy.Inside, "Copying a folder into its own sub-folder is refused");
        message = null;
        try { FolderCopyWork.Open(folder, Path.Combine(directory, new string('x', 200)), default); } catch (UserError error) { message = error.Message; }
        check(message is not null, "A destination too long for the copies is refused: " + message);
        var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
        using (var work = FolderCopyWork.Open(folder, target, default))
            reject(() => work.Execute(cancelled.Token, null), "A copy cancelled before it starts stops without making a folder");
        check(Directory.GetDirectories(target).Length == 2, "Only the two completed copies exist in the destination");
    }
}
