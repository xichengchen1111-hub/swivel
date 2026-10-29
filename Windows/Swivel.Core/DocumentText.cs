using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

public sealed record TextDocument(string Text,string Encoding,bool Docx,bool Odt=false,bool Rtf=false,bool Html=false,bool Epub=false,bool Doc=false);
public sealed record TextStatistics(int Words,int TextElements,int Scalars,int Lines,int Utf8Bytes);
public enum TextExport { Utf8, Rtf, Docx, Html, Odt }

// Explicit, bounded text decoding. No system document sniffing, extraction,
// browser/Office renderer, attachment loading or external resource resolution.
public static class DocumentText
{
    public const int MaxInputBytes=8*1024*1024, MaxCharacters=2_000_000, MaxXmlBytes=8*1024*1024;
    public static readonly string[] Extensions={"txt","text","md","markdown","csv","tsv","log","srt","docx","odt","rtf","html","htm","epub","doc"};
    private const string Invalid="Choose a valid UTF-8 text file, BOM-marked UTF-16 text file, DOC, DOCX, ODT, RTF, EPUB or supported static HTML document. At most 8 MiB input and 2,000,000 UTF-16 units of extracted text are supported.";
    private static readonly Encoding Utf8=new UTF8Encoding(false,true), Little=new UnicodeEncoding(false,false,true), Big=new UnicodeEncoding(true,false,true);
    public static TextDocument Read(Stream input,string extension,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); token.ThrowIfCancellationRequested();
        if(!Extensions.Contains(extension,StringComparer.OrdinalIgnoreCase))throw new UserError(Invalid);
        byte[] bytes=ReadBounded(input,MaxInputBytes,token);
        if(extension.Equals("docx",StringComparison.OrdinalIgnoreCase))return new(ReadDocx(bytes,token),"DOCX body text",true);
        if(extension.Equals("odt",StringComparison.OrdinalIgnoreCase))return new(DocumentOdt.Read(bytes,token),"ODT body text",false,true);
        if(extension.Equals("rtf",StringComparison.OrdinalIgnoreCase))return new(DocumentRtf.Read(bytes,token),"RTF body text",false,false,true);
        if(extension.Equals("doc",StringComparison.OrdinalIgnoreCase))
        {
            // Word files are often saved as RTF or DOCX under a .doc name.
            if(bytes.AsSpan().StartsWith("{\\rtf"u8))return new(DocumentRtf.Read(bytes,token),"RTF body text",false,false,true);
            if(bytes.AsSpan().StartsWith("PK\u0003\u0004"u8))return new(ReadDocx(bytes,token),"DOCX body text",true);
            return new(DocumentDoc.Read(bytes,token),"Word 97-2003 main document text",false,Doc:true);
        }
        if(extension.Equals("epub",StringComparison.OrdinalIgnoreCase))return new(DocumentEpub.Read(bytes,token),"EPUB spine body text",false,Epub:true);
        if(extension.Equals("html",StringComparison.OrdinalIgnoreCase)||extension.Equals("htm",StringComparison.OrdinalIgnoreCase))return new(DocumentHtml.Read(bytes,token),"HTML source body text",false,Html:true);
        Encoding encoding=Utf8; int skip=0; string label="UTF-8";
        if(bytes.Length>=4 && (bytes.AsSpan(0,4).SequenceEqual(new byte[]{0xff,0xfe,0,0})||bytes.AsSpan(0,4).SequenceEqual(new byte[]{0,0,0xfe,0xff})))throw new UserError(Invalid);
        if(bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf){skip=3;label="UTF-8 BOM";}
        else if(bytes.Length>=2&&bytes[0]==0xff&&bytes[1]==0xfe){encoding=Little;skip=2;label="UTF-16 LE BOM";}
        else if(bytes.Length>=2&&bytes[0]==0xfe&&bytes[1]==0xff){encoding=Big;skip=2;label="UTF-16 BE BOM";}
        string text;
        try {text=encoding.GetString(bytes,skip,bytes.Length-skip);}catch(DecoderFallbackException){throw new UserError(Invalid);}
        Validate(text,token);return new(text,label,false);
    }
    private static byte[] ReadBounded(Stream input,int limit,CancellationToken token)
    {
        using var buffer=new MemoryStream(); byte[] page=new byte[16*1024];
        for(;;){token.ThrowIfCancellationRequested();int length=input.Read(page,0,(int)Math.Min(page.Length,limit+1L-buffer.Length));if(length==0)break;buffer.Write(page,0,length);if(buffer.Length>limit)throw new UserError(Invalid);}
        token.ThrowIfCancellationRequested();return buffer.ToArray();
    }
    private static bool WordNamespace(string value)=>value is "http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private static string ReadDocx(byte[] bytes,CancellationToken token)
    {
        try
        {
            using var source=new MemoryStream(bytes,false);using var zip=new ZipArchive(source,ZipArchiveMode.Read,false);
            if(zip.Entries.Count>1000||zip.Entries.Sum(x=>(long)x.FullName.Length)>256*1024)throw new UserError(Invalid);
            var entries=zip.Entries.Where(x=>x.FullName.Equals("word/document.xml",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(entries.Length!=1||entries[0].FullName!="word/document.xml"||entries[0].Length>MaxXmlBytes)throw new UserError(Invalid);
            using var content=entries[0].Open();byte[] xml=ReadBounded(content,MaxXmlBytes,token);
            var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxCharacters,MaxCharactersFromEntities=1024,IgnoreComments=true,IgnoreProcessingInstructions=true};
            using var data=new MemoryStream(xml,false);using var reader=XmlReader.Create(data,settings);
            var text=new StringBuilder();bool root=false,body=false,seenBody=false;int bodyDepth=-1,excluded=-1;
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();if(reader.Depth>128)throw new UserError(Invalid);
                if(reader.NodeType==XmlNodeType.Element&&reader.Depth==0)
                {if(root||reader.LocalName!="document"||!WordNamespace(reader.NamespaceURI))throw new UserError(Invalid);root=true;}
                if(reader.NodeType==XmlNodeType.Element&&WordNamespace(reader.NamespaceURI)&&reader.LocalName=="body"&&reader.Depth==1)
                {if(seenBody)throw new UserError(Invalid);seenBody=true;body=!reader.IsEmptyElement;bodyDepth=reader.Depth;continue;}
                if(!body)continue;
                if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==bodyDepth){body=false;continue;}
                if(excluded>=0)
                {if(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==excluded)excluded=-1;continue;}
                if(!WordNamespace(reader.NamespaceURI))continue;
                if(reader.NodeType==XmlNodeType.Element && reader.LocalName is "drawing" or "pict" or "object" or "del" or "moveFrom")
                {if(!reader.IsEmptyElement)excluded=reader.Depth;continue;}
                if(reader.NodeType==XmlNodeType.Element)
                {
                    if(reader.LocalName=="t")
                    {
                        // ReadSubtree avoids advancing past the next sibling. XML
                        // remains parsed with DTD/character/depth budgets above.
                        using var part=reader.ReadSubtree();part.Read();
                        if(!part.IsEmptyElement)
                        {
                            while(part.Read())
                            {
                                token.ThrowIfCancellationRequested();
                                if(part.NodeType==XmlNodeType.Element)throw new UserError(Invalid);
                                if(part.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)text.Append(part.Value);
                                if(text.Length>MaxCharacters)throw new UserError(Invalid);
                            }
                        }
                    }
                    else if(reader.LocalName=="tab")text.Append('\t');
                    else if(reader.LocalName is "br" or "cr")text.Append('\n');
                    else if(reader.LocalName=="p"&&reader.IsEmptyElement)text.Append('\n');
                }
                else if(reader.NodeType==XmlNodeType.EndElement&&reader.LocalName=="p")text.Append('\n');
                if(text.Length>MaxCharacters)throw new UserError(Invalid);
            }
            if(!root||!seenBody)throw new UserError(Invalid);
            string result=text.ToString();Validate(result,token);return result;
        }
        catch(Exception error)when(error is InvalidDataException or XmlException or NotSupportedException or OverflowException or ArgumentException){throw new UserError(Invalid);}
    }
    public static void Validate(string text,CancellationToken token)
    {
        if(text.Length>MaxCharacters)throw new UserError(Invalid);
        for(int at=0;at<text.Length;)
        {
            token.ThrowIfCancellationRequested();
            if(Rune.DecodeFromUtf16(text.AsSpan(at),out Rune rune,out int used)!=OperationStatus.Done||Rune.IsControl(rune)&&rune.Value is not 9 and not 10 and not 13)throw new UserError(Invalid);
            at+=used;
        }
        token.ThrowIfCancellationRequested();
    }
    public static TextStatistics Count(string text,CancellationToken token)
    {
        Validate(text,token);int words=0,scalars=0,lines=text.Length==0?0:1;bool word=false;
        for(int at=0;at<text.Length;)
        {
            token.ThrowIfCancellationRequested();Rune.DecodeFromUtf16(text.AsSpan(at),out Rune rune,out int used);scalars++;
            bool space=Rune.IsWhiteSpace(rune);if(!space&&!word)words++;word=!space;
            if(rune.Value==13||rune.Value==10&&(at==0||text[at-1]!='\r')||rune.Value is 0x85 or 0x2028 or 0x2029)lines++;
            at+=used;
        }
        int characters=0;var elements=StringInfo.GetTextElementEnumerator(text);while(elements.MoveNext()){token.ThrowIfCancellationRequested();characters++;}
        token.ThrowIfCancellationRequested();return new(words,characters,scalars,lines,Utf8.GetByteCount(text));
    }
    public static void Write(Stream output,string text,TextExport format,CancellationToken token)
    {
        Validate(text,token);if(!Enum.IsDefined(format))throw new ArgumentOutOfRangeException(nameof(format));
        if(format==TextExport.Odt){DocumentOdtExport.Write(output,text,token);return;}
        if(format==TextExport.Docx){DocumentTextPackages.WriteDocx(output,text,token);return;}
        if(format==TextExport.Html){DocumentTextPackages.WriteHtml(output,text,token);return;}
        using var writer=new StreamWriter(output,format==TextExport.Utf8?Utf8:Encoding.ASCII,16384,true);
        if(format==TextExport.Utf8)
        {
            for(int at=0;at<text.Length;){token.ThrowIfCancellationRequested();int count=Math.Min(8192,text.Length-at);writer.Write(text.AsSpan(at,count));at+=count;}
        }
        else
        {
            writer.Write(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Segoe UI;}}\uc1\f0\fs22 ");
            for(int at=0;at<text.Length;at++)
            {
                token.ThrowIfCancellationRequested();char c=text[at];
                if(c=='\r'||c=='\n'){if(c=='\r'&&at+1<text.Length&&text[at+1]=='\n')at++;writer.Write(@"\par ");}
                else if(c=='\t')writer.Write(@"\tab ");
                else if(c is '\\' or '{' or '}'){writer.Write('\\');writer.Write(c);}
                else if(c is >= ' ' and <= '~')writer.Write(c);
                else{writer.Write(@"\u");writer.Write(unchecked((short)c).ToString(CultureInfo.InvariantCulture));writer.Write('?');}
            }
            writer.Write('}');
        }
        token.ThrowIfCancellationRequested();writer.Flush();token.ThrowIfCancellationRequested();
    }
}
