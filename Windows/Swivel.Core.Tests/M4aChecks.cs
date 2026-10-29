using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

// Hand-built audio-only M4A files for the box gate in front of the Windows decoder.
internal static class M4aChecks
{
    internal sealed record Spec
    {
        public string Codec{get;init;}="mp4a";public int Channels{get;init;}=2;public int Rate{get;init;}=44100;public int EntryRate{get;init;}=-1;
        public string Major{get;init;}="M4A ";public string[] Compatible{get;init;}=new[]{"M4A ","isom"};
        public string[] Handlers{get;init;}=new[]{"soun"};public byte Aot{get;init;}=2;public byte EsFlags{get;init;}=0;public uint DrefFlags{get;init;}=1;
        public ulong MovieDuration{get;init;}=1000;public bool Wide{get;init;}=false;public bool MdatToEnd{get;init;}=false;public long OffsetShift{get;init;}=0;
        public byte[][] MoovExtra{get;init;}=Array.Empty<byte[]>();public byte[][] TopExtra{get;init;}=Array.Empty<byte[]>();public byte[][] EntryExtra{get;init;}=Array.Empty<byte[]>();
        public byte[][] StblExtra{get;init;}=Array.Empty<byte[]>();public byte[][] TrakExtra{get;init;}=Array.Empty<byte[]>();
    }

    internal static byte[] Box(string type,params byte[][] parts)
    {
        int n=8+parts.Sum(p=>p.Length);var b=new byte[n];BinaryPrimitives.WriteUInt32BigEndian(b,(uint)n);Encoding.ASCII.GetBytes(type).CopyTo(b,4);int at=8;foreach(var p in parts){p.CopyTo(b,at);at+=p.Length;}return b;
    }
    static byte[] U32(uint v){var b=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(b,v);return b;}
    static byte[] U16(int v){var b=new byte[2];BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)v);return b;}
    static byte[] U64(ulong v){var b=new byte[8];BinaryPrimitives.WriteUInt64BigEndian(b,v);return b;}
    static byte[] Ascii(string s)=>Encoding.ASCII.GetBytes(s);
    static byte[] Z(int n)=>new byte[n];
    static byte[] Cat(params byte[][] parts)=>parts.SelectMany(p=>p).ToArray();
    static byte[] Desc(int tag,byte[] body)=>Cat(new[]{(byte)tag,(byte)0x80,(byte)0x80,(byte)0x80,(byte)body.Length},body);

    static byte[] Entry(Spec s)
    {
        int entryRate=s.EntryRate>=0?s.EntryRate:s.Rate;
        var head=Cat(Z(6),U16(1),U16(0),U16(0),U32(0),U16(s.Channels),U16(16),U16(0),U16(0),U32((uint)(entryRate<<16)));
        byte[] config;
        if(s.Codec is "alac")config=Box("alac",Cat(U32(0),U32(4096),new byte[]{0,16,40,10,14,(byte)s.Channels},U16(255),U32(0),U32(0),U32((uint)s.Rate)));
        else
        {
            int[] rates={96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350};int index=Array.IndexOf(rates,s.Rate);
            byte[] asc=index>=0?new[]{(byte)((s.Aot<<3)|(index>>1)),(byte)(((index&1)<<7)|(s.Channels<<3))}
                :new[]{(byte)((s.Aot<<3)|7),(byte)(0x80|(s.Rate>>17)),(byte)(s.Rate>>9),(byte)(s.Rate>>1),(byte)(((s.Rate&1)<<7)|(s.Channels<<3))};
            var dc=Cat(new byte[]{0x40,0x15},Z(3),U32(128000),U32(128000),Desc(5,asc));
            var es=Cat(U16(1),new[]{s.EsFlags},(s.EsFlags&0x40)!=0?Cat(new byte[]{4},Ascii("http")):Array.Empty<byte>(),Desc(4,dc),Desc(6,new byte[]{2}));
            config=Box("esds",U32(0),Desc(3,es));
        }
        return Box(s.Codec,new[]{head,config}.Concat(s.EntryExtra).ToArray());
    }

    static byte[] Trak(Spec s,string handler,uint offset)
    {
        var entry=handler=="vide"?Box("avc1",Z(78)):Entry(s);
        var stbl=Box("stbl",new[]{Box("stsd",U32(0),U32(1),entry),Box("stts",U32(0),U32(0)),Box("stsc",U32(0),U32(0)),Box("stsz",U32(0),U32(0),U32(0)),
            s.Wide?Box("co64",U32(0),U32(1),U64(offset)):Box("stco",U32(0),U32(1),U32(offset))}.Concat(s.StblExtra).ToArray());
        var minf=Box("minf",Box("smhd",Z(8)),Box("dinf",Box("dref",U32(0),U32(1),Box("url ",U32(s.DrefFlags)))),stbl);
        var mdia=Box("mdia",Box("mdhd",U32(0),U32(0),U32(0),U32((uint)s.Rate),U32((uint)s.Rate),Z(4)),Box("hdlr",U32(0),U32(0),Ascii(handler),Z(12),Z(1)),minf);
        return Box("trak",new[]{Box("tkhd",Z(84)),mdia}.Concat(s.TrakExtra).ToArray());
    }

    internal static byte[] Build(Spec s)
    {
        byte[] Make(uint offset)
        {
            var ftyp=Box("ftyp",new[]{Ascii(s.Major),U32(0)}.Concat(s.Compatible.Select(Ascii)).ToArray());
            var mvhd=s.MovieDuration>uint.MaxValue?Box("mvhd",new byte[]{1,0,0,0},U64(0),U64(0),U32(1000),U64(s.MovieDuration),Z(80)):Box("mvhd",U32(0),U32(0),U32(0),U32(1000),U32((uint)s.MovieDuration),Z(80));
            var moov=Box("moov",new[]{mvhd}.Concat(s.Handlers.Select(h=>Trak(s,h,offset))).Concat(s.MoovExtra).ToArray());
            var payload=Enumerable.Range(0,64).Select(i=>(byte)i).ToArray();var mdat=Box("mdat",payload);if(s.MdatToEnd)BinaryPrimitives.WriteUInt32BigEndian(mdat,0);
            return Cat(new[]{ftyp,moov}.Concat(s.TopExtra).Append(mdat).ToArray());
        }
        var first=Make(0);long mdatPayload=first.Length-64;return Make((uint)(mdatPayload+s.OffsetShift));
    }

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        void Refuse(byte[] bytes,string name)
        {
            using var input=new MemoryStream(bytes,false);bool refused=false;
            try{M4aAudio.Inspect(input,default);}catch(UserError){refused=true;}
            check(refused,"M4A gate refuses with UserError: "+name);
        }
        M4aInfo Accept(Spec s){using var input=new MemoryStream(Build(s),false);var info=M4aAudio.Inspect(input,default);check(input.Position==0,"M4A gate rewinds");return info;}

        var aac=Accept(new Spec());check(aac==new M4aInfo("AAC-LC",44100,2,1),"Valid stereo AAC-LC M4A facts from esds/mdhd");
        using(var input=new MemoryStream(Build(new Spec()),false))check(SystemAudioInput.Identify(input,default)=="M4A AAC-LC"&&input.Position==0,"System decoder gate identifies M4A AAC-LC");
        check(Accept(new Spec{Channels=1,Rate=8000})==new M4aInfo("AAC-LC",8000,1,1),"Mono 8 kHz AAC-LC lower bound");
        check(Accept(new Spec{Codec="alac",Rate=96000,EntryRate=0,Channels=1})==new M4aInfo("ALAC",96000,1,1),"ALAC 96 kHz mono from ALACSpecificConfig");
        check(Accept(new Spec{Codec="alac",Rate=192000,EntryRate=0}).Rate==192000,"ALAC 192 kHz upper bound");
        using(var input=new MemoryStream(Build(new Spec{Codec="alac"}),false))check(SystemAudioInput.Identify(input,default)=="M4A ALAC","System decoder gate identifies M4A ALAC");
        check(Accept(new Spec{Major="mp42",Compatible=new[]{"isom","mp42"}}).Codec=="AAC-LC","Generic mp42/isom brands accepted");
        check(Accept(new Spec{Wide=true}).Codec=="AAC-LC","co64 chunk offsets accepted");
        check(Accept(new Spec{MdatToEnd=true}).Codec=="AAC-LC","Final mdat extending to end of file accepted");
        check(Accept(new Spec{TopExtra=new[]{Box("free",Z(32)),Box("uuid",Z(16))},MoovExtra=new[]{Box("udta",Box("meta",Z(40)))}}).Codec=="AAC-LC","free/uuid/udta metadata skipped");
        check(Accept(new Spec{Rate=48000,Aot=2}).Rate==48000,"AAC escape-free 48 kHz");

        Refuse(Build(new Spec{Handlers=new[]{"soun","vide"}}),"video track beside audio");
        Refuse(Build(new Spec{Handlers=new[]{"vide"}}),"video only");
        Refuse(Build(new Spec{Handlers=new[]{"soun","soun"}}),"two audio tracks");
        Refuse(Build(new Spec{Handlers=new[]{"soun","text"}}),"text/chapter track");
        Refuse(Build(new Spec{Handlers=Array.Empty<string>()}),"no track");
        Refuse(Build(new Spec{Codec="enca"}),"DRM enca sample entry");
        Refuse(Build(new Spec{EntryExtra=new[]{Box("sinf",Box("frma",Ascii("mp4a")))}}),"protection sinf in sample entry");
        Refuse(Build(new Spec{MoovExtra=new[]{Box("pssh",Z(24))}}),"pssh in moov");
        Refuse(Build(new Spec{MoovExtra=new[]{Box("mvex",Box("trex",Z(24)))}}),"fragmented mvex");
        Refuse(Build(new Spec{TopExtra=new[]{Box("moof",Box("mfhd",Z(8)))}}),"top-level moof fragment");
        Refuse(Build(new Spec{TopExtra=new[]{Box("sidx",Z(24))}}),"segment index");
        Refuse(Build(new Spec{Major="qt  ",Compatible=new[]{"qt  ","isom"}}),"QuickTime major brand");
        Refuse(Build(new Spec{Major="avc1",Compatible=new[]{"dash"}}),"no known brand");
        Refuse(Build(new Spec{DrefFlags=0}),"external data reference");
        Refuse(Build(new Spec{EsFlags=0x40}),"URL ES descriptor");
        Refuse(Build(new Spec{Aot=5}),"HE-AAC object type 5 (not AAC-LC)");
        Refuse(Build(new Spec{Aot=1}),"AAC Main object type");
        Refuse(Build(new Spec{Channels=3}),"three channels");
        Refuse(Build(new Spec{Rate=7350}),"7350 Hz below 8 kHz");
        Refuse(Build(new Spec{Codec="alac",Rate=384000,EntryRate=0}),"ALAC 384 kHz");
        Refuse(Build(new Spec{EntryRate=48000}),"sample entry rate disagrees with esds");
        Refuse(Build(new Spec{Codec="Opus"}),"other codec sample entry");
        Refuse(Build(new Spec{MovieDuration=7201000}),"movie longer than two hours");
        Refuse(Build(new Spec{MovieDuration=uint.MaxValue}),"unknown duration");
        Refuse(Build(new Spec{MovieDuration=(ulong)uint.MaxValue+1}),"64-bit movie duration over two hours");
        Refuse(Build(new Spec{OffsetShift=64}),"chunk offset outside mdat");
        Refuse(Build(new Spec{OffsetShift=-200}),"chunk offset before mdat");
        Refuse(Build(new Spec{TrakExtra=new[]{Box("trak",Box("tkhd",Z(84)))}}),"trak nested inside trak");
        Refuse(Build(new Spec{StblExtra=new[]{Box("stbl",Z(8))}}),"stbl nested inside stbl");
        Refuse(Build(new Spec{StblExtra=new[]{Box("stsd",U32(0),U32(0))}}),"second stsd");
        Refuse(Build(new Spec{MoovExtra=new[]{Box("moov",Z(8))}}),"moov inside moov");
        Refuse(Build(new Spec{TopExtra=new[]{Box("ftyp",Ascii("M4A "),U32(0))}}),"second ftyp");
        Refuse(Build(new Spec{TopExtra=new[]{Box("abcd",Z(8))}}),"unknown top-level box");
        Refuse(Cat(Build(new Spec()),Box("moov",Z(8))),"second moov");

        var valid=Build(new Spec());
        Refuse(valid[..^1],"truncated file (mdat beyond end)");
        Refuse(valid[..40],"truncated inside moov");
        var small=valid.ToArray();BinaryPrimitives.WriteUInt32BigEndian(small.AsSpan(valid.Length-72),4);Refuse(small,"box size below header");
        var large=valid.ToArray();BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(valid.Length-72),uint.MaxValue);Refuse(large,"box size beyond file");
        var huge=Cat(valid[..^72],U32(1),Ascii("mdat"),U64(ulong.MaxValue),Z(8));Refuse(huge,"64-bit box size beyond file");
        var zero=valid.ToArray();int moovAt=BinaryPrimitives.ReadInt32BigEndian(zero);BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(moovAt),0);Refuse(zero,"size 0 only for final mdat");
        Refuse(Cat(Box("free",Z(8)),valid),"ftyp not first");
        Refuse(Build(new Spec{MoovExtra=Enumerable.Range(0,M4aAudio.MaxBoxes).Select(_=>Box("free")).ToArray()}),"box count limit");
        check(Accept(new Spec{MoovExtra=Enumerable.Range(0,M4aAudio.MaxBoxes-40).Select(_=>Box("free")).ToArray()}).Codec=="AAC-LC","Box count below limit accepted");
        using(var input=new MemoryStream(valid,false))reject(()=>M4aAudio.Inspect(input,new CancellationToken(true)),"Pre-cancelled M4A gate");
        using(var input=new MemoryStream(Box("ftyp",Ascii("M4A "),U32(0)),false))reject(()=>SystemAudioInput.Identify(input,default),"Bare ftyp refused");

        // Random corruption: every mutated file is either accepted with in-range facts or refused as a UserError.
        var rng=new Random(4711);int accepted=0;
        foreach(var seed in new[]{valid,Build(new Spec{Codec="alac",Rate=96000,EntryRate=0}),Build(new Spec{Wide=true})})
            for(int round=0;round<3000;round++)
            {
                var bytes=seed.ToArray();int edits=1+rng.Next(4);for(int e=0;e<edits;e++)bytes[rng.Next(bytes.Length)]=(byte)rng.Next(256);
                if(rng.Next(8)==0)bytes=bytes[..rng.Next(bytes.Length)];
                using var input=new MemoryStream(bytes,false);bool ok;
                try{var info=M4aAudio.Inspect(input,default);ok=info.Channels is 1 or 2&&info.Rate is >=8000 and <=192000&&info.Seconds is >=0 and <=7200;accepted++;}
                catch(UserError){ok=true;}catch{ok=false;}
                check(ok,"Mutated M4A accepted in range or refused cleanly");
            }
        check(accepted>0,"Some harmless mutations still pass the gate");
    }
}
