using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record TiffMetadataTag(int Tag,string Name,int Count,long Bytes,bool Removed);

// Original bounded classic-TIFF container rewriter, not a pixel decoder. Every page
// (top-level IFD) is kept. The output is rebuilt from scratch: a new header, each
// page's strip or tile bytes copied unchanged (compression untouched), the retained
// tag values and a new IFD. Removed tags, their payloads and any bytes no retained
// tag points at are never copied. Removed payloads are not interpreted.
// Structural checks do not replace native decoding of the rebuilt snapshot.
public static class TiffMetadata
{
    public const int MaxBytes=32*1024*1024,MaxPages=64,MaxEntries=512,MaxBlocks=65536,MaxIccBytes=4*1024*1024;
    public const string Invalid="Use a structurally valid classic TIFF, at most 32 MiB, 64 pages, 16 MP in all pages together and 16,384 pixels per side, with uncompressed, LZW, Deflate or PackBits strips or tiles of 1–16-bit unsigned gray, palette or RGB samples. Damaged offsets, overlapping or repeated pages, unsorted tags and unknown structural tags are refused.";
    public const string Big="BigTIFF files are not supported by this metadata tool. No copy was saved.";
    public const string SubImages="TIFF files with sub-image (SubIFD), JPEG-in-TIFF, YCbCr or volume tags are not supported by this metadata tool. No copy was saved.";

    // Tags this tool removes by name. Every other tag at or above 32768 except the
    // ICC profile is a private tag and is removed too.
    private static readonly Dictionary<int,string> Removed=new()
    {
        [269]="DocumentName",[270]="ImageDescription",[271]="Make",[272]="Model",[285]="PageName",[286]="XPosition",[287]="YPosition",
        [297]="PageNumber",[305]="Software",[306]="DateTime",[315]="Artist",[316]="HostComputer",[700]="XMP",[18246]="Rating",[18249]="RatingPercent",
        [33432]="Copyright",[33723]="IPTC",[34377]="Photoshop",[34665]="EXIF IFD",[34853]="GPS IFD",[40965]="Interoperability IFD",
    };
    // Tags kept because rendering needs or uses them. 274 Orientation is kept only on request.
    private static readonly Dictionary<int,string> Kept=new()
    {
        [254]="NewSubfileType",[255]="SubfileType",[256]="ImageWidth",[257]="ImageLength",[258]="BitsPerSample",[259]="Compression",[262]="PhotometricInterpretation",
        [266]="FillOrder",[273]="StripOffsets",[274]="Orientation",[277]="SamplesPerPixel",[278]="RowsPerStrip",[279]="StripByteCounts",[280]="MinSampleValue",[281]="MaxSampleValue",
        [282]="XResolution",[283]="YResolution",[284]="PlanarConfiguration",[296]="ResolutionUnit",[317]="Predictor",[318]="WhitePoint",[319]="PrimaryChromaticities",[320]="ColorMap",
        [322]="TileWidth",[323]="TileLength",[324]="TileOffsets",[325]="TileByteCounts",[338]="ExtraSamples",[339]="SampleFormat",[34675]="ICC profile",
    };
    // Structural meaning this tool does not rebuild: refused rather than dropped.
    private static readonly HashSet<int> Refused=new(){330,347,529,530,531,532,32995,32996,32997,32998};
    private static readonly int[] TypeSize={0,1,1,2,4,8,1,1,2,4,8,4,8};

    public static string TagName(int tag)=>Removed.TryGetValue(tag,out var a)?a:Kept.TryGetValue(tag,out var b)?b:tag>=32768?"Private tag":"Unknown tag";

    internal sealed record Entry(int Tag,int Type,uint Count,int ValueOffset,int ValueBytes);
    internal sealed record Page(Entry[] Entries,(int Offset,int Length)[] Blocks,bool Tiled,int Width,int Height,int Orientation);

    public sealed class Plan
    {
        private readonly byte[] source;
        private readonly Page[] pages;
        private readonly bool little;
        public int PageCount=>pages.Length;
        public int Width=>pages[0].Width;
        public int Height=>pages[0].Height;
        public bool LittleEndian=>little;
        public long SourceBytes=>source.Length;
        public int RemovedCount{get;}
        public long RemovedBytes{get;}
        public long ImageDataBytes{get;}
        public IReadOnlyList<TiffMetadataTag> Tags{get;}
        // Read only from each page's own baseline Orientation tag (274), never from EXIF or XMP.
        public ExifOrientationInfo Orientation{get;}
        public IReadOnlyList<(int Width,int Height)> PageSizes=>Array.AsReadOnly(pages.Select(p=>(p.Width,p.Height)).ToArray());
        public long OutputBytes=>Layout(false).Total;
        public long OutputBytesKeeping(bool keepOrientation)=>Layout(keepOrientation&&Orientation.CanKeep).Total;
        internal Plan(byte[] source,Page[] pages,bool little,ExifOrientationInfo orientation)
        {
            this.source=source;this.pages=pages;this.little=little;Orientation=orientation;
            var all=pages.SelectMany(p=>p.Entries).ToArray();
            RemovedCount=all.Count(e=>IsRemoved(e.Tag));RemovedBytes=all.Where(e=>IsRemoved(e.Tag)).Sum(e=>12L+(e.ValueBytes>4?e.ValueBytes:0));
            ImageDataBytes=pages.Sum(p=>p.Blocks.Sum(b=>(long)b.Length));
            Tags=Array.AsReadOnly(all.GroupBy(e=>e.Tag).OrderBy(g=>g.Key).Select(g=>new TiffMetadataTag(g.Key,TagName(g.Key),g.Count(),g.Sum(e=>(long)e.ValueBytes),IsRemoved(g.Key))).ToArray());
        }
        private static bool IsRemoved(int tag)=>tag is 274||!Kept.ContainsKey(tag);
        private IEnumerable<Entry> Retained(Page page,bool keepOrientation)=>page.Entries.Where(e=>!IsRemoved(e.Tag)||(keepOrientation&&e.Tag==274));
        private static long Even(long at)=>at+(at&1);

        // Offsets for every page: [blocks][values][IFD], each part starting on an even byte.
        private (long Total,long[] Ifds) Layout(bool keepOrientation)
        {
            long at=8;var ifds=new long[pages.Length];
            for(int i=0;i<pages.Length;i++)
            {
                var page=pages[i];foreach(var block in page.Blocks)at+=block.Length;at=Even(at);
                var entries=Retained(page,keepOrientation).ToArray();
                foreach(var e in entries)if(OutOfLine(e))at=Even(at+NewValueBytes(e,page));
                ifds[i]=at;at+=2+12L*entries.Length+4;
            }
            return (at,ifds);
        }
        private static bool IsOffsets(int tag)=>tag is 273 or 324;
        private static bool IsCounts(int tag)=>tag is 279 or 325;
        private static long NewValueBytes(Entry e,Page page)=>IsOffsets(e.Tag)||IsCounts(e.Tag)?4L*page.Blocks.Length:e.ValueBytes;
        private static bool OutOfLine(Entry e)=>(IsOffsets(e.Tag)||IsCounts(e.Tag)?4L*e.Count:e.ValueBytes)>4;

        public void Write(Stream output,CancellationToken token)=>Write(output,false,token);
        public void Write(Stream output,bool keepOrientation,CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(output);token.ThrowIfCancellationRequested();
            if(!output.CanWrite)throw new ArgumentException("A writable output stream is required.",nameof(output));
            if(keepOrientation&&!Orientation.CanKeep)throw new InvalidOperationException("No source orientation to keep.");
            var (_,ifds)=Layout(keepOrientation);
            long written=0;
            void Put(ReadOnlySpan<byte> bytes){output.Write(bytes);written+=bytes.Length;}
            var zero=new byte[1];void Pad(){if((written&1)!=0)Put(zero);}
            void Copy(int offset,int length){for(int at=0;at<length;){token.ThrowIfCancellationRequested();int n=Math.Min(65536,length-at);Put(source.AsSpan(offset+at,n));at+=n;}}
            var word=new byte[4];var value=new byte[4];
            void U16(int value){if(little)BinaryPrimitives.WriteUInt16LittleEndian(word,(ushort)value);else BinaryPrimitives.WriteUInt16BigEndian(word,(ushort)value);Put(word.AsSpan(0,2));}
            void U32(long value){if(little)BinaryPrimitives.WriteUInt32LittleEndian(word,(uint)value);else BinaryPrimitives.WriteUInt32BigEndian(word,(uint)value);Put(word);}
            Put(little?"II"u8:"MM"u8);U16(42);U32(ifds[0]);
            for(int i=0;i<pages.Length;i++)
            {
                token.ThrowIfCancellationRequested();var page=pages[i];
                var blockOffsets=new long[page.Blocks.Length];
                for(int b=0;b<page.Blocks.Length;b++){blockOffsets[b]=written;Copy(page.Blocks[b].Offset,page.Blocks[b].Length);}
                Pad();
                var entries=Retained(page,keepOrientation).ToArray();var valueAt=new long[entries.Length];
                for(int k=0;k<entries.Length;k++)
                {
                    var e=entries[k];if(!OutOfLine(e))continue;
                    valueAt[k]=written;
                    if(IsOffsets(e.Tag))foreach(long o in blockOffsets)U32(o);
                    else if(IsCounts(e.Tag))foreach(var block in page.Blocks)U32(block.Length);
                    else Copy(e.ValueOffset,e.ValueBytes);
                    Pad();
                }
                if(written!=ifds[i])throw new InvalidOperationException("TIFF layout mismatch.");
                U16(entries.Length);
                for(int k=0;k<entries.Length;k++)
                {
                    var e=entries[k];bool rebuilt=IsOffsets(e.Tag)||IsCounts(e.Tag);
                    U16(e.Tag);U16(rebuilt?4:e.Type);U32(rebuilt?page.Blocks.Length:e.Count);
                    if(OutOfLine(e)){U32(valueAt[k]);continue;}
                    // Inline values are rebuilt so unused padding bytes never carry source data.
                    Array.Clear(value);
                    if(IsOffsets(e.Tag)){if(little)BinaryPrimitives.WriteUInt32LittleEndian(value,(uint)blockOffsets[0]);else BinaryPrimitives.WriteUInt32BigEndian(value,(uint)blockOffsets[0]);}
                    else if(IsCounts(e.Tag)){if(little)BinaryPrimitives.WriteUInt32LittleEndian(value,(uint)page.Blocks[0].Length);else BinaryPrimitives.WriteUInt32BigEndian(value,(uint)page.Blocks[0].Length);}
                    else source.AsSpan(e.ValueOffset,e.ValueBytes).CopyTo(value.AsSpan());
                    Put(value);
                }
                U32(i+1<pages.Length?ifds[i+1]:0);
            }
            token.ThrowIfCancellationRequested();
        }
    }

    public static Plan Read(Stream input,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A readable seekable input stream is required.",nameof(input));
        long length=input.Length;if(length is <8 or >MaxBytes)throw new UserError(Invalid);
        var bytes=new byte[(int)length];input.Position=0;
        try{for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int count=Math.Min(65536,bytes.Length-at);input.ReadExactly(bytes.AsSpan(at,count));at+=count;}}
        catch(EndOfStreamException){throw new UserError(Invalid);}
        token.ThrowIfCancellationRequested();
        bool little;
        if(bytes[0]=='I'&&bytes[1]=='I')little=true;else if(bytes[0]=='M'&&bytes[1]=='M')little=false;else throw new UserError(Invalid);
        ushort U16(long at)=>little?BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)at,2)):BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)at,2));
        uint U32(long at)=>little?BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)at,4)):BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)at,4));
        int magic=U16(2);if(magic==43)throw new UserError(Big);if(magic!=42)throw new UserError(Invalid);
        // Header, IFD tables and image blocks may never overlap one another.
        var used=new List<(long Start,long End)>{(0,8)};var tables=new List<(long Start,long End)>{(0,8)};
        var pages=new List<Page>();long totalPixels=0;uint next=U32(4);
        var orientations=new List<int>();bool malformedOrientation=false;var visited=new HashSet<uint>();
        while(next!=0)
        {
            token.ThrowIfCancellationRequested();
            if(pages.Count>=MaxPages||next<8||next>bytes.Length-2||!visited.Add(next))throw new UserError(Invalid);
            int count=U16(next);long tableEnd=next+2L+12L*count+4;
            if(count is 0 or >MaxEntries||tableEnd>bytes.Length)throw new UserError(Invalid);
            used.Add((next,tableEnd));tables.Add((next,tableEnd));
            var entries=new Entry[count];int previous=-1;
            for(int i=0;i<count;i++)
            {
                long at=next+2+12L*i;int tag=U16(at),type=U16(at+2);uint n=U32(at+4);
                if(tag<=previous)throw new UserError(Invalid);previous=tag;
                if(Refused.Contains(tag))throw new UserError(SubImages);
                bool kept=Kept.ContainsKey(tag);
                if(!kept&&!Removed.ContainsKey(tag)&&tag<32768)throw new UserError(Invalid);
                // Removed tags are never read: their type, count and payload are not interpreted.
                if(!kept){entries[i]=new(tag,type,n,0,0);continue;}
                if(type is <1 or >12||n==0)throw new UserError(Invalid);
                long size=(long)TypeSize[type]*n;if(size>bytes.Length)throw new UserError(Invalid);
                long offset=size<=4?at+8:U32(at+8);
                if(offset+size>bytes.Length||(size>4&&offset<8))throw new UserError(Invalid);
                entries[i]=new(tag,type,n,(int)offset,(int)size);
            }
            var page=ReadPage(entries,bytes,little,U16,U32,used,token,out int orientation);
            if(orientation<0)malformedOrientation=true;else if(orientation>0)orientations.Add(orientation);
            totalPixels+=(long)page.Width*page.Height;if(totalPixels>ImageRules.MaxPixels)throw new UserError(Invalid);
            pages.Add(page);next=U32(tableEnd-4);
        }
        if(pages.Count==0)throw new UserError(Invalid);
        var sorted=used.OrderBy(r=>r.Start).ToArray();
        for(int i=1;i<sorted.Length;i++){if((i&4095)==0)token.ThrowIfCancellationRequested();if(sorted[i].Start<sorted[i-1].End)throw new UserError(Invalid);}
        // Every retained out-of-line value must also stay outside the header and IFD tables.
        foreach(var page in pages)foreach(var e in page.Entries)
            if(e.ValueBytes>4&&tables.Any(r=>e.ValueOffset<r.End&&e.ValueOffset+e.ValueBytes>r.Start))throw new UserError(Invalid);
        var orientationInfo=malformedOrientation?ExifOrientationInfo.Malformed:orientations.Count==0?ExifOrientationInfo.None:orientations.Distinct().Count()>1?ExifOrientationInfo.Ambiguous:new(ExifOrientationStatus.Found,orientations[0]);
        var plan=new Plan(bytes,pages.ToArray(),little,orientationInfo);
        if(plan.OutputBytesKeeping(true)>MaxBytes)throw new UserError(Invalid);
        token.ThrowIfCancellationRequested();return plan;
    }

    private static Page ReadPage(Entry[] entries,byte[] bytes,bool little,Func<long,ushort> u16,Func<long,uint> u32,List<(long Start,long End)> used,CancellationToken token,out int orientation)
    {
        Entry? Find(int tag)=>entries.FirstOrDefault(e=>e.Tag==tag);
        long Value(Entry e,int index)=>e.Type switch{1=>bytes[e.ValueOffset+index],3=>u16(e.ValueOffset+2L*index),4=>u32(e.ValueOffset+4L*index),_=>throw new UserError(Invalid)};
        long Single(int tag,long fallback,bool allowLong=false)
        {
            var e=Find(tag);if(e is null)return fallback;
            if(e.Count!=1||!(e.Type==3||(allowLong&&e.Type==4)))throw new UserError(Invalid);return Value(e,0);
        }
        long[] Shorts(Entry e,int count){if(e.Type!=3||e.Count!=count)throw new UserError(Invalid);return Enumerable.Range(0,count).Select(i=>Value(e,i)).ToArray();}
        long width=Single(256,0,true),height=Single(257,0,true);
        if(width is <1 or >16384||height is <1 or >16384||width*height>ImageRules.MaxPixels)throw new UserError(Invalid);
        int spp=(int)Single(277,1);if(spp is <1 or >8)throw new UserError(Invalid);
        long compression=Single(259,1),photometric=Single(262,-1),planar=Single(284,1),predictor=Single(317,1),fill=Single(266,1);
        if(compression is not (1 or 5 or 8 or 32773 or 32946)||planar is not (1 or 2)||fill is not (1 or 2))throw new UserError(Invalid);
        if(predictor is not (1 or 2)||(predictor==2&&compression is 1 or 32773))throw new UserError(Invalid);
        var bitsEntry=Find(258);long[] bits=bitsEntry is null?Enumerable.Repeat(1L,spp).ToArray():Shorts(bitsEntry,spp);
        int depth=(int)bits[0];if(bits.Any(b=>b!=depth)||depth is not (1 or 2 or 4 or 8 or 16))throw new UserError(Invalid);
        if(Find(339) is { } format&&Shorts(format,spp).Any(f=>f!=1))throw new UserError(Invalid);
        int baseChannels=photometric switch{0 or 1=>1,2=>3,3=>1,_=>throw new UserError(Invalid)};
        if(spp<baseChannels||(depth<8&&spp!=1)||(photometric==2&&depth<8))throw new UserError(Invalid);
        var extra=Find(338);
        if(extra is null?spp!=baseChannels:Shorts(extra,spp-baseChannels).Any(v=>v>2))throw new UserError(Invalid);
        if(photometric==3)
        {
            var map=Find(320);if(depth>8||map is null||map.Type!=3||map.Count!=3u<<depth)throw new UserError(Invalid);
        }
        else if(Find(320) is not null)throw new UserError(Invalid);
        foreach(int tag in new[]{280,281})if(Find(tag) is { } e)Shorts(e,spp);
        foreach(int tag in new[]{282,283})if(Find(tag) is { } e&&(e.Type!=5||e.Count!=1))throw new UserError(Invalid);
        if(Find(318) is { } white&&(white.Type!=5||white.Count!=2))throw new UserError(Invalid);
        if(Find(319) is { } primaries&&(primaries.Type!=5||primaries.Count!=6))throw new UserError(Invalid);
        if(Single(296,2) is not (1 or 2 or 3))throw new UserError(Invalid);
        Single(254,0,true);Single(255,1);
        if(Find(34675) is { } icc)
        {
            if(icc.Type is not (1 or 7)||icc.ValueBytes is <132 or >MaxIccBytes||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(icc.ValueOffset,4))!=icc.ValueBytes||!bytes.AsSpan(icc.ValueOffset+36,4).SequenceEqual("acsp"u8))throw new UserError(Invalid);
        }
        orientation=0;
        if(Find(274) is { } turn)
        {
            if(turn.Type!=3||turn.Count!=1)orientation=-1;
            else{long v=Value(turn,0);orientation=v is >=1 and <=8?(int)v:-1;}
        }
        bool tiled=Find(322) is not null||Find(323) is not null||Find(324) is not null||Find(325) is not null;
        bool striped=Find(273) is not null||Find(279) is not null||Find(278) is not null;
        if(tiled==striped)throw new UserError(Invalid);
        int planes=planar==2?spp:1,samplesPerBlockRow=planar==2?1:spp;
        long blocksAcross,blocksDown,blockWidth,blockRows;
        if(tiled)
        {
            blockWidth=Single(322,0,true);blockRows=Single(323,0,true);
            if(blockWidth is <16 or >16384||blockRows is <16 or >16384||blockWidth%16!=0||blockRows%16!=0)throw new UserError(Invalid);
            blocksAcross=(width+blockWidth-1)/blockWidth;blocksDown=(height+blockRows-1)/blockRows;
        }
        else
        {
            blockWidth=width;blockRows=Math.Min(Single(278,uint.MaxValue,true),height);if(blockRows<1)throw new UserError(Invalid);
            blocksAcross=1;blocksDown=(height+blockRows-1)/blockRows;
        }
        long blockCount=blocksAcross*blocksDown*planes;if(blockCount>MaxBlocks)throw new UserError(Invalid);
        var offsets=Find(tiled?324:273);var counts=Find(tiled?325:279);
        if(offsets is null||counts is null||offsets.Count!=blockCount||counts.Count!=blockCount||offsets.Type is not (3 or 4)||counts.Type is not (3 or 4))throw new UserError(Invalid);
        long rowBytes=(blockWidth*samplesPerBlockRow*depth+7)/8;
        var blocks=new (int,int)[blockCount];
        for(int i=0;i<blockCount;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            long offset=Value(offsets,i),size=Value(counts,i);
            if(size<1||offset<8||offset+size>bytes.Length)throw new UserError(Invalid);
            if(compression==1)
            {
                // Uncompressed blocks hold exactly their rows; a last strip may also be padded to full RowsPerStrip.
                long down=i/blocksAcross%blocksDown,rows=tiled?blockRows:Math.Min(blockRows,height-down*blockRows);
                if(size!=rows*rowBytes&&!(!tiled&&size==blockRows*rowBytes))throw new UserError(Invalid);
            }
            used.Add((offset,offset+size));blocks[i]=((int)offset,(int)size);
        }
        return new Page(entries,blocks,tiled,(int)width,(int)height,orientation);
    }
}
