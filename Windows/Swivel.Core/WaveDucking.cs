using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record WaveDucking(double ThresholdDb=-35,double Gain=.2)
{
    public const long MaxDataBytes=128L*1024*1024;
    public const int MaxSeconds=600;
    public const string Invalid="Choose an activity threshold from -60 to -10 dBFS and music during activity from 0–100%.";
    public const string Limit="Activity-based music lowering is limited to a selected main-audio range of 10 minutes and 128 MiB of PCM data. Shorten the selection or turn it off.";
    public AudioRange Validate(WaveInfo source,AudioEdit edit)
    {
        if(!double.IsFinite(ThresholdDb)||ThresholdDb is <-60 or >-10||!double.IsFinite(Gain)||Gain is <0 or >1)throw new UserError(Invalid);
        var range=edit.Range(source);
        if(range.Count>(long)MaxSeconds*source.Rate||range.Count>MaxDataBytes/source.Block)throw new UserError(Limit);
        return range;
    }
}

// Original fixed-window RMS activity gate. This class does not classify speech.
// Profile windows and fades use the complete trim, including during previews.
public sealed class WaveActivity
{
    private readonly bool[] active;
    public int WindowFrames{get;}
    public int WindowCount=>active.Length;
    public long Frames{get;}
    internal int Rate{get;}
    private WaveActivity(bool[] active,int windowFrames,long frames,int rate)
    {this.active=active;WindowFrames=windowFrames;Frames=frames;Rate=rate;}
    public bool IsActive(long frame)
    {
        if(frame<0||frame>=Frames)throw new ArgumentOutOfRangeException(nameof(frame));
        return active[(int)(frame/WindowFrames)];
    }
    public static WaveActivity Analyze(Stream input,AudioEdit edit,WaveDucking settings,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var info=PcmWave.Read(input,token);var range=settings.Validate(info,edit);
        int window=Math.Max(1,(info.Rate+25)/50);var flags=new bool[checked((int)((range.Count+window-1)/window))];
        var buffer=new byte[4096*info.Block];double divisor=Math.Pow(2,info.Bits-1),threshold=Math.Pow(10,settings.ThresholdDb/20);
        double squares=0;int inWindow=0,index=0;input.Position=info.DataOffset+range.First*info.Block;
        for(long at=0;at<range.Count;)
        {
            token.ThrowIfCancellationRequested();int count=(int)Math.Min(4096,range.Count-at);input.ReadExactly(buffer.AsSpan(0,count*info.Block));
            for(int frame=0;frame<count;frame++)
            {
                long position=at+frame;
                double gain=edit.Gain*PcmWave.Ramp(position,range.FadeIn)*PcmWave.Ramp(range.Count-1-position,range.FadeOut);
                for(int channel=0;channel<info.Channels;channel++)
                {
                    double sample=PcmWave.Sample(buffer.AsSpan(frame*info.Block+channel*(info.Bits/8),info.Bits/8),info.Bits)/divisor*gain;
                    squares+=sample*sample;
                }
                inWindow++;
                if(inWindow==window||position==range.Count-1)
                {
                    flags[index++]=squares/(inWindow*(double)info.Channels)>=threshold*threshold;
                    squares=0;inWindow=0;
                }
            }
            at+=count;
        }
        token.ThrowIfCancellationRequested();return new(flags,window,range.Count,info.Rate);
    }
}

internal sealed class WaveDuckEnvelope
{
    private readonly WaveActivity profile;
    private readonly double gain;
    private readonly long scale,attackStep,releaseStep;
    private long frame,lowered;
    internal WaveDuckEnvelope(WaveActivity profile,WaveDucking settings)
    {
        this.profile=profile;gain=settings.Gain;
        long attack=Math.Max(1,(profile.Rate+25)/50),release=Math.Max(1,(profile.Rate+2)/4);
        long a=attack,b=release;while(b!=0){long remainder=a%b;a=b;b=remainder;}
        // Integer common units implement exact fixed linear steps and reversal
        // without accumulating floating-point drift at attack/release endpoints.
        scale=attack/a*release;attackStep=scale/attack;releaseStep=scale/release;
    }
    internal double Next()
    {
        bool active=profile.IsActive(frame++);
        lowered=active?Math.Min(scale,lowered+attackStep):Math.Max(0,lowered-releaseStep);
        // Advance before applying this frame's music multiplier.
        return lowered==0?1:lowered==scale?gain:1-(1-gain)*((double)lowered/scale);
    }
}
