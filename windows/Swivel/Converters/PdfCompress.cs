using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Makes a PDF smaller, standing in for PDFKit's "save images as JPEG / optimize images for screen" options:
/// photos are re-encoded as JPEG (and very large ones scaled down), uncompressed streams are Flate-compressed, unused
/// objects are dropped and objects are packed into compressed object streams.
static class PdfCompress
{
    /// Longest image side kept (a US Letter page at ~180 DPI); larger images are scaled down.
    public const int MaxImageSide = 2000;
    public const int JpegQuality = 60;

    public static void Write(PdfFile file, Stream output)
    {
        // Soft masks and stencil masks must stay lossless.
        var masks = new HashSet<int>();
        foreach (var num in file.ObjectNumbers.ToList())
        {
            if (file.Get(num) is not PdfStream s || s.Dict.Name("Subtype") != "Image") continue;
            if (s.Dict["SMask"] is PdfRef sm) masks.Add(sm.Num);
            if (s.Dict["Mask"] is PdfRef mk) masks.Add(mk.Num);
        }
        PdfAssembler.Rewrite(file, output, changeStream: (num, s) => Stream(file, num, s, masks), compact: true);
    }

    static PdfStream Stream(PdfFile file, int num, PdfStream stream, HashSet<int> masks)
    {
        var dict = stream.Dict;
        // XMP metadata stays readable by other tools.
        if (dict.Name("Type") == "Metadata") return stream;
        var chain = PdfFilters.Chain(dict, file.Resolve);
        if (dict.Name("Subtype") == "Image" && !masks.Contains(num) && dict["ImageMask"] is not PdfBool { Value: true })
        {
            try
            {
                var smaller = Image(file, stream, chain);
                if (smaller != null) return smaller;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException)
            {
                // A JPEG Windows can't decode stays as it is.
            }
        }
        return Flate(file, stream, chain);
    }

    /// Uncompressed or weakly compressed streams become Flate streams.
    static PdfStream Flate(PdfFile file, PdfStream stream, List<(string Name, PdfDict? Parms)> chain)
    {
        if (chain.Any(f => PdfFilters.ImageFilters.Contains(f.Name))) return stream;
        bool plainFlate = chain.Count == 1 && chain[0].Name is "FlateDecode" or "Fl" && chain[0].Parms == null;
        if (chain.Count > 0 && !plainFlate && chain.Any(f => f.Parms != null && f.Name is "FlateDecode" or "Fl" or "LZWDecode" or "LZW")) return stream;
        if (stream.Data.Length < 64) return stream;
        var decoded = file.Decoded(stream);
        if (decoded == null) return stream;
        var compressed = PdfFilters.Deflate(decoded, CompressionLevel.SmallestSize);
        if (compressed.Length >= stream.Data.Length) return stream;
        var dict = stream.Dict.Clone();
        dict["Filter"] = new PdfName("FlateDecode");
        dict.Remove("DecodeParms");
        dict.Remove("DL");
        return new PdfStream(dict, compressed);
    }

    static int? Components(PdfFile file, PdfObj? colorSpace)
    {
        var cs = file.Resolve(colorSpace);
        switch (cs)
        {
            case PdfName { Value: "DeviceRGB" or "CalRGB" }: return 3;
            case PdfName { Value: "DeviceGray" or "CalGray" }: return 1;
            case PdfArray a when a.Count >= 2 && file.Resolve(a[0]) is PdfName { Value: var kind }:
                if (kind == "ICCBased" && file.Resolve(a[1]) is PdfStream icc && file.Resolve(icc.Dict["N"]) is PdfNum n && n.Int is 1 or 3) return n.Int;
                if (kind == "CalRGB") return 3;
                if (kind == "CalGray") return 1;
                return null;
            default: return null;
        }
    }

    /// A smaller JPEG version of a photo, or null to keep the image as it is.
    static PdfStream? Image(PdfFile file, PdfStream stream, List<(string Name, PdfDict? Parms)> chain)
    {
        var dict = stream.Dict;
        int width = (file.Resolve(dict["Width"]) as PdfNum)?.Int ?? 0, height = (file.Resolve(dict["Height"]) as PdfNum)?.Int ?? 0;
        if (width < 16 || height < 16) return null;
        // Colour-key masks and decode arrays depend on exact sample values.
        if (dict.Has("Decode") || dict["Mask"] is PdfArray) return null;
        int? components = Components(file, dict["ColorSpace"]);
        if (components == null) return null;
        bool dct = chain.Count >= 1 && chain[^1].Name is "DCTDecode" or "DCT" && chain.Take(chain.Count - 1).All(f => !PdfFilters.ImageFilters.Contains(f.Name));
        BitmapSource source;
        if (dct)
        {
            var jpeg = PdfFilters.Decode(stream.Data, chain[..^1]);
            if (jpeg == null) return null;
            var decoder = new JpegBitmapDecoder(new MemoryStream(jpeg), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
            if (source.Format == PixelFormats.Cmyk32) return null;
            int channels = source.Format == PixelFormats.Gray8 ? 1 : 3;
            if (channels != components) return null;
        }
        else
        {
            if (chain.Any(f => PdfFilters.ImageFilters.Contains(f.Name))) return null;
            int bpc = (file.Resolve(dict["BitsPerComponent"]) as PdfNum)?.Int ?? 8;
            if (bpc != 8) return null;
            // Small images (icons, rules) aren't worth a lossy copy.
            if ((long)width * height < 40_000) return null;
            var raw = file.Decoded(stream);
            int stride = width * components.Value;
            if (raw == null || raw.Length < (long)stride * height) return null;
            source = BitmapSource.Create(width, height, 72, 72, components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, raw, stride);
        }

        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        bool scaled = longest > MaxImageSide;
        if (scaled)
        {
            double scale = (double)MaxImageSide / longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (source.Format != PixelFormats.Gray8 && source.Format != PixelFormats.Bgr24)
            source = new FormatConvertedBitmap(source, components == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        var output = new MemoryStream();
        encoder.Save(output);
        var bytes = output.ToArray();
        // A re-encoded JPEG must save a worthwhile amount; a Flate image becomes JPEG only when that halves it
        // (photos), so line art and screenshots stay sharp.
        double threshold = dct ? (scaled ? 1.0 : 0.85) : 0.5;
        if (bytes.Length >= stream.Data.Length * threshold) return null;
        var newDict = dict.Clone();
        newDict["Filter"] = new PdfName("DCTDecode");
        newDict.Remove("DecodeParms");
        newDict.Remove("DL");
        newDict["Width"] = new PdfNum(source.PixelWidth);
        newDict["Height"] = new PdfNum(source.PixelHeight);
        newDict["BitsPerComponent"] = new PdfNum(8);
        return new PdfStream(newDict, bytes);
    }
}
