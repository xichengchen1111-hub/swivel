using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Swivel;

/// Lays a document out on US Letter pages (612 × 792 pt, 54 pt margins, like the Mac app's print settings) with
/// WPF's FlowDocument paginator, then writes each page as vector PDF: text as real text in embedded subset TrueType
/// fonts (Identity-H, with a ToUnicode map so it stays selectable and searchable, CJK included), rules and table
/// borders as paths. Glyphs from fonts whose licence forbids embedding are drawn as outlines instead.
/// Must run on an STA thread (FlowDocument).
sealed class PdfFromDoc
{
    const double PageWidthPt = 612, PageHeightPt = 792, MarginPt = 54;
    const double Scale = 72.0 / 96.0;

    sealed class FontUse
    {
        public required GlyphTypeface Typeface;
        public required string Resource;
        public readonly SortedSet<ushort> Glyphs = [];
        public readonly Dictionary<ushort, string> Unicode = [];
        public bool Failed;
        public PdfFontSubset? Subset;
    }

    readonly Dictionary<string, FontUse> fonts = [];
    readonly PdfOutput output = new();

    public static void Write(DocModel model, Stream stream, string title)
    {
        var doc = DocFlow.ToFlow(model);
        new PdfFromDoc().Render(doc, stream, title, model.GuessLanguage());
    }

    void Render(FlowDocument doc, Stream stream, string title, string language)
    {
        double width = PageWidthPt / Scale, height = PageHeightPt / Scale, margin = MarginPt / Scale;
        doc.PageWidth = width;
        doc.PageHeight = height;
        doc.PagePadding = new Thickness(margin);
        doc.ColumnWidth = double.PositiveInfinity;
        doc.ColumnGap = 0;
        doc.IsColumnWidthFlexible = false;
        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(width, height);
        paginator.ComputePageCount();

        var pageContents = new List<(byte[] Content, HashSet<string> Fonts)>();
        for (int i = 0; ; i++)
        {
            var page = paginator.GetPage(i);
            if (page == DocumentPage.Missing || page.Visual == null) break;
            var content = new StringBuilder();
            var used = new HashSet<string>();
            content.Append(CultureInfo.InvariantCulture, $"q {N(Scale)} 0 0 {N(-Scale)} 0 {N(PageHeightPt)} cm\n");
            WalkVisual(page.Visual, Matrix.Identity, content, used);
            content.Append("Q\n");
            pageContents.Add((Encoding.Latin1.GetBytes(content.ToString()), used));
            page.Dispose();
            if (i > 20000) break;
        }
        if (pageContents.Count == 0) pageContents.Add((Encoding.ASCII.GetBytes(""), []));

        var fontRefs = new Dictionary<string, PdfRef>();
        foreach (var font in fonts.Values.Where(f => !f.Failed && f.Glyphs.Count > 0)) fontRefs[font.Resource] = WriteFont(font);

        var catalogRef = output.Reserve();
        var pagesRef = output.Reserve();
        var kids = new PdfArray();
        foreach (var (content, used) in pageContents)
        {
            var contentDict = new PdfDict();
            contentDict["Filter"] = new PdfName("FlateDecode");
            var contentRef = output.Add(new PdfStream(contentDict, PdfFilters.Deflate(content)));
            var fontDict = new PdfDict();
            foreach (var name in used.Order(StringComparer.Ordinal))
                if (fontRefs.TryGetValue(name, out var r)) fontDict[name] = r;
            var resources = new PdfDict();
            if (fontDict.Items.Count > 0) resources["Font"] = fontDict;
            resources["ProcSet"] = new PdfArray([new PdfName("PDF"), new PdfName("Text")]);
            var pageDict = new PdfDict();
            pageDict["Type"] = new PdfName("Page");
            pageDict["Parent"] = pagesRef;
            pageDict["MediaBox"] = new PdfArray(0, 0, PageWidthPt, PageHeightPt);
            pageDict["Resources"] = resources;
            pageDict["Contents"] = contentRef;
            kids.Add(output.Add(pageDict));
        }
        var pages = new PdfDict();
        pages["Type"] = new PdfName("Pages");
        pages["Kids"] = kids;
        pages["Count"] = new PdfNum(kids.Count);
        output.Set(pagesRef, pages);
        var catalog = new PdfDict();
        catalog["Type"] = new PdfName("Catalog");
        catalog["Pages"] = pagesRef;
        catalog["Lang"] = PdfStr.FromText(language);
        output.Set(catalogRef, catalog);
        var info = new PdfDict();
        info["Title"] = PdfStr.FromText(title);
        info["Producer"] = PdfStr.FromText("Swivel");
        info["Creator"] = PdfStr.FromText("Swivel");
        info["CreationDate"] = PdfStr.FromText(PdfAssembler.PdfDate(DateTime.Now));
        output.Write(stream, catalogRef, output.Add(info));
    }

    static string N(double v) => PdfNum.Format(v);

    static string M(Matrix m) => $"{N(m.M11)} {N(m.M12)} {N(m.M21)} {N(m.M22)} {N(m.OffsetX)} {N(m.OffsetY)}";

    void WalkVisual(Visual visual, Matrix m, StringBuilder content, HashSet<string> used)
    {
        if (visual is UIElement { Visibility: not Visibility.Visible }) return;
        if (VisualTreeHelper.GetDrawing(visual) is { } drawing) WalkDrawing(drawing, m, content, used);
        int count = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(visual, i) is not Visual child) continue;
            var cm = VisualTreeHelper.GetTransform(child)?.Value ?? Matrix.Identity;
            var offset = VisualTreeHelper.GetOffset(child);
            cm.Translate(offset.X, offset.Y);
            WalkVisual(child, cm * m, content, used);
        }
    }

    void WalkDrawing(Drawing drawing, Matrix m, StringBuilder content, HashSet<string> used)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                if (group.Opacity <= 0) return;
                var gm = group.Transform?.Value ?? Matrix.Identity;
                foreach (var child in group.Children) WalkDrawing(child, gm * m, content, used);
                break;
            case GlyphRunDrawing glyphs when glyphs.GlyphRun != null:
                EmitGlyphRun(glyphs.GlyphRun, glyphs.ForegroundBrush, m, content, used);
                break;
            case GeometryDrawing geometry when geometry.Geometry != null:
                EmitGeometry(geometry.Geometry, geometry.Brush, geometry.Pen, m, content);
                break;
        }
    }

    static (double R, double G, double B)? Color(Brush? brush)
    {
        if (brush is SolidColorBrush { Color: var c } solid && c.A > 0 && solid.Opacity > 0)
        {
            // Partly transparent colours are mixed with the white page.
            double a = c.A / 255.0 * solid.Opacity;
            return (1 - a + a * c.R / 255.0, 1 - a + a * c.G / 255.0, 1 - a + a * c.B / 255.0);
        }
        if (brush is LinearGradientBrush { GradientStops.Count: > 0 } gradient)
        {
            var first = gradient.GradientStops[0].Color;
            return (first.R / 255.0, first.G / 255.0, first.B / 255.0);
        }
        return null;
    }

    static string Rgb((double R, double G, double B) c, bool stroke) => $"{N(Math.Round(c.R, 3))} {N(Math.Round(c.G, 3))} {N(Math.Round(c.B, 3))} {(stroke ? "RG" : "rg")}";

    static bool CanEmbed(GlyphTypeface typeface) => typeface.EmbeddingRights is
        FontEmbeddingRight.Installable or FontEmbeddingRight.Editable or FontEmbeddingRight.PreviewAndPrint;

    FontUse? Font(GlyphTypeface typeface)
    {
        // Faces of one .ttc file share the file name; tell them apart by name.
        var key = typeface.FontUri + "|" + string.Join(",", typeface.Win32FamilyNames.Values) + "|" + string.Join(",", typeface.Win32FaceNames.Values);
        if (fonts.TryGetValue(key, out var use)) return use.Failed ? null : use;
        use = new FontUse { Typeface = typeface, Resource = "F" + (fonts.Count + 1) };
        if (!CanEmbed(typeface)) use.Failed = true;
        else
        {
            // Only TrueType outlines are embedded (subset); other fonts (CFF, bitmap) are drawn as outlines.
            try
            {
                using var stream = typeface.GetFontStream();
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                use.Subset = PdfFontSubset.Open(data, typeface.Win32FamilyNames.Values, typeface.Win32FaceNames.Values, typeface.GlyphCount);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            use.Failed = use.Subset == null;
        }
        fonts[key] = use;
        return use.Failed ? null : use;
    }

    void EmitGlyphRun(GlyphRun run, Brush? brush, Matrix m, StringBuilder content, HashSet<string> used)
    {
        var color = Color(brush);
        if (color == null || run.GlyphIndices.Count == 0) return;
        var typeface = run.GlyphTypeface;
        var font = run.IsSideways ? null : Font(typeface);
        if (font == null)
        {
            // Not embeddable: draw the glyph outlines.
            EmitGeometry(run.BuildGeometry(), brush, null, m, content);
            return;
        }
        double em = run.FontRenderingEmSize;
        var indices = run.GlyphIndices;
        var advances = run.AdvanceWidths;
        var offsets = run.GlyphOffsets;
        bool rtl = (run.BidiLevel & 1) == 1;
        bool perGlyph = rtl || (offsets != null && offsets.Any(o => Math.Abs(o.X) > 0.001 || Math.Abs(o.Y) > 0.001));

        // Which characters each glyph stands for (clusters can map several characters to one glyph or the reverse).
        var chars = run.Characters;
        var clusterMap = run.ClusterMap;
        if (chars != null && chars.Count > 0)
        {
            int ci = 0;
            while (ci < chars.Count)
            {
                int glyph = clusterMap != null && ci < clusterMap.Count ? clusterMap[ci] : Math.Min(ci, indices.Count - 1);
                int cj = ci + 1;
                while (cj < chars.Count && clusterMap != null && cj < clusterMap.Count && clusterMap[cj] == glyph) cj++;
                var text = new string(chars.Skip(ci).Take(cj - ci).ToArray());
                if (glyph < indices.Count && text.Length > 0) font.Unicode.TryAdd(indices[glyph], text);
                ci = cj;
            }
        }
        foreach (var g in indices) font.Glyphs.Add(g);
        used.Add(font.Resource);

        var sims = typeface.StyleSimulations;
        double skew = (sims & StyleSimulations.ItalicSimulation) != 0 ? 1.0 / 3 : 0;
        content.Append("BT\n").Append(Rgb(color.Value, false)).Append('\n');
        if ((sims & StyleSimulations.BoldSimulation) != 0)
            content.Append(Rgb(color.Value, true)).Append(' ').Append(N(em * 0.03)).Append(" w 2 Tr\n");
        content.Append('/').Append(font.Resource).Append(' ').Append(N(em)).Append(" Tf\n");
        var origin = run.BaselineOrigin;

        Matrix TextMatrix(double x, double y) => new Matrix(1, 0, skew, -1, x, y) * m;

        if (!perGlyph)
        {
            content.Append(M(TextMatrix(origin.X, origin.Y))).Append(" Tm\n[<");
            for (int i = 0; i < indices.Count; i++)
            {
                content.Append(indices[i].ToString("X4"));
                if (i + 1 < indices.Count)
                {
                    double natural = typeface.AdvanceWidths.TryGetValue(indices[i], out var w) ? w * em : advances[i];
                    double adjust = (natural - advances[i]) / em * 1000;
                    if (Math.Abs(adjust) > 0.05) content.Append("> ").Append(N(Math.Round(adjust, 2))).Append(" <");
                }
            }
            content.Append(">] TJ\n");
        }
        else
        {
            double pen = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                double dx = offsets != null && i < offsets.Count ? offsets[i].X : 0;
                double dy = offsets != null && i < offsets.Count ? offsets[i].Y : 0;
                double x = rtl ? origin.X - pen - advances[i] - dx : origin.X + pen + dx;
                double y = origin.Y - dy;
                content.Append(M(TextMatrix(x, y))).Append(" Tm <").Append(indices[i].ToString("X4")).Append("> Tj\n");
                pen += advances[i];
            }
        }
        content.Append("ET\n");
    }

    static void EmitGeometry(Geometry geometry, Brush? brush, Pen? pen, Matrix m, StringBuilder content)
    {
        var fill = Color(brush);
        var stroke = pen != null && pen.Thickness > 0 ? Color(pen.Brush) : null;
        // The white page needs no painting.
        if (fill is { R: > 0.999, G: > 0.999, B: > 0.999 } && stroke == null) return;
        if (fill == null && stroke == null) return;
        var flat = geometry.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        if (flat.Figures.Count == 0) return;
        var gm = flat.Transform?.Value ?? Matrix.Identity;
        var path = new StringBuilder();
        foreach (var figure in flat.Figures)
        {
            var start = gm.Transform(figure.StartPoint);
            path.Append(N(start.X)).Append(' ').Append(N(start.Y)).Append(" m\n");
            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    LineSegment line => [line.Point],
                    PolyLineSegment poly => poly.Points,
                    BezierSegment bezier => [bezier.Point3],
                    PolyBezierSegment pb => pb.Points.Where((_, k) => k % 3 == 2),
                    QuadraticBezierSegment q => [q.Point2],
                    PolyQuadraticBezierSegment pq => pq.Points.Where((_, k) => k % 2 == 1),
                    ArcSegment arc => [arc.Point],
                    _ => [],
                };
                foreach (var p in points.Select(gm.Transform)) path.Append(N(Math.Round(p.X, 3))).Append(' ').Append(N(Math.Round(p.Y, 3))).Append(" l\n");
            }
            if (figure.IsClosed) path.Append("h\n");
        }
        content.Append("q ").Append(M(m)).Append(" cm\n");
        if (fill != null) content.Append(Rgb(fill.Value, false)).Append('\n');
        if (stroke != null)
        {
            content.Append(Rgb(stroke.Value, true)).Append(' ').Append(N(pen!.Thickness)).Append(" w ");
            content.Append(pen.StartLineCap switch { PenLineCap.Round => "1", PenLineCap.Square => "2", _ => "0" }).Append(" J\n");
        }
        content.Append(path);
        bool evenOdd = flat.FillRule == FillRule.EvenOdd;
        content.Append(fill != null && stroke != null ? (evenOdd ? "B*" : "B") : fill != null ? (evenOdd ? "f*" : "f") : "S").Append("\nQ\n");
    }

    PdfRef WriteFont(FontUse font)
    {
        var typeface = font.Typeface;
        var program = font.Subset!.Build(font.Glyphs, font.Unicode);

        string family = typeface.Win32FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var en) ? en : typeface.FamilyNames.Values.FirstOrDefault() ?? "Font";
        string face = typeface.Win32FaceNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var ef) ? ef : "Regular";
        var baseName = new string((family + (face is "Regular" or "Normal" ? "" : "-" + face)).Where(c => c is >= '!' and <= '~' && c is not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or ' ')).ToArray());
        if (baseName.Length == 0) baseName = "Font" + font.Resource;
        var tag = new StringBuilder();
        uint hash = (uint)(font.Resource.GetHashCode() ^ baseName.GetHashCode() ^ font.Glyphs.Count * 7919);
        for (int i = 0; i < 6; i++) { tag.Append((char)('A' + hash % 26)); hash /= 26; }
        var fontName = new PdfName(tag + "+" + baseName);

        var fileDict = new PdfDict();
        fileDict["Filter"] = new PdfName("FlateDecode");
        fileDict["Length1"] = new PdfNum(program.Length);
        var fileRef = output.Add(new PdfStream(fileDict, PdfFilters.Deflate(program)));

        bool italic = typeface.Style != FontStyles.Normal || (typeface.StyleSimulations & StyleSimulations.ItalicSimulation) != 0;
        var descriptor = new PdfDict();
        descriptor["Type"] = new PdfName("FontDescriptor");
        descriptor["FontName"] = fontName;
        descriptor["Flags"] = new PdfNum(4 | (italic ? 64 : 0));
        double ascent = typeface.Baseline * 1000, descent = -(typeface.Height - typeface.Baseline) * 1000;
        descriptor["FontBBox"] = new PdfArray(-500, Math.Round(descent), 1500, Math.Round(ascent));
        descriptor["ItalicAngle"] = new PdfNum(italic ? -12 : 0);
        descriptor["Ascent"] = new PdfNum(Math.Round(ascent));
        descriptor["Descent"] = new PdfNum(Math.Round(descent));
        descriptor["CapHeight"] = new PdfNum(Math.Round(typeface.CapsHeight * 1000));
        descriptor["StemV"] = new PdfNum(typeface.Weight.ToOpenTypeWeight() >= 600 ? 120 : 80);
        descriptor["FontFile2"] = fileRef;
        var descriptorRef = output.Add(descriptor);

        var widths = new PdfArray();
        var glyphs = font.Glyphs.ToList();
        int start = 0;
        while (start < glyphs.Count)
        {
            int end = start;
            while (end + 1 < glyphs.Count && glyphs[end + 1] == glyphs[end] + 1 && end - start < 500) end++;
            widths.Add(new PdfNum(glyphs[start]));
            widths.Add(new PdfArray(glyphs.Skip(start).Take(end - start + 1)
                .Select(g => (PdfObj)new PdfNum(Math.Round((typeface.AdvanceWidths.TryGetValue(g, out var w) ? w : 0) * 1000, 3))).ToList()));
            start = end + 1;
        }

        var cid = new PdfDict();
        cid["Type"] = new PdfName("Font");
        cid["Subtype"] = new PdfName("CIDFontType2");
        cid["BaseFont"] = fontName;
        var system = new PdfDict();
        system["Registry"] = PdfStr.FromText("Adobe");
        system["Ordering"] = PdfStr.FromText("Identity");
        system["Supplement"] = new PdfNum(0);
        cid["CIDSystemInfo"] = system;
        cid["FontDescriptor"] = descriptorRef;
        cid["DW"] = new PdfNum(1000);
        cid["W"] = widths;
        cid["CIDToGIDMap"] = new PdfName("Identity");
        var cidRef = output.Add(cid);

        var toUnicode = new PdfDict();
        toUnicode["Filter"] = new PdfName("FlateDecode");
        var toUnicodeRef = output.Add(new PdfStream(toUnicode, PdfFilters.Deflate(Encoding.ASCII.GetBytes(ToUnicodeCMap(font.Unicode)))));

        var type0 = new PdfDict();
        type0["Type"] = new PdfName("Font");
        type0["Subtype"] = new PdfName("Type0");
        type0["BaseFont"] = fontName;
        type0["Encoding"] = new PdfName("Identity-H");
        type0["DescendantFonts"] = new PdfArray([cidRef]);
        type0["ToUnicode"] = toUnicodeRef;
        return output.Add(type0);
    }

    public static string ToUnicodeCMap(IReadOnlyDictionary<ushort, string> map)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = map.OrderBy(kv => kv.Key).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (glyph, text) in chunk)
                sb.Append('<').Append(glyph.ToString("X4")).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }
}
