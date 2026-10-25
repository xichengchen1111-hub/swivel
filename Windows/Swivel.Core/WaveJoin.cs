using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record WaveJoinInfo(WaveInfo Format, long Frames, long SourceBytes, long OutputBytes)
{
    public double Seconds => (double)Frames / Format.Rate;
}

// Join integer PCM data, never RIFF files including their headers/padding.
// The owner pins every input and serializes all reads throughout the session.
public static class WaveJoin
{
    public const int MaxInputs = 32;
    public const string Selection = "Choose 2–32 integer PCM WAV files with the same sample rate, channel count and bit depth. Convert different formats with the separate WAV tools first.";
    public const string Limit = "Combined WAV sources and complete joined output are each limited to 512 MiB. The joined duration is limited to two hours; output headers and padding count toward the limit.";
    public static WaveJoinInfo Inspect(IReadOnlyList<Stream> inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(inputs); if (inputs.Count is < 2 or > MaxInputs) throw new UserError(Selection);
        var snapshot = inputs.ToArray();
        var info = ReadAll(snapshot, token); return Summarize(snapshot, info);
    }
    private static WaveInfo[] ReadAll(IReadOnlyList<Stream> inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 2 or > MaxInputs) throw new UserError(Selection);
        var seen = new HashSet<Stream>(ReferenceEqualityComparer.Instance); var infos = new WaveInfo[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (inputs[i] is null || !seen.Add(inputs[i])) throw new UserError(Selection);
            infos[i] = PcmWave.Read(inputs[i], token);
            if (i > 0 && (infos[i].Rate != infos[0].Rate || infos[i].Channels != infos[0].Channels || infos[i].Bits != infos[0].Bits)) throw new UserError(Selection);
        }
        return infos;
    }
    private static WaveJoinInfo Summarize(IReadOnlyList<Stream> inputs, WaveInfo[] infos)
    {
        long source = 0, frames = 0;
        for (int i = 0; i < inputs.Count; i++)
        { source = checked(source + inputs[i].Length); frames = checked(frames + infos[i].Frames); }
        var format = infos[0]; long data = checked(frames * format.Block);
        long output = checked((format.Bits > 16 ? 68 : 44) + data + (data & 1));
        if (source > PcmWave.MaxBytes || output > PcmWave.MaxBytes || frames > (long)format.Rate * PcmWave.MaxSeconds) throw new UserError(Limit);
        return new(format, frames, source, output);
    }
    public static void Export(IReadOnlyList<Stream> inputs, Stream output, CancellationToken token, bool preview = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 2 or > MaxInputs) throw new UserError(Selection);
        inputs = inputs.ToArray();
        if (!output.CanWrite || inputs.Any(input => ReferenceEquals(input, output))) throw new ArgumentException("Separate input streams and a writable output are required.");
        var infos = ReadAll(inputs, token); var summary = Summarize(inputs, infos); token.ThrowIfCancellationRequested();
        var format = summary.Format; int bits = preview ? 16 : format.Bits, block = format.Channels * (bits / 8);
        long remaining = preview ? Math.Min(summary.Frames, (long)PcmWave.PreviewSeconds * format.Rate) : summary.Frames;
        PcmWave.Header(output, format.Rate, format.Channels, bits, remaining);
        var source = new byte[4096 * format.Block]; var encoded = new byte[4096 * block];
        long written = 0; double divisor = Math.Pow(2, format.Bits - 1);
        for (int i = 0; i < inputs.Count && remaining > 0; i++)
        {
            inputs[i].Position = infos[i].DataOffset; long count = Math.Min(infos[i].Frames, remaining);
            for (long at = 0; at < count;)
            {
                token.ThrowIfCancellationRequested(); int frames = (int)Math.Min(4096, count - at);
                inputs[i].ReadExactly(source.AsSpan(0, frames * format.Block));
                if (bits == format.Bits) output.Write(source.AsSpan(0, frames * format.Block));
                else
                {
                    for (int j = 0; j < frames * format.Channels; j++)
                    {
                        double value = PcmWave.Sample(source.AsSpan(j * (format.Bits / 8), format.Bits / 8), format.Bits) / divisor * 32768;
                        long rounded = (long)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), -32768, 32767);
                        PcmWave.WriteSample(encoded.AsSpan(j * 2, 2), 16, rounded);
                    }
                    output.Write(encoded.AsSpan(0, frames * block));
                }
                at += frames; remaining -= frames; written += frames;
            }
        }
        if ((written * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested();
    }
}
