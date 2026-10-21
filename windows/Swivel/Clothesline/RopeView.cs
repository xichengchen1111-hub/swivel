using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Swivel;

/// How a newly hung print appears.
enum RopeArrival
{
    FromAbove,  // hung from the Actions menu: drops onto the rope and swings a little
    InPlace,    // dropped on the rope: fades in where the drag let go
    HandOff,    // its card is flying up to it: hidden until `HandOff(_:)`
}

/// What the rope asks its controller to do (the Mac view calls `Clothesline.shared` directly).
interface IRopeHost
{
    void Copy(string path);
    void Open(string path);
    void Preview(string path);
    void Discard(string path, bool fall);
    void ExtractText(string path);
    void Reveal(string path);
    /// A print was dragged out and dropped into a folder (File Explorer or the desktop): it comes off the rope.
    void TakenDown(string path);
    /// The file is gone (moved by the drop, or recycled): reload.
    void Reload();
    bool CanDiscardFile(string path);
    /// Files dropped on the rope.
    void Take(IReadOnlyList<string> paths);
    void Receive(IReadOnlyList<string> paths);
}

/// Draws the rope and its prints, moved on every display frame by a small physics model: the rope falls on a spring
/// and sags, each print is a damped pendulum on its peg, and a light breeze stirs them for a few seconds after
/// anything happens. Hover, press and sliding along the rope are retargetable springs. Everything is interruptible
/// because the state is simulated, not tweened. Once nothing moves the frame loop stops, and anything that changes
/// the picture wakes it. A port of the Mac `RopeView` (Core Animation layers → WPF visuals, same y-down geometry).
sealed class RopeView : FrameworkElement
{
    public const double PanelHeight = 214;
    static readonly double HiddenOffset = -(PanelHeight + 30);
    public const double RopeTop = 18;
    const double RestSag = 16;
    // Rope and peg share their saved colours with the Clothesline menu template (1.3.3); both default to Silver.
    ClotheslineColors Palette => colorsOverride ?? Prefs.ClotheslineColors;
    Color RopeColor => Palette.Rope.Color;
    Color PegColor => Palette.Peg.Color;
    static readonly Color Green = Color.FromRgb(52, 199, 89);
    static readonly double[] Tilts = [0.018, -0.015, 0.012, -0.02, 0.015, -0.012];
    static readonly Random random = new();

    sealed class Print(string path, Size size)
    {
        public readonly string Path = path;
        public Size Size = size;
        public ImageSource? Thumb;
        public bool Movie;
        public bool Placed;
        public bool Hidden;                     // doesn't fit before the right edge
        public Spring Slot = new(0, RopeTuning.Slide.Response, RopeTuning.Slide.Damping);   // x along the rope
        public double Tilt;                     // resting angle, so the line doesn't look ruled
        public double Angle, AngleV;
        public double Drop, DropV;
        public Spring Lift = new(0, RopeTuning.Lift.Response, RopeTuning.Lift.Damping);
        public Spring Hover = new(0, RopeTuning.FadeIn, 1);                                  // ring, × and shadow
        public Spring Press = new(0, RopeTuning.PressIn.Response, RopeTuning.PressIn.Damping);
        public Spring Opacity = new(1, RopeTuning.FadeIn, 1);   // a ghost while dragged off; fades in on arrival
        public Spring Flash = new(0, 0.35, 1);                  // the ring turns green after a copy
        public Spring ClosePress = new(0, 0.08, 1);
        public double? KickAt;
        public double Kick;
        public double? AwaitingSince;           // its card is still flying here
        public bool Lifted;                     // being dragged away
        public double? ChipStart;               // the "Copied" chip, by the animation clock
        public readonly double Phase = random.NextDouble() * 2 * Math.PI;
        public bool OnTop;
        public double DrawnClose = -1;
        public Color PegColor;

        // Visuals: Root carries the print's transform and opacity; the rest are its parts.
        public readonly ContainerVisual Root = new();
        public readonly DrawingVisual Pad = new();      // invisible, catches the mouse over the slack around the print
        public readonly DrawingVisual Body = new();     // the white-bordered picture, with its shadow
        public readonly DrawingVisual Peg = new();
        public readonly DrawingVisual Ring = new();
        public readonly DrawingVisual Close = new();
        public readonly DrawingVisual Chip = new();
        public readonly DropShadowEffect Shadow = new() { Color = Colors.Black, Direction = 270, RenderingBias = RenderingBias.Performance };
        public readonly SolidColorBrush RingBrush = new(Colors.DodgerBlue);
        public readonly ScaleTransform CloseScale = new();
        public readonly TranslateTransform ChipRise = new();

        public bool IsTouchable => !Hidden && AwaitingSince is null && !Lifted;
    }

    /// A print on its way out (fell off after ×, or faded because its file went), animated by the clock.
    sealed class Leaving
    {
        public required ContainerVisual Root;
        public required Size Size;
        public double X, Y, Angle, Scale, Opacity, Start;
        public bool Fall;
        public bool Reduced;
    }

    readonly VisualCollection children;
    readonly DrawingVisual backdrop = new();  // a 1/255 fill while something is being dropped, so all of the strip takes it
    readonly DrawingVisual ropeVisual = new();
    readonly List<Print> prints = [];
    readonly List<Leaving> leaving = [];
    readonly Dictionary<string, RopeArrival> arrivals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BitmapSource> thumbnails = new(StringComparer.OrdinalIgnoreCase);   // handed over by the card
    readonly HashSet<string> departing = new(StringComparer.OrdinalIgnoreCase);  // already off the rope; reloads ignore them
    readonly FrameDriver driver = new();
    double accumulator;
    double clock;          // physics time
    double animClock;      // frame time, for the few fixed-length exits

    // Rope state: vertical offset of the whole rope (falls in from above) and extra sag. A hidden rope stays hidden.
    double offset = -PanelHeight, offsetV, offsetTarget = HiddenOffset;
    double sag, sagV;
    Spring fade = new(1, 0.16, 1);        // the strip's opacity; animates only under reduced motion
    Spring highlight = new(0, 0.12, 1);   // something droppable is over the rope
    Spring breeze = new(0, RopeTuning.BreezeFade, 1);   // the wind's strength, 0…1
    double calmAt;                        // when the breeze starts to die down
    double[] drawnRope = [];              // what the rope's path was last built from
    readonly DropShadowEffect ropeShadow = new() { Color = Colors.Black, Opacity = 0.18, BlurRadius = 4, ShadowDepth = 2, Direction = 270, RenderingBias = RenderingBias.Performance };
    ClotheslineColors? drawnColors;       // what the cord and pegs were last drawn in
    ClotheslineColors? colorsOverride;

    /// For frame renders: colours to draw instead of the saved ones (so a render never writes the settings file).
    public ClotheslineColors? ColorsOverride
    {
        get => colorsOverride;
        set { colorsOverride = value; ColorsMayHaveChanged(); }
    }
    double insertGap;                     // room held open at the front for a drop
    Action? onLifted;
    string? hovered;
    bool receivingHighlight;
    bool isDraggingItem, isReceiving;
    bool? catching;
    double width;

    public IRopeHost? Host { get; set; }
    /// Whether the strip is on screen (the Mac's `window?.isVisible`). Tests and frame renders leave it off.
    public Func<bool> IsOnScreen { get; set; } = () => false;
    /// The pointer in view coordinates (from the controller's cursor poll), or null when it isn't over this monitor.
    public Func<Point?> PointerSource { get; set; } = () => null;
    /// Tells the window whether to catch the mouse (false = click-through).
    public Action<bool>? CatchMouse { get; set; }
    /// For tests and frame renders: a pointer instead of the real mouse, and a fixed motion setting.
    public Point? SimulatedPointer { get; set; }
    public bool? ReducedMotionOverride { get; set; }
    bool IsMotionReduced => ReducedMotionOverride ?? Motion.Reduced;

    public bool IsLifting { get; private set; }

    /// A print is being dragged off the rope (or its context menu is open): the rope stays down.
    public bool IsDraggingItem
    {
        get => isDraggingItem;
        set { if (isDraggingItem != value) { isDraggingItem = value; Wake(); } }
    }

    /// A card is being dragged toward the rope.
    public bool IsReceiving
    {
        get => isReceiving;
        set { if (isReceiving != value) { isReceiving = value; DrawBackdrop(); Wake(); } }
    }

    public RopeView(double width = 800)
    {
        children = new VisualCollection(this) { backdrop, ropeVisual };
        ropeVisual.Effect = ropeShadow;
        drawnColors = Palette;
        ListenForColors(this);
        driver.OnFrame = dt => Advance(dt);
        this.width = width;
        Width = width;
        Height = PanelHeight;
        AllowDrop = true;
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 700);
        Unloaded += (_, _) => driver.Stop();
    }

    /// Colour changes in Settings reach a rope that is already down (and its pegs) at once. The handler holds the
    /// view weakly, so views made by tests and frame renders can still go away.
    static void ListenForColors(RopeView view)
    {
        var weak = new WeakReference<RopeView>(view);
        Action? handler = null;
        handler = () =>
        {
            if (weak.TryGetTarget(out var target)) target.ColorsMayHaveChanged();
            else Prefs.Changed -= handler;
        };
        Prefs.Changed += handler;
    }

    void ColorsMayHaveChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ColorsMayHaveChanged));
            return;
        }
        var colors = Palette;
        if (colors == drawnColors) return;
        drawnColors = colors;
        foreach (var print in prints) DrawPeg(print);
        drawnRope = [];   // colour changes must redraw a resting rope too
        Render(IsMotionReduced);
    }

    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];
    protected override Size MeasureOverride(Size availableSize) => new(width, PanelHeight);
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    public double ViewWidth => width;

    /// The strip's new width (the monitor it lowers on).
    public void Resize(double newWidth)
    {
        if (Math.Abs(newWidth - width) < 0.5) return;
        width = newWidth;
        Width = newWidth;
        LayoutPrints(snap: true);
        DrawBackdrop();
        Render(IsMotionReduced);
    }

    void DrawBackdrop()
    {
        using var dc = backdrop.RenderOpen();
        if (isReceiving || receivingHighlight)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, width, PanelHeight));
    }

    // MARK: Contents

    public void MarkNew(string path, RopeArrival arrival = RopeArrival.FromAbove, BitmapSource? thumbnail = null)
    {
        arrivals[path] = arrival;
        if (thumbnail != null) thumbnails[path] = thumbnail;
    }

    public IReadOnlyList<string> Paths => prints.Select(p => p.Path).ToList();

    public void SetItems(IReadOnlyList<string> paths)
    {
        var list = paths.Where(p => !departing.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        var existing = new Dictionary<string, Print>(StringComparer.OrdinalIgnoreCase);
        foreach (var print in prints) existing.TryAdd(print.Path, print);
        foreach (var print in prints.ToList()) if (!wanted.Contains(print.Path)) Retire(print);
        var fresh = new List<Print>();
        var next = list.Select(path =>
        {
            if (existing.TryGetValue(path, out var print)) return print;
            print = MakePrint(path);
            fresh.Add(print);
            return print;
        }).ToList();
        prints.Clear();
        prints.AddRange(next);
        RestackPrints();
        // A drop's gap closes as the new print takes its place.
        if (!receivingHighlight) insertGap = 0;
        LayoutPrints();
        foreach (var print in fresh) Arrive(print, arrivals.TryGetValue(print.Path, out var a) ? a : null);
        arrivals.Clear();
        thumbnails.Clear();
        // Place new visuals now: otherwise one frame could show them at the origin before the frame loop runs.
        Render(IsMotionReduced);
        Wake();
    }

    void Arrive(Print print, RopeArrival? arrival)
    {
        bool reduced = IsMotionReduced;
        double sign = random.Next(2) == 0 ? 1 : -1;
        switch (arrival)
        {
            case RopeArrival.FromAbove when !reduced:
                // Falls onto the rope, settles and swings a little.
                print.Drop = RopeTuning.ArrivalHeight;
                print.AngleV = random.Between(RopeTuning.ArrivalSwingFromAbove) * sign;
                break;
            case RopeArrival.HandOff:
                print.AwaitingSince = clock;
                break;
            default:
                // Dropped in place, or a print that appeared some other way: it fades in where it hangs.
                if (IsOnScreen())
                {
                    print.Opacity.Snap(0);
                    print.Opacity.Target = 1;
                }
                if (arrival == RopeArrival.InPlace && !reduced) print.AngleV = random.Between(RopeTuning.ArrivalSwingInPlace) * sign;
                break;
        }
    }

    void LayoutPrints(bool snap = false)
    {
        var slots = RopeTuning.Slots(prints.Select(p => p.Size.Width).ToList(), width, insertGap);
        bool reduced = IsMotionReduced;
        bool instant = snap || reduced;
        for (int i = 0; i < prints.Count; i++)
        {
            var print = prints[i];
            print.Hidden = !slots[i].Fits;
            print.Tilt = Tilts[i % Tilts.Length];
            if (reduced)
            {
                print.Angle = print.Tilt;  // no swing to a new resting angle
                print.AngleV = 0;
            }
            if (instant || !print.Placed)
            {
                print.Slot.Snap(slots[i].X);
                print.Placed = true;
            }
            else print.Slot.Target = slots[i].X;
        }
    }

    Print MakePrint(string path)
    {
        bool movie = RopeThumbnails.IsMovie(path);
        thumbnails.TryGetValue(path, out var handed);
        var thumb = handed ?? RopeThumbnails.Image(path, 400);
        var size = thumb != null ? RopeTuning.PrintSize(thumb.PixelWidth / (double)Math.Max(thumb.PixelHeight, 1)) : new Size(160, 110);
        var print = new Print(path, size) { Thumb = thumb, Movie = movie };
        print.Root.Children.Add(print.Pad);
        print.Root.Children.Add(print.Body);
        print.Root.Children.Add(print.Peg);
        print.Root.Children.Add(print.Ring);
        print.Root.Children.Add(print.Close);
        print.Root.Children.Add(print.Chip);
        print.Body.Effect = print.Shadow;
        print.Close.Transform = print.CloseScale;
        print.Chip.Transform = print.ChipRise;
        print.Chip.Opacity = 0;
        print.Ring.Opacity = 0;
        print.Close.Opacity = 0;
        print.RingBrush.Color = Accent;
        DrawPrint(print);
        if (movie && thumb is null) _ = LoadMovieThumbnail(print);
        return print;
    }

    /// Movies get their first frame from the video module, which works in the background.
    async Task LoadMovieThumbnail(Print print)
    {
        var thumb = await RopeThumbnails.LoadAsync(print.Path, 400);
        if (thumb is null || !prints.Contains(print)) return;
        print.Thumb = thumb;
        print.Size = RopeTuning.PrintSize(thumb.PixelWidth / (double)Math.Max(thumb.PixelHeight, 1));
        DrawPrint(print);
        LayoutPrints();
        Render(IsMotionReduced);
        Wake();
    }

    static Color Accent
    {
        get
        {
            try { return SystemColors.AccentColor; } catch (Exception) { return Color.FromRgb(0, 120, 212); }
        }
    }

    void DrawPrint(Print print)
    {
        var size = print.Size;
        var rect = new Rect(size);
        using (var dc = print.Pad.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(-4, -14, size.Width + 8, size.Height + 28));
        using (var dc = print.Body.RenderOpen()) DrawPicture(dc, print.Thumb, size, print.Movie);
        DrawPeg(print);
        // Ring and × fade with the hover spring instead of snapping on and off.
        using (var dc = print.Ring.RenderOpen())
            dc.DrawRoundedRectangle(null, new Pen(print.RingBrush, 2), new Rect(-1, -1, size.Width + 2, size.Height + 2), 4, 4);
        var close = CloseFrame(size);
        print.CloseScale.CenterX = close.X + close.Width / 2;
        print.CloseScale.CenterY = close.Y + close.Height / 2;
        print.DrawnClose = -1;
        DrawClose(print, 0);
    }

    /// Neutral peg gripping the rope and the top edge of the print, in the saved peg colour.
    void DrawPeg(Print print)
    {
        var color = PegColor;
        print.PegColor = color;
        using var dc = print.Peg.RenderOpen();
        var peg = new Rect(print.Size.Width / 2 - 5, -14, 10, 28);
        // A soft contact shadow (the Mac's peg shadow went from 0.4 to 0.18 in 1.3.3; alpha 70 → 32 here).
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(32, 0, 0, 0)), null, new Rect(peg.X - 0.5, peg.Y + 1, peg.Width + 1, peg.Height + 0.5), 3, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(color), null, peg, 2.5, 2.5);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(158, 158, 158)), null, new Rect(peg.X, peg.Y + 12, 10, 2));
    }

    /// The white-bordered picture (also used for the drag image, the preview's zoom and the card's flight).
    static void DrawPicture(DrawingContext dc, ImageSource? thumb, Size size, bool movie)
    {
        var rect = new Rect(size);
        dc.DrawRoundedRectangle(Brushes.White, null, rect, 3, 3);
        if (thumb != null)
        {
            var inner = new Rect(5, 5, Math.Max(size.Width - 10, 1), Math.Max(size.Height - 10, 1));
            dc.DrawImage(thumb, inner);
        }
        if (movie)
        {
            var c = new Point(rect.Width / 2, rect.Height / 2);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, c, 16, 16);
            var play = new StreamGeometry();
            using (var g = play.Open())
            {
                g.BeginFigure(new Point(c.X - 5, c.Y - 8), true, true);
                g.LineTo(new Point(c.X + 9, c.Y), true, false);
                g.LineTo(new Point(c.X - 5, c.Y + 8), true, false);
            }
            play.Freeze();
            dc.DrawGeometry(Brushes.White, null, play);
        }
    }

    void DrawClose(Print print, double press)
    {
        if (Math.Abs(print.DrawnClose - press) < 0.01) return;
        print.DrawnClose = press;
        var frame = CloseFrame(print.Size);
        using var dc = print.Close.RenderOpen();
        var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Min(0.85 + 0.15 * press, 1)), 31, 31, 31));
        dc.DrawEllipse(fill, null, new Point(frame.X + 11, frame.Y + 11), 11, 11);
        var pen = new Pen(Brushes.White, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, new Point(frame.X + 7, frame.Y + 7), new Point(frame.X + 15, frame.Y + 15));
        dc.DrawLine(pen, new Point(frame.X + 15, frame.Y + 7), new Point(frame.X + 7, frame.Y + 15));
    }

    Print? Find(string path) => prints.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    /// The print's picture at 2× (for the drag image, the preview's zoom and the card's flight).
    public BitmapSource? ImageOf(string path, double scale = 2)
    {
        if (Find(path) is not { } print) return null;
        return RenderPicture(print.Thumb, print.Size, print.Movie, scale);
    }

    public static BitmapSource RenderPicture(ImageSource? thumb, Size size, bool movie, double scale)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) DrawPicture(dc, thumb, size, movie);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public Size? SizeOf(string path) => Find(path)?.Size;

    /// Where a print hangs right now in view coordinates (unrotated), and its angle. Used to land the card's flight
    /// exactly on it, even while the rope is still settling.
    public (Rect Rect, double Angle)? Landing(string path)
    {
        if (Find(path) is not { Hidden: false } print) return null;
        double x = print.Slot.Value;
        return (new Rect(x - print.Size.Width / 2, RopeY(x) + 4 + print.Drop, print.Size.Width, print.Size.Height), print.Angle);
    }

    /// The print's frame while the rope is down; null once it has gone (the preview then fades instead).
    public Rect? FrameOf(string path)
    {
        if (!IsOnScreen() || IsLifting || Find(path) is not { } print) return null;
        var (x, y) = PositionOf(print);
        return new Rect(x - print.Size.Width / 2, y, print.Size.Width, print.Size.Height);
    }

    // MARK: Motion

    /// Lowers the rope. From fully hidden it falls in on its spring and the prints get a staggered nudge; caught
    /// while still lifting, it turns around from where it is and keeps its speed. Under reduced motion it fades in.
    public void Lower(bool fromHidden)
    {
        bool reduced = IsMotionReduced;
        IsLifting = false;
        onLifted = null;
        offsetTarget = 0;
        if (fromHidden)
        {
            offset = reduced ? 0 : -PanelHeight;
            offsetV = 0;
            sag = 0;
            sagV = 0;
            accumulator = 0;
            // A press whose mouse-up went elsewhere (the preview took focus) mustn't keep a print lit.
            downPath = null;
            handled = true;
            foreach (var print in prints) print.Press.Snap(0);
            LayoutPrints(snap: true);
            if (!reduced)
            {
                for (int i = 0; i < prints.Count; i++)
                {
                    prints[i].KickAt = clock + 0.12 + i * 0.045;
                    prints[i].Kick = random.Between(RopeTuning.DropKick) * (i % 2 == 0 ? 1 : -1);
                }
            }
            fade.Snap(reduced ? 0 : 1);
            breeze.Snap(reduced ? 0 : 1);  // already blowing as it comes down
        }
        else if (reduced)
        {
            offset = 0;
            offsetV = 0;
        }
        fade.Target = 1;
        Stir();
        Render(reduced);
    }

    /// Pulled back up; the prints trail a little behind. Under reduced motion it fades out where it is.
    public void Lift(Action done)
    {
        IsLifting = true;
        onLifted = done;
        if (IsMotionReduced) fade.Target = 0;
        else
        {
            offsetTarget = HiddenOffset;
            foreach (var print in prints) print.AngleV += (random.NextDouble() * 2 - 1) * RopeTuning.LiftKick;
        }
        Wake();
    }

    /// Starts the frame loop while the rope is on screen. It stops by itself once nothing moves (see `NeedsFrames`)
    /// or the rope is put away, so everything that changes what the rope shows calls this.
    public void Wake()
    {
        Stir();
        if (IsOnScreen()) driver.Start();
    }

    /// Something happened: the breeze blows for a little while, then dies down.
    void Stir() => calmAt = clock + RopeTuning.BreezeHold;

    public void Halt() => driver.Stop();

    /// The pointer moved (or the controller's poll came round). The frame loop may be resting, so wake it when the
    /// print under the pointer has changed: hover, the ring, the × and mouse catching then follow.
    public void PointerMoved()
    {
        if (!IsOnScreen() || driver.IsRunning || !PointerChangesHover) return;
        Wake();
    }

    /// Whether a frame now would hover a different print (or none).
    public bool PointerChangesHover => !string.Equals(HoverTarget(), hovered, StringComparison.OrdinalIgnoreCase);

    /// Whether anything on the rope is still moving or about to, so the frame loop must keep running. A rope left
    /// down comes to rest once the breeze has died down; under reduced motion, as soon as it has faded in.
    public bool NeedsFrames
    {
        get
        {
            bool reduced = IsMotionReduced;
            bool charging = downPath != null && !handled && !closeDown;   // a held print gathers toward the preview
            bool blowing = !reduced && (clock < calmAt || !breeze.IsSettled);
            if (IsLifting || charging || blowing || !fade.IsSettled || !highlight.IsSettled || leaving.Count > 0) return true;
            const double still = 0.01;   // pixels (and pixels per second): well below a pixel
            if (Math.Abs(offset - offsetTarget) > still || Math.Abs(offsetV) > still || Math.Abs(sag) > still || Math.Abs(sagV) > still) return true;
            return prints.Any(print =>
                print.KickAt != null || print.AwaitingSince != null || print.ChipStart != null
                || !(print.Slot.IsSettled && print.Lift.IsSettled && print.Hover.IsSettled && print.Press.IsSettled
                     && print.Opacity.IsSettled && print.Flash.IsSettled && print.ClosePress.IsSettled)
                || Math.Abs(print.Angle - Rest(print, reduced)) > 1e-4 || Math.Abs(print.AngleV) > 1e-3
                || Math.Abs(print.Drop) > still || Math.Abs(print.DropV) > still);
        }
    }

    /// The angle a print hangs at: its own little tilt, or straight while hovered. Under reduced motion it keeps its
    /// tilt, since straightening would turn it.
    double Rest(Print print, bool reduced) => IsHovered(print) && !reduced ? 0 : print.Tilt;

    bool IsHovered(Print print) => string.Equals(print.Path, hovered, StringComparison.OrdinalIgnoreCase);

    public double RopeY(double x)
    {
        double t = width > 0 ? Math.Clamp(x / width, 0, 1) : 0.5;
        return RopeTop + offset + (RestSag + sag) * 4 * t * (1 - t);
    }

    /// One display frame: physics in fixed steps, springs by the frame's measured length, then the visuals.
    /// Returns false once there is nothing left on screen to animate.
    public bool Advance(double dt)
    {
        bool reduced = IsMotionReduced;
        UpdateHover(reduced);
        if (reduced) breeze.Snap(0);
        else
        {
            breeze.Target = clock < calmAt ? 1 : 0;
            breeze.Step(dt, RopeTuning.Tuning);
        }
        accumulator = Math.Min(accumulator + dt, RopeTuning.Step * RopeTuning.MaxSteps);
        while (accumulator >= RopeTuning.Step)
        {
            Physics(RopeTuning.Step, reduced);
            accumulator -= RopeTuning.Step;
        }
        animClock += dt;
        var tuning = RopeTuning.Tuning;
        foreach (var print in prints)
        {
            print.Slot.Step(dt, tuning);
            print.Lift.Step(dt, tuning);
            print.Hover.Step(dt, tuning);
            print.Press.Step(dt, tuning);
            print.Opacity.Step(dt, tuning);
            print.Flash.Step(dt, tuning);
            print.ClosePress.Step(dt, tuning);
            // A hand-off that never comes (the flight was cut short) mustn't leave a hole in the rope.
            if (print.AwaitingSince is { } since && clock - since > 1.2) Reveal(print);
        }
        fade.Step(dt, tuning);
        highlight.Step(dt, tuning);
        Render(reduced);
        AnimateLeaving();
        UpdateMouseCatching();

        bool raised = fade.Target == 0 ? fade.Value < 0.02 : offset < -PanelHeight + 1;
        if (IsLifting && raised)
        {
            IsLifting = false;
            offset = -PanelHeight;
            offsetV = 0;
            offsetTarget = HiddenOffset;
            var done = onLifted;
            onLifted = null;
            done?.Invoke();
            return false;
        }
        // At rest the loop stops; a hover change, a press, a drop or an arrival wakes it again.
        return IsOnScreen() && NeedsFrames;
    }

    void Physics(double h, bool reduced)
    {
        clock += h;
        // Whole rope: a spring that overshoots a little and settles; the cord lags behind its acceleration, so it
        // dips once in the middle when the rope stops.
        var tuning = RopeTuning.Tuning;
        double a = RopeTuning.Advance(ref offset, ref offsetV, offsetTarget, RopeTuning.Fall.Tuned(tuning), h: h);
        RopeTuning.Advance(ref sag, ref sagV, 0, RopeTuning.Sag.Tuned(tuning), force: -RopeTuning.SagCoupling * a, h: h);
        var swing = RopeTuning.Swing.Tuned(tuning);
        var arrive = RopeTuning.Arrive.Tuned(tuning);
        double t = clock;
        foreach (var print in prints)
        {
            if (print.KickAt is { } at && clock >= at) { print.AngleV += print.Kick; print.KickAt = null; }
            double rest = Rest(print, reduced);
            if (reduced)
            {
                // Under reduced motion a print just hangs at its tilt: no swing on hover, arrival or reordering.
                print.Angle = rest;
                print.AngleV = 0;
            }
            else
            {
                // Pendulum on its peg, pulled toward its resting tilt (straight when hovered), plus a breeze.
                double wind = breeze.Value * (0.07 * Math.Sin(1.1 * t + print.Phase) + 0.03 * Math.Sin(2.7 * t + print.Phase * 1.7));
                RopeTuning.Advance(ref print.Angle, ref print.AngleV, rest, swing, force: wind, h: h);
            }
            RopeTuning.Advance(ref print.Drop, ref print.DropV, 0, arrive, h: h);
        }
    }

    /// The print hover should show: the one being pressed, or the one under the pointer.
    string? HoverTarget()
    {
        if (downPath != null && !isDraggingItem) return downPath;  // a press keeps its print lit even if the pointer slips off it
        if (IsLifting || isDraggingItem) return null;
        var pointer = SimulatedPointer ?? PointerSource();
        return pointer is { } p ? Hit(p)?.Path : null;
    }

    void UpdateHover(bool reduced)
    {
        var now = HoverTarget();
        if (!string.Equals(now, hovered, StringComparison.OrdinalIgnoreCase))
        {
            hovered = now;
            Stir();
            ToolTip = now is null ? null : System.IO.Path.GetFileName(now) + "\n" + L("Click: copy · Double-click: open · Hold: preview · Drag: take it down");
        }
        foreach (var print in prints)
        {
            bool on = IsHovered(print);
            print.Hover.Target = on ? 1 : 0;
            print.Hover.Response = on ? RopeTuning.FadeIn : RopeTuning.FadeOut;
            print.Lift.Target = on && !reduced ? -RopeTuning.HoverLift : 0;
        }
    }

    (double X, double Y) PositionOf(Print print)
    {
        double x = print.Slot.Value;
        double sink = IsMotionReduced ? 0 : RopeTuning.PressSink * print.Press.Value;
        return (x, RopeY(x) + 4 + print.Lift.Value + print.Drop + sink);
    }

    double ScaleOf(Print print, bool reduced, double charge) =>
        reduced ? 1 : 1 + RopeTuning.HoverScale * print.Hover.Value - RopeTuning.PressScale * print.Press.Value + RopeTuning.ChargeScale * charge;

    static Matrix PrintMatrix(Size size, double x, double y, double angle, double scale)
    {
        var m = Matrix.Identity;
        m.Translate(-size.Width / 2, 0);    // hangs from the top centre
        m.Scale(scale, scale);
        m.Rotate(angle * 180 / Math.PI);
        m.Translate(x, y);
        return m;
    }

    Matrix MatrixOf(Print print)
    {
        var (x, y) = PositionOf(print);
        return PrintMatrix(print.Size, x, y, print.Angle, ScaleOf(print, IsMotionReduced, ChargeOf(print)));
    }

    double ChargeOf(Print print) =>
        downPath != null && string.Equals(print.Path, downPath, StringComparison.OrdinalIgnoreCase) && !handled && !closeDown
            ? RopeTuning.Charge(downClock.Elapsed.TotalSeconds - downTime) : 0;

    void Render(bool reduced)
    {
        // The cord is rebuilt only when it has moved or changed colour, not for every print's hover. Once the
        // highlight has settled its target is drawn exactly, so leaving the rope leaves no trace of blue behind.
        double ready = highlight.IsSettled ? highlight.Target : Math.Clamp(highlight.Value, 0, 1);
        double[] shape = [width, offset, sag, ready];
        bool reachedFeedbackTarget = highlight.IsSettled && drawnRope.Length == shape.Length && drawnRope[^1] != ready;
        if (shape.Length != drawnRope.Length || reachedFeedbackTarget || shape.Zip(drawnRope).Any(p => Math.Abs(p.First - p.Second) > 0.005))
        {
            drawnRope = shape;
            var path = RopeTuning.RopePath(width, RopeTop + offset, RestSag + sag);
            // Blue, wider and with a light blue halo means this drag can be released here, whatever the idle colour.
            double lineWidth = 2.6 + 2.4 * ready;
            var color = Mix.Lerp(RopeColor, RopeDropFeedback.ReadyBlue, ready);
            var pen = new Pen(new SolidColorBrush(color), lineWidth)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            using (var dc = ropeVisual.RenderOpen()) dc.DrawGeometry(null, pen, path);
            DrawnRopeColor = color;
            DrawnRopeWidth = lineWidth;
            // The Mac's shadow radius 2 + 3 * ready (a WPF blur radius is about twice a Core Animation one).
            ropeShadow.Color = Mix.Lerp(Colors.Black, RopeDropFeedback.ReadyBlue, ready);
            ropeShadow.Opacity = 0.18 + 0.22 * ready;
            ropeShadow.BlurRadius = 2 * (2 + 3 * ready);
        }
        bool restack = false;
        foreach (var print in prints)
        {
            double p = print.Hover.Value, press = print.Press.Value;
            double charge = ChargeOf(print);
            var (x, y) = PositionOf(print);
            double scale = ScaleOf(print, reduced, charge);
            print.Root.Transform = new MatrixTransform(PrintMatrix(print.Size, x, y, print.Angle, scale));
            print.Root.Opacity = print.Hidden ? 0 : print.AwaitingSince is null ? print.Opacity.Value : 0;
            // Coming toward you on hover, pressed flat against the rope on mouse-down.
            print.Shadow.BlurRadius = 2 * Math.Max(6 + 3 * p - 4 * press, 1.5);
            print.Shadow.ShadowDepth = Math.Max(4 + 3 * p - 2.5 * press, 1);
            print.Shadow.Opacity = 0.32 + 0.06 * p - 0.08 * press;
            double ring = Math.Max(Math.Max(p, charge), print.Flash.Value);
            print.Ring.Opacity = ring;
            if (ring > 0.005) print.RingBrush.Color = Mix.Lerp(Accent, Green, print.Flash.Value);
            double shown = print.Lifted ? 0 : p;
            print.Close.Opacity = shown;
            if (shown > 0.005)
            {
                // It grows in and gives under the finger, except under reduced motion (the darker fill still shows a press).
                double s = reduced ? 1 : (0.85 + 0.15 * shown) * (1 - 0.12 * print.ClosePress.Value);
                print.CloseScale.ScaleX = s;
                print.CloseScale.ScaleY = s;
                DrawClose(print, print.ClosePress.Value);
            }
            RenderChip(print, reduced);
            // Stays on top while it settles back, so it doesn't slip behind a neighbour mid-way.
            bool onTop = p > 0.05 || press > 0.01;
            if (onTop != print.OnTop) { print.OnTop = onTop; restack = true; }
        }
        if (restack) RestackPrints();
        if (Math.Abs(Opacity - fade.Value) > 0.001) Opacity = fade.Value;
    }

    /// Visual order: the backdrop, the cord, the prints in rope order with any lit one last, then anything leaving.
    void RestackPrints()
    {
        var order = new List<Visual> { backdrop, ropeVisual };
        order.AddRange(prints.Where(p => !p.OnTop).Select(p => p.Root));
        order.AddRange(prints.Where(p => p.OnTop).Select(p => p.Root));
        order.AddRange(leaving.Select(l => l.Root));
        bool same = order.Count == children.Count && order.Select((v, i) => ReferenceEquals(children[i], v)).All(b => b);
        if (same) return;
        children.Clear();
        foreach (var v in order) children.Add(v);
    }

    /// The window was shown again: tell it afresh whether to catch the mouse.
    public void ResetMouseCatching()
    {
        catching = null;
        UpdateMouseCatching();
    }

    /// Only catch the mouse where it matters, so the strip doesn't block the windows below.
    void UpdateMouseCatching()
    {
        bool catchIt = hovered != null || downPath != null || isReceiving || isDraggingItem || receivingHighlight;
        if (catching == catchIt) return;
        catching = catchIt;
        CatchMouse?.Invoke(catchIt);
    }

    /// The card has landed: its print appears with a small swing, the rope takes the weight.
    public void HandOff(string path)
    {
        if (Find(path) is not { AwaitingSince: not null } print) return;
        Reveal(print);
        Feedback.Press();
    }

    void Reveal(Print print)
    {
        print.AwaitingSince = null;
        if (IsMotionReduced)
        {
            print.Opacity.Snap(0);
            print.Opacity.Target = 1;
        }
        else
        {
            print.AngleV = random.Between(RopeTuning.ArrivalSwingInPlace) * (random.Next(2) == 0 ? 1 : -1);
            sagV += 40;
        }
        Render(IsMotionReduced);
        Wake();
    }

    // MARK: The "Copied" chip

    const double ChipTotal = 1.17;

    /// Confirms a click-to-copy on the print itself: a "Copied" chip, the ring flashing green, a small nod.
    public void ConfirmCopy(string path)
    {
        if (Find(path) is not { } print) return;
        print.Flash.Snap(1);
        print.Flash.Target = 0;
        if (!IsMotionReduced) print.AngleV += 0.25;
        var text = L("Copied");
        DrawChip(print, text);
        print.ChipStart = animClock;
        try
        {
            if (UIElementAutomationPeer.CreatePeerForElement(this) is { } peer)
                peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "swivel.copied");
        }
        catch (Exception) { }
        Wake();
    }

    void DrawChip(Print print, string text)
    {
        double pixelsPerDip = 1;
        try { pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception) { }
        var label = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, AppTypography.Weight(FontWeights.SemiBold), FontStretches.Normal),
            AppTypography.Size(11), Brushes.White, pixelsPerDip);
        var mark = new FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            10, Brushes.White, pixelsPerDip);
        double markWidth = mark.Width + 4;
        var size = new Size(Math.Ceiling(label.Width + markWidth + 20), 20 * AppTypography.Scale);
        var origin = new Point(print.Size.Width / 2 - size.Width / 2, print.Size.Height - size.Height / 2);
        using var dc = print.Chip.RenderOpen();
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(179, 0, 0, 0)), null, new Rect(origin, size), 10, 10);
        dc.DrawText(mark, new Point(origin.X + 10, origin.Y + (size.Height - mark.Height) / 2));
        dc.DrawText(label, new Point(origin.X + 10 + markWidth, origin.Y + (size.Height - label.Height) / 2));
    }

    /// Rises into place over 0.12 s, holds, then fades out (the Mac chip's keyframes).
    void RenderChip(Print print, bool reduced)
    {
        if (print.ChipStart is not { } start) return;
        double t = animClock - start;
        double a = 0.12, b = 0.92;
        double opacity, rise;
        if (t >= ChipTotal) { opacity = 0; rise = 0; print.ChipStart = null; }
        else if (t < a) { double e = RopeTuning.EaseOut(t / a); opacity = e; rise = 4 * (1 - e); }
        else if (t < b) { opacity = 1; rise = 0; }
        else { opacity = 1 - RopeTuning.EaseIn((t - b) / (ChipTotal - b)); rise = 0; }
        print.Chip.Opacity = opacity;
        print.ChipRise.Y = reduced ? 0 : rise;
    }

    // MARK: Leaving

    /// Takes a print off the rope now, before its file has gone: it falls (× clicked) or shrinks away (already
    /// dragged to the Recycle Bin). Its neighbours close the gap, and reloads ignore it until `Departed`.
    public void Remove(string path, bool falling)
    {
        if (Find(path) is not { } print) return;
        prints.Remove(print);
        departing.Add(path);
        if (IsHovered(print)) hovered = null;
        if (!IsMotionReduced) sagV -= 20;  // the rope springs up a little as the weight leaves
        LayoutPrints();
        if (falling) LetFall(print); else Retire(print);
        Wake();
    }

    /// The file has gone, or couldn't be thrown away (then the next reload hangs it again).
    public void Departed(string path) => departing.Remove(path);

    Leaving Detach(Print print, bool fall)
    {
        var (x, y) = PositionOf(print);
        var item = new Leaving
        {
            Root = print.Root, Size = print.Size, X = x, Y = y, Angle = print.Angle,
            Scale = ScaleOf(print, IsMotionReduced, 0), Opacity = print.Root.Opacity, Start = animClock, Fall = fall, Reduced = IsMotionReduced,
        };
        print.Close.Opacity = 0;
        leaving.Add(item);
        if (!prints.Contains(print)) RestackPrints();
        return item;
    }

    /// Discarded: the print drops off the rope, turning a little, and fades on the way down.
    void LetFall(Print print)
    {
        if (!IsOnScreen() && !rendersOffscreen) { children.Remove(print.Root); return; }
        Detach(print, fall: true);
    }

    /// Gone (moved away, deleted elsewhere): the print shrinks a little and fades where it hangs.
    void Retire(Print print)
    {
        if (IsHovered(print)) hovered = null;
        prints.Remove(print);
        if ((!IsOnScreen() && !rendersOffscreen) || print.Hidden) { children.Remove(print.Root); return; }
        Detach(print, fall: false);
        Wake();
    }

    bool rendersOffscreen;
    /// Frame renders keep the leaving animations although the view isn't in a window.
    public bool RendersOffscreen { get => rendersOffscreen; set => rendersOffscreen = value; }

    void AnimateLeaving()
    {
        if (leaving.Count == 0) return;
        foreach (var item in leaving.ToList())
        {
            double t = animClock - item.Start;
            double duration = item.Fall ? (item.Reduced ? 0.2 : 0.42) : 0.18;
            double u = Math.Clamp(t / duration, 0, 1);
            double x = item.X, y = item.Y, angle = item.Angle, scale = item.Scale, opacity;
            if (item.Fall && !item.Reduced)
            {
                // Falls first, fades late, so it reads as dropping rather than dissolving.
                double e = RopeTuning.EaseIn(u);
                y += 240 * e;
                angle += (item.Angle > 0 ? 0.35 : -0.35) * e;
                opacity = u < 0.45 ? item.Opacity : item.Opacity * (1 - (u - 0.45) / 0.55);
            }
            else if (item.Fall) opacity = item.Opacity * (1 - u);
            else
            {
                double e = RopeTuning.EaseIn(u);
                opacity = item.Opacity * (1 - e);
                if (!item.Reduced) scale *= 1 - 0.08 * e;
            }
            item.Root.Transform = new MatrixTransform(PrintMatrix(item.Size, x, y, angle, scale));
            item.Root.Opacity = opacity;
            if (u >= 1)
            {
                leaving.Remove(item);
                children.Remove(item.Root);
            }
        }
    }

    // MARK: Hit testing

    /// The × badge in its print's coordinates: centred on the top-right corner, so it reaches 9 px past the edge.
    static Rect CloseFrame(Size size) => new(size.Width - 13, -9, 22, 22);

    /// Where a press counts as on the ×: the badge and a little slack around it.
    static Rect CloseTarget(Size size) { var r = CloseFrame(size); r.Inflate(3, 3); return r; }

    Point Local(Print print, Point point)
    {
        var m = MatrixOf(print);
        if (!m.HasInverse) return new Point(double.NaN, double.NaN);
        m.Invert();
        return m.Transform(point);
    }

    Print? Hit(Point point)
    {
        for (int i = prints.Count - 1; i >= 0; i--)
        {
            var print = prints[i];
            if (!print.IsTouchable) continue;
            var local = Local(print, point);
            var slack = new Rect(-4, -14, print.Size.Width + 8, print.Size.Height + 28);
            if (slack.Contains(local)) return print;
            // While the × shows, all of it belongs to its print, so the part past the corner can be pointed at and
            // clicked. A hidden × doesn't widen the print.
            if (IsHovered(print) && CloseTarget(print.Size).Contains(local)) return print;
        }
        return null;
    }

    bool HitsClose(Print print, Point point) => CloseTarget(print.Size).Contains(Local(print, point));

    public bool IsOverPrint(Point viewPoint) => Hit(viewPoint) != null;

    /// Around the prints: 60 px either side of the visible ones, from the strip's bottom up to the top of the screen.
    public bool IsNear(Point viewPoint)
    {
        if (viewPoint.Y > PanelHeight) return false;
        var visible = prints.Where(p => !p.Hidden).ToList();
        if (visible.Count == 0) return false;
        var first = visible[0];
        var last = visible[^1];
        double x = viewPoint.X;
        return x >= first.Slot.Target - first.Size.Width / 2 - 60 && x <= last.Slot.Target + last.Size.Width / 2 + 60;
    }

    // MARK: Gestures

    static readonly Stopwatch downClock = Stopwatch.StartNew();
    Point downPoint;
    string? downPath;       // gestures follow the print, not its index, so a reload can't misdirect them
    double downTime;
    int downClicks;
    bool closeDown;         // the press began on the ×
    bool handled = true;
    DispatcherTimer? longPress;
    DispatcherTimer? singleClick;

    /// Pressed prints sink quickly; released, they spring back a little more slowly. The rope gives under the finger.
    void SetPressed(Print print, bool on)
    {
        if (print.Press.Target > 0 == on) return;
        var tune = on ? RopeTuning.PressIn : RopeTuning.PressOut;
        print.Press.Response = tune.Response;
        print.Press.Damping = tune.Damping;
        print.Press.Target = on ? 1 : 0;
        if (!IsMotionReduced) sagV += on ? 5 : -3;
        Wake();
    }

    /// For tests and frame renders: shows a print pressed or released.
    public void PreviewPress(string path, bool on)
    {
        if (Find(path) is { } print) SetPressed(print, on);
    }

    static DispatcherTimer After(double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return timer;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        longPress?.Stop();
        handled = false;
        closeDown = false;
        downPoint = e.GetPosition(this);
        downTime = downClock.Elapsed.TotalSeconds;
        downClicks = e.ClickCount;
        if (Hit(downPoint) is not { } print)
        {
            downPath = null;
            handled = true;
            Wake();
            return;
        }
        var path = print.Path;
        downPath = path;
        CaptureMouse();
        if (HitsClose(print, downPoint))
        {
            // The × presses in and only discards on release over it, like any button.
            closeDown = true;
            print.ClosePress.Target = 1;
            Wake();
            return;
        }
        SetPressed(print, true);
        longPress = After(RopeTuning.ChargeEnd, () =>
        {
            if (handled || !string.Equals(downPath, path, StringComparison.OrdinalIgnoreCase)) return;
            handled = true;
            // The Mac gives a haptic detent here; Windows has no public haptics API, and this moment has no sound.
            if (Find(path) is { } held) SetPressed(held, false);
            ReleaseMouseCapture();
            Host?.Preview(path);
        });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (handled || downPath is not { } path || Find(path) is not { } print) return;
        var p = e.GetPosition(this);
        if (closeDown)
        {
            // Sliding off the × cancels, as with a button.
            print.ClosePress.Target = HitsClose(print, p) ? 1 : 0;
            Wake();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((p - downPoint).Length <= 4) return;
        handled = true;
        longPress?.Stop();
        SetPressed(print, false);
        DragOut(print, p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        longPress?.Stop();
        var path = downPath;
        bool wasClose = closeDown;
        downPath = null;
        closeDown = false;
        ReleaseMouseCapture();
        try
        {
            if (path is null || Find(path) is not { } print) return;
            if (wasClose)
            {
                print.ClosePress.Target = 0;
                if (handled || !HitsClose(print, e.GetPosition(this))) return;
                handled = true;
                Host?.Discard(path, true);   // the Mac's haptic tick only; no sound
                return;
            }
            SetPressed(print, false);
            if (handled) return;
            handled = true;
            if (downClicks >= 2)
            {
                singleClick?.Stop();
                // A quick pulse toward the viewer, so the eye catches the print before the rope leaves.
                if (!IsMotionReduced) print.Press.Velocity -= 14;
                Host?.Open(path);
            }
            else
            {
                // Released with a small pop; the copy waits only to tell a click from a double-click.
                if (!IsMotionReduced) print.Lift.Velocity -= 60;
                singleClick?.Stop();
                singleClick = After(RopeNative.GetDoubleClickTime() / 1000.0, () => Host?.Copy(path));
            }
        }
        finally { Wake(); }  // every way out changes the press, the × or what the window catches
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture taken away mid-press (another window, a menu): nothing is left lit.
        if (downPath is { } path && !isDraggingItem && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            longPress?.Stop();
            if (Find(path) is { } print) { SetPressed(print, false); print.ClosePress.Target = 0; }
            downPath = null;
            closeDown = false;
            handled = true;
            Wake();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        if (Hit(e.GetPosition(this)) is not { } print || Host is null) return;
        var path = print.Path;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, PlacementTarget = this };
        MenuItem Item(string title, string glyph, Action action)
        {
            var item = new MenuItem
            {
                Header = title,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Item(L("Copy"), "", () => Host.Copy(path));
        if (Feature.TextCapture.IsOn() && FileKinds.Of(path) == FileKind.Image)
            Item(L("Extract Text"), "", () => Host.ExtractText(path));
        Item(L("Show in File Explorer"), "", () => Host.Reveal(path));
        menu.Items.Add(new Separator());
        Item(Host.CanDiscardFile(path) ? L("Move to Recycle Bin") : L("Remove from Clothesline"), "", () => Host.Discard(path, true));
        IsDraggingItem = true;
        menu.Closed += (_, _) => IsDraggingItem = false;
        menu.IsOpen = true;
    }

    // MARK: Dragging a print out

    bool draggingOut;

    /// Into an app: a copy (the original stays). Into a folder: it comes off the rope. Onto the Recycle Bin (only
    /// files the clothesline owns may go there): discarded. Windows' drag source only learns the drop effect, so
    /// "into a folder" is told by the window under the pointer when the drop lands.
    void DragOut(Print print, Point at)
    {
        var path = print.Path;
        // Unclipped: what stays on the rope dims to a ghost, and the rope springs up as the weight leaves.
        print.Lifted = true;
        print.Opacity.Target = 0.3;
        if (!IsMotionReduced) sagV -= 25;   // (the Mac's haptic detent is not ported)
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        // Explorer copies by default (Shift still moves); the shell then never deletes the original by surprise.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1 /* DROPEFFECT_COPY */)));
        bool owned = Host?.CanDiscardFile(path) == true;
        var allowed = owned ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy;
        if (ImageOf(path) is { } picture)
        {
            // The unrotated print, held where it was grabbed.
            var local = Local(print, downPoint);
            double s = picture.PixelWidth / Math.Max(print.Size.Width, 1);
            RopeNative.SetDragImage(data, picture, new Point(local.X * s, local.Y * s));
        }
        IsDraggingItem = true;
        draggingOut = true;
        ReleaseMouseCapture();
        Wake();
        DragDropEffects result = DragDropEffects.None;
        try { result = DragDrop.DoDragDrop(this, data, allowed); }
        catch (Exception ex) { Log.Write($"Clothesline drag: {ex.Message}"); }
        finally
        {
            draggingOut = false;
            IsDraggingItem = false;
            downPath = null;
        }
        Ended(path, result);
        Wake();
    }

    void Ended(string path, DragDropEffects result)
    {
        if (Find(path) is not { } print) return;
        if (!File.Exists(path))
        {
            // Moved by the drop (Shift-drag into a folder) or recycled: the reload lets the print go.
            Host?.Reload();
            return;
        }
        if (result != DragDropEffects.None && RopeNative.IsFolderWindowAt(Native.CursorPixels()))
        {
            Host?.TakenDown(path);
            return;
        }
        // Copied into an app, or dropped nowhere: the original stays and fills back in where it hangs.
        print.Lifted = false;
        print.Opacity.Target = 1;
    }

    // MARK: Receiving drops (a screenshot card, or images from File Explorer)

    readonly RopeNative.DropImage dropImage = new();
    List<string> acceptedPaths = [];
    readonly Dictionary<string, double> aspects = new(StringComparer.OrdinalIgnoreCase);

    List<string> Acceptable(IDataObject data)
    {
        if (draggingOut) return [];
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return [];
            return files.Where(f => File.Exists(f) && Clothesline.IsMedia(f)).ToList();
        }
        catch (Exception) { return []; }
    }

    double AspectOf(string path)
    {
        if (aspects.TryGetValue(path, out var known)) return known;
        return aspects[path] = RopeThumbnails.Aspect(path);
    }

    DragDropEffects Operation(DragEventArgs e)
    {
        var paths = acceptedPaths;
        // Always a copy toward the rope (Explorer deletes the source after a move), and only when the source allows it.
        var operation = RopeDropFeedback.Operation(e.AllowedEffects, paths.Count > 0);
        SetReceivingHighlight(operation == DragDropEffects.None ? null : paths.FirstOrDefault());
        return operation;
    }

    /// Crossing onto the rope engages it: the cord turns blue and dips, and room opens at the front where the print
    /// will hang. Leaving undoes it. Internal for tests and frame renders.
    internal void SetReceivingHighlight(string? incoming)
    {
        bool on = incoming != null;
        if (on == receivingHighlight) return;
        receivingHighlight = on;
        highlight.Target = on ? 1 : 0;
        if (incoming != null)
        {
            if (!IsMotionReduced) sagV += 30;   // (the Mac's haptic detent is not ported)
            insertGap = RopeTuning.PrintSize(AspectOf(incoming)).Width + RopeTuning.Spacing;
        }
        else insertGap = 0;
        DrawBackdrop();
        LayoutPrints();
        Wake();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        aspects.Clear();
        acceptedPaths = Acceptable(e.Data);
        e.Effects = Operation(e);
        e.Handled = true;
        if (Window.GetWindow(this) is { } window) dropImage.Enter(window, e.Data, (int)e.Effects);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = Operation(e);
        e.Handled = true;
        dropImage.Over((int)e.Effects);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        dropImage.Leave();
        // A drop keeps its gap until the print arrives (SetItems closes it); a cancelled drag closes it now.
        SetReceivingHighlight(null);
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;
        var paths = acceptedPaths.Count > 0 ? acceptedPaths : Acceptable(e.Data);
        bool ready = RopeDropFeedback.Operation(e.AllowedEffects, paths.Count > 0) != DragDropEffects.None;
        dropImage.Drop(e.Data, ready ? 1 : 0);
        if (!ready) { SetReceivingHighlight(null); e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        // The highlight goes; the gap stays open for the print that's about to hang there.
        receivingHighlight = false;
        highlight.Target = 0;
        DrawBackdrop();
        if (!IsMotionReduced) sagV += 80;  // catches the weight
        Feedback.Press();
        Host?.Take(paths);
        var host = Host;
        Dispatcher.BeginInvoke(() => host?.Receive(paths));
        Wake();
    }

    // MARK: For tests and frame renders

    internal int PrintCount => prints.Count;
    internal Point PrintPosition(int i) { var (x, y) = PositionOf(prints[i]); return new Point(x, y); }
    internal double PrintScale(int i) => ScaleOf(prints[i], IsMotionReduced, ChargeOf(prints[i]));
    internal double PrintAngle(int i) => prints[i].Angle;
    internal Size PrintSizeAt(int i) => prints[i].Size;
    internal double RingOpacity(int i) => prints[i].Ring.Opacity;
    internal double CloseOpacity(int i) => prints[i].Close.Opacity;
    internal double CloseScaleAt(int i) => prints[i].CloseScale.ScaleX;
    internal Matrix PrintTransform(int i) => ((MatrixTransform)prints[i].Root.Transform).Matrix;
    internal string PathAt(int i) => prints[i].Path;
    /// A point in print i's own (unrotated) coordinates, in view coordinates.
    internal Point PrintPoint(int i, Point local) => MatrixOf(prints[i]).Transform(local);
    internal double ChipOpacity(int i) => prints[i].Chip.Opacity;
    internal Color PegColorAt(int i) => prints[i].PegColor;
    internal Color DrawnRopeColor { get; private set; }
    internal double DrawnRopeWidth { get; private set; }
    internal Color RopeShadowColor => ropeShadow.Color;
    internal double RopeShadowOpacity => ropeShadow.Opacity;
}
