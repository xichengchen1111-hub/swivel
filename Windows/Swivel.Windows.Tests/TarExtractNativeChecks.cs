using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class TarExtractNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Cross-compiling this fixture is not execution.
        string root = Path.Combine(directory, "tar-extract"); Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "photos.tar.gz");
        byte[] body = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "假期/"));
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "假期/海 🦜.dat") { DataStream = new MemoryStream(body), ModificationTime = new DateTimeOffset(2020, 5, 6, 7, 8, 10, TimeSpan.Zero) };
            tar.WriteEntry(entry);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "note.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("hi")) });
        }
        Directory.CreateDirectory(Path.Combine(root, "photos")); // An existing folder is never reused.
        reject(() => ZipExtractWork.Open(archive, new CancellationToken(true)), "Native TAR pre-cancel opens nothing");
        ZipOutcome outcome;
        using (var work = ZipExtractWork.Open(archive, default))
        {
            check(work.Tar == TarKind.TarGzip && work.Zone == 3 && work.Plan.Files.Count == 2 && work.Plan.Directories.Count == 1, "Native TAR plan and default zone");
            reject(() => File.AppendAllText(archive, "x"), "Native TAR archive locked against writers while held");
            outcome = work.Extract(default, null);
        }
        string folder = Path.Combine(root, "photos 2");
        check(outcome.Folder == folder && outcome.FilesWritten == 2 && outcome.Error is null && !outcome.Cancelled, "Native TAR extracts into a new numbered folder: " + outcome.Folder + " " + outcome.Error);
        string extracted = Path.Combine(folder, "假期", "海 🦜.dat");
        check(File.ReadAllBytes(extracted).SequenceEqual(body) && File.ReadAllText(Path.Combine(folder, "note.txt")) == "hi", "Native TAR exact contents");
        check(File.ReadAllText(extracted + ":Zone.Identifier").Contains("ZoneId=3"), "Native TAR writes Mark of the Web");
        check(File.GetLastWriteTimeUtc(extracted).Year == 2020, "Native TAR keeps modified time");
        check(!Directory.EnumerateFiles(folder, ".swivel-*", SearchOption.AllDirectories).Any(), "Native TAR leaves no staging files");

        // Single-file gzip: one file named after the archive, inside a new folder.
        string single = Path.Combine(root, "report.pdf.gz");
        using (var file = File.Create(single)) using (var gzip = new GZipStream(file, CompressionLevel.Optimal)) gzip.Write(body);
        using (var work = ZipExtractWork.Open(single, default)) outcome = work.Extract(default, null);
        check(outcome.Error is null && File.ReadAllBytes(Path.Combine(root, "report", "report.pdf")).SequenceEqual(body), "Native gzip single file");

        // A plain TAR whose member changes after review is refused at the CRC check.
        string plain = Path.Combine(root, "plain.tar");
        using (var file = File.Create(plain)) using (var tar = new TarWriter(file, TarEntryFormat.Ustar))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "a.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("first")) });
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "b.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("second")) });
        }
        using (var work = ZipExtractWork.Open(plain, default)) outcome = work.Extract(default, null);
        check(outcome.Error is null && File.ReadAllText(Path.Combine(root, "plain", "b.txt")) == "second", "Native plain TAR");
        reject(() => ZipExtractWork.Open(Path.Combine(root, "note.rar"), default), "Native extraction refuses other extensions");

        // A child created by path must really be inside the held parent (a mount-point swap is refused).
        string inside = Path.Combine(root, "held"), elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(inside); Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(inside, "f.txt"), "a"); File.WriteAllText(Path.Combine(elsewhere, "f.txt"), "b");
        {
            using var held = OpenFolder(inside);
            using var good = File.OpenHandle(Path.Combine(inside, "f.txt"));
            using var bad = File.OpenHandle(Path.Combine(elsewhere, "f.txt"));
            NativeFiles.CheckCreatedIn(good, held, Path.Combine(inside, "f.txt"));
            check(true, "Native created-in check accepts a file inside the held folder");
            bool refused = false;
            try { NativeFiles.CheckCreatedIn(bad, held, Path.Combine(inside, "f.txt")); } catch (UserError) { refused = true; }
            check(refused, "Native created-in check refuses a file created elsewhere with a user-facing error");
        }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenFolder(string path)
    {
        var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        return handle;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
