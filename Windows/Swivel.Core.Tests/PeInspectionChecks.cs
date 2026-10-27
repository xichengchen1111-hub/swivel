using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class PeInspectionChecks
{
    internal static byte[] Fixture(bool plus=true,int certificates=0)
    {
        int optional=plus?240:224,baseAt=plus?112:96;byte[] data=new byte[1024+certificates*16];
        void U16(int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at),value);
        void U32(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at),value);
        data[0]=(byte)'M';data[1]=(byte)'Z';U32(60,128);"PE\0\0"u8.CopyTo(data.AsSpan(128));U16(132,plus?(ushort)0x8664:(ushort)0x14c);U16(134,1);U16(148,(ushort)optional);U16(150,2);
        U16(152,plus?(ushort)0x20b:(ushort)0x10b);U16(152+48,6);U16(152+68,2);U32(152+60,512);U32(152+baseAt-4,16);
        int section=152+optional;U32(section+16,512);U32(section+20,512);
        if(certificates>0)
        {
            U32(152+baseAt+32,1024);U32(152+baseAt+36,(uint)(certificates*16));
            for(int i=0;i<certificates;i++){U32(1024+i*16,9);U16(1028+i*16,0x200);U16(1030+i*16,2);}
        }
        return data;
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        PeStructure Read(byte[] bytes){using var stream=new MemoryStream(bytes);return PeInspection.Read(stream,default);}
        foreach(bool plus in new[]{false,true})foreach(int count in new[]{0,1,2,64,65})
        {
            byte[] bytes=Fixture(plus,count);using var source=new CountInput(bytes);
            var info=PeInspection.Read(source,default);check(info.Pe32Plus==plus&&info.Machine==(plus?0x8664:0x14c)&&info.Subsystem==2&&info.Sections==1&&info.Characteristics==2&&info.SubsystemMajor==6,"PE32/PE32+ declared fields read at correct distinct offsets");
            check(info.Certificates.Headers.Count==Math.Min(count,64)&&info.Certificates.State==(count==0?PeCertificateState.NotDeclared:count<=64?PeCertificateState.HeadersCoverTable:PeCertificateState.RecordLimit),"Absent/complete/exact64/65partial certificate header states stay distinct");
            check(source.ReadBytes<=PeInspection.MaxReadBytes&&source.MaxRead<=4096&&source.CanRead&&source.ToArray().SequenceEqual(bytes),"Inspector has fixed header-only read budget and preserves source bytes/ownership");
            check(source.PayloadReads==0,"No raw section or certificate payload is read");
            foreach(var h in info.Certificates.Headers)check(h.Length==9&&h.Revision==0x200&&h.Type==2&&(h.Offset&7)==0,"Certificate declared length rounded to8 only for next record, raw fields preserved");
            int directoryBase=152+(plus?112:96);byte[] changed=(byte[])bytes.Clone();BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(132),0xffff);
            check(Read(changed).Machine==0xffff,"Unknown machine code is preserved without compatibility inference");
            BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(directoryBase+112),0x2000);BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(directoryBase+116),72);
            check(Read(changed).ClrDirectory==PeDirectoryDeclaration.Declared,"CLR directory presence is a declaration only; RVA never followed");
            BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(directoryBase+116),0);
            check(Read(changed).ClrDirectory==PeDirectoryDeclaration.Inconsistent,"Half-empty CLR declaration is not absent");
        }
        void ChangeCertificate(uint offset,uint size,PeCertificateState expected)
        {var bytes=Fixture(true,1);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(152+112+32),offset);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(152+112+36),size);var info=Read(bytes);check(info.Machine==0x8664&&info.Certificates.State==expected,"Invalid certificate declarations preserve independent header facts and partial status");}
        ChangeCertificate(0,16,PeCertificateState.Inconsistent);ChangeCertificate(1024,0,PeCertificateState.Inconsistent);ChangeCertificate(1025,8,PeCertificateState.Inconsistent);ChangeCertificate(1024,15,PeCertificateState.Inconsistent);ChangeCertificate(uint.MaxValue-7,16,PeCertificateState.OutOfBounds);ChangeCertificate(8,16,PeCertificateState.OverlapsImage);ChangeCertificate(512,16,PeCertificateState.OverlapsImage);
        foreach(uint size in new[]{0u,1u,7u,17u,uint.MaxValue})
        {var bytes=Fixture(true,1);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1024),size);check(Read(bytes).Certificates.State==PeCertificateState.Inconsistent,"Record size cannot underflow, exceed table or wrap padded advancement");}
        {var bytes=Fixture(true,1);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1028),0xfedc);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1030),0xffff);var header=Read(bytes).Certificates.Headers.Single();check(header.Revision==0xfedc&&header.Type==0xffff,"Unknown certificate revision/type raw values retained without verification");}
        foreach(int field in new[]{60,128,148,152,152+60,152+108,392+16,392+20})
        {var bytes=Fixture();BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field),uint.MaxValue);reject(()=>Read(bytes),"Malformed offsets/header dimensions and section ranges refused without overflow");}
        using(var source=new CountInput(Fixture(true,65),shortReads:true)){var info=PeInspection.Read(source,default);check(info.Certificates.Headers.Count==64&&source.ReadBytes<=8536,"One-byte short reads preserve complete bounded success-byte accounting");}
        using(var source=new MemoryStream(Fixture()))reject(()=>PeInspection.Read(source,new CancellationToken(true)),"Pre-cancelled header inspection reads no structure");
        using(var cancellation=new CancellationTokenSource())using(var source=new CountInput(Fixture(),cancellation:cancellation))reject(()=>PeInspection.Read(source,cancellation.Token),"Mid-read cancellation refuses incomplete result");
        var prefs=new Prefs();check(!Catalog.Offered(prefs).Any(x=>x.Id=="doctor.executable"),"EXE inspection stays behind default-off PC Doctor");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.executable"),"EXE inspection uses its own explicit picker, not unrelated selected files");
    }
    private sealed class CountInput(byte[] bytes,bool shortReads=false,CancellationTokenSource? cancellation=null):MemoryStream(bytes)
    {
        public int ReadBytes{get;private set;}public int MaxRead{get;private set;}public int PayloadReads{get;private set;}
        public override int Read(Span<byte> buffer)
        {
            long at=Position;MaxRead=Math.Max(MaxRead,buffer.Length);int n=base.Read(shortReads?buffer[..Math.Min(1,buffer.Length)]:buffer);ReadBytes+=n;
            if(n>0&&(at is >=512 and <1024||at>=1024&&(at-1024)%16>=8))PayloadReads++;
            if(ReadBytes>64)cancellation?.Cancel();return n;
        }
    }
}
