using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class FloatWaveChecks
{
    internal static byte[] Fixture(double[] samples, int bits = 32, int channels = 1, int rate = 8000, bool extended = false, bool fact = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVEfmt "u8); writer.Write(extended ? 40 : 16);
        writer.Write(extended ? (ushort)0xfffe : (ushort)3); writer.Write((ushort)channels); writer.Write(rate);
        writer.Write(rate * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
        if (extended) { writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(channels == 1 ? 4u : 3u); writer.Write(new byte[] {3,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113}); }
        if (fact) { writer.Write("fact"u8); writer.Write(4); writer.Write(samples.Length / channels); }
        writer.Write("data"u8); writer.Write(samples.Length * bits / 8);
        foreach (double sample in samples) { if (bits == 32) writer.Write((float)sample); else writer.Write(sample); }
        var bytes = stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8); return bytes;
    }
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] Render(byte[] source, int bits, bool clip = false, bool preview = false)
        { using var input = new MemoryStream(source); using var output = new MemoryStream(); FloatWave.Export(input, output, new(bits,clip), default, preview); check(input.ToArray().SequenceEqual(source) && input.CanRead && output.CanWrite,"Float conversion preserves source and stream ownership");return output.ToArray(); }
        foreach (int bits in new[] {32,64}) foreach (int channels in new[] {1,2}) foreach(bool extended in new[]{false,true}) foreach(bool fact in new[]{false,true})
        {
            double[] values = {-1,1,-0d,0d,0.5,-0.5,0.25,-0.25}; byte[] source=Fixture(values,bits,channels,11025,extended,fact);
            using var input = new MemoryStream(source);var info=FloatWave.Read(input,default);var stats=FloatWave.Scan(input,default);
            check(info.Bits==bits&&info.Channels==channels&&info.Rate==11025&&info.Frames==values.Length/channels&&stats==new FloatWaveStats(1,0,8),"Canonical and Extensible float facts independent of format and channel count");
            foreach(int target in new[]{16,24,32})
            {
                byte[] output=Render(source,target);using var saved=new MemoryStream(output);var pcm=PcmWave.Read(saved,default);
                check(pcm.Bits==target&&pcm.Rate==11025&&pcm.Channels==channels&&pcm.Frames==info.Frames,"Float output preserves timebase and explicit target depth");
                long scale=1L<<(target-1);int[] expected=values.Select(x=>(int)(x==1?scale-1:x==-1?-scale:(long)(x*scale))).ToArray();
                check(WaveChecks.Decode(output).SequenceEqual(expected),"Float endpoints/zeros/fractions map to integer PCM without channel mix");
                check(output.Length==new FloatPcmOptions(target).OutputBytes(info),"Float full output header and padding budget agree");
                using var wanted=new MemoryStream();PcmWave.Export(saved,wanted,new(0,pcm.Seconds),default,true);
                check(Render(source,target,preview:true).SequenceEqual(wanted.ToArray()),"Float preview quantizes to chosen target first then PCM16");
            }
        }
        var rng=new Random(3301);
        foreach(int bits in new[]{32,64}) foreach(int target in new[]{16,24,32})
        {
            long scale=1L<<(target-1);var values=Enumerable.Range(0,4097*2).Select(_=>rng.NextDouble()*2-1).ToArray();
            values[0]=.5/scale;values[1]=-.5/scale;values[2]=double.Epsilon;values[3]=-double.Epsilon;
            if(bits==32)values=values.Select(x=>(double)(float)x).ToArray();
            var decoded=WaveChecks.Decode(Render(Fixture(values,bits,2,48000),target));
            for(int i=0;i<values.Length;i++) check(decoded[i]==(int)Math.Clamp(Math.Round(values[i]*scale,MidpointRounding.AwayFromZero),-scale,scale-1),"Float independent quantization across two block boundaries");
        }
        foreach(int bits in new[]{32,64})
        {
            double maximum=bits==32?float.MaxValue:double.MaxValue;byte[] source=Fixture(new[]{-maximum,-1.0001,-1,1,1.0001,maximum},bits);
            using var scan=new MemoryStream(source);var stats=FloatWave.Scan(scan,default);check(stats.Peak==maximum&&stats.OutsideUnit==4&&stats.Samples==6,"Out-of-unit counts individual sample values with finite absolute peak");
            foreach(int target in new[]{16,24,32})
            {
                using var input=new MemoryStream(source);using var output=new MemoryStream();reject(()=>FloatWave.Export(input,output,new(target),default),"Clipping requires explicit permission");check(output.Length==0,"Unapproved clipping writes no header");
                long scale=1L<<(target-1);check(WaveChecks.Decode(Render(source,target,true)).SequenceEqual(new[]{(int)-scale,(int)-scale,(int)-scale,(int)(scale-1),(int)(scale-1),(int)(scale-1)}),"Maximum finite floats clamp before multiplying without overflow");
            }
        }
        foreach(int bits in new[]{32,64})foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            var values=new double[80001];values[^1]=invalid;byte[] source=Fixture(values,bits);
            using var input=new MemoryStream(source);reject(()=>FloatWave.Scan(input,default),"Full source scan includes nonfinite beyond preview prefix");
            foreach(bool preview in new[]{true,false})using(var output=new MemoryStream()){reject(()=>FloatWave.Export(input,output,new(24,true),default,preview),"Export always refuses nonfinite before any output");check(output.Length==0,"NaN/Infinity beyond10s never writes a preview header");}
        }
        byte[] longSource=Fixture(Enumerable.Range(0,100000).Select(i=>Math.Sin(i/73d)*.9).ToArray(),64);
        foreach(int target in new[]{16,24,32})
        {byte[] output=Render(longSource,target),preview=Render(longSource,target,preview:true);using var input=new MemoryStream(output);using var wanted=new MemoryStream();PcmWave.Export(input,wanted,new(0,12.5),default,true);check(preview.SequenceEqual(wanted.ToArray()),"Ten-second float preview matches saved output prefix byte-exactly");}
        byte[] canonical=Fixture(new[]{.25,-.75});
        void Bad(Action<byte[]> mutate,string name){var bytes=canonical.ToArray();mutate(bytes);using var input=new MemoryStream(bytes);reject(()=>FloatWave.Read(input,default),name);}
        Bad(x=>x[0]=(byte)'X',"Non-RIFF refused");Bad(x=>x[4]--,"RIFF size mismatch");Bad(x=>x[20]=1,"Integer format remains separate");Bad(x=>x[22]=3,"Multichannel refused");Bad(x=>x[24]=1,"Out-of-range rate refused");Bad(x=>x[28]++,"Bad average byte rate refused");Bad(x=>x[32]++,"Bad block alignment refused");Bad(x=>x[34]=16,"Half-float/other widths refused");Bad(x=>x[40]--,"Misaligned data refused");Bad(x=>x[40]=0,"Empty data refused");
        byte[] withFact=Fixture(new[]{.1,.2},fact:true);withFact[44]=1;using(var input=new MemoryStream(withFact))reject(()=>FloatWave.Read(input,default),"Fact sample frames must match complete data");
        byte[] extendedBad=Fixture(new[]{.1},extended:true);extendedBad[44]=1;using(var input=new MemoryStream(extendedBad))reject(()=>FloatWave.Read(input,default),"Extensible PCM GUID is not float");
        byte[] AddChunks(byte[] source,byte[] addition)
        {var result=source.Concat(addition).ToArray();BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4),result.Length-8);return result;}
        byte[] unknown={(byte)'J',(byte)'U',(byte)'N',(byte)'K',1,0,0,0,99,0};
        using(var input=new MemoryStream(AddChunks(canonical,unknown)))check(FloatWave.Read(input,default).Frames==2,"Unknown odd chunk payload and pad skipped without content interpretation");
        using(var input=new MemoryStream(AddChunks(canonical,unknown[..^1])))reject(()=>FloatWave.Read(input,default),"Missing odd chunk pad refused");
        foreach(byte[] addition in new[]{canonical[12..36],canonical[36..],Fixture(new[]{0d},fact:true)[36..48].Concat(Fixture(new[]{0d},fact:true)[36..48]).ToArray()})
        {using var input=new MemoryStream(AddChunks(canonical,addition));reject(()=>FloatWave.Read(input,default),"Duplicate format/data/fact refused");}
        byte[] emptyUnknown={(byte)'J',(byte)'U',(byte)'N',(byte)'K',0,0,0,0};
        using(var input=new MemoryStream(AddChunks(canonical,Enumerable.Range(0,1022).SelectMany(_=>emptyUnknown).ToArray())))check(FloatWave.Read(input,default).Frames==2,"Exactly1024 chunks accepted");
        using(var input=new MemoryStream(AddChunks(canonical,Enumerable.Range(0,1023).SelectMany(_=>emptyUnknown).ToArray())))reject(()=>FloatWave.Read(input,default),"More than1024 chunks refused");
        // Max input can fit float32's short header but not the extended integer32 header.
        long frames=(PcmWave.MaxBytes-44)/4;var boundary=new FloatWaveInfo(192000,1,32,44,frames*4);
        reject(()=>new FloatPcmOptions(32).OutputBytes(boundary),"Float32 short header expansion is included before even preview");
        var exact=boundary with{DataBytes=(PcmWave.MaxBytes-68)/4*4};check(new FloatPcmOptions(32).OutputBytes(exact)==PcmWave.MaxBytes,"Exact512MiB target including68-byte header accepted");
        check(new FloatPcmOptions(24).OutputBytes(new(8000,1,64,44,8))==72,"Odd24-bit mono payload includes pad");
        foreach(int target in new[]{0,8,20,64})reject(()=>new FloatPcmOptions(target).OutputBytes(boundary),"Only16/24/32 integer output allowed");
        check(new FloatPcmOptions(16).OutputBytes(new(8000,1,32,44,8000L*7200*4))>0,"Exactly two hours allowed");reject(()=>new FloatPcmOptions(16).OutputBytes(new(8000,1,32,44,(8000L*7200+1)*4)),"Two-hour output limit enforced");
        using(var same=new MemoryStream(canonical))reject(()=>FloatWave.Export(same,same,new(),default),"Aliased streams refused");
        using(var input=new MemoryStream(canonical))using(var output=new MemoryStream()){reject(()=>FloatWave.Export(input,output,new(),new CancellationToken(true)),"Precancelled conversion");check(output.Length==0,"Precancelled conversion writes nothing");}
        using(var token=new CancellationTokenSource())using(var input=new CancelInput(longSource,token))reject(()=>FloatWave.Scan(input,token.Token),"Cancellation during full scan observed");
        using(var token=new CancellationTokenSource())using(var input=new MemoryStream(longSource))using(var output=new CancelOutput(token))reject(()=>FloatWave.Export(input,output,new(),token.Token),"Cancellation after staged output block observed");
        using(var input=new BrokenInput(longSource))using(var output=new MemoryStream()){reject(()=>FloatWave.Export(input,output,new(),default),"Read failure before header propagates");check(output.Length==0,"Damaged full scan writes nothing");}
        check(Catalog.Available(new Prefs(),new[]{"float.WAV"}).Any(x=>x.Id=="audio.floatpcm")&&!Catalog.Available(new Prefs(),new[]{"x.mp3"}).Any(x=>x.Id=="audio.floatpcm")&&!Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.floatpcm"),"Float converter requires one explicit WAV");
    }
    private sealed class CancelInput(byte[] bytes,CancellationTokenSource token):MemoryStream(bytes)
    {public override int Read(Span<byte> buffer){int count=base.Read(buffer);if(Position>40000)token.Cancel();return count;}}
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream
    {public override void Write(ReadOnlySpan<byte> bytes){base.Write(bytes);if(Length>100)token.Cancel();}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes)
    {public override int Read(Span<byte> buffer){if(Position>=44)throw new IOException("Injected failure");return base.Read(buffer);}}
}
