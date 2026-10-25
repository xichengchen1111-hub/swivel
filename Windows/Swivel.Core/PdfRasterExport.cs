using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// Raster-only PDF writer with explicit physical page sizes. No vectors or document metadata.
/// Image samples use zlib-compressed DeviceRGB; stream lengths are indirect objects.
public static class PdfRasterExport
{
    public static void Write(Stream output,IReadOnlyList<PdfRasterPage> pages,Func<int,PixelImage> read,CancellationToken token)
    {
        ImagePdf.Validate(pages.Select(p=>new PdfImageSize(p.Width,p.Height)).ToArray());
        if(pages.Any(p=>!double.IsFinite(p.PointsWidth)||!double.IsFinite(p.PointsHeight)||p.PointsWidth<=0||p.PointsHeight<=0||p.PointsWidth>14400||p.PointsHeight>14400))throw new UserError(PdfRasterPlan.Invalid);
        var placements=pages.Select(p=>new PdfPlacement(p.PointsWidth,p.PointsHeight,0,0,p.PointsWidth,p.PointsHeight)).ToArray();
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
