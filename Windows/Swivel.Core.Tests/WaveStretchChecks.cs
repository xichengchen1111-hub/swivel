using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveStretchChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int bits,int channels,int frames,int rate,Func<int,int,int> sample)
        {
            byte[] bytes=WaveChecks.Fixture(bits,channels,Enumerable.Range(0,frames*channels).Select(i=>sample(i/channels,i%channels)).ToArray());
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),rate*channels*(bits/8));return bytes;
        }
        byte[] Render(byte[] bytes,WaveSpeed speed,bool preview=false)
        {
            using var input=new MemoryStream(bytes);using var output=new MemoryStream();
            WaveConverter.ExportSpeed(input,output,speed,default,preview);
            check(input.ToArray().SequenceEqual(bytes),"Keep-pitch speed leaves source bytes intact");return output.ToArray();
        }
        WaveInfo Info(byte[] bytes){using var stream=new MemoryStream(bytes);return PcmWave.Read(stream,default);}
        byte[] Tone(int rate,double hz,int frames,int channels=1)=>Fixture(16,channels,frames,rate,(i,c)=>(int)Math.Round((c==0?12000:8000)*Math.Sin(2*Math.PI*hz*i/rate)));

        // Format, exact rounded frame count, padding and preview = saved quantization then PCM16, all depths/channels.
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            int scale=bits==32?int.MaxValue:1<<(bits-1);
            byte[] source=Fixture(bits,channels,3001,11025,(i,c)=>(int)Math.Round((scale-1)*.6*Math.Sin(2*Math.PI*(c==0?440:660)*i/11025d)));
            check(Render(source,new(100,true)).SequenceEqual(Render(source,new(100))),"Keep-pitch 100% is the sample-exact neutral path");
            foreach(int percent in new[]{25,50,73,137,200,400})
            {
                byte[] output=Render(source,new(percent,true));var info=Info(output);
                check(info.Frames==(3001L*100+percent/2)/percent&&info.Rate==11025&&info.Channels==channels&&info.Bits==bits,"Keep-pitch frame count rounds once and format stays unchanged");
                check(output.Length==info.DataOffset+info.DataBytes+(info.DataBytes&1),"Keep-pitch RIFF header and odd padding agree");
                byte[] preview=Render(source,new(percent,true),true);using var full=new MemoryStream(output);using var expected=new MemoryStream();
                PcmWave.Export(full,expected,new(0,info.Seconds),default,true);
                check(preview.SequenceEqual(expected.ToArray()),"Keep-pitch preview matches saved quantization then PCM16");
                check(Render(source,new(percent,true)).SequenceEqual(output),"Keep-pitch output is deterministic");
            }
        }
        // Off stays the original pitch-linked path byte for byte.
        byte[] toneSource=Tone(16000,997,16000);
        foreach(int percent in new[]{50,73,137,200})check(Render(toneSource,new(percent,false)).SequenceEqual(Render(toneSource,new(percent))),"Keep Pitch off is the unchanged pitch-linked output");
        check(!Render(toneSource,new(73,true)).SequenceEqual(Render(toneSource,new(73))),"Keep Pitch on takes the time-stretch path");

        // Ranges and budgets before writing.
        foreach(int percent in new[]{25,400})check(new WaveSpeed(percent,true).Frames(new(8000,1,16,44,800))>0,"Keep-pitch range end accepted: "+percent);
        foreach(int percent in new[]{int.MinValue,0,24,401,int.MaxValue})reject(()=>new WaveSpeed(percent,true).Frames(new(8000,1,16,44,800)),"Keep-pitch rejects out-of-range percent: "+percent);
        foreach(int percent in new[]{25,49,201,400})reject(()=>new WaveSpeed(percent).Frames(new(8000,1,16,44,800)),"Pitch-linked range stays 50–200%: "+percent);
        check(new WaveSpeed(25,true).Frames(new(8000,1,16,44,8000L*1800*2))==8000L*7200,"Exactly two output hours at 25% accepted");
        reject(()=>new WaveSpeed(25,true).Frames(new(8000,1,16,44,(8000L*1800+1)*2)),"More than two output hours at 25% refused");
        long boundary=(PcmWave.MaxBytes-44)/4;
        check(new WaveSpeed(25,true).Frames(new(192000,1,8,44,boundary))==boundary*4,"Exact 512 MiB output at 25% accepted including header");
        reject(()=>new WaveSpeed(25,true).Frames(new(192000,1,8,44,boundary+1)),"One source frame beyond 512 MiB at 25% refused");
        using(var source=new MemoryStream(toneSource))using(var output=new MemoryStream())
        {
            reject(()=>WaveConverter.ExportSpeed(source,output,new(401,true),default),"Invalid keep-pitch export refused");check(output.Length==0,"Invalid keep-pitch export writes nothing");
            reject(()=>WaveConverter.ExportSpeed(source,output,new(73,true),new CancellationToken(true)),"Pre-cancelled keep-pitch export");check(output.Length==0,"Pre-cancelled keep-pitch writes nothing");
        }
        using(var same=new MemoryStream(toneSource))reject(()=>WaveConverter.ExportSpeed(same,same,new(73,true),default),"Keep-pitch rejects aliased streams");
        byte[] longSource=Tone(8000,997,130000);
        using(var cancellation=new CancellationTokenSource())using(var source=new MemoryStream(longSource))using(var output=new CancelOutput(cancellation))
        {reject(()=>WaveConverter.ExportSpeed(source,output,new(37,true),cancellation.Token),"Keep-pitch cancellation after first block observed");check(output.Length<8000,"Cancelled keep-pitch does not finish");}
        using(var source=new CountingInput(longSource))using(var output=new MemoryStream())
        {WaveConverter.ExportSpeed(source,output,new(37,true),default);check(source.Reads<2*(130000/4096)+12,"Keep-pitch reads source frames once, in runs of up to 4096 frames: "+source.Reads);}
        using(var source=new BrokenInput(longSource))using(var output=new MemoryStream())reject(()=>WaveConverter.ExportSpeed(source,output,new(37,true),default),"Keep-pitch read failure propagates");
        foreach(int percent in new[]{25,400}){byte[] preview=Render(longSource,new(percent,true),true);check(Info(preview).Frames==Math.Min(80000,(130000L*100+percent/2)/percent),"Keep-pitch preview is at most ten output seconds");}

        // DC level and stereo separation: overlapping periodic-Hann pieces sum to one.
        foreach(int percent in new[]{25,61,137,400})
        {
            int[] values=WaveChecks.Decode(Render(Fixture(24,2,20000,48000,(_,c)=>c==0?1234567:-2345678),new(percent,true)));
            int tail=(int)(48000*.05*100/percent)*2+2000;
            check(values.Take(Math.Max(2,values.Length-tail)).Select((x,i)=>Math.Abs(x-(i%2==0?1234567:-2345678))).Max()<=2,"Keep-pitch DC level and stereo separation: "+percent);
        }

        // Measurements: pitch by Hann-windowed DFT peak, duration by exact frames, transients by click positions.
        double Peak(int[] samples,int rate,int channels,double lo,double hi)
        {
            int length=Math.Min(16384,samples.Length/channels-2*rate/10),offset=rate/10;
            var window=Enumerable.Range(0,length).Select(n=>.5-.5*Math.Cos(2*Math.PI*n/length)).ToArray();double Magnitude(double hz)
            {
                double c=2*Math.Cos(2*Math.PI*hz/rate),s1=0,s2=0;
                for(int n=0;n<length;n++){double x=samples[(offset+n)*channels]*window[n],s0=x+c*s1-s2;s2=s1;s1=s0;}
                return s1*s1+s2*s2-c*s1*s2;
            }
            double step=rate/(2d*length),best=lo,top=-1;for(double hz=lo;hz<=hi;hz+=step){double m=Magnitude(hz);if(m>top){top=m;best=hz;}}
            for(double hz=best-step;hz<=best+step;hz+=step/50){double m=Magnitude(hz);if(m>top){top=m;best=hz;}}
            return best;
        }
        double worst=0;var clock=Stopwatch.StartNew();
        foreach(var (rate,hz,channels) in new[]{(16000,220d,1),(16000,997d,1),(44100,3000d,2),(48000,123.4,2)})
        {
            byte[] source=Tone(rate,hz,rate*2,channels);double before=Peak(WaveChecks.Decode(source),rate,channels,hz*.4,hz*2.6);
            foreach(int percent in new[]{25,50,80,125,200,400})
            {
                double after=Peak(WaveChecks.Decode(Render(source,new(percent,true))),rate,channels,hz*.4,hz*2.6),shift=Math.Abs(after/before-1);worst=Math.Max(worst,shift);
                check(shift<.0035,$"Keep-pitch tone peak stays within 0.35%: {hz}Hz {percent}% {before:0.##}->{after:0.##}");
                if(percent is 50 or 200&&hz<8000*percent/200d){double linked=Peak(WaveChecks.Decode(Render(source,new(percent))),rate,channels,hz*.4,hz*2.6);check(Math.Abs(linked/before-percent/100d)<.002,"Pitch-linked tone moves with speed (control)");}
            }
        }
        Console.WriteLine($"Keep-pitch tone peaks (220/997/3000/123.4 Hz, 25–400%): worst relative shift {worst*100:0.0000}%");
        // Clicks every 0.25 s: count detected output clicks and the worst time error against source time × 100/percent.
        byte[] clicks=Fixture(16,1,64000,16000,(i,_) =>i%4000==2000?20000:0);
        foreach(int percent in new[]{25,50,80,125,200,400})
        {
            int[] values=WaveChecks.Decode(Render(clicks,new(percent,true)));var found=new System.Collections.Generic.List<int>();
            for(int i=0;i<values.Length;i++)if(Math.Abs(values[i])>4000&&(found.Count==0||i-found[^1]>160))found.Add(i);
            var expected=Enumerable.Range(0,16).Select(k=>(2000+4000*k)*100d/percent).Where(t=>t<values.Length-1).ToArray();
            double error=expected.Select(t=>found.Count==0?double.MaxValue:found.Min(f=>Math.Abs(f-t))).DefaultIfEmpty(0).Max()/16;
            int extra=found.Count(f=>expected.Min(t=>Math.Abs(f-t))>400);
            Console.WriteLine($"Keep-pitch clicks {percent}%: expected {expected.Length}, detected {found.Count}, echoes {extra}, worst expected-click distance {error:0.0} ms, peak {values.Select(Math.Abs).Max()}/20000");
            if(percent<=125)check(error<=30,"Keep-pitch clicks stay within 30 ms of scaled time at "+percent+"%");
        }
        byte[] big=Tone(48000,440,48000*20,2);var timer=Stopwatch.StartNew();Render(big,new(73,true));timer.Stop();
        Console.WriteLine($"Keep-pitch 20 s 48 kHz stereo at 73%: {timer.Elapsed.TotalSeconds:0.00} s (this Mac, core only); measurements {clock.Elapsed.TotalSeconds:0.0} s");
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation):MemoryStream
    {public override void Write(ReadOnlySpan<byte> buffer){base.Write(buffer);if(Length>100)cancellation.Cancel();}}
    private sealed class CountingInput(byte[] bytes):MemoryStream(bytes)
    {public int Reads{get;private set;}public override int Read(Span<byte> buffer){Reads++;return base.Read(buffer);}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes)
    {public override int Read(Span<byte> buffer){if(Position>=44)throw new IOException("Injected source read failure");return base.Read(buffer);}}
}
