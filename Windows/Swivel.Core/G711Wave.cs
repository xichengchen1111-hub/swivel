using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record G711Info(int Rate,int Channels,bool MuLaw,long DataOffset,long DataBytes)
{
    public long Frames=>DataBytes/Channels;
    public double Seconds=>(double)Frames/Rate;
    public long OutputBytes()
    {
        if(Rate is <8000 or >192000||Channels is not 1 and not 2||DataBytes<=0||DataBytes%Channels!=0)throw new UserError(G711Wave.Invalid);
        if(DataBytes>(PcmWave.MaxBytes-44)/2||Seconds>PcmWave.MaxSeconds)throw new UserError(G711Wave.Limit);
        return 44+DataBytes*2;
    }
}
// Original codeword expansion and bounded RIFF reader; no system codec or devices.
// Streams and immutable source identity are owned by the caller throughout use.
public static class G711Wave
{
    public const string Invalid="The G.711 WAV file is incomplete or has an invalid RIFF structure, format fields or frame count.";
    public const string Unsupported="Use a little-endian RIFF WAV with G.711 A-law or mu-law, 8-bit codewords, mono/stereo and 8–192 kHz. This tool requires an 18-byte fmt chunk with no extra format bytes and one fact chunk matching the data frame count. Raw streams, extensible WAV, RF64 and other codecs are not supported here.";
    public const string Limit="G.711 input and complete PCM output are each limited to 512 MiB and two hours. PCM16 needs twice the encoded audio bytes plus a 44-byte header; the full output limit applies even to preview.";
    public const string Scope="Expand G.711 A-law or mu-law codewords to 16-bit integer PCM WAV. Sample rate, channel order and frame count are preserved. This does not restore detail already lost in the encoded recording, and no gain, normalization, resampling, filtering or dither is applied.";
    public const string Privacy="Only the main data chunk is converted. Metadata, cues, loops and unknown chunks are not copied or interpreted; this is not an archival copy. No microphone, network, external codec or automatic playback is used. Save creates a new WAV beside the original without replacing it.";
    public static G711Info Read(Stream input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A seekable readable stream is required.");
        if(input.Length>PcmWave.MaxBytes)throw new UserError(Limit);
        if(input.Length<58)throw new UserError(Invalid);
        input.Position=0;Span<byte> head=stackalloc byte[12];input.ReadExactly(head);
        if(!head[..4].SequenceEqual("RIFF"u8)||!head[8..].SequenceEqual("WAVE"u8))throw new UserError(Unsupported);
        if(BinaryPrimitives.ReadUInt32LittleEndian(head[4..])+8L!=input.Length)throw new UserError(Invalid);
        int rate=0,channels=0,chunks=0;bool mu=false;long offset=-1,dataBytes=0;uint? fact=null;
        Span<byte> chunk=stackalloc byte[8];Span<byte> fmt=stackalloc byte[18];
        while(input.Position<input.Length)
        {
            token.ThrowIfCancellationRequested();
            if(++chunks>1024||input.Length-input.Position<8)throw new UserError(Invalid);
            input.ReadExactly(chunk);uint length=BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);long next=input.Position+length+(length&1);
            if(next>input.Length)throw new UserError(Invalid);
            if(chunk[..4].SequenceEqual("fmt "u8))
            {
                if(rate!=0)throw new UserError(Invalid);
                if(length!=18)throw new UserError(Unsupported);
                input.ReadExactly(fmt);
                int tag=BinaryPrimitives.ReadUInt16LittleEndian(fmt);channels=BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);uint hz=BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                if(tag is not 6 and not 7||channels is not 1 and not 2||hz is <8000 or >192000||BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..])!=8||BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..])!=0)throw new UserError(Unsupported);
                rate=(int)hz;mu=tag==7;
                if(BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..])!=channels||BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..])!=(uint)(rate*channels))throw new UserError(Invalid);
            }
            else if(chunk[..4].SequenceEqual("data"u8))
            {
                if(offset>=0||length==0)throw new UserError(Invalid);offset=input.Position;dataBytes=length;
            }
            else if(chunk[..4].SequenceEqual("fact"u8))
            {
                if(fact.HasValue||length<4)throw new UserError(Invalid);input.ReadExactly(fmt[..4]);fact=BinaryPrimitives.ReadUInt32LittleEndian(fmt);
            }
            else if(chunk[..4].SequenceEqual("LIST"u8)&&length>=4)
            {
                input.ReadExactly(fmt[..4]);if(fmt[..4].SequenceEqual("wavl"u8))throw new UserError(Unsupported);
            }
            else if(chunk[..4].SequenceEqual("slnt"u8))throw new UserError(Unsupported);
            input.Position=next;
        }
        token.ThrowIfCancellationRequested();
        if(rate==0||offset<0||dataBytes%channels!=0||!fact.HasValue||fact.Value!=dataBytes/channels)throw new UserError(Invalid);
        var info=new G711Info(rate,channels,mu,offset,dataBytes);if(info.Seconds>PcmWave.MaxSeconds)throw new UserError(Limit);return info;
    }
    // Sign/segment/mantissa reconstruction in the conventional signed PCM16 scale.
    // A-law even-bit inversion and mu-law complemented codewords are undone first.
    public static short Decode(byte code,bool muLaw)
    {
        int bits=muLaw?(code^255):(code^85),segment=(bits>>4)&7,mantissa=bits&15;
        int magnitude=muLaw?((mantissa*8+132)<<segment)-132:segment==0?mantissa*16+8:(mantissa*16+264)<<(segment-1);
        return (short)((bits&128)!=0?(muLaw?-magnitude:magnitude):(muLaw?magnitude:-magnitude));
    }
    public static void Export(Stream input,Stream output,CancellationToken token,bool preview=false)
    {
        token.ThrowIfCancellationRequested();if(ReferenceEquals(input,output)||!output.CanWrite)throw new ArgumentException("Separate readable input and writable output are required.");
        var info=Read(input,token);info.OutputBytes();token.ThrowIfCancellationRequested();
        long frames=preview?Math.Min(info.Frames,(long)PcmWave.PreviewSeconds*info.Rate):info.Frames;
        PcmWave.Header(output,info.Rate,info.Channels,16,frames);input.Position=info.DataOffset;
        var buffer=new byte[4096*info.Channels];var encoded=new byte[buffer.Length*2];
        for(long at=0;at<frames;)
        {
            token.ThrowIfCancellationRequested();int count=(int)Math.Min(4096,frames-at),bytes=count*info.Channels;input.ReadExactly(buffer.AsSpan(0,bytes));token.ThrowIfCancellationRequested();
            for(int i=0;i<bytes;i++)BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(i*2,2),Decode(buffer[i],info.MuLaw));
            output.Write(encoded.AsSpan(0,bytes*2));at+=count;
        }
        token.ThrowIfCancellationRequested();
    }
}
