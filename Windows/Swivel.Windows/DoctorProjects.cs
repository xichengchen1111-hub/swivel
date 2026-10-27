using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DoctorProjects
{
    internal static (FolderScanResult Scan, ProjectFolderResult Projects) Run(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = NativeFiles.MetadataDirectory.Open(path);
        var scan = FolderSizes.Scan(root, token);
        var projects = ProjectFolders.Find(scan, token);
        token.ThrowIfCancellationRequested(); return (scan, projects);
    }
}
