using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class GifImageChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        PixelImage Read(byte[] bytes)
        {
            using var input=new MemoryStream(bytes);var image=GifImage.Read(input,default);
            check(input.CanRead&&input.Position==0&&input.ToArray().SequenceEqual(bytes),"GIF streams stay caller-owned with unchanged bytes and reset position");return image;
        }
        foreach(bool interlace in new[]{false,true})foreach(bool local in new[]{false,true})foreach(int height in Enumerable.Range(1,19))
        {
            const int width=13;int[] indices=Rows(height,interlace).SelectMany(y=>Enumerable.Range(0,width).Select(x=>(x+2*y)%4)).ToArray();
            byte[] fixture=Fixture(width,height,indices,2,local,interlace,transparent:2);var image=Read(fixture);
            check(image.Width==width&&image.Height==height&&image.Bgra.Length==width*height*4,"Static GIF canvas and BGRA sizes match");
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int index=(x+2*y)%4;byte[] color=Palette(index);if(index==2)Array.Clear(color);
                check(image.Bgra.AsSpan((y*width+x)*4,4).SequenceEqual(color),"Global/local palette,binary transparency and all four interlace passes retain exact visible pixels");
            }
        }
        foreach(int minimum in Enumerable.Range(2,7))
        {
            int[] indices=Enumerable.Range(0,9000).Select(i=>i%(1<<minimum)).ToArray();var image=Read(Fixture(9000,1,indices,minimum));
            check(Enumerable.Range(0,indices.Length).All(i=>image.Bgra.AsSpan(i*4,4).SequenceEqual(Palette(indices[i]))),"LZW literal streams cross variable-width boundaries through12 bits and retain a full deferred-clear dictionary");
        }
        byte[] special=Fixture(3,1,new[]{0,0,0},2,codes:new[]{4,0,6,5});check(Read(special).Bgra.Where((_,i)=>i%4==3).All(x=>x==255),"LZW code==next KwKwK expands the previous string plus its first value");
        byte[] plain=Fixture(3,2,new[]{0,1,2,3,1,0},2);var expected=Read(plain);
        byte[] split=Fixture(3,2,new[]{0,1,2,3,1,0},2,blockSize:1);check(Read(split).Bgra.SequenceEqual(expected.Bgra),"Codes may cross any data sub-block boundary");
        foreach(byte aspect in new byte[]{0,49}){byte[] square=(byte[])plain.Clone();square[12]=aspect;check(Read(square).Bgra.SequenceEqual(expected.Bgra),"Unspecified and explicit square pixel aspects accepted");}
        foreach(int aspect in new[]{1,48,50,255}){byte[] invalid=(byte[])plain.Clone();invalid[12]=(byte)aspect;reject(()=>Read(invalid),"Non-square pixel aspect refuses instead of distorting");}
        byte[] noGlobal=Fixture(3,1,new[]{0,1,2},2,local:true,global:false);check(Read(noGlobal).Bgra.AsSpan(4,4).SequenceEqual(Palette(1)),"A complete local palette does not require a global table");
        byte[] comments=Insert(plain,plain.Length-1,new byte[]{0x21,0xfe,3,65,66,67,0});check(Read(comments).Bgra.SequenceEqual(expected.Bgra),"Comments after the frame are validated and dropped");
        byte[] loop=Insert(plain,13+4*3,new byte[]{0x21,0xff,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0});check(Read(loop).Bgra.SequenceEqual(expected.Bgra),"Recognized one-image looping control does not change static pixels");
        byte[] animated=plain[..^1].Concat(plain.AsSpan(13+12,plain.Length-(13+12)).ToArray()).ToArray();reject(()=>Read(animated),"Two image descriptors rejected as a whole,not first-frame export");
        foreach(var atValue in new[]{(26,1),(28,1),(30,2),(32,1)})
        {byte[] shifted=(byte[])plain.Clone();shifted[atValue.Item1]= (byte)atValue.Item2;reject(()=>Read(shifted),"Offset/partial canvas layouts refused");}
        reject(()=>Read(Insert(plain,25,new byte[]{0x21,0x01})),"Plain text rendering extension is not silently omitted");
        reject(()=>Read(Insert(plain,25,new byte[]{0x21,0xff,11,73,67,67,82,71,66,71,49,48,49,50})),"ICC/unknown application extension is not silently downgraded");
        reject(()=>Read(Insert(plain,25,new byte[]{0x21,0xf9,4,2,0,0,0,0})),"User-input controls refused");
        byte[] transparency=Insert(plain,25,new byte[]{0x21,0xf9,4,1,0,0,255,0});reject(()=>Read(transparency),"Transparent index outside actual palette refused");
        foreach(byte[] bad in new[]{plain[..^1],plain.Concat(new byte[]{0}).ToArray(),plain[..20],new byte[100]})reject(()=>Read(bad),"Truncation,wrong signature and bytes after trailer refused");
        byte[] reserved=(byte[])plain.Clone();reserved[34]=0x18;reject(()=>Read(reserved),"Image descriptor reserved bits refused");
        byte[] shortImage=Fixture(3,1,new[]{0},2,codes:new[]{4,0,5});reject(()=>Read(shortImage),"EOI before full pixel count is not filled with fake pixels");
        byte[] longImage=Fixture(1,1,new[]{0,1},2);reject(()=>Read(longImage),"Decoded pixels beyond canvas refused before out-of-bounds write");
        reject(()=>Read(Fixture(1,1,new[]{0},2,codes:new[]{4,7,5})),"Unassigned dictionary code refused");
        reject(()=>Read(Fixture(1,1,new[]{0},2,codes:new[]{4,0})),"Sub-block terminator cannot replace the LZW end code");
        byte[] tail=plain[..^2].Concat(new byte[]{1,0,0,0x3b}).ToArray();reject(()=>Read(tail),"Extra payload blocks after EOI refused explicitly");
        byte[] emptyPalette=Fixture(1,1,new[]{0},2);emptyPalette[10]=0;emptyPalette=emptyPalette[..13].Concat(emptyPalette[25..]).ToArray();reject(()=>Read(emptyPalette),"External or guessed palette not substituted for missing table");
        byte[] largeHeader=(byte[])plain.Clone();BinaryPrimitives.WriteUInt16LittleEndian(largeHeader.AsSpan(6),4000);BinaryPrimitives.WriteUInt16LittleEndian(largeHeader.AsSpan(8),4001);reject(()=>Read(largeHeader),"16MP image budget checked on canvas before allocation");
        byte[] wideHeader=(byte[])plain.Clone();BinaryPrimitives.WriteUInt16LittleEndian(wideHeader.AsSpan(6),16385);reject(()=>Read(wideHeader),"Side length checked before allocation");
        byte[] metadata=Comment(GifImage.MaxExtensionBytes);check(Read(Insert(plain,25,metadata)).Bgra.SequenceEqual(expected.Bgra),"Exact64KiB extension payload accepted");reject(()=>Read(Insert(plain,25,Comment(GifImage.MaxExtensionBytes+1))),"Extension payload over budget refused");
        byte[] manyComments=Enumerable.Range(0,GifImage.MaxExtensions).SelectMany(_=>new byte[]{0x21,0xfe,0}).ToArray();check(Read(Insert(plain,25,manyComments)).Bgra.SequenceEqual(expected.Bgra),"Exact1024 extensions accepted");reject(()=>Read(Insert(plain,25,manyComments.Concat(new byte[]{0x21,0xfe,0}).ToArray())),"Extension count +1 refused");
        reject(()=>Read(Fixture(1,1,new[]{0},2,codes:Enumerable.Repeat(4,4100).Concat(new[]{0,5}).ToArray())),"LZW clear-code bomb bounded independently of input bytes");
        int[] boundedPixels=new int[1000*467];reject(()=>Read(Fixture(1000,467,boundedPixels,8,clearEach:true,blockSize:1)),"Fragmented image data exceeds shared subblock budget and is refused");
        using(var input=new ReportedLength(plain,ImageRules.MaxInputBytes+1))reject(()=>GifImage.Read(input,default),"Input128MiB budget before parsing");
        using(var cancelled=new CancellationTokenSource())using(var input=new CancelRead(Fixture(9000,1,new int[9000],2),cancelled))reject(()=>GifImage.Read(input,cancelled.Token),"Mid-source cancellation propagates");
        using(var input=new MemoryStream(plain))reject(()=>GifImage.Read(input,new CancellationToken(true)),"Precancel GIF read");
        check(Catalog.IsImage("static.GIF")&&Catalog.Available(new Prefs(),new[]{"image.gif"}).Any(x=>x.Id=="convert.png"),"GIF explicit image actions available forconversion");
        check(ImageRecipe.Formats.Take(4).SequenceEqual(new[]{"png","jpg","bmp","tiff"}),"Existing four output recipeindices remain unchanged");
    }
    internal static byte[] Fixture(int width,int height,int[] indices,int minimum,bool local=false,bool interlace=false,int transparent=-1,bool global=true,int[]? codes=null,bool clearEach=false,int blockSize=255)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output);
        writer.Write("GIF89a"u8);writer.Write((ushort)width);writer.Write((ushort)height);writer.Write((byte)(global?128|(minimum-1):0));writer.Write((byte)0);writer.Write((byte)0);
        void Table(){for(int i=0;i<(1<<minimum);i++){byte[] c=Palette(i);writer.Write(c[2]);writer.Write(c[1]);writer.Write(c[0]);}}
        if(global)Table();
        if(transparent>=0)writer.Write(new byte[]{0x21,0xf9,4,1,0,0,(byte)transparent,0});
        writer.Write((byte)0x2c);writer.Write((ushort)0);writer.Write((ushort)0);writer.Write((ushort)width);writer.Write((ushort)height);writer.Write((byte)((local?128|(minimum-1):0)|(interlace?64:0)));if(local)Table();writer.Write((byte)minimum);
        var packed=new List<byte>();uint bits=0;int used=0,size=minimum+1,clear=1<<minimum,next=clear+2;bool previous=false;
        void Code(int value)
        {
            bits|=(uint)value<<used;used+=size;while(used>=8){packed.Add((byte)bits);bits>>=8;used-=8;}
            if(value==clear){size=minimum+1;next=clear+2;previous=false;}
            else if(value!=clear+1){if(previous&&next<4096){next++;if(next==(1<<size)&&size<12)size++;}previous=true;}
        }
        if(codes is not null){foreach(int code in codes)Code(code);}else{Code(clear);foreach(int index in indices){if(clearEach)Code(clear);Code(index);}Code(clear+1);}
        if(used>0)packed.Add((byte)bits);
        for(int at=0;at<packed.Count;){int count=Math.Min(blockSize,packed.Count-at);writer.Write((byte)count);writer.Write(packed.GetRange(at,count).ToArray());at+=count;}
        writer.Write((byte)0);writer.Write((byte)0x3b);return output.ToArray();
    }
    private static byte[] Palette(int index)=>new byte[]{(byte)(index*17),(byte)(index*53),(byte)(index*101),255};
    private static IEnumerable<int> Rows(int height,bool interlace)
    {if(!interlace){for(int y=0;y<height;y++)yield return y;}else foreach(var step in new[]{(0,8),(4,8),(2,4),(1,2)})for(int y=step.Item1;y<height;y+=step.Item2)yield return y;}
    private static byte[] Insert(byte[] bytes,int at,byte[] extra)=>bytes[..at].Concat(extra).Concat(bytes[at..]).ToArray();
    private static byte[] Comment(int count)
    {using var stream=new MemoryStream();stream.Write(new byte[]{0x21,0xfe});while(count>0){int size=Math.Min(255,count);stream.WriteByte((byte)size);stream.Write(new byte[size]);count-=size;}stream.WriteByte(0);return stream.ToArray();}
    private sealed class ReportedLength(byte[] bytes,long length):MemoryStream(bytes){public override long Length=>length;}
    private sealed class CancelRead(byte[] bytes,CancellationTokenSource cancellation):MemoryStream(bytes)
    {public override int Read(Span<byte> buffer){int count=base.Read(buffer);if(Position>100)cancellation.Cancel();return count;}}
}
