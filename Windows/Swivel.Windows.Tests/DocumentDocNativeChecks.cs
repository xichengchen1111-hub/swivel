using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class DocumentDocNativeChecks
{
    // macOS `textutil -convert doc` output (gzip, base64): a paragraph, a 3x3 table with CJK, and a paragraph.
    private const string TextutilTable="H4sIAAAAAAACE+2cW0hUQRjH/+fsurte0nUrM7ttJV3oQmVBRIR2sRuUpZD0ZmUpaIoZ5UsIEURPRQ+9BFHUUxBEERFE1lMvUT0kFAQVvXR52IQIgzz9Z86sjqvWbnRR+n7L/5z5ZvbMnDMzhzPfYb998rjw1cXrJa+RwhoE0OdlI2TlOdTMpBEFZpu8Ps/zVFac8oQxxccr982AJmL3/FEu5KA6auAbqHzsIxiBIj0Pqo/6Wo4f43kFP00nadLbS8a6hJ+jvjMB6XOLyqMepBz3hXqPf8dr3lJxd8B+Q02EMNrpNvNJEMYONWhBK8ahDUuxlwqb1LL+VBlTYSzaprZNqMMhtOucZhym3Y5GHt3K/DYqjKNUhy5fy5rb+WnhN8dJR49W+IyPUDFqKjWPWkgtplZRVVQNtdusB3p7Prx92f344f07N6Tv0iZv+5YAdlDrthSPKis3dfznpMyBlSnzoJb6al/aZ9tI2MYH23hgG3dt47ZtDJpUp2zjuG0cs42ODAcjyHVrqVNRaqd4VRMuJ4w1X2dPKnUi3ZV0g1Ce21nUNbNT9ZTjDFK6ecO22Z/62+1l+Rer8vJqrXu72+6lp3LXjg4GRusPzBM36ecJ/yVODAXIobvfyL3LSaU+9OPLP3mu9udD2MYVXBvXcHXaJ3e1t6weHeoxUmUeJc2bsoNDK1+NivIe7yL36k3BetRjP2vxV41xVJk14wG9bUUD8yrZ1kGWprLcvCdQ75n01szaoFnB1Bo7yzeRLA+rM44OqsQvVwduDJ120lsiN/Ly67nwjbMz6nGE+516WVvHkwVWsB43jXqq+aRqxh4eqTqyLM3WK9g9jabzyzI+Y/VSLoglkZPFSDzhSTbo419sbZqjlHypZ7/ic3TvBKLOkNxkTjyARFRunV/kSqgrfBM30YnYkt9fu5qHswZuRY5TRLpcEARBEARBEASfPnrUwZyhXqXKeXXiQk/v9obo1TMRLJh747nyV4pNmVLUOBzvvkk/CoIgCIIgCIIgCMJY9f/dZ4+enV88JXr2HP3/hb3XxP8XBEEQBEEQBEEQhLGJY3z4APwfxSd/QR+G/78O2dznULnwo5tVJGc+VWDKC7mPUePhR9GrSPUiapIpn8x9CTWFmkpNo6ZTM0y50iwr3Sd/yfBXUYEMLToeY4OOvGjLMJyuCFlOsi41h0LZfihEl19cOdwxpVSnSS9FDeqwB02o/6X5m8/Za19POsfon8SbCIZdOrplH9ZzvxeHddjEcBEoI1ECV8VRIZP2YbWfhWrdarOOhunAZra+vz/iRkVXt+ggk5GYx/Zdc++m2350UPupV57Z+axk+5n2f7HVviAIgvBv+A4fAQ09AEwAAA==";
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"doc-text");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"旧文档 🦜.DOC");
        byte[] original;using(var gzip=new GZipStream(new MemoryStream(Convert.FromBase64String(TextutilTable)),CompressionMode.Decompress))using(var copy=new MemoryStream()){gzip.CopyTo(copy);original=copy.ToArray();}
        File.WriteAllBytes(path,original);const string expected="Top\nr1c1\tr1c2\tr1c3\n中\t\tlast\nmulti\npara\tx\ty\nBottom\n";
        using(var file=DocumentTextFile.Open(path,default))
        {
            check(file.Document.Doc&&file.Document.Encoding=="Word 97-2003 main document text"&&file.Document.Text==expected,"Native DOC reads the main document text with table cells and rows: "+file.Document.Text);
            check(file.Statistics==DocumentText.Count(expected,default),"DOC statistics derive only from complete extracted text");
            check(!Directory.EnumerateFileSystemEntries(folder).Any(x=>x!=path),"DOC read extracts nothing to disk");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"DOC source pinned while preview/export open");reject(()=>Directory.Move(folder,folder+" moved"),"DOC ancestors pinned while modal/export open");
            var app=Application.Current as App??new App();var dialog=new DocumentTextDialog(app,file);var root=(DockPanel)dialog.Content;
            check(dialog.StatisticsReport.Contains(app.L(DocumentNotices.Doc),StringComparison.Ordinal)&&dialog.StatisticsReport.Contains(app.L("Word 97-2003 main document text"),StringComparison.Ordinal),"DOC limitations accompany the explicit-copy statistics report");
            var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
            check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Doc))&&!body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Docx)),"DOC notice shown in the native scroll body, without the DOCX notice");
            check(dialog.ExportFormat==TextExport.Utf8,"DOC input keeps the plain TXT default export");dialog.Close();
            foreach(var format in Enum.GetValues<TextExport>())
            {
                string saved=file.Save(format,default);check(Path.GetDirectoryName(saved)==folder&&saved!=path,"DOC source creates exclusive plain copies beside original");
                using var generated=File.OpenRead(saved);var result=DocumentText.Read(generated,Path.GetExtension(saved).TrimStart('.'),default);
                check(result.Text==file.Document.Text+(format is TextExport.Docx or TextExport.Odt?"\n":""),"DOC body export/readback: "+format);
            }
            int count=Directory.GetFiles(folder).Length;reject(()=>file.Save(TextExport.Html,new CancellationToken(true)),"DOC export pre-cancellation");
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Cancelled DOC output creates no publication/staging");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"DOC original bytes remain unchanged");
        check(new McpHost(()=>true,"AgentHistoryDoc-"+Guid.NewGuid().ToString("N")).ReadDocumentText(path,default)==expected,"Agent extract_text reads the same DOC text");
        string encrypted=Path.Combine(folder,"locked.doc");byte[] locked=(byte[])original.Clone();
        int word=Enumerable.Range(512,locked.Length-516).First(i=>locked[i]==0xEC&&locked[i+1]==0xA5&&locked[i+2]==0xC1);locked[word+0x0B]|=0x01;File.WriteAllBytes(encrypted,locked);
        int before=Directory.GetFiles(folder).Length;
        try{DocumentTextFile.Open(encrypted,default).Dispose();check(false,"Encrypted DOC refused before the modal (accepted)");}
        catch(UserError error){check(error.Message==DocumentDoc.Encrypted,"Encrypted DOC refused before the modal: "+error.Message);}
        check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Refused DOC has no output or staging");File.Delete(encrypted);
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"DOC lease released after success/refusal");
    }
}
