using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class TiffMetadataChecks
{
    internal sealed record E(int Tag,int Type,long[] Values,byte[]? Raw=null,int? TypeOverride=null,uint? CountOverride=null);
    internal sealed record P(E[] Entries,byte[][] Blocks);
    internal static E Short(int tag,params long[] v)=>new(tag,3,v);
    internal static E Long(int tag,params long[] v)=>new(tag,4,v);
    internal static E Ascii(int tag,string text)=>new(tag,2,Array.Empty<long>(),Encoding.ASCII.GetBytes(text+"\0"));
    internal static E Bytes(int tag,int type,byte[] raw)=>new(tag,type,Array.Empty<long>(),raw);
    internal static E Rational(int tag,params long[] v)=>new(tag,5,v);
    // Strip/tile offsets (273/324) are filled from the page blocks; counts default to block lengths.
    internal static byte[] Tiff(bool little,P[] pages,Func<int,long[],long>? nextIfd=null,int magic=42)
    {
        var output=new MemoryStream();
        void W16(long v){var b=new byte[2];if(little)BinaryPrimitives.WriteUInt16LittleEndian(b,(ushort)v);else BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)v);output.Write(b);}
        void W32(long v){var b=new byte[4];if(little)BinaryPrimitives.WriteUInt32LittleEndian(b,(uint)v);else BinaryPrimitives.WriteUInt32BigEndian(b,(uint)v);output.Write(b);}
        byte[] Encode(E e,long[] values)
        {
            if(e.Raw is not null)return e.Raw;
            var s=new MemoryStream();var keep=output;output=s;
            foreach(long v in values){if(e.Type==3)W16(v);else W32(v);}
            output=keep;return s.ToArray();
        }
        output.Write(little?"II"u8:"MM"u8);W16(magic);W32(0);
        var ifdPositions=new long[pages.Length];var nextFields=new long[pages.Length];
        for(int p=0;p<pages.Length;p++)
        {
            var page=pages[p];var offsets=new List<long>();
            foreach(var block in page.Blocks){offsets.Add(output.Position);output.Write(block);}
            if((output.Position&1)!=0)output.WriteByte(0);
            var encoded=new List<(E E,byte[] Value,long At)>();
            foreach(var e in page.Entries)
            {
                long[] values=e.Tag is 273 or 324&&e.Values.Length==0?offsets.ToArray():e.Tag is 279 or 325&&e.Values.Length==0?page.Blocks.Select(b=>(long)b.Length).ToArray():e.Values;
                var value=Encode(e,values);long at=0;
                if(value.Length>4){at=output.Position;output.Write(value);if((output.Position&1)!=0)output.WriteByte(0);}
                encoded.Add((e,value,at));
            }
            ifdPositions[p]=output.Position;W16(encoded.Count);
            foreach(var (e,value,at) in encoded)
            {
                int size=e.Type switch{3 or 8=>2,4 or 9 or 11=>4,5 or 10 or 12=>8,_=>1};
                W16(e.Tag);W16(e.TypeOverride??e.Type);W32(e.CountOverride??(uint)(value.Length/size));
                if(value.Length>4)W32(at);else{output.Write(value);for(int i=value.Length;i<4;i++)output.WriteByte(0);}
            }
            nextFields[p]=output.Position;W32(0);
        }
        var bytes=output.ToArray();
        void Set(long at,long v){if(little)BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)at),(uint)v);else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)at),(uint)v);}
        Set(4,ifdPositions[0]);
        for(int p=0;p<pages.Length;p++)Set(nextFields[p],nextIfd?.Invoke(p,ifdPositions)??(p+1<pages.Length?ifdPositions[p+1]:0));
        return bytes;
    }

    // Independent minimal reader of the rebuilt output: tag → (type, count, value bytes), plus image blocks.
    internal sealed record Parsed(Dictionary<int,(int Type,uint Count,byte[] Value)> Tags,byte[][] Blocks);
    internal static List<Parsed> Parse(byte[] bytes)
    {
        bool little=bytes[0]=='I';
        uint U16(long at)=>little?BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)at)):BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)at));
        uint U32(long at)=>little?BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)at)):BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)at));
        if(U16(2)!=42)throw new InvalidDataException();
        var pages=new List<Parsed>();
        for(uint ifd=U32(4);ifd!=0;)
        {
            if(ifd%2!=0)throw new InvalidDataException("IFD not word aligned");
            int n=(int)U16(ifd);var tags=new Dictionary<int,(int,uint,byte[])>();
            for(int i=0;i<n;i++)
            {
                long at=ifd+2+12L*i;int tag=(int)U16(at),type=(int)U16(at+2);uint count=U32(at+4);
                int size=type switch{3 or 8=>2,4 or 9 or 11=>4,5 or 10 or 12=>8,_=>1}*(int)count;
                long valueAt=size<=4?at+8:U32(at+8);
                if(size<=4&&!bytes.AsSpan((int)at+8+size,4-size).ToArray().All(b=>b==0))throw new InvalidDataException("Inline padding not zero");
                tags.Add(tag,(type,count,bytes.AsSpan((int)valueAt,size).ToArray()));
            }
            long[] Values(int tag){var (type,count,value)=tags[tag];return Enumerable.Range(0,(int)count).Select(i=>type==3?(long)(little?BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(2*i)):BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2*i))):(long)(little?BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4*i)):BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(4*i)))).ToArray();}
            int o=tags.ContainsKey(273)?273:324,c=o==273?279:325;
            var offsets=Values(o);var counts=Values(c);
            pages.Add(new(tags,offsets.Zip(counts,(x,y)=>bytes.AsSpan((int)x,(int)y).ToArray()).ToArray()));
            ifd=U32(ifd+2+12L*n);
        }
        return pages;
    }
    internal static byte[] Write(TiffMetadata.Plan plan,bool keep=false){using var output=new MemoryStream();plan.Write(output,keep,default);return output.ToArray();}
    private static byte[] Pattern(int length,int seed)=>Enumerable.Range(0,length).Select(i=>(byte)(i*seed+7)).ToArray();
    internal static byte[] Icc()
    {
        var icc=new byte[132];BinaryPrimitives.WriteUInt32BigEndian(icc,132);"acsp"u8.CopyTo(icc.AsSpan(36));icc[100]=0x5a;return icc;
    }
    internal static E[] Private()=>new[]
    {
        Ascii(269,"PRIVATE_DOCNAME"),Ascii(270,"PRIVATE_DESCRIPTION"),Ascii(271,"PRIVATE_MAKE"),Ascii(272,"PRIVATE_MODEL"),Ascii(285,"PRIVATE_PAGENAME"),
        Ascii(305,"PRIVATE_SOFTWARE"),Ascii(306,"2026:10:29 12:00:00"),Ascii(315,"PRIVATE_ARTIST"),Ascii(316,"PRIVATE_HOST"),Bytes(700,1,Encoding.ASCII.GetBytes("<x:PRIVATE_XMP/>")),
        Ascii(33432,"PRIVATE_COPYRIGHT"),Bytes(33723,7,Encoding.ASCII.GetBytes("PRIVATE_IPTC")),Bytes(34377,1,Encoding.ASCII.GetBytes("8BIMPRIVATE_PS")),
        Long(34665,8),Long(34853,8),Long(40965,8),Bytes(40000,7,Encoding.ASCII.GetBytes("PRIVATE_VENDOR")),
    };
    private static E[] Sorted(IEnumerable<E> entries)=>entries.OrderBy(e=>e.Tag).ToArray();
    // 4×3 gray8, two strips (2 rows + 1 row).
    internal static P Gray(int compression=1,bool privateTags=true,int? orientation=null,int seed=3,bool icc=false)
    {
        byte[][] blocks=compression==1?new[]{Pattern(8,seed),Pattern(4,seed+1)}:new[]{Pattern(5,seed),Pattern(3,seed+1)};
        var e=new List<E>{Short(256,4),Short(257,3),Short(258,8),Short(259,compression),Short(262,1),Long(273),Short(277,1),Short(278,2),Long(279),Rational(282,72,1),Rational(283,72,1),Short(296,2)};
        if(orientation is int o)e.Add(Short(274,o));if(privateTags)e.AddRange(Private());if(icc)e.Add(Bytes(34675,7,Icc()));
        return new(Sorted(e),blocks);
    }
    // 20×17 RGB8 tiled, 16×16 tiles: 2×2 tiles of 768 bytes each.
    internal static P Tiled(int compression=1,int seed=5)
    {
        byte[][] blocks=Enumerable.Range(0,4).Select(i=>compression==1?Pattern(768,seed+i):Pattern(40+i,seed+i)).ToArray();
        var e=new List<E>{Short(256,20),Short(257,17),Short(258,8,8,8),Short(259,compression),Short(262,2),Short(277,3),Short(284,1),Short(322,16),Short(323,16),Long(324),Long(325)};
        if(compression!=1)e.Add(Short(317,2));
        e.AddRange(Private());return new(Sorted(e),blocks);
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach(bool little in new[]{true,false})
        {
            string order=little?"II":"MM";
            var pages=new[]{Gray(icc:true,orientation:6),Tiled(5),Gray(compression:32773,seed:9,orientation:6),Tiled(1,11)};
            var source=Tiff(little,pages);var plan=TiffMetadata.Read(new MemoryStream(source),default);
            check(plan.PageCount==4&&plan.LittleEndian==little&&plan.Width==4&&plan.Height==3&&plan.PageSizes[1]==(20,17),order+" TIFF pages, order and sizes read");
            check(plan.RemovedCount==4*Private().Length+2&&plan.Orientation==new ExifOrientationInfo(ExifOrientationStatus.Found,6),order+" every listed private tag on every page plus Orientation counted for removal");
            var clean=Write(plan);var parsed=Parse(clean);
            check(clean.Length==plan.OutputBytes&&parsed.Count==4,order+" output length matches plan and every page is kept");
            check(parsed.Zip(pages).All(x=>x.First.Blocks.Length==x.Second.Blocks.Length&&x.First.Blocks.Zip(x.Second.Blocks).All(b=>b.First.SequenceEqual(b.Second))),order+" every strip and tile byte-identical, uncompressed, LZW and PackBits");
            int[] gone={269,270,271,272,274,285,305,306,315,316,700,33432,33723,34377,34665,34853,40965,40000};
            check(parsed.All(p=>!gone.Any(p.Tags.ContainsKey)),order+" removed tags absent from every rebuilt page");
            string text=Encoding.ASCII.GetString(clean);check(!text.Contains("PRIVATE")&&!text.Contains("2026:10:29"),order+" no removed payload bytes copied anywhere");
            check(parsed[0].Tags[34675].Value.SequenceEqual(Icc())&&parsed[0].Tags[282].Value.Length==8&&parsed[1].Tags[317].Count==1&&parsed[1].Tags[258].Count==3,order+" ICC, resolution, predictor and bits kept exactly");
            check(parsed.All(p=>p.Tags.Keys.SequenceEqual(p.Tags.Keys.OrderBy(k=>k))),order+" rebuilt tags ascending");
            var kept=Write(plan,true);var keptParsed=Parse(kept);
            check(kept.Length==plan.OutputBytesKeeping(true)&&keptParsed[0].Tags[274].Value.Take(2).SequenceEqual(little?new byte[]{6,0}:new byte[]{0,6})&&keptParsed[2].Tags.ContainsKey(274)&&!keptParsed[1].Tags.ContainsKey(274),order+" Keep orientation keeps 274 only on pages that had it");
            var again=TiffMetadata.Read(new MemoryStream(clean),default);check(again.RemovedCount==0&&Write(again).SequenceEqual(clean)&&again.Orientation==ExifOrientationInfo.None,order+" rebuilt TIFF re-reads with nothing left to remove; rewrite is idempotent");
            check(TiffMetadata.Read(new MemoryStream(kept),default).Orientation.Value==6,order+" kept copy re-reads orientation 6");
        }
        var basic=Tiff(true,new[]{Gray()});var basicPlan=TiffMetadata.Read(new MemoryStream(basic),default);
        Array.Fill(basic,(byte)0);check(Parse(Write(basicPlan))[0].Blocks[0].SequenceEqual(Pattern(8,3)),"Plan owns its snapshot independently of caller array");
        reject(()=>((IList<TiffMetadataTag>)basicPlan.Tags)[0]=new(0,"",0,0,false),"TIFF summary is readonly");
        check(basicPlan.Tags.Single(t=>t.Tag==40000).Name=="Private tag"&&basicPlan.Tags.Single(t=>t.Tag==271).Removed&&!basicPlan.Tags.Single(t=>t.Tag==273).Removed,"Summary names private tags and marks kept structure");
        var plainPlan=TiffMetadata.Read(new MemoryStream(Tiff(false,new[]{Gray(privateTags:false)})),default);check(plainPlan.RemovedCount==0&&!plainPlan.Orientation.CanKeep,"No-match TIFF reports nothing to remove");
        reject(()=>Write(plainPlan,true),"Keep orientation without source orientation refused");
        var paddedLast=Gray(privateTags:false);paddedLast=paddedLast with{Blocks=new[]{Pattern(8,3),Pattern(8,4)}};check(TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{paddedLast})),default).ImageDataBytes==16,"Uncompressed last strip padded to full RowsPerStrip accepted");
        var palette=new P(Sorted(new[]{Short(256,4),Short(257,3),Short(258,4),Short(259,1),Short(262,3),Long(273),Short(277,1),Short(278,3),Long(279),Short(320,Enumerable.Range(0,48).Select(i=>(long)i*1000).ToArray())}),new[]{Pattern(6,2)});
        check(Parse(Write(TiffMetadata.Read(new MemoryStream(Tiff(false,new[]{palette})),default)))[0].Tags[320].Count==48,"4-bit palette ColorMap retained");
        var malformedTurn=Gray(privateTags:false);malformedTurn=malformedTurn with{Entries=Sorted(malformedTurn.Entries.Append(Short(274,9)))};
        check(TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{malformedTurn})),default).Orientation.Status==ExifOrientationStatus.Malformed,"Orientation 9 is malformed and cannot be kept");
        check(TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{Gray(orientation:6),Gray(orientation:3)})),default).Orientation.Status==ExifOrientationStatus.Ambiguous,"Different page orientations are ambiguous");

        var hugeTurn=Gray(privateTags:false);hugeTurn=hugeTurn with{Entries=Sorted(hugeTurn.Entries.Append(new E(274,4,new long[]{1},null,null,0x40000000u)))};
        var hugePlan=TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{hugeTurn})),default);check(hugePlan.Orientation.Status==ExifOrientationStatus.Malformed&&hugePlan.RemovedCount==1&&!Parse(Write(hugePlan))[0].Tags.ContainsKey(274),"Damaged Orientation entry with huge count is removed as malformed, not refusing the file");
        var onlyTurn=TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{Gray(privateTags:false,orientation:6)})),default);
        check(onlyTurn.RemovedCount==1&&onlyTurn.RemovedCountKeeping(true)==0&&onlyTurn.RemovedCountKeeping(false)==1,"Orientation-only TIFF has nothing to remove while orientation is kept");
        check(basicPlan.Tags.Single(t=>t.Tag==271).Bytes=="PRIVATE_MAKE\0".Length&&basicPlan.RemovedBytes>12L*basicPlan.RemovedCount,"Removed tags report declared value sizes without reading payloads");
        // Refusals.
        void Refuse(byte[] bytes,string label,string? message=null)
        {
            try{TiffMetadata.Read(new MemoryStream(bytes),default);check(false,label);}
            catch(UserError error){check(message is null||error.Message==message,label);}
        }
        Refuse(Tiff(true,new[]{Gray()},magic:43),"BigTIFF refused",TiffMetadata.Big);
        Refuse(Tiff(false,new[]{Gray()},magic:43),"Big-endian BigTIFF refused",TiffMetadata.Big);
        Refuse(Tiff(true,new[]{Gray()},magic:41),"Wrong TIFF magic refused",TiffMetadata.Invalid);
        Refuse(Tiff(true,new[]{Gray()},(p,all)=>all[p]),"IFD pointing to itself refused",TiffMetadata.Invalid);
        Refuse(Tiff(true,new[]{Gray(),Gray(seed:4)},(p,all)=>all[1-p]),"Two-page IFD cycle refused");
        Refuse(Tiff(true,new[]{Gray(),Gray(seed:4)},(p,all)=>p==0?all[0]+12:0),"Overlapping IFD tables refused");
        var bad=Tiff(true,new[]{Gray(privateTags:false)});
        for(int n=0;n<bad.Length;n++){int k=n;Refuse(bad[..k],"Every truncated TIFF prefix refused");}
        var badOffset=Gray(privateTags:false);badOffset=badOffset with{Entries=badOffset.Entries.Select(e=>e.Tag==273?Long(273,8,100000):e).ToArray()};Refuse(Tiff(true,new[]{badOffset}),"Out-of-range strip offset refused");
        var intoHeader=Gray(privateTags:false);intoHeader=intoHeader with{Entries=intoHeader.Entries.Select(e=>e.Tag==273?Long(273,4,20):e).ToArray()};Refuse(Tiff(true,new[]{intoHeader}),"Strip overlapping header refused");
        var overlap=Gray(privateTags:false);overlap=overlap with{Entries=overlap.Entries.Select(e=>e.Tag==273?Long(273,8,8):e.Tag==279?Long(279,8,4):e).ToArray()};Refuse(Tiff(true,new[]{overlap}),"Overlapping strips refused");
        var badCount=Gray(privateTags:false);badCount=badCount with{Entries=badCount.Entries.Select(e=>e.Tag==279?Long(279,7,4):e).ToArray()};Refuse(Tiff(true,new[]{badCount}),"Uncompressed strip byte count must match rows");
        var unsorted=Gray(privateTags:false);unsorted=unsorted with{Entries=unsorted.Entries.Reverse().ToArray()};Refuse(Tiff(true,new[]{unsorted}),"Unsorted tags refused");
        var duplicate=Gray(privateTags:false);duplicate=duplicate with{Entries=Sorted(duplicate.Entries.Append(Ascii(271,"A")).Append(Ascii(271,"B")))};Refuse(Tiff(true,new[]{duplicate}),"Duplicate tags refused");
        foreach(var (entry,label,message) in new[]{(Short(400,1),"Unknown public tag refused",TiffMetadata.Invalid),(Long(330,8),"SubIFD refused",TiffMetadata.SubImages),(Short(32997,2),"ImageDepth refused",TiffMetadata.SubImages),(Long(347,1),"JPEGTables refused",TiffMetadata.SubImages)})
        {var page=Gray(privateTags:false);Refuse(Tiff(true,new[]{page with{Entries=Sorted(page.Entries.Append(entry))}}),label,message);}
        foreach(var (tag,value,label) in new[]{(259,7L,"JPEG compression refused"),(259,3L,"CCITT compression refused"),(262,5L,"CMYK refused"),(262,6L,"YCbCr refused"),(258,12L,"12-bit refused"),(284,3L,"Bad planar refused"),(277,2L,"Extra sample without ExtraSamples refused"),(256,0L,"Zero width refused"),(256,16385L,"Over-wide refused")})
        {var page=Gray(privateTags:false);Refuse(Tiff(true,new[]{page with{Entries=Sorted(page.Entries.Where(e=>e.Tag!=tag).Append(Short(tag,value)))}}),label);}
        var noPhotometric=Gray(privateTags:false);Refuse(Tiff(true,new[]{noPhotometric with{Entries=noPhotometric.Entries.Where(e=>e.Tag!=262).ToArray()}}),"Missing photometric refused");
        var floats=Gray(privateTags:false);Refuse(Tiff(true,new[]{floats with{Entries=Sorted(floats.Entries.Append(Short(339,3)))}}),"Float samples refused");
        var badIcc=Gray(privateTags:false);Refuse(Tiff(true,new[]{badIcc with{Entries=Sorted(badIcc.Entries.Append(Bytes(34675,7,new byte[132])))}}),"ICC without valid header refused");
        var wrongType=Gray(privateTags:false);Refuse(Tiff(true,new[]{wrongType with{Entries=wrongType.Entries.Select(e=>e.Tag==258?e with{TypeOverride=13}:e).ToArray()}}),"Unknown type in structural tag refused");
        var hugeCount=Gray(privateTags:false);Refuse(Tiff(true,new[]{hugeCount with{Entries=hugeCount.Entries.Select(e=>e.Tag==320?e:e.Tag==258?e with{CountOverride=uint.MaxValue}:e).ToArray()}}),"Huge structural count refused");
        var bothLayouts=Tiled();Refuse(Tiff(true,new[]{bothLayouts with{Entries=Sorted(bothLayouts.Entries.Append(Short(278,2)))}}),"Strips and tiles together refused");
        var badTile=Tiled();Refuse(Tiff(true,new[]{badTile with{Entries=badTile.Entries.Select(e=>e.Tag==322?Short(322,20):e).ToArray()}}),"Tile width not a multiple of 16 refused");
        var manyPages=Enumerable.Range(0,TiffMetadata.MaxPages).Select(i=>Gray(privateTags:false,seed:i)).ToArray();
        check(TiffMetadata.Read(new MemoryStream(Tiff(true,manyPages)),default).PageCount==64,"Exactly 64 pages accepted");
        Refuse(Tiff(true,manyPages.Append(Gray(privateTags:false)).ToArray()),"65 pages refused");
        var big=new P(Sorted(new[]{Short(256,4000),Short(257,4001),Short(258,1),Short(259,32773),Short(262,0),Long(273),Short(277,1),Long(278,4001),Long(279)}),new[]{Pattern(10,1)});Refuse(Tiff(true,new[]{big}),"Over 16 MP refused");
        var half=new P(Sorted(new[]{Short(256,4000),Short(257,2001),Short(258,1),Short(259,32773),Short(262,0),Long(273),Short(277,1),Long(278,2001),Long(279)}),new[]{Pattern(10,1)});
        check(TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{half})),default).PageCount==1,"8 MP page accepted");Refuse(Tiff(true,new[]{half,half with{Blocks=new[]{Pattern(10,2)}}}),"Over 16 MP across pages refused");
        reject(()=>TiffMetadata.Read(new MemoryStream(new byte[TiffMetadata.MaxBytes+1]),default),"32MiB+1 refused before parsing");
        reject(()=>TiffMetadata.Read(new MemoryStream(Tiff(true,new[]{Gray()})),new CancellationToken(true)),"Pre-cancel prevents TIFF preparation");
        using(var output=new MemoryStream()){reject(()=>basicPlan.Write(output,new CancellationToken(true)),"Pre-cancel prevents TIFF output");check(output.Length==0,"Pre-cancel writes zero bytes");}
        var prefs=new Prefs();var action=Catalog.Available(prefs,new[]{@"C:\a.tif"}).Single(a=>a.Id=="image.tiffmetadata");
        check(action.SingleFile&&Catalog.Available(prefs,new[]{@"C:\a.TIFF"}).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,new[]{@"C:\a.png"}).Any(a=>a.Id==action.Id)&&!Catalog.Available(prefs,new[]{@"C:\a.tif",@"C:\b.tif"}).Any(a=>a.Id==action.Id),"TIFF action requires exactly one explicit tif/tiff");
        prefs.SetCustomActions(new[]{action.Id});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()==action.Id,"Stable TIFF action settings roundtrip");
        foreach(string text in new[]{TiffMetadata.Invalid,TiffMetadata.Big,TiffMetadata.SubImages,action.Title})check(Strings.Table.TryGetValue(text,out var row)&&row.Length==3&&row.All(s=>s.Length>0),"TIFF text translated: "+text[..Math.Min(30,text.Length)]);
    }
}
