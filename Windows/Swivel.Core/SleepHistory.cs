using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

public enum SleepEventKind { EnteringSleep, Resumed, ReturnedFromLowPower }
// TargetState/Reason are kept as the raw numbers Windows recorded; their meanings are not documented.
public sealed record SleepEvent(DateTimeOffset Time, SleepEventKind Kind, long? TargetState, long? Reason, string? WakeSource, DateTimeOffset? SleepTime, DateTimeOffset? WakeTime);
public enum SleepLogStatus { Read, AccessDenied, Unavailable, Failed }

public sealed class SleepHistorySnapshot
{
    public IReadOnlyList<SleepEvent> Events { get; }
    public SleepLogStatus Status { get; }
    public int? ErrorCode { get; }
    // More matching events existed than the bound, or the time limit stopped reading.
    public bool Truncated { get; }
    public bool TimedOut { get; }
    public DateTimeOffset WindowStart { get; }
    public DateTimeOffset Captured { get; }
    public SleepHistorySnapshot(IEnumerable<SleepEvent> events, SleepLogStatus status, int? errorCode, bool truncated, bool timedOut, DateTimeOffset windowStart, DateTimeOffset captured)
    {
        var rows = events.ToArray();
        if (rows.Length > SleepHistory.MaxEvents || !Enum.IsDefined(status) || (status == SleepLogStatus.Read) != (errorCode is null) || rows.Any(x => !Enum.IsDefined(x.Kind))) throw new ArgumentException("Invalid sleep history snapshot.");
        Events = Array.AsReadOnly(rows.OrderByDescending(x => x.Time).ToArray()); Status = status; ErrorCode = errorCode; Truncated = truncated; TimedOut = timedOut; WindowStart = windowStart; Captured = captured;
    }
}

public sealed record WakeSourceCount(string Source, int Count);
public sealed record SleepHistorySummary(int Sleeps, int Resumes, int Wakes, DateTimeOffset? LastSleep, DateTimeOffset? LastResume, string? LastWakeSource, TimeSpan? LongestSleep, IReadOnlyList<WakeSourceCount> WakeSources);

// System-wide execution-state value and Swivel's own Keep Awake; per-app requests need an administrator.
public sealed record StayAwakeSnapshot(uint? ExecutionState, int? ExecutionStateStatus, bool KeepAwakeActive, bool KeepAwakeDisplay, DateTimeOffset? KeepAwakeUntil, DateTimeOffset Captured);

public static class SleepHistory
{
    public const int Days = 7, MaxEvents = 200, MaxWakeSources = 5, MaxSourceChars = 160;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);
    public const string KernelPower = "Microsoft-Windows-Kernel-Power", Troubleshooter = "Microsoft-Windows-Power-Troubleshooter";
    public const string RequestsCommand = "powercfg /requests", LastWakeCommand = "powercfg /lastwake";

    public const string Scope = "One explicit read-only look at what may be keeping this PC awake and at the System event log's sleep and wake events from the last 7 days (at most 200 events, newest first). Event entries are read through the standard Windows event log interface; no log is cleared or changed.";
    public const string Limits = "Windows only lists which apps or drivers are currently asking the PC to stay awake to an administrator, and Swivel never runs administrator commands. Without one, Swivel shows the system-wide execution state Windows reports and its own Keep Awake; other programs can still prevent sleep. Event log entries can be missing when the log was cleared, rotated or disabled, and some PCs (Modern Standby) record sleep differently. Wake sources are the text Windows recorded and can be general (for example Unknown).";
    public const string AdminNote = "To see the apps and drivers currently preventing sleep, or the device that last woke the PC, you can run these commands yourself in an administrator terminal. Swivel only shows them; it never runs them.";
    public const string ExecutionStateNote = "The reported execution state is a system-wide value; it does not name the app that set it, and Windows does not document it as a complete list of sleep blockers.";

    // XPath for EventLogQuery on the System log: three event kinds within the last Days.
    public static string Query(int days = Days)
    {
        if (days is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(days));
        long ms = days * 86_400_000L;
        return "*[System[((Provider[@Name='" + KernelPower + "'] and (EventID=42 or EventID=107)) or (Provider[@Name='" + Troubleshooter + "'] and EventID=1)) and TimeCreated[timediff(@SystemTime) <= " + ms.ToString(CultureInfo.InvariantCulture) + "]]]";
    }

    public static SleepEventKind? Kind(string? provider, int id) => provider switch
    {
        KernelPower when id == 42 => SleepEventKind.EnteringSleep,
        KernelPower when id == 107 => SleepEventKind.Resumed,
        Troubleshooter when id == 1 => SleepEventKind.ReturnedFromLowPower,
        _ => null
    };
    public static string Name(SleepEventKind kind) => kind switch
    {
        SleepEventKind.EnteringSleep => "Entering sleep (Kernel-Power 42)",
        SleepEventKind.Resumed => "Resumed from sleep (Kernel-Power 107)",
        SleepEventKind.ReturnedFromLowPower => "Returned from low power (Power-Troubleshooter 1)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static string Status(SleepLogStatus status) => status switch
    {
        SleepLogStatus.Read => "Read",
        SleepLogStatus.AccessDenied => "Access to the System event log was denied for this account.",
        SleepLogStatus.Unavailable => "The System event log or its power providers are not available.",
        SleepLogStatus.Failed => "The System event log could not be read.",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string Source(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Not recorded";
        return DoctorFacts.VisibleBounded(text.Trim(), MaxSourceChars);
    }

    public static SleepHistorySummary Summarize(IEnumerable<SleepEvent> events)
    {
        var rows = events.OrderByDescending(x => x.Time).ToArray();
        var sleeps = rows.Where(x => x.Kind == SleepEventKind.EnteringSleep).ToArray();
        var resumes = rows.Where(x => x.Kind == SleepEventKind.Resumed).ToArray();
        var wakes = rows.Where(x => x.Kind == SleepEventKind.ReturnedFromLowPower).ToArray();
        TimeSpan? longest = null;
        foreach (var w in wakes)
            if (w.SleepTime is DateTimeOffset s && w.WakeTime is DateTimeOffset e && e >= s && (longest is null || e - s > longest)) longest = e - s;
        var sources = wakes.GroupBy(x => Source(x.WakeSource), StringComparer.Ordinal)
            .Select(g => new WakeSourceCount(g.Key, g.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Source, StringComparer.Ordinal).Take(MaxWakeSources).ToArray();
        DateTimeOffset? lastWake = resumes.Concat(wakes).Select(x => (DateTimeOffset?)x.Time).Max();
        return new(sleeps.Length, resumes.Length, wakes.Length, sleeps.FirstOrDefault()?.Time, lastWake,
            wakes.Length == 0 ? null : Source(wakes[0].WakeSource), longest, Array.AsReadOnly(sources));
    }

    public static IReadOnlyList<string> ExecutionFlags(uint state)
    {
        var names = new List<string>();
        if ((state & 0x1) != 0) names.Add("System required");
        if ((state & 0x2) != 0) names.Add("Display required");
        if ((state & 0x4) != 0) names.Add("User present");
        if ((state & 0x40) != 0) names.Add("Away mode required");
        if ((state & 0x80000000) != 0) names.Add("Continuous");
        if ((state & ~0x80000047u) != 0) names.Add("Other bits");
        return names;
    }
}
