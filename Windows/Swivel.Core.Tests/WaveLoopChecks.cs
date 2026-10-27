using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveLoopChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int bits,int channels,int[] samples,int rate=8000)
        {byte[] b=WaveChecks.Fixture(bits,channels,samples);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28),rate*channels*(bits/8));return b;}
        byte[] Mix(byte[] source,byte[] music,AudioEdit edit,WaveMusic settings,bool preview=false)
        {using var a=new MemoryStream(source);using var b=new MemoryStream(music);using var o=new MemoryStream();WaveMixer.Export(a,b,o,edit,settings,default,preview);return o.ToArray();}
        foreach(int rate in new[]{8000,11025,48000,192000})foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        foreach(int musicBits in new[]{8,16,24,32})foreach(int musicChannels in new[]{1,2})foreach(int n in new[]{7,4095,4096,4097})
        {
            int count=n*2+17,first=3,start=5;double scale=Math.Pow(2,bits-1),mscale=Math.Pow(2,musicBits-1);
            int[] primary=Enumerable.Range(0,(count+first)*channels).Select(i=>(int)(scale*((i%7)-3)/32)).ToArray();
            int[] track=Enumerable.Range(0,n*musicChannels).Select(i=>(int)(mscale*((i%13)-6)/32)).ToArray();
            byte[] a=Fixture(bits,channels,primary,rate),b=Fixture(musicBits,musicChannels,track,rate);
            foreach(int c in new[]{2,n/2}.Distinct())
            {
                int period=n-c;var edit=new AudioEdit(first/(double)rate,(first+count)/(double)rate,.5);var settings=new WaveMusic(start/(double)rate,.5,true,LoopCrossfadeMilliseconds:c*1000d/rate);
                // 192kHz small clips can represent tiny millisecond values, while the
                // supported raw setting still has an independent 1000ms ceiling.
                if(settings.LoopCrossfadeMilliseconds>1000)continue;
                int[] actual=WaveChecks.Decode(Mix(a,b,edit,settings));
                double Sample(int t,int channel)
                {
                    int cycle=t/period,q=t%period;
                    double Read(int frame)=>track[frame*musicChannels+channel]/mscale;
                    if(cycle==0)return Read(t);
                    if(q>=c)return Read(q);
                    if(q==0)return Read(period);
                    if(q==c-1)return Read(q);
                    double w=q/(double)(c-1);return Read(period+q)*(1-w)+Read(q)*w;
                }
                int[] expected=Enumerable.Range(0,count*channels).Select(i=>
                {
                    int t=i/channels-start,ch=i%channels;double backing=0;
                    if(t>=0)backing=musicChannels==1?Sample(t,0):channels==1?(Sample(t,0)+Sample(t,1))/2:Sample(t,ch);
                    return (int)Math.Round((primary[(first+i/channels)*channels+ch]/scale*.5+backing*.5)*scale,MidpointRounding.AwayFromZero);
                }).ToArray();
                check(actual.SequenceEqual(expected),"Crossfade independent cycle/layer sample oracle: all rates/depths/channels, cache boundaries, C2/half and delayed trim-relative music");
                using var decoded=new MemoryStream(Mix(a,b,edit,settings));var info=PcmWave.Read(decoded,default);
                check(info.Rate==rate&&info.Channels==channels&&info.Bits==bits&&info.Frames==count,"Crossfade retains main format and complete selection duration");
            }
        }
        byte[] silent=Fixture(16,1,new int[25]),ramp=Fixture(16,1,new[]{100,200,300,400,500,600,700,800});
        int[] fixedExpected={100,200,300,400,500,600,700,200,300,400,500,600,700,200,300,400,500,600,700,200,300,400,500,600,700};
        check(WaveChecks.Decode(Mix(silent,ramp,new(0,25d/8000),new(Gain:1,Loop:true,LoopCrossfadeMilliseconds:.25))).SequenceEqual(fixedExpected),"Two-frame hardcoded crossfade exact tail/head endpoints and shortened period");
        var halfMix=new WaveMusic(Gain:1,Loop:true,LoopCrossfadeMilliseconds:.5);
        int[] half=WaveChecks.Decode(Mix(silent,ramp,new(0,25d/8000),halfMix));
        check(half[4]==500&&half[7]==400&&half[8]==500&&half[11]==400,"Half-file overlap has no zero-length body/gap, endpoints remain exact");
        byte[] longA=Fixture(16,2,Enumerable.Range(0,12*8000*2).Select(i=>i/1600%2==0?8192:0).ToArray()),longB=Fixture(24,2,Enumerable.Range(0,4097*2).Select(i=>i%2==0?150000:-100000).ToArray());
        var fullEdit=new AudioEdit(.1,11.9,.8,.2,2);var combined=new WaveMusic(.17,.6,true,.4,1,new(),100);
        var full=WaveChecks.Decode(Mix(longA,longB,fullEdit,combined));var preview=WaveChecks.Decode(Mix(longA,longB,fullEdit,combined,true));
        check(preview.SequenceEqual(full.Take(80000*2)),"Crossfade plus RMS activity/full music fades shares complete save prefix for16bit source");
        var trackInfo=new WaveInfo(8000,1,16,44,32000);check(new WaveMusic(Loop:true,LoopCrossfadeMilliseconds:1000).CrossfadeFrames(trackInfo)==8000,"Exactly1000ms and half-file accepted");
        var odd=trackInfo with{DataBytes=14};check(new WaveMusic(Loop:true,LoopCrossfadeMilliseconds:3d/8).CrossfadeFrames(odd)==3,"Odd file half floors, exact three-frame fade accepted");
        check(new WaveMusic(Loop:true,LoopCrossfadeMilliseconds:1.5/8).CrossfadeFrames(odd)==2,"Half-frame rounding uses away from zero");
        foreach(double value in new[]{double.NaN,double.NegativeInfinity,-.001,1000.0001,.001,.125,4d/8})
            reject(()=>new WaveMusic(Loop:true,LoopCrossfadeMilliseconds:value).CrossfadeFrames(odd),"Invalid/nonrepresentable/overhalf crossfade refused");
        reject(()=>new WaveMusic(LoopCrossfadeMilliseconds:1).CrossfadeFrames(trackInfo),"Crossfade requires explicit loop in core");
        foreach(int n in new[]{1,2,3})
        {
            byte[] tiny=Fixture(16,1,new int[n]);check(WaveChecks.Decode(Mix(silent,tiny,new(0,25d/8000),new(Loop:true))).Length==25,"Legacy tiny loops remain accepted at zero fade");
            reject(()=>Mix(silent,tiny,new(0,25d/8000),new(Loop:true,LoopCrossfadeMilliseconds:.25)),"Nonzero fade requires at least four music frames");
        }
        using var main=new BodyProbe(longA);using var music=new BodyProbe(longB);using var output=new MemoryStream();
        reject(()=>WaveMixer.Export(main,music,output,new(0,1),new(Loop:true,Ducking:new(),LoopCrossfadeMilliseconds:1001),default),"Bad crossfade fails before activity pass or header");
        check(main.BodyBytes==0&&music.BodyBytes==0&&output.Length==0,"Invalid crossfade no PCM reads or header");
        reject(()=>WaveMixer.Export(main,music,output,new(0,1),combined,new CancellationToken(true)),"Crossfade pre-cancel refused");check(output.Length==0,"Precancel noheader");
        foreach(int n in new[]{4095,4096,4097})
        {
            using var source=new MemoryStream(Fixture(16,1,new int[25000]));using var backing=new BodyProbe(Fixture(16,1,Enumerable.Repeat(1000,n).ToArray()));using var sink=new MemoryStream();
            WaveMixer.Export(source,backing,sink,new(0,25000d/8000),new(Loop:true,LoopCrossfadeMilliseconds:2d/8),default);
            check(backing.BodyReads<100,"Crossfade source reads bounded by blocks/segments, never one per sample on streaming track");
            if(n<=4096)check(backing.BodyBytes==n*2,"Cached crossfade reads whole short track exactly once");
        }
        using var cancelled=new CancellationTokenSource();using var abortSource=new MemoryStream(silent);using var abortTrack=new Abort(Fixture(16,1,Enumerable.Repeat(1000,4097).ToArray()),cancelled);using var abortOut=new MemoryStream();
        reject(()=>WaveMixer.Export(abortSource,abortTrack,abortOut,new(0,25d/8000),new(Loop:true,LoopCrossfadeMilliseconds:.25),cancelled.Token),"Cancellation during crossfade PCM read observed");
        check(abortSource.CanRead&&abortTrack.CanRead&&abortTrack.ToArray().Length>44,"Cancelled crossfade keeps caller streams open and intact");
    }
    private sealed class BodyProbe(byte[] bytes):MemoryStream(bytes)
    {
        public long BodyBytes{get;private set;}public int BodyReads{get;private set;}
        public override int Read(Span<byte> b){bool body=Position>=44;int n=base.Read(b);if(body){BodyReads++;BodyBytes+=n;}return n;}
    }
    private sealed class Abort(byte[] bytes,CancellationTokenSource cancel):MemoryStream(bytes)
    {public override int Read(Span<byte> b){bool body=Position>=44;int n=base.Read(b);if(body)cancel.Cancel();return n;}}
}
