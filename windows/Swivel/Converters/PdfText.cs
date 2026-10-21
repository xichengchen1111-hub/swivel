using System.Globalization;
using System.Text;

namespace Swivel;

/// A 2D affine transform in PDF's row-vector convention: [a b 0; c d 0; e f 1].
readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix operator *(PdfMatrix m, PdfMatrix n) => new(
        m.A * n.A + m.B * n.C, m.A * n.B + m.B * n.D,
        m.C * n.A + m.D * n.C, m.C * n.B + m.D * n.D,
        m.E * n.A + m.F * n.C + n.E, m.E * n.B + m.F * n.D + n.F);

    public (double X, double Y) Apply(double x, double y) => (x * A + y * C + E, x * B + y * D + F);
}

/// Parses CMap streams: ToUnicode maps and CID encoding maps.
sealed class PdfCMap
{
    public readonly List<(byte[] Low, byte[] High)> Codespaces = [];
    public readonly Dictionary<uint, string> Unicode = [];
    readonly List<(uint Low, uint High, int Bytes, string Start)> unicodeRanges = [];
    public readonly Dictionary<uint, int> Cids = [];
    readonly List<(uint Low, uint High, int Bytes, int Start)> cidRanges = [];
    public string? UseCMap;

    public static PdfCMap Parse(byte[] data)
    {
        var map = new PdfCMap();
        var lexer = new PdfLexer(data);
        var operands = new List<PdfObj>();
        string? section = null;
        while (true)
        {
            var o = lexer.Next(resolveRefs: false);
            if (o == null) break;
            if (o is PdfKeyword k)
            {
                switch (k.Value)
                {
                    case "begincodespacerange" or "beginbfchar" or "beginbfrange" or "begincidchar" or "begincidrange" or "beginnotdefrange":
                        section = k.Value[5..];
                        operands.Clear();
                        break;
                    case "endcodespacerange":
                        for (int i = 0; i + 1 < operands.Count; i += 2)
                            if (operands[i] is PdfStr lo && operands[i + 1] is PdfStr hi && lo.Bytes.Length == hi.Bytes.Length && lo.Bytes.Length is > 0 and <= 4)
                                map.Codespaces.Add((lo.Bytes, hi.Bytes));
                        section = null;
                        operands.Clear();
                        break;
                    case "endbfchar":
                        for (int i = 0; i + 1 < operands.Count; i += 2)
                            if (operands[i] is PdfStr src)
                            {
                                var dst = operands[i + 1] switch
                                {
                                    PdfStr s => Utf16(s.Bytes),
                                    PdfName n => PdfGlyphs.ToUnicode(n.Value),
                                    _ => null,
                                };
                                if (dst != null) map.Unicode[Code(src.Bytes)] = dst;
                            }
                        section = null;
                        operands.Clear();
                        break;
                    case "endbfrange":
                        for (int i = 0; i + 2 < operands.Count; i += 3)
                        {
                            if (operands[i] is not PdfStr lo || operands[i + 1] is not PdfStr hi) continue;
                            uint low = Code(lo.Bytes), high = Code(hi.Bytes);
                            if (high < low || high - low > 65535) continue;
                            if (operands[i + 2] is PdfStr start) map.unicodeRanges.Add((low, high, lo.Bytes.Length, Utf16(start.Bytes)));
                            else if (operands[i + 2] is PdfArray array)
                                for (uint c = low; c <= high && c - low < array.Count; c++)
                                    if (array[(int)(c - low)] is PdfStr s) map.Unicode[c] = Utf16(s.Bytes);
                        }
                        section = null;
                        operands.Clear();
                        break;
                    case "endcidchar":
                        for (int i = 0; i + 1 < operands.Count; i += 2)
                            if (operands[i] is PdfStr src && operands[i + 1] is PdfNum cid) map.Cids[Code(src.Bytes)] = cid.Int;
                        section = null;
                        operands.Clear();
                        break;
                    case "endcidrange":
                        for (int i = 0; i + 2 < operands.Count; i += 3)
                            if (operands[i] is PdfStr lo && operands[i + 1] is PdfStr hi && operands[i + 2] is PdfNum start)
                                map.cidRanges.Add((Code(lo.Bytes), Code(hi.Bytes), lo.Bytes.Length, start.Int));
                        section = null;
                        operands.Clear();
                        break;
                    case "endnotdefrange":
                        section = null;
                        operands.Clear();
                        break;
                    case "usecmap":
                        if (operands.LastOrDefault() is PdfName name) map.UseCMap = name.Value;
                        operands.Clear();
                        break;
                    default:
                        if (section == null) operands.Clear();
                        break;
                }
                continue;
            }
            operands.Add(o);
            if (section == null && operands.Count > 16) operands.RemoveAt(0);
        }
        return map;
    }

    static uint Code(byte[] b)
    {
        uint v = 0;
        foreach (var x in b.Take(4)) v = (v << 8) | x;
        return v;
    }

    static string Utf16(byte[] b)
    {
        if (b.Length == 1) return ((char)b[0]).ToString();
        return Encoding.BigEndianUnicode.GetString(b, 0, b.Length / 2 * 2);
    }

    public string? ToUnicode(uint code, int length)
    {
        if (Unicode.TryGetValue(code, out var s)) return s;
        foreach (var (low, high, bytes, start) in unicodeRanges)
        {
            if (code < low || code > high || (bytes != length && length != 0)) continue;
            if (start.Length == 0) return null;
            // The last UTF-16 unit counts up through the range.
            var chars = start.ToCharArray();
            chars[^1] = (char)(chars[^1] + (code - low));
            return new string(chars);
        }
        return null;
    }

    public int? Cid(uint code, int length)
    {
        if (Cids.TryGetValue(code, out var c)) return c;
        foreach (var (low, high, bytes, start) in cidRanges)
            if (code >= low && code <= high && bytes == length) return start + (int)(code - low);
        return null;
    }

    /// Splits a string into codes using the codespace ranges.
    public List<(uint Code, int Length)> Split(byte[] s, int fallbackLength)
    {
        var result = new List<(uint, int)>(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int length = 0;
            foreach (var (low, high) in Codespaces)
            {
                int n = low.Length;
                if (i + n > s.Length) continue;
                bool inside = true;
                for (int k = 0; k < n; k++)
                    if (s[i + k] < low[k] || s[i + k] > high[k]) { inside = false; break; }
                if (inside) { length = n; break; }
            }
            if (length == 0)
            {
                length = Codespaces.Count > 0 ? Codespaces.Min(c => c.Low.Length) : fallbackLength;
                length = Math.Clamp(length, 1, Math.Max(1, s.Length - i));
            }
            uint code = 0;
            for (int k = 0; k < length; k++) code = (code << 8) | s[i + k];
            result.Add((code, length));
            i += length;
        }
        return result;
    }
}

/// Glyph names → Unicode (the common part of the Adobe Glyph List) and the standard single-byte encodings.
static class PdfGlyphs
{
    static readonly Dictionary<string, string> names = Build();

    static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string list)
        {
            var parts = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i += 2) d[parts[i]] = ((char)int.Parse(parts[i + 1], NumberStyles.HexNumber)).ToString();
        }
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c.ToString();
        for (char c = 'a'; c <= 'z'; c++) d[c.ToString()] = c.ToString();
        string[] digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        for (int i = 0; i < 10; i++) d[digits[i]] = ((char)('0' + i)).ToString();
        Add("space 20 exclam 21 quotedbl 22 numbersign 23 dollar 24 percent 25 ampersand 26 quotesingle 27 parenleft 28 parenright 29 " +
            "asterisk 2A plus 2B comma 2C hyphen 2D period 2E slash 2F colon 3A semicolon 3B less 3C equal 3D greater 3E question 3F " +
            "at 40 bracketleft 5B backslash 5C bracketright 5D asciicircum 5E underscore 5F grave 60 braceleft 7B bar 7C braceright 7D " +
            "asciitilde 7E nbspace A0 nonbreakingspace A0 exclamdown A1 cent A2 sterling A3 currency A4 yen A5 brokenbar A6 section A7 " +
            "dieresis A8 copyright A9 ordfeminine AA guillemotleft AB logicalnot AC sfthyphen AD softhyphen AD registered AE macron AF " +
            "degree B0 plusminus B1 twosuperior B2 threesuperior B3 acute B4 mu B5 paragraph B6 periodcentered B7 middot B7 cedilla B8 " +
            "onesuperior B9 ordmasculine BA guillemotright BB onequarter BC onehalf BD threequarters BE questiondown BF multiply D7 divide F7 " +
            "quoteleft 2018 quoteright 2019 quotesinglbase 201A quotereversed 201B quotedblleft 201C quotedblright 201D quotedblbase 201E " +
            "endash 2013 emdash 2014 figuredash 2012 bullet 2022 ellipsis 2026 dagger 2020 daggerdbl 2021 perthousand 2030 " +
            "guilsinglleft 2039 guilsinglright 203A trademark 2122 Euro 20AC florin 192 circumflex 2C6 tilde 2DC caron 2C7 breve 2D8 " +
            "dotaccent 2D9 ring 2DA ogonek 2DB hungarumlaut 2DD fraction 2044 fi FB01 fl FB02 ff FB00 ffi FB03 ffl FB04 dotlessi 131 " +
            "Lslash 141 lslash 142 OE 152 oe 153 Scaron 160 scaron 161 Ydieresis 178 Zcaron 17D zcaron 17E minus 2212 " +
            "arrowleft 2190 arrowup 2191 arrowright 2192 arrowdown 2193 arrowboth 2194 infinity 221E notequal 2260 lessequal 2264 " +
            "greaterequal 2265 partialdiff 2202 summation 2211 product 220F radical 221A integral 222B approxequal 2248 lozenge 25CA " +
            "Delta 2206 Omega 2126 pi 3C0 alpha 3B1 beta 3B2 gamma 3B3 delta 3B4 epsilon 3B5 theta 3B8 lambda 3BB sigma 3C3 " +
            "omega 3C9 mu1 B5 periodcentered B7 copyrightserif A9 registerserif AE trademarkserif 2122 checkmark 2713 " +
            "Abreve 102 abreve 103 Aogonek 104 aogonek 105 Cacute 106 cacute 107 Ccaron 10C ccaron 10D Dcaron 10E dcaron 10F " +
            "Dcroat 110 dcroat 111 Ecaron 11A ecaron 11B Eogonek 118 eogonek 119 Emacron 112 emacron 113 Gbreve 11E gbreve 11F " +
            "Idotaccent 130 Imacron 12A imacron 12B Lacute 139 lacute 13A Lcaron 13D lcaron 13E Nacute 143 nacute 144 Ncaron 147 " +
            "ncaron 148 Ohungarumlaut 150 ohungarumlaut 151 Omacron 14C omacron 14D Racute 154 racute 155 Rcaron 158 rcaron 159 " +
            "Sacute 15A sacute 15B Scedilla 15E scedilla 15F Tcaron 164 tcaron 165 Tcedilla 162 tcedilla 163 Uring 16E uring 16F " +
            "Uhungarumlaut 170 uhungarumlaut 171 Umacron 16A umacron 16B Zacute 179 zacute 17A Zdotaccent 17B zdotaccent 17C " +
            "commaaccent F6C3 Gcommaaccent 122 gcommaaccent 123 Kcommaaccent 136 kcommaaccent 137 Lcommaaccent 13B lcommaaccent 13C " +
            "Ncommaaccent 145 ncommaaccent 146 Rcommaaccent 156 rcommaaccent 157 Scommaaccent 218 scommaaccent 219 " +
            "Tcommaaccent 21A tcommaaccent 21B Iogonek 12E iogonek 12F Uogonek 172 uogonek 173 Amacron 100 amacron 101 " +
            "estimated 212E onedotenleader 2024 twodotenleader 2025 numero 2116 afii61352 2116 afii00208 2015 " +
            "openbullet 25E6 filledbox 25A0 H22073 25A1 square 25A1 triagup 25B2 triagdn 25BC circle 25CB");
        string latin = "Agrave Aacute Acircumflex Atilde Adieresis Aring AE Ccedilla Egrave Eacute Ecircumflex Edieresis Igrave Iacute " +
            "Icircumflex Idieresis Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis multiply Oslash Ugrave Uacute Ucircumflex " +
            "Udieresis Yacute Thorn germandbls agrave aacute acircumflex atilde adieresis aring ae ccedilla egrave eacute ecircumflex " +
            "edieresis igrave iacute icircumflex idieresis eth ntilde ograve oacute ocircumflex otilde odieresis divide oslash ugrave " +
            "uacute ucircumflex udieresis yacute thorn ydieresis";
        var list = latin.Split(' ');
        for (int i = 0; i < list.Length; i++) d[list[i]] = ((char)(0xC0 + i)).ToString();
        return d;
    }

    /// The Unicode text for a glyph name: AGL names, uniXXXX, uXXXX[XX], ligature names joined by '_', suffixes after '.'.
    public static string? ToUnicode(string name)
    {
        if (names.TryGetValue(name, out var s)) return s;
        int dot = name.IndexOf('.');
        if (dot > 0) return ToUnicode(name[..dot]);
        if (name.Contains('_'))
        {
            var parts = name.Split('_').Select(ToUnicode).ToList();
            return parts.All(p => p != null) ? string.Concat(parts) : null;
        }
        if (name.StartsWith("uni") && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 3; i + 4 <= name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, null, out var v)) return null;
                sb.Append((char)v);
            }
            return sb.ToString();
        }
        if (name.StartsWith('u') && name.Length is >= 5 and <= 7 && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, null, out var cp) && cp <= 0x10FFFF)
            return char.ConvertFromUtf32(cp is >= 0xD800 and <= 0xDFFF ? 0xFFFD : cp);
        return null;
    }

    /// StandardEncoding's codes that differ from ASCII/Latin-1.
    static readonly Dictionary<int, char> standardHigh = new()
    {
        [0x27] = '’', [0x60] = '‘', [0xA1] = '¡', [0xA2] = '¢', [0xA3] = '£', [0xA4] = '⁄', [0xA5] = '¥', [0xA6] = 'ƒ',
        [0xA7] = '§', [0xA8] = '¤', [0xA9] = '\'', [0xAA] = '“', [0xAB] = '«', [0xAC] = '‹', [0xAD] = '›', [0xAE] = 'ﬁ',
        [0xAF] = 'ﬂ', [0xB1] = '–', [0xB2] = '†', [0xB3] = '‡', [0xB4] = '·', [0xB6] = '¶', [0xB7] = '•',
        [0xB8] = '‚', [0xB9] = '„', [0xBA] = '”', [0xBB] = '»', [0xBC] = '…', [0xBD] = '‰', [0xBF] = '¿',
        [0xC1] = '`', [0xC2] = '´', [0xC3] = 'ˆ', [0xC4] = '˜', [0xC5] = '¯', [0xC6] = '˘', [0xC7] = '˙', [0xC8] = '¨',
        [0xCA] = '˚', [0xCB] = '¸', [0xCD] = '˝', [0xCE] = '˛', [0xCF] = 'ˇ', [0xD0] = '—', [0xE1] = 'Æ',
        [0xE3] = 'ª', [0xE8] = 'Ł', [0xE9] = 'Ø', [0xEA] = 'Œ', [0xEB] = 'º', [0xF1] = 'æ', [0xF5] = 'ı', [0xF8] = 'ł',
        [0xF9] = 'ø', [0xFA] = 'œ', [0xFB] = 'ß',
    };

    /// A base encoding's character for a code.
    public static string? BaseChar(string? encoding, int code)
    {
        switch (encoding)
        {
            case "MacRomanEncoding":
                return code < 128 ? ((char)code).ToString() : DocEncoding.CodePage(10000).GetString([(byte)code]);
            case "StandardEncoding":
                if (standardHigh.TryGetValue(code, out var c)) return c.ToString();
                if (code is >= 32 and < 127) return ((char)code).ToString();
                return null;
            case "PDFDocEncoding" or "MacExpertEncoding" or "Symbol":
                return code is >= 32 and < 127 ? ((char)code).ToString() : null;
            default:
                // WinAnsiEncoding is Windows-1252, except that a few unused codes show as bullets.
                if (code is >= 32 and < 127) return ((char)code).ToString();
                if (code is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D) return "•";
                if (code < 32) return null;
                return DocEncoding.CodePage(1252).GetString([(byte)code]);
        }
    }

    /// Widths of the standard 14 fonts for ASCII 32…126 (for files that don't embed widths).
    public static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    public static readonly int[] TimesWidths =
    [
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278, 500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
        278, 278, 564, 564, 564, 444, 921, 722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, 556, 722, 667, 556,
        611, 722, 722, 944, 722, 722, 611, 333, 278, 333, 469, 500, 333, 444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778,
        500, 500, 500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444, 480, 200, 480, 541,
    ];
}

/// What text extraction needs from a font: code splitting, Unicode, widths and style.
sealed class PdfTextFont
{
    public bool Bold, Italic, Vertical;
    /// Glyph-space units per text-space unit (1000 for all but Type 3 fonts).
    public double Units = 1000;
    /// Type 3 fonts: the size scale of the font matrix relative to the usual 1/1000.
    public double SizeScale = 1;
    bool twoByte;
    PdfCMap? toUnicode, encodingCMap;
    Encoding? codePage;
    bool utf16;
    readonly string?[] simple = new string?[256];
    readonly Dictionary<int, double> cidWidths = [];
    double defaultWidth = 1000;
    readonly double[] simpleWidths = new double[256];
    Dictionary<int, string>? glyphUnicode;
    int[]? cidToGid;

    public static PdfTextFont Load(PdfFile file, PdfDict font)
    {
        var f = new PdfTextFont();
        var subtype = font.Name("Subtype");
        var baseName = font.Name("BaseFont") ?? "";
        int plus = baseName.IndexOf('+');
        var cleanName = plus == 6 ? baseName[7..] : baseName;
        var lower = cleanName.ToLowerInvariant();
        PdfDict? descriptor = file.Dict(font["FontDescriptor"]);
        PdfDict? cidFont = null;
        if (subtype == "Type0" && file.Resolve(font["DescendantFonts"]) is PdfArray desc && desc.Count > 0)
        {
            cidFont = file.Dict(desc[0]);
            descriptor = file.Dict(cidFont?["FontDescriptor"]) ?? descriptor;
        }
        int flags = (file.Resolve(descriptor?["Flags"]) as PdfNum)?.Int ?? 0;
        double weight = (file.Resolve(descriptor?["FontWeight"]) as PdfNum)?.Value ?? 0;
        double italicAngle = (file.Resolve(descriptor?["ItalicAngle"]) as PdfNum)?.Value ?? 0;
        f.Bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") ||
                 lower.Contains("demi") || weight >= 600 || (flags & (1 << 18)) != 0 || lower.EndsWith(",bold") ||
                 lower.Contains("-w6") || lower.Contains("-w7") || lower.Contains("-w8") || lower.Contains("-w9");
        f.Italic = lower.Contains("italic") || lower.Contains("oblique") || lower.EndsWith("-it") || lower.Contains("-it,") ||
                   Math.Abs(italicAngle) > 0.5 || (flags & (1 << 6)) != 0;

        if (file.Resolve(font["ToUnicode"]) is PdfStream tu && file.Decoded(tu) is { } tuData)
        {
            var cmap = PdfCMap.Parse(tuData);
            if (cmap.Unicode.Count > 0 || cmap.Codespaces.Count > 0) f.toUnicode = cmap;
        }

        if (subtype == "Type0")
        {
            f.twoByte = true;
            var encoding = file.Resolve(font["Encoding"]);
            if (encoding is PdfName en)
            {
                var name = en.Value;
                f.Vertical = name.EndsWith("-V");
                if (name.StartsWith("Identity")) { }
                else if (name.Contains("UCS2") || name.Contains("UTF16")) f.utf16 = true;
                else
                {
                    // Legacy CJK encodings: decode the bytes with the matching code page.
                    int cp = name.Contains("RKSJ") || name.StartsWith("90") || name.StartsWith("83pv") ? 932 :
                             name.StartsWith("GBK") || name.StartsWith("GB-") || name.StartsWith("GBpc") || name.StartsWith("GBT") ? 936 :
                             name.StartsWith("B5") || name.StartsWith("ETen") || name.StartsWith("HKscs") || name.StartsWith("ETHK") ? 950 :
                             name.StartsWith("KSC") || name.StartsWith("KSCms") ? 949 : 0;
                    if (name.StartsWith("EUC-") || name == "H" || name == "V") cp = 20932;
                    if (cp != 0) f.codePage = DocEncoding.CodePage(cp);
                }
            }
            else if (encoding is PdfStream es && file.Decoded(es) is { } ed)
            {
                f.encodingCMap = PdfCMap.Parse(ed);
                f.Vertical = (file.Resolve(es.Dict["WMode"]) as PdfNum)?.Int == 1;
            }
            if (cidFont != null)
            {
                f.defaultWidth = (file.Resolve(cidFont["DW"]) as PdfNum)?.Value ?? 1000;
                if (file.Resolve(cidFont["W"]) is PdfArray w)
                {
                    int i = 0;
                    while (i < w.Count)
                    {
                        if (file.Resolve(w[i]) is not PdfNum first) break;
                        if (i + 1 < w.Count && file.Resolve(w[i + 1]) is PdfArray widths)
                        {
                            for (int k = 0; k < widths.Count && k < 65536; k++)
                                if (file.Resolve(widths[k]) is PdfNum wn) f.cidWidths[first.Int + k] = wn.Value;
                            i += 2;
                        }
                        else if (i + 2 < w.Count && file.Resolve(w[i + 1]) is PdfNum last && file.Resolve(w[i + 2]) is PdfNum width)
                        {
                            for (int c = first.Int; c <= last.Int && c - first.Int < 65536; c++) f.cidWidths[c] = width.Value;
                            i += 3;
                        }
                        else break;
                    }
                }
                if (f.toUnicode == null && !f.utf16 && f.codePage == null)
                {
                    // No ToUnicode: map glyphs back through the embedded TrueType font's own cmap.
                    if (file.Resolve(descriptor?["FontFile2"]) is PdfStream ff && file.Decoded(ff) is { } fontData)
                        f.glyphUnicode = TrueTypeReverseCmap(fontData);
                    if (file.Resolve(cidFont["CIDToGIDMap"]) is PdfStream map && file.Decoded(map) is { } md)
                    {
                        f.cidToGid = new int[md.Length / 2];
                        for (int k = 0; k < f.cidToGid.Length; k++) f.cidToGid[k] = md[2 * k] << 8 | md[2 * k + 1];
                    }
                }
            }
            return f;
        }

        // Simple fonts: a base encoding plus Differences.
        string? baseEncoding = null;
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;
        var enc = file.Resolve(font["Encoding"]);
        PdfArray? differences = null;
        if (enc is PdfName encName) baseEncoding = encName.Value;
        else if (enc is PdfDict encDict)
        {
            baseEncoding = encDict.Name("BaseEncoding");
            differences = file.Resolve(encDict["Differences"]) as PdfArray;
        }
        if (baseEncoding == null)
        {
            if (lower.Contains("symbol") || lower.Contains("dingbat")) baseEncoding = "Symbol";
            else if (subtype is "Type1" or "MMType1" && !symbolic) baseEncoding = "StandardEncoding";
            else baseEncoding = "WinAnsiEncoding";
        }
        for (int c = 0; c < 256; c++) f.simple[c] = PdfGlyphs.BaseChar(baseEncoding, c);
        if (differences != null)
        {
            int code = 0;
            foreach (var item in differences.Items)
            {
                var o = file.Resolve(item);
                if (o is PdfNum n) code = n.Int;
                else if (o is PdfName gn)
                {
                    if (code is >= 0 and < 256)
                    {
                        var u = PdfGlyphs.ToUnicode(gn.Value);
                        if (u != null) f.simple[code] = u;
                        else if (gn.Value is ".notdef") f.simple[code] = null;
                    }
                    code++;
                }
            }
        }

        int firstChar = (file.Resolve(font["FirstChar"]) as PdfNum)?.Int ?? 0;
        if (file.Resolve(font["Widths"]) is PdfArray widthsArray)
        {
            double missing = (file.Resolve(descriptor?["MissingWidth"]) as PdfNum)?.Value ?? 0;
            for (int c = 0; c < 256; c++) f.simpleWidths[c] = missing;
            for (int k = 0; k < widthsArray.Count; k++)
                if (firstChar + k is >= 0 and < 256 && file.Resolve(widthsArray[k]) is PdfNum wn) f.simpleWidths[firstChar + k] = wn.Value;
        }
        else
        {
            // The standard 14 fonts have well-known widths.
            int[]? table = lower.Contains("courier") ? null : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") ? PdfGlyphs.TimesWidths : PdfGlyphs.HelveticaWidths;
            for (int c = 0; c < 256; c++) f.simpleWidths[c] = table == null ? 600 : c is >= 32 and <= 126 ? table[c - 32] : 556;
        }
        if (subtype == "Type3" && file.Resolve(font["FontMatrix"]) is PdfArray fm && fm.Count >= 4 && file.Resolve(fm[0]) is PdfNum a0 && a0.Value != 0)
        {
            f.Units = 1 / Math.Abs(a0.Value);
            double d = (file.Resolve(fm[3]) as PdfNum)?.Value ?? a0.Value;
            f.SizeScale = Math.Abs(d) * 1000;
        }
        return f;
    }

    /// Splits a shown string into glyphs: (Unicode text or null, width in text-space units, is a single-byte space).
    public List<(string? Text, double Width, bool Space)> Decode(byte[] s)
    {
        var result = new List<(string?, double, bool)>(s.Length);
        if (!twoByte)
        {
            foreach (var b in s)
            {
                var text = toUnicode?.ToUnicode(b, 1) ?? simple[b];
                result.Add((text, simpleWidths[b] / Units, b == 32));
            }
            return result;
        }
        if (codePage != null && toUnicode == null)
        {
            // Legacy multi-byte encodings: decode character by character (lead bytes take two bytes).
            int i = 0;
            var decoder = codePage.GetDecoder();
            while (i < s.Length)
            {
                int length = 1;
                var chars = new char[4];
                int count = decoder.GetChars(s, i, 1, chars, 0, flush: false);
                if (count == 0 && i + 1 < s.Length)
                {
                    count = decoder.GetChars(s, i + 1, 1, chars, 0, flush: true);
                    length = 2;
                }
                var text = new string(chars, 0, count);
                result.Add((text, (length == 1 ? 500 : 1000) / Units, length == 1 && s[i] == 32));
                i += length;
            }
            return result;
        }
        var codes = (encodingCMap ?? toUnicode)?.Split(s, 2) ?? SplitTwo(s);
        foreach (var (code, length) in codes)
        {
            int cid = encodingCMap?.Cid(code, length) ?? (int)code;
            string? text = toUnicode?.ToUnicode(code, length);
            if (text == null && utf16)
            {
                text = length == 2 ? ((char)code).ToString() : length == 4 ? Encoding.BigEndianUnicode.GetString([(byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code]) : ((char)code).ToString();
            }
            if (text == null && glyphUnicode != null)
            {
                int gid = cidToGid != null ? (cid < cidToGid.Length ? cidToGid[cid] : 0) : cid;
                glyphUnicode.TryGetValue(gid, out text);
            }
            double width = cidWidths.TryGetValue(cid, out var w) ? w : defaultWidth;
            result.Add((text, width / Units, length == 1 && code == 32));
        }
        return result;
    }

    static List<(uint, int)> SplitTwo(byte[] s)
    {
        var result = new List<(uint, int)>(s.Length / 2);
        for (int i = 0; i + 1 < s.Length; i += 2) result.Add(((uint)(s[i] << 8 | s[i + 1]), 2));
        return result;
    }

    /// Glyph index → Unicode from a TrueType font's cmap (formats 4 and 12).
    static Dictionary<int, string>? TrueTypeReverseCmap(byte[] font)
    {
        try
        {
            int U16(int o) => font[o] << 8 | font[o + 1];
            uint U32(int o) => (uint)(font[o] << 24 | font[o + 1] << 16 | font[o + 2] << 8 | font[o + 3]);
            int tables = U16(4);
            int cmap = -1;
            for (int t = 0; t < tables; t++)
            {
                int rec = 12 + 16 * t;
                if (Encoding.ASCII.GetString(font, rec, 4) == "cmap") { cmap = (int)U32(rec + 8); break; }
            }
            if (cmap < 0) return null;
            var result = new Dictionary<int, string>();
            int count = U16(cmap + 2);
            int best = -1, bestFormat = 0;
            for (int k = 0; k < count; k++)
            {
                int platform = U16(cmap + 4 + 8 * k), encoding = U16(cmap + 6 + 8 * k);
                int offset = cmap + (int)U32(cmap + 8 + 8 * k);
                int format = U16(offset);
                if (platform == 3 && encoding is 1 or 10 || platform == 0)
                    if (format == 12 || (format == 4 && bestFormat != 12)) { best = offset; bestFormat = format; }
            }
            if (best < 0) return null;
            if (bestFormat == 4)
            {
                int segX2 = U16(best + 6);
                int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                for (int s = 0; s < segX2 / 2; s++)
                {
                    int end = U16(ends + 2 * s), start = U16(starts + 2 * s), delta = (short)U16(deltas + 2 * s), rangeOffset = U16(ranges + 2 * s);
                    if (start == 0xFFFF) break;
                    for (int c = start; c <= end; c++)
                    {
                        int gid;
                        if (rangeOffset == 0) gid = (c + delta) & 0xFFFF;
                        else
                        {
                            int at = ranges + 2 * s + rangeOffset + 2 * (c - start);
                            if (at + 1 >= font.Length) continue;
                            gid = U16(at);
                            if (gid != 0) gid = (gid + delta) & 0xFFFF;
                        }
                        if (gid != 0) result.TryAdd(gid, ((char)c).ToString());
                    }
                }
            }
            else
            {
                uint groups = U32(best + 12);
                for (uint g = 0; g < groups && g < 100000; g++)
                {
                    int rec = best + 16 + (int)g * 12;
                    uint start = U32(rec), end = U32(rec + 4), gid = U32(rec + 8);
                    for (uint c = start; c <= end && c - start < 65536; c++)
                        if (c <= 0x10FFFF && !(c is >= 0xD800 and <= 0xDFFF)) result.TryAdd((int)(gid + c - start), char.ConvertFromUtf32((int)c));
                }
            }
            return result;
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentException) { return null; }
    }
}

/// Pulls the text out of a PDF's pages, in reading order, as paragraphs with bold/italic and sizes.
sealed class PdfTextExtractor(PdfFile file)
{
    sealed class Glyph
    {
        public string Text = "";
        public double X, Y, Width, Size, Dir;
        public bool Bold, Italic, Space;
    }

    sealed class TextState
    {
        public double CharSpacing, WordSpacing, Scale = 1, Leading, Rise, Size = 12;
        public int Render;
        public PdfTextFont? Font;
        public TextState Clone() => (TextState)MemberwiseClone();
    }

    readonly Dictionary<PdfObj, PdfTextFont> fonts = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, PdfTextFont> fontsByNum = [];
    List<Glyph> glyphs = [];
    int formDepth;

    public DocModel Extract()
    {
        var pages = new List<List<List<DocRun>>>();
        var sizeCounts = new Dictionary<double, int>();
        foreach (var page in file.Pages)
        {
            glyphs = [];
            try { Run(file.PageContent(page), page.Resources, PdfMatrix.Identity, 0); }
            catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidCastException) { }
            var lines = Lines(glyphs, page.Rotate);
            foreach (var g in glyphs) sizeCounts[Math.Round(g.Size)] = sizeCounts.GetValueOrDefault(Math.Round(g.Size)) + 1;
            pages.Add(lines);
        }
        double body = sizeCounts.Count > 0 ? sizeCounts.MaxBy(kv => kv.Value).Key : 12;
        var model = new DocModel();
        for (int p = 0; p < pages.Count; p++)
        {
            if (p > 0 && pages[p].Count > 0 && model.Blocks.Count > 0) model.Blocks.Add(new DocParagraph());
            foreach (var line in pages[p])
            {
                var paragraph = new DocParagraph();
                foreach (var run in line)
                {
                    if (run.Size is { } size && Math.Abs(size - body) < 1.5) run.Size = null;
                    else if (run.Size is { } s) run.Size = Math.Round(s * 2) / 2;
                }
                paragraph.Runs.AddRange(line);
                model.Blocks.Add(paragraph);
            }
        }
        while (model.Blocks.Count > 0 && model.Blocks[^1] is DocParagraph { Runs.Count: 0 }) model.Blocks.RemoveAt(model.Blocks.Count - 1);
        model.Normalize();
        return model;
    }

    PdfTextFont? Font(PdfObj? fontObj)
    {
        if (fontObj == null) return null;
        if (fontObj is PdfRef r && fontsByNum.TryGetValue(r.Num, out var cachedByNum)) return cachedByNum;
        var resolved = file.Resolve(fontObj);
        if (resolved is not PdfDict dict) return null;
        if (fonts.TryGetValue(dict, out var cached)) return cached;
        var font = PdfTextFont.Load(file, dict);
        fonts[dict] = font;
        if (fontObj is PdfRef rr) fontsByNum[rr.Num] = font;
        return font;
    }

    void Run(byte[] content, PdfObj? resourcesObj, PdfMatrix ctm, int depth)
    {
        var resources = file.Dict(resourcesObj);
        var fontResources = file.Dict(resources?["Font"]);
        var xobjects = file.Dict(resources?["XObject"]);
        var stack = new Stack<(PdfMatrix Ctm, TextState State)>();
        var state = new TextState();
        var tm = PdfMatrix.Identity;
        var tlm = PdfMatrix.Identity;
        var lexer = new PdfLexer(content);
        var operands = new List<PdfObj>(8);
        int ops = 0;

        double Num(int i) => i < operands.Count && operands[i] is PdfNum n ? n.Value : 0;

        while (true)
        {
            var o = lexer.Next(resolveRefs: false);
            if (o == null) break;
            if (o is not PdfKeyword k) { operands.Add(o); if (operands.Count > 64) operands.RemoveAt(0); continue; }
            if (++ops > 5_000_000) break;
            switch (k.Value)
            {
                case "q":
                    stack.Push((ctm, state.Clone()));
                    break;
                case "Q":
                    if (stack.Count > 0) (ctm, state) = stack.Pop();
                    break;
                case "cm":
                    if (operands.Count >= 6) ctm = new PdfMatrix(Num(0), Num(1), Num(2), Num(3), Num(4), Num(5)) * ctm;
                    break;
                case "BT":
                    tm = tlm = PdfMatrix.Identity;
                    break;
                case "Tc": state.CharSpacing = Num(0); break;
                case "Tw": state.WordSpacing = Num(0); break;
                case "Tz": state.Scale = Num(0) / 100; break;
                case "TL": state.Leading = Num(0); break;
                case "Ts": state.Rise = Num(0); break;
                case "Tr": state.Render = (int)Num(0); break;
                case "Tf":
                    if (operands.Count >= 2 && operands[0] is PdfName fn)
                    {
                        state.Font = Font(fontResources?[fn.Value]);
                        state.Size = Num(1);
                    }
                    break;
                case "Td":
                    tlm = new PdfMatrix(1, 0, 0, 1, Num(0), Num(1)) * tlm;
                    tm = tlm;
                    break;
                case "TD":
                    state.Leading = -Num(1);
                    tlm = new PdfMatrix(1, 0, 0, 1, Num(0), Num(1)) * tlm;
                    tm = tlm;
                    break;
                case "Tm":
                    if (operands.Count >= 6) tm = tlm = new PdfMatrix(Num(0), Num(1), Num(2), Num(3), Num(4), Num(5));
                    break;
                case "T*":
                    tlm = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading) * tlm;
                    tm = tlm;
                    break;
                case "Tj":
                    if (operands.LastOrDefault() is PdfStr tj) Show(tj.Bytes, state, ref tm, ctm);
                    break;
                case "'":
                    tlm = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading) * tlm;
                    tm = tlm;
                    if (operands.LastOrDefault() is PdfStr q1) Show(q1.Bytes, state, ref tm, ctm);
                    break;
                case "\"":
                    if (operands.Count >= 3)
                    {
                        state.WordSpacing = Num(0);
                        state.CharSpacing = Num(1);
                    }
                    tlm = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading) * tlm;
                    tm = tlm;
                    if (operands.LastOrDefault() is PdfStr q2) Show(q2.Bytes, state, ref tm, ctm);
                    break;
                case "TJ":
                    if (operands.LastOrDefault() is PdfArray array)
                        foreach (var item in array.Items)
                        {
                            if (item is PdfStr s) Show(s.Bytes, state, ref tm, ctm);
                            else if (item is PdfNum adjust)
                            {
                                double tx = -adjust.Value / 1000 * state.Size * state.Scale;
                                if (state.Font?.Vertical == true) tm = new PdfMatrix(1, 0, 0, 1, 0, -adjust.Value / 1000 * state.Size) * tm;
                                else tm = new PdfMatrix(1, 0, 0, 1, tx, 0) * tm;
                            }
                        }
                    break;
                case "Do":
                    if (operands.LastOrDefault() is PdfName xn && formDepth < 12 && file.Resolve(xobjects?[xn.Value]) is PdfStream form &&
                        form.Dict.Name("Subtype") == "Form" && file.Decoded(form) is { } formData)
                    {
                        var matrix = file.Resolve(form.Dict["Matrix"]) is PdfArray m && m.Count >= 6
                            ? new PdfMatrix(N(m[0]), N(m[1]), N(m[2]), N(m[3]), N(m[4]), N(m[5])) : PdfMatrix.Identity;
                        formDepth++;
                        try { Run(formData, form.Dict["Resources"] ?? resourcesObj, matrix * ctm, depth + 1); }
                        finally { formDepth--; }
                    }
                    break;
                case "BI":
                    SkipInlineImage(lexer);
                    break;
            }
            operands.Clear();
        }
    }

    double N(PdfObj o) => file.Resolve(o) is PdfNum n ? n.Value : 0;

    static void SkipInlineImage(PdfLexer lexer)
    {
        // Skip the dictionary up to ID, then the binary data up to a whitespace-delimited EI.
        while (true)
        {
            var o = lexer.Next(resolveRefs: false);
            if (o == null) return;
            if (o is PdfKeyword { Value: "ID" }) break;
        }
        lexer.Pos++;
        var data = lexer.Data;
        for (int i = lexer.Pos; i + 1 < lexer.End; i++)
        {
            if (data[i] == 'E' && data[i + 1] == 'I' && (i == 0 || PdfLexer.IsWhite(data[i - 1])) && (i + 2 >= lexer.End || PdfLexer.IsWhite(data[i + 2])))
            {
                lexer.Pos = i + 2;
                return;
            }
        }
        lexer.Pos = lexer.End;
    }

    void Show(byte[] bytes, TextState state, ref PdfMatrix tm, PdfMatrix ctm)
    {
        var font = state.Font;
        if (font == null) return;
        foreach (var (decoded, width, space) in font.Decode(bytes))
        {
            var text = decoded;
            var trm = new PdfMatrix(state.Size * state.Scale, 0, 0, state.Size, 0, state.Rise) * tm * ctm;
            double advance = (width * state.Size + state.CharSpacing + (space ? state.WordSpacing : 0)) * state.Scale;
            double verticalAdvance = width == 0 ? state.Size : width * state.Size + state.CharSpacing + (space ? state.WordSpacing : 0);
            var (x, y) = (trm.E, trm.F);
            double size = Math.Abs(state.Size) * Math.Sqrt((tm * ctm).C * (tm * ctm).C + (tm * ctm).D * (tm * ctm).D) * font.SizeScale;
            var full = tm * ctm;
            // Advance in device space along the baseline direction.
            double dx = advance * full.A, dy = advance * full.B;
            if (font.Vertical) (dx, dy) = (verticalAdvance * full.C, verticalAdvance * full.D);
            if (text != null && text.Length > 0 && !(text.Length == 1 && text[0] < 32 && text[0] != '\t'))
            {
                // Ligature glyphs (ﬁ, ﬂ…) come out as their letters, so the text stays searchable.
                if (text.Length == 1 && text[0] is >= 'ﬀ' and <= 'ﬆ') text = text.Normalize(NormalizationForm.FormKC);
                double skew = full.C * full.A + full.D * full.B;
                glyphs.Add(new Glyph
                {
                    Text = text,
                    X = x,
                    Y = y,
                    Width = Math.Sqrt(dx * dx + dy * dy),
                    Size = size,
                    Bold = font.Bold || state.Render == 2,
                    Italic = font.Italic || Math.Abs(skew) > 0.1 * Math.Abs(full.A * full.D - full.B * full.C),
                    Space = text == " ",
                });
                // Remember the direction for line building; vertical text reads downwards.
                glyphs[^1].Dir = font.Vertical ? Math.Atan2(-full.D, -full.C) : Math.Atan2(full.B, full.A);
            }
            if (font.Vertical) tm = new PdfMatrix(1, 0, 0, 1, 0, -verticalAdvance) * tm;
            else tm = new PdfMatrix(1, 0, 0, 1, advance, 0) * tm;
        }
    }

    /// Groups glyphs into lines in content order, adding spaces at gaps and blank lines at large vertical gaps.
    static List<List<DocRun>> Lines(List<Glyph> glyphs, int rotate)
    {
        var lines = new List<List<DocRun>>();
        if (glyphs.Count == 0) return lines;
        // Work in each glyph's reading frame: rotate so the baseline runs left to right.
        foreach (var g in glyphs)
        {
            double angle = Math.Round(g.Dir / (Math.PI / 2)) * (Math.PI / 2);
            double cos = Math.Cos(-angle), sin = Math.Sin(-angle);
            (g.X, g.Y) = (g.X * cos - g.Y * sin, g.X * sin + g.Y * cos);
        }
        var current = new List<DocRun>();
        Glyph? previous = null;
        double lastLineY = double.NaN, lastLineSize = 12;
        var recent = new List<Glyph>();

        void Append(Glyph g, string text)
        {
            var run = new DocRun(text) { Bold = g.Bold, Italic = g.Italic, Size = Math.Round(g.Size * 2) / 2 };
            if (current.Count > 0 && current[^1].SameStyle(run)) current[^1] = current[^1].With(current[^1].Text + text);
            else current.Add(run);
        }

        void EndLine()
        {
            // Trim trailing spaces.
            while (current.Count > 0)
            {
                var t = current[^1].Text.TrimEnd(' ');
                if (t.Length > 0) { current[^1] = current[^1].With(t); break; }
                current.RemoveAt(current.Count - 1);
            }
            if (current.Count > 0) lines.Add(current);
            current = [];
        }

        foreach (var g in glyphs)
        {
            if (g.Size <= 0.01) g.Size = 1;
            // Fake bold draws the same glyph twice, slightly offset: keep one.
            bool duplicate = !g.Space && g.Width > 0 && recent.Any(r => r.Text == g.Text && r.Width > 0 && Math.Abs(r.X - g.X) < g.Size * 0.15 && Math.Abs(r.Y - g.Y) < g.Size * 0.15);
            if (duplicate) continue;
            recent.Add(g);
            if (recent.Count > 12) recent.RemoveAt(0);

            if (previous != null)
            {
                double dy = g.Y - previous.Y;
                double size = Math.Max(Math.Min(g.Size, previous.Size), 1);
                double gap = g.X - (previous.X + previous.Width);
                bool newLine = Math.Abs(dy) > size * 0.6 || gap < -size * 2.5;
                if (newLine)
                {
                    EndLine();
                    // A blank line where the vertical gap is much bigger than the line height.
                    if (!double.IsNaN(lastLineY))
                    {
                        double step = lastLineY - g.Y;
                        if (step > Math.Max(lastLineSize, g.Size) * 2.1 && lines.Count > 0 && lines[^1].Count > 0) lines.Add([]);
                    }
                    lastLineY = g.Y;
                    lastLineSize = g.Size;
                }
                else if (gap > size * 0.18 && !g.Space && !previous.Space && !EndsWithSpace(current))
                {
                    // Wide gaps inside a line (table columns, tab stops) become tabs, narrower ones spaces.
                    Append(previous, gap > size * 2.5 ? "\t" : " ");
                }
            }
            else
            {
                lastLineY = g.Y;
                lastLineSize = g.Size;
            }
            if (!(g.Space && current.Count == 0)) Append(g, g.Text);
            previous = g;
        }
        EndLine();
        while (lines.Count > 0 && lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    static bool EndsWithSpace(List<DocRun> runs) => runs.Count == 0 || runs[^1].Text.EndsWith(' ') || runs[^1].Text.EndsWith('\t');
}
