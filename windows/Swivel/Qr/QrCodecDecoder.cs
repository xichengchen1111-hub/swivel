using System.Text;

namespace Swivel;

// From a sampled module grid to text: format and version information (BCH, nearest valid codeword), unmasking,
// codeword extraction, de-interleaving, Reed–Solomon correction and the data bitstream (ISO/IEC 18004 §7 and §8).

/// Character sets for QR byte data: ECI designators, and a guess when a symbol has none.
static class QrText
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Encoding?> strict = new();

    /// A code page whose encoder and decoder throw on invalid data, or null when Windows doesn't have it.
    public static Encoding? Encoding(int codePage) => strict.GetOrAdd(codePage, cp =>
    {
        try
        {
            var provider = CodePagesEncodingProvider.Instance;
            var encoding = provider.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? System.Text.Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return encoding;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    });

    /// Code page for an ECI assignment number (AIM ECI), or 0 when unknown.
    public static int CodePageForEci(int eci) => eci switch
    {
        0 or 2 => 437,
        1 or 3 => 28591,
        4 => 28592, 5 => 28593, 6 => 28594, 7 => 28595, 8 => 28596, 9 => 28597, 10 => 28598, 11 => 28599,
        13 => 874, 15 => 28603, 17 => 28605,
        20 => 932,
        21 => 1250, 22 => 1251, 23 => 1252, 24 => 1256,
        25 => 1201,
        26 => 65001,
        27 or 170 => 20127,
        28 => 950,
        29 => 54936,
        30 => 51949,
        _ => 0,
    };

    public static string Decode(byte[] bytes, int codePage)
    {
        if (codePage == 65001) return Utf8(bytes) ?? new UTF8Encoding(false, false).GetString(bytes);
        if (codePage == 28591) return Latin1(bytes);
        var encoding = Encoding(codePage);
        if (encoding is not null)
            try { return encoding.GetString(bytes); }
            catch (DecoderFallbackException) { }
        try { return System.Text.Encoding.GetEncoding(codePage == 0 ? 28591 : codePage).GetString(bytes); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return Latin1(bytes); }
    }

    /// Byte data without an ECI: UTF-8 when it is valid UTF-8, Shift JIS or GB 2312 when the bytes look like
    /// double-byte text in that set, ISO-8859-1 (the standard's default) otherwise.
    public static string Guess(byte[] bytes)
    {
        if (Utf8(bytes) is { } utf8) return utf8;
        if (LooksLikeShiftJis(bytes) && Encoding(932) is { } sjis)
            try { return sjis.GetString(bytes); } catch (DecoderFallbackException) { }
        if (LooksLikeGb2312(bytes) && Encoding(936) is { } gb)
            try { return gb.GetString(bytes); } catch (DecoderFallbackException) { }
        return Latin1(bytes);
    }

    static string? Utf8(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    static string Latin1(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    /// Valid Shift JIS with at least one double-byte character and no half-width katakana.
    static bool LooksLikeShiftJis(byte[] bytes)
    {
        int doubles = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b < 0x80) continue;
            if (b is >= 0xA1 and <= 0xDF) return false;
            if (b is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xFC))
            {
                if (i + 1 >= bytes.Length) return false;
                int t = bytes[++i];
                if (t is < 0x40 or 0x7F or > 0xFC) return false;
                doubles++;
                continue;
            }
            return false;
        }
        return doubles > 0;
    }

    /// Every non-ASCII byte is part of a GB 2312 pair (both bytes in 0xA1–0xFE).
    static bool LooksLikeGb2312(byte[] bytes)
    {
        int pairs = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b < 0x80) continue;
            if (b is < 0xA1 or > 0xF7 || i + 1 >= bytes.Length) return false;
            int t = bytes[++i];
            if (t is < 0xA1 or > 0xFE) return false;
            pairs++;
        }
        return pairs > 0;
    }
}

static class QrMatrixDecoder
{
    static readonly int[] formatCodes = Enumerable.Range(0, 32).Select(d => QrTables.FormatBits(QrTables.LevelFromBits(d >> 3), d & 7)).ToArray();
    static readonly int[] versionCodes = Enumerable.Range(0, 41).Select(v => v >= 7 ? QrTables.VersionBits(v) : -1).ToArray();

    /// Decodes a sampled symbol (row-major, true = dark). Tries the mirror image too. Returns null when it isn't a
    /// readable QR symbol; `versionHint` is set when the version information names a different size.
    public static string? Decode(bool[] modules, int size, out int versionHint)
    {
        versionHint = 0;
        foreach (bool mirrored in new[] { false, true })
        {
            var m = mirrored ? Transpose(modules, size) : modules;
            var text = DecodeOriented(m, size, out int hint);
            if (text is not null) return text;
            if (hint != 0) versionHint = hint;
        }
        return null;
    }

    static bool[] Transpose(bool[] modules, int size)
    {
        var t = new bool[modules.Length];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) t[x * size + y] = modules[y * size + x];
        return t;
    }

    static string? DecodeOriented(bool[] m, int size, out int versionHint)
    {
        versionHint = 0;
        if ((size - 17) % 4 != 0) return null;
        int version = (size - 17) / 4;
        if (version is < 1 or > 40) return null;
        if (version >= 7)
        {
            // Unreadable version information: trust the size measured from the finder patterns.
            int read = ReadVersion(m, size);
            if (read != 0 && read != version) { versionHint = read; return null; }
        }
        if (ReadFormat(m, size) is not var (level, mask)) return null;
        var data = Codewords(m, size, version, level, mask);
        if (data is null) return null;
        return QrBitstream.Parse(data, version);
    }

    /// (level, mask) from the closer of the two format information copies, within 3 bit errors.
    public static (QrErrorCorrection Level, int Mask)? ReadFormat(bool[] m, int size)
    {
        int a = 0, b = 0;
        bool At(int x, int y) => m[y * size + x];
        for (int i = 0; i <= 5; i++) if (At(8, i)) a |= 1 << i;
        if (At(8, 7)) a |= 1 << 6;
        if (At(8, 8)) a |= 1 << 7;
        if (At(7, 8)) a |= 1 << 8;
        for (int i = 9; i < 15; i++) if (At(14 - i, 8)) a |= 1 << i;
        for (int i = 0; i < 8; i++) if (At(size - 1 - i, 8)) b |= 1 << i;
        for (int i = 8; i < 15; i++) if (At(8, size - 15 + i)) b |= 1 << i;
        int best = -1, bestDistance = 4;
        for (int d = 0; d < 32; d++)
        {
            int da = System.Numerics.BitOperations.PopCount((uint)(a ^ formatCodes[d]));
            int db = System.Numerics.BitOperations.PopCount((uint)(b ^ formatCodes[d]));
            int distance = Math.Min(da, db);
            if (distance < bestDistance) { bestDistance = distance; best = d; }
        }
        return best < 0 ? null : (QrTables.LevelFromBits(best >> 3), best & 7);
    }

    /// Bit errors between a 15-bit word and the nearest valid format information.
    public static int FormatDistance(int bits) => formatCodes.Min(code => System.Numerics.BitOperations.PopCount((uint)(bits ^ code)));

    /// The version from the closer of the two version information blocks, within 3 bit errors; 0 when unreadable.
    public static int ReadVersion(bool[] m, int size)
    {
        int a = 0, b = 0;
        for (int i = 0; i < 18; i++)
        {
            int x = size - 11 + i % 3, y = i / 3;
            if (m[y * size + x]) a |= 1 << i;
            if (m[x * size + y]) b |= 1 << i;
        }
        int best = 0, bestDistance = 4;
        for (int v = 7; v <= 40; v++)
        {
            int distance = Math.Min(System.Numerics.BitOperations.PopCount((uint)(a ^ versionCodes[v])),
                                    System.Numerics.BitOperations.PopCount((uint)(b ^ versionCodes[v])));
            if (distance < bestDistance) { bestDistance = distance; best = v; }
        }
        return best;
    }

    /// Corrected data codewords, or null when a block can't be corrected.
    static byte[]? Codewords(bool[] m, int size, int version, QrErrorCorrection level, int mask)
    {
        var order = QrLayout.DataOrder(version);
        int raw = QrTables.TotalCodewords(version);
        var codewords = new byte[raw];
        for (int i = 0; i < raw * 8; i++)
        {
            int index = order[i];
            bool bit = m[index] ^ QrLayout.MaskBit(mask, index % size, index / size);
            if (bit) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        int blocks = QrTables.NumBlocks(version, level), ec = QrTables.EcCodewordsPerBlock(version, level);
        int numLong = raw % blocks, numShort = blocks - numLong, shortData = raw / blocks - ec;
        var parts = new byte[blocks][];
        for (int b = 0; b < blocks; b++) parts[b] = new byte[shortData + (b >= numShort ? 1 : 0) + ec];
        int k = 0;
        for (int i = 0; i < shortData; i++)
            for (int b = 0; b < blocks; b++) parts[b][i] = codewords[k++];
        for (int b = numShort; b < blocks; b++) parts[b][shortData] = codewords[k++];
        for (int i = 0; i < ec; i++)
            for (int b = 0; b < blocks; b++) parts[b][parts[b].Length - ec + i] = codewords[k++];

        int maxErrors = (ec - QrTables.MisdecodeProtection(version, level)) / 2;
        var data = new List<byte>(QrTables.DataCodewords(version, level));
        foreach (var part in parts)
        {
            if (QrReedSolomon.Correct(part, ec, maxErrors) < 0) return null;
            data.AddRange(part.AsSpan(0, part.Length - ec).ToArray());
        }
        return data.ToArray();
    }
}

/// Parses the data bitstream of a QR symbol into text.
static class QrBitstream
{
    sealed class Reader(byte[] data)
    {
        int position;
        public int Available => data.Length * 8 - position;
        public int Read(int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++, position++)
                value = value << 1 | ((data[position >> 3] >> (7 - (position & 7))) & 1);
            return value;
        }
    }

    /// The text, or null when the bitstream is malformed.
    public static string? Parse(byte[] data, int version)
    {
        var reader = new Reader(data);
        var text = new StringBuilder();
        int eciCodePage = 0;
        bool fnc1 = false;
        try
        {
            while (reader.Available >= 4)
            {
                var mode = (QrMode)reader.Read(4);
                switch (mode)
                {
                    case QrMode.Terminator:
                        return text.ToString();
                    case QrMode.Numeric:
                    {
                        int count = Count(reader, mode, version);
                        if (count < 0) return null;
                        for (; count >= 3; count -= 3)
                        {
                            if (reader.Available < 10) return null;
                            int v = reader.Read(10);
                            if (v >= 1000) return null;
                            text.Append(v.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        if (count == 2)
                        {
                            if (reader.Available < 7) return null;
                            int v = reader.Read(7);
                            if (v >= 100) return null;
                            text.Append(v.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else if (count == 1)
                        {
                            if (reader.Available < 4) return null;
                            int v = reader.Read(4);
                            if (v >= 10) return null;
                            text.Append((char)('0' + v));
                        }
                        break;
                    }
                    case QrMode.Alphanumeric:
                    {
                        int count = Count(reader, mode, version);
                        if (count < 0) return null;
                        var chars = new StringBuilder();
                        for (; count >= 2; count -= 2)
                        {
                            if (reader.Available < 11) return null;
                            int v = reader.Read(11);
                            if (v >= 45 * 45) return null;
                            chars.Append(QrTables.AlphanumericChars[v / 45]).Append(QrTables.AlphanumericChars[v % 45]);
                        }
                        if (count == 1)
                        {
                            if (reader.Available < 6) return null;
                            int v = reader.Read(6);
                            if (v >= 45) return null;
                            chars.Append(QrTables.AlphanumericChars[v]);
                        }
                        // In FNC1 (GS1) symbols "%" stands for the GS separator and "%%" for a percent sign.
                        text.Append(fnc1 ? chars.ToString().Replace("%%", "\u0001").Replace('%', '\u001D').Replace('\u0001', '%') : chars.ToString());
                        break;
                    }
                    case QrMode.Byte:
                    {
                        int count = Count(reader, mode, version);
                        if (count < 0 || reader.Available < count * 8) return null;
                        var bytes = new byte[count];
                        for (int i = 0; i < count; i++) bytes[i] = (byte)reader.Read(8);
                        text.Append(eciCodePage != 0 ? QrText.Decode(bytes, eciCodePage) : QrText.Guess(bytes));
                        break;
                    }
                    case QrMode.Kanji:
                    {
                        int count = Count(reader, mode, version);
                        if (count < 0 || reader.Available < count * 13) return null;
                        var bytes = new byte[count * 2];
                        for (int i = 0; i < count; i++)
                        {
                            int v = reader.Read(13);
                            int assembled = (v / 0xC0) << 8 | (v % 0xC0);
                            assembled += assembled < 0x1F00 ? 0x8140 : 0xC140;
                            bytes[2 * i] = (byte)(assembled >> 8);
                            bytes[2 * i + 1] = (byte)assembled;
                        }
                        text.Append(QrText.Decode(bytes, 932));
                        break;
                    }
                    case QrMode.Hanzi:
                    {
                        if (reader.Available < 4) return null;
                        int subset = reader.Read(4);
                        int count = Count(reader, mode, version);
                        if (subset != 1 || count < 0 || reader.Available < count * 13) return null;
                        var bytes = new byte[count * 2];
                        for (int i = 0; i < count; i++)
                        {
                            int v = reader.Read(13);
                            int assembled = (v / 0x60) << 8 | (v % 0x60);
                            assembled += assembled < 0x0A00 ? 0xA1A1 : 0xA6A1;
                            bytes[2 * i] = (byte)(assembled >> 8);
                            bytes[2 * i + 1] = (byte)assembled;
                        }
                        text.Append(QrText.Decode(bytes, 936));
                        break;
                    }
                    case QrMode.Eci:
                    {
                        if (reader.Available < 8) return null;
                        int first = reader.Read(8), value;
                        if ((first & 0x80) == 0) value = first;
                        else if ((first & 0xC0) == 0x80) { if (reader.Available < 8) return null; value = (first & 0x3F) << 8 | reader.Read(8); }
                        else if ((first & 0xE0) == 0xC0) { if (reader.Available < 16) return null; value = (first & 0x1F) << 16 | reader.Read(16); }
                        else return null;
                        int codePage = QrText.CodePageForEci(value);
                        if (codePage != 0) eciCodePage = codePage;
                        break;
                    }
                    case QrMode.StructuredAppend:
                        if (reader.Available < 16) return null;
                        reader.Read(16);  // sequence and parity: each symbol is reported on its own
                        break;
                    case QrMode.Fnc1First:
                        fnc1 = true;
                        break;
                    case QrMode.Fnc1Second:
                        if (reader.Available < 8) return null;
                        reader.Read(8);
                        fnc1 = true;
                        break;
                    default:
                        return null;
                }
            }
        }
        catch (IndexOutOfRangeException) { return null; }
        return text.ToString();
    }

    static int Count(Reader reader, QrMode mode, int version)
    {
        int bits = QrTables.CountBits(mode, version);
        return reader.Available < bits ? -1 : reader.Read(bits);
    }
}
