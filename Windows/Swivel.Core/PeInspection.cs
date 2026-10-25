using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public enum PeDirectoryDeclaration{NotDeclared,Declared,Inconsistent}
public enum PeCertificateState{NotDeclared,HeadersCoverTable,Inconsistent,OutOfBounds,OverlapsImage,RecordLimit}
public sealed record PeCertificateHeader(long Offset,uint Length,ushort Revision,ushort Type);
public sealed record PeCertificateTable(uint Offset,uint Bytes,PeCertificateState State,IReadOnlyList<PeCertificateHeader> Headers);
public sealed record PeStructure(long FileBytes,bool Pe32Plus,ushort Machine,ushort Subsystem,ushort Characteristics,int Sections,ushort SubsystemMajor,ushort SubsystemMinor,PeDirectoryDeclaration ClrDirectory,PeCertificateTable Certificates);

// Header declarations only: never a loader, ASN.1 parser, Authenticode verifier,
// architecture compatibility decision or signature-trust/malware assessment.
public static class PeInspection
{
    public const long MaxBytes=512L*1024*1024;
    public const int MaxPeOffset=1024*1024,MaxOptionalBytes=4096,MaxSections=96,MaxCertificateHeaders=64,MaxReadBytes=8536;
    public const string Limits="This read-only inspector supports PE32/PE32+ EXE files up to 512 MiB, with the PE header in the first 1 MiB, up to 4 KiB of optional header and 96 sections. DOS, NE, LE, COFF objects and larger structures are not supported here.";
    public const string Invalid="The PE headers or declared section ranges are incomplete or inconsistent. This limited inspection cannot read the structure; it is not a complete Windows loader diagnosis.";
    public static PeStructure Read(Stream input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A readable seekable stream is required.");
        long length=input.Length;if(length>MaxBytes)throw new UserError(Limits);if(length<64)throw new UserError(Invalid);
        byte[] ReadAt(long at,int count)
        {
            token.ThrowIfCancellationRequested();if(at<0||at>length||count<0||count>MaxOptionalBytes||count>length-at)throw new UserError(Invalid);
            var bytes=new byte[count];input.Position=at;input.ReadExactly(bytes);token.ThrowIfCancellationRequested();return bytes;
        }
        static ushort U16(byte[] data,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at,2));
        static uint U32(byte[] data,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4));
        byte[] dos=ReadAt(0,64);if(!dos.AsSpan(0,2).SequenceEqual("MZ"u8))throw new UserError(Invalid);
        long pe=U32(dos,60);if(pe<64)throw new UserError(Invalid);if(pe>MaxPeOffset)throw new UserError(Limits);
        byte[] coff=ReadAt(pe,24);if(!coff.AsSpan(0,4).SequenceEqual("PE\0\0"u8))throw new UserError(Invalid);
        ushort machine=U16(coff,4),sections=U16(coff,6),optionalBytes=U16(coff,20),characteristics=U16(coff,22);
        if(sections>MaxSections||optionalBytes>MaxOptionalBytes)throw new UserError(Limits);
        if(optionalBytes<2)throw new UserError(Invalid);byte[] optional=ReadAt(pe+24,optionalBytes);
        ushort magic=U16(optional,0);if(magic is not 0x10b and not 0x20b)throw new UserError(Limits);
        bool plus=magic==0x20b;int directoryBase=plus?112:96;if(optionalBytes<directoryBase)throw new UserError(Invalid);
        uint directories=U32(optional,directoryBase-4);if(directories>(optionalBytes-directoryBase)/8)throw new UserError(Invalid);
        long sectionAt=pe+24+optionalBytes,headerEnd=sectionAt+sections*40L;
        byte[] sectionHeaders=ReadAt(sectionAt,sections*40);uint sizeOfHeaders=U32(optional,60);
        if(sizeOfHeaders<headerEnd||sizeOfHeaders>length)throw new UserError(Invalid);
        var ranges=new (long First,long End)[sections];
        for(int i=0;i<sections;i++)
        {
            uint bytes=U32(sectionHeaders,i*40+16),first=U32(sectionHeaders,i*40+20);long end=(long)first+bytes;
            if(bytes!=0&&(first<sizeOfHeaders||end>length))throw new UserError(Invalid);
            ranges[i]=(first,end);
        }
        PeDirectoryDeclaration clr=PeDirectoryDeclaration.NotDeclared;
        if(directories>=15)
        {
            uint rva=U32(optional,directoryBase+14*8),bytes=U32(optional,directoryBase+14*8+4);
            clr=rva==0&&bytes==0?PeDirectoryDeclaration.NotDeclared:rva!=0&&bytes!=0?PeDirectoryDeclaration.Declared:PeDirectoryDeclaration.Inconsistent;
        }
        uint certificateAt=directories>=5?U32(optional,directoryBase+4*8):0,certificateBytes=directories>=5?U32(optional,directoryBase+4*8+4):0;
        var headers=new List<PeCertificateHeader>();
        PeCertificateState WalkCertificates()
        {
            if(certificateAt==0&&certificateBytes==0)return PeCertificateState.NotDeclared;
            if(certificateAt==0||certificateBytes==0||(certificateAt&7)!=0||(certificateBytes&7)!=0)return PeCertificateState.Inconsistent;
            long end=(long)certificateAt+certificateBytes;if(end>length)return PeCertificateState.OutOfBounds;
            if(certificateAt<sizeOfHeaders)return PeCertificateState.OverlapsImage;
            foreach(var range in ranges)if(range.End>range.First&&certificateAt<range.End&&end>range.First)return PeCertificateState.OverlapsImage;
            long at=certificateAt;
            while(at<end)
            {
                token.ThrowIfCancellationRequested();if(headers.Count==MaxCertificateHeaders)return PeCertificateState.RecordLimit;
                if(end-at<8)return PeCertificateState.Inconsistent;byte[] header=ReadAt(at,8);uint bytes=U32(header,0);
                long step=((long)bytes+7)&~7L;if(bytes<8||step>end-at)return PeCertificateState.Inconsistent;
                headers.Add(new(at,bytes,U16(header,4),U16(header,6)));at+=step;
            }
            return PeCertificateState.HeadersCoverTable;
        }
        var state=WalkCertificates();token.ThrowIfCancellationRequested();
        return new(length,plus,machine,U16(optional,68),characteristics,sections,U16(optional,48),U16(optional,50),clr,new(certificateAt,certificateBytes,state,Array.AsReadOnly(headers.ToArray())));
    }
}
