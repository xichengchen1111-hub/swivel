using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class DocumentEpubChecks
{
    internal const string Container="<?xml version='1.0' encoding='UTF-8'?><container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>";
    internal static string Page(string body,string head="")=>"<?xml version='1.0' encoding='utf-8'?><html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><head><title>skip</title>"+head+"</head><body>"+body+"</body></html>";
    internal static string Opf(string manifest,string spine)=>"<?xml version='1.0' encoding='UTF-8'?><package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Not body</dc:title></metadata><manifest>"+manifest+"</manifest><spine>"+spine+"</spine></package>";
    internal static byte[] Book(IEnumerable<(string Name,byte[] Data)> parts,string mimetype="application/epub+zip")
    {
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            if(mimetype.Length>0){var entry=zip.CreateEntry("mimetype",CompressionLevel.NoCompression);using var s=entry.Open();s.Write(Encoding.ASCII.GetBytes(mimetype));}
            foreach(var (name,data) in parts){using var s=zip.CreateEntry(name).Open();s.Write(data);}
        }
        return output.ToArray();
    }
    internal static byte[] Simple(params (string Name,string Text)[] parts)=>Book(parts.Select(x=>(x.Name,Encoding.UTF8.GetBytes(x.Text))));
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        TextDocument Read(byte[] bytes,CancellationToken token=default)=>DocumentText.Read(new MemoryStream(bytes),"EPUB",token);
        void Refuse(byte[] bytes,string expected,string label)
        {
            try{Read(bytes);check(false,label+" (accepted)");}
            catch(UserError error){check(error.Message==expected,label+": "+error.Message);}
        }
        string items="<item id='c2' href='Text/two.xhtml' media-type='application/xhtml+xml'/><item id='c1' href='Text/one.xhtml' media-type='application/xhtml+xml'/><item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/><item id='cover' href='Images/cover.jpg' media-type='image/jpeg'/><item id='font' href='Fonts/a.otf' media-type='font/otf'/><item id='remote' href='https://example.invalid/never.css' media-type='text/css'/>";
        (string,string)[] Parts(string spine,string one,string two)=>new[]{("META-INF/container.xml",Container),("OEBPS/content.opf",Opf(items,spine)),("OEBPS/Text/one.xhtml",one),("OEBPS/Text/two.xhtml",two),("OEBPS/nav.xhtml",Page("<nav epub:type='toc'><ol><li>TOC entry</li></ol></nav>")),("OEBPS/Images/cover.jpg","not really"),("OEBPS/Fonts/a.otf","font")};
        string one=Page("<h1>Chapter  One</h1><p>Hello <b>bold</b>  中文 😀 &amp; &#x4E16;</p><p>A<br/>B</p><img src='../Images/cover.jpg' alt='alt not text'/><script>never()</script><svg xmlns='http://www.w3.org/2000/svg'><text>svg</text></svg><p hidden='hidden'>secret</p><p><ruby>漢<rt>かん</rt></ruby></p><pre>  kept\n  lines</pre>","<style>p{}</style><link rel='stylesheet' href='../style.css'/>");
        string two=Page("<section><p>Second</p></section>");
        var book=Read(Simple(Parts("<itemref idref='cover'/><itemref idref='c1'/><itemref idref='c2' linear='no'/>",one,two)));
        check(book.Epub&&!book.Html&&!book.Docx&&book.Encoding=="EPUB spine body text","EPUB flagged and labelled as spine body text");
        check(book.Text=="Chapter One\nHello bold 中文 😀 & 世\nA\nB\n漢\n  kept\n  lines\nSecond","EPUB spine order, HTML whitespace, omitted image/script/svg/hidden/ruby, nav not in spine: "+book.Text.Replace("\n","⏎"));
        check(Read(Simple(Parts("<itemref idref='c2'/><itemref idref='c1'/>",one,two))).Text.StartsWith("Second\nChapter One",StringComparison.Ordinal),"Spine order, not manifest or ZIP order, decides the text order");
        check(Read(Simple(Parts("<itemref idref='nav'/><itemref idref='c2'/>",one,two))).Text=="TOC entry\nSecond","Navigation document is read only when the spine lists it");
        check(Read(Simple(Parts("<itemref idref='cover'/>",one,two))).Text=="","Image-only spine yields empty text");
        string doctype="<?xml version='1.0' encoding='utf-8'?><!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'><html xmlns='http://www.w3.org/1999/xhtml'><head><title>t</title></head><body><p>EPUB2 doctype</p></body></html>";
        check(Read(Simple(Parts("<itemref idref='c1'/>",doctype,two))).Text=="EPUB2 doctype","EPUB 2 XHTML doctype is skipped without resolving its DTD");
        string entity="<?xml version='1.0'?><!DOCTYPE html [<!ENTITY ext SYSTEM 'file:///never'><!ENTITY nb '&#160;'>]><html xmlns='http://www.w3.org/1999/xhtml'><body><p>&ext;&nb;&nbsp;</p></body></html>";
        Refuse(Simple(Parts("<itemref idref='c1'/>",entity,two)),DocumentEpub.Invalid,"Internal/external and DTD named entities refused, never expanded or fetched");
        string utf16Page=Page("<p>UTF-16 页</p>").Replace("utf-8","utf-16");
        var utf16=Parts("<itemref idref='c1'/>",one,two).Select(x=>(x.Item1,x.Item1.EndsWith("one.xhtml")?new byte[]{0xff,0xfe}.Concat(Encoding.Unicode.GetBytes(utf16Page)).ToArray():Encoding.UTF8.GetBytes(x.Item2)));
        check(Read(Book(utf16)).Text=="UTF-16 页","BOM-marked UTF-16 XHTML accepted");
        var latin=Parts("<itemref idref='c1'/>",one,two).Select(x=>(x.Item1,x.Item1.EndsWith("one.xhtml")?Encoding.Latin1.GetBytes(Page("<p>caf\u00e9</p>").Replace("utf-8","ISO-8859-1")):Encoding.UTF8.GetBytes(x.Item2)));
        Refuse(Book(latin),DocumentEpub.Invalid,"Legacy encoding declaration and non-UTF-8 bytes refused");
        var badUtf8=Parts("<itemref idref='c1'/>",one,two).Select(x=>(x.Item1,x.Item1.EndsWith("one.xhtml")?Encoding.Latin1.GetBytes(Page("<p>caf\u00e9</p>")):Encoding.UTF8.GetBytes(x.Item2)));
        Refuse(Book(badUtf8),DocumentEpub.Invalid,"Invalid UTF-8 bytes refused even with a UTF-8 declaration");
        string percent=Opf("<item id='s' href='Text/My%20Chapter.xhtml' media-type='application/xhtml+xml'/><item id='u' href='./Text/../Text/one.xhtml' media-type='application/xhtml+xml'/>","<itemref idref='s'/><itemref idref='u'/>");
        check(Read(Simple(("META-INF/container.xml",Container),("OEBPS/content.opf",percent),("OEBPS/Text/My Chapter.xhtml",Page("<p>Spaced</p>")),("OEBPS/Text/one.xhtml",Page("<p>Dots</p>")))).Text=="Spaced\nDots","Percent escapes decode once and . / .. inside the book resolve");
        string rootOpf=Container.Replace("OEBPS/content.opf","book.opf");
        check(Read(Simple(("META-INF/container.xml",rootOpf),("book.opf",Opf("<item id='a' href='a.xhtml' media-type='application/xhtml+xml'/>","<itemref idref='a'/>")),("a.xhtml",Page("<p>Root OPF</p>")))).Text=="Root OPF","OPF at the container root resolves bare hrefs");
        foreach(string href in new[]{"../../outside.xhtml","/OEBPS/Text/one.xhtml","https://example.invalid/a.xhtml","file:///C:/a.xhtml","Text\\one.xhtml","Text//one.xhtml","Text/one.xhtml#frag","Text/one.xhtml?q","Text/%2E%2E/%2E%2E/%2E%2E/x.xhtml","C:/a.xhtml","Text/one%00.xhtml",""})
        {
            string opf=Opf("<item id='x' href='"+href+"' media-type='application/xhtml+xml'/>","<itemref idref='x'/>");
            Refuse(Simple(("META-INF/container.xml",Container),("OEBPS/content.opf",opf),("OEBPS/Text/one.xhtml",one),("outside.xhtml",Page("<p>escaped</p>"))),DocumentEpub.Invalid,"Hostile or out-of-book href refused: "+href);
        }
        Refuse(Simple(("META-INF/container.xml",Container.Replace("OEBPS/content.opf","../content.opf")),("content.opf",Opf("","<itemref idref='x'/>"))),DocumentEpub.Invalid,"Rootfile path above the container refused");
        Refuse(Simple(Parts("<itemref idref='c1'/><itemref idref='missing'/>",one,two)),DocumentEpub.Invalid,"Spine idref missing from manifest refused");
        Refuse(Simple(Parts("<itemref idref='c1'/><itemref idref='c1'/>",one,two)),DocumentEpub.Invalid,"Duplicate spine idref refused");
        Refuse(Simple(Parts("<itemref idref='remote'/>",one,two)),DocumentEpub.Invalid,"Remote spine resource refused without any request");
        Refuse(Simple(Parts("<itemref idref='font'/>",one,two)),DocumentEpub.Unsupported,"Non-XHTML, non-image spine item refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Where(x=>!x.Item1.EndsWith("one.xhtml")).ToArray()),DocumentEpub.Invalid,"Manifest item missing from the ZIP refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("OEBPS/text/ONE.xhtml",one)).ToArray()),DocumentEpub.Invalid,"Case-alias ZIP entry names refused for the whole book");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("oebps/content.OPF","x")).ToArray()),DocumentEpub.Invalid,"Case-alias OPF twin refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("OEBPS\\Text\\one.xhtml",one)).ToArray()),DocumentEpub.Invalid,"Backslash alias ZIP entry refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/container.xml",Container)).ToArray()),DocumentEpub.Invalid,"Duplicate ZIP entry names refused");
        foreach(string mime in new[]{"","application/zip","application/epub+zip\n","APPLICATION/EPUB+ZIP"})
            Refuse(Book(Parts("<itemref idref='c1'/>",one,two).Select(x=>(x.Item1,Encoding.UTF8.GetBytes(x.Item2))),mime),DocumentEpub.Invalid,"mimetype must be exactly application/epub+zip: "+mime.Replace("\n","\\n"));
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Where(x=>x.Item1!="META-INF/container.xml").ToArray()),DocumentEpub.Invalid,"Missing container.xml refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Select(x=>x.Item1=="META-INF/container.xml"?(x.Item1,Container.Replace("application/oebps-package+xml","text/plain")):x).ToArray()),DocumentEpub.Invalid,"Container without an OPF rootfile refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Select(x=>x.Item1=="META-INF/container.xml"?(x.Item1,"<!DOCTYPE c SYSTEM 'file:///never'>"+Container[Container.IndexOf("<container",StringComparison.Ordinal)..]):x).ToArray()),DocumentEpub.Invalid,"Container DTD prohibited");
        Refuse(Simple(Parts("<itemref idref='c1'/>","<html xmlns='http://www.w3.org/1999/xhtml'><body><p>x</b></body></html>",two)),DocumentEpub.Invalid,"Malformed XHTML refused, not repaired");
        Refuse(Simple(Parts("<itemref idref='c1'/>",Page("x").Replace("http://www.w3.org/1999/xhtml","urn:other"),two)),DocumentEpub.Invalid,"Spine document must be an XHTML html root");
        string enc(string algorithm,string uri)=>"<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container' xmlns:enc='http://www.w3.org/2001/04/xmlenc#'><enc:EncryptedData><enc:EncryptionMethod Algorithm='"+algorithm+"'/><enc:CipherData><enc:CipherReference URI='"+uri+"'/></enc:CipherData></enc:EncryptedData></encryption>";
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/encryption.xml",enc("http://www.w3.org/2001/04/xmlenc#aes128-cbc","OEBPS/Text/one.xhtml"))).ToArray()),DocumentEpub.Encrypted,"Encrypted spine document refused clearly");
        Refuse(Simple(Parts("<itemref idref='c2'/>",one,two).Append(("META-INF/encryption.xml",enc("http://www.w3.org/2001/04/xmlenc#aes128-cbc","OEBPS/Fonts/a.otf"))).ToArray()),DocumentEpub.Encrypted,"Any real encryption marks the book as rights-managed");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/encryption.xml",enc("http://www.idpf.org/2008/embedding","OEBPS/Text/one.xhtml"))).ToArray()),DocumentEpub.Encrypted,"Obfuscation on a spine document refused");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/encryption.xml","<encryption")).ToArray()),DocumentEpub.Encrypted,"Unreadable encryption.xml refused as encrypted");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/rights.xml","<rights/>")).ToArray()),DocumentEpub.Encrypted,"rights.xml marks a DRM book");
        check(Read(Simple(Parts("<itemref idref='c2'/>",one,two).Append(("META-INF/encryption.xml",enc("http://www.idpf.org/2008/embedding","OEBPS/Fonts/a.otf"))).ToArray())).Text=="Second","Font obfuscation only does not block reading; fonts are never read");
        check(Read(Simple(Parts("<itemref idref='c2'/>",one,two).Append(("META-INF/encryption.xml",enc("http://ns.adobe.com/pdf/enc#RC","OEBPS/Fonts/a.otf"))).ToArray())).Text=="Second","Adobe font obfuscation also allowed");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/Encryption.xml",enc("http://www.w3.org/2001/04/xmlenc#aes128-cbc","OEBPS/Text/one.xhtml"))).ToArray()),DocumentEpub.Encrypted,"Case-variant encryption.xml still refuses");
        Refuse(Simple(Parts("<itemref idref='c1'/>",one,two).Append(("META-INF/RIGHTS.XML","<rights/>")).ToArray()),DocumentEpub.Encrypted,"Case-variant rights.xml still refuses");
        check(Read(Simple(Parts("<itemref idref='cover'/><itemref idref='c2'/>",one,two).Append(("META-INF/encryption.xml",enc("http://www.idpf.org/2008/embedding","OEBPS/Images/cover.jpg"))).ToArray())).Text=="Second","Obfuscated image in the spine is skipped, not refused");
        string stray=Opf("<item id='a' href='a.xhtml' media-type='application/xhtml+xml; charset=utf-8'/><item id='css' href='s.css#x' media-type='text/css'/><item id='up' href='../../x.css' media-type='text/css'/>","<itemref idref='a'/>").Replace("<dc:title>Not body</dc:title>","<dc:title>Not body</dc:title><item id='meta' href='meta.xhtml' media-type='application/xhtml+xml'/><itemref idref='meta'/>");
        check(Read(Simple(("META-INF/container.xml",Container),("OEBPS/content.opf",stray),("OEBPS/a.xhtml",Page("<p>Only a</p>")),("OEBPS/meta.xhtml",Page("<p>meta item</p>")))).Text=="Only a","item/itemref only count under manifest/spine; unusable non-spine hrefs and media-type parameters tolerated");
        check(Read(Simple(Parts("<itemref idref='c1'/><itemref idref='c2'/>",Page("<p>x</p>").Replace("<body>","<body hidden=''>"),Page("<p>y</p>").Replace("<html ","<html hidden='' ")))).Text=="","hidden on body or html suppresses that document");
        check(Read(Simple(Parts("<itemref idref='c1'/>",Page("<p>ascii</p>").Replace("utf-8","US-ASCII"),two))).Text=="ascii","US-ASCII declaration accepted (UTF-8 subset, strictly decoded)");
        string deep=Page(string.Concat(Enumerable.Repeat("<div>",130))+"x"+string.Concat(Enumerable.Repeat("</div>",130)));
        Refuse(Simple(Parts("<itemref idref='c1'/>",deep,two)),DocumentEpub.Limit,"XHTML depth over 128 refused");
        string space=Page("<p>"+new string(' ',9*1024*1024)+"</p>");
        Refuse(Simple(Parts("<itemref idref='c1'/>",space,two)),DocumentEpub.Limit,"XML part over 8 MiB refused before parsing (compressed bomb)");
        string half=Page("<p>"+new string('a',1_100_000)+"</p>");
        Refuse(Simple(Parts("<itemref idref='c1'/><itemref idref='c2'/>",half,half)),DocumentEpub.Limit,"Combined extracted text over 2,000,000 UTF-16 units refused");
        string filler=Page("<p>"+new string(' ',1_900_000)+"x</p>");var many=new List<(string,string)>{("META-INF/container.xml",Container)};var manifest=new StringBuilder();var spineRefs=new StringBuilder();
        for(int i=0;i<18;i++){manifest.Append("<item id='p"+i+"' href='p"+i+".xhtml' media-type='application/xhtml+xml'/>");spineRefs.Append("<itemref idref='p"+i+"'/>");many.Add(("OEBPS/p"+i+".xhtml",filler));}
        many.Add(("OEBPS/content.opf",Opf(manifest.ToString(),spineRefs.ToString())));
        Refuse(Simple(many.ToArray()),DocumentEpub.Limit,"Total decompressed XML over 32 MiB refused");
        Refuse(Simple(Enumerable.Range(0,1001).Select(i=>("f"+i,"x")).Concat(Parts("<itemref idref='c1'/>",one,two)).ToArray()),DocumentEpub.Limit,"More than 1,000 ZIP entries refused");
        reject(()=>DocumentText.Read(new MemoryStream(new byte[DocumentText.MaxInputBytes+1]),"epub",default),"EPUB input over 8 MiB refused");
        reject(()=>DocumentText.Read(new MemoryStream(Encoding.UTF8.GetBytes("not a zip")),"epub",default),"Non-ZIP EPUB refused");
        bool cancelled=false;try{Read(Simple(Parts("<itemref idref='c1'/>",one,two)),new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}
        check(cancelled,"EPUB read honours pre-cancellation");
        using(var copy=new MemoryStream()){DocumentText.Write(copy,book.Text,TextExport.Utf8,default);check(Encoding.UTF8.GetString(copy.ToArray())==book.Text,"EPUB text exports through the existing plain TXT writer");}
        var prefs=new Prefs();check(Catalog.Available(prefs,new[]{"book.EPUB"}).Any(x=>x.Id=="document.text"),"EPUB joins the explicit Inspect Document Text action");
        check(Catalog.Available(prefs,new[]{"book.epub"}).Any(x=>x.Id=="file.preview"),"EPUB offered to the read-only file preview");
        foreach(string key in new[]{DocumentEpub.Invalid,DocumentEpub.Limit,DocumentEpub.Encrypted,DocumentEpub.Unsupported,"EPUB spine body text"})
            check(Strings.Table.ContainsKey(key),"EPUB message translated: "+key[..Math.Min(40,key.Length)]);
    }
}
