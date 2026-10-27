namespace Swivel;

// Locating QR symbols in a binarised image. Finder patterns are found as 1:1:3:1:1 dark/light runs along rows,
// confirmed along the column and a diagonal through the same centre (the ratio holds along any line through a
// finder, so this works at any rotation). Every plausible trio of finders (a right angle with similar legs) is
// tried: the module size is measured along the trio's own axes, the size is estimated from the finder distances,
// the bottom-right alignment pattern is searched near its predicted place, and the grid is sampled through a
// projective transform. The version information and both read orientations (mirror images) are checked.

sealed class QrFinder(double x, double y, double size)
{
    public double X = x, Y = y, Size = size;
    public int Count = 1;
}

/// A decoded symbol in the coordinates of the image it was found in.
readonly record struct QrHit(string Text, double MinX, double MinY, double MaxX, double MaxY);

static class QrDetector
{
    const int MaxFinders = 40, MaxAttempts = 250;

    /// Every QR symbol found in the image, reading dark-on-light and (when `inverted`) light-on-dark.
    public static List<QrHit> Detect(QrBinary image, bool inverted = true)
    {
        var hits = new List<QrHit>();
        foreach (var img in inverted ? new[] { image, image.Inverted() } : [image])
        {
            var finders = FindFinders(img);
            if (finders.Count >= 3) DecodeTrios(img, finders, hits);
        }
        return hits;
    }

    // MARK: Finder patterns

    public static List<QrFinder> FindFinders(QrBinary img)
    {
        var list = new List<QrFinder>();
        int w = img.Width, h = img.Height;
        var bits = img.Bits;
        byte flip = img.Flip;
        int skip = h >= 1400 ? 2 : 1;
        Span<int> c = stackalloc int[5];
        for (int y = 0; y < h; y += skip)
        {
            c.Clear();
            int state = 0, row = y * w;
            for (int x = 0; x < w; x++)
            {
                if ((bits[row + x] ^ flip) != 0)
                {
                    if ((state & 1) == 1) state++;
                    c[state]++;
                }
                else if ((state & 1) == 0)
                {
                    if (state == 4)
                    {
                        if (RatioOk(c, 0.5)) TryCenter(img, c, y, x, list);
                        c[0] = c[2]; c[1] = c[3]; c[2] = c[4]; c[3] = 1; c[4] = 0;
                        state = 3;
                    }
                    else c[++state]++;
                }
                else c[state]++;
            }
            if (state == 4 && RatioOk(c, 0.5)) TryCenter(img, c, y, w, list);
        }
        return list;
    }

    static bool RatioOk(ReadOnlySpan<int> c, double tolerance)
    {
        int total = 0;
        for (int i = 0; i < 5; i++)
        {
            if (c[i] == 0) return false;
            total += c[i];
        }
        if (total < 7) return false;
        double module = total / 7.0, max = module * tolerance;
        return Math.Abs(module - c[0]) < max && Math.Abs(module - c[1]) < max && Math.Abs(3 * module - c[2]) < 3 * max
            && Math.Abs(module - c[3]) < max && Math.Abs(module - c[4]) < max;
    }

    static void TryCenter(QrBinary img, ReadOnlySpan<int> c, int y, int endX, List<QrFinder> list)
    {
        int total = c[0] + c[1] + c[2] + c[3] + c[4];
        double cx = endX - c[4] - c[3] - c[2] / 2.0;
        double cy = CrossCheck(img, (int)cx, y, 0, 1, c[2], total);
        if (double.IsNaN(cy)) return;
        cx = CrossCheck(img, (int)cx, (int)cy, 1, 0, c[2], total);
        if (double.IsNaN(cx)) return;
        if (!DiagonalOk(img, (int)cx, (int)cy, c[2])) return;
        double module = total / 7.0;
        foreach (var f in list)
        {
            if (Math.Abs(f.Y - cy) > module || Math.Abs(f.X - cx) > module) continue;
            double diff = Math.Abs(module - f.Size);
            if (diff > 1 && diff > f.Size) continue;
            int n = f.Count;
            f.X = (f.X * n + cx) / (n + 1);
            f.Y = (f.Y * n + cy) / (n + 1);
            f.Size = (f.Size * n + module) / (n + 1);
            f.Count = n + 1;
            return;
        }
        list.Add(new QrFinder(cx, cy, module));
    }

    /// Counts the five runs through (x, y) along (dx, dy) and returns the centre coordinate along that axis, or NaN.
    static double CrossCheck(QrBinary img, int x, int y, int dx, int dy, int maxCount, int originalTotal)
    {
        int w = img.Width, h = img.Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return double.NaN;
        Span<int> c = stackalloc int[5];
        int limit = maxCount * 2 + 2;
        int px = x, py = y;
        bool Inside(int ax, int ay) => ax >= 0 && ay >= 0 && ax < w && ay < h;
        while (Inside(px, py) && img.Dark(px, py)) { c[2]++; px -= dx; py -= dy; }
        if (!Inside(px, py)) return double.NaN;
        while (Inside(px, py) && !img.Dark(px, py) && c[1] <= limit) { c[1]++; px -= dx; py -= dy; }
        if (!Inside(px, py) || c[1] > limit) return double.NaN;
        while (Inside(px, py) && img.Dark(px, py) && c[0] <= limit) { c[0]++; px -= dx; py -= dy; }
        if (c[0] > limit) return double.NaN;
        px = x + dx; py = y + dy;
        while (Inside(px, py) && img.Dark(px, py)) { c[2]++; px += dx; py += dy; }
        if (!Inside(px, py)) return double.NaN;
        while (Inside(px, py) && !img.Dark(px, py) && c[3] <= limit) { c[3]++; px += dx; py += dy; }
        if (!Inside(px, py) || c[3] > limit) return double.NaN;
        while (Inside(px, py) && img.Dark(px, py) && c[4] <= limit) { c[4]++; px += dx; py += dy; }
        if (c[4] > limit) return double.NaN;
        int total = c[0] + c[1] + c[2] + c[3] + c[4];
        if (5 * Math.Abs(total - originalTotal) >= 2 * originalTotal) return double.NaN;
        if (!RatioOk(c, 0.5)) return double.NaN;
        int end = dx != 0 ? px : py;
        return end - c[4] - c[3] - c[2] / 2.0;
    }

    /// The 1:1:3:1:1 ratio along the down-right diagonal, with a looser tolerance.
    static bool DiagonalOk(QrBinary img, int x, int y, int maxCount)
    {
        int w = img.Width, h = img.Height, limit = maxCount * 2 + 2;
        Span<int> c = stackalloc int[5];
        int i = 0;
        while (x - i >= 0 && y - i >= 0 && img.Dark(x - i, y - i)) { c[2]++; i++; }
        if (x - i < 0 || y - i < 0) return false;
        while (x - i >= 0 && y - i >= 0 && !img.Dark(x - i, y - i) && c[1] <= limit) { c[1]++; i++; }
        if (x - i < 0 || y - i < 0 || c[1] > limit) return false;
        while (x - i >= 0 && y - i >= 0 && img.Dark(x - i, y - i) && c[0] <= limit) { c[0]++; i++; }
        if (c[0] > limit) return false;
        i = 1;
        while (x + i < w && y + i < h && img.Dark(x + i, y + i)) { c[2]++; i++; }
        if (x + i >= w || y + i >= h) return false;
        while (x + i < w && y + i < h && !img.Dark(x + i, y + i) && c[3] <= limit) { c[3]++; i++; }
        if (x + i >= w || y + i >= h || c[3] > limit) return false;
        while (x + i < w && y + i < h && img.Dark(x + i, y + i) && c[4] <= limit) { c[4]++; i++; }
        if (c[4] > limit) return false;
        return RatioOk(c, 0.75);
    }

    // MARK: Trios

    readonly record struct Trio(QrFinder TopLeft, QrFinder TopRight, QrFinder BottomLeft, double Score);

    static void DecodeTrios(QrBinary img, List<QrFinder> all, List<QrHit> hits)
    {
        var finders = all.OrderByDescending(f => f.Count).Take(MaxFinders).ToList();
        var trios = new List<Trio>();
        for (int i = 0; i < finders.Count; i++)
            for (int j = i + 1; j < finders.Count; j++)
                for (int k = j + 1; k < finders.Count; k++)
                    if (MakeTrio(finders[i], finders[j], finders[k]) is { } trio) trios.Add(trio);
        trios.Sort((p, q) => p.Score.CompareTo(q.Score));
        var used = new HashSet<QrFinder>();
        int attempts = 0;
        foreach (var trio in trios)
        {
            if (used.Contains(trio.TopLeft) || used.Contains(trio.TopRight) || used.Contains(trio.BottomLeft)) continue;
            if (++attempts > MaxAttempts) break;
            if (TryDecode(img, trio) is { } hit)
            {
                hits.Add(hit);
                used.Add(trio.TopLeft); used.Add(trio.TopRight); used.Add(trio.BottomLeft);
            }
        }
    }

    static Trio? MakeTrio(QrFinder a, QrFinder b, QrFinder c)
    {
        double minSize = Math.Min(a.Size, Math.Min(b.Size, c.Size)), maxSize = Math.Max(a.Size, Math.Max(b.Size, c.Size));
        if (maxSize > minSize * 2.6) return null;  // sizes are measured along rows; perspective stretches them
        double ab = Dist(a, b), bc = Dist(b, c), ac = Dist(a, c);
        // The corner finder (top left) is opposite the longest side.
        QrFinder corner, p, q;
        if (bc >= ab && bc >= ac) (corner, p, q) = (a, b, c);
        else if (ac >= ab && ac >= bc) (corner, p, q) = (b, a, c);
        else (corner, p, q) = (c, a, b);
        double l1 = Dist(corner, p), l2 = Dist(corner, q);
        if (l1 < 1 || l2 < 1) return null;
        double legRatio = Math.Max(l1, l2) / Math.Min(l1, l2);
        if (legRatio > 2.2) return null;
        double cos = ((p.X - corner.X) * (q.X - corner.X) + (p.Y - corner.Y) * (q.Y - corner.Y)) / (l1 * l2);
        if (Math.Abs(cos) > 0.5) return null;
        double size = (a.Size + b.Size + c.Size) / 3;
        // Finder sizes are measured along rows; a rotated symbol measures up to √2 larger, so allow a wide range.
        double modules = (l1 + l2) / 2 / size + 7;
        if (modules < 21 / 1.6 || modules > 177 * 1.2) return null;
        // In image coordinates (y down) the top-right finder is clockwise from the top-left's view.
        double cross = (p.X - corner.X) * (q.Y - corner.Y) - (p.Y - corner.Y) * (q.X - corner.X);
        var (topRight, bottomLeft) = cross > 0 ? (p, q) : (q, p);
        double score = Math.Abs(cos) + (legRatio - 1) + (maxSize / minSize - 1) * 0.5;
        return new Trio(corner, topRight, bottomLeft, score);
    }

    static double Dist(QrFinder a, QrFinder b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // MARK: Sampling

    static QrHit? TryDecode(QrBinary img, Trio trio)
    {
        var (tl, tr, bl) = (trio.TopLeft, trio.TopRight, trio.BottomLeft);
        double moduleTop = (FinderWidth(img, tl, tr) + FinderWidth(img, tr, tl)) / 14;
        double moduleLeft = (FinderWidth(img, tl, bl) + FinderWidth(img, bl, tl)) / 14;
        double fallback = (tl.Size + tr.Size + bl.Size) / 3;
        if (double.IsNaN(moduleTop) || moduleTop < 0.5) moduleTop = double.IsNaN(moduleLeft) ? fallback : moduleLeft;
        if (double.IsNaN(moduleLeft) || moduleLeft < 0.5) moduleLeft = moduleTop;
        double modules = (Dist(tl, tr) / moduleTop + Dist(tl, bl) / moduleLeft) / 2;
        int estimate = (int)Math.Round(modules) + 7;
        switch (estimate & 3)
        {
            case 0: estimate++; break;
            case 2: estimate--; break;
            case 3: estimate += 2; break;
        }
        double module = (moduleTop + moduleLeft) / 2;

        var candidates = new[] { estimate, estimate + 4, estimate - 4, estimate + 8, estimate - 8 };
        if (!Plausible(img, tl, tr, bl, PitchAt(img, tl, tr, moduleTop), PitchAt(img, tl, bl, moduleLeft))) return null;

        var queue = new List<int>(candidates);
        var tried = new HashSet<int>();
        for (int qi = 0; qi < queue.Count; qi++)
        {
            int size = queue[qi];
            if (size < 21 || size > 177 || !tried.Add(size)) continue;
            int version = (size - 17) / 4;
            var transforms = new List<QrPerspective>(4);
            if (version >= 2)
                foreach (var (ax, ay) in FindAlignments(img, tl, tr, bl, size, module))
                    transforms.Add(QrPerspective.QuadToQuad(
                        3.5, 3.5, size - 3.5, 3.5, size - 6.5, size - 6.5, 3.5, size - 3.5,
                        tl.X, tl.Y, tr.X, tr.Y, ax, ay, bl.X, bl.Y));
            transforms.Add(Affine(tl, tr, bl, size));
            foreach (var transform in transforms)
            {
                var grid = Sample(img, transform, size);
                if (grid is null) continue;
                var text = QrMatrixDecoder.Decode(grid, size, out int hint);
                if (text is not null) return Bounds(text, transform, size, img);
                if (hint != 0 && !queue.Contains(QrTables.Size(hint))) queue.Insert(qi + 1, QrTables.Size(hint));
            }
        }
        return null;
    }

    /// The module pitch at `from` along the line to `to` (its finder is 7 modules wide), or `fallback`.
    static double PitchAt(QrBinary img, QrFinder from, QrFinder to, double fallback)
    {
        double width = FinderWidth(img, from, to) / 7;
        return double.IsNaN(width) || width < 0.5 || width > fallback * 2 || width < fallback / 2 ? fallback : width;
    }

    /// Module space → image through the three finders, the fourth corner completing the parallelogram.
    static QrPerspective Affine(QrFinder tl, QrFinder tr, QrFinder bl, int size) => QrPerspective.QuadToQuad(
        3.5, 3.5, size - 3.5, 3.5, size - 3.5, size - 3.5, 3.5, size - 3.5,
        tl.X, tl.Y, tr.X, tr.Y, tr.X + bl.X - tl.X, tr.Y + bl.Y - tl.Y, bl.X, bl.Y);

    /// A quick test before the expensive search: around the top-left finder the timing patterns alternate and the
    /// first copy of the format information reads (either way round). Uses a local model at that finder with the
    /// module pitch measured there, which stays accurate under strong perspective and doesn't depend on the size.
    static bool Plausible(QrBinary img, QrFinder tl, QrFinder tr, QrFinder bl, double pitchRight, double pitchDown)
    {
        double rx = tr.X - tl.X, ry = tr.Y - tl.Y, rl = Math.Sqrt(rx * rx + ry * ry);
        double dx = bl.X - tl.X, dy = bl.Y - tl.Y, dl = Math.Sqrt(dx * dx + dy * dy);
        rx *= pitchRight / rl; ry *= pitchRight / rl; dx *= pitchDown / dl; dy *= pitchDown / dl;
        bool? Module(int x, int y)
        {
            double px = tl.X + (x - 3) * rx + (y - 3) * dx, py = tl.Y + (x - 3) * ry + (y - 3) * dy;
            if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) return null;
            return img.Dark((int)px, (int)py);
        }
        int good = 0, total = 0;
        for (int i = 8; i <= 12; i++)
        {
            if (Module(i, 6) is bool a) { total++; if (a == (i % 2 == 0)) good++; }
            if (Module(6, i) is bool b) { total++; if (b == (i % 2 == 0)) good++; }
        }
        if (total < 8 || good < total * 0.8) return false;
        // Copy 1 of the format information: bits 0–5 down column 8, 6–8 around the corner, 9–14 along row 8.
        foreach (bool mirrored in new[] { false, true })
        {
            bool At(int x, int y) => (mirrored ? Module(y, x) : Module(x, y)) == true;
            int bits = 0;
            for (int i = 0; i <= 5; i++) if (At(8, i)) bits |= 1 << i;
            if (At(8, 7)) bits |= 1 << 6;
            if (At(8, 8)) bits |= 1 << 7;
            if (At(7, 8)) bits |= 1 << 8;
            for (int i = 9; i < 15; i++) if (At(14 - i, 8)) bits |= 1 << i;
            if (QrMatrixDecoder.FormatDistance(bits) <= 3) return true;
        }
        return false;
    }

    /// The width of the finder at `from` (7 modules) measured on the line towards `to`, in pixels, or NaN.
    static double FinderWidth(QrBinary img, QrFinder from, QrFinder to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return double.NaN;
        dx /= length; dy /= length;
        double forward = EdgeDistance(img, from.X, from.Y, dx, dy, length);
        double backward = EdgeDistance(img, from.X, from.Y, -dx, -dy, length);
        if (double.IsNaN(forward)) return backward * 2;
        if (double.IsNaN(backward)) return forward * 2;
        return forward + backward;
    }

    /// Distance from the finder centre to its outer edge (after dark → light → dark → light), or NaN.
    static double EdgeDistance(QrBinary img, double x, double y, double dx, double dy, double maxLength)
    {
        int transitions = 0;
        bool previous = true;
        for (double t = 0; t < maxLength; t += 0.5)
        {
            int px = (int)(x + dx * t), py = (int)(y + dy * t);
            if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) return double.NaN;
            bool dark = img.Dark(px, py);
            if (dark == previous) continue;
            previous = dark;
            if (++transitions == 3) return t;
        }
        return double.NaN;
    }

    /// Candidate centres of the bottom-right alignment pattern, nearest to its predicted place first, up to 16
    /// modules away: under strong perspective the prediction (from the three finders) is far off, and another
    /// alignment pattern may be nearer to it.
    static List<(double X, double Y)> FindAlignments(QrBinary img, QrFinder tl, QrFinder tr, QrFinder bl, int size, double module)
    {
        double factor = (size - 10.0) / (size - 7.0);
        double ex = tl.X + factor * (tr.X - tl.X + bl.X - tl.X), ey = tl.Y + factor * (tr.Y - tl.Y + bl.Y - tl.Y);
        var (ux, uy, vx, vy) = ((tr.X - tl.X) / (size - 7), (tr.Y - tl.Y) / (size - 7), (bl.X - tl.X) / (size - 7), (bl.Y - tl.Y) / (size - 7));
        return SearchAlignment(img, ex, ey, module, 16, (ux, uy, vx, vy)).Take(6).ToList();
    }

    static List<(double X, double Y)> SearchAlignment(QrBinary img, double ex, double ey, double module, int allowance,
                                                       (double Ux, double Uy, double Vx, double Vy) axes)
    {
        var found = new List<(double X, double Y, double D)>();
        int radius = (int)Math.Ceiling(allowance * module);
        int x0 = Math.Max(0, (int)ex - radius), x1 = Math.Min(img.Width - 1, (int)ex + radius);
        int y0 = Math.Max(0, (int)ey - radius), y1 = Math.Min(img.Height - 1, (int)ey + radius);
        if (x1 - x0 < module * 3 || y1 - y0 < module * 3) return [];
        var starts = new List<int>();
        var lengths = new List<int>();
        var darks = new List<bool>();
        for (int y = y0; y <= y1; y++)
        {
            starts.Clear(); lengths.Clear(); darks.Clear();
            for (int x = x0; x <= x1; x++)
            {
                bool dark = img.Dark(x, y);
                if (darks.Count > 0 && darks[^1] == dark) lengths[^1]++;
                else { starts.Add(x); lengths.Add(1); darks.Add(dark); }
            }
            // dark | light | dark (centre) | light | dark
            for (int k = 1; k + 3 < darks.Count; k++)
            {
                if (darks[k] || !darks[k + 1] || darks[k + 2] || !darks[k - 1] || !darks[k + 3]) continue;
                if (!AlignmentRuns(lengths[k], lengths[k + 1], lengths[k + 2], module)) continue;
                double cx = starts[k + 1] + lengths[k + 1] / 2.0;
                if (AlignmentCross(img, (int)cx, y, 0, 1, module) is not var (cy, down)) continue;
                if (AlignmentCross(img, (int)cx, (int)cy, 1, 0, module) is not var (cx2, across)) continue;
                double local = Math.Max(across, down);
                if (!AlignmentRings(img, cx2, cy, axes) && !AlignmentEllipses(img, cx2, cy, across, down)) continue;
                if (found.Any(f => Math.Abs(f.X - cx2) < local && Math.Abs(f.Y - cy) < local)) continue;
                found.Add((cx2, cy, (cx2 - ex) * (cx2 - ex) + (cy - ey) * (cy - ey)));
            }
        }
        return found.OrderBy(f => f.D).Select(f => (f.X, f.Y)).ToList();
    }

    /// The 5 × 5 pattern in the symbol's own frame (module axes from the finders): the eight modules around the
    /// centre light, the sixteen of the outer ring dark. Tried at several scales per axis, since under perspective
    /// the local module size differs from the average, and differently along each axis.
    static readonly double[] Scales = [1.0, 0.8, 1.25, 0.65, 1.6];

    static bool AlignmentRings(QrBinary img, double cx, double cy, (double Ux, double Uy, double Vx, double Vy) axes)
    {
        foreach (double su in Scales)
        foreach (double sv in Scales)
        {
            int light = 0, dark = 0;
            bool inside = true;
            for (int j = -2; j <= 2 && inside; j++)
                for (int i = -2; i <= 2; i++)
                {
                    int ring = Math.Max(Math.Abs(i), Math.Abs(j));
                    if (ring == 0) continue;
                    double px = cx + su * i * axes.Ux + sv * j * axes.Vx, py = cy + su * i * axes.Uy + sv * j * axes.Vy;
                    if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) { inside = false; break; }
                    bool d = img.Dark((int)px, (int)py);
                    if (ring == 1 && !d) light++;
                    if (ring == 2 && d) dark++;
                }
            if (inside && light >= 7 && dark >= 14) return true;
        }
        return false;
    }

    /// The same check on circles (ellipses with the module sizes measured along the row and the column): a radius
    /// of 1 module lies in the light ring and 2.2 modules in the dark ring at any angle when the symbol is upright,
    /// which holds up under strong perspective where the finders' frame is skewed at this corner.
    static bool AlignmentEllipses(QrBinary img, double cx, double cy, double moduleX, double moduleY)
    {
        int light = 0, dark = 0;
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6, c = Math.Cos(a), s = Math.Sin(a);
            int ix = (int)(cx + c * moduleX), iy = (int)(cy + s * moduleY);
            int ox = (int)(cx + c * moduleX * 2.2), oy = (int)(cy + s * moduleY * 2.2);
            if (ox < 0 || oy < 0 || ox >= img.Width || oy >= img.Height) return false;
            if (!img.Dark(ix, iy)) light++;
            if (img.Dark(ox, oy)) dark++;
        }
        return light >= 10 && dark >= 9;
    }

    static bool AlignmentRuns(int a, int b, int c, double module)
    {
        double mean = (a + b + c) / 3.0;
        if (mean < module * 0.5 || mean > module * 2.2) return false;
        return Math.Max(a, Math.Max(b, c)) <= Math.Min(a, Math.Min(b, c)) * 2.2 + 1;
    }

    /// Along (dx, dy) through (x, y): a dark centre run with light runs on both sides and dark beyond. The centre
    /// coordinate along that axis and the mean of the three runs (the module size there), or null.
    static (double Centre, double Module)? AlignmentCross(QrBinary img, int x, int y, int dx, int dy, double module)
    {
        int w = img.Width, h = img.Height, limit = (int)(module * 2.5) + 2;
        bool Inside(int ax, int ay) => ax >= 0 && ay >= 0 && ax < w && ay < h;
        if (!Inside(x, y) || !img.Dark(x, y)) return null;
        int px = x, py = y, centre = 0, before = 0, after = 0;
        while (Inside(px, py) && img.Dark(px, py) && centre <= limit) { centre++; px -= dx; py -= dy; }
        int start = (dx != 0 ? px : py) + 1;
        while (Inside(px, py) && !img.Dark(px, py) && before <= limit) { before++; px -= dx; py -= dy; }
        if (!Inside(px, py) || before > limit) return null;
        px = x + dx; py = y + dy;
        while (Inside(px, py) && img.Dark(px, py) && centre <= limit) { centre++; px += dx; py += dy; }
        int end = dx != 0 ? px : py;
        while (Inside(px, py) && !img.Dark(px, py) && after <= limit) { after++; px += dx; py += dy; }
        if (!Inside(px, py) || after > limit || centre > limit) return null;
        if (!AlignmentRuns(before, centre, after, module)) return null;
        return ((start + end) / 2.0, (before + centre + after) / 3.0);
    }

    static bool[]? Sample(QrBinary img, QrPerspective transform, int size)
    {
        var grid = new bool[size * size];
        int w = img.Width, h = img.Height;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (px, py) = transform.Map(x + 0.5, y + 0.5);
                if (double.IsNaN(px) || double.IsNaN(py) || px < -2 || py < -2 || px > w + 1 || py > h + 1) return null;
                int ix = Math.Clamp((int)Math.Floor(px), 0, w - 1), iy = Math.Clamp((int)Math.Floor(py), 0, h - 1);
                grid[y * size + x] = img.Dark(ix, iy);
            }
        return grid;
    }

    static QrHit Bounds(string text, QrPerspective transform, int size, QrBinary img)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (u, v) in new[] { (0.0, 0.0), (size, 0.0), (size, size), (0.0, (double)size) })
        {
            var (x, y) = transform.Map(u, v);
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return new QrHit(text, Math.Max(0, minX), Math.Max(0, minY), Math.Min(img.Width, maxX), Math.Min(img.Height, maxY));
    }
}
