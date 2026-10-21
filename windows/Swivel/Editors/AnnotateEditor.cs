using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Swivel;

/// A mark on the photo. Points are normalised (0–1), top-left origin, so marks scale to full resolution.
sealed record Mark(Mark.Kinds Kind, Point Start, Point End, Color Color, double Width, string Text = "")
{
    public enum Kinds { Arrow, Rectangle, Ellipse, Text, Number }
}

/// Draws marks into any drawing area `rect` (the photo as shown, or the full-size photo).
static class MarkRenderer
{
    static readonly Typeface bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface heavy = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    static Point ToRect(Point p, Rect rect) => new(rect.X + p.X * rect.Width, rect.Y + p.Y * rect.Height);

    public static double LineWidth(Mark mark, Rect rect) => Math.Max(1, mark.Width * Math.Max(rect.Width, rect.Height));

    /// The soft shadow under every mark (the Mac's: down by 0.3 × the line width, blurred by the line width, 30 % black).
    public static Effect Shadow(Mark mark, Rect rect)
    {
        double lw = LineWidth(mark, rect);
        var effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, Direction = 270, ShadowDepth = lw * 0.3, BlurRadius = lw * 1.5 };
        effect.Freeze();
        return effect;
    }

    public static void Draw(DrawingContext dc, Mark mark, Rect rect, double pixelsPerDip)
    {
        var a = ToRect(mark.Start, rect);
        var b = ToRect(mark.End, rect);
        double lw = LineWidth(mark, rect);
        var brush = new SolidColorBrush(mark.Color);
        brush.Freeze();
        var pen = new Pen(brush, lw) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var box = EditorGeometry.FromEdges(a.X, a.Y, b.X, b.Y);
        switch (mark.Kind)
        {
            case Mark.Kinds.Arrow:
            {
                double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                double head = lw * 4.5;
                var shaftEnd = new Point(b.X - Math.Cos(angle) * head * 0.8, b.Y - Math.Sin(angle) * head * 0.8);
                dc.DrawLine(pen, a, shaftEnd);
                var tip = new StreamGeometry();
                using (var g = tip.Open())
                {
                    g.BeginFigure(b, isFilled: true, isClosed: true);
                    g.LineTo(new Point(b.X - Math.Cos(angle - Math.PI / 7) * head, b.Y - Math.Sin(angle - Math.PI / 7) * head), true, true);
                    g.LineTo(new Point(b.X - Math.Cos(angle + Math.PI / 7) * head, b.Y - Math.Sin(angle + Math.PI / 7) * head), true, true);
                }
                tip.Freeze();
                dc.DrawGeometry(brush, null, tip);
                break;
            }
            case Mark.Kinds.Rectangle:
                dc.DrawRoundedRectangle(null, pen, box, lw, lw);
                break;
            case Mark.Kinds.Ellipse:
                dc.DrawEllipse(null, pen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case Mark.Kinds.Text:
            {
                double size = lw * 7;
                var text = TextLayout(mark, size, pixelsPerDip);
                // The click is just above the text's cap height, as on the Mac (which draws from the baseline side).
                dc.DrawText(text, new Point(a.X, a.Y - size * 0.2));
                break;
            }
            case Mark.Kinds.Number:
            {
                double r = lw * 3.5;
                dc.DrawEllipse(brush, null, a, r, r);
                var text = new FormattedText(mark.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, heavy, Math.Max(1, r * 1.15), Brushes.White, pixelsPerDip);
                dc.DrawText(text, new Point(a.X - text.Width / 2, a.Y - text.Height / 2));
                break;
            }
        }
    }

    static FormattedText TextLayout(Mark mark, double size, double pixelsPerDip)
    {
        var brush = new SolidColorBrush(mark.Color);
        brush.Freeze();
        return new FormattedText(mark.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, Math.Max(1, size), brush, pixelsPerDip);
    }

    /// Where a just-placed mark grows from when it pops in: a number's centre, the middle of a text label.
    public static Point Anchor(Mark mark, Rect rect, double pixelsPerDip)
    {
        var a = ToRect(mark.Start, rect);
        if (mark.Kind != Mark.Kinds.Text) return a;
        double size = LineWidth(mark, rect) * 7;
        var text = TextLayout(mark, size, pixelsPerDip);
        return new Point(a.X + text.Width / 2, a.Y - size * 0.2 + text.Height / 2);
    }

    /// One mark as a visual with its shadow.
    public static DrawingVisual Visual(Mark mark, Rect rect, double pixelsPerDip)
    {
        var visual = new DrawingVisual { Effect = Shadow(mark, rect) };
        using (var dc = visual.RenderOpen()) Draw(dc, mark, rect, pixelsPerDip);
        return visual;
    }

    /// The photo with marks drawn in, at the photo's own size. Call on an STA thread.
    public static BitmapSource Composite(BitmapSource image, IReadOnlyList<Mark> marks)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var rect = new Rect(0, 0, w, h);
        var root = new ContainerVisual();
        var photo = new DrawingVisual();
        using (var dc = photo.RenderOpen()) dc.DrawImage(image, rect);
        root.Children.Add(photo);
        foreach (var mark in marks) root.Children.Add(Visual(mark, rect, 1));
        return Pixels.Render(root, w, h);
    }
}

/// What Shift does to a shape being drawn, in view points. Pure, so it can be tested.
static class MarkConstraint
{
    /// The end of a line from `a` toward `b`, turned to the nearest multiple of 45°: the pointer projected onto that
    /// direction, plus which of the eight directions it is.
    public static (Point Point, int Direction)? Snap45(Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Sqrt(dx * dx + dy * dy) <= 0.5) return null;
        double step = Math.PI / 4;
        int direction = (int)Math.Round(Math.Atan2(dy, dx) / step, MidpointRounding.AwayFromZero);
        double angle = direction * step;
        double length = dx * Math.Cos(angle) + dy * Math.Sin(angle);
        return (new Point(a.X + Math.Cos(angle) * length, a.Y + Math.Sin(angle) * length), ((direction % 8) + 8) % 8);
    }

    /// The corner opposite `a` of the square whose side is the larger of the drag's width and height.
    public static Point Square(Point a, Point b)
    {
        double side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + (b.X >= a.X ? side : -side), a.Y + (b.Y >= a.Y ? side : -side));
    }

    /// Pulls `b` back toward `a` (which is inside `rect`) until it's inside too, keeping the direction.
    public static Point Clamp(Point b, Point a, Rect rect)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = 1;
        if (b.X < rect.Left && dx != 0) t = Math.Min(t, (rect.Left - a.X) / dx);
        if (b.X > rect.Right && dx != 0) t = Math.Min(t, (rect.Right - a.X) / dx);
        if (b.Y < rect.Top && dy != 0) t = Math.Min(t, (rect.Top - a.Y) / dy);
        if (b.Y > rect.Bottom && dy != 0) t = Math.Min(t, (rect.Bottom - a.Y) / dy);
        t = Math.Max(t, 0);
        return new Point(a.X + dx * t, a.Y + dy * t);
    }
}

/// The photo with committed marks as retained visuals (redrawn only when they or the layout change), and the shape
/// being dragged on top, so drawing stays quick however many marks there are. A placed number or text pops in.
sealed class AnnotateCanvas : PictureCanvas
{
    public IReadOnlyList<Mark> Marks { get; private set; } = [];
    Mark? draft;
    public Func<Point, Mark?>? MakeMark { get; set; }
    public Action<Mark>? OnCommit { get; set; }
    /// A click that couldn't place anything (the Text tool with no text).
    public Action? OnRefused { get; set; }

    int? popping;
    Spring pop = new(1, 0.22, 0.7);
    /// The Shift constraint holding (a direction 0–7, or 100 for a square), for one snap as each one catches.
    int? constraint;
    Point? lastPoint;
    readonly FrameDriver driver = new();
    readonly ScaleTransform popScale = new();
    /// The shape being dragged, with the same shadow as the placed marks.
    readonly DrawingVisual draftVisual = new();

    public AnnotateCanvas()
    {
        driver.OnFrame = Advance;
        Cursor = Cursors.Cross;
    }

    double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    public Mark? Draft
    {
        get => draft;
        set
        {
            draft = value;
            RedrawDraft();
        }
    }

    /// Replaces the committed marks; `popLast` makes the newest one pop in.
    public void SetMarks(IReadOnlyList<Mark> marks, bool popLast)
    {
        popping = popLast && marks.Count > 0 && !Motion.Reduced ? marks.Count - 1 : null;
        Marks = marks;
        if (popping != null)
        {
            pop.Snap(1.2);
            pop.Target = 1;
            if (PresentationSource.FromVisual(this) != null) driver.Start();
            else SettlePop();
        }
        RebuildMarks();
    }

    protected override void OnLayoutChanged()
    {
        base.OnLayoutChanged();
        RebuildMarks();
    }

    void RebuildMarks()
    {
        MiddleLayer.Children.Clear();
        var rect = ImageRect;
        if (Picture is null || rect.IsEmpty)
        {
            MiddleLayer.Children.Add(draftVisual);
            return;
        }
        for (int i = 0; i < Marks.Count; i++)
        {
            var visual = MarkRenderer.Visual(Marks[i], rect, Dip);
            if (i == popping)
            {
                var anchor = MarkRenderer.Anchor(Marks[i], rect, Dip);
                popScale.CenterX = anchor.X;
                popScale.CenterY = anchor.Y;
                popScale.ScaleX = popScale.ScaleY = pop.Value;
                visual.Transform = popScale;
            }
            MiddleLayer.Children.Add(visual);
        }
        MiddleLayer.Children.Add(draftVisual);
        RedrawDraft();
    }

    void RedrawDraft()
    {
        var rect = ImageRect;
        if (draft is { } mark && Picture != null && !rect.IsEmpty)
        {
            draftVisual.Effect = MarkRenderer.Shadow(mark, rect);
            using var dc = draftVisual.RenderOpen();
            MarkRenderer.Draw(dc, mark, rect, Dip);
        }
        else
        {
            using var dc = draftVisual.RenderOpen();
        }
    }

    bool Advance(double dt)
    {
        pop.Step(dt);
        popScale.ScaleX = popScale.ScaleY = pop.Value;
        if (pop.IsSettled) SettlePop();
        return popping != null;
    }

    /// The popping mark settles at its full size.
    void SettlePop()
    {
        if (popping is null) return;
        popping = null;
        popScale.ScaleX = popScale.ScaleY = 1;
    }

    public void Stop() => driver.Stop();

    Point Normalized(Point p)
    {
        var r = ImageRect;
        return new Point(Math.Clamp((p.X - r.X) / r.Width, 0, 1), Math.Clamp((p.Y - r.Y) / r.Height, 0, 1));
    }

    protected override void DrawOverlay(DrawingContext dc) { }

    // MARK: Pointer

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Picture is null) return;
        // Clicking the photo ends typing in the Text field, so Ctrl+Z now means marks.
        Focus();
        SettlePop();
        var p = e.GetPosition(this);
        Press(p);
        if (draft != null) CaptureMouse();
        e.Handled = true;
    }

    /// A press on the canvas at `p` (view coordinates).
    public void Press(Point p)
    {
        lastPoint = p;
        constraint = null;
        Draft = MakeMark?.Invoke(Normalized(p));
        if (draft is null) OnRefused?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draft is null) return;
        DragTo(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    public void DragTo(Point p, bool shift)
    {
        lastPoint = p;
        Reshape(shift);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Pressing or releasing Shift mid-drag reshapes at once, without waiting for the pointer to move.
        if (draft != null && e.Key is Key.LeftShift or Key.RightShift) Reshape(true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (draft != null && e.Key is Key.LeftShift or Key.RightShift) Reshape(false);
    }

    /// Shift turns an arrow to the nearest 45° and makes a box or circle square, snapping as each direction catches.
    void Reshape(bool shift)
    {
        if (draft is not { } mark || mark.Kind is Mark.Kinds.Number or Mark.Kinds.Text || lastPoint is not Point p) return;
        var rect = ImageRect;
        var a = new Point(rect.X + mark.Start.X * rect.Width, rect.Y + mark.Start.Y * rect.Height);
        var b = new Point(Math.Clamp(p.X, rect.Left, rect.Right), Math.Clamp(p.Y, rect.Top, rect.Bottom));
        int? caught = null;
        if (shift)
        {
            if (mark.Kind == Mark.Kinds.Arrow)
            {
                if (MarkConstraint.Snap45(a, p) is { } snapped)
                {
                    b = MarkConstraint.Clamp(snapped.Point, a, rect);
                    caught = snapped.Direction;
                }
            }
            else
            {
                b = MarkConstraint.Clamp(MarkConstraint.Square(a, p), a, rect);
                caught = 100;
            }
        }
        if (caught != null && caught != constraint) Feedback.Snap();
        constraint = caught;
        Draft = mark with { End = Normalized(b) };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        Release();
    }

    public void Release()
    {
        if (draft is not { } mark) return;
        Draft = null;
        constraint = null;
        lastPoint = null;
        double dx = mark.End.X - mark.Start.X, dy = mark.End.Y - mark.Start.Y;
        bool moved = Math.Sqrt(dx * dx + dy * dy) > 0.01;
        if (mark.Kind is Mark.Kinds.Text or Mark.Kinds.Number || moved) OnCommit?.Invoke(mark);
    }
}

/// Arrows, boxes, circles, text and numbered callouts. Undo and redo go through the window (Ctrl+Z / Ctrl+Y), Reset too.
sealed class AnnotateEditor : Editor
{
    readonly AnnotateCanvas canvas;
    static readonly (string Name, Mark.Kinds Kind)[] tools =
        [("Arrow", Mark.Kinds.Arrow), ("Box", Mark.Kinds.Rectangle), ("Circle", Mark.Kinds.Ellipse), ("Text", Mark.Kinds.Text), ("Number", Mark.Kinds.Number)];
    readonly Segments tool;
    readonly ColorWell colorWell;
    readonly DetentSlider thickness;
    readonly TextBox textField = new() { Text = L("Note"), MinWidth = 200 };

    public AnnotateEditor(string path) : this(path, new AnnotateCanvas()) { }

    AnnotateEditor(string path, AnnotateCanvas canvas) : base("Annotate Photo", path, canvas)
    {
        this.canvas = canvas;
        tool = Segments(tools.Select(t => t.Name).ToList());
        AddRow("Tool", tool);
        colorWell = ColorWell(Color.FromRgb(0xFF, 0x3B, 0x30));
        thickness = Slider(0.002, 0.015, 0.006);
        AddRow("Style", colorWell, thickness);
        AddRow("Text", Stretch(WithPlaceholder(textField, L("Text to place"))));
        var undo = new Button { Content = L("Undo"), MinWidth = 70 };
        undo.Click += (_, _) =>
        {
            canvas.Focus();
            History.Undo();
        };
        var hint = Label(L("Drag on the photo to draw. Text and numbers are placed with a click."));
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        AddRow("", undo, Stretch(hint));

        canvas.MakeMark = point =>
        {
            var kind = tools[tool.SelectedIndex].Kind;
            var mark = new Mark(kind, point, point, colorWell.Color, thickness.Value);
            switch (kind)
            {
                case Mark.Kinds.Text:
                    if (textField.Text.Length == 0) return null;
                    mark = mark with { Text = textField.Text };
                    break;
                case Mark.Kinds.Number:
                    mark = mark with { Text = (canvas.Marks.Count(m => m.Kind == Mark.Kinds.Number) + 1).ToString(CultureInfo.CurrentCulture) };
                    break;
            }
            return mark;
        };
        canvas.OnCommit = mark => SetMarks([.. canvas.Marks, mark], pop: mark.Kind is Mark.Kinds.Number or Mark.Kinds.Text);
        canvas.OnRefused = () =>
        {
            // Nothing to place: say so and put the cursor where the text goes.
            Feedback.Refuse();
            textField.Focus();
            textField.SelectAll();
        };
    }

    /// A text box with grey hint text while it's empty.
    static FrameworkElement WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        if (hint.Foreground is null) hint.Foreground = Brushes.Gray;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    public AnnotateCanvas Canvas => canvas;

    /// Picks a tool (for tests): 0 arrow, 1 box, 2 circle, 3 text, 4 number.
    public void SelectTool(int index) => tool.SelectedIndex = index;

    public string PlacedText
    {
        get => textField.Text;
        set => textField.Text = value;
    }

    protected override async Task LoadAsync()
    {
        var source = Source;
        canvas.Picture = await Task.Run(() => Images.Load(source, 1600));
    }

    /// Replaces the marks in a way Ctrl+Z can take back (and Ctrl+Y redo).
    void SetMarks(IReadOnlyList<Mark> marks, bool pop = false)
    {
        var old = canvas.Marks;
        canvas.SetMarks(marks, pop);
        History.Register(() => SetMarks(old));
        NoteEdited();
    }

    public override bool HasUnsavedWork => canvas.Marks.Count > 0;

    protected override void Reset()
    {
        if (canvas.Marks.Count > 0) SetMarks([]);
    }

    public override void DidClose() => canvas.Stop();

    protected override Task<List<string>> ApplyAsync()
    {
        var marks = canvas.Marks.ToList();
        var source = Source;
        return Sta.Run(() =>
        {
            var result = MarkRenderer.Composite(Images.Load(source), marks);
            var ext = Images.WritableExt(source);
            var output = Paths.Output(source, ext, " annotated");
            Images.Write(result, output, ext, 0.95);
            return new List<string> { output };
        });
    }
}
