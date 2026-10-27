using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class DocumentTextFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public string Path=>input.Path;
    public TextDocument Document {get;}
    public TextStatistics Statistics {get;}
    private DocumentTextFile(NativeFiles.Input input,TextDocument document,TextStatistics statistics){this.input=input;Document=document;Statistics=statistics;}
    /// With `expected` (an agent's plan), a file that is no longer the planned one is refused before it is read.
    public static DocumentTextFile Open(string path,CancellationToken token,FileStamp? expected=null)
    {
        token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);
        try
        {
            if(expected is FileStamp planned&&!input.Stamp.SameObjectAndContent(planned))throw new McpToolError(McpSession.Changed);
            if(input.Stream.Length>DocumentText.MaxInputBytes)throw new UserError("Document input is limited to 8 MiB.");
            var document=DocumentText.Read(input.Stream,System.IO.Path.GetExtension(path).TrimStart('.'),token);
            var stats=DocumentText.Count(document.Text,token);token.ThrowIfCancellationRequested();return new(input,document,stats);
        }
        catch{input.Dispose();throw;}
    }
    public string Save(TextExport format,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Enum.IsDefined(format))throw new ArgumentOutOfRangeException(nameof(format));
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(Path)+" text",format switch { TextExport.Utf8=>"txt",TextExport.Rtf=>"rtf",TextExport.Docx=>"docx",TextExport.Html=>"html",TextExport.Odt=>"odt",_=>throw new ArgumentOutOfRangeException(nameof(format)) },output=>DocumentText.Write(output,Document.Text,format,token),token);
    }
    public void Dispose()=>input.Dispose();
}
