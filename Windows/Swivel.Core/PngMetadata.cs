using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record PngMetadataChunk(string Type, int Count, long Bytes, bool Removed);

// Original container-only editor. Kept chunks, their CRCs and order are copied
// exactly. Text/EXIF payloads are never decoded or decompressed by this editor.
// Structural checks do not replace the native PNG decoder validation at delivery.
public static class PngMetadata
{
    public const int MaxBytes = 32 * 1024 * 1024, MaxChunks = 4096;
    public const string Invalid = "Use a structurally valid static PNG, at most 32 MiB and 4,096 chunks, with supported SDR precision. Damaged CRCs, animation, unknown critical chunks and trailing data are refused.";
    public const string Credentials = "Signed or content-credential PNG files are not supported by this metadata tool. No copy was saved.";
    public const string Hdr = "PNG HDR signaling is not supported by this metadata tool. No copy was saved.";
    private static readonly byte[] Signature = {137,80,78,71,13,10,26,10};
    private static readonly uint[] CrcTable = MakeCrcTable();
    internal sealed record Chunk(string Type,int Offset,int Length,bool Removed);

    public sealed class Plan
    {
        private readonly byte[] source;
        private readonly Chunk[] chunks;
        public int Width { get; }
        public int Height { get; }
        public long SourceBytes => source.Length;
        public long RemovedBytes { get; }
        public long OutputBytes => SourceBytes-RemovedBytes;
        public int RemovedCount { get; }
        public IReadOnlyList<PngMetadataChunk> Chunks { get; }
        private Plan(byte[] source,Chunk[] chunks,int width,int height)
        {
            this.source=source;this.chunks=chunks;Width=width;Height=height;
            RemovedBytes=chunks.Where(c=>c.Removed).Sum(c=>(long)c.Length);
            RemovedCount=chunks.Count(c=>c.Removed);
            Chunks=Array.AsReadOnly(chunks.GroupBy(c=>c.Type).Select(g=>new PngMetadataChunk(g.Key,g.Count(),g.Sum(c=>(long)c.Length),g.First().Removed)).ToArray());
        }
        public void Write(Stream output,CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(output);token.ThrowIfCancellationRequested();
            if(!output.CanWrite)throw new ArgumentException("A writable output stream is required.",nameof(output));
            output.Write(Signature);
            foreach(var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();if(chunk.Removed)continue;
                for(int at=0;at<chunk.Length;)
                {token.ThrowIfCancellationRequested();int count=Math.Min(65536,chunk.Length-at);output.Write(source.AsSpan(chunk.Offset+at,count));at+=count;}
            }
            token.ThrowIfCancellationRequested();
        }
        internal static Plan Create(byte[] source,Chunk[] chunks,int width,int height)=>new(source,chunks,width,height);
    }
    public static Plan Read(Stream input,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A readable seekable input stream is required.",nameof(input));
        long length=input.Length;if(length is <45 or >MaxBytes)throw new UserError(Invalid);
        var bytes=new byte[(int)length];input.Position=0;
        try
        {
            for(int at=0;at<bytes.Length;)
            {token.ThrowIfCancellationRequested();int count=Math.Min(65536,bytes.Length-at);input.ReadExactly(bytes.AsSpan(at,count));at+=count;}
        }
        catch(EndOfStreamException){throw new UserError(Invalid);}
        token.ThrowIfCancellationRequested();
        if(!bytes.AsSpan(0,8).SequenceEqual(Signature))throw new UserError(Invalid);
        var chunks=new List<Chunk>();int offset=8,width=0,height=0,palette=0,color=-1,depth=0;
        bool data=false,dataEnded=false,end=false;long dataBytes=0;
        while(offset<bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if(chunks.Count>=MaxChunks||bytes.Length-offset<12)throw new UserError(Invalid);
            uint size=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset,4));
            if(size>int.MaxValue||size>(long)bytes.Length-offset-12)throw new UserError(Invalid);
            int payload=(int)size,total=payload+12;
            var name=bytes.AsSpan(offset+4,4);
            foreach(byte letter in name)if(letter is not (>=65 and <=90) and not (>=97 and <=122))throw new UserError(Invalid);
            if((name[2]&32)!=0)throw new UserError(Invalid);
            string type=Encoding.ASCII.GetString(name);
            uint crc=0xffffffff;
            for(int at=offset+4;at<offset+8+payload;at++)
            {if((at&65535)==0)token.ThrowIfCancellationRequested();crc=CrcTable[(crc^bytes[at])&255]^(crc>>8);}
            if((crc^0xffffffff)!=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset+8+payload,4)))throw new UserError(Invalid);
            if(type is "acTL" or "fcTL" or "fdAT")throw new UserError(Invalid);
            if(type is "dSIG" or "caBX")throw new UserError(Credentials);
            if(type is "cICP" or "mDCV" or "cLLI")throw new UserError(Hdr);
            if(chunks.Count==0&&type!="IHDR")throw new UserError(Invalid);
            if(type=="IHDR")
            {
                if(chunks.Count!=0||payload!=13)throw new UserError(Invalid);
                uint w=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset+8,4)),h=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset+12,4));
                if(w==0||h==0||w>16384||h>16384||(long)w*h>ImageRules.MaxPixels)throw new UserError(Invalid);
                width=(int)w;height=(int)h;depth=bytes[offset+16];color=bytes[offset+17];
                bool supported=color is 0 or 3?depth is 1 or 2 or 4 or 8:color is 2 or 4 or 6&&depth==8;
                if(!supported||bytes[offset+18]!=0||bytes[offset+19]!=0||bytes[offset+20]>1)throw new UserError(Invalid);
            }
            else if(type=="PLTE")
            {
                if(palette!=0||data||color is 0 or 4||payload is <3 or >768||payload%3!=0)throw new UserError(Invalid);
                palette=payload/3;if(color==3&&palette>1<<depth)throw new UserError(Invalid);
            }
            else if(type=="IDAT")
            {
                if(dataEnded||(color==3&&palette==0))throw new UserError(Invalid);
                data=true;dataBytes+=payload;
            }
            else
            {
                if(data)dataEnded=true;
                if(type=="IEND")
                {if(payload!=0||!data||dataBytes==0||offset+total!=bytes.Length)throw new UserError(Invalid);end=true;}
                else if((name[0]&32)==0)throw new UserError(Invalid);
                else if(type=="tRNS")
                {
                    if(data||!((color==0&&payload==2)||(color==2&&payload==6)||(color==3&&palette>0&&payload is >0&&payload<=palette)))throw new UserError(Invalid);
                }
            }
            chunks.Add(new(type,offset,total,type is "tEXt" or "zTXt" or "iTXt" or "eXIf" or "tIME"));offset+=total;
        }
        if(!end)throw new UserError(Invalid);
        token.ThrowIfCancellationRequested();return Plan.Create(bytes,chunks.ToArray(),width,height);
    }
    private static uint[] MakeCrcTable()
    {
        var table=new uint[256];for(uint i=0;i<256;i++){uint value=i;for(int bit=0;bit<8;bit++)value=(value&1)!=0?0xedb88320^(value>>1):value>>1;table[i]=value;}return table;
    }
}
