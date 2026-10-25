using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Swivel.Core;

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public readonly record struct PhysicalMemory(ulong Total, ulong Available);
public enum CpuSampleStatus { Available, Unavailable, MultipleProcessorGroups }
public sealed record UsageReading(CpuTimes? Cpu, PhysicalMemory? Memory, CpuSampleStatus CpuStatus);
public sealed record UsagePoint(double Seconds, double? CpuPercent, double? MemoryPercent, ulong? UsedBytes, ulong? TotalBytes, CpuSampleStatus CpuStatus);

public static class UsageMath
{
    // Kernel time includes idle. Decimal arithmetic avoids overflow when adding
    // two unsigned FILETIME deltas or multiplying a large physical byte count.
    public static double? CpuPercent(CpuTimes? before, CpuTimes? after)
    {
        if (before is not CpuTimes a || after is not CpuTimes b || a.Idle > a.Kernel || b.Idle > b.Kernel || b.Idle < a.Idle || b.Kernel < a.Kernel || b.User < a.User) return null;
        decimal idle = b.Idle - a.Idle, kernel = b.Kernel - a.Kernel, user = b.User - a.User;
        decimal total = kernel + user;
        if (total <= 0 || idle > kernel) return null;
        return (double)((total - idle) * 100 / total);
    }
    public static double? MemoryPercent(PhysicalMemory? memory) => memory is PhysicalMemory m && m.Total > 0 && m.Available <= m.Total
        ? (double)(((decimal)m.Total - m.Available) * 100 / m.Total) : null;
}

// Only the latest two minutes and at most 61 actual samples are retained. Gaps
// are real missing values; neither failed reads nor a pause become a zero line.
public sealed class UsageHistory
{
    public const int MaxPoints = 61;
    public const double WindowSeconds = 120;
    private readonly List<UsagePoint> points = new();
    private CpuTimes? previous;
    private double lastSeconds = -1;
    public IReadOnlyList<UsagePoint> Points => points.AsReadOnly();
    public void Break(double seconds)
    {
        ValidateTime(seconds);
        previous = null;
        AddPoint(new(seconds, null, null, null, null, CpuSampleStatus.Unavailable));
    }
    public UsagePoint Add(UsageReading reading, double seconds)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ValidateTime(seconds);
        CpuTimes? cpu = reading.CpuStatus == CpuSampleStatus.Available ? reading.Cpu : null;
        double? percent = UsageMath.CpuPercent(previous, cpu);
        previous = cpu;
        double? memory = UsageMath.MemoryPercent(reading.Memory);
        var point = new UsagePoint(seconds, percent, memory,
            memory.HasValue ? reading.Memory!.Value.Total - reading.Memory.Value.Available : null,
            memory.HasValue ? reading.Memory!.Value.Total : null, reading.CpuStatus);
        AddPoint(point); return point;
    }
    private void ValidateTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds < lastSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
    }
    private void AddPoint(UsagePoint point)
    {
        ValidateTime(point.Seconds); lastSeconds = point.Seconds;
        points.Add(point);
        points.RemoveAll(x => x.Seconds < point.Seconds - WindowSeconds);
        if (points.Count > MaxPoints) points.RemoveRange(0, points.Count - MaxPoints);
    }
}

// Single-flight worker. Pausing invalidates an in-flight result without starting
// another read. Disposal cancels and joins the outstanding native call, which may
// only observe cancellation once the operating-system API returns.
public sealed class UsageSampler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, UsageReading> read;
    private readonly CancellationTokenSource cancellation;
    private Task<UsageReading?> active = Task.FromResult<UsageReading?>(null);
    private Task? stopping;
    private bool enabled, stopped;
    private long generation;
    public UsageSampler(Func<CancellationToken, UsageReading> read, CancellationToken token)
    { this.read = read ?? throw new ArgumentNullException(nameof(read)); cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); }
    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (stopped || enabled == value) return;
            enabled = value; generation++;
        }
    }
    public Task<UsageReading?> SampleAsync()
    {
        lock (gate)
        {
            if (stopped || !enabled || cancellation.IsCancellationRequested || !active.IsCompleted) return Task.FromResult<UsageReading?>(null);
            active = RunAsync(generation); return active;
        }
    }
    private async Task<UsageReading?> RunAsync(long epoch)
    {
        UsageReading value;
        try { value = await Task.Run(() => { cancellation.Token.ThrowIfCancellationRequested(); return read(cancellation.Token); }).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        lock (gate) return !stopped && enabled && epoch == generation && !cancellation.IsCancellationRequested ? value : null;
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (stopping is null)
            {
                stopped = true; enabled = false; generation++;
                cancellation.Cancel(); stopping = JoinAsync(active);
            }
            return new ValueTask(stopping);
        }
    }
    private async Task JoinAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
}
