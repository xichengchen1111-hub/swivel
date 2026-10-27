using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class AiffDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    public AiffInfo Info { get; }
    public string Path => input.Path;
    private AiffDocument(NativeFiles.Input input, AiffInfo info) { this.input = input; Info = info; }
    public static AiffDocument Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try { var info = AiffWave.Read(input.Stream, token); info.OutputBytes(); return new(input, info); }
        catch { input.Dispose(); throw; }
    }
    // Caller serializes preview/export and joins outstanding preview before disposal.
    public byte[] Preview(CancellationToken token)
    {
        using var output = new MemoryStream(); AiffWave.Export(input.Stream, output, token, preview: true); return output.ToArray();
    }
    public string Save(CancellationToken token)
    {
        Info.OutputBytes(); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " PCM", "wav",
            output => AiffWave.Export(input.Stream, output, token), token);
    }
    public void Dispose() => input.Dispose();
}
