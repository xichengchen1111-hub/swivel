using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public readonly record struct PdfLayoutGlyph(int Scalar, int Font, float X, float Advance);
public sealed record PdfLayoutSettings(double PageWidth, double PageHeight, double Margin, double FontSize, double LineHeight, double TabStop, int MaxPages)
{
    /// A4 portrait, 20 mm margins, 11 pt text on a 15.4 pt line pitch, tab stops every 0.5 inch.
    public static readonly PdfLayoutSettings A4 = new(595.276, 841.89, 56.693, 11, 15.4, 36, 2000);
    public double LineWidth => PageWidth - 2 * Margin;
    public int LinesPerPage => Math.Max(1, (int)Math.Floor((PageHeight - 2 * Margin) / LineHeight));
}
public sealed record PdfTextLayoutResult(IReadOnlyList<PdfLayoutGlyph[]> Lines, int LinesPerPage)
{
    public int Pages => Math.Max(1, (Lines.Count + LinesPerPage - 1) / LinesPerPage);
}

/// Font-independent plain-text layout: paragraphs and line breaks, greedy wrapping at spaces,
/// tabs and East Asian character boundaries (with simple no-break-before/after punctuation),
/// a character break for words wider than the line, tab stops and page breaks. No shaping,
/// kerning, ligatures, bidirectional ordering or hyphenation.
public static class PdfTextLayout
{
    public const string ComplexScript = "PDF (text) can't lay out {0}: right-to-left and complex scripts (such as Arabic, Hebrew, Indic or Thai) and combining marks need text shaping, which this plain export doesn't do. Nothing was saved; choose TXT, RTF, DOCX, HTML or ODT.";
    public const string TooManyPages = "PDF (text) export is limited to 2,000 pages. This text needs more; nothing was saved. Choose TXT, RTF, DOCX, HTML or ODT.";

    /// Zero-width format characters and selectors: kept out of the layout and never drawn.
    public static bool Ignorable(int s) => s is 0x00AD or 0x034F or (>= 0x200B and <= 0x200F) or 0x202A or 0x202C or 0x202D or (>= 0x2060 and <= 0x2064) or 0x2066 or 0x2068 or 0x2069
        or 0xFEFF or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or (>= 0xE0000 and <= 0xE007F);
    public static bool Unsupported(int s)
    {
        if (Ignorable(s)) return false;
        if (s is 0x202B or 0x202E or 0x2067 or (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFE) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF)
            or (>= 0x0900 and <= 0x0DFF) or (>= 0x0E00 and <= 0x0FFF) or (>= 0x1000 and <= 0x109F) or (>= 0x1100 and <= 0x11FF) or (>= 0x1700 and <= 0x18AF) or (>= 0x1900 and <= 0x1CFF)
            or (>= 0xA800 and <= 0xAB2F) or (>= 0xABC0 and <= 0xABFF) or (>= 0xD7B0 and <= 0xD7FF) or (>= 0x11000 and <= 0x11FFF)) return true;
        return CharUnicodeInfo.GetUnicodeCategory(s) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
    public static bool EastAsian(int s) => s is (>= 0x2E80 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x1F300 and <= 0x1FAFF) or (>= 0x20000 and <= 0x3FFFF);
    private const string NoBreakBefore = "、。，．：；？！）」』】〕〉》〙〗〟｝］’”ー…‥ゝゞヽヾぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ々〻・゠〜)]},.;:!?%";
    private const string NoBreakAfter = "（「『【〔〈《〘〖〝｛［‘“([{";
    private static bool Before(int s) => s <= 0xFFFF && NoBreakBefore.Contains((char)s);
    private static bool After(int s) => s <= 0xFFFF && NoBreakAfter.Contains((char)s);
    /// A break is allowed between two adjacent visible characters when either is East Asian,
    /// unless the next closes or the previous opens.
    public static bool BreakBetween(int previous, int next) => (EastAsian(previous) || EastAsian(next)) && !Before(next) && !After(previous) && next != ' ';

    /// `measure` gives a scalar's font and advance in points, or null when no font has it.
    public static PdfTextLayoutResult Layout(string text, Func<int, (int Font, double Advance)?> measure, PdfLayoutSettings settings, Func<int, Exception> missing, CancellationToken token)
    {
        DocumentText.Validate(text, token);
        var lines = new List<PdfLayoutGlyph[]>(); int limit = checked(settings.MaxPages * settings.LinesPerPage);
        double width = settings.LineWidth;
        var line = new List<PdfLayoutGlyph>(); double x = 0; int lastBreak = -1, previous = -1; bool softStart = false;
        void Emit(List<PdfLayoutGlyph> glyphs)
        {
            int end = glyphs.Count; while (end > 0 && glyphs[end - 1].Scalar == ' ') end--;
            if (lines.Count >= limit) throw new UserError(TooManyPages);
            lines.Add(glyphs.GetRange(0, end).ToArray());
        }
        void Wrap()
        {
            if (lastBreak >= 0 && lastBreak < line.Count && (lastBreak > 0 || line[0].X > 0))
            {
                var rest = line.GetRange(lastBreak, line.Count - lastBreak); line.RemoveRange(lastBreak, line.Count - lastBreak); Emit(line);
                float shift = rest[0].X; line = rest.Select(g => g with { X = g.X - shift }).ToList(); x -= shift;
            }
            else { Emit(line); line = new(); x = 0; }
            lastBreak = -1; softStart = true;
        }
        void HardBreak() { Emit(line); line = new(); x = 0; lastBreak = -1; previous = -1; softStart = false; }
        (int Font, double Advance) Measure(int scalar) => measure(scalar) ?? throw missing(scalar);

        for (int at = 0; at < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            System.Text.Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out int used); at += used; int s = rune.Value;
            if (s == '\r') { if (at < text.Length && text[at] == '\n') at++; if (at < text.Length) HardBreak(); else { Emit(line); line = null!; } continue; }
            if (s is '\n' or 0x2028 or 0x2029) { if (at < text.Length) HardBreak(); else { Emit(line); line = null!; } continue; }
            if (Unsupported(s)) throw new UserError(ComplexScript, "U+" + s.ToString("X4", CultureInfo.InvariantCulture));
            if (Ignorable(s)) { if (s == 0x200B) lastBreak = line.Count; continue; }
            if (s == '\t')
            {
                double stop = (Math.Floor(x / settings.TabStop + 1e-9) + 1) * settings.TabStop;
                if (stop > width && line.Count > 0) { Emit(line); line = new(); x = 0; stop = settings.TabStop; }
                x = Math.Min(stop, width); lastBreak = line.Count; previous = -1; softStart = false; continue;
            }
            if (s == ' ')
            {
                if (softStart) continue;
                var (font, advance) = Measure(s);
                // A space that doesn't fit ends the line and is dropped, with any spaces after it.
                if (x + advance > width + 1e-6) { Emit(line); line = new(); x = 0; lastBreak = -1; previous = -1; softStart = true; continue; }
                line.Add(new(s, font, (float)x, (float)advance)); x += advance; lastBreak = line.Count; previous = s; continue;
            }
            var glyph = Measure(s);
            if (previous >= 0 && BreakBetween(previous, s)) lastBreak = line.Count;
            // Wrap at the last opportunity; a word wider than the line breaks between characters.
            while (x + glyph.Advance > width + 1e-6 && (line.Count > 0 || x > 0)) Wrap();
            softStart = false;
            line.Add(new(s, glyph.Font, (float)x, (float)glyph.Advance)); x += glyph.Advance; previous = s;
        }
        if (line is not null) Emit(line);
        return new(lines, settings.LinesPerPage);
    }
}

/// A candidate font for the PDF text export, loaded only when an earlier one lacks a character.
/// `Load` returns null when the font isn't installed.
public sealed record PdfFontCandidate(string Label, Func<CancellationToken, TrueTypeFace?> Load);

/// Original bounded PDF 1.4 writer for plain text: each used font is embedded as a TrueType
/// subset (CIDFontType2, Identity-H) with a ToUnicode CMap, so the text stays selectable and
/// searchable. No formatting, links, images, metadata or document information.
public sealed class DocumentPdf
{
    public const string Missing = "No embeddable font among those Swivel uses for PDF has a glyph for {0}. Nothing was saved; choose TXT, RTF, DOCX, HTML or ODT.";
    public const string TooManyCharacters = "PDF (text) supports at most 65,000 different characters per font. Nothing was saved; choose TXT, RTF, DOCX, HTML or ODT.";
    /// One embedded font: its subset and a CID for every distinct character drawn with it, so
    /// characters sharing a glyph (such as space and no-break space) keep their own ToUnicode entry.
    private sealed record EmbeddedFont(TrueTypeFace Face, TrueTypeFace.Subset Subset, int[] Scalars, Dictionary<int, int> Cids);
    private readonly PdfTextLayoutResult layout;
    private readonly EmbeddedFont[] fonts;
    private readonly PdfLayoutSettings settings;
    public int Pages => layout.Pages;
    public IReadOnlyList<string> FontNames => fonts.Select(f => f.Face.PostScriptName).ToArray();
    private DocumentPdf(PdfTextLayoutResult layout, EmbeddedFont[] fonts, PdfLayoutSettings settings) { this.layout = layout; this.fonts = fonts; this.settings = settings; }

    /// Width in PDF glyph-space units (1/1000 em), rounded as written in /W; layout uses the same value.
    private static double Units(TrueTypeFace face, int glyph) => Math.Round(face.Advance(glyph) * 1000.0 / face.UnitsPerEm, 3);

    /// Chooses fonts, lays out the whole text and builds the font subsets before anything is
    /// written: refuses complex scripts, characters no embeddable font covers, damaged fonts and
    /// text over the page limit.
    public static DocumentPdf Prepare(string text, IReadOnlyList<PdfFontCandidate> candidates, CancellationToken token, PdfLayoutSettings? settings = null)
    {
        settings ??= PdfLayoutSettings.A4;
        var loaded = new TrueTypeFace?[candidates.Count]; var tried = new bool[candidates.Count];
        var used = new List<TrueTypeFace>(); var usedIndex = new Dictionary<int, int>();
        var cache = new Dictionary<int, (int, double)?>();
        (int Font, double Advance)? Measure(int scalar)
        {
            if (cache.TryGetValue(scalar, out var known)) return known;
            (int, double)? result = null;
            bool blank = scalar == ' ' || CharUnicodeInfo.GetUnicodeCategory(scalar) == UnicodeCategory.SpaceSeparator;
            for (int i = 0; i < candidates.Count && result is null; i++)
            {
                if (!tried[i])
                {
                    tried[i] = true;
                    try { var face = candidates[i].Load(token); loaded[i] = face is { MayEmbedSubset: true } ? face : null; }
                    catch (UserError) { loaded[i] = null; }
                }
                // A visible character whose glyph has no outline (a color-only emoji, say) would print blank.
                if (loaded[i] is not { } f || f.Glyph(scalar) is not (> 0 and var glyph) || !blank && !f.HasOutline(glyph)) continue;
                if (!usedIndex.TryGetValue(i, out int index)) { index = used.Count; used.Add(f); usedIndex[i] = index; }
                result = (index, Units(f, glyph) * settings.FontSize / 1000);
            }
            cache[scalar] = result; return result;
        }
        var layout = PdfTextLayout.Layout(text, Measure, settings, s => new UserError(Missing, "U+" + s.ToString("X4", CultureInfo.InvariantCulture)), token);
        var scalars = used.Select(_ => new SortedSet<int>()).ToArray();
        foreach (var line in layout.Lines) { token.ThrowIfCancellationRequested(); foreach (var g in line) scalars[g.Font].Add(g.Scalar); }
        var fonts = new EmbeddedFont[used.Count];
        for (int i = 0; i < used.Count; i++)
        {
            if (scalars[i].Count > 65000) throw new UserError(TooManyCharacters);
            var unicode = scalars[i].ToDictionary(s => s, s => used[i].Glyph(s));
            var order = scalars[i].ToArray(); var cids = new Dictionary<int, int>();
            for (int c = 0; c < order.Length; c++) cids[order[c]] = c + 1;
            fonts[i] = new(used[i], used[i].Build(unicode.Values, unicode, token), order, cids);
        }
        token.ThrowIfCancellationRequested();
        return new(layout, fonts, settings);
    }

    /// Writes the PDF. The destination stays open: only an internal buffer is flushed into it.
    public void Write(Stream destination, CancellationToken token)
    {
        if (!destination.CanWrite) throw new ArgumentException("Writable output required.");
        var output = new BufferedStream(destination, 1 << 16); // never disposed: that would close the destination
        long position = 0; int pages = layout.Pages, objects = 3 + fonts.Length * 6 + pages * 2;
        var offsets = new long[objects];
        void Raw(byte[] bytes) { token.ThrowIfCancellationRequested(); output.Write(bytes); position += bytes.Length; }
        void Text(string value) => Raw(Encoding.ASCII.GetBytes(value));
        void Object(int id, string body) { offsets[id] = position; Text($"{id} 0 obj\n{body}\nendobj\n"); }
        void Stream(int id, string dictionary, byte[] data, bool compress)
        {
            byte[] body = data;
            if (compress) { using var buffer = new MemoryStream(); using (var zip = new ZLibStream(buffer, CompressionLevel.Optimal, true)) zip.Write(data); body = buffer.ToArray(); }
            offsets[id] = position;
            Text($"{id} 0 obj\n<< {(dictionary.Length > 0 ? dictionary + " " : "")}{(compress ? "/Filter /FlateDecode " : "")}/Length {body.Length} >>\nstream\n"); Raw(body); Text("\nendstream\nendobj\n");
        }
        static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        Text("%PDF-1.4\n"); Raw(new byte[] { 37, 226, 227, 207, 211, 10 });
        int firstPage = 3 + fonts.Length * 6;
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Count {pages} /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{firstPage + i * 2} 0 R")) + "] >>");
        for (int i = 0; i < fonts.Length; i++)
        {
            var (face, subset, scalars, _) = fonts[i]; int b = 3 + i * 6; double scale = 1000.0 / face.UnitsPerEm;
            string name = Tag(subset.Font) + "+" + (face.PostScriptName.Length > 0 ? face.PostScriptName : "SwivelFont" + i.ToString(CultureInfo.InvariantCulture));
            Object(b, $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{b + 1} 0 R] /ToUnicode {b + 4} 0 R >>");
            // CID 0 is .notdef; CID n is the n-th distinct character, drawn with its (shared) glyph.
            var gids = new int[scalars.Length + 1]; var widths = new StringBuilder("[0 [").Append(N(Units(face, 0)));
            for (int c = 0; c < scalars.Length; c++) { int glyph = face.Glyph(scalars[c]); gids[c + 1] = subset.NewGlyphs[glyph]; widths.Append(' ').Append(N(Units(face, glyph))); }
            widths.Append("]]");
            var map = new byte[gids.Length * 2]; for (int c = 0; c < gids.Length; c++) { map[c * 2] = (byte)(gids[c] >> 8); map[c * 2 + 1] = (byte)gids[c]; }
            Object(b + 1, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {b + 2} 0 R /DW 1000 /W {widths} /CIDToGIDMap {b + 5} 0 R >>");
            Object(b + 2, $"<< /Type /FontDescriptor /FontName /{name} /Flags 32 /FontBBox [{N(face.Box.XMin * scale)} {N(face.Box.YMin * scale)} {N(face.Box.XMax * scale)} {N(face.Box.YMax * scale)}] /ItalicAngle 0 /Ascent {N(face.Ascender * scale)} /Descent {N(face.Descender * scale)} /CapHeight {N(face.CapHeight * scale)} /StemV 80 /FontFile2 {b + 3} 0 R >>");
            Stream(b + 3, $"/Length1 {subset.Font.Length}", subset.Font, true);
            Stream(b + 4, "", Encoding.ASCII.GetBytes(ToUnicode(scalars)), true);
            Stream(b + 5, "", map, true);
        }
        string resources = string.Join(" ", Enumerable.Range(0, fonts.Length).Select(i => $"/F{i} {3 + i * 6} 0 R"));
        for (int p = 0; p < pages; p++)
        {
            token.ThrowIfCancellationRequested();
            int page = firstPage + p * 2;
            Object(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(settings.PageWidth)} {N(settings.PageHeight)}] /Resources << /Font << {resources} >> >> /Contents {page + 1} 0 R >>");
            var content = new StringBuilder("BT\n"); int current = -1;
            for (int l = p * layout.LinesPerPage; l < Math.Min(layout.Lines.Count, (p + 1) * layout.LinesPerPage); l++)
            {
                var line = layout.Lines[l]; double y = settings.PageHeight - settings.Margin - settings.FontSize - (l - p * layout.LinesPerPage) * settings.LineHeight;
                for (int start = 0; start < line.Length;)
                {
                    int end = start + 1;
                    while (end < line.Length && line[end].Font == line[start].Font && Math.Abs(line[end].X - (line[end - 1].X + line[end - 1].Advance)) < 0.01) end++;
                    int font = line[start].Font;
                    if (font != current) { content.Append("/F").Append(font).Append(' ').Append(N(settings.FontSize)).Append(" Tf\n"); current = font; }
                    content.Append("1 0 0 1 ").Append(N(settings.Margin + line[start].X)).Append(' ').Append(N(y)).Append(" Tm <");
                    for (int g = start; g < end; g++) content.Append(fonts[font].Cids[line[g].Scalar].ToString("X4", CultureInfo.InvariantCulture));
                    content.Append("> Tj\n"); start = end;
                }
            }
            content.Append("ET\n");
            Stream(page + 1, "", Encoding.ASCII.GetBytes(content.ToString()), true);
        }
        long xref = position; Text($"xref\n0 {objects}\n0000000000 65535 f \n");
        for (int i = 1; i < objects; i++) Text(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Text($"trailer\n<< /Size {objects} /Root 1 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        output.Flush(); token.ThrowIfCancellationRequested();
    }

    /// Six uppercase letters derived from the subset's bytes (PDF 9.6.4 subset tag).
    private static string Tag(byte[] font)
    {
        uint hash = 2166136261; foreach (byte b in font) hash = (hash ^ b) * 16777619;
        var tag = new char[6]; for (int i = 0; i < 6; i++) { tag[i] = (char)('A' + hash % 26); hash /= 26; }
        return new string(tag);
    }

    /// CID n (1-based) is the n-th scalar.
    private static string ToUnicode(int[] scalars)
    {
        var text = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int i = 0; i < scalars.Length; i += 100)
        {
            int count = Math.Min(100, scalars.Length - i);
            text.Append(count).Append(" beginbfchar\n");
            for (int c = i; c < i + count; c++)
            {
                text.Append('<').Append((c + 1).ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char unit in char.ConvertFromUtf32(scalars[c])) text.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
                text.Append(">\n");
            }
            text.Append("endbfchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return text.ToString();
    }

    /// The fonts to try, in order, for a given text: the Latin UI font first, then the East Asian
    /// font that matches the text (kana → Japanese, Hangul → Korean, otherwise the UI's Chinese
    /// variant), then the other East Asian fonts and symbols. Keys are font file names.
    public static IReadOnlyList<string> FontOrder(string text, string uiCulture)
    {
        bool kana = false, hangul = false;
        foreach (var rune in text.EnumerateRunes()) { int s = rune.Value; kana |= s is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F; hangul |= s is >= 0xAC00 and <= 0xD7AF or >= 0x3130 and <= 0x318F; if (kana && hangul) break; }
        bool traditional = uiCulture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || uiCulture is "zh-TW" or "zh-HK" or "zh-MO";
        string[] chinese = traditional ? new[] { "msjh.ttc", "msyh.ttc" } : new[] { "msyh.ttc", "msjh.ttc" }, japanese = { "YuGothR.ttc", "meiryo.ttc", "msgothic.ttc" }, korean = { "malgun.ttf" };
        IEnumerable<string> asian = kana ? japanese.Concat(chinese).Concat(korean) : hangul ? korean.Concat(chinese).Concat(japanese) : uiCulture.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? japanese.Concat(chinese).Concat(korean) : uiCulture.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? korean.Concat(chinese).Concat(japanese) : chinese.Concat(japanese).Concat(korean);
        return new[] { "segoeui.ttf", "arial.ttf" }.Concat(asian).Concat(new[] { "simsun.ttc", "seguisym.ttf", "seguiemj.ttf" }).ToArray();
    }
}
