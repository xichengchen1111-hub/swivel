using System;
using System.Buffers.Binary;
using System.Threading;
using static Swivel.Core.WebPLossyTables;

namespace Swivel.Core;

/// An original bounded decoder for one VP8 key frame, the lossy picture inside a WebP 'VP8 '
/// chunk, written from RFC 6386 (boolean decoder, frame header, intra prediction, coefficient
/// tokens, dequantisation, inverse WHT/DCT and loop filter). Interframes are not part of WebP
/// and are refused. The decoded Y'CbCr 4:2:0 planes are turned into colour with bilinear chroma
/// upsampling and BT.601 limited-range coefficients.
internal static class WebPLossy
{
    private static readonly byte[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7 };
    private static readonly byte[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static readonly sbyte[] BModeTree = { 0, 2, -1, 4, -2, 6, 8, 12, -3, 10, -5, -6, -4, 14, -7, 16, -8, -9 };
    private static readonly byte[][] Categories =
    {
        new byte[] { 159 }, new byte[] { 165, 145 }, new byte[] { 173, 148, 140 }, new byte[] { 176, 155, 140, 135 },
        new byte[] { 180, 157, 141, 134, 130 }, new byte[] { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 }
    };
    private static readonly int[] CategoryBase = { 5, 7, 11, 19, 35, 67 };
    private const int Dc = 0, V = 1, H = 2, Tm = 3, BPred = 4;

    /// The boolean entropy decoder of RFC 6386 section 7. Past the end it reads zero bytes and
    /// counts them; more than the two bytes it keeps buffered means the data ran out.
    private sealed class Bool
    {
        private readonly byte[] data; private readonly int end; private int position;
        private uint value, range = 255; private int bitCount;
        public int Overrun;
        public Bool(byte[] data, int start, int end)
        {
            this.data = data; this.end = end; position = start;
            value = (uint)(Next() << 8 | Next());
        }
        private int Next()
        {
            if (position < end) return data[position++];
            Overrun++; return 0;
        }
        public bool Exhausted => Overrun > 2;
        public bool Read(int probability)
        {
            uint split = 1 + (((range - 1) * (uint)probability) >> 8), big = split << 8;
            bool bit;
            if (value >= big) { bit = true; range -= split; value -= big; }
            else { bit = false; range = split; }
            while (range < 128)
            {
                value <<= 1; range <<= 1;
                if (++bitCount == 8) { bitCount = 0; value |= (uint)Next(); }
            }
            return bit;
        }
        public int Literal(int bits)
        {
            int v = 0;
            for (int i = 0; i < bits; i++) v = v << 1 | (Read(128) ? 1 : 0);
            return v;
        }
        public int Signed(int bits)
        {
            int v = Literal(bits);
            return Read(128) ? -v : v;
        }
        public int Optional(int bits) => Read(128) ? Signed(bits) : 0;
    }

    private struct Quant { public int YDc, YAc, Y2Dc, Y2Ac, UvDc, UvAc; }

    public static PixelImage Read(ReadOnlySpan<byte> chunk, CancellationToken token)
    {
        var planes = Planes(chunk, token);
        return ToColour(planes.Y, planes.U, planes.V, planes.YStride, planes.UvStride, planes.Width, planes.Height, token);
    }

    internal readonly record struct Decoded(byte[] Y, byte[] U, byte[] V, int YStride, int UvStride, int Width, int Height);

    /// The reconstructed, loop-filtered Y'CbCr planes (macroblock-padded).
    internal static Decoded Planes(ReadOnlySpan<byte> chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (chunk.Length < 10) throw new UserError(WebPImage.Invalid);
        int tag = chunk[0] | chunk[1] << 8 | chunk[2] << 16;
        // Bit 0 clear is a key frame; WebP holds exactly one shown key frame.
        if ((tag & 1) != 0 || (tag >> 1 & 7) > 3 || (tag >> 4 & 1) == 0) throw new UserError(WebPImage.Invalid);
        int firstSize = tag >> 5;
        if (chunk[3] != 0x9D || chunk[4] != 0x01 || chunk[5] != 0x2A) throw new UserError(WebPImage.Invalid);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(chunk[6..]) & 0x3FFF, height = BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]) & 0x3FFF;
        ImageRules.Validate(width, height);
        byte[] data = chunk.ToArray();
        if (firstSize > data.Length - 10) throw new UserError(WebPImage.Invalid);
        var d = new Bool(data, 10, 10 + firstSize);

        // ---- Frame header (RFC 6386 sections 9.2 to 9.11, key frame) ----
        d.Literal(1); d.Literal(1);   // colour space and clamping type: results are always clamped
        bool segmentation = d.Read(128), updateMap = false, absolute = false;
        var segmentQuant = new int[4]; var segmentFilter = new int[4];
        var segmentProbs = new byte[] { 255, 255, 255 };
        if (segmentation)
        {
            updateMap = d.Read(128);
            if (d.Read(128))
            {
                absolute = d.Read(128);
                for (int i = 0; i < 4; i++) segmentQuant[i] = d.Optional(7);
                for (int i = 0; i < 4; i++) segmentFilter[i] = d.Optional(6);
            }
            if (updateMap) for (int i = 0; i < 3; i++) segmentProbs[i] = d.Read(128) ? (byte)d.Literal(8) : (byte)255;
        }
        bool simple = d.Read(128);
        int filterLevel = d.Literal(6), sharpness = d.Literal(3);
        var refDelta = new int[4]; var modeDelta = new int[4];
        bool deltas = d.Read(128);
        if (deltas && d.Read(128))
        {
            for (int i = 0; i < 4; i++) refDelta[i] = d.Optional(6);
            for (int i = 0; i < 4; i++) modeDelta[i] = d.Optional(6);
        }
        int partitionCount = 1 << d.Literal(2);
        int baseQ = d.Literal(7);
        int yDcDelta = d.Optional(4), y2DcDelta = d.Optional(4), y2AcDelta = d.Optional(4), uvDcDelta = d.Optional(4), uvAcDelta = d.Optional(4);
        d.Read(128);   // refresh entropy probabilities: meaningless for a single frame
        var probs = (byte[])DefaultCoeffProbs.Clone();
        for (int i = 0; i < probs.Length; i++) if (d.Read(CoeffUpdateProbs[i])) probs[i] = (byte)d.Literal(8);
        bool useSkip = d.Read(128);
        int skipProb = useSkip ? d.Literal(8) : 0;
        if (d.Exhausted) throw new UserError(WebPImage.Invalid);

        var quants = new Quant[4];
        for (int s = 0; s < 4; s++)
        {
            int q = segmentation ? absolute ? segmentQuant[s] : baseQ + segmentQuant[s] : baseQ;
            static int DcQ(int q) => DcTable[Math.Clamp(q, 0, 127)];
            static int AcQ(int q) => AcTable[Math.Clamp(q, 0, 127)];
            quants[s] = new Quant
            {
                YDc = DcQ(q + yDcDelta), YAc = AcQ(q), Y2Dc = DcQ(q + y2DcDelta) * 2,
                Y2Ac = Math.Max(8, AcQ(q + y2AcDelta) * 155 / 100), UvDc = Math.Min(132, DcQ(q + uvDcDelta)), UvAc = AcQ(q + uvAcDelta)
            };
        }

        // ---- Token partitions (section 9.5) ----
        int partitionsAt = 10 + firstSize, sizesLength = 3 * (partitionCount - 1);
        if (data.Length - partitionsAt < sizesLength) throw new UserError(WebPImage.Invalid);
        var parts = new Bool[partitionCount];
        int partStart = partitionsAt + sizesLength;
        for (int p = 0; p < partitionCount; p++)
        {
            int size = p < partitionCount - 1 ? data[partitionsAt + 3 * p] | data[partitionsAt + 3 * p + 1] << 8 | data[partitionsAt + 3 * p + 2] << 16 : data.Length - partStart;
            if (size < 0 || size > data.Length - partStart) throw new UserError(WebPImage.Invalid);
            parts[p] = new Bool(data, partStart, partStart + size);
            partStart += size;
        }

        int mbWidth = (width + 15) >> 4, mbHeight = (height + 15) >> 4;
        int yStride = mbWidth * 16, uvStride = mbWidth * 8;
        var yPlane = new byte[(long)yStride * mbHeight * 16];
        var uPlane = new byte[(long)uvStride * mbHeight * 8];
        var vPlane = new byte[uPlane.Length];
        var mbLevel = new byte[mbWidth * mbHeight];
        var mbInner = new bool[mbWidth * mbHeight];

        var aboveModes = new byte[mbWidth * 4];
        var aboveNz = new byte[mbWidth * 9];   // 4 Y, 2 U, 2 V, 1 Y2 per macroblock column
        var leftModes = new byte[4];
        var leftNz = new byte[9];
        var coefficients = new short[25 * 16];
        var modes = new byte[16];
        var yWork = new byte[17 * 32];
        var uWork = new byte[9 * 16];
        var vWork = new byte[9 * 16];

        for (int mbY = 0; mbY < mbHeight; mbY++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(leftModes); Array.Clear(leftNz);
            var tokens = parts[mbY & (partitionCount - 1)];
            for (int mbX = 0; mbX < mbWidth; mbX++)
            {
                // ---- Macroblock header (section 19.3) ----
                int segment = 0;
                if (updateMap) segment = !d.Read(segmentProbs[0]) ? (d.Read(segmentProbs[1]) ? 1 : 0) : (d.Read(segmentProbs[2]) ? 3 : 2);
                bool skip = useSkip && d.Read(skipProb);
                int yMode;
                if (!d.Read(145)) yMode = BPred;
                else if (!d.Read(156)) yMode = d.Read(163) ? V : Dc;
                else yMode = d.Read(128) ? Tm : H;
                if (yMode == BPred)
                {
                    for (int b = 0; b < 16; b++)
                    {
                        int above = b < 4 ? aboveModes[mbX * 4 + b] : modes[b - 4];
                        int left = (b & 3) == 0 ? leftModes[b >> 2] : modes[b - 1];
                        int offset = (above * 10 + left) * 9, node = 0;
                        do node = BModeTree[node + (d.Read(KfBmodeProbs[offset + (node >> 1)]) ? 1 : 0)]; while (node > 0);
                        modes[b] = (byte)-node;
                    }
                }
                else
                {
                    // The implied subblock mode for contexts: DC, V, H, TM → B_DC, B_VE, B_HE, B_TM.
                    byte implied = yMode switch { V => 2, H => 3, Tm => 1, _ => 0 };
                    Array.Fill(modes, implied);
                }
                for (int i = 0; i < 4; i++) { aboveModes[mbX * 4 + i] = modes[12 + i]; leftModes[i] = modes[i * 4 + 3]; }
                int uvMode = !d.Read(142) ? Dc : !d.Read(114) ? V : d.Read(183) ? Tm : H;

                // ---- Residual tokens (section 13) ----
                Array.Clear(coefficients);
                var q = quants[segmentation ? segment : 0];
                int a = mbX * 9;
                bool anyCoefficient = false;
                if (!skip)
                {
                    int firstY = 0, yType = 3;
                    if (yMode != BPred)
                    {
                        bool nz = Block(tokens, probs, 1, aboveNz[a + 8] + leftNz[8], 0, coefficients, 384, q.Y2Dc, q.Y2Ac);
                        aboveNz[a + 8] = leftNz[8] = (byte)(nz ? 1 : 0);
                        InverseWht(coefficients);
                        firstY = 1; yType = 0;
                    }
                    for (int b = 0; b < 16; b++)
                    {
                        int column = b & 3, row = b >> 2;
                        bool nz = Block(tokens, probs, yType, aboveNz[a + column] + leftNz[row], firstY, coefficients, b * 16, q.YDc, q.YAc);
                        aboveNz[a + column] = leftNz[row] = (byte)(nz ? 1 : 0);
                    }
                    for (int b = 0; b < 8; b++)
                    {
                        int plane = b >> 2, column = b & 1, row = b >> 1 & 1;
                        int above = a + 4 + plane * 2 + column, left = 4 + plane * 2 + row;
                        bool nz = Block(tokens, probs, 2, aboveNz[above] + leftNz[left], 0, coefficients, 256 + b * 16, q.UvDc, q.UvAc);
                        aboveNz[above] = leftNz[left] = (byte)(nz ? 1 : 0);
                    }
                    for (int i = 0; i < 384 && !anyCoefficient; i++) anyCoefficient = coefficients[i] != 0;
                }
                else
                {
                    // A skipped macroblock leaves its blocks empty; the Y2 context only changes
                    // when the macroblock has a Y2 block.
                    for (int i = 0; i < 8; i++) aboveNz[a + i] = leftNz[i] = 0;
                    if (yMode != BPred) aboveNz[a + 8] = leftNz[8] = 0;
                }
                if (tokens.Exhausted || d.Exhausted) throw new UserError(WebPImage.Invalid);

                // ---- Reconstruction (sections 12 and 14) ----
                Reconstruct(yPlane, yStride, mbX, mbY, mbWidth, yMode, modes, coefficients, yWork);
                Chroma(uPlane, uvStride, mbX, mbY, uvMode, coefficients, 256, uWork);
                Chroma(vPlane, uvStride, mbX, mbY, uvMode, coefficients, 320, vWork);

                // ---- Loop-filter parameters for this macroblock (sections 9.3, 9.6, 15) ----
                int level = segmentation ? absolute ? segmentFilter[segment] : filterLevel + segmentFilter[segment] : filterLevel;
                if (deltas) { level += refDelta[0]; if (yMode == BPred) level += modeDelta[0]; }
                int index = mbY * mbWidth + mbX;
                mbLevel[index] = (byte)Math.Clamp(level, 0, 63);
                mbInner[index] = yMode == BPred || anyCoefficient;
            }
        }
        if (filterLevel != 0)
            LoopFilter(yPlane, uPlane, vPlane, yStride, uvStride, mbWidth, mbHeight, mbLevel, mbInner, simple, sharpness, token);
        return new Decoded(yPlane, uPlane, vPlane, yStride, uvStride, width, height);
    }

    /// One block's tokens (section 13). Returns whether the block was not empty (its first token
    /// was not end-of-block), which is the context for the neighbouring blocks.
    private static bool Block(Bool d, byte[] probs, int type, int context, int first, short[] output, int at, int dcFactor, int acFactor)
    {
        int i = first;
        int Base(int position, int ctx) => ((type * 8 + Bands[position]) * 3 + ctx) * 11;
        int p = Base(i, context);
        if (!d.Read(probs[p])) return false;
        while (true)
        {
            if (!d.Read(probs[p + 1]))
            {
                // DCT_0: the next token cannot be end-of-block.
                if (++i == 16) return true;
                p = Base(i, 0);
                continue;
            }
            int value;
            if (!d.Read(probs[p + 2])) value = 1;
            else if (!d.Read(probs[p + 3])) value = !d.Read(probs[p + 4]) ? 2 : d.Read(probs[p + 5]) ? 4 : 3;
            else
            {
                int category = !d.Read(probs[p + 6]) ? (d.Read(probs[p + 7]) ? 1 : 0)
                    : !d.Read(probs[p + 8]) ? (d.Read(probs[p + 9]) ? 3 : 2) : (d.Read(probs[p + 10]) ? 5 : 4);
                int extra = 0;
                foreach (byte probability in Categories[category]) extra = extra << 1 | (d.Read(probability) ? 1 : 0);
                value = CategoryBase[category] + extra;
            }
            if (d.Read(128)) value = -value;
            output[at + Zigzag[i]] = (short)(value * (i > 0 ? acFactor : dcFactor));
            int next = Math.Abs(value) == 1 ? 1 : 2;
            if (++i == 16) return true;
            p = Base(i, next);
            if (!d.Read(probs[p])) return true;
        }
    }

    /// The inverse Walsh-Hadamard transform of the Y2 block (section 14.3); each result becomes the
    /// DC coefficient of one Y block.
    private static void InverseWht(short[] c)
    {
        var temp = new int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = c[384 + i] + c[384 + 12 + i], b1 = c[384 + 4 + i] + c[384 + 8 + i];
            int c1 = c[384 + 4 + i] - c[384 + 8 + i], d1 = c[384 + i] - c[384 + 12 + i];
            temp[i] = a1 + b1; temp[4 + i] = c1 + d1; temp[8 + i] = a1 - b1; temp[12 + i] = d1 - c1;
        }
        for (int i = 0; i < 4; i++)
        {
            int a1 = temp[i * 4] + temp[i * 4 + 3], b1 = temp[i * 4 + 1] + temp[i * 4 + 2];
            int c1 = temp[i * 4 + 1] - temp[i * 4 + 2], d1 = temp[i * 4] - temp[i * 4 + 3];
            c[(i * 4 + 0) * 16] = (short)((a1 + b1 + 3) >> 3);
            c[(i * 4 + 1) * 16] = (short)((c1 + d1 + 3) >> 3);
            c[(i * 4 + 2) * 16] = (short)((a1 - b1 + 3) >> 3);
            c[(i * 4 + 3) * 16] = (short)((d1 - c1 + 3) >> 3);
        }
    }

    /// Inverse DCT (section 14.4) of one block, added to the prediction in place with clamping.
    private static void AddIdct(short[] c, int at, byte[] work, int offset, int stride)
    {
        bool empty = true;
        for (int i = 0; i < 16 && empty; i++) empty = c[at + i] == 0;
        if (empty) return;
        const int Cos = 20091, Sin = 35468;
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int i0 = c[at + i], i4 = c[at + 4 + i], i8 = c[at + 8 + i], i12 = c[at + 12 + i];
            int a1 = i0 + i8, b1 = i0 - i8;
            int c1 = (i4 * Sin >> 16) - (i12 + (i12 * Cos >> 16));
            int d1 = i4 + (i4 * Cos >> 16) + (i12 * Sin >> 16);
            temp[i] = a1 + d1; temp[12 + i] = a1 - d1; temp[4 + i] = b1 + c1; temp[8 + i] = b1 - c1;
        }
        for (int i = 0; i < 4; i++)
        {
            int r0 = temp[i * 4], r1 = temp[i * 4 + 1], r2 = temp[i * 4 + 2], r3 = temp[i * 4 + 3];
            int a1 = r0 + r2, b1 = r0 - r2;
            int c1 = (r1 * Sin >> 16) - (r3 + (r3 * Cos >> 16));
            int d1 = r1 + (r1 * Cos >> 16) + (r3 * Sin >> 16);
            int row = offset + i * stride;
            work[row] = Clip(work[row] + ((a1 + d1 + 4) >> 3));
            work[row + 3] = Clip(work[row + 3] + ((a1 - d1 + 4) >> 3));
            work[row + 1] = Clip(work[row + 1] + ((b1 + c1 + 4) >> 3));
            work[row + 2] = Clip(work[row + 2] + ((b1 - c1 + 4) >> 3));
        }
    }

    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // The luma work area is 17 rows of 32: row 0 is the row above the macroblock, column 0 the
    // column to its left; columns 17..20 of the above row hold the above-right pixels.
    private const int YS = 32;

    private static void Reconstruct(byte[] plane, int stride, int mbX, int mbY, int mbWidth, int yMode, byte[] modes, short[] c, byte[] w)
    {
        int x0 = mbX * 16, y0 = mbY * 16;
        // Edges: 127 above the frame, 129 left of it (RFC 6386 sections 12.2 and 12.3).
        w[0] = mbY == 0 ? (byte)127 : mbX == 0 ? (byte)129 : plane[(y0 - 1) * stride + x0 - 1];
        for (int i = 0; i < 16; i++) w[1 + i] = mbY == 0 ? (byte)127 : plane[(y0 - 1) * stride + x0 + i];
        for (int i = 0; i < 4; i++)
            w[17 + i] = mbY == 0 ? (byte)127 : mbX == mbWidth - 1 ? plane[(y0 - 1) * stride + x0 + 15] : plane[(y0 - 1) * stride + x0 + 16 + i];
        for (int r = 0; r < 16; r++) w[(r + 1) * YS] = mbX == 0 ? (byte)129 : plane[(y0 + r) * stride + x0 - 1];
        if (yMode == BPred)
        {
            // Right-edge subblocks below the first row use the macroblock's above-right pixels.
            for (int r = 4; r < 16; r += 4) for (int i = 0; i < 4; i++) w[r * YS + 17 + i] = w[17 + i];
            for (int b = 0; b < 16; b++)
            {
                int top = (b >> 2) * 4 + 1, left = (b & 3) * 4 + 1;
                PredictSubblock(w, top * YS + left, modes[b]);
                AddIdct(c, b * 16, w, top * YS + left, YS);
            }
        }
        else
        {
            Predict(w, YS, 16, yMode, mbX > 0, mbY > 0);
            for (int b = 0; b < 16; b++) AddIdct(c, b * 16, w, ((b >> 2) * 4 + 1) * YS + (b & 3) * 4 + 1, YS);
        }
        for (int r = 0; r < 16; r++) Buffer.BlockCopy(w, (r + 1) * YS + 1, plane, (y0 + r) * stride + x0, 16);
    }

    private static void Chroma(byte[] plane, int stride, int mbX, int mbY, int mode, short[] c, int at, byte[] w)
    {
        const int S = 16;
        int x0 = mbX * 8, y0 = mbY * 8;
        w[0] = mbY == 0 ? (byte)127 : mbX == 0 ? (byte)129 : plane[(y0 - 1) * stride + x0 - 1];
        for (int i = 0; i < 8; i++) w[1 + i] = mbY == 0 ? (byte)127 : plane[(y0 - 1) * stride + x0 + i];
        for (int r = 0; r < 8; r++) w[(r + 1) * S] = mbX == 0 ? (byte)129 : plane[(y0 + r) * stride + x0 - 1];
        Predict(w, S, 8, mode, mbX > 0, mbY > 0);
        for (int b = 0; b < 4; b++) AddIdct(c, at + b * 16, w, ((b >> 1) * 4 + 1) * S + (b & 1) * 4 + 1, S);
        for (int r = 0; r < 8; r++) Buffer.BlockCopy(w, (r + 1) * S + 1, plane, (y0 + r) * stride + x0, 8);
    }

    /// Whole-block prediction (DC, V, H, TM) into the work area whose block starts at (1, 1).
    private static void Predict(byte[] w, int stride, int size, int mode, bool hasLeft, bool hasTop)
    {
        int origin = stride + 1;
        switch (mode)
        {
            case Dc:
                int value, shift = size == 16 ? 4 : 3, sum = 0;
                if (hasTop) for (int i = 0; i < size; i++) sum += w[1 + i];
                if (hasLeft) for (int i = 0; i < size; i++) sum += w[(i + 1) * stride];
                if (hasTop && hasLeft) value = (sum + size) >> (shift + 1);
                else if (hasTop || hasLeft) value = (sum + (size >> 1)) >> shift;
                else value = 128;
                for (int r = 0; r < size; r++) w.AsSpan(origin + r * stride, size).Fill((byte)value);
                break;
            case V:
                for (int r = 0; r < size; r++) Buffer.BlockCopy(w, 1, w, origin + r * stride, size);
                break;
            case H:
                for (int r = 0; r < size; r++) w.AsSpan(origin + r * stride, size).Fill(w[(r + 1) * stride]);
                break;
            default:
                int corner = w[0];
                for (int r = 0; r < size; r++)
                {
                    int left = w[(r + 1) * stride] - corner;
                    for (int i = 0; i < size; i++) w[origin + r * stride + i] = Clip(left + w[1 + i]);
                }
                break;
        }
    }

    private static int Avg3(int x, int y, int z) => (x + y + y + z + 2) >> 2;
    private static int Avg2(int x, int y) => (x + y + 1) >> 1;

    /// The ten 4×4 luma subblock modes of RFC 6386 section 12.3; `at` is the subblock's top-left.
    private static void PredictSubblock(byte[] w, int at, int mode)
    {
        var A = new int[8];
        var L = new int[4];
        for (int i = 0; i < 8; i++) A[i] = w[at - YS + i];
        for (int i = 0; i < 4; i++) L[i] = w[at + i * YS - 1];
        int P = w[at - YS - 1];
        var E = new int[9];
        E[0] = L[3]; E[1] = L[2]; E[2] = L[1]; E[3] = L[0]; E[4] = P; E[5] = A[0]; E[6] = A[1]; E[7] = A[2]; E[8] = A[3];
        var B = new int[16];
        void Set(int r, int c, int v) => B[r * 4 + c] = v;
        int Ap(int i) => i < 0 ? P : A[i];
        int Lp(int i) => i < 0 ? P : L[i];
        switch (mode)
        {
            case 0:
                int dc = 4;
                for (int i = 0; i < 4; i++) dc += A[i] + L[i];
                Array.Fill(B, dc >> 3);
                break;
            case 1:
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) Set(r, c, Clip(L[r] + A[c] - P));
                break;
            case 2:
                for (int c = 0; c < 4; c++) { int v = Avg3(Ap(c - 1), A[c], A[c + 1]); for (int r = 0; r < 4; r++) Set(r, c, v); }
                break;
            case 3:
                for (int r = 0; r < 4; r++) { int v = r < 3 ? Avg3(Lp(r - 1), L[r], L[r + 1]) : Avg3(L[2], L[3], L[3]); for (int c = 0; c < 4; c++) Set(r, c, v); }
                break;
            case 4:   // LD
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                    Set(r, c, r + c == 6 ? Avg3(A[6], A[7], A[7]) : Avg3(A[r + c], A[r + c + 1], A[r + c + 2]));
                break;
            case 5:   // RD
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { int e = 4 - r + c; Set(r, c, Avg3(E[e - 1], E[e], E[e + 1])); }
                break;
            case 6:   // VR
                Set(3, 0, Avg3(E[1], E[2], E[3])); Set(2, 0, Avg3(E[2], E[3], E[4]));
                Set(3, 1, Avg3(E[3], E[4], E[5])); Set(1, 0, Avg3(E[3], E[4], E[5]));
                Set(2, 1, Avg2(E[4], E[5])); Set(0, 0, Avg2(E[4], E[5]));
                Set(3, 2, Avg3(E[4], E[5], E[6])); Set(1, 1, Avg3(E[4], E[5], E[6]));
                Set(2, 2, Avg2(E[5], E[6])); Set(0, 1, Avg2(E[5], E[6]));
                Set(3, 3, Avg3(E[5], E[6], E[7])); Set(1, 2, Avg3(E[5], E[6], E[7]));
                Set(2, 3, Avg2(E[6], E[7])); Set(0, 2, Avg2(E[6], E[7]));
                Set(1, 3, Avg3(E[6], E[7], E[8])); Set(0, 3, Avg2(E[7], E[8]));
                break;
            case 7:   // VL
                Set(0, 0, Avg2(A[0], A[1])); Set(1, 0, Avg3(A[0], A[1], A[2]));
                Set(2, 0, Avg2(A[1], A[2])); Set(0, 1, Avg2(A[1], A[2]));
                Set(1, 1, Avg3(A[1], A[2], A[3])); Set(3, 0, Avg3(A[1], A[2], A[3]));
                Set(2, 1, Avg2(A[2], A[3])); Set(0, 2, Avg2(A[2], A[3]));
                Set(3, 1, Avg3(A[2], A[3], A[4])); Set(1, 2, Avg3(A[2], A[3], A[4]));
                Set(2, 2, Avg2(A[3], A[4])); Set(0, 3, Avg2(A[3], A[4]));
                Set(3, 2, Avg3(A[3], A[4], A[5])); Set(1, 3, Avg3(A[3], A[4], A[5]));
                Set(2, 3, Avg3(A[4], A[5], A[6])); Set(3, 3, Avg3(A[5], A[6], A[7]));
                break;
            case 8:   // HD
                Set(3, 0, Avg2(E[0], E[1])); Set(3, 1, Avg3(E[0], E[1], E[2]));
                Set(2, 0, Avg2(E[1], E[2])); Set(3, 2, Avg2(E[1], E[2]));
                Set(2, 1, Avg3(E[1], E[2], E[3])); Set(3, 3, Avg3(E[1], E[2], E[3]));
                Set(2, 2, Avg2(E[2], E[3])); Set(1, 0, Avg2(E[2], E[3]));
                Set(2, 3, Avg3(E[2], E[3], E[4])); Set(1, 1, Avg3(E[2], E[3], E[4]));
                Set(1, 2, Avg2(E[3], E[4])); Set(0, 0, Avg2(E[3], E[4]));
                Set(1, 3, Avg3(E[3], E[4], E[5])); Set(0, 1, Avg3(E[3], E[4], E[5]));
                Set(0, 2, Avg3(E[4], E[5], E[6])); Set(0, 3, Avg3(E[5], E[6], E[7]));
                break;
            default:  // HU
                Set(0, 0, Avg2(L[0], L[1])); Set(0, 1, Avg3(L[0], L[1], L[2]));
                Set(0, 2, Avg2(L[1], L[2])); Set(1, 0, Avg2(L[1], L[2]));
                Set(0, 3, Avg3(L[1], L[2], L[3])); Set(1, 1, Avg3(L[1], L[2], L[3]));
                Set(1, 2, Avg2(L[2], L[3])); Set(2, 0, Avg2(L[2], L[3]));
                Set(1, 3, Avg3(L[2], L[3], L[3])); Set(2, 1, Avg3(L[2], L[3], L[3]));
                Set(2, 2, L[3]); Set(2, 3, L[3]); Set(3, 0, L[3]); Set(3, 1, L[3]); Set(3, 2, L[3]); Set(3, 3, L[3]);
                break;
        }
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) w[at + r * YS + c] = (byte)B[r * 4 + c];
    }

    // ---- Loop filter (section 15), run after the whole frame is reconstructed ----

    private static int S8(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    private static void LoopFilter(byte[] y, byte[] u, byte[] v, int yStride, int uvStride, int mbWidth, int mbHeight, byte[] levels, bool[] inner, bool simple, int sharpness, CancellationToken token)
    {
        for (int mbY = 0; mbY < mbHeight; mbY++)
        {
            token.ThrowIfCancellationRequested();
            for (int mbX = 0; mbX < mbWidth; mbX++)
            {
                int level = levels[mbY * mbWidth + mbX];
                if (level == 0) continue;
                int interior = level;
                if (sharpness > 0)
                {
                    interior >>= sharpness > 4 ? 2 : 1;
                    if (interior > 9 - sharpness) interior = 9 - sharpness;
                }
                if (interior < 1) interior = 1;
                int mbLimit = (level + 2) * 2 + interior, subLimit = level * 2 + interior;
                int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                bool filterInner = inner[mbY * mbWidth + mbX];
                int yAt = mbY * 16 * yStride + mbX * 16, uvAt = mbY * 8 * uvStride + mbX * 8;
                if (simple)
                {
                    if (mbX > 0) SimpleEdge(y, yAt, yStride, 1, 16, mbLimit);
                    if (filterInner) for (int i = 4; i < 16; i += 4) SimpleEdge(y, yAt + i, yStride, 1, 16, subLimit);
                    if (mbY > 0) SimpleEdge(y, yAt, 1, yStride, 16, mbLimit);
                    if (filterInner) for (int i = 4; i < 16; i += 4) SimpleEdge(y, yAt + i * yStride, 1, yStride, 16, subLimit);
                    continue;
                }
                if (mbX > 0)
                {
                    NormalEdge(y, yAt, yStride, 1, 16, mbLimit, interior, hev, true);
                    NormalEdge(u, uvAt, uvStride, 1, 8, mbLimit, interior, hev, true);
                    NormalEdge(v, uvAt, uvStride, 1, 8, mbLimit, interior, hev, true);
                }
                if (filterInner)
                {
                    for (int i = 4; i < 16; i += 4) NormalEdge(y, yAt + i, yStride, 1, 16, subLimit, interior, hev, false);
                    NormalEdge(u, uvAt + 4, uvStride, 1, 8, subLimit, interior, hev, false);
                    NormalEdge(v, uvAt + 4, uvStride, 1, 8, subLimit, interior, hev, false);
                }
                if (mbY > 0)
                {
                    NormalEdge(y, yAt, 1, yStride, 16, mbLimit, interior, hev, true);
                    NormalEdge(u, uvAt, 1, uvStride, 8, mbLimit, interior, hev, true);
                    NormalEdge(v, uvAt, 1, uvStride, 8, mbLimit, interior, hev, true);
                }
                if (filterInner)
                {
                    for (int i = 4; i < 16; i += 4) NormalEdge(y, yAt + i * yStride, 1, yStride, 16, subLimit, interior, hev, false);
                    NormalEdge(u, uvAt + 4 * uvStride, 1, uvStride, 8, subLimit, interior, hev, false);
                    NormalEdge(v, uvAt + 4 * uvStride, 1, uvStride, 8, subLimit, interior, hev, false);
                }
            }
        }
    }

    /// `at` is the first pixel after the edge; `along` steps along the edge, `across` across it.
    private static void SimpleEdge(byte[] p, int at, int along, int across, int length, int limit)
    {
        for (int i = 0; i < length; i++, at += along)
        {
            int p1 = p[at - 2 * across], p0 = p[at - across], q0 = p[at], q1 = p[at + across];
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 <= limit) CommonAdjust(p, at, across, true);
        }
    }

    private static int CommonAdjust(byte[] p, int at, int across, bool outer)
    {
        int p1 = p[at - 2 * across] - 128, p0 = p[at - across] - 128, q0 = p[at] - 128, q1 = p[at + across] - 128;
        int a = S8((outer ? S8(p1 - q1) : 0) + 3 * (q0 - p0));
        int b = S8(a + 3) >> 3;
        a = S8(a + 4) >> 3;
        p[at] = (byte)(S8(q0 - a) + 128);
        p[at - across] = (byte)(S8(p0 + b) + 128);
        return a;
    }

    private static void NormalEdge(byte[] p, int at, int along, int across, int length, int edgeLimit, int interior, int hevThreshold, bool macroblock)
    {
        for (int i = 0; i < length; i++, at += along)
        {
            int p3 = p[at - 4 * across], p2 = p[at - 3 * across], p1 = p[at - 2 * across], p0 = p[at - across];
            int q0 = p[at], q1 = p[at + across], q2 = p[at + 2 * across], q3 = p[at + 3 * across];
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edgeLimit
                || Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior || Math.Abs(p1 - p0) > interior
                || Math.Abs(q3 - q2) > interior || Math.Abs(q2 - q1) > interior || Math.Abs(q1 - q0) > interior) continue;
            bool hev = Math.Abs(p1 - p0) > hevThreshold || Math.Abs(q1 - q0) > hevThreshold;
            if (macroblock)
            {
                if (hev) { CommonAdjust(p, at, across, true); continue; }
                int sp2 = p2 - 128, sp1 = p1 - 128, sp0 = p0 - 128, sq0 = q0 - 128, sq1 = q1 - 128, sq2 = q2 - 128;
                int w = S8(S8(sp1 - sq1) + 3 * (sq0 - sp0));
                int a = S8((27 * w + 63) >> 7);
                p[at] = (byte)(S8(sq0 - a) + 128); p[at - across] = (byte)(S8(sp0 + a) + 128);
                a = S8((18 * w + 63) >> 7);
                p[at + across] = (byte)(S8(sq1 - a) + 128); p[at - 2 * across] = (byte)(S8(sp1 + a) + 128);
                a = S8((9 * w + 63) >> 7);
                p[at + 2 * across] = (byte)(S8(sq2 - a) + 128); p[at - 3 * across] = (byte)(S8(sp2 + a) + 128);
            }
            else
            {
                int a = (CommonAdjust(p, at, across, hev) + 1) >> 1;
                if (!hev)
                {
                    p[at + across] = (byte)(S8(q1 - 128 - a) + 128);
                    p[at - 2 * across] = (byte)(S8(p1 - 128 + a) + 128);
                }
            }
        }
    }

    // ---- Colour ----

    /// 4:2:0 chroma is upsampled bilinearly (weights 9, 3, 3, 1 around each luma sample, as the
    /// chroma samples sit between luma pairs) and converted with BT.601 limited-range
    /// coefficients in 14-bit fixed point. Alpha is opaque here; 'ALPH' replaces it later.
    private static PixelImage ToColour(byte[] yPlane, byte[] uPlane, byte[] vPlane, int yStride, int uvStride, int width, int height, CancellationToken token)
    {
        var bgra = new byte[(long)width * height * 4];
        int chromaWidth = (width + 1) >> 1, chromaHeight = (height + 1) >> 1;
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            // Nearest chroma row and the other row on the same side of this luma row.
            int near = y >> 1, far = (y & 1) == 0 ? Math.Max(near - 1, 0) : Math.Min(near + 1, chromaHeight - 1);
            for (int x = 0; x < width; x++)
            {
                int nx = x >> 1, fx = (x & 1) == 0 ? Math.Max(nx - 1, 0) : Math.Min(nx + 1, chromaWidth - 1);
                int Sample(byte[] plane) => (9 * plane[near * uvStride + nx] + 3 * plane[near * uvStride + fx] + 3 * plane[far * uvStride + nx] + plane[far * uvStride + fx] + 8) >> 4;
                int u = Sample(uPlane) - 128, v = Sample(vPlane) - 128, luma = 19077 * (yPlane[y * yStride + x] - 16);
                int at = (y * width + x) * 4;
                bgra[at] = Clip((luma + 33050 * u + 8192) >> 14);
                bgra[at + 1] = Clip((luma - 6419 * u - 13320 * v + 8192) >> 14);
                bgra[at + 2] = Clip((luma + 26149 * v + 8192) >> 14);
                bgra[at + 3] = 255;
            }
        }
        return new PixelImage(width, height, bgra);
    }
}
