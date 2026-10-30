using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

// Original EXIF 2.32 / classic TIFF 6.0 relocation. Only the four named EXIF
// directories and the GPS directory are understood. No opaque source padding or
// unreferenced bytes are copied. MakerNote is intentionally never relocated.
public static class ExifLocation
{
    public const string Missing="Remove location only requires exactly one source EXIF block.";
    public const string Invalid="Remove location only is unavailable: EXIF has unsupported types or structure, duplicate tags, cycles, overlapping areas, excessive entries or out-of-range values. Full removal is still available.";
    public const string Notice="Location mode rebuilds EXIF in its source byte order without GPS, MakerNote, the embedded thumbnail and any XMP, IPTC or Photoshop data stored inside EXIF, and removes the separate XMP and IPTC blocks entirely. Other EXIF values are kept. Retained text (such as titles, comments or keywords) and image pixels may still reveal location; this is not complete privacy sanitization.";
    public const string JpegScope="JPEG location mode removes standard and extended XMP APP1 and every APP13 block. Comments, other APP1 blocks and all other segments and compressed scans stay byte-identical.";
    public const string PngScope="PNG location mode removes tEXt, zTXt and iTXt chunks whose keyword is XML:com.adobe.xmp or starts with \"Raw profile type\" (copies of EXIF, XMP or IPTC written by some converters). Other text, time and all other chunks and image data stay byte-identical.";
    public const string Export="The rebuilt copy is validated with the native decoder, which may interpret retained metadata. Save creates a new copy beside the original using the existing output name rule, without replacing any file.";
    public const string Validation="The location-only copy could not be validated by the native decoder. Full removal is still available.";
    private sealed record Entry(int Tag,int Type,uint Count,byte[] Value);
    private sealed class Ifd
    {
        internal readonly List<Entry> Entries=new();
        internal Ifd? Exif,Interop,Gps,Next;
        internal byte[]? Thumbnail;
        internal int Offset;
    }
    private sealed class InvalidExif : Exception { }
    public static byte[]? Rebuild(ReadOnlySpan<byte> source,CancellationToken token,out string? reason)
    {
        token.ThrowIfCancellationRequested();reason=null;
        if(source.Length is <8 or >32*1024*1024){reason=Invalid;return null;}
        try{return RebuildCore(source.ToArray(),token);}
        catch(InvalidExif){reason=Invalid;return null;}
    }
    private static byte[] RebuildCore(byte[] bytes,CancellationToken token)
    {
        void Require(bool yes){if(!yes)throw new InvalidExif();}
        Require(bytes.Length is >=8 and <=32*1024*1024);
        bool little=bytes[0]=='I'&&bytes[1]=='I';Require(little||(bytes[0]=='M'&&bytes[1]=='M'));
        ushort U16(int p)=>little?BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p,2)):BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p,2));
        uint U32(int p)=>little?BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p,4)):BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p,4));
        Require(U16(2)==42);
        var ranges=new List<(int Start,int End)>{(0,8)};var seen=new HashSet<uint>();int total=0;
        void Area(uint start,long length){Require(start>=8&&length>0&&start+length<=bytes.Length);Require(!ranges.Any(r=>start<r.End&&start+length>r.Start));ranges.Add(((int)start,(int)(start+length)));}
        uint Scalar(Entry e){Require(e.Type==4&&e.Count==1);return little?BinaryPrimitives.ReadUInt32LittleEndian(e.Value):BinaryPrimitives.ReadUInt32BigEndian(e.Value);}
        int[] sizes={0,1,1,2,4,8,1,1,2,4,8,4,8};
        Ifd Read(uint offset,int role)
        {
            token.ThrowIfCancellationRequested();Require(offset>=8&&(offset&1)==0&&offset<=bytes.Length-2&&seen.Add(offset));
            int count=U16((int)offset);Require(count<=512&&(total+=count)<=2048);long table=2+12L*count+4;Area(offset,table);
            var node=new Ifd();int previous=-1;
            for(int i=0;i<count;i++)
            {
                token.ThrowIfCancellationRequested();int at=(int)offset+2+12*i,tag=U16(at),type=U16(at+2);uint n=U32(at+4);
                Require(tag>previous&&type is >=1 and <=12&&n>0);previous=tag;
                long length=(long)sizes[type]*n;Require(length<=bytes.Length);
                uint value=length<=4?(uint)(at+8):U32(at+8);Require(value+length<=bytes.Length);
                if(length>4){Require((value&1)==0);Area(value,length);}
                // These TIFF offsets cannot be blindly relocated as ordinary values.
                Require((role==4||tag is not (273 or 279))&&tag is not (288 or 289 or 324 or 325 or 330 or 519 or 520 or 521));
                var entry=new Entry(tag,type,n,bytes.AsSpan((int)value,(int)length).ToArray());node.Entries.Add(entry);
                if(tag is 0x8769 or 0x8825 or 0xa005)
                {
                    Require((tag==0x8769&&role==0)||(tag==0x8825&&role==0)||(tag==0xa005&&role==1));
                    uint target=Scalar(entry);Require(target!=0);
                    var child=Read(target,tag==0x8769?1:tag==0xa005?2:3);
                    if(tag==0x8769)node.Exif=child;else if(tag==0xa005)node.Interop=child;else node.Gps=child;
                }
                if(tag is 513 or 514)Require(role==4);
            }
            uint next=U32((int)(offset+table-4));
            if(next!=0){Require(role==0);node.Next=Read(next,4);}
            if(role==4)
            {
                var start=node.Entries.FirstOrDefault(e=>e.Tag==513);var size=node.Entries.FirstOrDefault(e=>e.Tag==514);
                // Invalid JPEG bounds withdraw only IFD1. Its table/values were still
                // strictly checked above, so a malformed directory never hides a cycle.
                if(!node.Entries.Any(e=>e.Tag is 273 or 279)&&start is not null&&size is not null&&start.Type==4&&start.Count==1&&size.Type==4&&size.Count==1)
                {
                    uint p=Scalar(start),n=Scalar(size);
                    if(p>=8&&n>=4&&(long)p+n<=bytes.Length&&bytes[p]==255&&bytes[p+1]==216&&bytes[p+n-2]==255&&bytes[p+n-1]==217)
                    {Area(p,n);node.Thumbnail=bytes.AsSpan((int)p,(int)n).ToArray();}
                }
            }
            return node;
        }
        var root=Read(U32(4),0);
        var ordered=ranges.OrderBy(r=>r.Start).ToArray();for(int i=1;i<ordered.Length;i++)Require(ordered[i].Start>=ordered[i-1].End);
        // The thumbnail (IFD1) is never kept: it can carry its own EXIF/XMP with GPS, or show
        // content cropped out of the main image. It was still parsed above for structure checks.
        root.Next=null;
        var nodes=new List<Ifd>();
        void Collect(Ifd node){nodes.Add(node);if(node.Exif is not null)Collect(node.Exif);if(node.Interop is not null)Collect(node.Interop);if(node.Next is not null)Collect(node.Next);}
        Collect(root);
        foreach(var node in nodes)node.Entries.RemoveAll(e=>e.Tag is 0x8825 or 0x927c or 700 or 33723 or 34377);
        int cursor=8;
        foreach(var node in nodes){node.Offset=cursor;cursor+=2+12*node.Entries.Count+4;}
        using var output=new MemoryStream();output.SetLength(cursor);output.Position=0;
        void W16(int n){Span<byte> b=stackalloc byte[2];if(little)BinaryPrimitives.WriteUInt16LittleEndian(b,(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)n);output.Write(b);}
        void W32(uint n){Span<byte> b=stackalloc byte[4];if(little)BinaryPrimitives.WriteUInt32LittleEndian(b,n);else BinaryPrimitives.WriteUInt32BigEndian(b,n);output.Write(b);}
        void WriteValue(byte[] value){for(int at=0;at<value.Length;){token.ThrowIfCancellationRequested();int n=Math.Min(65536,value.Length-at);output.Write(value.AsSpan(at,n));at+=n;}}
        void Pad(){if((cursor&1)!=0){output.Position=cursor++;output.WriteByte(0);}}
        output.Write(little?"II"u8:"MM"u8);W16(42);W32((uint)root.Offset);
        foreach(var node in nodes)
        {
            token.ThrowIfCancellationRequested();int thumb=0;
            if(node.Thumbnail is not null){Pad();thumb=cursor;output.Position=cursor;WriteValue(node.Thumbnail);cursor+=node.Thumbnail.Length;}
            output.Position=node.Offset;W16(node.Entries.Count);
            foreach(var e in node.Entries)
            {
                token.ThrowIfCancellationRequested();W16(e.Tag);W16(e.Type);W32(e.Count);
                if(e.Tag==0x8769)W32((uint)node.Exif!.Offset);
                else if(e.Tag==0xa005)W32((uint)node.Interop!.Offset);
                else if(e.Tag==513)W32((uint)thumb);
                else if(e.Tag==514)W32((uint)node.Thumbnail!.Length);
                else if(e.Value.Length<=4){output.Write(e.Value);for(int k=e.Value.Length;k<4;k++)output.WriteByte(0);}
                else
                {
                    long resume=output.Position;Pad();int valueAt=cursor;output.Position=cursor;WriteValue(e.Value);cursor+=e.Value.Length;output.Position=resume;W32((uint)valueAt);
                }
            }
            W32((uint)(node.Next?.Offset??0));
        }
        token.ThrowIfCancellationRequested();return output.ToArray();
    }
}
