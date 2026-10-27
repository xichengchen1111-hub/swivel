using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderStyleNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Cross-compiling this fixture is not execution.
        string root = Path.Combine(directory, "folder-style"); Directory.CreateDirectory(root);
        string plain = Path.Combine(root, "Photos"); Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "keep.txt"), "keep");
        check(NativeFiles.InspectFolderStyle(plain) == FolderStyleState.None, "Native folder style: plain folder has none");
        byte[] icon = FolderStyles.Icon(new[] { (16, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9 }) });
        NativeFiles.ApplyFolderStyle(plain, icon, default);
        string ini = Path.Combine(plain, "desktop.ini");
        var icons = Directory.GetFiles(plain, ".swivel-folder-*.ico");
        check(File.Exists(ini) && icons.Length == 1 && File.ReadAllBytes(icons[0]).SequenceEqual(icon), "Native folder style writes desktop.ini and one icon");
        var parsed = FolderStyles.ParseOurs(File.ReadAllBytes(ini));
        check(parsed is { ReadOnlyAdded: true } && parsed.Icon == Path.GetFileName(icons[0]), "Native desktop.ini is Swivel's and records the Read-only change");
        check((File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)
            && (File.GetAttributes(icons[0]) & FileAttributes.Hidden) != 0, "Native style files are hidden system files");
        check((File.GetAttributes(plain) & FileAttributes.ReadOnly) != 0, "Native styled folder is Read-only for Explorer");
        check(NativeFiles.InspectFolderStyle(plain) == FolderStyleState.Swivel, "Native inspect sees Swivel's style");
        check(!Directory.EnumerateFiles(plain, ".swivel-*.tmp").Any(), "Native folder style leaves no staging files");

        // Restyle replaces Swivel's own files and keeps the original Read-only record.
        NativeFiles.ApplyFolderStyle(plain, icon, default);
        var again = Directory.GetFiles(plain, ".swivel-folder-*.ico");
        check(again.Length == 1 && again[0] != icons[0] && FolderStyles.ParseOurs(File.ReadAllBytes(ini)) is { ReadOnlyAdded: true }, "Native restyle swaps the icon and keeps the Read-only record");

        NativeFiles.RemoveFolderStyle(plain, default);
        check(!File.Exists(ini) && !Directory.GetFiles(plain, ".swivel-folder-*.ico").Any() && File.ReadAllText(Path.Combine(plain, "keep.txt")) == "keep", "Native restore removes exactly Swivel's files");
        check((File.GetAttributes(plain) & FileAttributes.ReadOnly) == 0, "Native restore clears Read-only Swivel added");
        reject(() => NativeFiles.RemoveFolderStyle(plain, default), "Native restore without a style refused");

        // A folder that was Read-only already stays Read-only after restore.
        string already = Path.Combine(root, "Already"); Directory.CreateDirectory(already);
        File.SetAttributes(already, File.GetAttributes(already) | FileAttributes.ReadOnly);
        NativeFiles.ApplyFolderStyle(already, icon, default);
        check(FolderStyles.ParseOurs(File.ReadAllBytes(Path.Combine(already, "desktop.ini"))) is { ReadOnlyAdded: false }, "Native style records an existing Read-only folder");
        NativeFiles.RemoveFolderStyle(already, default);
        check((File.GetAttributes(already) & FileAttributes.ReadOnly) != 0, "Native restore keeps the user's own Read-only");
        // Styled while already Read-only, the user clears Read-only, then restyles: Swivel's own change is recorded.
        NativeFiles.ApplyFolderStyle(already, icon, default);
        File.SetAttributes(already, File.GetAttributes(already) & ~FileAttributes.ReadOnly);
        NativeFiles.ApplyFolderStyle(already, icon, default);
        check(FolderStyles.ParseOurs(File.ReadAllBytes(Path.Combine(already, "desktop.ini"))) is { ReadOnlyAdded: true } && !Directory.EnumerateFiles(already, ".swivel-*.tmp").Any(), "Native restyle records Read-only it adds again and leaves no moved-aside file");
        NativeFiles.RemoveFolderStyle(already, default);
        check((File.GetAttributes(already) & FileAttributes.ReadOnly) == 0, "Native restore then clears that Read-only");
        File.SetAttributes(already, File.GetAttributes(already) | FileAttributes.ReadOnly);

        // Someone else's desktop.ini is never touched.
        string foreign = Path.Combine(root, "Foreign"); Directory.CreateDirectory(foreign);
        string theirs = Path.Combine(foreign, "desktop.ini");
        File.WriteAllText(theirs, "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\shell32.dll,3\r\n", Encoding.Unicode);
        byte[] before = File.ReadAllBytes(theirs);
        check(NativeFiles.InspectFolderStyle(foreign) == FolderStyleState.Other, "Native inspect sees a foreign desktop.ini");
        reject(() => NativeFiles.ApplyFolderStyle(foreign, icon, default), "Native style refuses a foreign desktop.ini");
        reject(() => NativeFiles.RemoveFolderStyle(foreign, default), "Native restore refuses a foreign desktop.ini");
        check(File.ReadAllBytes(theirs).SequenceEqual(before) && !Directory.GetFiles(foreign, ".swivel-*").Any(), "Native foreign desktop.ini and folder left unchanged");

        reject(() => NativeFiles.ApplyFolderStyle(Path.GetPathRoot(root)!, icon, default), "Native style refuses a whole drive");
        reject(() => NativeFiles.ApplyFolderStyle(plain, icon, new CancellationToken(true)), "Native style pre-cancel writes nothing");
        check(!File.Exists(ini), "Native cancelled style left no desktop.ini");
        File.SetAttributes(already, File.GetAttributes(already) & ~FileAttributes.ReadOnly); // let the test folder be cleaned up

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            byte[] drawn = FolderStyleIcons.Icon(new FolderStyle(3, "旅"));
            check(BitConverter.ToUInt16(drawn, 4) == FolderStyles.IconSizes.Length && drawn.Length > 1000, "Native folder icon renders every size as PNG: " + drawn.Length + " bytes");
        }
    }
}
