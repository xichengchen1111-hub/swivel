using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveDuckingChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int bits,int channels,int[] samples,int rate=8000)
        {var b=WaveChecks.Fixture(bits,channels,samples);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28),rate*channels*(bits/8));return b;}
        WaveActivity Profile(byte[] bytes,AudioEdit? edit=null,WaveDucking? settings=null)
        {using var s=new MemoryStream(bytes);var i=PcmWave.Read(s,default);return WaveActivity.Analyze(s,edit??new(0,i.Seconds),settings??new(),default);}
        byte[] Render(byte[] a,byte[] b,AudioEdit edit,WaveMusic mix,bool preview=false)
        {using var s=new MemoryStream(a);using var m=new MemoryStream(b);using var o=new MemoryStream();WaveMixer.Export(s,m,o,edit,mix,default,preview);return o.ToArray();}
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})foreach(int rate in new[]{8000,11025,44100,48000,192000})
        {
            int window=(rate+25)/50;int level=(int)(Math.Pow(2,bits-1)/4);
            int[] values=Enumerable.Range(0,(window*2+1)*channels).Select(i=>i/channels<window?0:(i%channels==0?level:-level)).ToArray();
            var p=Profile(Fixture(bits,channels,values,rate));
            check(p.WindowFrames==window&&p.WindowCount==3&&p.Frames==window*2+1,"Activity nearest-frame window, partial last window and format");
            check(!p.IsActive(window-1)&&p.IsActive(window)&&p.IsActive(window*2),"Activity squares each channel independently, including opposite polarity and last single frame");
            var zero=Profile(Fixture(bits,channels,values,rate),new(0,(window*2+1)/(double)rate,0));
            check(!zero.IsActive(window)&&!zero.IsActive(window*2),"Activity uses main gain, does not classify raw source");
            reject(()=>p.IsActive(-1),"Activity negative position refused");reject(()=>p.IsActive(p.Frames),"Activity end position refused");
        }
        // Quarter full-scale amplitude has a directly computed dBFS boundary.
        byte[] quarter=Fixture(16,1,Enumerable.Repeat(8192,160).ToArray());
        double boundary=20*Math.Log10(.25);
        check(Profile(quarter,settings:new(boundary)).IsActive(0),"Threshold equality is active");
        check(!Profile(quarter,settings:new(boundary+.001)).IsActive(0)&&Profile(quarter,settings:new(boundary-.001)).IsActive(0),"Threshold sides independently separated");
        var fade=Profile(quarter,new(0,.02,1,.02),new(-15));check(!fade.IsActive(0),"Full-trim fade affects activity RMS");
        int[] offset=Enumerable.Repeat(0,160).Concat(Enumerable.Repeat(8192,160)).ToArray();
        check(Profile(Fixture(16,1,offset),new(.02,.04)).IsActive(0),"Activity profile begins at selected trim, not file beginning");
        // Independent decimal envelope oracle, per sample: fixed attack/recovery depth,
        // including reversals and activity continuing before a delayed music overlay.
        foreach(int rate in new[]{8000,11025,44100})foreach(int loopLength in new[]{4096,4097})
        {
            int window=(rate+25)/50,count=window*50;int[] primary=Enumerable.Range(0,count).Select(i=>i/window is 0 or 1 or 3 or 20 or 21?8192:0).ToArray();
            int[] backing=Enumerable.Range(0,loopLength).Select(i=>1200+i%503).ToArray();
            var a=Fixture(16,1,primary,rate);var b=Fixture(16,1,backing,rate);int start=window/2;
            var edit=new AudioEdit(0,count/(double)rate);var mix=new WaveMusic(start/(double)rate,.5,true,Ducking:new(-20,.2));
            int[] actual=WaveChecks.Decode(Render(a,b,edit,mix));decimal depth=0,attack=window,release=(rate+2)/4;
            for(int i=0;i<count;i++)
            {
                bool active=primary[i]!=0;depth=active?Math.Min(1,depth+1/attack):Math.Max(0,depth-1/release);
                double contribution=i<start?0:backing[(i-start)%loopLength]*.5*(1-.8*(double)depth);
                int expected=(int)Math.Round(primary[i]+contribution,MidpointRounding.AwayFromZero);
                check(Math.Abs(actual[i]-expected)<=1,"Independent decimal activity envelope/loop phase/rounding oracle");
            }
            check(Render(a,b,edit,new(Loop:true,Ducking:new(Gain:1))).SequenceEqual(Render(a,b,edit,new(Loop:true))),"Unity activity multiplier preserves all old mixer bytes");
            check(WaveChecks.Decode(Render(a,b,edit,new(Gain:0,Loop:true,Ducking:new()))).SequenceEqual(primary),"Activity never modifies main samples when music muted");
        }
        byte[] longMain=Fixture(16,2,Enumerable.Range(0,12*8000*2).Select(i=>i/(8000*2)%2==0?8192:0).ToArray());
        byte[] music=Fixture(16,1,Enumerable.Repeat(1200,4097).ToArray());var fullEdit=new AudioEdit(.123,11.75,1,.7,2);
        var fullMix=new WaveMusic(.25,.7,true,.3,1,new());
        var full=WaveChecks.Decode(Render(longMain,music,fullEdit,fullMix));var preview=WaveChecks.Decode(Render(longMain,music,fullEdit,fullMix,true));
        check(preview.SequenceEqual(full.Take(8000*10*2)),"Activity preview shares complete trim analysis and envelope, never relocates fades to ten-second endpoint");
        var info=new WaveInfo(8000,1,16,44,8000L*601*2);var selection=new AudioEdit(0,600);
        check(new WaveDucking().Validate(info,selection).Count==4800000,"Exact ten-minute activity limit accepted");
        reject(()=>new WaveDucking().Validate(info,new(0,600+1d/8000)),"Ten-minute plus one frame refused");
        var byteInfo=new WaveInfo(192000,2,32,44,WaveDucking.MaxDataBytes+8);double seconds=WaveDucking.MaxDataBytes/8d/192000;
        check(new WaveDucking().Validate(byteInfo,new(0,seconds)).Count==WaveDucking.MaxDataBytes/8,"Exact activity data limit accepted");
        reject(()=>new WaveDucking().Validate(byteInfo,new(0,seconds+1d/192000)),"Activity byte limit plus frame refused");
        check(new WaveMusic(Loop:true).Range(info,info,new(0,601)).Count==8000L*601,"Off option does not impose new duration budget");
        foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-61,-9})reject(()=>new WaveDucking(invalid).Validate(info,selection),"Invalid activity threshold refused");
        foreach(double invalid in new[]{double.NaN,double.NegativeInfinity,-.01,1.01})reject(()=>new WaveDucking(Gain:invalid).Validate(info,selection),"Invalid activity music level refused");
        using var aStream=new MemoryStream(quarter);using var bStream=new MemoryStream(quarter);using var output=new MemoryStream();
        reject(()=>WaveMixer.Export(aStream,bStream,output,new(0,.02),new(Ducking:new(double.NaN)),default),"Bad activity settings refused before output header");check(output.Length==0,"Invalid activity creates no output");
        reject(()=>WaveMixer.Export(aStream,bStream,output,new(0,.02),new(Ducking:new()),new CancellationToken(true)),"Activity pre-cancel refused");check(output.Length==0,"Pre-cancel creates no header");
        using var cts=new CancellationTokenSource();using var interrupted=new Interrupt(quarter,cts,false);
        reject(()=>WaveActivity.Analyze(interrupted,new(0,.02),new(),cts.Token),"Activity observes cancellation after last analysis read");
        using var failed=new Interrupt(quarter,cts,true);reject(()=>WaveActivity.Analyze(failed,new(0,.02),new(),default),"Activity content read failures propagate");
        check(aStream.CanRead&&bStream.CanRead&&aStream.ToArray().SequenceEqual(quarter)&&bStream.ToArray().SequenceEqual(quarter),"Activity never closes/modifies either input");

        // Speech intervals instead of volume: silence that "speaks" between 0.5–1.0 s and 1.3–1.5 s
        // (gap 0.3 s bridged), 2 s at 8 kHz; windows are 160 frames (20 ms).
        byte[] silent=Fixture(16,1,new int[16000]);
        var speech=new WaveDucking(-35,.2,new[]{new TranscriptSegment(1.3,1.5,"b"),new TranscriptSegment(.5,1.0,"a")});
        var sp=Profile(silent,settings:speech);
        check(!sp.IsActive(3999)&&sp.IsActive(4000)&&sp.IsActive(9000)&&sp.IsActive(11999)&&!sp.IsActive(12000)&&!sp.IsActive(15999),"Speech lowering follows intervals, bridges short pauses, ignores the audio's volume");
        var trimmed=Profile(silent,new(.6,1.2),speech);
        check(trimmed.Frames==4800&&trimmed.IsActive(0)&&trimmed.IsActive(4799),"Speech intervals are in source time and follow the trim");
        var apart=Profile(silent,settings:new(-35,.2,new[]{new TranscriptSegment(.2,.3,"a"),new TranscriptSegment(1.0,1.1,"b")}));
        check(apart.IsActive(1600)&&!apart.IsActive(4000)&&apart.IsActive(8000),"Speech pauses of half a second or more let the music recover");
        check(!Profile(silent,settings:new(-35,.2,Array.Empty<TranscriptSegment>())).IsActive(8000),"No speech found leaves the music unchanged");
        check(Profile(silent,settings:new(double.NaN,.2,Array.Empty<TranscriptSegment>())).WindowCount==100,"Speech lowering does not use the threshold");
        reject(()=>Profile(silent,settings:new(-35,1.5,Array.Empty<TranscriptSegment>())),"Speech lowering refuses a level above 100%");
        reject(()=>Profile(silent,settings:new(-35,.2,new[]{new TranscriptSegment(1,.5,"x")})),"Speech lowering refuses a reversed interval");
        reject(()=>Profile(silent,settings:new(-35,.2,new[]{new TranscriptSegment(double.NaN,1,"x")})),"Speech lowering refuses a non-finite interval");
        var longSilence=Fixture(16,1,new int[8000*700]);
        check(Profile(longSilence,settings:new(-35,.2,new[]{new TranscriptSegment(650,651,"late")})).IsActive(650*8000),"Speech lowering is not limited to 10 minutes");
        var mixed=Render(silent,Fixture(16,1,Enumerable.Repeat(8000,16000).ToArray()),new(0,2),new(0,1,Ducking:speech));
        short At(byte[] w,double t)=>BinaryPrimitives.ReadInt16LittleEndian(w.AsSpan(44+2*(int)(t*8000)));
        check(At(mixed,.25)==8000&&At(mixed,.8)==1600&&At(mixed,1.15)==1600&&At(mixed,1.9)==8000,"Music is lowered to the chosen level during speech and recovers after it");
    }
    private sealed class Interrupt(byte[] bytes,CancellationTokenSource token,bool fail):MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer){if(Position>=44&&fail)throw new IOException("injected");int n=base.Read(buffer);if(Position>44)token.Cancel();return n;}
    }
}
