using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class PdfToolsChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        var paths = new[] { Path.Combine(directory,"pdf-a.png"),Path.Combine(directory,"pdf-b.png") };
        for (int i = 0; i < paths.Length; i++)
        {
            var pixels = new byte[(20+i*10)*20*4]; for (int n = 0; n < pixels.Length; n+=4) { pixels[n+i] = 255; pixels[n+3] = 255; }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(20+i*10,20,96,96,PixelFormats.Bgra32,null,pixels,(20+i*10)*4)));
            using var stream = File.Create(paths[i]); encoder.Save(stream);
        }
        byte[][] original = paths.Select(File.ReadAllBytes).ToArray();
        using (var work = ImagePdfWork.Open(paths,CancellationToken.None))
        {
            check(work.Pages.Count == 2 && work.Pages.All(x => x.Thumbnail.IsFrozen),"PDF preview pages frozen");
            reject(() => File.WriteAllBytes(paths[0],new byte[1]),"PDF originals stay leased during preview");
            string output = work.Save(new[] { 1,0 },new(PdfPaper.Image,0),CancellationToken.None);
            check(Path.GetFileName(output) == "pdf-b images.pdf" && Encoding.ASCII.GetString(File.ReadAllBytes(output)).Contains("/Count 2"),"PDF filename follows first reordered page");
            string collision = work.Save(new[] { 1,0 },new(),CancellationToken.None); check(Path.GetFileName(collision) == "pdf-b images 2.pdf","PDF exclusive collision naming");
            reject(() => work.Save(new[] { 0,0 },new(),CancellationToken.None),"PDF duplicate page order refused");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int before = Directory.GetFiles(directory).Length;
            reject(() => work.Save(new[] { 0,1 },new(),cancellation.Token),"Cancelled PDF never publishes");
            check(Directory.GetFiles(directory).Length == before,"PDF cancelled without output or staging leak");
        }
        check(paths.Select((p,i) => File.ReadAllBytes(p).SequenceEqual(original[i])).All(x => x),"PDF original source bytes unchanged");
        string bad = Path.Combine(directory,"pdf-invalid.png"); File.WriteAllText(bad,"broken");
        reject(() => ImagePdfWork.Open(new[] { paths[0],bad },CancellationToken.None).Dispose(),"PDF invalid second page fails before export");
        using (var exclusive = new FileStream(paths[0],FileMode.Open,FileAccess.ReadWrite,FileShare.None)) check(exclusive.Length > 0,"PDF failed preparation releases earlier leases");
    }
}
