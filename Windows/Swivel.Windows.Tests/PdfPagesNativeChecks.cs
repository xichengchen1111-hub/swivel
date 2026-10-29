using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Swivel.Core;
using Swivel.Windows;

internal static class PdfPagesNativeChecks
{
    private static readonly string[] Contents={"BT /F1 20 Tf 72 700 Td (Native one) Tj ET","BT /F1 20 Tf 72 700 Td (Native two) Tj ET"};
    // Minimal classic-xref PDF: shared font, inherited MediaBox, one link that targets a page.
    private static byte[] Build()
    {
        var objects=new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Annots [8 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R /Rotate 90 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "STREAM0","STREAM1",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 9 9] /Dest [4 0 R /Fit] >>",
            "<< /Type /Outlines /Count 0 /Title (NativeOutlineMarker) >>",
        };
        var m=new MemoryStream();void W(string s)=>m.Write(Encoding.ASCII.GetBytes(s));var offsets=new List<long>();
        W("%PDF-1.4\n");
        for(int i=0;i<objects.Count;i++)
        {
            offsets.Add(m.Position);string body=objects[i];
            if(body.StartsWith("STREAM",StringComparison.Ordinal)){string c=Contents[body[6]-'0'];body=$"<< /Length {c.Length} >>\nstream\n{c}\nendstream";}
            W($"{i+1} 0 obj\n{body}\nendobj\n");
        }
        long xref=m.Position;W($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");foreach(long o in offsets)W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");return m.ToArray();
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){yield return child;foreach(var d in Children(child))yield return d;}
    }
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"pdf-pages");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"report.pdf");var source=Build();File.WriteAllBytes(path,source);
        string output;
        using(var document=PdfPagesDocument.Open(new[]{path},default))
        {
            check(document.Pages.Count==2&&document.Pages[0].Width==595&&document.Pages[1].Rotate==90,"Native structure reader lists both pages with inherited size and own rotation");
            reject(()=>File.Move(path,path+" moved"),"PDF source remains pinned during selection/save");reject(()=>Directory.Move(folder,folder+" moved"),"PDF ancestor remains pinned");
            var app=Application.Current as App??new App();var dialog=new PdfPagesDialog(app,document);var children=Children(dialog).ToArray();
            check(children.OfType<ListView>().Single().Items.Count==2&&children.OfType<TextBox>().Single().Text=="1-2"&&children.OfType<Button>().Any(b=>Equals(b.Content,app.L("Save PDF"))),"Native dialog lists pages, defaults to all and offers explicit Save");dialog.Close();
            reject(()=>document.Plan("3"),"Out-of-range selection refused");
            int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(document.Plan("1"),0,new CancellationToken(true)),"Canceled save does not publish");check(Directory.GetFiles(folder).Length==count,"Canceled save creates no file");
            output=document.Save(document.Plan("2,1,2"),90,default);
        }
        var bytes=File.ReadAllBytes(output);var copy=PdfStructure.Read(bytes,default);
        check(Path.GetFileName(output)=="report pages.pdf"&&File.ReadAllBytes(path).SequenceEqual(source),"New PDF beside the unchanged original");
        check(copy.Pages.Count==3&&copy.Pages.Select(p=>p.Rotate).SequenceEqual(new[]{180,90,180})&&copy.Pages.All(p=>p.Width==595&&p.Height==842),"Saved order, repeat and rotation read back");
        string text=Encoding.Latin1.GetString(bytes);
        check(text.Contains(Contents[1]+"\nendstream")&&text.Contains(Contents[0]+"\nendstream")&&!text.Contains("NativeOutlineMarker")&&!text.Contains("/Link"),"Content streams copied unchanged; outline and page link dropped");
        string second=Path.Combine(folder,"second.pdf");File.WriteAllBytes(second,source);
        using(var two=PdfPagesDocument.Open(new[]{path,second},default)){check(two.Pages.Count==4,"Two inputs combine their pages");check(Path.GetFileName(two.Save(two.Plan("4,1"),0,default))=="report pages 2.pdf","Second save takes the next free name");}
        string bad=Path.Combine(folder,"bad.pdf");File.WriteAllBytes(bad,source[..^40]);reject(()=>PdfPagesDocument.Open(new[]{bad},default),"Truncated PDF refused");
        reject(()=>PdfPagesDocument.Open(new[]{path},new CancellationToken(true)),"Pre-cancel precedes reading");
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"No failed or canceled PDF stages left behind");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All PDF input and ancestor leases released");
    }
}
