using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Swivel;

/// The new screenshot, waiting in the bottom-right corner of the work area (above the taskbar) like the system's own
/// thumbnail. Drag it up to the rope or click it to hang it; drag it into an app or folder like any file; ignore it
/// and it slides away (the file simply stays in the Screenshots folder).
sealed class IncomingCard
{
    public static IncomingCard Shared { get; } = new();

    static readonly Stopwatch time = Stopwatch.StartNew();
    static double Now => time.Elapsed.TotalSeconds;

    Window? window;
    IncomingView? view;
    readonly CardTimer timer = new();
    DispatcherTimer? dismissal;
    string? next;                 // arrived during a drag; shown once the drag ends
    int presentation;
    RopeMonitor monitor;
    Int32Rect frame;

    public string? Path { get; private set; }
    public bool IsDragging => timer.Dragging;
    internal CardTimer Timer => timer;

    Window Panel
    {
        get
        {
            if (window != null) return window;
            view = new IncomingView(this);
            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Focusable = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Title = L("Drag up to hang"),
                Content = view,
            };
            Native.MakeNonActivating(window);
            new WindowInteropHelper(window).EnsureHandle();
            return window;
        }
    }

    IncomingView View { get { _ = Panel; return view!; } }

    public void Present(string path)
    {
        // A newer screenshot never pulls the file out from under a drag; it waits until the drag ends. (Only a card
        // that is still here can be dragging.)
        if (timer.Dragging && Path != null)
        {
            if (next is { } waiting && !Same(waiting, path)) Release(waiting);
            next = path;
            return;
        }
        var panel = Panel;
        // Replacing a card that's showing: the old one fades away in place as the new one slides in.
        var ghost = Path != null && !Same(Path, path) && panel.IsVisible ? View.Snapshot() : null;
        if (Path is { } current && !Same(current, path)) Release(current);
        presentation++;
        Path = path;
        View.ResetInteraction();
        monitor = RopeNative.MonitorUnderPointer();
        View.SetThumbnail(RopeThumbnails.Image(path, 480), monitor.Scale);
        if (RopeThumbnails.IsMovie(path)) _ = LoadMovieThumbnail(path, presentation);
        Position();
        View.LayoutCard();
        bool hovered = View.CardScreenRect.Contains(new Point(Native.CursorPixels().X, Native.CursorPixels().Y));
        View.ResetInteraction(hovered);
        if (ghost is { } g) View.FadeGhost(g);
        if (!panel.IsVisible)
        {
            panel.Show();
            RopeNative.Place(panel, frame);
        }
        View.Enter();
        timer.Present(Now, hovered);
        StartDismissalClock();
    }

    async Task LoadMovieThumbnail(string path, int generation)
    {
        var thumb = await RopeThumbnails.LoadAsync(path, 480);
        if (thumb is null || generation != presentation || !Same(Path, path)) return;
        View.SetThumbnail(thumb, monitor.Scale);
        Position();
        View.LayoutCard();
    }

    /// The window reaches the work area's right edge, so the card slides in from (and out through) the edge itself.
    void Position()
    {
        var size = View.PreferredSize;
        double s = monitor.Scale;
        int w = (int)Math.Ceiling(size.Width * s), h = (int)Math.Ceiling(size.Height * s);
        frame = new Int32Rect(monitor.Work.X + monitor.Work.Width - w, monitor.Work.Y + monitor.Work.Height - (int)Math.Round(14 * s) - h, w, h);
        View.Width = size.Width;
        View.Height = size.Height;
        RopeNative.Place(Panel, frame);
        View.WindowFrame = frame;
        View.WindowScale = s;
    }

    static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// The card's thumbnail, for the rope to reuse instead of decoding the file again.
    public Dictionary<string, BitmapSource> Thumbnails(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        if (Path is { } path && paths.Any(p => Same(p, path)) && view?.Thumbnail is { } thumb) result[path] = thumb;
        return result;
    }

    void StartDismissalClock()
    {
        if (dismissal is null)
        {
            dismissal = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            dismissal.Tick += (_, _) =>
            {
                if (Path is null) { dismissal!.Stop(); return; }
                if (timer.Due(Now)) PutAway(Path);
            };
        }
        dismissal.Start();
    }

    // Called by the view.
    public void HoverChanged(bool on) => timer.SetHovered(on, Now);
    public void PressChanged(bool on) => timer.SetInteracting(on, Now, resume: false);

    /// Takes the card off screen; its frame loop stops with it, so the next card starts it afresh.
    void OrderOut()
    {
        window?.Hide();
        view?.Halt();
    }

    /// Not hung: a copied image Swivel saved goes where screenshots go (the Mac's "usual folder"); a screenshot is
    /// already there.
    static void Release(string path) => Clothesline.Shared.Release(path);

    /// The clothesline is going away (switched off, or Swivel is quitting): the card's file and one queued behind a
    /// drag are released now, and the card leaves.
    public void PutAwayAll(bool animated)
    {
        if (next is { } waiting)
        {
            next = null;
            Release(waiting);
        }
        if (Path is { } path) PutAway(path, animated);
    }

    /// Whether a window is the card's, so a click on it isn't taken for a click away from the rope.
    public bool Owns(IntPtr hwnd) => window != null && hwnd != IntPtr.Zero && hwnd == new WindowInteropHelper(window).Handle;

    /// Not wanted on the rope: released, and the card slides away.
    public void PutAway(string path, bool animated = true)
    {
        Release(path);
        if (!Same(path, Path)) return;
        Path = null;
        timer.Dismissed();
        if (!animated) { OrderOut(); return; }
        int generation = presentation;
        View.Exit(() => { if (presentation == generation) OrderOut(); });
    }

    /// Click: the file is hung at once, and the card flies up to its peg to show where it went.
    public void HangNow()
    {
        if (Path is not { } path) return;
        var thumbs = Thumbnails([path]);
        Path = null;
        timer.Dismissed();
        int generation = presentation;
        var from = View.PhotoScreenRect;
        var photo = View.PhotoImage;
        var rope = Clothesline.Shared.Panel;
        // The flight needs the rope on the card's monitor (where the rope will lower: the pointer is on the card).
        bool flies = !Motion.Reduced && photo != null;
        var hung = Clothesline.Shared.Receive([path], flies ? RopeArrival.HandOff : RopeArrival.InPlace, thumbs, owned: true);
        if (!flies || hung.Count == 0 || !rope.IsVisible || rope.Monitor.Handle != monitor.Handle)
        {
            if (hung.Count > 0)
            {
                Feedback.Press();   // hung in place (reduced motion): the click still lands firmly
                if (flies) rope.Rope.HandOff(hung[0]);
            }
            View.FadeOut(() => { if (presentation == generation) OrderOut(); });
            return;
        }
        var destination = hung[0];
        CardFlight.Shared.Fly(photo!, rope.Rope.ImageOf(destination), from, monitor,
            target: () => rope.Rope.Landing(destination) is { } l ? (rope.ToScreen(l.Rect), l.Angle) : null,
            started: () => { if (presentation == generation) OrderOut(); },  // the flight has drawn its first frame over the card
            landed: () => rope.Rope.HandOff(destination));
    }

    /// Double-click: opened to look at, not hung.
    public void Open()
    {
        if (Path is not { } path) return;
        Path = null;
        timer.Dismissed();
        var saved = Clothesline.Shared.Release(path) ?? path;
        int generation = presentation;
        View.FadeOut(() => { if (presentation == generation) OrderOut(); });
        Clothesline.Shared.Open(saved);
    }

    public void BeginDrag(Point downInView)
    {
        if (Path is not { } path) return;
        timer.BeginDrag();
        Feedback.Prepare();
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1 /* DROPEFFECT_COPY */)));
        if (View.PhotoImage is { } picture)
        {
            var photo = View.PhotoRect;
            double s = picture.PixelWidth / Math.Max(photo.Width, 1);
            RopeNative.SetDragImage(data, picture, new Point((downInView.X - photo.X) * s, (downInView.Y - photo.Y) * s));
        }
        View.SetDimmed(true);
        Clothesline.Shared.BeginReceiving();
        var result = DragDropEffects.None;
        // The file is the clothesline's (a screenshot or a saved copy), so it may also go to the Recycle Bin (a move).
        try { result = DragDrop.DoDragDrop(View, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (Exception ex) { Log.Write($"Clothesline card drag: {ex.Message}"); }
        DraggingEnded(result);
    }

    void DraggingEnded(DragDropEffects result)
    {
        var cursor = Native.CursorPixels();
        bool hovered = View.CardScreenRect.Contains(new Point(cursor.X, cursor.Y));
        View.ResetInteraction(hovered, afterDrag: true);
        Clothesline.Shared.EndReceiving();
        try
        {
            if (Path is not { } path)
            {
                timer.EndDrag(Now, hovered);
                View.SetDimmed(false);
                return;
            }
            bool intoFolder = result != DragDropEffects.None && RopeNative.IsFolderWindowAt(cursor);
            // The rope may have taken it without having hung it yet (that happens on the next dispatcher turn).
            if (Clothesline.Shared.IsTaking(path) || !File.Exists(path) || intoFolder)
            {
                // Hung on the rope, moved or copied into a folder, or thrown away: the card's job is done.
                if (intoFolder && File.Exists(path)) Clothesline.Shared.CopiedAway(path);
                Path = null;
                timer.Taken();
                OrderOut();
                View.SetDimmed(false, animated: false);
            }
            else
            {
                // Copied into an app, or dropped nowhere: the card comes back and waits again.
                View.SetDimmed(false);
                timer.EndDrag(Now, hovered);
            }
        }
        finally
        {
            if (next is { } waiting)
            {
                next = null;
                Present(waiting);
            }
        }
    }
}

/// The card's picture: a photo with a white border over a "Drag up to hang" capsule, drawn as visuals so it can
/// slide, lift and press on springs at the display's refresh rate. (y grows downward, unlike the Mac view.)
sealed class IncomingView : FrameworkElement
{
    readonly IncomingCard? card;
    readonly VisualCollection children;
    readonly DrawingVisual hitArea = new();   // a 1/255 fill over the card, so all of it tracks the pointer
    readonly ContainerVisual ghostLayer = new();
    readonly ContainerVisual cardLayer = new();
    readonly DrawingVisual photoLayer = new();
    readonly DrawingVisual capsuleLayer = new();
    readonly DropShadowEffect shadow = new() { Color = Colors.Black, Opacity = 0.35, Direction = 270, RenderingBias = RenderingBias.Performance };
    readonly FrameDriver driver = new();
    Spring slide = new(0, 0.32, 0.9);     // pixels to the right of its place
    Spring alpha = new(1, 0.14, 1);
    Spring hover = new(0, 0.25, 0.9);
    Spring press = new(0, RopeTuning.PressIn.Response, RopeTuning.PressIn.Damping);
    Spring ready = new(0, 0.25, 0.9);     // clicked: lifts a little while it waits to fly
    double? exitProgress;                 // leaving through the screen edge, 0…1
    double exitFrom;
    Action? onGone;
    double clock;
    (DrawingVisual Visual, double Start)? ghost;
    Point? downPoint;
    bool dragged;
    DispatcherTimer? singleClick;
    double renderScale = 2;

    public BitmapSource? Thumbnail { get; private set; }
    public BitmapSource? PhotoImage { get; private set; }
    public bool IsHovered { get; private set; }
    public bool IsInteracting => downPoint != null || singleClick != null;
    /// For tests and frame renders.
    public bool? ReducedMotionOverride { get; set; }
    bool IsMotionReduced => ReducedMotionOverride ?? Motion.Reduced;
    /// Where the window is on screen (physical pixels) and its scale, for screen rectangles.
    public Int32Rect WindowFrame { get; set; }
    public double WindowScale { get; set; } = 1;

    /// Room for the hover shadow on the left and top; the photo keeps 22 px from the screen's right edge.
    public const double CardMargin = 24;
    public const double EdgeInset = 22;
    const double ExitDuration = 0.22;
    static readonly Size MaxThumb = new(200, 128);
    const double PhotoBottom = 34;   // the capsule sits below the photo

    public IncomingView(IncomingCard? card = null)
    {
        this.card = card;
        children = new VisualCollection(this) { hitArea, ghostLayer, cardLayer };
        cardLayer.Children.Add(photoLayer);
        cardLayer.Children.Add(capsuleLayer);
        photoLayer.Effect = shadow;
        driver.OnFrame = Advance;
        Focusable = false;
    }

    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];
    protected override Size MeasureOverride(Size availableSize) => PreferredSize;

    public void ResetInteraction(bool hovered = false, bool afterDrag = false)
    {
        singleClick?.Stop();
        singleClick = null;
        downPoint = null;
        dragged = afterDrag;
        IsHovered = hovered;
        hover.Target = hovered ? 1 : 0;
        press.Target = 0;
        ready.Target = 0;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Wake();
    }

    // MARK: Layout

    Size ThumbSize
    {
        get
        {
            if (Thumbnail is not { PixelWidth: > 0, PixelHeight: > 0 } t) return new Size(180, 110);
            double w = t.PixelWidth, h = t.PixelHeight;
            double s = Math.Min(MaxThumb.Width / w, MaxThumb.Height / h);
            return new Size(w * s, h * s);
        }
    }

    Size PhotoSize => new(ThumbSize.Width + 8, ThumbSize.Height + 8);

    static string Hint => "↑  " + L("Drag up to hang");

    FormattedText HintText(double pixelsPerDip) => new(Hint, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, AppTypography.Weight(FontWeights.SemiBold), FontStretches.Normal),
        AppTypography.Size(11), Brushes.White, pixelsPerDip);

    // Text size scales the capsule's height as on the Mac; the photo keeps its place above it.
    Size CapsuleSize => new(Math.Ceiling(HintText(1).Width + 20), 20 * AppTypography.Scale);

    Size ContentSize => new(Math.Max(Math.Max(PhotoSize.Width, CapsuleSize.Width), 154), PhotoBottom + PhotoSize.Height);

    public Size PreferredSize => new(CardMargin + ContentSize.Width + EdgeInset, ContentSize.Height + CardMargin);

    /// The photo and capsule, in view coordinates; only this part tracks hover.
    public Rect ContentRect => new(new Point(CardMargin, CardMargin), ContentSize);

    public Rect PhotoRect
    {
        get
        {
            var content = ContentRect;
            var photo = PhotoSize;
            return new Rect(content.X + (content.Width - photo.Width) / 2, content.Y, photo.Width, photo.Height);
        }
    }

    Rect ToScreen(Rect r) => new(WindowFrame.X + r.X * WindowScale, WindowFrame.Y + r.Y * WindowScale, r.Width * WindowScale, r.Height * WindowScale);

    /// The card's content on screen, in physical pixels.
    public Rect CardScreenRect => ToScreen(ContentRect);

    /// The photo as it is drawn now, lift and press included, in physical pixels.
    public Rect PhotoScreenRect
    {
        get
        {
            var m = CardMatrix();
            var r = PhotoRect;
            var a = m.Transform(r.TopLeft);
            var b = m.Transform(r.BottomRight);
            return ToScreen(new Rect(a, b));
        }
    }

    double Offscreen => ContentSize.Width + EdgeInset + 24;

    public void SetThumbnail(BitmapSource? image, double scale = 2)
    {
        Thumbnail = image;
        renderScale = Math.Max(scale, 1);
        var size = PhotoSize;
        var thumb = ThumbSize;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) DrawPhoto(dc, image, size, thumb);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * renderScale), (int)Math.Ceiling(size.Height * renderScale), 96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        PhotoImage = bitmap;
        LayoutCard();
    }

    static void DrawPhoto(DrawingContext dc, ImageSource? image, Size size, Size thumb)
    {
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(size), 6, 6);
        if (image is null) return;
        var inner = new Rect(4, 4, thumb.Width, thumb.Height);
        dc.PushClip(new RectangleGeometry(inner, 3, 3));
        dc.DrawImage(image, inner);
        dc.Pop();
    }

    public void LayoutCard()
    {
        var content = ContentRect;
        var photo = PhotoRect;
        var capsule = CapsuleSize;
        using (var dc = hitArea.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, content);
        using (var dc = photoLayer.RenderOpen()) DrawPhoto(dc, Thumbnail, photo.Size, ThumbSize);
        photoLayer.Offset = new Vector(photo.X, photo.Y);
        double pixelsPerDip = 1;
        try { pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception) { }
        using (var dc = capsuleLayer.RenderOpen())
        {
            var rect = new Rect(content.X + (content.Width - capsule.Width) / 2, content.Bottom - 2 - capsule.Height, capsule.Width, capsule.Height);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, rect, 10, 10);
            var text = HintText(pixelsPerDip);
            dc.DrawText(text, new Point(rect.X + 10, rect.Y + (rect.Height - text.Height) / 2));
        }
        InvalidateMeasure();
        Render();
    }

    // MARK: Motion

    void Wake()
    {
        if (Window.GetWindow(this)?.IsVisible == true) driver.Start();
    }

    /// Slides in from the screen's right edge like the system's thumbnail (fades in place under reduced motion).
    public void Enter()
    {
        exitProgress = null;
        onGone = null;
        press.Snap(0);
        ready.Snap(0);
        hover.Snap(IsHovered ? 1 : 0);
        alpha.Response = 0.14;
        if (IsMotionReduced)
        {
            slide.Snap(0);
            alpha.Snap(0);
        }
        else
        {
            slide.Snap(Offscreen);
            slide.Target = 0;
            alpha.Snap(1);
        }
        alpha.Target = 1;
        Render();
        Wake();
    }

    /// Leaves through the screen edge, accelerating away; an exit is quicker than the entrance.
    public void Exit(Action done)
    {
        onGone = done;
        if (IsMotionReduced)
        {
            alpha.Response = 0.1;
            alpha.Target = 0;
        }
        else
        {
            exitFrom = slide.Value;
            exitProgress = 0;
        }
        Wake();
        if (Window.GetWindow(this)?.IsVisible != true) { onGone = null; done(); }
    }

    /// Fades out where it is (opened, or hung under reduced motion).
    public void FadeOut(Action done)
    {
        onGone = done;
        exitProgress = null;
        alpha.Response = 0.1;
        alpha.Target = 0;
        Wake();
        if (Window.GetWindow(this)?.IsVisible != true) { onGone = null; done(); }
    }

    /// While the card is being dragged it stays behind as a faint outline of where it came from.
    public void SetDimmed(bool dimmed, bool animated = true)
    {
        alpha.Response = 0.16;
        if (animated) alpha.Target = dimmed ? 0.25 : 1; else alpha.Snap(dimmed ? 0.25 : 1);
        Render();
        Wake();
    }

    /// The card as it looks now (screen pixels), for fading out in place when a newer screenshot replaces it.
    public (BitmapSource Image, Rect Rect)? Snapshot()
    {
        if (PhotoImage is null || cardLayer.Opacity < 0.05 || Window.GetWindow(this)?.IsVisible != true) return null;
        return (PhotoImage, PhotoScreenRect);
    }

    public void FadeGhost((BitmapSource Image, Rect Rect) old)
    {
        var rect = new Rect((old.Rect.X - WindowFrame.X) / WindowScale, (old.Rect.Y - WindowFrame.Y) / WindowScale, old.Rect.Width / WindowScale, old.Rect.Height / WindowScale);
        var visual = new DrawingVisual
        {
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 14, ShadowDepth = 4, Direction = 270 },
        };
        using (var dc = visual.RenderOpen()) dc.DrawImage(old.Image, rect);
        ghostLayer.Children.Clear();
        ghostLayer.Children.Add(visual);
        ghost = (visual, clock);
        Wake();
    }

    /// Stops the frame loop (the card is off screen).
    public void Halt() => driver.Stop();

    /// One display frame. Returns false when everything has settled.
    public bool Advance(double dt)
    {
        clock += dt;
        if (exitProgress is { } progress)
        {
            progress = Math.Min(progress + dt * RopeTuning.Tuning.SafeSpeed / ExitDuration, 1);
            exitProgress = progress;
            slide.Snap(Mix.Lerp(exitFrom, Offscreen, RopeTuning.Ease(progress, 0.4, 0, 1, 1)));
        }
        else slide.Step(dt, RopeTuning.Tuning);
        alpha.Step(dt, RopeTuning.Tuning);
        hover.Step(dt, RopeTuning.Tuning);
        press.Step(dt, RopeTuning.Tuning);
        ready.Step(dt, RopeTuning.Tuning);
        Render();
        bool ghosting = AnimateGhost();
        bool gone = exitProgress is { } p ? p >= 1 : alpha.Target == 0 && alpha.Value < 0.02;
        if (gone)
        {
            exitProgress = null;
            var done = onGone;
            onGone = null;
            done?.Invoke();
            return false;
        }
        return ghosting || exitProgress != null || !(slide.IsSettled && alpha.IsSettled && hover.IsSettled && press.IsSettled && ready.IsSettled);
    }

    /// The replaced card fades and sinks 16 px over 0.18 s, easing in.
    bool AnimateGhost()
    {
        if (ghost is not { } g) return false;
        double u = Math.Clamp((clock - g.Start) / 0.18, 0, 1);
        double e = RopeTuning.EaseIn(u);
        g.Visual.Opacity = 1 - e;
        g.Visual.Transform = IsMotionReduced ? null : new TranslateTransform(0, 16 * e);
        if (u < 1) return true;
        ghostLayer.Children.Clear();
        ghost = null;
        return false;
    }

    Matrix CardMatrix()
    {
        bool reduced = IsMotionReduced;
        double h = hover.Value, p = press.Value, r = ready.Value;
        // Hover lifts the card 2 px toward you, a click readies it 4 px more; a press gives 3 %.
        double lift = reduced ? 0 : 2 * h + 4 * r;
        double scale = reduced ? 1 : 1 - 0.03 * p;
        var content = ContentRect;
        var center = new Point(content.X + content.Width / 2, content.Y + content.Height / 2);
        var m = Matrix.Identity;
        m.ScaleAt(scale, scale, center.X, center.Y);
        m.Translate(slide.Value, -lift);
        return m;
    }

    void Render()
    {
        double h = hover.Value, p = press.Value, r = ready.Value;
        cardLayer.Transform = new MatrixTransform(CardMatrix());
        cardLayer.Opacity = Math.Clamp(alpha.Value, 0, 1);
        shadow.BlurRadius = 2 * Math.Max(7 + 3 * h + 2 * r - 3 * p, 3);
        shadow.ShadowDepth = Math.Max(4 + 3 * h + 2 * r - 2 * p, 1);
    }

    // MARK: Events

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        IsHovered = true;
        hover.Target = 1;
        card?.HoverChanged(true);
        Wake();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsMouseCaptured) return;
        IsHovered = false;
        hover.Target = 0;
        card?.HoverChanged(false);
        Wake();
    }

    void SetPressed(bool on)
    {
        var tune = on ? RopeTuning.PressIn : RopeTuning.PressOut;
        press.Response = tune.Response;
        press.Damping = tune.Damping;
        press.Target = on ? 1 : 0;
        Wake();
    }

    int downClicks;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        card?.PressChanged(true);
        downPoint = e.GetPosition(this);
        downClicks = e.ClickCount;
        dragged = false;
        CaptureMouse();
        SetPressed(true);
    }

    /// For frame renders: the press a mouse-down would show.
    internal void PreviewPress(bool on) => SetPressed(on);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragged || downPoint is not { } down || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if ((p - down).Length <= 4) return;
        dragged = true;
        singleClick?.Stop();
        singleClick = null;
        SetPressed(false);
        ReleaseMouseCapture();
        card?.BeginDrag(down);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        downPoint = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetPressed(false);
        if (dragged) return;
        if (downClicks >= 2)
        {
            singleClick?.Stop();
            singleClick = null;
            card?.Open();
        }
        else
        {
            // Readied at once, so the wait that tells a click from a double-click doesn't feel dead.
            ready.Target = 1;
            Wake();
            singleClick?.Stop();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(RopeNative.GetDoubleClickTime()) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (singleClick != timer) return;
                singleClick = null;
                card?.HangNow();
            };
            singleClick = timer;
            timer.Start();
        }
    }

    /// Whether a screenshot is waiting (the card's button is only there while one is).
    internal bool HasScreenshot => card?.Path != null;

    /// Screen readers and UI Automation see a "Hang Screenshot" button that hangs the screenshot when invoked
    /// (the Mac 1.4.2 accessibility press).
    internal void InvokeHang()
    {
        if (!HasScreenshot) return;
        singleClick?.Stop();
        singleClick = null;
        card?.HangNow();
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new IncomingViewPeer(this);
}

sealed class IncomingViewPeer(IncomingView owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner),
    System.Windows.Automation.Provider.IInvokeProvider
{
    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
        System.Windows.Automation.Peers.AutomationControlType.Button;
    protected override string GetClassNameCore() => nameof(IncomingView);
    protected override string GetNameCore() => L("Hang Screenshot");
    protected override string GetHelpTextCore() => L("Click to hang this screenshot on the clothesline.");
    protected override bool IsControlElementCore() => owner.HasScreenshot;
    protected override bool IsContentElementCore() => owner.HasScreenshot;
    protected override bool IsEnabledCore() => owner.HasScreenshot;

    public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
        patternInterface == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    public void Invoke()
    {
        if (!IsEnabled()) throw new System.Windows.Automation.ElementNotEnabledException();
        owner.InvokeHang();
    }
}

/// A clicked card's trip from the corner up to its peg. A small transparent, click-through window carries the photo
/// and aims every frame at the print's live position, so the hand-off lands exactly where the print hangs even while
/// the rope is still settling. On the way the photo turns into the print's own picture.
sealed class CardFlight
{
    public static CardFlight Shared { get; } = new();

    Window? window;
    readonly FlightView view = new();
    readonly FrameDriver driver = new();
    double elapsed;
    Rect from;                                  // physical pixels
    double scale = 1;                           // the monitor's
    Func<(Rect Rect, double Angle)?>? target;
    Action? started, landed;
    bool showingLanding;
    public const double Duration = 0.36;
    const double Pad = 40;                      // room around the photo for its shadow and tilt

    CardFlight() => driver.OnFrame = Advance;

    Window Panel
    {
        get
        {
            if (window != null) return window;
            window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Content = view,
                IsHitTestVisible = false,
            };
            Native.MakeNonActivating(window);
            window.SourceInitialized += (_, _) => Native.SetClickThrough(window, true);
            new WindowInteropHelper(window).EnsureHandle();
            return window;
        }
    }

    public void Fly(BitmapSource image, BitmapSource? printImage, Rect fromPixels, RopeMonitor monitor,
                    Func<(Rect Rect, double Angle)?> target, Action started, Action landed)
    {
        Land();   // one still in the air lands at once
        from = fromPixels;
        scale = monitor.Scale > 0 ? monitor.Scale : 1;
        this.target = target;
        this.started = started;
        this.landed = landed;
        elapsed = 0;
        var size = new Size(from.Width / scale, from.Height / scale);
        // Big enough for the photo at its largest (it only shrinks or grows to the print) plus its shadow.
        var goal = target()?.Rect;
        double grow = goal is { } g ? Math.Max(1, g.Width / Math.Max(from.Width, 1)) : 1;
        var box = new Size(size.Width * grow + 2 * Pad, size.Height * grow + 2 * Pad);
        view.Set(image, printImage, size, box, Pad);
        var panel = Panel;
        panel.Width = box.Width;
        panel.Height = box.Height;
        var px = new Int32Rect(0, 0, (int)Math.Ceiling(box.Width * scale), (int)Math.Ceiling(box.Height * scale));
        var anchor = new Point(from.X + from.Width / 2, from.Y);
        px.X = (int)Math.Round(anchor.X - box.Width / 2 * scale);
        px.Y = (int)Math.Round(anchor.Y - Pad * scale);
        view.Update(0, 1, 0, 0.35, 7);
        if (!panel.IsVisible) panel.Show();
        RopeNative.Place(panel, px);
        driver.Start();
    }

    bool Advance(double dt)
    {
        if (showingLanding)
        {
            // The print has been showing for a frame underneath, so nothing blinks when the flight goes.
            showingLanding = false;
            window?.Hide();
            return false;
        }
        if (started is { } s)
        {
            started = null;
            s();
        }
        if (target?.Invoke() is not { } goal)
        {
            Land();
            return false;
        }
        elapsed += dt * RopeTuning.Tuning.SafeSpeed;
        double t = Math.Min(elapsed / Duration, 1);
        double e = RopeTuning.Ease(t, 0.3, 0, 0.2, 1);
        double size = Mix.Lerp(1, goal.Rect.Width / Math.Max(from.Width, 1), e);
        var anchor = new Point(Mix.Lerp(from.X + from.Width / 2, goal.Rect.X + goal.Rect.Width / 2, e), Mix.Lerp(from.Y, goal.Rect.Y, e));
        view.Update(goal.Angle * e, size, Math.Clamp((t - 0.55) / 0.45, 0, 1), 0.35, Mix.Lerp(7, 6, e));
        if (window != null)
            RopeNative.Move(window, (int)Math.Round(anchor.X - window.Width / 2 * scale), (int)Math.Round(anchor.Y - Pad * scale));
        if (t >= 1)
        {
            target = null;
            var l = landed;
            landed = null;
            l?.Invoke();
            showingLanding = true;
        }
        return true;
    }

    /// Ends a flight now: the card goes, the print shows, and the window is put away.
    void Land()
    {
        if (target is null && !showingLanding) return;
        var s = started;
        var l = landed;
        started = null;
        landed = null;
        target = null;
        showingLanding = false;
        s?.Invoke();
        l?.Invoke();
        driver.Stop();
        window?.Hide();
    }
}

/// The flying photo, turning into the print, hung from its top centre.
sealed class FlightView : FrameworkElement
{
    readonly VisualCollection children;
    readonly ContainerVisual holder = new();
    readonly DrawingVisual photo = new();
    readonly DrawingVisual print = new();
    readonly DropShadowEffect shadow = new() { Color = Colors.Black, Direction = 270, RenderingBias = RenderingBias.Performance };
    Size size;
    Size box;
    double pad;

    public FlightView()
    {
        children = new VisualCollection(this) { holder };
        holder.Children.Add(photo);
        holder.Children.Add(print);
        photo.Effect = shadow;
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];
    protected override Size MeasureOverride(Size availableSize) => box;

    public void Set(BitmapSource image, BitmapSource? printImage, Size size, Size box, double pad)
    {
        this.size = size;
        this.box = box;
        this.pad = pad;
        Width = box.Width;
        Height = box.Height;
        using (var dc = photo.RenderOpen()) dc.DrawImage(image, new Rect(size));
        using (var dc = print.RenderOpen()) if (printImage != null) dc.DrawImage(printImage, new Rect(size));
        print.Opacity = 0;
    }

    public void Update(double angle, double scale, double printOpacity, double shadowOpacity, double shadowRadius)
    {
        var m = Matrix.Identity;
        m.Translate(-size.Width / 2, 0);
        m.Scale(scale, scale);
        m.Rotate(angle * 180 / Math.PI);
        m.Translate(box.Width / 2, pad);
        holder.Transform = new MatrixTransform(m);
        print.Opacity = printOpacity;
        shadow.Opacity = shadowOpacity;
        shadow.BlurRadius = 2 * shadowRadius / Math.Max(scale, 0.1);
        shadow.ShadowDepth = 4 / Math.Max(scale, 0.1);
    }
}
