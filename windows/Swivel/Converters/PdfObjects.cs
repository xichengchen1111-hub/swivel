using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Swivel;

// A small PDF object model, lexer and serializer (ISO 32000). It replaces PDFKit for reading, assembling and
// rewriting files; rendering pages to pixels uses Windows.Data.Pdf.

abstract class PdfObj
{
    public static readonly PdfNull Null = new();
}

sealed class PdfNull : PdfObj
{
    public override string ToString() => "null";
}

sealed class PdfBool(bool value) : PdfObj
{
    public readonly bool Value = value;
    public override string ToString() => Value ? "true" : "false";
}

sealed class PdfNum : PdfObj
{
    public readonly double Value;
    public readonly bool IsInt;

    public PdfNum(double value) { Value = value; IsInt = Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15; }
    public PdfNum(long value) { Value = value; IsInt = true; }

    public int Int => (int)Math.Clamp(Math.Round(Value), int.MinValue, int.MaxValue);
    public long Long => (long)Math.Round(Value);

    public override string ToString() => Format(Value);

    public static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
        if (Math.Abs(v - Math.Round(v)) < 1e-7 && Math.Abs(v) < 1e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        var s = v.ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

sealed class PdfStr(byte[] bytes, bool hex = false) : PdfObj
{
    public byte[] Bytes = bytes;
    public readonly bool Hex = hex;

    public static PdfStr FromText(string text)
    {
        // PDFDocEncoding covers ASCII; anything else goes as UTF-16BE with a byte order mark.
        if (text.All(c => c is >= ' ' and < (char)127)) return new PdfStr(Encoding.ASCII.GetBytes(text));
        var bytes = new List<byte> { 0xFE, 0xFF };
        bytes.AddRange(Encoding.BigEndianUnicode.GetBytes(text));
        return new PdfStr([.. bytes]);
    }

    /// The string as text: UTF-16BE/UTF-8 with BOM, else PDFDocEncoding (approximated by Latin-1).
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.Latin1.GetString(b);
        }
    }
}

sealed class PdfName(string value) : PdfObj
{
    public readonly string Value = value;
    public override string ToString() => "/" + Value;
    public override bool Equals(object? obj) => obj is PdfName n && n.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

sealed class PdfArray : PdfObj
{
    public readonly List<PdfObj> Items;

    public PdfArray() { Items = []; }
    public PdfArray(IEnumerable<PdfObj> items) { Items = [.. items]; }
    public PdfArray(params double[] numbers) { Items = [.. numbers.Select(n => (PdfObj)new PdfNum(n))]; }

    public int Count => Items.Count;
    public PdfObj this[int i] { get => Items[i]; set => Items[i] = value; }
    public void Add(PdfObj o) => Items.Add(o);
}

sealed class PdfDict : PdfObj
{
    public readonly OrderedDictionary<string, PdfObj> Items = new(StringComparer.Ordinal);

    public PdfObj? this[string key]
    {
        get => Items.TryGetValue(key, out var v) ? v : null;
        set { if (value == null) Items.Remove(key); else Items[key] = value; }
    }

    public bool Has(string key) => Items.ContainsKey(key);
    public void Remove(string key) => Items.Remove(key);

    public string? Name(string key) => this[key] is PdfName n ? n.Value : null;

    public PdfDict Clone()
    {
        var d = new PdfDict();
        foreach (var (k, v) in Items) d.Items[k] = v;
        return d;
    }
}

sealed class PdfStream(PdfDict dict, byte[] data) : PdfObj
{
    public PdfDict Dict = dict;
    /// The encoded bytes, as stored (after decryption).
    public byte[] Data = data;
}

sealed class PdfRef(int num, int gen) : PdfObj
{
    public readonly int Num = num, Gen = gen;
    public override string ToString() => $"{Num} {Gen} R";
    public override bool Equals(object? obj) => obj is PdfRef r && r.Num == Num && r.Gen == Gen;
    public override int GetHashCode() => HashCode.Combine(Num, Gen);
}

/// A content-stream or object-syntax operator such as `Tj` or `obj`.
sealed class PdfKeyword(string value) : PdfObj
{
    public readonly string Value = value;
    public override string ToString() => Value;
}

sealed class PdfFormatException(string message) : Exception(message);

/// Tokens and objects from PDF syntax.
sealed class PdfLexer(byte[] data, int start = 0, int end = -1)
{
    public readonly byte[] Data = data;
    public int Pos = start;
    public readonly int End = end < 0 ? data.Length : end;

    public static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWhite()
    {
        while (Pos < End)
        {
            byte b = Data[Pos];
            if (IsWhite(b)) { Pos++; continue; }
            if (b == '%')
            {
                while (Pos < End && Data[Pos] is not ((byte)'\n' or (byte)'\r')) Pos++;
                continue;
            }
            break;
        }
    }

    public bool AtEnd { get { SkipWhite(); return Pos >= End; } }

    /// Reads the next object; arrays and dictionaries are read whole. Returns null at the end of input.
    /// Operators come back as PdfKeyword; `]`, `>>` and `}` come back as keywords too.
    public PdfObj? Next(bool resolveRefs = true)
    {
        SkipWhite();
        if (Pos >= End) return null;
        byte b = Data[Pos];
        switch (b)
        {
            case (byte)'/':
                return ReadName();
            case (byte)'(':
                return ReadLiteralString();
            case (byte)'<':
                if (Pos + 1 < End && Data[Pos + 1] == '<')
                {
                    Pos += 2;
                    return ReadDict(resolveRefs);
                }
                return ReadHexString();
            case (byte)'>':
                if (Pos + 1 < End && Data[Pos + 1] == '>') { Pos += 2; return new PdfKeyword(">>"); }
                Pos++;
                return new PdfKeyword(">");
            case (byte)'[':
                Pos++;
                return ReadArray(resolveRefs);
            case (byte)']':
                Pos++;
                return new PdfKeyword("]");
            case (byte)'{':
                Pos++;
                return new PdfKeyword("{");
            case (byte)'}':
                Pos++;
                return new PdfKeyword("}");
            case (byte)')':
                Pos++;
                return new PdfKeyword(")");
        }
        if (b is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.')
        {
            var number = ReadNumber();
            if (number == null) return new PdfKeyword(ReadRegular());
            if (resolveRefs && number.IsInt && number.Value >= 0)
            {
                // "n g R" is a reference.
                int save = Pos;
                SkipWhite();
                if (Pos < End && Data[Pos] is >= (byte)'0' and <= (byte)'9')
                {
                    var gen = ReadNumber();
                    SkipWhite();
                    if (gen is { IsInt: true } && Pos < End && Data[Pos] == 'R' && (Pos + 1 >= End || IsWhite(Data[Pos + 1]) || IsDelimiter(Data[Pos + 1])))
                    {
                        Pos++;
                        return new PdfRef(number.Int, gen.Int);
                    }
                }
                Pos = save;
            }
            return number;
        }
        var word = ReadRegular();
        if (word.Length == 0) { Pos++; return new PdfKeyword(((char)b).ToString()); }
        return word switch
        {
            "true" => new PdfBool(true),
            "false" => new PdfBool(false),
            "null" => PdfObj.Null,
            _ => new PdfKeyword(word),
        };
    }

    string ReadRegular()
    {
        int s = Pos;
        while (Pos < End && !IsWhite(Data[Pos]) && !IsDelimiter(Data[Pos])) Pos++;
        return Encoding.Latin1.GetString(Data, s, Pos - s);
    }

    PdfNum? ReadNumber()
    {
        int s = Pos;
        if (Pos < End && Data[Pos] is (byte)'+' or (byte)'-') Pos++;
        bool digits = false, dot = false;
        while (Pos < End)
        {
            byte c = Data[Pos];
            if (c is >= (byte)'0' and <= (byte)'9') { digits = true; Pos++; }
            else if (c == '.' && !dot) { dot = true; Pos++; }
            // "--5" and similar malformed numbers appear in the wild; treat extra signs as part of the number.
            else if (c is (byte)'-' && Pos > s && !digits) Pos++;
            else break;
        }
        if (!digits) { Pos = s; return null; }
        var text = Encoding.Latin1.GetString(Data, s, Pos - s).Replace("--", "-");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) v = 0;
        return dot ? new PdfNum(v) { } : new PdfNum((long)Math.Clamp(v, long.MinValue, long.MaxValue));
    }

    PdfName ReadName()
    {
        Pos++;
        var bytes = new List<byte>();
        while (Pos < End && !IsWhite(Data[Pos]) && !IsDelimiter(Data[Pos]))
        {
            byte c = Data[Pos];
            if (c == '#' && Pos + 2 < End && IsHex(Data[Pos + 1]) && IsHex(Data[Pos + 2]))
            {
                bytes.Add((byte)(Hex(Data[Pos + 1]) * 16 + Hex(Data[Pos + 2])));
                Pos += 3;
                continue;
            }
            bytes.Add(c);
            Pos++;
        }
        var arr = bytes.ToArray();
        // Names are byte strings; most are ASCII, some are UTF-8 (font names in CJK files).
        string text;
        try { text = new UTF8Encoding(false, true).GetString(arr); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(arr); }
        return new PdfName(text);
    }

    static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
    static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    PdfStr ReadLiteralString()
    {
        Pos++;
        var bytes = new List<byte>();
        int depth = 1;
        while (Pos < End)
        {
            byte c = Data[Pos++];
            if (c == '\\')
            {
                if (Pos >= End) break;
                byte e = Data[Pos++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (Pos < End && Data[Pos] == '\n') Pos++;
                        break;
                    case (byte)'\n': break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && Pos < End && Data[Pos] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (Data[Pos++] - '0');
                            bytes.Add((byte)v);
                        }
                        else bytes.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            bytes.Add(c);
        }
        return new PdfStr([.. bytes]);
    }

    PdfStr ReadHexString()
    {
        Pos++;
        var bytes = new List<byte>();
        int high = -1;
        while (Pos < End)
        {
            byte c = Data[Pos++];
            if (c == '>') break;
            if (!IsHex(c)) continue;
            if (high < 0) high = Hex(c);
            else { bytes.Add((byte)(high * 16 + Hex(c))); high = -1; }
        }
        if (high >= 0) bytes.Add((byte)(high * 16));
        return new PdfStr([.. bytes], hex: true);
    }

    PdfArray ReadArray(bool resolveRefs)
    {
        var array = new PdfArray();
        while (true)
        {
            var o = Next(resolveRefs);
            if (o == null) break;
            if (o is PdfKeyword k)
            {
                if (k.Value == "]") break;
                if (k.Value is ">>" or "endobj" or "stream") { Pos -= k.Value.Length; break; }
                continue;
            }
            array.Add(o);
        }
        return array;
    }

    PdfDict ReadDict(bool resolveRefs)
    {
        var dict = new PdfDict();
        while (true)
        {
            var key = Next(resolveRefs);
            if (key == null) break;
            if (key is PdfKeyword k)
            {
                if (k.Value == ">>") break;
                if (k.Value is "endobj" or "stream") { Pos -= k.Value.Length; break; }
                continue;
            }
            if (key is not PdfName name) continue;
            int save = Pos;
            var value = Next(resolveRefs);
            if (value == null) break;
            if (value is PdfKeyword vk)
            {
                if (vk.Value == ">>") { dict[name.Value] = PdfObj.Null; break; }
                Pos = save;
                SkipWhite();
                // A missing value: skip the bad token.
                if (vk.Value is "endobj" or "stream") break;
                continue;
            }
            dict.Items[name.Value] = value;
        }
        return dict;
    }

    /// Searches forward for `pattern`; returns its position or -1.
    public static int Find(byte[] data, string pattern, int from, int to = -1)
    {
        var p = Encoding.ASCII.GetBytes(pattern);
        int limit = (to < 0 ? data.Length : Math.Min(to, data.Length)) - p.Length;
        var span = data.AsSpan();
        for (int i = Math.Max(0, from); i <= limit; )
        {
            int found = span[i..(limit + p.Length)].IndexOf(p);
            if (found < 0) return -1;
            return i + found;
        }
        return -1;
    }

    public static int FindLast(byte[] data, string pattern, int before)
    {
        var p = Encoding.ASCII.GetBytes(pattern);
        int end = Math.Min(before, data.Length);
        return data.AsSpan(0, end).LastIndexOf(p);
    }
}

/// Stream filters (decoding for reading; Flate for writing).
static class PdfFilters
{
    public static List<(string Name, PdfDict? Parms)> Chain(PdfDict dict, Func<PdfObj?, PdfObj?> resolve)
    {
        var result = new List<(string, PdfDict?)>();
        var filter = resolve(dict["Filter"] ?? dict["F"]);
        var parms = resolve(dict["DecodeParms"] ?? dict["DP"]);
        if (filter is PdfName n) result.Add((n.Value, resolve(parms is PdfArray pa && pa.Count > 0 ? pa[0] : parms) as PdfDict));
        else if (filter is PdfArray a)
            for (int i = 0; i < a.Count; i++)
                if (resolve(a[i]) is PdfName fn)
                    result.Add((fn.Value, parms is PdfArray pa2 && i < pa2.Count ? resolve(pa2[i]) as PdfDict : null));
        return result;
    }

    public static readonly HashSet<string> ImageFilters = ["DCTDecode", "DCT", "JPXDecode", "CCITTFaxDecode", "CCF", "JBIG2Decode"];

    /// Decodes all non-image filters. Returns null when a filter isn't supported (or the data is corrupt).
    /// `stopAtImage` leaves an image codec (DCT, JPX…) and anything after it encoded.
    public static byte[]? Decode(byte[] data, List<(string Name, PdfDict? Parms)> chain, bool stopAtImage = false)
    {
        foreach (var (name, parms) in chain)
        {
            if (ImageFilters.Contains(name))
            {
                if (stopAtImage) return data;
                return null;
            }
            byte[]? next = name switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data), parms),
                "LZWDecode" or "LZW" => Predict(Lzw(data, parms), parms),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "RunLengthDecode" or "RL" => RunLength(data),
                "Crypt" => data,
                _ => null,
            };
            if (next == null) return null;
            data = next;
        }
        return data;
    }

    public static byte[]? Inflate(byte[] data)
    {
        if (data.Length == 0) return [];
        // Most streams have a zlib header; some writers emit raw deflate.
        bool zlib = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
        var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(data);
            using Stream inflater = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int n;
            while ((n = inflater.Read(buffer)) > 0) output.Write(buffer, 0, n);
        }
        catch (InvalidDataException)
        {
            // Truncated or corrupt streams: keep what decoded (viewers do the same).
            if (output.Length == 0) return null;
        }
        return output.ToArray();
    }

    public static byte[] Deflate(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    static byte[]? Predict(byte[]? data, PdfDict? parms)
    {
        if (data == null || parms == null) return data;
        int predictor = (parms["Predictor"] as PdfNum)?.Int ?? 1;
        if (predictor <= 1) return data;
        int colors = Math.Max(1, (parms["Colors"] as PdfNum)?.Int ?? 1);
        int bpc = Math.Max(1, (parms["BitsPerComponent"] as PdfNum)?.Int ?? 8);
        int columns = Math.Max(1, (parms["Columns"] as PdfNum)?.Int ?? 1);
        int bpp = Math.Max(1, (colors * bpc + 7) / 8);
        int rowLength = (colors * bpc * columns + 7) / 8;
        if (predictor == 2)
        {
            // TIFF predictor (8-bit components only).
            if (bpc != 8) return data;
            var output = (byte[])data.Clone();
            for (int row = 0; row + rowLength <= output.Length; row += rowLength)
                for (int i = bpp; i < rowLength; i++) output[row + i] = (byte)(output[row + i] + output[row + i - bpp]);
            return output;
        }
        // PNG predictors: each row starts with its filter type.
        var result = new MemoryStream(data.Length);
        var previous = new byte[rowLength];
        var current = new byte[rowLength];
        int pos = 0;
        while (pos < data.Length)
        {
            int type = data[pos++];
            int n = Math.Min(rowLength, data.Length - pos);
            Array.Clear(current);
            Array.Copy(data, pos, current, 0, n);
            pos += n;
            for (int i = 0; i < rowLength; i++)
            {
                int left = i >= bpp ? current[i - bpp] : 0;
                int up = previous[i];
                int upLeft = i >= bpp ? previous[i - bpp] : 0;
                current[i] = type switch
                {
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + (left + up) / 2),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => current[i],
                };
            }
            result.Write(current, 0, n);
            (previous, current) = (current, previous);
        }
        return result.ToArray();
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (var c in data)
        {
            if (c == '>') break;
            int v = c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;
            if (v < 0) continue;
            if (high < 0) high = v;
            else { output.Add((byte)(high * 16 + v)); high = -1; }
        }
        if (high >= 0) output.Add((byte)(high * 16));
        return [.. output];
    }

    static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        int i = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~') i = 2;
        var group = new int[5];
        int count = 0;
        for (; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == '~') break;
            if (PdfLexer.IsWhite(c)) continue;
            if (c == 'z' && count == 0) { output.AddRange([0, 0, 0, 0]); continue; }
            if (c is < (byte)'!' or > (byte)'u') continue;
            group[count++] = c - '!';
            if (count == 5)
            {
                long v = 0;
                for (int k = 0; k < 5; k++) v = v * 85 + group[k];
                output.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
                count = 0;
            }
        }
        if (count > 1)
        {
            for (int k = count; k < 5; k++) group[k] = 84;
            long v = 0;
            for (int k = 0; k < 5; k++) v = v * 85 + group[k];
            for (int k = 0; k < count - 1; k++) output.Add((byte)(v >> (24 - 8 * k)));
        }
        return [.. output];
    }

    static byte[] RunLength(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int n = data[i++];
            if (n == 128) break;
            if (n < 128)
            {
                int len = Math.Min(n + 1, data.Length - i);
                for (int k = 0; k < len; k++) output.Add(data[i + k]);
                i += len;
            }
            else if (i < data.Length)
            {
                byte b = data[i++];
                for (int k = 0; k < 257 - n; k++) output.Add(b);
            }
        }
        return [.. output];
    }

    static byte[] Lzw(byte[] data, PdfDict? parms)
    {
        int early = (parms?["EarlyChange"] as PdfNum)?.Int ?? 1;
        var output = new MemoryStream();
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int k = 0; k < 256; k++) table.Add([(byte)k]);
            table.Add([]);
            table.Add([]);
        }
        Reset();
        int codeLength = 9;
        long bitBuffer = 0;
        int bits = 0;
        byte[]? previous = null;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= codeLength)
            {
                int code = (int)((bitBuffer >> (bits - codeLength)) & ((1 << codeLength) - 1));
                bits -= codeLength;
                if (code == 256)
                {
                    Reset();
                    codeLength = 9;
                    previous = null;
                    continue;
                }
                if (code == 257) return output.ToArray();
                byte[] entry;
                if (code < table.Count) entry = table[code];
                else if (previous != null) entry = [.. previous, previous[0]];
                else continue;
                output.Write(entry);
                if (previous != null && table.Count < 4096) table.Add([.. previous, entry[0]]);
                previous = entry;
                int threshold = table.Count + early;
                codeLength = threshold >= 2048 ? 12 : threshold >= 1024 ? 11 : threshold >= 512 ? 10 : 9;
            }
        }
        return output.ToArray();
    }
}

/// Writes objects in PDF syntax.
static class PdfSerializer
{
    public static void Write(Stream s, PdfObj obj)
    {
        switch (obj)
        {
            case PdfNull: Ascii(s, "null"); break;
            case PdfBool b: Ascii(s, b.Value ? "true" : "false"); break;
            case PdfNum n: Ascii(s, n.ToString()); break;
            case PdfName n: WriteName(s, n.Value); break;
            case PdfRef r: Ascii(s, $"{r.Num} {r.Gen} R"); break;
            case PdfStr str: WriteString(s, str); break;
            case PdfArray a:
                s.WriteByte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) s.WriteByte((byte)' ');
                    Write(s, a[i]);
                }
                s.WriteByte((byte)']');
                break;
            case PdfDict d:
                Ascii(s, "<<");
                foreach (var (k, v) in d.Items)
                {
                    WriteName(s, k);
                    s.WriteByte((byte)' ');
                    Write(s, v);
                }
                Ascii(s, ">>");
                break;
            case PdfStream st:
                st.Dict["Length"] = new PdfNum(st.Data.Length);
                Write(s, st.Dict);
                Ascii(s, "\nstream\n");
                s.Write(st.Data);
                Ascii(s, "\nendstream");
                break;
            case PdfKeyword k: Ascii(s, k.Value); break;
        }
    }

    public static byte[] ToBytes(PdfObj obj)
    {
        var ms = new MemoryStream();
        Write(ms, obj);
        return ms.ToArray();
    }

    public static void Ascii(Stream s, string text)
    {
        Span<byte> buffer = text.Length <= 256 ? stackalloc byte[text.Length] : new byte[text.Length];
        for (int i = 0; i < text.Length; i++) buffer[i] = (byte)text[i];
        s.Write(buffer);
    }

    static void WriteName(Stream s, string name)
    {
        s.WriteByte((byte)'/');
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b < 0x21 || b > 0x7E || b == '#' || PdfLexer.IsDelimiter(b)) Ascii(s, "#" + b.ToString("X2"));
            else s.WriteByte(b);
        }
    }

    static void WriteString(Stream s, PdfStr str)
    {
        var bytes = str.Bytes;
        int binary = bytes.Count(b => b < 32 || b > 126);
        if (str.Hex || binary > bytes.Length / 4)
        {
            s.WriteByte((byte)'<');
            Ascii(s, Convert.ToHexString(bytes));
            s.WriteByte((byte)'>');
            return;
        }
        s.WriteByte((byte)'(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\': s.WriteByte((byte)'\\'); s.WriteByte(b); break;
                case 10: Ascii(s, "\\n"); break;
                case 13: Ascii(s, "\\r"); break;
                default:
                    if (b < 32 || b > 126) Ascii(s, "\\" + Convert.ToString(b, 8).PadLeft(3, '0'));
                    else s.WriteByte(b);
                    break;
            }
        }
        s.WriteByte((byte)')');
    }
}
