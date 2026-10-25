using System.Collections.Generic;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class CommonDuplicateReader
{
    internal static CommonDuplicateResult Read(IReadOnlyList<CommonFolderChoice> choices, CancellationToken token) => CommonDuplicates.Scan(choices, Open, token);
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
