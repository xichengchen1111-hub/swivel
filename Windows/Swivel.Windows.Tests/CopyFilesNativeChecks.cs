using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

        // Undoing a copy (Copy Results): only the object the copy created, unchanged, goes to the Recycle Bin.
        using (var session = CopySession.Open(new[] { first, second }, b, default)) outcome = session.Execute(default);
        check(outcome.Error is null && outcome.Copied.Count == 2 && outcome.Copied.All(x => x.Verified && x.Stamp is { } stamp && stamp.Length == new FileInfo(x.Destination).Length
            && stamp.LastWrite == File.GetLastWriteTimeUtc(x.Destination).ToFileTimeUtc()), "Native copy records each verified copy's identity, length and modified time");
        check(NativeFiles.CountOnVolumeOf(new[] { first, second }, b) == 2 && NativeFiles.CountOnVolumeOf(new[] { Path.Combine(a, "missing.txt") }, b) is null,
            "Volume count: files on the destination's volume, unknown when a file cannot be opened");
        var made = outcome.Copied[0]; var edited = outcome.Copied[1];
        File.AppendAllText(edited.Destination, "edit");
        reject(() => NativeFiles.RecycleCopy(edited.Destination, edited.Stamp!), "Undoing a copy refuses a copy changed after copying");
        reject(() => NativeFiles.RecycleCopy(first, made.Stamp!), "Undoing a copy refuses any other file, such as the original");
        string link = Path.Combine(b, "photo link.jpg");
        if (!CreateHardLink(link, made.Destination, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        reject(() => NativeFiles.RecycleCopy(made.Destination, made.Stamp!), "Undoing a copy refuses a copy that gained another hard link");
        File.Delete(link);
        check(File.Exists(made.Destination) && File.Exists(edited.Destination) && File.ReadAllBytes(first).SequenceEqual(body), "Refused copies and the originals stay in place");

        var app = Application.Current as App ?? new App();
        var ghost = new CopyResult(first, Path.Combine(b, "ghost.jpg")); // a copy that could not be verified
        var dialog = new CopyFilesResultDialog(app, outcome with { Copied = outcome.Copied.Append(ghost).ToArray(), Total = 3 }, true);
        var buttons = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        var move = buttons.Single(x => Equals(x.Content, app.L("Move Selected to Recycle Bin…")));
        var list = ((StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<ListView>().Single();
        check(!move.IsEnabled && list.Items.Count == 3 && list.SelectedItems.Count == 0 && buttons.Last().IsCancel, "Copy Results lists every copy with nothing selected and Move disabled");
        list.SelectedItems.Add(list.Items[2]);
        check(!move.IsEnabled, "An unverified copy is not offered for undo");
        list.SelectAll();
        bool bin = new DriveInfo(Path.GetPathRoot(made.Destination)!).DriveType == DriveType.Fixed;
        check(move.IsEnabled == bin, "Selecting verified copies enables the move only on a fixed drive (one with a Recycle Bin)");
        check(dialog.CurrentReport.Contains(made.Destination) && dialog.CurrentReport.Contains(first) && dialog.CurrentReport.Contains(app.L("Not verified; not offered for undo"))
            && dialog.CurrentReport.Contains(app.L(bin ? "Can be moved to the Recycle Bin" : "This drive has no Recycle Bin; not offered for undo")), "Copy Results report lists each copy, its original and its state");
        dialog.Close();

        if (!RecycleBin.Available(made.Destination)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleCopy(made.Destination, made.Stamp!);
        check(!File.Exists(made.Destination) && stored.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) && File.ReadAllBytes(first).SequenceEqual(body),
            "Undoing a copy moves it to the Recycle Bin and leaves the original");
        NativeFiles.RestoreRecycled(made.Destination, stored, false);
        check(File.ReadAllBytes(made.Destination).SequenceEqual(body), "Undo Move restores the copy");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        using (var session = CopySession.Open(new[] { locked }, b, default)) outcome = session.Execute(default);
        var readOnly = outcome.Copied.Single();
        check(readOnly.Verified && readOnly.Stamp is not null && (File.GetAttributes(readOnly.Destination) & FileAttributes.ReadOnly) != 0, "A read-only copy keeps a stamp");
        stored = NativeFiles.RecycleCopy(readOnly.Destination, readOnly.Stamp!);
        check(!File.Exists(readOnly.Destination) && File.Exists(locked), "Undoing a read-only copy moves it to the Recycle Bin and leaves the original");
        NativeFiles.RestoreRecycled(readOnly.Destination, stored, false);
        check(File.Exists(readOnly.Destination), "Undo Move restores the read-only copy");
        File.SetAttributes(readOnly.Destination, FileAttributes.Normal); File.SetAttributes(locked, FileAttributes.Normal);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
