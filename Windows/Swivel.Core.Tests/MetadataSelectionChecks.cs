using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Swivel.Core;

internal static class MetadataSelectionChecks
{
    internal sealed record Value(int Tag,int Type,uint Count,byte[] Bytes,string Group);
    internal sealed record Parsed(Dictionary<string,Value> Values,Dictionary<string,byte[]> Pixels);
    private static readonly int[] Sizes={0,1,1,2,4,8,1,1,2,4,8,4,8};
    private static readonly string[] Groups={"Location","Camera and device","Dates","Author and copyright","Comments and descriptions","Software","Thumbnail","Maker note","XMP","IPTC","PNG text"};
    // A sparse independent fixture writer, never a production metadata writer.
    internal static byte[] Tiff(bool little,bool image,int pages=1,byte[]? thumbnail=null)
    {
        var b=new byte[24576];int data=12000;
        void S(int p,int n){if(little)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p),(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p),(ushort)n);}
        void L(int p,uint n){if(little)BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),n);else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p),n);}
        byte[] Short(int n){var a=new byte[2];if(little)BinaryPrimitives.WriteUInt16LittleEndian(a,(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(a,(ushort)n);return a;}
        byte[] Long(int n){var a=new byte[4];if(little)BinaryPrimitives.WriteUInt32LittleEndian(a,(uint)n);else BinaryPrimitives.WriteUInt32BigEndian(a,(uint)n);return a;}
        (int,int,uint,byte[]) Text(int tag,string value)=>(tag,2,(uint)value.Length+1,Encoding.ASCII.GetBytes(value+"\0"));
        void Ifd(int at,int next,IEnumerable<(int Tag,int Type,uint Count,byte[] Raw)> entries)
        {
            var es=entries.OrderBy(e=>e.Tag).ToArray();S(at,es.Length);int p=at+2;
            foreach(var e in es)
            {
                S(p,e.Tag);S(p+2,e.Type);L(p+4,e.Count);
                if(e.Raw.Length<=4)e.Raw.CopyTo(b,p+8);
                else{L(p+8,(uint)data);e.Raw.CopyTo(b,data);data+=e.Raw.Length;data+=data&1;}
                p+=12;
            }
            L(p,(uint)next);
        }
        b[0]=b[1]=(byte)(little?'I':'M');S(2,42);L(4,16);
        for(int page=0;page<pages;page++)
        {
            int root=16+page*2048,exif=512+page*2048,gps=768+page*2048,thumb=1024+page*2048;
            var xmp=Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:sel=\"urn:swivel:test\" sel:marker=\"SEL_XMP___"+page+"\"/></rdf:RDF></x:xmpmeta>");
            var iptcText=Encoding.ASCII.GetBytes("SEL_IPTC___"+page);var iptc=new byte[]{0x1c,2,5,0,(byte)iptcText.Length}.Concat(iptcText).ToArray();
            var entries=new List<(int,int,uint,byte[])>{Text(270,"SEL_COMMENT_"+page),Text(271,"SEL_CAMERA_"+page),Text(272,"SEL_MODEL_"+page),(274,3,1,Short(6)),Text(305,"SEL_SOFTWARE_"+page),Text(306,"2026:11:01 01:02:0"+page),Text(315,"SEL_AUTHOR_"+page),(700,1,(uint)xmp.Length,xmp),Text(33432,"SEL_COPYRIGHT_"+page),(33723,7,(uint)iptc.Length,iptc),(34665,4,1,Long(exif)),(34853,4,1,Long(gps))};
            if(image)
            {
                int pixel=22000+page*64;new byte[]{11,(byte)(31+page),51,71,91,111}.CopyTo(b,pixel);
                entries.AddRange(new[]{(256,3,1u,Short(3)),(257,3,1u,Short(2)),(258,3,1u,Short(8)),(259,3,1u,Short(1)),(262,3,1u,Short(1)),(273,4,1u,Long(pixel)),(277,3,1u,Short(1)),(278,3,1u,Short(2)),(279,4,1u,Long(6))});
                var icc=TiffMetadataChecks.Icc();entries.Add((34675,7,(uint)icc.Length,icc));
            }
            Ifd(root,image?(page+1<pages?root+2048:0):thumb,entries);
            Ifd(exif,0,new[]{Text(36867,"2025:09:03 04:05:0"+page),(37500,7,11u,Encoding.ASCII.GetBytes("SEL_MAKER_"+page)),(37510,7,22u,Encoding.ASCII.GetBytes("ASCII\0\0\0SEL_USERCOM_"+page+"!")),Text(42033,"SEL_SERIAL_"+page)});
            Ifd(gps,0,new[]{(0,1,4u,new byte[]{2,3,0,0}),(1,2,2u,new byte[]{78,0}),(27,7,21u,Encoding.ASCII.GetBytes("ASCII\0\0\0SEL_LOCATION"+page))});
            if(!image)
            {
                int payload=21000;var jpg=thumbnail??new byte[]{255,216}.Concat(Encoding.ASCII.GetBytes("SEL_THUMBNAIL_"+page)).Concat(new byte[]{255,217}).ToArray();jpg.CopyTo(b,payload);
                Ifd(thumb,0,new[]{(256,3,1u,Short(1)),(257,3,1u,Short(1)),(259,3,1u,Short(6)),(513,4,1u,Long(payload)),(514,4,1u,Long(jpg.Length))});
            }
        }
        return b;
    }
    // Independent readback oracle: no MetadataSelection types or classification helpers.
    internal static Parsed ParseTiff(byte[] b,bool image)
    {
        bool little=b[0]=='I';int S(int p)=>little?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)):BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
        uint L(int p)=>little?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)):BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
        var values=new Dictionary<string,Value>();var pixels=new Dictionary<string,byte[]>();var seen=new HashSet<int>();
        void Ifd(int at,string directory,bool thumbnail=false,bool gps=false)
        {
            if(!seen.Add(at))throw new Exception("Readback directory cycle");
            int count=S(at);var scalars=new Dictionary<int,uint>();
            for(int i=0;i<count;i++)
            {
                int p=at+2+12*i,tag=S(p),type=S(p+2);uint n=L(p+4);int length=checked((int)n*Sizes[type]),offset=length<=4?p+8:(int)L(p+8);var raw=b.AsSpan(offset,length).ToArray();
                if(tag is 34665 or 34853 or 40965){Ifd((int)L(offset),directory+"/"+tag,false,tag==34853);continue;}
                if(tag is 273 or 279 or 513 or 514){scalars[tag]=type==3?(uint)S(offset):L(offset);continue;}
                string group=thumbnail?"Thumbnail":gps?"Location":tag switch{271 or 272 or 42033=>"Camera and device",306 or 36867=>"Dates",315 or 33432=>"Author and copyright",270 or 37510=>"Comments and descriptions",305=>"Software",37500=>"Maker note",700=>"XMP",33723=>"IPTC",_=>"Rendering"};
                values[directory+"/"+tag]=new(tag,type,n,raw,group);
            }
            if(scalars.TryGetValue(273,out uint strip))pixels[directory]=b.AsSpan((int)strip,(int)scalars[279]).ToArray();
            if(scalars.TryGetValue(513,out uint jpeg))values[directory+"/thumbnail"]=new(513,7,scalars[514],b.AsSpan((int)jpeg,(int)scalars[514]).ToArray(),"Thumbnail");
            int next=(int)L(at+2+12*count);if(next!=0)Ifd(next,image?"page"+(int.Parse(directory[4..])+1):"thumbnail",!image);
        }
        Ifd((int)L(4),"page0");return new(values,pixels);
    }
    private static Dictionary<string,List<byte[]>> PngChunks(byte[] b)
    {
        var result=new Dictionary<string,List<byte[]>>();for(int at=8;at<b.Length;)
        {int n=(int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));string type=Encoding.ASCII.GetString(b,at+4,4);if(!result.TryGetValue(type,out var list))result[type]=list=new();list.Add(b.AsSpan(at+8,n).ToArray());at+=12+n;}return result;
    }
    private static byte[] JpegExif(byte[] b)
    {
        for(int p=2;p<b.Length;){while(b[p]==255)p++;int marker=b[p++];if(marker==0xda)break;int size=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));if(marker==0xe1&&b.AsSpan(p+2,size-2).StartsWith("Exif\0\0"u8))return b.AsSpan(p+8,size-8).ToArray();p+=size;}
        throw new Exception("Readback EXIF missing");
    }
    private static bool Contains(byte[] whole,byte[] value)=>whole.AsSpan().IndexOf(value)>=0;
    private static byte[] Written(MetadataSelection plan,int[] ids,string folder,string name)
    {
        string path=Path.Combine(folder,name);using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))plan.Write(output,ids,default);
        return File.ReadAllBytes(path);
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        int count=0;void Check(bool ok,string name){check(ok,"Selective metadata: "+name);count++;}
        void Reject(Action action,string name){reject(action,"Selective metadata: "+name);count++;}
        string folder=Path.Combine(Path.GetTempPath(),"swivel-selection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            foreach(bool little in new[]{true,false})foreach(string format in new[]{"jpeg","png","tiff"})
            {
                var tiff=Tiff(little,format=="tiff",format=="tiff"?2:1);var before=ParseTiff(tiff,format=="tiff");
                foreach(var packet in before.Values.Values.Where(v=>v.Tag==700))
                    Check(XDocument.Parse(Encoding.UTF8.GetString(packet.Bytes)).Root?.Name==XName.Get("xmpmeta","adobe:ns:meta/"),format+" fixture has well-formed XMP for native readers");
                foreach(var packet in before.Values.Values.Where(v=>v.Tag==33723))
                    Check(packet.Bytes.AsSpan(0,3).SequenceEqual(new byte[]{0x1c,2,5})&&BinaryPrimitives.ReadUInt16BigEndian(packet.Bytes.AsSpan(3))==packet.Bytes.Length-5,format+" fixture has a complete IPTC dataset for native readers");
                var entropy=new byte[]{1,2,255,0,3};var frame=JpegMetadataChecks.Frame();var scan=JpegMetadataChecks.Scan();var icc=JpegMetadataChecks.Icc(1,1);
                var outerXmp=JpegMetadataChecks.Segment(0xe1,"http://ns.adobe.com/xap/1.0/\0OUTER_XMP_MARKER"u8.ToArray());var outerIptc=JpegMetadataChecks.Segment(0xed,"OUTER_IPTC_MARKER"u8.ToArray());
                var exif=JpegMetadataChecks.Segment(0xe1,"Exif\0\0"u8.ToArray().Concat(tiff).ToArray());var comment=JpegMetadataChecks.Segment(0xfe,"OUTER_COMMENT_MARKER"u8.ToArray());
                var idat=PngMetadataChecks.Chunk("IDAT",PngMetadataChecks.ImageData());var end=PngMetadataChecks.Chunk("IEND",Array.Empty<byte>());
                var text=PngMetadataChecks.Chunk("tEXt","Title\0OUTER_PNG_TEXT_MARKER"u8.ToArray());var pngXmp=PngMetadataChecks.Chunk("iTXt","XML:com.adobe.xmp\0\0\0\0\0OUTER_XMP_MARKER"u8.ToArray());
                var pngIcc=PngMetadataChecks.Chunk("iCCP",new byte[]{65,0,0,120,156,3,0,0,0,0,1});
                byte[] source=format switch{"jpeg"=>JpegMetadataChecks.Jpeg(exif,outerXmp,outerIptc,comment,icc,frame,scan,entropy),"png"=>PngMetadataChecks.Png(PngMetadataChecks.Header(),PngMetadataChecks.Chunk("eXIf",tiff),text,pngXmp,pngIcc,idat,end),_=>tiff};
                MetadataSelection plan=format switch{"jpeg"=>JpegMetadata.Read(new MemoryStream(source),default).Selective(default),"png"=>PngMetadata.Read(new MemoryStream(source),default).Selective(default),_=>TiffMetadata.Read(new MemoryStream(source),default).Selective(default)};
                Check(plan.Items.Select(f=>(f.Directory,f.Name)).Distinct().Count()==plan.Items.Count,format+" directory/page and tag distinguish every occurrence");
                Check(!plan.MayDuplicate(Array.Empty<int>()),format+" empty selection has no duplicate-value warning");
                using(var empty=new MemoryStream()){Reject(()=>plan.Write(empty,Array.Empty<int>(),default),"Empty selective save refused");Check(empty.Length==0,"Empty selection writes no bytes");}
                var structural=plan.Items.First(i=>!i.CanRemove);Reject(()=>plan.Write(new MemoryStream(),new[]{structural.Id},default),"Pixel/ICC/structural removal refused");
                foreach(string group in Groups)
                {
                    var ids=plan.Items.Where(i=>i.CanRemove&&i.Group==group).Select(i=>i.Id).ToArray();if(ids.Length==0){Check(!before.Values.Values.Any(v=>v.Group==group),format+" inventories group "+group);continue;}
                    byte[] output=Written(plan,ids,folder,format+little+group.Replace(" ","")+".bin");byte[] body=format switch{"jpeg"=>JpegExif(output),"png"=>PngChunks(output)["eXIf"].Single(),_=>output};var after=ParseTiff(body,format=="tiff");
                    foreach(var pair in before.Values)
                    {
                        if(pair.Value.Group==group)
                        {
                            Check(!after.Values.ContainsKey(pair.Key),format+" removed tag "+pair.Key+" in "+group);
                            if(pair.Value.Bytes.Length>=8)Check(!Contains(output,pair.Value.Bytes),format+" selected value absent from whole file "+pair.Key);
                        }
                        else Check(after.Values.TryGetValue(pair.Key,out var actual)&&actual.Tag==pair.Value.Tag&&actual.Type==pair.Value.Type&&actual.Count==pair.Value.Count&&actual.Bytes.SequenceEqual(pair.Value.Bytes),format+" retained exact type/count/value "+pair.Key+" when removing "+group);
                    }
                    Check(after.Pixels.Count==before.Pixels.Count&&before.Pixels.All(p=>after.Pixels.TryGetValue(p.Key,out var bytes)&&bytes.SequenceEqual(p.Value)),format+" all page strip payloads unchanged "+group);
                    if(format=="jpeg")Check(Contains(output,icc)&&Contains(output,frame)&&Contains(output,scan.Concat(entropy).ToArray()),"JPEG ICC and entire compressed scan unchanged "+group);
                    if(format=="png"){var chunks=PngChunks(output);Check(chunks["IDAT"].Single().SequenceEqual(PngMetadataChecks.ImageData())&&Contains(output,pngIcc),"PNG IDAT and ICC bytes unchanged "+group);if(group=="PNG text")Check(!Contains(output,"OUTER_PNG_TEXT_MARKER"u8.ToArray()),"Selected PNG text value absent whole file");}
                    foreach(var (g,marker) in new[]{("XMP","OUTER_XMP_MARKER"),("IPTC","OUTER_IPTC_MARKER"),("Comments and descriptions","OUTER_COMMENT_MARKER")})if(format=="jpeg"||(format=="png"&&g=="XMP"))Check(Contains(output,Encoding.ASCII.GetBytes(marker))!=(group==g),format+" outer block selection "+g+" while removing "+group);
                }
                var make=plan.Items.First(i=>i.CanRemove&&i.Name.Contains("0x010F",StringComparison.Ordinal));
                Check(plan.MayDuplicate(new[]{make.Id}),format+" retained blocks warn of possible duplicate removed values");
                var one=Written(plan,new[]{make.Id},folder,format+little+"one-tag.bin");var oneBody=format switch{"jpeg"=>JpegExif(one),"png"=>PngChunks(one)["eXIf"].Single(),_=>one};var oneParsed=ParseTiff(oneBody,format=="tiff");
                foreach(var pair in before.Values)
                    Check(pair.Key=="page0/271"?!oneParsed.Values.ContainsKey(pair.Key):oneParsed.Values.TryGetValue(pair.Key,out var value)&&value.Type==pair.Value.Type&&value.Count==pair.Value.Count&&value.Bytes.SequenceEqual(pair.Value.Bytes),format+" a single tag choice leaves all other tags, including its group peers, identical "+pair.Key);
                Check(!Contains(one,before.Values["page0/271"].Bytes),format+" single selected Make value absent whole file");
                var all=Written(plan,plan.Items.Where(i=>i.CanRemove).Select(i=>i.Id).ToArray(),folder,format+little+"all-selected.bin");var allBody=format switch{"jpeg"=>JpegExif(all),"png"=>PngChunks(all)["eXIf"].Single(),_=>all};var allParsed=ParseTiff(allBody,format=="tiff");
                Check(allParsed.Values.Values.All(v=>v.Group=="Rendering"&&v.Tag!=274),format+" all explicitly selected metadata absent");
                Check(before.Values.Where(v=>v.Value.Group!="Rendering"&&v.Value.Bytes.Length>=8).All(v=>!Contains(all,v.Value.Bytes)),format+" no selected metadata value payload remains anywhere after combined removal");
                var orientation=plan.Items.Where(i=>i.CanRemove&&i.Name.Contains("Orientation",StringComparison.OrdinalIgnoreCase)).Select(i=>i.Id).ToArray();Check(orientation.Length>0,format+" exposes Orientation explicitly");
                var orientationOutput=Written(plan,orientation,folder,format+little+"orientation.bin");var orientationBody=format switch{"jpeg"=>JpegExif(orientationOutput),"png"=>PngChunks(orientationOutput)["eXIf"].Single(),_=>orientationOutput};Check(!ParseTiff(orientationBody,format=="tiff").Values.Values.Any(v=>v.Tag==274),format+" only explicit orientation choice removes it");
                using var cancelled=new MemoryStream();Reject(()=>plan.Write(cancelled,orientation,new CancellationToken(true)),"Selective pre-cancel output");Check(cancelled.Length==0,"Pre-cancel writes no bytes");
                Reject(()=>plan.Write(new MemoryStream(),new[]{int.MaxValue},default),"Foreign selection ID refused");
            }
            // SHORT byte counts (inline and out of line) are retained verbatim, not widened.
            foreach(bool little in new[]{true,false})
            {
                var gray=TiffMetadataChecks.Gray(privateTags:false);var tiled=TiffMetadataChecks.Tiled();var privateTags=TiffMetadataChecks.Private().Select(e=>e.Tag).ToHashSet();
                var strips=gray with{Entries=gray.Entries.Select(e=>e.Tag==279?TiffMetadataChecks.Short(279):e).Append(TiffMetadataChecks.Ascii(270,"SHORT_COUNT_STRIPS")).OrderBy(e=>e.Tag).ToArray()};
                var tiles=tiled with{Entries=tiled.Entries.Where(e=>!privateTags.Contains(e.Tag)).Select(e=>e.Tag==325?TiffMetadataChecks.Short(325):e).Append(TiffMetadataChecks.Ascii(270,"SHORT_COUNT_TILES")).OrderBy(e=>e.Tag).ToArray()};
                var single=gray with{Blocks=gray.Blocks[..1],Entries=gray.Entries.Select(e=>e.Tag==257?TiffMetadataChecks.Short(257,2):e.Tag==279?TiffMetadataChecks.Short(279):e).Append(TiffMetadataChecks.Ascii(270,"SHORT_COUNT_SINGLE")).OrderBy(e=>e.Tag).ToArray()};
                var source=TiffMetadataChecks.Tiff(little,new[]{strips,tiles,single});var before=TiffMetadataChecks.Parse(source);var plan=TiffMetadata.Read(new MemoryStream(source),default).Selective(default);
                var output=Written(plan,plan.Items.Where(f=>f.CanRemove&&f.Group=="Comments and descriptions").Select(f=>f.Id).ToArray(),folder,"short-counts-"+little+".bin");var after=TiffMetadataChecks.Parse(output);Check(after.Count==3,"SHORT bytecount output retains all three strip/tile pages");
                for(int page=0;page<before.Count;page++)
                {
                    int tag=page==1?325:279;var expected=before[page].Tags[tag];var actual=after[page].Tags[tag];
                    Check(expected.Type==3&&actual.Type==3&&actual.Count==expected.Count&&actual.Value.SequenceEqual(expected.Value),"SHORT count type/count/value bytes preserved for page "+page+" order "+little);
                    Check(before[page].Tags.Where(e=>e.Key is not (270 or 273 or 324)).All(e=>after[page].Tags.TryGetValue(e.Key,out var retained)&&retained.Type==e.Value.Type&&retained.Count==e.Value.Count&&retained.Value.SequenceEqual(e.Value.Value)),"Every unselected non-offset value remains byte-identical on SHORT-count page "+page);
                    Check(after[page].Blocks.Length==before[page].Blocks.Length&&after[page].Blocks.Zip(before[page].Blocks).All(b=>b.First.SequenceEqual(b.Second)),"Every strip/tile payload exact for SHORT-count page "+page);
                    Check(!after[page].Tags.ContainsKey(270)&&!Contains(output,before[page].Tags[270].Value),"Selected description and payload absent from real SHORT-count output page "+page);
                }
            }
            // JFIF RGB thumbnails are removed without discarding density/version or recompressing.
            {
                var jfif=new byte[]{74,70,73,70,0,1,2,0,0,1,0,1,2,1,196,51,182,72,219,103};var frame=JpegMetadataChecks.Frame();var scan=JpegMetadataChecks.Scan();var entropy=new byte[]{1,2,255,0,3};
                var com=JpegMetadataChecks.Segment(0xfe,"JFIF_KEEP_TEST_COMMENT"u8.ToArray());var app0=JpegMetadataChecks.Segment(0xe0,jfif);var source=JpegMetadataChecks.Jpeg(app0,com,frame,scan,entropy);var plan=JpegMetadata.Read(new MemoryStream(source),default).Selective(default);
                var thumb=plan.Items.Single(f=>f.Group=="Thumbnail");var output=Written(plan,new[]{thumb.Id},folder,"jfif-thumbnail.bin");var header=jfif[..14];header[12]=header[13]=0;
                Check(output.SequenceEqual(JpegMetadataChecks.Jpeg(JpegMetadataChecks.Segment(0xe0,header),com,frame,scan,entropy)),"JFIF removal changes only framing/thumbnail dimensions and drops RGB bytes");
                Check(output.AsSpan().IndexOf(jfif.AsSpan(14))<0&&Contains(output,scan.Concat(entropy).ToArray()),"JFIF selected RGB bytes absent whole file and compressed data exact");
                var comment=plan.Items.Single(f=>f.Group=="Comments and descriptions");var retained=Written(plan,new[]{comment.Id},folder,"jfif-retained.bin");Check(retained.SequenceEqual(JpegMetadataChecks.Jpeg(app0,frame,scan,entropy)),"Unchecked JFIF thumbnail including density/version stays byte-identical");
            }
            // Compressed PNG text remains opaque and selectable as one complete chunk.
            {
                using var m=new MemoryStream();using(var z=new ZLibStream(m,CompressionLevel.SmallestSize,true))z.Write("OPAQUE_COMPRESSED_PNG_TEXT_PAYLOAD"u8);
                var compressed="CompressedNote\0\0"u8.ToArray().Concat(m.ToArray()).ToArray();var plain="PlainNote\0RETAIN_PLAIN_PNG_TEXT"u8.ToArray();var idat=PngMetadataChecks.Chunk("IDAT",PngMetadataChecks.ImageData());var end=PngMetadataChecks.Chunk("IEND",Array.Empty<byte>());
                var source=PngMetadataChecks.Png(PngMetadataChecks.Header(),PngMetadataChecks.Chunk("zTXt",compressed),PngMetadataChecks.Chunk("tEXt",plain),idat,end);var plan=PngMetadata.Read(new MemoryStream(source),default).Selective(default);var field=plan.Items.Single(f=>f.Name.StartsWith("zTXt ",StringComparison.Ordinal));
                var output=Written(plan,new[]{field.Id},folder,"compressed-png-text.bin");var chunks=PngChunks(output);Check(!chunks.ContainsKey("zTXt")&&!Contains(output,compressed),"Selected zTXt tag and complete compressed value absent from real output");
                Check(chunks["tEXt"].Single().SequenceEqual(plain)&&chunks["IDAT"].Single().SequenceEqual(PngMetadataChecks.ImageData()),"Other PNG text and complete compressed image payload remain exact");
            }
            // Raw-profile text is labelled by its keyword, without parsing opaque contents.
            {
                var profiles=new[]{("exif","PNG text"),("xmp","XMP"),("iptc","IPTC"),("8bim","IPTC"),("other","PNG text")};
                var values=profiles.Select(p=>Encoding.ASCII.GetBytes("Raw profile type "+p.Item1+"\0RAW_PROFILE_"+p.Item1.ToUpperInvariant()+"_VALUE")).ToArray();var idat=PngMetadataChecks.Chunk("IDAT",PngMetadataChecks.ImageData());var end=PngMetadataChecks.Chunk("IEND",Array.Empty<byte>());
                var source=PngMetadataChecks.Png(new[]{PngMetadataChecks.Header()}.Concat(values.Select(v=>PngMetadataChecks.Chunk("tEXt",v))).Concat(new[]{idat,end}).ToArray());var plan=PngMetadata.Read(new MemoryStream(source),default).Selective(default);
                for(int i=0;i<profiles.Length;i++)
                {
                    var profile=profiles[i];var field=plan.Items.Single(f=>f.Name.Contains("Raw profile type "+profile.Item1,StringComparison.Ordinal));Check(field.Group==profile.Item2,"Raw profile "+profile.Item1+" classified accurately");
                    var bytes=Written(plan,new[]{field.Id},folder,"raw-profile-"+i+".bin");var chunks=PngChunks(bytes);Check(chunks["tEXt"].Count==profiles.Length-1&&!Contains(bytes,values[i]),"Selected raw profile "+profile.Item1+" whole chunk and payload absent");
                    Check(values.Where((v,n)=>n!=i).All(v=>chunks["tEXt"].Any(c=>c.SequenceEqual(v))),"Unselected opaque raw-profile chunks remain byte-identical");
                    Check(chunks["IDAT"].Single().SequenceEqual(PngMetadataChecks.ImageData()),"Raw-profile selection preserves compressed PNG image bytes");
                }
            }
            // Parsed descriptive IFD1 tags remain individually selectable while its image stays.
            foreach(bool little in new[]{true,false})
            {
                var source=Tiff(little,false);var value=Encoding.ASCII.GetBytes("THUMB_DESCRIPTION_VALUE_"+little+"\0");
                void S(int at,int n){if(little)BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(at),(ushort)n);else BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(at),(ushort)n);}
                void L(int at,uint n){if(little)BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(at),n);else BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(at),n);}
                Array.Copy(source,1026+3*12,source,1026+4*12,2*12+4);S(1024,6);int field=1026+3*12;S(field,270);S(field+2,2);L(field+4,(uint)value.Length);L(field+8,23000);value.CopyTo(source,23000);
                var before=ParseTiff(source,false);var jpeg=JpegMetadataChecks.Jpeg(JpegMetadataChecks.Segment(0xe1,"Exif\0\0"u8.ToArray().Concat(source).ToArray()),JpegMetadataChecks.Frame(),JpegMetadataChecks.Scan(),new byte[]{1,2,3});var plan=JpegMetadata.Read(new MemoryStream(jpeg),default).Selective(default);
                var description=plan.Items.Single(f=>f.Directory.EndsWith("/IFD1",StringComparison.Ordinal)&&f.Name.Contains("0x010E",StringComparison.Ordinal));Check(description.CanRemove&&description.RemovalParent is not null,"IFD1 description has independent selection and whole-thumbnail parent");
                var bytes=Written(plan,new[]{description.Id},folder,"thumbnail-description-"+little+".bin");var after=ParseTiff(JpegExif(bytes),false);
                Check(!after.Values.ContainsKey("thumbnail/270")&&!Contains(bytes,value),"Individually selected IFD1 description and its bytes absent from real output");
                foreach(var pair in before.Values.Where(v=>v.Key!="thumbnail/270"))Check(after.Values.TryGetValue(pair.Key,out var got)&&got.Type==pair.Value.Type&&got.Count==pair.Value.Count&&got.Bytes.SequenceEqual(pair.Value.Bytes),"Individual IFD1 tag selection retains all other values and embedded JPEG bytes "+pair.Key);
                var parentOutput=Written(plan,new[]{description.RemovalParent!.Value},folder,"thumbnail-parent-"+little+".bin");Check(!ParseTiff(JpegExif(parentOutput),false).Values.Keys.Any(k=>k.StartsWith("thumbnail/",StringComparison.Ordinal)),"Whole Thumbnail removal also removes descriptive children");
            }
            // Hostile EXIF is refused only by the selective mode; remove-all remains available.
            foreach(bool little in new[]{true,false})
            {
                var valid=Tiff(little,false);void L(byte[] b,int p,uint n){if(little)BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),n);else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p),n);}
                int U16(byte[] b,int p)=>little?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)):BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
                var bads=new List<byte[]>{valid[..7],valid[..20]};
                var cycle=(byte[])valid.Clone();int n=U16(cycle,16);L(cycle,16+2+12*n,16);bads.Add(cycle);
                var hugeCount=(byte[])valid.Clone();L(hugeCount,16+2+4,uint.MaxValue);bads.Add(hugeCount);
                var pointer=(byte[])valid.Clone();for(int i=0;i<n;i++)if(U16(pointer,18+12*i)==34665)L(pointer,18+12*i+8,16);bads.Add(pointer);
                var wrongThumbnailOffset=(byte[])valid.Clone();if(little){BinaryPrimitives.WriteUInt16LittleEndian(wrongThumbnailOffset.AsSpan(514),513);BinaryPrimitives.WriteUInt16LittleEndian(wrongThumbnailOffset.AsSpan(516),4);}else{BinaryPrimitives.WriteUInt16BigEndian(wrongThumbnailOffset.AsSpan(514),513);BinaryPrimitives.WriteUInt16BigEndian(wrongThumbnailOffset.AsSpan(516),4);}L(wrongThumbnailOffset,518,1);L(wrongThumbnailOffset,522,21000);bads.Add(wrongThumbnailOffset);
                var oversized=(byte[])valid.Clone();if(little)BinaryPrimitives.WriteUInt16LittleEndian(oversized.AsSpan(16),ushort.MaxValue);else BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(16),ushort.MaxValue);bads.Add(oversized);
                foreach(var bad in bads)
                {
                    var source=JpegMetadataChecks.Jpeg(JpegMetadataChecks.Segment(0xe1,"Exif\0\0"u8.ToArray().Concat(bad).ToArray()),JpegMetadataChecks.Frame(),JpegMetadataChecks.Scan(),new byte[]{1,2,3});var plan=JpegMetadata.Read(new MemoryStream(source),default);
                    Reject(()=>plan.Selective(default),"Hostile EXIF selective refused");using var clean=new MemoryStream();plan.Write(clean,default);Check(clean.Length>0,"Hostile EXIF still supports unchanged remove-all path");
                }
            }
            Check(Groups.Concat(new[]{MetadataSelection.Invalid,MetadataSelection.Notice,MetadataSelection.DuplicateWarning,MetadataSelection.Empty}).All(g=>Strings.Table.TryGetValue(g,out var row)&&row.Length==3&&row.All(t=>!string.IsNullOrWhiteSpace(t))),"All group labels, errors and notices translated");
        }
        finally{Directory.Delete(folder,true);}
        Console.WriteLine("Selective metadata independent real-file checks: "+count);
    }
}
