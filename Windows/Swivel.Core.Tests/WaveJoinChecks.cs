using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveJoinChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] Render(byte[][] bytes, bool preview = false)
        {
            var sources = bytes.Select(b => new MemoryStream(b)).ToArray();
            try { using var output = new MemoryStream(); WaveJoin.Export(sources,output,default,preview); check(sources.Select((s,i)=>s.CanRead&&s.ToArray().SequenceEqual(bytes[i])).All(x=>x),"Join preserves source bytes and stream ownership"); return output.ToArray(); }
            finally { foreach(var source in sources)source.Dispose(); }
        }
        var random = new Random(3701);
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})foreach(int rate in new[]{8000,11025,44100,192000})
        {
            var values = new[]{1,3,4095,4096,4097}.Select(frames=>Enumerable.Range(0,frames*channels).Select(_=>(int)random.NextInt64(-(1L<<(bits-1)),1L<<(bits-1))).ToArray()).ToArray();
            values[0][0]=bits==32?int.MinValue:-(1<<(bits-1)); values[1][0]=bits==32?int.MaxValue:(1<<(bits-1))-1;
            var source = values.Select(v=>Fixture(bits,channels,rate,v)).ToArray(); byte[] saved=Render(source);
            using var input=new MemoryStream(saved);var info=PcmWave.Read(input,default);check(info.Rate==rate&&info.Bits==bits&&info.Channels==channels&&info.Frames==values.Sum(v=>v.Length/channels),"Join keeps format, full frame count and channel order");
            check(WaveChecks.Decode(saved).SequenceEqual(values.SelectMany(v=>v)),"Join every sample exact across4096 and odd-source padding");
            check(saved.Length==(bits>16?68:44)+info.DataBytes+(info.DataBytes&1),"Join one header and only final combined padding");
            using var expected=new MemoryStream();PcmWave.Export(input,expected,new(0,info.Seconds),default,true);check(Render(source,true).SequenceEqual(expected.ToArray()),"Join preview matches saved full WAV decoded PCM16");
            check(WaveChecks.Decode(Render(source.Reverse().ToArray())).SequenceEqual(values.Reverse().SelectMany(v=>v)),"Join reverse order changes exact audio order");
        }
        foreach(int first in new[]{79999,80000,80001})
        {
            byte[][] source={Fixture(24,1,8000,Enumerable.Repeat(17,first).ToArray()),Fixture(24,1,8000,new[]{1234567,-7654321,8388607,-8388608})};
            var full=Render(source);using var input=new MemoryStream(full);var info=PcmWave.Read(input,default);using var expected=new MemoryStream();PcmWave.Export(input,expected,new(0,info.Seconds),default,true);
            check(Render(source,true).SequenceEqual(expected.ToArray()),"Join10s cut before/on/after source boundary with exact target quantization");
        }
        var two=new[]{Fixture(8,1,8000,new[]{1}),Fixture(8,1,8000,new[]{-1})};
        check(WaveChecks.Decode(Render(Enumerable.Repeat(two[0],32).ToArray())).Length==32,"Join32 distinct streams accepted");
        foreach(int count in new[]{0,1,33})using(var output=new MemoryStream())
        {var sources=Enumerable.Range(0,count).Select(_=>new MemoryStream(two[0])).ToArray();try{reject(()=>WaveJoin.Export(sources,output,default),"Join count refused");check(output.Length==0,"Count refusal writes no header");}finally{foreach(var source in sources)source.Dispose();}}
        foreach(byte[] last in new[]{Fixture(16,1,8000,new[]{0}),Fixture(8,2,8000,new[]{0,0}),Fixture(8,1,11025,new[]{0}),new byte[44],FloatWaveChecks.Fixture(new[]{0d})})
        {using var first=new MemoryStream(two[0]);using var bad=new MemoryStream(last);using var output=new MemoryStream();reject(()=>WaveJoin.Export(new Stream[]{first,bad},output,default,true),"Last source invalid/mismatched evenforpreview");check(output.Length==0,"Last-source refusal precedes output header");}
        using(var first=new MemoryStream(two[0]))using(var second=new MemoryStream(two[1]))
        {
            reject(()=>WaveJoin.Export(new Stream[]{first,second},second,default),"Output aliases last source refused");check(second.ToArray().SequenceEqual(two[1]),"Alias refusal leaves original intact");
            using var output=new MemoryStream();reject(()=>WaveJoin.Export(new Stream[]{first,first},output,default),"Same stream object twice refused");check(output.Length==0,"Duplicate stream refusal writes nothing");
        }
        using(var first=new VirtualWave(8,192000,PcmWave.MaxBytes/2-44))using(var second=new VirtualWave(8,192000,PcmWave.MaxBytes/2-44))
        {var sum=WaveJoin.Inspect(new Stream[]{first,second},default);check(sum.SourceBytes==PcmWave.MaxBytes&&sum.OutputBytes==PcmWave.MaxBytes-44,"Combined source exact512MiB includes every source header");using var preview=new MemoryStream();WaveJoin.Export(new Stream[]{first,second},preview,default,true);check(preview.Length==3840044,"Exactaggregate budget permits boundedpreview");}
        using(var first=new VirtualWave(8,192000,PcmWave.MaxBytes/2-44))using(var second=new VirtualWave(8,192000,PcmWave.MaxBytes/2-43))using(var output=new MemoryStream())
        {reject(()=>WaveJoin.Export(new Stream[]{first,second},output,default,true),"Aggregate adds one frame plus pad beyond512MiB refused evenforpreview");check(output.Length==0,"Overaggregate writes nothing");}
        foreach(int extra in new[]{0,1})using(var first=new VirtualWave(16,8000,8000L*3600*2))using(var second=new VirtualWave(16,8000,(8000L*3600+extra)*2))using(var output=new MemoryStream())
        {if(extra==0){var sum=WaveJoin.Inspect(new Stream[]{first,second},default);check(sum.Frames==8000L*7200,"Joined twohours exact integer-frame budget");}else{reject(()=>WaveJoin.Export(new Stream[]{first,second},output,default,true),"Joinedtwohours+1frame refused forpreview");check(output.Length==0,"Duration refusal beforeheader");}}
        using(var first=new MemoryStream(two[0]))using(var second=new MemoryStream(two[1]))using(var output=new MemoryStream())
        {reject(()=>WaveJoin.Export(new Stream[]{first,second},output,new CancellationToken(true)),"Joinpre-cancel");check(output.Length==0,"Joinpre-cancel noheader");}
        using(var token=new CancellationTokenSource())using(var first=new MemoryStream(Fixture(32,1,8000,new int[30000])))using(var second=new MemoryStream(two[0]))using(var output=new CancelOutput(token))
        { // Use a valid second format so cancellation happens during data, not format validation.
            using var valid=new MemoryStream(Fixture(32,1,8000,new[]{1}));reject(()=>WaveJoin.Export(new Stream[]{first,valid},output,token.Token),"Join observes middata cancellation");check(token.IsCancellationRequested,"Join cancellation reached data write");
        }
        using(var first=new BrokenInput(two[0]))using(var second=new MemoryStream(two[1]))using(var output=new MemoryStream())reject(()=>WaveJoin.Export(new Stream[]{first,second},output,default),"Join read failure not filled with silence");
        foreach(bool preview in new[]{false,true}) using(var output=new MemoryStream())
        {
            bool bounded=false;try{WaveJoin.Export(new PoisonList(33),output,default,preview);}catch(UserError){bounded=true;}check(bounded&&output.Length==0,"Join rejects oversized collection beforeenumeration");
        }
        bool inspectBounded=false;try{WaveJoin.Inspect(new PoisonList(33),default);}catch(UserError){inspectBounded=true;}check(inspectBounded,"Joininspect rejects count beforecloning");
        bool inspectCancelled=false;try{WaveJoin.Inspect(new PoisonList(2),new CancellationToken(true));}catch(OperationCanceledException){inspectCancelled=true;}check(inspectCancelled,"Joininspect precancel beforeenumeration");
        var prefs=new Prefs();prefs.Set(Feature.Wheel,false);
        check(Catalog.Available(prefs,new[]{"a.wav","b.WAV"}).Any(x=>x.Id=="audio.join"),"Join independentofWheel with twoWAV");
        foreach(var files in new[]{new[]{"a.wav"},new[]{"a.wav","b.aif"},Enumerable.Range(0,33).Select(i=>i+".wav").ToArray()})check(!Catalog.Available(prefs,files).Any(x=>x.Id=="audio.join"),"Joincatalog enforcesfilecount/extension");
    }
    private static byte[] Fixture(int bits,int channels,int rate,int[] samples)
    {byte[] bytes=WaveChecks.Fixture(bits,channels,samples);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),rate*channels*(bits/8));return bytes;}
    private sealed class VirtualWave:MemoryStream
    {
        private readonly long length;
        public VirtualWave(int bits,int rate,long data):base(Header(bits,rate,data)){length=44+data+(data&1);}
        public override long Length=>length;
        private static byte[] Header(int bits,int rate,long data)
        {byte[] h=Fixture(bits,1,rate,new[]{0})[..44];BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4),(uint)(36+data+(data&1)));BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40),(uint)data);return h;}
        public override int Read(Span<byte> target){if(Position<44)return base.Read(target);int count=(int)Math.Min(target.Length,Length-Position);target[..count].Clear();Position+=count;return count;}
    }
    private sealed class PoisonList(int count):IReadOnlyList<Stream>
    {
        public int Count=>count;public Stream this[int index]=>throw new Exception("Unexpected collection indexing");
        public IEnumerator<Stream> GetEnumerator()=>throw new Exception("Unexpected collection enumeration");IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream{public override void Write(ReadOnlySpan<byte> bytes){base.Write(bytes);if(Length>100)token.Cancel();}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes){public override int Read(Span<byte> bytes){if(Position>=44)throw new IOException("Injected join read failure");return base.Read(bytes);}}
}
