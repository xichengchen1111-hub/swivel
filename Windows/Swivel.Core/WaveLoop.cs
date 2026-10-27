using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// Original linear-amplitude overlap reader. The first pass plays [0,P),
// later passes repeat P=N-C frames: blend tail[P,P+C) with head[0,C),
// then play [C,P). Nothing here changes pitch or recognizes beat boundaries.
internal sealed class WaveLoopReader
{
    private readonly Stream input;
    private readonly WaveInfo info;
    private readonly long fade,period;
    private readonly double divisor;
    private readonly byte[] first,second;
    private readonly byte[]? cached;
    internal WaveLoopReader(Stream input,WaveInfo info,long crossfade,CancellationToken token)
    {
        if(crossfade<2||crossfade>info.Frames/2)throw new ArgumentOutOfRangeException(nameof(crossfade));
        this.input=input;this.info=info;fade=crossfade;period=info.Frames-crossfade;divisor=Math.Pow(2,info.Bits-1);
        first=new byte[4096*info.Block];second=new byte[4096*info.Block];
        if(info.Frames<=4096)
        {
            token.ThrowIfCancellationRequested();cached=new byte[checked((int)info.DataBytes)];
            input.Position=info.DataOffset;input.ReadExactly(cached);token.ThrowIfCancellationRequested();
        }
    }
    internal void Read(long position,Span<double> output,CancellationToken token)
    {
        if(position<0||output.Length%info.Channels!=0||output.Length/info.Channels>4096)throw new ArgumentOutOfRangeException(nameof(position));
        int frames=output.Length/info.Channels,done=0,bytes=info.Bits/8;
        while(done<frames)
        {
            token.ThrowIfCancellationRequested();long at=position+done,source;bool blend;
            int count;
            if(at<period){source=at;blend=false;count=(int)Math.Min(frames-done,period-at);}
            else
            {
                source=(at-period)%period;blend=source<fade;
                count=(int)Math.Min(frames-done,(blend?fade:period)-source);
            }
            ReadFrames(blend?period+source:source,count,first,token);
            if(blend)ReadFrames(source,count,second,token);
            for(int frame=0;frame<count;frame++)
            {
                long step=source+frame;double weight=blend?(double)step/(fade-1):0;
                for(int channel=0;channel<info.Channels;channel++)
                {
                    int offset=frame*info.Block+channel*bytes;
                    double tail=PcmWave.Sample(first.AsSpan(offset,bytes),info.Bits)/divisor,value=tail;
                    if(blend&&step!=0)
                    {
                        double head=PcmWave.Sample(second.AsSpan(offset,bytes),info.Bits)/divisor;
                        value=step==fade-1?head:tail*(1-weight)+head*weight;
                    }
                    output[(done+frame)*info.Channels+channel]=value;
                }
            }
            done+=count;
        }
    }
    private void ReadFrames(long frame,int count,byte[] buffer,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var target=buffer.AsSpan(0,count*info.Block);
        if(cached is not null)cached.AsSpan(checked((int)(frame*info.Block)),target.Length).CopyTo(target);
        else {input.Position=info.DataOffset+frame*info.Block;input.ReadExactly(target);}
        token.ThrowIfCancellationRequested();
    }
}
