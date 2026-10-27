using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class FloatWaveDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    public FloatWaveInfo Info { get; }
    public FloatWaveStats Stats { get; }
    public string Path => input.Path;
    private FloatWaveDocument(NativeFiles.Input input, FloatWaveInfo info, FloatWaveStats stats) { this.input = input; Info = info; Stats = stats; }
    public static FloatWaveDocument Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try { var info = FloatWave.Read(input.Stream, token); var stats = FloatWave.Scan(input.Stream, token); return new(input, info, stats); }
        catch { input.Dispose(); throw; }
    }
    // Preview/export are serialized by the modal owner; it joins preview before disposal.
    public byte[] Preview(FloatPcmOptions options, CancellationToken token)
    {
        options.Validate(Info, Stats);
        using var output = new MemoryStream(); FloatWave.Export(input.Stream, output, options, token, preview: true); return output.ToArray();
    }
    public string Save(FloatPcmOptions options, CancellationToken token)
    {
        options.Validate(Info, Stats); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " integer", "wav",
            output => FloatWave.Export(input.Stream, output, options, token), token);
    }
    public void Dispose() => input.Dispose();
}
