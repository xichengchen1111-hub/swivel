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
        // Every listed window of one app: same exact executable path, one request each, in order.
        Lease[] App(params Lease[] leases) => leases;
        CloseWindowsSession Many(Lease[] leases) => CloseWindows.Scan(new Source(new(leases.Select(lease => lease.Info.Handle).ToArray()), window => leases.Single(lease => lease.Info.Handle == window)), default);
        var group = App(new Lease(Info(1)), new Lease(Info(2)), new Lease(Info(3)), new Lease(Info(4) with { ExecutablePath = @"C:\Apps\Helper.exe" }), new Lease(Info(5) with { ExecutablePath = @"C:\APPS\SAMPLE.EXE" }));
        using (var session = Many(group))
        {
            check(session.SameApp(0).SequenceEqual(new[] { 0, 1, 2 }) && session.SameApp(2).SequenceEqual(new[] { 0, 1, 2 }) && session.SameApp(3).SequenceEqual(new[] { 3 })
                && session.SameApp(4).SequenceEqual(new[] { 4 }) && session.SameApp(-1).Count == 0 && session.SameApp(5).Count == 0,
                "Windows of one app are those with exactly the same executable path: helper programs and other spellings are separate");
            check(group.All(lease => lease.Sends == 0 && lease.Inspections == 0), "Finding an app's windows sends nothing and inspects nothing");
            group[1].Current = () => group[1].Info with { Title = "Changed" }; group[2].Sender = () => CloseWindowsResult.Unknown;
            var results = session.RequestEach(session.SameApp(0), default);
            check(results.SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.Processed, CloseWindowsResult.Changed, CloseWindowsResult.Unknown })
                && group[0].Sends == 1 && group[1].Sends == 0 && group[2].Sends == 1 && group[3].Sends == 0 && group[4].Sends == 0,
                "Each window of the app is requested once in order: a changed window is skipped and an uncertain result is kept");
            check(session.SameApp(0).Count == 0 && !session.CanRequest(0) && !session.CanRequest(1) && !session.CanRequest(2) && session.CanRequest(3) && session.CanRequest(4),
                "Requested windows are used up; windows of other paths stay requestable");
            var again = session.RequestEach(new[] { 0, 1, 2, -1, 9 }, default);
            check(again.All(result => result is null) && group[0].Sends == 1 && group[2].Sends == 1, "Used, negative and out-of-range entries are skipped without a request");
        }
        var failing = App(new Lease(Info(1)) { Sender = () => throw new InvalidOperationException("injected send") }, new Lease(Info(2)),
            new Lease(Info(3)) { Sender = () => throw new OperationCanceledException() });
        using (var session = Many(failing))
            check(session.RequestEach(new[] { 0, 1, 2 }, default).SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.Unknown, CloseWindowsResult.Processed, CloseWindowsResult.Unknown })
                && failing.All(lease => lease.Sends == 1), "A failed request counts as uncertain and the next windows still run; an unrequested cancellation is uncertain too");
        using (var cancel = new CancellationTokenSource())
        {
            var leases = App(new Lease(Info(1)), new Lease(Info(2)), new Lease(Info(3)));
            leases[0].Sender = () => { cancel.Cancel(); return CloseWindowsResult.Processed; };
            using var session = Many(leases);
            var results = session.RequestEach(new[] { 0, 1, 2 }, cancel.Token);
            check(results[0] == CloseWindowsResult.Processed && results[1] is null && results[2] is null && leases[1].Inspections == 0 && leases[2].Inspections == 0
                && session.CanRequest(1) && session.CanRequest(2), "Cancellation keeps the sent result and leaves the remaining windows unrequested and unused");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var leases = App(new Lease(Info(1)), new Lease(Info(2)));
            leases[0].Current = () => { cancel.Cancel(); return leases[0].Info; };
            using var session = Many(leases);
            var results = session.RequestEach(new[] { 0, 1 }, cancel.Token);
            check(results.All(result => result is null) && leases.All(lease => lease.Sends == 0) && !session.CanRequest(0) && session.CanRequest(1),
                "Cancellation after a window's last check sends nothing, uses up only that window and stops");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var leases = App(new Lease(Info(1)), new Lease(Info(2))); using var session = Many(leases); cancel.Cancel();
            check(session.RequestEach(new[] { 0, 1 }, cancel.Token).All(result => result is null) && leases.All(lease => lease.Inspections == 0) && session.CanRequest(0) && session.CanRequest(1),
                "A cancelled request for all windows uses nothing");
        }
        var nested = App(new Lease(Info(1)), new Lease(Info(2))); using (var session = Many(nested))
        {
            nested[0].Sender = () =>
            {
                check(session.RequestEach(new[] { 1 }, default).Single() is null && session.CanRequest(1) == false && nested[1].Inspections == 0, "A running request makes another request for all windows skip without using an entry");
                return CloseWindowsResult.Processed;
            };
            _ = session.Request(0, default);
            check(session.CanRequest(1), "The skipped window stays requestable afterwards");
        }
        var gone = App(new Lease(Info(1)), new Lease(Info(2))); var goneSession = Many(gone); goneSession.Dispose();
        check(goneSession.RequestEach(new[] { 0, 1 }, default).All(result => result is null) && goneSession.SameApp(0).Count == 0 && gone.All(lease => lease.Sends == 0), "A disposed session requests nothing for all windows");
        reject(() => goneSession.RequestEach(null!, default), "Requesting all windows needs a list");
        // After an uncertain result, the rest of that process waits: a save prompt may be open.
        var prompt = App(new Lease(Info(1)) { Sender = () => CloseWindowsResult.Unknown }, new Lease(Info(2) with { ProcessId = 101 }), new Lease(Info(3)), new Lease(Info(4) with { ProcessId = 101 }));
        using (var session = Many(prompt))
        {
            var results = session.RequestEach(new[] { 0, 1, 2, 3 }, default);
            check(results.SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.Unknown, CloseWindowsResult.HeldBack, CloseWindowsResult.Processed, CloseWindowsResult.HeldBack })
                && prompt[1].Inspections == 0 && prompt[3].Inspections == 0 && prompt[2].Sends == 1, "After an uncertain window, the other windows of that process are held back without a check or a request");
            check(!session.CanRequest(0) && session.CanRequest(1) && session.CanRequest(3), "Held-back windows stay requestable later, one at a time");
            check(session.HeldBack(1) && session.HeldBack(3) && !session.HeldBack(2) && !session.HeldBack(-1) && !session.HeldBack(9), "Held-back entries are those of a process with an uncertain request");
            check(session.RequestEach(new[] { 3, 3 }, default).SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.HeldBack, null }) && prompt[3].Inspections == 0,
                "A later request for all windows still holds that process back, and a repeated entry is reported once");
            check(session.Request(1, default) == CloseWindowsResult.Processed && prompt[1].Sends == 1, "A later explicit request for one window reaches a held-back window");
        }
        var single = App(new Lease(Info(1)) { Sender = () => CloseWindowsResult.Unknown }, new Lease(Info(2) with { ProcessId = 101 }), new Lease(Info(3)));
        using (var session = Many(single))
        {
            check(session.Request(0, default) == CloseWindowsResult.Unknown && session.HeldBack(1), "An uncertain single request marks its process");
            check(session.RequestEach(session.SameApp(1), default).SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.HeldBack, CloseWindowsResult.Processed }) && single[1].Inspections == 0,
                "A later request for all windows holds back the process of an uncertain single request");
        }
        var singleThrown = App(new Lease(Info(1)) { Sender = () => throw new InvalidOperationException("injected") }, new Lease(Info(2) with { ProcessId = 101 }));
        using (var session = Many(singleThrown))
        {
            reject(() => session.Request(0, default), "A failed single request is reported to the caller");
            check(session.HeldBack(1), "A failed single request marks its process as uncertain too");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var leases = App(new Lease(Info(1)), new Lease(Info(2) with { ProcessId = 101 }));
            leases[0].Sender = () => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); };
            using var session = Many(leases);
            reject(() => session.Request(0, cancel.Token), "A requested cancellation during a send is reported");
            check(!session.HeldBack(1), "A requested cancellation does not mark the process");
        }
        var thrown = App(new Lease(Info(1)) { Sender = () => throw new InvalidOperationException("injected") }, new Lease(Info(2) with { ProcessId = 101 }));
        using (var session = Many(thrown))
            check(session.RequestEach(new[] { 0, 1 }, default).SequenceEqual(new CloseWindowsResult?[] { CloseWindowsResult.Unknown, CloseWindowsResult.HeldBack }), "A failed request holds back the rest of its process too");
        check(CloseWindows.Summary(new CloseWindowsResult?[] { CloseWindowsResult.Processed, CloseWindowsResult.Changed, CloseWindowsResult.Unknown, CloseWindowsResult.HeldBack, null, null }, key => key)
            .Contains("Processed: 1. Changed, not sent: 1. Uncertain: 1. Held back after an uncertain window of the same process: 1. Not sent: 2.", StringComparison.Ordinal)
            && CloseWindows.Summary(Array.Empty<CloseWindowsResult?>(), key => key).StartsWith("Close requests finished.", StringComparison.Ordinal), "The summary counts every kind of result");
        string lines = CloseWindows.ResultLines(new[] { Info(1), Info(2) with { Title = "B\u202e" }, Info(3), Info(4), Info(5) },
            new CloseWindowsResult?[] { CloseWindowsResult.Processed, CloseWindowsResult.Changed, CloseWindowsResult.Unknown, CloseWindowsResult.HeldBack, null }, key => key);
        check(lines.Split(Environment.NewLine).SequenceEqual(new[] { "Processed · Report 1 · 101", "Changed, not sent · B[U+202E] · 102", "Uncertain · Report 3 · 103", "Held back · Report 4 · 104", "Not sent · Report 5 · 105" }),
            "Each requested window's result is listed with its visible title and process");
        reject(() => CloseWindows.ResultLines(new[] { Info(1) }, Array.Empty<CloseWindowsResult?>(), key => key), "Results must match the windows");
        foreach (string text in new[] { CloseWindows.AppConfirmation, CloseWindows.Scope, "Processed", "Changed, not sent", "Uncertain", "Held back", "Not sent", "Close Request Results", "Review All Listed Windows of This App…",
                     "Confirm Close Requests for One App", "Request Normal Close for All", "Requesting the Close action of {0} windows, one after another…", "Windows to request: {0}" })
            check(Strings.Table.ContainsKey(text), "Close-all text translated: " + text[..Math.Min(40, text.Length)]);
        string details = CloseWindows.AppDetails(new[] { Info(1) with { Title = "A\nB" }, Info(2) }, key => key);
        check(details.Contains(@"C:\Apps\Sample.exe", StringComparison.Ordinal) && details.Contains("Windows to request: 2", StringComparison.Ordinal) && details.Contains("A[U+000A]B", StringComparison.Ordinal)
            && details.Contains("Report 2", StringComparison.Ordinal) && CloseWindows.AppDetails(Array.Empty<CloseWindowInfo>(), key => key) == "",
            "The app confirmation lists the path, the count and every visible title");
        check(CloseWindows.AppConfirmation.Contains("exactly this executable path", StringComparison.Ordinal) && CloseWindows.AppConfirmation.Contains("No memory release", StringComparison.Ordinal)
            && CloseWindows.AppConfirmation.Contains("remaining windows of that process are not requested", StringComparison.Ordinal)
            && CloseWindows.Scope.Contains("notification area", StringComparison.Ordinal), "The app confirmation says which windows are left out and that an app may keep running");
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
