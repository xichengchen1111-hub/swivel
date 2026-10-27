using System.Text;

namespace Swivel;

// One-dimensional barcodes, from the published symbology specifications: EAN-13 / UPC-A / EAN-8 / UPC-E
// (ISO/IEC 15420), Code 128 (ISO/IEC 15417), Code 39 (ISO/IEC 16388), Interleaved 2 of 5 (ISO/IEC 16390) and
// Codabar. Rows and columns are scanned at many positions in both directions (so upside-down and 90°-rotated
// codes read too); each scan line is thresholded locally and turned into run lengths, which the decoders match
// against the element widths with a tolerance. A code counts when two scan lines agree.

readonly record struct BarcodeHit(string Text, BarcodeFormat Format, double MinX, double MinY, double MaxX, double MaxY);

static class BarcodeReader
{
    const int LinesPerAxis = 48;

    sealed class Vote
    {
        public int Lines;
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;
    }

    /// Every barcode found in the image. `binary` (the block-adaptive binarisation) is used as a second opinion.
    public static List<BarcodeHit> Scan(QrLuma luma, QrBinary? binary)
    {
        var votes = new Dictionary<(BarcodeFormat, string), Vote>();
        int totalLines = 0;
        foreach (bool vertical in new[] { false, true })
        {
            int length = vertical ? luma.Height : luma.Width, across = vertical ? luma.Width : luma.Height;
            if (length < 20 || across < 1) continue;
            int lines = Math.Min(LinesPerAxis, across);
            double spacing = (double)across / lines;
            var values = new byte[length];
            var dark = new bool[length];
            for (int i = 0; i < lines; i++)
            {
                int pos = (int)((i + 0.5) * spacing);
                for (int t = 0; t < length; t++)
                    values[t] = vertical ? luma.Pixels[t * luma.Width + pos] : luma.Pixels[pos * luma.Width + t];
                var found = new HashSet<(BarcodeFormat, string)>();
                Threshold(values, dark);
                ScanLine(dark, found, vertical, pos, spacing, votes);
                if (binary is not null)
                {
                    for (int t = 0; t < length; t++) dark[t] = vertical ? binary.Dark(pos, t) : binary.Dark(t, pos);
                    ScanLine(dark, found, vertical, pos, spacing, votes);
                }
                totalLines++;
            }
        }
        var result = new List<BarcodeHit>();
        foreach (var ((format, text), vote) in votes)
        {
            // Two agreeing scan lines: one line through texture or a QR symbol can pass a mod-10 check by chance.
            if (vote.Lines < 2) continue;
            result.Add(new BarcodeHit(text, format, Math.Max(0, vote.MinX), Math.Max(0, vote.MinY),
                Math.Min(luma.Width, vote.MaxX), Math.Min(luma.Height, vote.MaxY)));
        }
        return result;
    }

    /// Local threshold: the midpoint of the minimum and maximum in a window around each pixel, light where the
    /// window has too little contrast to hold bars.
    static void Threshold(byte[] values, bool[] dark)
    {
        int n = values.Length, block = Math.Max(8, n / 48);
        int blocks = (n + block - 1) / block;
        Span<byte> mins = blocks <= 1024 ? stackalloc byte[blocks] : new byte[blocks];
        Span<byte> maxs = blocks <= 1024 ? stackalloc byte[blocks] : new byte[blocks];
        for (int b = 0; b < blocks; b++)
        {
            byte lo = 255, hi = 0;
            for (int i = b * block; i < Math.Min(n, (b + 1) * block); i++)
            {
                if (values[i] < lo) lo = values[i];
                if (values[i] > hi) hi = values[i];
            }
            mins[b] = lo; maxs[b] = hi;
        }
        for (int b = 0; b < blocks; b++)
        {
            int lo = 255, hi = 0;
            for (int k = Math.Max(0, b - 1); k <= Math.Min(blocks - 1, b + 1); k++)
            {
                lo = Math.Min(lo, mins[k]);
                hi = Math.Max(hi, maxs[k]);
            }
            int mid = (lo + hi) / 2;
            bool flat = hi - lo < 32;
            for (int i = b * block; i < Math.Min(n, (b + 1) * block); i++) dark[i] = !flat && values[i] < mid;
        }
    }

    static void ScanLine(bool[] dark, HashSet<(BarcodeFormat, string)> found, bool vertical, int pos, double spacing,
                         Dictionary<(BarcodeFormat, string), Vote> votes)
    {
        int n = dark.Length;
        foreach (bool reversed in new[] { false, true })
        {
            var runs = new List<int>(64);
            var starts = new List<int>(64);
            runs.Add(0); starts.Add(0);
            bool current = false;
            for (int t = 0; t < n; t++)
            {
                bool d = dark[reversed ? n - 1 - t : t];
                if (d != current) { runs.Add(0); starts.Add(t); current = d; }
                runs[^1]++;
            }
            var r = runs.ToArray();
            foreach (var (format, text, first, last) in DecodeRuns(r))
            {
                if (!found.Add((format, text))) continue;
                double a = starts[first], b = starts[last] + r[last];
                if (reversed) (a, b) = (n - b, n - a);
                if (!votes.TryGetValue((format, text), out var vote)) votes[(format, text)] = vote = new Vote();
                vote.Lines++;
                double lo = pos - spacing / 2, hi = pos + spacing / 2;
                if (vertical)
                {
                    vote.MinX = Math.Min(vote.MinX, lo); vote.MaxX = Math.Max(vote.MaxX, hi);
                    vote.MinY = Math.Min(vote.MinY, a); vote.MaxY = Math.Max(vote.MaxY, b);
                }
                else
                {
                    vote.MinX = Math.Min(vote.MinX, a); vote.MaxX = Math.Max(vote.MaxX, b);
                    vote.MinY = Math.Min(vote.MinY, lo); vote.MaxY = Math.Max(vote.MaxY, hi);
                }
            }
        }
    }

    /// Codes in one line of run lengths (runs[0] is light, then alternating). Yields the first and last run index.
    public static IEnumerable<(BarcodeFormat Format, string Text, int First, int Last)> DecodeRuns(int[] runs)
    {
        for (int s = 1; s < runs.Length; s += 2)
        {
            (BarcodeFormat, string, int)? hit = TryUpcEan(runs, s) ?? TryCode128(runs, s) ?? TryCode39(runs, s) ?? TryItf(runs, s) ?? TryCodabar(runs, s);
            if (hit is not var (format, text, end)) continue;
            yield return (format, text, s, end - 1);
            s = end % 2 == 1 ? end - 2 : end - 1;  // continue at the next dark run after the code
        }
    }

    // MARK: Matching

    /// Average deviation per module between the runs and a pattern of module widths, or +∞ when one element is
    /// off by more than `maxIndividual` modules.
    static double Variance(int[] runs, int offset, ReadOnlySpan<byte> pattern, double maxIndividual = 0.7)
    {
        if (offset < 0 || offset + pattern.Length > runs.Length) return double.PositiveInfinity;
        int total = 0, patternTotal = 0;
        for (int i = 0; i < pattern.Length; i++) { total += runs[offset + i]; patternTotal += pattern[i]; }
        if (total < patternTotal) return double.PositiveInfinity;
        double unit = (double)total / patternTotal, sum = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            double diff = Math.Abs(runs[offset + i] - pattern[i] * unit) / unit;
            if (diff > maxIndividual) return double.PositiveInfinity;
            sum += diff;
        }
        return sum / patternTotal;
    }

    static bool Quiet(int[] runs, int index, double width)
    {
        if (index < 0 || index >= runs.Length) return false;
        bool edge = index == 0 || index == runs.Length - 1;
        return runs[index] >= width || (edge && runs[index] >= width / 3);
    }

    static int Sum(int[] runs, int offset, int count)
    {
        int s = 0;
        for (int i = 0; i < count; i++) s += runs[offset + i];
        return s;
    }

    static byte[] Widths(string digits) => digits.Select(c => (byte)(c - '0')).ToArray();

    // MARK: EAN / UPC

    static readonly byte[][] EanL = new[] { "3211", "2221", "2122", "1411", "1132", "1231", "1114", "1312", "1213", "3112" }.Select(Widths).ToArray();
    static readonly byte[][] EanG = EanL.Select(p => p.AsEnumerable().Reverse().ToArray()).ToArray();
    static readonly string[] EanParity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];
    static readonly byte[] Guard3 = [1, 1, 1], Guard5 = [1, 1, 1, 1, 1], Guard6 = [1, 1, 1, 1, 1, 1];
    const double MaxEanVariance = 0.48;

    /// The best digit for the four runs at `offset`: (digit, true for a G (even parity) pattern), or null.
    static (int Digit, bool G)? EanDigit(int[] runs, int offset, bool allowG, double unit)
    {
        if (offset + 4 > runs.Length) return null;
        double digitUnit = Sum(runs, offset, 4) / 7.0;
        if (digitUnit < unit * 0.6 || digitUnit > unit * 1.6) return null;
        double best = MaxEanVariance;
        (int, bool)? result = null;
        for (int d = 0; d < 10; d++)
        {
            double v = Variance(runs, offset, EanL[d]);
            if (v < best) { best = v; result = (d, false); }
            if (!allowG) continue;
            v = Variance(runs, offset, EanG[d]);
            if (v < best) { best = v; result = (d, true); }
        }
        return result;
    }

    static (BarcodeFormat, string, int)? TryUpcEan(int[] runs, int s)
    {
        if (Variance(runs, s, Guard3) > MaxEanVariance) return null;
        double unit = Sum(runs, s, 3) / 3.0;
        if (!Quiet(runs, s - 1, unit * 5)) return null;
        return TryEan13(runs, s, unit) ?? TryEan8(runs, s, unit) ?? TryUpcE(runs, s, unit);
    }

    static (BarcodeFormat, string, int)? TryEan13(int[] runs, int s, double unit)
    {
        if (s + 60 > runs.Length) return null;
        var digits = new StringBuilder(13);
        var parity = new StringBuilder(6);
        for (int i = 0; i < 6; i++)
        {
            if (EanDigit(runs, s + 3 + 4 * i, true, unit) is not var (d, g)) return null;
            digits.Append((char)('0' + d)); parity.Append(g ? 'G' : 'L');
        }
        if (Variance(runs, s + 27, Guard5) > MaxEanVariance) return null;
        for (int i = 0; i < 6; i++)
        {
            if (EanDigit(runs, s + 32 + 4 * i, false, unit) is not var (d, _)) return null;
            digits.Append((char)('0' + d));
        }
        if (Variance(runs, s + 56, Guard3) > MaxEanVariance || !Quiet(runs, s + 59, unit * 5)) return null;
        int first = Array.IndexOf(EanParity, parity.ToString());
        if (first < 0) return null;
        string code = (char)('0' + first) + digits.ToString();
        if (!EanChecksumOk(code)) return null;
        return code[0] == '0' ? (BarcodeFormat.UpcA, code[1..], s + 59) : (BarcodeFormat.Ean13, code, s + 59);
    }

    static (BarcodeFormat, string, int)? TryEan8(int[] runs, int s, double unit)
    {
        if (s + 44 > runs.Length) return null;
        var digits = new StringBuilder(8);
        for (int i = 0; i < 4; i++)
        {
            if (EanDigit(runs, s + 3 + 4 * i, false, unit) is not var (d, _)) return null;
            digits.Append((char)('0' + d));
        }
        if (Variance(runs, s + 19, Guard5) > MaxEanVariance) return null;
        for (int i = 0; i < 4; i++)
        {
            if (EanDigit(runs, s + 24 + 4 * i, false, unit) is not var (d, _)) return null;
            digits.Append((char)('0' + d));
        }
        if (Variance(runs, s + 40, Guard3) > MaxEanVariance || !Quiet(runs, s + 43, unit * 5)) return null;
        string code = digits.ToString();
        return EanChecksumOk(code) ? (BarcodeFormat.Ean8, code, s + 43) : null;
    }

    // UPC-E parity of the six digits (G = even) for number system 0, by check digit; number system 1 is the inverse.
    static readonly string[] UpcEParity = ["GGGLLL", "GGLGLL", "GGLLGL", "GGLLLG", "GLGGLL", "GLLGGL", "GLLLGG", "GLGLGL", "GLGLLG", "GLLGLG"];

    static (BarcodeFormat, string, int)? TryUpcE(int[] runs, int s, double unit)
    {
        if (s + 34 > runs.Length) return null;
        var digits = new StringBuilder(6);
        var parity = new StringBuilder(6);
        for (int i = 0; i < 6; i++)
        {
            if (EanDigit(runs, s + 3 + 4 * i, true, unit) is not var (d, g)) return null;
            digits.Append((char)('0' + d)); parity.Append(g ? 'G' : 'L');
        }
        if (Variance(runs, s + 27, Guard6) > MaxEanVariance || !Quiet(runs, s + 33, unit * 5)) return null;
        string p = parity.ToString();
        for (int system = 0; system <= 1; system++)
            for (int check = 0; check < 10; check++)
            {
                string expected = system == 0 ? UpcEParity[check] : new string(UpcEParity[check].Select(c => c == 'G' ? 'L' : 'G').ToArray());
                if (expected != p) continue;
                string code = (char)('0' + system) + digits.ToString() + (char)('0' + check);
                return EanChecksumOk(ExpandUpcE(code)) ? (BarcodeFormat.UpcE, code, s + 33) : null;
            }
        return null;
    }

    /// The UPC-A number an eight-digit UPC-E code stands for.
    public static string ExpandUpcE(string upce)
    {
        string d = upce.Substring(1, 6), ns = upce[..1], check = upce[7..];
        string body = d[5] switch
        {
            '0' or '1' or '2' => d[..2] + d[5] + "0000" + d.Substring(2, 3),
            '3' => d[..3] + "00000" + d.Substring(3, 2),
            '4' => d[..4] + "00000" + d[4],
            _ => d[..5] + "0000" + d[5],
        };
        return ns + body + check;
    }

    /// Mod-10 check of EAN-13 / EAN-8 / UPC-A (weights 3 and 1 from the right, the check digit excluded).
    public static bool EanChecksumOk(string code) => code.Length >= 2 && EanCheckDigit(code[..^1]) == code[^1] - '0';

    public static int EanCheckDigit(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++)
        {
            int weight = (body.Length - i) % 2 == 1 ? 3 : 1;
            sum += (body[i] - '0') * weight;
        }
        return (10 - sum % 10) % 10;
    }

    // MARK: Code 128

    /// Element widths (bar, space, bar, space, bar, space) of the 107 symbol values; 106 is the stop (7 elements).
    public static readonly byte[][] Code128Patterns = new[]
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    }.Select(Widths).ToArray();

    const double MaxCode128Variance = 0.25;

    static int Code128Symbol(int[] runs, int offset)
    {
        int best = -1;
        double bestVariance = MaxCode128Variance;
        for (int v = 0; v < Code128Patterns.Length; v++)
        {
            double variance = Variance(runs, offset, Code128Patterns[v].AsSpan(0, 6));
            if (variance < bestVariance) { bestVariance = variance; best = v; }
        }
        return best;
    }

    static (BarcodeFormat, string, int)? TryCode128(int[] runs, int s)
    {
        if (s + 6 > runs.Length) return null;
        int start = Code128Symbol(runs, s);
        if (start is < 103 or > 105) return null;
        double unit = Sum(runs, s, 6) / 11.0;
        if (!Quiet(runs, s - 1, unit * 5)) return null;
        var values = new List<int> { start };
        int pos = s + 6, end;
        while (true)
        {
            if (pos + 6 > runs.Length || values.Count > 300) return null;
            double symbolUnit = Sum(runs, pos, 6) / 11.0;
            if (symbolUnit < unit * 0.6 || symbolUnit > unit * 1.6) return null;
            int v = Code128Symbol(runs, pos);
            if (v < 0) return null;
            if (v == 106)
            {
                if (pos + 7 >= runs.Length) return null;
                double last = runs[pos + 6] / symbolUnit;
                if (last < 1.3 || last > 2.8 || !Quiet(runs, pos + 7, unit * 5)) return null;
                end = pos + 7;
                break;
            }
            if (v >= 103) return null;
            values.Add(v);
            pos += 6;
        }
        if (values.Count < 3) return null;  // start, at least one data symbol, checksum
        int checksum = values[^1], sum = start;
        for (int i = 1; i < values.Count - 1; i++) sum += i * values[i];
        if (sum % 103 != checksum) return null;
        var text = Code128Text(values.GetRange(0, values.Count - 1));
        return string.IsNullOrEmpty(text) ? null : (BarcodeFormat.Code128, text, end);
    }

    /// Text of Code 128 values (start symbol first, checksum excluded): code sets A/B/C, shift, FNC1 (GS1: ignored
    /// in first position, a GS separator elsewhere) and FNC4 (extended ASCII).
    public static string? Code128Text(IReadOnlyList<int> values)
    {
        var text = new StringBuilder();
        char set = values[0] switch { 103 => 'A', 104 => 'B', _ => 'C' };
        bool shift = false, fnc4Once = false, fnc4Latch = false;
        for (int i = 1; i < values.Count; i++)
        {
            int v = values[i];
            char current = shift ? (set == 'A' ? 'B' : 'A') : set;
            shift = false;
            if (v == 102)
            {
                if (i > 1) text.Append('\u001D');
                continue;
            }
            if (current == 'C')
            {
                if (v < 100) text.Append(v.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                else if (v == 100) set = 'B';
                else if (v == 101) set = 'A';
                continue;
            }
            if (v < 96)
            {
                int c = current == 'A' ? (v < 64 ? v + 32 : v - 64) : v + 32;
                if (fnc4Once != fnc4Latch) c += 128;
                fnc4Once = false;
                text.Append((char)c);
                continue;
            }
            switch (v)
            {
                case 96 or 97: break;  // FNC3 / FNC2: reader instructions, no data
                case 98: shift = true; break;
                case 99: set = 'C'; break;
                case 100 when current == 'A': set = 'B'; break;
                case 101 when current == 'B': set = 'A'; break;
                case 100 or 101:
                    // FNC4: twice in a row latches extended ASCII on or off.
                    if (fnc4Once) { fnc4Latch = !fnc4Latch; fnc4Once = false; }
                    else fnc4Once = true;
                    break;
            }
        }
        return text.ToString();
    }

    // MARK: Code 39

    const string Code39Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";

    /// Wide elements of each character, nine bits for bar, space, bar, … (first element = highest bit).
    public static readonly int[] Code39Patterns =
    [
        0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064,
        0x109, 0x049, 0x148, 0x019, 0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C,
        0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016,
        0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8,
        0x0A2, 0x08A, 0x02A, 0x094,
    ];

    /// The pattern of `count` elements at offset where exactly `wide` of them are wide, as bits (first element =
    /// highest bit), or −1 when there's no clear wide/narrow split.
    static int WidePattern(int[] runs, int offset, int count, int wide)
    {
        if (offset + count > runs.Length) return -1;
        Span<int> sorted = stackalloc int[count];
        for (int i = 0; i < count; i++) sorted[i] = runs[offset + i];
        sorted.Sort();
        int narrowMax = sorted[count - wide - 1], wideMin = sorted[count - wide];
        if (wideMin < narrowMax * 1.3 || wideMin < sorted[0] * 1.5) return -1;
        if (sorted[count - 1] > sorted[0] * 5 + 2) return -1;
        double threshold = (narrowMax + wideMin) / 2.0;
        int bits = 0;
        for (int i = 0; i < count; i++) if (runs[offset + i] > threshold) bits |= 1 << (count - 1 - i);
        return System.Numerics.BitOperations.PopCount((uint)bits) == wide ? bits : -1;
    }

    static char? Code39Char(int[] runs, int offset)
    {
        int bits = WidePattern(runs, offset, 9, 3);
        int index = bits < 0 ? -1 : Array.IndexOf(Code39Patterns, bits);
        return index < 0 ? null : Code39Chars[index];
    }

    static (BarcodeFormat, string, int)? TryCode39(int[] runs, int s)
    {
        if (Code39Char(runs, s) != '*') return null;
        int width = Sum(runs, s, 9);
        double narrow = width / 13.0;
        if (!Quiet(runs, s - 1, narrow * 5)) return null;
        var text = new StringBuilder();
        int pos = s + 9;
        while (true)
        {
            if (pos + 10 > runs.Length || text.Length > 80) return null;
            if (runs[pos] > width / 2.0) return null;  // inter-character gap
            int charWidth = Sum(runs, pos + 1, 9);
            if (charWidth < width * 0.7 || charWidth > width * 1.4) return null;
            if (Code39Char(runs, pos + 1) is not char c) return null;
            if (c == '*')
            {
                if (!Quiet(runs, pos + 10, narrow * 5)) return null;
                break;
            }
            text.Append(c);
            pos += 10;
        }
        return text.Length == 0 ? null : (BarcodeFormat.Code39, text.ToString(), pos + 10);
    }

    // MARK: Interleaved 2 of 5

    /// Wide elements of digits 0–9 (five bits, first element = highest bit).
    public static readonly int[] ItfPatterns = [0b00110, 0b10001, 0b01001, 0b11000, 0b00101, 0b10100, 0b01100, 0b00011, 0b10010, 0b01010];

    static (BarcodeFormat, string, int)? TryItf(int[] runs, int s)
    {
        if (s + 4 > runs.Length || Variance(runs, s, [1, 1, 1, 1], 0.5) > 0.35) return null;
        double narrow = Sum(runs, s, 4) / 4.0;
        if (!Quiet(runs, s - 1, narrow * 8)) return null;
        var digits = new StringBuilder();
        int pos = s + 4;
        Span<int> bars = stackalloc int[5], spaces = stackalloc int[5];
        var pair = new int[10];
        while (true)
        {
            // Stop pattern: wide bar, narrow space, narrow bar, then the quiet zone.
            if (pos + 3 < runs.Length && runs[pos] >= narrow * 1.7 && runs[pos] <= narrow * 4.5
                && runs[pos + 1] <= narrow * 1.6 && runs[pos + 2] <= narrow * 1.6 && Quiet(runs, pos + 3, narrow * 8))
                break;
            if (pos + 10 > runs.Length || digits.Length > 80) return null;
            for (int i = 0; i < 5; i++) { pair[i] = runs[pos + 2 * i]; pair[5 + i] = runs[pos + 2 * i + 1]; }
            int a = Array.IndexOf(ItfPatterns, WidePattern(pair, 0, 5, 2)), b = Array.IndexOf(ItfPatterns, WidePattern(pair, 5, 5, 2));
            if (a < 0 || b < 0) return null;
            digits.Append((char)('0' + a)).Append((char)('0' + b));
            pos += 10;
        }
        return digits.Length < 6 ? null : (BarcodeFormat.Itf, digits.ToString(), pos + 3);
    }

    // MARK: Codabar

    const string CodabarChars = "0123456789-$:/.+ABCD";

    /// Wide elements of each character, seven bits for bar, space, … (first element = highest bit).
    public static readonly int[] CodabarPatterns =
    [
        0x003, 0x006, 0x009, 0x060, 0x012, 0x042, 0x021, 0x024, 0x030, 0x048,
        0x00C, 0x018, 0x045, 0x051, 0x054, 0x015, 0x01A, 0x029, 0x00B, 0x00E,
    ];

    static char? CodabarChar(int[] runs, int offset)
    {
        if (offset + 7 > runs.Length) return null;
        foreach (int wide in new[] { 2, 3 })
        {
            int bits = WidePattern(runs, offset, 7, wide);
            int index = bits < 0 ? -1 : Array.IndexOf(CodabarPatterns, bits);
            if (index >= 0) return CodabarChars[index];
        }
        return null;
    }

    static (BarcodeFormat, string, int)? TryCodabar(int[] runs, int s)
    {
        if (CodabarChar(runs, s) is not char first || first < 'A') return null;
        int width = Sum(runs, s, 7);
        double narrow = width / 10.0;
        if (!Quiet(runs, s - 1, narrow * 5)) return null;
        var text = new StringBuilder();
        int pos = s + 7;
        while (true)
        {
            if (pos + 8 > runs.Length || text.Length > 80) return null;
            if (runs[pos] > narrow * 3.5) return null;  // inter-character gap
            int charWidth = Sum(runs, pos + 1, 7);
            if (charWidth < width * 0.6 || charWidth > width * 1.6) return null;
            if (CodabarChar(runs, pos + 1) is not char c) return null;
            if (c >= 'A')
            {
                if (!Quiet(runs, pos + 8, narrow * 5)) return null;
                break;
            }
            text.Append(c);
            pos += 8;
        }
        return text.Length < 2 ? null : (BarcodeFormat.Codabar, text.ToString(), pos + 8);
    }
}
