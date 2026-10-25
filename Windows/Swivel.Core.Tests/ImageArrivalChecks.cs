using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

internal static class ImageArrivalChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
        => RunAsync(check, reject).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject)
    {
        check(ImageArrivalRules.MaxCards == 12 && ImageArrivalRules.MaxQueue == 64 && ImageArrivalRules.MaxNotifications == 512 && ImageArrivalRules.MaxAttempts == 4 && ImageArrivalRules.MaxReservedBytes == 512L * 1024 * 1024, "Arrival limits fixed and bounded");
        check(Enumerable.Range(1, 4).Select(ImageArrivalRules.DelayMilliseconds).SequenceEqual(new[] { 350, 500, 1000, 2000 }), "Every attempt has a positive bounded spacing");
        foreach (string suffix in new[] { ".png", ".JPG", ".jpeg", ".bmp", ".tif", ".TIFF", ".gif" }) check(ImageArrivalRules.IsImageName("a" + suffix), "Supported image arrival suffix");
        foreach (string name in new[] { "a.png.tmp", "a.svg", "a.pdf", "a.mov", "a", "a." }) check(!ImageArrivalRules.IsImageName(name), "Unsupported arrival suffix ignored");
        int clean = 0, reads = 0;
        var simple = New((path, reserve, token) => { Interlocked.Increment(ref reads); reserve(40); return Task.FromResult(path); }, () => clean++);
        check(simple.Snapshot.Status == ArrivalStatus.Stopped && reads == 0 && simple.Snapshot.Cards.Count == 0, "Constructing a collector reads and enumerates nothing");
        check(!simple.Offer("old.png", ArrivalKind.Created), "Notifications before explicit Start ignored");
        simple.Start();
        check(!simple.Offer("x.txt", ArrivalKind.Created) && !simple.Offer("x.png", (ArrivalKind)99), "Only supported image notification kinds accepted");
        check(simple.Offer("a.png", ArrivalKind.Renamed), "Rename only supplies its current new path");
        for (int i = 0; i < 50; i++) check(!simple.Offer("a.png", ArrivalKind.Created), "Duplicate path never resets its attempt history");
        await Until(() => simple.Snapshot.Cards.Count == 1);
        var first = simple.Snapshot;
        check(first.EligibleNotifications == 51 && first.Attempts == 1 && first.ReservedBytes == 40 && reads == 1 && first.Cards[0].Kind == ArrivalKind.Renamed, "Duplicates counted before coalescing while one path reads once");
        reject(() => ((IList<ArrivalCard<string>>)first.Cards).Clear(), "Arrival card snapshot cannot be mutated");
        reject(() => ((IList<ArrivalIssue>)first.Issues).Add(new("", "", "")), "Arrival issue snapshot cannot be mutated");
        await simple.StopAsync();
        check(clean == 1 && simple.Completion.IsCompletedSuccessfully && simple.Snapshot.Cards.Count == 1 && simple.Snapshot.Status == ArrivalStatus.Stopped, "Stop joins cleanup and retains completed snapshots");
        check(first.Status == ArrivalStatus.Running && first.Cards.Count == 1, "Earlier immutable snapshot remains historical");
        await simple.StopAsync(ArrivalStopReason.Closed);
        check(clean == 1 && simple.Snapshot.StopReason == ArrivalStopReason.User, "Stop idempotent and preserves first stop reason");
        reject(simple.Start, "A stopped session cannot restart");
        simple.Dismiss(first.Cards[0].Id);
        check(simple.Snapshot.Cards.Count == 0 && first.Cards.Count == 1, "Dismiss only removes the current card snapshot");

        clean = 0;
        var neverStarted = New((_, _, _) => throw new Exception("must not read"), () => clean++);
        await neverStarted.StopAsync(ArrivalStopReason.Closed);
        check(clean == 1 && neverStarted.Snapshot.Attempts == 0, "Stop before Start releases a created session without reading");

        var notified = New((_, _, _) => throw new Exception("must not read"), delay: (_, token) => Task.Delay(Timeout.Infinite, token));
        notified.Start();
        for (int i = 0; i < 512; i++) notified.Offer("same.png", ArrivalKind.Created);
        check(notified.Snapshot.EligibleNotifications == 512 && notified.Snapshot.Status == ArrivalStatus.Running, "Exactly 512 eligible notifications accepted without integer or duplicate bypass");
        notified.Offer("same.png", ArrivalKind.Created); await notified.Completion;
        check(notified.Snapshot.StopReason == ArrivalStopReason.NotificationLimit && notified.Snapshot.EligibleNotifications == 512 && notified.Snapshot.Cards.Count == 0, "The next notification stops without a rescan");

        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = New((_, _, _) => throw new Exception("must not read"), delay: (_, token) => { began.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        full.Start(); full.Offer("active.png", ArrivalKind.Created); await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 64; i++) check(full.Offer(i + ".png", ArrivalKind.Created), "Bounded queue accepts each of its 64 slots");
        check(full.Snapshot.QueueCount == 64, "Queue capacity excludes the one in-flight path");
        check(!full.Offer("overflow.png", ArrivalKind.Created), "65th queued path refuses further work");
        await full.Completion;
        check(full.Snapshot.StopReason == ArrivalStopReason.QueueLimit && full.Snapshot.QueueCount == 0 && full.Snapshot.Attempts == 0, "Queue overflow cancels pending delay and drops remaining queue explicitly");

        var twelve = New((path, reserve, _) => { reserve(1); return Task.FromResult(path); });
        twelve.Start(); for (int i = 0; i < 12; i++) twelve.Offer(i + ".png", ArrivalKind.Created);
        await twelve.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        check(twelve.Snapshot.Cards.Count == 12 && twelve.Snapshot.StopReason == ArrivalStopReason.CardLimit && twelve.Snapshot.Attempts == 12 && twelve.Snapshot.ReservedBytes == 12, "Twelfth published card stops with all 12 frozen results retained");
        twelve.ClearCards(); check(twelve.Snapshot.Cards.Count == 0, "Clear drops only cards and does not restart watching");

        int failures = 0; var delayOrder = new List<int>();
        var retries = New((_, reserve, _) => { reserve(17); Interlocked.Increment(ref failures); throw new IOException("still writing"); }, delay: (attempt, _) => { delayOrder.Add(attempt); return Task.CompletedTask; });
        retries.Start(); retries.Offer("partial.png", ArrivalKind.Created);
        await Until(() => retries.Snapshot.FailedFiles == 1);
        for (int i = 0; i < 5; i++) retries.Offer("partial.png", ArrivalKind.Renamed);
        await retries.StopAsync();
        check(failures == 4 && retries.Snapshot.Attempts == 4 && retries.Snapshot.ReservedBytes == 68 && delayOrder.SequenceEqual(new[] { 1, 2, 3, 4 }), "Failed read attempts are spaced, all charged, and capped across duplicate notifications");
        check(retries.Snapshot.Issues.Count == 1 && retries.Snapshot.FailedFiles == 1 && retries.Snapshot.StopReason == ArrivalStopReason.User, "Exhausted ordinary file retries report one failure and keep the session usable");

        var cannotOpen = New((_, _, _) => throw new IOException("sharing violation"));
        cannotOpen.Start(); cannotOpen.Offer("busy.png", ArrivalKind.Created); await Until(() => cannotOpen.Snapshot.FailedFiles == 1); await cannotOpen.StopAsync();
        check(cannotOpen.Snapshot.Attempts == 4 && cannotOpen.Snapshot.ReservedBytes == 0, "Failed open still consumes attempts but no unknown length is invented");

        var exact = New((path, reserve, _) => { reserve(ImageArrivalRules.MaxReservedBytes); return Task.FromResult(path); });
        exact.Start(); exact.Offer("exact.png", ArrivalKind.Created); await exact.Completion;
        check(exact.Snapshot.Cards.Count == 1 && exact.Snapshot.ReservedBytes == ImageArrivalRules.MaxReservedBytes && exact.Snapshot.StopReason == ArrivalStopReason.ByteLimit, "Exact byte ceiling permits the charged attempt then stops");
        bool decoded = false;
        var over = New((path, reserve, _) => { reserve(ImageArrivalRules.MaxReservedBytes + 1); decoded = true; return Task.FromResult(path); });
        over.Start(); over.Offer("too-big.png", ArrivalKind.Created); await over.Completion;
        check(!decoded && over.Snapshot.ReservedBytes == 0 && over.Snapshot.StopReason == ArrivalStopReason.ByteLimit, "Reservation overflow stops before the decoder and never partially charges");

        var failedExact = New((_, reserve, _) => { reserve(ImageArrivalRules.MaxReservedBytes / 2); throw new IOException("decode failed"); });
        failedExact.Start(); failedExact.Offer("twice.png", ArrivalKind.Created); await failedExact.Completion;
        check(failedExact.Snapshot.Attempts == 2 && failedExact.Snapshot.FailedFiles == 1 && failedExact.Snapshot.Issues.Count == 1 && failedExact.Snapshot.ReservedBytes == ImageArrivalRules.MaxReservedBytes, "Failed attempts retain all reservations and the final read diagnostic when budget expires");

        var inRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sourceReleased = false, cleanedAfterRead = false;
        var late = New(async (_, reserve, _) => { reserve(20); inRead.TrySetResult(); try { return await release.Task; } finally { sourceReleased = true; } }, () => cleanedAfterRead = sourceReleased);
        late.Start(); late.Offer("late.png", ArrivalKind.Created); await inRead.Task;
        var stopping = late.StopAsync(ArrivalStopReason.Closed);
        check(!stopping.IsCompleted && late.Snapshot.Status == ArrivalStatus.Stopping, "Stop waits for a native read that has not yet returned");
        release.SetResult("historical"); await stopping;
        check(cleanedAfterRead && late.Snapshot.Cards.Count == 0 && late.Snapshot.StopReason == ArrivalStopReason.Closed, "Late success is discarded before cleanup and cannot create a card");

        var lateErrorInRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateErrorRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateError = New(async (_, _, _) => { lateErrorInRead.SetResult(); return await lateErrorRelease.Task; });
        lateError.Start(); lateError.Offer("late-error.png", ArrivalKind.Created); await lateErrorInRead.Task;
        var stoppingError = lateError.StopAsync(); lateErrorRelease.SetException(new IOException("late ordinary failure")); await stoppingError;
        check(lateError.Snapshot.Issues.Count == 0 && lateError.Snapshot.Cards.Count == 0, "Cancelled ordinary late failures never create misleading read reports");

        var cleanup = New((_, _, _) => throw new IOException(), () => throw new IOException("cleanup failure"));
        await cleanup.StopAsync();
        check(cleanup.Completion.IsCompletedSuccessfully && cleanup.Snapshot.StopReason == ArrivalStopReason.CleanupError && cleanup.Snapshot.Issues.Single().Detail.Contains("cleanup failure", StringComparison.Ordinal), "Cleanup failure retained while Completion always settles");
        var sourceCleanup = New((_, _, _) => throw new ArrivalCleanupException(new IOException("input cleanup")));
        sourceCleanup.Start(); sourceCleanup.Offer("cleanup.png", ArrivalKind.Created); await sourceCleanup.Completion;
        check(sourceCleanup.Snapshot.StopReason == ArrivalStopReason.CleanupError && sourceCleanup.Snapshot.Issues.Count == 1 && sourceCleanup.Snapshot.Attempts == 1, "Input cleanup failure stops immediately rather than retrying");
        check(sourceCleanup.Snapshot.Issues[0].Detail.Contains("input cleanup", StringComparison.Ordinal), "Input cleanup detail retains the underlying release failure");

        var watcher = New((_, _, _) => throw new Exception("must not read"));
        watcher.Start(); watcher.FailWatcher(new InternalBufferOverflowException("events lost")); await watcher.Completion;
        check(watcher.Snapshot.StopReason == ArrivalStopReason.WatcherError && watcher.Snapshot.Issues.Count == 1 && watcher.Snapshot.Attempts == 0, "Watcher overflow reports lost events and never scans for recovery");
        watcher.FailWatcher(new IOException("late callback"));
        check(watcher.Snapshot.Issues.Count == 1, "Late callback after stopping cannot append unrelated errors");

        var manyIssues = New((_, _, _) => throw new IOException(new string('x', 600)), retry: _ => false);
        manyIssues.Start();
        for (int i = 0; i < 40; i++) { manyIssues.Offer(i + ".png", ArrivalKind.Created); int count = i + 1; await Until(() => manyIssues.Snapshot.FailedFiles == count); }
        await manyIssues.StopAsync();
        check(manyIssues.Snapshot.Issues.Count == 32 && manyIssues.Snapshot.OmittedIssues == 8 && manyIssues.Snapshot.FailedFiles == 40 && manyIssues.Snapshot.Issues.All(x => x.Detail.Length == 500), "Diagnostics have bounded entries/detail and retain omitted totals");

        int concurrent = 0, maximum = 0;
        var serial = New(async (path, reserve, token) => { int now = Interlocked.Increment(ref concurrent); maximum = Math.Max(now, maximum); try { reserve(1); await Task.Yield(); token.ThrowIfCancellationRequested(); return path; } finally { Interlocked.Decrement(ref concurrent); } });
        serial.Start(); for (int i = 0; i < 12; i++) serial.Offer(i + ".png", ArrivalKind.Created); await serial.Completion;
        check(maximum == 1 && concurrent == 0, "All reads are single-flight including asynchronous completion");
        var changedThrows = New((path, _, _) => Task.FromResult(path)); changedThrows.Changed += () => throw new Exception("subscriber");
        changedThrows.Start(); changedThrows.Offer("good.png", ArrivalKind.Created); await Until(() => changedThrows.Snapshot.Cards.Count == 1); await changedThrows.StopAsync();
        check(changedThrows.Snapshot.Cards.Count == 1, "A faulty UI event subscriber cannot break source cleanup or completion");
    }
    private static ArrivalCollector<string> New(Func<string, Action<long>, CancellationToken, Task<string>> read,
        Action? cleanup = null, Func<Exception, bool>? retry = null, Func<int, CancellationToken, Task>? delay = null)
        => new(@"C:\chosen", read, cleanup ?? (() => { }), retry, delay ?? ((_, _) => Task.CompletedTask));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
