using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// A small PDF writer for Make PDF: one page per image, the page the image's pixel size in points (like PDFPage(image:)
/// at 72 dpi). Opaque images are embedded as JPEG (DCTDecode; a JPEG source that needs no rotation or colour
/// conversion is copied byte for byte), images with transparency as Flate RGB plus a Flate soft mask.
static class ImagesPdf
{
    /// PDF viewers (Acrobat) reject pages larger than 200 inches; bigger images are scaled down to fit.
    const double MaxPageSide = 14400;

    public static void Write(IReadOnlyList<string> paths, string output)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var temp = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                try { File.SetAttributes(temp, FileAttributes.Hidden); } catch (IOException) { }
                var writer = new PdfStreamWriter(file);
                writer.Begin();
                var pageIds = new List<int>();
                foreach (var path in paths) pageIds.Add(writer.AddImagePage(Prepare(path)));
                writer.Finish(pageIds);
            }
            File.Move(temp, output, overwrite: false);
            Images.Unhide(output);
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (e is ToolError) throw;
            if (e is IOException or UnauthorizedAccessException) throw new ToolError(L("Couldn't write PDF"));
            throw;
        }
    }

    internal sealed record PageImage(int Width, int Height, byte[] Data, bool Jpeg, bool Gray, byte[]? Mask);

    static PageImage Prepare(string path)
    {
        if (TryPassthrough(path) is { } direct) return direct;
        var raster = Images.LoadRaster(path);
        if (!raster.Alpha)
        {
            var jpeg = Images.Encode(raster.ToBitmap(), "jpg", 0.92);
            return new PageImage(raster.Width, raster.Height, jpeg, true, false, null);
        }
        var rgb = new byte[raster.Width * raster.Height * 3];
        var alpha = new byte[raster.Width * raster.Height];
        for (int i = 0; i < raster.Data.Length; i++)
        {
            int p = raster.Data[i];
            rgb[i * 3] = (byte)(p >> 16); rgb[i * 3 + 1] = (byte)(p >> 8); rgb[i * 3 + 2] = (byte)p;
            alpha[i] = (byte)(p >> 24);
        }
        return new PageImage(raster.Width, raster.Height, Deflate(rgb), false, false, Deflate(alpha));
    }

    /// A baseline RGB or gray JPEG that is already upright and untagged goes in as it is (no second compression).
    static PageImage? TryPassthrough(string path)
    {
        if (Images.Canonical(FileKinds.Ext(path)) != "jpg") return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (decoder is not JpegBitmapDecoder) return null;
            var frame = decoder.Frames[0];
            if (Images.Orientation(frame) != 1) return null;
            try { if (frame.ColorContexts is { Count: > 0 }) return null; } catch (NotSupportedException) { }
            bool gray = frame.Format == PixelFormats.Gray8;
            if (!gray && frame.Format != PixelFormats.Bgr24) return null;   // CMYK and others are re-encoded
            return new PageImage(frame.PixelWidth, frame.PixelHeight, bytes, true, gray, null);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or ArgumentException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }

    static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    sealed class PdfStreamWriter(Stream stream)
    {
        readonly List<long> offsets = [0, 0, 0];  // index = object number; 1 catalog and 2 page tree are written last
        long position;

        void Raw(byte[] bytes) { stream.Write(bytes); position += bytes.Length; }
        void Text(string s) => Raw(Encoding.ASCII.GetBytes(s));

        int Reserve() { offsets.Add(0); return offsets.Count - 1; }

        void Object(int id, string dictionary, byte[]? data = null)
        {
            offsets[id] = position;
            Text($"{id} 0 obj\n{dictionary}\n");
            if (data != null)
            {
                Text("stream\n");
                Raw(data);
                Text("\nendstream\n");
            }
            Text("endobj\n");
        }

        public void Begin()
        {
            Text("%PDF-1.4\n");
            Raw([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        }

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public int AddImagePage(PageImage image)
        {
            double scale = Math.Min(1, MaxPageSide / Math.Max(image.Width, image.Height));
            double w = image.Width * scale, h = image.Height * scale;
            int page = Reserve(), contents = Reserve(), xobject = Reserve();
            int? mask = image.Mask != null ? Reserve() : null;

            var draw = Encoding.ASCII.GetBytes($"q\n{N(w)} 0 0 {N(h)} 0 0 cm\n/Im0 Do\nQ\n");
            Object(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(w)} {N(h)}] " +
                         $"/Resources << /XObject << /Im0 {xobject} 0 R >> >> /Contents {contents} 0 R >>");
            Object(contents, $"<< /Length {draw.Length} >>", draw);
            var space = image.Gray ? "/DeviceGray" : "/DeviceRGB";
            var filter = image.Jpeg ? "/DCTDecode" : "/FlateDecode";
            var smask = mask is int m ? $" /SMask {m} 0 R" : "";
            Object(xobject, $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {space} " +
                            $"/BitsPerComponent 8 /Filter {filter}{smask} /Length {image.Data.Length} >>", image.Data);
            if (mask is int maskId)
                Object(maskId, $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray " +
                               $"/BitsPerComponent 8 /Filter /FlateDecode /Length {image.Mask!.Length} >>", image.Mask);
            return page;
        }

        public void Finish(List<int> pages)
        {
            Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
            Object(2, $"<< /Type /Pages /Kids [{string.Join(" ", pages.Select(p => $"{p} 0 R"))}] /Count {pages.Count} >>");
            int info = Reserve();
            Object(info, "<< /Producer (Swivel) >>");
            long xref = position;
            var sb = new StringBuilder();
            sb.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
            for (int i = 1; i < offsets.Count; i++) sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Text(sb.ToString());
        }
    }
}
