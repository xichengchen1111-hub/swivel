using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveFormatChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] Render(byte[] bytes, WaveFormat format, bool preview = false)
        {
            using var input = new MemoryStream(bytes); using var output = new MemoryStream();
            WaveConverter.Export(input, output, format, default, preview);
            check(input.CanRead && output.CanWrite && input.ToArray().SequenceEqual(bytes), "Conversion leaves streams owned and original bytes intact");
            return output.ToArray();
        }
        foreach (int sourceBits in new[] { 8,16,24,32 }) foreach (int sourceChannels in new[] { 1,2 })
        {
            double divisor = Math.Pow(2, sourceBits-1);
            int[] samples = Enumerable.Range(0, 9 * sourceChannels).Select(i => i%4 == 0 ? (int)-divisor : i%4 == 1 ? (int)(divisor-1) : i%4 == 2 ? 0 : (int)(divisor/3)).ToArray();
            byte[] source = WaveChecks.Fixture(sourceBits, sourceChannels, samples);
            foreach (int bits in new[] { 8,16,24,32 }) foreach (int channels in new[] { 1,2 })
            {
                var format = new WaveFormat(8000, channels, bits); byte[] bytes = Render(source, format);
                using var decoded = new MemoryStream(bytes); var info = PcmWave.Read(decoded, default);
                check(info.Rate == 8000 && info.Bits == bits && info.Channels == channels && info.Frames == 9, "Conversion format matrix preserves frame alignment and header");
                var expected = Enumerable.Range(0, 9 * channels).Select(i => {
                    int frame = i/channels, ch = i%channels;
                    double value = sourceChannels == 1 ? samples[frame] : channels == 1 ? (samples[frame*2]+(double)samples[frame*2+1])/2 : samples[frame*2+ch];
                    double scale = Math.Pow(2,bits-1);
                    return (int)Math.Clamp(Math.Round(value/divisor*scale,MidpointRounding.AwayFromZero),-scale,scale-1);
                }).ToArray();
                check(WaveChecks.Decode(bytes).SequenceEqual(expected), "Independent bit depth/channel mapping including signed extrema, stereo average and PCM8 bias");
                if (bits == sourceBits && channels == sourceChannels) check(WaveChecks.Decode(bytes).SequenceEqual(samples), "Neutral format is sample-exact for all integer precisions");
                byte[] preview = Render(source, format, true);
                using var saved = new MemoryStream(bytes); using var reference = new MemoryStream();
                PcmWave.Export(saved, reference, new(0,info.Seconds), default, true);
                check(preview.SequenceEqual(reference.ToArray()), "Preview includes selected quantization then PCM16 delivery, including 8-bit noise");
                check(bytes.Length == info.DataOffset+info.DataBytes+(info.DataBytes&1), "Converted RIFF padding and data length agree");
            }
        }
        foreach (var rates in new[] { (8000,192000), (192000,8000), (44100,48000), (48000,44100), (47999,11025), (11025,47999) })
        {
            byte[] source = Fixture(24, 2, Enumerable.Range(0, 10000*2).Select(i => i%2 == 0 ? 1234567 : -2345678).ToArray(), rates.Item1);
            byte[] bytes = Render(source, new(rates.Item2,2,24)); using var stream = new MemoryStream(bytes); var info = PcmWave.Read(stream,default);
            check(info.Frames == (10000L*rates.Item2+rates.Item1/2)/rates.Item1 && info.Rate == rates.Item2, "Rational resampling length rounds once with <=half output-frame duration difference");
            check(WaveChecks.Decode(bytes).Select((x,i) => Math.Abs(x-(i%2 == 0 ? 1234567 : -2345678))).Max() <= 1, "Resampling DC gain and independent channel alignment across cache boundaries");
        }
        byte[] Tone(int rate, int hz, int length) => Fixture(16,1,Enumerable.Range(0,length).Select(i => (int)Math.Round(12000*Math.Sin(2*Math.PI*hz*i/rate))).ToArray(),rate);
        foreach (var spec in new[] { (48000,16000,1000), (192000,8000,1000), (44100,48000,997), (8000,192000,1000) })
        {
            int[] values = WaveChecks.Decode(Render(Tone(spec.Item1,spec.Item3,spec.Item1/4),new(spec.Item2,1,16)));
            int edge = Math.Max(100, (int)Math.Ceiling(80d * spec.Item2 / spec.Item1));
            check(values.Length > 2*edge, "Passband fixture contains an interior beyond filter edge extension");
            double error = Math.Sqrt(values.Skip(edge).Take(values.Length-2*edge).Select((v,i) => Math.Pow(v-12000*Math.Sin(2*Math.PI*spec.Item3*(i+edge)/spec.Item2),2)).Average());
            check(error < 2, "Passband amplitude and phase agree with independently sampled sine: "+spec);
        }
        foreach (var spec in new[] { (48000,16000,14000), (48000,16000,8080), (48000,16000,8000), (48000,8000,4040), (192000,8000,4040), (192000,8000,20000) })
        {
            int[] values = WaveChecks.Decode(Render(Tone(spec.Item1,spec.Item3,spec.Item1/4),new(spec.Item2,1,16)));
            double rms = Math.Sqrt(values.Skip(200).Take(values.Length-400).Select(x=>(double)x*x).Average());
            check(rms < 2, "Downsampling suppresses above-Nyquist energy instead of aliasing: "+spec);
        }
        using(var input = new MemoryStream(Fixture(16,1,Enumerable.Range(0,8000).Select(i=>i%100<50 ? 32767 : -32768).ToArray(),8000))) using(var output = new MemoryStream())
        {
            long clipped = WaveConverter.Export(input,output,new(48000,1,16),default);
            int[] values = WaveChecks.Decode(output.ToArray());
            check(clipped > 0 && values.Min() == -32768 && values.Max() == 32767, "Filter overshoot clips and counts instead of wrapping signed samples");
        }
        // A rational-phase passband fixture also crosses both source page boundaries.
        byte[] rational = Render(Tone(44100,997,18000),new(48000,2,24));
        int[] converted = WaveChecks.Decode(rational);
        check(converted.Where((_,i)=>i%2==0).SequenceEqual(converted.Where((_,i)=>i%2==1)), "Mono duplication is exact after rational-phase interpolation");
        using(var readCounter = new CountingInput(Tone(48000,1000,20000))) using(var output = new MemoryStream())
        {
            WaveConverter.Export(readCounter,output,new(16000,1,16),default);
            check(readCounter.Reads < 20, "Source cache prevents per-sample IO at filter/page boundaries");
        }
        byte[] longSource = Tone(11025,997,12*11025); var target = new WaveFormat(16000,2,8);
        byte[] full = Render(longSource,target), shortBytes = Render(longSource,target,true);
        using(var fullStream = new MemoryStream(full)) using(var expected = new MemoryStream())
        {
            var info = PcmWave.Read(fullStream,default); PcmWave.Export(fullStream,expected,new(0,info.Seconds),default,true);
            check(shortBytes.SequenceEqual(expected.ToArray()), "Ten-second conversion preview is saved-output prefix, with same full-source filter boundaries");
        }
        foreach(var format in new[] { new WaveFormat(7999,1,16),new(192001,1,16),new(8000,0,16),new(8000,3,16),new(8000,1,20) })
            reject(()=>format.Frames(new(8000,1,16,44,16)), "Reject invalid conversion choices");
        reject(()=>new WaveFormat(8000,1,16).Frames(new(192000,1,16,44,2)), "Empty rounded output explicitly refused");
        check(new WaveFormat(8000,1,16).Frames(new(16000,1,16,44,2)) == 1, "Half output frame rounds up");
        reject(()=>new WaveFormat(192000,2,32).Frames(new(8000,1,8,44,3000000)), "Output expansion budget checked before allocation/writing");
        long boundary = (PcmWave.MaxBytes-68)/192;
        check(new WaveFormat(192000,2,32).Frames(new(8000,1,8,44,boundary)) == boundary*24, "Largest conversion inside byte budget accepted");
        reject(()=>new WaveFormat(192000,2,32).Frames(new(8000,1,8,44,boundary+1)), "One source frame beyond byte budget refused");
        using(var same = new MemoryStream(Tone(8000,1000,800))) reject(()=>WaveConverter.Export(same,same,new(8000,1,16),default), "Same input/output stream refused");
        using(var cancelled = new MemoryStream(longSource)) using(var output = new MemoryStream())
        {
            reject(()=>WaveConverter.Export(cancelled,output,target,new CancellationToken(true)), "Pre-cancelled conversion"); check(output.Length == 0, "Pre-cancellation writes no header");
        }
        using(var cancellation = new CancellationTokenSource()) using(var input = new MemoryStream(longSource)) using(var output = new CancelOutput(cancellation))
        {
            reject(()=>WaveConverter.Export(input,output,target,cancellation.Token), "Cancellation during streaming conversion observed");
            check(output.Length < 12*16000*2, "Cancellation prevents remaining output blocks");
        }
        using(var input = new MemoryStream(Tone(192000,1000,800))) using(var output = new MemoryStream())
        {
            // Simulate a stream failing after a cache read; no publishing happens in Core.
            using var broken = new BrokenInput(input.ToArray()); reject(()=>WaveConverter.Export(broken,output,new(8000,1,16),default), "Input read failure propagates instead of padding damaged audio");
        }
        check(Catalog.Available(new Prefs(),new[]{"talk.WAV"}).Any(x=>x.Id=="audio.convert") && !Catalog.Available(new Prefs(),new[]{"a.mp3"}).Any(x=>x.Id=="audio.convert") && !Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.convert"), "Conversion action requires one explicit WAV");
        string? folder = Environment.GetEnvironmentVariable("SWIVEL_FORMAT_FIXTURES");
        if(folder is not null)
        {
            Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder,"rational-stereo24.wav"),rational);
            File.WriteAllBytes(Path.Combine(folder,"downsample-low.wav"),Render(Tone(48000,1000,48000),new(16000,1,16)));
            File.WriteAllBytes(Path.Combine(folder,"downsample-high.wav"),Render(Tone(48000,14000,48000),new(16000,1,16)));
            File.WriteAllBytes(Path.Combine(folder,"downsample-near.wav"),Render(Tone(192000,4040,192000),new(8000,1,16)));
            File.WriteAllBytes(Path.Combine(folder,"preview8.wav"),shortBytes); File.WriteAllBytes(Path.Combine(folder,"full8.wav"),full);
        }
    }
    private static byte[] Fixture(int bits,int channels,int[] values,int rate)
    {
        byte[] bytes = WaveChecks.Fixture(bits,channels,values);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),rate); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),rate*channels*(bits/8)); return bytes;
    }
    private sealed class CountingInput(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public override int Read(Span<byte> buffer) { Reads++; return base.Read(buffer); }
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) { base.Write(buffer); if(Length>100) cancellation.Cancel(); }
    }
    private sealed class BrokenInput(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) { if(Position>=44) throw new IOException("Injected source read failure"); return base.Read(buffer); }
    }
}
