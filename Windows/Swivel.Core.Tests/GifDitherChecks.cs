using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class GifDitherChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Encode(GifPalette.Result result){using var stream=new MemoryStream();result.Write(stream,default);return stream.ToArray();}
        PixelImage Decode(byte[] bytes){using var stream=new MemoryStream(bytes);return GifImage.Read(stream,default);}
        int Color(byte[] data,int at)=>data[at+3]==0?-1:data[at]|data[at+1]<<8|data[at+2]<<16;
        var random=new Random(7401);
        foreach(int maximum in new[]{2,3,16,32,64,128,255,256})
        foreach(var size in new[]{(W:1,H:130),(W:131,H:1),(W:2,H:129),(W:17,H:19),(W:37,H:23)})
        foreach(int alpha in new[]{0,1,2})
        {
            var data=new byte[size.W*size.H*4];
            for(int y=0;y<size.H;y++)for(int x=0;x<size.W;x++)
            {
                int at=(y*size.W+x)*4;random.NextBytes(data.AsSpan(at,3));data[at+3]=(byte)(alpha==1&&(x+y)%7==0||alpha==2&&(x==size.W/2||y==size.H/2)?0:255);
            }
            var source=(byte[])data.Clone();var image=new PixelImage(size.W,size.H,data);var legacy=GifPalette.Reduce(image,maximum,default);byte[] legacyBytes=Encode(legacy);
            check(Encode(GifPalette.Reduce(image,maximum,GifDither.None,default)).SequenceEqual(legacyBytes)&&legacy.Dither==GifDither.None,"GIF explicit None and old overload retain identical bytes");
            var result=GifPalette.Reduce(image,maximum,GifDither.FloydSteinberg,default);byte[] bytes=Encode(result);var output=Decode(bytes);var originalColors=new HashSet<int>();for(int at=0;at<data.Length;at+=4)originalColors.Add(Color(data,at));
            var expected=originalColors.Count<=maximum?Decode(legacyBytes).Bgra:Reference(image,Decode(legacyBytes));
            check(output.Bgra.SequenceEqual(expected),"GIF serpentine diffusion matches independent exhaustive nearest-color reference");
            var actual=new HashSet<int>();long changed=0;
            for(int at=0;at<data.Length;at+=4)
            {
                actual.Add(Color(output.Bgra,at));check(output.Bgra[at+3]==data[at+3],"GIF dithering retains every binary alpha");
                if(data[at+3]==0)check(output.Bgra.AsSpan(at,4).SequenceEqual(new byte[4]),"GIF dithering zeroes hidden transparent RGB");
                else if(!output.Bgra.AsSpan(at,3).SequenceEqual(data.AsSpan(at,3)))changed++;
            }
            check(actual.Count==result.Colors&&actual.Count<=maximum&&changed==result.ChangedOpaquePixels&&result.Dither==GifDither.FloydSteinberg,"GIF dither actual used palette and changed RGB counts");
            check(source.SequenceEqual(data)&&Encode(GifPalette.Reduce(image,maximum,GifDither.FloydSteinberg,default)).SequenceEqual(bytes),"GIF dither source unchanged and complete output deterministic");
            var preview=result.Preview(11,default);
            for(int y=0;y<preview.Height;y++)for(int x=0;x<preview.Width;x++)check(preview.Bgra.AsSpan((y*preview.Width+x)*4,4).SequenceEqual(output.Bgra.AsSpan(((y*size.H/preview.Height)*size.W+x*size.W/preview.Width)*4,4)),"GIF dither preview samples the exact saved result");
            Array.Fill(preview.Bgra,(byte)0);Array.Fill(data,(byte)13);check(Encode(result).SequenceEqual(bytes),"GIF dither result owns pixels independently of source and preview");
        }
        // A smooth grayscale gradient must actually use spatial mixtures near a
        // quantization boundary. This measures a particular fixture, not all images.
        var gradientData=new byte[256*16*4];for(int y=0;y<16;y++)for(int x=0;x<256;x++){int at=(y*256+x)*4;gradientData[at]=gradientData[at+1]=gradientData[at+2]=(byte)x;gradientData[at+3]=255;}
        var gradient=new PixelImage(256,16,gradientData);var plain=GifPalette.Reduce(gradient,2,default);var dithered=GifPalette.Reduce(gradient,2,GifDither.FloydSteinberg,default);var no=Decode(Encode(plain));var yes=Decode(Encode(dithered));
        check(!no.Bgra.SequenceEqual(yes.Bgra),"GIF optional diffusion changes rich gradient pixels");
        double Error(PixelImage output){double sum=0;for(int first=80;first<176;first+=8){double average=0;for(int y=0;y<16;y++)for(int x=first;x<first+8;x++)average+=output.Bgra[(y*256+x)*4];sum+=Math.Abs(average/128-(first+3.5));}return sum;}
        check(Error(yes)<Error(no)/2,"GIF diffusion improves local mean fidelity in a bounded grayscale gradient fixture");
        var transparent=new PixelImage(1,1,new byte[]{51,29,7,0});var hidden=GifPalette.Reduce(transparent,2,GifDither.FloydSteinberg,default);
        check(hidden.Colors==1&&hidden.ChangedOpaquePixels==0&&Decode(Encode(hidden)).Bgra.All(x=>x==0),"GIF onepixel all-transparent dither stays exact");
        foreach(int alpha in new[]{1,127,254}){var bad=(byte[])gradientData.Clone();bad[^1]=(byte)alpha;reject(()=>GifPalette.Reduce(new(256,16,bad),2,GifDither.FloydSteinberg,default),"GIF dithering still rejects late partial transparency");}
        reject(()=>GifPalette.Reduce(gradient,2,(GifDither)2,default),"GIF undefined dither mode refused");
        reject(()=>GifPalette.Reduce(gradient,2,(GifDither)(-1),default),"GIF negative dither mode refused");
        reject(()=>GifPalette.Reduce(gradient,2,GifDither.FloydSteinberg,new CancellationToken(true)),"GIF dither pre-cancellation");
        reject(()=>GifPalette.Reduce(new(4000,4001,Array.Empty<byte>()),2,GifDither.FloydSteinberg,default),"GIF dither retains 16MP input budget");
        reject(()=>GifPalette.Reduce(new(16385,1,new byte[16385*4]),2,GifDither.FloydSteinberg,default),"GIF dither retains side budget");
        var widest=new byte[16384*4];for(int x=0;x<16384;x++){int at=x*4;widest[at]=(byte)x;widest[at+1]=(byte)(x>>4);widest[at+2]=(byte)(x>>7);widest[at+3]=255;}
        var widthResult=GifPalette.Reduce(new(16384,1,widest),16,GifDither.FloydSteinberg,default);check(widthResult.Width==16384&&widthResult.Colors<=16,"GIF dither maximum-width row and padding stay bounded");
        using(var stream=new MemoryStream()){reject(()=>dithered.Write(stream,new CancellationToken(true)),"GIF prepared dither export pre-cancellation");check(stream.Length==0,"GIF dither cancelled export creates no header");}
    }
    // Deliberately no KD tree or pair of reusable rows: straightforward exhaustive
    // palette search and a full tiny-fixture error grid validate the optimized path.
    private static byte[] Reference(PixelImage input,PixelImage paletteImage)
    {
        var palette=new SortedSet<int>();for(int at=0;at<paletteImage.Bgra.Length;at+=4)if(paletteImage.Bgra[at+3]!=0)palette.Add(paletteImage.Bgra[at]|paletteImage.Bgra[at+1]<<8|paletteImage.Bgra[at+2]<<16);
        var error=new double[input.Height+1,input.Width+2,3];var output=new byte[input.Bgra.Length];
        for(int y=0;y<input.Height;y++)
        {
            int direction=y%2==0?1:-1;for(int scan=0;scan<input.Width;scan++)
            {
                int x=direction==1?scan:input.Width-1-scan,at=(y*input.Width+x)*4;if(input.Bgra[at+3]==0)continue;
                var adjusted=new double[3];for(int c=0;c<3;c++)adjusted[c]=Math.Clamp(input.Bgra[at+c]+error[y,x+1,c],0,255);
                double best=double.PositiveInfinity;int selected=0;foreach(int color in palette){double distance=0;for(int c=0;c<3;c++){double difference=adjusted[c]-((color>>(c*8))&255);distance+=difference*difference;}if(distance<best){best=distance;selected=color;}}
                for(int c=0;c<3;c++)
                {
                    output[at+c]=(byte)(selected>>(c*8));double difference=adjusted[c]-output[at+c];
                    error[y,x+1+direction,c]+=difference*(7d/16);error[y+1,x+1-direction,c]+=difference*(3d/16);error[y+1,x+1,c]+=difference*(5d/16);error[y+1,x+1+direction,c]+=difference*(1d/16);
                }
                output[at+3]=255;
            }
        }
        return output;
    }
}
