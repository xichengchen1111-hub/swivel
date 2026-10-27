using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class QrMacExportNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "Mac QR PNG exports"); Directory.CreateDirectory(folder);
        QrEntry Entry(string name, string text) => new(Guid.NewGuid().ToString("N"), name, text, Correction: QrCorrection.Medium);
        var plan = QrMacExport.Prepare(new[] { Entry("First", "中文 emoji 🦜\r\n"), Entry("Second", new string('中', 666) + "ab"), Entry("Third", "last") });
        string occupied = Path.Combine(folder, plan.Rows[0].SuggestedFileName);
        byte[] sentinel = { 4, 3, 2, 1 }; File.WriteAllBytes(occupied, sentinel);
        int progressCalls = 0;
        var result = QrMacExportFiles.Save(plan, folder, default, (_, _) =>
        {
            progressCalls++;
            reject(() => Directory.Move(folder, folder + " moved"), "Mac PNG export keeps the selected directory pinned for its entire batch");
        });
        check(result.All(row => row.State == BatchState.Saved) && result.Count == 3 && progressCalls == 3, "Mac PNG native batch publishes each compatible entry: " + string.Join("; ", result.Select(row => row.State + " " + row.Error?.GetType().Name + " " + row.Error?.Message)) + " progress " + progressCalls);
        check(result[0].Output == Path.Combine(folder, plan.Rows[0].Stem + " 2.png") && File.ReadAllBytes(occupied).SequenceEqual(sentinel), "Mac PNG native export never replaces an existing filename");
        for (int i = 0; i < result.Count; i++)
        {
            using var input = File.OpenRead(result[i].Output!);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var actual = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray8, null, 0);
            var code = QrCode.Encode(plan.Compatible[i].Entry.Text, QrCorrection.Medium);
            int side = (code.Size + 8) * 8;
            byte[] bytes = new byte[side * side]; actual.CopyPixels(bytes, side, 0);
            check(actual.PixelWidth == side && actual.PixelHeight == side && bytes.SequenceEqual(code.Raster()), "Mac PNG native encoding preserves all modules at eight pixels with four-module quiet zone");
        }
        int before = Directory.GetFiles(folder).Length;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            reject(() => QrMacExportFiles.Save(plan, folder, canceled.Token), "Mac PNG native pre-cancel refuses output");
            check(Directory.GetFiles(folder).Length == before, "Mac PNG native pre-cancel creates no files");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var partial = QrMacExportFiles.Save(plan, folder, cancellation.Token, (index, _) => { if (index == 2) cancellation.Cancel(); });
            check(partial.Select(row => row.State).SequenceEqual(new[] { BatchState.Saved, BatchState.Cancelled, BatchState.NotStarted }) && File.Exists(partial[0].Output), "Mac PNG native cancellation returns and retains the completed first file");
        }
        var failed = QrMacExportFiles.Save(plan, folder, default, (index, _) => { if (index == 2) throw new IOException("injected progress failure"); });
        check(failed.Select(row => row.State).SequenceEqual(new[] { BatchState.Saved, BatchState.Failed, BatchState.NotStarted }) && File.Exists(failed[0].Output), "Mac PNG native failure returns and retains all earlier files");
        check(!Directory.GetFiles(folder, ".swivel-*.tmp").Any(), "Mac PNG export leaves no private staging files after success or cancellation");
    }
}
