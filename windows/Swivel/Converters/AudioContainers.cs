using System.Buffers.Binary;
using System.Text;

namespace Swivel;

/// Containers Media Foundation can't write, built from its output: AIFF (big-endian 16-bit PCM) and CAF (lpcm,
/// little-endian 16-bit, like afconvert's LEI16) from a WAV, and AAC in ADTS (a raw .aac stream) from an M4A.
static class AudioContainers
{
    /// 16-bit PCM read from a RIFF WAVE file.
    internal sealed record Pcm(int Channels, int SampleRate, int Bits, byte[] Data);

    public static Pcm ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a WAVE file");
        int channels = 0, rate = 0, bits = 0, format = 0;
        byte[]? data = null;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            int body = at + 8;
            long available = Math.Min(size, bytes.Length - body);
            if (id == "fmt " && available >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (format == 0xFFFE && available >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                // Streaming writers may leave the size at 0 or 0xFFFFFFFF: take the rest of the file then.
                if (size == 0 || size == 0xFFFFFFFF) available = bytes.Length - body;
                data = bytes.AsSpan(body, (int)available).ToArray();
            }
            at = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
            if (size == 0xFFFFFFFF) break;
        }
        if (data == null || format != 1 || bits != 16 || channels < 1 || rate < 1) throw new InvalidDataException("Unsupported WAVE");
        int frame = channels * 2;
        if (data.Length % frame != 0) data = data.AsSpan(0, data.Length - data.Length % frame).ToArray();
        return new Pcm(channels, rate, bits, data);
    }

    public static byte[] Wav(Pcm pcm)
    {
        using var stream = new MemoryStream(pcm.Data.Length + 44);
        var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write((uint)(36 + pcm.Data.Length)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16u); w.Write((ushort)1); w.Write((ushort)pcm.Channels); w.Write((uint)pcm.SampleRate);
        w.Write((uint)(pcm.SampleRate * pcm.Channels * 2)); w.Write((ushort)(pcm.Channels * 2)); w.Write((ushort)16);
        w.Write("data"u8); w.Write((uint)pcm.Data.Length); w.Write(pcm.Data);
        if ((pcm.Data.Length & 1) == 1) w.Write((byte)0);
        w.Flush();
        return stream.ToArray();
    }

    public static byte[] Aiff(Pcm pcm)
    {
        int frames = pcm.Data.Length / (pcm.Channels * 2);
        var swapped = new byte[pcm.Data.Length];
        for (int i = 0; i + 1 < pcm.Data.Length; i += 2) { swapped[i] = pcm.Data[i + 1]; swapped[i + 1] = pcm.Data[i]; }
        using var stream = new MemoryStream(swapped.Length + 64);
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); stream.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); stream.Write(b); }
        int ssnd = 8 + swapped.Length;
        stream.Write("FORM"u8); U32((uint)(4 + (8 + 18) + (8 + ssnd + (ssnd & 1)))); stream.Write("AIFF"u8);
        stream.Write("COMM"u8); U32(18); U16((ushort)pcm.Channels); U32((uint)frames); U16(16); stream.Write(Extended(pcm.SampleRate));
        stream.Write("SSND"u8); U32((uint)ssnd); U32(0); U32(0); stream.Write(swapped);
        if ((ssnd & 1) == 1) stream.WriteByte(0);
        return stream.ToArray();
    }

    /// An 80-bit IEEE 754 extended float (AIFF's sample rate field).
    internal static byte[] Extended(double value)
    {
        var result = new byte[10];
        if (value <= 0) return result;
        int exponent = (int)Math.Floor(Math.Log2(value));
        ulong mantissa = (ulong)Math.Round(value / Math.Pow(2, exponent - 63));
        if (mantissa == 0) return result;
        if ((mantissa & 0x8000000000000000UL) == 0) { mantissa <<= 1; exponent--; }
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)(exponent + 16383));
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(2), mantissa);
        return result;
    }

    internal static double FromExtended(ReadOnlySpan<byte> bytes)
    {
        int exponent = (BinaryPrimitives.ReadUInt16BigEndian(bytes) & 0x7FFF) - 16383;
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        return mantissa * Math.Pow(2, exponent - 63);
    }

    public static byte[] Caf(Pcm pcm)
    {
        using var stream = new MemoryStream(pcm.Data.Length + 80);
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); stream.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); stream.Write(b); }
        void I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); stream.Write(b); }
        stream.Write("caff"u8); U16(1); U16(0);
        stream.Write("desc"u8); I64(32);
        I64(BitConverter.DoubleToInt64Bits(pcm.SampleRate));
        stream.Write("lpcm"u8);
        U32(2);                              // kCAFLinearPCMFormatFlagIsLittleEndian
        U32((uint)(pcm.Channels * 2));       // bytes per packet
        U32(1);                              // frames per packet
        U32((uint)pcm.Channels);
        U32(16);
        stream.Write("data"u8); I64(4 + pcm.Data.Length); U32(0);  // edit count
        stream.Write(pcm.Data);
        return stream.ToArray();
    }

    // MARK: M4A → ADTS

    static readonly int[] AdtsRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// The AAC frames of an M4A's sound track, each with a 7-byte ADTS header.
    public static byte[] Adts(string m4aPath)
    {
        var file = File.ReadAllBytes(m4aPath);
        var moov = Child(file, 0, file.Length, "moov") ?? throw new InvalidDataException("No moov");
        foreach (var trak in Children(file, moov.Start, moov.End, "trak"))
        {
            var mdia = Child(file, trak.Start, trak.End, "mdia");
            if (mdia is null) continue;
            var hdlr = Child(file, mdia.Value.Start, mdia.Value.End, "hdlr");
            if (hdlr is null || Encoding.ASCII.GetString(file, hdlr.Value.Start + 8, 4) != "soun") continue;
            var stbl = Descend(file, mdia.Value, "minf", "stbl") ?? throw new InvalidDataException("No stbl");
            var stsd = Child(file, stbl.Start, stbl.End, "stsd") ?? throw new InvalidDataException("No stsd");
            // stsd: version/flags, entry count, then the first sample entry ('mp4a': 28 bytes before its child boxes).
            int entry = stsd.Start + 8;
            if (Encoding.ASCII.GetString(file, entry + 4, 4) != "mp4a") throw new InvalidDataException("Not AAC");
            int entryEnd = entry + (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry));
            var esds = Child(file, entry + 8 + 28, entryEnd, "esds") ?? throw new InvalidDataException("No esds");
            var (objectType, rateIndex, channels) = AudioSpecificConfig(file.AsSpan(esds.Start + 4, esds.End - esds.Start - 4));

            var sizes = SampleSizes(file, Child(file, stbl.Start, stbl.End, "stsz") ?? throw new InvalidDataException("No stsz"));
            var offsets = ChunkOffsets(file, stbl);
            var stsc = Child(file, stbl.Start, stbl.End, "stsc") ?? throw new InvalidDataException("No stsc");
            int runs = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsc.Start + 4));
            var firstChunk = new int[runs]; var perChunk = new int[runs];
            for (int i = 0; i < runs; i++)
            {
                firstChunk[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsc.Start + 8 + i * 12));
                perChunk[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsc.Start + 12 + i * 12));
            }

            using var output = new MemoryStream();
            int sample = 0, run = 0;
            for (int chunk = 1; chunk <= offsets.Length && sample < sizes.Length; chunk++)
            {
                while (run + 1 < runs && firstChunk[run + 1] <= chunk) run++;
                long position = offsets[chunk - 1];
                for (int s = 0; s < perChunk[run] && sample < sizes.Length; s++, sample++)
                {
                    int size = sizes[sample];
                    if (position + size > file.Length) throw new InvalidDataException("Truncated");
                    output.Write(AdtsHeader(objectType, rateIndex, channels, size));
                    output.Write(file, (int)position, size);
                    position += size;
                }
            }
            return output.ToArray();
        }
        throw new InvalidDataException("No sound track");
    }

    static byte[] AdtsHeader(int objectType, int rateIndex, int channels, int payload)
    {
        int length = payload + 7;
        int profile = Math.Clamp(objectType - 1, 0, 3);
        return
        [
            0xFF, 0xF1,  // sync word, MPEG-4, layer 0, no CRC
            (byte)((profile << 6) | (rateIndex << 2) | ((channels >> 2) & 1)),
            (byte)(((channels & 3) << 6) | ((length >> 11) & 3)),
            (byte)((length >> 3) & 0xFF),
            (byte)(((length & 7) << 5) | 0x1F),
            0xFC,        // buffer fullness 0x7FF (variable), one raw data block
        ];
    }

    /// Object type, sampling-frequency index and channel configuration from an esds box's DecoderSpecificInfo.
    static (int ObjectType, int RateIndex, int Channels) AudioSpecificConfig(ReadOnlySpan<byte> esds)
    {
        int at = 0;
        // Walk descriptors: 0x03 ES_Descriptor ⊃ 0x04 DecoderConfigDescriptor ⊃ 0x05 DecoderSpecificInfo.
        while (at < esds.Length)
        {
            int tag = esds[at++];
            int length = 0;
            for (int i = 0; i < 4 && at < esds.Length; i++)
            {
                int b = esds[at++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            if (tag == 0x03)
            {
                int flags = esds[at + 2];
                at += 3;
                if ((flags & 0x80) != 0) at += 2;
                if ((flags & 0x40) != 0) at += 1 + esds[at];
                if ((flags & 0x20) != 0) at += 2;
            }
            else if (tag == 0x04) at += 13;
            else if (tag == 0x05)
            {
                if (length < 2 || at + 2 > esds.Length) break;
                int objectType = esds[at] >> 3;
                int rateIndex = ((esds[at] & 7) << 1) | (esds[at + 1] >> 7);
                int channels = (esds[at + 1] >> 3) & 0xF;
                if (rateIndex == 15)
                {
                    // An explicit 24-bit rate: map it to the nearest ADTS index.
                    int rate = ((esds[at + 1] & 0x7F) << 17) | (esds[at + 2] << 9) | (esds[at + 3] << 1) | (esds[at + 4] >> 7);
                    channels = (esds[at + 4] >> 3) & 0xF;
                    rateIndex = Array.IndexOf(AdtsRates, rate);
                    if (rateIndex < 0) throw new InvalidDataException("Unsupported rate");
                }
                return (objectType, rateIndex, channels);
            }
            else at += length;
        }
        throw new InvalidDataException("No AudioSpecificConfig");
    }

    static int[] SampleSizes(byte[] file, (int Start, int End) stsz)
    {
        uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Start + 4));
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Start + 8));
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
            sizes[i] = fixedSize != 0 ? (int)fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Start + 12 + i * 4));
        return sizes;
    }

    static long[] ChunkOffsets(byte[] file, (int Start, int End) stbl)
    {
        if (Child(file, stbl.Start, stbl.End, "stco") is { } stco)
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stco.Start + 4));
            return Enumerable.Range(0, count).Select(i => (long)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stco.Start + 8 + i * 4))).ToArray();
        }
        var co64 = Child(file, stbl.Start, stbl.End, "co64") ?? throw new InvalidDataException("No chunk offsets");
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(co64.Start + 4));
        return Enumerable.Range(0, n).Select(i => (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(co64.Start + 8 + i * 8))).ToArray();
    }

    /// The body (after the 8- or 16-byte header) of each box of `type` directly inside [start, end).
    internal static IEnumerable<(int Start, int End)> Children(byte[] file, int start, int end, string type)
    {
        int at = start;
        while (at + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            int header = 8;
            if (size == 1 && at + 16 <= end) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 8)); header = 16; }
            else if (size == 0) size = end - at;
            if (size < header || at + size > end) yield break;
            if (Encoding.ASCII.GetString(file, at + 4, 4) == type) yield return (at + header, (int)(at + size));
            at += (int)size;
        }
    }

    internal static (int Start, int End)? Child(byte[] file, int start, int end, string type)
    {
        foreach (var box in Children(file, start, end, type)) return box;
        return null;
    }

    static (int Start, int End)? Descend(byte[] file, (int Start, int End) box, params string[] types)
    {
        (int Start, int End)? current = box;
        foreach (var t in types)
        {
            if (current is not { } c) return null;
            current = Child(file, c.Start, c.End, t);
        }
        return current;
    }
}
