using System.Numerics;

namespace Swivel;

// The scene of a saved code as plain data (a port of the model half of QRGarden.swift). Every template is built from
// the code's own modules: seen from straight above, its top surfaces are the unmodified QR code (dark modules carry
// leaves, flowers or land; light modules stay pale).

enum QrTemplate { Tree, Bouquet, Island }

static class QrTemplates
{
    public static readonly QrTemplate[] All = [QrTemplate.Tree, QrTemplate.Bouquet, QrTemplate.Island];

    public static string Title(this QrTemplate template) => template switch
    {
        QrTemplate.Tree => L("Tree"),
        QrTemplate.Bouquet => L("Bouquet"),
        _ => L("Island"),
    };

    public static string Key(this QrTemplate template) => template switch
    {
        QrTemplate.Tree => "tree",
        QrTemplate.Bouquet => "bouquet",
        _ => "island",
    };

    public static QrTemplate? Parse(string? key) => All.Cast<QrTemplate?>().FirstOrDefault(t => t!.Value.Key() == key);
}

static class QrModules
{
    /// The code's modules, row 0 at the top, without the quiet zone: `dark[row][column]`.
    public static bool[][] Matrix(string value)
    {
        var matrix = QrImage.Raw(value);
        var rows = new bool[matrix.Size][];
        for (int r = 0; r < matrix.Size; r++)
        {
            rows[r] = new bool[matrix.Size];
            for (int c = 0; c < matrix.Size; c++) rows[r][c] = matrix[c, r];
        }
        return rows;
    }
}

/// One axis-aligned block of the scene, in module units (x to the right, z down the code, y up). Colours are sRGB 0–1.
record struct QrVoxel(float X, float Z, float Y0, float Y1, float Size, Vector3 Color, float? Depth = null, Vector3? Side = null);

/// `Ground` and `Cover` stay in the top view; `Cover` grows when a code appears. `Structure` (trunks, stems, paper,
/// boats, filler leaves) fades out on the way to the top view, so only the code's own surfaces are left to scan.
sealed class QrGardenModel
{
    public int Size { get; init; }
    public float Margin { get; init; }
    public List<QrVoxel> Ground { get; } = [];
    public List<QrVoxel> Cover { get; } = [];
    public List<QrVoxel> Structure { get; } = [];
    public required bool[][] Dark { get; init; }

    public static QrGardenModel Build(bool[][] dark, QrTemplate template, QrSeason season, ulong seed)
    {
        var builder = new QrGardenBuilder(dark, season, seed);
        switch (template)
        {
            case QrTemplate.Tree: builder.Tree(); break;
            case QrTemplate.Bouquet: builder.Bouquet(); break;
            default: builder.Island(); break;
        }
        return builder.Model;
    }

    /// FNV-1a of the UTF-8 text: the same code always grows the same scene.
    public static ulong Seed(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ b) * 1099511628211UL);
        return hash;
    }

    /// Top colour seen from straight above at a module, or null when nothing covers it but the slab.
    public Vector3? TopColor(int row, int column)
    {
        float half = (Size - 1) / 2f, x = column - half, z = row - half;
        QrVoxel? best = null;
        foreach (var v in Ground.Concat(Cover))
        {
            if (MathF.Abs(v.X - x) < v.Size / 2 && MathF.Abs(v.Z - z) < (v.Depth ?? v.Size) / 2 && (best is null || v.Y1 > best.Value.Y1))
                best = v;
        }
        return best?.Color;
    }
}

/// Grows the three templates. The numbers are the Mac app's; the random sequence is the same LCG.
sealed class QrGardenBuilder
{
    readonly bool[][] dark;
    readonly QrSeason season;
    ulong state;
    readonly int n;
    readonly float half;
    public QrGardenModel Model { get; }

    public QrGardenBuilder(bool[][] dark, QrSeason season, ulong seed)
    {
        this.dark = dark;
        this.season = season;
        state = seed | 1;
        n = dark.Length;
        half = (n - 1) / 2f;
        Model = new QrGardenModel { Size = n, Margin = 2.5f, Dark = dark };
    }

    static Vector3 Rgb(float r, float g, float b) => new(r, g, b);

    float Random()
    {
        state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
        return (float)((state >> 33) & 0xffffff) / 0xffffff;
    }

    Vector3 Pick(Vector3[] colors) => colors[Math.Min(colors.Length - 1, (int)(Random() * colors.Length))];

    Vector3 Vary(Vector3 c, float amount = 0.06f)
    {
        float k = 1 + (Random() - 0.5f) * 2 * amount;
        return Vector3.Clamp(c * k, Vector3.Zero, Vector3.One);
    }

    (float X, float Z) Position(int row, int column) => (column - half, row - half);

    /// Swift's `rounded()`: halves away from zero.
    static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// A wobbly outline, so a canopy or a coast isn't a perfect circle.
    Func<float, float> Outline()
    {
        float p1 = Random() * 6.28f, p2 = Random() * 6.28f, p3 = Random() * 6.28f;
        return a => 1 + 0.09f * MathF.Sin(3 * a + p1) + 0.06f * MathF.Sin(5 * a + p2) + 0.04f * MathF.Sin(8 * a + p3);
    }

    void Slab(Vector3 slab, Vector3 slabSide)
    {
        float extent = n + 2 * Model.Margin;
        Model.Ground.Add(new QrVoxel(0, 0, -0.9f, 0, extent, slab, extent, slabSide));
    }

    void Tiles(Vector3[] tiles)
    {
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                if (dark[r][c]) continue;
                var p = Position(r, c);
                Model.Ground.Add(new QrVoxel(p.X, p.Z, 0, 0.06f + Random() * 0.04f, 0.96f, Pick(tiles)));
            }
    }

    void Line(Vector3 a, Vector3 b, Func<float, float> width, Vector3 color, float step = 0.35f)
    {
        float length = (b - a).Length();
        int count = Math.Max(1, (int)(length / step));
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count, w = width(t);
            var p = a + (b - a) * t;
            Model.Structure.Add(new QrVoxel(p.X, p.Z, p.Y - w / 2, p.Y + w / 2, w, Vary(color, 0.05f)));
        }
    }

    // MARK: Tree

    public void Tree()
    {
        Vector3[] leaves, grass, fallen;
        switch (season)
        {
            case QrSeason.Spring:
                leaves = [Rgb(0.86f, 0.44f, 0.58f), Rgb(0.78f, 0.36f, 0.52f), Rgb(0.90f, 0.52f, 0.64f), Rgb(0.70f, 0.30f, 0.46f), Rgb(0.40f, 0.58f, 0.26f)];
                grass = [Rgb(0.36f, 0.58f, 0.24f), Rgb(0.30f, 0.52f, 0.20f), Rgb(0.42f, 0.62f, 0.26f)];
                fallen = [Rgb(0.94f, 0.66f, 0.74f), Rgb(0.90f, 0.58f, 0.68f)];
                break;
            case QrSeason.Summer:
                leaves = [Rgb(0.27f, 0.52f, 0.18f), Rgb(0.33f, 0.60f, 0.20f), Rgb(0.20f, 0.43f, 0.15f), Rgb(0.40f, 0.63f, 0.22f), Rgb(0.16f, 0.36f, 0.14f)];
                grass = [Rgb(0.32f, 0.56f, 0.20f), Rgb(0.26f, 0.48f, 0.17f), Rgb(0.40f, 0.62f, 0.24f)];
                fallen = [];
                break;
            default:
                leaves = [Rgb(0.84f, 0.42f, 0.10f), Rgb(0.76f, 0.32f, 0.09f), Rgb(0.88f, 0.54f, 0.14f), Rgb(0.64f, 0.24f, 0.10f), Rgb(0.70f, 0.40f, 0.10f)];
                grass = [Rgb(0.62f, 0.48f, 0.16f), Rgb(0.54f, 0.42f, 0.14f), Rgb(0.66f, 0.52f, 0.20f)];
                fallen = [Rgb(0.88f, 0.50f, 0.14f), Rgb(0.80f, 0.36f, 0.10f)];
                break;
        }
        Slab(Rgb(0.93f, 0.91f, 0.87f), Rgb(0.62f, 0.56f, 0.50f));
        Tiles([Rgb(0.94f, 0.93f, 0.90f), Rgb(0.91f, 0.90f, 0.87f), Rgb(0.96f, 0.95f, 0.92f), Rgb(0.89f, 0.88f, 0.85f)]);
        float size = n, radius = size * 0.40f, trunkTop = size * 0.27f, dome = size * 0.46f;
        var wobble = Outline();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var p = Position(r, c);
                float d = MathF.Sqrt(p.X * p.X + p.Z * p.Z), a = MathF.Atan2(p.Z, p.X);
                float reach = radius * wobble(a);
                if (d < reach)
                {
                    // Leaves over light modules fill the canopy out from the side and fade away from above.
                    bool filler = !dark[r][c];
                    if (filler && Random() > 0.5f) continue;
                    float t = d / reach;
                    float top = trunkTop + dome * MathF.Sqrt(1 - t * t) * (0.82f + 0.18f * Random()) + (filler ? -0.4f : 0.5f);
                    float bottom = trunkTop + dome * 0.2f * (1 - t) - 0.6f;
                    int layers = Math.Max(1, (int)Round((top - bottom) * (filler ? 0.25f : 0.35f + 0.35f * Random())));
                    for (int k = 0; k < layers; k++)
                    {
                        if (!(k == 0 || Random() > 0.22f)) continue;
                        float y1 = top - k * 1.02f;
                        var leaf = new QrVoxel(p.X, p.Z, y1 - 0.92f, y1, k == 0 ? 0.94f : 0.9f, Vary(Pick(leaves)));
                        (filler ? Model.Structure : Model.Cover).Add(leaf);
                    }
                }
                else if (dark[r][c])
                {
                    float height = 0.3f + Random() * 0.4f;
                    Model.Cover.Add(new QrVoxel(p.X, p.Z, 0, height, 0.94f, Vary(Pick(grass))));
                    int blades = Random() > 0.4f ? 2 : 1;
                    for (int i = 0; i < blades; i++)
                    {
                        float bx = p.X + (Random() - 0.5f) * 0.5f, bz = p.Z + (Random() - 0.5f) * 0.5f;
                        float y1 = height + 0.2f + Random() * 0.45f;
                        Model.Cover.Add(new QrVoxel(bx, bz, height, y1, 0.22f, Vary(Pick(grass), 0.12f)));
                    }
                }
            }
        // Trunk with a gentle curve, branches into the canopy, a few roots.
        float bend = Random() * 6.28f;
        float trunkHeight = trunkTop + dome * 0.35f;
        var trunk = Rgb(0.45f, 0.30f, 0.19f);
        int steps = (int)(trunkHeight / 0.4f);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            var q = new Vector3(0.6f * MathF.Sin(t * 2.4f + bend) * t, t * trunkHeight, 0.5f * MathF.Cos(t * 2.1f + bend) * t);
            float w = 1.25f - 0.55f * t;
            Model.Structure.Add(new QrVoxel(q.X, q.Z, q.Y - 0.25f, q.Y + 0.25f, w, Vary(trunk, 0.06f)));
        }
        for (int b = 0; b < 8; b++)
        {
            float a = b / 8f * 6.28f + Random() * 0.6f, start = trunkTop * (0.7f + 0.3f * Random());
            var from = new Vector3(0.6f * MathF.Sin(start / trunkHeight * 2.4f + bend) * start / trunkHeight, start,
                                   0.5f * MathF.Cos(start / trunkHeight * 2.1f + bend) * start / trunkHeight);
            float reach = radius * (0.45f + 0.35f * Random());
            var to = new Vector3(MathF.Cos(a) * reach, trunkTop + dome * (0.25f + 0.3f * Random()), MathF.Sin(a) * reach);
            Line(from, to, t => 0.6f - 0.35f * t, trunk);
        }
        for (int b = 0; b < 5; b++)
        {
            float a = b / 5f * 6.28f + Random();
            Line(new Vector3(0, 0.5f, 0), new Vector3(MathF.Cos(a) * 2.2f, 0.08f, MathF.Sin(a) * 2.2f), t => 0.55f - 0.3f * t, trunk);
        }
        int petals = fallen.Length == 0 ? 0 : n * 2;
        for (int i = 0; i < petals; i++)
        {
            int r = (int)(Random() * n), c = (int)(Random() * n);
            if (dark[r][c]) continue;
            var q = Position(r, c);
            float x = q.X + (Random() - 0.5f) * 0.6f, z = q.Z + (Random() - 0.5f) * 0.6f;
            Model.Structure.Add(new QrVoxel(x, z, 0.08f, 0.13f, 0.28f, Pick(fallen)));
        }
    }

    // MARK: Bouquet

    public void Bouquet()
    {
        Vector3[] flowers = season switch
        {
            QrSeason.Spring => [Rgb(0.86f, 0.40f, 0.56f), Rgb(0.92f, 0.50f, 0.42f), Rgb(0.74f, 0.30f, 0.50f), Rgb(0.94f, 0.58f, 0.30f), Rgb(0.80f, 0.42f, 0.70f)],
            QrSeason.Summer => [Rgb(0.36f, 0.42f, 0.80f), Rgb(0.56f, 0.38f, 0.78f), Rgb(0.30f, 0.56f, 0.82f), Rgb(0.82f, 0.36f, 0.52f), Rgb(0.94f, 0.66f, 0.16f)],
            _ => [Rgb(0.62f, 0.16f, 0.24f), Rgb(0.86f, 0.44f, 0.14f), Rgb(0.86f, 0.62f, 0.16f), Rgb(0.48f, 0.18f, 0.32f), Rgb(0.74f, 0.30f, 0.18f)],
        };
        Vector3[] leaves = [Rgb(0.28f, 0.50f, 0.26f), Rgb(0.22f, 0.42f, 0.24f), Rgb(0.36f, 0.56f, 0.30f)];
        Slab(Rgb(0.95f, 0.93f, 0.91f), Rgb(0.78f, 0.70f, 0.64f));
        Tiles([Rgb(0.96f, 0.94f, 0.92f), Rgb(0.94f, 0.92f, 0.90f), Rgb(0.97f, 0.95f, 0.94f)]);
        float size = n, radius = size * 0.38f, @base = size * 0.40f, dome = size * 0.26f;
        // Flower heads: each module belongs to its nearest bloom, so neighbours share a colour and a rounded top.
        var blooms = new List<(float X, float Z, Vector3 Color, bool Leaf)>();
        int count = Math.Max(8, (int)(MathF.PI * radius * radius / 12));
        for (int i = 0; i < count * 4; i++)
        {
            if (blooms.Count >= count) continue;
            float a = Random() * 6.28f, d = MathF.Sqrt(Random()) * radius;
            float x = MathF.Cos(a) * d, z = MathF.Sin(a) * d;
            if (!blooms.All(b => (b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) > 6)) continue;
            bool leaf = Random() < 0.18f;
            blooms.Add((x, z, leaf ? Pick(leaves) : Pick(flowers), leaf));
        }
        var wobble = Outline();
        var nearestBloom = new NearestPoint(blooms.Select(b => (b.X, b.Z)).ToList());
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var p = Position(r, c);
                float d = MathF.Sqrt(p.X * p.X + p.Z * p.Z), a = MathF.Atan2(p.Z, p.X);
                float reach = radius * wobble(a);
                if (d < reach && blooms.Count > 0)
                {
                    var bloom = blooms[nearestBloom.Find(p.X, p.Z)];
                    // Petals over light modules round the blooms out from the side and fade away from above.
                    bool filler = !dark[r][c];
                    if (filler && Random() > 0.55f) continue;
                    float t = d / reach, bd = Hypot(bloom.X - p.X, bloom.Z - p.Z);
                    float top = @base + dome * MathF.Sqrt(1 - t * t) + MathF.Max(0, 1.3f - bd * 0.5f) + Random() * 0.25f - (filler ? 0.6f : 0);
                    var petal = bloom.Leaf ? bloom.Color : bloom.Color * (1 - MathF.Min(0.22f, bd * 0.06f));
                    var target = filler ? Model.Structure : Model.Cover;
                    target.Add(new QrVoxel(p.X, p.Z, top - 0.9f, top, 0.94f, Vary(petal, 0.05f)));
                    target.Add(new QrVoxel(p.X, p.Z, top - 2.0f, top - 1.0f, 0.86f, Vary(Pick(leaves))));
                    if (!bloom.Leaf && !filler && bd < 0.8f)  // a raised centre in each bloom
                        target.Add(new QrVoxel(p.X, p.Z, top, top + 0.35f, 0.5f, Vary(bloom.Color * 0.7f, 0.04f)));
                }
                else if (dark[r][c])
                {
                    // Petals and leaves dropped on the table.
                    float height = 0.12f + Random() * 0.18f;
                    var color = Random() < 0.55f ? Pick(leaves) : Pick(flowers);
                    Model.Cover.Add(new QrVoxel(p.X, p.Z, 0, height, 0.94f, Vary(color)));
                }
            }
        // Stems gathered at a ribbon, wrapped in paper that opens toward the viewer.
        var knot = new Vector3(0, size * 0.13f, 0);
        foreach (var bloom in blooms)
        {
            Line(new Vector3(bloom.X * 0.75f, @base - 1, bloom.Z * 0.75f), knot, _ => 0.3f, Rgb(0.32f, 0.52f, 0.24f), 0.5f);
            Line(knot, new Vector3(bloom.X * 0.08f, 0.1f, bloom.Z * 0.08f), _ => 0.3f, Rgb(0.30f, 0.48f, 0.22f), 0.5f);
        }
        Vector3 kraft = Rgb(0.80f, 0.64f, 0.46f), tissue = Rgb(0.97f, 0.90f, 0.88f);
        float low = size * 0.10f, high = @base + dome * 0.5f;
        int steps = (int)((high - low) / 0.55f);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps, y = low + (high - low) * t;
            float ring = 1.3f + (radius * 0.96f - 1.3f) * MathF.Pow(t, 0.8f);
            int around = Math.Max(8, (int)(ring * 6.28f / 0.6f));
            for (int j = 0; j < around; j++)
            {
                float a = (float)j / around * 6.28f;
                float gap = a - 0.785f;
                gap = MathF.Atan2(MathF.Sin(gap), MathF.Cos(gap));  // open toward +x +z
                // The paper rises behind the flowers and opens wider toward the viewer as it climbs.
                float opening = 0.25f + 1.9f * MathF.Max(0, t - 0.35f);
                if (MathF.Abs(gap) < opening) continue;
                bool rim = i == steps || MathF.Abs(MathF.Abs(gap) - opening) < 0.12f;
                var color = rim ? Rgb(0.88f, 0.76f, 0.60f) : kraft;
                Model.Structure.Add(new QrVoxel(MathF.Cos(a) * ring, MathF.Sin(a) * ring, y - 0.3f, y + 0.3f, 0.62f, Vary(color, 0.04f)));
                if (t > 0.7f && j % 2 == 0)
                    Model.Structure.Add(new QrVoxel(MathF.Cos(a) * (ring - 0.5f), MathF.Sin(a) * (ring - 0.5f), y, y + 0.6f, 0.5f, Vary(tissue, 0.02f)));
            }
        }
        var ribbon = season == QrSeason.Summer ? Rgb(0.30f, 0.38f, 0.70f) : Rgb(0.72f, 0.18f, 0.26f);
        for (int j = 0; j < 16; j++)
        {
            float a = j / 16f * 6.28f;
            Model.Structure.Add(new QrVoxel(MathF.Cos(a) * 1.6f, MathF.Sin(a) * 1.6f, knot.Y - 0.3f, knot.Y + 0.4f, 0.55f, ribbon));
        }
        foreach (float side in new[] { -1f, 1f })
        {
            Line(new Vector3(1.2f, knot.Y, 1.2f), new Vector3(1.9f + side * 1.2f, knot.Y + 1.1f, 1.9f - side * 1.2f), _ => 0.5f, ribbon);
            Line(new Vector3(1.2f, knot.Y, 1.2f), new Vector3(1.6f + side * 0.6f, knot.Y - 2.0f, 1.6f - side * 0.6f), _ => 0.4f, ribbon);
        }
    }

    static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// The nearest of a set of points (the first one on a tie, like Swift's `min(by:)`), through a grid of cells so a
    /// large code's thousand blooms don't make the bouquet take seconds.
    internal sealed class NearestPoint
    {
        const float Cell = 4;
        readonly List<(float X, float Z)> points;
        readonly Dictionary<(int, int), List<int>> cells = [];
        readonly int span;

        public NearestPoint(List<(float X, float Z)> points)
        {
            this.points = points;
            int extent = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var key = Key(points[i].X, points[i].Z);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                list.Add(i);
                extent = Math.Max(extent, Math.Max(Math.Abs(key.Item1), Math.Abs(key.Item2)));
            }
            span = extent + 1;
        }

        static (int, int) Key(float x, float z) => ((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));

        public int Find(float x, float z)
        {
            var (cx, cz) = Key(x, z);
            int best = -1;
            float bestDistance = float.PositiveInfinity;
            for (int ring = 0; ring <= span + Math.Max(Math.Abs(cx), Math.Abs(cz)); ring++)
            {
                for (int i = cx - ring; i <= cx + ring; i++)
                    for (int j = cz - ring; j <= cz + ring; j++)
                    {
                        if (Math.Max(Math.Abs(i - cx), Math.Abs(j - cz)) != ring || !cells.TryGetValue((i, j), out var list)) continue;
                        foreach (int index in list)
                        {
                            float e = Hypot(points[index].X - x, points[index].Z - z);
                            if (e < bestDistance || (e == bestDistance && index < best)) { bestDistance = e; best = index; }
                        }
                    }
                // Every point in a later ring is at least `ring` cells away.
                if (best >= 0 && bestDistance < ring * Cell) break;
            }
            return best;
        }
    }

    // MARK: Island

    public void Island()
    {
        Vector3[] green, fronds;
        switch (season)
        {
            case QrSeason.Spring:
                green = [Rgb(0.36f, 0.60f, 0.28f), Rgb(0.30f, 0.54f, 0.26f), Rgb(0.44f, 0.64f, 0.30f), Rgb(0.70f, 0.40f, 0.56f)];
                fronds = [Rgb(0.30f, 0.58f, 0.24f), Rgb(0.38f, 0.64f, 0.26f)];
                break;
            case QrSeason.Summer:
                green = [Rgb(0.18f, 0.50f, 0.26f), Rgb(0.24f, 0.56f, 0.24f), Rgb(0.14f, 0.42f, 0.24f), Rgb(0.30f, 0.58f, 0.22f)];
                fronds = [Rgb(0.20f, 0.52f, 0.22f), Rgb(0.28f, 0.58f, 0.20f)];
                break;
            default:
                green = [Rgb(0.50f, 0.50f, 0.20f), Rgb(0.62f, 0.46f, 0.16f), Rgb(0.42f, 0.44f, 0.18f), Rgb(0.72f, 0.38f, 0.14f)];
                fronds = [Rgb(0.46f, 0.50f, 0.18f), Rgb(0.56f, 0.52f, 0.18f)];
                break;
        }
        Vector3[] reef = [Rgb(0.14f, 0.40f, 0.46f), Rgb(0.12f, 0.34f, 0.42f), Rgb(0.20f, 0.46f, 0.50f), Rgb(0.30f, 0.34f, 0.38f)];
        Vector3[] water = [Rgb(0.80f, 0.92f, 0.95f), Rgb(0.84f, 0.94f, 0.96f), Rgb(0.77f, 0.90f, 0.94f)];
        Vector3 sand = Rgb(0.95f, 0.89f, 0.74f), stone = Rgb(0.92f, 0.90f, 0.85f), earth = Rgb(0.56f, 0.42f, 0.28f);
        Slab(Rgb(0.80f, 0.92f, 0.95f), Rgb(0.32f, 0.58f, 0.72f));
        float size = n, radius = size * 0.40f, peak = size * 0.16f;
        var wobble = Outline();
        var heights = new float[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var p = Position(r, c);
                float d = MathF.Sqrt(p.X * p.X + p.Z * p.Z), a = MathF.Atan2(p.Z, p.X);
                float reach = radius * wobble(a);
                if (d < reach)
                {
                    float t = d / reach, h = 0.35f + peak * MathF.Pow(1 - t, 1.4f);
                    heights[r, c] = h;
                    if (dark[r][c])
                    {
                        var top = Vary(Pick(green));
                        Model.Ground.Add(new QrVoxel(p.X, p.Z, 0, h, 1, top, Side: Vary(earth, 0.08f)));
                        float bush = Random() > 0.35f ? 0.3f + Random() * 0.7f : 0.12f;
                        Model.Cover.Add(new QrVoxel(p.X, p.Z, h, h + bush, 0.94f, Vary(Pick(green))));
                    }
                    else
                    {
                        Model.Ground.Add(new QrVoxel(p.X, p.Z, 0, h, 1, Vary(h < 0.35f + peak * 0.3f ? sand : stone, 0.03f)));
                    }
                }
                else if (dark[r][c])
                {
                    float y1 = 0.18f + Random() * 0.35f;
                    Model.Cover.Add(new QrVoxel(p.X, p.Z, 0, y1, 0.94f, Vary(Pick(reef))));
                }
                else
                {
                    float y1 = 0.04f + Random() * 0.05f;
                    Model.Ground.Add(new QrVoxel(p.X, p.Z, 0, y1, 0.96f, Pick(water)));
                }
            }
        // Palms: fronds lie over dark modules only; trunks and coconuts fade away from above.
        var trunk = Rgb(0.58f, 0.44f, 0.28f);
        (float X, float Z, float Height, float Reach)[] palms =
        [
            (-size * 0.04f, size * 0.02f, size * 0.42f, size * 0.20f),
            (size * 0.18f, -size * 0.12f, size * 0.28f, size * 0.13f),
            (-size * 0.16f, -size * 0.18f, size * 0.22f, size * 0.11f),
        ];
        foreach (var palm in palms)
        {
            int groundRow = Math.Min(n - 1, Math.Max(0, (int)Round(palm.Z + half)));
            int groundColumn = Math.Min(n - 1, Math.Max(0, (int)Round(palm.X + half)));
            var foot = new Vector3(palm.X, heights[groundRow, groundColumn], palm.Z);
            float lean = Random() * 6.28f;
            var crown = foot + new Vector3(MathF.Cos(lean) * palm.Height * 0.18f, palm.Height, MathF.Sin(lean) * palm.Height * 0.18f);
            var mid = (foot + crown) / 2 + new Vector3(MathF.Cos(lean) * 0.6f, 0, MathF.Sin(lean) * 0.6f);
            Line(foot, mid, t => 0.75f - 0.1f * t, trunk, 0.3f);
            Line(mid, crown, t => 0.65f - 0.15f * t, trunk, 0.3f);
            for (int k = 0; k < 3; k++)
            {
                float a = k * 2.1f;
                Model.Structure.Add(new QrVoxel(crown.X + MathF.Cos(a) * 0.45f, crown.Z + MathF.Sin(a) * 0.45f, crown.Y - 0.9f, crown.Y - 0.35f,
                                                0.5f, Rgb(0.42f, 0.30f, 0.16f)));
            }
            int arms = 7;
            float turn = Random() * 6.28f, sector = 6.28f / arms;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    var p = Position(r, c);
                    float dx = p.X - crown.X, dz = p.Z - crown.Z, d = Hypot(dx, dz);
                    if (d >= palm.Reach) continue;
                    float off = MathF.Atan2(dz, dx) - turn;
                    off -= Round(off / sector) * sector;
                    if (!(d < 1.6f || MathF.Abs(off) * d < 1.1f)) continue;
                    float y = crown.Y + 0.5f - 2.6f * MathF.Pow(d / palm.Reach, 2) * palm.Height / (size * 0.42f);
                    if (!(y > heights[r, c] + 1.2f)) continue;
                    var frond = new QrVoxel(p.X, p.Z, y - 0.45f, y, 0.94f, Vary(Pick(fronds)));
                    (dark[r][c] ? Model.Cover : Model.Structure).Add(frond);
                }
        }
        // A small boat on the water.
        float boatX = size * 0.36f, boatZ = size * 0.42f;
        for (int i = 0; i < 4; i++)
            Model.Structure.Add(new QrVoxel(boatX - 1.2f + i * 0.8f, boatZ, 0.1f, 0.6f, 0.8f, Rgb(0.70f, 0.40f, 0.26f)));
        Line(new Vector3(boatX, 0.6f, boatZ), new Vector3(boatX, 3.4f, boatZ), _ => 0.18f, Rgb(0.40f, 0.28f, 0.18f));
        for (int k = 0; k < 5; k++)
        {
            float w = 1.8f - k * 0.32f;
            Model.Structure.Add(new QrVoxel(boatX - w / 2 - 0.15f, boatZ, 1.0f + k * 0.5f, 1.5f + k * 0.5f, w, Rgb(0.98f, 0.97f, 0.94f), 0.12f));
        }
    }
}
