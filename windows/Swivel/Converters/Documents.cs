using System.Windows;
using System.Windows.Media.Imaging;

namespace Swivel;

// Port of the Docs, PDFs and Info parts of Sources/Swivel/Documents.swift. The Mac app reads and writes documents
// with Cocoa's text system (NSAttributedString) and PDFs with PDFKit; Windows has neither, so this module brings its
// own engine: DocModel (rich text) with readers/writers per format, FlowDocument for layout and RTF, and a small PDF
// reader/writer (PdfFile, PdfOutput). Page images come from Windows.Data.Pdf.

sealed record DocFormat(string Name, string Ext);

static class Docs
{
    /// What the Document menus accept. The Mac app also reads .rtfd and .webarchive, which are macOS-only formats.
    public static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "docx", "doc", "rtf", "odt", "txt", "text", "html", "htm", "md", "markdown" };

    /// Output formats, in the Mac app's order. DOC (legacy binary Word) is left out: writing it needs Word itself
    /// (the Mac app uses Cocoa's built-in .doc writer, which has no Windows counterpart). Reading .doc works.
    public static List<DocFormat> Formats =>
    [
        new("DOCX", "docx"),
        new("PDF", "pdf"),
        new("RTF", "rtf"),
        new("TXT", "txt"),
        new("HTML", "html"),
        new("ODT", "odt"),
    ];

    public static async Task<List<string>> Convert(string path, DocFormat format)
    {
        var model = await Read(path);
        var output = Paths.Output(path, format.Ext);
        await Write(model, output, format);
        return [output];
    }

    /// Throws `Info` with "name: N words, M characters".
    public static async Task<List<string>> WordCount(string path)
    {
        var model = await Read(path);
        var (words, characters) = DocModel.Count(model.PlainText(markers: false));
        throw new Info(L("%@: %d words, %d characters", Path.GetFileName(path), words, characters));
    }

    public static Task<DocModel> Read(string path)
    {
        var ext = FileKinds.Ext(path);
        var name = Path.GetFileName(path);
        byte[] Bytes()
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ToolError(L("Couldn't open %@", name));
            }
        }
        // RTF goes through WPF's converter, which needs an STA thread; everything else is plain code.
        if (ext == "rtf") return DocSta.Run(() => DocFlow.ReadRtf(Bytes()));
        return Task.Run(() => ext switch
        {
            "docx" => DocReaders.ReadDocx(path),
            "odt" => DocReaders.ReadOdt(path),
            "doc" => DocLegacy.Read(Bytes(), name),
            "html" or "htm" => DocReaders.ReadHtml(Bytes()),
            "md" or "markdown" => DocReaders.ReadMarkdown(DocEncoding.Decode(Bytes())),
            _ => DocReaders.ReadText(DocEncoding.Decode(Bytes())),
        });
    }

    /// Writes atomically (temp file, then move).
    public static Task Write(DocModel model, string output, DocFormat format)
    {
        var title = Path.GetFileNameWithoutExtension(output);
        switch (format.Ext)
        {
            case "pdf":
                return DocSta.Run(() => PdfOutput.Save(output, s => PdfFromDoc.Write(model, s, title)));
            case "rtf":
                return DocSta.Run(() => PdfOutput.Save(output, s => DocFlow.WriteRtf(model, s)));
        }
        return Task.Run(() => PdfOutput.Save(output, s =>
        {
            switch (format.Ext)
            {
                case "docx": DocWriters.WriteDocx(model, s, title); break;
                case "odt": DocWriters.WriteOdt(model, s, title); break;
                case "html": DocWriters.WriteHtml(model, s, title); break;
                default: DocWriters.WriteText(model, s); break;
            }
        }));
    }

    public static DocFormat Format(string ext) => Formats.First(f => f.Ext == ext);
}

static class Pdfs
{
    public static List<FileOption> ConvertItems =>
    [
        new("PNG", path => PdfRender.Pages(path, "png")),
        new("JPG", path => PdfRender.Pages(path, "jpg")),
        new("TXT", path => Text(path, Docs.Format("txt"))),
        new("DOCX", path => Text(path, Docs.Format("docx"))),
        new("RTF", path => Text(path, Docs.Format("rtf"))),
        new("HTML", path => Text(path, Docs.Format("html"))),
    ];

    public static List<FileOption> ToolItems =>
    [
        new("Compress", Compress),
        new("Split Pages", Split),
        new("Rotate", Rotate),
        new("Page Count", path =>
        {
            int count = PageCount(path);
            var name = Path.GetFileName(path);
            throw new Info(count == 1 ? L("%@: 1 page", name) : L("%@: %d pages", name, count));
        }),
    ];

    /// Opens a PDF; throws ToolError for unreadable or password-protected files.
    public static PdfFile Open(string path) => PdfFile.Open(path);

    /// Joins the PDFs into "Merged.pdf" next to the first one.
    public static Task<string> Merge(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        var files = paths.Select(Open).ToList();
        var output = Paths.Unique(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? "", "Merged.pdf"));
        PdfOutput.Save(output, s => PdfAssembler.Pages(files.Select(f => (f, (IReadOnlyList<PdfPage>)f.Pages)).ToList(), s));
        return output;
    });

    /// Throws ToolError for unreadable or password-protected files.
    public static int PageCount(string path) => Open(path).Pages.Count;

    /// Renders one page (0-based) on white at the given DPI (Windows.Data.Pdf).
    public static Task<BitmapSource> RenderPage(string path, int index, double dpi) => PdfRender.RenderPage(path, index, dpi);

    /// Writes a copy whose every page's crop box is cut to `fraction` of that page's visible box (0…1 units, origin
    /// top-left, as drawn on screen), like the Mac app's PDF crop. Returns the output path.
    public static string CropPages(string path, Rect fraction, string outputPath)
    {
        var file = Open(path);
        PdfOutput.Save(outputPath, s => PdfAssembler.Rewrite(file, s, (page, dict) =>
        {
            var box = PageRect(fraction, page.Rotate, page.Visible);
            var array = new PdfArray(box.X0, box.Y0, box.X1, box.Y1);
            // As on the Mac, both boxes are set, so every viewer shows the cropped page.
            dict["CropBox"] = array;
            dict["MediaBox"] = new PdfArray(box.X0, box.Y0, box.X1, box.Y1);
            foreach (var key in new[] { "TrimBox", "BleedBox", "ArtBox" }) dict.Remove(key);
        }));
        return outputPath;
    }

    /// A crop given as a fraction of the page as displayed (top-left origin, after the page's clockwise rotation),
    /// in the page's own coordinates (bottom-left origin, unrotated). Same mapping as CropEditor.pageRect on the Mac.
    public static (double X0, double Y0, double X1, double Y1) PageRect(Rect unit, int rotation, (double X0, double Y0, double X1, double Y1) box)
    {
        double minX = Math.Clamp(unit.X, 0, 1), minY = Math.Clamp(unit.Y, 0, 1);
        double maxX = Math.Clamp(unit.X + unit.Width, minX, 1), maxY = Math.Clamp(unit.Y + unit.Height, minY, 1);
        double s0, s1, t0, t1;
        switch (((rotation % 360) + 360) % 360)
        {
            case 90: (s0, s1, t0, t1) = (minY, maxY, 1 - maxX, 1 - minX); break;
            case 180: (s0, s1, t0, t1) = (1 - maxX, 1 - minX, 1 - maxY, 1 - minY); break;
            case 270: (s0, s1, t0, t1) = (1 - maxY, 1 - minY, minX, maxX); break;
            default: (s0, s1, t0, t1) = (minX, maxX, minY, maxY); break;
        }
        double w = box.X1 - box.X0, h = box.Y1 - box.Y0;
        return (box.X0 + s0 * w, box.Y1 - t1 * h, box.X0 + s1 * w, box.Y1 - t0 * h);
    }

    /// PDF → TXT/DOCX/RTF/HTML: the text of every page in reading order.
    static async Task<List<string>> Text(string path, DocFormat format)
    {
        var model = await Task.Run(() => new PdfTextExtractor(Open(path)).Extract());
        if (!model.HasText) throw new ToolError(L("No text found in this PDF"));
        var output = Paths.Output(path, format.Ext);
        await Docs.Write(model, output, format);
        return [output];
    }

    static Task<List<string>> Compress(string path) => Task.Run(() =>
    {
        var file = Open(path);
        var output = Paths.Output(path, "pdf", " compressed");
        var buffer = new MemoryStream();
        PdfCompress.Write(file, buffer);
        // Never hand back a bigger file (or one that saves less than 1%, which isn't worth a second copy).
        if (buffer.Length >= file.Data.Length * 0.99) throw new Info(L("%@ is already as small as it can be", Path.GetFileName(path)));
        PdfOutput.Save(output, s => buffer.WriteTo(s));
        return new List<string> { output };
    });

    /// One PDF per page, next to the original: "name 1.pdf", "name 2.pdf"…
    static Task<List<string>> Split(string path) => Task.Run(() =>
    {
        var file = Open(path);
        var outputs = new List<string>();
        for (int i = 0; i < file.Pages.Count; i++)
        {
            var output = Paths.Output(path, "pdf", $" {i + 1}");
            var page = file.Pages[i];
            PdfOutput.Save(output, s => PdfAssembler.Pages([(file, [page])], s));
            outputs.Add(output);
        }
        return outputs;
    });

    /// Turns every page 90° clockwise.
    static Task<List<string>> Rotate(string path) => Task.Run(() =>
    {
        var file = Open(path);
        var output = Paths.Output(path, "pdf", " rotated");
        PdfOutput.Save(output, s => PdfAssembler.Rewrite(file, s, (page, dict) => dict["Rotate"] = new PdfNum((page.Rotate + 90) % 360)));
        return new List<string> { output };
    });
}
