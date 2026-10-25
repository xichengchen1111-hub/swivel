using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class ImageBatchChecks
{
    public static void Run(Action<bool, string> check)
    {
        string[] files = { "first.png", "second.png", "third.png" };
        int calls = 0;
        var failed = ImageBatch.Run(files, file => {
            calls++; if (calls == 2) throw new InvalidOperationException("fixture"); return "new-" + file;
        }, CancellationToken.None);
        check(calls == 2 && failed.Select(x => x.State).SequenceEqual(new[] { BatchState.Saved, BatchState.Failed, BatchState.NotStarted }), "Batch stops after first error and accounts for all inputs");
        check(failed[0].Output == "new-first.png" && failed[1].Error is InvalidOperationException && failed[2].Input == files[2], "Partial results preserve saved output and failed input");
        using var cancellation = new CancellationTokenSource(); calls = 0;
        var cancelled = ImageBatch.Run(files, file => { calls++; cancellation.Cancel(); return "saved.png"; }, cancellation.Token);
        check(calls == 1 && cancelled[0].State == BatchState.Saved && cancelled[1].State == BatchState.Cancelled && cancelled[2].State == BatchState.NotStarted, "Cancel after publication retains output and does not run remaining files");
        var preCancelled = ImageBatch.Run(files, _ => throw new Exception("must not run"), cancellation.Token);
        check(preCancelled[0].State == BatchState.Cancelled && preCancelled.Count == 3, "Cancel before processing accounts for every file");
        using var during = new CancellationTokenSource();
        var interrupted = ImageBatch.Run(files, _ => { during.Cancel(); during.Token.ThrowIfCancellationRequested(); return "unreachable"; }, during.Token);
        check(interrupted[0].State == BatchState.Cancelled && interrupted.All(x => x.Output is null), "In-flight cancellation never invents an output");
        int progress = 0;
        var completed = ImageBatch.Run(files, file => file + ".png", CancellationToken.None, (index, total) => {
            check(total == 3 && index == ++progress, "Progress follows input order");
        });
        check(completed.All(x => x.State == BatchState.Saved) && completed.Count == 3, "Successful batch contains all outputs");
        var unrelatedCancel = ImageBatch.Run(files, _ => throw new OperationCanceledException(), CancellationToken.None);
        check(unrelatedCancel[0].State == BatchState.Failed, "Unrelated cancellation exception is reported as failure");
        foreach (var pair in new[] { ("convert.jpg", "jpg"), ("image.compress", "jpg"), ("convert.bmp", "bmp"), ("convert.tiff", "tiff"), ("image.resize", "png"), ("image.rotate", "png") })
            check(ImageOutputInfo.Format(pair.Item1) == pair.Item2, "Notice and encoder share output format: " + pair.Item1);
        check(ImageOutputInfo.Format("preset.fixture", new ImageRecipe("jpg")) == "jpg", "Preset notice uses saved recipe format");
    }
}
