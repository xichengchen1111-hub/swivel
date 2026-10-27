using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record JpegMetadataSegment(int Marker,int Count,long Bytes,bool Removed);

// Original bounded container editor, not an entropy decoder or complete JPEG
// conformance checker. Native delivery additionally validates the clean snapshot.
public static class JpegMetadata
{
    public const int MaxBytes=32*1024*1024,MaxMarkers=65536,MaxScans=512;
    public const string Invalid="Use a structurally supported 8-bit baseline or progressive JPEG, at most 32 MiB, 65,536 markers and 512 scans. Damaged framing, unsupported coding and trailing data are refused.";
    public const string Extended="APP11 and non-ICC APP2 JPEG blocks are not supported by this metadata tool. They may carry extended images or credentials. No copy was saved.";
    public const string Icc="JPEG ICC chunks must precede image scans and have unique, complete sequence numbers with one consistent total. No copy was saved.";
    internal sealed record Piece(int Marker,int Offset,int Length,bool Removed);
    public sealed class Plan
    {
        private readonly byte[] source;
        private readonly Piece[] pieces;
        public int Width{get;}
        public int Height{get;}
        public bool Progressive{get;}
        public int ScanCount{get;}
        public int MarkerCount{get;}
        public int RestartCount{get;}
        public long SourceBytes=>source.Length;
        public long RemovedBytes{get;}
        public long OutputBytes=>SourceBytes-RemovedBytes;
        public int RemovedCount{get;}
        public IReadOnlyList<JpegMetadataSegment> Segments{get;}
        private Plan(byte[] source,Piece[] pieces,int width,int height,bool progressive,int scans,int markers,int restarts)
        {
            this.source=source;this.pieces=pieces;Width=width;Height=height;Progressive=progressive;ScanCount=scans;MarkerCount=markers;RestartCount=restarts;
            RemovedBytes=pieces.Where(p=>p.Removed).Sum(p=>(long)p.Length);RemovedCount=pieces.Count(p=>p.Removed);
            Segments=Array.AsReadOnly(pieces.GroupBy(p=>p.Marker).Select(g=>new JpegMetadataSegment(g.Key,g.Count(),g.Sum(p=>(long)p.Length),g.First().Removed)).ToArray());
        }
        internal static Plan Create(byte[] source,Piece[] pieces,int width,int height,bool progressive,int scans,int markers,int restarts)=>new(source,pieces,width,height,progressive,scans,markers,restarts);
        public void Write(Stream output,CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(output);token.ThrowIfCancellationRequested();
            if(!output.CanWrite)throw new ArgumentException("A writable output stream is required.",nameof(output));
            foreach(var piece in pieces)
            {
                token.ThrowIfCancellationRequested();if(piece.Removed)continue;
                for(int at=0;at<piece.Length;)
                {token.ThrowIfCancellationRequested();int count=Math.Min(65536,piece.Length-at);output.Write(source.AsSpan(piece.Offset+at,count));at+=count;}
            }
            token.ThrowIfCancellationRequested();
        }
    }
    public static Plan Read(Stream input,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A readable seekable input stream is required.",nameof(input));
        long length=input.Length;if(length is <28 or >MaxBytes)throw new UserError(Invalid);
        var bytes=new byte[(int)length];input.Position=0;
        try{for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int count=Math.Min(65536,bytes.Length-at);input.ReadExactly(bytes.AsSpan(at,count));at+=count;}}
        catch(EndOfStreamException){throw new UserError(Invalid);}
        token.ThrowIfCancellationRequested();if(bytes[0]!=255||bytes[1]!=0xd8)throw new UserError(Invalid);
        var pieces=new List<Piece>{new(0xd8,0,2,false)};var components=new HashSet<byte>();
        int position=2,width=0,height=0,scans=0,markers=1,restarts=0,restartInterval=0,expectedRestart=0,iccTotal=0;
        bool progressive=false,entropy=false,ended=false;var iccParts=new HashSet<int>();
        void CountMarker(){token.ThrowIfCancellationRequested();if(++markers>MaxMarkers)throw new UserError(Invalid);}
        while(position<bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if(entropy)
            {
                int start=position;
                while(position<bytes.Length)
                {
                    if((position&65535)==0)token.ThrowIfCancellationRequested();
                    if(bytes[position]!=255){position++;continue;}
                    int fill=position;while(position<bytes.Length&&bytes[position]==255){if((position&65535)==0)token.ThrowIfCancellationRequested();position++;}
                    if(position==bytes.Length)throw new UserError(Invalid);
                    byte code=bytes[position];
                    if(code==0){if(position!=fill+1)throw new UserError(Invalid);position++;continue;}
                    if(code is >=0xd0 and <=0xd7)
                    {
                        if(restartInterval==0||code!=0xd0+expectedRestart)throw new UserError(Invalid);
                        CountMarker();restarts++;expectedRestart=(expectedRestart+1)&7;position++;continue;
                    }
                    // Fill bytes before a real marker belong to that marker, not
                    // the compressed scan. Retained scan bytes are never rewritten.
                    position=fill;break;
                }
                if(position==start||position==bytes.Length)throw new UserError(Invalid);
                pieces.Add(new(-1,start,position-start,false));entropy=false;continue;
            }
            int markerStart=position;if(bytes[position]!=255)throw new UserError(Invalid);
            while(position<bytes.Length&&bytes[position]==255){if((position&65535)==0)token.ThrowIfCancellationRequested();position++;}
            if(position==bytes.Length)throw new UserError(Invalid);
            int marker=bytes[position++];CountMarker();
            if(marker==0xd9)
            {
                if(width==0||scans==0||position!=bytes.Length)throw new UserError(Invalid);
                pieces.Add(new(marker,markerStart,position-markerStart,false));ended=true;break;
            }
            if(marker is not (0xc0 or 0xc2 or 0xc4 or 0xda or 0xdb or 0xdd or 0xfe) && marker is not (>=0xe0 and <=0xef))throw new UserError(Invalid);
            if(bytes.Length-position<2)throw new UserError(Invalid);
            int size=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position,2));
            if(size<2||size>bytes.Length-position)throw new UserError(Invalid);
            int dataStart=position+2,payload=size-2,segmentEnd=position+size;
            if(marker==0xeb)throw new UserError(Extended);
            if(marker==0xe2)
            {
                if(payload<14||!bytes.AsSpan(dataStart,12).SequenceEqual("ICC_PROFILE\0"u8))throw new UserError(Extended);
                int sequence=bytes[dataStart+12],total=bytes[dataStart+13];
                if(scans!=0||total==0||sequence==0||sequence>total||(iccTotal!=0&&iccTotal!=total)||!iccParts.Add(sequence))throw new UserError(Icc);
                iccTotal=total;
            }
            if(marker is 0xc0 or 0xc2)
            {
                if(width!=0||scans!=0||payload<6||bytes[dataStart]!=8)throw new UserError(Invalid);
                height=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(dataStart+1,2));width=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(dataStart+3,2));
                int count=bytes[dataStart+5];
                if(width is <1 or >16384||height is <1 or >16384||(long)width*height>ImageRules.MaxPixels||count is not (1 or 3)||payload!=6+3*count)throw new UserError(Invalid);
                int blocks=0;
                for(int i=0;i<count;i++)
                {
                    int at=dataStart+6+3*i,h=bytes[at+1]>>4,v=bytes[at+1]&15;
                    if(!components.Add(bytes[at])||h is <1 or >4||v is <1 or >4||bytes[at+2]>3)throw new UserError(Invalid);
                    blocks+=h*v;
                }
                if(blocks>10)throw new UserError(Invalid);progressive=marker==0xc2;
            }
            else if(marker==0xda)
            {
                if(width==0||payload<4||++scans>MaxScans)throw new UserError(Invalid);
                int count=bytes[dataStart];if(count<1||count>components.Count||payload!=4+2*count)throw new UserError(Invalid);
                var selected=new HashSet<byte>();
                for(int i=0;i<count;i++)
                {
                    int at=dataStart+1+2*i;
                    if(!components.Contains(bytes[at])||!selected.Add(bytes[at])||(bytes[at+1]>>4)>3||(bytes[at+1]&15)>3)throw new UserError(Invalid);
                }
                int ss=bytes[dataStart+1+2*count],se=bytes[dataStart+2+2*count],ah=bytes[dataStart+3+2*count]>>4,al=bytes[dataStart+3+2*count]&15;
                if(!progressive){if(ss!=0||se!=63||ah!=0||al!=0)throw new UserError(Invalid);}
                else if(ss>63||se>63||ss>se||(ss==0&&se!=0)||(ss!=0&&count!=1)||ah>13||al>13||(ah!=0&&ah!=al+1))throw new UserError(Invalid);
                entropy=true;expectedRestart=0;
            }
            else if(marker==0xdd)
            {if(payload!=2)throw new UserError(Invalid);restartInterval=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(dataStart,2));}
            pieces.Add(new(marker,markerStart,segmentEnd-markerStart,marker is 0xe1 or 0xed or 0xfe));position=segmentEnd;
        }
        if(!ended||(iccTotal!=0&&iccParts.Count!=iccTotal))throw new UserError(iccTotal!=0&&iccParts.Count!=iccTotal?Icc:Invalid);
        token.ThrowIfCancellationRequested();return Plan.Create(bytes,pieces.ToArray(),width,height,progressive,scans,markers,restarts);
    }
    public static string MarkerName(int marker)=>marker switch
    {
        -1=>"Compressed scan data",0xd8=>"SOI",0xd9=>"EOI",0xc0=>"SOF0",0xc2=>"SOF2",0xc4=>"DHT",0xdb=>"DQT",0xdd=>"DRI",0xda=>"SOS",0xfe=>"COM",
        >=0xe0 and <=0xef=>"APP"+(marker-0xe0),_=>throw new ArgumentOutOfRangeException(nameof(marker))
    };
}
