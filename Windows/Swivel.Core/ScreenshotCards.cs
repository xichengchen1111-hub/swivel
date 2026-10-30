using System;

namespace Swivel.Core;

/// Screenshot cards: the Clothesline's own arrival session pointed at the Screenshots
/// known folder (where Win+PrintScreen and the Snipping Tool's automatic save put files).
/// No system setting is changed, the folder is never created, scanned or written.
public static class ScreenshotCards
{
    /// FOLDERID_Screenshots.
    public static readonly Guid FolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    public const string NoFolder = "Windows has no Screenshots folder yet. Cards start once it exists: take a screenshot with Windows+Print Screen (it creates the folder).";
    public const string Unreported = "Windows did not report a Screenshots folder.";

    /// The watcher runs only while Clothesline and this setting are both on and Swivel is not quitting.
    public static bool ShouldRun(bool clotheslineOn,bool settingOn,bool exiting) => clotheslineOn && settingOn && !exiting;

    /// Whether a finished session should be reported to the person (not after their own stop or Quit).
    public static bool Reportable(ArrivalStopReason reason) =>
        reason is not (ArrivalStopReason.None or ArrivalStopReason.User or ArrivalStopReason.Closed or ArrivalStopReason.FeatureDisabled or ArrivalStopReason.Cancelled);

    /// Session budgets reached in ordinary long use, or notifications lost (the folder was deleted, say):
    /// a fresh session may start (at most once a minute).
    public static bool Renewable(ArrivalStopReason reason) =>
        reason is ArrivalStopReason.WatcherError or ArrivalStopReason.CardLimit or ArrivalStopReason.QueueLimit or ArrivalStopReason.NotificationLimit or ArrivalStopReason.ByteLimit;

    /// Top-left of a card resting in the bottom-right corner of `work` (device-independent units).
    public static (double Left,double Top) Corner(double workLeft,double workTop,double workRight,double workBottom,double width,double height,double margin)
    {
        if (!double.IsFinite(workLeft) || !double.IsFinite(workTop) || !double.IsFinite(workRight) || !double.IsFinite(workBottom) || width <= 0 || height <= 0 || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        double left = Math.Max(workLeft,workRight - width - margin), top = Math.Max(workTop,workBottom - height - margin);
        return (left,top);
    }
}

/// When an unattended card leaves: 2.5 s after it appears, paused while the pointer is over
/// it or it is pressed or dragged, and at least 1 s more after that ends.
public sealed class CardDismissClock
{
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(2.5), Grace = TimeSpan.FromSeconds(1);
    private TimeSpan? deadline;
    private bool hover, pressed;
    public bool Held => hover || pressed;
    public TimeSpan? Deadline => Held ? null : deadline;

    public void Show(TimeSpan now) { hover = false; pressed = false; deadline = now + Idle; }
    public void SetHover(bool on,TimeSpan now) => Set(ref hover,on,now);
    public void SetPressed(bool on,TimeSpan now) => Set(ref pressed,on,now);
    public bool Due(TimeSpan now) => !Held && deadline is { } at && now >= at;

    private void Set(ref bool flag,bool on,TimeSpan now)
    {
        bool before = Held; flag = on;
        if (deadline is null || before == Held || Held) return;
        // Released: keep whatever was left, but never less than the grace second.
        deadline = deadline.Value > now + Grace ? deadline.Value : now + Grace;
    }
}

public enum CardGesture { Pending, Lift, HandOut }

/// A press on a card becomes a lift toward the rope (mostly upward) or a file drag out (any other direction).
public static class CardGestures
{
    public const double LiftCommit = 64;
    public static CardGesture Classify(double dx,double dy,double threshold)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(threshold) || threshold <= 0) return CardGesture.Pending;
        if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold) return CardGesture.Pending;
        return -dy >= threshold && -dy >= 1.5 * Math.Abs(dx) ? CardGesture.Lift : CardGesture.HandOut;
    }
    /// Released while lifting: hang once it travelled far enough upward, otherwise settle back.
    public static bool Hangs(double dy) => double.IsFinite(dy) && -dy >= LiftCommit;
}
