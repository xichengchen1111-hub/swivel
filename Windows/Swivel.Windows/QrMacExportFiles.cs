using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public static class QrMacExportFiles
{
    // The caller awaits this whole operation on its STA worker. Hold the chosen
    // directory and every ancestor across every encoder and output, rather than
    // letting the folder be replaced between files in a batch.
    public static IReadOnlyList<ImageBatchEntry> Save(QrMacExportPlan plan, string directory,
        CancellationToken token, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        if (plan.Compatible.Count == 0) return Array.Empty<ImageBatchEntry>();
        WindowsPathPolicy.Validate(Path.Combine(directory, plan.Compatible[0].SuggestedFileName));
        using var lease = NativeFiles.DirectoryLease.Open(directory);
        return QrMacExport.Run(plan, row =>
        {
            token.ThrowIfCancellationRequested();
            var code = QrCode.Encode(row.Entry.Text, row.Entry.Correction);
            token.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(QrTools.Render(code)));
            return NativeFiles.WriteInDirectory(directory, row.Stem, "png", stream =>
            {
                token.ThrowIfCancellationRequested();
                encoder.Save(stream);
                token.ThrowIfCancellationRequested();
            }, token);
        }, token, progress);
    }
}
