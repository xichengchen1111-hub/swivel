using System.Collections.Generic;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DoctorTextSimilarity
{
    internal static (FolderScanResult Folder, TextSimilarityResult Similar) Run(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = NativeFiles.MetadataDirectory.Open(path);
        var folder = FolderSizes.Scan(root, token);
        var candidates = new List<DuplicateCandidate>();
        void Add(FolderSizeNode node, string prefix)
        {
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                string relative = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                if (child.Directory) Add(child, relative);
                else if (child.Issue is null && child.Files == 1) candidates.Add(new(relative, (long)child.Bytes));
            }
        }
        Add(folder.Root, "");
        var similar = TextSimilarity.Scan(candidates, root.OpenDuplicate, token);
        token.ThrowIfCancellationRequested(); return (folder, similar);
    }
}
