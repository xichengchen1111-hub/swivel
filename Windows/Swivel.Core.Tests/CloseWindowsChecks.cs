using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class CloseWindowsChecks
{
    private sealed class Source(CloseWindowsEnumeration enumeration, Func<nint, ICloseWindowLease?> open) : ICloseWindowsSource
    {
        public int Enumerations, Opens;
        public CloseWindowsEnumeration Enumerate(CancellationToken token) { Enumerations++; return enumeration; }
        public ICloseWindowLease? Open(nint window, CancellationToken token) { Opens++; return open(window); }
    }
    private sealed class Lease(CloseWindowInfo info) : ICloseWindowLease
    {
        public CloseWindowInfo Info { get; } = info;
        public Func<CloseWindowInfo?>? Current;
        public Func<CloseWindowsResult>? Sender;
        public bool ThrowOnDispose;
        public int Inspections, Sends, Disposals;
        public CloseWindowInfo? Inspect(CancellationToken token) { token.ThrowIfCancellationRequested(); Inspections++; return Current is null ? Info : Current(); }
        public CloseWindowsResult Send(CancellationToken token) { token.ThrowIfCancellationRequested(); Sends++; return Sender?.Invoke() ?? CloseWindowsResult.Processed; }
        public void Dispose() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("injected dispose " + Info.Handle); }
    }
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        CloseWindowInfo Info(int value = 1) => new((nint)value, (uint)(100 + value), (uint)(200 + value), (ulong)(300 + value), 1, "Report " + value, "TestWindow", @"C:\Apps\Sample.exe");
        CloseWindowsSession Session(Lease lease) => CloseWindows.Scan(new Source(new(new[] { lease.Info.Handle }), _ => lease), default);
        var basic = new Lease(Info()); using (var session = Session(basic))
        {
            check(session.Windows.Count == 1 && session.CanRequest(0) && !session.CanRequest(-1) && !session.CanRequest(1), "Window request session retains a bounded candidate and requires a valid explicit index");
            check(basic.Sends == 0 && basic.Inspections == 0, "Window enumeration does not send or automatically re-inspect a target");
            check(session.Request(0, default) == CloseWindowsResult.Processed && basic.Sends == 1 && basic.Inspections == 1, "Window request checks its snapshot before one send");
            check(!session.CanRequest(0), "Window entry is single attempt after processed request");
            reject(() => session.Request(0, default), "Window requests never automatically retry a used entry");
        }
        check(basic.Disposals == 1, "Window session releases the held process lease");
        foreach (var changed in new[] { Info() with { Handle = 2 }, Info() with { ProcessId = 202 }, Info() with { ThreadId = 999 },
                     Info() with { Created = 999 }, Info() with { SessionId = 2 }, Info() with { Title = "Different" },
                     Info() with { ClassName = "Different" }, Info() with { ExecutablePath = @"C:\Other\Sample.exe" } })
        {
            var lease = new Lease(Info()) { Current = () => changed }; using var session = Session(lease);
            check(session.Request(0, default) == CloseWindowsResult.Changed && lease.Sends == 0 && !session.CanRequest(0), "Every changed window identity field prevents send and consumes the stale entry");
        }
        var absent = new Lease(Info()) { Current = () => null }; using (var session = Session(absent))
            check(session.Request(0, default) == CloseWindowsResult.Changed && absent.Sends == 0, "Exited or unavailable target receives no message");
        foreach (var result in new[] { CloseWindowsResult.Unknown, CloseWindowsResult.Changed })
        {
            var lease = new Lease(Info()) { Sender = () => result }; using var session = Session(lease);
            check(session.Request(0, default) == result && lease.Sends == 1 && !session.CanRequest(0), "Uncertain native result is preserved and never retried");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var lease = new Lease(Info()); using var session = Session(lease); cancel.Cancel();
            reject(() => session.Request(0, cancel.Token), "Window request pre-cancel refuses send");
            check(lease.Inspections == 0 && lease.Sends == 0 && session.CanRequest(0), "Pre-cancelled request did not use the candidate");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var lease = new Lease(Info()); lease.Current = () => { cancel.Cancel(); return lease.Info; }; using var session = Session(lease);
            reject(() => session.Request(0, cancel.Token), "Window request checks cancellation after final managed inspection");
            check(lease.Sends == 0 && !session.CanRequest(0), "Cancellation after inspection consumes stale entry without sending");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var lease = new Lease(Info()) { Sender = () => { cancel.Cancel(); return CloseWindowsResult.Unknown; } }; using var session = Session(lease);
            check(session.Request(0, cancel.Token) == CloseWindowsResult.Unknown && lease.Sends == 1, "Cancellation after send does not misreport the request as never delivered");
        }
        var during = new Lease(Info()); using (var session = Session(during))
        {
            during.Sender = () =>
            {
                check(!session.CanRequest(0), "Concurrent send gate disables all requests");
                reject(session.Dispose, "Live send cannot release process handle underneath the native call");
                reject(() => session.Request(0, default), "Concurrent request is refused");
                return CloseWindowsResult.Processed;
            };
            _ = session.Request(0, default);
        }
        var disposed = new Lease(Info()); var closed = Session(disposed); closed.Dispose(); closed.Dispose();
        check(disposed.Disposals == 1 && !closed.CanRequest(0), "Dispose is idempotent and disables requests");
        reject(() => closed.Request(0, default), "Disposed session refuses request");
        var many = new List<Lease>(); var source = new Source(new(Enumerable.Range(1, 100).Select(i => (nint)i).ToArray()), window => { var value = new Lease(Info((int)window)); many.Add(value); return value; });
        using (var session = CloseWindows.Scan(source, default)) check(session.Windows.Count == 64 && session.Limited && source.Opens == 64, "Window snapshot holds at most 64 process leases");
        check(many.All(lease => lease.Disposals == 1), "Window budget releases every retained lease");
        var duplicateLease = new Lease(Info()); using (var session = CloseWindows.Scan(new Source(new(new nint[] { 0, 0xffff, 1, 1, 2 }), value => value == 1 ? duplicateLease : null), default))
            check(session.Windows.Count == 1 && session.Omitted == 4, "Null broadcast duplicate and unavailable window entries are omitted");
        reject(() => CloseWindows.Scan(new Source(new(Enumerable.Range(1, 4097).Select(i => (nint)i).ToArray()), _ => null), default), "Window enumeration source cannot exceed 4096 handles");
        TimeSpan elapsed = TimeSpan.Zero; var late = new Lease(Info());
        using (var session = CloseWindows.ScanWithClock(new Source(new(new nint[] { 1 }), _ => { elapsed = TimeSpan.FromSeconds(11); return late; }), () => elapsed, default))
            check(session.TimeLimited && session.Windows.Count == 1, "Window snapshot marks a late final native query as soft time limited");
        using (var session = CloseWindows.ScanWithClock(new Source(new(new nint[] { 1 }), _ => throw new Exception()), () => TimeSpan.FromSeconds(10), default))
            check(session.TimeLimited && session.Windows.Count == 0, "Window time budget stops before opening another process");
        var bad = new Lease(Info() with { Handle = 2 }); using (var session = CloseWindows.Scan(new Source(new(new nint[] { 1 }), _ => bad), default))
            check(session.Windows.Count == 0 && bad.Disposals == 1, "Mismatched source handle is rejected and released");
        using (var cancellation = new CancellationTokenSource())
        {
            var lease = new Lease(Info());
            reject(() => CloseWindows.Scan(new Source(new(new nint[] { 1 }), _ => { cancellation.Cancel(); return lease; }), cancellation.Token), "Window scan propagates cancellation after process opening");
            check(lease.Disposals == 1, "Window scan cancellation disposes a newly opened lease");
        }
        foreach (var invalid in new[] { Info() with { Title = "" }, Info() with { Title = new string('x', 257) }, Info() with { ClassName = new string('x', 129) },
                     Info() with { ProcessId = 4 }, Info() with { ThreadId = 0 }, Info() with { Created = 0 }, Info() with { ExecutablePath = @"\\server\share\app.exe" }, Info() with { ExecutablePath = @"C:\Apps\..\app.exe" } })
            check(!CloseWindows.ValidInfo(invalid), "Window snapshot rejects invalid or oversized source metadata");
        check(CloseWindows.ValidInfo(Info() with { Title = new string('x', 256), ClassName = new string('x', 128) }), "Window snapshot accepts exact title and class boundaries");
        check(CloseWindows.Details(Info() with { Title = "A\nB\u202eC" }, key => key).Contains("A[U+000A]B[U+202E]C", StringComparison.Ordinal), "Window confirmation displays control and format characters explicitly");
        check(CloseWindows.IdentityLimit.Contains("not atomic", StringComparison.Ordinal) && CloseWindows.Scope.Contains("without asking", StringComparison.Ordinal) && CloseWindows.Unknown.Contains("may already have reached", StringComparison.Ordinal), "Window UI makes handle races direct-close behavior and delivery uncertainty explicit");
        var failures = new[] { new Lease(Info(1)) { ThrowOnDispose = true }, new Lease(Info(2)), new Lease(Info(3)) { ThrowOnDispose = true } };
        var failureSession = CloseWindows.Scan(new Source(new(new nint[] { 1, 2, 3 }), id => failures[(int)id - 1]), default);
        AggregateException? releaseError = null; try { failureSession.Dispose(); } catch (AggregateException error) { releaseError = error; }
        check(releaseError?.InnerExceptions.Count == 2 && failures.All(lease => lease.Disposals == 1), "Window Dispose attempts every lease and aggregates all cleanup failures");
        failureSession.Dispose(); check(failures.All(lease => lease.Disposals == 1), "Failed aggregate Dispose is still idempotent");
        failures = new[] { new Lease(Info(1)) { ThrowOnDispose = true }, new Lease(Info(2)), new Lease(Info(3)) { ThrowOnDispose = true } };
        var originalFailure = new InvalidOperationException("original source failure"); AggregateException? scanError = null;
        try { _ = CloseWindows.Scan(new Source(new(new nint[] { 1, 2, 3, 4 }), id => id == 4 ? throw originalFailure : failures[(int)id - 1]), default); }
        catch (AggregateException error) { scanError = error; }
        check(scanError?.InnerExceptions.Count == 3 && ReferenceEquals(scanError.InnerExceptions[0], originalFailure) && failures.All(lease => lease.Disposals == 1), "Window failed scan retains original failure and releases all earlier leases despite errors");
        using (var canceled = new CancellationTokenSource())
        {
            var prior = new Lease(Info(1)) { ThrowOnDispose = true }; var opened = new Lease(Info(2)) { ThrowOnDispose = true }; OperationCanceledException? cancellationError = null;
            try { _ = CloseWindows.Scan(new Source(new(new nint[] { 1, 2 }), id => { if (id == 1) return prior; canceled.Cancel(); return opened; }), canceled.Token); }
            catch (OperationCanceledException error) { cancellationError = error; }
            check(cancellationError is not null && cancellationError.CancellationToken == canceled.Token && cancellationError.InnerException is AggregateException && prior.Disposals == 1 && opened.Disposals == 1, "Window cancelled scan preserves cancellation precedence and all cleanup diagnostics");
        }
    }
}
