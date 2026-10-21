using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;

namespace Swivel;

/// Groups consecutive list paragraphs into nested lists for the writers that need real list structure.
sealed class DocListNode
{
    public bool Ordered;
    public int Start = 1;
    public int Id;
    public List<DocListItem> Items = [];

    /// Builds the tree for `paragraphs[index…]` while they belong to list `id`; advances `index` past them.
    public static DocListNode Build(IReadOnlyList<DocParagraph> paragraphs, ref int index, int level)
    {
        var first = paragraphs[index].List!;
        var node = new DocListNode { Ordered = first.Ordered, Start = first.Start, Id = first.Id };
        while (index < paragraphs.Count && paragraphs[index].List is { } info && info.Id == first.Id && info.Level >= level)
        {
            if (info.Level == level)
            {
                var item = new DocListItem { Paragraph = paragraphs[index] };
                node.Items.Add(item);
                index++;
                if (index < paragraphs.Count && paragraphs[index].List is { } next && next.Id == first.Id && next.Level > level)
                    item.Child = Build(paragraphs, ref index, level + 1);
            }
            else
            {
                // A deeper item without a parent item: hang it on an empty item.
                var item = new DocListItem();
                node.Items.Add(item);
                item.Child = Build(paragraphs, ref index, level + 1);
            }
        }
        return node;
    }
}

sealed class DocListItem
{
    public DocParagraph? Paragraph;
    public DocListNode? Child;
}

static class DocWriters
{
    public const double DefaultSize = 12;
    public static readonly double[] HeadingSizes = [24, 18, 15, 13, 12, 11];

    static readonly UTF8Encoding Utf8 = new(false);

    /// Splits a block list into plain blocks and runs of list paragraphs.
    public static IEnumerable<(DocBlock? Block, DocListNode? List)> Group(List<DocBlock> blocks)
    {
        int i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i] is DocParagraph { List: not null })
            {
                var run = new List<DocParagraph>();
                int id = ((DocParagraph)blocks[i]).List!.Id;
                while (i < blocks.Count && blocks[i] is DocParagraph { List: { } info } p && info.Id == id) { run.Add(p); i++; }
                int index = 0;
                while (index < run.Count)
                {
                    int minLevel = run.Skip(index).Min(p => p.List!.Level);
                    yield return (null, DocListNode.Build(run, ref index, Math.Min(minLevel, run[index].List!.Level)));
                }
                continue;
            }
            yield return (blocks[i], null);
            i++;
        }
    }

    // ─────────────────────────────── TXT ───────────────────────────────

    public static void WriteText(DocModel model, Stream stream)
    {
        var bytes = Utf8.GetBytes(model.PlainText(markers: true, newline: "\r\n"));
        stream.Write(bytes);
    }

    // ─────────────────────────────── HTML ───────────────────────────────

    public static void WriteHtml(DocModel model, Stream stream, string title)
    {
        var sb = new StringBuilder();
        var lang = model.GuessLanguage();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(lang.Split('-')[0] == "en" ? "en" : lang).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"generator\" content=\"Swivel\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n");
        sb.Append("<style>\n")
          .Append("body { font-family: Calibri, \"Segoe UI\", \"Microsoft YaHei\", \"Yu Gothic\", sans-serif; font-size: 12pt; line-height: 1.4; }\n")
          .Append("p { margin: 0; }\n")
          .Append("table { border-collapse: collapse; margin: 6pt 0; }\n")
          .Append("td, th { border: 1px solid #999; padding: 3pt 6pt; vertical-align: top; }\n")
          .Append("pre, code { font-family: Consolas, \"Courier New\", monospace; }\n")
          .Append("pre { margin: 0; white-space: pre-wrap; }\n")
          .Append("</style>\n</head>\n<body>\n");
        WriteHtmlBlocks(sb, model.Blocks);
        sb.Append("</body>\n</html>\n");
        stream.Write(Utf8.GetBytes(sb.ToString()));
    }

    static void WriteHtmlBlocks(StringBuilder sb, List<DocBlock> blocks, bool header = false)
    {
        var groups = Group(blocks).ToList();
        for (int g = 0; g < groups.Count; g++)
        {
            var (block, list) = groups[g];
            if (list != null) { WriteHtmlList(sb, list, header); continue; }
            if (block is DocParagraph p)
            {
                if (p.Pre)
                {
                    // Consecutive code lines form one <pre>.
                    sb.Append("<pre>");
                    bool first = true;
                    while (true)
                    {
                        if (!first) sb.Append('\n');
                        first = false;
                        foreach (var run in ((DocParagraph)groups[g].Block!).Runs) WriteHtmlRun(sb, run, header, pre: true);
                        if (g + 1 < groups.Count && groups[g + 1].Block is DocParagraph { Pre: true, List: null }) g++;
                        else break;
                    }
                    sb.Append("</pre>\n");
                    continue;
                }
                var tag = p.Heading > 0 ? "h" + p.Heading : "p";
                sb.Append('<').Append(tag).Append(AlignStyle(p.Align)).Append('>');
                WriteHtmlRuns(sb, p, header);
                sb.Append("</").Append(tag).Append(">\n");
            }
            else if (block is DocTable t)
            {
                sb.Append("<table>\n");
                foreach (var row in t.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var cell in row)
                    {
                        var ctag = cell.Header ? "th" : "td";
                        sb.Append('<').Append(ctag);
                        if (cell.ColSpan > 1) sb.Append(" colspan=\"").Append(cell.ColSpan).Append('"');
                        sb.Append('>');
                        if (cell.Blocks.Count == 1 && cell.Blocks[0] is DocParagraph { Heading: 0, List: null, Pre: false } only)
                            WriteHtmlRuns(sb, only, false);
                        else
                        {
                            var inner = new StringBuilder();
                            WriteHtmlBlocks(inner, cell.Blocks);
                            sb.Append(inner.ToString().TrimEnd('\n'));
                        }
                        sb.Append("</").Append(ctag).Append('>');
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</table>\n");
            }
        }
    }

    static string AlignStyle(DocAlign align) => align switch
    {
        DocAlign.Center => " style=\"text-align: center\"",
        DocAlign.Right => " style=\"text-align: right\"",
        DocAlign.Justify => " style=\"text-align: justify\"",
        _ => "",
    };

    static void WriteHtmlList(StringBuilder sb, DocListNode list, bool header)
    {
        var tag = list.Ordered ? "ol" : "ul";
        sb.Append('<').Append(tag);
        if (list.Ordered && list.Start != 1) sb.Append(" start=\"").Append(list.Start).Append('"');
        sb.Append(">\n");
        foreach (var item in list.Items)
        {
            sb.Append("<li>");
            if (item.Paragraph != null) WriteHtmlRuns(sb, item.Paragraph, header);
            if (item.Child != null)
            {
                sb.Append('\n');
                WriteHtmlList(sb, item.Child, header);
            }
            sb.Append("</li>\n");
        }
        sb.Append("</").Append(tag).Append(">\n");
    }

    static void WriteHtmlRuns(StringBuilder sb, DocParagraph p, bool header)
    {
        if (p.Runs.Count == 0 || p.Text.Length == 0) { sb.Append("<br>"); return; }
        bool atStart = true;
        for (int i = 0; i < p.Runs.Count; i++) WriteHtmlRun(sb, p.Runs[i], header, pre: false, ref atStart, last: i == p.Runs.Count - 1);
        // A trailing line break needs something after it to show.
        if (p.Text.EndsWith('\n')) sb.Append("<br>");
    }

    static void WriteHtmlRun(StringBuilder sb, DocRun run, bool header, bool pre)
    {
        bool atStart = false;
        WriteHtmlRun(sb, run, header, pre, ref atStart, last: false);
    }

    static void WriteHtmlRun(StringBuilder sb, DocRun run, bool header, bool pre, ref bool atStart, bool last)
    {
        var open = new StringBuilder();
        var close = new List<string>();
        void Tag(string name, string attributes = "")
        {
            open.Append('<').Append(name).Append(attributes).Append('>');
            close.Insert(0, "</" + name + ">");
        }
        if (run.Link != null) Tag("a", " href=\"" + WebUtility.HtmlEncode(run.Link) + "\"");
        if (run.Bold && !header) Tag("strong");
        if (run.Italic) Tag("em");
        if (run.Underline && run.Link == null) Tag("u");
        if (run.Strike) Tag("s");
        if (run.Code && !pre) Tag("code");
        var css = new List<string>();
        if (run.Size is { } size) css.Add($"font-size: {size.ToString("0.##", CultureInfo.InvariantCulture)}pt");
        if (run.Font != null && !run.Code) css.Add($"font-family: '{run.Font.Replace("'", "")}'");
        if (run.Color != null) css.Add($"color: #{run.Color}");
        if (css.Count > 0) Tag("span", " style=\"" + WebUtility.HtmlEncode(string.Join("; ", css)) + "\"");

        sb.Append(open);
        if (pre) sb.Append(WebUtility.HtmlEncode(run.Text));
        else
        {
            // HTML collapses whitespace: spaces that would collapse (at the start or end of a line, or after
            // another space) are written as &nbsp;; tabs keep their width with white-space: pre.
            var text = run.Text;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == ' ')
                {
                    int n = 0;
                    while (i + n < text.Length && text[i + n] == ' ') n++;
                    bool end = i + n == text.Length ? last : text[i + n] == '\n';
                    bool plain = !(atStart || end);
                    if (plain) sb.Append(' ');
                    for (int k = plain ? 1 : 0; k < n; k++) sb.Append("&nbsp;");
                    i += n;
                    atStart = false;
                    continue;
                }
                switch (c)
                {
                    case '\n': sb.Append("<br>"); atStart = true; i++; continue;
                    case '\t': sb.Append("<span style=\"white-space: pre\">\t</span>"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case ' ': sb.Append("&nbsp;"); break;
                    default: sb.Append(c); break;
                }
                atStart = false;
                i++;
            }
        }
        foreach (var c in close) sb.Append(c);
    }

    // ─────────────────────────────── DOCX ───────────────────────────────

    const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static XmlWriterSettings XmlSettings => new() { Encoding = Utf8, Indent = false, OmitXmlDeclaration = false };

    static void Entry(ZipArchive zip, string name, Action<XmlWriter> write, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, XmlSettings);
        xml.WriteStartDocument(true);
        write(xml);
        xml.WriteEndDocument();
    }

    static void RawEntry(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(Utf8.GetBytes(text));
    }

    sealed class DocxContext
    {
        public readonly Dictionary<string, string> Links = [];
        public readonly SortedDictionary<int, Dictionary<int, (bool Ordered, int Start)>> Lists = [];

        public string LinkId(string target)
        {
            if (!Links.TryGetValue(target, out var id)) Links[target] = id = "rIdL" + (Links.Count + 1);
            return id;
        }
    }

    public static void WriteDocx(DocModel model, Stream stream, string title)
    {
        var ctx = new DocxContext();
        foreach (var p in model.AllParagraphs)
            if (p.List is { } info)
            {
                if (!ctx.Lists.TryGetValue(info.Id, out var levels)) ctx.Lists[info.Id] = levels = [];
                levels.TryAdd(info.Level, (info.Ordered, info.Start));
            }
        var lang = model.GuessLanguage();

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        RawEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
            (ctx.Lists.Count > 0 ? "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" : "") +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>");
        RawEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
            "</Relationships>");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        RawEntry(zip, "docProps/core.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<dc:title>" + WebUtility.HtmlEncode(title) + "</dc:title>" +
            "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:created>" +
            "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:modified>" +
            "</cp:coreProperties>");
        RawEntry(zip, "docProps/app.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Swivel</Application></Properties>");
        RawEntry(zip, "word/settings.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<w:settings xmlns:w=\"" + WNs + "\"><w:defaultTabStop w:val=\"720\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>");
        Entry(zip, "word/styles.xml", xml => WriteDocxStyles(xml, lang));
        if (ctx.Lists.Count > 0) Entry(zip, "word/numbering.xml", xml => WriteDocxNumbering(xml, ctx));
        Entry(zip, "word/document.xml", xml =>
        {
            xml.WriteStartElement("w", "document", WNs);
            xml.WriteAttributeString("xmlns", "r", null, RNs);
            xml.WriteStartElement("w", "body", WNs);
            WriteDocxBlocks(xml, model.Blocks, ctx, false);
            xml.WriteStartElement("w", "sectPr", WNs);
            xml.WriteStartElement("w", "pgSz", WNs);
            xml.WriteAttributeString("w", "w", WNs, "12240");
            xml.WriteAttributeString("w", "h", WNs, "15840");
            xml.WriteEndElement();
            xml.WriteStartElement("w", "pgMar", WNs);
            foreach (var side in new[] { "top", "right", "bottom", "left" }) xml.WriteAttributeString("w", side, WNs, "1080");
            foreach (var side in new[] { "header", "footer" }) xml.WriteAttributeString("w", side, WNs, "720");
            xml.WriteAttributeString("w", "gutter", WNs, "0");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        });
        RawEntry(zip, "word/_rels/document.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>" +
            (ctx.Lists.Count > 0 ? "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" : "") +
            string.Concat(ctx.Links.Select(l =>
                $"<Relationship Id=\"{l.Value}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{WebUtility.HtmlEncode(l.Key)}\" TargetMode=\"External\"/>")) +
            "</Relationships>");
    }

    static void W(XmlWriter xml, string element, params (string Name, string Value)[] attributes)
    {
        xml.WriteStartElement("w", element, WNs);
        foreach (var (n, v) in attributes) xml.WriteAttributeString("w", n, WNs, v);
        xml.WriteEndElement();
    }

    static void WriteDocxStyles(XmlWriter xml, string lang)
    {
        xml.WriteStartElement("w", "styles", WNs);
        xml.WriteStartElement("w", "docDefaults", WNs);
        xml.WriteStartElement("w", "rPrDefault", WNs);
        xml.WriteStartElement("w", "rPr", WNs);
        W(xml, "rFonts", ("ascii", "Calibri"), ("hAnsi", "Calibri"), ("eastAsiaTheme", "minorEastAsia"), ("cs", "Calibri"));
        W(xml, "sz", ("val", "24"));
        W(xml, "szCs", ("val", "24"));
        W(xml, "lang", ("val", "en-US"), ("eastAsia", lang.StartsWith("en") ? "zh-CN" : lang), ("bidi", "ar-SA"));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("w", "pPrDefault", WNs);
        xml.WriteStartElement("w", "pPr", WNs);
        W(xml, "spacing", ("after", "0"), ("line", "259"), ("lineRule", "auto"));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        void Style(string type, string id, string name, bool isDefault, Action? pPr, Action? rPr, string? basedOn = "Normal")
        {
            xml.WriteStartElement("w", "style", WNs);
            xml.WriteAttributeString("w", "type", WNs, type);
            if (isDefault) xml.WriteAttributeString("w", "default", WNs, "1");
            xml.WriteAttributeString("w", "styleId", WNs, id);
            W(xml, "name", ("val", name));
            if (basedOn != null) W(xml, "basedOn", ("val", basedOn));
            if (type == "paragraph") W(xml, "qFormat");
            if (pPr != null) { xml.WriteStartElement("w", "pPr", WNs); pPr(); xml.WriteEndElement(); }
            if (rPr != null) { xml.WriteStartElement("w", "rPr", WNs); rPr(); xml.WriteEndElement(); }
            xml.WriteEndElement();
        }

        Style("paragraph", "Normal", "Normal", true, null, null, null);
        for (int level = 1; level <= 6; level++)
        {
            int l = level;
            Style("paragraph", "Heading" + l, "heading " + l, false,
                () =>
                {
                    W(xml, "keepNext");
                    W(xml, "spacing", ("before", l <= 2 ? "240" : "160"), ("after", "80"));
                    W(xml, "outlineLvl", ("val", (l - 1).ToString(CultureInfo.InvariantCulture)));
                },
                () =>
                {
                    W(xml, "b");
                    W(xml, "bCs");
                    var half = ((int)(HeadingSizes[l - 1] * 2)).ToString(CultureInfo.InvariantCulture);
                    W(xml, "sz", ("val", half));
                    W(xml, "szCs", ("val", half));
                });
        }
        Style("paragraph", "Code", "Code", false, null, () => W(xml, "rFonts", ("ascii", "Consolas"), ("hAnsi", "Consolas"), ("cs", "Consolas")));
        Style("paragraph", "ListParagraph", "List Paragraph", false, () => W(xml, "ind", ("left", "720")), null);
        Style("character", "Hyperlink", "Hyperlink", false, null, () =>
        {
            W(xml, "color", ("val", "0563C1"));
            W(xml, "u", ("val", "single"));
        }, null);

        xml.WriteStartElement("w", "style", WNs);
        xml.WriteAttributeString("w", "type", WNs, "table");
        xml.WriteAttributeString("w", "styleId", WNs, "TableGrid");
        W(xml, "name", ("val", "Table Grid"));
        xml.WriteStartElement("w", "tblPr", WNs);
        xml.WriteStartElement("w", "tblBorders", WNs);
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            W(xml, side, ("val", "single"), ("sz", "4"), ("space", "0"), ("color", "999999"));
        xml.WriteEndElement();
        xml.WriteStartElement("w", "tblCellMar", WNs);
        W(xml, "left", ("w", "108"), ("type", "dxa"));
        W(xml, "right", ("w", "108"), ("type", "dxa"));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteEndElement();
    }

    static void WriteDocxNumbering(XmlWriter xml, DocxContext ctx)
    {
        xml.WriteStartElement("w", "numbering", WNs);
        foreach (var (id, levels) in ctx.Lists)
        {
            xml.WriteStartElement("w", "abstractNum", WNs);
            xml.WriteAttributeString("w", "abstractNumId", WNs, (id - 1).ToString(CultureInfo.InvariantCulture));
            W(xml, "multiLevelType", ("val", "hybridMultilevel"));
            for (int level = 0; level < 9; level++)
            {
                var (ordered, start) = levels.TryGetValue(level, out var info) ? info : (levels.Count > 0 && levels.Values.First().Ordered, 1);
                xml.WriteStartElement("w", "lvl", WNs);
                xml.WriteAttributeString("w", "ilvl", WNs, level.ToString(CultureInfo.InvariantCulture));
                W(xml, "start", ("val", start.ToString(CultureInfo.InvariantCulture)));
                W(xml, "numFmt", ("val", ordered ? "decimal" : "bullet"));
                W(xml, "lvlText", ("val", ordered ? $"%{level + 1}." : (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" }));
                W(xml, "lvlJc", ("val", "left"));
                xml.WriteStartElement("w", "pPr", WNs);
                W(xml, "ind", ("left", (720 * (level + 1)).ToString(CultureInfo.InvariantCulture)), ("hanging", "360"));
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        foreach (var (id, levels) in ctx.Lists)
        {
            xml.WriteStartElement("w", "num", WNs);
            xml.WriteAttributeString("w", "numId", WNs, id.ToString(CultureInfo.InvariantCulture));
            W(xml, "abstractNumId", ("val", (id - 1).ToString(CultureInfo.InvariantCulture)));
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    static void WriteDocxBlocks(XmlWriter xml, List<DocBlock> blocks, DocxContext ctx, bool header)
    {
        foreach (var block in blocks)
        {
            if (block is DocParagraph p) WriteDocxParagraph(xml, p, ctx, header);
            else if (block is DocTable t && t.Rows.Count > 0) WriteDocxTable(xml, t, ctx);
        }
    }

    static void WriteDocxTable(XmlWriter xml, DocTable table, DocxContext ctx)
    {
        int columns = Math.Max(1, table.Columns);
        int width = 10080 / columns;
        xml.WriteStartElement("w", "tbl", WNs);
        xml.WriteStartElement("w", "tblPr", WNs);
        W(xml, "tblStyle", ("val", "TableGrid"));
        W(xml, "tblW", ("w", "0"), ("type", "auto"));
        W(xml, "tblLook", ("val", "04A0"), ("firstRow", "1"), ("lastRow", "0"), ("firstColumn", "1"), ("lastColumn", "0"), ("noHBand", "0"), ("noVBand", "1"));
        xml.WriteEndElement();
        xml.WriteStartElement("w", "tblGrid", WNs);
        for (int c = 0; c < columns; c++) W(xml, "gridCol", ("w", width.ToString(CultureInfo.InvariantCulture)));
        xml.WriteEndElement();
        foreach (var row in table.Rows)
        {
            xml.WriteStartElement("w", "tr", WNs);
            int used = 0;
            foreach (var cell in row)
            {
                int span = Math.Max(1, cell.ColSpan);
                WriteDocxCell(xml, cell.Blocks, span, width, cell.Header, ctx);
                used += span;
            }
            // Every row fills the grid.
            for (; used < columns; used++) WriteDocxCell(xml, [], 1, width, false, ctx);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        // Two tables in a row would merge in Word; a paragraph keeps them apart.
        WriteDocxParagraph(xml, new DocParagraph(), ctx, false);
    }

    static void WriteDocxCell(XmlWriter xml, List<DocBlock> blocks, int span, int width, bool header, DocxContext ctx)
    {
        xml.WriteStartElement("w", "tc", WNs);
        xml.WriteStartElement("w", "tcPr", WNs);
        W(xml, "tcW", ("w", (width * span).ToString(CultureInfo.InvariantCulture)), ("type", "dxa"));
        if (span > 1) W(xml, "gridSpan", ("val", span.ToString(CultureInfo.InvariantCulture)));
        xml.WriteEndElement();
        // A cell must end with a paragraph.
        WriteDocxBlocks(xml, blocks, ctx, header);
        if (blocks.Count == 0 || blocks[^1] is not DocParagraph) WriteDocxParagraph(xml, new DocParagraph(), ctx, false);
        xml.WriteEndElement();
    }

    static void WriteDocxParagraph(XmlWriter xml, DocParagraph p, DocxContext ctx, bool header)
    {
        xml.WriteStartElement("w", "p", WNs);
        bool hasProps = p.Heading > 0 || p.Pre || p.List != null || p.Align != DocAlign.Left;
        if (hasProps)
        {
            xml.WriteStartElement("w", "pPr", WNs);
            if (p.Heading > 0) W(xml, "pStyle", ("val", "Heading" + p.Heading));
            else if (p.Pre) W(xml, "pStyle", ("val", "Code"));
            else if (p.List != null) W(xml, "pStyle", ("val", "ListParagraph"));
            if (p.List is { } list)
            {
                xml.WriteStartElement("w", "numPr", WNs);
                W(xml, "ilvl", ("val", list.Level.ToString(CultureInfo.InvariantCulture)));
                W(xml, "numId", ("val", list.Id.ToString(CultureInfo.InvariantCulture)));
                xml.WriteEndElement();
            }
            if (p.Align != DocAlign.Left)
                W(xml, "jc", ("val", p.Align switch { DocAlign.Center => "center", DocAlign.Right => "right", _ => "both" }));
            xml.WriteEndElement();
        }
        int i = 0;
        while (i < p.Runs.Count)
        {
            var link = p.Runs[i].Link;
            if (link == null) { WriteDocxRun(xml, p.Runs[i], header || false, p.Pre); i++; continue; }
            xml.WriteStartElement("w", "hyperlink", WNs);
            if (link.StartsWith('#')) xml.WriteAttributeString("w", "anchor", WNs, link[1..]);
            else xml.WriteAttributeString("r", "id", RNs, ctx.LinkId(link));
            xml.WriteAttributeString("w", "history", WNs, "1");
            while (i < p.Runs.Count && p.Runs[i].Link == link) WriteDocxRun(xml, p.Runs[i++], header, p.Pre);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    static void WriteDocxRun(XmlWriter xml, DocRun run, bool header, bool pre)
    {
        xml.WriteStartElement("w", "r", WNs);
        var bold = run.Bold || header;
        bool props = bold || run.Italic || run.Underline || run.Strike || run.Code || run.Size != null || run.Font != null || run.Color != null || run.Link != null;
        if (props)
        {
            xml.WriteStartElement("w", "rPr", WNs);
            if (run.Link != null) W(xml, "rStyle", ("val", "Hyperlink"));
            var font = run.Code && !pre ? "Consolas" : run.Font;
            if (font != null) W(xml, "rFonts", ("ascii", font), ("hAnsi", font), ("eastAsia", font), ("cs", font));
            if (bold) { W(xml, "b"); W(xml, "bCs"); }
            if (run.Italic) { W(xml, "i"); W(xml, "iCs"); }
            if (run.Strike) W(xml, "strike");
            if (run.Color != null) W(xml, "color", ("val", run.Color));
            if (run.Size is { } size)
            {
                var half = Math.Max(2, (int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture);
                W(xml, "sz", ("val", half));
                W(xml, "szCs", ("val", half));
            }
            if (run.Underline && run.Link == null) W(xml, "u", ("val", "single"));
            xml.WriteEndElement();
        }
        var text = new StringBuilder();
        void FlushText()
        {
            if (text.Length == 0) return;
            xml.WriteStartElement("w", "t", WNs);
            xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(XmlSafe(text.ToString()));
            xml.WriteEndElement();
            text.Clear();
        }
        foreach (var c in run.Text)
        {
            if (c == '\t') { FlushText(); W(xml, "tab"); }
            else if (c == '\n') { FlushText(); W(xml, "br"); }
            else text.Append(c);
        }
        FlushText();
        xml.WriteEndElement();
    }

    /// Drops characters XML 1.0 can't carry (control characters other than tab/newline, lone surrogates).
    public static string XmlSafe(string s)
    {
        if (s.All(c => c >= 0x20 && !char.IsSurrogate(c) && c is not ('￾' or '￿'))) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            if (char.IsSurrogate(c) || c is '￾' or '￿') continue;
            if (c < 0x20 && c is not ('\t' or '\n' or '\r')) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ─────────────────────────────── ODT ───────────────────────────────

    const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    const string StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    const string FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    const string XLinkNs = "http://www.w3.org/1999/xlink";
    const string SvgNs = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    const string MetaNs = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    const string DcNs = "http://purl.org/dc/elements/1.1/";

    sealed class OdtContext
    {
        public readonly Dictionary<string, string> TextStyles = [];
        public readonly SortedDictionary<int, Dictionary<int, (bool Ordered, int Start)>> Lists = [];
        public readonly HashSet<string> Fonts = ["Calibri", "Consolas"];
        public int Tables;

        public static string Key(DocRun r, bool header) =>
            $"{r.Bold || header}|{r.Italic}|{r.Underline}|{r.Strike}|{r.Code}|{r.Size}|{r.Font}|{r.Color}";

        public string? StyleFor(DocRun r, bool header)
        {
            bool bold = r.Bold || header;
            if (!bold && !r.Italic && !r.Underline && !r.Strike && !r.Code && r.Size == null && r.Font == null && r.Color == null) return null;
            var key = Key(r, header);
            if (!TextStyles.TryGetValue(key, out var name)) TextStyles[key] = name = "T" + (TextStyles.Count + 1);
            if (r.Font != null) Fonts.Add(r.Font);
            return name;
        }
    }

    public static void WriteOdt(DocModel model, Stream stream, string title)
    {
        var ctx = new OdtContext();
        foreach (var p in model.AllParagraphs)
        {
            if (p.List is { } info)
            {
                if (!ctx.Lists.TryGetValue(info.Id, out var levels)) ctx.Lists[info.Id] = levels = [];
                levels.TryAdd(info.Level, (info.Ordered, info.Start));
            }
        }
        // Body first (it registers the automatic styles), then the document around it.
        var body = new StringBuilder();
        using (var bodyXml = XmlWriter.Create(body, new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment, OmitXmlDeclaration = true }))
        {
            bodyXml.WriteStartElement("office", "text", OfficeNs);
            WriteOdtBlocks(bodyXml, model.Blocks, ctx, false);
            bodyXml.WriteEndElement();
        }
        var lang = model.GuessLanguage();

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        // The mimetype comes first and uncompressed, so tools can identify the file.
        RawEntry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        RawEntry(zip, "META-INF/manifest.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>");
        RawEntry(zip, "meta.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            $"<office:document-meta xmlns:office=\"{OfficeNs}\" xmlns:meta=\"{MetaNs}\" xmlns:dc=\"{DcNs}\" office:version=\"1.2\"><office:meta>" +
            "<meta:generator>Swivel</meta:generator><dc:title>" + WebUtility.HtmlEncode(title) + "</dc:title>" +
            "<meta:creation-date>" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "</meta:creation-date>" +
            "</office:meta></office:document-meta>");

        Entry(zip, "content.xml", xml =>
        {
            xml.WriteStartElement("office", "document-content", OfficeNs);
            WriteOdtNamespaces(xml);
            xml.WriteAttributeString("office", "version", OfficeNs, "1.2");
            WriteOdtFonts(xml, ctx);
            xml.WriteStartElement("office", "automatic-styles", OfficeNs);
            foreach (var (key, name) in ctx.TextStyles)
            {
                var parts = key.Split('|');
                xml.WriteStartElement("style", "style", StyleNs);
                xml.WriteAttributeString("style", "name", StyleNs, name);
                xml.WriteAttributeString("style", "family", StyleNs, "text");
                xml.WriteStartElement("style", "text-properties", StyleNs);
                if (parts[0] == "True")
                    foreach (var a in new[] { "font-weight" }) { xml.WriteAttributeString("fo", a, FoNs, "bold"); xml.WriteAttributeString("style", "font-weight-asian", StyleNs, "bold"); xml.WriteAttributeString("style", "font-weight-complex", StyleNs, "bold"); }
                if (parts[1] == "True") { xml.WriteAttributeString("fo", "font-style", FoNs, "italic"); xml.WriteAttributeString("style", "font-style-asian", StyleNs, "italic"); }
                if (parts[2] == "True")
                {
                    xml.WriteAttributeString("style", "text-underline-style", StyleNs, "solid");
                    xml.WriteAttributeString("style", "text-underline-width", StyleNs, "auto");
                    xml.WriteAttributeString("style", "text-underline-color", StyleNs, "font-color");
                }
                if (parts[3] == "True") xml.WriteAttributeString("style", "text-line-through-style", StyleNs, "solid");
                var font = parts[4] == "True" ? "Consolas" : parts[6].Length > 0 ? parts[6] : null;
                if (font != null) { xml.WriteAttributeString("style", "font-name", StyleNs, font); xml.WriteAttributeString("style", "font-name-asian", StyleNs, font); }
                if (parts[5].Length > 0)
                {
                    var size = double.Parse(parts[5], CultureInfo.CurrentCulture).ToString("0.##", CultureInfo.InvariantCulture) + "pt";
                    xml.WriteAttributeString("fo", "font-size", FoNs, size);
                    xml.WriteAttributeString("style", "font-size-asian", StyleNs, size);
                }
                if (parts[7].Length > 0) xml.WriteAttributeString("fo", "color", FoNs, "#" + parts[7]);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            foreach (var align in new[] { ("PCenter", "center"), ("PRight", "end"), ("PJustify", "justify") })
            {
                xml.WriteStartElement("style", "style", StyleNs);
                xml.WriteAttributeString("style", "name", StyleNs, align.Item1);
                xml.WriteAttributeString("style", "family", StyleNs, "paragraph");
                xml.WriteAttributeString("style", "parent-style-name", StyleNs, "Standard");
                xml.WriteStartElement("style", "paragraph-properties", StyleNs);
                xml.WriteAttributeString("fo", "text-align", FoNs, align.Item2);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            foreach (var (id, levels) in ctx.Lists)
            {
                xml.WriteStartElement("text", "list-style", TextNs);
                xml.WriteAttributeString("style", "name", StyleNs, "L" + id);
                for (int level = 0; level < 10; level++)
                {
                    var (ordered, start) = levels.TryGetValue(level, out var info) ? info : (levels.Values.First().Ordered, 1);
                    xml.WriteStartElement("text", ordered ? "list-level-style-number" : "list-level-style-bullet", TextNs);
                    xml.WriteAttributeString("text", "level", TextNs, (level + 1).ToString(CultureInfo.InvariantCulture));
                    if (ordered)
                    {
                        xml.WriteAttributeString("style", "num-suffix", StyleNs, ".");
                        xml.WriteAttributeString("style", "num-format", StyleNs, "1");
                        if (start != 1) xml.WriteAttributeString("text", "start-value", TextNs, start.ToString(CultureInfo.InvariantCulture));
                    }
                    else xml.WriteAttributeString("text", "bullet-char", TextNs, (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" });
                    xml.WriteStartElement("style", "list-level-properties", StyleNs);
                    xml.WriteAttributeString("text", "list-level-position-and-space-mode", TextNs, "label-alignment");
                    xml.WriteStartElement("style", "list-level-label-alignment", StyleNs);
                    xml.WriteAttributeString("text", "label-followed-by", TextNs, "listtab");
                    var indent = (0.25 * (level + 2)).ToString("0.##", CultureInfo.InvariantCulture) + "in";
                    xml.WriteAttributeString("text", "list-tab-stop-position", TextNs, indent);
                    xml.WriteAttributeString("fo", "text-indent", FoNs, "-0.25in");
                    xml.WriteAttributeString("fo", "margin-left", FoNs, indent);
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteStartElement("style", "style", StyleNs);
            xml.WriteAttributeString("style", "name", StyleNs, "Cell");
            xml.WriteAttributeString("style", "family", StyleNs, "table-cell");
            xml.WriteStartElement("style", "table-cell-properties", StyleNs);
            xml.WriteAttributeString("fo", "border", FoNs, "0.5pt solid #999999");
            xml.WriteAttributeString("fo", "padding", FoNs, "0.04in");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("office", "body", OfficeNs);
            xml.WriteRaw(body.ToString());
            xml.WriteEndElement();
            xml.WriteEndElement();
        });

        Entry(zip, "styles.xml", xml =>
        {
            xml.WriteStartElement("office", "document-styles", OfficeNs);
            WriteOdtNamespaces(xml);
            xml.WriteAttributeString("office", "version", OfficeNs, "1.2");
            WriteOdtFonts(xml, ctx);
            xml.WriteStartElement("office", "styles", OfficeNs);
            xml.WriteStartElement("style", "default-style", StyleNs);
            xml.WriteAttributeString("style", "family", StyleNs, "paragraph");
            xml.WriteStartElement("style", "text-properties", StyleNs);
            xml.WriteAttributeString("style", "font-name", StyleNs, "Calibri");
            xml.WriteAttributeString("fo", "font-size", FoNs, "12pt");
            xml.WriteAttributeString("style", "font-size-asian", StyleNs, "12pt");
            var parts = lang.Split('-');
            xml.WriteAttributeString("fo", "language", FoNs, "en");
            xml.WriteAttributeString("fo", "country", FoNs, "US");
            if (!lang.StartsWith("en"))
            {
                xml.WriteAttributeString("style", "language-asian", StyleNs, parts[0]);
                if (parts.Length > 1) xml.WriteAttributeString("style", "country-asian", StyleNs, parts[1]);
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
            void ParagraphStyle(string name, string display, string? parent, int outline, Action? text)
            {
                xml.WriteStartElement("style", "style", StyleNs);
                xml.WriteAttributeString("style", "name", StyleNs, name);
                xml.WriteAttributeString("style", "display-name", StyleNs, display);
                xml.WriteAttributeString("style", "family", StyleNs, "paragraph");
                if (parent != null) xml.WriteAttributeString("style", "parent-style-name", StyleNs, parent);
                if (outline > 0) xml.WriteAttributeString("style", "default-outline-level", StyleNs, outline.ToString(CultureInfo.InvariantCulture));
                if (outline > 0)
                {
                    xml.WriteStartElement("style", "paragraph-properties", StyleNs);
                    xml.WriteAttributeString("fo", "margin-top", FoNs, outline <= 2 ? "0.17in" : "0.11in");
                    xml.WriteAttributeString("fo", "margin-bottom", FoNs, "0.06in");
                    xml.WriteAttributeString("fo", "keep-with-next", FoNs, "always");
                    xml.WriteEndElement();
                }
                if (text != null)
                {
                    xml.WriteStartElement("style", "text-properties", StyleNs);
                    text();
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            ParagraphStyle("Standard", "Standard", null, 0, null);
            ParagraphStyle("Heading", "Heading", "Standard", 0, null);
            for (int level = 1; level <= 6; level++)
            {
                int l = level;
                ParagraphStyle("Heading_20_" + l, "Heading " + l, "Heading", l, () =>
                {
                    var size = HeadingSizes[l - 1].ToString("0.##", CultureInfo.InvariantCulture) + "pt";
                    xml.WriteAttributeString("fo", "font-size", FoNs, size);
                    xml.WriteAttributeString("style", "font-size-asian", StyleNs, size);
                    xml.WriteAttributeString("fo", "font-weight", FoNs, "bold");
                    xml.WriteAttributeString("style", "font-weight-asian", StyleNs, "bold");
                });
            }
            ParagraphStyle("Preformatted_20_Text", "Preformatted Text", "Standard", 0, () => xml.WriteAttributeString("style", "font-name", StyleNs, "Consolas"));
            ParagraphStyle("Table_20_Contents", "Table Contents", "Standard", 0, null);
            xml.WriteStartElement("style", "style", StyleNs);
            xml.WriteAttributeString("style", "name", StyleNs, "Internet_20_link");
            xml.WriteAttributeString("style", "display-name", StyleNs, "Internet link");
            xml.WriteAttributeString("style", "family", StyleNs, "text");
            xml.WriteStartElement("style", "text-properties", StyleNs);
            xml.WriteAttributeString("fo", "color", FoNs, "#0563C1");
            xml.WriteAttributeString("style", "text-underline-style", StyleNs, "solid");
            xml.WriteAttributeString("style", "text-underline-width", StyleNs, "auto");
            xml.WriteAttributeString("style", "text-underline-color", StyleNs, "font-color");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("office", "automatic-styles", OfficeNs);
            xml.WriteStartElement("style", "page-layout", StyleNs);
            xml.WriteAttributeString("style", "name", StyleNs, "pm1");
            xml.WriteStartElement("style", "page-layout-properties", StyleNs);
            xml.WriteAttributeString("fo", "page-width", FoNs, "8.5in");
            xml.WriteAttributeString("fo", "page-height", FoNs, "11in");
            xml.WriteAttributeString("style", "print-orientation", StyleNs, "portrait");
            foreach (var side in new[] { "top", "bottom", "left", "right" }) xml.WriteAttributeString("fo", "margin-" + side, FoNs, "0.75in");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("office", "master-styles", OfficeNs);
            xml.WriteStartElement("style", "master-page", StyleNs);
            xml.WriteAttributeString("style", "name", StyleNs, "Standard");
            xml.WriteAttributeString("style", "page-layout-name", StyleNs, "pm1");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        });
    }

    static void WriteOdtNamespaces(XmlWriter xml)
    {
        xml.WriteAttributeString("xmlns", "style", null, StyleNs);
        xml.WriteAttributeString("xmlns", "text", null, TextNs);
        xml.WriteAttributeString("xmlns", "table", null, TableNs);
        xml.WriteAttributeString("xmlns", "fo", null, FoNs);
        xml.WriteAttributeString("xmlns", "xlink", null, XLinkNs);
        xml.WriteAttributeString("xmlns", "svg", null, SvgNs);
    }

    static void WriteOdtFonts(XmlWriter xml, OdtContext ctx)
    {
        xml.WriteStartElement("office", "font-face-decls", OfficeNs);
        foreach (var font in ctx.Fonts)
        {
            xml.WriteStartElement("style", "font-face", StyleNs);
            xml.WriteAttributeString("style", "name", StyleNs, font);
            xml.WriteAttributeString("svg", "font-family", SvgNs, font.Contains(' ') ? $"'{font}'" : font);
            if (font == "Consolas") xml.WriteAttributeString("style", "font-pitch", StyleNs, "fixed");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    static void WriteOdtBlocks(XmlWriter xml, List<DocBlock> blocks, OdtContext ctx, bool header)
    {
        foreach (var (block, list) in Group(blocks))
        {
            if (list != null) WriteOdtList(xml, list, ctx, header, true);
            else if (block is DocParagraph p) WriteOdtParagraph(xml, p, ctx, header);
            else if (block is DocTable t && t.Rows.Count > 0)
            {
                int columns = Math.Max(1, t.Columns);
                xml.WriteStartElement("table", "table", TableNs);
                xml.WriteAttributeString("table", "name", TableNs, "Table" + ++ctx.Tables);
                xml.WriteStartElement("table", "table-column", TableNs);
                xml.WriteAttributeString("table", "number-columns-repeated", TableNs, columns.ToString(CultureInfo.InvariantCulture));
                xml.WriteEndElement();
                foreach (var row in t.Rows)
                {
                    xml.WriteStartElement("table", "table-row", TableNs);
                    int used = 0;
                    foreach (var cell in row)
                    {
                        int span = Math.Max(1, cell.ColSpan);
                        WriteOdtCell(xml, cell.Blocks, span, cell.Header, ctx);
                        for (int k = 1; k < span; k++) { xml.WriteStartElement("table", "covered-table-cell", TableNs); xml.WriteEndElement(); }
                        used += span;
                    }
                    for (; used < columns; used++) WriteOdtCell(xml, [], 1, false, ctx);
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
        }
    }

    static void WriteOdtCell(XmlWriter xml, List<DocBlock> blocks, int span, bool header, OdtContext ctx)
    {
        xml.WriteStartElement("table", "table-cell", TableNs);
        xml.WriteAttributeString("table", "style-name", TableNs, "Cell");
        if (span > 1) xml.WriteAttributeString("table", "number-columns-spanned", TableNs, span.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("office", "value-type", OfficeNs, "string");
        if (blocks.Count == 0) WriteOdtParagraph(xml, new DocParagraph(), ctx, false);
        else WriteOdtBlocks(xml, blocks, ctx, header);
        xml.WriteEndElement();
    }

    static void WriteOdtList(XmlWriter xml, DocListNode list, OdtContext ctx, bool header, bool top)
    {
        xml.WriteStartElement("text", "list", TextNs);
        if (top) xml.WriteAttributeString("text", "style-name", TextNs, "L" + list.Id);
        foreach (var item in list.Items)
        {
            xml.WriteStartElement("text", "list-item", TextNs);
            if (item.Paragraph != null) WriteOdtParagraph(xml, item.Paragraph, ctx, header);
            if (item.Child != null) WriteOdtList(xml, item.Child, ctx, header, false);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    static void WriteOdtParagraph(XmlWriter xml, DocParagraph p, OdtContext ctx, bool header)
    {
        if (p.Heading > 0)
        {
            xml.WriteStartElement("text", "h", TextNs);
            xml.WriteAttributeString("text", "style-name", TextNs, "Heading_20_" + p.Heading);
            xml.WriteAttributeString("text", "outline-level", TextNs, p.Heading.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            xml.WriteStartElement("text", "p", TextNs);
            xml.WriteAttributeString("text", "style-name", TextNs, p.Pre ? "Preformatted_20_Text" : p.Align switch
            {
                DocAlign.Center => "PCenter",
                DocAlign.Right => "PRight",
                DocAlign.Justify => "PJustify",
                _ => "Standard",
            });
        }
        bool afterSpace = true;
        int i = 0;
        while (i < p.Runs.Count)
        {
            var link = p.Runs[i].Link;
            if (link != null)
            {
                xml.WriteStartElement("text", "a", TextNs);
                xml.WriteAttributeString("xlink", "type", XLinkNs, "simple");
                xml.WriteAttributeString("xlink", "href", XLinkNs, link);
                xml.WriteAttributeString("text", "style-name", TextNs, "Internet_20_link");
            }
            do
            {
                var run = p.Runs[i];
                var style = ctx.StyleFor(run, header);
                if (style != null)
                {
                    xml.WriteStartElement("text", "span", TextNs);
                    xml.WriteAttributeString("text", "style-name", TextNs, style);
                }
                WriteOdtText(xml, XmlSafe(run.Text), ref afterSpace, i == p.Runs.Count - 1);
                if (style != null) xml.WriteEndElement();
                i++;
            } while (link != null && i < p.Runs.Count && p.Runs[i].Link == link);
            if (link != null) xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    /// ODF collapses whitespace like HTML: spaces that would collapse (at the start, after another space, at the end)
    /// become text:s; tabs are text:tab and line breaks text:line-break.
    static void WriteOdtText(XmlWriter xml, string text, ref bool afterSpace, bool last)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0) { xml.WriteString(sb.ToString()); sb.Clear(); }
        }
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\t' || c == '\n')
            {
                Flush();
                xml.WriteStartElement("text", c == '\t' ? "tab" : "line-break", TextNs);
                xml.WriteEndElement();
                afterSpace = true;
                i++;
                continue;
            }
            if (c == ' ')
            {
                int n = 0;
                while (i + n < text.Length && text[i + n] == ' ') n++;
                bool atEnd = last && i + n == text.Length;
                int literal = afterSpace || atEnd ? 0 : 1;
                if (literal == 1) sb.Append(' ');
                if (n - literal > 0)
                {
                    Flush();
                    xml.WriteStartElement("text", "s", TextNs);
                    if (n - literal > 1) xml.WriteAttributeString("text", "c", TextNs, (n - literal).ToString(CultureInfo.InvariantCulture));
                    xml.WriteEndElement();
                }
                i += n;
                afterSpace = true;
                continue;
            }
            sb.Append(c);
            afterSpace = false;
            i++;
        }
        Flush();
    }
}
