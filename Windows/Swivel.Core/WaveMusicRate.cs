using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed class WaveMusicRatePlan
{
    public const long MaxBytes=128L*1024*1024;
    public const string Limit="Preparing background music requires an integer PCM WAV and a complete converted WAV no larger than 128 MiB. Use Convert WAV Format separately for larger music.";
    public WaveFormat Format{get;}
    public long Frames{get;}
    public int Bytes{get;}
    private WaveMusicRatePlan(WaveFormat format,long frames,int bytes){Format=format;Frames=frames;Bytes=bytes;}
    public static WaveMusicRatePlan Create(WaveInfo source,int targetRate)
    {
        if(source.Rate is <8000 or >192000||source.Channels is not 1 and not 2||source.Bits is not 8 and not 16 and not 24 and not 32||source.DataOffset is <44 or >PcmWave.MaxBytes
            ||source.DataBytes<=0||source.DataBytes>PcmWave.MaxBytes-source.DataOffset||source.DataBytes%source.Block!=0||source.Seconds>PcmWave.MaxSeconds)
            throw new UserError(Limit);
        var format=new WaveFormat(targetRate,source.Channels,source.Bits);long frames=format.Frames(source);
        long data=frames*source.Block,bytes=data+(data&1)+(source.Bits>16?68:44);
        if(bytes>MaxBytes)throw new UserError(Limit);
        return new(format,frames,(int)bytes);
    }
}

// Owns one complete, private, read-only prepared buffer. The caller keeps the
// original source lease alive and serializes mixing/preview until Dispose.
public sealed class PreparedWaveMusic:IDisposable
{
    private readonly MemoryStream audio;
    public WaveInfo Info{get;}
    public long ClippedSamples{get;}
    public int Bytes{get;}
    private PreparedWaveMusic(MemoryStream audio,WaveInfo info,long clipped,int bytes){this.audio=audio;Info=info;ClippedSamples=clipped;Bytes=bytes;}
    public static PreparedWaveMusic Prepare(Stream source,int targetRate,CancellationToken token,WaveInfo? expectedSource=null)
    {
        var original=PcmWave.Read(source,token);
        if(expectedSource is not null&&original!=expectedSource)throw new UserError("The music format changed. Close this operation and choose the music again.");
        var plan=WaveMusicRatePlan.Create(original,targetRate);token.ThrowIfCancellationRequested();
        // Fixed-size buffer prevents MemoryStream growth/doubling and ToArray copies.
        var bytes=new byte[plan.Bytes];long clipped;
        using(var writer=new MemoryStream(bytes,true))
        {
            clipped=WaveConverter.Export(source,writer,plan.Format,token);
            token.ThrowIfCancellationRequested();
            if(writer.Position!=plan.Bytes)throw new IOException("Prepared music length changed during conversion.");
        }
        var audio=new MemoryStream(bytes,false);
        try
        {
            var info=PcmWave.Read(audio,token);
            if(info.Rate!=plan.Format.Rate||info.Channels!=plan.Format.Channels||info.Bits!=plan.Format.Bits||info.Frames!=plan.Frames||audio.Length!=plan.Bytes)
                throw new IOException("Prepared music format changed during conversion.");
            token.ThrowIfCancellationRequested();return new(audio,info,clipped,plan.Bytes);
        }
        catch{audio.Dispose();throw;}
    }
    public long ExportMix(Stream primary,Stream output,AudioEdit edit,WaveMusic settings,CancellationToken token,bool preview=false)
    {
        if(ReferenceEquals(primary,output))throw new ArgumentException("Input and output must be different streams.");
        return WaveMixer.Export(primary,audio,output,edit,settings,token,preview);
    }
    public void Dispose()=>audio.Dispose();
}
