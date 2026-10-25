using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class DocumentRtfChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        TextDocument Read(string rtf)=>DocumentText.Read(new MemoryStream(Encoding.Latin1.GetBytes(rtf)),"rtf",default);
        TextDocument Body(string body)=>Read(@"{\rtf1\ansi "+body+"}");
        foreach(var (body,expected) in new[]{
            ("",""),(@"\uc1 Lab\u915Gvalue","LabΓvalue"),("A\r\nB\tC","AB\tC"),(@"a\par b\line c\tab d\cell e\row f\page g\sect h","a\nb\nc\td\te\nf\ng\nh"),
            (@"\{x\}\\","{x}\\"),(@"\'80\'e9","€é"),(@"\uc1\u20013?\u25991?","中文"),
            (@"\uc0\u55357\u56832","😀"),(@"\u-10179?\u-8704?","😀"),
            (@"\uc2\u65?\par B","AB"),(@"\uc1\u65\bin3 {}\B","AB"),
            (@"\uc2\u65?{B}C","ABC"),(@"\uc2\u65?\{B","AB"),
            (@"\uc1\u65?{\uc0\u66}C\u68?","ABCD"),
            (@"a{\b bold}z","aboldz"),(@"a{\v secret}b{\deleted old}c\v x\v0 d","abcd"),
            (@"a{\v hidden\plain visible}b","avisibleb"),
            (@"a{\*\unknown ignore{nested}x}b","ab"),
            (@"a{\field{\*\fldinst INCLUDETEXT bad}{\fldrslt omit}}b","ab"),
            (@"a{\pict\bin4 {}\\}b","ab"),(@"{\fonttbl{\f0\fcharset128 ignored}}\uc1\u20013?","中"),
            (@"\ansicpg936\u20013\'d6\u25991\'ce","中文"),
            (@"\ansicpg936 ASCII","ASCII"),(@"\ansi \emdash\endash\bullet\lquote\rquote\ldblquote\rdblquote","—–•‘’“”"),
            ("a\\\nb\\\r\nc","a\nb\nc"),(@"\uc1\u65\uc0\u66?","AB"),
            (@"\ansicpg936{\v "+(char)0xe9+"}ASCII","ASCII"),
            (@"{\fonttbl{\f0\fcharset128 ignored}}\uc1\u20013\'d6","中")})
        {
            var doc=Body(body);check(doc.Rtf&&!doc.Docx&&!doc.Odt&&doc.Encoding=="RTF body text"&&doc.Text==expected,"RTF bounded body/control/Unicode/fallback case: "+body);
        }
        foreach(string destination in new[]{"fonttbl","colortbl","stylesheet","info","object","header","footer","footnote","annotation","listtext","pntext","shp","shppict"})
            check(Body("a{\\"+destination+" discarded}b").Text=="ab","RTF omitted resource destination: "+destination);
        foreach(string rtf in new[]{"",@"{\rtf10 x}",@"{\rtf1 x",@"{\rtf1 x}}",@"{\rtf1 x}body",@"{\rtf1}{\rtf1}",@"{\rtf1\bin-1 x}",@"{\rtf1\bin99 x}",@"{\rtf1\bin x}",@"{\rtf1\u65536?}",@"{\rtf1\u-32769?}",@"{\rtf1\u55296?}",@"{\rtf1\u0?}",@"{\rtf1\uc17}",@"{\rtf1\uc-1}",@"{\rtf1\'gg}",@"{\rtf1\'a}",@"{\rtf1\u?}",@"{\rtf1\upr{a}{b}}",@"{\rtf1\foo- x}","{\\rtf1\\"+new string('a',33)+" x}"})
            reject(()=>Read(rtf),"RTF malformed syntax, Unicode, bounds or unsupported alternative refused");
        foreach(string body in new[]{@"\ansicpg936 "+(char)0xe9,@"\mac "+(char)0xe9,@"\pc "+(char)0xe9,@"\pca "+(char)0xe9,@"{\fonttbl{\f0\fcharset2 Symbol;}}\'e9",@"\'e9{\fonttbl{\f0\fcharset128;}}",@"\'e9\ansicpg936",@"\ansicpg936\ansi "+(char)0xe9})
            reject(()=>Body(body),"RTF unknown byte encoding not guessed regardless of declaration order");
        check(Body(new string('{',127)+"x"+new string('}',127)).Text=="x","RTF exact group depth 128");
        reject(()=>Body(new string('{',128)+"x"+new string('}',128)),"RTF group overflow refused");
        check(Body("\\"+new string('a',32)+" ").Text=="","RTF exact 32-letter unknown control");
        check(Body(@"\unknown"+new string('9',100_000)+" x").Text=="x","RTF unknown numeric parameter bounded scan without integer parsing");
        check(Body(new string('x',DocumentText.MaxCharacters)).Text.Length==DocumentText.MaxCharacters,"RTF exact output budget");
        reject(()=>Body(new string('x',DocumentText.MaxCharacters+1)),"RTF output overflow never truncated");
        for(int i=0;i<128;i++)
        {
            char c=(char)(0x400+i);string source="A"+c+"😀\tB\r\nC";
            using var saved=new MemoryStream();DocumentText.Write(saved,source,TextExport.Rtf,default);
            check(DocumentText.Read(new MemoryStream(saved.ToArray()),"RTF",default).Text==source.Replace("\r\n","\n"),"RTF original writer Unicode roundtrip "+i);
        }
        reject(()=>DocumentText.Read(new MemoryStream(Encoding.ASCII.GetBytes(@"{\rtf1 x}")),"rtf",new CancellationToken(true)),"RTF pre-cancelled before read");
        check(Catalog.Available(new Prefs(),new[]{"document.RTF"}).Any(x=>x.Id=="document.text"),"RTF explicit catalog action without new setting");
    }
}
