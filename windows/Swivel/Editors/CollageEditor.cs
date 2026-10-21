using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// Combines several images into a grid, row, column or featured layout, saved as "Collage.png" next to the first.
sealed class CollageEditor : Editor
{
    public readonly record struct Look(int Layout, double Gap, double Corners, Color Background);

    readonly ImagePreview preview;
    public IReadOnlyList<string> Sources { get; }
    List<BitmapSource> thumbs = [];
    static readonly string[] layouts = ["Grid", "Row", "Column", "Featured"];
    readonly Segments layout;
    readonly DetentSlider spacing, corners;
    readonly ColorWell colorWell;
    int lastLayout;
    bool fadeNext;
    readonly LiveRender<BitmapSource?> renderer;

    public CollageEditor(IReadOnlyList<string> paths) : this(paths, new ImagePreview()) { }

    CollageEditor(IReadOnlyList<string> paths, ImagePreview preview) : base("Make a Collage", paths[0], preview)
    {
        this.preview = preview;
        Sources = paths;
        renderer = new LiveRender<BitmapSource?>(Show);
        SetSubtitle(L("%d images", paths.Count));
        layout = Segments(layouts);
        AddRow("Layout", layout);
        spacing = Slider(0, 0.06, 0.02);
        AddRow("Spacing", spacing);
        corners = Slider(0, 0.15, 0.03);
        AddRow("Corners", corners);
        colorWell = ColorWell(Colors.White);
        AddRow("Background", colorWell);
    }

    Look CurrentLook => new(layout.SelectedIndex, spacing.Value, corners.Value, colorWell.Color);

    protected override async Task LoadAsync()
    {
        // Decode every image and compose the first preview off the UI thread, so the window opens on the collage.
        var paths = Sources.ToList();
        var look = CurrentLook;
        var (decoded, first) = await Sta.Run(() =>
        {
            var images = paths.Select(p => Images.Load(p, 700)).ToList();
            return (images, Render(images, 1200, look));
        });
        thumbs = decoded;
        preview.Image = first;
    }

    /// Cell frames in a unit-width canvas (y up, as on the Mac), plus the canvas height.
    public static (List<Rect> Frames, double Height) Cells(IReadOnlyList<Size> sizes, int layout, double gap)
    {
        int n = sizes.Count;
        double Aspect(int i) => sizes[i].Width / Math.Max(sizes[i].Height, 1);
        switch (layout)
        {
            case 1:
            {
                // Row: equal heights, widths follow each image's aspect.
                double total = Enumerable.Range(0, n).Sum(Aspect);
                double h = (1 - gap * (n + 1)) / total;
                double x = gap;
                var frames = new List<Rect>();
                for (int i = 0; i < n; i++)
                {
                    frames.Add(new Rect(x, gap, h * Aspect(i), h));
                    x += h * Aspect(i) + gap;
                }
                return (frames, h + gap * 2);
            }
            case 2:
            {
                // Column: equal widths stacked top to bottom.
                double w = 1 - gap * 2;
                var heights = Enumerable.Range(0, n).Select(i => w / Aspect(i)).ToList();
                double y = gap;
                var frames = new List<Rect>();
                for (int i = n - 1; i >= 0; i--)
                {
                    frames.Insert(0, new Rect(gap, y, w, heights[i]));
                    y += heights[i] + gap;
                }
                return (frames, y);
            }
            case 3 when n > 1:
            {
                // Featured: first image large on top, the rest in a row beneath.
                var big = new Rect(gap, 0, 1 - gap * 2, (1 - gap * 2) / Aspect(0));
                int rest = n - 1;
                double side = (1 - gap * (rest + 1)) / rest;
                var frames = new List<Rect>();
                for (int i = 0; i < rest; i++) frames.Add(new Rect(gap + i * (side + gap), gap, side, side));
                var bigFrame = new Rect(big.X, big.Y + side + gap * 2, big.Width, big.Height);
                return ([bigFrame, .. frames], bigFrame.Bottom + gap);
            }
            default:
            {
                // Grid of square cells.
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                int rows = (int)Math.Ceiling((double)n / cols);
                double side = (1 - gap * (cols + 1)) / cols;
                double height = gap + rows * (side + gap);
                var frames = Enumerable.Range(0, n).Select(i =>
                {
                    int row = i / cols, col = i % cols;
                    return new Rect(gap + col * (side + gap), height - (row + 1) * (side + gap), side, side);
                }).ToList();
                return (frames, height);
            }
        }
    }

    /// The collage `width` pixels wide. Call on an STA thread.
    public static BitmapSource Render(IReadOnlyList<BitmapSource> images, double width, Look look)
    {
        var sizes = images.Select(i => new Size(i.PixelWidth, i.PixelHeight)).ToList();
        var (frames, unitHeight) = Cells(sizes, look.Layout, look.Gap);
        double height = unitHeight * width;
        int pw = (int)Math.Round(width), ph = Math.Max(1, (int)Math.Round(height));
        return Pixels.Render(pw, ph, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(look.Background), null, new Rect(0, 0, pw, ph));
            for (int i = 0; i < Math.Min(images.Count, frames.Count); i++)
            {
                var image = images[i];
                var unit = frames[i];
                // y up → y down.
                var cell = new Rect(unit.X * width, height - unit.Bottom * width, unit.Width * width, unit.Height * width);
                // Fill the cell, cropping the image's overflow.
                double scale = Math.Max(cell.Width / image.PixelWidth, cell.Height / image.PixelHeight);
                var drawn = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
                double r = look.Corners * Math.Min(cell.Width, cell.Height);
                dc.PushClip(new RectangleGeometry(cell, r, r));
                dc.DrawImage(image, new Rect(cell.X + cell.Width / 2 - drawn.Width / 2, cell.Y + cell.Height / 2 - drawn.Height / 2, drawn.Width, drawn.Height));
                dc.Pop();
            }
        });
    }

    void Show(BitmapSource? image)
    {
        if (image is null) return;
        preview.SetImage(image, fadeNext);
        fadeNext = false;
    }

    protected override void Update()
    {
        // A new layout rearranges every cell: crossfade it rather than cut.
        if (layout.SelectedIndex != lastLayout)
        {
            lastLayout = layout.SelectedIndex;
            fadeNext = true;
        }
        if (thumbs.Count == 0) return;
        var images = thumbs;
        var look = CurrentLook;
        renderer.Request(() => Sta.Invoke(() => Render(images, 1200, look)));
    }

    protected override void Reset()
    {
        layout.SelectedIndex = 0;
        spacing.Value = 0.02;
        corners.Value = 0.03;
        colorWell.Color = Colors.White;
    }

    public override void DidClose()
    {
        renderer.Cancel();
        preview.Stop();
    }

    protected override Task<List<string>> ApplyAsync()
    {
        var paths = Sources.ToList();
        var look = CurrentLook;
        return Sta.Run(() =>
        {
            var full = paths.Select(p => Images.Load(p, 3000)).ToList();
            var result = Render(full, 4000, look);
            var output = Paths.Unique(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(paths[0]) ?? "", "Collage.png"));
            Images.Write(result, output, "png", 1);
            return new List<string> { output };
        });
    }
}
