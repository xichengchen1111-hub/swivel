using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class PngMetadataChecks
{
    // Independent slow bitwise CRC oracle (not the production lookup table).
    internal static byte[] Chunk(string type,byte[] payload)
    {
        var bytes=new byte[payload.Length+12];BinaryPrimitives.WriteUInt32BigEndian(bytes,(uint)payload.Length);Encoding.ASCII.GetBytes(type).CopyTo(bytes,4);payload.CopyTo(bytes,8);
        uint crc=uint.MaxValue;foreach(byte b in bytes.AsSpan(4,payload.Length+4)){crc^=b;for(int i=0;i<8;i++)crc=(crc&1)==0?crc>>1:(crc>>1)^0xedb88320;}
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length-4),~crc);return bytes;
    }
    internal static byte[] Header(int width=3,int height=2,int depth=8,int color=6,int interlace=0)
    {var h=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(h,(uint)width);BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4),(uint)height);h[8]=(byte)depth;h[9]=(byte)color;h[12]=(byte)interlace;return Chunk("IHDR",h);}
    internal static byte[] Png(params byte[][] chunks)=>new byte[]{137,80,78,71,13,10,26,10}.Concat(chunks.SelectMany(c=>c)).ToArray();
    // Zero-filled filter-0 rows matching an IHDR; interlaced data sums the seven Adam7 passes.
    internal static byte[] ImageData(int width,int height,int depth,int color,bool interlaced=false,byte filter=0,int extra=0)
    {
        int channels=color switch{0=>1,2=>3,3=>1,4=>2,_=>4};var raw=new MemoryStream();
        void Pass(int w,int h){if(w<=0||h<=0)return;int row=(w*channels*depth+7)/8;for(int y=0;y<h;y++){raw.WriteByte(filter);raw.Write(new byte[row]);}}
        if(!interlaced)Pass(width,height);
        else{int[] x0={0,4,0,2,0,1,0},dx={8,8,4,4,2,2,1},y0={0,0,4,0,2,0,1},dy={8,8,8,4,4,2,2};for(int p=0;p<7;p++)Pass((width-x0[p]+dx[p]-1)/dx[p],(height-y0[p]+dy[p]-1)/dy[p]);}
        raw.Write(new byte[extra]);
        using var target=new MemoryStream();using(var z=new ZLibStream(target,CompressionLevel.Optimal,true))z.Write(raw.ToArray());return target.ToArray();
    }
    internal static byte[] ImageData()
    {
        using var target=new MemoryStream();using(var z=new ZLibStream(target,CompressionLevel.SmallestSize,true))z.Write(Enumerable.Range(0,26).Select(i=>(byte)(i is 0 or 13?0:i*7)).ToArray());return target.ToArray();
    }
    internal static byte[] Clean(PngMetadata.Plan plan,CancellationToken token=default){using var output=new MemoryStream();plan.Write(output,token);return output.ToArray();}
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var end=Chunk("IEND",Array.Empty<byte>());check(Convert.ToHexString(end)=="0000000049454E44AE426082","Independent PNG CRC matches standard IEND vector");
        var data=ImageData();var idat=Chunk("IDAT",data);var plain=Png(Header(),idat,end);
        var retained=new[]{Header(),Chunk("gAMA",new byte[]{0,0,177,143}),Chunk("vpAg",new byte[]{7,8,9}),idat,Chunk("prIV",new byte[]{42}),end};
        var removed=new[]{Chunk("tEXt",Encoding.UTF8.GetBytes("Author\0private")),Chunk("zTXt",new byte[]{65,0,0,255,255}),Chunk("iTXt",new byte[]{88,0,1,0,0,0,254}),Chunk("eXIf",new byte[]{0,255,1}),Chunk("tIME",new byte[]{7,234,10,24,1,2,3})};
        byte[] decorated=Png(retained[0],removed[0],retained[1],removed[1],retained[2],removed[2],retained[3],removed[3],retained[4],removed[4],retained[5]);
        using var input=new MemoryStream(decorated);var plan=PngMetadata.Read(input,default);
        check(plan.Width==3&&plan.Height==2&&plan.SourceBytes==decorated.Length&&plan.RemovedCount==5&&plan.RemovedBytes==removed.Sum(c=>c.Length),"Review identifies exact complete removable chunks without parsing malformed text/EXIF payloads");
        check(Clean(plan).SequenceEqual(Png(retained))&&plan.OutputBytes==Png(retained).Length,"Retained color/private/IDAT bytes and interleaved order are unchanged, only five chunk types removed");
        check(plan.Chunks.Where(c=>c.Removed).Select(c=>c.Type).SequenceEqual(new[]{"tEXt","zTXt","iTXt","eXIf","tIME"}),"Review names only closed chunk types and whole-frame byte sizes");
        reject(()=>((IList<PngMetadataChunk>)plan.Chunks)[0]=new("xxxx",0,0,false),"Summary cannot be mutated");
        Array.Fill(decorated,(byte)0);check(Clean(plan).SequenceEqual(Png(retained)),"Caller mutation cannot alter owned immutable source snapshot");
        var untouched=PngMetadata.Read(new MemoryStream(plain),default);check(untouched.RemovedCount==0&&Clean(untouched).SequenceEqual(plain),"No-match core plan is identity; native save separately refuses meaningless copies");
        var repeated=Png(Header(),removed[0],removed[0],idat,removed[0],end);var repeatedPlan=PngMetadata.Read(new MemoryStream(repeated),default);
        check(repeatedPlan.RemovedCount==3&&repeatedPlan.Chunks.Single(c=>c.Type=="tEXt").Count==3&&Clean(repeatedPlan).SequenceEqual(plain),"Repeated text before and after IDAT is fully removed and aggregated accurately");
        using(var canceled=new MemoryStream()){reject(()=>untouched.Write(canceled,new CancellationToken(true)),"Pre-cancel output rejected");check(canceled.Length==0,"Pre-cancel writes no output signature");}
        reject(()=>PngMetadata.Read(new MemoryStream(plain),new CancellationToken(true)),"Pre-cancel input rejected before reading");
        var shortReads=new TinyReads(plain);check(Clean(PngMetadata.Read(shortReads,default)).SequenceEqual(plain),"Short input reads are filled exactly");
        for(int length=0;length<plain.Length;length++){int captured=length;reject(()=>PngMetadata.Read(new MemoryStream(plain[..captured]),default),"Every truncated prefix is refused");}
        for(int i=0;i<plain.Length;i++){var broken=(byte[])plain.Clone();broken[i]^=1;reject(()=>PngMetadata.Read(new MemoryStream(broken),default),"Every single-byte corruption is refused by framing/header/CRC");}
        foreach(string type in new[]{"acTL","fcTL","fdAT","dSIG","caBX","cICP","mDCV","cLLI","ABCD","abca","a1Ca"})
        {
            var chunk=Chunk(type,new byte[]{1});reject(()=>PngMetadata.Read(new MemoryStream(Png(Header(),chunk,idat,end)),default),"Unsupported signaling/names/critical chunks fail before native decode");
        }
        foreach(var chunks in new[]{new[]{idat,end},new[]{Header(),Header(),idat,end},new[]{Header(),end},new[]{Header(),Chunk("IDAT",Array.Empty<byte>()),end},new[]{Header(),idat,Chunk("vpAg",Array.Empty<byte>()),idat,end},new[]{Header(),idat,Chunk("IEND",new byte[]{0})},new[]{Header(),idat,end,end}})
            reject(()=>PngMetadata.Read(new MemoryStream(Png(chunks)),default),"Bad critical ordering/noncontiguous IDAT/nonterminal IEND rejected");
        var trailing=plain.Concat(new byte[]{0}).ToArray();reject(()=>PngMetadata.Read(new MemoryStream(trailing),default),"Trailing bytes not silently copied or discarded");
        foreach(int color in new[]{0,2,3,4,6})foreach(int depth in new[]{1,2,4,8,16})
        {
            var fitting=depth<=8?Chunk("IDAT",ImageData(3,2,depth,color)):idat;var candidate=color==3?Png(Header(depth:depth,color:color),Chunk("PLTE",new byte[]{0,0,0}),fitting,end):Png(Header(depth:depth,color:color),fitting,end);
            bool supported=color is 0 or 3?depth<=8:depth==8;
            if(supported)check(PngMetadata.Read(new MemoryStream(candidate),default).Width==3,"Allowed SDR header subset; native decoder still validates actual pixels");
            else reject(()=>PngMetadata.Read(new MemoryStream(candidate),default),"Invalid or high-precision color/depth pairing refused");
        }
        check(PngMetadata.Read(new MemoryStream(Png(Header(9,10,interlace:1),Chunk("IDAT",ImageData(9,10,8,6,interlaced:true)),end)),default).Width==9,"Interlaced image data inflates to the exact Adam7 size");
        foreach(var damaged in new[]{new byte[]{1,2,3},ImageData(3,2,8,6)[..^4].Concat(new byte[4]).ToArray(),ImageData(3,3,8,6),ImageData(3,2,8,6,extra:1),ImageData(3,2,8,6,filter:5),ImageData(3,2,8,6,interlaced:true),ImageData(3,2,8,6)[..^4],ImageData(3,2,8,6).Concat(new byte[]{0}).ToArray()})
            reject(()=>PngMetadata.Read(new MemoryStream(Png(Header(),Chunk("IDAT",damaged),end)),default),"Damaged, checksum-failing, short, long, bad-filter or wrongly interlaced image data refused before any system decoder");
        foreach(var h in new[]{Header(0),Header(height:0),Header(16385),Header(4001,4000),Header(interlace:2),Header(color:1)})reject(()=>PngMetadata.Read(new MemoryStream(Png(h,idat,end)),default),"Dimension/pixel/interlace/unknown color bounds");
        foreach(var chunks in new[]{new[]{Header(color:3),idat,end},new[]{Header(color:0),Chunk("PLTE",new byte[]{0,0,0}),idat,end},new[]{Header(),idat,Chunk("PLTE",new byte[]{0,0,0}),end},new[]{Header(),Chunk("PLTE",new byte[]{0}),idat,end},new[]{Header(depth:1,color:3),Chunk("PLTE",new byte[9]),idat,end},new[]{Header(),Chunk("tRNS",new byte[1]),idat,end}})
            reject(()=>PngMetadata.Read(new MemoryStream(Png(chunks)),default),"Palette/transparency structural bounds");
        var many=new List<byte[]>{Header()};for(int i=0;i<PngMetadata.MaxChunks-3;i++)many.Add(Chunk("vpAg",Array.Empty<byte>()));many.Add(idat);many.Add(end);
        var atLimit=Png(many.ToArray());check(PngMetadata.Read(new MemoryStream(atLimit),default).Chunks.Sum(c=>c.Count)==4096,"Exactly4096 chunks accepted including header/IDAT/IEND");
        many.Insert(1,Chunk("vpAg",Array.Empty<byte>()));reject(()=>PngMetadata.Read(new MemoryStream(Png(many.ToArray())),default),"4097 chunks refused");
        var padding=new byte[PngMetadata.MaxBytes-plain.Length-12];var exact=Png(Header(),Chunk("vpAg",padding),idat,end);
        check(exact.Length==PngMetadata.MaxBytes&&PngMetadata.Read(new MemoryStream(exact),default).SourceBytes==PngMetadata.MaxBytes,"Exact32MiB source ceiling accepted without metadata inflation");
        reject(()=>PngMetadata.Read(new MemoryStream(new byte[PngMetadata.MaxBytes+1]),default),"Source ceiling checked before allocation/read");
        var files=new[]{@"C:\a.png"};var prefs=new Prefs();var action=Catalog.Available(prefs,files).Single(a=>a.Id=="image.pngmetadata");
        check(action.SingleFile&&!Catalog.Available(prefs,new[]{@"C:\a.jpg"}).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,new[]{@"C:\a.png",@"C:\b.png"}).Any(a=>a.Id==action.Id),"Stable action offered only for exactly one explicit PNG");
        prefs.SetCustomActions(new[]{action.Id});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()==action.Id,"New action retains stable MyWheel settings roundtrip");
    }
    private sealed class TinyReads(byte[] bytes):MemoryStream(bytes)
    {public override int Read(Span<byte> target)=>base.Read(target[..Math.Min(3,target.Length)]);}
}
