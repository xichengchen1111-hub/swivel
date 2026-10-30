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
    // Made on a Mac with bsdtar 3.5.3 and Info-ZIP; the same fixtures are cross-checked in the core suite.
    private const string Aes256 = "UEsDBBQACQBjAEZ0Xl0AAAAAAAAAAAAAAAAFACsAYS50eHR1eAsAAQT1AQAABAAAAAABmQcAAQBBRQMIAFVUDQAHRR7kakUe5GpFHuRqnktowvlADEnV3pn+HXJ6CzdOpYlNIYffSS3elDhCLFMxrlyQo92IhXQUIGXuD8XLqy5UUEsHCOxhUB0zAAAAMgAAAFBLAwQUAAkAYwBGdF5dAAAAAAAAAAAAAAAABQArAGIudHh0dXgLAAEE9QEAAAQAAAAAAZkHAAIAQUUDCABVVA0AB0Ue5GpFHuRqRR7katCxwPaH2QA293QG5/ywje6yAbewAVWe32z5uN1ny1RQSwcIAAAAAB8AAAABAAAAUEsBAhQDFAAJAGMARnReXexhUB0zAAAAMgAAAAUAIwAAAAAAAAAAAKSBAAAAAGEudHh0dXgLAAEE9QEAAAQAAAAAAZkHAAEAQUUDCABVVAUAAUUe5GpQSwECFAMUAAkAYwBGdF5dAAAAAB8AAAABAAAABQAjAAAAAAAAAAAApIGRAAAAYi50eHR1eAsAAQT1AQAABAAAAAABmQcAAgBBRQMIAFVUBQABRR7kalBLBQYAAAAAAgACAKwAAAAOAQAAAAA=";
    private const string Mixed = "UEsDBBQACQAIAEd0Xl3sYVAdIwAAADIAAAAFABwAYS50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAhHLPUpyDGFnFhcSO4pFtzEx9Q2z/5g+0W2ctbiHuwxHiInpQSwcI7GFQHSMAAAAyAAAAUEsDBAoACQAAAEd0Xl2DFtyMDQAAAAEAAAAFABwAYi50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAB3h6Ac5Ce1mbyGAeQlBLBwiDFtyMDQAAAAEAAABQSwECHgMUAAkACABHdF5d7GFQHSMAAAAyAAAABQAYAAAAAAABAAAApIEAAAAAYS50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwECHgMKAAkAAABHdF5dgxbcjA0AAAABAAAABQAYAAAAAAABAAAApIFyAAAAYi50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwUGAAAAAAIAAgCWAAAAzgAAAAAA";

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

        // Encrypted ZIP (bsdtar AES-256: a.txt AE-1, b.txt AE-2; password "secret").
        byte[] aText = Encoding.ASCII.GetBytes("hello aes world, hello aes world, hello aes world\n");
        string locked = Path.Combine(root, "locked.zip"); File.WriteAllBytes(locked, Convert.FromBase64String(Aes256));
        using (var work = ZipExtractWork.Open(locked, default))
        {
            check(work.Encrypted.Count == 2 && work.Encrypted.All(f => f.Encryption == ZipEncryption.Aes256), "Native encrypted ZIP plan lists AES-256 members");
            reject(() => work.Extract(default, null), "Native encrypted ZIP refuses extraction without a password");
            using (var wrong = new ZipPassword("Secret")) check(!work.PasswordMatchesFirst(wrong, default), "Native encrypted ZIP first-member password check");
            using (var wrong = new ZipPassword("Secret"))
            {
                string message = "";
                try { work.Extract(default, null, wrong); } catch (UserError error) { message = error.Message; }
                check(message == "The password is not correct. Nothing was extracted." && !Directory.Exists(Path.Combine(root, "locked")), "Native wrong password refuses before any folder exists");
            }
            using var password = new ZipPassword("secret");
            check(work.PasswordMatchesFirst(password, default), "Native right password matches");
            outcome = work.Extract(default, null, password);
        }
        string lockedFolder = Path.Combine(root, "locked");
        check(outcome.Folder == lockedFolder && outcome.FilesWritten == 2 && outcome.Error is null && outcome.Refused.Count == 0, "Native AES ZIP extracts: " + outcome.Error);
        check(File.ReadAllBytes(Path.Combine(lockedFolder, "a.txt")).SequenceEqual(aText) && File.ReadAllText(Path.Combine(lockedFolder, "b.txt")) == "x", "Native AES ZIP exact contents");
        check(File.ReadAllText(Path.Combine(lockedFolder, "a.txt") + ":Zone.Identifier").Contains("ZoneId=3"), "Native AES ZIP outputs keep Mark of the Web");
        check(!Directory.EnumerateFiles(lockedFolder, ".swivel-*", SearchOption.AllDirectories).Any(), "Native AES ZIP leaves no staging files");

        // A tampered second member fails authentication: the first stays, the second never appears.
        byte[] tampered = Convert.FromBase64String(Aes256);
        int second = (int)ZipExtract.Read(new MemoryStream(tampered), default).Files[1].LocalOffset;
        int dataStart = second + 30 + BitConverter.ToUInt16(tampered, second + 26) + BitConverter.ToUInt16(tampered, second + 28);
        tampered[dataStart + 16 + 2] ^= 1; // the one encrypted byte, after salt and verifier
        string altered = Path.Combine(root, "altered.zip"); File.WriteAllBytes(altered, tampered);
        using (var work = ZipExtractWork.Open(altered, default))
        using (var password = new ZipPassword("secret")) outcome = work.Extract(default, null, password);
        check(outcome.FilesWritten == 1 && outcome.Error is UserError { Message: ZipDecrypt.AuthFailed }, "Native AES authentication failure stops and reports: " + outcome.Error?.Message);
        check(File.Exists(Path.Combine(root, "altered", "a.txt")) && !File.Exists(Path.Combine(root, "altered", "b.txt"))
            && !Directory.EnumerateFiles(Path.Combine(root, "altered"), ".swivel-*").Any(), "Native unauthenticated member never appears");

        // Cancellation keeps the completed encrypted file.
        using (var cancel = new CancellationTokenSource())
        using (var work = ZipExtractWork.Open(locked, default))
        using (var password = new ZipPassword("secret"))
        {
            outcome = work.Extract(cancel.Token, (done, _) => { if (done == 1) cancel.Cancel(); }, password);
            check(outcome.Cancelled && outcome.FilesWritten == 1 && outcome.Folder == Path.Combine(root, "locked 2"), "Native encrypted ZIP cancellation keeps completed files");
        }

        // Two passwords (Info-ZIP ZipCrypto: a.txt "one", b.txt "two"): asked once, the other is left out.
        string mixed = Path.Combine(root, "mixed.zip"); File.WriteAllBytes(mixed, Convert.FromBase64String(Mixed));
        using (var work = ZipExtractWork.Open(mixed, default))
        using (var password = new ZipPassword("one")) outcome = work.Extract(default, null, password);
        check(outcome.FilesWritten == 1 && outcome.FileCount == 2 && outcome.Refused.SequenceEqual(new[] { "b.txt" }) && outcome.Error is null, "Native mixed passwords: matching members extract, others listed");
        check(File.ReadAllBytes(Path.Combine(root, "mixed", "a.txt")).SequenceEqual(aText) && !File.Exists(Path.Combine(root, "mixed", "b.txt")), "Native mixed passwords exact contents");
        using (var work = ZipExtractWork.Open(mixed, default))
        using (var password = new ZipPassword("two"))
            reject(() => work.Extract(default, null, password), "Native mixed passwords: first member mismatch refuses everything");
        check(!Directory.Exists(Path.Combine(root, "mixed 2")), "Native first-member mismatch creates no folder");
    }
}
