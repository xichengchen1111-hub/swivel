using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class GifPaletteChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Encode(GifPalette.Result result){using var s=new MemoryStream();result.Write(s,default);check(s.CanWrite,"Palette output remains caller-owned");return s.ToArray();}
        PixelImage Decode(byte[] bytes){using var s=new MemoryStream(bytes);return GifImage.Read(s,default);}
        int Color(byte[] p,int i)=>p[i+3]==0?-1:p[i]|p[i+1]<<8|p[i+2]<<16;
        var random=new Random(4701);
        foreach(int maximum in new[]{2,3,16,32,64,128,255,256})foreach(bool alpha in new[]{false,true})foreach(bool rich in new[]{false,true})
        {
            var data=new byte[64*65*4];for(int i=0;i<data.Length;i+=4){int color=rich?random.Next(1<<24):(i/4)%Math.Max(1,maximum-(alpha?1:0));data[i]=(byte)color;data[i+1]=(byte)(color>>8);data[i+2]=(byte)(color>>16);data[i+3]=(byte)(alpha&&(i/4)%19==0?0:255);}
            var original=(byte[])data.Clone();var image=new PixelImage(64,65,data);var result=GifPalette.Reduce(image,maximum,default);byte[] encoded=Encode(result);var output=Decode(encoded);var colors=new HashSet<int>();long changed=0;
            for(int i=0;i<data.Length;i+=4)
            {
                colors.Add(Color(output.Bgra,i));check(output.Bgra[i+3]==data[i+3],"Palette retains binary alpha at every pixel");
                if(data[i+3]==0)check(output.Bgra.AsSpan(i,4).SequenceEqual(new byte[4]),"Palette clears hidden transparent RGB");
                else if(!output.Bgra.AsSpan(i,3).SequenceEqual(data.AsSpan(i,3)))changed++;
            }
            check(colors.Count==result.Colors&&colors.Count<=maximum&&result.Limit==maximum&&result.Width==64&&result.Height==65,"Palette actual entry count includes transparency and dimensions retained");
            check(changed==result.ChangedOpaquePixels,"Palette changed count excludes hidden transparent RGB");check(data.SequenceEqual(original),"Palette source pixels unchanged");
            check(Encode(GifPalette.Reduce(image,maximum,default)).SequenceEqual(encoded),"Palette deterministic complete GIF bytes");
            if(!rich)
            {using var legacy=new MemoryStream();GifExport.Write(image,legacy,default);check(legacy.ToArray().SequenceEqual(encoded)&&changed==0,"Already-fitting image retains all visible RGB and old exact GIF bytes");}
            // Histogram/weighted splitting must not depend on pixel enumeration order.
            var reverse=new byte[data.Length];for(int i=0,n=data.Length/4;i<n;i++)data.AsSpan(i*4,4).CopyTo(reverse.AsSpan((n-1-i)*4));
            var backwards=Decode(Encode(GifPalette.Reduce(new(64,65,reverse),maximum,default)));
            check(Enumerable.Range(0,data.Length/4).All(i=>backwards.Bgra.AsSpan((data.Length/4-1-i)*4,4).SequenceEqual(output.Bgra.AsSpan(i*4,4))),"Palette histogram mapping invariant under reversed pixel order");
            var preview=result.Preview(16,default);check(preview.Width<=16&&preview.Height<=16,"Palette thumbnail edge bound");
            for(int y=0;y<preview.Height;y++)for(int x=0;x<preview.Width;x++){int sx=x*64/preview.Width,sy=y*65/preview.Height;check(preview.Bgra.AsSpan((y*preview.Width+x)*4,4).SequenceEqual(output.Bgra.AsSpan((sy*64+sx)*4,4)),"Palette preview independent nearest-neighbor coordinates from full result");}
            Array.Fill(preview.Bgra,(byte)0);Array.Fill(data,(byte)13);check(Encode(result).SequenceEqual(encoded),"Result owned immutable pixels survive caller source/preview mutation");
        }
        var weighted=new PixelImage(3,1,new byte[]{0,0,0,255,0,1,0,255,9,8,7,0});var w=GifPalette.Reduce(weighted,2,default);var wd=Decode(Encode(w));
        check(w.Colors==2&&w.ChangedOpaquePixels==1&&wd.Bgra.SequenceEqual(new byte[]{0,1,0,255,0,1,0,255,0,0,0,0}),"One opaque slot weighted mean uses original8bit samples and rounds half up within one5bit bin");
        var transparent=GifPalette.Reduce(new(2,1,new byte[]{1,2,3,0,4,5,6,0}),2,default);check(transparent.Colors==1&&transparent.ChangedOpaquePixels==0&&Decode(Encode(transparent)).Bgra.All(x=>x==0),"All-transparent palette needs only one entry");
        var low=new PixelImage(257,1,new byte[257*4]);for(int i=0;i<257;i++){low.Bgra[i*4]=(byte)i;low.Bgra[i*4+1]=(byte)(i>>8);low.Bgra[i*4+3]=255;}
        using(var sink=new MemoryStream())reject(()=>GifExport.Write(low,sink,default),"Existing direct GIF still refuses257colors");check(GifPalette.Reduce(low,256,default).Colors<=256,"Explicit tool can reduce257colors");
        foreach(int a in new[]{1,127,254})
        {var invalid=(byte[])low.Bgra.Clone();invalid[^1]=(byte)a;reject(()=>GifPalette.Reduce(new(257,1,invalid),2,default),"Late partial alpha refused even after palette limit exceeded");}
        foreach(int n in new[]{-1,0,1,257,int.MaxValue})reject(()=>GifPalette.Reduce(weighted,n,default),"Invalid maximum refused");
        reject(()=>GifPalette.Reduce(new(1,1,new byte[3]),2,default),"Invalid pixel buffer refused");reject(()=>GifPalette.Reduce(new(4000,4001,Array.Empty<byte>()),2,default),"Quantizer keeps16MP budget");
        reject(()=>GifPalette.Reduce(weighted,2,new CancellationToken(true)),"Quantizer pre-cancel");reject(()=>w.Preview(0,default),"Preview zero edge refused");reject(()=>w.Preview(961,default),"Preview over960 refused");check(w.Preview(1,default).Width==1,"Onepixel preview supported without enlarging");
        using(var s=new MemoryStream()){reject(()=>w.Write(s,new CancellationToken(true)),"Prepared GIF pre-cancel");check(s.Length==0,"Prepared cancellation beforeheader writes nothing");}
        using(var s=new Broken())reject(()=>w.Write(s,default),"Prepared GIF write error propagated");
        // A16MP dominant white bin overflows int RGB sums. Exact result must stay
        // white; the two rare endpoints share one purple bucket.
        var big=new byte[64_000_000];for(int i=0;i<big.Length;i+=4)big[i]=big[i+1]=big[i+2]=big[i+3]=255;
        big[^8]=255;big[^7]=0;big[^6]=0;big[^4]=0;big[^3]=0;big[^2]=255;
        var large=GifPalette.Reduce(new(4000,4000,big),2,default);check(large.Colors==2&&large.ChangedOpaquePixels==2&&large.Preview(1,default).Bgra.SequenceEqual(new byte[]{255,255,255,255}),"16MP long weighted sums/dominant-last median split stay valid and nonempty");
        var prefs=new Prefs();check(Catalog.Available(prefs,new[]{"a.png"}).Any(x=>x.Id=="image.gifpalette")&&!Catalog.Available(prefs,new[]{"a.png","b.png"}).Any(x=>x.Id=="image.gifpalette")&&!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="image.gifpalette")&&!Catalog.Available(prefs,new[]{"a.mp4"}).Any(x=>x.Id=="image.gifpalette"),"Palette action requires exactly one supported explicit image");
        prefs.SetCustomActions(new[]{"image.gifpalette"});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Contains("image.gifpalette"),"New palette MyWheel action roundtrip");
    }
    private sealed class Broken:MemoryStream{public override void Write(ReadOnlySpan<byte> value)=>throw new IOException("injected");}
}
