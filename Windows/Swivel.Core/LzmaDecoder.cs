using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

/// Malformed LZMA, LZMA2 or filter data. Callers turn it into a user-facing error.
public sealed class LzmaDataException : Exception
{
    public LzmaDataException() : base("The compressed data is damaged.") { }
}

/// Original LZMA decoder core (range decoder, literal/match/repeat model), written from the
/// published LZMA format description. Output goes to a circular window that is never larger
/// than min(dictionary, declared output), so memory is bounded before decoding starts.
/// Every distance is checked against what has really been produced since the last reset.
internal sealed class LzmaCore
{
    private const int States = 12, PosBitsMax = 4, EndPosModel = 14, FullDistances = 128, AlignBits = 4;
    private const uint Top = 1u << 24;

    private readonly byte[] window;
    private int windowPosition;
    private readonly uint dictionary;
    private long total; // bytes since the last dictionary reset
    private int lc, lp, pb;
    private ushort[] literal = Array.Empty<ushort>();
    private readonly ushort[] isMatch = new ushort[States << PosBitsMax], isRep = new ushort[States], isRepG0 = new ushort[States],
        isRepG1 = new ushort[States], isRepG2 = new ushort[States], isRep0Long = new ushort[States << PosBitsMax],
        posSlot = new ushort[4 << 6], specPos = new ushort[1 + FullDistances - EndPosModel], align = new ushort[1 << AlignBits],
        matchLength = new ushort[514], repLength = new ushort[514];
    private int state;
    private uint rep0, rep1, rep2, rep3;
    private int pending; // bytes of the current match not yet produced

    private Func<int> next = () => throw new LzmaDataException();
    private uint range, code;

    /// `windowBytes` is min(dictionary, output) and was checked against the memory limit by the caller.
    public LzmaCore(uint dictionary, int windowBytes)
    {
        this.dictionary = Math.Max(dictionary, 4096u);
        window = new byte[Math.Max(windowBytes, 1)];
    }

    public bool HasPendingMatch => pending > 0;
    public bool RangeFinished => code == 0;

    public void SetProperties(int properties)
    {
        if (properties is < 0 or >= 9 * 5 * 5) throw new LzmaDataException();
        int newLc = properties % 9, newLp = properties / 9 % 5, newPb = properties / 45;
        if (literal.Length != 0x300 << (newLc + newLp)) literal = new ushort[0x300 << (newLc + newLp)];
        lc = newLc; lp = newLp; pb = newPb;
    }

    public void ResetState()
    {
        foreach (var table in new[] { literal, isMatch, isRep, isRepG0, isRepG1, isRepG2, isRep0Long, posSlot, specPos, align, matchLength, repLength })
            Array.Fill(table, (ushort)1024);
        state = 0; rep0 = rep1 = rep2 = rep3 = 0; pending = 0;
    }

    public void ResetDictionary() { total = 0; windowPosition = 0; }

    /// Starts a new range-coded stream that reads its bytes through `source`.
    public void StartRange(Func<int> source)
    {
        next = source;
        if (next() != 0) throw new LzmaDataException();
        code = 0; range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++) code = (code << 8) | (uint)next();
        if (code == range) throw new LzmaDataException();
    }

    /// Bytes stored as they are (LZMA2 uncompressed chunks) still enter the window.
    public void PutRaw(byte value, byte[] output, int at) => Put(value, output, at);

    /// Produces exactly `count` bytes.
    public void Decode(byte[] output, int offset, int count)
    {
        int done = 0;
        while (pending > 0 && done < count) { Put(Back(rep0), output, offset + done); done++; pending--; }
        while (done < count)
        {
            int posState = (int)(total & ((1 << pb) - 1));
            if (Bit(isMatch, (state << PosBitsMax) + posState) == 0)
            {
                Literal(output, offset + done); done++;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }
            int length;
            if (Bit(isRep, state) != 0)
            {
                if (Bit(isRepG0, state) == 0)
                {
                    if (Bit(isRep0Long, (state << PosBitsMax) + posState) == 0)
                    {
                        CheckDistance(rep0);
                        state = state < 7 ? 9 : 11;
                        Put(Back(rep0), output, offset + done); done++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (Bit(isRepG1, state) == 0) distance = rep1;
                    else
                    {
                        if (Bit(isRepG2, state) == 0) distance = rep2;
                        else { distance = rep3; rep3 = rep2; }
                        rep2 = rep1;
                    }
                    rep1 = rep0; rep0 = distance;
                }
                length = Length(repLength, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2; rep2 = rep1; rep1 = rep0;
                length = Length(matchLength, posState);
                state = state < 7 ? 7 : 10;
                rep0 = Distance(length);
                // The end marker before the declared size means the data is shorter than promised.
                if (rep0 == 0xFFFFFFFF) throw new LzmaDataException();
            }
            CheckDistance(rep0);
            int left = length + 2;
            while (left > 0 && done < count) { Put(Back(rep0), output, offset + done); done++; left--; }
            pending = left;
        }
    }

    private void CheckDistance(uint distance)
    {
        if (distance >= total || distance >= dictionary) throw new LzmaDataException();
    }

    private void Literal(byte[] output, int at)
    {
        int previous = total > 0 ? Back(0) : 0;
        int litState = (int)((total & ((1 << lp) - 1)) << lc) + (previous >> (8 - lc));
        int baseIndex = litState * 0x300, symbol = 1;
        if (state >= 7)
        {
            int match = Back(rep0);
            do
            {
                int matchBit = (match >> 7) & 1; match <<= 1;
                int bit = Bit(literal, baseIndex + ((1 + matchBit) << 8) + symbol);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit) break;
            } while (symbol < 0x100);
        }
        while (symbol < 0x100) symbol = (symbol << 1) | Bit(literal, baseIndex + symbol);
        Put((byte)symbol, output, at);
    }

    private int Length(ushort[] table, int posState)
    {
        if (Bit(table, 0) == 0) return Tree(table, 2 + (posState << 3), 3);
        if (Bit(table, 1) == 0) return 8 + Tree(table, 130 + (posState << 3), 3);
        return 16 + Tree(table, 258, 8);
    }

    private uint Distance(int length)
    {
        int slot = Tree(posSlot, Math.Min(length, 3) << 6, 6);
        if (slot < 4) return (uint)slot;
        int direct = (slot >> 1) - 1;
        uint distance = (uint)(2 | (slot & 1)) << direct;
        if (slot < EndPosModel) return distance + (uint)Reverse(specPos, (int)distance - slot, direct);
        distance += Direct(direct - AlignBits) << AlignBits;
        return distance + (uint)Reverse(align, 0, AlignBits);
    }

    private byte Back(uint distance)
    {
        int at = windowPosition - (int)distance - 1;
        if (at < 0) at += window.Length;
        return window[at];
    }

    private void Put(byte value, byte[] output, int at)
    {
        window[windowPosition] = value;
        if (++windowPosition == window.Length) windowPosition = 0;
        total++;
        output[at] = value;
    }

    private int Bit(ushort[] probabilities, int index)
    {
        uint p = probabilities[index], bound = (range >> 11) * p;
        int bit;
        if (code < bound) { range = bound; probabilities[index] = (ushort)(p + ((2048 - p) >> 5)); bit = 0; }
        else { range -= bound; code -= bound; probabilities[index] = (ushort)(p - (p >> 5)); bit = 1; }
        if (range < Top) { range <<= 8; code = (code << 8) | (uint)next(); }
        return bit;
    }

    private uint Direct(int count)
    {
        uint result = 0;
        for (; count > 0; count--)
        {
            range >>= 1; code -= range;
            uint t = 0 - (code >> 31);
            code += range & t;
            if (code == range) throw new LzmaDataException();
            if (range < Top) { range <<= 8; code = (code << 8) | (uint)next(); }
            result = (result << 1) + (t + 1);
        }
        return result;
    }

    private int Tree(ushort[] table, int offset, int bits)
    {
        int m = 1;
        for (int i = 0; i < bits; i++) m = (m << 1) + Bit(table, offset + m);
        return m - (1 << bits);
    }

    private int Reverse(ushort[] table, int offset, int bits)
    {
        int m = 1, symbol = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = Bit(table, offset + m);
            m = (m << 1) + bit; symbol |= bit << i;
        }
        return symbol;
    }
}

/// A read-only stream that produces exactly `outputSize` bytes and then ends.
public abstract class DecodedStream : Stream
{
    private readonly CancellationToken token;
    protected long Left { get; private set; }
    protected DecodedStream(long outputSize, CancellationToken token) { Left = outputSize; this.token = token; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        if (Left == 0 || count == 0) return 0;
        int produced = Produce(buffer, offset, (int)Math.Min(Math.Min(count, Left), 65536));
        Left -= produced;
        if (Left == 0) Finish();
        return produced;
    }
    protected abstract int Produce(byte[] buffer, int offset, int count);
    /// Called once all declared output was produced; checks the stream ends correctly.
    protected virtual void Finish() { }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected static Func<int> ByteSource(Stream input)
    {
        var buffer = new byte[65536]; int at = 0, count = 0;
        return () =>
        {
            if (at == count)
            {
                count = input.Read(buffer, 0, buffer.Length); at = 0;
                if (count == 0) throw new LzmaDataException();
            }
            return buffer[at++];
        };
    }
}

/// LZMA as stored in 7z: 5 property bytes (lc/lp/pb, dictionary) and a known output size.
/// An end marker after the declared size is allowed and not read.
public sealed class LzmaStream : DecodedStream
{
    private readonly LzmaCore core;
    public LzmaStream(Stream input, ReadOnlySpan<byte> properties, long outputSize, int maxWindow, CancellationToken token) : base(outputSize, token)
    {
        if (properties.Length != 5) throw new LzmaDataException();
        uint dictionary = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(properties[1..]);
        core = new LzmaCore(dictionary, WindowFor(dictionary, outputSize, maxWindow));
        core.SetProperties(properties[0]); core.ResetState(); core.ResetDictionary();
        if (outputSize > 0) core.StartRange(ByteSource(input));
    }
    internal static int WindowFor(uint dictionary, long outputSize, int maxWindow)
    {
        long window = Math.Min(Math.Max(dictionary, 4096u), outputSize);
        if (window > maxWindow) throw new UserError("This 7z archive needs more than 256 MB of decoder memory and was not extracted.");
        return (int)window;
    }
    protected override int Produce(byte[] buffer, int offset, int count) { core.Decode(buffer, offset, count); return count; }
    protected override void Finish() { if (core.HasPendingMatch) throw new LzmaDataException(); }
}

/// LZMA2: chunks of LZMA or stored data, each with its own sizes and reset level, ending
/// with a 0x00 control byte. Chunk rules follow the format: the first chunk resets the
/// dictionary, the first LZMA chunk after a dictionary reset sets new properties, every
/// LZMA chunk consumes exactly its packed size and ends with a finished range coder.
public sealed class Lzma2Stream : DecodedStream
{
    private readonly LzmaCore core;
    private readonly Func<int> input;
    private int chunkLeft, packedLeft, needLevel = 0xE0;
    private bool lzmaChunk, inRange;

    public Lzma2Stream(Stream input, ReadOnlySpan<byte> properties, long outputSize, int maxWindow, CancellationToken token) : base(outputSize, token)
    {
        if (properties.Length != 1 || properties[0] > 40) throw new LzmaDataException();
        uint dictionary = Dictionary(properties[0]);
        core = new LzmaCore(dictionary, LzmaStream.WindowFor(dictionary, outputSize, maxWindow));
        this.input = ByteSource(input);
    }

    internal static uint Dictionary(byte property) => property == 40 ? 0xFFFFFFFF : (uint)(2 | (property & 1)) << (property / 2 + 11);

    protected override int Produce(byte[] buffer, int offset, int count)
    {
        if (chunkLeft == 0) NextChunk();
        int n = Math.Min(count, chunkLeft);
        if (lzmaChunk) core.Decode(buffer, offset, n);
        else for (int i = 0; i < n; i++) core.PutRaw((byte)input(), buffer, offset + i);
        chunkLeft -= n;
        if (chunkLeft == 0 && lzmaChunk) EndLzmaChunk();
        return n;
    }

    private void EndLzmaChunk()
    {
        if (packedLeft != 0 || !core.RangeFinished || core.HasPendingMatch) throw new LzmaDataException();
        inRange = false;
    }

    private void NextChunk()
    {
        int control = input();
        if (control == 0) throw new LzmaDataException(); // ended before the declared size
        if (control < 0x80)
        {
            if (control == 1) { needLevel = 0xC0; core.ResetDictionary(); }
            else if (control != 2 || needLevel == 0xE0) throw new LzmaDataException();
            chunkLeft = (input() << 8 | input()) + 1;
            lzmaChunk = false;
            return;
        }
        if (control < needLevel) throw new LzmaDataException();
        needLevel = 0;
        chunkLeft = ((control & 0x1F) << 16 | input() << 8 | input()) + 1;
        packedLeft = (input() << 8 | input()) + 1;
        int reset = (control >> 5) & 3;
        if (reset == 3) core.ResetDictionary();
        if (reset >= 2)
        {
            int properties = input();
            if (properties >= 9 * 5 * 5 || properties % 9 + properties / 9 % 5 > 4) throw new LzmaDataException();
            core.SetProperties(properties);
        }
        if (reset >= 1) core.ResetState();
        else if (core.HasPendingMatch) throw new LzmaDataException();
        lzmaChunk = true; inRange = true;
        core.StartRange(() => packedLeft-- > 0 ? input() : throw new LzmaDataException());
    }

    protected override void Finish()
    {
        if (chunkLeft != 0 || inRange || input() != 0) throw new LzmaDataException();
    }
}

/// The x86 branch converter (BCJ) in the decoding direction: relative CALL/JMP targets that
/// the encoder made absolute are made relative again. Output size equals input size; the last
/// four bytes of the stream are passed through unchanged, as the format requires.
public sealed class BcjX86Stream : DecodedStream
{
    private static readonly bool[] Allowed = { true, true, true, false, true, false, false, false };
    private static readonly int[] BitNumber = { 0, 1, 2, 2, 3, 3, 3, 3 };
    private readonly Stream inner;
    private readonly byte[] buffer = new byte[65536];
    private int start, converted, filled;
    private uint position, mask;
    private bool innerEnded;

    public BcjX86Stream(Stream inner, long outputSize, CancellationToken token) : base(outputSize, token) { this.inner = inner; }

    protected override int Produce(byte[] output, int offset, int count)
    {
        while (start == converted)
        {
            Array.Copy(buffer, converted, buffer, 0, filled - converted);
            filled -= converted; start = converted = 0;
            while (!innerEnded && filled < buffer.Length)
            {
                int read = inner.Read(buffer, filled, buffer.Length - filled);
                if (read == 0) innerEnded = true; else filled += read;
            }
            if (filled == 0) throw new LzmaDataException();
            if (innerEnded) { Convert(buffer, filled); converted = filled; } // the last bytes stay as stored
            else { converted = Convert(buffer, filled); position += (uint)converted; }
        }
        int n = Math.Min(count, converted - start);
        Array.Copy(buffer, start, output, offset, n);
        start += n;
        return n;
    }

    // Returns how many leading bytes are final; up to four may wait for more data.
    private int Convert(byte[] data, int size)
    {
        if (size < 5) return 0;
        uint ip = position + 5;
        long bufferPos = 0, previous = -1;
        uint previousMask = mask & 7;
        for (;;)
        {
            long p = bufferPos, limit = size - 4;
            while (p < limit && (data[p] & 0xFE) != 0xE8) p++;
            bufferPos = p;
            if (p >= limit) break;
            long gap = bufferPos - previous;
            if (gap > 3) previousMask = 0;
            else
            {
                previousMask = (previousMask << (int)(gap - 1)) & 7;
                if (previousMask != 0)
                {
                    byte b = data[p + 4 - BitNumber[previousMask]];
                    if (!Allowed[previousMask] || b is 0 or 0xFF)
                    {
                        previous = bufferPos;
                        previousMask = ((previousMask << 1) & 7) | 1;
                        bufferPos++;
                        continue;
                    }
                }
            }
            previous = bufferPos;
            if (data[p + 4] is 0 or 0xFF)
            {
                uint source = (uint)data[p + 4] << 24 | (uint)data[p + 3] << 16 | (uint)data[p + 2] << 8 | data[p + 1], destination;
                for (;;)
                {
                    destination = source - (ip + (uint)bufferPos);
                    if (previousMask == 0) break;
                    int index = BitNumber[previousMask] * 8;
                    byte b = (byte)(destination >> (24 - index));
                    if (b is not (0 or 0xFF)) break;
                    source = destination ^ ((1u << (32 - index)) - 1);
                }
                data[p + 4] = (byte)~(((destination >> 24) & 1) - 1);
                data[p + 3] = (byte)(destination >> 16);
                data[p + 2] = (byte)(destination >> 8);
                data[p + 1] = (byte)destination;
                bufferPos += 5;
            }
            else
            {
                previousMask = ((previousMask << 1) & 7) | 1;
                bufferPos++;
            }
        }
        long since = bufferPos - previous;
        mask = since > 3 ? 0 : (previousMask << (int)(since - 1)) & 7;
        return (int)bufferPos;
    }
}
