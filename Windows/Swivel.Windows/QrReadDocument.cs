using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class QrReadDocument:IDisposable
{
    private readonly ImageTools.Document source;
    internal string Path=>source.Input.Path;
    internal IReadOnlyList<ImageCodeResult> Results{get;}
    internal QrReadResult Result=>Results.FirstOrDefault(r=>r.Qr is not null)?.Qr??throw new UserError(QrPhotoRead.NoCode);
    internal QrDataRecoveryResult? Recovery{get;}
    private QrReadDocument(ImageTools.Document source,IReadOnlyList<ImageCodeResult> results,QrDataRecoveryResult? recovery=null)
    {this.source=source;Results=results;Recovery=recovery;}
    internal static QrReadDocument Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Catalog.IsImage(path))throw new UserError("Choose one supported local image.");
        var source=ImageTools.OpenForReading(path,token);
        try
        {
            var results=ImageCodeRead.Read(ImageTools.ReadPixels(source.Bitmap,0,token),token);
            token.ThrowIfCancellationRequested();if(results.Count==0)throw new UserError(ImageCodeRead.NoCode);return new(source,results);
        }
        catch{source.Dispose();throw;}
    }
    internal static QrReadDocument OpenForRecovery(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Catalog.IsImage(path))throw new UserError("Choose one supported local image.");
        var source=ImageTools.OpenForReading(path,token);
        try
        {
            var recovery=QrFlatRead.ReadWithDataRecovery(ImageTools.ReadPixels(source.Bitmap,0,token),token);
            token.ThrowIfCancellationRequested();return new(source,new[]{new ImageCodeResult("QR",recovery.Result.Text,0,0,source.Bitmap.PixelWidth,source.Bitmap.PixelHeight,recovery.Result,recovery.Result.ClockwiseOrientationDegrees)},recovery);
        }
        catch{source.Dispose();throw;}
    }
    public void Dispose()=>source.Dispose();
}
