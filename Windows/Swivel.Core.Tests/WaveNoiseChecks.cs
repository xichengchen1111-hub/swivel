using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveNoiseChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] Render(byte[] source, WaveNoise settings, bool preview = false)
        {
            using var input = new MemoryStream(source); using var output = new MemoryStream();
            WaveDenoiser.Export(input,output,settings,default,preview);
            check(input.ToArray().SequenceEqual(source) && input.CanRead && output.CanWrite, "Denoising leaves original bytes and stream ownership intact");
            return output.ToArray();
        }
        foreach (int bits in new[]{8,16,24,32}) foreach(int channels in new[]{1,2})
        {
            double scale = Math.Pow(2,bits-1);
            int[] samples = Enumerable.Range(0,1237*channels).Select(i => i<400*channels ? 0 : i%4==0 ? (int)-scale : i%4==1 ? (int)(scale-1) : i%4==2 ? 0 : (int)(scale/3)).ToArray();
            byte[] source = Fixture(bits,channels,samples,8000); var settings = new WaveNoise(0,.05);
            byte[] neutral = Render(source,settings with {Strength=0});
            check(WaveChecks.Decode(neutral).SequenceEqual(samples), "Zero strength is sample-exact for every bit depth and channel layout");
            byte[] unity = Render(source,settings); using var decoded = new MemoryStream(unity); var info = PcmWave.Read(decoded,default);
            check(info.Frames==1237 && info.Bits==bits && info.Rate==8000 && info.Channels==channels, "Denoising preserves exact duration and source format, including partial final hop");
            check(WaveChecks.Decode(unity).SequenceEqual(samples), "Silent profile gives FFT/overlap-add unity including signed extrema and stream edges");
            byte[] preview=Render(source,settings,true); using var saved=new MemoryStream(unity);using var expected=new MemoryStream();
            PcmWave.Export(saved,expected,new(0,info.Seconds),default,true);
            check(preview.SequenceEqual(expected.ToArray()), "Processed preview applies original quantization then PCM16, including PCM8 bias");
            check(unity.Length==info.DataOffset+info.DataBytes+(info.DataBytes&1),"Noise output RIFF data and odd-byte padding agree");
            int[] silence=WaveChecks.Decode(Render(Fixture(bits,channels,new int[1237*channels],8000),settings with {Strength=1}));
            check(silence.All(x=>x==0),"Digital silence has finite gain and remains silent at maximum strength");
        }
        // A complete selected sample is shorter than the FFT window at 8kHz.
        foreach(int frames in new[]{400,511,512,513,1024,1025})
        {
            byte[] source=Fixture(16,1,Enumerable.Repeat(6000,frames).ToArray(),8000);
            int[] values=WaveChecks.Decode(Render(source,new(0,.05,1)));
            check(values.Length==frames && values.All(x=>x>=0 && x<=6000),"Short profile/partial window cannot amplify DC or lose frames: "+frames);
        }
        var pair = Signal(8000,3,2); var options=new WaveNoise(0,.25,1);
        byte[] full=Render(pair.Bytes,options); int[] clean=WaveChecks.Decode(full);
        for(int ch=0;ch<2;ch++)
        {
            int channel=ch;
            double before=Rms(pair.Values,2,channel,20800,23200),after=Rms(clean,2,channel,20800,23200);
            double attenuation=20*Math.Log10(after/before);
            check(attenuation < -8,"Stationary broadband noise reduced by >8dB per independent channel");
            double originalAmplitude=Amplitude(pair.Values,2,channel,8000,channel==0?1000:1375,8000,16000);
            double cleanAmplitude=Amplitude(clean,2,channel,8000,channel==0?1000:1375,8000,16000);
            double change=20*Math.Log10(cleanAmplitude/originalAmplitude);
            check(Math.Abs(change)<.5,"Signal sinusoid amplitude preserved within 0.5dB over stationary noise");
            Console.WriteLine($"WAV denoise channel {ch}: noise {attenuation:0.00}dB, tone {change:0.000}dB (synthetic fixture only)");
        }
        // Select the profile after the preview horizon: profile source remains
        // full-length and processing history starts from the same first frame.
        var longPair=Signal(11025,12,1); var late=new WaveNoise(11.5,11.75,.8);
        byte[] longFull=Render(longPair.Bytes,late),longPreview=Render(longPair.Bytes,late,true);
        using(var saved=new MemoryStream(longFull)) using(var expected=new MemoryStream())
        {
            var info=PcmWave.Read(saved,default);PcmWave.Export(saved,expected,new(0,info.Seconds),default,true);
            check(longPreview.SequenceEqual(expected.ToArray()),"10-second preview equals saved prefix even with noise sample after 10 seconds");
        }
        foreach(int rate in new[]{8000,44100,48000,192000})
        {
            int frames=(int)Math.Ceiling(rate*.05); byte[] source=Fixture(24,2,new int[frames*2],rate);
            using var result=new MemoryStream(Render(source,new(0,(double)frames/rate)));
            var info=PcmWave.Read(result,default);check(info.Frames==frames && info.Rate==rate && WaveDenoiser.WindowSize(rate) is >=512 and <=8192,"Rate-dependent bounded FFT at input extrema and short samples");
        }
        var sourceInfo=new WaveInfo(8000,1,16,44,48000);
        foreach(var settings in new[]{new WaveNoise(-.01,.1),new(0,0),new(0,.049),new(0,2.001),new(0,4),new(double.NaN,.1),new(0,double.PositiveInfinity),new(0,.1,double.NaN),new(0,.1,-.01),new(0,.1,1.001)})
            reject(()=>settings.Range(sourceInfo),"Invalid noise sample/strength refused");
        check(new WaveNoise(1,3).Range(sourceInfo)==new NoiseSample(8000,16000),"Exact two-second profile bound accepted");
        check(new WaveNoise(0,.05).Range(new(11025,1,16,44,22050))==new NoiseSample(0,551),"0.05 seconds at nonintegral frame count follows nearest-frame rounding");
        reject(()=>new WaveNoise(0,.049).Range(sourceInfo with {DataBytes=784}),"Too-short recording gets explicit sample bound error");
        reject(()=>Render(pair.Bytes,new(0,.25,double.PositiveInfinity)),"Non-finite strength rejected by export");
        using(var input=new MemoryStream(pair.Bytes)) using(var output=new MemoryStream())
        {
            reject(()=>WaveDenoiser.Export(input,output,options,new CancellationToken(true)),"Pre-cancelled denoise");check(output.Length==0,"Pre-cancellation writes no header");
        }
        using(var cancellation=new CancellationTokenSource()) using(var input=new CancelInput(pair.Bytes,cancellation)) using(var output=new MemoryStream())
        {
            reject(()=>WaveDenoiser.Export(input,output,options,cancellation.Token),"Cancellation during noise sample read observed");check(output.Length==0,"Profile cancellation cannot produce an output header");
        }
        using(var input=new MemoryStream(pair.Bytes)) using(var cancellation=new CancellationTokenSource()) using(var output=new CancelOutput(cancellation))
        {
            reject(()=>WaveDenoiser.Export(input,output,options,cancellation.Token),"Cancellation during overlap/add output observed");check(output.Length<full.Length,"Cancellation stops remaining noise output");
        }
        using(var input=new BrokenInput(pair.Bytes)) using(var output=new MemoryStream()) reject(()=>WaveDenoiser.Export(input,output,options,default),"Profile IO failure is propagated");
        using(var same=new MemoryStream(pair.Bytes)) reject(()=>WaveDenoiser.Export(same,same,options,default),"Denoiser rejects shared input/output stream");
        check(new[]{"speech.WAV","talk.mp3","talk.aac","take.FLAC","memo.M4A"}.All(f=>Catalog.Available(new Prefs(),new[]{f}).Any(x=>x.Id=="audio.noise")) && !Catalog.Available(new Prefs(),new[]{"speech.mp4"}).Any(x=>x.Id=="audio.noise") && !Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.noise"),"Noise action takes one WAV, MP3, AAC, FLAC or M4A file (the others decoded by Windows first)");
        string? folder=Environment.GetEnvironmentVariable("SWIVEL_NOISE_FIXTURES");
        if(folder is not null)
        {
            Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"noise-original.wav"),pair.Bytes);File.WriteAllBytes(Path.Combine(folder,"noise-clean.wav"),full);
            File.WriteAllBytes(Path.Combine(folder,"late-original.wav"),longPair.Bytes);File.WriteAllBytes(Path.Combine(folder,"late-full.wav"),longFull);File.WriteAllBytes(Path.Combine(folder,"late-preview.wav"),longPreview);
        }
    }
    private static (byte[] Bytes,int[] Values) Signal(int rate,int seconds,int channels)
    {
        uint seed=47291;int[] values=new int[rate*seconds*channels];
        for(int i=0;i<rate*seconds;i++)for(int ch=0;ch<channels;ch++)
        {
            seed=unchecked(seed*1664525+1013904223);double noise=((seed>>8)/(double)(1<<24)*2-1)*(ch==0?1800:2500);
            double tone=i>=rate/2 && i<(seconds-1)*rate ? (ch==0?9000:12000)*Math.Sin(2*Math.PI*(ch==0?1000:1375)*i/rate):0;
            values[i*channels+ch]=(int)Math.Round(noise+tone);
        }
        return(Fixture(16,channels,values,rate),values);
    }
    private static double Rms(int[] values,int channels,int channel,int first,int end) => Math.Sqrt(Enumerable.Range(first,end-first).Select(i=>(double)values[i*channels+channel]*values[i*channels+channel]).Average());
    private static double Amplitude(int[] values,int channels,int channel,int rate,int hz,int first,int end)
    {
        double sine=0,cosine=0;for(int i=first;i<end;i++){sine+=values[i*channels+channel]*Math.Sin(2*Math.PI*hz*i/rate);cosine+=values[i*channels+channel]*Math.Cos(2*Math.PI*hz*i/rate);}return 2*Math.Sqrt(sine*sine+cosine*cosine)/(end-first);
    }
    private static byte[] Fixture(int bits,int channels,int[] values,int rate)
    {
        byte[] bytes=WaveChecks.Fixture(bits,channels,values);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),rate*channels*(bits/8));return bytes;
    }
    private sealed class CancelInput(byte[] bytes,CancellationTokenSource cancellation):MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer){int n=base.Read(buffer);if(Position>44)cancellation.Cancel();return n;}
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation):MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer){base.Write(buffer);if(Length>100)cancellation.Cancel();}
    }
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer){if(Position>=44)throw new IOException("Injected sample read failure");return base.Read(buffer);}
    }
}
