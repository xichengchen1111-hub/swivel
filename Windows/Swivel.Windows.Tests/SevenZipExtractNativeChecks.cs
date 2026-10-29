using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class SevenZipExtractNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Fixtures come from Swivel.Core.Tests/Fixtures/7z (libarchive and hand-assembled).
        string root = Path.Combine(directory, "sevenzip-extract"); Directory.CreateDirectory(root);
        string Fixture(string name)
        {
            string copy = Path.Combine(root, name);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "7z", name), copy, true);
            return copy;
        }
        string archive = Fixture("libarchive-lzma2.7z");
        Directory.CreateDirectory(Path.Combine(root, "libarchive-lzma2")); // An existing folder is never reused.
        reject(() => ZipExtractWork.Open(archive, new CancellationToken(true)), "Native 7z pre-cancel opens nothing");
        ZipOutcome outcome;
        using (var work = ZipExtractWork.Open(archive, default))
        {
            check(work.Tar is null && work.Zone == 3 && work.Plan.Files.Count == 5 && work.Plan.Directories.Count == 2, "Native 7z plan and default zone");
            reject(() => File.AppendAllText(archive, "x"), "Native 7z archive locked against writers while held");
            outcome = work.Extract(default, null);
        }
        string folder = Path.Combine(root, "libarchive-lzma2 2");
        check(outcome.Folder == folder && outcome.FilesWritten == 5 && outcome.Error is null && !outcome.Cancelled, "Native 7z extracts into a new numbered folder: " + outcome.Folder + " " + outcome.Error);
        string pattern = Path.Combine(folder, "子目录", "数据 🦜.bin");
        byte[] expected = Enumerable.Range(0, 70000).Select(i => (byte)((i * 7 + (i >> 8)) & 0xFF)).ToArray();
        check(File.ReadAllBytes(pattern).SequenceEqual(expected) && File.ReadAllText(Path.Combine(folder, "a.txt")) == "hello 7z\n"
            && new FileInfo(Path.Combine(folder, "empty.txt")).Length == 0 && new FileInfo(Path.Combine(folder, "large.txt")).Length == 150000 * 19
            && Directory.Exists(Path.Combine(folder, "空文件夹")), "Native 7z exact contents, empty file and empty folder");
        check(File.ReadAllText(pattern + ":Zone.Identifier").Contains("ZoneId=3"), "Native 7z writes Mark of the Web");
        check(File.GetLastWriteTimeUtc(pattern) == DateTime.UnixEpoch.AddSeconds(1600000000), "Native 7z keeps modified time");
        check(!Directory.EnumerateFiles(folder, ".swivel-*", SearchOption.AllDirectories).Any(), "Native 7z leaves no staging files");

        // A Restricted archive stays Restricted.
        string restricted = Fixture("handmade-bcj.7z");
        File.WriteAllText(restricted + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=4\r\n");
        using (var work = ZipExtractWork.Open(restricted, default))
        {
            check(work.Zone == 4, "Native 7z keeps a Restricted zone");
            outcome = work.Extract(default, null);
        }
        check(outcome.Error is null && outcome.FilesWritten == 2 && File.ReadAllText(Path.Combine(root, "handmade-bcj", "code", "two.dll") + ":Zone.Identifier").Contains("ZoneId=4"), "Native 7z BCJ archive extracted as Restricted");

        reject(() => ZipExtractWork.Open(Fixture("libarchive-ppmd.7z"), default), "Native 7z refuses PPMd before creating anything");
        check(!Directory.Exists(Path.Combine(root, "libarchive-ppmd")), "Native 7z refusal leaves no folder");
        reject(() => ZipExtractWork.Open(Fixture("libarchive-symlink.7z"), default), "Native 7z refuses links");

        // Cancellation during extraction keeps what was published and reports it.
        using (var cts = new CancellationTokenSource())
        using (var work = ZipExtractWork.Open(Fixture("libarchive-lzma1.7z"), default))
        {
            outcome = work.Extract(cts.Token, (done, total) => { if (done == 1) cts.Cancel(); });
            check(outcome.Cancelled && outcome.FilesWritten == 1, "Native 7z cancellation stops after the current file");
        }
    }
}
