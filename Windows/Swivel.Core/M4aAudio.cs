using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record M4aInfo(string Codec,int Rate,int Channels,double Seconds);

/// Original bounded ISO base media (MP4) box gate for audio-only M4A files, checked before the
/// file reaches the Windows decoder. It reads box headers and a few small boxes only; it is not a
/// decoder and does not prove that every sample is valid.
public static class M4aAudio
{
    public const string Unsupported="Choose an audio-only M4A file with exactly one AAC-LC or ALAC track (mono or stereo, 8–192 kHz, at most two hours). Video, several audio tracks, protected (DRM), fragmented, QuickTime reference and other MP4 files are not accepted.";
    public const int MaxBoxes=10000,MaxDepth=6;
    public const long MaxMoovBytes=16L*1024*1024;
    private static readonly HashSet<string> Brands=new(StringComparer.Ordinal){"M4A ","M4B ","mp41","mp42","isom","iso2","iso3","iso4","iso5","iso6"};
    private static readonly HashSet<string> TopLevel=new(StringComparer.Ordinal){"ftyp","moov","mdat","free","skip","wide","uuid"};
    // Boxes that mean fragments, DRM, compressed or reference movies: refused wherever the walk meets them.
    private static readonly HashSet<string> Refused=new(StringComparer.Ordinal){"moof","mfra","mvex","sidx","ssix","styp","emsg","pssh","sinf","enca","encv","cmov","rmra","rmda"};
    private static readonly int[] AacRates={96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350};

    private sealed class Walk
    {
        public Stream Stream=null!;public CancellationToken Token;public int Boxes;
        public readonly List<(long Start,long End)> Mdat=new();
        public int Moov,Audio;public double? MovieSeconds;
        public M4aInfo? Track;public (long At,long Count,bool Wide)? Offsets;
    }

    public static M4aInfo Inspect(Stream input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek||input.Length<16)throw new UserError(Unsupported);
        var walk=new Walk{Stream=input,Token=token};long at=0,length=input.Length;bool first=true;
        while(at<length)
        {
            var (type,payload,end)=Header(walk,at,length,top:true);
            if(first){if(type!="ftyp")throw new UserError(Unsupported);Ftyp(walk,payload,end);first=false;}
            else if(type=="ftyp"||!TopLevel.Contains(type))throw new UserError(Unsupported);
            else if(type=="moov"){if(++walk.Moov>1||end-payload>MaxMoovBytes)throw new UserError(Unsupported);Children(walk,"moov",payload,end,1);}
            else if(type=="mdat")walk.Mdat.Add((payload,end));
            at=end;
        }
        if(walk.Moov!=1||walk.Mdat.Count==0||walk.Audio!=1||walk.Track is null||walk.Offsets is null||walk.MovieSeconds is null)throw new UserError(Unsupported);
        if(walk.MovieSeconds>7200||walk.Track.Seconds>7200)throw new UserError(Unsupported);
        ChunkOffsets(walk,walk.Offsets.Value);
        input.Position=0;token.ThrowIfCancellationRequested();return walk.Track;
    }

    private static (string Type,long Payload,long End) Header(Walk walk,long at,long limit,bool top=false)
    {
        walk.Token.ThrowIfCancellationRequested();if(++walk.Boxes>MaxBoxes||limit-at<8)throw new UserError(Unsupported);
        Span<byte> head=stackalloc byte[16];Read(walk.Stream,at,head[..8]);
        long size=BinaryPrimitives.ReadUInt32BigEndian(head);string type=Type(head[4..8]);long payload=at+8;
        if(size==1){if(limit-at<16)throw new UserError(Unsupported);Read(walk.Stream,at+8,head[8..16]);ulong large=BinaryPrimitives.ReadUInt64BigEndian(head[8..]);if(large>(ulong)long.MaxValue)throw new UserError(Unsupported);size=(long)large;payload=at+16;if(size<16)throw new UserError(Unsupported);}
        else if(size==0){if(!top||type!="mdat")throw new UserError(Unsupported);size=limit-at;}
        else if(size<8)throw new UserError(Unsupported);
        if(size>limit-at)throw new UserError(Unsupported);
        if(Refused.Contains(type))throw new UserError(Unsupported);
        return(type,payload,at+size);
    }

    private static void Children(Walk walk,string parent,long at,long end,int depth)
    {
        if(depth>MaxDepth)throw new UserError(Unsupported);
        int mvhd=0,mdia=0,mdhd=0,hdlr=0,minf=0,stbl=0,dinf=0,dref=0,stsd=0,stco=0,stsz=0;string? handler=null;M4aInfo? entry=null;double? mediaSeconds=null;(long,long,bool)? offsets=null;
        while(at<end)
        {
            var (type,payload,next)=Header(walk,at,end);
            switch(parent,type)
            {
                case("moov","mvhd"):mvhd++;walk.MovieSeconds=Duration(walk,payload,next);break;
                case("moov","trak"):Children(walk,"trak",payload,next,depth+1);break;
                case("trak","tkhd"):break;
                case("trak","mdia"):mdia++;Children(walk,"mdia",payload,next,depth+1);break;
                case("mdia","mdhd"):mdhd++;break;
                case("mdia","hdlr"):hdlr++;break;
                case("mdia","minf"):minf++;Children(walk,"minf",payload,next,depth+1);break;
                case("minf","dinf"):dinf++;Children(walk,"dinf",payload,next,depth+1);break;
                case("dinf","dref"):dref++;DataReference(walk,payload,next);break;
                case("minf","stbl"):stbl++;Children(walk,"stbl",payload,next,depth+1);break;
                case("stbl","stsd"):stsd++;break;
                case("stbl","stco"):case("stbl","co64"):stco++;break;
                case("stbl","stsz"):case("stbl","stz2"):stsz++;break;
                default:
                    // Track structure boxes are accepted only under their own parent (no nesting games).
                    if(type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "stsd" or "mvhd" or "mdhd" or "hdlr" or "stco" or "co64" or "tkhd" or "dinf" or "dref")throw new UserError(Unsupported);
                    break;
            }
            if(parent=="mdia"&&type=="mdhd")mediaSeconds=Duration(walk,payload,next);
            if(parent=="mdia"&&type=="hdlr")handler=Handler(walk,payload,next);
            if(parent=="stbl"&&type=="stsd")entry=SampleDescription(walk,payload,next,depth+1);
            if(parent=="stbl"&&type is "stco" or "co64")offsets=OffsetTable(walk,payload,next,type=="co64");
            at=next;
        }
        switch(parent)
        {
            case "moov":if(mvhd!=1)throw new UserError(Unsupported);break;
            case "trak":if(mdia!=1)throw new UserError(Unsupported);break;
            case "minf":if(stbl!=1||dinf!=1)throw new UserError(Unsupported);break;
            case "dinf":if(dref!=1)throw new UserError(Unsupported);break;
            case "stbl":
                if(stsd!=1||stco!=1||stsz!=1||entry is null||offsets is null)throw new UserError(Unsupported);
                walk.Track=entry;walk.Offsets=offsets;break;
            case "mdia":
                if(mdhd!=1||hdlr!=1||minf!=1||handler is null||mediaSeconds is null)throw new UserError(Unsupported);
                // Exactly one track in the whole file, and it is sound: video, text or chapter tracks are refused.
                if(handler!="soun"||++walk.Audio>1||walk.Track is null||walk.Offsets is null)throw new UserError(Unsupported);
                walk.Track=walk.Track with{Seconds=mediaSeconds.Value};break;
        }
    }

    private static void Ftyp(Walk walk,long payload,long end)
    {
        long size=end-payload;if(size<8||size>4096||size%4!=0)throw new UserError(Unsupported);
        var bytes=Bytes(walk,payload,end);string major=Type(bytes.AsSpan(0,4));if(major=="qt  ")throw new UserError(Unsupported);
        bool known=Brands.Contains(major);for(int i=8;i<bytes.Length;i+=4)known|=Brands.Contains(Type(bytes.AsSpan(i,4)));
        if(!known)throw new UserError(Unsupported);
    }

    private static double Duration(Walk walk,long payload,long end)
    {
        var b=Bytes(walk,payload,end,256);if(b.Length<4)throw new UserError(Unsupported);
        uint scale;ulong duration;
        if(b[0]==0){if(b.Length<20)throw new UserError(Unsupported);scale=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));duration=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));if(duration==uint.MaxValue)throw new UserError(Unsupported);}
        else if(b[0]==1){if(b.Length<32)throw new UserError(Unsupported);scale=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20));duration=BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24));if(duration==ulong.MaxValue)throw new UserError(Unsupported);}
        else throw new UserError(Unsupported);
        if(scale==0)throw new UserError(Unsupported);double seconds=(double)duration/scale;if(seconds>7200)throw new UserError(Unsupported);return seconds;
    }

    private static string Handler(Walk walk,long payload,long end)
    {var b=Bytes(walk,payload,end,64*1024);if(b.Length<12||b[0]!=0)throw new UserError(Unsupported);return Type(b.AsSpan(8,4));}

    // One self-contained data reference ('url ' with flag 1): the samples are in this file, not elsewhere.
    private static void DataReference(Walk walk,long payload,long end)
    {
        var b=Bytes(walk,payload,end,256);
        if(b.Length!=20||b[0]!=0||BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4))!=1||BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8))!=12||Type(b.AsSpan(12,4))!="url "||BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16))!=1)throw new UserError(Unsupported);
        walk.Boxes++;
    }

    private static (long,long,bool) OffsetTable(Walk walk,long payload,long end,bool wide)
    {
        var b=Bytes(walk,payload,Math.Min(end,payload+8));if(b.Length<8||b[0]!=0)throw new UserError(Unsupported);
        long count=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));if(count==0||count*(wide?8:4)!=end-payload-8)throw new UserError(Unsupported);
        return(payload+8,count,wide);
    }

    private static M4aInfo SampleDescription(Walk walk,long payload,long end,int depth)
    {
        if(depth>MaxDepth)throw new UserError(Unsupported);
        var head=Bytes(walk,payload,Math.Min(end,payload+8));if(head.Length<8||head[0]!=0||BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4))!=1)throw new UserError(Unsupported);
        var (type,entry,entryEnd)=Header(walk,payload+8,end);if(entryEnd!=end||type is not "mp4a" and not "alac"||entryEnd-entry>64*1024)throw new UserError(Unsupported);
        var e=Bytes(walk,entry,entryEnd);
        // SampleEntry (6 reserved, data reference index) + ISO AudioSampleEntry version 0 (28 bytes).
        if(e.Length<28||BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(6))!=1||BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(8))!=0)throw new UserError(Unsupported);
        int channels=BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(16));int entryRate=(int)(BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(24))>>16);
        int at=28,rate=0,configChannels=0;bool config=false;
        while(at<e.Length)
        {
            walk.Token.ThrowIfCancellationRequested();if(++walk.Boxes>MaxBoxes||e.Length-at<8)throw new UserError(Unsupported);
            long size=BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(at));string child=Type(e.AsSpan(at+4,4));
            if(size<8||size>e.Length-at)throw new UserError(Unsupported);if(Refused.Contains(child))throw new UserError(Unsupported);
            var body=e.AsSpan(at+8,(int)size-8);
            if(type=="mp4a"&&child=="esds"){if(config)throw new UserError(Unsupported);(rate,configChannels)=Esds(body);config=true;}
            else if(type=="alac"&&child=="alac"){if(config)throw new UserError(Unsupported);(rate,configChannels)=Alac(body);config=true;}
            at+=(int)size;
        }
        if(!config||channels!=configChannels||channels is not 1 and not 2||rate is <8000 or >192000)throw new UserError(Unsupported);
        if(entryRate!=0&&entryRate!=rate&&!(rate>65535&&type=="alac"))throw new UserError(Unsupported);
        return new(type=="mp4a"?"AAC-LC":"ALAC",rate,channels,0);
    }

    private static (int Rate,int Channels) Alac(ReadOnlySpan<byte> b)
    {
        // Full box header, then ALACSpecificConfig (24 bytes).
        if(b.Length!=28||b[0]!=0)throw new UserError(Unsupported);var c=b[4..];
        if(BinaryPrimitives.ReadUInt32BigEndian(c)==0||c[5] is not 16 and not 20 and not 24 and not 32)throw new UserError(Unsupported);
        return((int)Math.Min(int.MaxValue,BinaryPrimitives.ReadUInt32BigEndian(c[20..])),c[9]);
    }

    private static (int Rate,int Channels) Esds(ReadOnlySpan<byte> b)
    {
        if(b.Length<4||b[0]!=0)throw new UserError(Unsupported);int at=4;
        var es=Descriptor(b,ref at,3);if(es.Length<3)throw new UserError(Unsupported);int flags=es[2],p=3;
        // Stream dependence, URL and OCR stream: a URL would point outside the file.
        if((flags&0x40)!=0)throw new UserError(Unsupported);if((flags&0x80)!=0)p+=2;if((flags&0x20)!=0)p+=2;if(p>es.Length)throw new UserError(Unsupported);
        var dc=Descriptor(es,ref p,4);if(dc.Length<13||dc[0]!=0x40||dc[1]>>2!=5)throw new UserError(Unsupported);
        int q=13;var asc=Descriptor(dc,ref q,5);if(asc.Length<2)throw new UserError(Unsupported);
        ulong bits=0;for(int i=0;i<Math.Min(8,asc.Length);i++)bits|=(ulong)asc[i]<<(56-8*i);
        int aot=(int)(bits>>59);if(aot!=2)throw new UserError(Unsupported);
        int index=(int)(bits>>55)&15,rate,channels;
        if(index==15){if(asc.Length<5)throw new UserError(Unsupported);rate=(int)(bits>>31)&0xffffff;channels=(int)(bits>>27)&15;}
        else{if(index>=AacRates.Length)throw new UserError(Unsupported);rate=AacRates[index];channels=(int)(bits>>51)&15;}
        return(rate,channels);
    }

    private static ReadOnlySpan<byte> Descriptor(ReadOnlySpan<byte> b,ref int at,int tag)
    {
        if(at>=b.Length||b[at]!=tag)throw new UserError(Unsupported);at++;int length=0;
        for(int i=0;;i++){if(i==4||at>=b.Length)throw new UserError(Unsupported);byte v=b[at++];length=(length<<7)|(v&127);if((v&128)==0)break;}
        if(length>b.Length-at)throw new UserError(Unsupported);var body=b.Slice(at,length);at+=length;return body;
    }

    private static void ChunkOffsets(Walk walk,(long At,long Count,bool Wide) table)
    {
        int width=table.Wide?8:4;var buffer=new byte[4096*8];long done=0;
        while(done<table.Count)
        {
            walk.Token.ThrowIfCancellationRequested();int n=(int)Math.Min(4096,table.Count-done);Read(walk.Stream,table.At+done*width,buffer.AsSpan(0,n*width));
            for(int i=0;i<n;i++)
            {
                ulong offset=table.Wide?BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(i*8)):BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i*4));
                bool inside=false;foreach(var (start,end) in walk.Mdat)inside|=offset>=(ulong)start&&offset<(ulong)end;
                if(!inside)throw new UserError(Unsupported);
            }
            done+=n;
        }
    }

    private static byte[] Bytes(Walk walk,long from,long end,int max=64*1024)
    {long n=end-from;if(n<0||n>max)throw new UserError(Unsupported);var b=new byte[n];Read(walk.Stream,from,b);return b;}
    private static void Read(Stream stream,long at,Span<byte> into){stream.Position=at;stream.ReadExactly(into);}
    private static string Type(ReadOnlySpan<byte> b)=>string.Create(4,b.ToArray(),(s,v)=>{for(int i=0;i<4;i++)s[i]=(char)v[i];});
}
