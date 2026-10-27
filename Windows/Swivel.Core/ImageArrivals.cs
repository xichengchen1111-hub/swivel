using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Swivel.Core;

public enum ArrivalKind { Created, Renamed }
public enum ArrivalStatus { Stopped, Running, Stopping }
public enum ArrivalStopReason { None, User, Closed, FeatureDisabled, Cancelled, CardLimit, QueueLimit, NotificationLimit, ByteLimit, WatcherError, ReadFailure, CleanupError }
public sealed record ArrivalCard<T>(Guid Id, string Path, T Thumbnail, ArrivalKind Kind);
public sealed record ArrivalIssue(string Path, string Message, string Detail);
public sealed record ArrivalSnapshot<T>(Guid SessionId, long Revision, string Directory, ArrivalStatus Status,
    ArrivalStopReason StopReason, IReadOnlyList<ArrivalCard<T>> Cards, int QueueCount, int EligibleNotifications,
    int Attempts, long ReservedBytes, int FailedFiles, IReadOnlyList<ArrivalIssue> Issues, int OmittedIssues);

public static class ImageArrivalRules
{
    public const int MaxCards = 12, MaxQueue = 64, MaxNotifications = 512, MaxAttempts = 4, MaxIssues = 32;
    public const long MaxReservedBytes = 512L * 1024 * 1024;
    public const string ReadFailed = "This reported image path could not be read.";
    public const string WatchFailed = "Folder notifications failed. Some arrivals may have been missed.";
    public const string CleanupFailed = "Image arrival monitoring stopped with a cleanup error.";
    public static int DelayMilliseconds(int attempt) => attempt switch { 1 => 350, 2 => 500, 3 => 1000, 4 => 2000, _ => throw new ArgumentOutOfRangeException(nameof(attempt)) };
    public static bool IsImageName(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif";
}

// Kept distinct from read/decode failures: stopping must never hide failed cleanup.
public sealed class ArrivalCleanupException : Exception
{
    public ArrivalCleanupException(Exception inner) : base(ImageArrivalRules.CleanupFailed, inner) { }
}

/// One session, one consumer. The loader opens a current path and reserves its length BEFORE
/// decoding. Reservations are never refunded, including failed attempts. Notification history
/// is deliberately path-based, not a claim about the identity of an event's original object.
public sealed class ArrivalCollector<T>
{
    private readonly object gate = new();
    private readonly Queue<(string Path, ArrivalKind Kind)> pending = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly List<ArrivalCard<T>> cards = new();
    private readonly List<ArrivalIssue> issues = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, Action<long>, CancellationToken, Task<T>> read;
    private readonly Action cleanup;
    private readonly Func<int, CancellationToken, Task> delay;
    private readonly Func<Exception, bool> retry;
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly string directory;
    private ArrivalStatus status;
    private ArrivalStopReason reason;
    private long revision, reservedBytes;
    private int notifications, attempts, failedFiles, omittedIssues;
    private bool started, stopRequested, workerStarted;

    public event Action? Changed;
    public Task Completion => completion.Task;
    public ArrivalSnapshot<T> Snapshot
    {
        get { lock (gate) return new(sessionId, revision, directory, status, reason,
            Array.AsReadOnly(cards.ToArray()), pending.Count, notifications, attempts, reservedBytes,
            failedFiles, Array.AsReadOnly(issues.ToArray()), omittedIssues); }
    }
    public ArrivalCollector(string directory, Func<string, Action<long>, CancellationToken, Task<T>> read,
        Action cleanup, Func<Exception, bool>? retry = null, Func<int, CancellationToken, Task>? delay = null)
    {
        this.directory = directory; this.read = read; this.cleanup = cleanup;
        this.retry = retry ?? (_ => true);
        this.delay = delay ?? ((attempt, ct) => Task.Delay(ImageArrivalRules.DelayMilliseconds(attempt), ct));
        token = cancellation.Token;
    }
    public void Start()
    {
        lock (gate)
        {
            if (started || stopRequested) throw new InvalidOperationException("Create a new image arrival session before starting again.");
            started = true; status = ArrivalStatus.Running; revision++; EnsureWorkerLocked();
        }
        Notify();
    }
    private void EnsureWorkerLocked()
    {
        if (workerStarted) return;
        workerStarted = true; _ = Task.Run(ProcessAsync);
    }

    // The Windows adapter validates that this is a single leaf under its held root.
    // Duplicates consume the notification budget before being coalesced; they cannot reset retries.
    public bool Offer(string path, ArrivalKind kind)
    {
        if (!Enum.IsDefined(kind) || !ImageArrivalRules.IsImageName(path)) return false;
        bool accepted = false, stop = false;
        lock (gate)
        {
            if (status != ArrivalStatus.Running) return false;
            if (notifications == ImageArrivalRules.MaxNotifications)
                stop = RequestStopLocked(ArrivalStopReason.NotificationLimit);
            else
            {
                notifications++; revision++;
                if (!seen.Contains(path))
                {
                    if (pending.Count == ImageArrivalRules.MaxQueue) stop = RequestStopLocked(ArrivalStopReason.QueueLimit);
                    else { seen.Add(path); pending.Enqueue((path, kind)); accepted = true; }
                }
            }
        }
        if (accepted) signal.Release();
        if (stop) cancellation.Cancel();
        Notify(); return accepted;
    }
    public Task StopAsync(ArrivalStopReason stopReason = ArrivalStopReason.User)
    {
        bool stop;
        lock (gate) stop = RequestStopLocked(stopReason == ArrivalStopReason.None ? ArrivalStopReason.User : stopReason);
        if (stop) { cancellation.Cancel(); Notify(); }
        return Completion;
    }
    public void FailWatcher(Exception error)
    {
        bool stop;
        lock (gate)
        {
            if (stopRequested) return;
            AddIssueLocked("", ImageArrivalRules.WatchFailed, error);
            stop = RequestStopLocked(ArrivalStopReason.WatcherError);
        }
        if (stop) cancellation.Cancel(); Notify();
    }
    private bool RequestStopLocked(ArrivalStopReason stopReason)
    {
        if (stopRequested) return false;
        stopRequested = true; reason = stopReason; status = ArrivalStatus.Stopping; pending.Clear(); revision++;
        EnsureWorkerLocked(); return true;
    }
    public void Dismiss(Guid id)
    {
        bool changed;
        lock (gate) { changed = cards.RemoveAll(x => x.Id == id) != 0; if (changed) revision++; }
        if (changed) Notify();
    }
    public void ClearCards()
    {
        bool changed;
        lock (gate) { changed = cards.Count != 0; if (changed) { cards.Clear(); revision++; } }
        if (changed) Notify();
    }
    private void Reserve(long sourceLength)
    {
        token.ThrowIfCancellationRequested();
        if (sourceLength < 0) throw new ArgumentOutOfRangeException(nameof(sourceLength));
        bool stop = false;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (status != ArrivalStatus.Running) throw new OperationCanceledException(token);
            if (sourceLength > ImageArrivalRules.MaxReservedBytes - reservedBytes)
                stop = RequestStopLocked(ArrivalStopReason.ByteLimit);
            else { reservedBytes += sourceLength; revision++; }
        }
        if (stop) cancellation.Cancel();
        Notify(); token.ThrowIfCancellationRequested();
    }
    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                (string Path, ArrivalKind Kind) item;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (status != ArrivalStatus.Running) throw new OperationCanceledException(token);
                    if (pending.Count == 0) continue;
                    item = pending.Dequeue(); revision++;
                }
                Notify();
                for (int attempt = 1; attempt <= ImageArrivalRules.MaxAttempts; attempt++)
                {
                    await delay(attempt, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    lock (gate) { attempts++; revision++; }
                    Notify();
                    bool finished = false;
                    try
                    {
                        T thumbnail = await read(item.Path, Reserve, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        bool stop;
                        lock (gate)
                        {
                            token.ThrowIfCancellationRequested();
                            if (status != ArrivalStatus.Running) throw new OperationCanceledException(token);
                            cards.Add(new(Guid.NewGuid(), item.Path, thumbnail, item.Kind)); revision++;
                            stop = cards.Count == ImageArrivalRules.MaxCards && RequestStopLocked(ArrivalStopReason.CardLimit);
                        }
                        if (stop) cancellation.Cancel();
                        Notify(); finished = true;
                    }
                    catch (ArrivalCleanupException error)
                    {
                        lock (gate) { AddIssueLocked(item.Path, ImageArrivalRules.CleanupFailed, error); reason = ArrivalStopReason.CleanupError; }
                        _ = StopAsync(ArrivalStopReason.CleanupError); throw new OperationCanceledException(token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        // A cancellation may race an ordinary I/O error. It must not produce a late popup/card.
                        token.ThrowIfCancellationRequested();
                        bool budgetSpent;
                        lock (gate) budgetSpent = reservedBytes == ImageArrivalRules.MaxReservedBytes;
                        bool last = attempt == ImageArrivalRules.MaxAttempts || budgetSpent || !retry(error);
                        if (last)
                        {
                            lock (gate) { failedFiles++; AddIssueLocked(item.Path, ImageArrivalRules.ReadFailed, error); }
                            Notify(); finished = true;
                        }
                    }
                    bool spent;
                    lock (gate) spent = reservedBytes == ImageArrivalRules.MaxReservedBytes;
                    if (spent) _ = StopAsync(ArrivalStopReason.ByteLimit);
                    token.ThrowIfCancellationRequested();
                    if (finished) break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) { AddIssueLocked("", ImageArrivalRules.ReadFailed, error); RequestStopLocked(ArrivalStopReason.ReadFailure); }
            cancellation.Cancel();
        }
        finally
        {
            try { cleanup(); }
            catch (Exception error)
            {
                lock (gate) { AddIssueLocked("", ImageArrivalRules.CleanupFailed, error); reason = ArrivalStopReason.CleanupError; }
            }
            lock (gate) { pending.Clear(); status = ArrivalStatus.Stopped; revision++; }
            Notify(); completion.TrySetResult();
            // No WaitHandle is ever requested from signal; it owns no native wait resource.
            // Keep cancellation alive until the collector is reclaimed: Stop/Offer may still be returning.
        }
    }
    private void AddIssueLocked(string path, string message, Exception error)
    {
        string detail = error is ArrivalCleanupException { InnerException: { } cleanup } ? cleanup.Message : error.Message;
        if (detail.Length > 500) detail = detail[..500];
        if (issues.Count < ImageArrivalRules.MaxIssues) issues.Add(new(path, message, detail));
        else omittedIssues++;
        revision++;
    }
    private void Notify()
    {
        // UI subscribers schedule their own dispatcher work; they may never break resource cleanup.
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { } }
    }
}
