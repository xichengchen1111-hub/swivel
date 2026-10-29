using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class TiffMetadataDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    private readonly byte[] cleaned;
    private readonly byte[]? kept;
    private bool disposed;
    internal TiffMetadata.Plan Plan { get; }
    internal string Path => input.Path;
    internal bool CanKeepOrientation => kept is not null;
    private TiffMetadataDocument(NativeFiles.Input input,TiffMetadata.Plan plan,byte[] cleaned,byte[]? kept)
    {this.input=input;Plan=plan;this.cleaned=cleaned;this.kept=kept;}
    internal static TiffMetadataDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string extension=System.IO.Path.GetExtension(path);
        if(!extension.Equals(".tif",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".tiff",StringComparison.OrdinalIgnoreCase))throw new UserError(TiffMetadata.Invalid);
        var input=NativeFiles.OpenRead(path);
        try
        {
            var plan=TiffMetadata.Read(input.Stream,token);
            using var output=new MemoryStream(checked((int)plan.OutputBytes));plan.Write(output,token);var cleaned=output.ToArray();
            // Only the rebuilt snapshot reaches the built-in TIFF decoder, never removed tag payloads.
            Validate(cleaned,plan,token);
            byte[]? kept=null;
            if(plan.Orientation.CanKeep)
            {
                using var keeping=new MemoryStream(checked((int)plan.OutputBytesKeeping(true)));plan.Write(keeping,true,token);kept=keeping.ToArray();
                // A failure here only withdraws the option; the plain rebuilt copy stays available.
                try{Validate(kept,plan,token);}
                catch(Exception error) when (error is UserError or System.Runtime.InteropServices.COMException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException or OverflowException){kept=null;}
            }
            token.ThrowIfCancellationRequested();return new(input,plan,cleaned,kept);
        }
        catch{input.Dispose();throw;}
    }
    // Every page is decoded by the built-in WIC TIFF codec (never another installed decoder)
    // and must have the stored size the container declared. The core reader already bounds
    // all pages together to 16 MP, so this decodes at most 64 MB of BGRA one page at a time.
    private static void Validate(byte[] bytes,TiffMetadata.Plan plan,CancellationToken token)
    {
        try
        {
            using var stream=new MemoryStream(bytes,false);
            var decoder=new TiffBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreImageCache,BitmapCacheOption.OnDemand);
            if(decoder.Frames.Count!=plan.PageCount)throw new UserError(TiffMetadata.Invalid);
            for(int i=0;i<plan.PageCount;i++)
            {
                token.ThrowIfCancellationRequested();
                var frame=decoder.Frames[i];var (width,height)=plan.PageSizes[i];
                if(frame.PixelWidth!=width||frame.PixelHeight!=height)throw new UserError(TiffMetadata.Invalid);
                var pixels=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);var row=new byte[checked(width*4)];
                for(int y=0;y<height;y++)
                {if((y&63)==0)token.ThrowIfCancellationRequested();pixels.CopyPixels(new System.Windows.Int32Rect(0,y,width,1),row,row.Length,0);}
            }
        }
        catch(Exception error) when (error is System.Runtime.InteropServices.COMException or NotSupportedException or ArgumentException or FileFormatException or OverflowException){throw new UserError(TiffMetadata.Invalid);}
    }
    internal string Save(bool keepOrientation,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(keepOrientation&&kept is null)throw new InvalidOperationException("No source orientation to keep.");
        var rebuilt=keepOrientation?kept!:cleaned;
        if(Plan.RemovedCount==0)throw new UserError("No removable TIFF metadata tags were found. No copy was saved.");
        string extension=System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(Path)+" metadata removed",extension,output=>{
            for(int at=0;at<rebuilt.Length;)
            {token.ThrowIfCancellationRequested();int count=Math.Min(65536,rebuilt.Length-at);output.Write(rebuilt.AsSpan(at,count));at+=count;}
            token.ThrowIfCancellationRequested();
        },token);
    }
    public void Dispose(){if(disposed)return;disposed=true;input.Dispose();}
}
