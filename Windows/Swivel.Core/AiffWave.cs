using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record AiffInfo(int Rate, int Channels, int Bits, long Frames, long DataOffset, bool LittleEndian, bool CompressedForm)
{
    public int Block => Channels * (Bits / 8);
    public double Seconds => (double)Frames / Rate;
    public long OutputBytes()
    {
        long bytes = checked(Frames * Block), total = checked((Bits > 16 ? 68 : 44) + bytes + (bytes & 1));
        if (total > PcmWave.MaxBytes || Seconds > PcmWave.MaxSeconds) throw new UserError(AiffWave.Limit);
        return total;
    }
}

// Original bounded AIFF PCM reader: no system codecs, resource forks or paths.
// AIFC is a container name here; only its explicit integer encodings are accepted.
public static class AiffWave
{
    public const string Invalid = "The AIFF file is incomplete or has an invalid FORM, chunk, sample count or padding.";
    public const string Unsupported = "Use integer PCM AIFF or AIFC NONE/sowt/twos/in24/in32: mono or stereo, 8/16/24/32-bit, an exact integer-Hz rate in the 8–192 kHz range and zero SSND block size. Compressed/float encodings and other layouts are not supported here.";
    public const string Limit = "AIFF input and complete WAV output are each limited to 512 MiB and two hours, with at most 1,024 input chunks.";
    public static AiffInfo Read(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.");
        if (input.Length > PcmWave.MaxBytes) throw new UserError(Limit);
        if (input.Length < 54) throw new UserError(Invalid);
        input.Position = 0; Span<byte> header = stackalloc byte[12]; input.ReadExactly(header);
        bool aifc = header[8..].SequenceEqual("AIFC"u8);
        if (!header[..4].SequenceEqual("FORM"u8) || (!aifc && !header[8..].SequenceEqual("AIFF"u8))) throw new UserError(Unsupported);
        if (BinaryPrimitives.ReadUInt32BigEndian(header[4..]) + 8L != input.Length) throw new UserError(Invalid);
        int rate = 0, channels = 0, bits = 0, chunks = 0; long frames = 0, dataOffset = -1, dataBytes = 0;
        bool version = false, little = false; Span<byte> chunk = stackalloc byte[8]; Span<byte> fields = stackalloc byte[23];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > 1024) throw new UserError(Limit);
            if (input.Length - input.Position < 8) throw new UserError(Invalid);
            input.ReadExactly(chunk); uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            long start = input.Position, end = start + length, next = end + (length & 1);
            if (next > input.Length) throw new UserError(Invalid);
            if ((length & 1) != 0) { input.Position = end; if (input.ReadByte() != 0) throw new UserError(Invalid); input.Position = start; }
            if (chunk[..4].SequenceEqual("COMM"u8))
            {
                if (rate != 0 || (!aifc && length != 18) || (aifc && length is < 24 or > 278)) throw new UserError(Invalid);
                input.ReadExactly(fields[..(aifc ? 23 : 18)]);
                channels = BinaryPrimitives.ReadUInt16BigEndian(fields); frames = BinaryPrimitives.ReadUInt32BigEndian(fields[2..]); bits = BinaryPrimitives.ReadUInt16BigEndian(fields[6..]);
                if (channels is not 1 and not 2 || bits is not 8 and not 16 and not 24 and not 32 || frames == 0) throw new UserError(Unsupported);
                rate = Rate(fields[8..18]);
                if (aifc)
                {
                    little = fields[18..22].SequenceEqual("sowt"u8);
                    bool big = fields[18..22].SequenceEqual("NONE"u8)
                        || (bits == 16 && fields[18..22].SequenceEqual("twos"u8))
                        || (bits == 24 && fields[18..22].SequenceEqual("in24"u8))
                        || (bits == 32 && fields[18..22].SequenceEqual("in32"u8));
                    if (!little && !big) throw new UserError(Unsupported);
                    int nameBytes = fields[22] + 1, padded = nameBytes + (nameBytes & 1);
                    if (length != 22 + padded) throw new UserError(Invalid);
                    // The Pascal codec name is never decoded, displayed or used to choose a codec.
                    if ((nameBytes & 1) != 0) { input.Position = end - 1; if (input.ReadByte() != 0) throw new UserError(Invalid); }
                }
            }
            else if (chunk[..4].SequenceEqual("FVER"u8))
            {
                if (!aifc) throw new UserError(Unsupported);
                if (version || length != 4) throw new UserError(Invalid);
                input.ReadExactly(fields[..4]); if (BinaryPrimitives.ReadUInt32BigEndian(fields) != 0xa2805140) throw new UserError(Unsupported); version = true;
            }
            else if (chunk[..4].SequenceEqual("SSND"u8))
            {
                if (dataOffset >= 0 || length < 8) throw new UserError(Invalid);
                input.ReadExactly(fields[..8]); uint offset = BinaryPrimitives.ReadUInt32BigEndian(fields), block = BinaryPrimitives.ReadUInt32BigEndian(fields[4..]);
                if (block != 0) throw new UserError(Unsupported);
                if (offset > length - 8) throw new UserError(Invalid);
                dataOffset = input.Position + offset; dataBytes = length - 8L - offset;
            }
            // Remaining chunk payloads are skipped within the held local stream.
            input.Position = next;
        }
        token.ThrowIfCancellationRequested();
        if (rate == 0 || dataOffset < 0 || (aifc && !version) || dataBytes != frames * channels * (bits / 8)) throw new UserError(Invalid);
        var info = new AiffInfo(rate, channels, bits, frames, dataOffset, little, aifc);
        if (info.Seconds > PcmWave.MaxSeconds) throw new UserError(Limit);
        return info;
    }
    private static int Rate(ReadOnlySpan<byte> field)
    {
        // Only canonical positive normal extended80 values, exactly integer Hz.
        // Accepted range's exponent is 12..17, so all shifts stay within 46..51.
        int exponent = BinaryPrimitives.ReadUInt16BigEndian(field);
        ulong significand = BinaryPrimitives.ReadUInt64BigEndian(field[2..]);
        if (exponent is < 16395 or > 16400 || (significand >> 63) != 1) throw new UserError(Unsupported);
        int shift = 16383 + 63 - exponent;
        if ((significand & ((1UL << shift) - 1)) != 0) throw new UserError(Unsupported);
        ulong value = significand >> shift;
        if (value is < 8000 or > 192000) throw new UserError(Unsupported);
        return (int)value;
    }
    public static void Export(Stream input, Stream output, CancellationToken token, bool preview = false)
    {
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(input, output) || !output.CanWrite) throw new ArgumentException("Separate readable input and writable output are required.");
        var info = Read(input, token); info.OutputBytes(); token.ThrowIfCancellationRequested();
        long frames = preview ? Math.Min(info.Frames, (long)PcmWave.PreviewSeconds * info.Rate) : info.Frames;
        int bits = preview ? 16 : info.Bits, block = info.Channels * (bits / 8), width = info.Bits / 8;
        PcmWave.Header(output, info.Rate, info.Channels, bits, frames); input.Position = info.DataOffset;
        var buffer = new byte[4096 * info.Block]; var encoded = new byte[4096 * block]; double scale = Math.Pow(2, info.Bits - 1);
        for (long at = 0; at < frames;)
        {
            token.ThrowIfCancellationRequested(); int count = (int)Math.Min(4096, frames - at); input.ReadExactly(buffer.AsSpan(0, count * info.Block));
            for (int i = 0; i < count * info.Channels; i++)
            {
                var sample = buffer.AsSpan(i * width, width); int value;
                if (info.Bits == 8) value = (sbyte)sample[0];
                else if (info.Bits == 16) value = info.LittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(sample) : BinaryPrimitives.ReadInt16BigEndian(sample);
                else if (info.Bits == 24) value = (info.LittleEndian ? sample[0] | sample[1] << 8 | sample[2] << 16 : sample[2] | sample[1] << 8 | sample[0] << 16) << 8 >> 8;
                else value = info.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(sample) : BinaryPrimitives.ReadInt32BigEndian(sample);
                long result = preview ? (long)Math.Clamp(Math.Round(value / scale * 32768, MidpointRounding.AwayFromZero), -32768, 32767) : value;
                PcmWave.WriteSample(encoded.AsSpan(i * (bits / 8), bits / 8), bits, result);
            }
            output.Write(encoded.AsSpan(0, count * block)); at += count;
        }
        if ((frames * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested();
    }
}
