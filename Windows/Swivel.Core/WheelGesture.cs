using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

public enum WheelGestureModifier { Control, Shift, Alt }
public enum WheelGestureStatus { Starting, Running, Stopping, Stopped, Faulted }
public sealed record WheelGestureIssue(string Message, int? NativeErrorCode = null);
public sealed record WheelGestureSnapshot(Guid SessionId, long Revision, WheelGestureStatus Status,
    IReadOnlyList<WheelGestureIssue> Issues, int OmittedIssues);
public sealed record WheelGestureRequest(Guid SessionId, long Sequence, long Timestamp,
    int PhysicalX, int PhysicalY, bool PrimaryIsRight);

/// <summary>Only the selected modifier's held state. No text or other key history is retained.</summary>
public sealed class WheelGestureEdges
{
    private readonly WheelGestureModifier modifier;
    private int held;
    public WheelGestureEdges(WheelGestureModifier modifier, bool leftHeld = false, bool rightHeld = false)
    {
        if (!Enum.IsDefined(modifier)) throw new ArgumentOutOfRangeException(nameof(modifier));
        this.modifier = modifier;
        held = (leftHeld ? 1 : 0) | (rightHeld ? 2 : 0);
    }
    public bool IsSelectedKey(uint key) => Bit(key) != 0;
    public bool Observe(uint key, bool down, bool injected)
    {
        int bit = Bit(key);
        if (bit == 0 || injected) return false;
        int before = held;
        held = down ? held | bit : held & ~bit;
        // Holding either side suppresses repeats and the second side's down event.
        return down && before == 0 && held != 0;
    }
    private int Bit(uint key) => modifier switch
    {
        WheelGestureModifier.Control => key switch { 0xA2 => 1, 0xA3 => 2, 0x11 => 4, _ => 0 },
        WheelGestureModifier.Shift => key switch { 0xA0 => 1, 0xA1 => 2, 0x10 => 4, _ => 0 },
        WheelGestureModifier.Alt => key switch { 0xA4 => 1, 0xA5 => 2, 0x12 => 4, _ => 0 },
        _ => 0
    };
}

/// <summary>A single pending, expiring display request. It never grants permission to read files.</summary>
public sealed class WheelGestureRequests
{
    public const int MaxAgeMilliseconds = 500;
    private readonly Guid sessionId;
    private WheelGestureRequest? pending;
    private long sequence;
    private int stopped;
    public WheelGestureRequests(Guid sessionId) => this.sessionId = sessionId;
    public bool Offer(long timestamp, int physicalX, int physicalY, bool primaryIsRight)
    {
        if (Volatile.Read(ref stopped) != 0 || timestamp < 0) return false;
        var request = new WheelGestureRequest(sessionId, Interlocked.Increment(ref sequence), timestamp, physicalX, physicalY, primaryIsRight);
        if (Interlocked.CompareExchange(ref pending, request, null) != null) return false;
        if (Volatile.Read(ref stopped) != 0) { Interlocked.Exchange(ref pending, null); return false; }
        return true;
    }
    public bool TryTake(long now, long frequency, bool primaryDown, bool primaryIsRight, out WheelGestureRequest? request)
    {
        request = null;
        var candidate = Interlocked.Exchange(ref pending, null);
        if (candidate == null || Volatile.Read(ref stopped) != 0 || !primaryDown || candidate.PrimaryIsRight != primaryIsRight ||
            frequency <= 0 || now < candidate.Timestamp ||
            (decimal)(now - candidate.Timestamp) * 1000 > (decimal)frequency * MaxAgeMilliseconds) return false;
        request = candidate;
        return true;
    }
    public void Stop()
    {
        Interlocked.Exchange(ref stopped, 1);
        Interlocked.Exchange(ref pending, null);
    }
}

public static class GestureDropRules
{
    public const int MaxFiles = 100, MaxPathLength = 240;
    public static string[] Snapshot(string[] paths)
    {
        if (paths == null || paths.Length is < 1 or > MaxFiles)
            throw new UserError("Drop between 1 and 100 local files.");
        // Snapshot first: the OLE provider owns its array and may change it after Drop returns.
        var result = (string[])paths.Clone();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in result)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.Contains('\0'))
                throw new UserError("A dropped path is empty, too long, or contains a null character.");
            if (!unique.Add(path))
                throw new UserError("The dropped files contain duplicate paths or paths that differ only in letter case. Select each path once.");
        }
        return result;
    }
}
