using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Swivel.Core;

public sealed record CloseWindowInfo(nint Handle, uint ProcessId, uint ThreadId, ulong Created, uint SessionId,
    string Title, string ClassName, string ExecutablePath);
public sealed record CloseWindowsEnumeration(IReadOnlyList<nint> Handles, bool Limited = false, bool TimeLimited = false, bool Unavailable = false);
// HeldBack only comes from RequestEach: not sent and not used up, because an earlier window
// of the same process gave an uncertain result in this session.
public enum CloseWindowsResult { Processed, Unknown, Changed, HeldBack }
public interface ICloseWindowLease : IDisposable
{
    CloseWindowInfo Info { get; }
    CloseWindowInfo? Inspect(CancellationToken token);
    // Implementations must repeat their native identity/scope checks immediately
    // before sending. These checks reduce, but cannot eliminate, HWND reuse races.
    CloseWindowsResult Send(CancellationToken token);
}
public interface ICloseWindowsSource
{
    CloseWindowsEnumeration Enumerate(CancellationToken token);
    ICloseWindowLease? Open(nint window, CancellationToken token);
}

public sealed class CloseWindowsSession : IDisposable
{
    private readonly IReadOnlyList<ICloseWindowLease> leases;
    private readonly HashSet<int> attempted = new();
    // Processes with an uncertain request in this session: a save prompt may be open there.
    private readonly HashSet<uint> uncertain = new();
    private readonly object sync = new();
    private bool disposed, requesting;
    public IReadOnlyList<CloseWindowInfo> Windows { get; }
    public DateTimeOffset RecordedUtc { get; } = DateTimeOffset.UtcNow;
    public int Omitted { get; }
    public bool Limited { get; }
    public bool TimeLimited { get; }
    public bool Unavailable { get; }
    internal CloseWindowsSession(List<ICloseWindowLease> leases, int omitted, bool limited, bool timeLimited, bool unavailable)
    {
        this.leases = leases.AsReadOnly(); Windows = Array.AsReadOnly(leases.Select(lease => lease.Info).ToArray());
        Omitted = omitted; Limited = limited; TimeLimited = timeLimited; Unavailable = unavailable;
    }
    public bool CanRequest(int index)
    {
        lock (sync) return !disposed && !requesting && index >= 0 && index < leases.Count && !attempted.Contains(index);
    }
    public CloseWindowsResult Request(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryBegin(index)) throw new InvalidOperationException("A window request is already running or this entry was already attempted.");
        return Attempt(index, token);
    }
    // Takes the single attempt of an entry and the send gate in one step.
    private bool TryBegin(int index)
    {
        lock (sync)
        {
            if (disposed || requesting || index < 0 || index >= leases.Count || !attempted.Add(index)) return false;
            requesting = true; return true;
        }
    }
    private CloseWindowsResult Attempt(int index, CancellationToken token)
    {
        try
        {
            var lease = leases[index];
            if (lease.Inspect(token) != Windows[index]) return CloseWindowsResult.Changed;
            token.ThrowIfCancellationRequested();
            var result = lease.Send(token);
            if (result == CloseWindowsResult.Unknown) MarkUncertain(index);
            return result;
        }
        catch (Exception e) when (!(e is OperationCanceledException && token.IsCancellationRequested)) { MarkUncertain(index); throw; }
        finally { lock (sync) requesting = false; }
    }
    private void MarkUncertain(int index) { lock (sync) uncertain.Add(Windows[index].ProcessId); }
    /// True when an earlier request to a window of this entry's process was uncertain in this
    /// session. RequestEach holds such entries back; Request still sends to one on its own.
    public bool HeldBack(int index)
    {
        lock (sync) return index >= 0 && index < leases.Count && uncertain.Contains(Windows[index].ProcessId);
    }
    /// The entries recorded for the same executable path as `index` (exact, ordinal) that can
    /// still be requested, in list order. Windows of helper processes with other paths are not
    /// included.
    public IReadOnlyList<int> SameApp(int index)
    {
        lock (sync)
        {
            if (disposed || index < 0 || index >= leases.Count) return Array.Empty<int>();
            string path = Windows[index].ExecutablePath;
            return Enumerable.Range(0, leases.Count).Where(i => !attempted.Contains(i) && string.Equals(Windows[i].ExecutablePath, path, StringComparison.Ordinal)).ToArray();
        }
    }
    /// Requests the entries one after another, each as Request does. Null where no message was
    /// sent: an entry already attempted, out of range or refused by a running request or by
    /// Dispose is skipped, and after cancellation the rest are left.
    /// Any other failure counts as Unknown for that entry, since the request may have reached
    /// the app. After an Unknown, here or in an earlier request of this session, the entries of
    /// that process are HeldBack and stay unused: the window may be showing a save prompt, and
    /// another close request would run inside it. A repeated index is reported once.
    public IReadOnlyList<CloseWindowsResult?> RequestEach(IReadOnlyList<int> indices, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var results = new CloseWindowsResult?[indices.Count];
        var seen = new HashSet<int>();
        for (int i = 0; i < indices.Count; i++)
        {
            if (token.IsCancellationRequested) break;
            int index = indices[i];
            if (!seen.Add(index)) continue;
            if (HeldBack(index))
            {
                if (CanRequest(index)) results[i] = CloseWindowsResult.HeldBack;
                continue;
            }
            if (!TryBegin(index)) continue;
            try { results[i] = Attempt(index, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { results[i] = CloseWindowsResult.Unknown; }
        }
        return Array.AsReadOnly(results);
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (requesting) throw new InvalidOperationException("Await the pending window request before disposing its process handles.");
            disposed = true;
        }
        var errors = CloseWindowsLifetime.DisposeAll(leases);
        if (errors.Count > 0) throw new AggregateException("Some window process handles could not be released.", errors);
    }
}

public static class CloseWindows
{
    public const int MaxEnumerated = 4096, MaxWindows = 64, MaxTitle = 256, MaxClass = 128, MaxPath = 240, SendTimeoutMilliseconds = 1500;
    public static readonly TimeSpan SoftTimeLimit = TimeSpan.FromSeconds(10);
    public const string Title = "Request Window Close…";
    public const string Scope = "Choose one visible desktop window, or every listed window of one app, review the titles and the executable path, then explicitly request the normal Close action. Save your work first: the other app may close immediately without asking. This does not force a process to end: an app can keep running in the background or in the notification area after its windows close.";
    public const string Filter = "Only current-user, current-session desktop windows with a readable title and ordinary executable path are offered. Swivel, elevated or critical processes, Windows-directory executables, system shell windows, owned dialogs, tool windows and cloaked windows are excluded. Missing windows are not a diagnosis.";
    public const string Bounds = "This button takes a one-time snapshot of up to 64 eligible windows from at most 4,096 top-level windows, with an approximately 10-second soft limit. Titles may contain private document names. Nothing is copied, saved, refreshed or sent over the network automatically.";
    public const string IdentityLimit = "A selected window is checked again before the request, but Windows can recycle window handles between checking and sending. The check is not atomic. If anything has changed, refresh by closing this list and opening it again.";
    public const string RequestLimit = "Each selected entry can be attempted once. A normal Close message is sent with a 1.5-second timeout; system calls can take longer. An uncertain result may already have reached the app, so no automatic retry is made. Cancellation stops work that has not started and waits for any current request to return.";
    public const string Confirmation = "Request the normal Close action for this exact recorded window? Save any needed work first. The other app may close without a save prompt, ask you to save, cancel, or keep running. No memory release is guaranteed.";
    public const string AppConfirmation = "Request the normal Close action for each of these recorded windows of one app, one after another? Save any needed work first. The app may close without a save prompt, ask you to save, cancel, or keep running in the background or in the notification area. A window that changed is skipped. If a window does not confirm its request within 1.5 seconds, for example because it is asking whether to save, the remaining windows of that process are not requested; check the app first. Only windows in this list with exactly this executable path are included: windows of helper programs, dialogs, windows left out of the list and those of other users or sessions are not. No memory release is guaranteed.";
    public const string Changed = "The window, process or access conditions changed. No close message was sent by this attempt. Open a fresh list before trying again.";
    public const string Processed = "The app processed the close request. It may still be running or showing a save prompt. Check the app; this does not confirm exit or memory release.";
    public const string Unknown = "The close request could not be confirmed. It may already have reached the app or may have been refused. Check the app before doing anything else; this entry will not be retried.";
    public const string Partial = "The window list is incomplete because a limit or unavailable system query was encountered.";

    public static CloseWindowsSession Scan(ICloseWindowsSource source, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); return ScanWithClock(source, () => clock.Elapsed, token);
    }
    public static CloseWindowsSession ScanWithClock(ICloseWindowsSource source, Func<TimeSpan> elapsed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(elapsed);
        var leases = new List<ICloseWindowLease>();
        try
        {
            token.ThrowIfCancellationRequested(); var found = source.Enumerate(token); token.ThrowIfCancellationRequested();
            if (found.Handles.Count > MaxEnumerated) throw new ArgumentException("Window source exceeded its enumeration budget.", nameof(source));
            bool limited = found.Limited, time = found.TimeLimited; int omitted = 0; var seen = new HashSet<nint>();
            foreach (nint window in found.Handles)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed() >= SoftTimeLimit) { time = true; break; }
                if (leases.Count == MaxWindows) { limited = true; break; }
                if (window == 0 || window == 0xffff || !seen.Add(window)) { omitted++; continue; }
                var lease = source.Open(window, token);
                if (lease is null) { omitted++; continue; }
                bool owned = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!ValidInfo(lease.Info) || lease.Info.Handle != window)
                    {
                        omitted++; owned = false;
                        var errors = CloseWindowsLifetime.DisposeAll(new[] { lease });
                        if (errors.Count > 0) throw new AggregateException("A rejected window process handle could not be released.", errors);
                        continue;
                    }
                    leases.Add(lease); owned = false;
                }
                catch (Exception error)
                {
                    if (owned) CloseWindowsLifetime.RethrowAfterCleanup(error, new[] { lease }, token);
                    throw;
                }
            }
            token.ThrowIfCancellationRequested();
            if (elapsed() >= SoftTimeLimit) time = true;
            return new(leases, omitted, limited, time, found.Unavailable);
        }
        catch (Exception error) { CloseWindowsLifetime.RethrowAfterCleanup(error, leases, token); throw; }
    }
    public static bool ValidInfo(CloseWindowInfo info)
    {
        if (info.Handle == 0 || info.Handle == 0xffff || info.ProcessId is 0 or 4 || info.ThreadId == 0 || info.Created == 0 ||
            string.IsNullOrWhiteSpace(info.Title) || info.Title.Length > MaxTitle || string.IsNullOrEmpty(info.ClassName) || info.ClassName.Length > MaxClass) return false;
        try { WindowsPathPolicy.Validate(info.ExecutablePath); return true; } catch (UserError) { return false; }
    }
    /// The review text for closing every listed window of one app.
    public static string AppDetails(IReadOnlyList<CloseWindowInfo> windows, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) return "";
        var lines = new List<string> { localize("Executable Path") + ": " + LibraryJson.Visible(windows[0].ExecutablePath),
            string.Format(CultureInfo.CurrentCulture, localize("Windows to request: {0}"), windows.Count) };
        foreach (var window in windows)
            lines.Add(localize("Window Title") + ": " + LibraryJson.Visible(window.Title) + " · " + localize("Process Identifiers") + ": " + window.ProcessId.ToString(CultureInfo.InvariantCulture));
        return string.Join(Environment.NewLine, lines);
    }
    /// One summary line for RequestEach's results.
    public static string Summary(IReadOnlyList<CloseWindowsResult?> results, Func<string, string> localize)
        => string.Format(CultureInfo.CurrentCulture, localize("Close requests finished. Processed: {0}. Changed, not sent: {1}. Uncertain: {2}. Held back after an uncertain window of the same process: {3}. Not sent: {4}. Check the app; this does not confirm that it quit or released memory."),
            results.Count(r => r == CloseWindowsResult.Processed), results.Count(r => r == CloseWindowsResult.Changed), results.Count(r => r == CloseWindowsResult.Unknown),
            results.Count(r => r == CloseWindowsResult.HeldBack), results.Count(r => r is null));
    /// One line per requested window: its result and its title.
    public static string ResultLines(IReadOnlyList<CloseWindowInfo> windows, IReadOnlyList<CloseWindowsResult?> results, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(windows); ArgumentNullException.ThrowIfNull(results);
        if (windows.Count != results.Count) throw new ArgumentException("Each window needs one result.", nameof(results));
        return string.Join(Environment.NewLine, windows.Select((window, i) => localize(results[i] switch
        {
            CloseWindowsResult.Processed => "Processed",
            CloseWindowsResult.Changed => "Changed, not sent",
            CloseWindowsResult.Unknown => "Uncertain",
            CloseWindowsResult.HeldBack => "Held back",
            _ => "Not sent"
        }) + " · " + LibraryJson.Visible(window.Title) + " · " + window.ProcessId.ToString(CultureInfo.InvariantCulture)));
    }
    public static string Details(CloseWindowInfo info, Func<string, string> localize)
        => localize("Window Title") + ": " + LibraryJson.Visible(info.Title) + Environment.NewLine +
           localize("Executable Path") + ": " + LibraryJson.Visible(info.ExecutablePath) + Environment.NewLine +
           localize("Process Identifiers") + ": " + info.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
           localize("Window Class") + ": " + LibraryJson.Visible(info.ClassName);
}

internal static class CloseWindowsLifetime
{
    internal static List<Exception> DisposeAll(IEnumerable<IDisposable> values)
    {
        var errors = new List<Exception>();
        foreach (var value in values) try { value.Dispose(); } catch (Exception error) { errors.Add(error); }
        return errors;
    }
    internal static void RethrowAfterCleanup(Exception original, IEnumerable<IDisposable> values, CancellationToken token)
    {
        var errors = DisposeAll(values);
        if (errors.Count == 0) { ExceptionDispatchInfo.Capture(original).Throw(); return; }
        errors.Insert(0, original);
        var combined = new AggregateException("The window operation failed and some process handles could not be released.", errors);
        if (original is OperationCanceledException && token.IsCancellationRequested)
            throw new OperationCanceledException(original.Message, combined, token);
        throw combined;
    }
}
