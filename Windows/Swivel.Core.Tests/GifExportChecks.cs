using System;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class GifExportChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Render(PixelImage source)
        {var original=(byte[])source.Bgra.Clone();using var output=new MemoryStream();GifExport.Write(source,output,default);check(source.Bgra.SequenceEqual(original)&&output.CanWrite,"GIFexport source unchanged and output callerowned");return output.ToArray();}
        var random=new Random(3801);
        foreach(int colors in new[]{1,2,3,4,7,8,15,16,31,32,63,64,127,128,255,256})foreach(bool alpha in new[]{false,true})foreach(int count in new[]{1,17,255,4095,4096,4097,20000})
        {
            int width=Math.Min(100,count),height=(count+width-1)/width;var bytes=new byte[width*height*4];
            for(int i=0;i<width*height;i++){int color=i<colors?i:random.Next(colors);bytes[i*4]=(byte)color;bytes[i*4+1]=(byte)(color*37);bytes[i*4+2]=(byte)(color*83);bytes[i*4+3]=(byte)(alpha&&color==0?0:255);}
            var image=new PixelImage(width,height,bytes);byte[] encoded=Render(image);using var input=new MemoryStream(encoded);var decoded=GifImage.Read(input,default);
            check(decoded.Width==width&&decoded.Height==height&&encoded.AsSpan(0,6).SequenceEqual("GIF89a"u8),"GIFexport dimensions/version fitfullcanvas");
            check(Enumerable.Range(0,width*height).All(i=>bytes[i*4+3]==0?decoded.Bgra.AsSpan(i*4,4).SequenceEqual(new byte[4]):decoded.Bgra.AsSpan(i*4,4).SequenceEqual(bytes.AsSpan(i*4,4))),"GIFexport exact allvisibleRGB/binaryalpha acrossLZW widths/resets/subblocks");
            check(Render(image).SequenceEqual(encoded),"GIFexport deterministic firstoccurrencepalette andLZW");
        }
        foreach(int alpha in new[]{1,127,254})
        {using var output=new MemoryStream();reject(()=>GifExport.Write(new(1,1,new byte[]{10,20,30,(byte)alpha}),output,default),"GIFexport partialalpha refused");check(output.Length==0,"Partialalpha refuses beforeheader");}
        var excess=new byte[257*4];for(int i=0;i<257;i++){excess[i*4]=(byte)i;excess[i*4+1]=(byte)(i>>8);excess[i*4+3]=255;}
        using(var output=new MemoryStream()){reject(()=>GifExport.Write(new(257,1,excess),output,default),"GIFexport257opaque colors refused");check(output.Length==0,"Paletteoverflow writesnoheader");}
        excess[256*4+3]=0;using(var output=new MemoryStream()){reject(()=>GifExport.Write(new(257,1,excess),output,default),"GIFexport256opaque plusone transparent needs257entries");check(output.Length==0,"Transparentpalette budget refusesbeforeheader");}
        var hidden=new PixelImage(2,1,new byte[]{0,0,0,255,99,88,77,0});using(var input=new MemoryStream(Render(hidden)))check(GifImage.Read(input,default).Bgra.SequenceEqual(new byte[]{0,0,0,255,0,0,0,0}),"Opaque black and transparent paletteentries remain distinct");
        var uniform=new PixelImage(4000,4000,new byte[64_000_000]);using(var input=new MemoryStream(Render(uniform)))check(GifImage.Read(input,default).Bgra.All(b=>b==0),"GIFexport exact16MP alltransparent boundedlongphrase path");
        foreach(var shape in new[]{(0,1),(16385,1),(4000,4001)})using(var output=new MemoryStream()){reject(()=>GifExport.Write(new(shape.Item1,shape.Item2,Array.Empty<byte>()),output,default),"GIFexport invalid/overbudget shape");check(output.Length==0,"Invaliddimension writesnothing");}
        using(var output=new MemoryStream()){reject(()=>GifExport.Write(new(1,1,new byte[3]),output,default),"GIFexportshortpixelbuffer");check(output.Length==0,"Shortpixelbuffer writesnothing");}
        using(var output=new MemoryStream()){reject(()=>GifExport.Write(hidden,output,new CancellationToken(true)),"GIFexportprecancel");check(output.Length==0,"GIFprecancel writesnothing");}
        using(var cts=new CancellationTokenSource())using(var output=new CancelOutput(cts))reject(()=>GifExport.Write(new(2000,1,Enumerable.Range(0,2000).SelectMany(i=>new[]{(byte)i,(byte)0,(byte)0,(byte)255}).ToArray()),output,cts.Token),"GIFexportmid-encode cancellation");
        using(var output=new BrokenOutput())reject(()=>GifExport.Write(hidden,output,default),"GIFexportoutputfailure propagates");
        check(ImageOutputInfo.Format("convert.gif")=="gif"&&Catalog.Available(new Prefs(),new[]{"a.png","b.gif"}).Any(x=>x.Id=="convert.gif"),"GIF conversion format andcatalog integrated");
        check(!Catalog.Available(new Prefs(),new[]{"a.mp4"}).Any(x=>x.Id=="convert.gif"),"GIFconversion notoffered formedia");
        new ImageRecipe("gif").Validate(); check(ImageRecipe.Formats[^1]=="gif","GIF preset format appended after the existing four formats");
    }
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream{public override void Write(ReadOnlySpan<byte> data){base.Write(data);if(Length>1000)token.Cancel();}}
    private sealed class BrokenOutput:MemoryStream{public override void Write(ReadOnlySpan<byte> data)=>throw new IOException("Injected outputfailure");}
}
