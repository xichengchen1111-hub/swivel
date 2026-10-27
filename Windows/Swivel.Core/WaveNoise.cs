using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public readonly record struct NoiseSample(long First, long Count);
public sealed record WaveNoise(double Start, double End, double Strength = .75)
{
    public NoiseSample Range(WaveInfo info)
    {
        if (!double.IsFinite(Start) || !double.IsFinite(End) || !double.IsFinite(Strength) || Start < 0 || End > info.Seconds || End <= Start || Strength is < 0 or > 1)
            throw new UserError("Choose a noise-only sample of 0.05–2 seconds within the recording and strength from 0–100%.");
        long first = (long)Math.Round(Start * info.Rate, MidpointRounding.AwayFromZero);
        long count = (long)Math.Round(End * info.Rate, MidpointRounding.AwayFromZero) - first;
        // Match the nearest-frame rounding used for both entered boundaries:
        // e.g. 0.05 s at 11025 Hz is 551 frames, not an invalid 552-frame gap.
        if (count < (long)Math.Round(info.Rate * .05, MidpointRounding.AwayFromZero) || count > 2L * info.Rate)
            throw new UserError("Choose a noise-only sample of 0.05–2 seconds within the recording and strength from 0–100%.");
        return new(first, count);
    }
}

// Original bounded STFT spectral subtraction, using an explicitly chosen noise
// segment. No speech classifier, model, device, paths or external FFT library.
public static class WaveDenoiser
{
    public static int WindowSize(int rate)
    {
        int size = 512; while (size < rate * .032 && size < 8192) size *= 2; return size;
    }
    public static long Export(Stream input, Stream output, WaveNoise settings, CancellationToken token, bool preview = false)
    {
        if (ReferenceEquals(input, output)) throw new ArgumentException("Input and output must be different streams.");
        var info = PcmWave.Read(input, token); var sample = settings.Range(info);
        new WaveFormat(info.Rate, info.Channels, info.Bits).Frames(info);
        if (settings.Strength == 0) return PcmWave.Export(input, output, new(0, info.Seconds), token, preview);
        int size = WindowSize(info.Rate), hop = size / 4, bins = size / 2 + 1;
        var fft = new Fourier(size); var real = new double[size]; var imaginary = new double[size];
        var window = new double[size]; double windowPower = 0;
        for (int i = 0; i < size; i++) { window[i] = Math.Sqrt(.5 - .5 * Math.Cos(2 * Math.PI * (i + .5) / size)); windowPower += window[i] * window[i]; }
        var reader = new WindowReader(input, info, size, token);
        double[][] Matrix(int columns, double initial = 0)
        {
            var result = new double[info.Channels][];
            for (int ch = 0; ch < info.Channels; ch++) { result[ch] = new double[columns]; if (initial != 0) Array.Fill(result[ch], initial); }
            return result;
        }
        var noise = Matrix(bins); int profiles = 0;
        // Use whole analysis windows, ignoring a final partial hop. A sample
        // shorter than a window is zero-padded and corrected for window energy.
        long last = sample.First + Math.Max(0, sample.Count - size);
        for (long start = sample.First; start <= last; start += hop)
        {
            token.ThrowIfCancellationRequested(); reader.Read(start, sample.First, sample.First + sample.Count);
            double usedPower = 0; for (int i = reader.Prefix; i < reader.Prefix + reader.Count; i++) usedPower += window[i] * window[i];
            double correction = windowPower / usedPower;
            for (int ch = 0; ch < info.Channels; ch++)
            {
                reader.Window(ch, window, real); Array.Clear(imaginary); fft.Transform(real, imaginary, inverse: false);
                for (int k = 0; k < bins; k++) noise[ch][k] += (real[k] * real[k] + imaginary[k] * imaginary[k]) * correction;
            }
            profiles++;
        }
        for (int ch = 0; ch < info.Channels; ch++) for (int k = 0; k < bins; k++) noise[ch][k] /= profiles;
        token.ThrowIfCancellationRequested();
        long length = preview ? Math.Min(info.Frames, (long)PcmWave.PreviewSeconds * info.Rate) : info.Frames;
        int bits = preview ? 16 : info.Bits, block = info.Channels * (bits / 8);
        PcmWave.Header(output, info.Rate, info.Channels, bits, length);
        var accumulation = Matrix(size); var previous = Matrix(bins, 1); var weights = new double[size]; var encoded = new byte[hop * block];
        double floor = Math.Pow(.01, settings.Strength), alpha = 1 + 3 * settings.Strength;
        double sourceDivisor = Math.Pow(2, info.Bits - 1), outputDivisor = Math.Pow(2, bits - 1); long clipped = 0;
        // Preview uses the full source for lookahead and the same gain history.
        // Its ten-second end never becomes a new signal/window boundary.
        for (long start = -size + hop; start < length; start += hop)
        {
            token.ThrowIfCancellationRequested(); reader.Read(start, 0, info.Frames);
            for (int ch = 0; ch < info.Channels; ch++)
            {
                reader.Window(ch, window, real); Array.Clear(imaginary); fft.Transform(real, imaginary, inverse: false);
                for (int k = 0; k < bins; k++)
                {
                    double power = real[k] * real[k] + imaginary[k] * imaginary[k];
                    double gain = Math.Sqrt(Math.Max(floor * floor, 1 - alpha * noise[ch][k] / Math.Max(1e-30, power)));
                    double speed = gain > previous[ch][k] ? .8 : .3;
                    gain = speed * gain + (1 - speed) * previous[ch][k]; previous[ch][k] = gain;
                    real[k] *= gain; imaginary[k] *= gain;
                    if (k > 0 && k < size / 2) { real[size - k] *= gain; imaginary[size - k] *= gain; }
                }
                fft.Transform(real, imaginary, inverse: true);
                for (int i = 0; i < size; i++) accumulation[ch][i] += real[i] * window[i];
            }
            for (int i = 0; i < size; i++) weights[i] += window[i] * window[i];
            if (start >= 0)
            {
                int count = (int)Math.Min(hop, length - start);
                for (int i = 0; i < count; i++) for (int ch = 0; ch < info.Channels; ch++)
                {
                    double value = accumulation[ch][i] / Math.Max(weights[i], 1e-20);
                    double quantized = Math.Round(value * sourceDivisor, MidpointRounding.AwayFromZero);
                    if (quantized < -sourceDivisor || quantized > sourceDivisor - 1) clipped++;
                    quantized = Math.Clamp(quantized, -sourceDivisor, sourceDivisor - 1);
                    double final = preview ? Math.Round(quantized / sourceDivisor * outputDivisor, MidpointRounding.AwayFromZero) : quantized;
                    PcmWave.WriteSample(encoded.AsSpan(i * block + ch * (bits / 8), bits / 8), bits, (long)Math.Clamp(final, -outputDivisor, outputDivisor - 1));
                }
                output.Write(encoded.AsSpan(0, count * block));
            }
            for (int ch = 0; ch < info.Channels; ch++) { Array.Copy(accumulation[ch], hop, accumulation[ch], 0, size - hop); Array.Clear(accumulation[ch], size - hop, hop); }
            Array.Copy(weights, hop, weights, 0, size - hop); Array.Clear(weights, size - hop, hop);
        }
        if ((length * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested(); return clipped;
    }

    private sealed class WindowReader(Stream input, WaveInfo info, int size, CancellationToken token)
    {
        private readonly byte[] buffer = new byte[size * info.Block];
        public int Prefix { get; private set; }
        public int Count { get; private set; }
        public void Read(long start, long lower, long upper)
        {
            token.ThrowIfCancellationRequested(); long first = Math.Max(start, lower);
            Prefix = (int)(first - start); Count = (int)Math.Max(0, Math.Min(start + size, upper) - first);
            input.Position = info.DataOffset + first * info.Block;
            input.ReadExactly(buffer.AsSpan(0, Count * info.Block)); token.ThrowIfCancellationRequested();
        }
        public void Window(int channel, double[] window, double[] real)
        {
            Array.Clear(real); int bytes = info.Bits / 8; double divisor = Math.Pow(2, info.Bits - 1);
            for (int i = 0; i < Count; i++) real[Prefix + i] = PcmWave.Sample(buffer.AsSpan(i * info.Block + channel * bytes, bytes), info.Bits) / divisor * window[Prefix + i];
        }
    }
    // Iterative radix-2 complex FFT with one bounded twiddle table. Inverse is
    // normalized here, so overlap/add only needs its accumulated window weights.
    private sealed class Fourier
    {
        private readonly int size;
        private readonly double[] cosine, sine;
        public Fourier(int size)
        {
            this.size = size; cosine = new double[size / 2]; sine = new double[size / 2];
            for (int k = 0; k < size / 2; k++) { cosine[k] = Math.Cos(2 * Math.PI * k / size); sine[k] = Math.Sin(2 * Math.PI * k / size); }
        }
        public void Transform(double[] real, double[] imaginary, bool inverse)
        {
            for (int i = 1, j = 0; i < size; i++)
            {
                int bit = size >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i < j) { (real[i], real[j]) = (real[j], real[i]); (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]); }
            }
            for (int length = 2; length <= size; length *= 2)
            {
                int half = length / 2, stride = size / length;
                for (int start = 0; start < size; start += length) for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half, phase = k * stride;
                    double re = cosine[phase], im = inverse ? sine[phase] : -sine[phase];
                    double x = real[b] * re - imaginary[b] * im, y = real[b] * im + imaginary[b] * re;
                    real[b] = real[a] - x; imaginary[b] = imaginary[a] - y; real[a] += x; imaginary[a] += y;
                }
            }
            if (inverse) for (int i = 0; i < size; i++) { real[i] /= size; imaginary[i] /= size; }
        }
    }
}
