using System;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

// Keep the optional source and its ancestors pinned throughout preview/export.
public sealed class QrMacMergeFile : IDisposable
{
    private readonly NativeFiles.Input input;
    private readonly byte[] bytes;
    public ReadOnlyMemory<byte> Bytes=>bytes;
    private QrMacMergeFile(NativeFiles.Input input,byte[] bytes) {this.input=input;this.bytes=bytes;}
    public static QrMacMergeFile Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input=NativeFiles.OpenRead(path);
        try
        {
            if(input.Stream.Length>QrMacLibraryExport.MaxMergeBytes) throw new UserError(QrMacLibraryExport.InvalidMerge);
            byte[] bytes=new byte[checked((int)input.Stream.Length)]; input.Stream.ReadExactly(bytes);
            token.ThrowIfCancellationRequested();
            _=QrMacLibraryExport.Prepare(Array.Empty<QrEntry>(),bytes);
            return new(input,bytes);
        }
        catch {input.Dispose();throw;}
    }
    public void Dispose()=>input.Dispose();
}
public static class QrMacLibraryFiles
{
    public static string Save(QrMacLibraryPlan plan,string folder,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[] bytes=plan.Encode();
        return NativeFiles.WriteInDirectory(folder,"library-v1","json",stream=>
        { token.ThrowIfCancellationRequested(); stream.Write(bytes); token.ThrowIfCancellationRequested(); },token);
    }
}
