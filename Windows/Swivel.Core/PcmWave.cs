using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record WaveInfo(int Rate, int Channels, int Bits, long DataOffset, long DataBytes)
{
    public int Block => Channels * (Bits / 8);
    public long Frames => DataBytes / Block;
    public double Seconds => (double)Frames / Rate;
}
public readonly record struct AudioRange(long First, long Count, long FadeIn, long FadeOut);
public sealed record AudioEdit(double Start, double End, double Gain = 1, double FadeIn = 0, double FadeOut = 0)
{
    public AudioRange Range(WaveInfo info)
    {
        if (!double.IsFinite(Start) || !double.IsFinite(End) || !double.IsFinite(Gain) || !double.IsFinite(FadeIn) || !double.IsFinite(FadeOut)
            || Start < 0 || End > info.Seconds || Start >= End || Gain is < 0 or > 4 || FadeIn < 0 || FadeOut < 0 || FadeIn > End-Start || FadeOut > End-Start)
            throw new UserError("Choose a non-empty audio range, volume from 0–400%, and fades no longer than the selection.");
        long Frame(double seconds) => (long)Math.Round(seconds * info.Rate, MidpointRounding.AwayFromZero);
        long first = Frame(Start), count = Frame(End)-first;
        if (count < 1) throw new UserError("The audio selection must contain at least one sample frame.");
        return new(first,count,Math.Min(count,Frame(FadeIn)),Math.Min(count,Frame(FadeOut)));
    }
}

// Original bounded RIFF/PCM implementation. No paths, codecs, devices or reference
// media are opened here. The caller owns and pins both streams throughout export.
public static class PcmWave
{
    public const long MaxBytes = 512L * 1024 * 1024;
    public const int MaxSeconds = 7200, PreviewSeconds = 10;
    private const string Invalid = "The WAV file is incomplete or has an invalid RIFF structure.";
    private const string Unsupported = "Use an uncompressed integer PCM WAV: mono or stereo, 8/16/24/32-bit, 8–192 kHz. Float, compressed audio and other channel layouts are not supported yet.";
    private const string Limit = "WAV files are limited to 512 MB and two hours.";
    public static WaveInfo Read(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.");
        if (input.Length > MaxBytes) throw new UserError(Limit);
        if (input.Length < 44) throw new UserError(Invalid);
        input.Position = 0; Span<byte> header = stackalloc byte[12]; input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)) throw new UserError(Unsupported);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L != input.Length) throw new UserError(Invalid);
        int rate = 0, channels = 0, bits = 0, block = 0, chunks = 0; long dataOffset = -1, dataBytes = 0;
        Span<byte> chunk = stackalloc byte[8]; Span<byte> format = stackalloc byte[40];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > 1024 || input.Length-input.Position < 8) throw new UserError(Invalid);
            input.ReadExactly(chunk); uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long next = input.Position + length + (length & 1);
            if (next > input.Length) throw new UserError(Invalid);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (rate != 0 || length is not 16 and not 18 and not 40) throw new UserError(Unsupported);
                input.ReadExactly(format[..(int)length]);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(format), cb = length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) : 0;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]); bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                uint hz = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]), average = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                block = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                if (channels is not 1 and not 2 || bits is not 8 and not 16 and not 24 and not 32 || hz is < 8000 or > 192000) throw new UserError(Unsupported);
                rate = (int)hz;
                if (tag == 0xfffe)
                {
                    if (length != 40 || cb != 22 || BinaryPrimitives.ReadUInt16LittleEndian(format[18..]) != bits
                        || !format[24..40].SequenceEqual(new byte[] {1,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113})) throw new UserError(Unsupported);
                    uint mask = BinaryPrimitives.ReadUInt32LittleEndian(format[20..]);
                    if (mask != 0 && mask != (channels == 1 ? 4u : 3u)) throw new UserError(Unsupported);
                }
                else if (tag != 1 || length == 40 || cb != 0) throw new UserError(Unsupported);
                if (block != channels*(bits/8) || average != (uint)(rate*block)) throw new UserError(Invalid);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (dataOffset >= 0 || length == 0) throw new UserError(Invalid);
                dataOffset = input.Position; dataBytes = length;
            }
            // Metadata and LIST contents are never interpreted or preserved.
            input.Position = next;
        }
        token.ThrowIfCancellationRequested();
        if (rate == 0 || dataOffset < 0 || dataBytes % block != 0) throw new UserError(Invalid);
        var result = new WaveInfo(rate,channels,bits,dataOffset,dataBytes);
        if (result.Seconds > MaxSeconds) throw new UserError(Limit);
        return result;
    }

    // Peak envelope scans in small chunks; memory does not grow with duration.
    public static double[] Peaks(Stream input, int bins, CancellationToken token)
    {
        if (bins is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(bins));
        var info = Read(input,token); var result = new double[bins]; var buffer = new byte[4096*info.Block]; input.Position = info.DataOffset;
        double divisor = Math.Pow(2,info.Bits-1); int bytes = info.Bits/8;
        for (long at = 0; at < info.Frames;)
        {
            token.ThrowIfCancellationRequested(); int frames = (int)Math.Min(4096,info.Frames-at); input.ReadExactly(buffer.AsSpan(0,frames*info.Block));
            for (int i = 0; i < frames; i++)
            {
                int bin = (int)((at+i)*bins/info.Frames);
                for (int ch = 0; ch < info.Channels; ch++) result[bin] = Math.Max(result[bin],Math.Abs(Sample(buffer.AsSpan(i*info.Block+ch*bytes,bytes),info.Bits)/divisor));
            }
            at += frames;
        }
        token.ThrowIfCancellationRequested(); return result;
    }

    // preview uses the SAME range/envelope, only limits the number of written
    // frames and changes to PCM16 for the system preview player. It never changes
    // the real fade-out time or truncates the saved output.
    public static long Export(Stream input, Stream output, AudioEdit edit, CancellationToken token, bool preview = false)
    {
        var info = Read(input,token); var range = edit.Range(info);
        long count = preview ? Math.Min(range.Count,(long)PreviewSeconds*info.Rate) : range.Count;
        int bits = preview ? 16 : info.Bits, outputBlock = info.Channels*(bits/8);
        Header(output,info.Rate,info.Channels,bits,count); input.Position = info.DataOffset+range.First*info.Block;
        var buffer = new byte[4096*info.Block]; var encoded = new byte[4096*outputBlock];
        double sourceDivisor = Math.Pow(2,info.Bits-1), outputDivisor = Math.Pow(2,bits-1); long clipped = 0;
        bool exact = bits == info.Bits && edit.Gain == 1 && range.FadeIn == 0 && range.FadeOut == 0;
        for (long at = 0; at < count;)
        {
            token.ThrowIfCancellationRequested(); int frames = (int)Math.Min(4096,count-at); input.ReadExactly(buffer.AsSpan(0,frames*info.Block));
            if (exact) output.Write(buffer.AsSpan(0,frames*info.Block));
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    double gain = edit.Gain * Ramp(at+i,range.FadeIn) * Ramp(range.Count-1-at-i,range.FadeOut);
                    for (int ch = 0; ch < info.Channels; ch++)
                    {
                        double value = Sample(buffer.AsSpan(i*info.Block+ch*(info.Bits/8),info.Bits/8),info.Bits)/sourceDivisor*gain*outputDivisor;
                        double rounded = Math.Round(value,MidpointRounding.AwayFromZero);
                        if (rounded < -outputDivisor || rounded > outputDivisor-1) clipped++;
                        WriteSample(encoded.AsSpan(i*outputBlock+ch*(bits/8),bits/8),bits,(long)Math.Clamp(rounded,-outputDivisor,outputDivisor-1));
                    }
                }
                output.Write(encoded.AsSpan(0,frames*outputBlock));
            }
            at += frames;
        }
        if ((count*outputBlock & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested(); return clipped;
    }
    internal static double Ramp(long frame, long fade) => fade == 0 ? 1 : fade == 1 ? (frame == 0 ? 0 : 1) : Math.Min(1,(double)frame/(fade-1));
    internal static int Sample(ReadOnlySpan<byte> bytes, int bits) => bits switch {
        8 => bytes[0]-128,
        16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        24 => (bytes[0] | bytes[1]<<8 | bytes[2]<<16) << 8 >> 8,
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes)
    };
    internal static void WriteSample(Span<byte> bytes,int bits,long value)
    {
        if (bits == 8) bytes[0] = (byte)(value+128);
        else if (bits == 16) BinaryPrimitives.WriteInt16LittleEndian(bytes,(short)value);
        else if (bits == 24) { bytes[0] = (byte)value; bytes[1] = (byte)(value>>8); bytes[2] = (byte)(value>>16); }
        else BinaryPrimitives.WriteInt32LittleEndian(bytes,(int)value);
    }
    internal static void Header(Stream output,int rate,int channels,int bits,long frames)
    {
        bool extended = bits > 16; int dataAt = extended ? 60 : 36;
        uint size = checked((uint)(frames*channels*(bits/8))); Span<byte> header = stackalloc byte[extended ? 68 : 44]; header.Clear();
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..],(uint)(header.Length-8)+size+(size&1)); "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..],extended ? 40u : 16u); BinaryPrimitives.WriteUInt16LittleEndian(header[20..],extended ? (ushort)0xfffe : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..],(ushort)channels); BinaryPrimitives.WriteUInt32LittleEndian(header[24..],(uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..],(uint)(rate*channels*(bits/8))); BinaryPrimitives.WriteUInt16LittleEndian(header[32..],(ushort)(channels*(bits/8)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..],(ushort)bits);
        if (extended)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header[36..],22); BinaryPrimitives.WriteUInt16LittleEndian(header[38..],(ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..],channels == 1 ? 4u : 3u);
            new byte[] {1,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113}.CopyTo(header[44..]);
        }
        "data"u8.CopyTo(header[dataAt..]); BinaryPrimitives.WriteUInt32LittleEndian(header[(dataAt+4)..],size); output.Write(header);
    }
}
