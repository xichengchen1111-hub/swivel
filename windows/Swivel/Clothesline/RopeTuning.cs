using System.Windows;
using System.Windows.Media;

namespace Swivel;

/// The clothesline's motion constants and the small pure helpers behind them, kept together so tests can hold them
/// to the owner's rule: gentle and well damped (damping ratio ≥ 0.6 for anything that swings or bounces), at most
/// two visible oscillations, settled within 1.5 s. A straight port of the Mac app's `RopeTuning`; the numbers are the
/// same (Mac points = WPF device-independent pixels).
static class RopeTuning
{
    /// A mass–spring pair integrated in fixed steps: stiffness `K` and damping `C` per unit mass.
    public readonly record struct Oscillator(double K, double C)
    {
        public double DampingRatio => C / (2 * Math.Sqrt(K));
    }

    public static readonly Oscillator Fall = new(260, 24);     // the whole rope lowering and lifting (ζ ≈ 0.74)
    public static readonly Oscillator Sag = new(150, 16);      // the cord's extra dip in the middle (ζ ≈ 0.65)
    public static readonly Oscillator Swing = new(34, 7.6);    // each print's pendulum on its peg (ζ ≈ 0.65)
    public static readonly Oscillator Arrive = new(200, 20);   // a new print settling onto the rope (ζ ≈ 0.71)
    public const double SagCoupling = 0.05;                    // the cord lags behind the rope's acceleration

    /// Fixed physics step. Each display frame feeds its measured length through an accumulator, so the motion is
    /// the same at 60 or 144 Hz and a late frame doesn't slow it down.
    public const double Step = 1.0 / 240;
    public const int MaxSteps = 8;

    // Nudges, in rad/s: staggered as the rope lowers, trailing as it lifts, and for a new print.
    public static readonly (double Lo, double Hi) DropKick = (0.3, 0.5);
    public const double LiftKick = 0.25;
    public static readonly (double Lo, double Hi) ArrivalSwingFromAbove = (0.5, 0.8);
    public static readonly (double Lo, double Hi) ArrivalSwingInPlace = (0.3, 0.5);
    public const double ArrivalHeight = -110;                  // hung from the menu: falls this far onto the rope

    /// The breeze blows while something happens and for this long after, then dies down (critically damped, over
    /// about two seconds), so a rope left down comes to rest and its frame loop can stop.
    public const double BreezeHold = 3;
    public const double BreezeFade = 1.2;

    // Hover and press, as retargetable springs.
    public static readonly (double Response, double Damping) Lift = (0.34, 0.81);
    public static readonly (double Response, double Damping) PressIn = (0.1, 0.9);
    public static readonly (double Response, double Damping) PressOut = (0.28, 0.8);
    public static readonly (double Response, double Damping) Slide = (0.4, 0.9);   // along the rope as a gap opens or closes
    public const double FadeIn = 0.14, FadeOut = 0.1;                               // opacity and colour (exits are faster)
    public const double HoverLift = 6;
    public const double HoverScale = 0.025;
    public const double PressSink = 4;                         // pressed, a print sinks back toward the rope…
    public const double PressScale = 0.035;                    // …and gives a little (3.5 % from hovered)
    public const double ChargeScale = 0.03;                    // held toward the preview, it gathers again

    /// Semi-implicit Euler step toward `target`; returns the acceleration so the cord can follow the rope.
    public static double Advance(ref double x, ref double v, double target, Oscillator o, double force = 0, double h = Step)
    {
        double a = -o.K * (x - target) - o.C * v + force;
        v += a * h;
        x += v * h;
        return a;
    }

    /// Holding a print: it gathers between 0.2 s and 0.55 s, then the preview opens.
    public const double ChargeStart = 0.2, ChargeEnd = 0.55;

    public static double Charge(double heldFor) => Math.Clamp((heldFor - ChargeStart) / (ChargeEnd - ChargeStart), 0, 1);

    /// How long the rope waits before lifting, or null to stay. Reaching along the rope for a print gets more time
    /// than leaving it, so it doesn't run away from the pointer on a wide screen. `inTopBand` is the Windows
    /// counterpart of the Mac's menu bar: the strip between the top of the screen and the cord.
    public static double? HideDelay(bool overPrint, bool inTopBand, bool nearPrints, bool inStrip)
    {
        if (overPrint || inTopBand) return null;
        if (nearPrints) return 1.5;
        return inStrip ? 1.0 : 0.5;
    }

    public const double FirstSlot = 76, Spacing = 22;
    public static readonly Size MaxPrint = new(168, 112);

    /// Prints hang left-aligned from `FirstSlot`, `Spacing` apart; `gap` opens room at the front for something about
    /// to be hung. Each print's centre, and whether it fits before the right edge.
    public static (double X, bool Fits)[] Slots(IReadOnlyList<double> widths, double width, double gap = 0)
    {
        var result = new (double, bool)[widths.Count];
        double x = FirstSlot + gap;
        for (int i = 0; i < widths.Count; i++)
        {
            double w = widths[i];
            result[i] = (x + w / 2, x + w < width - 40);
            x += w + Spacing;
        }
        return result;
    }

    /// A print's size for a picture of this aspect ratio, white border included.
    public static Size PrintSize(double aspect)
    {
        double a = Math.Max(aspect, 0.05);
        double h = Math.Min(MaxPrint.Width / a, MaxPrint.Height);
        return new Size(h * a + 10, h + 10);
    }

    /// The rope sags in a parabola, which one quadratic Bézier draws exactly. Returns the start, control and end
    /// points of that curve (y grows downward); the path adds 4 px of straight cord past each end.
    public static (Point Start, Point Control, Point End) RopeCurve(double width, double y, double sag) =>
        (new Point(0, y), new Point(width / 2, y + 2 * sag), new Point(width, y));

    public static StreamGeometry RopePath(double width, double y, double sag)
    {
        var (start, control, end) = RopeCurve(width, y, sag);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(-4, y), false, false);
            ctx.LineTo(start, true, false);
            ctx.QuadraticBezierTo(control, end, true, true);
            ctx.LineTo(new Point(width + 4, y), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// A CSS-style cubic-Bézier timing curve at `t`, solved by bisection (monotonic for x1, x2 in 0…1).
    public static double Ease(double t, double x1, double y1, double x2, double y2)
    {
        t = Math.Clamp(t, 0, 1);
        static double Curve(double u, double a, double b) => 3 * a * u * (1 - u) * (1 - u) + 3 * b * u * u * (1 - u) + u * u * u;
        double lo = 0, hi = 1;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (Curve(mid, x1, x2) < t) lo = mid; else hi = mid;
        }
        return Curve((lo + hi) / 2, y1, y2);
    }

    /// CSS "ease-in" and "ease-out", for the few fixed-length exits (a print falling off, the "Copied" chip).
    public static double EaseIn(double t) => Ease(t, 0.42, 0, 1, 1);
    public static double EaseOut(double t) => Ease(t, 0, 0, 0.58, 1);

    public static double Between(this Random random, (double Lo, double Hi) range) => range.Lo + random.NextDouble() * (range.Hi - range.Lo);
}

/// Touching the top edge reveals the rope immediately. Moving away rearms it, so clicking at the top of the screen
/// or the hide shortcut can dismiss the rope without it reopening in place (the Mac 1.1.1 behaviour).
sealed class TopEdgeTrigger
{
    bool wasAtTopEdge;

    /// The top two pixels of a monitor count as its edge.
    public const double EdgePixels = 2;

    /// One look at the pointer. Returns true when the rope should come down now.
    public bool Update(bool atTopEdge, bool buttonDown, bool canShow)
    {
        bool reached = atTopEdge && !wasAtTopEdge;
        wasAtTopEdge = atTopEdge;
        return reached && !buttonDown && canShow;
    }

    public void Reset() => wasAtTopEdge = false;
}

/// When the screenshot card leaves by itself: after 2.5 s left alone, never while it is hovered, pressed or dragged,
/// and one more second after the pointer leaves or a drag ends without taking it. Each presentation is a new
/// generation, so an old deadline can never put away a newer card. Pure (time is passed in) so tests can drive it.
sealed class CardTimer
{
    public const double WaitSeconds = 2.5;
    public const double ResumeSeconds = 1;

    public int Generation { get; private set; }
    public double? Deadline { get; private set; }
    public bool Hovered { get; private set; }
    public bool Interacting { get; private set; }
    public bool Dragging { get; private set; }
    public bool Showing { get; private set; }

    bool Blocked => Hovered || Interacting || Dragging;

    public void Present(double now, bool hovered = false)
    {
        Generation++;
        Showing = true;
        Hovered = hovered;
        Interacting = false;
        Dragging = false;
        Schedule(now, WaitSeconds);
    }

    /// The card left (hung, opened, put away).
    public void Dismissed()
    {
        Showing = false;
        Deadline = null;
    }

    public void Pause() => Deadline = null;

    public void Resume(double now) => Schedule(now, ResumeSeconds);

    void Schedule(double now, double delay)
    {
        Deadline = null;
        if (!Showing || Blocked) return;
        Deadline = now + delay;
    }

    public void SetHovered(bool on, double now)
    {
        Hovered = on;
        if (on) Pause(); else Resume(now);
    }

    /// Mouse down (or the wait that tells a click from a double-click).
    public void SetInteracting(bool on, double now, bool resume = true)
    {
        Interacting = on;
        if (on) Pause(); else if (resume) Resume(now);
    }

    public void BeginDrag()
    {
        Dragging = true;
        Pause();
    }

    /// A drag ended and the card is still there (copied into an app, or dropped nowhere): it waits once more.
    public void EndDrag(double now, bool hovered)
    {
        Dragging = false;
        Interacting = false;
        Hovered = hovered;
        Resume(now);
    }

    /// Whether the card should leave now; true at most once per deadline.
    public bool Due(double now)
    {
        if (Deadline is not { } d || Blocked || !Showing || now < d) return false;
        Deadline = null;
        return true;
    }
}
