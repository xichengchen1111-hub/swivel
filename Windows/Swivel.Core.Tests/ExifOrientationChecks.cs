using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class ExifOrientationChecks
{
    // Independent TIFF builder: IFD0 entries (tag,type,count,value) in either byte order,
    // optional trailing bytes standing in for out-of-line data.
    internal static byte[] Tiff(bool little,params (int Tag,int Type,uint Count,uint Value)[] entries)
    {
        var bytes=new List<byte>();void U16(int v){var b=new byte[2];if(little)BinaryPrimitives.WriteUInt16LittleEndian(b,(ushort)v);else BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)v);bytes.AddRange(b);}
        void U32(uint v){var b=new byte[4];if(little)BinaryPrimitives.WriteUInt32LittleEndian(b,v);else BinaryPrimitives.WriteUInt32BigEndian(b,v);bytes.AddRange(b);}
        bytes.AddRange(Encoding.ASCII.GetBytes(little?"II":"MM"));U16(42);U32(8);U16(entries.Length);
        foreach(var e in entries){U16(e.Tag);U16(e.Type);U32(e.Count);if(e.Type==3&&e.Count==1){U16((int)e.Value);U16(0);}else U32(e.Value);}
        U32(0);bytes.AddRange(Encoding.ASCII.GetBytes("Canon\0GPSDATA-PRIVATE"));return bytes.ToArray();
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(Convert.ToHexString(ExifOrientation.Tiff(6))=="4D4D002A000000080001011200030000000100060000"+"00000000","Minimal TIFF: MM, 42, IFD0 at 8, one SHORT×1 Orientation, no next IFD");
        for(int v=1;v<=8;v++){var info=ExifOrientation.Read(ExifOrientation.Tiff(v));check(info==new ExifOrientationInfo(ExifOrientationStatus.Found,v)&&info.CanKeep==(v!=1),"Written orientation reads back "+v);check(ExifOrientation.Describe(v).Length>0,"Description "+v);}
        foreach(bool little in new[]{true,false})
        {
            var full=Tiff(little,(0x010f,2,6,40),(0x0112,3,1,8),(0x8825,4,1,46),(0x0132,2,20,52));
            check(ExifOrientation.Read(full)==new ExifOrientationInfo(ExifOrientationStatus.Found,8),"Orientation among other IFD0 tags, byte order "+(little?"II":"MM"));
            check(ExifOrientation.Read(Tiff(little,(0x010f,2,6,40)))==ExifOrientationInfo.None,"No Orientation tag reads as None");
            foreach(var bad in new (int Tag,int Type,uint Count,uint Value)[]{(0x0112,3,1,0u),(0x0112,3,1,9u),(0x0112,4,1,6u),(0x0112,3,2,6u),(0x0112,9,1,6u)})
                check(ExifOrientation.Read(Tiff(little,bad))==ExifOrientationInfo.Malformed,"Out-of-range value, wrong type or count is Malformed");
            check(ExifOrientation.Read(Tiff(little,(0x0112,3,1,6),(0x0112,3,1,6)))==ExifOrientationInfo.Malformed,"Duplicate Orientation tags are Malformed");
            check(ExifOrientation.Read(Tiff(little))==ExifOrientationInfo.Malformed,"Empty IFD0 is Malformed");
            var plain=Tiff(little,(0x0112,3,1,3));int frame=8+2+12+4;
            for(int n=0;n<frame;n++)check(ExifOrientation.Read(plain.AsSpan(0,n))==ExifOrientationInfo.Malformed,"Every truncated TIFF prefix is Malformed");
            var offset=(byte[])plain.Clone();if(little)BinaryPrimitives.WriteUInt32LittleEndian(offset.AsSpan(4),(uint)offset.Length-1);else BinaryPrimitives.WriteUInt32BigEndian(offset.AsSpan(4),(uint)offset.Length-1);
            check(ExifOrientation.Read(offset)==ExifOrientationInfo.Malformed,"IFD0 offset past the block is Malformed");
            var low=(byte[])plain.Clone();low[little?4:7]=4;check(ExifOrientation.Read(low)==ExifOrientationInfo.Malformed,"IFD0 offset inside the header is Malformed");
            var count=(byte[])plain.Clone();count[little?8:9]=200;check(ExifOrientation.Read(count)==ExifOrientationInfo.Malformed,"Entry count beyond the block is Malformed");
        }
        foreach(string header in new[]{"IM","MI","II\0+","MM+\0","Exif"})check(ExifOrientation.Read(Encoding.ASCII.GetBytes(header).Concat(new byte[30]).ToArray())==ExifOrientationInfo.Malformed,"Bad byte order or magic is Malformed");
        var random=new Random(20261029);var seed=Tiff(true,(0x0112,3,1,6),(0x010f,2,6,40));
        for(int i=0;i<20000;i++){var m=(byte[])seed.Clone();for(int k=random.Next(1,4);k>0;k--)m[random.Next(m.Length)]=(byte)random.Next(256);var r=ExifOrientation.Read(m.AsSpan(0,random.Next(m.Length+1)));check(r.Status!=ExifOrientationStatus.Found||r.Value is >=1 and <=8,"Mutated TIFF never throws and never reports an out-of-range value");}
        reject(()=>ExifOrientation.Tiff(0),"Writer refuses value 0");reject(()=>ExifOrientation.Tiff(9),"Writer refuses value 9");

        // JPEG: exact replacement in place of the source EXIF APP1, every other byte unchanged.
        byte[] Exif(byte[] tiff)=>JpegMetadataChecks.Segment(0xe1,Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray());
        var data=new byte[]{1,2,255,0,3};var app0=JpegMetadataChecks.Segment(0xe0,new byte[]{1,2,3});var com=JpegMetadataChecks.Segment(0xfe,new byte[]{9});
        var xmp=JpegMetadataChecks.Segment(0xe1,Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x tiff:Orientation=\"6\"/>"));
        var exif=Exif(Tiff(true,(0x010f,2,6,40),(0x0112,3,1,6)));var filled=new byte[]{255,255}.Concat(exif.Skip(1)).ToArray();
        var jpeg=JpegMetadataChecks.Jpeg(app0,filled,xmp,JpegMetadataChecks.Frame(),com,JpegMetadataChecks.Scan(),data);
        var plan=JpegMetadata.Read(new MemoryStream(jpeg),default);
        check(plan.Orientation==new ExifOrientationInfo(ExifOrientationStatus.Found,6),"JPEG orientation read from the EXIF APP1 (little-endian), not XMP");
        byte[] Write(JpegMetadata.Plan p,bool keep){using var o=new MemoryStream();p.Write(o,keep,default);return o.ToArray();}
        var expectedJpeg=JpegMetadataChecks.Jpeg(app0,Exif(ExifOrientation.Tiff(6)),JpegMetadataChecks.Frame(),JpegMetadataChecks.Scan(),data);
        var kept=Write(plan,true);
        check(kept.SequenceEqual(expectedJpeg)&&kept.Length==plan.OutputBytesKeeping(true)&&JpegMetadata.OrientationSegment(6).SequenceEqual(Exif(ExifOrientation.Tiff(6))),"Kept JPEG = original bytes with only a minimal orientation EXIF APP1 where EXIF stood");
        check(Write(plan,false).SequenceEqual(JpegMetadataChecks.Clean(plan))&&plan.OutputBytesKeeping(false)==plan.OutputBytes,"Unchecked option is byte-identical to the existing cleanup");
        var again=JpegMetadata.Read(new MemoryStream(kept),default);check(again.Orientation==plan.Orientation&&again.RemovedCount==1&&again.RemovedBytes==JpegMetadata.OrientationSegmentLength,"Kept JPEG re-reads with the same orientation and only the minimal segment removable");
        foreach(var (pieces,status,label) in new[]{
            (new[]{xmp},ExifOrientationStatus.None,"XMP-only orientation is ignored"),
            (new[]{Exif(Tiff(false,(0x0112,3,1,1)))},ExifOrientationStatus.Found,"Orientation 1 is found but not keepable"),
            (new[]{Exif(Tiff(false,(0x0112,3,1,11)))},ExifOrientationStatus.Malformed,"Out-of-range EXIF value reported as Malformed"),
            (new[]{Exif(new byte[]{1,2,3})},ExifOrientationStatus.Malformed,"Damaged EXIF TIFF reported as Malformed"),
            (new[]{exif,Exif(ExifOrientation.Tiff(3))},ExifOrientationStatus.Ambiguous,"Two EXIF APP1 segments are Ambiguous")})
        {
            var p=JpegMetadata.Read(new MemoryStream(JpegMetadataChecks.Jpeg(pieces.Concat(new[]{JpegMetadataChecks.Frame(),JpegMetadataChecks.Scan(),data}).ToArray())),default);
            check(p.Orientation.Status==status&&!p.Orientation.CanKeep,label);reject(()=>Write(p,true),"Keep refused without a keepable orientation");
        }
        check(JpegMetadata.Read(new MemoryStream(JpegMetadataChecks.Jpeg(JpegMetadataChecks.Frame(),JpegMetadataChecks.Scan(),data)),default).Orientation==ExifOrientationInfo.None,"No APP1 means no orientation");

        // PNG: independent CRC oracle; eXIf kept in place before IDAT or moved just before the first IDAT.
        check(PngMetadata.OrientationChunk(8).SequenceEqual(PngMetadataChecks.Chunk("eXIf",ExifOrientation.Tiff(8))),"Minimal eXIf chunk length/type/payload/CRC match the independent CRC oracle");
        var idat=PngMetadataChecks.Chunk("IDAT",PngMetadataChecks.ImageData());var end=PngMetadataChecks.Chunk("IEND",Array.Empty<byte>());var text=PngMetadataChecks.Chunk("tEXt",Encoding.ASCII.GetBytes("a\0b"));var gama=PngMetadataChecks.Chunk("gAMA",new byte[]{0,0,177,143});
        var exifChunk=PngMetadataChecks.Chunk("eXIf",Tiff(false,(0x0112,3,1,8),(0x8825,4,1,30)));
        byte[] WritePng(PngMetadata.Plan p,bool keep){using var o=new MemoryStream();p.Write(o,keep,default);return o.ToArray();}
        var before=PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),gama,exifChunk,text,idat,end)),default);
        check(before.Orientation==new ExifOrientationInfo(ExifOrientationStatus.Found,8)&&WritePng(before,true).SequenceEqual(PngMetadataChecks.Png(PngMetadataChecks.Header(),gama,PngMetadata.OrientationChunk(8),idat,end)),"PNG eXIf before IDAT replaced in place, other chunks unchanged");
        check(WritePng(before,true).Length==before.OutputBytesKeeping(true)&&WritePng(before,false).SequenceEqual(PngMetadataChecks.Clean(before)),"PNG byte counts and unchecked identity");
        var keptPng=PngMetadata.Read(new MemoryStream(WritePng(before,true)),default);check(keptPng.Orientation==before.Orientation&&keptPng.RemovedCount==1,"Kept PNG re-reads with the same orientation");
        var after=PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),gama,idat,exifChunk,end)),default);
        check(WritePng(after,true).SequenceEqual(PngMetadataChecks.Png(PngMetadataChecks.Header(),gama,PngMetadata.OrientationChunk(8),idat,end)),"PNG eXIf after IDAT is written just before the first IDAT");
        check(PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),exifChunk,exifChunk,idat,end)),default).Orientation==ExifOrientationInfo.Ambiguous,"Two eXIf chunks are Ambiguous");
        var prefixed=PngMetadataChecks.Chunk("eXIf",Encoding.ASCII.GetBytes("Exif\0\0").Concat(ExifOrientation.Tiff(6)).ToArray());
        var prefixedPlan=PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),prefixed,idat,end)),default);
        check(prefixedPlan.Orientation==ExifOrientationInfo.Malformed,"Nonstandard Exif-prefixed eXIf payload is reported Malformed, not guessed");reject(()=>WritePng(prefixedPlan,true),"Keep refused for malformed PNG EXIF");
    }
}
