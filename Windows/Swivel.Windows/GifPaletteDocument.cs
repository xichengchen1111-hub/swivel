using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class GifPaletteDocument : IDisposable
{
    private readonly ImageTools.Document source;
    private readonly PixelImage pixels;
    private bool disposed;
    internal string Path=>source.Input.Path;
    internal int Width=>pixels.Width;internal int Height=>pixels.Height;
    internal sealed class Preparation
    {
        internal GifPaletteDocument Owner{get;}
        internal GifPalette.Result Result{get;}
        internal Preparation(GifPaletteDocument owner,GifPalette.Result result){Owner=owner;Result=result;}
    }
    private GifPaletteDocument(ImageTools.Document source,PixelImage pixels){this.source=source;this.pixels=pixels;}
    internal static GifPaletteDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Catalog.IsImage(path))throw new UserError("Choose one supported local image.");
        var source=ImageTools.Open(path,token);
        try{var pixels=ImageTools.ReadPixels(source.Bitmap,0,token);token.ThrowIfCancellationRequested();return new(source,pixels);}
        catch{source.Dispose();throw;}
    }
    internal Preparation Prepare(int maximum,CancellationToken token)
        =>Prepare(maximum,GifDither.None,token);
    internal Preparation Prepare(int maximum,GifDither dither,CancellationToken token)
    {ObjectDisposedException.ThrowIf(disposed,this);return new(this,GifPalette.Reduce(pixels,maximum,dither,token));}
    internal PixelImage OriginalPreview(CancellationToken token)
    {ObjectDisposedException.ThrowIf(disposed,this);return GifPalette.Thumbnail(pixels,960,token);}
    internal string Save(Preparation prepared,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        if(!ReferenceEquals(prepared.Owner,this))throw new ArgumentException("The preview belongs to another image.",nameof(prepared));
        return NativeFiles.WriteBeside(source.Input,System.IO.Path.GetFileNameWithoutExtension(Path)+" reduced colors","gif",output=>prepared.Result.Write(output,token),token);
    }
    public void Dispose(){if(disposed)return;disposed=true;source.Dispose();}
}
