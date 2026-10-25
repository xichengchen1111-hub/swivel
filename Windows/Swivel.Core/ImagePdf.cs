using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum PdfPaper { Image, A4, Letter }
public sealed record PdfPlacement(double PageWidth,double PageHeight,double X,double Y,double Width,double Height);
public sealed record PdfImageSize(int Width,int Height);
public sealed record ImagePdfSettings(PdfPaper Paper = PdfPaper.A4,int Margin = 36,bool AutoLandscape = true)
{
    public PdfPlacement Layout(int width,int height)
    {
        ImageRules.Validate(width,height);
        if (!Enum.IsDefined(Paper) || Margin is < 0 or > 72) throw new UserError("Choose a PDF margin from 0 to 72 points.");
        double pw = Paper switch { PdfPaper.A4 => 595.276, PdfPaper.Letter => 612, _ => width * .75 + 2*Margin };
        double ph = Paper switch { PdfPaper.A4 => 841.89, PdfPaper.Letter => 792, _ => height * .75 + 2*Margin };
        if (Paper != PdfPaper.Image && AutoLandscape && width > height) (pw,ph) = (ph,pw);
        double scale = Math.Min((pw-2*Margin)/width,(ph-2*Margin)/height);
        return new(pw,ph,(pw-width*scale)/2,(ph-height*scale)/2,width*scale,height*scale);
    }
}

/// Original minimal PDF 1.4 image writer. No input PDF parser, fonts, links, actions or metadata.
/// Image samples use zlib-compressed DeviceRGB; stream lengths are indirect objects.
public static class ImagePdf
{
    public static void Validate(IReadOnlyList<PdfImageSize> pages)
    {
        if (pages.Count is < 1 or > 50) throw new UserError("Choose 1–50 images, with at most 100 megapixels in total, for one PDF.");
        long pixels = 0;
        foreach (var page in pages) { ImageRules.Validate(page.Width,page.Height); pixels += (long)page.Width*page.Height; }
        if (pixels > 100_000_000) throw new UserError("Choose 1–50 images, with at most 100 megapixels in total, for one PDF.");
    }
    public static void Write(Stream output,IReadOnlyList<PdfImageSize> pages,ImagePdfSettings settings,Func<int,PixelImage> read,CancellationToken token)
    {
        Validate(pages); var placements = pages.Select(p => settings.Layout(p.Width,p.Height)).ToArray();
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0) throw new ArgumentException("Empty seekable output required.");
        token.ThrowIfCancellationRequested();
        var offsets = new long[3+pages.Count*4];
        void Text(string value) { token.ThrowIfCancellationRequested(); output.Write(Encoding.ASCII.GetBytes(value)); }
        void Object(int id,string body) { offsets[id] = output.Position; Text($"{id} 0 obj\n{body}\nendobj\n"); }
        static string Number(double value) => value.ToString("0.######",CultureInfo.InvariantCulture);
        Text("%PDF-1.4\n"); output.Write(new byte[] { 37,226,227,207,211,10 });
        Object(1,"<< /Type /Catalog /Pages 2 0 R >>");
        Object(2,$"<< /Type /Pages /Count {pages.Count} /Kids ["+string.Join(" ",Enumerable.Range(0,pages.Count).Select(i => $"{3+i*4} 0 R"))+"] >>");
        for (int i = 0; i < pages.Count; i++)
        {
            token.ThrowIfCancellationRequested(); int page = 3+i*4, image = page+1, content = page+2, length = page+3; var place = placements[i];
            Object(page,$"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(place.PageWidth)} {Number(place.PageHeight)}] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>");
            var pixels = read(i); pixels.Validate();
            if (pixels.Width != pages[i].Width || pixels.Height != pages[i].Height) throw new InvalidDataException("Image dimensions changed.");
            offsets[image] = output.Position;
            Text($"{image} 0 obj\n<< /Type /XObject /Subtype /Image /Width {pixels.Width} /Height {pixels.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {length} 0 R >>\nstream\n");
            long start = output.Position;
            using (var zip = new ZLibStream(output,CompressionLevel.Fastest,leaveOpen:true))
            {
                var row = new byte[pixels.Width*3];
                for (int y = 0; y < pixels.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        int source = (y*pixels.Width+x)*4, dest = x*3, alpha = pixels.Bgra[source+3];
                        for (int c = 0; c < 3; c++) row[dest+c] = (byte)((pixels.Bgra[source+2-c]*alpha+255*(255-alpha)+127)/255);
                    }
                    zip.Write(row);
                }
            }
            long size = output.Position-start; Text("\nendstream\nendobj\n"); Object(length,size.ToString(CultureInfo.InvariantCulture));
            string drawing = $"q\n{Number(place.Width)} 0 0 {Number(place.Height)} {Number(place.X)} {Number(place.Y)} cm\n/Im0 Do\nQ\n";
            Object(content,$"<< /Length {Encoding.ASCII.GetByteCount(drawing)} >>\nstream\n{drawing}endstream");
        }
        long crossReference = output.Position; Text($"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Length; i++) Text(offsets[i].ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{crossReference.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
    }
}
