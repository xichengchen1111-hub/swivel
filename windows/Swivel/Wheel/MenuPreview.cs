using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Swivel;

/// Live menu previews for the settings window's Style and Customize pages.
static class MenuPreview
{
    /// A menu drawn as it would open, with these item titles (the first eight; more show a second page dot), on a
    /// soft desktop-like backdrop. Interactive previews follow the pointer like the real menu while they are on screen,
    /// press an item on click (with the drop's sound) and then open again; nothing runs. `wallpaper: false` leaves the
    /// background clear for a caller that draws its own.
    ///
    /// The preview moves with the saved pop-up menu tuning (`Prefs.Interaction.Menu`, read live) unless `tuning` is
    /// given: the Feel & Text page passes the tuning being edited, and can change it later through
    /// `PreviewHost.Tuning` and show it with `PreviewHost.Replay()`. Palettes and text size are read live and redraw
    /// on `Prefs.Changed`.
    public static FrameworkElement Create(MenuStyle style, IReadOnlyList<string> titles, WheelMode mode, int? hovered = null,
                                          bool interactive = true, bool wallpaper = true, MotionTuning? tuning = null) =>
        new PreviewHost(Draw(style, titles, mode, hovered)) { IsInteractive = interactive, DrawsWallpaper = wallpaper, Tuning = tuning };

    /// The menu itself at its natural size, settled (the Mac's `menuPreview`).
    internal static WheelView Draw(MenuStyle style, IReadOnlyList<string> titles, WheelMode mode, int? hovered = null)
    {
        var shown = titles.Take(WheelController.PerPage).ToList();
        var view = new WheelView { ViewSize = WheelView.MenuSize(style, shown.Count), MenuStyle = style, Subtitle = "photo.jpg" };
        view.Configure(shown.Select(t => new WheelItem(t, _ => Task.FromResult(new ActionResult("")))).ToList(), mode, 0,
            titles.Count > WheelController.PerPage ? 2 : 1);
        view.Hovered = hovered;
        view.ResetVisit();  // the resting hover is only a picture; the user hasn't visited anything yet
        return view;
    }

    /// The colours of the desktop-like backdrop behind previews and rendered frames.
    internal static LinearGradientBrush WallpaperBrush(Rect rect, bool three = true) =>
        Painter.Gradient(rect, -60, three
            ? [Look.Rgb(0.47, 0.66, 0.9), Look.Rgb(0.72, 0.78, 0.95), Look.Rgb(0.98, 0.8, 0.62)]
            : [Look.Rgb(0.47, 0.66, 0.9), Look.Rgb(0.98, 0.8, 0.62)]);
}

/// Hosts a preview, scaling it to fit while keeping its proportions.
///
/// An interactive host lets the menu be tried with the mouse while it is on screen: moving over it hovers items the
/// way a drag does, pressing an item presses it in with the drop's sound, and the menu then opens again. Off screen
/// the preview is not live (no frame driver, no sound) and goes back to its resting picture.
sealed class PreviewHost : FrameworkElement
{
    public WheelView Preview { get; }
    readonly Size natural;
    /// What the preview showed hovered before anyone touched it.
    readonly int? restingHover;
    /// Bumped by every press and by anything that resets the menu, so a pending re-open can't act on a newer state.
    int trial;
    /// The current click already pressed an item (on mouse-down), so its release doesn't press again.
    bool pressedThisClick;
    bool interactive = true;

    /// How long a pressed item takes to clear before the menu opens again.
    public const double ReopenDelay = 0.6;

    public bool DrawsWallpaper { get; set; } = true;

    /// The motion the preview uses instead of the saved menu tuning (null: follow `Prefs.Interaction.Menu`). Takes
    /// effect from the next frame; call `Replay` to show it from the start.
    public MotionTuning? Tuning
    {
        get => Preview.TuningOverride;
        set => Preview.TuningOverride = value;
    }

    /// Opens the menu again from the start, so a changed tuning can be seen. Silent, and does nothing off screen;
    /// the resting hover comes back after the bloom.
    public void Replay()
    {
        if (Live is not { } preview) return;
        trial++;
        pressedThisClick = false;
        preview.BeginOpen();
        if (IsMouseOver) Follow(Mouse.GetPosition(this));
        else
        {
            preview.PointerLeft();
            // Showing the resting hover is a picture, not a hover by the user: no tick, no visit.
            preview.ShowHover(restingHover);
            preview.ResetVisit();
        }
    }

    public bool IsInteractive
    {
        get => interactive;
        set
        {
            interactive = value;
            UpdateLiveness();
        }
    }

    public PreviewHost(WheelView preview)
    {
        Preview = preview;
        natural = preview.ViewSize;
        restingHover = preview.Hovered;
        preview.ResetVisit();
        AddVisualChild(preview);
        AddLogicalChild(preview);
        ClipToBounds = true;
        Focusable = false;
        IsVisibleChanged += (_, _) => UpdateLiveness();
        Loaded += (_, _) => UpdateLiveness();
        Unloaded += (_, _) => UpdateLiveness();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => Preview;

    protected override Size MeasureOverride(Size available)
    {
        Preview.Measure(natural);
        double w = double.IsInfinity(available.Width) ? natural.Width : available.Width;
        double h = double.IsInfinity(available.Height) ? natural.Height : available.Height;
        if (double.IsInfinity(available.Width) && !double.IsInfinity(available.Height)) w = h * natural.Width / natural.Height;
        if (double.IsInfinity(available.Height) && !double.IsInfinity(available.Width)) h = w * natural.Height / natural.Width;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var fit = Fit(natural, new Rect(10, 10, Math.Max(final.Width - 20, 1), Math.Max(final.Height - 20, 1)));
        double s = fit.Width / natural.Width;
        var matrix = new Matrix(s, 0, 0, s, fit.X, fit.Y);
        // Set before arranging: the render transform becomes part of the child's placement during Arrange.
        if (Preview.RenderTransform is not MatrixTransform current || current.Matrix != matrix)
            Preview.RenderTransform = new MatrixTransform(matrix);
        Preview.Arrange(new Rect(natural));
        return final;
    }

    /// `size` scaled to fit inside `rect`, centred, never larger than natural size.
    static Rect Fit(Size size, Rect rect)
    {
        double s = Math.Min(Math.Min(rect.Width / size.Width, rect.Height / size.Height), 1);
        double w = size.Width * s, h = size.Height * s;
        return new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (DrawsWallpaper) dc.DrawRoundedRectangle(MenuPreview.WallpaperBrush(rect), null, rect, 10, 10);
        // Transparent, but there, so the pointer is tracked over the margin too.
        else dc.DrawRectangle(Brushes.Transparent, null, rect);
    }

    // MARK: Live only on screen

    /// The frame driver needs a window, and a preview that isn't showing must never tick or sound.
    void UpdateLiveness()
    {
        if (interactive && IsVisible && PresentationSource.FromVisual(this) != null)
        {
            Preview.IsLive = true;
        }
        else if (Preview.IsLive)
        {
            Preview.IsLive = false;
            Rest();
        }
    }

    /// Back to the resting picture with no motion. Runs while the preview is not live, so nothing gives feedback.
    void Rest()
    {
        trial++;
        pressedThisClick = false;
        if (Preview.Pressed != null) Preview.BeginOpen();
        Preview.PointerLeft();
        Preview.Hovered = restingHover;
        // Setting the resting hover marks it visited; clear that, or the next try opens on "Cancel".
        Preview.ResetVisit();
        Preview.Settle();
    }

    // MARK: Pointer

    /// A point in this host's coordinates in the preview's own, unscaled coordinates, if it is over the menu.
    public Point? PreviewPoint(Point hostPoint)
    {
        var p = TranslatePoint(hostPoint, Preview);
        return p.X >= 0 && p.Y >= 0 && p.X <= natural.Width && p.Y <= natural.Height ? p : null;
    }

    WheelView? Live => Preview.IsLive ? Preview : null;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        // Each entry is a fresh try: items visited last time don't make an empty spot mean "cancel" now.
        if (Live is { Pressed: null } preview) preview.ResetVisit();
        Follow(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Moving with the button held is a drag: hovering keeps following until something is pressed.
        if (!(e.LeftButton == MouseButtonState.Pressed && pressedThisClick)) Follow(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Live?.PointerLeft();
    }

    /// Follows the pointer at a host point (also used by tests).
    public void Follow(Point hostPoint)
    {
        if (Live is not { } preview) return;
        if (PreviewPoint(hostPoint) is { } p) preview.Track(p); else preview.PointerLeft();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        pressedThisClick = false;
        if (Live is { Pressed: null } preview && PreviewPoint(e.GetPosition(this)) is { } p)
        {
            preview.Track(p);
            if (preview.Hovered is { } index) Press(index);
        }
        // Not handled: a Style card around the preview presses too.
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        // Letting go on an item reached with the button held is a drop.
        if (!pressedThisClick && Live is { Pressed: null } preview && PreviewPoint(e.GetPosition(this)) != null && preview.Hovered is { } index)
            Press(index);
        pressedThisClick = false;
    }

    /// Presses an item in, the way a drop does, then opens the menu again so it can be tried once more.
    public void Press(int index)
    {
        pressedThisClick = true;
        Preview.BeginConfirm(index);
        Feedback.Press();
        int current = ++trial;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ReopenDelay / Preview.Tuning.SafeSpeed) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (trial == current) Reopen();
        };
        timer.Start();
    }

    void Reopen()
    {
        if (Live is not { } preview) return;
        preview.BeginOpen();
        // Hover whatever is under the pointer now; it may have left while the item was pressed.
        if (IsMouseOver) Follow(Mouse.GetPosition(this)); else preview.PointerLeft();
    }
}
