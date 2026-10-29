using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DocumentNotices
{
    internal const string Limits="Explicit local text, DOCX, ODT, RTF or static HTML input, up to 8 MiB and 2,000,000 UTF-16 units of extracted text. Plain text needs strict UTF-8 or BOM-marked UTF-16 LE/BE; HTML needs strict UTF-8 and also limits decoded source markup to 2,000,000 UTF-16 units. Other encodings are not guessed. DOCX/ODT allow up to 1,000 ZIP entries, 8 MiB document XML, 2,000,000 XML characters and XML depth 128.";
    internal const string Counting="Words are nonempty Unicode-whitespace-separated segments, not language-aware words. Chinese and Japanese without spaces may count as one segment. Characters are Unicode text elements (graphemes); scalar and UTF-8 byte counts are separate. CRLF is one line break; a final break creates an empty last line. Empty text has zero lines.";
    internal const string Docx="DOCX shows stored body XML text in order, not Word's rendered view. Formatting, images, headers, footnotes and object content are omitted. Deleted/moved-from blocks are skipped; hidden text and field results can remain, and alternate-content choices are not resolved. Nothing is executed or fetched.";
    internal const string Odt="ODT shows a limited content.xml body, not Office layout. Paragraphs/headings, lists and table paragraphs are read once in XML order; repeated rows/columns and list numbers are not expanded. Ordinary XML whitespace collapses across supported spans/links; explicit spaces, tabs and breaks remain. Fields, hidden-text/hidden-paragraph elements, objects, drawings, annotations, notes, tracked-change storage, ruby pronunciation and unknown inline elements are omitted. Inserted body text remains. Style and conditional visibility are not evaluated; hidden text can remain. No style, script or resource is loaded, and links never open.";
    internal const string Rtf="RTF reads limited stored body text, not rendered layout. Unicode escapes and Windows-1252 bytes are supported; unknown codepages/font charsets with visible high bytes and Unicode alternative groups are refused. Font and symbol glyph mappings are not interpreted. Fonts, styles, images, objects, fields (including results), headers, footers, notes, annotations and unknown ignorable destinations are omitted. Explicit hidden/deleted text is skipped, but style-based visibility is not evaluated. Paragraph, row and page controls become LF; cell controls become tabs. No extra final break is added. Unknown body controls may be omitted. Limits: group depth 128, 32-letter controls and Unicode fallback 0–16. Nothing is rendered, executed or fetched.";
    internal const string Html="HTML reads a strict UTF-8 source-markup subset, not browser layout or a complete HTML5 DOM. An explicit html/body and balanced end tags are required. Head, style/title/textarea, templates, noscript, objects, iframe, canvas, audio/video and select contents are omitted; hidden attributes suppress their subtree, even hidden=false. CSS, aria-hidden, dynamic and conditional visibility are not evaluated, so hidden text can remain. Links contribute text only. Attribute values, image alt text and form values are omitted from extracted text; no link is followed or external resource loaded. Script elements and on-prefixed attributes, unknown/custom/foreign elements, legacy charset and http-equiv declarations are refused. Ordinary space/tab/CR/LF collapse; block boundaries merge to LF without an added final break, while br emits an explicit LF. PRE preserves stored whitespace, including its first LF and CRLF, unlike browser newline handling. Entities decode once with the system entity set; unknown names stay literal. Numeric references in extracted body segments require a semicolon and at most eight digits; invalid scalars and controls except tab/CR/LF are refused. Limits: 2M UTF-16 units each for markup/text, depth 128, 100K markup tokens, 16K per tag, 128 attributes and 64-character names. Only the simple HTML doctype is allowed; no external resource, script or renderer is used. Exported HTML copies may exceed these reader budgets; TXT remains available.";
    internal const string Epub="EPUB reads the XHTML documents in the book's spine order, not a reading system's layout. Each spine document's body follows the HTML rules above for whitespace, blocks and line breaks, and documents are joined with a line break. Navigation and table-of-contents files are read only when the spine lists them. Images, SVG/MathML and other foreign markup, scripts, styles, head, hidden elements, ruby pronunciation and attribute text are omitted; image spine items are skipped. Encrypted or rights-managed books are refused; obfuscated fonts are never read and do not block reading. Named entities such as &nbsp; that need a DTD are refused. Limits: 1,000 ZIP entries, 8 MiB per XML part, 32 MiB of XML in total and depth 128. Nothing is extracted to disk, executed or fetched, and links never open.";
    internal const string Export="Exports contain only this extracted text. TXT uses UTF-8 without BOM and preserves decoded line endings. RTF uses a plain style and paragraph breaks. DOCX and ODT use one plain paragraph with tabs and line breaks; reading them back adds the final paragraph break. HTML is escaped static text, without active content. DOCX, ODT and HTML normalize CRLF/CR to LF. External readers may represent soft line breaks with different Unicode separators. Original formatting, links, images, attachments, macros and metadata are not preserved. A new file is saved beside the original without replacing it.";
}

internal sealed class DocumentTextDialog : Window
{
    internal const int PreviewLimit=65536;
    public TextExport ExportFormat=>formats.SelectedIndex switch { 1=>TextExport.Rtf,2=>TextExport.Docx,3=>TextExport.Html,4=>TextExport.Odt,_=>TextExport.Utf8 };
    private readonly ComboBox formats=new(){MinWidth=220,Margin=new Thickness(0,4,0,10)};
    internal string StatisticsReport {get;}="";
    internal static string Preview(string text)
    {
        int length=Math.Min(text.Length,PreviewLimit);
        if(length<text.Length&&length>0&&char.IsHighSurrogate(text[length-1]))length--;
        return text[..length];
    }
    public DocumentTextDialog(App app,DocumentTextFile file,bool previewOnly=false)
    {
        Title=app.L(previewOnly ? "Preview Document Text" : "Document Text and Word Count");Width=780;Height=900;MinWidth=500;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(22)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Statistics"),()=>app.Try(()=>Clipboard.SetText(StatisticsReport))));
        if(!previewOnly)footer.Children.Add(UI.Button(app.L("Save Text Copy…"),()=>DialogResult=true));
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        if(previewOnly)body.Children.Add(UI.Text(app.L("Read-only extracted text preview, not Office or browser layout. Nothing is executed, fetched or exported. Copy Statistics copies only the displayed statistics report, including its displayed source path and format notes, when explicitly pressed; text can be selected and copied manually. Full input-format and visibility limits are listed below."),12));
        string path=DoctorFacts.VisibleBounded(file.Path,512);
        body.Children.Add(new TextBox{Text=path,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var stats=file.Statistics;
        string summary=string.Join(Environment.NewLine,
            string.Format(app.L("Words / whitespace segments: {0:N0}"),stats.Words),
            string.Format(app.L("Characters / graphemes: {0:N0}"),stats.TextElements),
            string.Format(app.L("Unicode scalars: {0:N0}"),stats.Scalars),
            string.Format(app.L("Lines: {0:N0}"),stats.Lines),
            string.Format(app.L("UTF-8 text bytes (no BOM): {0:N0}"),stats.Utf8Bytes));
        string source=app.L(file.Document.Encoding);
        var report=new StringBuilder();report.AppendLine(path);report.AppendLine(source);report.AppendLine(summary);report.AppendLine(app.L(DocumentNotices.Counting));
        if(file.Document.Docx)report.AppendLine(app.L(DocumentNotices.Docx));if(file.Document.Odt)report.AppendLine(app.L(DocumentNotices.Odt));if(file.Document.Rtf)report.AppendLine(app.L(DocumentNotices.Rtf));if(file.Document.Html)report.AppendLine(app.L(DocumentNotices.Html));if(file.Document.Epub)report.AppendLine(app.L(DocumentNotices.Epub));report.AppendLine(app.L(DocumentNotices.Limits));StatisticsReport=report.ToString();
        body.Children.Add(UI.Text(source,13));body.Children.Add(UI.Text(summary,17));body.Children.Add(UI.Text(app.L(DocumentNotices.Counting),12));
        if(file.Document.Docx)body.Children.Add(UI.Text(app.L(DocumentNotices.Docx),12));if(file.Document.Odt)body.Children.Add(UI.Text(app.L(DocumentNotices.Odt),12));
        if(file.Document.Rtf)body.Children.Add(UI.Text(app.L(DocumentNotices.Rtf),12));
        if(file.Document.Html)body.Children.Add(UI.Text(app.L(DocumentNotices.Html),12));
        if(file.Document.Epub)body.Children.Add(UI.Text(app.L(DocumentNotices.Epub),12));
        body.Children.Add(UI.Text(app.L("Plain Text Preview"),14));
        if(file.Document.Text.Length>PreviewLimit)body.Children.Add(UI.Text(app.L(previewOnly ? "Preview is limited to the first 65,536 UTF-16 units. Counts use the entire extracted text; this preview does not export files." : "Preview is limited to the first 65,536 UTF-16 units. Counts and exports use the entire extracted text."),12));
        var preview=new TextBox{Text=Preview(file.Document.Text),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=260,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        AutomationProperties.SetName(preview,app.L("Plain Text Preview"));body.Children.Add(preview);
        if(!previewOnly)
        {
            body.Children.Add(UI.Text(app.L("Export Text As"),14));
            formats.Items.Add(app.L("TXT — UTF-8, no BOM"));formats.Items.Add(app.L("RTF — plain text style"));formats.Items.Add(app.L("DOCX — plain text style"));formats.Items.Add(app.L("HTML — static plain text"));formats.Items.Add(app.L("ODT — plain text style"));formats.SelectedIndex=0;AutomationProperties.SetName(formats,app.L("Export Text As"));body.Children.Add(formats);
            body.Children.Add(UI.Text(app.L(DocumentNotices.Export),12));body.Children.Add(UI.Text(app.L("Plain DOCX/ODT XML is limited to 2,000,000 UTF-16 units including markup and escaping. ODT also reserves its final paragraph break within the extracted-text limit. Large text or many spaces/tabs/line breaks may exceed the limits; it is refused, never truncated. TXT, RTF and HTML remain available."),12));
        }
        body.Children.Add(UI.Text(app.L(DocumentNotices.Limits),12));
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
    }
    private void FitWorkArea(Window reference)
    {
        // Screen working areas are physical pixels; an owner's WPF scale is in
        // DIPs. Fit before display so the fixed footer remains on that monitor.
        var handle=new WindowInteropHelper(reference).Handle;
        var area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(440,height);MinWidth=Math.Min(500,width);
        MaxHeight=height;MaxWidth=width;Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}
