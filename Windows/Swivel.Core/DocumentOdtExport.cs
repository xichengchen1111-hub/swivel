using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

internal static class DocumentOdtExport
{
    internal const string Invalid="The plain ODT copy exceeds its 2,000,000 XML/output character budget or contains characters XML cannot represent. Choose TXT, RTF or HTML instead.";
    private const string Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0",Mime="application/vnd.oasis.opendocument.text";
    private static readonly Encoding Utf8=new UTF8Encoding(false,true);
    private sealed class BoundedText(CancellationToken token) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding=>Utf8;
        public override void Write(char c){token.ThrowIfCancellationRequested();if(GetStringBuilder().Length>=DocumentText.MaxCharacters)throw new UserError(Invalid);GetStringBuilder().Append(c);}
        public override void Write(string? value){if(value is not null)Write(value.AsSpan());}
        public override void Write(char[] value,int index,int count)=>Write(value.AsSpan(index,count));
        public override void Write(ReadOnlySpan<char> value){token.ThrowIfCancellationRequested();if(value.Length>DocumentText.MaxCharacters-GetStringBuilder().Length)throw new UserError(Invalid);GetStringBuilder().Append(value);}
    }
    internal static void Write(Stream output,string text,CancellationToken token)
    {
        // NativeFiles supplies an empty seekable staging FileStream. Requiring
        // it here also fixes the ODF first-header MIME position/CRC fields.
        if(!output.CanSeek||output.Position!=0||output.Length!=0)throw new ArgumentException("ODT output must be a new seekable stream.",nameof(output));
        token.ThrowIfCancellationRequested();string normalized=text.Replace("\r\n","\n").Replace('\r','\n');
        if(normalized.Length>=DocumentText.MaxCharacters)throw new UserError(Invalid); // final paragraph LF
        using var serialized=new BoundedText(token);
        try
        {
            using var xml=XmlWriter.Create(serialized,new XmlWriterSettings{CheckCharacters=true,CloseOutput=false,Indent=false});
            xml.WriteStartDocument();xml.WriteStartElement("office","document-content",Office);xml.WriteAttributeString("office","version",Office,"1.3");
            xml.WriteAttributeString("xmlns","text",null,Text);xml.WriteStartElement("office","body",Office);xml.WriteStartElement("office","text",Office);xml.WriteStartElement("text","p",Text);
            for(int at=0;at<normalized.Length;)
            {
                token.ThrowIfCancellationRequested();char c=normalized[at];
                if(c is ' ' or '\t' or '\n')
                {
                    int end=at+1;if(c==' ')while(end<normalized.Length&&normalized[end]==' ')end++;
                    xml.WriteStartElement("text",c==' '?"s":c=='\t'?"tab":"line-break",Text);
                    if(c==' '&&end-at>1)xml.WriteAttributeString("text","c",Text,(end-at).ToString(CultureInfo.InvariantCulture));
                    xml.WriteEndElement();at=end;continue;
                }
                int stop=at;while(stop<normalized.Length&&stop-at<8192&&normalized[stop] is not ' ' and not '\t' and not '\n')stop++;
                if(stop<normalized.Length&&char.IsHighSurrogate(normalized[stop-1]))stop--;
                xml.WriteString(normalized.Substring(at,stop-at));at=stop;
            }
            xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndElement();xml.WriteEndDocument();xml.Flush();
        }
        catch(ArgumentException){throw new UserError(Invalid);}
        token.ThrowIfCancellationRequested();byte[] document=Utf8.GetBytes(serialized.ToString());if(document.Length>DocumentText.MaxXmlBytes)throw new UserError(Invalid);
        using var zip=new ZipArchive(output,ZipArchiveMode.Create,true);
        void Entry(string name,byte[] bytes,CompressionLevel compression)
        {
            token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(name,compression);entry.LastWriteTime=new DateTimeOffset(1980,1,1,0,0,0,TimeSpan.Zero);
            using var stream=entry.Open();for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int count=Math.Min(16384,bytes.Length-at);stream.Write(bytes,at,count);at+=count;}token.ThrowIfCancellationRequested();
        }
        Entry("mimetype",Encoding.ASCII.GetBytes(Mime),CompressionLevel.NoCompression);
        Entry("META-INF/manifest.xml",Utf8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\""+Mime+"\" manifest:version=\"1.3\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>"),CompressionLevel.Optimal);
        Entry("content.xml",document,CompressionLevel.Optimal);token.ThrowIfCancellationRequested();
    }
}
