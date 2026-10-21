using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Swivel;

/// WPF text objects (FlowDocument, TextRange) belong to the STA thread that made them. Document work runs on its
/// own short-lived STA thread so it never blocks the UI and never touches the thread pool's MTA threads.
static class DocSta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        }, 64 * 1024 * 1024) { IsBackground = true, Name = "Swivel documents" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    public static Task Run(Action work) => Run(() => { work(); return true; });
}

/// Converts between the document model and WPF's FlowDocument (used for RTF and for laying out PDF pages).
static class DocFlow
{
    /// 1 pt = 96/72 device-independent pixels.
    public const double Dip = 96.0 / 72.0;
    public const string BaseFont = "Calibri";
    public const string CodeFont = "Consolas";

    // ─────────────────────────────── model → FlowDocument ───────────────────────────────

    public static FlowDocument ToFlow(DocModel model)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily(BaseFont),
            FontSize = DocWriters.DefaultSize * Dip,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(0),
            Language = XmlLanguage.GetLanguage(model.GuessLanguage()),
        };
        AddBlocks(doc.Blocks, model.Blocks);
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        return doc;
    }

    static void AddBlocks(BlockCollection into, List<DocBlock> blocks)
    {
        foreach (var (block, list) in DocWriters.Group(blocks))
        {
            if (list != null) into.Add(MakeList(list));
            else if (block is DocParagraph p) into.Add(MakeParagraph(p, false));
            else if (block is DocTable t && t.Rows.Count > 0) into.Add(MakeTable(t));
        }
    }

    static List MakeList(DocListNode node)
    {
        int depth = 0;
        var list = new List
        {
            MarkerStyle = node.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, node.Start),
            Margin = new Thickness(0),
            Padding = new Thickness(24 * Dip, 0, 0, 0),
        };
        foreach (var item in node.Items)
        {
            var li = new ListItem();
            if (item.Paragraph != null) li.Blocks.Add(MakeParagraph(item.Paragraph, false));
            else li.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            if (item.Child != null)
            {
                var child = MakeList(item.Child);
                child.MarkerStyle = item.Child.Ordered ? TextMarkerStyle.Decimal : (++depth % 2 == 1 ? TextMarkerStyle.Circle : TextMarkerStyle.Square);
                li.Blocks.Add(child);
            }
            list.ListItems.Add(li);
        }
        return list;
    }

    static Table MakeTable(DocTable t)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 3 * Dip, 0, 3 * Dip), BorderBrush = Brushes.Transparent };
        int columns = Math.Max(1, t.Columns);
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var border = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        border.Freeze();
        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            int used = 0;
            foreach (var cell in row)
            {
                var tc = new TableCell
                {
                    ColumnSpan = Math.Max(1, cell.ColSpan),
                    BorderBrush = border,
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(4 * Dip, 2 * Dip, 4 * Dip, 2 * Dip),
                };
                foreach (var (block, list) in DocWriters.Group(cell.Blocks))
                {
                    if (list != null) tc.Blocks.Add(MakeList(list));
                    else if (block is DocParagraph p) tc.Blocks.Add(MakeParagraph(p, cell.Header));
                    else if (block is DocTable inner && inner.Rows.Count > 0) tc.Blocks.Add(MakeTable(inner));
                }
                if (tc.Blocks.Count == 0) tc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
                tr.Cells.Add(tc);
                used += tc.ColumnSpan;
            }
            for (; used < columns; used++)
                tr.Cells.Add(new TableCell(new Paragraph { Margin = new Thickness(0) }) { BorderBrush = border, BorderThickness = new Thickness(0.75) });
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        return table;
    }

    static Paragraph MakeParagraph(DocParagraph p, bool header)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (p.Heading > 0)
        {
            paragraph.FontSize = DocWriters.HeadingSizes[p.Heading - 1] * Dip;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, (p.Heading <= 2 ? 12 : 8) * Dip, 0, 4 * Dip);
            paragraph.KeepWithNext = true;
        }
        if (p.Pre) paragraph.FontFamily = new FontFamily(CodeFont);
        if (header) paragraph.FontWeight = FontWeights.Bold;
        paragraph.TextAlignment = p.Align switch
        {
            DocAlign.Center => TextAlignment.Center,
            DocAlign.Right => TextAlignment.Right,
            DocAlign.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        int i = 0;
        while (i < p.Runs.Count)
        {
            var link = p.Runs[i].Link;
            if (link != null)
            {
                var hyperlink = new Hyperlink { Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x63, 0xC1)) };
                if (Uri.TryCreate(link, UriKind.RelativeOrAbsolute, out var uri)) hyperlink.NavigateUri = uri;
                while (i < p.Runs.Count && p.Runs[i].Link == link) AddRun(hyperlink.Inlines, p.Runs[i++]);
                paragraph.Inlines.Add(hyperlink);
            }
            else AddRun(paragraph.Inlines, p.Runs[i++]);
        }
        return paragraph;
    }

    static void AddRun(InlineCollection into, DocRun run)
    {
        var parts = run.Text.Split('\n');
        for (int k = 0; k < parts.Length; k++)
        {
            if (k > 0) into.Add(new LineBreak());
            if (parts[k].Length == 0) continue;
            var r = new Run(parts[k]);
            if (run.Bold) r.FontWeight = FontWeights.Bold;
            if (run.Italic) r.FontStyle = FontStyles.Italic;
            if (run.Underline || run.Strike || run.Link != null)
            {
                var decorations = new TextDecorationCollection();
                if (run.Underline || run.Link != null) decorations.Add(TextDecorations.Underline);
                if (run.Strike) decorations.Add(TextDecorations.Strikethrough);
                r.TextDecorations = decorations;
            }
            if (run.Code) r.FontFamily = new FontFamily(CodeFont);
            else if (run.Font != null) r.FontFamily = new FontFamily(run.Font + ", " + BaseFont);
            if (run.Size is { } size && size > 0) r.FontSize = Math.Clamp(size, 1, 400) * Dip;
            if (run.Color is { } hex && hex.Length == 6)
                r.Foreground = new SolidColorBrush(Color.FromRgb(
                    System.Convert.ToByte(hex[..2], 16), System.Convert.ToByte(hex[2..4], 16), System.Convert.ToByte(hex[4..], 16)));
            into.Add(r);
        }
    }

    // ─────────────────────────────── FlowDocument → model ───────────────────────────────

    public static DocModel FromFlow(FlowDocument doc)
    {
        // The most common size and font are the document's defaults; only differences are kept on runs.
        var sizes = new Dictionary<double, int>();
        var fonts = new Dictionary<string, int>();
        foreach (var run in Runs(doc.Blocks))
        {
            var size = Math.Round(run.FontSize / Dip * 2) / 2;
            sizes[size] = sizes.GetValueOrDefault(size) + run.Text.Length;
            var font = run.FontFamily.Source;
            fonts[font] = fonts.GetValueOrDefault(font) + run.Text.Length;
        }
        var context = new FlowReadContext
        {
            DefaultSize = sizes.Count > 0 ? sizes.MaxBy(kv => kv.Value).Key : DocWriters.DefaultSize,
            DefaultFont = fonts.Count > 0 ? fonts.MaxBy(kv => kv.Value).Key : BaseFont,
        };
        var model = new DocModel();
        context.ReadBlocks(doc.Blocks, model.Blocks, null, 0);
        model.Normalize();
        if (model.Blocks.Count == 0) model.Blocks.Add(new DocParagraph());
        return model;
    }

    static IEnumerable<Run> Runs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    foreach (var r in Runs(p.Inlines)) yield return r;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var r in Runs(item.Blocks)) yield return r;
                    break;
                case Section s:
                    foreach (var r in Runs(s.Blocks)) yield return r;
                    break;
                case Table t:
                    foreach (var group in t.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var r in Runs(cell.Blocks)) yield return r;
                    break;
            }
        }
    }

    static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run r) yield return r;
            else if (inline is Span s)
                foreach (var inner in Runs(s.Inlines)) yield return inner;
        }
    }

    sealed class FlowReadContext
    {
        public double DefaultSize;
        public string DefaultFont = BaseFont;
        int nextList;

        public void ReadBlocks(IEnumerable<Block> blocks, List<DocBlock> into, DocListInfo? listTemplate, int level)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p:
                        into.Add(ReadParagraph(p));
                        break;
                    case Section s:
                        ReadBlocks(s.Blocks, into, listTemplate, level);
                        break;
                    case List list:
                    {
                        bool ordered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                            or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
                        int id = listTemplate?.Id ?? ++nextList;
                        int listLevel = listTemplate == null ? 0 : level;
                        var info = new DocListInfo(id, ordered, Math.Min(listLevel, 8), Math.Max(1, list.StartIndex));
                        foreach (var item in list.ListItems)
                        {
                            bool first = true;
                            foreach (var child in item.Blocks)
                            {
                                if (child is Paragraph ip)
                                {
                                    var paragraph = ReadParagraph(ip);
                                    if (first) paragraph.List = info;
                                    first = false;
                                    into.Add(paragraph);
                                }
                                else if (child is List)
                                    ReadBlocks([child], into, info, listLevel + 1);
                                else ReadBlocks([child], into, null, 0);
                            }
                        }
                        break;
                    }
                    case Table t:
                    {
                        var table = new DocTable();
                        foreach (var group in t.RowGroups)
                            foreach (var row in group.Rows)
                            {
                                var cells = new List<DocCell>();
                                foreach (var cell in row.Cells)
                                {
                                    var c = new DocCell { ColSpan = Math.Max(1, cell.ColumnSpan) };
                                    ReadBlocks(cell.Blocks, c.Blocks, null, 0);
                                    cells.Add(c);
                                }
                                if (cells.Count > 0) table.Rows.Add(cells);
                            }
                        if (table.Rows.Count > 0) into.Add(table);
                        break;
                    }
                }
            }
        }

        DocParagraph ReadParagraph(Paragraph p)
        {
            var paragraph = new DocParagraph
            {
                Align = p.TextAlignment switch
                {
                    TextAlignment.Center => DocAlign.Center,
                    TextAlignment.Right => DocAlign.Right,
                    TextAlignment.Justify => DocAlign.Justify,
                    _ => DocAlign.Left,
                },
            };
            ReadInlines(p.Inlines, paragraph, null);
            return paragraph;
        }

        void ReadInlines(InlineCollection inlines, DocParagraph into, string? link)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run r when r.Text.Length > 0:
                        into.Runs.Add(Style(r, link));
                        break;
                    case LineBreak:
                        into.Runs.Add(new DocRun("\n") { Link = link });
                        break;
                    case Hyperlink h:
                        ReadInlines(h.Inlines, into, h.NavigateUri?.OriginalString ?? link);
                        break;
                    case Span s:
                        ReadInlines(s.Inlines, into, link);
                        break;
                }
            }
        }

        DocRun Style(Run r, string? link)
        {
            var run = new DocRun(r.Text.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                Bold = r.FontWeight.ToOpenTypeWeight() >= 600,
                Italic = r.FontStyle != FontStyles.Normal,
                Link = link,
            };
            for (DependencyObject? e = r; e is TextElement element; e = element.Parent)
            {
                var decorations = element switch
                {
                    Inline i => i.TextDecorations,
                    Paragraph p => p.TextDecorations,
                    _ => null,
                };
                if (decorations == null) continue;
                foreach (var d in decorations)
                {
                    if (d.Location == TextDecorationLocation.Underline) run.Underline = true;
                    if (d.Location == TextDecorationLocation.Strikethrough) run.Strike = true;
                }
                if (element is Block) break;
            }
            if (link != null) run.Underline = false;
            var size = Math.Round(r.FontSize / Dip * 2) / 2;
            if (Math.Abs(size - DefaultSize) > 0.01) run.Size = size;
            var font = r.FontFamily.Source;
            var primary = font.Split(',')[0].Trim();
            if (primary is "Consolas" or "Courier New" or "Courier" or "Lucida Console") run.Code = true;
            else if (font != DefaultFont) run.Font = primary;
            if (r.Foreground is SolidColorBrush { Color: var c } && (c.R > 40 || c.G > 40 || c.B > 40) && link == null && c.A > 0)
                run.Color = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            return run;
        }
    }

    // ─────────────────────────────── RTF ───────────────────────────────

    /// Reads RTF with WPF's converter. Must run on an STA thread.
    public static DocModel ReadRtf(byte[] bytes)
    {
        var doc = new FlowDocument();
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        try
        {
            using var stream = new MemoryStream(bytes);
            range.Load(stream, DataFormats.Rtf);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            throw new ToolError(L("Couldn't read this document"));
        }
        return FromFlow(doc);
    }

    /// Writes RTF with WPF's converter. Must run on an STA thread.
    public static void WriteRtf(DocModel model, Stream stream)
    {
        var doc = ToFlow(model);
        new TextRange(doc.ContentStart, doc.ContentEnd).Save(stream, DataFormats.Rtf);
    }
}
