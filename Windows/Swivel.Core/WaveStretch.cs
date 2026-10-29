using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// Original bounded WSOLA (waveform-similarity overlap-add) time-stretch: the output is built
// from 40 ms periodic-Hann pieces laid down every 10 ms (75% overlap). Each piece is read near
// its nominal input position (output time × speed) but may move up to ±10 ms so that it lines
// up with the natural continuation of the previous piece (best energy-normalized correlation of
// the channel sum; coarse search on every rate/8000-th sample, then a finer pass). Aligned
// pieces keep the waveform's periods, so pitch stays; sound is repeated (slower) or skipped
// (faster). Each output sample is the window-weighted average of the real source samples that
// cover it, so the start, end and very short files are not faded by silence outside the source.
// Memory is one sliding source window of about 0.12 s + 0.01 s × speed/100 plus 4096 frames of
// read-ahead, never the duration. No transient detection, phase locking, formant or voice model.
internal static class WaveStretch
{
    public static long Export(Stream input, Stream output, WaveInfo source, int percent, long fullCount, CancellationToken token, bool preview)
    {
        int rate = source.Rate, channels = source.Channels, bits = preview ? 16 : source.Bits, block = channels * (bits / 8);
        long count = preview ? Math.Min(fullCount, (long)PcmWave.PreviewSeconds * rate) : fullCount;
        int hop = Math.Max(8, rate / 100), half = hop * 2, window = hop * 4, tolerance = Math.Max(8, rate / 100);
        int coarse = Math.Max(1, rate / 8000), fine = Math.Max(1, coarse / 4);
        long advance = ((long)hop * percent + 99) / 100 + 1;
        var buffer = new Window(input, source, checked((int)(advance + 2L * tolerance + 2L * half + hop + 8 + 4096)), token);
        var shape = new double[window];
        for (int n = 0; n < window; n++) shape[n] = .5 - .5 * Math.Cos(2 * Math.PI * n / window);
        var sum = new double[window * channels]; var weight = new double[window]; var encoded = new byte[hop * block];
        double divisor = Math.Pow(2, bits - 1), targetDivisor = Math.Pow(2, source.Bits - 1); long clipped = 0;
        token.ThrowIfCancellationRequested(); PcmWave.Header(output, rate, channels, bits, count);
        long emitted = 0, previous = 0;
        for (long piece = 0; emitted < count; piece++)
        {
            token.ThrowIfCancellationRequested();
            long nominal = (piece * hop * percent + 50) / 100;
            buffer.Load(nominal - advance - tolerance - half, nominal + tolerance + half + hop);
            long position = piece == 0 ? 0 : nominal + Search(buffer, previous + hop, nominal, half, -tolerance, tolerance, coarse, fine);
            for (int n = 0; n < window; n++)
            {
                long frame = position - half + n; if (frame < 0 || frame >= source.Frames) continue;
                weight[n] += shape[n];
                for (int ch = 0; ch < channels; ch++) sum[n * channels + ch] += buffer.Get(frame, ch) * shape[n];
            }
            // Output frames piece*hop-half .. +hop are now complete; those before time 0 are dropped.
            if (piece * hop >= half)
            {
                int frames = (int)Math.Min(hop, count - emitted);
                for (int i = 0; i < frames; i++)
                    for (int ch = 0; ch < channels; ch++)
                    {
                        double value = weight[i] > 1e-9 ? sum[i * channels + ch] / weight[i] : 0;
                        double target = Math.Round(value * targetDivisor, MidpointRounding.AwayFromZero);
                        if (target < -targetDivisor || target > targetDivisor - 1) clipped++;
                        target = Math.Clamp(target, -targetDivisor, targetDivisor - 1);
                        double rounded = preview ? Math.Round(target / targetDivisor * divisor, MidpointRounding.AwayFromZero) : target;
                        PcmWave.WriteSample(encoded.AsSpan(i * block + ch * (bits / 8), bits / 8), bits, (long)Math.Clamp(rounded, -divisor, divisor - 1));
                    }
                output.Write(encoded.AsSpan(0, frames * block)); emitted += frames;
            }
            Array.Copy(sum, hop * channels, sum, 0, (window - hop) * channels); Array.Clear(sum, (window - hop) * channels, hop * channels);
            Array.Copy(weight, hop, weight, 0, window - hop); Array.Clear(weight, window - hop, hop);
            previous = position;
        }
        if ((count * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested(); return clipped;
    }

    // Offset in [low, high] whose piece best matches the piece centred at `template`. Ties keep the
    // offset nearest zero, so exact continuations (and silence) stay at the nominal place.
    private static int Search(Window buffer, long template, long nominal, int half, int low, int high, int coarse, int fine)
    {
        double Score(int offset, int stride)
        {
            long a = template - half, b = nominal + offset - half; double cross = 0, energy = 0;
            for (int n = 0; n < half * 2; n += stride) { double c = buffer.Mono(b + n); cross += buffer.Mono(a + n) * c; energy += c * c; }
            return cross / Math.Sqrt(energy + 1e-12);
        }
        int Scan(int center, int from, int to, int step, int stride)
        {
            int best = center; double top = Score(center, stride);
            for (int j = step; center + j <= to || center - j >= from; j += step)
            {
                if (center + j <= to) { double up = Score(center + j, stride); if (up > top) { top = up; best = center + j; } }
                if (center - j >= from) { double down = Score(center - j, stride); if (down > top) { top = down; best = center - j; } }
            }
            return best;
        }
        int best = Scan(Math.Clamp(0, low, high), low, high, coarse, coarse);
        return coarse == 1 ? best : Scan(best, Math.Max(low, best - coarse + 1), Math.Min(high, best + coarse - 1), 1, fine);
    }

    // Sliding source window [start, end) with 4096 frames of read-ahead room: frames outside the
    // source read as silence. Loads move forward only, reading each source frame once in runs <= 4096.
    private sealed class Window(Stream input, WaveInfo info, int capacity, CancellationToken token)
    {
        private readonly double[] samples = new double[capacity * info.Channels], mono = new double[capacity];
        private readonly byte[] page = new byte[4096 * info.Block];
        private readonly double divisor = Math.Pow(2, info.Bits - 1);
        private long start = long.MinValue / 4, end = long.MinValue / 4;
        public void Load(long first, long last)
        {
            if (last - first > capacity - 4096) throw new InvalidOperationException("Stretch window too small.");
            if (last - start > capacity)
            {
                long keep = Math.Max(0, end - first);
                if (keep > 0)
                {
                    int shift = (int)(first - start);
                    Array.Copy(samples, shift * info.Channels, samples, 0, (int)keep * info.Channels);
                    Array.Copy(mono, shift, mono, 0, (int)keep);
                }
                start = first; end = first + keep;
            }
            while (end < last)
            {
                token.ThrowIfCancellationRequested();
                int at = (int)(end - start); long room = start + capacity - end;
                if (end < 0 || end >= info.Frames)
                {
                    long silent = end < 0 ? Math.Min(last, 0) - end : last - end;
                    Array.Clear(samples, at * info.Channels, (int)silent * info.Channels); Array.Clear(mono, at, (int)silent); end += silent; continue;
                }
                int frames = (int)Math.Min(Math.Min(4096, info.Frames - end), room);
                input.Position = info.DataOffset + end * info.Block; input.ReadExactly(page.AsSpan(0, frames * info.Block));
                for (int i = 0; i < frames; i++)
                {
                    double total = 0;
                    for (int ch = 0; ch < info.Channels; ch++)
                    {
                        double value = PcmWave.Sample(page.AsSpan(i * info.Block + ch * (info.Bits / 8), info.Bits / 8), info.Bits) / divisor;
                        samples[(at + i) * info.Channels + ch] = value; total += value;
                    }
                    mono[at + i] = total;
                }
                end += frames;
            }
        }
        public double Get(long frame, int channel) => samples[(frame - start) * info.Channels + channel];
        public double Mono(long frame) => mono[frame - start];
    }
}
