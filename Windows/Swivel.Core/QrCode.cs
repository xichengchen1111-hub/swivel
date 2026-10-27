using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Swivel.Core;

public enum QrCorrection { Low, Medium, Quartile, High }

/// Original, bounded QR Model 2 encoder: UTF-8 ECI 26, byte mode, versions 1–40,
/// levels L, M, Q and H, with separately protected and interleaved Reed–Solomon blocks.
/// No external code, service, model or payload metadata is used.
public sealed partial class QrCode
{
    public const int MaxBytes = 2952, MaxMediumBytes = 2330, MaxQuartileBytes = 1662, MaxHighBytes = 1272;
    public const int QuietZone = 4;
    // ISO/IEC 18004 Model 2 levels L/M/Q/H. CheckWords is per block; longer data blocks
    // follow shorter blocks when the data count is not divisible by BlockCounts.
    private static readonly int[] DataWords = { 19, 34, 55, 80, 108, 136, 156, 194, 232, 274, 324, 370, 428, 461, 523, 589, 647, 721, 795, 861, 932, 1006, 1094, 1174, 1276, 1370, 1468, 1531, 1631, 1735, 1843, 1955, 2071, 2191, 2306, 2434, 2566, 2702, 2812, 2956 };
    private static readonly int[] CheckWords = { 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
    private static readonly int[] BlockCounts = { 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 };
    private static readonly int[] MediumDataWords = { 16, 28, 44, 64, 86, 108, 124, 154, 182, 216, 254, 290, 334, 365, 415, 453, 507, 563, 627, 669, 714, 782, 860, 914, 1000, 1062, 1128, 1193, 1267, 1373, 1455, 1541, 1631, 1725, 1812, 1914, 1992, 2102, 2216, 2334 };
    private static readonly int[] MediumCheckWords = { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
    private static readonly int[] MediumBlockCounts = { 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
    private static readonly int[] QuartileDataWords = { 13, 22, 34, 48, 62, 76, 88, 110, 132, 154, 180, 206, 244, 261, 295, 325, 367, 397, 445, 485, 512, 568, 614, 664, 718, 754, 808, 871, 911, 985, 1033, 1115, 1171, 1231, 1286, 1354, 1426, 1502, 1582, 1666 };
    private static readonly int[] QuartileCheckWords = { 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
    private static readonly int[] QuartileBlockCounts = { 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 };
    private static readonly int[] HighDataWords = { 9, 16, 26, 36, 46, 60, 66, 86, 100, 122, 140, 158, 180, 197, 223, 253, 283, 313, 341, 385, 406, 442, 464, 514, 538, 596, 628, 661, 701, 745, 793, 845, 901, 961, 986, 1054, 1096, 1142, 1222, 1276 };
    private static readonly int[] HighCheckWords = { 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
    private static readonly int[] HighBlockCounts = { 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 };
    // Standard Model 2 alignment coordinates, indexed by version minus one.
    private static readonly int[][] AlignmentCenters = {
        Array.Empty<int>(),
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
        new[] { 6, 24, 42 },
        new[] { 6, 26, 46 },
        new[] { 6, 28, 50 },
        new[] { 6, 30, 54 },
        new[] { 6, 32, 58 },
        new[] { 6, 34, 62 },
        new[] { 6, 26, 46, 66 },
        new[] { 6, 26, 48, 70 },
        new[] { 6, 26, 50, 74 },
        new[] { 6, 30, 54, 78 },
        new[] { 6, 30, 56, 82 },
        new[] { 6, 30, 58, 86 },
        new[] { 6, 34, 62, 90 },
        new[] { 6, 28, 50, 72, 94 },
        new[] { 6, 26, 50, 74, 98 },
        new[] { 6, 30, 54, 78, 102 },
        new[] { 6, 28, 54, 80, 106 },
        new[] { 6, 32, 58, 84, 110 },
        new[] { 6, 30, 58, 86, 114 },
        new[] { 6, 34, 62, 90, 118 },
        new[] { 6, 26, 50, 74, 98, 122 },
        new[] { 6, 30, 54, 78, 102, 126 },
        new[] { 6, 26, 52, 78, 104, 130 },
        new[] { 6, 30, 56, 82, 108, 134 },
        new[] { 6, 34, 60, 86, 112, 138 },
        new[] { 6, 30, 58, 86, 114, 142 },
        new[] { 6, 34, 62, 90, 118, 146 },
        new[] { 6, 30, 54, 78, 102, 126, 150 },
        new[] { 6, 24, 50, 76, 102, 128, 154 },
        new[] { 6, 28, 54, 80, 106, 132, 158 },
        new[] { 6, 32, 58, 84, 110, 136, 162 },
        new[] { 6, 26, 54, 82, 110, 138, 166 },
        new[] { 6, 30, 58, 86, 114, 142, 170 }
    };
    private readonly bool[,] modules;
    public int Size => modules.GetLength(0);
    public int Version => (Size - 17) / 4;
    public int Mask { get; }
    public QrCorrection Correction { get; }
    public bool this[int x, int y] => modules[x, y];
    private QrCode(bool[,] modules, int mask, QrCorrection correction) { this.modules = modules; Mask = mask; Correction = correction; }

    public static string Level(QrCorrection correction)=>correction switch
    {
        QrCorrection.Low=>"L",QrCorrection.Medium=>"M",QrCorrection.Quartile=>"Q",QrCorrection.High=>"H",
        _=>throw new UserError("Choose L, M, Q or H QR error correction.")
    };
    public static int Limit(QrCorrection correction)=>correction switch
    {
        QrCorrection.Low=>MaxBytes,QrCorrection.Medium=>MaxMediumBytes,QrCorrection.Quartile=>MaxQuartileBytes,QrCorrection.High=>MaxHighBytes,
        _=>throw new UserError("Choose L, M, Q or H QR error correction.")
    };
    public static string CapacityMessage(QrCorrection correction)=>correction switch
    {
        QrCorrection.Low=>"Enter text using 1–2952 UTF-8 bytes. Chinese and emoji use more than one byte.",
        QrCorrection.Medium=>"Enter text using 1–2330 UTF-8 bytes for M error correction. Chinese and emoji use more than one byte.",
        QrCorrection.Quartile=>"Enter text using 1–1662 UTF-8 bytes for Q error correction. Chinese and emoji use more than one byte.",
        QrCorrection.High=>"Enter text using 1–1272 UTF-8 bytes for H error correction. Chinese and emoji use more than one byte.",
        _=>throw new UserError("Choose L, M, Q or H QR error correction.")
    };
    public static byte[] Payload(string text)=>Payload(text,QrCorrection.Low);
    public static byte[] Payload(string text,QrCorrection correction)
    {
        int limit=Limit(correction);
        string error=CapacityMessage(correction);
        if (string.IsNullOrWhiteSpace(text) || text.Length > limit)
            throw new UserError(error);
        foreach (char character in text)
            if (char.IsControl(character) && character is not '\n' and not '\r' and not '\t')
                throw new UserError("QR text cannot contain hidden control characters.");
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(text); }
        catch (EncoderFallbackException) { throw new UserError("QR text must be valid Unicode."); }
        if (bytes.Length > limit) throw new UserError(error);
        return bytes;
    }

    public static QrCode Encode(string text)=>Encode(text,QrCorrection.Low);
    public static QrCode Encode(string text,QrCorrection correction)
    {
        byte[] payload = Payload(text,correction);
        int[] dataWords=correction switch { QrCorrection.Low=>DataWords,QrCorrection.Medium=>MediumDataWords,QrCorrection.Quartile=>QuartileDataWords,_=>HighDataWords };
        int[] checkWords=correction switch { QrCorrection.Low=>CheckWords,QrCorrection.Medium=>MediumCheckWords,QrCorrection.Quartile=>QuartileCheckWords,_=>HighCheckWords };
        int[] blockCounts=correction switch { QrCorrection.Low=>BlockCounts,QrCorrection.Medium=>MediumBlockCounts,QrCorrection.Quartile=>QuartileBlockCounts,_=>HighBlockCounts };
        int version = 1;
        // The ECI header takes 12 bits, followed by byte-mode's 4-bit indicator
        // and an 8-bit length for versions 1–9 or 16-bit length from version 10.
        while (version <= dataWords.Length && 16 + (version < 10 ? 8 : 16) + payload.Length * 8 > dataWords[version - 1] * 8) version++;
        if (version > dataWords.Length) throw new InvalidOperationException("QR payload capacity mismatch.");
        int capacity = dataWords[version - 1], parity = checkWords[version - 1];
        var bits = new List<bool>();
        void Append(int value, int length) { for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(7, 4); Append(26, 8); // ECI: UTF-8, including ASCII-only input.
        Append(4, 4); Append(payload.Length, version < 10 ? 8 : 16);
        foreach (byte value in payload) Append(value, 8);
        Append(0, Math.Min(4, capacity * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        int pad = 0;
        while (bits.Count < capacity * 8) Append(pad++ % 2 == 0 ? 0xec : 0x11, 8);
        byte[] data = new byte[capacity];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i / 8] |= (byte)(1 << (7 - i % 8));
        byte[] words = Interleave(data, blockCounts[version - 1], parity);
        int size = 17 + 4 * version;
        var baseModules = new bool[size, size]; var reserved = new bool[size, size];
        void Set(int x, int y, bool dark) { if (x < 0 || y < 0 || x >= size || y >= size) return; baseModules[x, y] = dark; reserved[x, y] = true; }
        void Finder(int x, int y)
        {
            for (int j = -1; j <= 7; j++) for (int i = -1; i <= 7; i++)
                Set(x + i, y + j, i is >= 0 and <= 6 && j is >= 0 and <= 6 && (i is 0 or 6 || j is 0 or 6 || i is >= 2 and <= 4 && j is >= 2 and <= 4));
        }
        Finder(0, 0); Finder(size - 7, 0); Finder(0, size - 7);
        for (int i = 8; i < size - 8; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        if (version > 1)
        {
            int[] centers = AlignmentCenters[version - 1];
            foreach (int cy in centers) foreach (int cx in centers)
            {
                // Only the three finder corners are excluded. Alignment patterns
                // at other centers on row/column 6 replace the timing pattern.
                if (cx == 6 && (cy == 6 || cy == size - 7) || cy == 6 && cx == size - 7) continue;
                for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++)
                    Set(cx + x, cy + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1);
            }
        }
        if (version >= 7) VersionInformation(Set, size, version);
        Format(Set, size, 0, correction); // Reserve both format copies and the fixed dark module.
        bool[,]? best = null; int bestMask = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone(); int bit = 0;
            bool upward = true;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right--;
                for (int row = 0; row < size; row++)
                {
                    int y = upward ? size - 1 - row : row;
                    for (int delta = 0; delta < 2; delta++)
                    {
                        int x = right - delta;
                        if (reserved[x, y]) continue;
                        bool value = bit < words.Length * 8 && (words[bit / 8] & (1 << (7 - bit % 8))) != 0;
                        bit++; candidate[x, y] = value ^ MaskAt(mask, x, y);
                    }
                }
                upward = !upward;
            }
            if (bit != words.Length * 8 + (version switch { >= 2 and <= 6 => 7, >= 14 and <= 20 => 3, >= 21 and <= 27 => 4, >= 28 and <= 34 => 3, _ => 0 })) throw new InvalidOperationException("QR layout capacity mismatch.");
            Format((x, y, value) => candidate[x, y] = value, size, mask, correction);
            int penaltyScore = Penalty(candidate);
            if (penaltyScore < bestPenalty) { best = candidate; bestMask = mask; bestPenalty = penaltyScore; }
        }
        return new(best!, bestMask, correction);
    }

    public byte[] Raster(int scale = 8)
    {
        if (scale is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(scale));
        int side = (Size + QuietZone * 2) * scale;
        var pixels = new byte[side * side]; Array.Fill(pixels, (byte)255);
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            if (modules[x, y]) for (int dy = 0; dy < scale; dy++)
                Array.Clear(pixels, ((y + QuietZone) * scale + dy) * side + (x + QuietZone) * scale, scale);
        return pixels;
    }

    private static byte Multiply(byte a, byte b)
    {
        int result = 0, term = a;
        for (int bit = 0; bit < 8; bit++) { if ((b & (1 << bit)) != 0) result ^= term; term <<= 1; if ((term & 0x100) != 0) term ^= 0x11d; }
        return (byte)result;
    }
    private static byte[] Interleave(byte[] data, int count, int parity)
    {
        int shortLength = data.Length / count, shortCount = count - data.Length % count;
        var blocks = new byte[count][]; var checks = new byte[count][]; int offset = 0;
        for (int block = 0; block < count; block++)
        {
            int length = shortLength + (block < shortCount ? 0 : 1);
            blocks[block] = data.AsSpan(offset, length).ToArray(); offset += length;
            checks[block] = Remainder(blocks[block], parity);
        }
        var words = new byte[data.Length + count * parity]; offset = 0;
        for (int column = 0; column <= shortLength; column++)
            foreach (byte[] block in blocks) if (column < block.Length) words[offset++] = block[column];
        for (int column = 0; column < parity; column++)
            foreach (byte[] block in checks) words[offset++] = block[column];
        return words;
    }
    private static byte[] Remainder(byte[] data, int degree)
    {
        byte[] polynomial = { 1 }; byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            var next = new byte[polynomial.Length + 1];
            for (int j = 0; j < polynomial.Length; j++) { next[j] ^= polynomial[j]; next[j + 1] ^= Multiply(polynomial[j], root); }
            polynomial = next; root = Multiply(root, 2);
        }
        var remainder = new byte[degree];
        foreach (byte value in data)
        {
            byte factor = (byte)(value ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, degree - 1); remainder[^1] = 0;
            for (int j = 0; j < degree; j++) remainder[j] ^= Multiply(factor, polynomial[j + 1]);
        }
        return remainder;
    }
    private static bool MaskAt(int mask, int x, int y) => mask switch {
        0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0, 5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0 };
    private static void Format(Action<int, int, bool> set, int size, int mask, QrCorrection correction)
    {
        int data = ((correction switch { QrCorrection.Low=>1,QrCorrection.Medium=>0,QrCorrection.Quartile=>3,_=>2 }) << 3) | mask, remainder = data << 10;
        for (int bit = 14; bit >= 10; bit--) if ((remainder & (1 << bit)) != 0) remainder ^= 0x537 << (bit - 10);
        int value = ((data << 10) | remainder) ^ 0x5412;
        bool Bit(int i) => ((value >> i) & 1) != 0;
        for (int i = 0; i < 6; i++) set(8, i, Bit(i));
        set(8, 7, Bit(6)); set(8, 8, Bit(7)); set(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) set(14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) set(size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) set(8, size - 15 + i, Bit(i));
        set(8, size - 8, true);
    }
    private static void VersionInformation(Action<int, int, bool> set, int size, int version)
    {
        int remainder = version << 12;
        for (int bit = 17; bit >= 12; bit--) if ((remainder & (1 << bit)) != 0) remainder ^= 0x1f25 << (bit - 12);
        int value = (version << 12) | remainder;
        for (int bit = 0; bit < 18; bit++)
        {
            bool dark = ((value >> bit) & 1) != 0;
            int shortAxis = size - 11 + bit % 3, longAxis = bit / 3;
            set(shortAxis, longAxis, dark); set(longAxis, shortAxis, dark);
        }
    }
    private static int Penalty(bool[,] cells)
    {
        int size = cells.GetLength(0), score = 0, dark = 0;
        for (int axis = 0; axis < 2; axis++) for (int line = 0; line < size; line++)
        {
            bool previous = false; int run = 0, pattern = 0;
            for (int p = 0; p < size; p++)
            {
                bool value = axis == 0 ? cells[p, line] : cells[line, p];
                if (p == 0 || value != previous) { if (run >= 5) score += run - 2; run = 1; previous = value; } else run++;
                pattern = ((pattern << 1) | (value ? 1 : 0)) & 0x7ff;
                if (p >= 10 && pattern is 0x5d0 or 0x05d) score += 40;
            }
            if (run >= 5) score += run - 2;
        }
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            bool value = cells[x, y]; if (value) dark++;
            if (x > 0 && y > 0 && cells[x - 1, y] == value && cells[x, y - 1] == value && cells[x - 1, y - 1] == value) score += 3;
        }
        score += Math.Abs(dark * 20 - size * size * 10) / (size * size) * 10;
        return score;
    }
}
