using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel.Tests;

/// The QR encoder / reader and the 1D barcode reader (Qr/QrCodec*.cs, Qr/BarcodeReader.cs).
[TestClass]
public sealed class QrCodecTests
{
    // MARK: Published test vectors

    [TestMethod]
    public void EncoderMatchesPublishedCodewords()
    {
        // ISO/IEC 18004 Annex I: "01234567" as 1-M.
        var segments = new List<QrSegment> { QrSegment.Numeric("01234567") };
        Assert.AreEqual(1, QrEncoder.ChooseVersion(segments, QrErrorCorrection.M));
        CollectionAssert.AreEqual(Hex("10 20 0C 56 61 80 EC 11 EC 11 EC 11 EC 11 EC 11"),
            QrEncoder.DataCodewords(segments, QrErrorCorrection.M, 1));
        var all = QrEncoder.Codewords(segments, QrErrorCorrection.M, out int version);
        Assert.AreEqual(1, version);
        CollectionAssert.AreEqual(Hex("A5 24 D4 C1 ED 36 C7 87 2C 55"), all[16..]);

        // "HELLO WORLD" as 1-M (alphanumeric), data and error correction codewords.
        var hello = new List<QrSegment> { QrSegment.Alphanumeric("HELLO WORLD") };
        CollectionAssert.AreEqual(Hex("20 5B 0B 78 D1 72 DC 4D 43 40 EC 11 EC 11 EC 11"),
            QrEncoder.DataCodewords(hello, QrErrorCorrection.M, 1));
        CollectionAssert.AreEqual(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 },
            QrEncoder.Codewords(hello, QrErrorCorrection.M, out _)[16..]);

        // The public encoder picks numeric / alphanumeric / byte mode the same way.
        Assert.AreEqual(QrMode.Numeric, QrEncoder.SegmentsFor("01234567")[0].Mode);
        Assert.AreEqual(QrMode.Alphanumeric, QrEncoder.SegmentsFor("HELLO WORLD")[0].Mode);
        Assert.AreEqual(QrMode.Byte, QrEncoder.SegmentsFor("hello")[0].Mode);
        Assert.AreEqual(21, QrCodec.Encode("01234567").Size);
    }

    [TestMethod]
    public void FormatAndVersionInformationMatchTheStandard()
    {
        // Annex C, Table C.1 (masked format information) and Annex D, Table D.1 (version information).
        string[] l = ["111011111000100", "111001011110011", "111110110101010", "111100010011101", "110011000101111", "110001100011000", "110110001000001", "110100101110110"];
        for (int mask = 0; mask < 8; mask++) Assert.AreEqual(Convert.ToInt32(l[mask], 2), QrTables.FormatBits(QrErrorCorrection.L, mask));
        Assert.AreEqual(Convert.ToInt32("101010000010010", 2), QrTables.FormatBits(QrErrorCorrection.M, 0));
        Assert.AreEqual(Convert.ToInt32("011010101011111", 2), QrTables.FormatBits(QrErrorCorrection.Q, 0));
        Assert.AreEqual(Convert.ToInt32("001011010001001", 2), QrTables.FormatBits(QrErrorCorrection.H, 0));
        Assert.AreEqual(0x07C94, QrTables.VersionBits(7));
        Assert.AreEqual(0x085BC, QrTables.VersionBits(8));
        Assert.AreEqual(0x28C69, QrTables.VersionBits(40));
    }

    [TestMethod]
    public void CapacitiesMatchTheStandard()
    {
        // Table 7: data codewords.
        CollectionAssert.AreEqual(new[] { 19, 16, 13, 9 }, Levels.Select(e => QrTables.DataCodewords(1, e)).ToArray());
        CollectionAssert.AreEqual(new[] { 2956, 2334, 1666, 1276 }, Levels.Select(e => QrTables.DataCodewords(40, e)).ToArray());
        Assert.AreEqual(216, QrTables.DataCodewords(10, QrErrorCorrection.M));
        Assert.AreEqual(62, QrTables.DataCodewords(5, QrErrorCorrection.Q));
        Assert.AreEqual(60, QrTables.DataCodewords(6, QrErrorCorrection.H));
        for (int v = 1; v <= 40; v++)
        {
            Assert.AreEqual(QrTables.RawDataModules(v), QrLayout.DataOrder(v).Length);
            Assert.AreEqual(QrTables.RawDataModules(v), QrLayout.FunctionMask(v).Count(f => !f), $"version {v}");
        }
        // Table 7: character capacities at the limits of versions 1 and 40.
        Assert.AreEqual(1, QrEncoder.ChooseVersion([QrSegment.Numeric(new string('7', 41))], QrErrorCorrection.L));
        Assert.AreEqual(2, QrEncoder.ChooseVersion([QrSegment.Numeric(new string('7', 42))], QrErrorCorrection.L));
        Assert.AreEqual(1, QrEncoder.ChooseVersion([QrSegment.Alphanumeric(new string('A', 25))], QrErrorCorrection.L));
        Assert.AreEqual(1, QrEncoder.ChooseVersion([QrSegment.Bytes(new byte[17])], QrErrorCorrection.L));
        Assert.AreEqual(40, QrEncoder.ChooseVersion([QrSegment.Numeric(new string('7', 7089))], QrErrorCorrection.L));
        Assert.AreEqual(0, QrEncoder.ChooseVersion([QrSegment.Numeric(new string('7', 7090))], QrErrorCorrection.L));
        Assert.AreEqual(40, QrEncoder.ChooseVersion([QrSegment.Alphanumeric(new string('A', 4296))], QrErrorCorrection.L));
        int[] bytes = [2953, 2331, 1663, 1273];
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(bytes[i], QrCodec.ByteCapacity(Levels[i]));
            Assert.AreEqual(40, QrCodec.Encode(new string('a', bytes[i]), Levels[i]).Version);
            Assert.ThrowsExactly<ToolError>(() => QrCodec.Encode(new string('a', bytes[i] + 1), Levels[i]));
        }
    }

    [TestMethod]
    public void AlignmentTableMatchesTheSpacingRule()
    {
        // Independent check of the typed-in Table E.1: evenly spaced from the far side, last at size − 7.
        for (int v = 2; v <= 40; v++)
        {
            int count = v / 7 + 2, size = QrTables.Size(v);
            int step = (v * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var expected = new int[count];
            expected[0] = 6;
            for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step) expected[i] = pos;
            CollectionAssert.AreEqual(expected, QrTables.AlignmentPositions[v], $"version {v}");
        }
    }

    [TestMethod]
    public void ReedSolomonCorrectsUpToHalfTheCheckCodewords()
    {
        var random = new Random(7);
        foreach (int ec in new[] { 7, 10, 18, 26, 30 })
        {
            var data = new byte[random.Next(10, 120)];
            random.NextBytes(data);
            var block = data.Concat(QrReedSolomon.Encode(data, ec)).ToArray();
            for (int errors = 0; errors <= ec / 2; errors++)
            {
                var damaged = (byte[])block.Clone();
                foreach (int i in Enumerable.Range(0, block.Length).OrderBy(_ => random.Next()).Take(errors)) damaged[i] ^= (byte)random.Next(1, 256);
                Assert.AreEqual(errors, QrReedSolomon.Correct(damaged, ec, ec / 2), $"ec {ec}, {errors} errors");
                CollectionAssert.AreEqual(block, damaged);
            }
            var tooMany = (byte[])block.Clone();
            for (int i = 0; i < ec / 2 + 2; i++) tooMany[i * 2] ^= 0x5A;
            Assert.AreEqual(-1, QrReedSolomon.Correct(tooMany, ec, ec / 2));
        }
    }

    [TestMethod]
    public void EncodedSymbolsHaveFunctionPatternsAndReadableFormat()
    {
        foreach (int length in new[] { 1, 40, 200, 600, 1500 })
        {
            var m = QrCodec.Encode(new string('x', length), QrErrorCorrection.Q);
            int size = m.Size;
            foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            {
                Assert.IsTrue(m[cx, cy] && m[cx - 3, cy - 3] && m[cx + 3, cy + 3], "finder corners and centre");
                Assert.IsFalse(m[cx - 2, cy] || m[cx + 2, cy], "finder light ring");
            }
            for (int i = 8; i < size - 8; i++) { Assert.AreEqual(i % 2 == 0, m[i, 6]); Assert.AreEqual(i % 2 == 0, m[6, i]); }
            Assert.IsTrue(m[8, size - 8], "dark module");
            var grid = Grid(m);
            Assert.AreEqual(QrErrorCorrection.Q, QrMatrixDecoder.ReadFormat(grid, size)!.Value.Level);
            if (m.Version >= 7) Assert.AreEqual(m.Version, QrMatrixDecoder.ReadVersion(grid, size));
            Assert.AreEqual(new string('x', length), QrMatrixDecoder.Decode(grid, size, out _));
        }
    }

    // MARK: Round trips

    [TestMethod]
    public void RoundTripsEveryLevelModeAndManyLengths()
    {
        var random = new Random(11);
        foreach (var level in Levels)
            foreach (int length in new[] { 0, 1, 2, 3, 7, 16, 17, 33, 61, 100, 151, 260 })
            {
                foreach (var alphabet in new[] { "0123456789", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:", "abcdefghijklmnopqrstuvwxyz0123456789-_~!?&=#" })
                {
                    if (length == 0 && alphabet != "abcdefghijklmnopqrstuvwxyz0123456789-_~!?&=#") continue;
                    var text = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                    var m = QrCodec.Encode(text, level);
                    Assert.AreEqual(text, QrMatrixDecoder.Decode(Grid(m), m.Size, out _), $"{level} {length}");
                    if (length > 0 && (length % 3 == 0 || length > 100)) AssertReads(text, Image(m, 3 + length % 4));
                }
            }
    }

    [TestMethod]
    public void RoundTripsUnicodeText()
    {
        foreach (var text in new[] { "你好，世界！二维码测试", "こんにちは、世界。カタカナ ﾊﾝｶｸ", "😀🎉 emoji 👨‍👩‍👧 ok", "Ünïcödé café — “quotes”", "https://example.com/资料?a=1&b=two#hello", "한국어 텍스트" })
            foreach (var level in Levels)
            {
                var m = QrCodec.Encode(text, level);
                AssertReads(text, Image(m, 5));
            }
    }

    [TestMethod]
    public void RoundTripsUpToVersion40()
    {
        var random = new Random(3);
        foreach (int version in new[] { 7, 10, 14, 20, 27, 33, 40 })
        {
            int capacity = QrTables.DataCodewords(version, QrErrorCorrection.L) - 3;
            var text = new string(Enumerable.Range(0, capacity).Select(_ => (char)random.Next('a', 'z' + 1)).ToArray());
            var m = QrCodec.Encode(text, QrErrorCorrection.L);
            Assert.AreEqual(version, m.Version);
            AssertReads(text, Image(m, version == 40 ? 2 : 3));
        }
        var max = QrCodec.Encode(new string('é', 1160), QrErrorCorrection.M);
        Assert.AreEqual(40, max.Version);
        AssertReads(new string('é', 1160), Image(max, 4));
    }

    [TestMethod]
    public void EveryMaskDecodes()
    {
        for (int mask = 0; mask < 8; mask++)
            foreach (int length in new[] { 5, 90 })
            {
                var text = "Mask " + mask + new string('#', length);
                var m = QrEncoder.Encode(QrEncoder.SegmentsFor(text), QrErrorCorrection.M, mask)!;
                Assert.AreEqual(mask, QrMatrixDecoder.ReadFormat(Grid(m), m.Size)!.Value.Mask);
                AssertReads(text, Image(m, 4));
            }
    }

    [TestMethod]
    public void TinyAndHugeModulesRead()
    {
        var m = QrCodec.Encode("tiny code", QrErrorCorrection.M);
        AssertReads("tiny code", Image(m, 1, 2));
        AssertReads("tiny code", Image(m, 2));
        AssertReads("tiny code", Image(m, 40));
        var big = QrCodec.Encode(new string('7', 300), QrErrorCorrection.M);
        AssertReads(new string('7', 300), Image(big, 1));
    }

    // MARK: Geometry and image quality

    [TestMethod]
    public void ReadsRotatedMirroredAndScaledCodes()
    {
        var text = "https://example.com/rotate?id=42";
        foreach (var level in new[] { QrErrorCorrection.L, QrErrorCorrection.H })
        {
            var qr = Image(QrCodec.Encode(text, level), 6);
            foreach (double angle in new[] { 0, 7, 15, 30, 45, 60, 90, 135, 180, 200, 270, 333 })
                foreach (bool mirror in new[] { false, true })
                {
                    var canvas = Canvas(700, 700);
                    PasteRotated(canvas, qr, 350, 350, angle, 1, mirror);
                    AssertReads(text, canvas, $"{level} {angle}° mirror {mirror}");
                }
            foreach (double scale in new[] { 0.45, 0.7, 1.3, 2.2 })
            {
                var canvas = Canvas(900, 800);
                PasteRotated(canvas, qr, 420, 410, 12, scale);
                AssertReads(text, canvas, $"scale {scale}");
            }
        }
    }

    [TestMethod]
    public void ReadsBlurredNoisyAndDamagedCodes()
    {
        var text = "Blur and noise survive: 2026";
        var qr = Image(QrCodec.Encode(text, QrErrorCorrection.M), 7);
        foreach (int radius in new[] { 1, 2, 3 })
        {
            var canvas = Canvas(600, 600);
            PasteRotated(canvas, qr, 300, 300, 20);
            AssertReads(text, Blur(canvas, radius), $"blur {radius}");
        }
        foreach (int amplitude in new[] { 40, 80, 110 })
        {
            var canvas = Canvas(600, 600);
            PasteRotated(canvas, qr, 300, 300, -10);
            Noise(canvas, amplitude, amplitude);
            AssertReads(text, canvas, $"noise {amplitude}");
        }
        // Low contrast, uneven light: grey code on a gradient.
        var dim = PhotoBackground(700, 600, 4);
        var faint = Image(QrCodec.Encode(text, QrErrorCorrection.M), 6);
        for (int i = 0; i < faint.Px.Length; i++) faint.Px[i] = (byte)(faint.Px[i] == 0 ? 90 : 170);
        PasteRotated(dim, faint, 330, 300, 33);
        AssertReads(text, dim, "low contrast");

        // A blot over part of an H-level symbol (Reed–Solomon restores it).
        var robust = QrCodec.Encode("Damaged but readable", QrErrorCorrection.H);
        var damaged = Image(robust, 6);
        int side = damaged.W;
        for (int y = side * 45 / 100; y < side * 62 / 100; y++)
            for (int x = side * 40 / 100; x < side * 70 / 100; x++) damaged.Px[y * side + x] = 30;
        AssertReads("Damaged but readable", damaged, "blot");
    }

    [TestMethod]
    public void ReadsPerspectiveSkewedCodes()
    {
        var text = "Perspective: 斜めから撮影";
        foreach (int length in new[] { 0, 120 })
        {
            var payload = text + new string('+', length);
            var qr = Image(QrCodec.Encode(payload, QrErrorCorrection.M), 6);
            (double, double)[][] quads =
            [
                [(150, 120), (620, 160), (600, 640), (130, 580)],
                [(200, 100), (560, 100), (680, 650), (90, 650)],
                [(120, 200), (640, 80), (700, 700), (160, 560)],
                [(300, 90), (660, 330), (380, 700), (60, 400)],
            ];
            foreach (var quad in quads)
            {
                var canvas = Canvas(800, 800);
                PastePerspective(canvas, qr, quad);
                AssertReads(payload, canvas, $"quad {quad[0]} length {length}");
            }
        }
    }

    [TestMethod]
    public void ReadsInvertedCodes()
    {
        var text = "Light on dark";
        var qr = Image(QrCodec.Encode(text, QrErrorCorrection.Q), 6);
        Invert(qr);
        AssertReads(text, qr);
        var canvas = Canvas(600, 600, 20);
        PasteRotated(canvas, qr, 300, 300, 25);
        AssertReads(text, canvas, "rotated inverted");
    }

    [TestMethod]
    public void ReadsSeveralCodesInOneImage()
    {
        var canvas = PhotoBackground(1400, 900, 9);
        string[] texts = ["SWIVEL TEST", "https://example.com/path", "第三个二维码", "4th code, a bit longer than the others to reach version 5 or so"];
        PasteRotated(canvas, Image(QrCodec.Encode(texts[0]), 5), 230, 230, 0);
        PasteRotated(canvas, Image(QrCodec.Encode(texts[1]), 4), 700, 220, 30);
        PasteRotated(canvas, Image(QrCodec.Encode(texts[2], QrErrorCorrection.H), 4), 1150, 260, -80);
        PasteRotated(canvas, Image(QrCodec.Encode(texts[3]), 4), 350, 650, 180);
        PasteRotated(canvas, BarcodeImage(Ean13Modules("400638133393"), 3, 160), 1000, 680, 0);
        var codes = Read(canvas).ToList();
        CollectionAssert.AreEquivalent(texts.Append("4006381333931").ToArray(), codes.Select(c => c.Text).ToArray());
        Assert.AreEqual(BarcodeFormat.Ean13, codes.Single(c => c.Text == "4006381333931").Format);
        // Reading order: top row first, left to right.
        Assert.IsLessThan(codes.IndexOf(codes.Single(c => c.Text == texts[3])), codes.IndexOf(codes.Single(c => c.Text == texts[1])));
        var first = codes.Single(c => c.Text == texts[0]).Bounds;
        Assert.IsTrue(first.X > 170 && first.X < 186 && first.Right > 274 && first.Right < 290 && first.Y > 170 && first.Bottom < 290, $"bounds {first}");
    }

    // MARK: Data modes and character sets

    [TestMethod]
    public void DecodesEciKanjiHanziAndGuessedCharacterSets()
    {
        string Roundtrip(params QrSegment[] segments)
        {
            var m = QrEncoder.Encode(segments, QrErrorCorrection.M)!;
            return Read(Image(m, 4)).Single().Text;
        }
        var sjis = QrText.Encoding(932)!;
        var gb = QrText.Encoding(936)!;
        Assert.AreEqual("Grüße 你好", Roundtrip(QrSegment.Eci(26), QrSegment.Bytes(Encoding.UTF8.GetBytes("Grüße 你好"))));
        Assert.AreEqual("日本語テキスト", Roundtrip(QrSegment.Eci(20), QrSegment.Bytes(sjis.GetBytes("日本語テキスト"))));
        Assert.AreEqual("日本語のテキスト", Roundtrip(QrSegment.Bytes(sjis.GetBytes("日本語のテキスト"))), "Shift JIS without ECI");
        Assert.AreEqual("中文测试内容", Roundtrip(QrSegment.Bytes(gb.GetBytes("中文测试内容"))), "GB 2312 without ECI");
        Assert.AreEqual("café crème", Roundtrip(QrSegment.Bytes(Encoding.Latin1.GetBytes("café crème"))), "ISO-8859-1 without ECI");
        Assert.AreEqual("Ελληνικά", Roundtrip(QrSegment.Eci(9), QrSegment.Bytes(QrText.Encoding(28597)!.GetBytes("Ελληνικά"))));
        Assert.AreEqual("漢字モード", Roundtrip(QrSegment.Kanji("漢字モード")!));
        Assert.AreEqual("汉字模式", Roundtrip(QrSegment.Hanzi("汉字模式")!));
        Assert.AreEqual("ORDER 12345 qty=7 漢字", Roundtrip(QrSegment.Alphanumeric("ORDER "), QrSegment.Numeric("12345"),
            QrSegment.Bytes(Encoding.ASCII.GetBytes(" qty=7 ")), QrSegment.Kanji("漢字")!));
        Assert.IsNull(QrSegment.Kanji("abc"));
    }

    // MARK: 1D barcodes

    [TestMethod]
    public void BarcodeTablesFollowTheSpecs()
    {
        var code128 = BarcodeReader.Code128Patterns;
        Assert.HasCount(107, code128);
        for (int v = 0; v < 106; v++)
        {
            Assert.HasCount(6, code128[v]);
            Assert.AreEqual(11, code128[v].Sum(b => b), $"value {v}");
            Assert.AreEqual(0, (code128[v][0] + code128[v][2] + code128[v][4]) % 2, $"bars of {v} are even");
        }
        Assert.AreEqual(13, code128[106].Sum(b => b));
        Assert.HasCount(107, code128.Select(p => string.Concat(p.Take(6))).Distinct().ToList(), "the stop's first six elements are no symbol");
        Assert.HasCount(107, code128.Select(p => string.Concat(p)).Distinct().ToList());

        var code39 = BarcodeReader.Code39Patterns;
        Assert.HasCount(44, code39.Distinct().ToList());
        for (int i = 0; i < 44; i++)
        {
            Assert.AreEqual(3, System.Numerics.BitOperations.PopCount((uint)code39[i]));
            int wideBars = Enumerable.Range(0, 9).Count(e => e % 2 == 0 && (code39[i] >> (8 - e) & 1) == 1);
            Assert.AreEqual(i is >= 39 and <= 42 ? 0 : 2, wideBars, $"Code 39 character {i}");
        }
        Assert.AreEqual(0x109, code39[10]);  // A = B w b w b W b w B
        Assert.AreEqual(0x094, code39[43]);  // * = b W b w B w B w b

        Assert.IsTrue(BarcodeReader.ItfPatterns.All(p => System.Numerics.BitOperations.PopCount((uint)p) == 2));
        Assert.HasCount(10, BarcodeReader.ItfPatterns.Distinct().ToList());
        Assert.HasCount(20, BarcodeReader.CodabarPatterns.Distinct().ToList());
        Assert.IsTrue(BarcodeReader.CodabarPatterns.All(p => System.Numerics.BitOperations.PopCount((uint)p) is 2 or 3));
        Assert.AreEqual("04210000526", BarcodeReader.ExpandUpcE("04252614")[..11]);
    }

    [TestMethod]
    public void Reads1DBarcodesInEveryOrientation()
    {
        var cases = new (string Modules, string Text, BarcodeFormat Format)[]
        {
            (Ean13Modules("590123412345"), "5901234123457", BarcodeFormat.Ean13),
            (Ean13Modules("978030640615"), "9780306406157", BarcodeFormat.Ean13),
            (Ean13Modules("003600029145"), "036000291452", BarcodeFormat.UpcA),
            (Ean8Modules("9638507"), "96385074", BarcodeFormat.Ean8),
            (UpcEModules("0425261"), "04252614", BarcodeFormat.UpcE),
            (UpcEModules("1123456"), "1123456" + BarcodeReader.EanCheckDigit(BarcodeReader.ExpandUpcE("11234560")[..11]), BarcodeFormat.UpcE),
            (Code128Modules(Code128B("Swivel-128 ok")), "Swivel-128 ok", BarcodeFormat.Code128),
            (Code128Modules(Code128C("0123456789012345")), "0123456789012345", BarcodeFormat.Code128),
            (Code39Modules("CODE-39 TEST"), "CODE-39 TEST", BarcodeFormat.Code39),
            (ItfModules("1234567890"), "1234567890", BarcodeFormat.Itf),
            (CodabarModules("A40156B"), "40156", BarcodeFormat.Codabar),
        };
        foreach (var (modules, text, format) in cases)
            foreach (int module in new[] { 2, 3 })
            {
                var image = BarcodeImage(modules, module, 90);
                foreach (var (name, img) in new[] { ("0°", image), ("90°", Rotate90(image)), ("180°", Rotate180(image)), ("270°", Rotate90(Rotate180(image))) })
                {
                    var codes = Read(img);
                    Assert.HasCount(1, codes, $"{format} {text} {name} module {module}: {string.Join(", ", codes.Select(c => c.Format + " " + c.Text))}");
                    Assert.AreEqual(text, codes[0].Text, $"{format} {name}");
                    Assert.AreEqual(format, codes[0].Format, $"{text} {name}");
                }
            }
        // On a photo, slightly blurred and noisy, with uneven module widths from resampling.
        var photo = PhotoBackground(1000, 700, 21);
        PasteRotated(photo, BarcodeImage(Code128Modules(Code128B("PHOTO 1D")), 3, 140), 500, 350, 0, 1.15);
        var noisy = Blur(photo, 1);
        Noise(noisy, 25, 5);
        Assert.AreEqual("PHOTO 1D", Read(noisy).Single().Text);
    }

    // MARK: Negative cases

    [TestMethod]
    public void ImagesWithoutCodesReadNothing()
    {
        Assert.IsEmpty(Read(Canvas(300, 300)));
        Assert.IsEmpty(Read(Canvas(1, 1)));
        var noise = Canvas(800, 600);
        Noise(noise, 127, 1);
        Assert.IsEmpty(Read(noise));
        for (int seed = 0; seed < 4; seed++) Assert.IsEmpty(Read(PhotoBackground(1200, 900, seed)), $"photo {seed}");
        // Stripes and a checkerboard look like barcodes and finder patterns but are neither.
        var stripes = Canvas(600, 200);
        for (int y = 0; y < 200; y++) for (int x = 0; x < 600; x++) stripes.Px[y * 600 + x] = (byte)((x / 4 + x / 9) % 2 == 0 ? 0 : 255);
        Assert.IsEmpty(Read(stripes));
        var checker = Canvas(512, 512);
        for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) checker.Px[y * 512 + x] = (byte)((x / 8 + y / 8) % 2 == 0 ? 0 : 255);
        Assert.IsEmpty(Read(checker));
        // A QR symbol with its format information scratched out on both copies does not decode.
        var m = QrCodec.Encode("unreadable");
        var grid = Grid(m);
        for (int i = 0; i < 9; i++) { grid[8 * m.Size + i] ^= true; grid[i * m.Size + 8] ^= true; grid[8 * m.Size + m.Size - 1 - i % 8] ^= true; grid[(m.Size - 1 - i % 7) * m.Size + 8] ^= true; }
        Assert.IsNull(QrMatrixDecoder.Decode(grid, m.Size, out _));
    }

    // MARK: Realistic fixtures, files and speed

    [STATestMethod]
    public void ReadsWpfRenderedPhotoFixtures()
    {
        var text = "https://example.com/swivel/fixture?lang=zh-CN&q=二维码";
        var qr = QrCodec.Render(QrCodec.Encode(text), 6);
        foreach (var (angle, skewX, skewY, scale) in new[] { (0.0, 0.0, 0.0, 1.0), (17.0, 8.0, 0.0, 0.8), (-38.0, 0.0, 12.0, 1.1), (110.0, 15.0, 5.0, 0.6), (200.0, -10.0, -10.0, 0.9) })
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var gradient = new LinearGradientBrush(Color.FromRgb(210, 180, 140), Color.FromRgb(70, 90, 110), 35);
                dc.DrawRectangle(gradient, null, new Rect(0, 0, 1200, 900));
                var random = new Random((int)angle + 100);
                for (int i = 0; i < 40; i++)
                {
                    var brush = new SolidColorBrush(Color.FromArgb((byte)random.Next(30, 120), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
                    dc.DrawEllipse(brush, null, new Point(random.Next(1200), random.Next(900)), random.Next(10, 140), random.Next(10, 90));
                }
                dc.DrawText(new FormattedText("Swivel 二维码 fixture 12345", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 48, Brushes.Black, 1), new Point(40, 800));
                var group = new TransformGroup();
                group.Children.Add(new TranslateTransform(-qr.PixelWidth / 2.0, -qr.PixelHeight / 2.0));
                group.Children.Add(new ScaleTransform(scale, scale));
                group.Children.Add(new SkewTransform(skewX, skewY));
                group.Children.Add(new RotateTransform(angle));
                group.Children.Add(new TranslateTransform(620, 430));
                dc.PushTransform(group);
                dc.DrawImage(qr, new Rect(0, 0, qr.PixelWidth, qr.PixelHeight));
                dc.Pop();
            }
            var target = new RenderTargetBitmap(1200, 900, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            var codes = QrCodec.Read(target);
            Assert.HasCount(1, codes, $"angle {angle} skew {skewX}/{skewY}");
            Assert.AreEqual(text, codes[0].Text);

            // The same picture as a JPEG file, read through WIC on a background thread.
            var path = Path.Combine(TestSetup.NewFolder("qrcodec14-fixture"), "photo.jpg");
            var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (var file = File.Create(path)) encoder.Save(file);
            var fromFile = QrCodec.ReadFile(path).GetAwaiter().GetResult();
            Assert.AreEqual(text, fromFile.Single().Text, $"jpeg angle {angle}");
        }
    }

    [STATestMethod]
    public void ReadFileAppliesExifOrientationAndRejectsBrokenFiles()
    {
        var dir = TestSetup.NewFolder("qrcodec14-files");
        var canvas = Canvas(600, 300);
        PasteRotated(canvas, Image(QrCodec.Encode("oriented"), 4), 150, 150, 0);
        var path = Path.Combine(dir, "rotated.jpg");
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);  // 90° clockwise to display
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(canvas), null, metadata, null));
        using (var file = File.Create(path)) encoder.Save(file);
        Assert.AreEqual(6, Images.Orientation(BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)));
        var original = File.ReadAllBytes(path);
        var codes = QrCodec.ReadFile(path).GetAwaiter().GetResult();
        var bounds = codes.Single().Bounds;
        Assert.AreEqual("oriented", codes[0].Text);
        // Upright the image is 300 × 600 and the code, at the left of the stored pixels, is at the top.
        Assert.IsLessThan(300, bounds.Bottom, $"bounds {bounds}");
        Assert.IsGreaterThan(30, bounds.Left, $"bounds {bounds}");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path), "reading never changes the file");

        var broken = Path.Combine(dir, "broken.png");
        File.WriteAllText(broken, "not an image");
        Assert.ThrowsExactly<ToolError>(() => QrCodec.ReadFile(broken).GetAwaiter().GetResult());
        var blank = Path.Combine(dir, "blank.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(ToBitmap(Canvas(300, 300))));
        using (var file = File.Create(blank)) png.Save(file);
        Assert.IsEmpty(QrCodec.ReadFile(blank).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void TwelveMegapixelPhotosReadQuickly()
    {
        var photo = PhotoBackground(4000, 3000, 12);
        PasteRotated(photo, Image(QrCodec.Encode("https://example.com/12mp"), 14), 2400, 1300, 21);
        var bitmap = ToBitmap(photo);
        QrCodec.Read(ToBitmap(Canvas(200, 200)));  // JIT warm-up
        var timer = Stopwatch.StartNew();
        var codes = QrCodec.Read(bitmap);
        long found = timer.ElapsedMilliseconds;
        Assert.AreEqual("https://example.com/12mp", codes.Single().Text);
        Assert.IsLessThan(1000L, found, $"12 MP with a code took {found} ms");

        var empty = ToBitmap(PhotoBackground(4000, 3000, 13));
        timer.Restart();
        Assert.IsEmpty(QrCodec.Read(empty));
        long none = timer.ElapsedMilliseconds;
        Assert.IsLessThan(2000L, none, $"12 MP without a code took {none} ms");

        // A small code in a big photo needs the full-resolution pass.
        var small = PhotoBackground(4000, 3000, 14);
        PasteRotated(small, Image(QrCodec.Encode("small in big"), 3), 900, 2100, 8);
        timer.Restart();
        Assert.AreEqual("small in big", QrCodec.Read(ToBitmap(small)).Single().Text);
        Console.WriteLine($"12 MP: found {found} ms, empty {none} ms, small code {timer.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void ReadIsThreadSafe()
    {
        var images = Enumerable.Range(0, 12).Select(i =>
        {
            var canvas = Canvas(500, 500);
            PasteRotated(canvas, Image(QrCodec.Encode("parallel " + i, Levels[i % 4]), 5), 250, 250, i * 29);
            return ToBitmap(canvas);
        }).ToArray();
        var results = new string[images.Length];
        Parallel.For(0, images.Length * 3, k => { var codes = QrCodec.Read(images[k % images.Length]); results[k % images.Length] = codes.Single().Text; });
        for (int i = 0; i < images.Length; i++) Assert.AreEqual("parallel " + i, results[i]);
    }

    [TestMethod]
    public void ReadsBlockyModulesWithGapsAndTexture()
    {
        // Like a straight-down render of the QR Library's 3D garden: ~24 px per module, each dark module a separate
        // shaded block with a light gap around it, texture inside blocks and on the ground between them.
        foreach (var (text, module, gap, seed) in new[] { ("https://example.com/swivel", 24, 3, 1), ("https://example.com/swivel", 22, 5, 2), ("Garden 花园 block code", 26, 2, 3), (new string('g', 60), 14, 3, 4) })
        {
            var canvas = BlockyImage(text, module, gap, seed);
            AssertReads(text, canvas, $"module {module} gap {gap}");
        }
    }

    // MARK: Mac app parity (Tests/SwivelTests/FileActionsTests.swift)

    [TestMethod]
    public void MacParityRotatedUnicodeUrlAndTwoCodesSideBySide()
    {
        var payload = "https://example.com/你好?q=a%26b";
        var qr = Image(QrCodec.Encode(payload), 8, 0);
        var rotated = Rotate90(qr);
        var codes = Read(rotated);
        Assert.HasCount(1, codes);
        Assert.AreEqual(payload, codes[0].Text);

        var left = Image(QrCodec.Encode("SWIVEL TEST"), 8, 0);
        var right = Image(QrCodec.Encode("https://example.com/path"), 8, 0);
        int width = Math.Max(left.W, right.W) + 100;
        var canvas = Canvas(width * 2, width);
        Blit(canvas, left, 30, 30);
        Blit(canvas, right, width + 30, 30);
        CollectionAssert.AreEquivalent(new[] { "SWIVEL TEST", "https://example.com/path" }, Read(canvas).Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void RenderIsCrispFrozenAndSized()
    {
        var m = QrCodec.Encode("render");
        var image = QrCodec.Render(m, 3, 4);
        Assert.IsTrue(image.IsFrozen);
        Assert.AreEqual((m.Size + 8) * 3, image.PixelWidth);
        var gray = FromBitmap(image);
        Assert.IsTrue(gray.Px.All(p => p is 0 or 255));
        Assert.AreEqual(0, gray.Px[(4 * 3) * gray.W + 4 * 3], "top-left finder corner");
        Assert.AreEqual(255, gray.Px[(4 * 3 - 1) * gray.W + 4 * 3 - 1], "quiet zone");
        Assert.AreEqual(m.Size, QrCodec.Render(m, 1, 0).PixelWidth);
    }

    // MARK: Helpers

    static readonly QrErrorCorrection[] Levels = [QrErrorCorrection.L, QrErrorCorrection.M, QrErrorCorrection.Q, QrErrorCorrection.H];

    static byte[] Hex(string s) => s.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();

    static bool[] Grid(QrMatrix m)
    {
        var grid = new bool[m.Size * m.Size];
        for (int y = 0; y < m.Size; y++) for (int x = 0; x < m.Size; x++) grid[y * m.Size + x] = m[x, y];
        return grid;
    }

    sealed class Gray(byte[] px, int w, int h)
    {
        public readonly byte[] Px = px;
        public readonly int W = w, H = h;
    }

    static Gray Canvas(int w, int h, byte value = 255)
    {
        var px = new byte[w * h];
        Array.Fill(px, value);
        return new Gray(px, w, h);
    }

    static Gray FromBitmap(BitmapSource bitmap)
    {
        var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        var px = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(px, gray.PixelWidth, 0);
        return new Gray(px, gray.PixelWidth, gray.PixelHeight);
    }

    static BitmapSource ToBitmap(Gray g)
    {
        var b = BitmapSource.Create(g.W, g.H, 96, 96, PixelFormats.Gray8, null, g.Px, g.W);
        b.Freeze();
        return b;
    }

    static Gray Image(QrMatrix m, int scale, int quiet = 4) => FromBitmap(QrCodec.Render(m, scale, quiet));

    static IReadOnlyList<DecodedCode> Read(Gray g) => QrCodec.Read(ToBitmap(g));

    static void AssertReads(string text, Gray image, string? context = null)
    {
        var codes = Read(image);
        Assert.HasCount(1, codes, $"{context} expected \"{Short(text)}\", got [{string.Join(", ", codes.Select(c => Short(c.Text)))}]");
        Assert.AreEqual(text, codes[0].Text, context);
        Assert.AreEqual(BarcodeFormat.Qr, codes[0].Format, context);
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    static void Blit(Gray canvas, Gray src, int left, int top)
    {
        for (int y = 0; y < src.H; y++)
            for (int x = 0; x < src.W; x++)
                if (left + x < canvas.W && top + y < canvas.H) canvas.Px[(top + y) * canvas.W + left + x] = src.Px[y * src.W + x];
    }

    static byte Sample(Gray src, double u, double v)
    {
        u -= 0.5; v -= 0.5;
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        double fx = u - x0, fy = v - y0;
        double P(int x, int y) => src.Px[Math.Clamp(y, 0, src.H - 1) * src.W + Math.Clamp(x, 0, src.W - 1)];
        return (byte)Math.Round((P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy) + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy);
    }

    /// Draws `src` centred at (cx, cy), rotated clockwise by `angle` degrees, scaled, optionally mirrored.
    static void PasteRotated(Gray canvas, Gray src, double cx, double cy, double angle, double scale = 1, bool mirror = false)
    {
        double a = angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double half = Math.Sqrt(src.W * src.W + src.H * src.H) / 2 * scale + 2;
        for (int y = Math.Max(0, (int)(cy - half)); y < Math.Min(canvas.H, (int)(cy + half)); y++)
            for (int x = Math.Max(0, (int)(cx - half)); x < Math.Min(canvas.W, (int)(cx + half)); x++)
            {
                double dx = (x + 0.5 - cx) / scale, dy = (y + 0.5 - cy) / scale;
                double u = cos * dx + sin * dy, v = -sin * dx + cos * dy;
                if (mirror) u = -u;
                u += src.W / 2.0; v += src.H / 2.0;
                if (u < 0 || v < 0 || u >= src.W || v >= src.H) continue;
                canvas.Px[y * canvas.W + x] = Sample(src, u, v);
            }
    }

    /// Draws `src` with its corners at the four points (top left, top right, bottom right, bottom left).
    static void PastePerspective(Gray canvas, Gray src, (double X, double Y)[] quad)
    {
        var toSource = QrPerspective.QuadToQuad(quad[0].X, quad[0].Y, quad[1].X, quad[1].Y, quad[2].X, quad[2].Y, quad[3].X, quad[3].Y,
            0, 0, src.W, 0, src.W, src.H, 0, src.H);
        for (int y = 0; y < canvas.H; y++)
            for (int x = 0; x < canvas.W; x++)
            {
                var (u, v) = toSource.Map(x + 0.5, y + 0.5);
                if (u < 0 || v < 0 || u >= src.W || v >= src.H) continue;
                canvas.Px[y * canvas.W + x] = Sample(src, u, v);
            }
    }

    static Gray Blur(Gray g, int radius)
    {
        var tmp = new byte[g.Px.Length];
        var result = new byte[g.Px.Length];
        for (int y = 0; y < g.H; y++)
            for (int x = 0; x < g.W; x++)
            {
                int sum = 0, n = 0;
                for (int k = -radius; k <= radius; k++) { int xx = Math.Clamp(x + k, 0, g.W - 1); sum += g.Px[y * g.W + xx]; n++; }
                tmp[y * g.W + x] = (byte)(sum / n);
            }
        for (int y = 0; y < g.H; y++)
            for (int x = 0; x < g.W; x++)
            {
                int sum = 0, n = 0;
                for (int k = -radius; k <= radius; k++) { int yy = Math.Clamp(y + k, 0, g.H - 1); sum += tmp[yy * g.W + x]; n++; }
                result[y * g.W + x] = (byte)(sum / n);
            }
        return new Gray(result, g.W, g.H);
    }

    static void Noise(Gray g, int amplitude, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < g.Px.Length; i++) g.Px[i] = (byte)Math.Clamp(g.Px[i] + random.Next(-amplitude, amplitude + 1), 0, 255);
    }

    static void Invert(Gray g)
    {
        for (int i = 0; i < g.Px.Length; i++) g.Px[i] = (byte)(255 - g.Px[i]);
    }

    /// A photo-like scene: lighting gradient, soft blobs, fine texture and some hard-edged "text".
    static Gray PhotoBackground(int w, int h, int seed)
    {
        var random = new Random(seed);
        var blobs = Enumerable.Range(0, 12).Select(_ => (X: random.NextDouble() * w, Y: random.NextDouble() * h, R: 50 + random.NextDouble() * Math.Min(w, h) / 3, A: random.Next(-60, 60))).ToArray();
        var px = new byte[w * h];
        double gx = random.NextDouble() * 80, gy = random.NextDouble() * 80;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double v = 150 + gx * x / w - gy * y / h + 12 * Math.Sin(x * 0.05 + y * 0.03) + 6 * Math.Sin(x * 0.31 - y * 0.17);
                foreach (var b in blobs)
                {
                    double d2 = ((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y)) / (b.R * b.R);
                    if (d2 < 4) v += b.A * Math.Exp(-d2);
                }
                px[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        });
        // Rows of short dark strokes, like printed text.
        for (int line = 0; line < 6; line++)
        {
            int top = random.Next(h - 40), left = random.Next(Math.Max(1, w - 400));
            for (int c = 0; c < 30; c++)
            {
                int cw = random.Next(3, 9), ch = random.Next(10, 24), cx = left + c * 12;
                for (int y = top; y < Math.Min(h, top + ch); y++)
                    for (int x = cx; x < Math.Min(w, cx + cw); x++) if ((x + y) % 3 != 0) px[y * w + x] = 35;
            }
        }
        return new Gray(px, w, h);
    }

    /// A straight-down "garden" render: shaded blocks with light gaps and texture on a photo-like ground.
    static Gray BlockyImage(string text, int module, int gap, int seed)
    {
        var m = QrCodec.Encode(text);
        var random = new Random(seed);
        var canvas = PhotoBackground(900, 760, seed + 40);
        int side = (m.Size + 2) * module, left = (900 - side) / 2, top = (760 - side) / 2;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                canvas.Px[(top + y) * 900 + left + x] = (byte)(205 + random.Next(-12, 13) - ((x / 3 + y / 5) % 7 == 0 ? 60 : 0));
        for (int my = 0; my < m.Size; my++)
            for (int mx = 0; mx < m.Size; mx++)
            {
                if (!m[mx, my]) continue;
                int x0 = left + (mx + 1) * module, y0 = top + (my + 1) * module;
                for (int y = gap; y < module - gap / 2; y++)
                    for (int x = gap; x < module - gap / 2; x++)
                    {
                        int v = 55 + random.Next(-25, 26);
                        if (y < gap + 2 || x < gap + 2) v = 115;              // lit bevel on the top and left edges
                        if ((x * 7 + y * 3) % 11 == 0) v += 70;               // small texture marks inside the block
                        canvas.Px[(y0 + y) * 900 + x0 + x] = (byte)Math.Clamp(v, 0, 255);
                    }
            }
        return canvas;
    }

    static Gray Rotate90(Gray g)
    {
        var px = new byte[g.Px.Length];
        for (int y = 0; y < g.H; y++)
            for (int x = 0; x < g.W; x++) px[x * g.H + (g.H - 1 - y)] = g.Px[y * g.W + x];
        return new Gray(px, g.H, g.W);
    }

    static Gray Rotate180(Gray g) => new(g.Px.Reverse().ToArray(), g.W, g.H);

    // 1D symbols, generated from the specifications' module patterns.

    static readonly string[] EanLModules = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    static string EanR(int d) => new(EanLModules[d].Select(c => c == '0' ? '1' : '0').ToArray());
    static string EanG(int d) => new(EanR(d).Reverse().ToArray());
    static readonly string[] EanFirstDigitParity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    static int Mod10(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++) sum += (body[body.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10;
    }

    static string Ean13Modules(string twelve)
    {
        string code = twelve + Mod10(twelve);
        var sb = new StringBuilder("101");
        string parity = EanFirstDigitParity[code[0] - '0'];
        for (int i = 1; i <= 6; i++) sb.Append(parity[i - 1] == 'L' ? EanLModules[code[i] - '0'] : EanG(code[i] - '0'));
        sb.Append("01010");
        for (int i = 7; i <= 12; i++) sb.Append(EanR(code[i] - '0'));
        return sb.Append("101").ToString();
    }

    static string Ean8Modules(string seven)
    {
        string code = seven + Mod10(seven);
        var sb = new StringBuilder("101");
        for (int i = 0; i < 4; i++) sb.Append(EanLModules[code[i] - '0']);
        sb.Append("01010");
        for (int i = 4; i < 8; i++) sb.Append(EanR(code[i] - '0'));
        return sb.Append("101").ToString();
    }

    /// Number system + six digits; the check digit is computed from the UPC-A expansion and shown by parity.
    static string UpcEModules(string seven)
    {
        int check = Mod10(BarcodeReader.ExpandUpcE(seven + "0")[..11]);
        string[] parity0 = ["GGGLLL", "GGLGLL", "GGLLGL", "GGLLLG", "GLGGLL", "GLLGGL", "GLLLGG", "GLGLGL", "GLGLLG", "GLLGLG"];
        string parity = parity0[check];
        if (seven[0] == '1') parity = new string(parity.Select(c => c == 'G' ? 'L' : 'G').ToArray());
        var sb = new StringBuilder("101");
        for (int i = 0; i < 6; i++) sb.Append(parity[i] == 'L' ? EanLModules[seven[i + 1] - '0'] : EanG(seven[i + 1] - '0'));
        return sb.Append("010101").ToString();
    }

    static string WidthsToModules(IEnumerable<int> widths)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (int w in widths) sb.Append(i++ % 2 == 0 ? '1' : '0', w);
        return sb.ToString();
    }

    static List<int> Code128B(string text) => [104, .. text.Select(c => c - 32)];

    static List<int> Code128C(string digits) => [105, .. Enumerable.Range(0, digits.Length / 2).Select(i => int.Parse(digits.Substring(i * 2, 2)))];

    static string Code128Modules(List<int> values)
    {
        int sum = values[0];
        for (int i = 1; i < values.Count; i++) sum += i * values[i];
        var all = values.Append(sum % 103).Append(106);
        return string.Concat(all.Select(v => WidthsToModules(BarcodeReader.Code128Patterns[v].Select(b => (int)b))));
    }

    static string WideNarrow(int bits, int count, int wide) =>
        WidthsToModules(Enumerable.Range(0, count).Select(i => (bits >> (count - 1 - i) & 1) == 1 ? wide : 1));

    static string Code39Modules(string text)
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";
        return string.Join("0", ("*" + text + "*").Select(c => WideNarrow(BarcodeReader.Code39Patterns[chars.IndexOf(c)], 9, 3)));
    }

    static string ItfModules(string digits)
    {
        var widths = new List<int> { 1, 1, 1, 1 };
        for (int i = 0; i < digits.Length; i += 2)
        {
            int a = BarcodeReader.ItfPatterns[digits[i] - '0'], b = BarcodeReader.ItfPatterns[digits[i + 1] - '0'];
            for (int k = 4; k >= 0; k--) { widths.Add((a >> k & 1) == 1 ? 3 : 1); widths.Add((b >> k & 1) == 1 ? 3 : 1); }
        }
        widths.AddRange([3, 1, 1]);
        return WidthsToModules(widths);
    }

    static string CodabarModules(string text)
    {
        const string chars = "0123456789-$:/.+ABCD";
        return string.Join("0", text.Select(c => WideNarrow(BarcodeReader.CodabarPatterns[chars.IndexOf(c)], 7, 3)));
    }

    static Gray BarcodeImage(string modules, int moduleWidth, int height, int quiet = 14)
    {
        int w = (modules.Length + 2 * quiet) * moduleWidth, h = height + 20;
        var g = Canvas(w, h);
        for (int i = 0; i < modules.Length; i++)
        {
            if (modules[i] != '1') continue;
            for (int y = 10; y < 10 + height; y++)
                for (int x = 0; x < moduleWidth; x++) g.Px[y * w + (quiet + i) * moduleWidth + x] = 0;
        }
        return g;
    }
}
