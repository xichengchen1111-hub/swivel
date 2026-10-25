using System;

namespace Swivel.Core;

public enum ClotheslineGestureAction { None, Copy, Preview }

// Only intent and elapsed time: no mouse polling, clipboard, file or window access.
public sealed class ClotheslineGesture
{
    public static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(600);
    private readonly TimeSpan doubleClickDelay;
    private TimeSpan pressedAt, copyAt;
    private bool holdArmed, pendingCopy;
    public bool Pressed { get; private set; }
    public bool Waiting => holdArmed || pendingCopy;

    public ClotheslineGesture(TimeSpan doubleClickDelay)
    {
        if (doubleClickDelay <= TimeSpan.Zero || doubleClickDelay > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(doubleClickDelay));
        this.doubleClickDelay = doubleClickDelay;
    }

    public ClotheslineGestureAction Begin(TimeSpan now, bool isDoubleClick)
    {
        bool preview = isDoubleClick && pendingCopy;
        Cancel();
        if (preview) return ClotheslineGestureAction.Preview;
        // A new press replaces a still-pending click on this same print. Native
        // double-click qualification supplies both the system time and distance rules.
        Pressed = holdArmed = true; pressedAt = now;
        return ClotheslineGestureAction.None;
    }

    public ClotheslineGestureAction CompleteClick(TimeSpan now)
    {
        if (!Pressed) return ClotheslineGestureAction.None;
        // A busy UI may deliver the release before the due timer callback.
        if (holdArmed && now - pressedAt >= HoldDelay)
        {
            Cancel(); return ClotheslineGestureAction.Preview;
        }
        Pressed = holdArmed = false; pendingCopy = true; copyAt = now + doubleClickDelay;
        return ClotheslineGestureAction.None;
    }

    public void EndPress()
    {
        Pressed = holdArmed = false;
    }

    public void Leave()
    {
        // Preserve a held press for drag-out and an already completed click for copy.
        // Returning over the print does not restart its long-press timer.
        holdArmed = false;
    }

    public ClotheslineGestureAction Tick(TimeSpan now)
    {
        if (Pressed && holdArmed && now - pressedAt >= HoldDelay)
        {
            Cancel(); return ClotheslineGestureAction.Preview;
        }
        if (pendingCopy && now >= copyAt)
        {
            Cancel(); return ClotheslineGestureAction.Copy;
        }
        return ClotheslineGestureAction.None;
    }

    public void Cancel() => Pressed = holdArmed = pendingCopy = false;
}
