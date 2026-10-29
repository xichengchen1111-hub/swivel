using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace Swivel.Core;

// Bounded EPUB 2/3 spine text inspection, never a reading system, renderer or
// package extractor. Only caller-supplied bytes are parsed in memory; no
// stylesheet, script, font, image, link or remote resource is loaded.
public static class DocumentEpub
{
    public const int MaxEntries=1000, MaxNameUnits=256*1024, MaxPartBytes=8*1024*1024, MaxTotalXmlBytes=32*1024*1024, MaxDepth=128;
    public const string Invalid="Choose an EPUB 2 or 3 book with mimetype application/epub+zip, META-INF/container.xml, one OPF package, and a spine of XHTML documents that exist in the book. Paths outside the book, duplicate or case-alias entry names, malformed XML and named entities such as &nbsp; that need a DTD are not supported.";
    public const string Limit="EPUB is limited to 8 MiB input, 1,000 ZIP entries, 8 MiB per XML part, 32 MiB of XML in total, 2,000,000 XML characters per part and extracted UTF-16 units overall, and XML depth 128.";
    public const string Encrypted="This EPUB is encrypted or rights-managed, so its text cannot be read. Only books without encryption, or with obfuscated fonts only, are supported.";
    public const string Unsupported="This EPUB spine uses content other than XHTML documents or images. Only XHTML spine documents are read; images contribute no text.";
    private const string ContainerNs="urn:oasis:names:tc:opendocument:xmlns:container", OpfNs="http://www.idpf.org/2007/opf", Xhtml="http://www.w3.org/1999/xhtml", EncNs="http://www.w3.org/2001/04/xmlenc#";
    private static readonly HashSet<string> FontObfuscation=new(StringComparer.Ordinal){"http://www.idpf.org/2008/embedding","http://ns.adobe.com/pdf/enc#RC"};
    private static readonly HashSet<string> Blocks=Set("address article aside blockquote caption dd details dialog div dl dt fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 header hgroup hr legend li main menu nav ol p pre section summary table tbody td tfoot th thead tr ul");
    private static readonly HashSet<string> Omitted=Set("head style title template noscript object iframe canvas audio video select textarea script button rt rp");
    private static readonly Regex Declaration=new("^<\\?xml\\s[^>]*?encoding\\s*=\\s*[\"']([^\"']*)[\"']",RegexOptions.CultureInvariant);
    private static readonly Regex Scheme=new("^[A-Za-z][A-Za-z0-9+.-]*:",RegexOptions.CultureInvariant);
    private static HashSet<string> Set(string words)=>new(words.Split(' '),StringComparer.Ordinal);

    public static string Read(byte[] bytes,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(bytes.Length>DocumentText.MaxInputBytes)throw new UserError(Limit);
        try
        {
            using var source=new MemoryStream(bytes,false);using var zip=new ZipArchive(source,ZipArchiveMode.Read,false);
            if(zip.Entries.Count>MaxEntries||zip.Entries.Sum(x=>(long)x.FullName.Length)>MaxNameUnits)throw new UserError(Limit);
            var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);var folded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                // Duplicate and case/backslash aliases are refused for the whole
                // archive, so no reader can be steered to a different twin.
                if(!folded.Add(entry.FullName.Replace('\\','/')))throw new UserError(Invalid);
                entries[entry.FullName]=entry;
            }
            long total=0;
            byte[] Part(string name,int limit)
            {
                if(!entries.TryGetValue(name,out var entry))throw new UserError(Invalid);
                if(entry.Length>limit)throw new UserError(Limit);
                using var content=entry.Open();using var buffer=new MemoryStream();byte[] page=new byte[16384];
                for(;;)
                {
                    token.ThrowIfCancellationRequested();int count=content.Read(page,0,(int)Math.Min(page.Length,limit+1L-buffer.Length));
                    if(count==0)break;buffer.Write(page,0,count);if(buffer.Length>limit)throw new UserError(Limit);
                    if((total+=count)>MaxTotalXmlBytes)throw new UserError(Limit);
                }
                return buffer.ToArray();
            }
            if(!Part("mimetype",64).AsSpan().SequenceEqual("application/epub+zip"u8))throw new UserError(Invalid);
            if(entries.ContainsKey("META-INF/rights.xml"))throw new UserError(Encrypted);
            var encrypted=entries.ContainsKey("META-INF/encryption.xml")?EncryptedPaths(Part("META-INF/encryption.xml",MaxPartBytes),token):new HashSet<string>(StringComparer.Ordinal);
            string opf=Rootfile(Part("META-INF/container.xml",MaxPartBytes),token);
            if(encrypted.Contains(opf))throw new UserError(Encrypted);
            var spine=Spine(Part(opf,MaxPartBytes),Folder(opf),token);
            var output=new StringBuilder();
            foreach(var (path,mediaType) in spine)
            {
                token.ThrowIfCancellationRequested();
                if(encrypted.Contains(path))throw new UserError(Encrypted);
                if(!entries.ContainsKey(path))throw new UserError(Invalid);
                if(mediaType.StartsWith("image/",StringComparison.OrdinalIgnoreCase))continue;
                if(!mediaType.Equals("application/xhtml+xml",StringComparison.OrdinalIgnoreCase))throw new UserError(Unsupported);
                if(output.Length>0&&output[^1]!='\n'){if(output.Length>=DocumentText.MaxCharacters)throw new UserError(Limit);output.Append('\n');}
                Body(Part(path,MaxPartBytes),output,token);
            }
            string result=output.ToString();DocumentText.Validate(result,token);return result;
        }
        catch(Exception error)when(error is InvalidDataException or NotSupportedException or OverflowException or ArgumentException){throw new UserError(Invalid);}
    }

    private static readonly Encoding Utf8=new UTF8Encoding(false,true), Little=new UnicodeEncoding(false,false,true), Big=new UnicodeEncoding(true,false,true);
    // OCF requires UTF-8 or UTF-16 XML. Bytes are decoded strictly here, so a
    // declaration naming a legacy encoding can never switch the decoder.
    private static XmlReader Xml(byte[] bytes,bool ignoreDoctype)
    {
        string text;
        try
        {
            if(bytes.Length>=2&&bytes[0]==0xff&&bytes[1]==0xfe)text=Little.GetString(bytes,2,bytes.Length-2);
            else if(bytes.Length>=2&&bytes[0]==0xfe&&bytes[1]==0xff)text=Big.GetString(bytes,2,bytes.Length-2);
            else{int skip=bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf})?3:0;text=Utf8.GetString(bytes,skip,bytes.Length-skip);}
        }
        catch(DecoderFallbackException){throw new UserError(Invalid);}
        if(text.Length>DocumentText.MaxCharacters)throw new UserError(Limit);
        var declared=Declaration.Match(text);
        if(declared.Success&&!(declared.Groups[1].Value.Equals("utf-8",StringComparison.OrdinalIgnoreCase)||declared.Groups[1].Value.Equals("utf-16",StringComparison.OrdinalIgnoreCase)))throw new UserError(Invalid);
        return XmlReader.Create(new StringReader(text),new XmlReaderSettings
        {
            // EPUB 2 XHTML often carries an XHTML 1.1 doctype. Ignore skips it
            // entirely: nothing is resolved, and entities it would declare stay
            // undefined, so they fail instead of expanding.
            DtdProcessing=ignoreDoctype?DtdProcessing.Ignore:DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=DocumentText.MaxCharacters,
            MaxCharactersFromEntities=1024,IgnoreComments=true,IgnoreProcessingInstructions=true,CloseInput=true
        });
    }
    private static void Depth(XmlReader reader){if(reader.Depth>MaxDepth)throw new UserError(Limit);}

    // Paths are case-sensitive container paths. Percent escapes decode once;
    // schemes, queries, fragments, absolute or escaping paths are refused.
    public static string Resolve(string directory,string href)
    {
        if(href.Length==0||href.Length>4096||href.IndexOfAny(new[]{'\\','?','#'})>=0||href[0]=='/'||Scheme.IsMatch(href))throw new UserError(Invalid);
        string decoded=Uri.UnescapeDataString(href);
        if(decoded.Any(c=>c=='\\'||char.IsControl(c))||decoded.Length==0)throw new UserError(Invalid);
        var parts=new List<string>(directory.Length==0?Array.Empty<string>():directory.Split('/'));
        foreach(string segment in decoded.Split('/'))
        {
            if(segment.Length==0)throw new UserError(Invalid);
            if(segment==".")continue;
            if(segment==".."){if(parts.Count==0)throw new UserError(Invalid);parts.RemoveAt(parts.Count-1);continue;}
            parts.Add(segment);
        }
        if(parts.Count==0)throw new UserError(Invalid);return string.Join('/',parts);
    }
    private static string Folder(string path){int slash=path.LastIndexOf('/');return slash<0?"":path[..slash];}

    private static string Rootfile(byte[] xml,CancellationToken token)
    {
        try
        {
            using var reader=Xml(xml,false);bool root=false;
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();Depth(reader);if(reader.NodeType!=XmlNodeType.Element)continue;
                if(reader.Depth==0){if(root||reader.LocalName!="container"||reader.NamespaceURI!=ContainerNs)throw new UserError(Invalid);root=true;continue;}
                if(reader.Depth==2&&reader.LocalName=="rootfile"&&reader.NamespaceURI==ContainerNs&&reader.GetAttribute("media-type")=="application/oebps-package+xml")
                    return Resolve("",reader.GetAttribute("full-path")??"");
            }
            throw new UserError(Invalid);
        }
        catch(XmlException){throw new UserError(Invalid);}
    }

    private static List<(string Path,string MediaType)> Spine(byte[] xml,string directory,CancellationToken token)
    {
        try
        {
            using var reader=Xml(xml,false);bool root=false,manifest=false,spine=false;
            var items=new Dictionary<string,(string Path,string MediaType)>(StringComparer.Ordinal);var order=new List<string>();var used=new HashSet<string>(StringComparer.Ordinal);
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();Depth(reader);if(reader.NodeType!=XmlNodeType.Element)continue;
                if(reader.Depth==0){if(root||reader.LocalName!="package"||reader.NamespaceURI!=OpfNs)throw new UserError(Invalid);root=true;continue;}
                if(reader.NamespaceURI!=OpfNs)continue;
                if(reader.Depth==1&&reader.LocalName=="manifest"){if(manifest)throw new UserError(Invalid);manifest=true;continue;}
                if(reader.Depth==1&&reader.LocalName=="spine"){if(spine)throw new UserError(Invalid);spine=true;continue;}
                if(reader.Depth==2&&reader.LocalName=="item")
                {
                    string id=reader.GetAttribute("id")??"",href=reader.GetAttribute("href")??"",type=reader.GetAttribute("media-type")??"";
                    if(id.Length==0||type.Length==0||items.ContainsKey(id))throw new UserError(Invalid);
                    // Remote resources are legal in EPUB 3 manifests but never
                    // read; only a spine reference to one is refused below.
                    items[id]=Scheme.IsMatch(href)?("",type):(Resolve(directory,href),type);
                }
                else if(reader.Depth==2&&reader.LocalName=="itemref")
                {
                    string idref=reader.GetAttribute("idref")??"";
                    if(!used.Add(idref)||order.Count>=MaxEntries)throw new UserError(Invalid);order.Add(idref);
                }
            }
            if(!root||!manifest||!spine)throw new UserError(Invalid);
            return order.Select(id=>items.TryGetValue(id,out var item)&&item.Path.Length>0?item:throw new UserError(Invalid)).ToList();
        }
        catch(XmlException){throw new UserError(Invalid);}
    }

    private static HashSet<string> EncryptedPaths(byte[] xml,CancellationToken token)
    {
        var paths=new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader=Xml(xml,false);string? algorithm=null;bool inData=false;int dataDepth=-1,count=0;
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();Depth(reader);
                if(reader.NodeType==XmlNodeType.EndElement&&inData&&reader.Depth==dataDepth)
                {inData=false;if(algorithm is null||!FontObfuscation.Contains(algorithm))throw new UserError(Encrypted);continue;}
                if(reader.NodeType!=XmlNodeType.Element||reader.NamespaceURI!=EncNs)continue;
                if(reader.LocalName=="EncryptedData")
                {
                    if(inData)throw new UserError(Encrypted);if(++count>MaxEntries)throw new UserError(Limit);
                    algorithm=null;inData=!reader.IsEmptyElement;dataDepth=reader.Depth;if(!inData)throw new UserError(Encrypted);
                }
                else if(inData&&reader.LocalName=="EncryptionMethod")algorithm=reader.GetAttribute("Algorithm");
                else if(inData&&reader.LocalName=="CipherReference")
                {
                    string uri=reader.GetAttribute("URI")??"";
                    // Obfuscated fonts are never read, so they need no key.
                    try{paths.Add(Resolve("",uri));}catch(UserError){throw new UserError(Encrypted);}
                }
            }
            return paths;
        }
        catch(XmlException){throw new UserError(Encrypted);}
    }

    // XHTML body subset with the source HTML reader's whitespace rules:
    // ordinary spaces collapse, block edges become LF, br emits LF and pre
    // keeps stored whitespace. Foreign-namespace (SVG/MathML/epub:switch),
    // hidden and omitted elements contribute nothing; attributes never do.
    private static void Body(byte[] xml,StringBuilder output,CancellationToken token)
    {
        try
        {
            using var reader=Xml(xml,true);bool root=false,seenBody=false,body=false,space=false,boundary=false;int bodyDepth=-1,excluded=-1;
            var pre=new Stack<int>();
            void Append(char c){if(output.Length>=DocumentText.MaxCharacters)throw new UserError(Limit);output.Append(c);}
            void Flush(char next)
            {
                if(output.Length>0)
                {
                    char last=output[^1];
                    if(boundary){if(last is not '\n' and not '\r'&&next is not '\n' and not '\r')Append('\n');}
                    else if(space&&last is not '\n' and not '\r' and not ' ')Append(' ');
                }
                boundary=space=false;
            }
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();Depth(reader);
                var type=reader.NodeType;
                if(type==XmlNodeType.DocumentType)continue;
                if(type==XmlNodeType.EntityReference)throw new UserError(Invalid);
                if(type==XmlNodeType.Element&&reader.Depth==0)
                {if(root||reader.LocalName!="html"||reader.NamespaceURI!=Xhtml)throw new UserError(Invalid);root=true;continue;}
                if(type==XmlNodeType.Element&&reader.Depth==1&&reader.LocalName=="body"&&reader.NamespaceURI==Xhtml)
                {if(seenBody)throw new UserError(Invalid);seenBody=true;body=!reader.IsEmptyElement;bodyDepth=1;boundary=true;continue;}
                if(!body)continue;
                if(type==XmlNodeType.EndElement&&reader.Depth==bodyDepth){body=false;continue;}
                if(excluded>=0){if(type==XmlNodeType.EndElement&&reader.Depth==excluded)excluded=-1;continue;}
                if(type==XmlNodeType.Element)
                {
                    string name=reader.LocalName;
                    if(reader.NamespaceURI!=Xhtml||Omitted.Contains(name)||reader.GetAttribute("hidden") is not null)
                    {if(!reader.IsEmptyElement)excluded=reader.Depth;continue;}
                    if(name=="br"){space=boundary=false;Append('\n');continue;}
                    if(Blocks.Contains(name)){space=false;boundary=true;}
                    if(name=="pre"&&!reader.IsEmptyElement)pre.Push(reader.Depth);
                }
                else if(type==XmlNodeType.EndElement)
                {
                    if(pre.Count>0&&pre.Peek()==reader.Depth)pre.Pop();
                    if(reader.NamespaceURI==Xhtml&&Blocks.Contains(reader.LocalName)){space=false;boundary=true;}
                }
                else if(type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    string value=reader.Value;DocumentText.Validate(value,token);bool keep=pre.Count>0;
                    for(int i=0;i<value.Length;i++)
                    {
                        if((i&4095)==0)token.ThrowIfCancellationRequested();char c=value[i];
                        if(!keep&&c is ' ' or '\t' or '\r' or '\n'){space=true;continue;}
                        Flush(c);Append(c);
                    }
                }
            }
            if(!root||!seenBody)throw new UserError(Invalid);
        }
        catch(XmlException){throw new UserError(Invalid);}
    }
}
