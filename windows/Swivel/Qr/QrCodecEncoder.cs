using System.Text;

namespace Swivel;

// QR encoder after ISO/IEC 18004: segments → data codewords → Reed–Solomon blocks → interleaving → matrix with
// function patterns → best of the eight masks by the standard's penalty rules → format / version information.

/// A growable list of bits, most significant bit first.
sealed class QrBitBuffer
{
    readonly List<bool> bits = [];
    public int Count => bits.Count;
    public bool this[int index] => bits[index];

    public void Append(int value, int length)
    {
        for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    public void Append(QrBitBuffer other) => bits.AddRange(other.bits);
}

/// One run of data in a single mode. `Count` is the character count written in the count indicator.
sealed class QrSegment(QrMode mode, int count, QrBitBuffer data)
{
    public QrMode Mode { get; } = mode;
    public int Count { get; } = count;
    public QrBitBuffer Data { get; } = data;

    public static bool IsNumeric(string text) => text.All(c => c is >= '0' and <= '9');
    public static bool IsAlphanumeric(string text) => text.All(c => QrTables.AlphanumericChars.Contains(c));

    public static QrSegment Numeric(string digits)
    {
        if (!IsNumeric(digits)) throw new ArgumentException("Not numeric", nameof(digits));
        var bits = new QrBitBuffer();
        for (int i = 0; i < digits.Length; i += 3)
        {
            int n = Math.Min(3, digits.Length - i);
            bits.Append(int.Parse(digits.AsSpan(i, n), System.Globalization.CultureInfo.InvariantCulture), n * 3 + 1);
        }
        return new QrSegment(QrMode.Numeric, digits.Length, bits);
    }

    public static QrSegment Alphanumeric(string text)
    {
        if (!IsAlphanumeric(text)) throw new ArgumentException("Not alphanumeric", nameof(text));
        var bits = new QrBitBuffer();
        int i = 0;
        for (; i + 1 < text.Length; i += 2)
            bits.Append(QrTables.AlphanumericChars.IndexOf(text[i]) * 45 + QrTables.AlphanumericChars.IndexOf(text[i + 1]), 11);
        if (i < text.Length) bits.Append(QrTables.AlphanumericChars.IndexOf(text[i]), 6);
        return new QrSegment(QrMode.Alphanumeric, text.Length, bits);
    }

    public static QrSegment Bytes(ReadOnlySpan<byte> data)
    {
        var bits = new QrBitBuffer();
        foreach (byte b in data) bits.Append(b, 8);
        return new QrSegment(QrMode.Byte, data.Length, bits);
    }

    /// Kanji mode (Shift JIS double-byte characters 0x8140–0x9FFC and 0xE040–0xEBBF); null when a character doesn't fit.
    public static QrSegment? Kanji(string text)
    {
        var sjis = QrText.Encoding(932);
        if (sjis is null) return null;
        var bits = new QrBitBuffer();
        foreach (var element in EnumerateChars(text))
        {
            byte[] b;
            try { b = sjis.GetBytes(element); } catch (EncoderFallbackException) { return null; }
            if (b.Length != 2) return null;
            int v = b[0] << 8 | b[1];
            if (v is >= 0x8140 and <= 0x9FFC) v -= 0x8140;
            else if (v is >= 0xE040 and <= 0xEBBF) v -= 0xC140;
            else return null;
            bits.Append((v >> 8) * 0xC0 + (v & 0xFF), 13);
        }
        return new QrSegment(QrMode.Kanji, text.Length, bits);
    }

    /// Hanzi mode, GB 2312 subset (ISO/IEC 18004 Chinese national annex); null when a character doesn't fit.
    public static QrSegment? Hanzi(string text)
    {
        var gb = QrText.Encoding(936);
        if (gb is null) return null;
        var bits = new QrBitBuffer();
        foreach (var element in EnumerateChars(text))
        {
            byte[] b;
            try { b = gb.GetBytes(element); } catch (EncoderFallbackException) { return null; }
            if (b.Length != 2) return null;
            int v = b[0] << 8 | b[1];
            if (v is >= 0xA1A1 and <= 0xAAFE) v -= 0xA1A1;
            else if (v is >= 0xB0A1 and <= 0xFAFE) v -= 0xA6A1;
            else return null;
            bits.Append((v >> 8) * 0x60 + (v & 0xFF), 13);
        }
        return new QrSegment(QrMode.Hanzi, text.Length, bits);
    }

    /// An Extended Channel Interpretation designator (e.g. 26 = UTF-8, 20 = Shift JIS).
    public static QrSegment Eci(int assignment)
    {
        var bits = new QrBitBuffer();
        if (assignment < 0) throw new ArgumentOutOfRangeException(nameof(assignment));
        if (assignment < 1 << 7) bits.Append(assignment, 8);
        else if (assignment < 1 << 14) { bits.Append(2, 2); bits.Append(assignment, 14); }
        else if (assignment < 1_000_000) { bits.Append(6, 3); bits.Append(assignment, 21); }
        else throw new ArgumentOutOfRangeException(nameof(assignment));
        return new QrSegment(QrMode.Eci, 0, bits);
    }

    static IEnumerable<string> EnumerateChars(string text)
    {
        for (int i = 0; i < text.Length; i++) yield return text[i].ToString();
    }

    /// Bits this segment takes in a symbol of `version`, or −1 when its count doesn't fit the count indicator.
    public int BitLength(int version)
    {
        if (Mode == QrMode.Eci) return 4 + Data.Count;
        int countBits = QrTables.CountBits(Mode, version);
        if (Count >= 1 << countBits) return -1;
        return 4 + (Mode == QrMode.Hanzi ? 4 : 0) + countBits + Data.Count;
    }
}

static class QrLayout
{
    static readonly bool[]?[] functionMasks = new bool[41][];
    static readonly int[]?[] dataOrders = new int[41][];

    /// Modules that belong to function patterns (finders, separators, timing, alignment, format/version areas,
    /// dark module), row-major.
    public static bool[] FunctionMask(int version)
    {
        if (functionMasks[version] is { } cached) return cached;
        int size = QrTables.Size(version);
        var modules = new bool[size * size];
        var function = new bool[size * size];
        DrawFunctionPatterns(modules, function, version);
        functionMasks[version] = function;
        return function;
    }

    /// Indices (y·size + x) of the data modules in placement order: two-column zigzag from the bottom right.
    public static int[] DataOrder(int version)
    {
        if (dataOrders[version] is { } cached) return cached;
        int size = QrTables.Size(version);
        var function = FunctionMask(version);
        var order = new int[QrTables.RawDataModules(version)];
        int n = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;  // skip the vertical timing pattern
            bool upward = ((right + 1) & 2) == 0;
            for (int vert = 0; vert < size; vert++)
            {
                int y = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j, index = y * size + x;
                    if (!function[index]) order[n++] = index;
                }
            }
        }
        if (n != order.Length) throw new InvalidOperationException("QR layout mismatch");
        dataOrders[version] = order;
        return order;
    }

    public static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    public static void DrawFunctionPatterns(bool[] modules, bool[] function, int version)
    {
        int size = QrTables.Size(version);
        void Set(int x, int y, bool dark) { modules[y * size + x] = dark; function[y * size + x] = true; }

        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }

        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, d != 2 && d != 4);
                }

        var positions = QrTables.AlignmentPositions[version];
        int count = positions.Length;
        for (int i = 0; i < count; i++)
            for (int j = 0; j < count; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == count - 1) || (i == count - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        DrawFormatBits(modules, function, size, 0);
        if (version >= 7) DrawVersionBits(modules, function, version);
    }

    /// Both copies of the 15-bit format information, bit 0 = least significant, and the dark module.
    public static void DrawFormatBits(bool[] modules, bool[]? function, int size, int bits)
    {
        void Set(int x, int y, bool dark)
        {
            modules[y * size + x] = dark;
            if (function is not null) function[y * size + x] = true;
        }
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
        Set(8, size - 8, true);
    }

    static void DrawVersionBits(bool[] modules, bool[] function, int version)
    {
        int size = QrTables.Size(version), bits = QrTables.VersionBits(version);
        for (int i = 0; i < 18; i++)
        {
            bool dark = ((bits >> i) & 1) != 0;
            int a = size - 11 + i % 3, b = i / 3;
            modules[b * size + a] = dark; function[b * size + a] = true;
            modules[a * size + b] = dark; function[a * size + b] = true;
        }
    }

    /// The penalty score of ISO/IEC 18004 §7.8.3 (N1 = 3, N2 = 3, N3 = 40, N4 = 10). Modules outside the symbol
    /// count as light for the finder-like pattern rule.
    public static int Penalty(bool[] modules, int size)
    {
        int penalty = 0;
        Span<bool> line = stackalloc bool[size];
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++) line[b] = pass == 0 ? modules[a * size + b] : modules[b * size + a];
                // Rule 1: runs of five or more modules of the same colour.
                int run = 1;
                for (int b = 1; b <= size; b++)
                {
                    if (b < size && line[b] == line[b - 1]) { run++; continue; }
                    if (run >= 5) penalty += 3 + run - 5;
                    run = 1;
                }
                // Rule 3: 1:1:3:1:1 dark:light:dark:light:dark with four light modules on either side.
                for (int b = 0; b + 7 <= size; b++)
                {
                    if (!(line[b] && !line[b + 1] && line[b + 2] && line[b + 3] && line[b + 4] && !line[b + 5] && line[b + 6])) continue;
                    if (LightRun(line, b - 4, b)) penalty += 40;
                    if (LightRun(line, b + 7, b + 11)) penalty += 40;
                }
            }
        // Rule 2: 2×2 blocks of one colour.
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                bool c = modules[y * size + x];
                if (c == modules[y * size + x + 1] && c == modules[(y + 1) * size + x] && c == modules[(y + 1) * size + x + 1]) penalty += 3;
            }
        // Rule 4: 10 points per full 5 % deviation of the dark proportion from 50 %.
        int dark = 0;
        foreach (bool m in modules) if (m) dark++;
        int total = size * size;
        penalty += Math.Abs(dark * 20 - total * 10) / total * 10;
        return penalty;
    }

    static bool LightRun(ReadOnlySpan<bool> line, int from, int to)
    {
        for (int i = from; i < to; i++) if (i >= 0 && i < line.Length && line[i]) return false;
        return true;
    }
}

static class QrEncoder
{
    /// The segments CIQRCodeGenerator-style encoding uses for `text`: one numeric or alphanumeric segment when the
    /// whole text fits that mode, otherwise UTF-8 in byte mode without an ECI header (scanners recognise UTF-8; an
    /// ECI header confuses some older ones).
    public static List<QrSegment> SegmentsFor(string text)
    {
        if (text.Length > 0 && QrSegment.IsNumeric(text)) return [QrSegment.Numeric(text)];
        if (text.Length > 0 && QrSegment.IsAlphanumeric(text)) return [QrSegment.Alphanumeric(text)];
        return [QrSegment.Bytes(new UTF8Encoding(false).GetBytes(text))];
    }

    /// The smallest version in [minVersion, 40] that holds the segments, or 0.
    public static int ChooseVersion(IReadOnlyList<QrSegment> segments, QrErrorCorrection level, int minVersion = 1)
    {
        for (int version = Math.Max(1, minVersion); version <= QrTables.MaxVersion; version++)
        {
            int bits = TotalBits(segments, version);
            if (bits >= 0 && bits <= QrTables.DataCodewords(version, level) * 8) return version;
        }
        return 0;
    }

    static int TotalBits(IReadOnlyList<QrSegment> segments, int version)
    {
        int total = 0;
        foreach (var s in segments)
        {
            int n = s.BitLength(version);
            if (n < 0) return -1;
            total += n;
        }
        return total;
    }

    /// Data codewords (before error correction) for the given version: segments, terminator, padding.
    public static byte[] DataCodewords(IReadOnlyList<QrSegment> segments, QrErrorCorrection level, int version)
    {
        var bits = new QrBitBuffer();
        foreach (var s in segments)
        {
            bits.Append((int)s.Mode, 4);
            if (s.Mode == QrMode.Hanzi) bits.Append(1, 4);  // GB 2312 subset
            if (s.Mode != QrMode.Eci) bits.Append(s.Count, QrTables.CountBits(s.Mode, version));
            bits.Append(s.Data);
        }
        int capacity = QrTables.DataCodewords(version, level) * 8;
        if (bits.Count > capacity) throw new ArgumentException("Data too long for the version");
        bits.Append(0, Math.Min(4, capacity - bits.Count));
        bits.Append(0, (8 - bits.Count % 8) % 8);
        var bytes = new byte[capacity / 8];
        int n = bits.Count / 8;
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        for (int i = n, pad = 0; i < bytes.Length; i++, pad ^= 1) bytes[i] = (byte)(pad == 0 ? 0xEC : 0x11);
        return bytes;
    }

    /// Splits data into blocks, appends each block's Reed–Solomon codewords and interleaves them.
    public static byte[] Interleave(byte[] data, int version, QrErrorCorrection level)
    {
        int blocks = QrTables.NumBlocks(version, level), ec = QrTables.EcCodewordsPerBlock(version, level);
        int raw = QrTables.TotalCodewords(version);
        int numLong = raw % blocks, numShort = blocks - numLong, shortData = raw / blocks - ec;
        var dataBlocks = new byte[blocks][];
        var ecBlocks = new byte[blocks][];
        for (int b = 0, k = 0; b < blocks; b++)
        {
            int length = shortData + (b >= numShort ? 1 : 0);
            dataBlocks[b] = data.AsSpan(k, length).ToArray();
            ecBlocks[b] = QrReedSolomon.Encode(dataBlocks[b], ec);
            k += length;
        }
        var result = new byte[raw];
        int n = 0;
        for (int i = 0; i <= shortData; i++)
            for (int b = 0; b < blocks; b++)
                if (i < dataBlocks[b].Length) result[n++] = dataBlocks[b][i];
        for (int i = 0; i < ec; i++)
            for (int b = 0; b < blocks; b++) result[n++] = ecBlocks[b][i];
        return result;
    }

    /// Final codewords (data + error correction, interleaved) for the smallest fitting version.
    public static byte[] Codewords(IReadOnlyList<QrSegment> segments, QrErrorCorrection level, out int version, int minVersion = 1)
    {
        version = ChooseVersion(segments, level, minVersion);
        if (version == 0) throw new ArgumentException("Data too long for a QR code");
        return Interleave(DataCodewords(segments, level, version), version, level);
    }

    /// The symbol for the segments, or null when they don't fit version 40. `mask` −1 picks the lowest penalty.
    public static QrMatrix? Encode(IReadOnlyList<QrSegment> segments, QrErrorCorrection level, int mask = -1, int minVersion = 1)
    {
        int version = ChooseVersion(segments, level, minVersion);
        if (version == 0) return null;
        var codewords = Interleave(DataCodewords(segments, level, version), version, level);
        int size = QrTables.Size(version);
        var modules = new bool[size * size];
        var function = new bool[size * size];
        QrLayout.DrawFunctionPatterns(modules, function, version);
        var order = QrLayout.DataOrder(version);
        for (int i = 0; i < order.Length; i++)
            modules[order[i]] = i < codewords.Length * 8 && ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;

        int best = mask, bestPenalty = int.MaxValue;
        if (best < 0)
            for (int m = 0; m < 8; m++)
            {
                var trial = Masked(modules, function, size, m, level);
                int p = QrLayout.Penalty(trial, size);
                if (p < bestPenalty) { bestPenalty = p; best = m; }
            }
        var final = Masked(modules, function, size, best, level);
        var matrix = new QrMatrix(size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) matrix[x, y] = final[y * size + x];
        return matrix;
    }

    static bool[] Masked(bool[] modules, bool[] function, int size, int mask, QrErrorCorrection level)
    {
        var result = (bool[])modules.Clone();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                if (!function[i] && QrLayout.MaskBit(mask, x, y)) result[i] = !result[i];
            }
        QrLayout.DrawFormatBits(result, null, size, QrTables.FormatBits(level, mask));
        return result;
    }
}
