namespace Swivel;

// Constants of ISO/IEC 18004 (QR Code, versions 1–40), written out from the standard's tables.

/// Mode indicators (4 bits).
enum QrMode { Terminator = 0, Numeric = 1, Alphanumeric = 2, StructuredAppend = 3, Byte = 4, Fnc1First = 5, Eci = 7, Kanji = 8, Fnc1Second = 9, Hanzi = 13 }

static class QrTables
{
    public const int MinVersion = 1, MaxVersion = 40;

    public static int Size(int version) => 17 + 4 * version;

    // Error correction codewords per block, [level L/M/Q/H][version]; index 0 unused.
    static readonly byte[][] EcPerBlock =
    [
        [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    // Number of error correction blocks, [level][version].
    static readonly byte[][] Blocks =
    [
        [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    /// Row/column centres of the alignment patterns (Annex E, Table E.1).
    public static readonly int[][] AlignmentPositions =
    [
        [], [],
        [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62],
        [6, 26, 46, 66], [6, 26, 48, 70], [6, 26, 50, 74], [6, 30, 54, 78], [6, 30, 56, 82], [6, 30, 58, 86], [6, 34, 62, 90],
        [6, 28, 50, 72, 94], [6, 26, 50, 74, 98], [6, 30, 54, 78, 102], [6, 28, 54, 80, 106], [6, 32, 58, 84, 110], [6, 30, 58, 86, 114], [6, 34, 62, 90, 118],
        [6, 26, 50, 74, 98, 122], [6, 30, 54, 78, 102, 126], [6, 26, 52, 78, 104, 130], [6, 30, 56, 82, 108, 134], [6, 34, 60, 86, 112, 138], [6, 30, 58, 86, 114, 142], [6, 34, 62, 90, 118, 146],
        [6, 30, 54, 78, 102, 126, 150], [6, 24, 50, 76, 102, 128, 154], [6, 28, 54, 80, 106, 132, 158], [6, 32, 58, 84, 110, 136, 162], [6, 26, 54, 82, 110, 138, 166], [6, 30, 58, 86, 114, 142, 170],
    ];

    public static int EcCodewordsPerBlock(int version, QrErrorCorrection level) => EcPerBlock[(int)level][version];
    public static int NumBlocks(int version, QrErrorCorrection level) => Blocks[(int)level][version];

    /// Modules left for data and error correction codewords (including remainder bits).
    public static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    public static int TotalCodewords(int version) => RawDataModules(version) / 8;

    public static int DataCodewords(int version, QrErrorCorrection level) =>
        TotalCodewords(version) - EcCodewordsPerBlock(version, level) * NumBlocks(version, level);

    /// Misdecode protection codewords (Table 9 "p"): codewords of small symbols that may not be used for correction.
    public static int MisdecodeProtection(int version, QrErrorCorrection level) => (version, level) switch
    {
        (1, QrErrorCorrection.L) => 3,
        (1, QrErrorCorrection.M) => 2,
        (1, _) => 1,
        (2, QrErrorCorrection.L) => 2,
        (3, QrErrorCorrection.L) => 1,
        _ => 0,
    };

    /// The two-bit level indicator of the format information (L = 01, M = 00, Q = 11, H = 10).
    public static int LevelBits(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => 1, QrErrorCorrection.M => 0, QrErrorCorrection.Q => 3, _ => 2,
    };

    public static QrErrorCorrection LevelFromBits(int bits) => bits switch
    {
        1 => QrErrorCorrection.L, 0 => QrErrorCorrection.M, 3 => QrErrorCorrection.Q, _ => QrErrorCorrection.H,
    };

    /// The 15-bit format information (BCH(15,5) with generator 0x537, masked with 0x5412).
    public static int FormatBits(QrErrorCorrection level, int mask)
    {
        int data = LevelBits(level) << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return (data << 10 | (rem & 0x3FF)) ^ 0x5412;
    }

    /// The 18-bit version information (BCH(18,6) with generator 0x1F25), versions 7–40.
    public static int VersionBits(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return version << 12 | (rem & 0xFFF);
    }

    /// Bits of the character count indicator.
    public static int CountBits(QrMode mode, int version)
    {
        int band = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return mode switch
        {
            QrMode.Numeric => new[] { 10, 12, 14 }[band],
            QrMode.Alphanumeric => new[] { 9, 11, 13 }[band],
            QrMode.Byte => new[] { 8, 16, 16 }[band],
            QrMode.Kanji or QrMode.Hanzi => new[] { 8, 10, 12 }[band],
            _ => 0,
        };
    }

    public const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
}

/// GF(256) with the QR field polynomial x⁸ + x⁴ + x³ + x² + 1 (0x11D) and generator α = 2.
static class QrGalois
{
    public static readonly byte[] Exp = new byte[512];
    public static readonly byte[] Log = new byte[256];

    static QrGalois()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
    public static int Div(int a, int b) => a == 0 ? 0 : Exp[Log[a] + 255 - Log[b]];
    /// α^e for any integer e.
    public static int Alpha(int e) => Exp[(e % 255 + 255) % 255];
}

/// Reed–Solomon codes over GF(256) with consecutive roots α⁰ … α^(n−1), as QR uses them.
static class QrReedSolomon
{
    static readonly byte[]?[] generators = new byte[256][];

    /// Coefficients of g(x) = Π (x − αⁱ), highest degree first (the leading 1 included).
    public static byte[] Generator(int degree)
    {
        if (generators[degree] is { } cached) return cached;
        var poly = new byte[degree + 1];
        poly[0] = 1;
        for (int i = 0; i < degree; i++)
        {
            int root = QrGalois.Exp[i];
            // Multiply the i+1 coefficients so far by (x + root), in place from the end.
            poly[i + 1] = (byte)QrGalois.Mul(poly[i], root);
            for (int j = i; j >= 1; j--) poly[j] ^= (byte)QrGalois.Mul(poly[j - 1], root);
        }
        generators[degree] = poly;
        return poly;
    }

    /// The error correction codewords for one block of data.
    public static byte[] Encode(ReadOnlySpan<byte> data, int ecLength)
    {
        var g = Generator(ecLength);
        var rem = new byte[ecLength];
        foreach (byte d in data)
        {
            int factor = d ^ rem[0];
            Array.Copy(rem, 1, rem, 0, ecLength - 1);
            rem[ecLength - 1] = 0;
            if (factor == 0) continue;
            for (int i = 0; i < ecLength; i++) rem[i] ^= (byte)QrGalois.Mul(g[i + 1], factor);
        }
        return rem;
    }

    /// Corrects a received block (data followed by `ecLength` check codewords) in place. Returns the number of
    /// corrected codewords, or −1 when the block has more than `maxErrors` errors or is not correctable.
    public static int Correct(byte[] block, int ecLength, int maxErrors)
    {
        int n = block.Length;
        Span<int> syndromes = stackalloc int[ecLength];
        if (!Syndromes(block, syndromes)) return 0;

        // Berlekamp–Massey: the error locator Λ(x), lowest degree first.
        var locator = new int[ecLength + 1];
        var previous = new int[ecLength + 1];
        var scratch = new int[ecLength + 1];
        locator[0] = previous[0] = 1;
        int errors = 0, shift = 1, lastDiscrepancy = 1;
        for (int k = 0; k < ecLength; k++)
        {
            int d = syndromes[k];
            for (int i = 1; i <= errors; i++) d ^= QrGalois.Mul(locator[i], syndromes[k - i]);
            if (d == 0) { shift++; continue; }
            int coef = QrGalois.Div(d, lastDiscrepancy);
            if (2 * errors <= k)
            {
                Array.Copy(locator, scratch, locator.Length);
                for (int i = 0; i + shift <= ecLength; i++) locator[i + shift] ^= QrGalois.Mul(coef, previous[i]);
                errors = k + 1 - errors;
                (previous, scratch) = (scratch, previous);
                lastDiscrepancy = d;
                shift = 1;
            }
            else
            {
                for (int i = 0; i + shift <= ecLength; i++) locator[i + shift] ^= QrGalois.Mul(coef, previous[i]);
                shift++;
            }
        }
        if (errors == 0 || errors > maxErrors || 2 * errors > ecLength) return -1;

        // Error evaluator Ω(x) = S(x)·Λ(x) mod x^ecLength.
        var evaluator = new int[ecLength];
        for (int i = 0; i < ecLength; i++)
            for (int j = 0; j <= Math.Min(i, errors); j++) evaluator[i] ^= QrGalois.Mul(locator[j], syndromes[i - j]);

        // Chien search over every position, Forney for the magnitudes (first root α⁰: e = X·Ω(X⁻¹)/Λ'(X⁻¹)).
        int found = 0;
        for (int j = 0; j < n; j++)
        {
            int power = n - 1 - j;
            int inverse = QrGalois.Alpha(-power);
            int value = 0;
            for (int i = errors; i >= 0; i--) value = QrGalois.Mul(value, inverse) ^ locator[i];
            if (value != 0) continue;
            int derivative = 0;
            for (int i = 1; i <= errors; i += 2)
                derivative ^= QrGalois.Mul(locator[i], QrGalois.Alpha(QrGalois.Log[inverse] * (i - 1)));
            if (derivative == 0) return -1;
            int omega = 0;
            for (int i = ecLength - 1; i >= 0; i--) omega = QrGalois.Mul(omega, inverse) ^ evaluator[i];
            block[j] ^= (byte)QrGalois.Mul(QrGalois.Alpha(power), QrGalois.Div(omega, derivative));
            found++;
        }
        if (found != errors) return -1;
        return Syndromes(block, syndromes) ? -1 : errors;
    }

    /// Fills the syndromes; true when any is non-zero.
    static bool Syndromes(byte[] block, Span<int> syndromes)
    {
        bool any = false;
        for (int i = 0; i < syndromes.Length; i++)
        {
            int a = QrGalois.Exp[i], s = 0;
            foreach (byte r in block) s = QrGalois.Mul(s, a) ^ r;
            syndromes[i] = s;
            any |= s != 0;
        }
        return any;
    }
}
