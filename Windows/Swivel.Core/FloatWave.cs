using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record FloatWaveInfo(int Rate, int Channels, int Bits, long DataOffset, long DataBytes)
{
    public int Block => Channels * (Bits / 8);
    public long Frames => DataBytes / Block;
    public double Seconds => (double)Frames / Rate;
}
public sealed record FloatWaveStats(double Peak, long OutsideUnit, long Samples);
public sealed record FloatPcmOptions(int Bits = 24, bool AllowClipping = false)
{
    public long OutputBytes(FloatWaveInfo info)
    {
        if (Bits is not 16 and not 24 and not 32) throw new UserError(FloatWave.Depth);
        long bytes = checked(info.Frames * info.Channels * (Bits / 8));
        long total = checked((Bits == 16 ? 44 : 68) + bytes + (bytes & 1));
        if (total > PcmWave.MaxBytes || info.Seconds > PcmWave.MaxSeconds) throw new UserError(FloatWave.Limit);
        return total;
    }
    public void Validate(FloatWaveInfo info, FloatWaveStats stats)
    {
        OutputBytes(info);
        if (stats.OutsideUnit > 0 && !AllowClipping) throw new UserError(FloatWave.Clipping);
    }
}

// Original RIFF float reader and explicit integer quantizer. No codec/device,
// normalization, resampling or metadata interpretation. Caller owns all streams.
public static class FloatWave
{
    public const string Invalid = "The float WAV file is incomplete or has an invalid RIFF structure.";
    public const string Unsupported = "Use a little-endian IEEE float WAV: 32/64-bit, mono or stereo, 8–192 kHz. Integer PCM uses the existing WAV tools; compressed audio, other channel layouts and RF64 are not supported here.";
    public const string Limit = "Float WAV input and complete PCM output are each limited to 512 MiB and two hours.";
    public const string Depth = "Choose 16, 24 or 32-bit integer PCM output.";
    public const string NonFinite = "The float WAV contains NaN or infinity. It cannot be converted safely.";
    public const string Clipping = "Samples exceed the unit range. Explicitly allow clipping to convert this file; no automatic normalization is applied.";
    public static FloatWaveInfo Read(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.");
        if (input.Length > PcmWave.MaxBytes) throw new UserError(Limit);
        if (input.Length < 44) throw new UserError(Invalid);
        input.Position = 0; Span<byte> header = stackalloc byte[12]; input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)) throw new UserError(Unsupported);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L != input.Length) throw new UserError(Invalid);
        int rate = 0, channels = 0, bits = 0, block = 0, chunks = 0; long offset = -1, dataBytes = 0; uint? fact = null;
        Span<byte> chunk = stackalloc byte[8]; Span<byte> fmt = stackalloc byte[40];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > 1024 || input.Length - input.Position < 8) throw new UserError(Invalid);
            input.ReadExactly(chunk); uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long next = input.Position + length + (length & 1);
            if (next > input.Length) throw new UserError(Invalid);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (rate != 0 || length is not 16 and not 18 and not 40) throw new UserError(Unsupported);
                input.ReadExactly(fmt[..(int)length]);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt), cb = length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) : 0;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]); bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                uint hz = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]), average = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
                block = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                if (channels is not 1 and not 2 || bits is not 32 and not 64 || hz is < 8000 or > 192000) throw new UserError(Unsupported);
                rate = (int)hz;
                if (tag == 0xfffe)
                {
                    if (length != 40 || cb != 22 || BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]) != bits
                        || !fmt[24..40].SequenceEqual(new byte[] {3,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113})) throw new UserError(Unsupported);
                    uint mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
                    if (mask != 0 && mask != (channels == 1 ? 4u : 3u)) throw new UserError(Unsupported);
                }
                else if (tag != 3 || length == 40 || cb != 0) throw new UserError(Unsupported);
                if (block != channels * (bits / 8) || average != (uint)(rate * block)) throw new UserError(Invalid);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (offset >= 0 || length == 0) throw new UserError(Invalid);
                offset = input.Position; dataBytes = length;
            }
            else if (chunk[..4].SequenceEqual("fact"u8))
            {
                if (fact.HasValue || length < 4) throw new UserError(Invalid);
                input.ReadExactly(fmt[..4]); fact = BinaryPrimitives.ReadUInt32LittleEndian(fmt);
            }
            // Unknown chunks are bounded by the RIFF length; no content is loaded.
            input.Position = next;
        }
        token.ThrowIfCancellationRequested();
        if (rate == 0 || offset < 0 || dataBytes % block != 0) throw new UserError(Invalid);
        var info = new FloatWaveInfo(rate, channels, bits, offset, dataBytes);
        if (fact.HasValue && fact.Value != info.Frames) throw new UserError(Invalid);
        if (info.Seconds > PcmWave.MaxSeconds) throw new UserError(Limit);
        return info;
    }

    // Every sample is inspected, including those beyond the ten-second preview.
    public static FloatWaveStats Scan(Stream input, CancellationToken token)
    {
        var info = Read(input, token); var buffer = new byte[4096 * info.Block];
        input.Position = info.DataOffset; double peak = 0; long outside = 0; int bytes = info.Bits / 8;
        for (long at = 0; at < info.Frames;)
        {
            token.ThrowIfCancellationRequested(); int count = (int)Math.Min(4096, info.Frames - at);
            input.ReadExactly(buffer.AsSpan(0, count * info.Block));
            for (int i = 0; i < count * info.Channels; i++)
            {
                double value = Sample(buffer.AsSpan(i * bytes, bytes), info.Bits);
                if (!double.IsFinite(value)) throw new UserError(NonFinite);
                peak = Math.Max(peak, Math.Abs(value)); if (value < -1 || value > 1) outside++;
            }
            at += count;
        }
        token.ThrowIfCancellationRequested(); return new(peak, outside, info.Frames * info.Channels);
    }

    public static void Export(Stream input, Stream output, FloatPcmOptions options, CancellationToken token, bool preview = false)
    {
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(input, output) || !output.CanWrite) throw new ArgumentException("Separate readable input and writable output are required.");
        var info = Read(input, token); options.OutputBytes(info);
        // Full validation precedes even the preview/header. The native source lease
        // prevents changes between validation, reading and exclusive publication.
        options.Validate(info, Scan(input, token)); token.ThrowIfCancellationRequested();
        long frames = preview ? Math.Min(info.Frames, (long)PcmWave.PreviewSeconds * info.Rate) : info.Frames;
        int bits = preview ? 16 : options.Bits, block = info.Channels * (bits / 8), bytes = info.Bits / 8;
        PcmWave.Header(output, info.Rate, info.Channels, bits, frames); input.Position = info.DataOffset;
        var buffer = new byte[4096 * info.Block]; var encoded = new byte[4096 * block];
        double targetScale = Math.Pow(2, options.Bits - 1);
        for (long at = 0; at < frames;)
        {
            token.ThrowIfCancellationRequested(); int count = (int)Math.Min(4096, frames - at);
            input.ReadExactly(buffer.AsSpan(0, count * info.Block));
            for (int i = 0; i < count * info.Channels; i++)
            {
                double value = Sample(buffer.AsSpan(i * bytes, bytes), info.Bits);
                if (!double.IsFinite(value)) throw new UserError(NonFinite);
                if ((value < -1 || value > 1) && !options.AllowClipping) throw new UserError(Clipping);
                // Clamp before multiplication: finite double.MaxValue must not overflow.
                long quantized = value >= 1 ? (long)targetScale - 1 : value <= -1 ? -(long)targetScale
                    : (long)Math.Clamp(Math.Round(value * targetScale, MidpointRounding.AwayFromZero), -targetScale, targetScale - 1);
                if (preview) quantized = (long)Math.Clamp(Math.Round(quantized / targetScale * 32768, MidpointRounding.AwayFromZero), -32768, 32767);
                PcmWave.WriteSample(encoded.AsSpan(i * (bits / 8), bits / 8), bits, quantized);
            }
            output.Write(encoded.AsSpan(0, count * block)); at += count;
        }
        if ((frames * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested();
    }
    private static double Sample(ReadOnlySpan<byte> bytes, int bits) => bits == 32
        ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
        : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
}
