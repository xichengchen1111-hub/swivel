using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Swivel;

namespace Swivel.Tests;

/// The Documents & PDF module: readers, writers, the PDF engine and the menu operations.
[TestClass]
public sealed class DocumentTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
    }

    // ─────────────────────────────── fixtures ───────────────────────────────

    const string Chinese = "中文测试";
    const string Japanese = "日本語のテスト";

    static void Zip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var s = zip.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    /// A .docx written by hand the way Word writes one (styles, numbering, a table).
    static string MakeDocx(string dir)
    {
        var path = Path.Combine(dir, "sample.docx");
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Zip(path,
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/\" TargetMode=\"External\"/></Relationships>"),
            ("word/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles xmlns:w=\"{w}\"><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr></w:style></w:styles>"),
            ("word/numbering.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:numbering xmlns:w=\"{w}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"•\"/></w:lvl></w:abstractNum>" +
                "<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>"),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{w}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" +
                "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t xml:space=\"preserve\">Hello </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>bold</w:t></w:r><w:r><w:t xml:space=\"preserve\"> and </w:t></w:r>" +
                "<w:r><w:rPr><w:i/></w:rPr><w:t>italic</w:t></w:r><w:r><w:t xml:space=\"preserve\"> text.</w:t></w:r><w:r><w:tab/><w:t>Tabbed</w:t><w:br/><w:t>Next line</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>First item</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr></w:pPr><w:r><w:t>Numbered item</w:t></w:r></w:p>" +
                "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>A1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr>" +
                "<w:tr><w:tc><w:p><w:r><w:t>A2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                "<w:p><w:hyperlink r:id=\"rId9\"><w:r><w:t>Example link</w:t></w:r></w:hyperlink></w:p>" +
                $"<w:p><w:r><w:t>{Chinese} {Japanese}</w:t></w:r></w:p>" +
                "<w:p><w:r><w:rPr><w:strike/><w:u w:val=\"single\"/></w:rPr><w:t>struck under</w:t></w:r><w:del><w:r><w:delText>deleted</w:delText></w:r></w:del></w:p>" +
                "<w:sectPr/></w:body></w:document>"));
        return path;
    }

    static string MakeOdt(string dir)
    {
        var path = Path.Combine(dir, "sample.odt");
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";
        Zip(path,
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("META-INF/manifest.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/></manifest:manifest>"),
            ("content.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {ns} office:version=\"1.2\"><office:automatic-styles>" +
                "<style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>" +
                "<style:style style:name=\"T2\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\"/></style:style>" +
                "<text:list-style style:name=\"L1\"><text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"•\"/></text:list-style>" +
                "</office:automatic-styles><office:body><office:text>" +
                "<text:h text:outline-level=\"1\">Title</text:h>" +
                "<text:p>Hello <text:span text:style-name=\"T1\">bold</text:span> and <text:span text:style-name=\"T2\">italic</text:span> text.<text:tab/>Tabbed<text:line-break/>Next line</text:p>" +
                "<text:list text:style-name=\"L1\"><text:list-item><text:p>First item</text:p></text:list-item></text:list>" +
                "<table:table><table:table-column table:number-columns-repeated=\"2\"/><table:table-row><table:table-cell><text:p>A1</text:p></table:table-cell><table:table-cell><text:p>B1</text:p></table:table-cell></table:table-row></table:table>" +
                $"<text:p>{Chinese}<text:s/> {Japanese}</text:p>" +
                "</office:text></office:body></office:document-content>"));
        return path;
    }

    const string Rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Calibri;}}\f0\fs24 {\b\fs32 Title}\par Hello {\b bold} and {\i italic} text.\tab Tabbed\line Next line\par First item\par A1\tab B1\par \u20013?\u25991?\u27979?\u35797? \u26085?\u26412?\u35486?\u12398?\u12486?\u12473?\u12488?\par}";

    static readonly string Html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Ignored title</title><style>p { color: red }</style>" +
        "<script>document.write('nope')</script></head><body><h1>Title</h1><p>Hello <b>bold</b> and <i>italic</i> text.&nbsp;&amp; more</p>" +
        "<ul><li>First item</li><li>Second &lt;item&gt;</li></ul><table><tr><td>A1</td><td>B1</td></tr></table>" +
        $"<p>{Chinese} {Japanese}</p><p><a href=\"https://example.com/\">Example link</a></p></body></html>";

    static readonly string Markdown = $"# Title\nHello **bold** and *italic* text.\tTabbed\n- First item\nA1 B1\n{Chinese} {Japanese}\n`code` and [Example link](https://example.com/) and ~~gone~~ \\*literal\\*\n";

    static readonly string PlainText = $"Title\r\nHello bold and italic text.\tTabbed\r\nFirst item\r\nA1\tB1\r\n{Chinese} {Japanese}\r\n";

    static string Write(string dir, string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    static async Task<string> TextPdf(string dir, string name, string text)
    {
        var source = Write(dir, name + ".txt", text);
        var outputs = await Docs.Convert(source, Docs.Format("pdf"));
        return outputs.Single();
    }

    static DocModel ReadBack(string path) => FileKinds.Ext(path) == "pdf"
        ? new PdfTextExtractor(PdfFile.Open(path)).Extract()
        : Docs.Read(path).GetAwaiter().GetResult();

    static string Flat(DocModel model) => model.PlainText(markers: false);

    static DocRun? RunWith(DocModel model, string text) =>
        model.AllParagraphs.SelectMany(p => p.Runs).FirstOrDefault(r => r.Text.Contains(text));

    // ─────────────────────────────── readers ───────────────────────────────

    [TestMethod]
    public void MarkdownIsInlineOnlyAndKeepsWhitespace()
    {
        var model = DocReaders.ReadMarkdown("# Not a heading\n**bold** and *it* and __b2__ _i2_ `co*de*` [link](http://a.b/c \"t\") ~~s~~ \\*x\\* a*b*c 2*3*4\n  indented\n");
        var text = Flat(model);
        Assert.AreEqual("# Not a heading\nbold and it and b2 i2 co*de* link s *x* abc 234\n  indented", text);
        Assert.IsTrue(RunWith(model, "bold")!.Bold);
        Assert.IsTrue(RunWith(model, "it")!.Italic);
        Assert.IsTrue(RunWith(model, "b2")!.Bold);
        Assert.IsTrue(RunWith(model, "i2")!.Italic);
        Assert.IsTrue(RunWith(model, "co*de*")!.Code);
        Assert.AreEqual("http://a.b/c", RunWith(model, "link")!.Link);
        Assert.IsTrue(RunWith(model, "s")!.Strike);
        Assert.IsFalse(RunWith(model, "*x*")!.Italic);
        Assert.AreEqual(0, model.AllParagraphs.First().Heading);
        Assert.AreEqual("  indented", model.AllParagraphs.Last().Text);
    }

    [TestMethod]
    public void HtmlReaderHandlesCommonMarkup()
    {
        var model = DocReaders.ReadHtml(Encoding.UTF8.GetBytes(Html));
        var text = Flat(model);
        Assert.IsFalse(text.Contains("nope"), text);
        Assert.IsFalse(text.Contains("Ignored"), text);
        Assert.IsFalse(text.Contains("color"), text);
        Assert.IsTrue(text.Contains("Hello bold and italic text. & more"), text);
        Assert.IsTrue(text.Contains("Second <item>"), text);
        Assert.AreEqual(1, model.AllParagraphs.First().Heading);
        Assert.IsTrue(RunWith(model, "bold")!.Bold);
        Assert.IsTrue(RunWith(model, "italic")!.Italic);
        Assert.AreEqual("https://example.com/", RunWith(model, "Example link")!.Link);
        var table = model.Blocks.OfType<DocTable>().Single();
        Assert.AreEqual("A1", table.Rows[0][0].Text);
        Assert.AreEqual("B1", table.Rows[0][1].Text);
        var items = model.AllParagraphs.Where(p => p.List != null).ToList();
        Assert.HasCount(2, items);
        Assert.IsFalse(items[0].List!.Ordered);

        var pre = DocReaders.ReadHtml("<pre>\n  a  b\n c</pre><p>x<br>y</p><ol start=\"3\"><li>three<ol><li>inner</li></ol></li></ol>");
        Assert.AreEqual("  a  b\n c", pre.AllParagraphs.First().Text);
        Assert.IsTrue(pre.AllParagraphs.First().Pre);
        Assert.AreEqual("x\ny", pre.AllParagraphs.ElementAt(1).Text);
        var markers = pre.ListMarkers();
        var three = pre.AllParagraphs.First(p => p.Text == "three");
        Assert.AreEqual("3.", markers[three]);
        Assert.AreEqual(1, pre.AllParagraphs.First(p => p.Text == "inner").List!.Level);
    }

    [TestMethod]
    public void PlainTextDetectsEncodings()
    {
        var dir = TestSetup.NewFolder("txt");
        var utf16 = Path.Combine(dir, "utf16.txt");
        File.WriteAllText(utf16, "Grüße " + Chinese, new UnicodeEncoding(false, true));
        Assert.AreEqual("Grüße " + Chinese, Flat(Docs.Read(utf16).Result));
        var utf16be = Path.Combine(dir, "utf16be.txt");
        File.WriteAllText(utf16be, "Grüße", new UnicodeEncoding(true, true));
        Assert.AreEqual("Grüße", Flat(Docs.Read(utf16be).Result));
        var utf8 = Write(dir, "utf8.text", "Grüße\n" + Japanese);
        Assert.AreEqual("Grüße\n" + Japanese, Flat(Docs.Read(utf8).Result));
        // Not valid UTF-8: the system's ANSI code page.
        byte[] ansi = [0x47, 0x72, 0xFC, 0xDF, 0x65];
        var ansiPath = Path.Combine(dir, "ansi.txt");
        File.WriteAllBytes(ansiPath, ansi);
        Assert.AreEqual(DocEncoding.Ansi.GetString(ansi), Flat(Docs.Read(ansiPath).Result));
    }

    [TestMethod]
    public void DocxReaderKeepsStructure()
    {
        var dir = TestSetup.NewFolder("docx-read");
        var model = Docs.Read(MakeDocx(dir)).Result;
        var paragraphs = model.AllParagraphs.ToList();
        Assert.AreEqual(1, paragraphs[0].Heading);
        Assert.AreEqual("Hello bold and italic text.\tTabbed\nNext line", paragraphs[1].Text);
        Assert.IsFalse(paragraphs[1].Runs[0].Bold);
        Assert.IsTrue(RunWith(model, "bold")!.Bold);
        Assert.IsTrue(RunWith(model, "italic")!.Italic);
        Assert.IsFalse(paragraphs[2].List!.Ordered);
        Assert.IsTrue(paragraphs[3].List!.Ordered);
        Assert.AreEqual("https://example.com/", RunWith(model, "Example link")!.Link);
        var table = model.Blocks.OfType<DocTable>().Single();
        Assert.AreEqual("B2", table.Rows[1][1].Text);
        var struck = RunWith(model, "struck")!;
        Assert.IsTrue(struck.Strike && struck.Underline);
        Assert.IsFalse(Flat(model).Contains("deleted"));
    }

    // ─────────────────────────────── read × write ───────────────────────────────

    [TestMethod]
    public async Task EveryReadWriteCombinationKeepsTextAndStyle()
    {
        var dir = TestSetup.NewFolder("matrix");
        var sources = new List<(string Path, bool Styled)>
        {
            (MakeDocx(dir), true),
            (MakeOdt(dir), true),
            (Write(dir, "sample.rtf", Rtf, Encoding.ASCII), true),
            (Write(dir, "sample.html", Html), true),
            (Write(dir, "sample.md", Markdown), true),
            (Write(dir, "sample.txt", PlainText), false),
        };
        var failures = new List<string>();
        foreach (var (source, styled) in sources)
        {
            foreach (var format in Docs.Formats)
            {
                if (FileKinds.Ext(source) == format.Ext) continue;
                var label = $"{Path.GetExtension(source)} → {format.Name}";
                try
                {
                    var output = (await Docs.Convert(source, format)).Single();
                    Assert.IsTrue(File.Exists(output), label);
                    var back = ReadBack(output);
                    var text = Flat(back);
                    foreach (var expected in new[] { "Title", "Hello", "bold", "italic", "First item", "A1", "B1", Chinese, Japanese })
                        if (!text.Contains(expected)) failures.Add($"{label}: missing “{expected}” in “{text}”");
                    if (styled && format.Ext != "txt")
                    {
                        if (RunWith(back, "bold") is not { Bold: true }) failures.Add($"{label}: “bold” isn't bold");
                        if (RunWith(back, "italic") is not { Italic: true }) failures.Add($"{label}: “italic” isn't italic");
                        if (RunWith(back, "Hello") is { Bold: true }) failures.Add($"{label}: “Hello” became bold");
                    }
                }
                catch (Exception ex) when (ex is not AssertFailedException)
                {
                    failures.Add($"{label}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public async Task DocxPackageIsValid()
    {
        var dir = TestSetup.NewFolder("docx-pkg");
        var output = (await Docs.Convert(Write(dir, "notes.md", Markdown), Docs.Format("docx"))).Single();
        Assert.AreEqual(Path.Combine(dir, "notes.docx"), output);
        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels" })
            CollectionAssert.Contains(names, part);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        {
            using var s = entry.Open();
            XDocument.Load(s);
        }
        using (var s = zip.GetEntry("[Content_Types].xml")!.Open())
            Assert.IsTrue(new StreamReader(s).ReadToEnd().Contains("/word/document.xml"));
        using (var s = zip.GetEntry("word/_rels/document.xml.rels")!.Open())
            Assert.IsTrue(new StreamReader(s).ReadToEnd().Contains("https://example.com/"));
    }

    [TestMethod]
    public async Task OdtPackageIsValid()
    {
        var dir = TestSetup.NewFolder("odt-pkg");
        var output = (await Docs.Convert(MakeDocx(dir), Docs.Format("odt"))).Single();
        var bytes = File.ReadAllBytes(output);
        // The first entry is "mimetype", stored (method 0) without an extra field.
        Assert.AreEqual(0x04034B50u, BitConverter.ToUInt32(bytes, 0));
        Assert.AreEqual(0, BitConverter.ToUInt16(bytes, 8));
        int nameLength = BitConverter.ToUInt16(bytes, 26);
        Assert.AreEqual(0, BitConverter.ToUInt16(bytes, 28));
        Assert.AreEqual("mimetype", Encoding.ASCII.GetString(bytes, 30, nameLength));
        Assert.AreEqual("application/vnd.oasis.opendocument.text", Encoding.ASCII.GetString(bytes, 30 + nameLength, 39));
        using var zip = ZipFile.OpenRead(output);
        foreach (var part in new[] { "META-INF/manifest.xml", "content.xml", "styles.xml", "meta.xml" })
        {
            var entry = zip.GetEntry(part);
            Assert.IsNotNull(entry, part);
            using var s = entry.Open();
            XDocument.Load(s);
        }
    }

    [TestMethod]
    public void WhitespaceSurvivesOdtAndHtml()
    {
        var dir = TestSetup.NewFolder("spaces");
        var model = new DocModel();
        model.Blocks.Add(new DocParagraph("  two leading,  double   triple, trailing  "));
        model.Blocks.Add(new DocParagraph(""));
        model.Blocks.Add(new DocParagraph("tab\there"));
        foreach (var ext in new[] { "odt", "html", "docx", "rtf" })
        {
            var path = Path.Combine(dir, "spaces." + ext);
            Docs.Write(model, path, Docs.Format(ext)).Wait();
            var back = Docs.Read(path).Result;
            Assert.AreEqual(model.PlainText(), back.PlainText(), ext);
        }
    }

    [TestMethod]
    public void WordCountCountsCjkCharactersAsWords()
    {
        var dir = TestSetup.NewFolder("count");
        var path = Write(dir, "count.txt", "Hello, world! 你好。\nテスト ok");
        var info = Assert.ThrowsExactly<Info>(() => Docs.WordCount(path).GetAwaiter().GetResult());
        // Hello, world! = 2 words; 你 好 = 2; テ ス ト = 3; ok = 1.
        Assert.AreEqual("count.txt: 8 words, 24 characters", info.Message);
        Assert.AreEqual((0, 0), DocModel.Count(""));
        Assert.AreEqual((3, 11), DocModel.Count("one two\tsix"));
    }

    // ─────────────────────────────── text → PDF ───────────────────────────────

    [TestMethod]
    public async Task TextToPdfIsSearchableIncludingCjk()
    {
        var dir = TestSetup.NewFolder("pdf-text");
        var pdf = await TextPdf(dir, "hello", $"Hello PDF world\n{Chinese}，{Japanese}\n한국어");
        Assert.AreEqual(Path.Combine(dir, "hello.pdf"), pdf);
        var file = PdfFile.Open(pdf);
        Assert.HasCount(1, file.Pages);
        Assert.AreEqual((0.0, 0.0, 612.0, 792.0), file.Pages[0].Media);
        var text = Flat(new PdfTextExtractor(file).Extract());
        Assert.IsTrue(text.Contains("Hello PDF world"), text);
        Assert.IsTrue(text.Contains(Chinese), text);
        Assert.IsTrue(text.Contains(Japanese), text);
        Assert.IsTrue(text.Contains("한국어"), text);
        // Fonts are embedded, and the CJK glyphs are real glyphs (not .notdef).
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.IsTrue(raw.Contains("/FontFile2"));
        foreach (var num in file.ObjectNumbers)
        {
            if (file.Get(num) is not PdfDict { } font || font.Name("Subtype") != "Type0") continue;
            var cmap = PdfCMap.Parse(file.Decoded((PdfStream)file.Resolve(font["ToUnicode"])!)!);
            Assert.IsFalse(cmap.Unicode.ContainsKey(0), "glyph 0 used for " + cmap.Unicode.GetValueOrDefault(0u));
        }
    }

    [TestMethod]
    public async Task LongTextFlowsOntoMorePages()
    {
        var dir = TestSetup.NewFolder("pdf-long");
        var lines = Enumerable.Range(1, 400).Select(i => $"Line {i} of a long document that needs several pages.");
        var pdf = await TextPdf(dir, "long", string.Join("\n", lines));
        var file = PdfFile.Open(pdf);
        // 400 lines at ~14.6 pt per line on 684 pt of text height: about 47 lines a page.
        Assert.IsTrue(file.Pages.Count is >= 7 and <= 11, file.Pages.Count.ToString());
        Assert.IsTrue(file.Pages.All(p => p.Media == (0, 0, 612, 792)));
        var text = Flat(new PdfTextExtractor(file).Extract());
        Assert.IsTrue(text.Contains("Line 1 of a long"), text[..100]);
        Assert.IsTrue(text.Contains("Line 400 of a long"));
        Assert.AreEqual(file.Pages.Count, Pdfs.PageCount(pdf));
    }

    // ─────────────────────────────── PDF operations ───────────────────────────────

    [TestMethod]
    public async Task MergeSplitRotateKeepPages()
    {
        var dir = TestSetup.NewFolder("pdf-ops");
        var a = await TextPdf(dir, "a", "First file");
        var b = await TextPdf(dir, "b", string.Join("\n", Enumerable.Range(1, 120).Select(i => "Second file line " + i)));
        int bPages = Pdfs.PageCount(b);
        Assert.IsTrue(bPages >= 2);

        var merged = await Pdfs.Merge([a, b]);
        Assert.AreEqual(Path.Combine(dir, "Merged.pdf"), merged);
        Assert.AreEqual(1 + bPages, Pdfs.PageCount(merged));
        var mergedText = Flat(new PdfTextExtractor(PdfFile.Open(merged)).Extract());
        Assert.IsTrue(mergedText.IndexOf("First file") < mergedText.IndexOf("Second file line 1"));
        Assert.AreEqual(Path.Combine(dir, "Merged 2.pdf"), await Pdfs.Merge([a, b]));

        var split = await Run("Split Pages", merged);
        Assert.HasCount(1 + bPages, split);
        Assert.AreEqual(Path.Combine(dir, "Merged 1.pdf"), split[0]);
        Assert.IsTrue(split.All(p => Pdfs.PageCount(p) == 1));
        Assert.IsTrue(Flat(new PdfTextExtractor(PdfFile.Open(split[0])).Extract()).Contains("First file"));

        var rotated = (await Run("Rotate", b)).Single();
        Assert.AreEqual(Path.Combine(dir, "b rotated.pdf"), rotated);
        var rf = PdfFile.Open(rotated);
        Assert.AreEqual(bPages, rf.Pages.Count);
        Assert.IsTrue(rf.Pages.All(p => p.Rotate == 90));
        var twice = PdfFile.Open((await Run("Rotate", rotated)).Single());
        Assert.IsTrue(twice.Pages.All(p => p.Rotate == 180));

        var count = Assert.ThrowsExactly<Info>(() => Run("Page Count", b).GetAwaiter().GetResult());
        Assert.AreEqual($"b.pdf: {bPages} pages", count.Message);
        Assert.AreEqual("a.pdf: 1 page", Assert.ThrowsExactly<Info>(() => Run("Page Count", a).GetAwaiter().GetResult()).Message);
    }

    static Task<List<string>> Run(string title, string path) => Pdfs.ToolItems.Concat(Pdfs.ConvertItems).Single(o => o.Title == title).Run(path);

    [TestMethod]
    public void RotateRespectsInheritedRotation()
    {
        var dir = TestSetup.NewFolder("pdf-inherit");
        var path = Path.Combine(dir, "inherit.pdf");
        File.WriteAllBytes(path, ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Rotate 270 /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Rotate 90 /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 20 20 Td (Inherited) Tj ET")));
        var file = PdfFile.Open(path);
        Assert.AreEqual(270, file.Pages[0].Rotate);
        Assert.AreEqual((0.0, 0.0, 300.0, 400.0), file.Pages[0].Media);
        var rotated = PdfFile.Open(Run("Rotate", path).Result.Single());
        CollectionAssert.AreEqual(new[] { 0, 180 }, rotated.Pages.Select(p => p.Rotate).ToArray());
        Assert.AreEqual((0.0, 0.0, 300.0, 400.0), rotated.Pages[1].Media);
        // Split pages carry the inherited attributes themselves.
        var first = PdfFile.Open(Run("Split Pages", path).Result[0]);
        Assert.AreEqual(270, first.Pages[0].Rotate);
        Assert.IsTrue(Flat(new PdfTextExtractor(first).Extract()).Contains("Inherited"));
    }

    [TestMethod]
    public async Task CompressShrinksPhotosAndNeverGrows()
    {
        var dir = TestSetup.NewFolder("pdf-compress");
        // A page with a big uncompressed photo-like RGB image and an uncompressed content stream.
        int w = 1200, h = 900;
        var pixels = new byte[w * h * 3];
        var random = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                pixels[i] = (byte)(x * 255 / w);
                pixels[i + 1] = (byte)(y * 255 / h);
                pixels[i + 2] = (byte)(128 + random.Next(-20, 20));
            }
        var path = Path.Combine(dir, "photo.pdf");
        File.WriteAllBytes(path, ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>",
            Stream("q 400 0 0 300 100 400 cm /Im1 Do Q BT /F1 24 Tf 100 200 Td (Photo page) Tj ET"),
            StreamBytes($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Unused true /Padding (" + new string('x', 5000) + ") >>"));
        var output = (await Run("Compress", path)).Single();
        Assert.AreEqual(Path.Combine(dir, "photo compressed.pdf"), output);
        Assert.IsTrue(new FileInfo(output).Length < new FileInfo(path).Length / 5, $"{new FileInfo(output).Length} vs {new FileInfo(path).Length}");
        var file = PdfFile.Open(output);
        Assert.HasCount(1, file.Pages);
        Assert.IsTrue(Flat(new PdfTextExtractor(file).Extract()).Contains("Photo page"));
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Assert.IsTrue(raw.Contains("/DCTDecode"));
        Assert.IsFalse(raw.Contains("Unused"));
        // The page still renders.
        var image = await Pdfs.RenderPage(output, 0, 72);
        Assert.AreEqual(612, image.PixelWidth);

        // Compressing again can't help: report it instead of writing a bigger file.
        var again = await Assert.ThrowsExactlyAsync<Info>(() => Run("Compress", output));
        Assert.AreEqual("photo compressed.pdf is already as small as it can be", again.Message);
    }

    [TestMethod]
    public async Task CropPagesCutsEveryPageByTheSameProportion()
    {
        var dir = TestSetup.NewFolder("pdf-crop");
        var path = Path.Combine(dir, "crop.pdf");
        File.WriteAllBytes(path, ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Rotate 90 /CropBox [12 12 600 780] /Contents 5 0 R >>",
            Stream("0 0 1 rg 100 100 200 200 re f")));
        var output = Path.Combine(dir, "crop cropped.pdf");
        Assert.AreEqual(output, Pdfs.CropPages(path, new System.Windows.Rect(0.25, 0.25, 0.5, 0.5), output));
        var file = PdfFile.Open(output);
        Assert.AreEqual((153.0, 198.0, 459.0, 594.0), file.Box(file.Pages[0].Dict["CropBox"]));
        Assert.AreEqual((153.0, 198.0, 459.0, 594.0), file.Pages[0].Media);
        // Rotated 90°: the displayed left quarter is the page's bottom quarter.
        var box = file.Box(file.Pages[1].Dict["CropBox"])!.Value;
        Assert.AreEqual(12 + 0.25 * 588, box.X0, 1e-6);
        Assert.AreEqual(12 + 0.75 * 588, box.X1, 1e-6);
        Assert.AreEqual(12 + 0.25 * 768, box.Y0, 1e-6);
        Assert.AreEqual(12 + 0.75 * 768, box.Y1, 1e-6);
        Assert.AreEqual(90, file.Pages[1].Rotate);
        // A top-left crop of an unrotated page.
        var topLeft = Pdfs.PageRect(new System.Windows.Rect(0, 0, 0.5, 0.25), 0, (0, 0, 612, 792));
        Assert.AreEqual((0.0, 594.0, 306.0, 792.0), topLeft);
        var rendered = await Pdfs.RenderPage(output, 0, 72);
        Assert.AreEqual(306, rendered.PixelWidth);
        Assert.AreEqual(396, rendered.PixelHeight);
    }

    // ─────────────────────────────── PDF parsing ───────────────────────────────

    static string Stream(string content) => $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    static string StreamBytes(string dict, byte[] data) => $"<< {dict} /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream";

    /// A PDF with a classic cross-reference table; objects are numbered from 1.
    static byte[] ClassicPdf(params string[] objects) => ClassicPdfWithTrailer("", objects);

    static byte[] ClassicPdfWithTrailer(string trailerExtra, params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    [TestMethod]
    public void ParsesXrefStreamsObjectStreamsAndIncrementalUpdates()
    {
        var dir = TestSetup.NewFolder("pdf-xrefstm");
        var latin = Encoding.Latin1;
        var file = new MemoryStream();
        void Put(string s) => file.Write(latin.GetBytes(s));
        Put("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new Dictionary<int, long>();
        // Object 4: content stream (plain object). Objects 1, 2, 3, 5 live in object stream 6.
        offsets[4] = file.Length;
        var content = "BT /F1 18 Tf 72 700 Td (Old text) Tj ET";
        Put($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        string[] packed =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ];
        int[] nums = [1, 2, 3, 5];
        var header = new StringBuilder();
        var body = new StringBuilder();
        for (int i = 0; i < packed.Length; i++)
        {
            header.Append($"{nums[i]} {body.Length} ");
            body.Append(packed[i]).Append('\n');
        }
        var objstm = Deflate(latin.GetBytes(header.ToString() + body));
        offsets[6] = file.Length;
        Put($"6 0 obj\n<< /Type /ObjStm /N 4 /First {header.Length} /Filter /FlateDecode /Length {objstm.Length} >>\nstream\n");
        file.Write(objstm);
        Put("\nendstream\nendobj\n");
        // Cross-reference stream (object 7), W [1 2 1], PNG Up predictor.
        long xrefOffset = file.Length;
        offsets[7] = xrefOffset;
        var rows = new List<byte[]>();
        for (int n = 0; n <= 7; n++)
        {
            if (n == 0) rows.Add([0, 0, 0, 255]);
            else if (Array.IndexOf(nums, n) is int idx and >= 0) rows.Add([2, 0, 6, (byte)idx]);
            else rows.Add([1, (byte)(offsets[n] >> 8), (byte)offsets[n], 0]);
        }
        var predicted = new List<byte>();
        var previous = new byte[4];
        foreach (var row in rows)
        {
            predicted.Add(2);
            for (int k = 0; k < 4; k++) predicted.Add((byte)(row[k] - previous[k]));
            previous = row;
        }
        var xrefData = Deflate([.. predicted]);
        Put($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 2 1] /Root 1 0 R /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >> /Length {xrefData.Length} >>\nstream\n");
        file.Write(xrefData);
        Put($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        // Incremental update: a new content stream for object 4, with a classic table pointing back.
        long updated = file.Length;
        var newContent = "BT /F1 18 Tf 72 700 Td (Updated text) Tj ET";
        Put($"4 0 obj\n<< /Length {newContent.Length} >>\nstream\n{newContent}\nendstream\nendobj\n");
        long xref2 = file.Length;
        Put($"xref\n4 1\n{updated:D10} 00000 n \ntrailer\n<< /Size 8 /Root 1 0 R /Prev {xrefOffset} >>\nstartxref\n{xref2}\n%%EOF\n");
        var path = Path.Combine(dir, "xrefstm.pdf");
        File.WriteAllBytes(path, file.ToArray());

        var pdf = PdfFile.Open(path);
        Assert.HasCount(1, pdf.Pages);
        var text = Flat(new PdfTextExtractor(pdf).Extract());
        Assert.AreEqual("Updated text", text);
        // Rewriting keeps it readable (and drops the old objects).
        var rotated = PdfFile.Open(Run("Rotate", path).Result.Single());
        Assert.AreEqual("Updated text", Flat(new PdfTextExtractor(rotated).Extract()));
        var compressed = Path.Combine(dir, "packed.pdf");
        using (var s = File.Create(compressed)) PdfCompress.Write(pdf, s);
        Assert.AreEqual("Updated text", Flat(new PdfTextExtractor(PdfFile.Open(compressed)).Extract()));
        Assert.IsTrue(Encoding.Latin1.GetString(File.ReadAllBytes(compressed)).Contains("/ObjStm"));
    }

    [TestMethod]
    public void RepairsBrokenCrossReferences()
    {
        var dir = TestSetup.NewFolder("pdf-broken");
        var bytes = ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>",
            Stream("BT /F1 12 Tf 72 700 Td (Still readable) Tj ET"));
        var text = Encoding.Latin1.GetString(bytes);
        // Shift every object by inserting junk after the header; the table now points to wrong places.
        text = text.Replace("%PDF-1.4\n", "%PDF-1.4\n% junk junk junk junk junk junk\n");
        var path = Path.Combine(dir, "broken.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        var pdf = PdfFile.Open(path);
        Assert.HasCount(1, pdf.Pages);
        var model = new PdfTextExtractor(pdf).Extract();
        Assert.AreEqual("Still readable", Flat(model));
        Assert.IsTrue(model.AllParagraphs.Single().Runs.Single().Bold);

        File.WriteAllText(Path.Combine(dir, "not.pdf"), "hello");
        Assert.AreEqual("Couldn't open not.pdf", Assert.ThrowsExactly<ToolError>(() => Pdfs.PageCount(Path.Combine(dir, "not.pdf"))).Message);
    }

    [TestMethod]
    public void PasswordProtectedPdfIsRefused()
    {
        var dir = TestSetup.NewFolder("pdf-locked");
        var id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        var path = Path.Combine(dir, "locked.pdf");
        // A user password is set: the U entry doesn't match the empty password.
        File.WriteAllBytes(path, ClassicPdfWithTrailer($"/Encrypt 4 0 R /ID [{Hex(id)} {Hex(id)}]",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 /O {Hex(new byte[32])} /U {Hex(Enumerable.Repeat((byte)7, 32).ToArray())} >>"));
        var error = Assert.ThrowsExactly<ToolError>(() => Pdfs.PageCount(path));
        Assert.AreEqual("locked.pdf is password protected", error.Message);
        Assert.AreEqual("locked.pdf is password protected", Assert.ThrowsExactly<ToolError>(() => Run("Rotate", path).GetAwaiter().GetResult()).Message);
        Prefs.LanguageId = "zh-Hans";
        Assert.AreEqual("“locked.pdf”受密码保护", Assert.ThrowsExactly<ToolError>(() => Pdfs.PageCount(path)).Message);
        Prefs.LanguageId = "en";

        // Only an owner password (permissions): opens without asking, like on the Mac, and is written decrypted.
        int p = -3904;
        var (o, u, key) = PdfCrypt.MakeRc4(Encoding.ASCII.GetBytes("owner"), id, p);
        var content = Encoding.Latin1.GetString(PdfCrypt.Rc4Object(key, 4, 0, Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 700 Td (Secret text) Tj ET")));
        var title = Encoding.Latin1.GetString(PdfCrypt.Rc4Object(key, 6, 0, Encoding.ASCII.GetBytes("Owner only")));
        var open = Path.Combine(dir, "owner.pdf");
        File.WriteAllBytes(open, ClassicPdfWithTrailer($"/Encrypt 7 0 R /Info 6 0 R /ID [{Hex(id)} {Hex(id)}]",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Title {Hex(Encoding.Latin1.GetBytes(title))} >>",
            $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P {p} /O {Hex(o)} /U {Hex(u)} >>"));
        var pdf = PdfFile.Open(open);
        Assert.IsTrue(pdf.WasEncrypted);
        Assert.AreEqual("Secret text", Flat(new PdfTextExtractor(pdf).Extract()));
        Assert.AreEqual("Owner only", ((PdfStr)pdf.Dict(pdf.Trailer["Info"])!["Title"]!).Text);
        var rotated = PdfFile.Open(Run("Rotate", open).Result.Single());
        Assert.IsFalse(rotated.WasEncrypted);
        Assert.AreEqual("Secret text", Flat(new PdfTextExtractor(rotated).Extract()));
    }

    [TestMethod]
    public void ExtractsTextFromCommonFontEncodings()
    {
        var dir = TestSetup.NewFolder("pdf-fonts");
        // Simple font with Differences, a Type0 font with a ToUnicode CMap (bfrange), TJ spacing and T*.
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                   "2 beginbfrange <0010> <0012> <4E2D> <0020> <0021> [<6587> <0041>] endbfrange endcmap end end";
        var path = Path.Combine(dir, "fonts.pdf");
        File.WriteAllBytes(path, ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 8 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Garamond-Italic /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [33 /fi /eacute] >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /SimSun /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SimSun /DW 1000 /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>] /ToUnicode 7 0 R >>",
            Stream("BT /F1 12 Tf 14 TL 72 700 Td (Hello) Tj ( world) Tj T* (\\041ne caf\\042) Tj ET " +
                   "BT /F2 12 Tf 72 650 Td <001000110012> Tj [<0020> -1500 <0021>] TJ ET " +
                   "BT /F3 10 Tf 72 600 Td [(Kern) -120 (ed)] TJ 0 -12 Td [(Spaced) -900 (out)] TJ ET"),
            Stream(cmap),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"));
        var model = new PdfTextExtractor(PdfFile.Open(path)).Extract();
        var lines = model.AllParagraphs.Where(p => p.Text.Length > 0).Select(p => p.Text).ToList();
        CollectionAssert.AreEqual(new[] { "Hello world", "fine café", "中丮丯文 A", "Kerned", "Spaced out" }, lines, string.Join(" | ", lines));
        Assert.IsTrue(RunWith(model, "Hello")!.Italic);
        Assert.IsTrue(RunWith(model, "Kerned")!.Bold);
    }

    [TestMethod]
    public async Task PdfWithoutTextReportsIt()
    {
        var dir = TestSetup.NewFolder("pdf-empty");
        var path = Path.Combine(dir, "shapes.pdf");
        File.WriteAllBytes(path, ClassicPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("1 0 0 rg 100 100 300 300 re f")));
        var error = await Assert.ThrowsExactlyAsync<ToolError>(() => Run("TXT", path));
        Assert.AreEqual("No text found in this PDF", error.Message);
    }

    // ─────────────────────────────── PDF → images ───────────────────────────────

    [TestMethod]
    public async Task PdfPagesRenderAt300Dpi()
    {
        var dir = TestSetup.NewFolder("pdf-png");
        var one = await TextPdf(dir, "one", "A single page");
        var png = (await Run("PNG", one)).Single();
        Assert.AreEqual(Path.Combine(dir, "one.png"), png);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(png), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.AreEqual(2550, decoder.Frames[0].PixelWidth);
        Assert.AreEqual(3300, decoder.Frames[0].PixelHeight);
        Assert.AreEqual(300, decoder.Frames[0].DpiX, 0.5);

        var two = await TextPdf(dir, "two", string.Join("\n", Enumerable.Range(1, 80).Select(i => "Row " + i)));
        var jpgs = await Run("JPG", two);
        CollectionAssert.AreEqual(new[] { Path.Combine(dir, "two 1.jpg"), Path.Combine(dir, "two 2.jpg") }, jpgs);
        var jpeg = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(jpgs[1]), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.AreEqual(2550, jpeg.Frames[0].PixelWidth);

        // A rotated page renders turned; text is dark on white.
        var rotated = (await Run("Rotate", one)).Single();
        var image = await Pdfs.RenderPage(rotated, 0, 72);
        Assert.AreEqual(792, image.PixelWidth);
        Assert.AreEqual(612, image.PixelHeight);
        var page = await Pdfs.RenderPage(one, 0, 96);
        int stride = page.PixelWidth * 3;
        var pixels = new byte[stride * page.PixelHeight];
        page.CopyPixels(pixels, stride, 0);
        Assert.AreEqual(255, pixels[0]);
        Assert.IsTrue(pixels.Any(b => b < 100), "no dark text pixels");
    }

    [TestMethod]
    public async Task RenderedCjkTextIsNotTofu()
    {
        var dir = TestSetup.NewFolder("pdf-cjk");
        var pdf = await TextPdf(dir, "cjk", "中文字体渲染测试\n日本語のフォント");
        var page = await Pdfs.RenderPage(pdf, 0, 150);
        // Ink where the first line is (that the glyphs aren't empty boxes is checked in TextToPdfIsSearchableIncludingCjk).
        int stride = page.PixelWidth * 3;
        var pixels = new byte[stride * page.PixelHeight];
        page.CopyPixels(pixels, stride, 0);
        int top = (int)(54 / 72.0 * 150), left = (int)(54 / 72.0 * 150);
        int dark = 0;
        for (int y = top; y < top + 30; y++)
            for (int x = left; x < left + 400; x++)
                if (pixels[y * stride + x * 3] < 128) dark++;
        Assert.IsTrue(dark > 300, dark.ToString());
    }

    // ─────────────────────────────── legacy .doc ───────────────────────────────

    /// A tiny Word 97 file: a compound file with WordDocument and 1Table streams and a one-piece Unicode text.
    static byte[] MakeDoc(string text)
    {
        var chars = Encoding.Unicode.GetBytes(text);
        // WordDocument: FIB (0x200 bytes is plenty) then the text at 0x400.
        var word = new byte[Math.Max(4096, 0x400 + chars.Length + 16)];
        BitConverter.GetBytes((ushort)0xA5EC).CopyTo(word, 0);
        BitConverter.GetBytes((ushort)193).CopyTo(word, 2);
        BitConverter.GetBytes((ushort)0x0200).CopyTo(word, 0x0A);
        BitConverter.GetBytes((ushort)14).CopyTo(word, 32);
        BitConverter.GetBytes((ushort)22).CopyTo(word, 62);
        BitConverter.GetBytes(text.Length).CopyTo(word, 64 + 3 * 4);
        BitConverter.GetBytes((ushort)93).CopyTo(word, 152);
        chars.CopyTo(word, 0x400);
        // 1Table: Clx = Pcdt with one piece covering the text.
        var table = new byte[4096];
        table[0] = 0x02;
        BitConverter.GetBytes(4 * 2 + 8).CopyTo(table, 1);
        BitConverter.GetBytes(0).CopyTo(table, 5);
        BitConverter.GetBytes(text.Length).CopyTo(table, 9);
        BitConverter.GetBytes((uint)0x400).CopyTo(table, 13 + 2);
        BitConverter.GetBytes(0).CopyTo(word, 154 + 33 * 8);
        BitConverter.GetBytes(21).CopyTo(word, 154 + 33 * 8 + 4);

        // Compound file: header, FAT sector 0, directory sector 1, WordDocument from sector 2, 1Table after it.
        const int sector = 512;
        int wordSectors = word.Length / sector, tableSectors = table.Length / sector;
        var data = new byte[sector * (1 + 2 + wordSectors + tableSectors)];
        byte[] sig = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        sig.CopyTo(data, 0);
        BitConverter.GetBytes((ushort)0x3E).CopyTo(data, 0x18);
        BitConverter.GetBytes((ushort)3).CopyTo(data, 0x1A);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(data, 0x1C);
        BitConverter.GetBytes((ushort)9).CopyTo(data, 0x1E);
        BitConverter.GetBytes((ushort)6).CopyTo(data, 0x20);
        BitConverter.GetBytes(1).CopyTo(data, 0x2C);
        BitConverter.GetBytes(1).CopyTo(data, 0x30);
        BitConverter.GetBytes(4096).CopyTo(data, 0x38);
        BitConverter.GetBytes(0xFFFFFFFE).CopyTo(data, 0x3C);
        BitConverter.GetBytes(0xFFFFFFFE).CopyTo(data, 0x44);
        for (int i = 0; i < 109; i++) BitConverter.GetBytes(i == 0 ? 0u : 0xFFFFFFFF).CopyTo(data, 0x4C + i * 4);
        int fat = sector;
        var fatEntries = new List<uint> { 0xFFFFFFFD, 0xFFFFFFFE };
        for (int i = 0; i < wordSectors; i++) fatEntries.Add(i == wordSectors - 1 ? 0xFFFFFFFE : (uint)(2 + i + 1));
        for (int i = 0; i < tableSectors; i++) fatEntries.Add(i == tableSectors - 1 ? 0xFFFFFFFE : (uint)(2 + wordSectors + i + 1));
        for (int i = 0; i < 128; i++) BitConverter.GetBytes(i < fatEntries.Count ? fatEntries[i] : 0xFFFFFFFF).CopyTo(data, fat + i * 4);
        int dir = 2 * sector;
        void Entry(int index, string name, byte type, uint start, uint size)
        {
            int at = dir + index * 128;
            Encoding.Unicode.GetBytes(name).CopyTo(data, at);
            BitConverter.GetBytes((ushort)((name.Length + 1) * 2)).CopyTo(data, at + 64);
            data[at + 66] = type;
            BitConverter.GetBytes(0xFFFFFFFF).CopyTo(data, at + 68);
            BitConverter.GetBytes(0xFFFFFFFF).CopyTo(data, at + 72);
            BitConverter.GetBytes(0xFFFFFFFF).CopyTo(data, at + 76);
            BitConverter.GetBytes(start).CopyTo(data, at + 116);
            BitConverter.GetBytes(size).CopyTo(data, at + 120);
        }
        Entry(0, "Root Entry", 5, 0xFFFFFFFE, 0);
        Entry(1, "WordDocument", 2, 2, (uint)word.Length);
        Entry(2, "1Table", 2, (uint)(2 + wordSectors), (uint)table.Length);
        word.CopyTo(data, 3 * sector);
        table.CopyTo(data, (3 + wordSectors) * sector);
        return data;
    }

    [TestMethod]
    public async Task ReadsLegacyWordText()
    {
        var dir = TestSetup.NewFolder("doc");
        var path = Path.Combine(dir, "old.doc");
        File.WriteAllBytes(path, MakeDoc("Hello old Word\r\u0013 HYPERLINK \"x\" \u0014Link text\u0015 here\vline two\rA1\u0007B1\u0007\u0007After table\r" + Chinese + "\r"));
        var model = await Docs.Read(path);
        var text = Flat(model);
        Assert.AreEqual($"Hello old Word\nLink text here\nline two\nA1\tB1\nAfter table\n{Chinese}", text);
        Assert.AreEqual("B1", model.Blocks.OfType<DocTable>().Single().Rows[0][1].Text);
        Assert.AreEqual(FileKind.Document, FileKinds.Of(path));
        var docx = (await Docs.Convert(path, Docs.Format("docx"))).Single();
        Assert.IsTrue(Flat(await Docs.Read(docx)).Contains(Chinese));
    }

    // ─────────────────────────────── menus ───────────────────────────────

    [TestMethod]
    public void MenusOfferTheMacItems()
    {
        var docx = Catalog.DefaultItems([@"Z:\x\a.docx"], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.AreEqual(new[] { "PDF", "RTF", "TXT", "HTML", "ODT" }, docx);
        var md = Catalog.DefaultItems([@"Z:\x\a.md"], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.AreEqual(new[] { "DOCX", "PDF", "RTF", "TXT", "HTML", "ODT" }, md);
        var pdf = Catalog.DefaultItems([@"Z:\x\a.pdf"], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.AreEqual(new[] { "PNG", "JPG", "TXT", "DOCX", "RTF", "HTML" }, pdf);
        CollectionAssert.AreEqual(new[] { "Compress", "Split Pages", "Rotate", "Page Count" }, Pdfs.ToolItems.Select(o => o.Title).ToList());
        CollectionAssert.AreEqual(new[] { "Word Count" }, Catalog.DefaultItems([@"Z:\x\a.rtf"], WheelMode.Tools).Select(i => i.Title).ToList());
    }
}
