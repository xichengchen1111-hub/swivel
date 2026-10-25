using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

// New documents contain the extracted text only. Never copy original package
// relationships, properties, objects, styles, links or executable content.
internal static class DocumentTextPackages
{
    internal const string DocxLimit = "The plain DOCX copy exceeds its 2,000,000 XML character budget or contains characters XML cannot represent. Choose TXT, RTF or HTML instead.";
    private const string Word="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Encoding Utf8=new UTF8Encoding(false,true);
    private sealed class BoundedXmlText(CancellationToken token) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding=>Utf8;
        public override void Write(char value)
        { token.ThrowIfCancellationRequested();if(GetStringBuilder().Length>=DocumentText.MaxCharacters)throw new UserError(DocxLimit);GetStringBuilder().Append(value); }
        public override void Write(string? value) { if(value is not null)Write(value.AsSpan()); }
        public override void Write(char[] buffer,int index,int count)=>Write(buffer.AsSpan(index,count));
        public override void Write(ReadOnlySpan<char> value)
        { token.ThrowIfCancellationRequested();if(value.Length>DocumentText.MaxCharacters-GetStringBuilder().Length)throw new UserError(DocxLimit);GetStringBuilder().Append(value); }
    }
    internal static void WriteDocx(Stream output,string text,CancellationToken token)
    {
        using var serialized=new BoundedXmlText(token);
        try
        {
            // Preflight the complete serialized XML before touching output. The
            // UTF-16 budget includes escaping/markup, and implies <=6MiB UTF-8.
            using var xml=XmlWriter.Create(serialized,new XmlWriterSettings{CheckCharacters=true,CloseOutput=false,Indent=false});
            xml.WriteStartDocument();xml.WriteStartElement("w","document",Word);xml.WriteStartElement("w","body",Word);
            xml.WriteStartElement("w","p",Word);xml.WriteStartElement("w","r",Word);
            for(int at=0;at<text.Length;)
            {
                token.ThrowIfCancellationRequested();char c=text[at];
                if(c is '\r' or '\n' or '\t')
                {
                    xml.WriteStartElement("w",c=='\t'?"tab":"br",Word);xml.WriteEndElement();
                    at+=c=='\r'&&at+1<text.Length&&text[at+1]=='\n'?2:1;continue;
                }
                int end=at;
                while(end<text.Length&&end-at<8192&&text[end] is not '\r' and not '\n' and not '\t')end++;
                if(end<text.Length&&char.IsHighSurrogate(text[end-1]))end--;
                xml.WriteStartElement("w","t",Word);xml.WriteAttributeString("xml","space","http://www.w3.org/XML/1998/namespace","preserve");
                xml.WriteString(text.Substring(at,end-at));xml.WriteEndElement();at=end;
            }
            xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndDocument();xml.Flush();
        }
        catch(ArgumentException){throw new UserError(DocxLimit);}
        token.ThrowIfCancellationRequested();byte[] document=Utf8.GetBytes(serialized.ToString());
        if(document.Length>DocumentText.MaxXmlBytes)throw new UserError(DocxLimit);
        using var zip=new ZipArchive(output,ZipArchiveMode.Create,true);
        void Entry(string name,string content)=>Bytes(name,Utf8.GetBytes(content));
        void Bytes(string name,byte[] bytes)
        {
            token.ThrowIfCancellationRequested();var item=zip.CreateEntry(name,CompressionLevel.Optimal);item.LastWriteTime=new DateTimeOffset(1980,1,1,0,0,0,TimeSpan.Zero);
            using var stream=item.Open();
            for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int count=Math.Min(16384,bytes.Length-at);stream.Write(bytes,at,count);at+=count;}
            token.ThrowIfCancellationRequested();
        }
        Entry("[Content_Types].xml","<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Entry("_rels/.rels","<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Bytes("word/document.xml",document);token.ThrowIfCancellationRequested();
    }
    internal static void WriteHtml(Stream output,string text,CancellationToken token)
    {
        using var writer=new StreamWriter(output,Utf8,16384,true);
        writer.Write("<!doctype html><html lang=\"und\"><head><meta charset=\"utf-8\"><title>Text copy</title></head><body><pre style=\"white-space:pre-wrap;overflow-wrap:anywhere\"><span>");
        for(int at=0;at<text.Length;at++)
        {
            token.ThrowIfCancellationRequested();char c=text[at];
            // span prevents HTML's special removal of an initial LF after pre.
            if(c=='\r'){if(at+1<text.Length&&text[at+1]=='\n')at++;writer.Write('\n');}
            else
            {
                string? escaped=c switch{'&'=>"&amp;",'<'=>"&lt;",'>'=>"&gt;",'"'=>"&quot;",'\''=>"&#39;",_=>null};
                if(escaped is null)writer.Write(c);else writer.Write(escaped);
            }
        }
        writer.Write("</span></pre></body></html>");token.ThrowIfCancellationRequested();writer.Flush();token.ThrowIfCancellationRequested();
    }
}
