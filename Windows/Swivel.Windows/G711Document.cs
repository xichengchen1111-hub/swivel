using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class G711Document:IDisposable
{
    private readonly NativeFiles.Input input;
    public G711Info Info{get;}
    public string Path=>input.Path;
    private G711Document(NativeFiles.Input input,G711Info info){this.input=input;Info=info;}
    public static G711Document Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);
        try{var info=G711Wave.Read(input.Stream,token);info.OutputBytes();token.ThrowIfCancellationRequested();return new(input,info);}
        catch{input.Dispose();throw;}
    }
    // Modal caller serializes stream use and joins preview before save/disposal.
    public byte[] Preview(CancellationToken token)
    {using var output=new MemoryStream();G711Wave.Export(input.Stream,output,token,preview:true);return output.ToArray();}
    public string Save(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Info.OutputBytes();
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(input.Path)+" decoded","wav",output=>G711Wave.Export(input.Stream,output,token),token);
    }
    public void Dispose()=>input.Dispose();
}
