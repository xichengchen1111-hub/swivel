using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PngMetadataDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    private readonly byte[] cleaned;
    private readonly byte[]? kept;
    private bool disposed;
    internal PngMetadata.Plan Plan { get; }
    internal string Path => input.Path;
    internal bool CanKeepOrientation => kept is not null;
    private PngMetadataDocument(NativeFiles.Input input,PngMetadata.Plan plan,byte[] cleaned,byte[]? kept)
    {this.input=input;Plan=plan;this.cleaned=cleaned;this.kept=kept;}
    /// With `expected` (an agent's plan), a file that is no longer the planned one is refused before it is read.
    internal static PngMetadataDocument Open(string path,CancellationToken token,FileStamp? expected=null)
    {
        token.ThrowIfCancellationRequested();
        if(!System.IO.Path.GetExtension(path).Equals(".png",StringComparison.OrdinalIgnoreCase))throw new UserError(PngMetadata.Invalid);
        var input=NativeFiles.OpenRead(path);
        try
        {
            if(expected is FileStamp planned&&!input.Stamp.SameObjectAndContent(planned))throw new McpToolError(McpSession.Changed);
            var plan=PngMetadata.Read(input.Stream,token);
            using var output=new MemoryStream(checked((int)plan.OutputBytes));plan.Write(output,token);var cleaned=output.ToArray();
            // Validate only the cleaned, bounded snapshot through the established
            // built-in codec/SDR/size rules, never the removed text or EXIF payloads.
            var bitmap=ImageTools.DecodeSnapshot(cleaned,path,token);
            byte[]? kept=null;
            if(plan.Orientation.CanKeep)
            {
                // The orientation-keeping variant differs only by the new minimal EXIF;
                // it is validated the same way before it can be offered for saving.
                using var keeping=new MemoryStream(checked((int)plan.OutputBytesKeeping(true)));plan.Write(keeping,true,token);kept=keeping.ToArray();
                // The shared decoder applies EXIF orientation when the codec exposes it, so 5–8
                // may come back with width and height swapped. A failure here only withdraws the option; the plain
                // cleaned copy stays available.
                try
                {
                    var check=ImageTools.DecodeSnapshot(kept,path,token);bool swap=plan.Orientation.Value>=5;
                    bool stored=check.PixelWidth==plan.Width&&check.PixelHeight==plan.Height,oriented=swap&&check.PixelWidth==plan.Height&&check.PixelHeight==plan.Width;
                    if(!stored&&!oriented)kept=null;
                }
                catch(Exception error) when (error is UserError or System.Runtime.InteropServices.COMException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException){kept=null;}
            }
            if(bitmap.PixelWidth!=plan.Width||bitmap.PixelHeight!=plan.Height)throw new UserError(PngMetadata.Invalid);
            token.ThrowIfCancellationRequested();return new(input,plan,cleaned,kept);
        }
        catch{input.Dispose();throw;}
    }
    internal string Save(CancellationToken token)=>Save(false,token);
    internal string Save(bool keepOrientation,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(keepOrientation&&kept is null)throw new InvalidOperationException("No source orientation to keep.");
        var cleaned=keepOrientation?kept!:this.cleaned;
        if(Plan.RemovedCount==0)throw new UserError("No matching PNG text, EXIF or time chunks were found. No copy was saved.");
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(Path)+" text-exif removed","png",output=>{
            for(int at=0;at<cleaned.Length;)
            {token.ThrowIfCancellationRequested();int count=Math.Min(65536,cleaned.Length-at);output.Write(cleaned.AsSpan(at,count));at+=count;}
            token.ThrowIfCancellationRequested();
        },token);
    }
    public void Dispose(){if(disposed)return;disposed=true;input.Dispose();}
}
