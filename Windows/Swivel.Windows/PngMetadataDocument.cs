using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PngMetadataDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    private readonly byte[] cleaned;
    private bool disposed;
    internal PngMetadata.Plan Plan { get; }
    internal string Path => input.Path;
    private PngMetadataDocument(NativeFiles.Input input,PngMetadata.Plan plan,byte[] cleaned)
    {this.input=input;Plan=plan;this.cleaned=cleaned;}
    internal static PngMetadataDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!System.IO.Path.GetExtension(path).Equals(".png",StringComparison.OrdinalIgnoreCase))throw new UserError(PngMetadata.Invalid);
        var input=NativeFiles.OpenRead(path);
        try
        {
            var plan=PngMetadata.Read(input.Stream,token);
            using var output=new MemoryStream(checked((int)plan.OutputBytes));plan.Write(output,token);var cleaned=output.ToArray();
            // Validate only the cleaned, bounded snapshot through the established
            // built-in codec/SDR/size rules, never the removed text or EXIF payloads.
            var bitmap=ImageTools.DecodeSnapshot(cleaned,path,token);
            if(bitmap.PixelWidth!=plan.Width||bitmap.PixelHeight!=plan.Height)throw new UserError(PngMetadata.Invalid);
            token.ThrowIfCancellationRequested();return new(input,plan,cleaned);
        }
        catch{input.Dispose();throw;}
    }
    internal string Save(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(Plan.RemovedCount==0)throw new UserError("No matching PNG text, EXIF or time chunks were found. No copy was saved.");
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(Path)+" text-exif removed","png",output=>{
            for(int at=0;at<cleaned.Length;)
            {token.ThrowIfCancellationRequested();int count=Math.Min(65536,cleaned.Length-at);output.Write(cleaned.AsSpan(at,count));at+=count;}
            token.ThrowIfCancellationRequested();
        },token);
    }
    public void Dispose(){if(disposed)return;disposed=true;input.Dispose();}
}
