using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Swivel;

/// Renders PDF pages to bitmaps with Windows' built-in PDF renderer (Windows.Data.Pdf), the counterpart of PDFKit's
/// page drawing on the Mac.
static partial class PdfRender
{
    /// Very large pages at high DPI would need gigabytes; the long side is capped (the Mac app fails with "Page too
    /// large" where Core Graphics can't make the bitmap; here the page is rendered smaller instead).
    const double MaxSide = 20000, MaxPixels = 180_000_000;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    /// Opens a PDF for rendering, with the parsed file for page sizes when it can be parsed.
    public static async Task<(WinPdf.PdfDocument Document, PdfFile? Parsed)> Load(string path)
    {
        var name = Path.GetFileName(path);
        PdfFile? parsed = null;
        try { parsed = await Task.Run(() => PdfFile.Open(path)); }
        catch (ToolError ex) when (ex.Message == L("%@ is password protected", name)) { throw; }
        catch (ToolError) { }
        StorageFile file;
        try { file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            throw new ToolError(L("Couldn't open %@", name));
        }
        try { return (await WinPdf.PdfDocument.LoadFromFileAsync(file), parsed); }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 0x8007052B: the file needs a password.
            if (ex.HResult == unchecked((int)0x8007052B)) throw new ToolError(L("%@ is password protected", name));
            throw new ToolError(L("Couldn't open %@", name));
        }
    }

    /// The pixel size of a page at `dpi`: the visible (crop) area, turned by the page's rotation.
    public static (uint Width, uint Height) PixelSize(WinPdf.PdfPage page, PdfFile? parsed, double dpi)
    {
        double w, h;
        if (parsed != null && page.Index < parsed.Pages.Count)
        {
            var p = parsed.Pages[(int)page.Index];
            double pw = p.Visible.X1 - p.Visible.X0, ph = p.Visible.Y1 - p.Visible.Y0;
            bool turned = p.Rotate % 180 != 0;
            // PDF units are points (1/72 inch).
            w = (turned ? ph : pw) * dpi / 72;
            h = (turned ? pw : ph) * dpi / 72;
        }
        else
        {
            // Windows.Data.Pdf reports sizes in DIPs scaled for the system DPI (192 at 200%).
            double scale = 96.0 / Math.Max(96, GetDpiForSystem());
            w = page.Size.Width * scale * dpi / 96;
            h = page.Size.Height * scale * dpi / 96;
        }
        double fit = Math.Min(1, Math.Min(MaxSide / Math.Max(w, h), Math.Sqrt(MaxPixels / Math.Max(1, w * h))));
        return ((uint)Math.Max(1, Math.Round(w * fit)), (uint)Math.Max(1, Math.Round(h * fit)));
    }

    public static async Task<BitmapSource> Render(WinPdf.PdfPage page, PdfFile? parsed, double dpi)
    {
        var (width, height) = PixelSize(page, parsed, dpi);
        // The destination size is taken in DIPs and multiplied by the system scale (2× at 200%), so ask for less.
        double factor = Math.Max(96, GetDpiForSystem()) / 96.0;
        var image = await RenderAt(page, Math.Max(1, (uint)Math.Round(width / factor)), Math.Max(1, (uint)Math.Round(height / factor)));
        if (Math.Abs(image.PixelWidth - (int)width) > 2 || Math.Abs(image.PixelHeight - (int)height) > 2)
        {
            // Some other scale was applied: measure it and render once more.
            double fx = (double)image.PixelWidth / Math.Max(1, Math.Round(width / factor)) ;
            double fy = (double)image.PixelHeight / Math.Max(1, Math.Round(height / factor));
            image = await RenderAt(page, Math.Max(1, (uint)Math.Round(width / factor / fx)), Math.Max(1, (uint)Math.Round(height / factor / fy)));
        }
        if (image.PixelWidth != width || image.PixelHeight != height)
        {
            // Rounding left a pixel or two over: scale to the exact size.
            var exact = new TransformedBitmap(image, new ScaleTransform((double)width / image.PixelWidth, (double)height / image.PixelHeight));
            image = new FormatConvertedBitmap(exact, PixelFormats.Bgr24, null, 0);
        }
        int stride = ((int)width * 3 + 3) / 4 * 4;
        var pixels = new byte[stride * (int)height];
        image.CopyPixels(new System.Windows.Int32Rect(0, 0, Math.Min((int)width, image.PixelWidth), Math.Min((int)height, image.PixelHeight)), pixels, stride, 0);
        // Tag the bitmap with the DPI it was rendered at, so saved files say 300 DPI.
        var result = BitmapSource.Create((int)width, (int)height, dpi, dpi, PixelFormats.Bgr24, null, pixels, stride);
        result.Freeze();
        return result;
    }

    static async Task<BitmapSource> RenderAt(WinPdf.PdfPage page, uint width, uint height)
    {
        var options = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
        };
        using var stream = new InMemoryRandomAccessStream();
        try { await page.RenderToStreamAsync(stream, options); }
        catch (Exception ex) when (ex is COMException or ArgumentException or OutOfMemoryException)
        {
            throw new ToolError(L("Couldn't render page %d", (int)page.Index + 1));
        }
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // The page is opaque (white background): drop the alpha channel, like the Mac app's noneSkipLast bitmap.
        var opaque = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }

    public static async Task<BitmapSource> RenderPage(string path, int index, double dpi)
    {
        var (doc, parsed) = await Load(path);
        if (doc.PageCount == 0) throw new ToolError(L("This PDF has no pages"));
        if (index < 0 || index >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        using var page = doc.GetPage((uint)index);
        return await Render(page, parsed, dpi);
    }

    public static void Save(BitmapSource image, string path, string ext)
    {
        BitmapEncoder encoder = ext == "png" ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        PdfOutput.Save(path, encoder.Save);
    }

    /// Every page at 300 DPI next to the original: "name.png" for one page, "name 1.png", "name 2.png"… for more.
    public static async Task<List<string>> Pages(string path, string ext)
    {
        var (doc, parsed) = await Load(path);
        var outputs = new List<string>();
        int count = (int)doc.PageCount;
        for (int i = 0; i < count; i++)
        {
            using var page = doc.GetPage((uint)i);
            var image = await Render(page, parsed, 300);
            var output = Paths.Output(path, ext, count > 1 ? $" {i + 1}" : "");
            await Task.Run(() => Save(image, output, ext));
            outputs.Add(output);
        }
        return outputs;
    }
}
