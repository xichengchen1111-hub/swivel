using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record WaveFormat(int Rate, int Channels, int Bits)
{
    public long Frames(WaveInfo source)
    {
        if (Rate is < 8000 or > 192000 || Channels is not 1 and not 2 || Bits is not 8 and not 16 and not 24 and not 32)
            throw new UserError("Choose 8–192 kHz, mono or stereo, and 8/16/24/32-bit integer PCM.");
        long frames = checked((source.Frames * Rate + source.Rate / 2) / source.Rate);
        if (frames < 1) throw new UserError("The converted audio must contain at least one sample frame. Choose a higher sample rate.");
        long bytes = checked(frames * Channels * (Bits / 8));
        if (bytes + (bytes & 1) + (Bits > 16 ? 68 : 44) > PcmWave.MaxBytes)
            throw new UserError("The converted WAV would exceed 512 MB. Choose a lower rate, bit depth or channel count.");
        return frames;
    }
}

// Original finite windowed-sinc resampler: 64 zero crossings per side when downsampling (32 up), Blackman
// window, 94% cutoff, 1024 interpolated fractional phases. Memory is bounded by
// the rate ratio (<=24), never by the duration. Edge samples are extended.
public static class WaveConverter
{
    private const int Phases = 1024;
    public static long Export(Stream input, Stream output, WaveFormat format, CancellationToken token, bool preview = false)
    {
        if (ReferenceEquals(input, output)) throw new ArgumentException("Input and output must be different streams.");
        var source = PcmWave.Read(input, token); long fullCount = format.Frames(source);
        return ExportCore(input, output, source, format, fullCount, source.Rate, format.Rate, token, preview);
    }
    public static long ExportSpeed(Stream input, Stream output, WaveSpeed speed, CancellationToken token, bool preview = false)
    {
        if (ReferenceEquals(input, output)) throw new ArgumentException("Input and output must be different streams.");
        var source = PcmWave.Read(input, token); long fullCount = speed.Frames(source);
        if (speed.KeepPitch && speed.Percent != 100) return WaveStretch.Export(input, output, source, speed.Percent, fullCount, token, preview);
        return ExportCore(input, output, source, new(source.Rate, source.Channels, source.Bits), fullCount, speed.Percent, 100, token, preview);
    }
    private static long ExportCore(Stream input, Stream output, WaveInfo source, WaveFormat format, long fullCount,
        int stepNumerator, int stepDenominator, CancellationToken token, bool preview)
    {
        int bits = preview ? 16 : format.Bits, block = format.Channels * (bits / 8);
        long count = preview ? Math.Min(fullCount, (long)PcmWave.PreviewSeconds * format.Rate) : fullCount;
        if (stepNumerator == stepDenominator && source.Channels == format.Channels && source.Bits == bits && source.Bits == format.Bits)
            return PcmWave.Export(input, output, new(0, source.Seconds), token, preview);
        double[][]? kernels = null; int radius = 0;
        if (stepNumerator != stepDenominator)
        {
            double cutoff = .94 * Math.Min(1, (double)stepDenominator / stepNumerator);
            radius = (int)Math.Ceiling((stepDenominator < stepNumerator ? 64 : 32) / cutoff); int taps = radius * 2 + 1;
            kernels = new double[Phases + 1][];
            for (int phase = 0; phase <= Phases; phase++)
            {
                token.ThrowIfCancellationRequested(); var row = kernels[phase] = new double[taps]; double sum = 0;
                for (int tap = 0; tap < taps; tap++)
                {
                    double x = tap - radius - (double)phase / Phases;
                    double window = Math.Abs(x) >= radius ? 0 : .42 + .5 * Math.Cos(Math.PI * x / radius) + .08 * Math.Cos(2 * Math.PI * x / radius);
                    double t = Math.PI * cutoff * x;
                    row[tap] = cutoff * (Math.Abs(t) < 1e-15 ? 1 : Math.Sin(t) / t) * window; sum += row[tap];
                }
                for (int tap = 0; tap < taps; tap++) row[tap] /= sum;
            }
        }
        token.ThrowIfCancellationRequested(); PcmWave.Header(output, format.Rate, format.Channels, bits, count);
        var samples = new SourceSamples(input, source, token); var encoded = new byte[1024 * block];
        double divisor = Math.Pow(2, bits - 1), targetDivisor = Math.Pow(2, format.Bits - 1); long clipped = 0;
        for (long at = 0; at < count;)
        {
            token.ThrowIfCancellationRequested(); int frames = (int)Math.Min(1024, count - at);
            for (int i = 0; i < frames; i++)
            {
                long numerator = (at + i) * stepNumerator, center = numerator / stepDenominator;
                double phase = (double)(numerator % stepDenominator) * Phases / stepDenominator;
                int p = (int)phase; double fraction = phase - p;
                double left = 0, right = 0;
                if (kernels is null) { left = samples.Get(center, 0); right = source.Channels == 2 ? samples.Get(center, 1) : left; }
                else
                {
                    var a = kernels[p]; var b = kernels[p + 1];
                    for (int tap = 0; tap < a.Length; tap++)
                    {
                        double weight = a[tap] + (b[tap] - a[tap]) * fraction;
                        long frame = Math.Clamp(center + tap - radius, 0, source.Frames - 1);
                        left += samples.Get(frame, 0) * weight;
                        if (source.Channels == 2) right += samples.Get(frame, 1) * weight;
                    }
                    if (source.Channels == 1) right = left;
                }
                for (int ch = 0; ch < format.Channels; ch++)
                {
                    double value = format.Channels == 1 ? (left + right) * .5 : ch == 0 ? left : right;
                    double target = Math.Round(value * targetDivisor, MidpointRounding.AwayFromZero);
                    if (target < -targetDivisor || target > targetDivisor - 1) clipped++;
                    target = Math.Clamp(target, -targetDivisor, targetDivisor - 1);
                    double rounded = preview ? Math.Round(target / targetDivisor * divisor, MidpointRounding.AwayFromZero) : target;
                    PcmWave.WriteSample(encoded.AsSpan(i * block + ch * (bits / 8), bits / 8), bits, (long)Math.Clamp(rounded, -divisor, divisor - 1));
                }
            }
            output.Write(encoded.AsSpan(0, frames * block)); at += frames;
        }
        if ((count * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested(); return clipped;
    }

    private sealed class SourceSamples(Stream input, WaveInfo info, CancellationToken token)
    {
        // Two adjacent 4096-frame pages hold every possible <=3271-frame filter
        // window, so crossing a page boundary does not seek/read per sample.
        private readonly byte[][] pages = { new byte[4096 * info.Block], new byte[4096 * info.Block] };
        private readonly long[] positions = { -1, -1 };
        private readonly double divisor = Math.Pow(2, info.Bits - 1);
        public double Get(long frame, int channel)
        {
            long page = frame / 4096; int slot = (int)(page & 1);
            if (positions[slot] != page)
            {
                token.ThrowIfCancellationRequested(); input.Position = info.DataOffset + page * 4096 * info.Block;
                int frames = (int)Math.Min(4096, info.Frames - page * 4096);
                input.ReadExactly(pages[slot].AsSpan(0, frames * info.Block)); positions[slot] = page;
            }
            return PcmWave.Sample(pages[slot].AsSpan((int)(frame % 4096) * info.Block + channel * (info.Bits / 8), info.Bits / 8), info.Bits) / divisor;
        }
    }
}
