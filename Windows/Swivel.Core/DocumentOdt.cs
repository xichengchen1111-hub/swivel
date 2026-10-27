using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

// Bounded body-text inspection, never an Office renderer or package extractor.
internal static class DocumentOdt
{
    internal const string Invalid="Choose an ODT with one content.xml body. Limits: 8 MiB input/XML, 1,000 ZIP entries, 2,000,000 XML characters and extracted UTF-16 units, and XML depth 128. Encrypted documents, malformed XML and excessive space repetition are not supported.";
    private const string Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0",Table="urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    internal static string Read(byte[] bytes,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();using var source=new MemoryStream(bytes,false);using var zip=new ZipArchive(source,ZipArchiveMode.Read,false);
            if(zip.Entries.Count>1000||zip.Entries.Sum(x=>(long)x.FullName.Length)>256*1024)throw new UserError(Invalid);
            var entries=zip.Entries.Where(x=>x.FullName.Equals("content.xml",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(entries.Length!=1||entries[0].FullName!="content.xml"||entries[0].Length>DocumentText.MaxXmlBytes)throw new UserError(Invalid);
            using var content=entries[0].Open();using var buffer=new MemoryStream();byte[] page=new byte[16384];
            for(;;)
            {
                token.ThrowIfCancellationRequested();int count=content.Read(page,0,(int)Math.Min(page.Length,DocumentText.MaxXmlBytes+1L-buffer.Length));
                if(count==0)break;buffer.Write(page,0,count);if(buffer.Length>DocumentText.MaxXmlBytes)throw new UserError(Invalid);
            }
            buffer.Position=0;
            using var reader=XmlReader.Create(buffer,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=DocumentText.MaxCharacters,MaxCharactersFromEntities=1024,IgnoreComments=true,IgnoreProcessingInstructions=true});
            var result=new StringBuilder();bool root=false,seenOfficeBody=false,insideOfficeBody=false,seenText=false,active=false,hasContent=false,pendingSpace=false;
            int excluded=-1,paragraph=-1;
            void Append(char value,int count=1)
            {
                token.ThrowIfCancellationRequested();if(count>DocumentText.MaxCharacters-result.Length)throw new UserError(Invalid);result.Append(value,count);
            }
            void FlushSpace(){if(pendingSpace&&hasContent)Append(' ');pendingSpace=false;}
            void Symbol(char value,int count=1){FlushSpace();Append(value,count);hasContent=true;}
            void Ordinary(string value)
            {
                foreach(char c in value)
                {
                    token.ThrowIfCancellationRequested();
                    if(c is ' ' or '\t' or '\r' or '\n'){pendingSpace=hasContent;continue;}
                    FlushSpace();Append(c);hasContent=true;
                }
            }
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();if(reader.Depth>128)throw new UserError(Invalid);
                bool element=reader.NodeType==XmlNodeType.Element,end=reader.NodeType==XmlNodeType.EndElement;
                string ns=reader.NamespaceURI,name=reader.LocalName;
                if(element&&reader.Depth==0)
                {if(root||ns!=Office||name!="document-content")throw new UserError(Invalid);root=true;}
                if(element&&reader.Depth==1&&ns==Office&&name=="body")
                {if(seenOfficeBody)throw new UserError(Invalid);seenOfficeBody=true;insideOfficeBody=!reader.IsEmptyElement;continue;}
                if(end&&reader.Depth==1&&ns==Office&&name=="body"){insideOfficeBody=false;continue;}
                if(element&&insideOfficeBody&&reader.Depth==2&&ns==Office&&name=="text")
                {if(seenText)throw new UserError(Invalid);seenText=true;active=!reader.IsEmptyElement;continue;}
                if(end&&reader.Depth==2&&ns==Office&&name=="text"){active=false;continue;}
                if(!active)continue;
                if(excluded>=0){if(end&&reader.Depth==excluded)excluded=-1;continue;}
                if(element)
                {
                    bool omit=ns==Table&&name=="tracked-changes" || ns!=Text&&ns!=Table || ns==Text&&name is "note" or "tracked-changes" or "variable-decls" or "user-field-decls" or "sequence-decls" or "script" or "execute-macro" or "ruby-text" or "hidden-text" or "hidden-paragraph";
                    // Inline containers are intentionally closed. Stored field,
                    // unknown inline and foreign content is omitted, never run.
                    if(paragraph>=0&&!(ns==Text&&name is "span" or "a" or "ruby" or "ruby-base" or "meta" or "s" or "tab" or "line-break" or "p" or "h" or "soft-page-break" or "bookmark" or "bookmark-start" or "bookmark-end" or "reference-mark" or "reference-mark-start" or "reference-mark-end" or "change" or "change-start" or "change-end"))omit=true;
                    if(omit){if(!reader.IsEmptyElement)excluded=reader.Depth;continue;}
                    if(ns==Text&&name is "p" or "h")
                    {
                        if(paragraph>=0)throw new UserError(Invalid);hasContent=pendingSpace=false;
                        if(reader.IsEmptyElement)Append('\n');else paragraph=reader.Depth;continue;
                    }
                    if(paragraph>=0&&ns==Text&&name is "soft-page-break" or "bookmark" or "bookmark-start" or "bookmark-end" or "reference-mark" or "reference-mark-start" or "reference-mark-end" or "change" or "change-start" or "change-end")
                    {if(!reader.IsEmptyElement)excluded=reader.Depth;continue;}
                    if(paragraph>=0&&ns==Text&&name is "s" or "tab" or "line-break")
                    {
                        int count=1;
                        if(name=="s")
                        {
                            string? raw=reader.GetAttribute("c",Text);
                            if(raw is not null&&(!int.TryParse(raw.Trim(),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out count)||count<1||count>DocumentText.MaxCharacters))throw new UserError(Invalid);
                        }
                        EmptySymbol(reader,token);Symbol(name=="s"?' ':name=="tab"?'\t':'\n',count);continue;
                    }
                }
                else if(end&&reader.Depth==paragraph)
                {Append('\n');paragraph=-1;hasContent=pendingSpace=false;}
                else if(paragraph>=0&&reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)Ordinary(reader.Value);
            }
            if(!root||!seenOfficeBody||!seenText)throw new UserError(Invalid);
            string value=result.ToString();DocumentText.Validate(value,token);return value;
        }
        catch(Exception error)when(error is InvalidDataException or XmlException or NotSupportedException or OverflowException or ArgumentException){throw new UserError(Invalid);}
    }
    private static void EmptySymbol(XmlReader reader,CancellationToken token)
    {
        if(reader.IsEmptyElement)return;
        using var subtree=reader.ReadSubtree();subtree.Read();
        while(subtree.Read())
        {
            token.ThrowIfCancellationRequested();
            if(subtree.NodeType is not XmlNodeType.EndElement and not XmlNodeType.Whitespace and not XmlNodeType.SignificantWhitespace)throw new UserError(Invalid);
        }
    }
}
