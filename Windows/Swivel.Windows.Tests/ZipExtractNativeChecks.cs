using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class ZipExtractNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Cross-compiling this fixture is not execution.
        string root = Path.Combine(directory, "zip-extract"); Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "photos.zip");
        byte[] body = Enumerable.Range(0, 200000).Select(i => (byte)(i % 249)).ToArray();
        using (var file = File.Create(archive))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
        {
            var entry = zip.CreateEntry("假期/海 🦜.dat"); entry.LastWriteTime = new DateTimeOffset(2020, 5, 6, 7, 8, 10, TimeSpan.Zero);
            using (var s = entry.Open()) s.Write(body);
            zip.CreateEntry("empty/").ToString();
            using (var s = zip.CreateEntry("note.txt").Open()) s.Write(Encoding.UTF8.GetBytes("hi"));
        }
        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=4\r\n");
        Directory.CreateDirectory(Path.Combine(root, "photos")); // An existing folder is never reused.
        File.WriteAllText(Path.Combine(root, "photos", "note.txt"), "keep");
        reject(() => ZipExtractWork.Open(archive, new CancellationToken(true)), "Native ZIP pre-cancel opens nothing");
        ZipOutcome outcome;
        using (var work = ZipExtractWork.Open(archive, default))
        {
            check(work.Zone == 4 && work.Plan.Files.Count == 2 && work.Plan.Directories.Count == 2, "Native ZIP plan and Restricted zone kept");
            reject(() => File.AppendAllText(archive, "x"), "Native ZIP archive locked against writers while held");
            reject(() => Directory.Move(root, root + " moved"), "Native ZIP folder ancestors held");
            outcome = work.Extract(default, null);
        }
        string folder = Path.Combine(root, "photos 2");
        check(outcome.Folder == folder && outcome.FilesWritten == 2 && outcome.Error is null && !outcome.Cancelled, "Native ZIP extracts into a new numbered folder: " + outcome.Folder + " written " + outcome.FilesWritten + " " + outcome.Error);
        check(File.ReadAllText(Path.Combine(root, "photos", "note.txt")) == "keep", "Native ZIP never touches an existing folder");
        string extracted = Path.Combine(folder, "假期", "海 🦜.dat");
        check(File.ReadAllBytes(extracted).SequenceEqual(body) && File.ReadAllText(Path.Combine(folder, "note.txt")) == "hi", "Native ZIP exact contents");
        check(Directory.Exists(Path.Combine(folder, "empty")) && !Directory.EnumerateFileSystemEntries(Path.Combine(folder, "empty")).Any(), "Native ZIP empty folder");
        check(File.ReadAllText(extracted + ":Zone.Identifier") == "[ZoneTransfer]\r\nZoneId=4\r\n", "Native ZIP writes Mark of the Web on each file");
        check((File.GetAttributes(extracted) & (FileAttributes.Hidden | FileAttributes.Temporary)) == 0, "Native ZIP outputs are ordinary files");
        check(File.GetLastWriteTimeUtc(extracted).Year == 2020, "Native ZIP keeps modified time");
        check(!Directory.EnumerateFiles(folder, ".swivel-*", SearchOption.AllDirectories).Any(), "Native ZIP leaves no staging files");

        // A damaged member stops extraction; earlier files stay and the folder is reported.
        string broken = Path.Combine(root, "broken.zip");
        using (var file = File.Create(broken))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
        {
            using (var s = zip.CreateEntry("a.txt", CompressionLevel.NoCompression).Open()) s.Write(Encoding.UTF8.GetBytes("first"));
            using (var s = zip.CreateEntry("b.txt", CompressionLevel.NoCompression).Open()) s.Write(Encoding.UTF8.GetBytes("second"));
        }
        byte[] bytes = File.ReadAllBytes(broken);
        int at = Encoding.ASCII.GetString(bytes).IndexOf("second", StringComparison.Ordinal); bytes[at] ^= 1; File.WriteAllBytes(broken, bytes);
        using (var work = ZipExtractWork.Open(broken, default)) outcome = work.Extract(default, null);
        check(outcome.Folder == Path.Combine(root, "broken") && outcome.FilesWritten == 1 && outcome.Error is UserError, "Native ZIP CRC failure keeps earlier files and reports");
        check(File.Exists(Path.Combine(root, "broken", "a.txt")) && !File.Exists(Path.Combine(root, "broken", "b.txt")), "Native ZIP failed member never appears");
        check(File.ReadAllText(Path.Combine(root, "broken", "a.txt") + ":Zone.Identifier").Contains("ZoneId=3"), "Native ZIP default Internet zone");
        // Staging file plus its ":Zone.Identifier" stream must stay below MAX_PATH: refuse up front.
        string deep = root; while (deep.Length < 180) deep = Path.Combine(deep, "d");
        int pad = 260 - (deep.Length + 1 + 1 + 6 + 1 + 45 + 16) - 1; // "\x.zip"-folder at the exact limit
        string limit = Path.Combine(deep, new string('p', Math.Max(1, pad)));
        Directory.CreateDirectory(limit); string tight = Path.Combine(limit, "x.zip");
        using (var file = File.Create(tight)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8)) zip.CreateEntry("a.txt").Open().Dispose();
        reject(() => ZipExtractWork.Open(tight, default).Dispose(), "Native ZIP refuses paths whose Zone stream would exceed MAX_PATH");
        check(!Directory.Exists(Path.Combine(limit, "x")), "Native ZIP path refusal leaves no folder");
        using (var cancel = new CancellationTokenSource())
        using (var work = ZipExtractWork.Open(archive, default))
        {
            outcome = work.Extract(cancel.Token, (done, _) => { if (done == 1) cancel.Cancel(); });
            check(outcome.Cancelled && outcome.FilesWritten == 1 && outcome.Folder == Path.Combine(root, "photos 3"), "Native ZIP cancellation keeps completed files");
        }
    }
}
