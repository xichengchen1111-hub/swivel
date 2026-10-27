using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DoctorLargeFiles
{
    internal static (FolderScanResult Scan, LargeFileResult Files) Run(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = NativeFiles.MetadataDirectory.Open(path);
        var scan = FolderSizes.Scan(root, token);
        var files = LargeFiles.Find(scan, token);
        token.ThrowIfCancellationRequested(); return (scan, files);
    }
}
