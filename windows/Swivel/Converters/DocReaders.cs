using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Swivel;

/// Plain text decoding shared by the readers.
static partial class DocEncoding
{
    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    static int registered;

    static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 0) Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// The system's ANSI code page (936 on Simplified Chinese Windows, 1252 on English…).
    public static Encoding Ansi
    {
        get
        {
            Register();
            try { return Encoding.GetEncoding((int)GetACP()); }
            catch (Exception) { return Encoding.GetEncoding(1252); }
        }
    }

    public static Encoding? ByName(string name)
    {
        Register();
        try { return Encoding.GetEncoding(name.Trim().Trim('"', '\'')); }
        catch (ArgumentException) { return null; }
    }

    public static Encoding CodePage(int codePage)
    {
        Register();
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception) { return Encoding.GetEncoding(1252); }
    }

    /// BOM → that encoding; otherwise UTF-8 when the bytes are valid UTF-8, else the system ANSI code page.
    public static string Decode(byte[] bytes, Encoding? declared = null)
    {
        if (Bom(bytes) is { } bom) return bom.Encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length);
        if (declared != null) return declared.GetString(bytes);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Ansi.GetString(bytes); }
    }

    public static (Encoding Encoding, int Length)? Bom(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), 3);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return (new UTF32Encoding(false, false), 4);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return (new UTF32Encoding(true, false), 4);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, false), 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, false), 2);
        return null;
    }
}

static class DocReaders
{
    /// Each line becomes a paragraph; tabs and spaces are kept.
    public static DocModel ReadText(string text)
    {
        var model = new DocModel();
        foreach (var line in SplitLines(text)) model.Blocks.Add(new DocParagraph(line));
        return model;
    }

    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        // A final line break ends the last line rather than starting an empty one.
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // ─────────────────────────────── Markdown ───────────────────────────────

    /// Inline-only Markdown that keeps whitespace and line breaks as written, like the Mac app's
    /// `AttributedString(markdown:, options: .inlineOnlyPreservingWhitespace)`: **bold**, *italic*, `code`,
    /// [links](url), <autolinks>, ~~strikethrough~~ and backslash escapes. Block syntax (# headings, lists) stays text.
    public static DocModel ReadMarkdown(string source)
    {
        var runs = new MarkdownInline(source.Replace("\r\n", "\n").Replace('\r', '\n')).Parse();
        var model = new DocModel();
        var paragraph = new DocParagraph();
        foreach (var run in runs)
        {
            var parts = run.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    model.Blocks.Add(paragraph);
                    paragraph = new DocParagraph();
                }
                if (parts[i].Length > 0) paragraph.Runs.Add(run.With(parts[i]));
            }
        }
        if (paragraph.Runs.Count > 0 || model.Blocks.Count == 0) model.Blocks.Add(paragraph);
        model.Normalize();
        return model;
    }

    sealed class MarkdownInline(string text)
    {
        sealed class Segment
        {
            public DocRun Run = new();
            // Delimiter runs (* _ ~) awaiting emphasis processing.
            public char Delim;
            public int Count, Original;
            public bool CanOpen, CanClose, Active;
        }

        readonly List<Segment> segments = [];

        public List<DocRun> Parse() => ParseRange(0, text.Length, null);

        List<DocRun> ParseRange(int start, int end, string? link)
        {
            var saved = new List<Segment>(segments);
            segments.Clear();
            var literal = new StringBuilder();
            void FlushLiteral()
            {
                if (literal.Length == 0) return;
                segments.Add(new Segment { Run = new DocRun(literal.ToString()) { Link = link } });
                literal.Clear();
            }

            int i = start;
            while (i < end)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < end && (char.IsAsciiLetterOrDigit(text[i + 1]) == false && text[i + 1] < 128 && !char.IsWhiteSpace(text[i + 1])))
                {
                    literal.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    int n = Count(i, end, '`');
                    int close = FindBackticks(i + n, end, n);
                    if (close < 0)
                    {
                        literal.Append('`', n);
                        i += n;
                        continue;
                    }
                    FlushLiteral();
                    var code = text[(i + n)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                    segments.Add(new Segment { Run = new DocRun(code) { Code = true, Link = link } });
                    i = close + n;
                    continue;
                }
                if (c == '<' && link == null && TryAutolink(i, end, out var url, out var after))
                {
                    FlushLiteral();
                    segments.Add(new Segment { Run = new DocRun(url) { Link = url.Contains('@') && !url.Contains("://") ? "mailto:" + url : url } });
                    i = after;
                    continue;
                }
                if ((c == '[' || (c == '!' && i + 1 < end && text[i + 1] == '[')) && link == null)
                {
                    int open = c == '!' ? i + 1 : i;
                    if (TryLink(open, end, out var labelEnd, out var target, out var next))
                    {
                        FlushLiteral();
                        var inner = ParseRange(open + 1, labelEnd, c == '!' ? null : target);
                        foreach (var r in inner) segments.Add(new Segment { Run = r });
                        i = next;
                        continue;
                    }
                }
                if (c == '&' && TryEntity(i, end, out var decoded, out var entityEnd))
                {
                    literal.Append(decoded);
                    i = entityEnd;
                    continue;
                }
                if (c is '*' or '_' or '~')
                {
                    int n = Count(i, end, c);
                    FlushLiteral();
                    char before = i > 0 ? text[i - 1] : '\n';
                    char afterChar = i + n < text.Length ? text[i + n] : '\n';
                    bool beforeSpace = char.IsWhiteSpace(before), afterSpace = char.IsWhiteSpace(afterChar);
                    bool beforePunct = IsPunct(before), afterPunct = IsPunct(afterChar);
                    bool left = !afterSpace && (!afterPunct || beforeSpace || beforePunct);
                    bool right = !beforeSpace && (!beforePunct || afterSpace || afterPunct);
                    var seg = new Segment { Delim = c, Count = n, Original = n, Active = true, Run = new DocRun("") { Link = link } };
                    if (c == '_')
                    {
                        seg.CanOpen = left && (!right || beforePunct);
                        seg.CanClose = right && (!left || afterPunct);
                    }
                    else
                    {
                        seg.CanOpen = left;
                        seg.CanClose = right;
                    }
                    if (c == '~' && n > 2) seg.CanOpen = seg.CanClose = false;
                    segments.Add(seg);
                    i += n;
                    continue;
                }
                literal.Append(c);
                i++;
            }
            FlushLiteral();
            ProcessEmphasis();
            var result = new List<DocRun>();
            foreach (var s in segments)
            {
                if (s.Delim != 0)
                {
                    if (s.Count > 0) result.Add(s.Run.With(new string(s.Delim, s.Count)));
                }
                else if (s.Run.Text.Length > 0) result.Add(s.Run);
            }
            segments.Clear();
            segments.AddRange(saved);
            return result;
        }

        void ProcessEmphasis()
        {
            for (int c = 0; c < segments.Count; c++)
            {
                var closer = segments[c];
                if (closer.Delim == 0 || !closer.Active || !closer.CanClose) continue;
                while (closer.Count > 0)
                {
                    int o = -1;
                    for (int k = c - 1; k >= 0; k--)
                    {
                        var s = segments[k];
                        if (s.Delim != closer.Delim || !s.Active || !s.CanOpen || s.Count == 0) continue;
                        if (closer.Delim == '~')
                        {
                            if (s.Count != closer.Count) continue;
                        }
                        else if ((s.CanClose || closer.CanOpen) && (s.Original + closer.Original) % 3 == 0 &&
                                 !(s.Original % 3 == 0 && closer.Original % 3 == 0)) continue;
                        o = k;
                        break;
                    }
                    if (o < 0) break;
                    var opener = segments[o];
                    int use = closer.Delim == '~' ? closer.Count : (opener.Count >= 2 && closer.Count >= 2 ? 2 : 1);
                    for (int k = o + 1; k < c; k++)
                    {
                        var run = segments[k].Run;
                        if (closer.Delim == '~') run.Strike = true;
                        else if (use == 2) run.Bold = true;
                        else run.Italic = true;
                        if (segments[k].Delim != 0) segments[k].Active = false;
                    }
                    opener.Count -= use;
                    closer.Count -= use;
                }
            }
        }

        int Count(int i, int end, char c)
        {
            int n = 0;
            while (i + n < end && text[i + n] == c) n++;
            return n;
        }

        int FindBackticks(int from, int end, int n)
        {
            int i = from;
            while (i < end)
            {
                if (text[i] != '`') { i++; continue; }
                int m = Count(i, end, '`');
                if (m == n) return i;
                i += m;
            }
            return -1;
        }

        static bool IsPunct(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

        bool TryAutolink(int i, int end, out string url, out int after)
        {
            url = "";
            after = i;
            int close = text.IndexOf('>', i + 1, end - i - 1);
            if (close < 0) return false;
            var inner = text[(i + 1)..close];
            if (inner.Length == 0 || inner.Any(ch => char.IsWhiteSpace(ch) || ch == '<')) return false;
            bool uri = System.Text.RegularExpressions.Regex.IsMatch(inner, @"^[A-Za-z][A-Za-z0-9+.\-]{1,31}:");
            bool email = System.Text.RegularExpressions.Regex.IsMatch(inner, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
            if (!uri && !email) return false;
            url = inner;
            after = close + 1;
            return true;
        }

        /// `[label](destination "title")` starting at '['.
        bool TryLink(int open, int end, out int labelEnd, out string target, out int next)
        {
            labelEnd = next = 0;
            target = "";
            int depth = 0, i = open;
            for (; i < end; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '`')
                {
                    int n = Count(i, end, '`');
                    int close = FindBackticks(i + n, end, n);
                    if (close >= 0) { i = close + n - 1; continue; }
                    i += n - 1;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) break;
            }
            if (i >= end || i + 1 >= end || text[i + 1] != '(') return false;
            labelEnd = i;
            int j = i + 2;
            while (j < end && text[j] is ' ' or '\t' or '\n') j++;
            var dest = new StringBuilder();
            if (j < end && text[j] == '<')
            {
                j++;
                while (j < end && text[j] != '>' && text[j] != '\n') dest.Append(text[j++]);
                if (j >= end || text[j] != '>') return false;
                j++;
            }
            else
            {
                int parens = 0;
                while (j < end && !char.IsWhiteSpace(text[j]))
                {
                    if (text[j] == '\\' && j + 1 < end) { dest.Append(text[j + 1]); j += 2; continue; }
                    if (text[j] == '(') parens++;
                    else if (text[j] == ')') { if (parens == 0) break; parens--; }
                    dest.Append(text[j++]);
                }
            }
            while (j < end && text[j] is ' ' or '\t' or '\n') j++;
            if (j < end && text[j] is '"' or '\'' or '(')
            {
                char closeQuote = text[j] == '(' ? ')' : text[j];
                j++;
                while (j < end && text[j] != closeQuote) { if (text[j] == '\\') j++; j++; }
                if (j >= end) return false;
                j++;
                while (j < end && text[j] is ' ' or '\t' or '\n') j++;
            }
            if (j >= end || text[j] != ')') return false;
            target = dest.ToString();
            next = j + 1;
            return true;
        }

        bool TryEntity(int i, int end, out string decoded, out int after)
        {
            decoded = "";
            after = i;
            int semi = text.IndexOf(';', i, Math.Min(end - i, 40));
            if (semi < 0) return false;
            var entity = text[i..(semi + 1)];
            if (!System.Text.RegularExpressions.Regex.IsMatch(entity, @"^&(#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});$")) return false;
            var value = System.Net.WebUtility.HtmlDecode(entity);
            if (value == entity) return false;
            decoded = value;
            after = semi + 1;
            return true;
        }
    }

    // ─────────────────────────────── HTML ───────────────────────────────

    public static DocModel ReadHtml(byte[] bytes)
    {
        Encoding? declared = null;
        if (DocEncoding.Bom(bytes) == null)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = System.Text.RegularExpressions.Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?([A-Za-z0-9_\-:.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) declared = DocEncoding.ByName(m.Groups[1].Value);
        }
        return new HtmlReader(DocEncoding.Decode(bytes, declared)).Read();
    }

    public static DocModel ReadHtml(string html) => new HtmlReader(html).Read();

    sealed class HtmlReader(string html)
    {
        static readonly HashSet<string> Void = ["br", "img", "hr", "meta", "link", "input", "col", "area", "base", "wbr", "source", "track", "embed", "param", "keygen"];
        static readonly HashSet<string> Raw = ["script", "style", "title", "textarea", "template", "noscript", "svg", "math", "iframe", "object", "noembed", "noframes", "select"];
        static readonly HashSet<string> Blocks = ["p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "pre", "blockquote", "center", "section",
            "article", "header", "footer", "main", "nav", "aside", "address", "figure", "figcaption", "dd", "dt", "dl", "form",
            "fieldset", "legend", "details", "summary", "body", "html", "ul", "ol", "table", "tr", "td", "th", "caption", "thead", "tbody", "tfoot", "listing", "xmp", "plaintext"];

        sealed class TableContext
        {
            public DocTable Table = new();
            public List<DocCell>? Row;
            public bool CellOpen;
        }

        readonly DocModel model = new();
        readonly List<List<DocBlock>> containers = [];
        readonly List<(string Tag, DocRun Style, bool Pre)> styles = [];
        readonly List<(string Tag, int Heading, DocAlign? Align)> blocks = [];
        readonly List<(bool Ordered, int Id, int Start)> lists = [];
        readonly List<TableContext> tables = [];
        DocParagraph? current;
        bool pendingListItem;
        int preDepth, nextListId;

        List<DocBlock> Container => containers.Count > 0 ? containers[^1] : model.Blocks;
        DocRun Style => styles.Count > 0 ? styles[^1].Style : new DocRun();
        bool InPre => preDepth > 0 || styles.Any(s => s.Pre);

        public DocModel Read()
        {
            int i = 0;
            var text = new StringBuilder();
            while (i < html.Length)
            {
                char c = html[i];
                if (c != '<') { text.Append(c); i++; continue; }
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    Text(text);
                    int close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = close < 0 ? html.Length : close + 3;
                    continue;
                }
                if (i + 1 < html.Length && html[i + 1] is '!' or '?')
                {
                    Text(text);
                    if (string.Compare(html, i, "<![CDATA[", 0, 9, StringComparison.Ordinal) == 0)
                    {
                        int close = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        var cdata = html[(i + 9)..(close < 0 ? html.Length : close)];
                        AddText(cdata);
                        i = close < 0 ? html.Length : close + 3;
                        continue;
                    }
                    int gt = html.IndexOf('>', i);
                    i = gt < 0 ? html.Length : gt + 1;
                    continue;
                }
                bool end = i + 1 < html.Length && html[i + 1] == '/';
                int nameStart = i + (end ? 2 : 1);
                int j = nameStart;
                while (j < html.Length && (char.IsAsciiLetterOrDigit(html[j]) || html[j] is ':' or '-')) j++;
                if (j == nameStart || !char.IsAsciiLetter(html[nameStart]))
                {
                    text.Append(c);
                    i++;
                    continue;
                }
                Text(text);
                var name = html[nameStart..j].ToLowerInvariant();
                var attributes = ParseAttributes(ref j, out bool selfClosing);
                i = j;
                if (end) EndTag(name);
                else
                {
                    if (Raw.Contains(name) && !selfClosing)
                    {
                        int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                        if (close < 0) close = html.Length;
                        int gt = html.IndexOf('>', close);
                        i = gt < 0 ? html.Length : gt + 1;
                        continue;
                    }
                    StartTag(name, attributes);
                    if (selfClosing && !Void.Contains(name)) EndTag(name);
                }
            }
            Text(text);
            Flush();
            while (tables.Count > 0) CloseTable();
            foreach (var p in model.AllParagraphs)
                for (int k = 0; k < p.Runs.Count; k++)
                    if (p.Runs[k].Text.Contains('\u00A0')) p.Runs[k] = p.Runs[k].With(p.Runs[k].Text.Replace('\u00A0', ' '));
            model.Normalize();
            if (model.Blocks.Count == 0) model.Blocks.Add(new DocParagraph());
            return model;
        }

        Dictionary<string, string> ParseAttributes(ref int j, out bool selfClosing)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            selfClosing = false;
            while (j < html.Length)
            {
                while (j < html.Length && char.IsWhiteSpace(html[j])) j++;
                if (j >= html.Length) break;
                if (html[j] == '>') { j++; break; }
                if (html[j] == '/' && j + 1 < html.Length && html[j + 1] == '>') { selfClosing = true; j += 2; break; }
                int ns = j;
                while (j < html.Length && !char.IsWhiteSpace(html[j]) && html[j] is not ('=' or '>') && !(html[j] == '/' && j + 1 < html.Length && html[j + 1] == '>')) j++;
                var name = html[ns..j];
                if (name.Length == 0) { j++; continue; }
                while (j < html.Length && char.IsWhiteSpace(html[j])) j++;
                string value = "";
                if (j < html.Length && html[j] == '=')
                {
                    j++;
                    while (j < html.Length && char.IsWhiteSpace(html[j])) j++;
                    if (j < html.Length && html[j] is '"' or '\'')
                    {
                        char q = html[j++];
                        int vs = j;
                        while (j < html.Length && html[j] != q) j++;
                        value = html[vs..Math.Min(j, html.Length)];
                        j++;
                    }
                    else
                    {
                        int vs = j;
                        while (j < html.Length && !char.IsWhiteSpace(html[j]) && html[j] != '>') j++;
                        value = html[vs..j];
                    }
                }
                result[name] = System.Net.WebUtility.HtmlDecode(value);
            }
            return result;
        }

        void Text(StringBuilder text)
        {
            if (text.Length == 0) return;
            AddText(System.Net.WebUtility.HtmlDecode(text.ToString()));
            text.Clear();
        }

        void AddText(string s)
        {
            if (!InPre)
            {
                var sb = new StringBuilder(s.Length);
                bool space = false;
                foreach (var ch in s)
                {
                    if (ch is ' ' or '\t' or '\n' or '\r' or '\f')
                    {
                        if (!space) sb.Append(' ');
                        space = true;
                    }
                    else
                    {
                        // Non-breaking spaces stay until the end of reading, so trimming never removes them.
                        sb.Append(ch);
                        space = false;
                    }
                }
                s = sb.ToString();
                if (current == null || current.Runs.Count == 0 || EndsWithSpace(current))
                    s = s.TrimStart(' ');
                if (s.Length == 0) return;
                // Whitespace between table cells or list items is not content.
                if (current == null && s.Trim().Length == 0) return;
            }
            else
            {
                s = s.Replace("\r\n", "\n").Replace('\r', '\n');
                // A newline right after <pre> is not part of the content.
                if (current == null && s.StartsWith('\n')) s = s[1..];
                if (s.Length == 0) return;
            }
            Paragraph().Runs.Add(Style.With(s));
        }

        static bool EndsWithSpace(DocParagraph p)
        {
            var last = p.Runs.LastOrDefault(r => r.Text.Length > 0);
            return last == null || last.Text[^1] is ' ' or '\n';
        }

        DocParagraph Paragraph()
        {
            if (current != null) return current;
            current = new DocParagraph { Pre = preDepth > 0 };
            for (int k = blocks.Count - 1; k >= 0; k--)
                if (blocks[k].Heading > 0) { current.Heading = blocks[k].Heading; break; }
            for (int k = blocks.Count - 1; k >= 0; k--)
                if (blocks[k].Align is { } align) { current.Align = align; break; }
            if (pendingListItem && lists.Count > 0)
            {
                var top = lists[^1];
                current.List = new DocListInfo(top.Id, top.Ordered, lists.Count - 1, top.Start);
                pendingListItem = false;
            }
            Container.Add(current);
            return current;
        }

        void Flush()
        {
            if (current == null) return;
            // A <br> at the end of a block doesn't start another line.
            for (int k = current.Runs.Count - 1; k >= 0; k--)
            {
                if (current.Runs[k].Text.Length == 0) continue;
                if (current.Runs[k].Text.EndsWith('\n')) current.Runs[k] = current.Runs[k].With(current.Runs[k].Text[..^1]);
                break;
            }
            // Trailing collapsed whitespace isn't content.
            if (!current.Pre)
                for (int k = current.Runs.Count - 1; k >= 0; k--)
                {
                    var trimmed = current.Runs[k].Text.TrimEnd(' ');
                    if (trimmed.Length == current.Runs[k].Text.Length) break;
                    current.Runs[k] = current.Runs[k].With(trimmed);
                    if (trimmed.Length > 0) break;
                }
            current = null;
        }

        void StartTag(string name, Dictionary<string, string> attributes)
        {
            if (name == "br")
            {
                var p = Paragraph();
                p.Runs.Add(Style.With("\n"));
                return;
            }
            if (name is "img")
            {
                if (attributes.TryGetValue("alt", out var alt) && alt.Trim().Length > 0) AddText(alt);
                return;
            }
            if (name == "hr") { Flush(); return; }
            if (Void.Contains(name)) return;

            bool block = Blocks.Contains(name);
            if (block)
            {
                // An open <p> ends at the next block; an open <li> ends at the next <li>.
                if (blocks.Count > 0 && blocks[^1].Tag == "p" && name != "p" || name == "p" && blocks.Count > 0 && blocks[^1].Tag == "p") PopBlock("p");
                if (name == "li" && blocks.Count > 0 && blocks.FindLastIndex(b => b.Tag == "li") is int li and >= 0 &&
                    blocks.FindLastIndex(b => b.Tag is "ul" or "ol") < li) PopBlock("li");
                Flush();
            }

            var style = Style.With("");
            ApplyInline(name, attributes, style);
            // white-space: pre (and pre-wrap) keeps spaces, tabs and newlines inside this element.
            bool whitespacePre = attributes.TryGetValue("style", out var styleCss) && Css(styleCss).TryGetValue("white-space", out var ws) &&
                                 (ws.StartsWith("pre") || ws == "break-spaces");
            styles.Add((name, style, whitespacePre));

            if (!block) return;
            int heading = name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6' ? name[1] - '0' : 0;
            DocAlign? align = name == "center" ? DocAlign.Center : null;
            if (attributes.TryGetValue("align", out var a)) align = ParseAlign(a) ?? align;
            if (attributes.TryGetValue("style", out var css) && Css(css).TryGetValue("text-align", out var ta)) align = ParseAlign(ta) ?? align;
            blocks.Add((name, heading, align));

            switch (name)
            {
                case "pre" or "listing" or "xmp" or "plaintext":
                    preDepth++;
                    break;
                case "ul" or "ol":
                    int start = 1;
                    if (name == "ol" && attributes.TryGetValue("start", out var s) && int.TryParse(s, out var n)) start = n;
                    lists.Add((name == "ol", lists.Count == 0 ? ++nextListId : lists[^1].Id, start));
                    break;
                case "li":
                    pendingListItem = true;
                    break;
                case "table":
                    var context = new TableContext();
                    Container.Add(context.Table);
                    tables.Add(context);
                    break;
                case "tr":
                    if (tables.Count > 0)
                    {
                        CloseCell();
                        tables[^1].Row = [];
                        tables[^1].Table.Rows.Add(tables[^1].Row!);
                    }
                    break;
                case "td" or "th":
                    if (tables.Count > 0)
                    {
                        var t = tables[^1];
                        CloseCell();
                        if (t.Row == null) { t.Row = []; t.Table.Rows.Add(t.Row); }
                        var cell = new DocCell { Header = name == "th" };
                        if (attributes.TryGetValue("colspan", out var span) && int.TryParse(span, out var cs)) cell.ColSpan = Math.Clamp(cs, 1, 100);
                        t.Row.Add(cell);
                        containers.Add(cell.Blocks);
                        t.CellOpen = true;
                    }
                    break;
            }
        }

        void EndTag(string name)
        {
            if (name == "br") { StartTag("br", []); return; }
            if (name == "p" && !blocks.Any(b => b.Tag == "p"))
            {
                // A stray </p> is an empty paragraph in browsers; here it just ends the line.
                Flush();
                return;
            }
            if (Blocks.Contains(name))
            {
                if (blocks.Any(b => b.Tag == name)) PopBlock(name);
                return;
            }
            int index = styles.FindLastIndex(s => s.Tag == name);
            if (index >= 0 && !styles.Skip(index + 1).Any(s => Blocks.Contains(s.Tag))) styles.RemoveRange(index, styles.Count - index);
        }

        void PopBlock(string name)
        {
            Flush();
            while (blocks.Count > 0)
            {
                var (tag, _, _) = blocks[^1];
                blocks.RemoveAt(blocks.Count - 1);
                int si = styles.FindLastIndex(s => s.Tag == tag);
                if (si >= 0) styles.RemoveRange(si, styles.Count - si);
                switch (tag)
                {
                    case "pre" or "listing" or "xmp" or "plaintext": preDepth = Math.Max(0, preDepth - 1); break;
                    case "ul" or "ol": if (lists.Count > 0) lists.RemoveAt(lists.Count - 1); break;
                    case "li": pendingListItem = false; break;
                    case "td" or "th": CloseCell(); break;
                    case "tr": CloseCell(); if (tables.Count > 0) tables[^1].Row = null; break;
                    case "table": CloseTable(); break;
                }
                if (tag == name) break;
            }
        }

        void CloseCell()
        {
            if (tables.Count == 0 || !tables[^1].CellOpen) return;
            Flush();
            if (containers.Count > 0) containers.RemoveAt(containers.Count - 1);
            tables[^1].CellOpen = false;
        }

        void CloseTable()
        {
            CloseCell();
            if (tables.Count > 0)
            {
                var t = tables[^1];
                tables.RemoveAt(tables.Count - 1);
                // Rows can't be empty in the writers; give every row at least one cell.
                t.Table.Rows.RemoveAll(r => r.Count == 0);
                if (t.Table.Rows.Count == 0) Container.Remove(t.Table);
            }
        }

        static void ApplyInline(string name, Dictionary<string, string> attributes, DocRun style)
        {
            switch (name)
            {
                case "b" or "strong" or "th": style.Bold = true; break;
                case "i" or "em" or "cite" or "dfn" or "var" or "address": style.Italic = true; break;
                case "u" or "ins": style.Underline = true; break;
                case "s" or "strike" or "del": style.Strike = true; break;
                case "code" or "kbd" or "samp" or "tt" or "pre" or "listing" or "xmp" or "plaintext": style.Code = true; break;
                case "a":
                    if (attributes.TryGetValue("href", out var href) && href.Trim().Length > 0 && !href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        style.Link = href.Trim();
                    break;
                case "font":
                    if (attributes.TryGetValue("face", out var face)) style.Font = face.Split(',')[0].Trim().Trim('"', '\'');
                    if (attributes.TryGetValue("color", out var color) && ParseColor(color) is { } hex) style.Color = hex;
                    if (attributes.TryGetValue("size", out var size) && int.TryParse(size.TrimStart('+'), out var sz))
                    {
                        if (size.StartsWith('+') || size.StartsWith('-')) sz = 3 + (size.StartsWith('-') ? -Math.Abs(sz) : sz);
                        double[] points = [7.5, 10, 12, 13.5, 18, 24, 36];
                        style.Size = points[Math.Clamp(sz, 1, 7) - 1];
                    }
                    break;
            }
            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") style.Bold = true;
            if (attributes.TryGetValue("style", out var css))
            {
                foreach (var (key, value) in Css(css))
                {
                    var v = value.ToLowerInvariant();
                    switch (key)
                    {
                        case "font-weight":
                            style.Bold = v is "bold" or "bolder" || int.TryParse(v, out var w) && w >= 600;
                            break;
                        case "font-style":
                            style.Italic = v is "italic" or "oblique";
                            break;
                        case "text-decoration" or "text-decoration-line":
                            if (v.Contains("underline")) style.Underline = true;
                            if (v.Contains("line-through")) style.Strike = true;
                            if (v == "none") style.Underline = style.Strike = false;
                            break;
                        case "font-family":
                            var family = value.Split(',')[0].Trim().Trim('"', '\'');
                            if (family.Length > 0 && family is not ("serif" or "sans-serif" or "monospace" or "inherit" or "initial")) style.Font = family;
                            if (family == "monospace") style.Code = true;
                            break;
                        case "font-size":
                            if (ParseLength(v) is { } pt) style.Size = pt;
                            break;
                        case "color":
                            if (ParseColor(v) is { } c) style.Color = c;
                            break;
                    }
                }
            }
        }

        static Dictionary<string, string> Css(string css)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in css.Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                result[part[..colon].Trim().ToLowerInvariant()] = part[(colon + 1)..].Replace("!important", "").Trim();
            }
            return result;
        }

        static DocAlign? ParseAlign(string value) => value.Trim().ToLowerInvariant() switch
        {
            "center" or "middle" => DocAlign.Center,
            "right" or "end" => DocAlign.Right,
            "justify" => DocAlign.Justify,
            "left" or "start" => DocAlign.Left,
            _ => null,
        };

        static double? ParseLength(string v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^([0-9.]+)\s*(pt|px|em|rem|%)?$");
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            return m.Groups[2].Value switch
            {
                "px" or "" => n * 0.75,
                "em" or "rem" => n * 12,
                "%" => n / 100 * 12,
                _ => n,
            };
        }
    }

    /// "#rgb", "#rrggbb", "rgb(r, g, b)" or a few colour names → "RRGGBB".
    public static string? ParseColor(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length >= 6 && hex[..6].All(Uri.IsHexDigit)) return hex[..6].ToUpperInvariant();
            return null;
        }
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        if (m.Success)
            return string.Concat(Enumerable.Range(1, 3).Select(k => Math.Clamp(int.Parse(m.Groups[k].Value), 0, 255).ToString("X2")));
        return v switch
        {
            "red" => "FF0000", "green" => "008000", "blue" => "0000FF", "gray" or "grey" => "808080", "white" => "FFFFFF",
            "orange" => "FFA500", "purple" => "800080", "navy" => "000080", "maroon" => "800000", "teal" => "008080",
            _ => null,
        };
    }

    // ─────────────────────────────── DOCX ───────────────────────────────

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    static XDocument? LoadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    sealed class DocxStyle
    {
        public string? BasedOn;
        public XElement? RunProperties;
        public XElement? ParagraphProperties;
        public string Name = "";
    }

    sealed class DocxReader
    {
        readonly Dictionary<string, DocxStyle> styles = [];
        readonly Dictionary<string, string> links = [];
        readonly Dictionary<string, string> numToAbstract = [];
        readonly Dictionary<(string, int), (bool Ordered, int Start)> levels = [];
        readonly Dictionary<(string, int), int> startOverrides = [];
        readonly Dictionary<string, int> listIds = [];
        double defaultSize = 11;
        string? defaultFont;
        string? defaultParagraphStyle;
        readonly DocModel model = new();

        public DocModel Read(ZipArchive zip)
        {
            string main = "word/document.xml";
            if (LoadXml(zip, "_rels/.rels") is { } rootRels)
            {
                var target = rootRels.Root?.Elements(PR + "Relationship")
                    .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument") == true)?.Attribute("Target")?.Value;
                if (target != null) main = target.TrimStart('/');
            }
            var document = LoadXml(zip, main) ?? throw new ToolError(L("Couldn't read this document"));
            var folder = main.Contains('/') ? main[..main.LastIndexOf('/')] : "";
            var relsName = (folder.Length > 0 ? folder + "/" : "") + "_rels/" + Path.GetFileName(main) + ".rels";
            if (LoadXml(zip, relsName) is { } rels)
                foreach (var r in rels.Root?.Elements(PR + "Relationship") ?? [])
                    if ((string?)r.Attribute("Id") is { } id && (string?)r.Attribute("Target") is { } target) links[id] = target;
            if (LoadXml(zip, (folder.Length > 0 ? folder + "/" : "") + "styles.xml") is { } stylesXml) ReadStyles(stylesXml);
            if (LoadXml(zip, (folder.Length > 0 ? folder + "/" : "") + "numbering.xml") is { } numbering) ReadNumbering(numbering);

            var body = document.Root?.Element(W + "body") ?? throw new ToolError(L("Couldn't read this document"));
            ReadBlocks(body, model.Blocks);
            model.Normalize();
            if (model.Blocks.Count == 0) model.Blocks.Add(new DocParagraph());
            return model;
        }

        void ReadStyles(XDocument xml)
        {
            var defaults = xml.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
            if (defaults?.Element(W + "sz")?.Attribute(W + "val")?.Value is { } sz && double.TryParse(sz, CultureInfo.InvariantCulture, out var half)) defaultSize = half / 2;
            defaultFont = defaults?.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value;
            foreach (var s in xml.Root?.Elements(W + "style") ?? [])
            {
                var id = (string?)s.Attribute(W + "styleId");
                if (id == null) continue;
                styles[id] = new DocxStyle
                {
                    BasedOn = s.Element(W + "basedOn")?.Attribute(W + "val")?.Value,
                    RunProperties = s.Element(W + "rPr"),
                    ParagraphProperties = s.Element(W + "pPr"),
                    Name = s.Element(W + "name")?.Attribute(W + "val")?.Value ?? id,
                };
                if ((string?)s.Attribute(W + "type") == "paragraph" && IsOn(s.Attribute(W + "default"))) defaultParagraphStyle = id;
            }
        }

        static bool IsOn(XAttribute? a) => a != null && a.Value is "1" or "true" or "on";

        void ReadNumbering(XDocument xml)
        {
            foreach (var a in xml.Root?.Elements(W + "abstractNum") ?? [])
            {
                var id = (string?)a.Attribute(W + "abstractNumId") ?? "";
                foreach (var lvl in a.Elements(W + "lvl"))
                {
                    int ilvl = int.TryParse((string?)lvl.Attribute(W + "ilvl"), out var l) ? l : 0;
                    var fmt = lvl.Element(W + "numFmt")?.Attribute(W + "val")?.Value ?? "bullet";
                    int start = int.TryParse(lvl.Element(W + "start")?.Attribute(W + "val")?.Value, out var st) ? st : 1;
                    levels[(id, ilvl)] = (fmt is not ("bullet" or "none"), start);
                }
            }
            foreach (var n in xml.Root?.Elements(W + "num") ?? [])
            {
                var id = (string?)n.Attribute(W + "numId") ?? "";
                numToAbstract[id] = n.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value ?? "";
                foreach (var o in n.Elements(W + "lvlOverride"))
                    if (int.TryParse((string?)o.Attribute(W + "ilvl"), out var l) &&
                        int.TryParse(o.Element(W + "startOverride")?.Attribute(W + "val")?.Value, out var st))
                        startOverrides[(id, l)] = st;
            }
        }

        IEnumerable<DocxStyle> Chain(string? id)
        {
            var seen = new HashSet<string>();
            while (id != null && seen.Add(id) && styles.TryGetValue(id, out var s))
            {
                yield return s;
                id = s.BasedOn;
            }
        }

        void ReadBlocks(XElement parent, List<DocBlock> into)
        {
            foreach (var e in parent.Elements())
            {
                if (e.Name == W + "p") into.Add(ReadParagraph(e));
                else if (e.Name == W + "tbl") into.Add(ReadTable(e));
                else if (e.Name == W + "sdt") { if (e.Element(W + "sdtContent") is { } c) ReadBlocks(c, into); }
                else if (e.Name == W + "customXml" || e.Name == W + "ins" || e.Name == W + "moveTo") ReadBlocks(e, into);
                else if (e.Name == MC + "AlternateContent")
                {
                    var choice = e.Element(MC + "Choice") ?? e.Element(MC + "Fallback");
                    if (choice != null) ReadBlocks(choice, into);
                }
            }
        }

        DocTable ReadTable(XElement tbl)
        {
            var table = new DocTable();
            foreach (var tr in tbl.Elements(W + "tr"))
            {
                var row = new List<DocCell>();
                foreach (var tc in tr.Elements().SelectMany(e => e.Name == W + "sdt" ? e.Element(W + "sdtContent")?.Elements() ?? [] : [e]).Where(e => e.Name == W + "tc"))
                {
                    var props = tc.Element(W + "tcPr");
                    // Vertically merged continuation cells stay as empty cells so columns line up.
                    var cell = new DocCell();
                    if (int.TryParse(props?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value, out var span)) cell.ColSpan = Math.Max(1, span);
                    ReadBlocks(tc, cell.Blocks);
                    row.Add(cell);
                }
                if (row.Count > 0) table.Rows.Add(row);
            }
            return table;
        }

        DocParagraph ReadParagraph(XElement p)
        {
            var paragraph = new DocParagraph();
            var pPr = p.Element(W + "pPr");
            var styleId = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? defaultParagraphStyle;
            var chain = Chain(styleId).ToList();

            int? outline = null;
            foreach (var props in new[] { pPr }.Concat(chain.Select(s => s.ParagraphProperties)))
                if (props?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value is { } o && int.TryParse(o, out var ol)) { outline = ol; break; }
            if (outline is >= 0 and < 6) paragraph.Heading = outline.Value + 1;
            else
                foreach (var s in chain)
                {
                    var name = s.Name.ToLowerInvariant();
                    if (name.StartsWith("heading ") && int.TryParse(name[8..], out var h) && h is >= 1 and <= 6) { paragraph.Heading = h; break; }
                    if (name == "title") { paragraph.Heading = 1; break; }
                }

            var jc = pPr?.Element(W + "jc")?.Attribute(W + "val")?.Value ?? chain.Select(s => s.ParagraphProperties?.Element(W + "jc")?.Attribute(W + "val")?.Value).FirstOrDefault(v => v != null);
            paragraph.Align = jc switch { "center" => DocAlign.Center, "right" or "end" => DocAlign.Right, "both" or "distribute" => DocAlign.Justify, _ => DocAlign.Left };

            var numPr = pPr?.Element(W + "numPr") ?? chain.Select(s => s.ParagraphProperties?.Element(W + "numPr")).FirstOrDefault(n => n != null);
            if (numPr != null)
            {
                var numId = numPr.Element(W + "numId")?.Attribute(W + "val")?.Value;
                int ilvl = int.TryParse(numPr.Element(W + "ilvl")?.Attribute(W + "val")?.Value, out var l) ? l : 0;
                if (numId != null && numId != "0")
                {
                    var abs = numToAbstract.GetValueOrDefault(numId, "");
                    var (ordered, start) = levels.GetValueOrDefault((abs, ilvl), (false, 1));
                    if (startOverrides.TryGetValue((numId, ilvl), out var so)) start = so;
                    if (!listIds.TryGetValue(numId, out var listId)) listIds[numId] = listId = listIds.Count + 1;
                    paragraph.List = new DocListInfo(listId, ordered, Math.Clamp(ilvl, 0, 8), start);
                }
            }

            // Paragraph-style character formatting (not for headings: their look comes from the heading level).
            var baseStyle = new DocRun();
            if (paragraph.Heading == 0)
                foreach (var s in Enumerable.Reverse(chain))
                    ApplyRunProperties(s.RunProperties, baseStyle);
            var fields = new List<(string Instr, bool Result)>();
            ReadInline(p, paragraph, baseStyle, null, fields);
            return paragraph;
        }

        void ReadInline(XElement parent, DocParagraph paragraph, DocRun baseStyle, string? link, List<(string Instr, bool Result)> fields)
        {
            foreach (var e in parent.Elements())
            {
                var n = e.Name;
                if (n == W + "r") ReadRun(e, paragraph, baseStyle, link, fields);
                else if (n == W + "hyperlink")
                {
                    string? target = null;
                    if (e.Attribute(R + "id")?.Value is { } id && links.TryGetValue(id, out var t)) target = t;
                    else if (e.Attribute(W + "anchor")?.Value is { } anchor) target = "#" + anchor;
                    ReadInline(e, paragraph, baseStyle, target ?? link, fields);
                }
                else if (n == W + "ins" || n == W + "smartTag" || n == W + "customXml" || n == W + "fldSimple" || n == W + "moveTo" || n == W + "dir" || n == W + "bdo")
                {
                    string? fieldLink = link;
                    if (n == W + "fldSimple" && HyperlinkTarget((string?)e.Attribute(W + "instr")) is { } fl) fieldLink = fl;
                    ReadInline(e, paragraph, baseStyle, fieldLink, fields);
                }
                else if (n == W + "sdt") { if (e.Element(W + "sdtContent") is { } c) ReadInline(c, paragraph, baseStyle, link, fields); }
                else if (n == MC + "AlternateContent")
                {
                    var choice = e.Element(MC + "Choice") ?? e.Element(MC + "Fallback");
                    if (choice != null) ReadInline(choice, paragraph, baseStyle, link, fields);
                }
            }
        }

        static string? HyperlinkTarget(string? instr)
        {
            if (instr == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(instr, @"^\s*HYPERLINK\s+(?:\\l\s+)?""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        void ReadRun(XElement r, DocParagraph paragraph, DocRun baseStyle, string? link, List<(string Instr, bool Result)> fields)
        {
            var style = baseStyle.With("");
            var rPr = r.Element(W + "rPr");
            if (rPr?.Element(W + "rStyle")?.Attribute(W + "val")?.Value is { } rStyle)
                foreach (var s in Chain(rStyle).Reverse()) ApplyRunProperties(s.RunProperties, style);
            ApplyRunProperties(rPr, style);
            if (rPr?.Element(W + "vanish") is { } vanish && (vanish.Attribute(W + "val") == null || IsOn(vanish.Attribute(W + "val")))) return;
            string? fieldLink = fields.LastOrDefault(f => f.Result && HyperlinkTarget(f.Instr) != null).Instr is { } instr ? HyperlinkTarget(instr) : null;
            style.Link = link ?? fieldLink;
            if (style.Link != null && style.Color == "0563C1") style.Color = null;

            var text = new StringBuilder();
            foreach (var c in r.Elements())
            {
                var n = c.Name;
                if (n == W + "fldChar")
                {
                    switch ((string?)c.Attribute(W + "fldCharType"))
                    {
                        case "begin": fields.Add(("", false)); break;
                        case "separate": if (fields.Count > 0) fields[^1] = (fields[^1].Instr, true); break;
                        case "end": if (fields.Count > 0) fields.RemoveAt(fields.Count - 1); break;
                    }
                    continue;
                }
                if (n == W + "instrText")
                {
                    if (fields.Count > 0) fields[^1] = (fields[^1].Instr + c.Value, fields[^1].Result);
                    continue;
                }
                // Inside a field's instructions nothing is shown.
                if (fields.Count > 0 && !fields[^1].Result) continue;
                if (n == W + "t") text.Append(c.Value);
                else if (n == W + "tab" || n == W + "ptab") text.Append('\t');
                else if (n == W + "br" || n == W + "cr") text.Append('\n');
                else if (n == W + "noBreakHyphen") text.Append('‑');
                else if (n == W + "sym" && c.Attribute(W + "char")?.Value is { } hex && int.TryParse(hex, NumberStyles.HexNumber, null, out var code))
                {
                    // Symbol-font characters live in the private-use area F0xx; map the common ones back.
                    if (code >= 0xF000) code -= 0xF000;
                    if (code >= 32) text.Append((char)code);
                }
            }
            if (text.Length > 0) paragraph.Runs.Add(style.With(text.ToString()));
        }

        void ApplyRunProperties(XElement? rPr, DocRun style)
        {
            if (rPr == null) return;
            static bool Toggle(XElement e)
            {
                var v = (string?)e.Attribute(W + "val");
                return v == null || v is "1" or "true" or "on";
            }
            if (rPr.Element(W + "b") is { } b) style.Bold = Toggle(b);
            if (rPr.Element(W + "i") is { } i) style.Italic = Toggle(i);
            if (rPr.Element(W + "u") is { } u) style.Underline = (string?)u.Attribute(W + "val") is not ("none" or null) || u.Attribute(W + "val") == null;
            if (rPr.Element(W + "strike") is { } s) style.Strike = Toggle(s);
            if (rPr.Element(W + "dstrike") is { } ds && Toggle(ds)) style.Strike = true;
            if (rPr.Element(W + "sz")?.Attribute(W + "val")?.Value is { } sz && double.TryParse(sz, CultureInfo.InvariantCulture, out var half))
                style.Size = Math.Abs(half / 2 - defaultSize) < 0.01 ? null : half / 2;
            if (rPr.Element(W + "rFonts") is { } fonts)
            {
                var font = (string?)fonts.Attribute(W + "ascii") ?? (string?)fonts.Attribute(W + "hAnsi") ?? (string?)fonts.Attribute(W + "eastAsia");
                if (font != null) style.Font = font == defaultFont ? null : font;
                if (font is "Consolas" or "Courier New" or "Courier") style.Code = true;
            }
            if (rPr.Element(W + "color")?.Attribute(W + "val")?.Value is { } color)
                style.Color = color.Length == 6 && color.All(Uri.IsHexDigit) && color != "000000" ? color.ToUpperInvariant() : null;
        }
    }

    public static DocModel ReadDocx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return new DocxReader().Read(zip);
        }
        catch (InvalidDataException) { throw new ToolError(L("Couldn't read this document")); }
        catch (System.Xml.XmlException) { throw new ToolError(L("Couldn't read this document")); }
    }

    // ─────────────────────────────── ODT ───────────────────────────────

    static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    sealed class OdtReader
    {
        sealed class OdtStyle
        {
            public string? Parent;
            public XElement? Text, Paragraph;
            public int Outline;
        }

        readonly Dictionary<string, OdtStyle> textStyles = [], paragraphStyles = [];
        readonly Dictionary<string, Dictionary<int, (bool Ordered, int Start)>> listStyles = [];
        readonly Dictionary<string, string> fontFaces = [];
        readonly DocModel model = new();
        int nextListId;
        bool trailingLiteralSpace;

        public DocModel Read(ZipArchive zip)
        {
            var content = LoadXml(zip, "content.xml") ?? throw new ToolError(L("Couldn't read this document"));
            if (LoadXml(zip, "styles.xml") is { } stylesXml) ReadStyles(stylesXml.Root);
            ReadStyles(content.Root);
            var text = content.Root?.Element(Office + "body")?.Element(Office + "text") ?? throw new ToolError(L("Couldn't read this document"));
            ReadBlocks(text, model.Blocks, null, 0, null);
            model.Normalize();
            if (model.Blocks.Count == 0) model.Blocks.Add(new DocParagraph());
            return model;
        }

        void ReadStyles(XElement? root)
        {
            if (root == null) return;
            foreach (var face in root.Descendants(Style + "font-face"))
                if ((string?)face.Attribute(Style + "name") is { } name)
                    fontFaces[name] = ((string?)face.Attribute(XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0") + "font-family") ?? name).Trim('\'', '"');
            foreach (var container in new[] { root.Element(Office + "styles"), root.Element(Office + "automatic-styles") })
            {
                if (container == null) continue;
                foreach (var s in container.Elements(Style + "style"))
                {
                    var name = (string?)s.Attribute(Style + "name");
                    if (name == null) continue;
                    var style = new OdtStyle
                    {
                        Parent = (string?)s.Attribute(Style + "parent-style-name"),
                        Text = s.Element(Style + "text-properties"),
                        Paragraph = s.Element(Style + "paragraph-properties"),
                        Outline = int.TryParse((string?)s.Attribute(Style + "default-outline-level"), out var o) ? o : 0,
                    };
                    var family = (string?)s.Attribute(Style + "family");
                    if (family == "text") textStyles[name] = style;
                    else if (family == "paragraph") paragraphStyles[name] = style;
                }
                foreach (var ls in container.Elements(Text + "list-style"))
                {
                    var name = (string?)ls.Attribute(Style + "name");
                    if (name == null) continue;
                    var map = new Dictionary<int, (bool, int)>();
                    foreach (var level in ls.Elements())
                    {
                        int l = int.TryParse((string?)level.Attribute(Text + "level"), out var lv) ? lv : 1;
                        bool ordered = level.Name == Text + "list-level-style-number" && ((string?)level.Attribute(Style + "num-format") ?? "1") != "";
                        int start = int.TryParse((string?)level.Attribute(Text + "start-value"), out var sv) ? sv : 1;
                        map[l - 1] = (ordered, start);
                    }
                    listStyles[name] = map;
                }
            }
        }

        IEnumerable<OdtStyle> Chain(Dictionary<string, OdtStyle> styles, string? name)
        {
            var seen = new HashSet<string>();
            while (name != null && seen.Add(name) && styles.TryGetValue(name, out var s))
            {
                yield return s;
                name = s.Parent;
            }
        }

        void ApplyTextProperties(XElement? props, DocRun run)
        {
            if (props == null) return;
            if ((string?)props.Attribute(Fo + "font-weight") is { } w) run.Bold = w == "bold" || int.TryParse(w, out var n) && n >= 600;
            if ((string?)props.Attribute(Fo + "font-style") is { } st) run.Italic = st is "italic" or "oblique";
            if ((string?)props.Attribute(Style + "text-underline-style") is { } u) run.Underline = u != "none";
            if ((string?)props.Attribute(Style + "text-line-through-style") is { } lt) run.Strike = lt != "none";
            if ((string?)props.Attribute(Fo + "font-size") is { } size && size.EndsWith("pt") &&
                double.TryParse(size[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt)) run.Size = Math.Abs(pt - 12) < 0.01 ? null : pt;
            if ((string?)props.Attribute(Style + "font-name") is { } font) run.Font = fontFaces.GetValueOrDefault(font, font);
            if ((string?)props.Attribute(Fo + "color") is { } color && ParseColor(color) is { } hex) run.Color = hex == "000000" ? null : hex;
            if (run.Font is "Consolas" or "Courier New" or "Liberation Mono" or "DejaVu Sans Mono") run.Code = true;
        }

        void ReadBlocks(XElement parent, List<DocBlock> into, string? listStyle, int listDepth, int? listId)
        {
            foreach (var e in parent.Elements())
            {
                if (e.Name == Text + "p" || e.Name == Text + "h")
                    into.Add(ReadParagraph(e, null));
                else if (e.Name == Text + "list")
                {
                    var style = (string?)e.Attribute(Text + "style-name") ?? listStyle;
                    int id = listId ?? ++nextListId;
                    foreach (var item in e.Elements().Where(x => x.Name == Text + "list-item" || x.Name == Text + "list-header"))
                    {
                        bool first = item.Name == Text + "list-item";
                        foreach (var child in item.Elements())
                        {
                            if (child.Name == Text + "p" || child.Name == Text + "h")
                            {
                                DocListInfo? info = null;
                                if (first)
                                {
                                    var (ordered, start) = style != null && listStyles.TryGetValue(style, out var map) && map.TryGetValue(listDepth, out var lv) ? lv : (false, 1);
                                    info = new DocListInfo(id, ordered, Math.Min(listDepth, 8), start);
                                    first = false;
                                }
                                into.Add(ReadParagraph(child, info));
                            }
                            else if (child.Name == Text + "list") ReadBlocks(new XElement("wrap", child), into, style, listDepth + 1, id);
                            else ReadBlocks(new XElement("wrap", child), into, style, listDepth, id);
                        }
                    }
                }
                else if (e.Name == Table + "table") into.Add(ReadTable(e));
                else if (e.Name == Text + "section" || e.Name == Text + "index-body" || e.Name == Text + "table-of-content" ||
                         e.Name == Text + "alphabetical-index" || e.Name == Text + "illustration-index" || e.Name == Text + "user-index" ||
                         e.Name == Text + "bibliography" || e.Name == Office + "text")
                    ReadBlocks(e, into, listStyle, listDepth, listId);
            }
        }

        DocTable ReadTable(XElement t)
        {
            var table = new DocTable();
            IEnumerable<XElement> Rows(XElement x) => x.Elements().SelectMany(e =>
                e.Name == Table + "table-row" ? [e] :
                e.Name == Table + "table-header-rows" || e.Name == Table + "table-rows" || e.Name == Table + "table-row-group" ? Rows(e) : []);
            foreach (var tr in Rows(t))
            {
                var row = new List<DocCell>();
                foreach (var tc in tr.Elements())
                {
                    if (tc.Name == Table + "table-cell")
                    {
                        var cell = new DocCell();
                        if (int.TryParse((string?)tc.Attribute(Table + "number-columns-spanned"), out var span)) cell.ColSpan = Math.Max(1, span);
                        ReadBlocks(tc, cell.Blocks, null, 0, null);
                        int repeat = int.TryParse((string?)tc.Attribute(Table + "number-columns-repeated"), out var rp) ? Math.Clamp(rp, 1, 64) : 1;
                        row.Add(cell);
                        for (int k = 1; k < repeat; k++) row.Add(new DocCell());
                    }
                }
                if (row.Count > 0) table.Rows.Add(row);
            }
            return table;
        }

        DocParagraph ReadParagraph(XElement e, DocListInfo? list)
        {
            var p = new DocParagraph { List = list };
            var styleName = (string?)e.Attribute(Text + "style-name");
            var chain = Chain(paragraphStyles, styleName).ToList();
            if (e.Name == Text + "h")
                p.Heading = Math.Clamp(int.TryParse((string?)e.Attribute(Text + "outline-level"), out var lv) ? lv : 1, 1, 6);
            else if (chain.FirstOrDefault(s => s.Outline > 0) is { } outlined) p.Heading = Math.Clamp(outlined.Outline, 1, 6);
            var align = chain.Select(s => (string?)s.Paragraph?.Attribute(Fo + "text-align")).FirstOrDefault(a => a != null);
            p.Align = align switch { "center" => DocAlign.Center, "end" or "right" => DocAlign.Right, "justify" => DocAlign.Justify, _ => DocAlign.Left };
            var style = new DocRun();
            if (p.Heading == 0)
                foreach (var s in Enumerable.Reverse(chain)) ApplyTextProperties(s.Text, style);
            bool space = true;
            trailingLiteralSpace = false;
            ReadInline(e, p, style, ref space);
            // A collapsed space at the end of the paragraph isn't content.
            if (trailingLiteralSpace && p.Runs.Count > 0 && p.Runs[^1].Text.EndsWith(' '))
                p.Runs[^1] = p.Runs[^1].With(p.Runs[^1].Text[..^1]);
            return p;
        }

        void ReadInline(XElement parent, DocParagraph p, DocRun style, ref bool space)
        {
            foreach (var node in parent.Nodes())
            {
                if (node is XText t)
                {
                    var sb = new StringBuilder();
                    foreach (var ch in t.Value)
                    {
                        if (ch is ' ' or '\t' or '\n' or '\r')
                        {
                            if (!space) sb.Append(' ');
                            space = true;
                        }
                        else { sb.Append(ch); space = false; }
                    }
                    if (sb.Length > 0)
                    {
                        p.Runs.Add(style.With(sb.ToString()));
                        trailingLiteralSpace = sb[^1] == ' ';
                    }
                    continue;
                }
                if (node is not XElement e) continue;
                trailingLiteralSpace = false;
                if (e.Name == Text + "s")
                {
                    int c = int.TryParse((string?)e.Attribute(Text + "c"), out var n) ? n : 1;
                    p.Runs.Add(style.With(new string(' ', Math.Clamp(c, 1, 1000))));
                    space = false;
                }
                else if (e.Name == Text + "tab") { p.Runs.Add(style.With("\t")); space = true; }
                else if (e.Name == Text + "line-break") { p.Runs.Add(style.With("\n")); space = true; }
                else if (e.Name == Text + "span")
                {
                    var inner = style.With("");
                    foreach (var s in Enumerable.Reverse(Chain(textStyles, (string?)e.Attribute(Text + "style-name")).ToList())) ApplyTextProperties(s.Text, inner);
                    ReadInline(e, p, inner, ref space);
                }
                else if (e.Name == Text + "a")
                {
                    var inner = style.With("");
                    inner.Link = (string?)e.Attribute(XLink + "href");
                    ReadInline(e, p, inner, ref space);
                }
                else if (e.Name == Text + "note" || e.Name == Text + "bookmark" || e.Name == Text + "bookmark-start" || e.Name == Text + "bookmark-end" ||
                         e.Name == Text + "soft-page-break" || e.Name == Office + "annotation" || e.Name == Office + "annotation-end" ||
                         e.Name == Text + "change" || e.Name == Text + "change-start" || e.Name == Text + "change-end" ||
                         e.Name.Namespace.NamespaceName.Contains(":drawing:"))
                    continue;
                else if (e.Name == Text + "ruby") { if (e.Element(Text + "ruby-base") is { } rb) ReadInline(rb, p, style, ref space); }
                else ReadInline(e, p, style, ref space);
            }
        }
    }

    public static DocModel ReadOdt(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return new OdtReader().Read(zip);
        }
        catch (InvalidDataException) { throw new ToolError(L("Couldn't read this document")); }
        catch (System.Xml.XmlException) { throw new ToolError(L("Couldn't read this document")); }
    }
}
