using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Swivel.Core;

// Original plain AIFF writer for the existing validated integer PCM WAV contract.
// It preserves numeric samples, not RIFF bytes, metadata or instrument information.
public static class AiffExport
{
    public const string Limit = "Integer WAV input and complete AIFF output are each limited to 512 MiB and two hours. The AIFF header and final padding count toward the output limit.";
    public static long OutputBytes(WaveInfo info)
    {
        long data = checked(info.Frames * info.Block), total = checked(54 + data + (data & 1));
        if (total > PcmWave.MaxBytes || info.Seconds > PcmWave.MaxSeconds) throw new UserError(Limit);
        return total;
    }
    public static void Export(Stream input, Stream output, CancellationToken token, bool preview = false)
    {
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(input, output) || !output.CanWrite) throw new ArgumentException("Separate readable input and writable output are required.");
        var info = PcmWave.Read(input, token); OutputBytes(info); token.ThrowIfCancellationRequested();
        if (preview) { PcmWave.Export(input, output, new(0, info.Seconds), token, preview: true); return; }
        Header(output, info); input.Position = info.DataOffset;
        int width = info.Bits / 8; var source = new byte[4096 * info.Block]; var encoded = new byte[source.Length];
        for (long at = 0; at < info.Frames;)
        {
            token.ThrowIfCancellationRequested(); int count = (int)Math.Min(4096, info.Frames - at);
            input.ReadExactly(source.AsSpan(0, count * info.Block));
            for (int i = 0; i < count * info.Channels; i++)
            {
                uint value = unchecked((uint)PcmWave.Sample(source.AsSpan(i * width, width), info.Bits));
                for (int b = 0; b < width; b++) encoded[i * width + b] = (byte)(value >> ((width - 1 - b) * 8));
            }
            output.Write(encoded.AsSpan(0, count * info.Block)); at += count;
        }
        if ((info.DataBytes & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested();
    }
    private static void Header(Stream output, WaveInfo info)
    {
        Span<byte> header = stackalloc byte[54]; header.Clear();
        "FORM"u8.CopyTo(header); BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)(OutputBytes(info) - 8)));
        "AIFFCOMM"u8.CopyTo(header[8..]); BinaryPrimitives.WriteUInt32BigEndian(header[16..], 18);
        BinaryPrimitives.WriteUInt16BigEndian(header[20..], (ushort)info.Channels); BinaryPrimitives.WriteUInt32BigEndian(header[22..], checked((uint)info.Frames));
        BinaryPrimitives.WriteUInt16BigEndian(header[26..], (ushort)info.Bits);
        int exponent = BitOperations.Log2((uint)info.Rate);
        BinaryPrimitives.WriteUInt16BigEndian(header[28..], (ushort)(16383 + exponent));
        BinaryPrimitives.WriteUInt64BigEndian(header[30..], (ulong)info.Rate << (63 - exponent));
        "SSND"u8.CopyTo(header[38..]); BinaryPrimitives.WriteUInt32BigEndian(header[42..], checked((uint)info.DataBytes + 8));
        output.Write(header);
    }
}
