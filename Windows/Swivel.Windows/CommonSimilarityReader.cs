using System;
using System.Collections.Generic;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class CommonSimilarityReader
{
    // The App awaits this complete call on its STA worker. The coordinator holds
    // all root leases through the single shared content pass and joins cleanup.
    internal static CommonSimilarityResult Read(CommonSimilarityKind kind, IReadOnlyList<CommonFolderChoice> choices, CancellationToken token)
        => kind switch
        {
            CommonSimilarityKind.Text => CommonSimilarity.ScanText(choices, Open, token),
            CommonSimilarityKind.Images => CommonSimilarity.ScanImages(choices, Open, Fingerprint, token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    private static ulong? Fingerprint(byte[] bytes, string name, CancellationToken token, Action checkpoint)
    {
        checkpoint(); var bitmap = ImageTools.DecodeSnapshot(bytes, name, token); checkpoint();
        var pixels = ImageTools.ReadPixels(bitmap, 0, token); checkpoint();
        return PhotoSimilarity.Fingerprint(pixels, token, checkpoint);
    }
    private static ICommonDuplicateRoot Open(CommonFolderChoice choice)
    {
        var root = NativeFiles.MetadataDirectory.Open(choice.Path!);
        try { return new Root(root); } catch { root.Dispose(); throw; }
    }
    private sealed class Root(NativeFiles.MetadataDirectory source) : ICommonDuplicateRoot
    {
        public string Name => source.Name;
        public IEnumerable<FolderEntry> Entries(CancellationToken token) => source.Entries(token);
        public IFolderMetadata OpenChild(string name) => source.OpenChild(name);
        public IDuplicateInput OpenDuplicate(DuplicateCandidate candidate) => source.OpenDuplicate(candidate);
        public void Dispose() => source.Dispose();
    }
}
