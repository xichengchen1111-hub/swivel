using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Swivel.Core;

namespace Swivel.Windows;

public sealed partial class App
{
    private async Task ExportPresetsForMacAsync(Window owner, Action<string> report, CancellationToken token)
    {
        var snapshot = Prefs.Presets.ToArray(); var plan = MacPresetExport.Prepare(snapshot);
        MacPresetExportFile? current = null; string? macPath = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var preview = new MacPresetExportDialog(this, plan, macPath, token);
                if (!ShowOperationDialog(preview, owner, token)) return;
                token.ThrowIfCancellationRequested();
                if (!preview.MergeRequested) break;
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = L("Image preset libraries (*.json)|*.json"), Multiselect = false, CheckFileExists = true };
                if (picker.ShowDialog(owner) != true) continue;
                token.ThrowIfCancellationRequested(); macPath = picker.FileName;
                current?.Dispose(); current = null;
                current = await Task.Run(() => MacPresetExportFile.Open(snapshot, macPath, token), token);
                token.ThrowIfCancellationRequested(); plan = current.Plan;
            }
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Mac Export Folder…") };
            token.ThrowIfCancellationRequested(); if (folder.ShowDialog(owner) != true) return;
            token.ThrowIfCancellationRequested(); string destination = folder.FolderName;
            string output = await Task.Run(() => MacPresetExportFile.Save(plan, destination, token), token);
            string status = L("Mac preset export saved."); report(status); Home.ShowResults(new[] { output }, status);
            token.ThrowIfCancellationRequested(); ShowOperationDialog(new MacPresetInstallDialog(this, output, token), owner, token);
        }
        catch when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { current?.Dispose(); }
    }
}
