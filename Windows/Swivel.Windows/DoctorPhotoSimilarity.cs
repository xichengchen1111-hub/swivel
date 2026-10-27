using System.Collections.Generic;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DoctorPhotoSimilarity
{
    // Caller runs the complete session on a joined worker STA, never the UI thread.
    internal static (FolderScanResult Folder, PhotoSimilarityResult Similar) Run(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = NativeFiles.MetadataDirectory.Open(path);
        var folder = FolderSizes.Scan(root, token); var candidates = new List<DuplicateCandidate>();
        void Add(FolderSizeNode node, string prefix)
        {
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested(); string relative = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                if (child.Directory) Add(child, relative);
                else if (child.Issue is null && child.Files == 1) candidates.Add(new(relative, (long)child.Bytes));
            }
        }
        Add(folder.Root, "");
        var result = PhotoSimilarity.Scan(candidates, root.OpenDuplicate, (bytes, name, cancellation, checkpoint) => {
            checkpoint(); var bitmap = ImageTools.DecodeSnapshot(bytes, name, cancellation); checkpoint();
            var pixels = ImageTools.ReadPixels(bitmap, 0, cancellation); checkpoint();
            return PhotoSimilarity.Fingerprint(pixels, cancellation, checkpoint);
        }, token);
        token.ThrowIfCancellationRequested(); return (folder, result);
    }
}
