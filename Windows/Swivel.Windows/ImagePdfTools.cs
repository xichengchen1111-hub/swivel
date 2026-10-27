using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public sealed record PdfSource(int Index,string Path,PdfImageSize Size,BitmapSource Thumbnail);
public sealed class ImagePdfWork : IDisposable
{
    private readonly List<NativeFiles.Input> inputs = new();
    private readonly List<PdfSource> pages = new();
    public IReadOnlyList<PdfSource> Pages => pages.AsReadOnly();
    private ImagePdfWork() { }
    public static ImagePdfWork Open(IReadOnlyList<string> paths,CancellationToken token)
    {
        if (paths.Count is < 1 or > 50) throw new UserError("Choose 1–50 images, with at most 100 megapixels in total, for one PDF.");
        var work = new ImagePdfWork();
        try
        {
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path); work.inputs.Add(input);
                var bitmap = ImageTools.Decode(input,token); var size = new PdfImageSize(bitmap.PixelWidth,bitmap.PixelHeight);
                ImagePdf.Validate(work.pages.Select(x => x.Size).Append(size).ToArray());
                var small = ImageTools.ReadPixels(bitmap,320,token);
                var thumbnail = BitmapSource.Create(small.Width,small.Height,96,96,PixelFormats.Bgra32,null,small.Bgra,small.Width*4); thumbnail.Freeze();
                work.pages.Add(new(work.pages.Count,path,size,thumbnail));
            }
            return work;
        }
        catch { work.Dispose(); throw; }
    }
    public string Save(int[] order,ImagePdfSettings settings,CancellationToken token)
    {
        if (order.Length != pages.Count || !order.Order().SequenceEqual(Enumerable.Range(0,pages.Count))) throw new ArgumentException("Complete page order required.");
        var sizes = order.Select(i => pages[i].Size).ToArray(); ImagePdf.Validate(sizes);
        var first = inputs[order[0]];
        return NativeFiles.WriteBeside(first,Path.GetFileNameWithoutExtension(first.Path)+" images","pdf",stream =>
            ImagePdf.Write(stream,sizes,settings,i => ImageTools.ReadPixels(ImageTools.Decode(inputs[order[i]],token),0,token),token),token);
    }
    public void Dispose() { foreach (var input in inputs) input.Dispose(); inputs.Clear(); }
}
