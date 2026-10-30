using System;
using System.Collections.Generic;

namespace Swivel.Core;

/// Numbers for the top-edge rope. Every oscillator is a MotionSpring (analytic,
/// retargetable) with damping ratio at least 0.6, so a swing is barely seen twice
/// and comes to rest well inside 1.5 seconds. Checked in RopeMotionChecks.
public static class RopeTuning
{
    public const double DropResponse = .55, DropDamping = .78;   // the whole rope lowering and lifting
    public const double SagResponse = .6, SagDamping = .65;      // the cord's extra dip in the middle
    public const double SwingResponse = .85, SwingDamping = .66; // each print's pendulum on its peg
    public const double RestSag = 10;                            // DIP the middle hangs below the ends at rest
    public const double DropSagKick = 70;                        // DIP/s of extra dip as the rope arrives
    public const double DropSwingKick = 22;                      // degrees/s, staggered and alternating
    public const double ArrivalSwingKick = 28;                   // degrees/s for a print hung while the rope is down
    public const double MaxSwing = 6;                            // degrees; larger pushes are clamped
    // Below these the motion is invisible; the spring snaps and the frame loop may stop.
    public const double DropRest = .002, DropRestVelocity = .02;
    public const double SagRest = .1, SagRestVelocity = .5;
    public const double SwingRest = .05, SwingRestVelocity = .3;
    /// A frame gap longer than this (a stalled window, a sleep) snaps everything to rest.
    public const double MaxFrame = .25;
    /// A visible swing: an excursion of at least this share of the first one.
    public const double VisibleShare = .05;

    /// Push for print `index` of `count` when the rope lowers: neighbours swing opposite ways, edges a little more.
    public static double DropKick(int index,int count)
    {
        if (count <= 0 || index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        double centre = (count - 1) / 2.0, spread = count == 1 ? 0 : Math.Abs(index - centre) / centre;
        return (index % 2 == 0 ? 1 : -1) * DropSwingKick * (.75 + .25 * spread);
    }
}

/// The rope's state: how far it is lowered (0 hidden … 1 down), the extra dip in the
/// middle, and one swing angle per print. Pure; the window advances it per frame.
public sealed class RopeMotion
{
    private readonly MotionSpring drop = new(0,RopeTuning.DropResponse,RopeTuning.DropDamping);
    private readonly MotionSpring sag = new(RopeTuning.RestSag,RopeTuning.SagResponse,RopeTuning.SagDamping);
    private readonly List<MotionSpring> swings = new();
    private bool reduced;

    /// Reduce Motion: no travel, no swing; lowering and lifting snap.
    public bool Reduced { get => reduced; set { reduced = value; if (value) SnapAll(); } }
    public double Drop => drop.Value;
    public double Sag => sag.Value;
    public int Count => swings.Count;
    public double Swing(int index) => swings[index].Value;
    public bool Lowered => drop.Target == 1;
    /// Lifted all the way: the window can be hidden.
    public bool Hidden => drop.Target == 0 && drop.Value == 0 && drop.Velocity == 0;
    public bool Moving { get; private set; }

    /// Keeps the swing of prints that stay; new ones start at rest.
    public void SetPrints(int count)
    {
        if (count is < 0 or > ClotheslineRules.Limit) throw new ArgumentOutOfRangeException(nameof(count));
        while (swings.Count > count) swings.RemoveAt(swings.Count - 1);
        while (swings.Count < count) swings.Add(new MotionSpring(0,RopeTuning.SwingResponse,RopeTuning.SwingDamping));
    }

    /// Caught while lifting, the rope turns around where it is instead of starting over.
    public void Lower()
    {
        bool fromAbove = drop.Target == 0 && drop.Value < .5;
        drop.Target = 1;
        if (reduced) { SnapAll(); return; }
        if (fromAbove)
        {
            sag.Nudge(RopeTuning.DropSagKick);
            for (int i = 0; i < swings.Count; i++) Push(i,RopeTuning.DropKick(i,swings.Count));
        }
        Moving = true;
    }

    /// Puts the rope back up at once (moving to another monitor), so the next Lower drops it in again.
    public void Raise() { drop.Target = 0; drop.Snap(); }

    public void Lift()
    {
        drop.Target = 0;
        if (reduced) { SnapAll(); return; }
        Moving = true;
    }

    /// A print hung or stitched while the rope is down.
    public void Arrive(int index) => Push(index,RopeTuning.ArrivalSwingKick * (index % 2 == 0 ? 1 : -1));

    public void Push(int index,double velocity)
    {
        if (index < 0 || index >= swings.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (reduced) return;
        var spring = swings[index];
        // A push never makes a swing larger than MaxSwing: with x the current angle, the undamped
        // amplitude sqrt(x² + (v/ω)²) bounds the next excursion, so v is limited to keep it at MaxSwing.
        double omega = 2 * Math.PI / RopeTuning.SwingResponse, x = Math.Min(Math.Abs(spring.Value - spring.Target),RopeTuning.MaxSwing);
        double limit = omega * Math.Sqrt(RopeTuning.MaxSwing * RopeTuning.MaxSwing - x * x);
        double wanted = Math.Clamp(spring.Velocity + velocity,-limit,limit);
        // Never adds speed against the clamp: an already faster swing is left as it is.
        if (Math.Abs(wanted) < Math.Abs(spring.Velocity) && Math.Sign(velocity) == Math.Sign(spring.Velocity)) return;
        spring.Nudge(wanted - spring.Velocity);
        Moving = true;
    }

    /// Advances by a measured frame length; returns whether anything still moves.
    public bool Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (reduced || seconds > RopeTuning.MaxFrame) { SnapAll(); return false; }
        drop.Advance(seconds); sag.Advance(seconds);
        foreach (var swing in swings) swing.Advance(seconds);
        bool rest = drop.SettleWithin(RopeTuning.DropRest,RopeTuning.DropRestVelocity);
        rest &= sag.SettleWithin(RopeTuning.SagRest,RopeTuning.SagRestVelocity);
        foreach (var swing in swings) rest &= swing.SettleWithin(RopeTuning.SwingRest,RopeTuning.SwingRestVelocity);
        Moving = !rest;
        return Moving;
    }

    private void SnapAll() { drop.Snap(); sag.Snap(); foreach (var swing in swings) swing.Snap(); Moving = false; }
}

/// Where the prints hang on a rope of a given width (DIP).
public static class RopeLayout
{
    public const double Height = 236, RopeY = 18, Print = 118, Gap = 14, Margin = 24;
    /// Print size and the x of each peg, centred; prints shrink on narrow screens rather than overflow.
    public static (double Size,double[] Pegs) Pegs(int count,double width)
    {
        if (count is < 0 or > ClotheslineRules.Limit) throw new ArgumentOutOfRangeException(nameof(count));
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (count == 0) return (Print,Array.Empty<double>());
        double usable = Math.Max(0,width - 2 * Margin);
        double size = Math.Clamp(usable / count - Gap,36,Print), pitch = size + Gap, start = width / 2 - pitch * (count - 1) / 2;
        var pegs = new double[count];
        for (int i = 0; i < count; i++) pegs[i] = start + i * pitch;
        return (size,pegs);
    }
    /// How far below the ends the rope hangs at x: a parabola through both ends.
    public static double Dip(double x,double width,double sag)
    {
        if (width <= 0) return 0;
        double t = Math.Clamp(x / width,0,1);
        return 4 * sag * t * (1 - t);
    }
}

/// A screen rectangle in physical pixels; Right and Bottom are exclusive.
public readonly record struct ScreenRect(int Left,int Top,int Right,int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x,int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public bool Covers(ScreenRect other) => Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
}

public enum RopeEdgeAction { None, Show, Hide }

/// When the top-edge rope shows and hides. Fed one cursor sample per poll.
/// Shows after the pointer rests in the top two pixel rows of a monitor for Dwell,
/// unless a full-screen app is in front there. Hides once the pointer has been
/// outside the rope for LeaveDelay, unless a print is being used. Hidden while the
/// pointer is at the edge, the pointer must leave the edge before the rope can come back.
public sealed class RopeEdgeTrigger
{
    public const int EdgeRows = 2;
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(1);
    private TimeSpan? edgeSince, leftSince, last;
    private bool armed = true;
    public bool Showing { get; private set; }
    /// The monitor the rope is on while showing.
    public ScreenRect? Monitor { get; private set; }

    public static bool AtEdge(int x,int y,ScreenRect monitor) => monitor.Contains(x,y) && y < monitor.Top + EdgeRows;

    /// A window in front counts as full screen when it covers the whole monitor and is
    /// not the desktop, one of Swivel's own windows, or a maximized or captioned frame
    /// (a maximized window's rectangle includes invisible borders and can cover a
    /// monitor without a visible taskbar).
    public static bool IsFullScreen(ScreenRect? foreground,ScreenRect monitor,bool sameMonitor,bool desktopOrOwn,bool framed) =>
        foreground is { } window && sameMonitor && !desktopOrOwn && !framed && monitor.Width > 0 && monitor.Height > 0 && window.Covers(monitor);

    /// `monitor` is the monitor under the pointer; `rope` the rope window's rectangle while showing;
    /// `engaged` is true while a print is pressed, dragged, open in a menu or running an action.
    public RopeEdgeAction Update(TimeSpan now,int x,int y,ScreenRect monitor,bool fullScreen,ScreenRect? rope,bool engaged)
    {
        // A clock that goes backwards restarts the waits instead of firing early.
        if (last is { } previous && now < previous) { edgeSince = null; leftSince = null; }
        last = now;
        bool atEdge = AtEdge(x,y,monitor);
        if (!atEdge) armed = true;
        if (!Showing)
        {
            if (!atEdge || !armed || fullScreen) { edgeSince = null; return RopeEdgeAction.None; }
            edgeSince ??= now;
            if (now - edgeSince.Value < Dwell) return RopeEdgeAction.None;
            Showing = true; Monitor = monitor; edgeSince = null; leftSince = null;
            return RopeEdgeAction.Show;
        }
        if (fullScreen && !engaged && Monitor == monitor) return Hide(atEdge);
        // Moved to the top edge of another monitor: the rope follows after the same rest.
        if (atEdge && armed && !fullScreen && Monitor != monitor)
        {
            edgeSince ??= now;
            if (now - edgeSince.Value >= Dwell) { Monitor = monitor; edgeSince = null; leftSince = null; return RopeEdgeAction.Show; }
        }
        else edgeSince = null;
        bool inside = engaged || (rope is { } area && area.Contains(x,y)) || (atEdge && Monitor == monitor);
        if (inside) { leftSince = null; return RopeEdgeAction.None; }
        leftSince ??= now;
        return now - leftSince.Value >= LeaveDelay ? Hide(atEdge) : RopeEdgeAction.None;
    }

    /// Shown from outside (a screenshot card was hung): the rope stays down for `linger`
    /// before the ordinary leave delay applies, or longer while the pointer is on it.
    public void Present(TimeSpan now,ScreenRect monitor,TimeSpan linger)
    {
        if (linger < LeaveDelay) linger = LeaveDelay;
        Showing = true; Monitor = monitor; edgeSince = null; last = now;
        leftSince = now + (linger - LeaveDelay);
    }

    /// Hidden from outside (feature or setting turned off, Swivel quitting): no action is reported.
    public void Reset() { Showing = false; Monitor = null; edgeSince = null; leftSince = null; armed = true; }

    private RopeEdgeAction Hide(bool atEdge)
    {
        Showing = false; Monitor = null; edgeSince = null; leftSince = null;
        armed = !atEdge; // hidden while the pointer is at the edge: wait until it leaves before showing again
        return RopeEdgeAction.Hide;
    }
}
