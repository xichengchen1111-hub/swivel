using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class WaveJoinDocument : IDisposable
{
    private readonly NativeFiles.Input[] inputs;
    public WaveJoinInfo Info { get; }
    public IReadOnlyList<string> Paths { get; }
    private WaveJoinDocument(NativeFiles.Input[] inputs, WaveJoinInfo info)
    { this.inputs = inputs; Info = info; Paths = Array.AsReadOnly(inputs.Select(input => input.Path).ToArray()); }
    public static WaveJoinDocument Open(IReadOnlyList<string> paths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (paths.Count is < 2 or > WaveJoin.MaxInputs) throw new UserError(WaveJoin.Selection);
        paths = paths.ToArray();
        var held = new List<NativeFiles.Input>();
        try
        {
            foreach (var path in paths)
            { token.ThrowIfCancellationRequested(); held.Add(NativeFiles.OpenRead(path)); }
            var info = WaveJoin.Inspect(held.Select(input => input.Stream).ToArray(), token);
            token.ThrowIfCancellationRequested(); return new(held.ToArray(), info);
        }
        catch { foreach (var input in held) input.Dispose(); throw; }
    }
    private NativeFiles.Input[] Ordered(IReadOnlyList<int> order)
    {
        if (order.Count != inputs.Length) throw new ArgumentException("A complete input permutation is required.");
        order = order.ToArray();
        if (order.Count != inputs.Length || order.Any(i => i < 0 || i >= inputs.Length) || order.Distinct().Count() != inputs.Length) throw new ArgumentException("A complete input permutation is required.");
        return order.Select(i => inputs[i]).ToArray();
    }
    public byte[] Preview(IReadOnlyList<int> order, CancellationToken token)
    {
        var snapshot = Ordered(order); using var output = new MemoryStream();
        WaveJoin.Export(snapshot.Select(input => input.Stream).ToArray(), output, token, preview: true); return output.ToArray();
    }
    public string Save(IReadOnlyList<int> order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var snapshot = Ordered(order); var streams = snapshot.Select(input => input.Stream).ToArray();
        WaveJoin.Inspect(streams, token);
        return NativeFiles.WriteBeside(snapshot[0], Path.GetFileNameWithoutExtension(snapshot[0].Path) + " Joined", "wav", output => WaveJoin.Export(streams, output, token), token);
    }
    public void Dispose() { foreach (var input in inputs) input.Dispose(); }
}
