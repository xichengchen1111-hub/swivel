using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class ExifLocationChecks
{
    // Independent fixture writer with deliberately sparse tables and data, rather
    // than the production compact layout. Values cover every TIFF 6.0 type.
    private static byte[] Fixture(bool le)
    {
        var b=new byte[4096];int data=1800;
        void S(int at,int n){if(le)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at),(ushort)n);}
        void L(int at,uint n){if(le)BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),n);else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at),n);}
        byte[] Long(uint n){var v=new byte[4];if(le)BinaryPrimitives.WriteUInt32LittleEndian(v,n);else BinaryPrimitives.WriteUInt32BigEndian(v,n);return v;}
        byte[] Short(int n){var v=new byte[2];if(le)BinaryPrimitives.WriteUInt16LittleEndian(v,(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(v,(ushort)n);return v;}
        void Ifd(int at,int next,params (int Tag,int Type,uint Count,byte[] Value)[] es)
        {
            S(at,es.Length);int p=at+2;
            foreach(var e in es.OrderBy(e=>e.Tag))
            {
                S(p,e.Tag);S(p+2,e.Type);L(p+4,e.Count);
                if(e.Value.Length<=4)e.Value.CopyTo(b,p+8);
                else{L(p+8,(uint)data);e.Value.CopyTo(b,data);data+=e.Value.Length+3;data+=data&1;}
                p+=12;
            }
            L(p,(uint)next);
        }
        b[0]=b[1]=(byte)(le?'I':'M');S(2,42);L(4,16);
        var root=new List<(int,int,uint,byte[])>{(0x010f,2,7,Encoding.ASCII.GetBytes("Camera\0")),(0x0112,3,1,Short(6)),(0x8769,4,1,Long(600)),(0x8825,4,1,Long(900))};
        int[] sizes={0,1,1,2,4,8,1,1,2,4,8,4,8};
        for(int type=1;type<=12;type++)root.Add((0xc000+type,type,3,Enumerable.Range(0,3*sizes[type]).Select(i=>(byte)(type*11+i)).ToArray()));
        Ifd(16,1200,root.ToArray());
        Ifd(600,0,(0x9000,7,4,"0232"u8.ToArray()),(0x927c,7,12,"privateGPS!!"u8.ToArray()),(0xa005,4,1,Long(750)));
        Ifd(750,0,(1,2,4,"R98\0"u8.ToArray()),(2,7,4,"0100"u8.ToArray()));
        Ifd(900,0,(0,1,4,new byte[]{2,3,0,0}),(1,2,2,"N\0"u8.ToArray()),(2,5,3,Enumerable.Range(0,24).Select(i=>(byte)i).ToArray()));
        Ifd(1200,0,(0x0100,4,1,Long(1)),(0x0103,3,1,Short(6)),(513,4,1,Long(3000)),(514,4,1,Long(6)));
        new byte[]{255,216,1,2,255,217}.CopyTo(b,3000);
        return b;
    }
    // Independent reader/oracle for fixture and output; physical offsets are not
    // compared, but tag/type/count and exact value bytes are, including numerics.
    private static Dictionary<string,string> Values(byte[] b)
    {
        bool le=b[0]=='I';int S(int p)=>le?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)):BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
        uint L(int p)=>le?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)):BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
        var result=new Dictionary<string,string>();int[] sizes={0,1,1,2,4,8,1,1,2,4,8,4,8};
        void Read(int at,string name)
        {
            int count=S(at);for(int i=0;i<count;i++)
            {
                int p=at+2+12*i,tag=S(p),type=S(p+2);uint n=L(p+4);int len=checked((int)n*sizes[type]);int v=len<=4?p+8:(int)L(p+8);
                if(tag is 0x8769 or 0x8825 or 0xa005){Read((int)L(v),name+"/"+tag);continue;}
                if(tag==513){result[name+"/thumbnail"]=Convert.ToHexString(b.AsSpan((int)L(v),6));continue;}
                result[name+"/"+tag]=type+":"+n+":"+Convert.ToHexString(b.AsSpan(v,len));
            }
            int next=(int)L(at+2+12*count);if(next!=0)Read(next,"IFD1");
        }
        Read((int)L(4),"IFD0");return result;
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Exif(byte[] b)=>JpegMetadataChecks.Segment(0xe1,"Exif\0\0"u8.ToArray().Concat(b).ToArray());
        var app0=JpegMetadataChecks.Segment(0xe0,new byte[]{1,2,3});var com=JpegMetadataChecks.Segment(0xfe,"keep comment"u8.ToArray());
        var other=JpegMetadataChecks.Segment(0xe1,"other APP1"u8.ToArray());var xmp=JpegMetadataChecks.Segment(0xe1,"http://ns.adobe.com/xap/1.0/\0exif:GPSLatitude"u8.ToArray());
        var ext=JpegMetadataChecks.Segment(0xe1,"http://ns.adobe.com/xmp/extension/\0location"u8.ToArray());var iptc=JpegMetadataChecks.Segment(0xed,"location"u8.ToArray());
        var frame=JpegMetadataChecks.Frame();var scan=JpegMetadataChecks.Scan();var entropy=new byte[]{1,2,255,0,3};
        var idat=PngMetadataChecks.Chunk("IDAT",PngMetadataChecks.ImageData());var end=PngMetadataChecks.Chunk("IEND",Array.Empty<byte>());
        var text=PngMetadataChecks.Chunk("tEXt","note\0keep"u8.ToArray());var itxt=PngMetadataChecks.Chunk("iTXt","note\0\0\0\0\0keep"u8.ToArray());
        var xml=PngMetadataChecks.Chunk("iTXt","XML:com.adobe.xmp\0\0\0\0\0GPS"u8.ToArray());var zippedXml=PngMetadataChecks.Chunk("iTXt","XML:com.adobe.xmp\0\x01\0\0\0compressed"u8.ToArray());
        var time=PngMetadataChecks.Chunk("tIME",new byte[]{7,234,10,30,1,2,3});
        foreach(bool le in new[]{false,true})
        {
            var source=Fixture(le);var rebuilt=ExifLocation.Rebuild(source,default,out var reason)!;
            check(rebuilt is not null&&reason is null&&rebuilt[0]==source[0],"Location EXIF same byte order, available "+le);
            var expected=Values(source).Where(v=>!v.Key.StartsWith("IFD0/34853/")&&!v.Key.EndsWith("/37500")&&!v.Key.StartsWith("IFD1/")).ToDictionary(v=>v.Key,v=>v.Value);
            var actual=Values(rebuilt!);check(actual.Count==expected.Count,"All retained EXIF entries preserved, GPS/MakerNote/thumbnail omitted");
            foreach(var pair in expected)check(actual.TryGetValue(pair.Key,out var value)&&value==pair.Value,"Exact retained type/count/value: "+pair.Key+" "+le);
            check(!actual.Keys.Any(k=>k.StartsWith("IFD0/34853/")||k.EndsWith("/37500")||k.StartsWith("IFD1/")),"GPS, MakerNote and the thumbnail absent");
            check(ExifOrientation.Read(rebuilt!)==new ExifOrientationInfo(ExifOrientationStatus.Found,6),"Orientation retained");
            check(ExifLocation.Rebuild(rebuilt!,default,out _)!.SequenceEqual(rebuilt!),"Compact EXIF parses and rebuilds identically");
            var jpeg=JpegMetadataChecks.Jpeg(app0,Exif(source),xmp,ext,iptc,other,com,frame,scan,entropy);
            var jp=JpegMetadata.Read(new MemoryStream(jpeg),default);using var jo=new MemoryStream();jp.WriteLocationOnly(jo,default);
            var expectedJpeg=JpegMetadataChecks.Jpeg(app0,Exif(rebuilt!),other,com,frame,scan,entropy);
            check(jp.CanRemoveLocation&&jo.ToArray().SequenceEqual(expectedJpeg),"JPEG exact retained segments and entropy; XMP/extended XMP/IPTC gone");
            check(JpegMetadata.Read(new MemoryStream(jo.ToArray()),default).CanRemoveLocation,"Location JPEG parses");
            var cleaned=JpegMetadataChecks.Jpeg(app0,frame,scan,entropy);check(JpegMetadataChecks.Clean(jp).SequenceEqual(cleaned),"Full JPEG removal unchanged");
            var chunk=PngMetadataChecks.Chunk("eXIf",source);
            var rawExif=PngMetadataChecks.Chunk("zTXt","Raw profile type exif\0\0x"u8.ToArray());var rawXmp=PngMetadataChecks.Chunk("tEXt","Raw profile type xmp\0\n xmp\n"u8.ToArray());var textXml=PngMetadataChecks.Chunk("tEXt","XML:com.adobe.xmp\0GPS"u8.ToArray());
            var png=PngMetadataChecks.Png(PngMetadataChecks.Header(),chunk,text,itxt,xml,zippedXml,rawExif,rawXmp,textXml,time,idat,end);
            var pp=PngMetadata.Read(new MemoryStream(png),default);using var po=new MemoryStream();pp.WriteLocationOnly(po,default);
            var expectedPng=PngMetadataChecks.Png(PngMetadataChecks.Header(),PngMetadataChecks.Chunk("eXIf",rebuilt!),text,itxt,time,idat,end);
            check(pp.CanRemoveLocation&&po.ToArray().SequenceEqual(expectedPng),"PNG exact retained chunks/CRCs/IDAT/text/time; XMP in iTXt/tEXt and Raw profile copies removed");
            check(PngMetadata.Read(new MemoryStream(po.ToArray()),default).CanRemoveLocation,"Location PNG parses");
            check(PngMetadataChecks.Clean(pp).SequenceEqual(PngMetadataChecks.Png(PngMetadataChecks.Header(),idat,end)),"Full PNG removal unchanged");
            void Set(byte[] b,int at,uint n){if(le)BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),n);else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at),n);}
            var mutations=new List<byte[]>();
            void Bad(int at,uint n){var b=(byte[])source.Clone();Set(b,at,n);mutations.Add(b);}
            // Root's sorted entries: Make, Orientation, Exif pointer, GPS pointer, private types.
            Bad(16+2+2*12+8,16); // Exif -> root cycle
            Bad(16+2+8,99999); // Make outside source
            Bad(16+2+4,uint.MaxValue); // huge Make count
            Bad(16+2+4*12+2,0); // unknown type / zero count
            Bad(16+2+6*12+8,1800); // second out-of-line value overlaps Make
            Bad(600+2+2*12+8,600); // Interop -> Exif cycle
            Bad(16+2+3*12+8,99998); // GPS offset outside block
            Bad(16+2+8,16); // Make value overlaps IFD0 table
            Bad(1200+2+4*12,1200); // IFD1 next cycle
            var duplicateTag=(byte[])source.Clone();
            if(le)BinaryPrimitives.WriteUInt16LittleEndian(duplicateTag.AsSpan(16+2+12),0x010f);else BinaryPrimitives.WriteUInt16BigEndian(duplicateTag.AsSpan(16+2+12),0x010f);
            mutations.Add(duplicateTag);
            foreach(var bad in mutations)
            {
                check(ExifLocation.Rebuild(bad,default,out var why) is null&&!string.IsNullOrEmpty(why),"Malformed EXIF unavailable with reason "+le);
                var j=JpegMetadata.Read(new MemoryStream(JpegMetadataChecks.Jpeg(app0,Exif(bad),frame,scan,entropy)),default);
                check(!j.CanRemoveLocation&&!string.IsNullOrEmpty(j.LocationUnavailableReason)&&JpegMetadataChecks.Clean(j).SequenceEqual(cleaned),"Malformed JPEG EXIF still supports full removal");
                var p=PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),PngMetadataChecks.Chunk("eXIf",bad),idat,end)),default);
                check(!p.CanRemoveLocation&&!string.IsNullOrEmpty(p.LocationUnavailableReason)&&PngMetadataChecks.Clean(p).SequenceEqual(PngMetadataChecks.Png(PngMetadataChecks.Header(),idat,end)),"Malformed PNG EXIF still supports full removal");
                reject(()=>j.WriteLocationOnly(new MemoryStream(),default),"Unavailable location write refused");
            }
            var invalidThumb=(byte[])source.Clone();Set(invalidThumb,1200+2+2*12+8,99999);
            var noThumb=ExifLocation.Rebuild(invalidThumb,default,out reason);check(noThumb is not null&&reason is null&&!Values(noThumb).Keys.Any(k=>k.StartsWith("IFD1/")),"Invalid thumbnail bounds still leave a rebuilt EXIF without IFD1");
            var nonJpeg=(byte[])source.Clone();
            if(le){BinaryPrimitives.WriteUInt16LittleEndian(nonJpeg.AsSpan(1200+2+2*12),273);BinaryPrimitives.WriteUInt16LittleEndian(nonJpeg.AsSpan(1200+2+3*12),279);}
            else{BinaryPrimitives.WriteUInt16BigEndian(nonJpeg.AsSpan(1200+2+2*12),273);BinaryPrimitives.WriteUInt16BigEndian(nonJpeg.AsSpan(1200+2+3*12),279);}
            var droppedRaster=ExifLocation.Rebuild(nonJpeg,default,out reason);check(droppedRaster is not null&&reason is null&&!Values(droppedRaster).Keys.Any(k=>k.StartsWith("IFD1/")),"Non-JPEG IFD1 is omitted, never copied as an opaque thumbnail");
            var duplicateJ=JpegMetadata.Read(new MemoryStream(JpegMetadataChecks.Jpeg(Exif(source),Exif(source),frame,scan,entropy)),default);
            check(!duplicateJ.CanRemoveLocation&&duplicateJ.LocationUnavailableReason==ExifLocation.Missing&&JpegMetadataChecks.Clean(duplicateJ).SequenceEqual(JpegMetadataChecks.Jpeg(frame,scan,entropy)),"Duplicate JPEG EXIF disables only location");
            var duplicateP=PngMetadata.Read(new MemoryStream(PngMetadataChecks.Png(PngMetadataChecks.Header(),chunk,chunk,idat,end)),default);
            check(!duplicateP.CanRemoveLocation&&duplicateP.LocationUnavailableReason==ExifLocation.Missing&&PngMetadataChecks.Clean(duplicateP).SequenceEqual(PngMetadataChecks.Png(PngMetadataChecks.Header(),idat,end)),"Duplicate PNG EXIF disables only location");
            // XMP (700), IPTC (33723) and Photoshop (34377) stored inside EXIF are removed too.
            var embedded=new byte[64];
            void P16(int at,int n){if(le)BinaryPrimitives.WriteUInt16LittleEndian(embedded.AsSpan(at),(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(embedded.AsSpan(at),(ushort)n);}
            embedded[0]=embedded[1]=(byte)(le?'I':'M');P16(2,42);Set(embedded,4,8);P16(8,3);
            P16(10,0x0112);P16(12,3);Set(embedded,14,1);P16(18,6);
            P16(22,700);P16(24,1);Set(embedded,26,8);Set(embedded,30,50);
            P16(34,33723);P16(36,1);Set(embedded,38,4);"IPTC"u8.CopyTo(embedded.AsSpan(42));
            Set(embedded,46,0);"GPS=48N!"u8.CopyTo(embedded.AsSpan(50));
            var noEmbedded=ExifLocation.Rebuild(embedded,default,out reason);
            check(noEmbedded is not null&&reason is null&&!Values(noEmbedded).Keys.Any(k=>k.EndsWith("/700")||k.EndsWith("/33723"))&&ExifOrientation.Read(noEmbedded)==new ExifOrientationInfo(ExifOrientationStatus.Found,6),"XMP/IPTC tags inside EXIF removed, orientation kept "+le);
            var random=new Random(711);for(int i=0;i<1000;i++){var b=(byte[])source.Clone();b[random.Next(b.Length)]=(byte)random.Next(256);ExifLocation.Rebuild(b,default,out _);}
            check(true,"1000 EXIF mutations do not escape parser error boundary "+le);
        }
        check(ExifLocation.Rebuild(Array.Empty<byte>(),default,out var missing) is null&&missing is not null,"Empty EXIF refused");
        using var cancel=new CancellationTokenSource();cancel.Cancel();reject(()=>ExifLocation.Rebuild(Fixture(true),cancel.Token,out _),"EXIF parser honors cancellation");
    }
}
