using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Swivel.Core;
using Swivel.Windows;

internal static class DuplicateNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        check(Marshal.SizeOf<IdentityLayout>() == 24 && Marshal.OffsetOf<IdentityLayout>(nameof(IdentityLayout.Low)).ToInt32() == 8,
            "Duplicate identity uses 64-bit volume plus 128-bit file ID on either architecture");
        string folder = Path.Combine(directory, "duplicates"); Directory.CreateDirectory(folder);
        string child = Path.Combine(folder, "child"); Directory.CreateDirectory(child);
        var bytes = new byte[200000]; new Random(14).NextBytes(bytes);
        string first = Path.Combine(folder, "a.bin"), second = Path.Combine(child, "b.bin"), different = Path.Combine(folder,"different.bin");
        File.WriteAllBytes(first, bytes); File.WriteAllBytes(second, bytes);
        var changedMiddle = (byte[])bytes.Clone(); changedMiddle[100000] ^= 1; File.WriteAllBytes(different, changedMiddle);
        File.WriteAllBytes(Path.Combine(folder,"empty.bin"), Array.Empty<byte>());
        using (var root = NativeFiles.MetadataDirectory.Open(folder))
        {
            using (var input = root.OpenDuplicate(new("a.bin", bytes.Length)))
            {
                check(input.Stamp.Length == bytes.Length && input.Stamp.Identity.Length == 48 && input.Refresh() == input.Stamp,
                    "Held source provides stable full-width identity, length and native last-write time");
                reject(() => File.Open(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(), "Comparison source forbids concurrent writers");
                reject(() => File.Move(first, first + " moved"), "Comparison source forbids rename");
                reject(() => Directory.Move(folder, folder + " moved"), "Chosen root remains pinned throughout comparison");
            }
            foreach (string invalid in new[] { @"..\escape", @"child\..\a.bin", @"\a.bin", @"C:\a.bin", "a.bin:ads", "child/b.bin" })
                reject(() => root.OpenDuplicate(new(invalid, bytes.Length)).Dispose(), "Reject nonlocal/escaping duplicate candidate " + invalid);
            reject(() => root.OpenDuplicate(new("a.bin", bytes.Length + 1)).Dispose(), "Reject changed metadata length before hashing");
        }
        var result = DoctorDuplicates.Run(folder, default);
        check(result.Duplicates.Groups.Count == 1 && result.Duplicates.Groups[0].Paths.Count == 2 && result.Duplicates.Groups[0].Paths.Contains(@"child\b.bin"),
            "Native comparison verifies full files, separating equal ends with different middle bytes");
        check(result.Duplicates.Issues.Any(x => x.RelativePath == "empty.bin"), "Empty files have an explicit skip reason");
        string alias = Path.Combine(folder, "hardlink.bin");
        if (CreateHardLink(alias, first, IntPtr.Zero))
        {
            var linked = DoctorDuplicates.Run(folder, default);
            check(linked.Duplicates.Groups.Single().Paths.Count == 2 && linked.Duplicates.Issues.Any(x => x.Reason == "Same file identity (hard link)"),
                "Hard-link aliases are counted as one identity, not another content copy");
            File.Delete(alias);
        }
        else Console.WriteLine("SKIP: duplicate hard-link fixture not supported by this volume.");
        string system = Path.Combine(folder, "system.bin"); File.WriteAllBytes(system, bytes); File.SetAttributes(system, FileAttributes.System);
        try
        {
            using var root = NativeFiles.MetadataDirectory.Open(folder);
            reject(() => root.OpenDuplicate(new("system.bin", bytes.Length)).Dispose(), "Direct candidate rejects system attribute before content access");
            var limited = DoctorDuplicates.Run(folder, default);
            check(limited.Folder.Root.Skipped == 1 && limited.Duplicates.Groups.Single().Paths.Count == 2, "System file is a visible metadata skip, never a compared candidate");
        }
        finally { File.SetAttributes(system, FileAttributes.Normal); File.Delete(system); }
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = DoctorDuplicates.Run(folder, default);
            check(blocked.Duplicates.Groups.Count == 0 && blocked.Duplicates.Issues.Any(x => x.RelativePath == @"child\b.bin"), "Unreadable file is skipped rather than forming a partial-hash match");
        }
        using (var root = NativeFiles.MetadataDirectory.Open(folder))
        {
            int firstOpens = 0;
            var changed = DuplicateFiles.Scan(new[] { new DuplicateCandidate("a.bin",bytes.Length), new DuplicateCandidate(@"child\b.bin",bytes.Length) }, candidate =>
            {
                if (candidate.RelativePath == "a.bin" && ++firstOpens == 2)
                {
                    File.WriteAllBytes(first, changedMiddle); File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
                }
                return root.OpenDuplicate(candidate);
            }, default);
            check(changed.Groups.Count == 0 && changed.Issues.Any(x => x.RelativePath == "a.bin" && x.Reason == "File changed during comparison"), "Native replacement between sample and full hash is excluded");
        }
        File.WriteAllBytes(first, bytes);
        string outside = Path.Combine(directory, "duplicate-outside.bin"); File.WriteAllBytes(outside, bytes);
        string symlink = Path.Combine(folder,"link.bin"); bool madeLink = false;
        try { File.CreateSymbolicLink(symlink,outside); madeLink = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        { Console.WriteLine("SKIP: duplicate symlink fixture requires Windows policy permission."); }
        if (madeLink)
        {
            using (var root = NativeFiles.MetadataDirectory.Open(folder))
                reject(() => root.OpenDuplicate(new("link.bin",bytes.Length)).Dispose(), "File symlink is refused by attribute probe before content open");
            var linked = DoctorDuplicates.Run(folder, default);
            check(linked.Folder.Root.Skipped == 1 && linked.Duplicates.Groups.Single().Paths.Count == 2, "Symlink is a metadata skip and outside contents are not counted");
            File.Delete(symlink);
        }
        reject(() => DoctorDuplicates.Run(folder,new CancellationToken(true)), "Pre-cancellation precedes native root access");
        var app = Application.Current as App ?? new App();
        var dialog = new DuplicateFilesDialog(app,folder,result.Folder,result.Duplicates);
        check(dialog.MinWidth == 540 && dialog.MinHeight == 480 && dialog.Content is System.Windows.Controls.DockPanel,
            "Duplicate results use responsive native controls and a fixed footer");
        check(dialog.CurrentReport.Contains(folder) && dialog.CurrentReport.Contains(@"child\b.bin") && dialog.CurrentReport.Contains("empty.bin"),
            "Explicit report includes chosen root, complete group paths and skipped rows"); dialog.Close();
        var confirmation = new DuplicateScanConfirmation(app,folder); check(confirmation.Content is System.Windows.Controls.DockPanel,"Content-reading confirmation is a cancellable native window"); confirmation.Close();
        check(File.ReadAllBytes(first).SequenceEqual(bytes) && File.ReadAllBytes(second).SequenceEqual(bytes) && File.ReadAllBytes(different).SequenceEqual(changedMiddle),
            "Duplicate scans preserve all input bytes");
        Directory.Move(folder,folder+" released"); check(Directory.Exists(folder+" released"),"All duplicate/root/ancestor handles released on completion");
    }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityLayout { public ulong Volume, Low, High; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint="CreateHardLinkW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr reserved);
}
