using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveSpeedChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int bits,int channels,int frames,int rate,Func<int,int,int> sample)
        {
            byte[] bytes=WaveChecks.Fixture(bits,channels,Enumerable.Range(0,frames*channels).Select(i=>sample(i/channels,i%channels)).ToArray());
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),rate*channels*(bits/8));return bytes;
        }
        byte[] Render(byte[] bytes,int percent,bool preview=false)
        {
            using var input=new MemoryStream(bytes);using var output=new MemoryStream();
            WaveConverter.ExportSpeed(input,output,new(percent),default,preview);
            check(input.CanRead&&output.CanWrite&&input.ToArray().SequenceEqual(bytes),"Speed leaves streams owned and original bytes intact");return output.ToArray();
        }
        WaveInfo Info(byte[] bytes){using var stream=new MemoryStream(bytes);return PcmWave.Read(stream,default);}
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            int scale=bits==32?int.MaxValue:1<<(bits-1);
            byte[] source=Fixture(bits,channels,11,11025,(i,c)=>(i+c)%4==0?-(bits==32?int.MaxValue:scale):(i+c)%4==1?scale-1:(i+c)%4==2?0:scale/3);
            byte[] neutral=Render(source,100);check(WaveChecks.Decode(neutral).SequenceEqual(WaveChecks.Decode(source)),"100% is sample-exact at all integer depths and channels");
            foreach(int percent in new[]{50,73,99,100,101,137,199,200})
            {
                byte[] output=Render(source,percent);var info=Info(output);
                check(info.Frames==(11L*100+percent/2)/percent&&info.Rate==11025&&info.Channels==channels&&info.Bits==bits,"Speed frame count rounds once and format stays unchanged");
                check(output.Length==info.DataOffset+info.DataBytes+(info.DataBytes&1),"Speed RIFF header and odd-byte padding agree");
                byte[] preview=Render(source,percent,true);using var full=new MemoryStream(output);using var expected=new MemoryStream();
                PcmWave.Export(full,expected,new(0,info.Seconds),default,true);
                check(preview.SequenceEqual(expected.ToArray()),"Preview matches saved quantization then PCM16 for every depth/channel/speed");
            }
        }
        foreach(int percent in new[]{50,73,99,101,137,199,200})
        {
            byte[] source=Fixture(24,2,13000,47999,(_,c)=>c==0?1234567:-2345678);var values=WaveChecks.Decode(Render(source,percent));
            check(values.Select((x,i)=>Math.Abs(x-(i%2==0?1234567:-2345678))).Max()<=1,"Speed DC and stereo separation across both source cache page boundaries");
        }
        byte[] Tone(int rate,double hz,int frames)=>Fixture(16,1,frames,rate,(i,_) =>(int)Math.Round(12000*Math.Sin(2*Math.PI*hz*i/rate)));
        foreach(int percent in new[]{50,73,99,101,125,150,199,200})
        {
            int[] values=WaveChecks.Decode(Render(Tone(16000,997,16000),percent));
            double error=Math.Sqrt(values.Skip(300).Take(values.Length-600).Select((v,i)=>Math.Pow(v-12000*Math.Sin(2*Math.PI*997*percent*(i+300)/(16000d*100)),2)).Average());
            check(error<2,"Pitch/time follow requested playback rate at rational phases: "+percent);
        }
        foreach(int percent in new[]{125,150,200})foreach(double ratio in new[]{1d,1.01,1.5})
        {
            double hz=8000d*100/percent*ratio;if(hz>=8000)continue;
            int[] values=WaveChecks.Decode(Render(Tone(16000,hz,16000),percent));double rms=Math.Sqrt(values.Skip(300).Take(values.Length-600).Select(x=>(double)x*x).Average());
            check(rms<2,"Speed-up suppresses target Nyquist and above instead of aliasing: "+percent+"/"+ratio);
        }
        foreach(int percent in new[]{125,150,200})
        {
            double hz=8000d*100/percent;
            byte[] source=Fixture(16,1,16000,16000,(i,_) =>(int)Math.Round(12000*Math.Cos(2*Math.PI*hz*i/16000)));
            int[] values=WaveChecks.Decode(Render(source,percent));
            double rms=Math.Sqrt(values.Skip(300).Take(values.Length-600).Select(x=>(double)x*x).Average());
            check(rms<=2.1,"Speed-up target Nyquist worst-phase cosine stays within quantized2.1PCM16-count RMS");
        }
        byte[] longSource=Tone(8000,997,130000);
        foreach(int percent in new[]{50,137})
        {
            byte[] output=Render(longSource,percent),preview=Render(longSource,percent,true);var info=Info(output);
            using var full=new MemoryStream(output);using var expected=new MemoryStream();PcmWave.Export(full,expected,new(0,info.Seconds),default,true);
            check(preview.SequenceEqual(expected.ToArray())&&Info(preview).Frames==80000,"Ten output-second preview is prefix with identical full-source boundaries");
        }
        foreach(int percent in new[]{int.MinValue,0,49,201,int.MaxValue})reject(()=>new WaveSpeed(percent).Frames(new(8000,1,16,44,20)),"Speed rejects out-of-range percent");
        check(new WaveSpeed(200).Frames(new(8000,1,16,44,2))==1,"One input frame at200% rounds half up");
        check(new WaveSpeed(50).Frames(new(8000,1,16,44,8000L*3600*2))==8000L*7200,"Exactly two output hours accepted");
        reject(()=>new WaveSpeed(50).Frames(new(8000,1,16,44,(8000L*3600+1)*2)),"More than two output hours refused");
        long boundary=(PcmWave.MaxBytes-44)/2;
        check(new WaveSpeed(50).Frames(new(192000,1,8,44,boundary))==boundary*2,"Exact512MiB output bound accepted including header");
        reject(()=>new WaveSpeed(50).Frames(new(192000,1,8,44,boundary+1)),"One source frame beyond byte bound refused before writing");
        using(var same=new MemoryStream(longSource))reject(()=>WaveConverter.ExportSpeed(same,same,new(),default),"Speed rejects aliased input/output streams");
        using(var source=new MemoryStream(longSource))using(var output=new MemoryStream())
        {
            reject(()=>WaveConverter.ExportSpeed(source,output,new(49),default),"Invalid speed export refused");check(output.Length==0,"Invalid speed writes no header");
            reject(()=>WaveConverter.ExportSpeed(source,output,new(),new CancellationToken(true)),"Pre-cancelled speed export");check(output.Length==0,"Pre-cancellation writes nothing");
        }
        using(var cancellation=new CancellationTokenSource())using(var source=new MemoryStream(longSource))using(var output=new CancelOutput(cancellation))
        {
            reject(()=>WaveConverter.ExportSpeed(source,output,new(73),cancellation.Token),"Cancellation after first output block observed");check(output.Length<4096,"Cancelled speed does not finish writing");
        }
        using(var source=new CountingInput(Tone(16000,997,20000)))using(var output=new MemoryStream())
        {WaveConverter.ExportSpeed(source,output,new(137),default);check(source.Reads<20,"Speed cache avoids per-sample file reads");}
        using(var source=new BrokenInput(longSource))using(var output=new MemoryStream())reject(()=>WaveConverter.ExportSpeed(source,output,new(73),default),"Speed read failures propagate without padding damaged source");
        check(Catalog.Available(new Prefs(),new[]{"clip.WAV"}).Any(x=>x.Id=="audio.speed")&&!Catalog.Available(new Prefs(),new[]{"a.mp3"}).Any(x=>x.Id=="audio.speed")&&!Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.speed"),"Speed action needs one explicit WAV");
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation):MemoryStream
    {public override void Write(ReadOnlySpan<byte> buffer){base.Write(buffer);if(Length>100)cancellation.Cancel();}}
    private sealed class CountingInput(byte[] bytes):MemoryStream(bytes)
    {public int Reads{get;private set;}public override int Read(Span<byte> buffer){Reads++;return base.Read(buffer);}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes)
    {public override int Read(Span<byte> buffer){if(Position>=44)throw new IOException("Injected source read failure");return base.Read(buffer);}}
}
