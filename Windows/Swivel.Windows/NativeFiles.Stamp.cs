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
}
