using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Swivel.Windows;

public sealed record PdfRasterSource(int Number,string Path,int Page,PdfPageSize Size);
public sealed class PdfRasterDocument:IDisposable
{
    public const string Fidelity="This is a rendered page copy, not a vector-preserving PDF editor. Text becomes pixels; search, links, forms, attachments, layers, accessibility structure, metadata and digital signatures are not preserved. Colors, transparency, annotations and rendering can differ. Pages use a white background. The originals are kept unchanged.";
    private sealed class Source(NativeFiles.Input input,BoundedStream view,IRandomAccessStream stream,PdfDocument pdf):IDisposable
    {public NativeFiles.Input Input=input;public BoundedStream View=view;public IRandomAccessStream Stream=stream;public PdfDocument Pdf=pdf;public void Dispose(){Stream.Dispose();View.Dispose();Input.Dispose();}}
    private readonly List<Source> sources=new();private readonly List<PdfRasterSource> pages=new();private readonly List<(int Source,uint Page)> locations=new();
    public IReadOnlyList<PdfRasterSource> Pages=>pages.AsReadOnly();
    private PdfRasterDocument(){}
    public static async Task<PdfRasterDocument> OpenAsync(IReadOnlyList<string> paths,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();NativeIdentity.Require();if(paths.Count is <1 or >10)throw new UserError(PdfRasterPlan.Limit);var work=new PdfRasterDocument();long total=0;
        try
        {
            foreach(string path in paths)
            {
                token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);BoundedStream? view=null;IRandomAccessStream? stream=null;
                try
                {
                    total+=input.Stream.Length;if(input.Stream.Length>32L*1024*1024||total>128L*1024*1024)throw new UserError(PdfRasterPlan.Limit);
                    view=new(input.Stream,32L*1024*1024,false,token);stream=view.AsRandomAccessStream();
                    var pdf=await NativeAsync.Join(PdfDocument.LoadFromStreamAsync(stream),token).ConfigureAwait(false);
                    if(pdf.IsPasswordProtected)throw new UserError("Password-protected PDFs are not supported by this tool.");
                    if(pdf.PageCount<1||pdf.PageCount>PdfRasterPlan.MaxPages-work.pages.Count)throw new UserError(PdfRasterPlan.Limit);
                    int sourceIndex=work.sources.Count;work.sources.Add(new(input,view,stream,pdf));view=null;stream=null;
                    for(uint p=0;p<pdf.PageCount;p++){token.ThrowIfCancellationRequested();using var page=pdf.GetPage(p);var size=page.Size;work.locations.Add((sourceIndex,p));work.pages.Add(new(work.pages.Count+1,path,(int)p+1,PdfRasterPlan.FromDip(size.Width,size.Height)));}
                }
                catch{stream?.Dispose();view?.Dispose();if(!work.sources.Any(s=>ReferenceEquals(s.Input,input)))input.Dispose();throw;}
            }
            token.ThrowIfCancellationRequested();return work;
        }
        catch{work.Dispose();throw;}
    }
    public PdfRasterPage[] Plan(string selection,int edge,int turn)=>PdfRasterPlan.Build(pages.Select(p=>p.Size).ToArray(),PdfRasterPlan.Parse(selection,pages.Count),edge,turn);
    public async Task<PixelImage> RenderAsync(PdfRasterPage plan,CancellationToken token)
    {
        ValidatePlan(plan);token.ThrowIfCancellationRequested();if(plan.Index<0||plan.Index>=locations.Count)throw new UserError(PdfRasterPlan.Invalid);var loc=locations[plan.Index];var source=sources[loc.Source];
        using var page=source.Pdf.GetPage(loc.Page);using var bytes=new MemoryStream();using var view=new BoundedStream(bytes,80L*1024*1024,true,token);using var output=view.AsRandomAccessStream();
        bool swapped=plan.Turn is 90 or 270;var options=new PdfPageRenderOptions{DestinationWidth=(uint)(swapped?plan.Height:plan.Width),DestinationHeight=(uint)(swapped?plan.Width:plan.Height),BackgroundColor=global::Windows.UI.Color.FromArgb(255,255,255,255),IsIgnoringHighContrast=true};
        await NativeAsync.Join(page.RenderToStreamAsync(output,options),token).ConfigureAwait(false);token.ThrowIfCancellationRequested();bytes.Position=0;
        var bitmap=BitmapFrame.Create(bytes,BitmapCreateOptions.IgnoreImageCache,BitmapCacheOption.OnLoad);bitmap.Freeze();
        if(bitmap.PixelWidth!=options.DestinationWidth||bitmap.PixelHeight!=options.DestinationHeight)throw new UserError(PdfRasterPlan.Invalid);
        return PdfRasterPlan.Rotate(ImageTools.ReadPixels(bitmap,0,token),plan.Turn);
    }
    private void ValidatePlan(PdfRasterPage plan)
    {
        var expected=PdfRasterPlan.Build(pages.Select(x=>x.Size).ToArray(),new[]{plan.Index},Math.Max(plan.Width,plan.Height),plan.Turn)[0];
        if(expected!=plan)throw new UserError(PdfRasterPlan.Invalid);
    }
    public string SavePdf(IReadOnlyList<PdfRasterPage> plan,CancellationToken token)
    {
        foreach(var page in plan)ValidatePlan(page);ImagePdf.Validate(plan.Select(p=>new PdfImageSize(p.Width,p.Height)).ToArray());token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(sources[0].Input,System.IO.Path.GetFileNameWithoutExtension(sources[0].Input.Path)+" rendered pages","pdf",output=>
        {using var bounded=new BoundedStream(output,512L*1024*1024,true,token);PdfRasterExport.Write(bounded,plan,i=>RenderAsync(plan[i],token).GetAwaiter().GetResult(),token);},token);
    }
    public string SavePng(PdfRasterPage plan,CancellationToken token)
    {
        var pixels=RenderAsync(plan,token).GetAwaiter().GetResult();token.ThrowIfCancellationRequested();
        var bitmap=BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4);bitmap.Freeze();
        return NativeFiles.WriteBeside(sources[0].Input,System.IO.Path.GetFileNameWithoutExtension(sources[0].Input.Path)+" page "+(plan.Index+1),"png",output=>
        {using var bounded=new BoundedStream(output,80L*1024*1024,true,token);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(bounded);},token);
    }
    public void Dispose(){foreach(var source in sources)source.Dispose();sources.Clear();}
}
