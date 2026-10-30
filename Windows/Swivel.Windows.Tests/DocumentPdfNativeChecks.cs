using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Storage.Streams;

/// PDF (text) with the real Windows fonts: structure, embedded subsets, ToUnicode read-back and,
/// where Windows.Data.Pdf works in this unpackaged process, a rendered page with ink on it.
internal static class DocumentPdfNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        var samples=new (string Name,string Text,string Language)[]
        {
            ("latin","Hello, Swivel PDF!\nTabs:\tone\ttwo\n\n"+string.Join(" ",Enumerable.Repeat("Wrapping words continue until the line is full.",12)),"en-US"),
            ("chinese","中文段落：用于测试换行的中文文本，句号不在行首。"+string.Concat(Enumerable.Repeat("汉字",50))+"\n繁體中文測試","zh-Hans"),
            ("japanese","日本語のひらがなとカタカナ、漢字。","ja"),
            ("korean","한국어 문장입니다.","ko"),
        };
        foreach(var (name,text,language) in samples)
        {
            DocumentPdf plan;
            try{plan=DocumentPdf.Prepare(text,PdfFonts.For(text,language),default);}
            catch(UserError error)when(name!="latin"&&error.Template==DocumentPdf.Missing){Console.WriteLine("SKIP: no installed font covers the "+name+" sample on this runner: "+error.Message);continue;}
            byte[] bytes;using(var stream=new MemoryStream()){plan.Write(stream,default);bytes=stream.ToArray();}
            var objects=DocumentPdfChecks.Validate(bytes,check,"Windows "+name+" PDF");
            string extracted=DocumentPdfChecks.Extract(objects).Replace("\n","").Replace("\f","");
            check(extracted.Replace(" ","")==text.Replace("\n","").Replace("\t","").Replace(" ",""),"Windows "+name+" PDF text reads back through ToUnicode with every character");
            foreach(var file in objects.Values.Where(o=>o.Dictionary.Contains("/Length1",StringComparison.Ordinal)))
            {var face=TrueTypeFace.Parse(file.Stream!);check(face.MayEmbedSubset&&face.GlyphCount<2000,"Windows "+name+" embedded subset parses and is small: "+face.PostScriptName+" "+face.GlyphCount);}
            Console.WriteLine("INFO: "+name+" PDF fonts "+string.Join(", ",plan.FontNames)+", "+bytes.Length+" bytes, "+plan.Pages+" page(s)");
            Render(bytes,name,plan.Pages,check);
        }
        // End to end beside a source file, exclusive and cancellable.
        string folder=Path.Combine(directory,"pdf-text");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"笔记 notes.txt");
        File.WriteAllText(path,"First line\n第二行\tTab",new System.Text.UTF8Encoding(false));byte[] original=File.ReadAllBytes(path);
        using(var file=DocumentTextFile.Open(path,default))
        {
            string first=file.Save(TextExport.Pdf,default),second=file.Save(TextExport.Pdf,default);
            check(Path.GetFileName(first)=="笔记 notes text.pdf"&&Path.GetFileName(second)=="笔记 notes text 2.pdf"&&File.ReadAllBytes(first).AsSpan().StartsWith("%PDF-1.4\n"u8),"PDF text copies are exclusive beside the source");
            check(DocumentPdfChecks.Extract(DocumentPdfChecks.Validate(File.ReadAllBytes(first),check,"Saved PDF")).Replace("\n","")=="First line第二行Tab\f","Saved PDF holds the complete extracted text");
            int count=Directory.GetFiles(folder).Length;
            reject(()=>file.Save(TextExport.Pdf,new CancellationToken(true)),"PDF save pre-cancellation");
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Cancelled PDF save leaves no output or staging");
            var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var dialog=new DocumentTextDialog(app,file);
            try
            {
                var body=(StackPanel)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single().Content;var formats=body.Children.OfType<ComboBox>().Single();
                formats.SelectedIndex=5;check(formats.Items.Count==6&&dialog.ExportFormat==TextExport.Pdf,"Native selector offers PDF (text) as the sixth format");
                check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(DocumentNotices.Pdf)),"PDF layout and font limits are stated in the dialog");
            }
            finally{dialog.Close();}
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"PDF export leaves the source unchanged");
        string hebrew=Path.Combine(folder,"rtl.txt");File.WriteAllText(hebrew,"abc שלום",new System.Text.UTF8Encoding(false));
        using(var file=DocumentTextFile.Open(hebrew,default))
        {
            int count=Directory.GetFiles(folder).Length;
            try{file.Save(TextExport.Pdf,default);check(false,"Right-to-left PDF text refused");}
            catch(UserError error){check(error.Template==PdfTextLayout.ComplexScript&&error.Message.Contains("U+05E9",StringComparison.Ordinal),"Right-to-left PDF text refused with the character");}
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Refused PDF creates no file or staging");
        }
    }

    private static void Render(byte[] bytes,string name,int pages,Action<bool,string> check)
    {
        global::Windows.Data.Pdf.PdfDocument pdf;
        using var memory=new InMemoryRandomAccessStream();
        try
        {
            using(var writer=new DataWriter(memory.GetOutputStreamAt(0))){writer.WriteBytes(bytes);writer.StoreAsync().AsTask().GetAwaiter().GetResult();writer.DetachStream();}
            pdf=global::Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(memory).AsTask().GetAwaiter().GetResult();
        }
        catch(Exception error)when(error is not OutOfMemoryException){Console.WriteLine("SKIP: Windows.Data.Pdf unavailable in this process ("+error.GetType().Name+"): structure checks only for "+name);return;}
        check(pdf.PageCount==pages&&!pdf.IsPasswordProtected,"Windows.Data.Pdf opens the "+name+" PDF with its page count");
        using var page=pdf.GetPage(0);using var image=new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(image).AsTask().GetAwaiter().GetResult();
        image.Seek(0);using var managed=image.AsStreamForRead();
        var frame=BitmapDecoder.Create(managed,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        var bgra=new FormatConvertedBitmap(frame,System.Windows.Media.PixelFormats.Bgra32,null,0);int stride=bgra.PixelWidth*4;var pixels=new byte[stride*bgra.PixelHeight];bgra.CopyPixels(pixels,stride,0);
        int ink=0;for(int i=0;i<pixels.Length;i+=4)if(pixels[i]<128&&pixels[i+1]<128&&pixels[i+2]<128)ink++;
        check(ink>200&&ink<pixels.Length/4/4,"Windows.Data.Pdf renders the "+name+" PDF page with text ink ("+ink+" dark pixels of "+pixels.Length/4+")");
    }
}
