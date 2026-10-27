using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class DocumentOdtChecks
{
    private const string Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string Body(string inner)=>"<office:document-content xmlns:office='"+Office+"' xmlns:text='"+Text+"' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' xmlns:draw='urn:oasis:names:tc:opendocument:xmlns:drawing:1.0' xmlns:xlink='http://www.w3.org/1999/xlink'><office:body><office:text>"+inner+"</office:text></office:body></office:document-content>";
        byte[] Package(string xml,string name="content.xml",string? duplicate=null)
        {
            using var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            {
                using(var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false)))writer.Write(xml);
                if(duplicate is not null)using(var writer=new StreamWriter(zip.CreateEntry(duplicate).Open()))writer.Write(xml);
                using(var writer=new StreamWriter(zip.CreateEntry("META-INF/manifest.xml").Open()))writer.Write("<!DOCTYPE x SYSTEM 'https://example.invalid/never-read'>");
            }return output.ToArray();
        }
        TextDocument Read(string inner)=>DocumentText.Read(new MemoryStream(Package(Body(inner))),"odt",default);
        foreach(var (xml,expected) in new[]{
            ("",""),("<text:p/>","\n"),("<text:h> Title </text:h><text:p> Hello 中文 😀 </text:p>","Title\nHello 中文 😀\n"),
            ("<text:p> A <text:span>  B </text:span><text:a xlink:href='https://example.invalid'> C</text:a> </text:p>","A B C\n"),
            ("<text:p> A <text:s text:c='2'/> B<text:line-break/> C<text:tab/> D </text:p>","A    B\n C\t D\n"),
            ("<text:p> <text:s text:c='2'/> </text:p>","  \n"),("<text:p><text:line-break/> B</text:p>","\n B\n"),
            ("<text:p><![CDATA[A  B]]> C</text:p>","A B C\n"),("<text:p><text:s></text:s><text:tab></text:tab><text:line-break></text:line-break></text:p>"," \t\n\n"),
            ("<text:list><text:list-item><text:p>one</text:p></text:list-item></text:list><table:table><table:table-row table:number-rows-repeated='1000000'><table:table-cell><text:p>two</text:p></table:table-cell></table:table-row></table:table>","one\ntwo\n"),
            ("<table:tracked-changes><table:deletion><text:p>deleted cell</text:p></table:deletion></table:tracked-changes><text:p>current cell</text:p>","current cell\n"),
            ("<text:p>A<text:bookmark><text:span>not marker text</text:span></text:bookmark>B</text:p>","AB\n"),
            ("<text:tracked-changes><text:p>deleted</text:p></text:tracked-changes><text:p>kept<text:change-start/> inserted<text:change-end/></text:p>","kept inserted\n"),
            ("<text:p>A<text:ruby><text:ruby-base>漢</text:ruby-base><text:ruby-text>かん</text:ruby-text></text:ruby>B</text:p>","A漢B\n")})
        {
            var result=Read(xml);check(result.Odt&&!result.Docx&&result.Encoding=="ODT body text"&&result.Text==expected,"ODT body/ODF whitespace/explicit symbols/limited structure: "+xml);
        }
        foreach(var excluded in new[]{"text:note","office:annotation","draw:frame","text:script","text:execute-macro","text:hidden-text","text:date","text:unknown"})
            check(Read("<text:p>keep <"+excluded+"><text:span>omitted</text:span></"+excluded+">end</text:p>").Text=="keep end\n","ODT excludes unhandled inline/objects rather than executing or guessing fields");
        foreach(string count in new[]{"0","-1","2147483648","100000000000000","1.5","1e4",""})
            reject(()=>Read("<text:p><text:s text:c='"+count+"'/></text:p>"),"ODT invalid count refuses without expansion");
        check(Read("<text:p><text:s text:c=' +2 '/></text:p>").Text=="  \n","Positive-integer count accepts XML lexical sign/whitespace");
        check(Read("<text:p><text:s text:c='1999999'/></text:p>").Text.Length==DocumentText.MaxCharacters,"ODT exact extracted budget includes paragraph LF");
        reject(()=>Read("<text:p><text:s text:c='2000000'/></text:p>"),"ODT final paragraph break included in extracted budget");
        reject(()=>Read("<text:p><text:s text:c='2000001'/></text:p>"),"ODT repeated spaces cannot exceed output budget");
        foreach(string bad in new[]{"<text:p><text:p>x</text:p></text:p>","<text:p><text:s>bad</text:s></text:p>","<text:p><text:tab><text:span/></text:tab></text:p>"})
            reject(()=>Read(bad),"ODT malformed nested paragraphs/symbol content refused");
        foreach(string xml in new[]{"<document/>",Body("").Replace("office:text","office:spreadsheet"),"<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///never'>]>"+Body("<text:p>&ext;</text:p>"),Body(new string('x',DocumentText.MaxCharacters))})
            reject(()=>DocumentText.Read(new MemoryStream(Package(xml)),"odt",default),"ODT malformed root/DTD/XML budget rejected without external reads");
        foreach(var names in new[]{("CONTENT.XML",(string?)null),("content.xml","content.xml"),("content.xml","CONTENT.XML")})
            reject(()=>DocumentText.Read(new MemoryStream(Package(Body(""),names.Item1,names.Item2)),"odt",default),"ODT duplicate/case-alias body rejected");
        reject(()=>DocumentText.Read(new MemoryStream(Package(Body(""))),"odt",new CancellationToken(true)),"ODT pre-cancelled before reads");
        var prefs=new Prefs();check(Catalog.Available(prefs,new[]{"document.ODT"}).Any(x=>x.Id=="document.text"),"ODT joins the existing explicit single-file action without a switch");
    }
}
