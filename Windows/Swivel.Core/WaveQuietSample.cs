using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record QuietSampleCandidate(long First,long Count,int Rate,double RootMeanSquare,int Windows)
{
    public double Start=>(double)First/Rate;
    public double End=>(double)(First+Count)/Rate;
}

// A bounded energy hint, never a speech/noise classifier. The caller must offer
// explicit listening and adoption; finding a result must not change the editor.
public static class WaveQuietSample
{
    public static QuietSampleCandidate Find(Stream input,long windowFrames,CancellationToken token)
    {
        var info=PcmWave.Read(input,token);
        if(windowFrames<(long)Math.Round(info.Rate*.05,MidpointRounding.AwayFromZero)||windowFrames>2L*info.Rate||windowFrames>info.Frames)
            throw new UserError("Choose a noise-only sample of 0.05–2 seconds within the recording and strength from 0–100%.");
        var buffer=new byte[4096*info.Block];UInt128 best=UInt128.MaxValue;long bestFirst=0;int windows=0,bytes=info.Bits/8;
        void Consider(long first)
        {
            token.ThrowIfCancellationRequested();input.Position=info.DataOffset+first*info.Block;UInt128 energy=0;long remaining=windowFrames;
            while(remaining>0)
            {
                token.ThrowIfCancellationRequested();int frames=(int)Math.Min(4096,remaining);input.ReadExactly(buffer.AsSpan(0,frames*info.Block));token.ThrowIfCancellationRequested();
                for(int frame=0;frame<frames;frame++)for(int channel=0;channel<info.Channels;channel++)
                {
                    long sample=PcmWave.Sample(buffer.AsSpan(frame*info.Block+channel*bytes,bytes),info.Bits);
                    energy+=(UInt128)(sample*sample); // <=2^62 per sample; <=2^82 per window.
                }
                remaining-=frames;
            }
            windows++;if(energy<best){best=energy;bestFirst=first;}
        }
        long full=info.Frames/windowFrames;
        for(long i=0;i<full;i++)Consider(i*windowFrames);
        long tail=info.Frames-windowFrames;if(tail!=(full-1)*windowFrames)Consider(tail);
        token.ThrowIfCancellationRequested();double rms=Math.Sqrt((double)best/(windowFrames*info.Channels))/Math.ScaleB(1,info.Bits-1);
        return new(bestFirst,windowFrames,info.Rate,rms,windows);
    }
}
