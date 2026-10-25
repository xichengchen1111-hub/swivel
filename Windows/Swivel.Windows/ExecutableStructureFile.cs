using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ExecutableStructureFile:IDisposable
{
    private readonly NativeFiles.Input input;
    internal string Path=>input.Path;
    internal PeStructure Info{get;}
    private ExecutableStructureFile(NativeFiles.Input input,PeStructure info){this.input=input;Info=info;}
    internal static ExecutableStructureFile Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!System.IO.Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new UserError("Choose one local EXE file for this inspection.");
        var input=NativeFiles.OpenRead(path);
        try{return new(input,PeInspection.Read(input.Stream,token));}catch{input.Dispose();throw;}
    }
    public void Dispose()=>input.Dispose();
}
