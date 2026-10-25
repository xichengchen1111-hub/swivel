using System;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class QrReadDocument:IDisposable
{
    private readonly ImageTools.Document source;
    internal string Path=>source.Input.Path;
    internal QrReadResult Result{get;}
    internal QrDataRecoveryResult? Recovery{get;}
    private QrReadDocument(ImageTools.Document source,QrReadResult result,QrDataRecoveryResult? recovery=null)
    {this.source=source;Result=result;Recovery=recovery;}
    internal static QrReadDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Catalog.IsImage(path))throw new UserError("Choose one supported local image.");
        var source=ImageTools.Open(path,token);
        try
        {
            var result=QrFlatRead.Read(ImageTools.ReadPixels(source.Bitmap,0,token),token);
            token.ThrowIfCancellationRequested();return new(source,result);
        }
        catch{source.Dispose();throw;}
    }
    internal static QrReadDocument OpenForRecovery(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Catalog.IsImage(path))throw new UserError("Choose one supported local image.");
        var source=ImageTools.Open(path,token);
        try
        {
            var recovery=QrFlatRead.ReadWithDataRecovery(ImageTools.ReadPixels(source.Bitmap,0,token),token);
            token.ThrowIfCancellationRequested();return new(source,recovery.Result,recovery);
        }
        catch{source.Dispose();throw;}
    }
    public void Dispose()=>source.Dispose();
}
