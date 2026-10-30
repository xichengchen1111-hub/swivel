using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class DocumentEpubNativeChecks
{
    private static byte[] Book(params (string Name,string Text)[] parts)
    {
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            using(var s=zip.CreateEntry("mimetype",CompressionLevel.NoCompression).Open())s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));
            foreach(var (name,text) in parts){using var s=zip.CreateEntry(name).Open();s.Write(Encoding.UTF8.GetBytes(text));}
        }
        return output.ToArray();
    }
    private const string Container="<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OPS/book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>";
    private static string Opf(string href)=>"<package xmlns='http://www.idpf.org/2007/opf' version='3.0'><manifest><item id='b' href='b.xhtml' media-type='application/xhtml+xml'/><item id='a' href='"+href+"' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='a'/><itemref idref='b'/></spine></package>";
    private static string Page(string body)=>"<?xml version='1.0' encoding='utf-8'?><html xmlns='http://www.w3.org/1999/xhtml'><head><link rel='stylesheet' href='file:///private/never.css'/></head><body>"+body+"</body></html>";
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"epub-text");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"图书 🦜.EPUB");
        byte[] original=Book(("META-INF/container.xml",Container),("OPS/book.opf",Opf("a.xhtml")),("OPS/a.xhtml",Page("<h1>第一章</h1><p>Hello &amp; 世界</p><img src='file:///private/x.png' alt='alt'/>")),("OPS/b.xhtml",Page("<p><a href='https://example.invalid'>link</a> two</p>")));
        File.WriteAllBytes(path,original);const string expected="第一章\nHello & 世界\nlink two";
        using(var file=DocumentTextFile.Open(path,default))
        {
            check(file.Document.Epub&&file.Document.Encoding=="EPUB spine body text"&&file.Document.Text==expected,"NativeEPUB reads spine XHTML in order without resources: "+file.Document.Text);
            check(file.Statistics==DocumentText.Count(expected,default),"EPUB statistics derive only from complete extracted text");
            check(!Directory.EnumerateFileSystemEntries(folder).Any(x=>x!=path),"EPUB read extracts nothing to disk");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"EPUB source pinned while preview/export open");reject(()=>Directory.Move(folder,folder+" moved"),"EPUB ancestors pinned while modal/export open");
            var app=Application.Current as App??new App();var dialog=new DocumentTextDialog(app,file);var root=(DockPanel)dialog.Content;
            check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Epub),StringComparison.Ordinal)&&dialog.StatisticsReport.Contains(app.L("EPUB spine body text"),StringComparison.Ordinal),"EPUB limitations accompany the explicit-copy statistics report");
            var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
            check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Epub))&&!body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Html)),"EPUB notice shown in the native scroll body, without the HTML-file notice");
            check(dialog.ExportFormat==TextExport.Utf8,"EPUB input keeps the plain TXT default export");dialog.Close();
            foreach(var format in Enum.GetValues<TextExport>().Where(x=>x!=TextExport.Pdf))
            {
                string saved=file.Save(format,default);check(Path.GetDirectoryName(saved)==folder&&saved!=path,"EPUB source creates exclusive plain copies beside original");
                using var generated=File.OpenRead(saved);var result=DocumentText.Read(generated,Path.GetExtension(saved).TrimStart('.'),default);
                check(result.Text==file.Document.Text+(format is TextExport.Docx or TextExport.Odt?"\n":""),"EPUB body export/readback: "+format);
            }
            int count=Directory.GetFiles(folder).Length;reject(()=>file.Save(TextExport.Html,new CancellationToken(true)),"EPUB export pre-cancellation");
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Cancelled EPUB output creates no publication/staging");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"EPUB original bytes remain unchanged");
        check(new McpHost(()=>true,"AgentHistoryEpub-"+Guid.NewGuid().ToString("N")).ReadDocumentText(path,default)==expected,"Agent extract_text reads the same EPUB spine text");
        string hostile=Path.Combine(folder,"escape.epub");File.WriteAllBytes(hostile,Book(("META-INF/container.xml",Container),("OPS/book.opf",Opf("../../../outside.xhtml")),("OPS/b.xhtml",Page("<p>b</p>"))));
        File.WriteAllText(Path.Combine(directory,"outside.xhtml"),Page("<p>must not be read</p>"));int before=Directory.GetFiles(folder).Length;
        reject(()=>DocumentTextFile.Open(hostile,default).Dispose(),"EPUB spine path outside the book refused before the modal");
        check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Refused EPUB has no output or staging");File.Delete(hostile);
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"EPUB lease released after success/refusal");
    }
}
