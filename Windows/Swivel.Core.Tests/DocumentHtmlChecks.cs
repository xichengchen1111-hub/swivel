using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class DocumentHtmlChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string Read(string source)=>DocumentHtml.Read(Encoding.UTF8.GetBytes(source),default);
        string Page(string body)=>"<!doctype html><html><head><meta charset='UTF-8'><title>not body</title></head><body>"+body+"</body></html>";
        check(Read(Page("<p>Hello <b>world</b> &amp; 中文</p><p>next</p>"))=="Hello world & 中文\nnext","HTML inline joins and lazy block separators without trailingLF");
        check(Read(Page(" \t\r\n A  B\r\n C&nbsp;D "))=="A B C\u00a0D","HTML normal four-whitespace collapse retains NBSP");
        check(Read(Page("<br><br>x<br><br>"))=="\n\nx\n\n","HTML br explicitly keeps initial/final/repeated newlines");
        check(Read(Page("<pre>\n x\r\n\t y </pre>"))=="\n x\r\n\t y ","PRE stored firstLF/CRLF/space/tab are preserved without browser normalization");
        check(Read(Page("<p>x</p><pre> y </pre><p>z</p>"))=="x\n y \nz","PRE and normal blocks have defined boundaries");
        check(Read(Page("<div hidden id='x'>secret <span>child</span></div><p>shown</p><i hidden=false>no</i><i hidden=until-found>no</i>"))=="shown","Boolean hidden and inherited subtrees omitted, including hidden=false");
        check(Read("<html hidden id=x><head><title>no</title></head><body>secret</body></html>")=="","Root hidden state reaches body");
        check(Read(Page("<span aria-hidden=true style='display:none'>stored</span>"))=="stored","CSS and aria-hidden are not evaluated or mistaken for hidden attribute");
        check(Read(Page("<a href='https://example.invalid/x?x=&quot;&gt;' title=unused>link</a><img src='file:///private/secret' alt='not body'><input value='private'>"))=="link","Link/attribute/image/form values never become body text or resources");
        check(Read(Page("<style></stylex>css <script>text</style><textarea></textareax><b>not structure</textarea><template><p>hidden</p></template><noscript>ignored</noscript>x"))=="x","Raw fake closes not tokens, skipped textarea/template/noscript");
        check(Read("<HTML><HEAD><TITLE></TITLEX><b>ignored</TITLE><STYLE> a < b </StYlE></HEAD><BODY><P>X</P></BODY></HTML>")=="X","ASCII-case tags and raw title/style ending with exact name boundaries");
        check(Read(Page("<table><tr><td>A</td><td>B</td></tr></table><ul><li>C</li><li>D</li></ul>"))=="A\nB\nC\nD","Stored table/list text separated without inventing layout or numbering");
        check(Read(Page("&amp;lt;script&amp;gt; &lt;b&gt; &#x1F600; &#65; &notKnown;"))=="&lt;script&gt; <b> 😀 A &notKnown;","Entities decode once and never retokenize decoded angle brackets");
        check(Read(Page("<pre>&#9;&#10;&#13; &#x10ffff;</pre>"))=="\t\n\r "+char.ConvertFromUtf32(0x10ffff),"Numeric scalar upperbound and permitted control values");
        check(Read(Page("<object><p>ignored</p></object><iframe></iframex><b>raw fallback</iframe><canvas>ignored</canvas><audio>ignored</audio><video>ignored</video><select><option>ignored</option></select>body"))=="body","Stored fallback and control subtree omission explicit");
        foreach(string bad in new[]{"fragment","<p>fragment</p>","<html><body><p>x</body></html>","<html><body>x</html>","<html><body></body><body></body></html>","<html><head></head><head></head><body></body></html>","<html><body><div/></body></html>","<html><body><br></br></body></html>","<html><body><span a='x'b='y'>x</span></body></html>"})reject(()=>Read(bad),"HTML malformed/fragment/implicit repair refused");
        foreach(string bad in new[]{"<script>x</script>","<p onclick='x'>x</p>","<div hidden><script>x</script></div>","<template><p onload=x>x</p></template>","<custom>text</custom>","<svg><text>text</text></svg>","<?xml version='1.0'?>"})reject(()=>Read(Page(bad)),"HTML unsupported active/custom/foreign/declaration refused even inside hidden/template");
        foreach(string bad in new[]{"&#0;","&#xD800;","&#x110000;","&#128;","&#12;","&#65","&#x;","&#999999999;","&#000000001;","&#-1;"})reject(()=>Read(Page(bad)),"Numeric references reject invalid/control/unterminated/too-long forms");
        foreach(string bad in new[]{"<html><head><meta charset=windows-1252></head><body>x</body></html>","<html><head><meta http-equiv='refresh' content='0;url=file:///private/x'></head><body>x</body></html>","<!DOCTYPE html SYSTEM 'file:///private/x'><html><body>x</body></html>","<!DOCTYPE html [<!ENTITY x SYSTEM 'file:///x'>]><html><body>x</body></html>","<html><body><p A=1 a=2>x</p></body></html>"})reject(()=>Read(bad),"Charsets/declarations/duplicate attributes not repaired or followed");
        foreach(byte[] source in new[]{new byte[]{0xff},Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Page("x"))).ToArray()})reject(()=>DocumentHtml.Read(source,default),"HTML encoding is strict UTF8 only");
        var bom=new byte[]{0xef,0xbb,0xbf}.Concat(Encoding.UTF8.GetBytes(Page("x"))).ToArray();check(DocumentHtml.Read(bom,default)=="x","HTML optional UTF8 BOM");
        foreach(string ext in new[]{"html","HTM"})
        {using var input=new MemoryStream(Encoding.UTF8.GetBytes(Page("x")));var doc=DocumentText.Read(input,ext,default);check(doc.Html&&!doc.Docx&&!doc.Odt&&!doc.Rtf&&doc.Encoding=="HTML source body text"&&doc.Text=="x","Document routing uses explicit HTML extension and session flag");}
        var random=new Random(3601);
        foreach(string text in new[]{"", "\nHello\r\n 世界 & < > ' \" 😀\t", new string('a',8191)+"😀end", string.Concat(Enumerable.Range(0,5000).Select(_=>char.ConvertFromUtf32(random.Next(0x4e00,0x9fff))))})
        {using var output=new MemoryStream();DocumentText.Write(output,text,TextExport.Html,default);check(DocumentHtml.Read(output.ToArray(),default)==text.Replace("\r\n","\n").Replace('\r','\n'),"Existing escaped HTMLwriter reads back exactly after existing newline normalization");}
        string exact="<html><body>"+new string('a',DocumentText.MaxCharacters-26)+"</body></html>";check(exact.Length==DocumentText.MaxCharacters&&Read(exact).Length==DocumentText.MaxCharacters-26,"HTML decodedmarkup budget exact boundary");reject(()=>Read(exact+" "),"Markup +1 refused without truncation");
        reject(()=>DocumentHtml.Read(new byte[DocumentText.MaxInputBytes+1],default),"HTML direct sourcebyte budget independent of markup decoder");
        check(Read(Page(new string('<',0)+string.Concat(Enumerable.Repeat("<span>",126))+"x"+string.Concat(Enumerable.Repeat("</span>",126))))=="x","HTML exact128 stackdepth includes html/body");reject(()=>Read(Page(string.Concat(Enumerable.Repeat("<span>",127))+"x"+string.Concat(Enumerable.Repeat("</span>",127)))),"Depth129 refused");
        string attrs=string.Concat(Enumerable.Range(0,128).Select(i=>" a"+i+"=x"));check(Read(Page("<p"+attrs+">x</p>"))=="x","Exactly128 attributes");reject(()=>Read(Page("<p"+attrs+" extra=x>x</p>")),"Attribute129 refused");
        string name=new string('a',64);check(Read(Page("<p "+name+"=x>x</p>"))=="x","Exactly64-character attribute names");reject(()=>Read(Page("<p "+name+"a=x>x</p>")),"Name65 refused");
        string prefix="<span title='",suffix="'>";string tag=prefix+new string('x',DocumentHtml.MaxTag-prefix.Length-suffix.Length)+suffix;check(Read(Page(tag+"x</span>"))=="x","Tag16384 exact boundary");reject(()=>Read(Page(tag.Insert(prefix.Length,"x")+"x</span>")),"Tag16385 refused");
        string basic="<html><body>";int count=DocumentHtml.MaxTokens-4;check(Read(basic+string.Concat(Enumerable.Repeat("<br>",count))+"</body></html>").Length==count,"Exact100K markup token budget counts both end tags");reject(()=>Read(basic+string.Concat(Enumerable.Repeat("<br>",count+1))+"</body></html>"),"Token100001 refused");
        reject(()=>DocumentHtml.Read(Encoding.UTF8.GetBytes(Page("x")),new CancellationToken(true)),"HTML precancellation");
        using(var token=new CancellationTokenSource())using(var input=new CancelInput(Encoding.UTF8.GetBytes(Page(new string('a',100000))),token))reject(()=>DocumentText.Read(input,"html",token.Token),"Cancellation during bounded source input");
    }
    private sealed class CancelInput(byte[] bytes,CancellationTokenSource token):MemoryStream(bytes)
    {public override int Read(byte[] buffer,int offset,int count){int read=base.Read(buffer,offset,count);if(Position>=16384)token.Cancel();return read;}}
}
