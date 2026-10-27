using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Swivel;

/// One drop target in a menu layout (y-up view coordinates, like the Mac code).
sealed class Slot
{
    public required Geometry Path { get; init; }    // hit area and fill
    public Point Label { get; init; }               // label centre
    public double Angle { get; init; }              // label rotation in degrees (counter-clockwise)
    public double MaxWidth { get; init; }
    public double FontSize { get; init; } = 12;
    public double LabelScale { get; init; } = 1;
    public double Alpha { get; init; } = 1;
}

/// Motion constants for the pop-up menu, in one place so the feel can be tuned (the Mac app's numbers).
static class Tune
{
    // Hover: the item under the pointer springs out with a small overshoot and settles back calmly. Its colour
    // arrives in the first few frames; only the shape springs.
    public static readonly (double Response, double Damping) PopOut = (0.24, 0.7);
    public static readonly (double Response, double Damping) PopBack = (0.18, 0.95);
    public const double PopLift = 10;       // wheel sector moves outwards (DIPs)
    public const double PopGrow = 4;        // and its outer edge grows
    // Press on drop: sink fast and hold for a beat, then release with a bounce while the rest of the menu clears.
    public static readonly (double Response, double Damping) PressIn = (0.09, 1);
    public static readonly (double Response, double Damping) PressOut = (0.28, 0.6);
    public const double PressHold = 0.06;   // how long the pressed state is held before releasing
    public const double PressSink = 5;      // pressed sector sinks back towards the hub
    public const double PressScale = 0.04;  // and shrinks by this fraction
    public static readonly (double Response, double Damping) Exit = (0.2, 1);
    // Open: items bloom out from the hub one after another, clockwise from the top. No overshoot on a menu.
    public static readonly (double Response, double Damping) Bloom = (0.26, 0.86);
    public const double BloomStagger = 0.01;
    public const double BloomRise = 14;
    // Page turn: the ring turns by part of a segment and settles.
    public static readonly (double Response, double Damping) Turn = (0.27, 0.84);
    public const double TurnFraction = 0.5;
}

/// The pop-up menu. Lays its items out in the chosen `MenuStyle` and draws them; every item is a drop target.
///
/// A port of the Mac app's WheelView. Geometry is computed in the Mac's y-up coordinates with the same numbers, and
/// drawn through a flip; the public pointer API (`Track`, `BeginOpen`) takes ordinary WPF coordinates (y down).
/// Live menus (`IsLive`) animate on a FrameDriver: each item has its own spring for hovering, the dropped item is
/// pressed in, and opening blooms the items out from the centre. Previews draw the settled state.
sealed class WheelView : FrameworkElement
{
    public Action<WheelItem, IReadOnlyList<string>>? Dropped { get; set; }
    public Action? OnDragEndedWithoutDrop { get; set; }
    public string Subtitle { get; set; } = "";

    /// The keyboard tool wheel (1.4): items are clicked, or chosen with the arrow keys and Enter, instead of dropped on.
    public Action<WheelItem>? Clicked { get; set; }
    /// Esc, or a click on the hub or outside the items, in a clickable menu.
    public Action? Cancelled { get; set; }
    /// Space in a clickable menu.
    public Action? NextPage { get; set; }

    bool clickable;
    /// Opened from the keyboard rather than during a drag: it takes clicks and keys, and its hints say so.
    public bool IsClickable
    {
        get => clickable;
        set
        {
            if (clickable == value) return;
            clickable = value;
            Focusable = value;
            Invalidate();
            RefreshAutomation();
        }
    }

    MenuStyle style = MenuStyle.Wheel;
    public MenuStyle MenuStyle
    {
        get => style;
        set { style = value; Invalidate(); }
    }

    /// Animate and give feedback. Off for the static previews in Settings.
    public bool IsLive { get; set; }
    /// Tests and the frames tool drive the animation with `Advance` instead of the frame driver.
    public bool ManualClock { get; set; }
    /// Forces the light or dark look (the frames tool); null follows Windows.
    public bool? DarkOverride { get; set; }

    int? hovered;
    public int? Hovered
    {
        get => hovered;
        set
        {
            if (hovered == value) return;
            var old = hovered;
            hovered = value;
            HoverChanged(old);
        }
    }

    List<WheelItem> items = [];
    public IReadOnlyList<WheelItem> Items => items;
    WheelMode mode = WheelMode.Convert;
    int page;
    int pageCount = 1;
    public int Page => page;
    public int PageCount => pageCount;

    public const double OuterRadius = 122;
    const double InnerRadius = 52, RimWidth = 9, Gap = 7;

    Size viewSize = new(320, 320);
    /// The menu's own size (its "bounds"); previews scale the view, but it always lays out at this size.
    public Size ViewSize
    {
        get => viewSize;
        set
        {
            viewSize = value;
            Width = value.Width;
            Height = value.Height;
            Invalidate();
        }
    }

    Point Center => new(viewSize.Width / 2, viewSize.Height / 2);

    // MARK: Motion state

    readonly FrameDriver driver = new();
    Spring[] pops = [];                         // per item: 0 at rest, 1 hovered
    Spring[] blooms = [];                       // per item: 0 hidden, 1 shown
    double bloomClock = double.PositiveInfinity; // seconds since opening, while items are still being released
    Spring press = new(0, Tune.PressIn.Response, Tune.PressIn.Damping);
    double pressClock = double.PositiveInfinity;
    int? pressed;
    public int? Pressed => pressed;
    Spring exit = new(0, Tune.Exit.Response, Tune.Exit.Damping);
    Spring turn = new(0, Tune.Turn.Response, Tune.Turn.Damping);
    Spring reveal = new(1, 0.2, 1);              // labels fading in after a page turn
    Spring dial = new(90, 0.2, 0.8);             // hub indicator angle, degrees
    Spring dialAlpha = new(0, 0.14, 1);
    Spring swap = new(1, 0.2, 1);                // hub/header headline crossfade
    string previousHeadline = "";
    Spring row = new(0, 0.15, 0.9);              // list: highlight position in rows
    Spring rowAlpha = new(0, 0.12, 1);
    Spring hubPress = new(0, 0.16, 1);           // the hub sinks while it means "cancel"
    /// An item has been hovered since the menu opened, so coming back to the hub (or an empty spot) means cancel.
    bool visited;
    bool pointerInside;
    Point? openPoint;
    /// Where the pointer is (y-up view coordinates), for the light that follows it across a raised sector.
    Point? pointer;
    /// Every header title the current page can show; the header is sized for the widest, so it doesn't shift sideways
    /// as items are hovered. Measured when drawn, at the current text size.
    List<string> headerTitles = [];

    /// The user's Feel & Text motion for pop-up menus (1.3), used by every spring and clock of the menu.
    /// `TuningOverride` lets the Feel & Text page preview a tuning before it is saved. Under reduced motion the
    /// tuned 1.2 timing stays, since nothing travels anyway.
    public MotionTuning? TuningOverride { get; set; }
    public MotionTuning Tuning => Motion.Reduced ? MotionTuning.Default : TuningOverride ?? Prefs.Interaction[FeelArea.Menu];

    bool cancelArmed;
    /// Releasing now would cancel: the pointer came back to the hub after visiting an item.
    public bool CancelArmed
    {
        get => cancelArmed;
        private set
        {
            if (cancelArmed == value) return;
            bool old = cancelArmed;
            cancelArmed = value;
            previousHeadline = old ? L("Cancel") : Headline(hovered);
            swap.Snap(0);
            swap.Target = 1;
            hubPress.Target = cancelArmed ? 1 : 0;
            if (cancelArmed && IsLive) Feedback.Hover(sound: false);
            Kick();
        }
    }

    readonly VisualCollection visuals;
    readonly Painter painter;

    public WheelView()
    {
        visuals = new VisualCollection(this);
        painter = new Painter(v => visuals.Add(v));
        driver.OnFrame = dt =>
        {
            bool more = Advance(dt);
            Redraw();
            return more;
        };
        Width = viewSize.Width;
        Height = viewSize.Height;
        SnapsToDevicePixels = false;
        Unloaded += (_, _) =>
        {
            driver.Stop();
            Prefs.Changed -= PrefsChanged;
        };
        Loaded += (_, _) =>
        {
            // Palettes, rope colours and text size apply to a menu already on screen (and to the Settings previews).
            Prefs.Changed -= PrefsChanged;
            Prefs.Changed += PrefsChanged;
            Invalidate();
        };
    }

    void PrefsChanged() => Dispatcher.BeginInvoke(Invalidate);

    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];
    protected override Size MeasureOverride(Size availableSize) => viewSize;
    protected override Size ArrangeOverride(Size finalSize) => viewSize;

    /// The whole menu area is one target (gaps and margins included).
    protected override HitTestResult HitTestCore(PointHitTestParameters hit) =>
        new Rect(viewSize).Contains(hit.HitPoint) ? new PointHitTestResult(this, hit.HitPoint) : null!;

    public void Configure(IReadOnlyList<WheelItem> newItems, WheelMode newMode, int newPage, int newPageCount)
    {
        bool turning = IsLive && (ManualClock || IsVisible) && newMode == mode && newPage != page
            && newItems.Count > 0 && pressed == null;
        Hovered = null;
        items = [.. newItems];
        mode = newMode;
        page = newPage;
        pageCount = newPageCount;
        pops = items.Select(_ => new Spring(0, Tune.PopBack.Response, Tune.PopBack.Damping)).ToArray();
        // Tags on the rope swing in as they appear (gently: still well damped); everything else settles without overshoot.
        var bloom = style == MenuStyle.Clothesline ? (Response: 0.42, Damping: 0.62) : Tune.Bloom;
        blooms = items.Select(_ => new Spring(1, bloom.Response, bloom.Damping)).ToArray();
        headerTitles = [.. new[] { mode.Title(), L("Cancel") }.Concat(items.Select(i => $"{mode.Title()}: {i.DisplayTitle}"))];
        bloomClock = double.PositiveInfinity;
        swap.Snap(1);
        rowAlpha.Snap(0);
        dialAlpha.Snap(0);
        if (!turning) visited = false;
        CancelArmed = false;
        hubPress.Snap(0);
        RefreshAutomation();
        if (turning)
        {
            if (!Motion.Reduced)
            {
                turn.Snap(Step * Tune.TurnFraction);
                turn.Target = 0;
            }
            reveal.Snap(0);
            reveal.Target = 1;
            Feedback.Page();
            Kick();
        }
        Invalidate();
    }

    /// Starts the opening animation; called as the window appears. `pointerAt` is where the drag is (WPF view
    /// coordinates): items only react once the pointer has moved a little, so a menu that had to shift to fit on
    /// screen can't select whatever happened to land under a still pointer.
    public void BeginOpen(Point? pointerAt = null)
    {
        openPoint = pointerAt is { } p ? Flip(p) : null;
        pressed = null;
        pressClock = double.PositiveInfinity;
        press.Snap(0);
        exit.Snap(0);
        turn.Snap(0);
        reveal.Snap(1);
        swap.Snap(1);
        dialAlpha.Snap(0);
        rowAlpha.Snap(0);
        visited = false;
        pointerInside = false;
        cancelArmed = false;
        pointer = null;
        hubPress.Snap(0);
        if (Motion.Reduced)
        {
            for (int i = 0; i < blooms.Length; i++) blooms[i].Snap(1);
            bloomClock = double.PositiveInfinity;
        }
        else
        {
            for (int i = 0; i < blooms.Length; i++) blooms[i].Snap(0);
            bloomClock = 0;
        }
        Kick();
    }

    /// Presses the dropped item in, then releases it while the rest of the menu clears away.
    public void BeginConfirm(int index)
    {
        if (index < 0 || index >= items.Count) return;
        pressedTint = PressedColour();
        pressed = index;
        if (hovered != index) Hovered = index;
        press.Response = Tune.PressIn.Response;
        press.Damping = Tune.PressIn.Damping;
        press.Target = 1;
        pressClock = 0;
        Kick();
    }

    /// Jumps every animation to its end state (used by previews and tests).
    public void Settle()
    {
        bloomClock = double.PositiveInfinity;
        for (int i = 0; i < pops.Length; i++) pops[i].Snap(pops[i].Target);
        for (int i = 0; i < blooms.Length; i++) blooms[i].Snap(1);
        press.Snap(press.Target);
        exit.Snap(exit.Target);
        turn.Snap(turn.Target);
        reveal.Snap(reveal.Target);
        dial.Snap(dial.Target);
        dialAlpha.Snap(dialAlpha.Target);
        swap.Snap(swap.Target);
        row.Snap(row.Target);
        rowAlpha.Snap(rowAlpha.Target);
        hubPress.Snap(hubPress.Target);
        Invalidate();
    }

    /// Advances every spring by `dt` seconds. Returns true while anything is still moving.
    public bool Advance(double dt)
    {
        var tuning = Tuning;
        if (!double.IsInfinity(bloomClock))
        {
            // The stagger and the press hold run on the tuned clock too, so a faster menu is faster throughout.
            bloomClock += dt * tuning.SafeSpeed;
            for (int i = 0; i < blooms.Length; i++)
                if (bloomClock >= i * Tune.BloomStagger) blooms[i].Target = 1;
            if (bloomClock >= blooms.Length * Tune.BloomStagger) bloomClock = double.PositiveInfinity;
        }
        if (!double.IsInfinity(pressClock))
        {
            pressClock += dt * tuning.SafeSpeed;
            if (pressClock >= Tune.PressHold && press.Target == 1)
            {
                press.Response = Tune.PressOut.Response;
                press.Damping = Tune.PressOut.Damping;
                press.Target = 0;
                exit.Target = 1;
                pressClock = double.PositiveInfinity;
            }
        }
        for (int i = 0; i < pops.Length; i++) pops[i].Step(dt, tuning);
        for (int i = 0; i < blooms.Length; i++) blooms[i].Step(dt, tuning);
        press.Step(dt, tuning);
        exit.Step(dt, tuning);
        turn.Step(dt, tuning);
        reveal.Step(dt, tuning);
        dial.Step(dt, tuning);
        dialAlpha.Step(dt, tuning);
        swap.Step(dt, tuning);
        row.Step(dt, tuning);
        rowAlpha.Step(dt, tuning);
        hubPress.Step(dt, tuning);
        dirty = true;
        return !IsSettled;
    }

    public bool IsSettled =>
        double.IsInfinity(bloomClock) && double.IsInfinity(pressClock) && pops.All(s => s.IsSettled) && blooms.All(s => s.IsSettled)
        && press.IsSettled && exit.IsSettled && turn.IsSettled && reveal.IsSettled && dial.IsSettled
        && dialAlpha.IsSettled && swap.IsSettled && row.IsSettled && rowAlpha.IsSettled && hubPress.IsSettled;

    /// Runs the frame driver for live menus on screen; previews jump straight to the end state.
    void Kick()
    {
        if (ManualClock) return;
        if (IsLive && PresentationSource.FromVisual(this) != null)
        {
            driver.Start();
        }
        else
        {
            Settle();
        }
    }

    bool dirty = true;
    bool redrawQueued;

    /// Marks the picture stale; it is redrawn on the next frame (or right away by `Redraw`).
    public void Invalidate()
    {
        dirty = true;
        if (driver.IsRunning || redrawQueued || ManualClock) return;
        redrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            redrawQueued = false;
            if (dirty) Redraw();
        });
    }

    void HoverChanged(int? old)
    {
        if (old is { } o && o >= 0 && o < pops.Length)
        {
            pops[o].Response = Tune.PopBack.Response;
            pops[o].Damping = Tune.PopBack.Damping;
            pops[o].Target = 0;
        }
        if (hovered is { } n && n >= 0 && n < pops.Length)
        {
            // Tags on the rope straighten gently (the owner's rule for anything on the clothesline).
            var popOut = style == MenuStyle.Clothesline ? (Response: 0.42, Damping: 0.8) : Tune.PopOut;
            pops[n].Response = Motion.Reduced ? 0.14 : popOut.Response;
            pops[n].Damping = Motion.Reduced ? 1 : popOut.Damping;
            pops[n].Target = 1;
            double angle = Unwrap(CentreAngle(n), dial.Value);
            if (dialAlpha.Value < 0.05 || Motion.Reduced) dial.Snap(angle); else dial.Target = angle;
            if (rowAlpha.Value < 0.05 || Motion.Reduced) row.Snap(n); else row.Target = n;
        }
        if (hovered != null) visited = true;
        dialAlpha.Target = hovered == null ? 0 : 1;
        rowAlpha.Target = hovered == null ? 0 : 1;
        previousHeadline = Headline(old);
        swap.Snap(0);
        swap.Target = 1;
        if (IsLive && hovered != null && pressed == null && !quietHover) Feedback.Hover();
        Kick();
    }

    bool quietHover;

    /// Shows an item hovered, animated but without the hover's sound (a preview's resting picture).
    public void ShowHover(int? index)
    {
        quietHover = true;
        try { Hovered = index is { } i && i >= 0 && i < items.Count ? i : null; }
        finally { quietHover = false; }
    }

    /// The angle equivalent to `angle` (mod 360) closest to `reference`, so the dial turns the short way.
    static double Unwrap(double angle, double reference)
    {
        double a = angle;
        while (a - reference > 180) a -= 360;
        while (a - reference < -180) a += 360;
        return a;
    }

    double PopValue(int i) => i >= 0 && i < pops.Length ? pops[i].Value : 0;
    /// Colour follows the pop but arrives in the first few frames, so the highlight never lags the pointer.
    double TintValue(int i) => Math.Clamp(PopValue(i) * 2.2, 0, 1);
    double BloomValue(int i) => i >= 0 && i < blooms.Length ? blooms[i].Value : 1;
    double PressValue(int i) => i == pressed ? press.Value : 0;
    /// While a dropped item is confirmed, the others clear away (0 → 1).
    double ExitValue(int i) => pressed != null && i != pressed ? exit.Value : 0;

    // MARK: Sizes per style

    public const double HeaderHeight = 34;
    public const double ListRow = 32;

    static double BookWidth(int i) => new double[] { 40, 34, 44, 37, 42, 35, 39, 43 }[i % 8];
    static double BookHeight(int i) => new double[] { 150, 132, 158, 140, 128, 154, 136, 146 }[i % 8];

    /// Natural view size for a style and item count. The live wheel is larger than it looks: the clear margin keeps
    /// catching the drag when a flick overshoots the ring, so the chosen sector stays chosen.
    public static Size MenuSize(MenuStyle style, int count, bool live = false)
    {
        int n = Math.Max(count, 1);
        switch (style)
        {
            case MenuStyle.Wheel:
                return live ? new Size(400, 400) : new Size(320, 320);
            case MenuStyle.List:
                return new Size(240, HeaderHeight + n * ListRow + 14);
            case MenuStyle.Shelf:
            {
                double books = Enumerable.Range(0, n).Sum(i => BookWidth(i) + 5);
                return new Size(Math.Max(books + 56, 220), 168 + HeaderHeight + 24);
            }
            case MenuStyle.Notes:
            {
                int cols = Math.Min(n, 4), rows = (int)Math.Ceiling(n / 4.0);
                return new Size(Math.Max(cols * 108 + 28, 220), rows * 108 + HeaderHeight + 28);
            }
            default:
                return new Size(Math.Max(n * 82 + 64, 240), 178 + HeaderHeight);
        }
    }

    /// The frosted backdrop behind the menu in WPF coordinates, if the style uses one (rect, corner radius).
    public (Rect Rect, double Radius)? Backdrop
    {
        get
        {
            var b = BackdropUp;
            if (b is not { } value) return null;
            var r = value.Rect;
            return (new Rect(r.X, viewSize.Height - r.Bottom, r.Width, r.Height), value.Radius);
        }
    }

    (Rect Rect, double Radius)? BackdropUp => style switch
    {
        MenuStyle.Wheel => (new Rect(Center.X - OuterRadius, Center.Y - OuterRadius, OuterRadius * 2, OuterRadius * 2), OuterRadius),
        MenuStyle.List => (Inset(new Rect(viewSize), 4, 4), 14),
        _ => null,
    };

    static Rect Inset(Rect r, double dx, double dy) =>
        new(r.X + dx, r.Y + dy, Math.Max(r.Width - 2 * dx, 0), Math.Max(r.Height - 2 * dy, 0));

    // MARK: Layout (y-up)

    double Step => 360.0 / Math.Max(items.Count, 1);

    /// Wheel segment i is centred at the top and proceeds clockwise.
    double CentreAngle(int i) => 90 - i * Step;

    /// Area below the header that the items occupy.
    Rect Body => new(0, 0, viewSize.Width, Math.Max(viewSize.Height - HeaderHeight, 0));

    /// Item geometry. `settled` lays items out as hit targets: the hovered item fully out, no transient motion.
    List<Slot> Slots(bool settled = false)
    {
        var target = hovered;
        Func<int, double> pop = settled ? i => i == target ? 1 : 0 : PopValue;
        return style switch
        {
            MenuStyle.Wheel => WheelSlots(pop, settled),
            MenuStyle.List => ListSlots(),
            MenuStyle.Shelf => ShelfSlots(pop, settled),
            MenuStyle.Notes => NoteSlots(pop, settled),
            _ => ClothesSlots(pop, settled),
        };
    }

    static Point Polar(Point c, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(c.X + Math.Cos(rad) * radius, c.Y + Math.Sin(rad) * radius);
    }

    List<Slot> WheelSlots(Func<int, double> pop, bool settled)
    {
        double half = Step / 2;
        double baseOuter = OuterRadius - RimWidth, inner = InnerRadius + 6;
        double mid = (baseOuter + inner) / 2;
        double rotation = settled ? 0 : turn.Value;
        var c = Center;
        var result = new List<Slot>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            double p = pop(i);
            double sunk = settled ? 0 : PressValue(i);
            double bloom = settled ? 1 : BloomValue(i);
            double away = settled ? 0 : ExitValue(i);
            // Under Reduce Motion nothing moves; the pop shows as colour only.
            double move = Motion.Reduced ? 0 : 1;
            double outer = baseOuter + Tune.PopGrow * Math.Max(p, 0) * move;
            // The gap to the neighbours narrows as a sector comes out, so it reads as one raised keycap.
            double closing = 1 - 0.45 * Math.Clamp(p, 0, 1) * move;
            double padOut = Gap / 2 * closing / outer * 180 / Math.PI;
            double padIn = Gap / 2 * closing / inner * 180 / Math.PI;
            double centre = CentreAngle(i) + rotation;
            double rad = centre * Math.PI / 180;
            var dir = new Vector(Math.Cos(rad), Math.Sin(rad));
            double lift = move * (Tune.PopLift * p - Tune.PressSink * sunk - Tune.BloomRise * (1 - bloom) - 12 * away);

            // Scale about the sector's own centre, then move it out along its direction.
            var rest = c + dir * mid;
            var labelCentre = rest + dir * lift;
            double scale = Motion.Reduced ? 1 : (1 - Tune.PressScale * sunk) * (0.9 + 0.1 * bloom) * (1 - 0.08 * away);
            Point T(Point q) => labelCentre + (q - rest) * scale;

            Geometry path;
            if (items.Count == 1)
            {
                var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(c, outer, outer));
                ring.Children.Add(new EllipseGeometry(c, inner, inner));
                ring.Freeze();
                path = ring;
            }
            else
            {
                double a0 = centre - half + padOut, a1 = centre + half - padOut;
                double a2 = centre + half - padIn, a3 = centre - half + padIn;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(T(Polar(c, outer, a0)), true, true);
                    g.ArcTo(T(Polar(c, outer, a1)), new Size(outer * scale, outer * scale), 0, a1 - a0 > 180,
                        SweepDirection.Clockwise, true, true);
                    g.LineTo(T(Polar(c, inner, a2)), true, true);
                    g.ArcTo(T(Polar(c, inner, a3)), new Size(inner * scale, inner * scale), 0, a2 - a3 > 180,
                        SweepDirection.Counterclockwise, true, true);
                }
                geometry.Freeze();
                path = geometry;
            }
            result.Add(new Slot
            {
                Path = path,
                Label = items.Count == 1 ? new Point(c.X, c.Y + mid) : labelCentre,
                MaxWidth = mid * Math.Sin(Math.PI / Math.Max(items.Count, 2)) * 1.7,
                FontSize = items.Count > 6 ? 11 : 12,
                LabelScale = scale * (1 + 0.07 * Math.Max(p, 0) * move),
                Alpha = Math.Clamp(bloom, 0, 1) * (1 - away),
            });
        }
        return result;
    }

    Rect ListRect(double r)
    {
        double top = viewSize.Height - HeaderHeight;
        return new Rect(10, top - (r + 1) * ListRow, Math.Max(viewSize.Width - 20, 0), ListRow - 4);
    }

    List<Slot> ListSlots() => items.Select((_, i) =>
    {
        var rect = ListRect(i);
        return new Slot
        {
            Path = Frozen(new RectangleGeometry(rect, 7, 7)),
            Label = new Point(rect.X + 14, rect.Y + rect.Height / 2), MaxWidth = rect.Width - 28, FontSize = 13,
        };
    }).ToList();

    const double ShelfTop = 30;  // top surface of the plank

    List<Slot> ShelfSlots(Func<int, double> pop, bool settled)
    {
        double total = items.Select((_, i) => BookWidth(i) + 5).Sum() - 5;
        double x = (viewSize.Width - total) / 2;
        var result = new List<Slot>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            double w = BookWidth(i), h = BookHeight(i);
            double bloom = settled ? 1 : BloomValue(i);
            // Neighbours of the hovered book rise a little too, so it reads as one physical row (Dock-style).
            double near = settled ? 0 : Math.Max(i > 0 ? pop(i - 1) : 0, i + 1 < items.Count ? pop(i + 1) : 0);
            double lift = Motion.Reduced ? 0 : 12 * pop(i) + 3 * Math.Max(near, 0) - (settled ? 0 : 4 * PressValue(i)) - 26 * (1 - bloom);
            var rect = new Rect(x, ShelfTop + lift, w, h);
            x += w + 5;
            result.Add(new Slot
            {
                Path = Frozen(new RectangleGeometry(rect, 3, 3)),
                Label = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2 + 6), Angle = 90, MaxWidth = h - 34, FontSize = 12,
                Alpha = Math.Clamp(bloom * 1.6, 0, 1) * (1 - (settled ? 0 : ExitValue(i))),
            });
        }
        return result;
    }

    List<Slot> NoteSlots(Func<int, double> pop, bool settled)
    {
        int cols = Math.Min(Math.Max(items.Count, 1), 4);
        const double side = 94, pitch = 108;
        double gridWidth = cols * pitch;
        double originX = (viewSize.Width - gridWidth) / 2 + (pitch - side) / 2;
        double top = Body.Bottom - 10;
        double[] rests = [-3, 2.5, -1.5, 3, -2.5, 1.5, -3.5, 2];
        var result = new List<Slot>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            int r = i / cols, col = i % cols;
            var rect = new Rect(originX + col * pitch, top - (r + 1) * pitch + (pitch - side) / 2, side, side);
            double p = pop(i), bloom = settled ? 1 : BloomValue(i);
            double rest = rests[i % 8];
            double tilt = Motion.Reduced ? rest : rest * (1 - p) + 6 * (1 - bloom);
            double scale = Motion.Reduced ? 1 : (1 + 0.08 * p - (settled ? 0 : 0.06 * PressValue(i))) * (0.82 + 0.18 * bloom);
            var mid = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var m = Matrix.Identity;
            m.Translate(-mid.X, -mid.Y);
            m.Scale(scale, scale);
            m.Rotate(tilt);
            m.Translate(mid.X, mid.Y);
            var path = new RectangleGeometry(rect, 8, 8) { Transform = new MatrixTransform(m) };
            result.Add(new Slot
            {
                Path = Frozen(path), Label = m.Transform(new Point(mid.X, mid.Y - 4)), Angle = tilt,
                MaxWidth = side - 16, FontSize = 15, LabelScale = scale,
                Alpha = Math.Clamp(bloom * 1.4, 0, 1) * (1 - (settled ? 0 : ExitValue(i))),
            });
        }
        return result;
    }

    /// Rope height at x: a gentle sag between the two posts.
    double RopeY(double x)
    {
        double left = 22, right = viewSize.Width - 22;
        double t = (x - left) / Math.Max(right - left, 1);
        return Body.Bottom - 18 - 16 * 4 * t * (1 - t);
    }

    List<Slot> ClothesSlots(Func<int, double> pop, bool settled)
    {
        const double pitch = 82;
        double start = (viewSize.Width - items.Count * pitch) / 2 + pitch / 2;
        double[] rests = [4, -3, 2.5, -4, 3, -2, 3.5, -3];
        var result = new List<Slot>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            double x = start + i * pitch;
            double hang = RopeY(x);
            const double w = 66, h = 92;
            double p = pop(i), bloom = settled ? 1 : BloomValue(i);
            double rest = rests[i % 8];
            // Tags swing in from the rope as they appear and straighten when hovered.
            double tilt = Motion.Reduced ? rest : rest * (1 - p) + 14 * (1 - bloom);
            double near = settled ? 0 : Math.Max(i > 0 ? pop(i - 1) : 0, i + 1 < items.Count ? pop(i + 1) : 0);
            double lift = Motion.Reduced ? 0 : 4 * p + 1.5 * Math.Max(near, 0) - (settled ? 0 : 5 * PressValue(i));
            var rect = new Rect(x - w / 2, hang - 6 - h + lift, w, h);
            var m = Matrix.Identity;
            m.RotateAt(tilt, x, hang);
            var path = new RectangleGeometry(rect, 6, 6) { Transform = new MatrixTransform(m) };
            result.Add(new Slot
            {
                Path = Frozen(path), Label = m.Transform(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2 - 8)), Angle = tilt,
                MaxWidth = w - 10, FontSize = 12,
                Alpha = Math.Clamp(bloom * 1.5, 0, 1) * (1 - (settled ? 0 : ExitValue(i))),
            });
        }
        return result;
    }

    static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }

    /// How far (degrees) the pointer may stray past a hovered sector's edge before the next one takes over: about
    /// 10 DIPs of arc, so it's the same distance for the hand whether the pointer is near the hub or the rim.
    static double Hysteresis(double radius) => Math.Clamp(10 / Math.Max(radius, 1) * 180 / Math.PI, 4, 9);

    /// The item under a y-up point, if any.
    int? SlotAt(Point point)
    {
        if (items.Count == 0) return null;
        int n = items.Count;
        switch (style)
        {
            case MenuStyle.Wheel:
            {
                double dx = point.X - Center.X, dy = point.Y - Center.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                // The hub is the cancel zone; once a sector is chosen it takes a little more to fall back into it.
                if (distance <= (hovered == null ? InnerRadius + 2 : InnerRadius - 6)) return null;
                // A flick that overshoots the ring keeps its sector, but a deliberate move well away from it is empty
                // space again: releasing there cancels.
                if (distance >= (hovered == null ? OuterRadius + 40 : OuterRadius + 52)) return null;
                double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (hovered is { } h && h >= 0 && h < n && n > 1)
                {
                    double off = (angle - CentreAngle(h)) % 360;
                    if (off > 180) off -= 360;
                    if (off < -180) off += 360;
                    if (Math.Abs(off) <= Step / 2 + Hysteresis(distance)) return h;
                }
                double offset = (90 - angle + Step / 2) % 360;
                if (offset < 0) offset += 360;
                return Math.Min((int)(offset / Step), n - 1);
            }
            // Other templates: whole bands and cells, gaps included, so moving between items never blinks off and a
            // lifted or straightened item can't move out from under the pointer.
            case MenuStyle.List:
            {
                double top = viewSize.Height - HeaderHeight - 4;
                if (!(point.Y < top && point.X > 4 && point.X < viewSize.Width - 4)) return null;
                int i = (int)Math.Floor((top - point.Y) / ListRow);
                return i < n ? i : null;
            }
            case MenuStyle.Shelf:
            {
                double total = items.Select((_, i) => BookWidth(i) + 5).Sum() - 5;
                double x = (viewSize.Width - total) / 2;
                if (!(point.Y > ShelfTop - 16 && point.Y < ShelfTop + 172)) return null;
                for (int i = 0; i < n; i++)
                {
                    double w = BookWidth(i);
                    if (point.X >= x - 2.5 && point.X < x + w + 2.5) return i;
                    x += w + 5;
                }
                return null;
            }
            case MenuStyle.Notes:
            {
                int cols = Math.Min(Math.Max(n, 1), 4);
                const double pitch = 108;
                double originX = (viewSize.Width - cols * pitch) / 2;
                double top = Body.Bottom - 10;
                if (!(point.X >= originX && point.Y <= top)) return null;
                int col = (int)Math.Floor((point.X - originX) / pitch), r = (int)Math.Floor((top - point.Y) / pitch);
                int i = r * cols + col;
                return col < cols && i < n ? i : null;
            }
            default:
            {
                const double pitch = 82;
                double start = (viewSize.Width - n * pitch) / 2;
                if (!(point.X >= start && point.Y < Body.Bottom - 4 && point.Y > 4)) return null;
                int i = (int)Math.Floor((point.X - start) / pitch);
                return i < n ? i : null;
            }
        }
    }

    /// WPF (y-down) view point ↔ the Mac's y-up view point.
    Point Flip(Point p) => new(p.X, viewSize.Height - p.Y);

    /// Re-checks what is under the pointer without waiting for the next drag update (after a page turn).
    public void Rehover()
    {
        if (!pointerInside) return;
        Point? at = null;
        if (PresentationSource.FromVisual(this) != null)
        {
            var c = Native.CursorPixels();
            try { at = Flip(PointFromScreen(new Point(c.X, c.Y))); } catch (InvalidOperationException) { }
        }
        at ??= pointer;
        if (at is { } p) Hovered = SlotAt(p);
    }

    // MARK: Pointer

    /// Updates the hover state for a pointer at `point` (WPF view coordinates). Returns true if a drop there would
    /// apply an item.
    public bool Track(Point wpfPoint)
    {
        if (pressed != null) return false;
        var point = Flip(wpfPoint);
        pointerInside = true;
        if (style == MenuStyle.Wheel && hovered != null && pointer != point) Invalidate();
        pointer = point;
        if (openPoint is { } start)
        {
            if ((point - start).Length < 10) return false;
            openPoint = null;
        }
        Hovered = SlotAt(point);
        CancelArmed = IsLive && visited && hovered == null;
        return hovered != null;
    }

    /// The pointer left the menu: nothing is hovered, and releasing out there isn't a cancel on the hub.
    public void PointerLeft()
    {
        if (pressed != null) return;
        pointerInside = false;
        pointer = null;
        Hovered = null;
        CancelArmed = false;
    }

    /// Starts a fresh try without changing what is shown as hovered (Settings previews rest on a hovered item, which
    /// mustn't count as the user having visited it).
    public void ResetVisit()
    {
        visited = false;
        CancelArmed = false;
    }

    /// A drop at the current hover: presses the item in with its sound, then runs it on the next dispatcher turn.
    /// Returns false when nothing is hovered (a drop on the hub or an empty spot cancels).
    public bool PerformDrop(IReadOnlyList<string> files)
    {
        if (hovered is not { } index || index < 0 || index >= items.Count || pressed != null || files.Count == 0) return false;
        var item = items[index];
        // Feedback lands on the drop itself; the action starts on the next dispatcher turn.
        BeginConfirm(index);
        Feedback.Press();
        Dispatcher.BeginInvoke(() => Dropped?.Invoke(item, files));
        return true;
    }

    // MARK: Clicks and keys (the keyboard tool wheel)

    /// A click at `wpfPoint` (WPF view coordinates): presses the item under it, or cancels on the hub or a gap. The
    /// press starts on mouse-down; the item runs on the next dispatcher turn.
    public bool Click(Point wpfPoint)
    {
        if (!clickable || pressed != null) return false;
        openPoint = null; // a deliberate click counts even if the pointer hasn't moved since the menu opened
        Track(wpfPoint);
        if (hovered is { } index && index >= 0 && index < items.Count)
        {
            Choose(index);
            return true;
        }
        Cancelled?.Invoke();
        return false;
    }

    /// Esc closes, Space turns the page, the arrow keys step through the items (left/up backwards) and Enter runs the
    /// highlighted one. Returns true for a key the menu used.
    public bool HandleKey(Key key)
    {
        if (!clickable) return false;
        if (pressed != null) return true;
        switch (key)
        {
            case Key.Escape:
                Cancelled?.Invoke();
                return true;
            case Key.Space:
                NextPage?.Invoke();
                return true;
            case Key.Left or Key.Up or Key.Right or Key.Down:
                if (items.Count == 0) return true;
                bool backwards = key is Key.Left or Key.Up;
                int from = hovered ?? (backwards ? 0 : -1);
                openPoint = null;
                Hovered = (from + (backwards ? -1 : 1) + items.Count) % items.Count;
                CancelArmed = false;
                return true;
            case Key.Enter:
                if (hovered is { } index && index >= 0 && index < items.Count) Choose(index);
                return true;
        }
        return false;
    }

    /// UI Automation's Invoke on an item: the same as clicking it.
    internal void Invoke(int index)
    {
        if (clickable && pressed == null && index >= 0 && index < items.Count) Choose(index);
    }

    /// Presses the item in with its sound, then runs it.
    void Choose(int index)
    {
        var item = items[index];
        BeginConfirm(index);
        Feedback.Press();
        Dispatcher.BeginInvoke(() => Clicked?.Invoke(item));
    }

    /// The item's outline on screen (physical pixels), for UI Automation.
    internal Rect ItemScreenRect(int index)
    {
        var slots = Slots(settled: true);
        if (index < 0 || index >= slots.Count || PresentationSource.FromVisual(this) == null) return Rect.Empty;
        var b = slots[index].Path.Bounds;
        var wpf = new Rect(b.X, viewSize.Height - b.Y - b.Height, b.Width, b.Height);
        try { return new Rect(PointToScreen(wpf.TopLeft), PointToScreen(wpf.BottomRight)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    void RefreshAutomation()
    {
        if (UIElementAutomationPeer.FromElement(this) is not { } peer) return;
        peer.ResetChildrenCache();
        peer.InvalidatePeer();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WheelViewPeer(this);

    // MARK: Drawing

    bool IsDark => DarkOverride ?? Native.DarkMode;
    static Color AccentColour => Look.Accent.WithAlpha(0.92);
    /// The accent a quarter darker, for an item being pressed (resolved once per drop).
    Color pressedTint = PressedColour();
    static Color PressedColour() => Look.Accent.Mix(Colors.Black, 0.25);
    Color Ink => IsDark ? Colors.White : Look.Gray(0.13);
    Color AccentInk => Look.AccentText(IsDark);
    Color labelGlow = Colors.White;

    /// Rebuilds the picture now.
    public void Redraw()
    {
        dirty = false;
        painter.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        painter.Begin(viewSize.Height);
        labelGlow = IsDark ? Colors.Black.WithAlpha(0.6) : Colors.White.WithAlpha(0.8);
        switch (style)
        {
            case MenuStyle.Wheel: DrawWheel(); break;
            case MenuStyle.List: DrawList(); break;
            case MenuStyle.Shelf: DrawShelf(); break;
            case MenuStyle.Notes: DrawNotes(); break;
            default: DrawClothesline(); break;
        }
        painter.End();
    }

    string Headline(int? index)
    {
        if (cancelArmed) return L("Cancel");
        if (index is { } i && i >= 0 && i < items.Count)
            return style == MenuStyle.Wheel ? items[i].DisplayTitle : $"{mode.Title()}: {items[i].DisplayTitle}";
        return mode.Title();
    }

    /// A flash on the confirmed item as it is released (0 → 1 → 0).
    double Flash => pressed == null ? 0 : Math.Sin(Math.PI * Math.Clamp(exit.Value, 0, 1));

    static readonly FontWeight Semibold = FontWeights.SemiBold;

    void DrawWheel()
    {
        // Frosted keycaps on clear glass: the desktop shows through, labels stay legible.
        bool dark = IsDark;
        var cap = dark ? Look.Gray(0, 0.30) : Look.Gray(1, 0.38);
        var hubFill = dark ? Look.Gray(0, 0.38) : Look.Gray(1, 0.6);
        var line = dark ? Look.Gray(1, 0.28) : Look.Gray(1, 0.9);
        var accent = AccentColour;
        var c = Center;
        if (Native.TransparencyOff && BackdropUp is { } backdrop)
        {
            // No glass with transparency effects off: a solid disc keeps the labels readable on any desktop.
            var r = backdrop.Rect;
            painter.Plain.DrawEllipse(Look.Brush(WindowBackground(dark).WithAlpha(0.96)), null,
                new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        }
        var all = Slots();

        // Raised sectors are drawn last so their shadow falls on their neighbours.
        foreach (int i in Enumerable.Range(0, all.Count).OrderBy(PopValue))
        {
            var slot = all[i];
            if (slot.Alpha <= 0.01) continue;
            double p = Math.Clamp(PopValue(i), 0, 1), sink = PressValue(i);
            double tint = TintValue(i);
            painter.Save();
            painter.SetAlpha(slot.Alpha);
            var fill = cap.Mix(accent, tint);
            if (sink > 0) fill = fill.Mix(pressedTint, sink * 0.6);
            if (i == pressed && Flash > 0) fill = fill.Mix(Colors.White, 0.3 * Flash);
            using (var s = painter.Shadow(3 + 9 * p * (1 - sink), (dark ? 0.4 : 0.12) + 0.18 * p * (1 - sink), 1 + 3 * p * (1 - sink)))
                s.Context.DrawGeometry(Look.Brush(fill), null, slot.Path);
            var dc = painter.Plain;
            if (p > 0.01)
            {
                // A soft sheen across the raised keycap, and a light under the pointer that follows it.
                var bounds = slot.Path.Bounds;
                dc.DrawGeometry(Painter.Gradient(bounds, -90, Colors.White.WithAlpha(0.28 * p * (1 - sink)), Colors.White.WithAlpha(0)), null, slot.Path);
                if (pointer is { } at && i == hovered && !Motion.Reduced)
                    dc.DrawGeometry(Painter.Radial(at, 70, Colors.White.WithAlpha(0.22 * p * (1 - sink)), Colors.White.WithAlpha(0)), null, slot.Path);
            }
            dc.DrawGeometry(null, Painter.Pen(line.Mix(Colors.White.WithAlpha(0.9), tint), 1), slot.Path);
            double labelAlpha = i == hovered || sink > 0 ? 1 : reveal.Value;
            DrawLabel(items[i].DisplayTitle, slot, Ink.Mix(Colors.White, tint).WithAlpha(labelAlpha), glow: tint < 0.5);
            painter.Restore();
        }

        // Hub: shrinks in on open, fades as a confirmed menu clears.
        double hubBloom = blooms.Length == 0 ? 1 : Math.Clamp(BloomValue(0) * 1.2, 0, 1);
        double sunk = hubPress.Value;
        double still = Motion.Reduced ? 0 : 1;
        double hubRadius = InnerRadius * (0.86 + 0.14 * hubBloom) * (1 - 0.04 * sunk * still);
        painter.Save();
        painter.SetAlpha(Math.Clamp(hubBloom, 0, 1) * (pressed == null ? 1 : 1 - exit.Value));
        var hub = new EllipseGeometry(c, hubRadius, hubRadius);
        hub.Freeze();
        using (var s = painter.Shadow(3 * (1 - sunk), dark ? 0.4 : 0.12))
            s.Context.DrawGeometry(Look.Brush(hubFill.Mix(dark ? Look.Gray(0, 0.55) : Look.Gray(0.82, 0.7), sunk)), null, hub);
        var hubDc = painter.Plain;
        hubDc.DrawGeometry(null, Painter.Pen(line, 1), hub);
        if (sunk > 0.01)
        {
            // A small × above the title while releasing would cancel.
            var x = new Point(c.X, c.Y + 24);
            const double r = 4;
            var pen = Painter.Pen(Ink.WithAlpha(0.7 * sunk), 1.6, round: true);
            hubDc.DrawLine(pen, new Point(x.X - r, x.Y - r), new Point(x.X + r, x.Y + r));
            hubDc.DrawLine(pen, new Point(x.X - r, x.Y + r), new Point(x.X + r, x.Y - r));
        }

        // A short accent arc on the hub's rim turns to the chosen sector, like a dial clicking into place.
        if (dialAlpha.Value > 0.01)
        {
            double span = Math.Min(Step * 0.7, 54);
            double radius = hubRadius - 2.5;
            double from = dial.Value + turn.Value - span / 2, to = dial.Value + turn.Value + span / 2;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(Polar(c, radius, from), false, false);
                g.ArcTo(Polar(c, radius, to), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            hubDc.DrawGeometry(null, Painter.Pen(Look.Accent.WithAlpha(0.95 * Math.Min(dialAlpha.Value, 1)), 3, round: true), arc);
        }

        DrawHeadline(new Point(c.X, c.Y + 7), hubRadius * 1.7);
        DrawText(clickable ? (hovered == null ? L("Esc to close") : L("Click or Enter"))
            : cancelArmed ? L("release to cancel") : hovered == null ? Subtitle : L("drop to apply"),
            new Point(c.X, c.Y - 8), FontWeights.Medium, 8.5, Ink.WithAlpha(0.6), hubRadius * 1.6);
        DrawPageDots(new Point(c.X, c.Y - 26), hintBelow: true);
        painter.Restore();
    }

    static Color WindowBackground(bool dark) => dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// The wheel hub's title: crossfades with a small vertical slide when the hovered item changes.
    void DrawHeadline(Point point, double maxWidth)
    {
        double s = Math.Clamp(swap.Value, 0, 1);
        var current = Headline(hovered);
        // The old title leaves upwards in the first third; the new one rises in after it, so they never overlap.
        bool changing = previousHeadline != current;
        double slide = Motion.Reduced ? 0 : 6;   // Reduce Motion: a plain crossfade
        double outT = Math.Min(s / 0.35, 1), into = Math.Clamp((s - 0.2) / 0.8, 0, 1);
        if (changing && outT < 1)
        {
            bool wasItem = previousHeadline != mode.Title() && previousHeadline != L("Cancel");
            DrawText(previousHeadline, new Point(point.X, point.Y + slide * outT), FontWeights.Bold, 12,
                (wasItem ? AccentInk : Ink).WithAlpha(1 - outT), maxWidth);
        }
        DrawText(current, new Point(point.X, point.Y - (changing ? slide * (1 - into) : 0)), FontWeights.Bold, 12,
            (hovered == null ? Ink : AccentInk).WithAlpha(changing ? into : 1), maxWidth);
    }

    /// Title, file name and page dots above the items (every style but the wheel).
    void DrawHeader(bool onGlass)
    {
        var rect = new Rect(0, viewSize.Height - HeaderHeight, viewSize.Width, HeaderHeight);
        var title = Headline(hovered);
        var detail = clickable ? (hovered == null ? L("Esc to close") : L("Click or Enter"))
            : cancelArmed ? L("release to cancel") : hovered == null ? Subtitle : L("drop to apply");
        FontWeight bold = Fw(FontWeights.Bold), regular = Fw(FontWeights.Normal), semibold = Fw(Semibold);
        double titleSize = Fs(13), detailSize = Fs(11), hintSize = Fs(9);
        // Sized for the widest title this page can show, so hovering never slides the header sideways.
        double shownTitle = painter.Width(title, Look.UI, bold, titleSize);
        double titleWidth = Math.Max(shownTitle, headerTitles.Select(t => painter.Width(t, Look.UI, bold, titleSize)).DefaultIfEmpty(0).Max());
        var details = clickable ? new[] { L("Esc to close"), L("Click or Enter") } : new[] { Subtitle, L("drop to apply"), L("release to cancel") };
        double detailWidth = Math.Min(details
            .Max(t => painter.Width(t, Look.UI, regular, detailSize)), 150);
        // Windows key names are words ("Shift next"), not symbols, so the dots' room follows the hint's width.
        // In a narrow menu the hint shortens to the key's name, so the detail text keeps its room.
        headerHint = PageHint;
        double hintWidth = pageCount > 1 ? Math.Min(painter.Width(headerHint, Look.UI, semibold, hintSize), 64) : 0;
        double dotsWidth = pageCount > 1 ? (pageCount - 1) * 7 + 4 + 10 + hintWidth + 8 : 0;
        if (pageCount > 1 && titleWidth + Math.Min(detailWidth, 80) + dotsWidth + 34 > viewSize.Width - 8)
        {
            headerHint = PageKey;
            hintWidth = Math.Min(painter.Width(headerHint, Look.UI, semibold, hintSize), 64);
            dotsWidth = (pageCount - 1) * 7 + 4 + 10 + hintWidth + 8;
        }
        double capsuleWidth = Math.Min(titleWidth + detailWidth + dotsWidth + 34, viewSize.Width - 8);
        // In a narrow menu the detail text gives way so it never runs into the page dots.
        detailWidth = Math.Max(Math.Min(detailWidth, capsuleWidth - titleWidth - dotsWidth - 34), 0);
        var capsule = new Rect(rect.X + rect.Width / 2 - capsuleWidth / 2, rect.Y + rect.Height / 2 - 13, capsuleWidth, 26);
        double midY = capsule.Y + 13;

        painter.Save();
        painter.SetAlpha(pressed == null ? 1 : 1 - exit.Value);
        if (!onGlass)
        {
            using var s = painter.Shadow(6, IsDark ? 0.5 : 0.18);
            s.Context.DrawRoundedRectangle(Look.Brush(IsDark ? Look.Gray(0.12, 0.86) : Look.Gray(1, 0.88)), null, capsule, 13, 13);
        }
        double sw = Math.Clamp(swap.Value, 0, 1);
        double x = capsule.X + 13;
        double titleShown = Math.Min(shownTitle, titleWidth);
        var dc = painter.Plain;
        painter.Text(dc, title, new Point(x + titleShown / 2, midY - (Motion.Reduced ? 0 : 3 * (1 - sw))), Look.UI, bold, titleSize,
            (hovered == null ? Ink : AccentInk).WithAlpha(0.4 + 0.6 * sw), titleWidth, minSize: MinText);
        x += titleShown + 8;
        double shownDetail = Math.Min(painter.Width(detail, Look.UI, regular, detailSize), detailWidth);
        if (detailWidth > 4)
            painter.Text(dc, detail, new Point(x + shownDetail / 2, midY), Look.UI, regular, detailSize, Ink.WithAlpha(0.55), detailWidth, minSize: MinText);
        if (pageCount > 1)
            DrawHeaderDots(dc, capsule.Right - 13 - hintWidth - 8 - (pageCount - 1) * 7, midY, hintWidth, semibold, hintSize);
        painter.Restore();
    }

    /// The key that turns the page: the menu's drag key, or Space in a clickable menu.
    string PageKey => clickable ? L("Space") : mode.KeySymbol();
    string PageHint => L("%@ next", PageKey);

    double DotSize(int p) => p == page && !Motion.Reduced ? 4 * (1 + 0.5 * (1 - Math.Clamp(reveal.Value, 0, 1))) : 4;

    void DrawHeaderDots(System.Windows.Media.DrawingContext dc, double dotsX, double y, double hintWidth, FontWeight weight, double size)
    {
        for (int p = 0; p < pageCount; p++)
        {
            double dot = DotSize(p);
            dc.DrawEllipse(Look.Brush(p == page ? AccentColour : Ink.WithAlpha(0.3)), null, new Point(dotsX + p * 7, y), dot / 2, dot / 2);
        }
        double hintX = dotsX + (pageCount - 1) * 7 + 10;
        painter.Text(dc, headerHint, new Point(hintX + hintWidth / 2, y), Look.UI, weight, size, Ink.WithAlpha(0.55), hintWidth, minSize: MinText);
    }

    string headerHint = "";

    /// The wheel hub's page dots, with the key hint below them.
    void DrawPageDots(Point point, bool hintBelow)
    {
        if (pageCount <= 1) return;
        double width = (pageCount - 1) * 7.0;
        double dotsX = point.X - width / 2;
        var dc = painter.Plain;
        for (int p = 0; p < pageCount; p++)
        {
            double size = DotSize(p);
            dc.DrawEllipse(Look.Brush(p == page ? AccentColour : Ink.WithAlpha(0.3)), null, new Point(dotsX + p * 7, point.Y), size / 2, size / 2);
        }
        if (hintBelow) DrawText(PageHint, new Point(point.X, point.Y - 10), Semibold, 7.5, Ink.WithAlpha(0.55), 80);
    }

    void DrawList()
    {
        bool dark = IsDark;
        // Light tint over the glass backdrop so rows read like a native context menu.
        if (BackdropUp is { } backdrop)
        {
            var dc0 = painter.Plain;
            dc0.DrawRoundedRectangle(Look.Brush(dark ? Look.Gray(0, 0.25) : Look.Gray(1, 0.35)),
                Painter.Pen(Colors.White.WithAlpha(dark ? 0.15 : 0.7), 0.5), backdrop.Rect, backdrop.Radius, backdrop.Radius);
        }
        DrawHeader(onGlass: true);
        var dc = painter.Plain;
        dc.DrawRectangle(Look.Brush(dark ? Look.Gray(1, 0.1) : Look.Gray(0, 0.1)), null,
            new Rect(16, viewSize.Height - HeaderHeight - 1, Math.Max(viewSize.Width - 32, 0), 1));

        // One highlight that slides from row to row instead of jumping.
        if (rowAlpha.Value > 0.01)
        {
            var rect = ListRect(row.Value);
            double sunk = pressed is { } pi ? PressValue(pi) : 0;
            if (!Motion.Reduced) rect = Inset(rect, rect.Width * 0.02 * sunk, rect.Height * 0.06 * sunk);
            var fill = AccentColour.WithAlpha(0.92 * Math.Min(rowAlpha.Value, 1));
            if (sunk > 0) fill = fill.Mix(pressedTint, sunk * 0.6);
            if (Flash > 0) fill = fill.Mix(Colors.White, 0.3 * Flash);
            dc.DrawRoundedRectangle(Look.Brush(fill), null, rect, 7, 7);
        }

        var slots = Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            // Text turns white as the highlight passes under it.
            double under = rowAlpha.Value * Math.Max(0, 1 - Math.Abs(row.Value - i));
            double bloom = Math.Clamp(BloomValue(i) * 1.5, 0, 1);
            var color = Ink.Mix(Colors.White, under).WithAlpha(bloom * (1 - ExitValue(i)) * (i == hovered ? 1 : reveal.Value));
            double nudge = Motion.Reduced ? 0 : 3 * Math.Clamp(PopValue(i), 0, 1) - 8 * (1 - bloom);
            painter.TextLeft(dc, items[i].DisplayTitle, slot.Label.X + nudge, slot.Label.Y, Look.UI, Fw(FontWeights.Medium), Fs(slot.FontSize), color, slot.MaxWidth);
        }
    }

    // MARK: Quiet templates (1.3.3)
    //
    // Bookshelf, Sticky Notes and Clothesline draw near-neutral opaque cards (MenuAppearance) with fine edges and
    // gentle shadows, in the system font. Bookshelf and Sticky Notes take their card and rail colours from the user's
    // template palette; the Clothesline template's rope and pegs follow the real clothesline's colours. Only a small
    // selection tint uses the accent colour.

    /// A fine card edge that takes on some of the accent as the item is selected.
    static Pen CardEdge(MenuAppearance theme, double tint) =>
        Painter.Pen(theme.Edge.Mix(MenuAppearance.Accent, 0.45 * tint), 0.75 + 0.5 * tint);

    void DrawShelf()
    {
        bool dark = IsDark;
        var theme = new MenuAppearance(dark);
        var palette = Prefs.TemplateColors[ColorTemplate.Shelf];
        DrawHeader(onGlass: false);

        // A satin rail and inset brackets replace the varnished wood and gold bands.
        var plank = new Rect(10, ShelfTop - 16, Math.Max(viewSize.Width - 20, 0), 16);
        using (var s = painter.Shadow(7, 0.14))
            s.Context.DrawRoundedRectangle(Look.Brush(palette.RailColor(dark)), null, plank, 5, 5);
        var dc = painter.Plain;
        dc.DrawRoundedRectangle(null, Painter.Pen(theme.Edge, 0.75), plank, 5, 5);
        foreach (double bx in new[] { plank.X + 22, plank.Right - 34 })
            dc.DrawRoundedRectangle(Look.Brush(theme.Trim), null, new Rect(bx, plank.Y - 10, 12, 12), 3, 3);

        // Books rise out from behind the plank as the menu opens, so clip them to above its top.
        painter.Save();
        painter.SetClip(Frozen(new RectangleGeometry(new Rect(0, ShelfTop - 3, viewSize.Width, viewSize.Height))));
        var slots = Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Alpha <= 0.01) continue;
            double p = Math.Clamp(PopValue(i), 0, 1), tint = TintValue(i);
            painter.Save();
            painter.SetAlpha(slot.Alpha);
            var color = palette.Card(i, dark, tint);
            if (i == pressed && Flash > 0) color = color.Mix(theme.Surface, 0.3 * Flash);
            using (var s = painter.Shadow(3 + 4 * p, 0.15, 1 + p))
                s.Context.DrawGeometry(Look.Brush(color), null, slot.Path);
            var book = painter.Plain;
            book.DrawGeometry(null, CardEdge(theme, tint), slot.Path);
            // Two hairline bands where the gilt used to be.
            var rect = slot.Path.Bounds;
            var band = Look.Brush(theme.Edge);
            book.DrawRectangle(band, null, new Rect(rect.X + 5, rect.Bottom - 16, Math.Max(rect.Width - 10, 0), 0.75));
            book.DrawRectangle(band, null, new Rect(rect.X + 5, rect.Y + 14, Math.Max(rect.Width - 10, 0), 0.75));
            DrawLabel(items[i].DisplayTitle, slot, palette.Ink(i, dark, tint).WithAlpha(i == hovered ? 1 : reveal.Value),
                glow: false, weight: Semibold);
            painter.Restore();
        }
        painter.Restore();
    }

    void DrawNotes()
    {
        bool dark = IsDark;
        var theme = new MenuAppearance(dark);
        var palette = Prefs.TemplateColors[ColorTemplate.Notes];
        DrawHeader(onGlass: false);
        var all = Slots();
        foreach (int i in Enumerable.Range(0, all.Count).OrderBy(PopValue))
        {
            var slot = all[i];
            if (slot.Alpha <= 0.01) continue;
            double p = Math.Clamp(PopValue(i), 0, 1), tint = TintValue(i), lifted = p * (1 - PressValue(i));
            painter.Save();
            painter.SetAlpha(slot.Alpha);
            var color = palette.Card(i, dark, tint);
            if (i == pressed && Flash > 0) color = color.Mix(theme.Surface, 0.3 * Flash);
            using (var s = painter.Shadow(5 + 5 * lifted, 0.14, 1 + 2 * lifted))
                s.Context.DrawGeometry(Look.Brush(color), null, slot.Path);
            var dc = painter.Plain;
            dc.DrawGeometry(null, CardEdge(theme, tint), slot.Path);
            // A small paper tab keeps the note metaphor without a large strip of tape.
            var box = slot.Path.Bounds;
            var tab = new RectangleGeometry(new Rect(box.X + box.Width / 2 - 13, box.Bottom - 10, 26, 5), 2.5, 2.5);
            var m = Matrix.Identity;
            m.RotateAt(slot.Angle, box.X + box.Width / 2, box.Bottom - 5);
            tab.Transform = new MatrixTransform(m);
            tab.Freeze();
            dc.DrawGeometry(Look.Brush(theme.Trim.WithAlpha(0.4)), null, tab);
            DrawLabel(items[i].DisplayTitle, slot, palette.Ink(i, dark, tint).WithAlpha(i == hovered ? 1 : reveal.Value),
                glow: false, weight: FontWeights.Medium);
            painter.Restore();
        }
    }

    void DrawClothesline()
    {
        bool dark = IsDark;
        var theme = new MenuAppearance(dark);
        var colors = Prefs.ClotheslineColors;
        DrawHeader(onGlass: false);
        double left = 22, right = viewSize.Width - 22;

        foreach (double x in new[] { left, right })
        {
            using var s = painter.Shadow(3, 0.14);
            s.Context.DrawRoundedRectangle(Look.Brush(theme.Rail), null, new Rect(x - 4, 6, 8, Math.Max(RopeY(x) - 6 + 8, 0)), 4, 4);
        }
        var rope = new StreamGeometry();
        using (var g = rope.Open())
        {
            g.BeginFigure(new Point(left, RopeY(left)), false, false);
            for (double x = left; x <= right + 0.001; x += 4) g.LineTo(new Point(x, RopeY(x)), true, true);
        }
        rope.Freeze();
        using (var s = painter.Shadow(2, 0.18))
            s.Context.DrawGeometry(null, Painter.Pen(colors.Rope.Color, 2.5, round: true), rope);

        var slots = Slots();
        var peg = Look.Brush(colors.Peg.Color);
        var hinge = Look.Brush(theme.Edge);
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Alpha <= 0.01) continue;
            double p = Math.Clamp(PopValue(i), 0, 1), tint = TintValue(i);
            painter.Save();
            painter.SetAlpha(slot.Alpha);
            var card = theme.Card(i, tint);
            if (i == pressed && Flash > 0) card = card.Mix(theme.Surface, 0.3 * Flash);
            using (var s = painter.Shadow(4 + 4 * p, 0.15, 1 + p))
                s.Context.DrawGeometry(Look.Brush(card), null, slot.Path);
            painter.Plain.DrawGeometry(null, CardEdge(theme, tint), slot.Path);
            // Peg where the tag meets the rope.
            var bounds = slot.Path.Bounds;
            double x = bounds.X + bounds.Width / 2;
            double pegTop = RopeY(x) + 7;
            using (var s = painter.Shadow(2, 0.16))
                s.Context.DrawRoundedRectangle(peg, null, new Rect(x - 4, pegTop - 24, 8, 24), 3, 3);
            painter.Plain.DrawRectangle(hinge, null, new Rect(x - 4, pegTop - 12, 8, 1));
            DrawLabel(items[i].DisplayTitle, slot, theme.Ink.WithAlpha(i == hovered ? 1 : reveal.Value), glow: false,
                weight: FontWeights.Medium);
            painter.Restore();
        }
    }

    // Menu text follows Feel & Text's size and bold text (the Mac's AppTypography.font). Sizes here are the Mac's
    // points at standard size; `Fs`/`Fw` apply the user's choice.
    static double Fs(double size) => AppTypography.Size(size);
    static FontWeight Fw(FontWeight weight) => AppTypography.Weight(weight);
    /// Long labels shrink to fit, but never below the chosen reading size's floor; past that they lose their middle.
    static double MinText => Fs(7);

    /// Draws a slot's label at its centre, rotated by the slot's angle.
    void DrawLabel(string text, Slot slot, Color color, bool glow, FontWeight? weight = null)
    {
        var dc = glow ? painter.Glow(labelGlow) : painter.Plain;
        painter.Text(dc, text, slot.Label, Look.UI, Fw(weight ?? Semibold), Fs(slot.FontSize), color, slot.MaxWidth,
            slot.Angle, slot.LabelScale, MinText);
    }

    /// Centred text; glows by default, like the Mac's drawText.
    void DrawText(string text, Point at, FontWeight weight, double size, Color color, double maxWidth, bool glow = true)
    {
        if (color.A < 3) return;
        var dc = glow ? painter.Glow(labelGlow) : painter.Plain;
        painter.Text(dc, text, at, Look.UI, Fw(weight), Fs(size), color, maxWidth, minSize: MinText);
    }
}

/// UI Automation for the menu: in a clickable menu (the keyboard tool wheel) every item is a button that can be
/// invoked, followed by Cancel and, with several pages, Next Page (the Mac's WheelAccessibilityButton). A drag menu
/// keeps the default peer: it is a drop target, not something to click.
sealed class WheelViewPeer(WheelView view) : FrameworkElementAutomationPeer(view)
{
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        view.IsClickable ? AutomationControlType.Menu : AutomationControlType.Custom;

    protected override string GetClassNameCore() => "SwivelMenu";

    protected override string GetNameCore() => "Swivel";

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (!view.IsClickable) return base.GetChildrenCore();
        var children = new List<AutomationPeer>();
        for (int i = 0; i < view.Items.Count; i++)
        {
            int index = i;
            children.Add(new WheelButtonPeer(view.Items[i].DisplayTitle, () => view.ItemScreenRect(index), () => view.Invoke(index)));
        }
        children.Add(new WheelButtonPeer(L("Cancel"), null, () => view.Cancelled?.Invoke()));
        if (view.PageCount > 1) children.Add(new WheelButtonPeer(L("Next Page"), null, () => view.NextPage?.Invoke()));
        return children;
    }
}

/// One invokable button of a clickable menu. Cancel and Next Page have no outline of their own (Esc and Space).
sealed class WheelButtonPeer(string name, Func<Rect>? bounds, Action press) : AutomationPeer, IInvokeProvider
{
    public void Invoke() => press();
    public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : null;
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    protected override string GetAutomationIdCore() => "";
    protected override Rect GetBoundingRectangleCore() => bounds?.Invoke() ?? Rect.Empty;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override string GetClassNameCore() => "SwivelMenuItem";
    protected override Point GetClickablePointCore() =>
        bounds?.Invoke() is { IsEmpty: false } r ? new Point(r.X + r.Width / 2, r.Y + r.Height / 2) : new Point(double.NaN, double.NaN);
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override string GetNameCore() => name;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override bool HasKeyboardFocusCore() => false;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool IsOffscreenCore() => bounds != null && bounds().IsEmpty;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override void SetFocusCore() { }
}
