using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class JpegMetadataDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    private readonly byte[] cleaned;
    private bool disposed;
    internal JpegMetadata.Plan Plan { get; }
    internal string Path => input.Path;
    private JpegMetadataDocument(NativeFiles.Input input,JpegMetadata.Plan plan,byte[] cleaned)
    {this.input=input;Plan=plan;this.cleaned=cleaned;}
    internal static JpegMetadataDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string extension=System.IO.Path.GetExtension(path);
        if(!extension.Equals(".jpg",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".jpeg",StringComparison.OrdinalIgnoreCase))throw new UserError(JpegMetadata.Invalid);
        var input=NativeFiles.OpenRead(path);
        try
        {
            var plan=JpegMetadata.Read(input.Stream,token);
            using var output=new MemoryStream(checked((int)plan.OutputBytes));plan.Write(output,token);var cleaned=output.ToArray();
            // Validate only the cleaned, bounded snapshot through the established
            // built-in codec/SDR/size rules, never the removed APP1/APP13/COM payloads.
            var bitmap=ImageTools.DecodeSnapshot(cleaned,path,token);
            if(bitmap.PixelWidth!=plan.Width||bitmap.PixelHeight!=plan.Height)throw new UserError(JpegMetadata.Invalid);
            token.ThrowIfCancellationRequested();return new(input,plan,cleaned);
        }
        catch{input.Dispose();throw;}
    }
    internal string Save(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(Plan.RemovedCount==0)throw new UserError("No JPEG APP1, APP13 or comment segments were found. No copy was saved.");
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(Path)+" exif-comments removed","jpg",output=>{
            for(int at=0;at<cleaned.Length;)
            {token.ThrowIfCancellationRequested();int count=Math.Min(65536,cleaned.Length-at);output.Write(cleaned.AsSpan(at,count));at+=count;}
            token.ThrowIfCancellationRequested();
        },token);
    }
    public void Dispose(){if(disposed)return;disposed=true;input.Dispose();}
}
