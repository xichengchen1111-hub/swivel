using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public static class SystemAudioInput
{
    public const string Unsupported="Choose a self-contained RIFF WAV, FLAC, MP3 Layer III, ADTS AAC or audio-only M4A (AAC-LC or ALAC) file. Playlists, URLs, reference movies, video and other containers are not accepted by this decoder.";
    public static string Identify(Stream input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!input.CanRead||!input.CanSeek||input.Length<4)throw new UserError(Unsupported);input.Position=0;Span<byte> bytes=stackalloc byte[12];int n=(int)Math.Min(12,input.Length);input.ReadExactly(bytes[..n]);
        if(n>=12&&bytes[..4].SequenceEqual("RIFF"u8)&&bytes[8..12].SequenceEqual("WAVE"u8))
        {if(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])+8L!=input.Length)throw new UserError(Unsupported);input.Position=0;token.ThrowIfCancellationRequested();return "WAV";}
        // ISO base media: only the bounded audio-only M4A box gate decides; nothing else with an ftyp passes.
        if(n>=8&&bytes[4..8].SequenceEqual("ftyp"u8)){var m4a=M4aAudio.Inspect(input,token);return "M4A "+m4a.Codec;}
        long at=0;if(n>=10&&bytes[..3].SequenceEqual("ID3"u8))
        {
            if(bytes[3] is <2 or >4||bytes[4]==255||(bytes[6]|bytes[7]|bytes[8]|bytes[9])>=128)throw new UserError(Unsupported);
            int flags=bytes[5],allowed=bytes[3]==2?0xc0:bytes[3]==3?0xe0:0xf0;if((flags&~allowed)!=0)throw new UserError(Unsupported);
            long size=(bytes[6]<<21)|(bytes[7]<<14)|(bytes[8]<<7)|bytes[9];at=10+size+(bytes[3]==4&&(flags&16)!=0?10:0);
            if(at>32*1024*1024||at>input.Length-4)throw new UserError(Unsupported);input.Position=at;input.ReadExactly(bytes[..4]);
        }
        if(bytes[..4].SequenceEqual("fLaC"u8)){input.Position=0;token.ThrowIfCancellationRequested();return "FLAC";}
        bool sync=bytes[0]==255;
        if(sync&&(bytes[1]&0xf6)==0xf0&&((bytes[2]>>2)&15)<13){input.Position=0;token.ThrowIfCancellationRequested();return "ADTS AAC";}
        if(sync&&(bytes[1]&0xe0)==0xe0&&(bytes[1]&0x18)!=8&&(bytes[1]&6)==2&&(bytes[2]&0xf0)!=0xf0&&(bytes[2]&12)!=12)
        {input.Position=0;token.ThrowIfCancellationRequested();return "MP3 Layer III";}
        throw new UserError(Unsupported);
    }
}
