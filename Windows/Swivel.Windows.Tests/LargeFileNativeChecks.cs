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
    }
}
