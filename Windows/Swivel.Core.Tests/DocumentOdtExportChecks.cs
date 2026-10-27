using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Swivel.Core;

internal static class DocumentOdtExportChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach(string text in new[]{"","a","  leading  inner  trailing  ","\r\n\t 中文 👩‍👩‍👧‍👦 e\u0301\r\nlast\r",new string('x',8191)+"😀tail",@"<script a='&'>\object</script>",new string(' ',DocumentText.MaxCharacters-1)})
        {
            using var stream=new MemoryStream();DocumentText.Write(stream,text,TextExport.Odt,default);byte[] bytes=stream.ToArray();
            check(bytes.AsSpan(0,4).SequenceEqual(new byte[]{80,75,3,4})&&BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8))==0&&BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26))==8&&BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28))==0&&Encoding.ASCII.GetString(bytes,30,8)=="mimetype","ODT first local header is Stored mimetype without extra fields");
            using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
            check(zip.Entries.Select(x=>x.FullName).SequenceEqual(new[]{"mimetype","META-INF/manifest.xml","content.xml"}),"ODT minimal package order and exact three parts");
            using(var mime=new StreamReader(zip.GetEntry("mimetype")!.Open(),Encoding.ASCII))check(mime.ReadToEnd()=="application/vnd.oasis.opendocument.text","ODT MIME has no BOM/newline");
            XNamespace manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
            using(var input=zip.GetEntry("META-INF/manifest.xml")!.Open())
            {
                var doc=XDocument.Load(input);var files=doc.Root!.Elements(manifest+"file-entry").ToArray();
                check(doc.Root.Attribute(manifest+"version")?.Value=="1.3"&&files.Length==2&&files[0].Attribute(manifest+"full-path")?.Value=="/"&&files[0].Attribute(manifest+"media-type")?.Value=="application/vnd.oasis.opendocument.text"&&files[1].Attribute(manifest+"full-path")?.Value=="content.xml"&&files[1].Attribute(manifest+"media-type")?.Value=="text/xml","ODT manifest lists root/content with exact MIME and no self/mimetype entries");
            }
            var document=DocumentText.Read(new MemoryStream(bytes),"odt",default);
            check(document.Odt&&document.Text==text.Replace("\r\n","\n").Replace('\r','\n')+"\n","Generated ODT preserves all spaces/Unicode and explicit breaks with final paragraph LF");
        }
        foreach(string text in new[]{new string('x',DocumentText.MaxCharacters),new string('&',500_000),new string('\t',200_000),"\uffff"})
        {
            using var stream=new MemoryStream();reject(()=>DocumentText.Write(stream,text,TextExport.Odt,default),"ODT character/XML/output expansion budget refused before writing");
            check(stream.Length==0,"ODT preflight refusal leaves output untouched");
        }
        using(var stream=new MemoryStream())
        { reject(()=>DocumentText.Write(stream,"test",TextExport.Odt,new CancellationToken(true)),"ODT precancel before output");check(stream.Length==0,"ODT precancel publishes no bytes"); }
        using(var stream=new MemoryStream())
        {
            stream.WriteByte(77);reject(()=>DocumentText.Write(stream,"text",TextExport.Odt,default),"ODT rejects nonempty output rather than move first MIME offset");
            check(stream.ToArray().SequenceEqual(new byte[]{77}),"ODT output-shape refusal preserves caller bytes");
        }
        using(var stream=new NoSeek())
        { reject(()=>DocumentText.Write(stream,"text",TextExport.Odt,default),"ODT requires seekable output for fixed first MIME header");check(stream.Written==0,"ODT nonseekable refusal writes nothing"); }
    }
    private sealed class NoSeek:Stream
    {
        public int Written;public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] value,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] value,int offset,int count)=>Written+=count;
    }
}
