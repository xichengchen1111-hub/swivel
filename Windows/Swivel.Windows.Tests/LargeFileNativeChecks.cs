using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class LargeFileNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string root = Path.Combine(directory,"large-file-metadata"); Directory.CreateDirectory(root);
        string nested = Path.Combine(root,"照片"); Directory.CreateDirectory(nested);
        string source = Path.Combine(nested,"假安装包.MSI"), empty = Path.Combine(root,"empty.bin");
        byte[] original = { 1, 2, 0xff, 0, 5 };
        File.WriteAllBytes(source,original); File.WriteAllBytes(empty,Array.Empty<byte>());
        // Invalid MSI contents and an exclusive data lock demonstrate that the
        // feature only reads directory entries, not installer/file contents.
        using (var content = new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var result = DoctorLargeFiles.Run(root,default);
            check(result.Files.KnownFiles == 2 && result.Files.KnownBytes == 5,"Large-file scan counts metadata despite a content lock");
            var file = result.Files.Files.Single(x => x.RelativePath == @"照片\假安装包.MSI");
            check(file.Bytes == 5 && file.InstallerHint,"Installer hint depends only on the case-insensitive final extension, not content validation");
            check(LargeFiles.Filter(result.Files,5,true,default).Single() == file,"Inclusive threshold and name filter operate on snapshot");
        }
        File.SetAttributes(source,FileAttributes.System);
        try
        {
            var skipped = DoctorLargeFiles.Run(root,default);
            check(skipped.Files.KnownFiles == 1 && skipped.Files.Incomplete && skipped.Files.Issues.Any(x => x.RelativePath == @"照片\假安装包.MSI"),"System-marked data is an unknown skipped item, never a zero-byte file");
            check(LargeFiles.Filter(skipped.Files,0,false,default).Count == 1 && skipped.Files.Issues.Count > 0,"All-files filtering retains the separate issues");
        }
        finally { File.SetAttributes(source,FileAttributes.Normal); }
        using (var held = NativeFiles.MetadataDirectory.Open(root))
        {
            var scan = FolderSizes.Scan(held,default,new(MaxEntries:1));
            var partial = LargeFiles.Find(scan,default);
            check(partial.Incomplete && partial.Issues.Any(x => x.Reason == "Scan entry limit"),"Entry-limited native metadata remains explicitly partial");
            reject(() => Directory.Move(root,root+" moved"),"Selected metadata root stays pinned through analysis");
        }
        reject(() => DoctorLargeFiles.Run(root,new CancellationToken(true)),"Large-file pre-cancellation occurs before native access");
        reject(() => DoctorLargeFiles.Run(@"\\server\share",default),"Large-file scan refuses network roots");
        check(File.ReadAllBytes(source).SequenceEqual(original) && new FileInfo(empty).Length == 0,"Inspection preserves original files");
        Directory.Move(root,root+" released");
        check(Directory.Exists(root+" released"),"Large-file scan releases all directory leases before returning");

        // Recycle Bin move with recheck, refusal and Undo (Windows CI runs on a fixed drive with a Recycle Bin).
        string scanned = root+" released";
        string big = Path.Combine(scanned,"big.bin"); File.WriteAllBytes(big,new byte[3000]);
        string linked = Path.Combine(scanned,"linked.bin"); File.WriteAllBytes(linked,new byte[2000]);
        check(CreateHardLink(Path.Combine(scanned,"linked copy.bin"),linked,IntPtr.Zero),"Hard-link fixture created");
        var found = DoctorLargeFiles.Run(scanned,default).Files.Files;
        var bigFile = found.Single(x => x.RelativePath == "big.bin");
        File.AppendAllText(big,"x");
        reject(() => NativeFiles.RecycleFile(big,bigFile.Bytes),"Recycle refuses a file whose size changed since the scan");
        File.WriteAllBytes(big,new byte[3000]);
        reject(() => NativeFiles.RecycleFile(linked,found.Single(x => x.RelativePath == "linked.bin").Bytes),"Recycle refuses a file with another hard link");
        check(File.Exists(big) && File.Exists(linked),"Refused files stay in place");
        if (!RecycleBin.Available(big)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleFile(big,bigFile.Bytes);
        check(!File.Exists(big) && new FileInfo(stored).Length == 3000 && stored.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Recycle moves the large file to the Recycle Bin and reports where: "+stored);
        File.WriteAllBytes(big,new byte[1]);
        reject(() => NativeFiles.RestoreRecycled(big,stored,false),"File Undo refuses while something exists at the original place");
        File.Delete(big);
        NativeFiles.RestoreRecycled(big,stored,false);
        check(new FileInfo(big).Length == 3000 && !File.Exists(stored),"Undo restores the large file from the Recycle Bin");
    }

    [DllImport("kernel32.dll", EntryPoint="CreateHardLinkW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr reserved);
}
