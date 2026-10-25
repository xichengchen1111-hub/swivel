using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class AiffExportChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Render(byte[] source,bool preview=false)
        {using var input=new MemoryStream(source);using var output=new MemoryStream();AiffExport.Export(input,output,default,preview);check(input.ToArray().SequenceEqual(source)&&input.CanRead&&output.CanWrite,"AIFF export preserves source bytes and stream ownership");return output.ToArray();}
        var random=new Random(3501);
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})foreach(int rate in new[]{8000,11025,44100,47999,192000})
        {
            long scale=1L<<(bits-1);int[] samples=Enumerable.Range(0,4097*channels).Select(_=>(int)random.NextInt64(-scale,scale)).ToArray();samples[0]=(int)-scale;samples[1]=(int)(scale-1);samples[2]=0;
            byte[] wav=WaveChecks.Fixture(bits,channels,samples);BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28),rate*channels*(bits/8));
            byte[] aiff=Render(wav);using var saved=new MemoryStream(aiff);var info=AiffWave.Read(saved,default);
            check(info.Bits==bits&&info.Channels==channels&&info.Rate==rate&&info.Frames==4097&&!info.CompressedForm&&!info.LittleEndian,"Plain AIFF output preserves original integer format with canonical rate");
            check(aiff.SequenceEqual(AiffChecks.Fixture(bits,channels,rate,samples)),"AIFF output header/sample order/sign/pad matches standalone test fixture");
            using var back=new MemoryStream();AiffWave.Export(saved,back,default);check(WaveChecks.Decode(back.ToArray()).SequenceEqual(samples),"AIFF export retains integer extremes/random/zero and channel order across4096 boundary");
            using var expected=new MemoryStream();AiffWave.Export(saved,expected,default,true);check(Render(wav,true).SequenceEqual(expected.ToArray()),"AIFF playback preview equals savedAIFF decoded asPCM16");
        }
        foreach(int bits in new[]{8,16,24,32})foreach(int count in new[]{1,2,3,80001})
        {
            byte[] wav=WaveChecks.Fixture(bits,1,Enumerable.Range(0,count).Select(i=>i%3-1).ToArray());byte[] aiff=Render(wav);using var saved=new MemoryStream(aiff);var info=AiffWave.Read(saved,default);
            check(aiff.Length==54L+info.Frames*(bits/8)+((info.Frames*(bits/8))&1),"AIFF output body and outside pad use exact source frames");
            check(BinaryPrimitives.ReadUInt32BigEndian(aiff.AsSpan(4))==aiff.Length-8&&BinaryPrimitives.ReadUInt32BigEndian(aiff.AsSpan(42))==8+info.Frames*(bits/8),"FORM includes finalpad while SSND length excludes it");
            using var expected=new MemoryStream();AiffWave.Export(saved,expected,default,true);check(Render(wav,true).SequenceEqual(expected.ToArray()),"AIFF long preview stops at10s with exact sample quantization");
        }
        byte[] canonical=WaveChecks.Fixture(16,1,new[]{-32768,0,32767});
        // Inspect 24/32-bit EXTENSIBLE inputs independently of ordinary fmt16 fixtures.
        foreach(int bits in new[]{24,32})
        {
            byte[] source=WaveChecks.Fixture(bits,2,new[]{-1,0,123,-123});using var input=new MemoryStream(source);using var extended=new MemoryStream();PcmWave.Export(input,extended,new(0,2d/8000),default);
            using var aiff=new MemoryStream(Render(extended.ToArray()));using var back=new MemoryStream();AiffWave.Export(aiff,back,default);check(WaveChecks.Decode(back.ToArray()).SequenceEqual(new[]{-1,0,123,-123}),"Extensible integerPCM input preserves all sample values inAIFF");
        }
        long exact=PcmWave.MaxBytes-54;
        check(AiffExport.OutputBytes(new(192000,1,8,44,exact))==PcmWave.MaxBytes,"AIFF output exact512MiB including54byte header");
        reject(()=>AiffExport.OutputBytes(new(192000,1,8,44,exact+1)),"One source frame adds oddpad and exceeds AIFF budget");
        using(var input=new VirtualWave(exact))using(var output=new MemoryStream())
        {AiffExport.Export(input,output,default,true);check(output.Length==3840044,"Exact full-output byte budget still permits bounded10sPCM16preview");}
        foreach(long bytes in new[]{exact+1,PcmWave.MaxBytes-44})using(var input=new VirtualWave(bytes))using(var output=new MemoryStream())
        {reject(()=>AiffExport.Export(input,output,default,true),"ValidWAV whose AIFF header expands beyond512MiB refused evenforpreview");check(output.Length==0,"Overbudget AIFF preview writes noheader");}
        check(AiffExport.OutputBytes(new(8000,1,8,44,8000L*7200))>0,"Two-hour output exactly accepted");reject(()=>AiffExport.OutputBytes(new(8000,1,8,44,8000L*7200+1)),"Over-two-hour output refused");
        using(var same=new MemoryStream(canonical))reject(()=>AiffExport.Export(same,same,default),"AIFF export refuses aliased streams");
        using(var input=new MemoryStream(canonical))using(var output=new MemoryStream()){reject(()=>AiffExport.Export(input,output,new CancellationToken(true)),"AIFF precancelled export");check(output.Length==0,"AIFF precancel writes nothing");}
        using(var token=new CancellationTokenSource())using(var input=new MemoryStream(WaveChecks.Fixture(32,2,new int[30000])) )using(var output=new CancelOutput(token))reject(()=>AiffExport.Export(input,output,token.Token),"AIFF export observes cancellation after output block");
        using(var input=new BrokenInput(canonical))using(var output=new MemoryStream())reject(()=>AiffExport.Export(input,output,default),"AIFF output read failures do not fillmissing samples");
        using(var input=new MemoryStream(FloatWaveChecks.Fixture(new[]{0d})))using(var output=new MemoryStream()){reject(()=>AiffExport.Export(input,output,default),"Float input must first use explicit float→PCMconverter");check(output.Length==0,"Unsupported source writes no AIFFheader");}
        check(Catalog.Available(new Prefs(),new[]{"clip.WAV"}).Any(x=>x.Id=="audio.exportaiff")&&!Catalog.Available(new Prefs(),new[]{"a.aif"}).Any(x=>x.Id=="audio.exportaiff")&&!Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.exportaiff"),"AIFF export requires one explicitly supplied WAV");
    }
    private sealed class VirtualWave:MemoryStream
    {
        private readonly long length;
        public VirtualWave(long bytes):base(Header(bytes)){length=44+bytes+(bytes&1);}
        public override long Length=>length;
        private static byte[] Header(long bytes)
        {byte[] h=WaveChecks.Fixture(8,1,new[]{0})[..44];BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4),(uint)(36+bytes+(bytes&1)));BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40),(uint)bytes);BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24),192000);BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28),192000);return h;}
        public override int Read(Span<byte> target){if(Position<44)return base.Read(target);int count=(int)Math.Min(target.Length,Length-Position);target[..count].Clear();Position+=count;return count;}
    }
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream{public override void Write(ReadOnlySpan<byte> bytes){base.Write(bytes);if(Length>100)token.Cancel();}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes){public override int Read(Span<byte> bytes){if(Position>=44)throw new IOException("Injected sample read failure");return base.Read(bytes);}}
}
