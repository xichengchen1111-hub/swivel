using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class StartupFolderReader
{
    internal static IReadOnlyList<StartupFolderChoice> ReadLocations(CancellationToken token)
    {
        var choices=new List<StartupFolderChoice>(2);
        foreach(var kind in Enum.GetValues<StartupFolderKind>())
        {
            token.ThrowIfCancellationRequested();StartupFolderChoice choice;
            try
            {
                var folder=kind==StartupFolderKind.CurrentUser?Environment.SpecialFolder.Startup:Environment.SpecialFolder.CommonStartup;
                string path=Environment.GetFolderPath(folder,Environment.SpecialFolderOption.DoNotVerify);token.ThrowIfCancellationRequested();choice=StartupFolderChoice.FromReported(kind,path);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
            {token.ThrowIfCancellationRequested();choice=StartupFolderChoice.Unavailable(kind);}
            token.ThrowIfCancellationRequested();choices.Add(choice);
        }
        return choices.AsReadOnly();
    }
    internal static IReadOnlyList<StartupFolderResult> ReadEntries(IReadOnlyList<StartupFolderChoice> choices,CancellationToken token)=>StartupFolders.Scan(choices,c=>NativeFiles.MetadataDirectory.Open(c.Path!),token);
}
