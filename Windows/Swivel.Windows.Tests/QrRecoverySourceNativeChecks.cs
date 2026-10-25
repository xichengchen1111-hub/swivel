using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class QrRecoverySourceNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"qr-recovery-source");Directory.CreateDirectory(folder);
        const string text="QR source lease recovery 中文 😀";
        var code=QrCode.Encode(text,QrCorrection.High);const int scale=3;
        int side=(code.Size+8)*scale;byte[] clean=code.Raster(scale),changed=(byte[])clean.Clone();
        // Bottom-right module is the first data-codeword bit, outside all function modules.
        int first=(code.Size+3)*scale;
        for(int y=first;y<first+scale;y++)for(int x=first;x<first+scale;x++)changed[y*side+x]=(byte)(255-changed[y*side+x]);
        string original=Path.Combine(folder,"clean.png"),damaged=Path.Combine(folder,"damaged.png");
        void Save(string path,byte[] bytes)
        {
            var bitmap=BitmapSource.Create(side,side,96,96,PixelFormats.Gray8,null,bytes,side);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);encoder.Save(output);
        }
        Save(original,clean);Save(damaged,changed);byte[] before=File.ReadAllBytes(damaged);
        using(var strict=QrReadDocument.Open(original,default))check(strict.Recovery is null&&strict.Result.Text==text,"Original QR source factory remains strict and has no recovery mode");
        using(var recovered=QrReadDocument.OpenForRecovery(original,default))
            check(recovered.Recovery is {CorrectedCodewords:0,CorrectedBlocks:0}&&recovered.Result.Text==text,"Explicit recovery can read clean source with zero candidate changes");
        reject(()=>QrReadDocument.Open(damaged,default).Dispose(),"Old source factory does not automatically recover damaged data");
        using(var recovered=QrReadDocument.OpenForRecovery(damaged,default))
        {
            check(recovered.Recovery is {CorrectedCodewords:1,CorrectedBlocks:1}&&recovered.Result.Text==text,"Explicit source factory retains recovery metadata and full original text");
            reject(()=>File.WriteAllBytes(damaged,new byte[1]),"Recovery source stays pinned through result review");
            reject(()=>Directory.Move(folder,folder+" moved"),"Recovery source ancestors stay pinned through result review");
        }
        check(File.ReadAllBytes(damaged).SequenceEqual(before)&&Directory.GetFiles(folder).Length==2,"Recovery does not rewrite the image or create an output");
        bool canceled=false;try{QrReadDocument.OpenForRecovery(Path.Combine(folder,"not-created.png"),new CancellationToken(true)).Dispose();}
        catch(OperationCanceledException){canceled=true;}
        check(canceled,"Pre-cancelled recovery ends before attempting a missing input");
        var invalid=(byte[])clean.Clone();invalid[0]=0;string bad=Path.Combine(folder,"bad-border.png");Save(bad,invalid);
        reject(()=>QrReadDocument.OpenForRecovery(bad,default).Dispose(),"Recovery still refuses a damaged quiet border");
        File.Move(bad,bad+" released");Directory.Move(folder,folder+" released");
        check(Directory.Exists(folder+" released"),"Both success and failure release all recovery source leases");
    }
}
