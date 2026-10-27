using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class DocumentHtmlNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"html-text");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"网页 🦜.HTM");
        const string html="<!doctype html><html><head><meta charset='utf-8'><style>p{display:none}</style><title>not body</title></head><body><p>Hello &amp; 世界</p><p hidden id=x>secret</p><p><a href='file:///private/ignored'>link</a></p></body></html>";
        byte[] original=Encoding.UTF8.GetBytes(html);File.WriteAllBytes(path,original);
        using(var file=DocumentTextFile.Open(path,default))
        {
            check(file.Document.Html&&file.Document.Encoding=="HTML source body text"&&file.Document.Text=="Hello & 世界\nlink","NativeHTML uses explicit source subset without CSS rendering or URI access");
            check(file.Statistics==DocumentText.Count("Hello & 世界\nlink",default),"HTML statistics derive only from complete extracted text");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"HTML source pinned while preview/export open");reject(()=>Directory.Move(folder,folder+" moved"),"HTML ancestors pinned while modal/export open");
            var app=Application.Current as App??new App();var dialog=new DocumentTextDialog(app,file);var root=(DockPanel)dialog.Content;
            check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Html),StringComparison.Ordinal)&&dialog.StatisticsReport.Contains(app.L("HTML source body text"),StringComparison.Ordinal),"HTML extraction limitations accompany explicit-copy statistics report");
            var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
            check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Html))&&root.Children.OfType<WrapPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom),"HTML notice shown in native scroll body with fixed actions");
            check(dialog.ExportFormat==TextExport.Utf8,"HTML input retains existing default plainTXT export");dialog.Close();
            foreach(var format in Enum.GetValues<TextExport>())
            {
                string saved=file.Save(format,default);check(Path.GetDirectoryName(saved)==folder&&saved!=path,"HTML source creates exclusive plain copies beside original");
                using var generated=File.OpenRead(saved);var result=DocumentText.Read(generated,Path.GetExtension(saved).TrimStart('.'),default);
                string expected=file.Document.Text+(format is TextExport.Docx or TextExport.Odt?"\n":"");
                check(result.Text==expected,"HTML body export/readback uses documented plain-format newline rules: "+format);
            }
            string first=file.Save(TextExport.Utf8,default),second=file.Save(TextExport.Utf8,default);
            check(first!=second&&File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"HTML plaincopy collisions do not replace existing output");
            int count=Directory.GetFiles(folder).Length;reject(()=>file.Save(TextExport.Html,new CancellationToken(true)),"HTMLsource export precancellation");
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"CancelledHTML output creates no publication/staging");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"HTML original bytes remain unchanged");
        string bad=Path.Combine(folder,"script.html");File.WriteAllText(bad,"<html><body><script src='file:///private/x'></script>x</body></html>");int before=Directory.GetFiles(folder).Length;
        reject(()=>DocumentTextFile.Open(bad,default).Dispose(),"Script HTML refused before modal without loading resource");check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"RefusedHTML has no output or staging");File.Delete(bad);
        string large=Path.Combine(folder,"long.html");File.WriteAllText(large,"<html><body><pre>"+new string('a',65535)+"😀end</pre></body></html>");
        using(var file=DocumentTextFile.Open(large,default))
        {check(DocumentTextDialog.Preview(file.Document.Text).Length==65535,"HTML readonly preview never splits emoji at65536units");string saved=file.Save(TextExport.Utf8,default);check(File.ReadAllText(saved)==file.Document.Text,"HTML full body exports independently of bounded preview");}
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"HTML lease released after success/refusal");
    }
}
