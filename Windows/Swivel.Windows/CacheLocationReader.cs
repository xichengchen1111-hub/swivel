using System;
using System.IO;
using System.Security;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class CacheLocationReader
{
    internal static System.Collections.Generic.IReadOnlyList<CacheLocation> Read(CancellationToken token)
    {
        string? Resolve(Func<string> read)
        {
            token.ThrowIfCancellationRequested();string? path;
            try{path=read();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException){path=null;}
            token.ThrowIfCancellationRequested();return path;
        }
        string? temporary=Resolve(System.IO.Path.GetTempPath);
        string? internet=Resolve(()=>Environment.GetFolderPath(Environment.SpecialFolder.InternetCache,Environment.SpecialFolderOption.DoNotVerify));
        token.ThrowIfCancellationRequested();return CacheLocations.Choices(temporary,internet);
    }
    internal static FolderScanResult Scan(CacheLocation location,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string path=location.Path??throw new UserError("Choose an available cache location first.");
        try
        {
            using var root=NativeFiles.MetadataDirectory.Open(path);var result=FolderSizes.Scan(root,token);token.ThrowIfCancellationRequested();return result;
        }
        catch { token.ThrowIfCancellationRequested();throw; }
    }
}
