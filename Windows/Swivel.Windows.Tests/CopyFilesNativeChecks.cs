using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class CopyFilesNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Cross-compiling this fixture is not execution.
        string root = Path.Combine(directory, "copy-files"), a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string first = Path.Combine(a, "photo.jpg"), second = Path.Combine(a, ".profile");
        byte[] body = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(first, body); File.WriteAllText(first + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); File.WriteAllBytes(first + ":extra", new byte[] { 1, 2, 3 });
        File.WriteAllText(second, "dot"); File.SetLastWriteTimeUtc(first, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(b, "photo.jpg"), "existing");
        reject(() => CopySession.Open(new[] { first }, b, new CancellationToken(true)), "Native copy pre-cancel opens nothing");
        CopyOutcome outcome;
        using (var session = CopySession.Open(new[] { first, second }, b, default))
        {
            reject(() => File.AppendAllText(first, "x"), "Native copy source locked against writers");
            outcome = session.Execute(default);
        }
        string copy = Path.Combine(b, "photo 2.jpg");
        check(outcome.Error is null && !outcome.Cancelled && outcome.Copied.Select(x => x.Destination).SequenceEqual(new[] { copy, Path.Combine(b, ".profile") }), "Native copy names and order");
        check(File.ReadAllText(Path.Combine(b, "photo.jpg")) == "existing", "Native copy never overwrites");
        check(File.ReadAllBytes(copy).SequenceEqual(body) && File.ReadAllBytes(first).SequenceEqual(body), "Native copy exact contents, original kept");
        check(File.ReadAllText(copy + ":Zone.Identifier").Contains("ZoneId=3") && File.ReadAllBytes(copy + ":extra").SequenceEqual(new byte[] { 1, 2, 3 }), "Native copy keeps every data stream");
        check(File.GetLastWriteTimeUtc(copy) == File.GetLastWriteTimeUtc(first), "Native copy keeps modified time");
        string empty = Path.Combine(a, "empty.bin"); File.WriteAllBytes(empty, Array.Empty<byte>());
        using (var session = CopySession.Open(new[] { empty }, b, default)) outcome = session.Execute(default);
        check(outcome.Error is null && new FileInfo(Path.Combine(b, "empty.bin")).Length == 0, "Native copy of an empty file verifies");
        string locked = Path.Combine(a, "locked.txt"); File.WriteAllText(locked, "ro"); File.SetAttributes(locked, FileAttributes.ReadOnly);
        using (var session = CopySession.Open(new[] { locked }, b, default)) outcome = session.Execute(default);
        check(outcome.Error is null && (File.GetAttributes(Path.Combine(b, "locked.txt")) & FileAttributes.ReadOnly) != 0, "Native copy of a read-only file keeps the attribute and verifies");
        File.SetAttributes(locked, FileAttributes.Normal); File.SetAttributes(Path.Combine(b, "locked.txt"), FileAttributes.Normal);
        using (var cancel = new CancellationTokenSource())
        using (var session = CopySession.Open(new[] { first, second }, b, default))
        {
            outcome = session.Execute(cancel.Token, (done, _) => { if (done == 1) cancel.Cancel(); });
            check(outcome.Cancelled && outcome.Copied.Count == 1 && File.Exists(Path.Combine(b, "photo 3.jpg")), "Native copy cancellation keeps completed copies");
        }
    }
}
