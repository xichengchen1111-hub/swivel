using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Swivel;

// The library's 3D picture of a code (the view half of QRGarden.swift), drawn with WPF's built-in 3D.

/// Turns voxels into WPF meshes. Faces are flat-shaded once here (the light comes from the upper left), so the top
/// faces keep their exact colour and the scene needs only a white ambient light (constant lighting: ambient white ×
/// diffuse colour = the colour). WPF meshes have no vertex colours, so faces are merged into one mesh per colour; the
/// Mac's gentle darkening of side faces toward the ground becomes one flat shade per side (its average).
static class QrGardenGeometry
{
    /// sRGB shade of each side relative to the top: front (+z), right (+x), left (−x), back (−z).
    static readonly float[] SideShades = [0.86f, 0.70f, 0.78f, 0.90f];

    /// Colours are quantized to 6 bits per channel so the many slightly varied voxels share a few hundred meshes.
    public static Color Quantize(Vector3 c)
    {
        static byte q(float v) => (byte)Math.Round(Math.Round(Math.Clamp(v, 0, 1) * 63) * 255 / 63);
        return Color.FromRgb(q(c.X), q(c.Y), q(c.Z));
    }

    sealed class Mesh
    {
        public readonly List<Point3D> Positions = [];
        public readonly List<int> Indices = [];
    }

    /// One frozen GeometryModel3D per colour. `brushes` receives each model's brush so a caller can fade them.
    public static Model3DGroup Make(IReadOnlyList<QrVoxel> boxes, List<SolidColorBrush>? brushes = null)
    {
        var meshes = new Dictionary<Color, Mesh>();
        Mesh For(Color color)
        {
            if (!meshes.TryGetValue(color, out var mesh)) meshes[color] = mesh = new Mesh();
            return mesh;
        }
        void Quad(Mesh mesh, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int start = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
            mesh.Indices.Add(start); mesh.Indices.Add(start + 1); mesh.Indices.Add(start + 2);
            mesh.Indices.Add(start); mesh.Indices.Add(start + 2); mesh.Indices.Add(start + 3);
        }
        foreach (var box in boxes)
        {
            double hx = box.Size / 2, hz = (box.Depth ?? box.Size) / 2;
            double x0 = box.X - hx, x1 = box.X + hx, z0 = box.Z - hz, z1 = box.Z + hz, y0 = box.Y0, y1 = box.Y1;
            var side = box.Side ?? box.Color;
            // The Mac shades each side from `floor` at the ground up to its full shade at the top; flat here.
            float floor = box.Y1 - box.Y0 > 1.5f ? 0.86f : 0.93f, average = (1 + floor) / 2;
            Quad(For(Quantize(box.Color)), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0));
            Quad(For(Quantize(side * SideShades[0] * average)), new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
            Quad(For(Quantize(side * SideShades[1] * average)), new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));
            Quad(For(Quantize(side * SideShades[2] * average)), new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
            Quad(For(Quantize(side * SideShades[3] * average)), new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));
        }
        var group = new Model3DGroup();
        foreach (var (color, mesh) in meshes)
        {
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection(mesh.Positions),
                TriangleIndices = new Int32Collection(mesh.Indices),
            };
            geometry.Freeze();
            var brush = new SolidColorBrush(color);
            if (brushes != null) brushes.Add(brush); else brush.Freeze();
            var material = new DiffuseMaterial(brush) { AmbientColor = Colors.White };
            group.Children.Add(new GeometryModel3D(geometry, material));
        }
        return group;
    }

    /// The number of triangles in a group (for timing checks).
    public static int Triangles(Model3DGroup group) =>
        group.Children.OfType<GeometryModel3D>().Sum(m => ((MeshGeometry3D)m.Geometry).TriangleIndices.Count / 3);
}

/// A small voxel scene grown from the code's modules. A click turns the camera from a three-quarter view to straight
/// above, where the leaves, flowers or land line up into the scannable code.
sealed class QrGardenView : Border
{
    public SavedQrCode? Entry { get; private set; }
    public QrGardenModel? Model { get; private set; }

    readonly Viewport3D viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    readonly ModelVisual3D visual = new();
    readonly Model3DGroup scene = new();
    readonly OrthographicCamera camera = new() { NearPlaneDistance = 1, FarPlaneDistance = 2000 };
    readonly ScaleTransform3D growth = new(1, 1, 1);
    Model3DGroup ground = new(), cover = new(), structure = new();
    readonly List<SolidColorBrush> structureBrushes = [];
    /// Points that bound what the camera must keep in frame.
    Vector3[] hull = [];
    Spring flip = new(0, response: 0.75, damping: 1);
    Spring grow = new(1, response: 0.7, damping: 0.78);
    readonly FrameDriver driver = new();
    double structureOpacity = 1;

    public bool ShowsCode => flip.Target == 1;
    public Action<bool>? OnToggle { get; set; }

    public QrGardenView()
    {
        CornerRadius = new CornerRadius(12);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        Focusable = true;
        Cursor = Cursors.Hand;
        SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
        ToolUI.AutomationName(this, L("Show QR code or scene"));
        viewport.Margin = new Thickness(8);
        viewport.Camera = camera;
        visual.Content = scene;
        viewport.Children.Add(visual);
        Child = viewport;
        driver.OnFrame = dt =>
        {
            var tuning = ToolUI.Tuning;
            if (Motion.Reduced) { flip.Snap(flip.Target); grow.Snap(grow.Target); }
            else { flip.Step(dt, tuning); grow.Step(dt, tuning); }
            Apply();
            return !flip.IsSettled || !grow.IsSettled;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            Toggle();
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter)
            {
                Toggle();
                e.Handled = true;
            }
        };
        SizeChanged += (_, _) => Apply();
        Unloaded += (_, _) =>
        {
            driver.Stop();
            flip.Snap(flip.Target);
            grow.Snap(grow.Target);
            Apply();
        };
    }

    public static ulong Seed(string text) => QrGardenModel.Seed(text);

    /// Shows a saved code (or nothing). Built before anything changes, so a code that can't be generated leaves the
    /// view as it was.
    public void Display(SavedQrCode? entry)
    {
        var model = entry is null ? null
            : QrGardenModel.Build(QrModules.Matrix(entry.Value), entry.Template, entry.Season, Seed(entry.Value) ^ Seed(entry.Template.Key()));
        Display(entry, model);
    }

    /// Shows a code's prepared scene.
    internal void Display(SavedQrCode? entry, QrGardenModel? model)
    {
        var previous = Entry;
        Entry = entry;
        Model = model;
        Focusable = entry != null;
        Install(model);
        if (entry is null) return;
        flip.Snap(0);
        // A new code (or a new template) grows in; a season change just recolours.
        if (previous?.Id != entry.Id || previous?.Template != entry.Template)
        {
            grow.Snap(0);
            grow.Target = 1;
        }
        else grow.Snap(1);
        Apply();
        if (IsLoaded && !Motion.Reduced) driver.Start();
        else
        {
            grow.Snap(1);
            Apply();
        }
    }

    void Install(QrGardenModel? model)
    {
        scene.Children.Clear();
        structureBrushes.Clear();
        hull = [];
        structureOpacity = 1;
        if (model is null)
        {
            viewport.Visibility = Visibility.Hidden;
            return;
        }
        viewport.Visibility = Visibility.Visible;
        ground = QrGardenGeometry.Make(model.Ground);
        cover = QrGardenGeometry.Make(model.Cover);
        structure = QrGardenGeometry.Make(model.Structure, structureBrushes);
        cover.Transform = growth;
        structure.Transform = growth;
        scene.Children.Add(new AmbientLight(Colors.White));
        scene.Children.Add(ground);
        scene.Children.Add(cover);
        // Last, so it blends over the rest while it fades.
        scene.Children.Add(structure);
        float edge = model.Size / 2f + model.Margin;
        var points = new List<Vector3>(model.Cover.Count + model.Structure.Count + 8);
        foreach (var x in new[] { -edge, edge })
            foreach (var z in new[] { -edge, edge })
            {
                points.Add(new Vector3(x, 0, z));
                points.Add(new Vector3(x, -0.9f, z));
            }
        foreach (var v in model.Cover) points.Add(new Vector3(v.X, v.Y1, v.Z));
        foreach (var v in model.Structure) points.Add(new Vector3(v.X, v.Y1, v.Z));
        hull = [.. points];
    }

    public void SetShowsCode(bool value)
    {
        flip.Target = value ? 1 : 0;
        if (Motion.Reduced || !IsLoaded)
        {
            flip.Snap(flip.Target);
            Apply();
        }
        else driver.Start();
        OnToggle?.Invoke(value);
    }

    void Toggle()
    {
        if (Entry is null) return;
        Feedback.Press();
        SetShowsCode(!ShowsCode);
    }

    /// Camera, fade and growth for the current spring values.
    void Apply() => Apply(camera, viewport.ActualWidth, viewport.ActualHeight, flip.Value, grow.Value);

    void Apply(OrthographicCamera target, double width, double height, double flipValue, double growValue)
    {
        float phase = (float)Math.Clamp(flipValue, 0, 1);
        float eased = phase * phase * (3 - 2 * phase);
        growth.ScaleY = Math.Max(0.001, growValue);
        SetStructureOpacity(1 - Smoothstep(0.45f, 0.9f, phase));
        if (width <= 0 || height <= 0) { width = 600; height = 450; }
        var (right, up, forward) = Orientation(eased);
        Frame(target, right, up, forward, (float)(width / Math.Max(1, height)), 1.06f - 0.02f * eased, (float)Math.Max(0.001, growValue));
    }

    void SetStructureOpacity(double opacity)
    {
        if (Math.Abs(opacity - structureOpacity) < 0.002 && opacity is > 0 and < 1) return;
        structureOpacity = opacity;
        bool shown = opacity > 0.004;
        if (shown && !scene.Children.Contains(structure) && structure.Children.Count > 0) scene.Children.Add(structure);
        if (!shown) scene.Children.Remove(structure);
        foreach (var brush in structureBrushes) brush.Opacity = Math.Clamp(opacity, 0, 1);
    }

    /// Elevation 32° → 90° and azimuth 45° → 0° (the Mac's quaternions, written out as the camera's axes).
    public static (Vector3 Right, Vector3 Up, Vector3 Forward) Orientation(float t)
    {
        float elevation = (32 + 58 * t) * MathF.PI / 180, azimuth = 45 * (1 - t) * MathF.PI / 180;
        float ce = MathF.Cos(elevation), se = MathF.Sin(elevation), ca = MathF.Cos(azimuth), sa = MathF.Sin(azimuth);
        var forward = new Vector3(-ce * sa, -se, -ce * ca);
        var up = new Vector3(-se * sa, ce, -se * ca);
        var right = new Vector3(ca, 0, -sa);
        return (right, up, forward);
    }

    public static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    void Frame(OrthographicCamera target, Vector3 right, Vector3 up, Vector3 forward, float aspect, float padding, float grown)
    {
        float minR = float.PositiveInfinity, maxR = float.NegativeInfinity, minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
        float lift = Math.Max(grown, 1);
        foreach (var p in hull)
        {
            var point = new Vector3(p.X, p.Y > 0 ? p.Y * lift : p.Y, p.Z);
            float r = Vector3.Dot(point, right), u = Vector3.Dot(point, up);
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
        }
        if (!float.IsFinite(minR)) return;
        var center = right * (minR + maxR) / 2 + up * (minU + maxU) / 2;
        var position = center - forward * 500;
        // SceneKit's orthographicScale is half the visible height; WPF's Width is the whole visible width.
        float scale = Math.Max((maxU - minU) / 2, (maxR - minR) / 2 / aspect) * padding;
        target.Position = new Point3D(position.X, position.Y, position.Z);
        target.LookDirection = new Vector3D(forward.X, forward.Y, forward.Z);
        target.UpDirection = new Vector3D(up.X, up.Y, up.Z);
        target.Width = 2 * scale * aspect;
    }

    /// Where a scene point lands in a `Snapshot` of this size and phase, in pixels from the top left (for checks).
    internal Point Project(Vector3 point, int width, int height, double phase)
    {
        var shot = new OrthographicCamera();
        Apply(shot, width, height, phase, 1);
        Apply();
        float t = (float)Math.Clamp(phase, 0, 1);
        var (right, up, _) = Orientation(t * t * (3 - 2 * t));
        var offset = point - new Vector3((float)shot.Position.X, (float)shot.Position.Y, (float)shot.Position.Z);
        double scale = width / shot.Width;
        return new Point(width / 2.0 + Vector3.Dot(offset, right) * scale, height / 2.0 - Vector3.Dot(offset, up) * scale);
    }

    /// Renders the scene offscreen at rest in either view (for checks and exports), on a transparent background.
    public BitmapSource Snapshot(int width, int height, bool showsCode) => Snapshot(width, height, showsCode ? 1 : 0);

    /// `phase` runs from the three-quarter view (0) to straight above (1).
    public BitmapSource Snapshot(int width, int height, double phase)
    {
        var shot = new OrthographicCamera { NearPlaneDistance = 1, FarPlaneDistance = 2000 };
        Apply(shot, width, height, phase, 1);
        // RenderTargetBitmap draws 3D without antialiasing: render at twice the size and scale down smoothly (the
        // Mac snapshots with 4× multisampling). Flat areas keep their exact colour.
        const int factor = 2;
        int w = width * factor, h = height * factor;
        var offscreen = new Viewport3D { Camera = shot, Width = w, Height = h, ClipToBounds = true };
        // A model can belong to one visual at a time: borrow the scene for the render.
        visual.Content = null;
        var borrowed = new ModelVisual3D { Content = scene };
        offscreen.Children.Add(borrowed);
        try
        {
            offscreen.Measure(new Size(w, h));
            offscreen.Arrange(new Rect(0, 0, w, h));
            offscreen.UpdateLayout();
            var large = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            large.Render(offscreen);
            large.Freeze();
            var reduce = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(reduce, BitmapScalingMode.HighQuality);
            using (var dc = reduce.RenderOpen()) dc.DrawImage(large, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(reduce);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            borrowed.Content = null;
            offscreen.Children.Clear();
            visual.Content = scene;
            Apply();
        }
    }
}
