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
    int PhysicalX, int PhysicalY, bool PrimaryIsRight, uint PointerThreadId = 0);

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
    public bool IsHeld => Volatile.Read(ref held) != 0;
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
    public bool Offer(long timestamp, int physicalX, int physicalY, bool primaryIsRight, uint pointerThreadId = 0)
    {
        if (Volatile.Read(ref stopped) != 0 || timestamp < 0) return false;
        var request = new WheelGestureRequest(sessionId, Interlocked.Increment(ref sequence), timestamp, physicalX, physicalY, primaryIsRight, pointerThreadId);
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

public enum WheelGesturePhase { Armed, WaitingForEvidence, HiddenTarget, Wheel, Dropped, Cancelled }

/// <summary>One gesture, on the UI thread. Times are monotonic milliseconds; terminal states never rearm.</summary>
public sealed class WheelGestureFlow
{
    public const int EvidenceMilliseconds = 500, ProbeMilliseconds = 450, WheelMilliseconds = 30000, ReleaseMilliseconds = 150;
    public WheelGesturePhase Phase { get; private set; } = WheelGesturePhase.Armed;
    private long started, last, released = -1, pageSince;
    private int pageHover;
    public int Page { get; private set; }
    public bool HoverPage(long now, int direction, int pageCount)
    {
        if (Phase != WheelGesturePhase.Wheel || !Tick(now, true)) return false;
        if (direction is < -1 or > 1 || pageCount < 1) return false;
        if (direction != pageHover) { pageHover = direction; pageSince = now; }
        if (direction == 0 || now - pageSince < 700) return false;
        pageSince = now;
        int next = Math.Clamp(Page + direction, 0, pageCount - 1);
        if (next == Page) return false;
        Page = next; return true;
    }
    public bool Begin(long now)
    {
        if (Phase != WheelGesturePhase.Armed || now < 0) return false;
        started = last = now; Phase = WheelGesturePhase.WaitingForEvidence; return true;
    }
    public bool ObserveEvidence(long now, bool primaryDown, bool oleCapture)
    {
        if (!Tick(now, primaryDown)) return false;
        if (Phase == WheelGesturePhase.WaitingForEvidence)
        {
            if (oleCapture) { started = now; Phase = WheelGesturePhase.HiddenTarget; }
        }
        else if (!oleCapture && (Phase == WheelGesturePhase.HiddenTarget || primaryDown)) { Cancel(); return false; }
        return true;
    }
    public bool Tick(long now, bool primaryDown)
    {
        if (Phase is not (WheelGesturePhase.WaitingForEvidence or WheelGesturePhase.HiddenTarget or WheelGesturePhase.Wheel)) return false;
        int lifetime = Phase == WheelGesturePhase.WaitingForEvidence ? EvidenceMilliseconds : Phase == WheelGesturePhase.HiddenTarget ? ProbeMilliseconds : WheelMilliseconds;
        if (now < last || now - started >= lifetime) { Cancel(); return false; }
        last = now;
        if (!primaryDown)
        {
            if (Phase != WheelGesturePhase.Wheel) { Cancel(); return false; }
            if (released < 0) released = now;
        }
        // A second press cannot resurrect a drag whose original button was released.
        if (released >= 0 && now - released >= ReleaseMilliseconds) { Cancel(); return false; }
        return true;
    }
    public bool ConfirmFiles(long now, bool fileDrop)
    {
        if (Phase != WheelGesturePhase.HiddenTarget || !Tick(now, true)) return false;
        if (!fileDrop) { Cancel(); return false; }
        Phase = WheelGesturePhase.Wheel; return true;
    }
    public bool TryDrop(long now, int item)
    {
        if (Phase != WheelGesturePhase.Wheel || !Tick(now, true)) return false;
        if (item is < 0 or >= 8) { Cancel(); return false; }
        Phase = WheelGesturePhase.Dropped; return true;
    }
    public void Cancel() { if (Phase != WheelGesturePhase.Dropped) Phase = WheelGesturePhase.Cancelled; }
}

/// <summary>Cheap evidence of a missed modifier event, not proof that an idle hook is healthy.</summary>
public sealed class WheelGestureHealth
{
    private long mismatch = -1, last;
    public bool Observe(long now, bool hookHeld, bool sampledHeld)
    {
        if (now < last || now < 0) { mismatch = -1; last = Math.Max(0, now); return false; }
        last = now;
        if (hookHeld == sampledHeld) { mismatch = -1; return false; }
        if (mismatch < 0) mismatch = now;
        return now - mismatch >= 1000;
    }
}
