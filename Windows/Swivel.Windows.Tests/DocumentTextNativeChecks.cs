using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Swivel.Core;
using Swivel.Windows;

internal static class DocumentTextNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string dataPath=Path.Combine(directory,"preview data.json");
        string dataText="{\"text\":\""+new string('a',65526)+"😀"+new string('b',30)+"\"}";
        File.WriteAllText(dataPath,dataText,new UTF8Encoding(false));
        byte[] dataOriginal=File.ReadAllBytes(dataPath);int beforePreview=Directory.GetFiles(directory).Length;
        using(var preview=FileTextPreview.Open(dataPath,default))
        {
            check(preview.Text==dataText,"Strict data preview preserves validated source");
            reject(()=>File.WriteAllText(dataPath,"{}"),"Data preview source remains pinned");
            reject(()=>Directory.Move(directory,directory+" moved"),"Data preview ancestors remain pinned");
            var previewApp=Application.Current as App??new App();previewApp.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var window=new FileTextPreviewDialog(previewApp,preview);
            try
            {
                var root=(DockPanel)window.Content;
                check(root.Children.OfType<Button>().Count()==1&&root.Children.OfType<Button>().Single().IsCancel,"Data preview has only Close and no export/play action");
                var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
                var text=body.Children.OfType<TextBox>().Last();
                check(body.Children.OfType<TextBox>().All(x=>x.IsReadOnly)&&text.Text==DocumentTextDialog.Preview(dataText)&&text.Text.Length<=65536&&!char.IsHighSurrogate(text.Text[^1]),"Data display uses existing surrogate-safe 65536 bound");
            }
            finally{window.Close();}
        }
        check(File.ReadAllBytes(dataPath).SequenceEqual(dataOriginal)&&Directory.GetFiles(directory).Length==beforePreview,"Data preview creates no files and leaves original bytes unchanged");
        File.Move(dataPath,dataPath+" released");
        reject(()=>FileTextPreview.Open(dataPath,new CancellationToken(true)),"Data preview pre-cancel precedes file access");
        string path=Path.Combine(directory,"document source.txt");byte[] source=new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("中文 👩‍👩‍👧‍👦 {\\object}\r\nlast")).ToArray();File.WriteAllBytes(path,source);
        using(var file=DocumentTextFile.Open(path,default))
        {
            check(file.Document.Text.StartsWith("中文")&&file.Statistics.Words==4,"Native held UTF8 input is decoded and counted");
            reject(()=>File.Move(path,path+" moved"),"Document source remains pinned throughout preview and export");
            reject(()=>Directory.Move(directory,directory+" moved"),"Document ancestors remain pinned");
            string output=file.Save(TextExport.Utf8,default),second=file.Save(TextExport.Utf8,default),rtf=file.Save(TextExport.Rtf,default);
            check(output!=second&&File.ReadAllBytes(output).SequenceEqual(Encoding.UTF8.GetBytes(file.Document.Text))&&File.ReadAllBytes(second).SequenceEqual(File.ReadAllBytes(output)),"Document UTF8 copies are exclusive and contain complete decoded text");
            check(Encoding.ASCII.GetString(File.ReadAllBytes(rtf)).Contains(@"\{\\object\}"),"Native RTF export cannot interpret source controls");
            string docx=file.Save(TextExport.Docx,default),html=file.Save(TextExport.Html,default);
            using(var generated=File.OpenRead(docx))check(DocumentText.Read(generated,"docx",default).Text==file.Document.Text.Replace("\r\n","\n")+"\n","Native DOCX output uses full extracted text with explicit normalized/final paragraph break");
            string htmlPage=File.ReadAllText(html);string htmlText=htmlPage[(htmlPage.IndexOf("<span>",StringComparison.Ordinal)+6)..htmlPage.IndexOf("</span>",StringComparison.Ordinal)];
            check(Path.GetExtension(docx)==".docx"&&Path.GetExtension(html)==".html"&&System.Net.WebUtility.HtmlDecode(htmlText)==file.Document.Text.Replace("\r\n","\n"),"Native new formats use correct beside-source extensions and contain the complete static HTML text");
            int before=Directory.GetFiles(directory).Length;reject(()=>file.Save(TextExport.Rtf,new CancellationToken(true)),"Document precancel publishes nothing");
            check(Directory.GetFiles(directory).Length==before,"Cancelled document export has no output or staging");
            foreach(var format in new[]{TextExport.Docx,TextExport.Html})reject(()=>file.Save(format,new CancellationToken(true)),"New native format precancel publishes nothing");
            check(Directory.GetFiles(directory).Length==before,"Cancelled new exports have no output or staging");
            var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var dialog=new DocumentTextDialog(app,file);
            try
            {
                var root=(DockPanel)dialog.Content;var footer=root.Children.OfType<WrapPanel>().Single();
                check(DockPanel.GetDock(footer)==Dock.Bottom&&footer.Children.OfType<Button>().Count()==3,"Document native fixed footer contains only explicit Copy/Save/Close");
                check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Counting))&&dialog.StatisticsReport.Contains(path),"Explicit statistics copy includes source scope and counting rules");
                check(dialog.ExportFormat==TextExport.Utf8,"Native text export defaults to UTF8 without BOM");
                var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
                var formats=body.Children.OfType<ComboBox>().Single();
                check(formats.Items.Count==6,"Native text selector offers all six explicit copy formats");
                formats.SelectedIndex=2;check(dialog.ExportFormat==TextExport.Docx,"Native DOCX selection maps to package export");
                formats.SelectedIndex=3;check(dialog.ExportFormat==TextExport.Html,"Native HTML selection maps to static text export");
                formats.SelectedIndex=4;check(dialog.ExportFormat==TextExport.Odt,"Native ODT selection maps to minimal package export");
            }
            finally{dialog.Close();}
            var previewOnly=new DocumentTextDialog(app,file,previewOnly:true);
            try
            {
                var root=(DockPanel)previewOnly.Content;var footer=root.Children.OfType<WrapPanel>().Single();var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
                check(footer.Children.OfType<Button>().Count()==2&&!body.Children.OfType<ComboBox>().Any(),"Read-only document preview removes Save and the entire export selector");
                check(body.Children.OfType<TextBox>().All(x=>x.IsReadOnly)&&body.Children.OfType<TextBox>().Any(x=>x.Text==DocumentTextDialog.Preview(file.Document.Text)),"Document preview is bounded read-only extracted text");
                check(previewOnly.StatisticsReport==dialog.StatisticsReport,"Read-only preview retains complete counting report and source/format scope");
            }
            finally{previewOnly.Close();}
        }
        check(File.ReadAllBytes(path).SequenceEqual(source),"Document inspection/export never changes original bytes");
        File.Move(path,path+" released");check(File.Exists(path+" released"),"Document input/ancestor handles release at session end");
        reject(()=>DocumentTextFile.Open(path,new CancellationToken(true)),"Document pre-cancellation occurs before native path access");
        string odtPath=Path.Combine(directory,"ODT source.odt");
        using(var stream=File.Create(odtPath))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
        using(var writer=new StreamWriter(zip.CreateEntry("content.xml").Open(),new UTF8Encoding(false)))
            writer.Write("<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><office:body><office:text><text:p>  中文 <text:span> ODT </text:span><text:s text:c='2'/>end </text:p></office:text></office:body></office:document-content>");
        byte[] odtOriginal=File.ReadAllBytes(odtPath);
        using(var file=DocumentTextFile.Open(odtPath,default))
        {
            check(file.Document.Odt&&!file.Document.Docx&&file.Document.Text=="中文 ODT   end\n","Native ODT input uses normalized ordinary and preserved explicit spaces");
            reject(()=>File.Move(odtPath,odtPath+".moved"),"ODT source pinned through inspection and copy export");
            foreach(var format in Enum.GetValues<TextExport>().Where(x=>x!=TextExport.Pdf))
            {
                string output=file.Save(format,default);check(File.Exists(output),"ODT extracted text supports every existing explicit export format");
                if(format==TextExport.Utf8)check(File.ReadAllText(output)==file.Document.Text,"ODT TXT export uses full bounded body text");
                if(format==TextExport.Odt)using(var exported=File.OpenRead(output))check(DocumentText.Read(exported,"odt",default).Text==file.Document.Text+"\n","Native minimal ODT export holds source and uses complete extracted text");
            }
            var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var dialog=new DocumentTextDialog(app,file);
            try{check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Odt)),"Copied ODT statistics includes every extraction limitation");}finally{dialog.Close();}
            int before=Directory.GetFiles(directory).Length;reject(()=>file.Save(TextExport.Docx,new CancellationToken(true)),"ODT export precancel publishes no file");
            check(Directory.GetFiles(directory).Length==before,"ODT cancellation leaves no staging or output");
        }
        check(File.ReadAllBytes(odtPath).SequenceEqual(odtOriginal),"ODT inspection and copies preserve all original package bytes");
        File.Move(odtPath,odtPath+" released");check(File.Exists(odtPath+" released"),"ODT lease releases after full session");
        string rtfPath=Path.Combine(directory,"RTF source.rtf");
        byte[] rtfOriginal=Encoding.ASCII.GetBytes(@"{\rtf1\ansi\uc1 Chinese \u20013? \u-10179?\u-8704?\par end}");File.WriteAllBytes(rtfPath,rtfOriginal);
        using(var file=DocumentTextFile.Open(rtfPath,default))
        {
            check(file.Document.Rtf&&file.Document.Text=="Chinese 中 😀\nend","Native RTF byte lexer is used without an Office renderer");
            reject(()=>File.Move(rtfPath,rtfPath+" moved"),"RTF source pinned through preview and exports");
            reject(()=>Directory.Move(directory,directory+" moved"),"RTF ancestors remain pinned");
            foreach(var format in Enum.GetValues<TextExport>().Where(x=>x!=TextExport.Pdf))
            {
                string output=file.Save(format,default);check(File.Exists(output),"RTF body can use every existing explicit copy format");
                if(format==TextExport.Utf8)check(File.ReadAllText(output)==file.Document.Text,"RTF complete text preserved in UTF8 copy");
                if(format==TextExport.Rtf)using(var stream=File.OpenRead(output))check(DocumentText.Read(stream,"rtf",default).Text==file.Document.Text,"RTF writer/readback preserves extracted body");
            }
            var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var dialog=new DocumentTextDialog(app,file);
            try{check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Rtf)),"RTF copied statistics includes all extraction limitations");}finally{dialog.Close();}
            int before=Directory.GetFiles(directory).Length;reject(()=>file.Save(TextExport.Odt,new CancellationToken(true)),"RTF session precancel publishes nothing");
            check(Directory.GetFiles(directory).Length==before,"RTF cancelled export leaves no output/staging");
        }
        check(File.ReadAllBytes(rtfPath).SequenceEqual(rtfOriginal),"RTF copies preserve source bytes");
        File.Move(rtfPath,rtfPath+" released");check(File.Exists(rtfPath+" released"),"RTF full-session lease releases");
        string prefix=new string('a',DocumentTextDialog.PreviewLimit-1)+"😀tail";
        check(DocumentTextDialog.Preview(prefix).Length==DocumentTextDialog.PreviewLimit-1&&!char.IsHighSurrogate(DocumentTextDialog.Preview(prefix)[^1]),"Preview truncation never splits a surrogate pair");
        check(DocumentTextDialog.Preview(new string('a',100_000)).Length==DocumentTextDialog.PreviewLimit,"Large preview is bounded independently of complete export");
    }
}
