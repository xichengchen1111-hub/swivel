using System;
using System.Collections.Generic;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class MacPresetExportFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public MacPresetExportPlan Plan { get; }
    private MacPresetExportFile(NativeFiles.Input input, MacPresetExportPlan plan) { this.input = input; Plan = plan; }
    public static MacPresetExportFile Open(IReadOnlyList<ImagePreset> presets, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try
        {
            if (input.Stream.Length > PresetLibrary.MaxBytes) throw new UserError(MacPresetExport.Invalid);
            byte[] data = new byte[checked((int)input.Stream.Length)]; input.Stream.ReadExactly(data);
            var plan = MacPresetExport.Prepare(presets, data);
            token.ThrowIfCancellationRequested(); return new(input, plan);
        }
        catch { input.Dispose(); throw; }
    }
    public static string Save(MacPresetExportPlan plan, string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[] data = plan.Encode();
        return NativeFiles.WriteInDirectory(folder, "swivel-mac-presets", "json", stream => stream.Write(data), token);
    }
    public void Dispose() => input.Dispose();
}
