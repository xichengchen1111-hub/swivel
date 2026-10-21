using System.Globalization;
using System.Text;

namespace Swivel;

// The in-memory rich text every document reader produces and every writer consumes. It stands in for the Mac app's
// NSAttributedString: paragraphs of styled runs, headings, simple lists and tables.

/// A stretch of text with one style. `Text` may contain '\t' (tab) and '\n' (line break inside the paragraph).
sealed class DocRun
{
    public string Text = "";
    public bool Bold, Italic, Underline, Strike;
    /// Monospaced (Markdown/HTML code).
    public bool Code;
    /// Font size in points; null = the document's default.
    public double? Size;
    public string? Font;
    /// "RRGGBB"; null = automatic (black).
    public string? Color;
    public string? Link;

    public DocRun() { }
    public DocRun(string text) { Text = text; }

    public DocRun With(string text)
    {
        var copy = (DocRun)MemberwiseClone();
        copy.Text = text;
        return copy;
    }

    public bool SameStyle(DocRun o) =>
        Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Strike == o.Strike && Code == o.Code &&
        Size == o.Size && Font == o.Font && Color == o.Color && Link == o.Link;

    public override string ToString() => Text;
}

enum DocAlign { Left, Center, Right, Justify }

/// Membership of a list: paragraphs with the same `Id` belong to one list; `Level` 0 is the outermost.
sealed record DocListInfo(int Id, bool Ordered, int Level, int Start = 1);

abstract class DocBlock;

sealed class DocParagraph : DocBlock
{
    public List<DocRun> Runs = [];
    /// 1…6 for headings, 0 for body text.
    public int Heading;
    public DocListInfo? List;
    /// Preformatted (code block) text.
    public bool Pre;
    public DocAlign Align;

    public string Text => string.Concat(Runs.Select(r => r.Text));

    public DocParagraph() { }
    public DocParagraph(string text) { if (text.Length > 0) Runs.Add(new DocRun(text)); }

    /// Merges neighbouring runs of the same style and drops empty ones.
    public void Normalize()
    {
        var merged = new List<DocRun>();
        foreach (var run in Runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].SameStyle(run)) merged[^1] = merged[^1].With(merged[^1].Text + run.Text);
            else merged.Add(run);
        }
        Runs = merged;
    }
}

sealed class DocCell
{
    public List<DocBlock> Blocks = [];
    public int ColSpan = 1;
    public bool Header;

    public string Text => string.Join("\n", DocModel.Paragraphs(Blocks).Select(p => p.Text));
}

sealed class DocTable : DocBlock
{
    public List<List<DocCell>> Rows = [];
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Sum(c => Math.Max(1, c.ColSpan)));
}

sealed class DocModel
{
    public List<DocBlock> Blocks = [];
    /// The document's main language as a BCP-47 tag (for picking CJK fonts), or null.
    public string? Language;

    public static IEnumerable<DocParagraph> Paragraphs(IEnumerable<DocBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is DocParagraph p) yield return p;
            else if (block is DocTable t)
                foreach (var row in t.Rows)
                    foreach (var cell in row)
                        foreach (var inner in Paragraphs(cell.Blocks)) yield return inner;
        }
    }

    public IEnumerable<DocParagraph> AllParagraphs => Paragraphs(Blocks);

    public bool HasText => AllParagraphs.Any(p => p.Runs.Any(r => r.Text.Any(c => !char.IsWhiteSpace(c))));

    public void Normalize()
    {
        foreach (var p in AllParagraphs) p.Normalize();
    }

    /// The text as one string: paragraphs separated by '\n', table cells by '\t', list items with their markers.
    public string PlainText(bool markers = true, string newline = "\n")
    {
        var sb = new StringBuilder();
        var numbers = markers ? ListMarkers() : null;
        AppendPlain(sb, Blocks, numbers, newline);
        if (sb.Length >= newline.Length) sb.Length -= newline.Length;
        return sb.ToString();
    }

    void AppendPlain(StringBuilder sb, List<DocBlock> blocks, Dictionary<DocParagraph, string>? numbers, string newline)
    {
        foreach (var block in blocks)
        {
            if (block is DocParagraph p)
            {
                if (numbers != null && numbers.TryGetValue(p, out var marker))
                    sb.Append(new string('\t', p.List!.Level)).Append(marker).Append(' ');
                sb.Append(p.Text.Replace("\n", newline)).Append(newline);
            }
            else if (block is DocTable t)
            {
                foreach (var row in t.Rows)
                {
                    sb.Append(string.Join("\t", row.Select(c => c.Text.Replace("\n", " ").Replace("\t", " "))));
                    sb.Append(newline);
                }
            }
        }
    }

    /// "•" / "1." style markers for every list paragraph, numbered per list and level like a word processor.
    public Dictionary<DocParagraph, string> ListMarkers()
    {
        var result = new Dictionary<DocParagraph, string>();
        var counters = new Dictionary<(int, int), int>();
        foreach (var p in AllParagraphs)
        {
            if (p.List is not { } list) continue;
            // A new item at a level restarts the deeper levels.
            foreach (var key in counters.Keys.Where(k => k.Item1 == list.Id && k.Item2 > list.Level).ToList()) counters.Remove(key);
            int n = counters.TryGetValue((list.Id, list.Level), out var c) ? c + 1 : list.Start;
            counters[(list.Id, list.Level)] = n;
            result[p] = list.Ordered ? $"{n}." : (list.Level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
        }
        return result;
    }

    /// Picks a language from the text so CJK characters get a matching font (Han characters look different in
    /// Chinese and Japanese fonts).
    public string GuessLanguage()
    {
        if (Language != null) return Language;
        bool han = false, kana = false, hangul = false;
        foreach (var p in AllParagraphs)
            foreach (var r in p.Runs)
                foreach (var ch in r.Text)
                {
                    if (ch is >= '぀' and <= 'ヿ') kana = true;
                    else if (ch is >= '가' and <= '힯') hangul = true;
                    else if (ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han = true;
                }
        if (kana) return "ja-JP";
        if (hangul && !han) return "ko-KR";
        if (han)
        {
            var ui = CultureInfo.CurrentUICulture.Name;
            if (ui.StartsWith("zh-Hant") || ui is "zh-TW" or "zh-HK" or "zh-MO") return "zh-TW";
            if (ui.StartsWith("ja")) return "ja-JP";
            return "zh-CN";
        }
        return "en-US";
    }

    /// Words and characters, for Word Count.
    ///
    /// Words: runs of letters/digits separated by whitespace count as one word each, as on the Mac. Chinese, Japanese
    /// and Korean text has no spaces between words, so — like Microsoft Word's word count — every CJK character
    /// (ideographs, kana, Hangul syllables) counts as one word; CJK and full-width punctuation is not counted as a word.
    /// Characters: user-perceived characters (grapheme clusters) including spaces and line breaks, like Swift's
    /// `String.count` on the Mac.
    public static (int Words, int Characters) Count(string text)
    {
        int words = 0;
        bool inWord = false;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                cp = char.ConvertToUtf32(text[i], text[++i]);
            if (IsCjk(cp))
            {
                words++;
                inWord = false;
            }
            else if (IsSeparator(cp)) inWord = false;
            else if (!inWord) { words++; inWord = true; }
        }
        int characters = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) characters++;
        return (words, characters);
    }

    static bool IsSeparator(int cp)
    {
        if (cp <= 0xFFFF && char.IsWhiteSpace((char)cp)) return true;
        // CJK symbols and punctuation, full-width forms' punctuation: separators, not words.
        if (cp is >= 0x3000 and <= 0x303F) return true;
        if (cp is >= 0xFF01 and <= 0xFF0F or >= 0xFF1A and <= 0xFF20 or >= 0xFF3B and <= 0xFF40 or >= 0xFF5B and <= 0xFF65) return true;
        return false;
    }

    public static bool IsCjk(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF       // CJK Unified Ideographs
            or >= 0x3400 and <= 0x4DBF      // Extension A
            or >= 0x20000 and <= 0x3134F    // Extensions B–H
            or >= 0xF900 and <= 0xFAFF      // Compatibility Ideographs
            or >= 0x3040 and <= 0x30FF      // Hiragana, Katakana
            or >= 0x31F0 and <= 0x31FF      // Katakana extensions
            or >= 0xFF66 and <= 0xFF9F      // Half-width katakana
            or >= 0xAC00 and <= 0xD7AF      // Hangul syllables
            or >= 0x3100 and <= 0x312F;     // Bopomofo
}
