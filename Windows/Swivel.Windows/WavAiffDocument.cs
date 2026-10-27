using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class WavAiffDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    public WaveInfo Info { get; }
    public string Path => input.Path;
    private WavAiffDocument(NativeFiles.Input input, WaveInfo info) { this.input = input; Info = info; }
    public static WavAiffDocument Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try { var info = PcmWave.Read(input.Stream, token); AiffExport.OutputBytes(info); return new(input, info); }
        catch { input.Dispose(); throw; }
    }
    // Modal owner serializes preview/save and joins preview before Dispose.
    public byte[] Preview(CancellationToken token)
    {
        using var output = new MemoryStream(); AiffExport.Export(input.Stream, output, token, preview: true); return output.ToArray();
    }
    public string Save(CancellationToken token)
    {
        AiffExport.OutputBytes(Info); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " AIFF", "aiff",
            output => AiffExport.Export(input.Stream, output, token), token);
    }
    public void Dispose() => input.Dispose();
}
