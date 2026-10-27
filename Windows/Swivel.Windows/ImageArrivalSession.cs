using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal interface IImageArrivalSession
{
    ArrivalSnapshot<BitmapSource> Snapshot { get; }
    event Action? Changed;
    void Start();
    Task StopAsync(ArrivalStopReason reason = ArrivalStopReason.User);
    Task Completion { get; }
    void Dismiss(Guid id);
    void ClearCards();
}

/// Explicit, session-only monitoring of current paths reported AFTER Start. This never
/// enumerates the directory or claims an event identifies a newly created screenshot.
internal sealed class ImageArrivalSession : IImageArrivalSession
{
    private readonly NativeFiles.DirectoryLease root;
    private readonly FileSystemWatcher watcher;
    private readonly ArrivalCollector<BitmapSource> collector;
    private readonly string directory;
    private readonly object callbacks = new();
    private readonly List<Exception> cleanupFailures = new(3);
    private bool closing;
    private int activeCallbacks;

    public ArrivalSnapshot<BitmapSource> Snapshot => collector.Snapshot;
    public event Action? Changed { add => collector.Changed += value; remove => collector.Changed -= value; }
    public Task Completion => collector.Completion;

    private ImageArrivalSession(string directory, NativeFiles.DirectoryLease root, FileSystemWatcher watcher)
    {
        this.directory = directory; this.root = root; this.watcher = watcher;
        collector = new(directory, ReadThumbnailAsync, Cleanup, error => error is not UserError and not OutOfMemoryException);
        watcher.Created += Created;
        watcher.Renamed += Renamed;
        watcher.Error += WatcherError;
    }

    // Call on a worker. Cancellation here governs creation; the window owns StopAsync
    // for its running session and joins any late-created session before closing.
    public static ImageArrivalSession Create(string directory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (directory.Length > 3 && directory.EndsWith('\\')) directory = directory[..^1];
        WindowsPathPolicy.Validate(directory.Length == 3 && directory[1] == ':' && directory[2] == '\\' ? directory + "_" : directory);
        NativeFiles.DirectoryLease? root = null;
        FileSystemWatcher? watcher = null;
        var failures = new List<Exception>(3);
        try
        {
            root = NativeFiles.DirectoryLease.Open(directory);
            cancellation.ThrowIfCancellationRequested();
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = false
            };
            cancellation.ThrowIfCancellationRequested();
            return new(directory, root, watcher);
        }
        catch (Exception error)
        {
            failures.Add(error);
            try { watcher?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            try { root?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
    }
    public void Start()
    {
        collector.Start();
        try
        {
            lock (callbacks)
            {
                // Stop may race the Start call on the UI thread. Never re-enable after cleanup.
                if (closing || collector.Snapshot.Status != ArrivalStatus.Running) return;
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception error) { collector.FailWatcher(error); }
    }
    public Task StopAsync(ArrivalStopReason reason = ArrivalStopReason.User) => collector.StopAsync(reason);
    public void Dismiss(Guid id) => collector.Dismiss(id);
    public void ClearCards() => collector.ClearCards();
    private void Created(object sender, FileSystemEventArgs args) => Receive(args.Name, ArrivalKind.Created);
    private void Renamed(object sender, RenamedEventArgs args) => Receive(args.Name, ArrivalKind.Renamed);
    private bool EnterCallback()
    {
        lock (callbacks) { if (closing) return false; activeCallbacks++; return true; }
    }
    private void LeaveCallback()
    {
        lock (callbacks) { activeCallbacks--; Monitor.PulseAll(callbacks); }
    }
    private void Receive(string? name, ArrivalKind kind)
    {
        if (!EnterCallback()) return;
        try
        {
            if (name is null || !ImageArrivalRules.IsImageName(name)) return;
            WindowsPathPolicy.ValidateName(name); // Only one leaf, never a subtree or OldFullPath.
            string path = Path.Combine(directory, name);
            WindowsPathPolicy.Validate(path);
            collector.Offer(path, kind);
        }
        catch (Exception error) { collector.FailWatcher(error); }
        finally { LeaveCallback(); }
    }
    private void WatcherError(object sender, ErrorEventArgs args)
    {
        if (!EnterCallback()) return;
        try { collector.FailWatcher(args.GetException()); }
        finally { LeaveCallback(); }
    }
    private void Cleanup()
    {
        lock (callbacks) closing = true;
        var failures = cleanupFailures;
        try { watcher.EnableRaisingEvents = false; } catch (Exception error) { failures.Add(error); }
        watcher.Created -= Created; watcher.Renamed -= Renamed; watcher.Error -= WatcherError;
        try { watcher.Dispose(); } catch (Exception error) { failures.Add(error); }
        // Cleanup runs on the consumer, never inside the callback that requested Stop.
        // New callbacks return before touching resources; already admitted callbacks are joined.
        lock (callbacks) { while (activeCallbacks != 0) Monitor.Wait(callbacks); }
        try { root.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(ImageArrivalRules.CleanupFailed, failures);
    }
    private static Task<BitmapSource> ReadThumbnailAsync(string path, Action<long> reserve, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(ReadThumbnail(path, reserve, cancellation)); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Swivel image arrival reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    internal static BitmapSource ReadThumbnail(string path, Action<long> reserve, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        NativeFiles.Input? input = null;
        Exception? failure = null;
        BitmapSource? result = null;
        var cleanupFailures = new List<Exception>(3);
        try
        {
            input = NativeFiles.OpenRead(path);
            cancellation.ThrowIfCancellationRequested();
            reserve(input.Stream.Length); // A failed decode or retry never refunds this reservation.
            cancellation.ThrowIfCancellationRequested();
            BitmapSource source = ImageTools.Decode(input, cancellation);
            double scale = Math.Min(1, Math.Min(248.0 / source.PixelWidth, 206.0 / source.PixelHeight));
            int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            BitmapSource resized = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
            if (resized.Format != PixelFormats.Bgra32) resized = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            cancellation.ThrowIfCancellationRequested();
            resized.CopyPixels(pixels, stride, 0);
            cancellation.ThrowIfCancellationRequested();
            // Copy only the tile pixels: keeping TransformedBitmap would retain the full source.
            result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
        }
        catch (Exception error) { failure = error; }
        if (input is not null)
        {
            // Keep attempting every owned release even if an earlier Dispose fails.
            try { input.Stream.Dispose(); } catch (Exception error) { cleanupFailures.Add(error); }
            try { input.Parent.Dispose(); } catch (Exception error) { cleanupFailures.Add(error); }
        }
        if (cleanupFailures.Count != 0)
        {
            if (failure is not null) cleanupFailures.Insert(0, failure);
            throw new ArrivalCleanupException(new AggregateException(cleanupFailures));
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}
