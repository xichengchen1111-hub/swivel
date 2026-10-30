using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

// Read-only: the System event log through EventLogReader (wevtapi), bounded to the last
// 7 days, 200 events and about 10 seconds; plus one CallNtPowerInformation value.
// powercfg is never started: its commands are shown as text only.
internal static class SleepHistoryReader
{
    private const int SystemExecutionState = 16, ErrorTimeout = unchecked((int)0x800705B4);
    private static readonly string[] Paths =
    {
        "Event/EventData/Data[@Name='TargetState']", "Event/EventData/Data[@Name='Reason']",
        "Event/EventData/Data[@Name='WakeSourceText']", "Event/EventData/Data[@Name='SleepTime']", "Event/EventData/Data[@Name='WakeTime']"
    };

    internal static SleepHistorySnapshot ReadHistory(CancellationToken token) => ReadHistory(SleepHistory.Query(), SleepHistory.MaxEvents, SleepHistory.TimeLimit, token);

    // afterRecord is a test hook called after each record is read (before it is filtered).
    internal static SleepHistorySnapshot ReadHistory(string query, int maxEvents, TimeSpan timeLimit, CancellationToken token, Action? afterRecord = null)
    {
        if (maxEvents is < 1 or > SleepHistory.MaxEvents) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        token.ThrowIfCancellationRequested();
        var start = DateTimeOffset.Now.AddDays(-SleepHistory.Days);
        var events = new List<SleepEvent>(); bool truncated = false, timedOut = false;
        var clock = Stopwatch.StartNew();
        // The time limit cancels the native read itself (EvtCancel) as well as the loop, so a
        // slow batch over a large, sparse log is bounded too; events already read are kept.
        using var limit = new CancellationTokenSource(timeLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
        try
        {
            var logQuery = new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true };
            using var reader = new EventLogReader(logQuery);
            using var selector = new EventLogPropertySelector(Paths);
            using var cancel = linked.Token.Register(() => { try { reader.CancelReading(); } catch (Exception) { } });
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = timeLimit - clock.Elapsed;
                if (remaining <= TimeSpan.Zero || limit.IsCancellationRequested) { timedOut = true; break; }
                EventRecord? next;
                // The token is the time limit; the native wait gets slack so it never races it.
                try { next = reader.ReadEvent(remaining + TimeSpan.FromSeconds(2)); }
                catch (Exception e) when (!token.IsCancellationRequested && (limit.IsCancellationRequested || (e is EventLogException && (e.HResult == ErrorTimeout || clock.Elapsed >= timeLimit - TimeSpan.FromMilliseconds(50)))))
                { timedOut = true; break; }
                using EventRecord? record = next;
                if (record is not null) afterRecord?.Invoke();
                token.ThrowIfCancellationRequested();
                if (record is null) { if (clock.Elapsed >= timeLimit) timedOut = true; break; }
                if (events.Count >= maxEvents) { truncated = true; break; }
                var kind = SleepHistory.Kind(record.ProviderName, record.Id);
                if (kind is null || record.TimeCreated is not DateTime created) continue;
                IList<object?> values;
                try { values = record is EventLogRecord logRecord ? logRecord.GetPropertyValues(selector) : new object?[Paths.Length]; }
                catch (Exception e) when (e is not OperationCanceledException) { values = new object?[Paths.Length]; }
                events.Add(new(Time(created), kind.Value, Number(values, 0), Number(values, 1), values.Count > 2 ? values[2] as string : null, OptionalTime(values, 3), OptionalTime(values, 4)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException e) { token.ThrowIfCancellationRequested(); return new(Array.Empty<SleepEvent>(), SleepLogStatus.AccessDenied, e.HResult, false, false, start, DateTimeOffset.Now); }
        catch (EventLogNotFoundException e) { token.ThrowIfCancellationRequested(); return new(Array.Empty<SleepEvent>(), SleepLogStatus.Unavailable, e.HResult, false, false, start, DateTimeOffset.Now); }
        catch (EventLogProviderDisabledException e) { token.ThrowIfCancellationRequested(); return new(Array.Empty<SleepEvent>(), SleepLogStatus.Unavailable, e.HResult, false, false, start, DateTimeOffset.Now); }
        catch (EventLogException e) { token.ThrowIfCancellationRequested(); return new(Array.Empty<SleepEvent>(), SleepLogStatus.Failed, e.HResult, false, false, start, DateTimeOffset.Now); }
        token.ThrowIfCancellationRequested();
        return new(events, SleepLogStatus.Read, null, truncated, timedOut, start, DateTimeOffset.Now);
    }

    // Keep Awake state is captured on the UI thread by the caller and passed in.
    internal static StayAwakeSnapshot ReadStayAwake(bool keepAwake, bool display, DateTimeOffset? until, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int status = CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out uint state, sizeof(uint));
        token.ThrowIfCancellationRequested();
        return new(status == 0 ? state : null, status == 0 ? null : status, keepAwake, keepAwake && display, keepAwake ? until : null, DateTimeOffset.Now);
    }

    private static DateTimeOffset Time(DateTime value) => value.Kind == DateTimeKind.Unspecified ? new(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : new(value);
    private static DateTimeOffset? OptionalTime(IList<object?> values, int index) => values.Count > index && values[index] is DateTime t ? Time(t) : null;
    private static long? Number(IList<object?> values, int index)
    {
        if (values.Count <= index) return null;
        return values[index] switch
        {
            byte v => v, sbyte v => v, ushort v => v, short v => v, uint v => v, int v => v, long v => v,
            ulong v when v <= long.MaxValue => (long)v,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => null
        };
    }

    // NTSTATUS CallNtPowerInformation(POWER_INFORMATION_LEVEL, PVOID, ULONG, PVOID, ULONG); SystemExecutionState returns one ULONG.
    [DllImport("powrprof.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CallNtPowerInformation(int level, IntPtr input, uint inputSize, out uint output, uint outputSize);
}
