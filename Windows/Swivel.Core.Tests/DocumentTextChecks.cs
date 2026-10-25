using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class DocumentTextChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        TextDocument Read(byte[] data,string ext="txt")=>DocumentText.Read(new MemoryStream(data),ext,default);
        string sample="Hello 中文 👩‍👩‍👧‍👦 e\u0301\r\nnext\n";
        foreach(var encoding in new Encoding[]{new UTF8Encoding(false,true),new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true)})
            check(Read(encoding.GetPreamble().Concat(encoding.GetBytes(sample)).ToArray()).Text==sample,"Strict text decoding preserves Unicode and line endings: "+encoding.WebName);
        check(Read(Array.Empty<byte>()).Text==""&&Read(new byte[]{0xef,0xbb,0xbf}).Text=="","Empty and BOM-only input remain empty");
        foreach(var bytes in new[]{new byte[]{0xc0,0xaf},new byte[]{0xff,0xfe,0x00},new byte[]{0xff,0xfe,0,0},new byte[]{0,0,0xfe,0xff},new byte[]{0xff,0xfe,0,0xd8}})
            reject(()=>Read(bytes),"Malformed Unicode or UTF32 is never guessed");
        foreach(char c in new[]{'\0','\u000b','\u001b','\u0085'})reject(()=>Read(Encoding.UTF8.GetBytes("a"+c+"b")),"Unsupported text control rejected");
        reject(()=>DocumentText.Validate("\ud800",default),"Unpaired Unicode rejected before export");
        reject(()=>Read(Encoding.UTF8.GetBytes("text"),"html"),"HTML is not sniffed as unstructured plain text");
        reject(()=>Read(new byte[DocumentText.MaxInputBytes+1]),"Input read budget checked independently of stream length");
        reject(()=>DocumentText.Validate(new string('a',DocumentText.MaxCharacters+1),default),"Extracted UTF16 budget enforced");
        var s=DocumentText.Count("你好 世界 a\u0301 👩‍👩‍👧‍👦\r\nx\n",default);
        check(s.Words==5&&s.TextElements==12&&s.Scalars==20&&s.Lines==3,"Whitespace segments, graphemes, scalar and CRLF line counts are distinct");
        check(DocumentText.Count("",default)==new TextStatistics(0,0,0,0,0),"Empty text has zero lines and counts");
        check(DocumentText.Count("\r\n\r\n",default).Lines==3&&DocumentText.Count("a\u2028b\u2029",default).Lines==3,"Trailing and Unicode line separators count explicitly");
        foreach(string text in new[]{"",sample,@"{\rtf1\object malicious}",new string('a',8191)+"😀end",new string('b',DocumentText.MaxCharacters)})
        {
            using var txt=new MemoryStream();DocumentText.Write(txt,text,TextExport.Utf8,default);
            check(txt.ToArray().SequenceEqual(new UTF8Encoding(false,true).GetBytes(text)),"UTF8 export preserves exact text across encoder chunk boundaries");
            using var rtf=new MemoryStream();DocumentText.Write(rtf,text,TextExport.Rtf,default);string value=Encoding.ASCII.GetString(rtf.ToArray());
            check(value.StartsWith(@"{\rtf1\ansi")&&value.EndsWith('}')&&rtf.ToArray().All(x=>x<128),"Original RTF writer emits ASCII control syntax and Unicode escapes only");
        }
        using(var rtf=new MemoryStream())
        {
            DocumentText.Write(rtf,@"{\object x}",TextExport.Rtf,default);
            check(Encoding.ASCII.GetString(rtf.ToArray()).Contains(@"\{\\object x\}"),"RTF braces/backslashes cannot inject object controls");
        }
        byte[] Doc(string xml,bool duplicate=false,bool alternate=false)
        {
            using var stream=new MemoryStream();using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                using(var writer=new StreamWriter(zip.CreateEntry("word/document.xml").Open(),new UTF8Encoding(false)))writer.Write(xml);
                if(duplicate)using(var writer=new StreamWriter(zip.CreateEntry(alternate?"WORD/document.xml":"word/document.xml").Open()))writer.Write(xml);
                using(var writer=new StreamWriter(zip.CreateEntry("word/_rels/document.xml.rels").Open()))writer.Write("<!DOCTYPE not-read SYSTEM 'https://example.invalid/'>");
            }return stream.ToArray();
        }
        string Body(string inner,string ns="http://schemas.openxmlformats.org/wordprocessingml/2006/main")=>"<w:document xmlns:w='"+ns+"'><w:body>"+inner+"</w:body></w:document>";
        string xml=Body("<w:p><w:r><w:t>Hello &amp; 中文</w:t><w:tab/><w:t>x</w:t><w:br/><w:t>end</w:t></w:r><w:del><w:r><w:t>deleted</w:t></w:r></w:del></w:p><w:p/>");
        check(Read(Doc(xml),"docx").Text=="Hello & 中文\tx\nend\n\n","DOCX fixed body text, tab, explicit and paragraph breaks; relations are never parsed");
        check(Read(Doc(Body("<w:p><w:r><w:t>strict</w:t></w:r></w:p>","http://purl.oclc.org/ooxml/wordprocessingml/main")),"docx").Text=="strict\n","Strict OpenXML namespace supported");
        foreach(var tag in new[]{"object","drawing","pict","del","moveFrom"})
            check(Read(Doc(Body("<w:p><w:"+tag+"><w:t>excluded</w:t></w:"+tag+"><w:r><w:t>kept</w:t></w:r></w:p>")),"docx").Text=="kept\n","Excluded DOCX block: "+tag);
        reject(()=>Read(Doc(xml,true),"docx"),"Duplicate XML entry rejected");reject(()=>Read(Doc(xml,true,true),"docx"),"Case alias XML entry rejected");
        foreach(string bad in new[]{"<document/>","<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'/>","<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///private'>]>"+xml,Body("<w:p><w:r><w:t><w:t>nested</w:t></w:t></w:r></w:p>")})
            reject(()=>Read(Doc(bad),"docx"),"Malformed/DTD/nested text document rejected without external reads");
        reject(()=>DocumentText.Read(new MemoryStream(new byte[]{1}),"txt",new CancellationToken(true)),"Read pre-cancellation");
        reject(()=>DocumentText.Count(sample,new CancellationToken(true)),"Count pre-cancellation");
        using(var output=new MemoryStream())reject(()=>DocumentText.Write(output,sample,TextExport.Rtf,new CancellationToken(true)),"Export pre-cancellation");
        foreach(string text in new[]{"","plain",sample,"\n first & <script>\"' {\\object} \t\r\nlast ",new string('a',8191)+"😀end",new string('界',100_000)})
        {
            using var docx=new MemoryStream();DocumentText.Write(docx,text,TextExport.Docx,default);
            string normalized=text.Replace("\r\n","\n").Replace('\r','\n');
            check(Read(docx.ToArray(),"docx").Text==normalized+"\n","Generated plain DOCX preserves escaped Unicode/tabs and normalizes explicit breaks, with final paragraph break");
            using(var zip=new ZipArchive(new MemoryStream(docx.ToArray()),ZipArchiveMode.Read))
            {
                check(zip.Entries.Select(x=>x.FullName).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{"[Content_Types].xml","_rels/.rels","word/document.xml"}.OrderBy(x=>x,StringComparer.Ordinal)),"Generated DOCX contains only minimal fixed internal parts, no inherited resources");
                using var xmlText=new StreamReader(zip.GetEntry("word/document.xml")!.Open());string body=xmlText.ReadToEnd();
                check(body.Length<=DocumentText.MaxCharacters && body.Contains("xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\""),"Complete serialized DOCX XML respects budget including markup");
            }
            using var html=new MemoryStream();DocumentText.Write(html,text,TextExport.Html,default);string page=Encoding.UTF8.GetString(html.ToArray());
            string encoded=page[(page.IndexOf("<span>",StringComparison.Ordinal)+6)..page.IndexOf("</span>",StringComparison.Ordinal)];
            check(System.Net.WebUtility.HtmlDecode(encoded)==normalized && !encoded.Contains('<') && !encoded.Contains('>'),"Static HTML text escapes tag syntax, preserves initial LF and normalizes CR/CRLF without interpreting source markup");
            check(!html.ToArray().AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}) && !page.Contains("<script>") && !page.Contains("href="),"HTML copy is UTF8 without BOM, executable markup or links");
        }
        foreach(string tooLarge in new[]{new string('a',DocumentText.MaxCharacters),new string('&',500_000),new string('\t',300_000),new string('\n',300_000)})
        {
            using var output=new MemoryStream();reject(()=>DocumentText.Write(output,tooLarge,TextExport.Docx,default),"DOCX complete XML expansion refused instead of truncating");
            check(output.Length==0,"DOCX limit rejection happens before ZIP output");
        }
        foreach(string invalidXml in new[]{"a\ufffeb","\uffff"})
        {
            using var output=new MemoryStream();reject(()=>DocumentText.Write(output,invalidXml,TextExport.Docx,default),"XML-illegal characters reject only DOCX");
            check(output.Length==0,"XML-illegal source publishes no DOCX bytes");
            foreach(var format in new[]{TextExport.Utf8,TextExport.Rtf,TextExport.Html})
            { using var other=new MemoryStream();DocumentText.Write(other,invalidXml,format,default);check(other.Length>0,"Other text formats retain their Unicode contract when DOCX is unavailable"); }
        }
        foreach(var format in new[]{TextExport.Docx,TextExport.Html})
        { using var output=new MemoryStream();reject(()=>DocumentText.Write(output,sample,format,new CancellationToken(true)),"New format precancellation before any output");check(output.Length==0,"New format precancellation leaves output empty"); }
        var prefs=new Prefs();check(Catalog.Available(prefs,new[]{"sample.DOCX"}).Any(x=>x.Id=="document.text"),"Document text is an explicit basic single-file action");
        check(!Catalog.Available(prefs,new[]{"sample.exe"}).Any(x=>x.Id=="document.text")&&!Catalog.Available(prefs,new[]{"a.txt","b.txt"}).Any(x=>x.Id=="document.text"),"Document catalog whitelist and single input enforced");
    }
}
