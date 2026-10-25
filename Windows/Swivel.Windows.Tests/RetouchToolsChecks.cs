using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class RetouchToolsChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        int w = 160,h = 100; var pixels = new byte[w*h*4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int p = (y*w+x)*4; byte value = (byte)((x/4+y/4)%2 == 0 ? 30 : 220); pixels[p] = pixels[p+1] = pixels[p+2] = value; pixels[p+3] = (byte)(y < 10 ? 0 : 255); }
        for (int x = 65; x < 78; x++) { int p = (30*w+x)*4; pixels[p] = 255; pixels[p+1] = 0; }
        string path = Path.Combine(directory,"retouch original.png"); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4))); using (var stream = File.Create(path)) encoder.Save(stream);
        byte[] original = File.ReadAllBytes(path); var strokes = new[] { new RetouchStroke(new[] { new ImagePoint(.4,.3),new ImagePoint(.5,.3) },.05) };
        using (var document = ImageTools.Open(path,CancellationToken.None))
        {
            var before = ImageTools.ReadPixels(document.Bitmap,0,CancellationToken.None).Bgra;
            string first = ImageTools.Export(document,"image.retouch",1920,CancellationToken.None,retouch:strokes);
            string second = ImageTools.Export(document,"image.retouch",1920,CancellationToken.None,retouch:strokes);
            check(Path.GetFileName(first) == "retouch original retouched.png" && Path.GetFileName(second) == "retouch original retouched 2.png","Retouch creates exclusive PNG beside original");
            using var repaired = ImageTools.Open(first,CancellationToken.None); var result = ImageTools.ReadPixels(repaired.Bitmap,0,CancellationToken.None);
            check(Enumerable.Range(65,13).All(x => result.Bgra[(30*w+x)*4] == result.Bgra[(30*w+x)*4+1]),"Retouch full-size PNG repairs painted test object");
            bool outside = true;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (y < 24 || y > 36 || x < 58 || x > 86) outside &= result.Bgra.AsSpan((y*w+x)*4,4).SequenceEqual(before.AsSpan((y*w+x)*4,4));
            check(outside,"Retouch PNG preserves untouched normalized SDR pixels including transparent rows");
            int count = Directory.GetFiles(directory).Length; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            reject(() => ImageTools.Export(document,"image.retouch",1920,cancelled.Token,retouch:strokes),"Cancelled Retouch creates no output");
            reject(() => ImageTools.Export(document,"image.retouch",1920,CancellationToken.None,retouch:Array.Empty<RetouchStroke>()),"Empty Retouch save refused");
            check(Directory.GetFiles(directory).Length == count,"Rejected repair leaves no staging or result");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"Retouch source bytes unchanged");
        using var exclusive = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); check(exclusive.Length == original.Length,"Retouch releases source lease after editor operation");
    }
}
