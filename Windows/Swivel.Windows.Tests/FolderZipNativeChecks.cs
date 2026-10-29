using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderZipNativeChecks
{
    // Compress Folder to ZIP on a real NTFS folder tree, read back by .NET's ZIP reader.
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string parent = Path.Combine(directory, "folder-zip"); Directory.CreateDirectory(parent);
        string folder = Path.Combine(parent, "报告 2024");
        Directory.CreateDirectory(Path.Combine(folder, "sub", "empty"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "alpha", Encoding.UTF8);
        var big = new byte[3_000_000]; new Random(9).NextBytes(big);
        File.WriteAllBytes(Path.Combine(folder, "sub", "big.bin"), big);
        var stamp = new DateTime(2023, 6, 1, 8, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(Path.Combine(folder, "a.txt"), stamp);
        string ini = Path.Combine(folder, "desktop.ini"); File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        string hidden = Path.Combine(folder, "notes.txt"); File.WriteAllText(hidden, "n"); File.SetAttributes(hidden, FileAttributes.Hidden);

        string zipPath;
        using (var work = FolderZipWork.Open(folder, default))
        {
            check(work.Plan.Files == 3 && work.Plan.Folders == 2 && work.Plan.Skipped.Count == 1 && work.Plan.Skipped[0].RelativePath == "desktop.ini",
                $"The real folder plans 3 files in 2 sub-folders and skips the system desktop.ini: {work.Plan.Files}/{work.Plan.Folders}/{work.Plan.Skipped.Count}");
            int last = 0;
            zipPath = work.Save(default, (done, total) => last = done);
            check(Path.GetFileName(zipPath) == "报告 2024.zip" && Path.GetDirectoryName(zipPath) == parent && last == 3, "The ZIP is saved beside the folder under its name: " + Path.GetFileName(zipPath));
            string second = work.Save(default, null);
            check(Path.GetFileName(second) == "报告 2024 2.zip", "A second ZIP takes the next numbered name: " + Path.GetFileName(second));
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            check(names.SequenceEqual(new[] { "报告 2024/", "报告 2024/a.txt", "报告 2024/notes.txt", "报告 2024/sub/", "报告 2024/sub/big.bin", "报告 2024/sub/empty/" }),
                "Entries: the folder, its files (hidden kept), sub-folders and the empty folder: " + string.Join(", ", names));
            using (var stream = zip.GetEntry("报告 2024/sub/big.bin")!.Open()) { var memory = new MemoryStream(); stream.CopyTo(memory); check(memory.ToArray().SequenceEqual(big), "A 3 MB file comes back exactly"); }
            var time = zip.GetEntry("报告 2024/a.txt")!.LastWriteTime.DateTime;
            check(Math.Abs((time - stamp).TotalSeconds) <= 2, $"The file's modified time is kept: {time}");
        }
        check(File.ReadAllText(Path.Combine(folder, "a.txt")) == "alpha" && File.GetAttributes(ini).HasFlag(FileAttributes.System), "The folder itself is unchanged");

        // Extracting with Swivel's own extractor gives the same tree back.
        using (var extract = ZipExtractWork.Open(zipPath, default))
        {
            var outcome = extract.Extract(default, null);
            string back = Path.Combine(outcome.Folder!, "报告 2024", "sub", "big.bin");
            check(outcome.Error is null && File.ReadAllBytes(back).SequenceEqual(big), "Swivel's ZIP extraction reads the folder ZIP back");
        }

        // A whole drive and a link are refused; a pre-cancelled save writes nothing.
        string? message = null;
        try { FolderZipWork.Open(Path.GetPathRoot(directory)!, default); } catch (UserError error) { message = error.Message; }
        check(message == "Choose a folder, not a whole drive.", "A whole drive is refused");
        string target = Path.Combine(parent, "target"); Directory.CreateDirectory(target);
        string junction = Path.Combine(folder, "jump");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", junction, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mklink!.WaitForExit();
        if (Directory.Exists(junction))
            using (var work = FolderZipWork.Open(folder, default))
            {
                check(work.Plan.Skipped.Any(s => s.RelativePath == "jump" && s.Reason.StartsWith("Link")), "A junction inside the folder is skipped, not followed");
            }
        // Remove the junction itself (not its target) so the test folder can be cleaned up.
        if (Directory.Exists(junction)) Directory.Delete(junction);
        var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
        int before = Directory.GetFiles(parent).Length;
        using (var work = FolderZipWork.Open(folder, default))
            reject(() => work.Save(cancelled.Token, null), "A cancelled folder ZIP stops");
        check(Directory.GetFiles(parent).Length == before && !Directory.GetFiles(parent, ".swivel-*").Any(), "A cancelled folder ZIP leaves no file behind");
    }
}
