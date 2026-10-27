using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    /// The identity of a local regular file, read through an attribute-only handle with every
    /// ancestor held, so a link or cloud placeholder is refused rather than followed or downloaded.
    public static FileStamp InspectStamp(string path)
    {
        WindowsPathPolicy.Validate(path);
        using var parent = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!);
        using var probe = Open(path, ReadAttributes, 7, 3, OpenReparse);
        return ReadStamp(probe);
    }

    /// The file's own name as the file system stores it (its long name, in its exact case),
    /// read through the same attribute-only handle.
    public static string StoredName(string path)
    {
        WindowsPathPolicy.Validate(path);
        using var parent = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!);
        using var probe = Open(path, ReadAttributes, 7, 3, OpenReparse);
        Check(probe, false);
        return Leaf(FinalPath(probe));
    }
    private static string Leaf(string path) => path[(path.LastIndexOf('\\') + 1)..];
}
