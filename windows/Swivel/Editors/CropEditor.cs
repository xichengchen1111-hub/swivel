using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Swivel;

/// What a press on the crop box grabs. Coordinates are top-left origin.
enum CropHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Move }

static class CropHandles
{
    public static readonly CropHandle[] All =
        [CropHandle.TopLeft, CropHandle.Top, CropHandle.TopRight, CropHandle.Right, CropHandle.BottomRight, CropHandle.Bottom,
         CropHandle.BottomLeft, CropHandle.Left, CropHandle.Move];

    public static bool IsLeft(this CropHandle h) => h is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft;
    public static bool IsRight(this CropHandle h) => h is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight;
    public static bool IsTop(this CropHandle h) => h is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight;
    public static bool IsBottom(this CropHandle h) => h is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight;
    public static bool IsCorner(this CropHandle h) => (h.IsLeft() || h.IsRight()) && (h.IsTop() || h.IsBottom());

    /// Like CGRect.contains: the min edges are inside, the max edges are not.
    static bool Contains(Rect r, Point p) => !r.IsEmpty && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// The handle under `p` for a box drawn at `box`, or null outside it. `tolerance` reaches outward from each edge;
    /// inward it shrinks to a quarter of the box, so a small box keeps a middle you can move it by and never grabs
    /// both opposite edges at once.
    public static CropHandle? Hit(Point p, Rect box, double tolerance)
    {
        var outer = new Rect(box.X - tolerance, box.Y - tolerance, box.Width + 2 * tolerance, box.Height + 2 * tolerance);
        if (!Contains(outer, p)) return null;
        double inX = Math.Min(tolerance, box.Width / 4), inY = Math.Min(tolerance, box.Height / 4);
        bool Near(double edge, double value, bool outsideIsBelow, double inner)
        {
            bool outside = outsideIsBelow ? value < edge : value > edge;
            return Math.Abs(value - edge) < (outside ? tolerance : inner);
        }
        bool l = Near(box.Left, p.X, true, inX);
        bool r = !l && Near(box.Right, p.X, false, inX);
        bool t = Near(box.Top, p.Y, true, inY);
        bool b = !t && Near(box.Bottom, p.Y, false, inY);
        if (l && t) return CropHandle.TopLeft;
        if (r && t) return CropHandle.TopRight;
        if (l && b) return CropHandle.BottomLeft;
        if (r && b) return CropHandle.BottomRight;
        if (l) return CropHandle.Left;
        if (r) return CropHandle.Right;
        if (t) return CropHandle.Top;
        if (b) return CropHandle.Bottom;
        return Contains(box, p) ? CropHandle.Move : null;
    }
}

/// Picks the snap target a dragged edge should take: the one it already holds while still within `leave`, else the
/// nearest within `enter`. Holding on a little longer than it takes to catch is what stops a guide flickering.
static class SnapPick
{
    public readonly record struct Candidate(int Id, double Delta);  // Delta: target − current position

    public static Candidate? Pick(IEnumerable<Candidate> candidates, int? previous, double enter, double leave)
    {
        var list = candidates.ToList();
        if (previous is int held)
            foreach (var c in list)
                if (c.Id == held && Math.Abs(c.Delta) <= leave) return c;
        Candidate? best = null;
        foreach (var c in list)
            if (Math.Abs(c.Delta) <= enter && (best is null || Math.Abs(c.Delta) < Math.Abs(best.Value.Delta))) best = c;
        return best;
    }
}

/// The geometry of one crop drag, in image pixels with a top-left origin. Free of WPF input so it can be tested.
///
/// Snap ids: lines 0–4 are the image's left/top edge, first third, centre, second third and right/bottom edge (a
/// moving box adds 10 × which of its own edges is caught: 0 leading, 1 middle, 2 trailing). Ids from 100 are
/// `FreeRatios[id − 100]`.
struct CropDrag
{
    public Rect Start;
    public CropHandle Handle;
    public Size Size;
    /// Locked width / height (an aspect segment, or Shift on a corner).
    public double? Ratio;
    /// Ratios a free drag snaps to.
    public double[] FreeRatios;
    /// Snap distance in pixels (the view converts its points).
    public double Enter;
    public double MinSide;

    public CropDrag(Rect start, CropHandle handle, Size size, double enter, double? ratio = null, double[]? freeRatios = null, double minSide = 16)
    {
        Start = start;
        Handle = handle;
        Size = size;
        Ratio = ratio;
        FreeRatios = freeRatios ?? [];
        Enter = enter;
        MinSide = minSide;
    }

    public readonly record struct Snap(int? X = null, int? Y = null);

    public readonly record struct Result(Rect Rect, Snap Snap);

    public readonly double Leave => Enter * 1.5;

    public static double[] Lines(double length) => [0, length / 3, length / 2, length * 2 / 3, length];

    /// The rect for a pointer that has moved by (dx, dy) pixels since the press.
    public readonly Result Rect(double dx, double dy, bool snapping, Snap previous = default)
    {
        double W = Size.Width, H = Size.Height;
        int? snapX = null, snapY = null;
        if (Handle == CropHandle.Move)
        {
            double x = Math.Min(Math.Max(0, Start.X + dx), W - Start.Width);
            double y = Math.Min(Math.Max(0, Start.Y + dy), H - Start.Height);
            if (snapping)
            {
                if (SnapPick.Pick(MoveCandidates(x, Start.Width, Lines(W)), previous.X, Enter, Leave) is { } hitX)
                {
                    x = Math.Min(Math.Max(0, x + hitX.Delta), W - Start.Width);
                    snapX = hitX.Id;
                }
                if (SnapPick.Pick(MoveCandidates(y, Start.Height, Lines(H)), previous.Y, Enter, Leave) is { } hitY)
                {
                    y = Math.Min(Math.Max(0, y + hitY.Delta), H - Start.Height);
                    snapY = hitY.Id;
                }
            }
            return new Result(EditorGeometry.RoundedToPixels(new Rect(x, y, Start.Width, Start.Height)), new Snap(snapX, snapY));
        }
        if (Ratio is double ratio) return Locked(ratio, dx, dy, snapping, previous);

        double minX = Start.Left, maxX = Start.Right, minY = Start.Top, maxY = Start.Bottom;
        var handle = Handle;
        if (handle.IsLeft()) minX = Math.Min(Math.Max(0, minX + dx), maxX - MinSide);
        if (handle.IsRight()) maxX = Math.Max(Math.Min(W, maxX + dx), minX + MinSide);
        if (handle.IsTop()) minY = Math.Min(Math.Max(0, minY + dy), maxY - MinSide);
        if (handle.IsBottom()) maxY = Math.Max(Math.Min(H, maxY + dy), minY + MinSide);
        if (snapping)
        {
            // Horizontal edge first; a corner's vertical edge may then catch a ratio of the width it got.
            if (handle.IsLeft() || handle.IsRight())
            {
                double edge = handle.IsLeft() ? minX : maxX;
                var candidates = LineCandidates(edge, Lines(W));
                if (!(handle.IsTop() || handle.IsBottom()))
                {
                    double h = maxY - minY;
                    for (int i = 0; i < FreeRatios.Length; i++)
                        candidates.Add(new SnapPick.Candidate(100 + i, (handle.IsLeft() ? maxX - h * FreeRatios[i] : minX + h * FreeRatios[i]) - edge));
                }
                double lo = minX, hi = maxX;
                var self = this;
                if (SnapPick.Pick(candidates.Where(c => self.Valid(edge + c.Delta, true, lo, hi)), previous.X, Enter, Leave) is { } hit)
                {
                    if (handle.IsLeft()) minX += hit.Delta; else maxX += hit.Delta;
                    snapX = hit.Id;
                }
            }
            if (handle.IsTop() || handle.IsBottom())
            {
                double edge = handle.IsTop() ? minY : maxY;
                double w = maxX - minX;
                var candidates = LineCandidates(edge, Lines(H));
                for (int i = 0; i < FreeRatios.Length; i++)
                    candidates.Add(new SnapPick.Candidate(100 + i, (handle.IsTop() ? maxY - w / FreeRatios[i] : minY + w / FreeRatios[i]) - edge));
                double lo = minY, hi = maxY;
                var self = this;
                if (SnapPick.Pick(candidates.Where(c => self.Valid(edge + c.Delta, false, lo, hi)), previous.Y, Enter, Leave) is { } hit)
                {
                    if (handle.IsTop()) minY += hit.Delta; else maxY += hit.Delta;
                    snapY = hit.Id;
                }
            }
        }
        return new Result(EditorGeometry.RoundedToPixels(new Rect(minX, minY, maxX - minX, maxY - minY)), new Snap(snapX, snapY));
    }

    /// With a locked ratio: an edge drives its own dimension and the other follows, centred; a corner follows the
    /// pointer projected onto the box's diagonal, with the opposite corner pinned.
    readonly Result Locked(double ratio, double dx, double dy, bool snapping, Snap previous)
    {
        double W = Size.Width, H = Size.Height;
        var handle = Handle;
        var start = Start;
        if (handle.IsCorner())
        {
            double sx = handle.IsRight() ? 1 : -1, sy = handle.IsBottom() ? 1 : -1;
            // Growth of the height along the diagonal (width grows by ratio × this).
            double t = (sx * dx * ratio + sy * dy) / (ratio * ratio + 1);
            double room = Math.Min(handle.IsRight() ? (W - start.Right) / ratio : start.Left / ratio,
                                   handle.IsBottom() ? H - start.Bottom : start.Top);
            t = Math.Min(Math.Max(t, Math.Max((MinSide - start.Width) / ratio, MinSide - start.Height)), room);
            double w = start.Width + t * ratio, h = start.Height + t;
            double x = handle.IsRight() ? start.Left : start.Right - w;
            double y = handle.IsBottom() ? start.Top : start.Bottom - h;
            return new Result(EditorGeometry.RoundedToPixels(new Rect(x, y, Math.Max(0, w), Math.Max(0, h))), new Snap());
        }
        if (handle.IsLeft() || handle.IsRight())
        {
            int? snapX = null;
            double edge = (handle.IsLeft() ? start.Left : start.Right) + dx;
            if (snapping && SnapPick.Pick(LineCandidates(edge, Lines(W)), previous.X, Enter, Leave) is { } hit)
            {
                edge += hit.Delta;
                snapX = hit.Id;
            }
            double maxW = Math.Min(handle.IsLeft() ? start.Right : W - start.Left, H * ratio);
            double w = Math.Min(Math.Max(handle.IsLeft() ? start.Right - edge : edge - start.Left, Math.Max(MinSide, MinSide * ratio)), maxW);
            double h = w / ratio;
            double x = handle.IsLeft() ? start.Right - w : start.Left;
            double y = Math.Min(Math.Max(0, (start.Top + start.Bottom) / 2 - h / 2), H - h);
            return new Result(EditorGeometry.RoundedToPixels(new Rect(x, y, w, h)), new Snap(snapX, null));
        }
        {
            int? snapY = null;
            double edge = (handle.IsTop() ? start.Top : start.Bottom) + dy;
            if (snapping && SnapPick.Pick(LineCandidates(edge, Lines(H)), previous.Y, Enter, Leave) is { } hit)
            {
                edge += hit.Delta;
                snapY = hit.Id;
            }
            double maxH = Math.Min(handle.IsTop() ? start.Bottom : H - start.Top, W / ratio);
            double h = Math.Min(Math.Max(handle.IsTop() ? start.Bottom - edge : edge - start.Top, Math.Max(MinSide, MinSide / ratio)), maxH);
            double w = h * ratio;
            double y = handle.IsTop() ? start.Bottom - h : start.Top;
            double x = Math.Min(Math.Max(0, (start.Left + start.Right) / 2 - w / 2), W - w);
            return new Result(EditorGeometry.RoundedToPixels(new Rect(x, y, w, h)), new Snap(null, snapY));
        }
    }

    /// The largest rect of `ratio` inside `rect`, for a press that takes over a glide toward a locked ratio (the box
    /// drawn mid-glide has neither ratio). It keeps the grabbed edges where they are, so they stay under the pointer,
    /// and centres the rest; it stays inside the image and at least `minSide` on each side.
    public static Rect Fitted(Rect rect, double ratio, CropHandle handle, Size size, double minSide = 16)
    {
        double w = rect.Width, h = rect.Height;
        if (w / h > ratio) w = h * ratio; else h = w / ratio;
        double grow = Math.Max(1, minSide / Math.Min(w, h));
        w *= grow;
        h *= grow;
        double shrink = Math.Min(1, Math.Min(size.Width / w, size.Height / h));
        w *= shrink;
        h *= shrink;
        double x = handle.IsLeft() ? rect.Left : handle.IsRight() ? rect.Right - w : (rect.Left + rect.Right) / 2 - w / 2;
        double y = handle.IsTop() ? rect.Top : handle.IsBottom() ? rect.Bottom - h : (rect.Top + rect.Bottom) / 2 - h / 2;
        x = Math.Min(Math.Max(0, x), size.Width - w);
        y = Math.Min(Math.Max(0, y), size.Height - h);
        return EditorGeometry.RoundedToPixels(new Rect(x, y, w, h));
    }

    static List<SnapPick.Candidate> LineCandidates(double edge, double[] lines) =>
        lines.Select((line, i) => new SnapPick.Candidate(i, line - edge)).ToList();

    /// A moving box: its leading and trailing edges catch the image edges and thirds, its middle the centre.
    static List<SnapPick.Candidate> MoveCandidates(double origin, double length, double[] lines)
    {
        var list = new List<SnapPick.Candidate> { new(12, lines[2] - (origin + length / 2)) };
        foreach (var i in new[] { 0, 1, 3, 4 })
        {
            list.Add(new(i, lines[i] - origin));
            list.Add(new(20 + i, lines[i] - (origin + length)));
        }
        return list;
    }

    readonly bool Valid(double edge, bool horizontal, double low, double high)
    {
        double limit = horizontal ? Size.Width : Size.Height;
        if (edge < 0 || edge > limit) return false;
        bool leading = horizontal ? Handle.IsLeft() : Handle.IsTop();
        return leading ? edge <= high - MinSide : edge >= low + MinSide;
    }
}

/// Draggable crop box over an image. `Crop` is in pixels of the full image with a top-left origin; what's drawn is a
/// smaller proxy of it.
///
/// Handles show resize cursors and grow under the pointer; the grabbed one turns white. While dragging, the thirds grid
/// fades in and edges snap to the image's edges, thirds and centre lines (in Free mode also to the original and square
/// ratios), with the snap sound as each snap catches and a guide line while it holds. Ctrl turns snapping off (⌘ on the
/// Mac); Shift on a corner keeps the ratio. Aspect, size and reset changes glide on a spring.
sealed class CropView : PictureCanvas
{
    public Size PixelSize { get; private set; } = new(1, 1);
    public Rect Crop { get; private set; } = new(0, 0, 1, 1);
    public double? Ratio { get; private set; }
    public event Action? OnChange;

    readonly FrameDriver driver = new();
    // Motion: the drawn box (minX, minY, maxX, maxY), per-handle growth, the grabbed handle whitening, the grid and the
    // snap guides.
    readonly Spring[] shown = [new(0, 0.3, 0.9), new(0, 0.3, 0.9), new(0, 0.3, 0.9), new(0, 0.3, 0.9)];
    readonly Dictionary<CropHandle, Spring> growth = CropHandles.All.ToDictionary(h => h, _ => new Spring(0, 0.2, 0.7));
    Spring press = new(0, 0.1, 1);
    Spring grid = new(0, 0.14, 1);
    Spring guideX = new(0, 0.1, 1);
    Spring guideY = new(0, 0.1, 1);
    Spring pill = new(0, 0.12, 1);
    double guideXAt, guideYAt;
    string pillText = "";

    CropHandle? hover;
    CropHandle? grabbed;
    CropDrag? drag;
    CropDrag.Snap snap;
    Point startPoint;

    const double Tolerance = 12;
    const double SnapPoints = 6;

    public CropView()
    {
        driver.OnFrame = Advance;
    }

    protected override double Inset => 14;

    protected override Size PictureSize => PixelSize;

    public void SetImage(BitmapSource image, Size pixelSize)
    {
        PixelSize = pixelSize;
        Picture = image;
        SetCrop(new Rect(new Point(0, 0), pixelSize), animated: false);
    }

    /// View points per image pixel.
    double Scale => Math.Max(ImageRect.IsEmpty ? 0 : ImageRect.Width / PixelSize.Width, 0.0001);

    Rect ShownCrop => new(shown[0].Value, shown[1].Value, Math.Max(0, shown[2].Value - shown[0].Value), Math.Max(0, shown[3].Value - shown[1].Value));

    Rect ViewRect(Rect r)
    {
        var i = ImageRect;
        if (i.IsEmpty) return Rect.Empty;
        double s = Scale;
        return new Rect(i.X + r.X * s, i.Y + r.Y * s, r.Width * s, r.Height * s);
    }

    // MARK: Changes from the controls

    public void SetRatio(double? ratio)
    {
        Ratio = ratio;
        if (ratio is not double r) return;
        double W = PixelSize.Width, H = PixelSize.Height;
        var size = W / H > r ? new Size(H * r, H) : new Size(W, W / r);
        SetCrop(EditorGeometry.RoundedToPixels(new Rect((W - size.Width) / 2, (H - size.Height) / 2, size.Width, size.Height)), animated: true);
    }

    public void SetSize(double width, double height)
    {
        double W = PixelSize.Width, H = PixelSize.Height;
        double w = Math.Min(Math.Max(width, 16), W), h = Math.Min(Math.Max(height, 16), H);
        double cx = Crop.X + Crop.Width / 2, cy = Crop.Y + Crop.Height / 2;
        double x = Math.Min(Math.Max(0, cx - w / 2), W - w);
        double y = Math.Min(Math.Max(0, cy - h / 2), H - h);
        SetCrop(EditorGeometry.RoundedToPixels(new Rect(x, y, w, h)), animated: true);
    }

    public void Reset()
    {
        Ratio = null;
        SetCrop(new Rect(new Point(0, 0), PixelSize), animated: true);
    }

    /// `Crop` takes the new value at once (fields and Apply read it); the drawing glides there unless told not to.
    void SetCrop(Rect rect, bool animated)
    {
        Crop = rect;
        double[] edges = [rect.Left, rect.Top, rect.Right, rect.Bottom];
        for (int i = 0; i < 4; i++)
        {
            if (animated) shown[i].Target = edges[i];
            else shown[i].Snap(edges[i]);
        }
        Kick();
    }

    // MARK: Motion

    bool OnScreen => PresentationSource.FromVisual(this) != null;

    void Kick()
    {
        if (!OnScreen || Motion.Reduced)
        {
            // No movement: everything lands at once, but fades still read as fades on the next frames.
            for (int i = 0; i < 4; i++) shown[i].Snap(shown[i].Target);
            foreach (var key in CropHandles.All) { var s = growth[key]; s.Snap(0); growth[key] = s; }
        }
        RedrawOverlay();
        if (OnScreen) driver.Start();
    }

    bool Advance(double dt)
    {
        for (int i = 0; i < 4; i++) shown[i].Step(dt);
        foreach (var key in CropHandles.All) { var s = growth[key]; s.Step(dt); growth[key] = s; }
        press.Step(dt);
        grid.Step(dt);
        guideX.Step(dt);
        guideY.Step(dt);
        pill.Step(dt);
        RedrawOverlay();
        return !(shown.All(s => s.IsSettled) && growth.Values.All(s => s.IsSettled)
                 && press.IsSettled && grid.IsSettled && guideX.IsSettled && guideY.IsSettled && pill.IsSettled);
    }

    void SetHover(CropHandle? handle)
    {
        hover = handle;
        RetargetHandles();
    }

    void RetargetHandles()
    {
        var active = grabbed ?? hover;
        foreach (var key in CropHandles.All)
        {
            var s = growth[key];
            s.Target = key == active && key != CropHandle.Move && !Motion.Reduced ? 1 : 0;
            growth[key] = s;
        }
        press.Target = grabbed is null or CropHandle.Move ? 0 : 1;
        // Exits are a little quicker than entrances.
        grid.Response = grabbed is null ? 0.11 : 0.14;
        grid.Target = grabbed is null ? 0 : 0.35;
        Kick();
    }

    // MARK: Pointer

    static Cursor CursorFor(CropHandle? handle) => handle switch
    {
        null => Cursors.Arrow,
        // Windows has no open/closed-hand cursors; the move cursor says the same.
        CropHandle.Move => Cursors.SizeAll,
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    CropHandle? HandleAt(Point p) => Picture is null ? null : CropHandles.Hit(p, ViewRect(ShownCrop), Tolerance);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (grabbed != null)
        {
            DragTo(p, Keyboard.Modifiers);
            return;
        }
        var handle = HandleAt(p);
        if (handle != hover) SetHover(handle);
        Cursor = CursorFor(handle);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (grabbed != null) return;
        SetHover(null);
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (PressAt(e.GetPosition(this)))
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (grabbed is null) return;
        ReleaseMouseCapture();
        Release(e.GetPosition(this));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (grabbed != null) Release(Mouse.GetPosition(this));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Pressing or releasing Shift / Ctrl mid-drag reshapes at once.
        if (grabbed != null && e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) DragTo(Mouse.GetPosition(this), Keyboard.Modifiers);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (grabbed != null && e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) DragTo(Mouse.GetPosition(this), Keyboard.Modifiers);
    }

    /// A press at a point in this view; false when it grabbed nothing.
    public bool PressAt(Point p)
    {
        if (HandleAt(p) is not CropHandle handle) return false;
        // Take over from a running glide where the box is drawn now, so the drag starts 1:1 under the pointer. With a
        // ratio locked, the in-between box is first fitted to it: no drag restores a ratio it didn't start with, and
        // the crop would be saved off-ratio with the ratio still selected.
        var current = EditorGeometry.RoundedToPixels(ShownCrop);
        if (current != Crop)
        {
            if (Ratio is double ratio) current = CropDrag.Fitted(current, ratio, handle, PixelSize);
            SetCrop(current, animated: false);
            OnChange?.Invoke();
        }
        grabbed = handle;
        startPoint = p;
        snap = default;
        drag = new CropDrag(Crop, handle, PixelSize, SnapPoints / Scale, Ratio, [PixelSize.Width / PixelSize.Height, 1]);
        RetargetHandles();
        Cursor = CursorFor(handle);
        return true;
    }

    public void DragTo(Point p, ModifierKeys modifiers)
    {
        if (drag is not CropDrag d) return;
        if (Ratio is null)
            d.Ratio = modifiers.HasFlag(ModifierKeys.Shift) && d.Handle.IsCorner() ? d.Start.Width / d.Start.Height : null;
        var result = d.Rect((p.X - startPoint.X) / Scale, (p.Y - startPoint.Y) / Scale, snapping: !modifiers.HasFlag(ModifierKeys.Control), snap);
        drag = d;
        Caught(result.Snap);
        SetCrop(result.Rect, animated: false);
        OnChange?.Invoke();
    }

    public void Release(Point p)
    {
        if (grabbed is null) return;
        grabbed = null;
        drag = null;
        Caught(default);
        hover = HandleAt(p);
        RetargetHandles();
        Cursor = CursorFor(hover);
    }

    /// Snaps once as each snap catches (not while it holds, never on letting go) and shows its guide while it holds.
    void Caught(CropDrag.Snap next)
    {
        if ((next.X != null && next.X != snap.X) || (next.Y != null && next.Y != snap.Y)) Feedback.Snap();
        snap = next;
        double[] lines = [0, 1.0 / 3, 0.5, 2.0 / 3, 1];
        // Guides only for lines inside the image; at its edges the box itself shows the catch.
        if (next.X is int x && x < 100 && x % 10 is 1 or 2 or 3)
        {
            guideXAt = lines[x % 10] * PixelSize.Width;
            guideX.Target = 1;
        }
        else guideX.Target = 0;
        if (next.Y is int y && y < 100 && y % 10 is 1 or 2 or 3)
        {
            guideYAt = lines[y % 10] * PixelSize.Height;
            guideY.Target = 1;
        }
        else guideY.Target = 0;
        int? ratioId = next.X >= 100 ? next.X : next.Y >= 100 ? next.Y : null;
        if (ratioId is int id)
        {
            pillText = id == 100 ? L("Original ratio") : "1:1";
            pill.Target = 1;
        }
        else pill.Target = 0;
        Kick();
    }

    // MARK: Drawing

    protected override void DrawOverlay(DrawingContext dc)
    {
        if (Picture is null || ImageRect.IsEmpty) return;
        var rect = ImageRect;
        var box = ViewRect(ShownCrop);
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(rect));
        dim.Children.Add(new RectangleGeometry(box));
        dc.DrawGeometry(Brush(Color.FromArgb(128, 0, 0, 0)), null, dim);

        // Rule-of-thirds guides, only while the box is held (as in Photos).
        if (grid.Value > 0.003)
        {
            var pen = Pen(WithAlpha(Colors.White, Math.Min(grid.Value, 0.35)), 1);
            for (int i = 1; i <= 2; i++)
            {
                double x = box.X + box.Width * i / 3, y = box.Y + box.Height * i / 3;
                dc.DrawLine(pen, new Point(x, box.Top), new Point(x, box.Bottom));
                dc.DrawLine(pen, new Point(box.Left, y), new Point(box.Right, y));
            }
        }

        var accent = Accent;
        dc.DrawRectangle(null, Pen(accent, 2), box);

        // Snap guides run across the whole image, over the border, so a caught edge visibly lights up. Yellow, as in
        // Keynote: it stands apart from the accent border and reads on most photos.
        var yellow = Color.FromRgb(0xFF, 0xCC, 0x00);
        if (guideX.Value > 0.003)
        {
            double x = Math.Round(rect.X + guideXAt * Scale);
            dc.DrawRectangle(Brush(WithAlpha(yellow, Math.Min(guideX.Value, 1))), null, new Rect(x - 0.5, rect.Y, 1, rect.Height));
        }
        if (guideY.Value > 0.003)
        {
            double y = Math.Round(rect.Y + guideYAt * Scale);
            dc.DrawRectangle(Brush(WithAlpha(yellow, Math.Min(guideY.Value, 1))), null, new Rect(rect.X, y - 0.5, rect.Width, 1));
        }

        foreach (var handle in CropHandles.All)
        {
            if (handle == CropHandle.Move) continue;
            double grow = growth[handle].Value;
            var center = new Point(handle.IsLeft() ? box.Left : handle.IsRight() ? box.Right : box.X + box.Width / 2,
                                   handle.IsTop() ? box.Top : handle.IsBottom() ? box.Bottom : box.Y + box.Height / 2);
            Size size;
            if (handle.IsCorner())
            {
                double s = 10 + 4 * grow;
                size = new Size(s, s);
            }
            else
            {
                double longSide = 20 + 6 * grow, shortSide = 5 + 2 * grow;
                size = handle.IsTop() || handle.IsBottom() ? new Size(longSide, shortSide) : new Size(shortSide, longSide);
            }
            var frame = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
            // The grabbed handle turns white with an accent ring: pressed, under the finger.
            double pressed = handle == grabbed ? press.Value : 0;
            var fill = Brush(Mix.Lerp(accent, Colors.White, pressed));
            var ring = pressed > 0.01 ? Pen(WithAlpha(accent, pressed), 1.5) : null;
            double radius = handle.IsCorner() ? 0 : 2;
            dc.DrawRoundedRectangle(fill, ring, frame, radius, radius);
        }

        if (pill.Value > 0.01 && pillText.Length > 0)
        {
            var text = new FormattedText(pillText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11,
                Brush(WithAlpha(Colors.White, pill.Value)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var frame = new Rect(box.X + box.Width / 2 - text.Width / 2 - 8, box.Top + 10, text.Width + 16, text.Height + 6);
            frame.Y = Math.Min(frame.Y, box.Bottom - frame.Height - 6);
            dc.DrawRoundedRectangle(Brush(WithAlpha(accent, 0.9 * pill.Value)), null, frame, frame.Height / 2, frame.Height / 2);
            dc.DrawText(text, new Point(frame.X + 8, frame.Y + 3));
        }
    }

    public void Stop() => driver.Stop();
}

/// Reads an image file's header without decoding its pixels.
static class ImageHeader
{
    /// The decoded size of the full image (EXIF rotation applied), or null when the header can't be read.
    public static Size? UprightSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            return Orientation(frame) >= 5 ? new Size(h, w) : new Size(w, h);
        }
        catch (Exception) { return null; }
    }

    /// The EXIF orientation (1–8), 1 when there is none.
    public static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is { } value)
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception) { /* No metadata in this format. */ }
        return 1;
    }
}

/// Crops an image, or every page of a PDF by the same proportion.
sealed class CropEditor : Editor
{
    readonly CropView view;
    static readonly (string Title, double? Ratio)[] ratios =
        [("Free", null), ("1:1", 1), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:3", 4.0 / 3), ("3:4", 3.0 / 4)];
    readonly Segments aspect;
    readonly NumberField widthField, heightField;
    int lastAspect;
    readonly bool isPdf;
    /// Output units per image pixel (PDF crops are shown in points; the preview is rendered at 144 dpi).
    readonly double unit;
    /// The longest side of the image drawn on screen; the full image is decoded only to apply.
    const int ProxyPixels = 2400;

    public CropEditor(string path) : this(path, new CropView()) { }

    CropEditor(string path, CropView view) : base("Crop Image", path, view)
    {
        this.view = view;
        isPdf = FileKinds.Of(path) == FileKind.Pdf;
        unit = isPdf ? 0.5 : 1;
        if (isPdf) SetTitle("Crop PDF");

        aspect = Segments(ratios.Select(r => r.Title).ToList());
        AddRow("Aspect ratio", aspect);
        widthField = NumberField(0);
        heightField = NumberField(0);
        foreach (var field in new[] { widthField, heightField }) field.Committed += SizeEdited;
        var units = Label(isPdf ? "pt" : "px");
        AddRow("Size", Label(L("W"), secondary: false), widthField, Label("×", secondary: false), Label(L("H"), secondary: false), heightField, units);
        view.OnChange += SyncFields;
    }

    public CropView View => view;

    protected override async Task LoadAsync()
    {
        var source = Source;
        if (isPdf)
        {
            int pages = await Task.Run(() => Pdfs.PageCount(source));
            if (pages < 1) throw new ToolError(L("This PDF has no pages"));
            var page = await Pdfs.RenderPage(source, 0, 144);
            view.SetImage(page, new Size(page.PixelWidth, page.PixelHeight));
            return;
        }
        var (image, size) = await Task.Run(() =>
        {
            if (ImageHeader.UprightSize(source) is not Size header)
            {
                var full = Images.Load(source);
                return (full, new Size(full.PixelWidth, full.PixelHeight));
            }
            return (Images.Load(source, ProxyPixels), header);
        });
        view.SetImage(image, size);
    }

    void SyncFields()
    {
        widthField.Value = (int)Math.Round(view.Crop.Width * unit, MidpointRounding.AwayFromZero);
        heightField.Value = (int)Math.Round(view.Crop.Height * unit, MidpointRounding.AwayFromZero);
    }

    protected override void Update()
    {
        if (aspect.SelectedIndex != lastAspect)
        {
            lastAspect = aspect.SelectedIndex;
            view.SetRatio(ratios[lastAspect].Ratio);
        }
        SyncFields();
    }

    void SizeEdited()
    {
        if (!IsLoaded) return;
        view.SetSize(widthField.Value / unit, heightField.Value / unit);
        SyncFields();
    }

    protected override void Reset()
    {
        view.Reset();
        aspect.SelectedIndex = 0;
        lastAspect = 0;
    }

    public override void DidClose() => view.Stop();

    protected override Task<List<string>> ApplyAsync()
    {
        var crop = view.Crop;
        var size = view.PixelSize;
        var source = Source;
        if (isPdf)
        {
            return Task.Run(() =>
            {
                // The fraction of the page as displayed (top-left origin); the PDF side maps it through each page's
                // rotation onto its own box (see `PageRect`).
                var fraction = new Rect(crop.X / size.Width, crop.Y / size.Height, crop.Width / size.Width, crop.Height / size.Height);
                var output = Pdfs.CropPages(source, fraction, Paths.Output(source, "pdf", " cropped"));
                return new List<string> { output };
            });
        }
        return Task.Run(() =>
        {
            var full = Images.Load(source);
            // The crop was measured against the header's size; scale in case the decoder returned another.
            double sx = full.PixelWidth / size.Width, sy = full.PixelHeight / size.Height;
            var scaled = EditorGeometry.RoundedToPixels(new Rect(crop.X * sx, crop.Y * sy, crop.Width * sx, crop.Height * sy));
            scaled.Intersect(new Rect(0, 0, full.PixelWidth, full.PixelHeight));
            if (scaled.IsEmpty || scaled.Width < 1 || scaled.Height < 1) throw new ToolError(L("Couldn't crop"));
            var cropped = new CroppedBitmap(full, new Int32Rect((int)scaled.X, (int)scaled.Y, (int)scaled.Width, (int)scaled.Height));
            cropped.Freeze();
            var ext = Images.WritableExt(source);
            var output = Paths.Output(source, ext, " cropped");
            Images.Write(cropped, output, ext, 0.95);
            return new List<string> { output };
        });
    }

    /// A crop given as a fraction of the page as displayed (top-left origin, after the page's clockwise `rotation`),
    /// in the page's own coordinates (bottom-left origin, unrotated), like PDF boxes.
    public static Rect PageRect(Rect unit, int rotation, Rect box)
    {
        // (s, t): fractions across and down the unrotated page.
        double s0, s1, t0, t1;
        switch (((rotation % 360) + 360) % 360)
        {
            case 90: (s0, s1, t0, t1) = (unit.Top, unit.Bottom, 1 - unit.Right, 1 - unit.Left); break;
            case 180: (s0, s1, t0, t1) = (1 - unit.Right, 1 - unit.Left, 1 - unit.Bottom, 1 - unit.Top); break;
            case 270: (s0, s1, t0, t1) = (1 - unit.Bottom, 1 - unit.Top, unit.Left, unit.Right); break;
            default: (s0, s1, t0, t1) = (unit.Left, unit.Right, unit.Top, unit.Bottom); break;
        }
        return new Rect(box.X + s0 * box.Width, box.Y + box.Height - t1 * box.Height, (s1 - s0) * box.Width, (t1 - t0) * box.Height);
    }
}

/// Re-encodes an image with a chosen quality, size cap and format, showing the resulting size and look live.
sealed class CompressEditor : Editor
{
    readonly record struct Settings(double Quality, int MaxPixels, int Format);

    sealed record Estimate(Settings Settings, byte[] Data, BitmapSource? Preview);

    readonly ImagePreview preview;
    readonly long originalBytes;
    static readonly int[] sizes = [0, 3840, 2560, 1920, 1280, 800];
    readonly List<(string Title, string Ext)> formats = [];
    /// Decoded source per size cap, so dragging the quality only re-encodes.
    public DecodedSources Decoded { get; } = new();
    Estimate? latest;
    readonly DetentSlider quality;
    readonly TextBlock qualityLabel;
    readonly ComboBox maxSize, format;
    readonly TextBlock estimate = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer calculating;
    readonly LiveRender<Estimate?> estimator;

    /// An estimate is being worked out or waits its turn.
    public bool IsEstimating => estimator.IsBusy;

    public CompressEditor(string path) : this(path, new ImagePreview()) { }

    CompressEditor(string path, ImagePreview preview) : base("Compress Image", path, preview)
    {
        this.preview = preview;
        estimator = new LiveRender<Estimate?>(Show);
        try { originalBytes = new FileInfo(path).Length; } catch (IOException) { }

        string sourceExt;
        try { sourceExt = Images.WritableExt(path); } catch (Exception) { sourceExt = "png"; }
        if (sourceExt is "jpg" or "jpeg" or "heic" or "heif" or "webp" or "avif")
            formats.Add((L("Same (%@)", sourceExt.ToUpperInvariant()), sourceExt));
        formats.Add(("JPG", "jpg"));
        // HEIC needs the HEIF Image Extensions from the Store; offered only when this PC can write it.
        bool heic;
        try { heic = Images.OutputFormats.Any(f => f.Ext == "heic"); } catch (Exception) { heic = false; }
        if (heic) formats.Add(("HEIC", "heic"));
        formats.Add(("PNG", "png"));

        quality = Slider(0.1, 1, 0.7);
        qualityLabel = new TextBlock { Text = "70%", Width = 40, VerticalAlignment = VerticalAlignment.Center };
        qualityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AddRow("Quality", quality, qualityLabel);
        maxSize = Popup(sizes.Select(s => s == 0 ? L("Original size") : L("Max %d px", s)));
        AddRow("Dimensions", maxSize);
        format = Popup(formats.Select(f => f.Title));
        AddRow("Format", format);
        AddRow("Result", estimate);

        calculating = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.4) };
        calculating.Tick += (_, _) =>
        {
            calculating.Stop();
            estimate.Text = L("Calculating…");
        };
    }

    protected override async Task LoadAsync()
    {
        var source = Source;
        var image = await Task.Run(() => Images.Load(source, 1400));
        preview.Image = image;
        preview.Original = image;
    }

    Settings Current => new(quality.Value, sizes[Math.Max(0, maxSize.SelectedIndex)], Math.Max(0, format.SelectedIndex));

    protected override void Update()
    {
        qualityLabel.Text = $"{(int)Math.Round(quality.Value * 100, MidpointRounding.AwayFromZero)}%";
        var settings = Current;
        var ext = formats[settings.Format].Ext;
        var decoded = Decoded;
        var source = Source;
        // Keep the last result visible, dimmed, while the new one is worked out; only a slow encode says so in words.
        estimate.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        calculating.Stop();
        calculating.Start();
        // The source is looked up when the job runs, not now: a request queued behind the first (full-size) decode then
        // reuses that decode instead of starting another.
        estimator.Request(() =>
        {
            var image = decoded.Image(source, settings.MaxPixels);
            var data = Images.Encode(image, ext, settings.Quality);
            return new Estimate(settings, data, DecodePreview(data, 1400));
        });
    }

    /// The encoded result decoded back at preview size, so the quality change shows in the picture.
    static BitmapSource? DecodePreview(byte[] data, int maxPixels)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var image = Pixels.Downscaled(frame, maxPixels);
            if (!image.IsFrozen) image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    void Show(Estimate? result)
    {
        if (result is null) return;
        latest = result;
        bool current = result.Settings == Current;
        if (current) calculating.Stop();
        long bytes = result.Data.LongLength;
        int change = originalBytes > 0 ? (int)((1 - (double)bytes / originalBytes) * 100) : 0;
        estimate.Text = $"{EditorFormat.Bytes(originalBytes)} → {EditorFormat.Bytes(bytes)}" + (change > 0 ? $"  (−{change}%)" : $"  (+{-change}%)");
        estimate.SetResourceReference(TextBlock.ForegroundProperty,
            !current ? "TextFillColorTertiaryBrush" : change > 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        if (result.Preview is { } image) preview.Image = image;
    }

    /// The text of the size estimate (for tests).
    public string EstimateText => estimate.Text;

    protected override void Reset()
    {
        quality.Value = 0.7;
        maxSize.SelectedIndex = 0;
        format.SelectedIndex = 0;
    }

    public override void DidClose()
    {
        estimator.Cancel();
        calculating.Stop();
        preview.Stop();
    }

    protected override Task<List<string>> ApplyAsync()
    {
        var settings = Current;
        var ext = formats[settings.Format].Ext;
        var source = Source;
        // The estimate already encoded exactly this; write those bytes rather than encode again.
        var ready = latest?.Settings == settings ? latest.Data : null;
        var decoded = Decoded;
        return Task.Run(() =>
        {
            var data = ready ?? Images.Encode(decoded.Image(source, settings.MaxPixels), ext, settings.Quality);
            var output = Paths.Output(source, ext, " compressed");
            Pixels.WriteAtomically(output, data);
            return new List<string> { output };
        });
    }
}
