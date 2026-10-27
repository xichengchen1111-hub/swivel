using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Swivel.Core;

internal static class PdfChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.PdfPages),"PDF feature defaults off"); prefs.Set(Feature.PdfPages,true); prefs.Set(Feature.Wheel,false);
        check(Catalog.Available(prefs,new[] { "a.png","b.jpg" }).Any(x => x.Id == "pdf.images"),"Image PDF independent of Wheel");
        check(!Catalog.Available(prefs,new[] { "a.pdf" }).Any(x => x.Id == "pdf.images"),"Image PDF cannot parse input PDFs");
        var fit = new ImagePdfSettings(PdfPaper.Image,0).Layout(400,200); check(fit.PageWidth == 300 && fit.PageHeight == 150 && fit.X == 0 && fit.Y == 0,"Image page at exactly 96 DPI");
        foreach (var paper in Enum.GetValues<PdfPaper>()) foreach (var size in new[] { new PdfImageSize(400,200),new PdfImageSize(200,400),new PdfImageSize(1,16384) })
        {
            var p = new ImagePdfSettings(paper,36).Layout(size.Width,size.Height);
            check(p.X >= 36-1e-9 && p.Y >= 36-1e-9 && p.X+p.Width <= p.PageWidth-36+1e-9 && p.Y+p.Height <= p.PageHeight-36+1e-9,"PDF content stays within margins");
            check(Math.Abs(p.Width/p.Height-(double)size.Width/size.Height) < 1e-9,"PDF preserves image aspect");
        }
        reject(() => new ImagePdfSettings(Margin:-1).Layout(100,100),"Negative PDF margin refused");
        reject(() => ImagePdf.Validate(Array.Empty<PdfImageSize>()),"Empty PDF refused");
        reject(() => ImagePdf.Validate(Enumerable.Repeat(new PdfImageSize(1,1),51).ToArray()),"PDF page count budget");
        reject(() => ImagePdf.Validate(Enumerable.Repeat(new PdfImageSize(4000,4000),7).ToArray()),"PDF aggregate pixel budget");
        var first = new PixelImage(2,2,new byte[] { 0,0,255,255, 0,255,0,255, 255,0,0,255, 0,0,0,0 });
        var second = new PixelImage(1,2,new byte[] { 0,0,0,128, 255,255,255,255 });
        var pages = new[] { new PdfImageSize(2,2),new PdfImageSize(1,2) };
        using var output = new MemoryStream();
        var previousCulture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); ImagePdf.Write(output,pages,new(PdfPaper.Image,0),i => i == 0 ? first : second,CancellationToken.None); }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        byte[] bytes = output.ToArray(); string text = Encoding.Latin1.GetString(bytes);
        check(text.StartsWith("%PDF-1.4\n") && text.Contains("/Count 2 /Kids [3 0 R 7 0 R]") && text.Contains("/MediaBox [0 0 1.5 1.5]"),"PDF page tree and invariant numeric syntax");
        int xref = int.Parse(Regex.Match(text,@"startxref\n([0-9]+)\n").Groups[1].Value,CultureInfo.InvariantCulture);
        check(text[xref..].StartsWith("xref\n0 11\n"),"PDF trailer points to exact xref byte offset");
        var entries = text[xref..].Split('\n').Skip(2).Take(11).ToArray();
        for (int i = 1; i < entries.Length; i++) { int offset = int.Parse(entries[i][..10],CultureInfo.InvariantCulture); check(entries[i].Length == 19 && text[offset..].StartsWith($"{i} 0 obj\n"),"PDF xref entry targets exact object"); }
        var expected = new[] { new byte[] { 255,0,0,0,255,0,0,0,255,255,255,255 },new byte[] { 127,127,127,255,255,255 } };
        for (int i = 0; i < 2; i++)
        {
            int obj = 4+4*i, lenObj = obj+2, pos = text.IndexOf($"{obj} 0 obj\n",StringComparison.Ordinal);
            int start = text.IndexOf("stream\n",pos,StringComparison.Ordinal)+7;
            int length = int.Parse(Regex.Match(text,$@"(?:\n){lenObj} 0 obj\n([0-9]+)\n").Groups[1].Value,CultureInfo.InvariantCulture);
            using var compressed = new MemoryStream(bytes,start,length); using var zip = new ZLibStream(compressed,CompressionMode.Decompress); using var decoded = new MemoryStream(); zip.CopyTo(decoded);
            check(decoded.ToArray().SequenceEqual(expected[i]),"PDF samples retain RGB orientation and flatten alpha to white");
        }
        check(!text.Contains("/JavaScript") && !text.Contains("/EmbeddedFiles") && !text.Contains("/URI") && !text.Contains("/Metadata"),"PDF has no active content or source metadata");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); using var empty = new MemoryStream();
        reject(() => ImagePdf.Write(empty,pages,new(),_ => first,cancelled.Token),"PDF cancellation before content"); check(empty.Length == 0,"Precancelled PDF writes no bytes");
        using var wrong = new MemoryStream(); reject(() => ImagePdf.Write(wrong,pages,new(),_ => second,CancellationToken.None),"Mismatched callback image refused");
        string? fixtureDirectory = Environment.GetEnvironmentVariable("SWIVEL_PDF_FIXTURES");
        if (fixtureDirectory is not null)
        {
            Directory.CreateDirectory(fixtureDirectory);
            using var fixture = File.Create(Path.Combine(fixtureDirectory,"rgb-alpha.pdf"));
            ImagePdf.Write(fixture,pages,new(PdfPaper.Letter,36),i => i == 0 ? first : second,CancellationToken.None);
        }
    }
}
