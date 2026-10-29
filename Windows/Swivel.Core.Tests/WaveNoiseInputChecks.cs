using System;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveNoiseInputChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] Clean(Stream source, bool preview = false)
        { using var output = new MemoryStream(); WaveDenoiser.Export(source, output, new(0,.05,.75), default, preview); return output.ToArray(); }
        void Compare(byte[] bytes, Action<Stream,Stream> convert, int bits, int channels)
        {
            using var source = new MemoryStream(bytes);
            using var expected = new MemoryStream(); convert(source, expected);
            using var prepared = WaveNoiseInput.Prepare(source, default);
            check(prepared is not null && prepared.ToArray().SequenceEqual(expected.ToArray()), "Noise preparation equals existing complete converter bytes");
            var info = PcmWave.Read(prepared!, default);
            check(info.Bits == bits && info.Rate == 8000 && info.Channels == channels && info.Frames == 801, "Noise conversion preserves timebase/layout and expected integer depth");
            foreach (bool preview in new[]{false,true})
                check(Clean(prepared!, preview).SequenceEqual(Clean(expected, preview)), "Prepared noise full/preview reuse unchanged denoiser");
            check(source.CanRead && source.ToArray().SequenceEqual(bytes), "Noise preparation preserves caller source and ownership");
            reject(()=>WaveNoiseInput.Prepare(source,new CancellationToken(true))?.Dispose(), "Noise preparation observes pre-cancellation");
        }
        foreach (int bits in new[]{32,64}) foreach (int channels in new[]{1,2}) foreach (bool extended in new[]{false,true})
        {
            double[] values=Enumerable.Range(0,801*channels).Select(i=>i%31==0?-1:i%37==0?1:Math.Sin(i/13d)*.8).ToArray();
            Compare(FloatWaveChecks.Fixture(values,bits,channels,extended:extended), (s,o)=>FloatWave.Export(s,o,new(),default),24,channels);
        }
        foreach (int bits in new[]{8,16,24,32}) foreach (int channels in new[]{1,2}) foreach (string? codec in new string?[]{null,"NONE","sowt"})
            Compare(AiffChecks.Fixture(bits,channels,8000,Enumerable.Range(0,801*channels).Select(i=>i%101-50).ToArray(),codec), (s,o)=>AiffWave.Export(s,o,default),bits,channels);
        foreach (bool mu in new[]{false,true}) foreach (int channels in new[]{1,2})
            Compare(G711Checks.Fixture(Enumerable.Range(0,801*channels).Select(i=>(byte)i).ToArray(),mu,channels,order:"dcf"), (s,o)=>G711Wave.Export(s,o,default),16,channels);
        foreach (int bits in new[]{8,16,24,32}) foreach (int channels in new[]{1,2})
        {
            byte[] bytes=WaveChecks.Fixture(bits,channels,Enumerable.Range(0,801*channels).Select(i=>i%63-31).ToArray());
            using var input=new MemoryStream(bytes);
            byte[] expected=Clean(input);
            using var prepared=WaveNoiseInput.Prepare(input,default);
            check(prepared is null && Clean(input).SequenceEqual(expected),"Existing integer WAV uses original stream and unchanged denoised bytes");
        }
        void Refuse(byte[] bytes, string message)
        {
            using var input=new MemoryStream(bytes); bool refused=false;
            try { using var prepared=WaveNoiseInput.Prepare(input,default); }
            catch(UserError error) { refused=error.Message==message; }
            check(refused && input.CanRead,"Unsafe float noise input refused with precise message and caller ownership retained");
        }
        foreach(int bits in new[]{32,64}) foreach(double invalid in new[]{-1.001,1.001,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            var values=new double[80001];values[^1]=invalid;
            Refuse(FloatWaveChecks.Fixture(values,bits),double.IsFinite(invalid)?WaveNoiseInput.Clipping:FloatWave.NonFinite);
        }
        foreach(string ext in new[]{"wav","aif","aiff","aifc","mp3","aac","flac","m4a"})
            check(Catalog.All(new Prefs()).Single(a=>a.Id=="audio.noise").Extensions!.Contains(ext),"Noise action retains and extends input selection");
        foreach(string key in new[]{WaveNoiseInput.Hint,WaveNoiseInput.Clipping}) foreach(string lang in new[]{"zh-Hans","zh-Hant","ja"})
            check(Strings.L(key,lang)!=key,"Noise preparation notices translated");
    }
}
