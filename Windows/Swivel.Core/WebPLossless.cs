using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

/// An original bounded decoder for the WebP lossless bitstream ('VP8L'), written from the format
/// description in RFC 9649 section 3: transforms (predictor, colour, subtract-green, colour
/// indexing), colour cache, meta prefix codes and LZ77 backward references. No third-party code.
/// Pixels come out as 0xAARRGGBB in scan order.
internal static class WebPLossless
{
    // Prefix-code tables of all groups together may hold at most this many entries (4 bytes
    // each, plus a fixed allowance per code for its own arrays); a file that needs more is
    // refused rather than allocating without limit.
    internal const long MaxTableEntries = 16_000_000;
    private const int RootBits = 8;
    private const int CodeOverhead = 64;
    private static readonly int[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly sbyte[] DistanceMap =
    {
        0,1, 1,0, 1,1, -1,1, 0,2, 2,0, 1,2, -1,2, 2,1, -2,1, 2,2, -2,2, 0,3, 3,0, 1,3, -1,3, 3,1, -3,1, 2,3, -2,3, 3,2,
        -3,2, 0,4, 4,0, 1,4, -1,4, 4,1, -4,1, 3,3, -3,3, 2,4, -2,4, 4,2, -4,2, 0,5, 3,4, -3,4, 4,3, -4,3, 5,0, 1,5, -1,5,
        5,1, -5,1, 2,5, -2,5, 5,2, -5,2, 4,4, -4,4, 3,5, -3,5, 5,3, -5,3, 0,6, 6,0, 1,6, -1,6, 6,1, -6,1, 2,6, -2,6, 6,2,
        -6,2, 4,5, -4,5, 5,4, -5,4, 3,6, -3,6, 6,3, -6,3, 0,7, 7,0, 1,7, -1,7, 5,5, -5,5, 7,1, -7,1, 4,6, -4,6, 6,4, -6,4,
        2,7, -2,7, 7,2, -7,2, 3,7, -3,7, 7,3, -7,3, 5,6, -5,6, 6,5, -6,5, 8,0, 4,7, -4,7, 7,4, -7,4, 8,1, 8,2, 6,6, -6,6,
        8,3, 5,7, -5,7, 7,5, -7,5, 8,4, 6,7, -6,7, 7,6, -7,6, 8,5, 7,7, -7,7, 8,6, 8,7
    };

    /// Reads a VP8L chunk: the 5-byte header (signature, size, alpha hint, version) then the image.
    internal static uint[] Read(ReadOnlySpan<byte> chunk, out int width, out int height, CancellationToken token)
    {
        if (chunk.Length < 5 || chunk[0] != 0x2F) throw new UserError(WebPImage.Invalid);
        var bits = new Bits(chunk[1..].ToArray());
        width = (int)bits.Read(14) + 1; height = (int)bits.Read(14) + 1;
        bits.Read(1);
        if (bits.Read(3) != 0) throw new UserError(WebPImage.Invalid);
        ImageRules.Validate(width, height);
        var budget = new Budget();
        return ImageStream(bits, width, height, budget, token);
    }

    /// The headerless image-stream used inside an 'ALPH' chunk, of known size.
    internal static uint[] ReadHeaderless(byte[] data, int width, int height, CancellationToken token)
        => ImageStream(new Bits(data), width, height, new Budget(), token);

    private sealed class Budget { public long Entries; }

    private static uint[] ImageStream(Bits bits, int width, int height, Budget budget, CancellationToken token)
    {
        var transforms = new List<Transform>();
        int seen = 0, codedWidth = width;
        while (bits.Read(1) == 1)
        {
            int type = (int)bits.Read(2);
            if ((seen & 1 << type) != 0) throw new UserError(WebPImage.Invalid);
            seen |= 1 << type;
            var transform = new Transform { Type = type, Width = codedWidth };
            if (type is 0 or 1)
            {
                transform.Bits = (int)bits.Read(3) + 2;
                transform.Data = Entropy(bits, Div(codedWidth, transform.Bits), Div(height, transform.Bits), false, budget, token);
            }
            else if (type == 3)
            {
                int size = (int)bits.Read(8) + 1;
                var table = Entropy(bits, size, 1, false, budget, token);
                for (int i = 1; i < size; i++) table[i] = AddPixels(table[i], table[i - 1]);
                transform.Data = table;
                transform.Bits = size <= 2 ? 3 : size <= 4 ? 2 : size <= 16 ? 1 : 0;
                codedWidth = Div(codedWidth, transform.Bits);
            }
            transforms.Add(transform);
        }
        var pixels = Entropy(bits, codedWidth, height, true, budget, token);
        if (bits.Overrun) throw new UserError(WebPImage.Invalid);
        for (int i = transforms.Count - 1; i >= 0; i--) pixels = Inverse(transforms[i], pixels, height, token);
        return pixels;
    }

    private sealed class Transform { public int Type, Bits, Width; public uint[] Data = Array.Empty<uint>(); }

    private static int Div(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xFF00FF00u) + (b & 0xFF00FF00u), redBlue = (a & 0x00FF00FFu) + (b & 0x00FF00FFu);
        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }

    // ---- Entropy-coded image ----

    private sealed class Code
    {
        public int[] Table = Array.Empty<int>();     // (symbol << 4 | length), length 0 = slow path
        public int TableBits;
        public int Single = -1;                       // the only symbol of a zero-bit code
        public int[] Count = new int[16];
        public int[] First = new int[16];
        public int[] Offset = new int[16];
        public int[] Sorted = Array.Empty<int>();
    }

    private static uint[] Entropy(Bits bits, int width, int height, bool argb, Budget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int cacheBits = 0;
        if (bits.Read(1) == 1)
        {
            cacheBits = (int)bits.Read(4);
            if (cacheBits is < 1 or > 11) throw new UserError(WebPImage.Invalid);
        }
        int prefixBits = 0, prefixWidth = 0;
        uint[]? meta = null;
        int groupCount = 1;
        if (argb && bits.Read(1) == 1)
        {
            prefixBits = (int)bits.Read(3) + 2;
            prefixWidth = Div(width, prefixBits);
            meta = Entropy(bits, prefixWidth, Div(height, prefixBits), false, budget, token);
            for (int i = 0; i < meta.Length; i++) { meta[i] = meta[i] >> 8 & 0xFFFF; groupCount = Math.Max(groupCount, (int)meta[i] + 1); }
        }
        // Groups no block uses are still read (they are in the stream) but not kept.
        var used = new bool[groupCount];
        if (meta is null) used[0] = true; else foreach (uint m in meta) used[m] = true;
        int cacheSize = cacheBits == 0 ? 0 : 1 << cacheBits;
        int[] alphabets = { 256 + 24 + cacheSize, 256, 256, 256, 40 };
        var groups = new Code[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            if ((g & 63) == 0) token.ThrowIfCancellationRequested();
            var group = new Code[5];
            for (int c = 0; c < 5; c++) group[c] = ReadCode(bits, alphabets[c], used[g], budget);
            if (used[g]) groups[g] = group;
            if (bits.Overrun) throw new UserError(WebPImage.Invalid);
        }

        var cache = cacheBits == 0 ? null : new uint[cacheSize];
        int cacheShift = 32 - cacheBits;
        long total = (long)width * height;
        var pixels = new uint[total];
        long position = 0;
        int x = 0, y = 0;
        Code[] current = groups[0];
        int lastCached = 0;
        void Cache(long end)
        {
            if (cache is null) return;
            for (; lastCached < end; lastCached++) cache[(0x1E35A7BDu * pixels[lastCached]) >> cacheShift] = pixels[lastCached];
        }
        while (position < total)
        {
            if ((position & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            if (meta is not null && (x & ((1 << prefixBits) - 1)) == 0)
                current = groups[meta[(y >> prefixBits) * prefixWidth + (x >> prefixBits)]];
            int green = Decode(bits, current[0]);
            if (green < 256)
            {
                int red = Decode(bits, current[1]), blue = Decode(bits, current[2]), alpha = Decode(bits, current[3]);
                pixels[position] = (uint)(alpha << 24 | red << 16 | green << 8 | blue);
                position++;
                if (++x == width) { x = 0; y++; Cache(position); }
            }
            else if (green < 280)
            {
                int length = PrefixValue(bits, green - 256);
                int distanceSymbol = Decode(bits, current[4]);
                int distanceCode = PrefixValue(bits, distanceSymbol);
                long distance;
                if (distanceCode > 120) distance = distanceCode - 120;
                else
                {
                    distance = DistanceMap[(distanceCode - 1) * 2] + (long)DistanceMap[(distanceCode - 1) * 2 + 1] * width;
                    if (distance < 1) distance = 1;
                }
                if (distance > position || length > total - position) throw new UserError(WebPImage.Invalid);
                for (int i = 0; i < length; i++) pixels[position + i] = pixels[position + i - distance];
                position += length;
                x += length;
                while (x >= width) { x -= width; y++; }
                if (cache is not null) Cache(position);
                if (meta is not null && position < total) current = groups[meta[(y >> prefixBits) * prefixWidth + (x >> prefixBits)]];
            }
            else
            {
                int key = green - 280;
                if (cache is null || key >= cacheSize) throw new UserError(WebPImage.Invalid);
                Cache(position);
                pixels[position] = cache[key];
                position++;
                if (++x == width) { x = 0; y++; Cache(position); }
            }
            if (bits.Overrun) throw new UserError(WebPImage.Invalid);
        }
        return pixels;
    }

    private static int PrefixValue(Bits bits, int prefix)
    {
        if (prefix < 4) return prefix + 1;
        int extra = (prefix - 2) >> 1;
        int offset = (2 + (prefix & 1)) << extra;
        return offset + (int)bits.Read(extra) + 1;
    }

    private static Code ReadCode(Bits bits, int alphabet, bool keep, Budget budget)
    {
        var lengths = new int[alphabet];
        if (bits.Read(1) == 1)
        {
            int count = (int)bits.Read(1) + 1;
            int first = (int)bits.Read(bits.Read(1) == 1 ? 8 : 1);
            if (first >= alphabet) throw new UserError(WebPImage.Invalid);
            lengths[first] = 1;
            if (count == 2)
            {
                int second = (int)bits.Read(8);
                if (second >= alphabet) throw new UserError(WebPImage.Invalid);
                lengths[second] = 1;
            }
        }
        else
        {
            var codeLengthLengths = new int[19];
            int number = (int)bits.Read(4) + 4;
            for (int i = 0; i < number; i++) codeLengthLengths[CodeLengthOrder[i]] = (int)bits.Read(3);
            var lengthCode = Build(codeLengthLengths, new Budget(), true);
            int maxSymbol = alphabet;
            if (bits.Read(1) == 1)
            {
                int lengthBits = 2 + 2 * (int)bits.Read(3);
                maxSymbol = 2 + (int)bits.Read(lengthBits);
                if (maxSymbol > alphabet) throw new UserError(WebPImage.Invalid);
            }
            int symbol = 0, previous = 8;
            while (symbol < alphabet)
            {
                if (maxSymbol-- == 0) break;
                if (bits.Overrun) throw new UserError(WebPImage.Invalid);
                int value = Decode(bits, lengthCode);
                if (value < 16)
                {
                    lengths[symbol++] = value;
                    if (value != 0) previous = value;
                    continue;
                }
                int repeat = value == 16 ? 3 + (int)bits.Read(2) : value == 17 ? 3 + (int)bits.Read(3) : 11 + (int)bits.Read(7);
                if (symbol + repeat > alphabet) throw new UserError(WebPImage.Invalid);
                int fill = value == 16 ? previous : 0;
                for (int i = 0; i < repeat; i++) lengths[symbol++] = fill;
            }
        }
        // Each kept code also costs its own arrays, single-symbol codes included.
        if (keep && (budget.Entries += CodeOverhead) > MaxTableEntries) throw new UserError(WebPImage.TooComplex);
        return Build(lengths, keep ? budget : new Budget(), keep);
    }

    /// Builds canonical prefix-code decoding data from code lengths. The code must be complete,
    /// except that a single used symbol forms a code that consumes no bits.
    private static Code Build(int[] lengths, Budget budget, bool keep)
    {
        var code = new Code();
        int used = 0, last = 0, maxLength = 0;
        for (int s = 0; s < lengths.Length; s++)
            if (lengths[s] > 0) { used++; last = s; code.Count[lengths[s]]++; maxLength = Math.Max(maxLength, lengths[s]); }
        if (used == 0) throw new UserError(WebPImage.Invalid);
        if (used == 1) { code.Single = last; return code; }
        // Completeness: the Kraft sum of all lengths must be exactly one.
        long kraft = 0;
        for (int length = 1; length < 16; length++) kraft += (long)code.Count[length] << (15 - length);
        if (kraft != 1 << 15) throw new UserError(WebPImage.Invalid);
        if (!keep) return code;
        code.TableBits = Math.Min(maxLength, RootBits);
        budget.Entries += (1L << code.TableBits) + used;
        if (budget.Entries > MaxTableEntries) throw new UserError(WebPImage.TooComplex);
        int next = 0;
        for (int length = 1; length < 16; length++) { code.First[length] = next; code.Offset[length] = code.Offset[length - 1] + code.Count[length - 1]; next = (next + code.Count[length]) << 1; }
        code.Sorted = new int[used];
        var place = (int[])code.Offset.Clone();
        var codes = new int[16];
        Array.Copy(code.First, codes, 16);
        code.Table = new int[1 << code.TableBits];
        for (int s = 0; s < lengths.Length; s++)
        {
            int length = lengths[s];
            if (length == 0) continue;
            code.Sorted[place[length]++] = s;
            int canonical = codes[length]++;
            if (length > code.TableBits) continue;
            int reversed = Reverse(canonical, length);
            for (int index = reversed; index < code.Table.Length; index += 1 << length) code.Table[index] = s << 4 | length;
        }
        return code;
    }

    private static int Reverse(int value, int length)
    {
        int result = 0;
        for (int i = 0; i < length; i++) { result = result << 1 | (value & 1); value >>= 1; }
        return result;
    }

    private static int Decode(Bits bits, Code code)
    {
        if (code.Single >= 0) return code.Single;
        uint peek = bits.Peek();
        int entry = code.Table[peek & ((1u << code.TableBits) - 1)];
        if ((entry & 15) != 0) { bits.Skip(entry & 15); return entry >> 4; }
        // Longer than the table: walk the canonical code one bit at a time.
        int value = 0;
        for (int length = 1; length < 16; length++)
        {
            value = value << 1 | (int)(peek >> (length - 1) & 1);
            int index = value - code.First[length];
            if (index >= 0 && index < code.Count[length]) { bits.Skip(length); return code.Sorted[code.Offset[length] + index]; }
        }
        throw new UserError(WebPImage.Invalid);
    }

    // ---- Inverse transforms ----

    private static uint[] Inverse(Transform transform, uint[] pixels, int height, CancellationToken token)
    {
        int width = transform.Width;
        switch (transform.Type)
        {
            case 0: Predict(pixels, width, height, transform, token); return pixels;
            case 1:
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    int row = (y >> transform.Bits) * Div(width, transform.Bits);
                    for (int x = 0; x < width; x++)
                    {
                        uint element = transform.Data[row + (x >> transform.Bits)];
                        uint pixel = pixels[y * width + x];
                        int green = (int)(pixel >> 8 & 255);
                        int red = (int)(pixel >> 16 & 255) + Delta(element, green);
                        int blue = (int)(pixel & 255) + Delta(element >> 8, green) + Delta(element >> 16, red & 255);
                        pixels[y * width + x] = (pixel & 0xFF00FF00u) | (uint)(red & 255) << 16 | (uint)(blue & 255);
                    }
                }
                return pixels;
            case 2:
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                    uint green = pixels[i] >> 8 & 255;
                    pixels[i] = AddPixels(pixels[i], green << 16 | green);
                }
                return pixels;
            default:
                int perByte = 1 << transform.Bits, bitsPer = 8 >> transform.Bits, packed = Div(width, transform.Bits);
                var output = new uint[(long)width * height];
                uint[] table = transform.Data;
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        uint source = pixels[y * packed + (x >> transform.Bits)];
                        int index = (int)(source >> 8 & 255);
                        if (perByte > 1) index = index >> ((x & (perByte - 1)) * bitsPer) & ((1 << bitsPer) - 1);
                        output[y * width + x] = index < table.Length ? table[index] : 0u;
                    }
                }
                return output;
        }
    }

    private static int Delta(uint transform, int colour) => (sbyte)(byte)transform * (sbyte)(byte)colour >> 5;

    private static void Predict(uint[] pixels, int width, int height, Transform transform, CancellationToken token)
    {
        int blocks = Div(width, transform.Bits);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                uint prediction;
                if (y == 0) prediction = x == 0 ? 0xFF000000u : pixels[at - 1];
                else if (x == 0) prediction = pixels[at - width];
                else
                {
                    uint mode = transform.Data[(y >> transform.Bits) * blocks + (x >> transform.Bits)] >> 8 & 15;
                    uint left = pixels[at - 1], top = pixels[at - width], topLeft = pixels[at - width - 1], topRight = pixels[at - width + 1];
                    prediction = mode switch
                    {
                        0 => 0xFF000000u, 1 => left, 2 => top, 3 => topRight, 4 => topLeft,
                        5 => Average(Average(left, topRight), top), 6 => Average(left, topLeft), 7 => Average(left, top),
                        8 => Average(topLeft, top), 9 => Average(top, topRight),
                        10 => Average(Average(left, topLeft), Average(top, topRight)),
                        11 => Select(left, top, topLeft), 12 => ClampFull(left, top, topLeft),
                        13 => ClampHalf(Average(left, top), topLeft),
                        // 14 and 15 are not defined; like the reference decoder, they predict black.
                        _ => 0xFF000000u
                    };
                }
                pixels[at] = AddPixels(pixels[at], prediction);
            }
        }
    }

    private static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

    private static int Channel(uint pixel, int shift) => (int)(pixel >> shift & 255);

    private static uint Select(uint left, uint top, uint topLeft)
    {
        int distanceLeft = 0, distanceTop = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int estimate = Channel(left, shift) + Channel(top, shift) - Channel(topLeft, shift);
            distanceLeft += Math.Abs(estimate - Channel(left, shift));
            distanceTop += Math.Abs(estimate - Channel(top, shift));
        }
        return distanceLeft < distanceTop ? left : top;
    }

    private static uint ClampFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8) result |= (uint)Math.Clamp(Channel(a, shift) + Channel(b, shift) - Channel(c, shift), 0, 255) << shift;
        return result;
    }

    private static uint ClampHalf(uint a, uint b)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = Channel(a, shift);
            result |= (uint)Math.Clamp(ca + (ca - Channel(b, shift)) / 2, 0, 255) << shift;
        }
        return result;
    }

    /// Least-significant-bit-first reader. Reading past the end gives zero bits and marks Overrun,
    /// which the decoder turns into a refusal.
    private sealed class Bits
    {
        private readonly byte[] data;
        private long bitPosition;
        public Bits(byte[] data) => this.data = data;
        public bool Overrun => bitPosition > (long)data.Length * 8;
        public uint Peek()
        {
            long byteIndex = bitPosition >> 3;
            int shift = (int)(bitPosition & 7);
            ulong value = 0;
            for (int i = 0; i < 5; i++)
            {
                long index = byteIndex + i;
                if (index < data.Length) value |= (ulong)data[index] << (8 * i);
            }
            return (uint)(value >> shift);
        }
        public void Skip(int count) => bitPosition += count;
        public uint Read(int count)
        {
            if (count == 0) return 0;
            uint value = Peek() & (uint)((1L << count) - 1);
            bitPosition += count;
            return value;
        }
    }
}
