using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class JpegMetadataChecks
{
    internal static byte[] Segment(int marker,byte[] payload,int fill=0)
    {var bytes=new byte[payload.Length+4+fill];Array.Fill(bytes,(byte)255,0,fill+1);bytes[fill+1]=(byte)marker;BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(fill+2),(ushort)(payload.Length+2));payload.CopyTo(bytes,fill+4);return bytes;}
    internal static byte[] Frame(bool progressive=false,int width=3,int height=2,int precision=8)=>Segment(progressive?0xc2:0xc0,new[]{(byte)precision,(byte)(height>>8),(byte)height,(byte)(width>>8),(byte)width,(byte)1,(byte)1,(byte)0x11,(byte)0});
    internal static byte[] Scan(bool progressive=false,int ss=0,int se=-1,int approximation=0)=>Segment(0xda,new[]{(byte)1,(byte)1,(byte)0,(byte)ss,(byte)(se<0?(progressive?0:63):se),(byte)approximation});
    internal static byte[] Join(params byte[][] arrays)=>arrays.SelectMany(a=>a).ToArray();
    internal static byte[] Jpeg(params byte[][] pieces)=>Join(new[]{new byte[]{255,0xd8}}.Concat(pieces).Concat(new[]{new byte[]{255,0xd9}}).ToArray());
    internal static byte[] Clean(JpegMetadata.Plan plan,CancellationToken token=default){using var output=new MemoryStream();plan.Write(output,token);return output.ToArray();}
    internal static byte[] Icc(int sequence,int count)=>Segment(0xe2,Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new[]{(byte)sequence,(byte)count,(byte)9}).ToArray());
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        // These small opaque entropy fixtures test container structure only.
        // Native tests and independent external fixtures validate actual JPEG data.
        var data=new byte[]{1,2,255,0,3};var plain=Jpeg(Frame(),Scan(),data);
        var app0=Segment(0xe0,new byte[]{1,2,3});var app14=Segment(0xee,new byte[]{4,5,6});
        var remove=new[]{Segment(0xe1,Encoding.ASCII.GetBytes("EXIF_PRIVATE"),2),Segment(0xed,new byte[]{255,0xd9,255,0xda}),Segment(0xfe,new byte[]{0,1,255})};
        var decorated=Jpeg(app0,remove[0],Icc(2,2),Frame(),remove[1],Icc(1,2),app14,remove[2],Scan(),data);var expected=Jpeg(app0,Icc(2,2),Frame(),Icc(1,2),app14,Scan(),data);
        var plan=JpegMetadata.Read(new MemoryStream(decorated),default);
        check(plan.Width==3&&plan.Height==2&&!plan.Progressive&&plan.ScanCount==1&&plan.RemovedCount==3&&plan.RemovedBytes==remove.Sum(x=>x.Length),"JPEG review counts exact complete APP1/APP13/COM with fill bytes; payload marker-like bytes are opaque");
        check(Clean(plan).SequenceEqual(expected)&&plan.OutputBytes==expected.Length,"ICC out-of-order pieces, APP0/APP14 and stuffed compressed bytes kept exactly in original order");
        check(plan.MarkerCount==11&&plan.RestartCount==0&&plan.Segments.Single(s=>s.Marker==-1).Bytes==data.Length,"Marker totals and scan-byte summary exclude fill as extra markers");
        reject(()=>((IList<JpegMetadataSegment>)plan.Segments)[0]=new(0,0,0,false),"JPEG summary is readonly");Array.Fill(decorated,(byte)0);check(Clean(plan).SequenceEqual(expected),"JPEG plan owns snapshot independently of caller array");
        var noMatch=JpegMetadata.Read(new MemoryStream(plain),default);check(noMatch.RemovedCount==0&&Clean(noMatch).SequenceEqual(plain),"No-match core identity; native Save separately refuses");
        var restartData=new byte[]{1,255,0xd0,2,255,255,0xd1,3};var dri=Segment(0xdd,new byte[]{0,1});
        var progressive=Jpeg(Frame(true),dri,Scan(true),restartData,remove[0],Scan(true,1,63),new byte[]{3,255,0xd0,4});
        var progressivePlan=JpegMetadata.Read(new MemoryStream(progressive),default);
        check(progressivePlan.Progressive&&progressivePlan.ScanCount==2&&progressivePlan.RestartCount==3&&progressivePlan.MarkerCount==10,"Progressive restart order resets at each scan; DRI persists and fill before restart is preserved");
        check(Clean(progressivePlan).SequenceEqual(Jpeg(Frame(true),dri,Scan(true),restartData,Scan(true,1,63),new byte[]{3,255,0xd0,4})),"APP removed between scans while all other scan/marker bytes unchanged");
        var filledEnd=plain[..^2].Concat(new byte[]{255,255,255,0xd9}).ToArray();check(Clean(JpegMetadata.Read(new MemoryStream(filledEnd),default)).SequenceEqual(filledEnd),"Fill before EOI is retained as part of the real marker");
        foreach(var entropy in new[]{new byte[]{255,255,0,1},new byte[]{255},Array.Empty<byte>(),new byte[]{1,255,0xd0,2}})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),Scan(),entropy)),default),"Malformed stuffing/empty scan/restart without DRI refused");
        foreach(var entropy in new[]{new byte[]{1,255,0xd1,2},new byte[]{1,255,0xd0,2,255,0xd0,3}})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),dri,Scan(),entropy)),default),"Incorrect first/repeated restart index refused");
        reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),Segment(0xdd,new byte[]{0,0}),Scan(),restartData)),default),"DRI0 does not enable restarts");
        for(int length=0;length<plain.Length;length++){int n=length;reject(()=>JpegMetadata.Read(new MemoryStream(plain[..n]),default),"Every truncated JPEG prefix refused");}
        foreach(int marker in new[]{0xc1,0xc3,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xcc,0xce,0xcf,0xdc,0xde,0xdf,0xeb,0xf0,0x01,0xd8,0xd0})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Segment(marker,new byte[]{1}),Frame(),Scan(),data)),default),"Closed unsupported coding/APP11/standalone marker refusal");
        foreach(var icc in new[]{new[]{Icc(1,2)},new[]{Icc(1,2),Icc(1,2)},new[]{Icc(0,1)},new[]{Icc(2,1)},new[]{Icc(1,0)},new[]{Icc(1,2),Icc(2,3)},new[]{Segment(0xe2,Encoding.ASCII.GetBytes("MPF\0private"))}})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(icc.Concat(new[]{Frame(),Scan(),data}).ToArray())),default),"Incomplete/duplicate/inconsistent ICC or non-ICC APP2 refused");
        reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),Scan(),data,Icc(1,1))),default),"ICC must precede first scan");
        foreach(var frame in new[]{Frame(width:0),Frame(height:0),Frame(width:16385),Frame(precision:12),Frame(width:4001,height:4000),Segment(0xc0,new byte[]{8,0,2,0,3,4,1,17,0,2,17,0,3,17,0,4,17,0}),Segment(0xc0,new byte[]{8,0,2,0,3,1,1,0,0})})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(frame,Scan(),data)),default),"Unsupported dimensions/precision/CMYK/sampling refused");
        foreach(var sos in new[]{Segment(0xda,new byte[]{1,2,0,0,63,0}),Segment(0xda,new byte[]{2,1,0,1,0,0,63,0}),Scan(se:62),Scan(approximation:1)})
            reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),sos,data)),default),"Invalid component selection, duplicate IDs and baseline spectral/approximation fields refused");
        foreach(var sos in new[]{Scan(true,se:63),Scan(true,ss:64,se:64),Scan(true,ss:2,se:1),Scan(true,approximation:0xe0),Scan(true,approximation:0x21)})
        {
            bool valid=sos.SequenceEqual(Scan(true,approximation:0x21));
            if(valid)check(JpegMetadata.Read(new MemoryStream(Jpeg(Frame(true),sos,data)),default).Progressive,"Valid progressive refinement field structure accepted, full progression remains native validation");
            else reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(true),sos,data)),default),"Invalid progressive fields refused");
        }
        reject(()=>JpegMetadata.Read(new MemoryStream(plain.Concat(new byte[]{0}).ToArray()),default),"No trailing bytes after EOI");
        reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(Frame(),Frame(),Scan(),data)),default),"Duplicate frame refused");
        var truncatedLength=Join(new byte[]{255,0xd8,255,0xe1,255,255},plain);reject(()=>JpegMetadata.Read(new MemoryStream(truncatedLength),default),"Declared payload length cannot run beyond bounded snapshot");
        var scans=new List<byte[]>{Frame(true)};for(int i=0;i<JpegMetadata.MaxScans;i++){scans.Add(Scan(true));scans.Add(new byte[]{1});}
        check(JpegMetadata.Read(new MemoryStream(Jpeg(scans.ToArray())),default).ScanCount==512,"Exact512 structural scan budget accepted");scans.Add(Scan(true));scans.Add(new byte[]{1});reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(scans.ToArray())),default),"513 scans refused");
        var markers=new List<byte[]>{Frame(),Scan(),data};for(int i=0;i<JpegMetadata.MaxMarkers-4;i++)markers.Add(Segment(0xfe,Array.Empty<byte>()));
        check(JpegMetadata.Read(new MemoryStream(Jpeg(markers.ToArray())),default).MarkerCount==65536,"Marker budget includes SOI/frame/SOS/EOI");markers.Add(Segment(0xfe,Array.Empty<byte>()));reject(()=>JpegMetadata.Read(new MemoryStream(Jpeg(markers.ToArray())),default),"65537 markers refused");
        var exact=Join(plain[..^2],Enumerable.Repeat((byte)255,JpegMetadata.MaxBytes-plain.Length).ToArray(),plain[^2..]);check(JpegMetadata.Read(new MemoryStream(exact),default).SourceBytes==JpegMetadata.MaxBytes,"Exact32MiB snapshot including long fill run accepted with byte-page cancellation points");
        reject(()=>JpegMetadata.Read(new MemoryStream(new byte[JpegMetadata.MaxBytes+1]),default),"32MiB+1 refused before reading source");
        reject(()=>JpegMetadata.Read(new MemoryStream(plain),new CancellationToken(true)),"Pre-cancel prevents JPEG preparation");using(var output=new MemoryStream()){reject(()=>plan.Write(output,new CancellationToken(true)),"Pre-cancel prevents JPEG output");check(output.Length==0,"Pre-cancel writes zero bytes");}
        check(Clean(JpegMetadata.Read(new TinyReads(plain),default)).SequenceEqual(plain),"Short reads are fully collected before structural parse");
        var prefs=new Prefs();var action=Catalog.Available(prefs,new[]{@"C:\a.jpeg"}).Single(a=>a.Id=="image.jpegmetadata");check(action.SingleFile&&Catalog.Available(prefs,new[]{@"C:\a.JPG"}).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,new[]{@"C:\a.png"}).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,new[]{@"C:\a.jpg",@"C:\b.jpg"}).Any(a=>a.Id==action.Id),"JPEG action requires exactly one explicit jpg/jpeg");
        prefs.SetCustomActions(new[]{action.Id});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()==action.Id,"Stable JPEG action settings roundtrip");
    }
    private sealed class TinyReads(byte[] bytes):MemoryStream(bytes){public override int Read(Span<byte> target)=>base.Read(target[..Math.Min(3,target.Length)]);}
}
