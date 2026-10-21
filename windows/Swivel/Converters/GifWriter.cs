namespace Swivel;

/// Swivel's own GIF encoder (WIC's GIF encoder maps everything to one fixed halftone palette): every frame gets its
/// own 256-colour palette by median cut over a 15-bit histogram, then LZW. Stills are Floyd–Steinberg dithered.
/// Animation frames use a fixed 8 × 8 ordered dither instead (error diffusion crawls from frame to frame), applied
/// only where the palette has no close colour, so flat areas such as screen recordings stay clean.
sealed class GifWriter
{
    readonly Stream stream;
    readonly int width, height;
    bool finished;

    public GifWriter(Stream stream, int width, int height, bool loopForever)
    {
        this.stream = stream;
        this.width = width; this.height = height;
        Write("GIF89a"u8);
        WriteShort(width); WriteShort(height);
        stream.WriteByte(0x70);  // no global colour table, 8-bit colour resolution
        stream.WriteByte(0);     // background index
        stream.WriteByte(0);     // square pixels
        if (loopForever)
        {
            Write([0x21, 0xFF, 0x0B]);
            Write("NETSCAPE2.0"u8);
            Write([0x03, 0x01, 0x00, 0x00, 0x00]);  // loop count 0 = forever
        }
    }

    public static byte[] EncodeStill(Raster image)
    {
        using var output = new MemoryStream();
        var writer = new GifWriter(output, image.Width, image.Height, loopForever: false);
        writer.AddFrame(image, 0, dither: true);
        writer.Finish();
        return output.ToArray();
    }

    /// Adds a frame the size of the canvas. `delay` is in hundredths of a second.
    public void AddFrame(Raster frame, int delay, bool dither)
    {
        if (frame.Width != width || frame.Height != height) frame = frame.Resized(width, height);
        bool transparent = frame.Alpha;
        var palette = MedianCut(frame.Data, transparent ? 255 : 256);
        int transparentIndex = transparent ? palette.Length : -1;
        int colors = palette.Length + (transparent ? 1 : 0);
        int tableBits = 1;
        while ((1 << tableBits) < colors) tableBits++;
        var indices = dither ? MapDithered(frame, palette, transparentIndex) : MapOrdered(frame, palette, transparentIndex);

        // Graphic control extension: disposal "do not dispose", delay, transparency.
        Write([0x21, 0xF9, 0x04, (byte)((1 << 2) | (transparent ? 1 : 0))]);
        WriteShort(Math.Clamp(delay, 0, 65535));
        stream.WriteByte((byte)Math.Max(transparentIndex, 0));
        stream.WriteByte(0);

        // Image descriptor with a local colour table.
        stream.WriteByte(0x2C);
        WriteShort(0); WriteShort(0); WriteShort(width); WriteShort(height);
        stream.WriteByte((byte)(0x80 | (tableBits - 1)));
        var table = new byte[(1 << tableBits) * 3];
        for (int i = 0; i < palette.Length; i++)
        {
            int c = palette[i];
            table[i * 3] = (byte)(c >> 16); table[i * 3 + 1] = (byte)(c >> 8); table[i * 3 + 2] = (byte)c;
        }
        Write(table);

        int minCodeSize = Math.Max(2, tableBits);
        stream.WriteByte((byte)minCodeSize);
        var data = Lzw(indices, minCodeSize);
        for (int offset = 0; offset < data.Length; offset += 255)
        {
            int n = Math.Min(255, data.Length - offset);
            stream.WriteByte((byte)n);
            stream.Write(data, offset, n);
        }
        stream.WriteByte(0);
    }

    public void Finish()
    {
        if (finished) return;
        finished = true;
        stream.WriteByte(0x3B);
        stream.Flush();
    }

    void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
    void WriteShort(int v) { stream.WriteByte((byte)v); stream.WriteByte((byte)(v >> 8)); }

    // MARK: Palette

    /// Median cut: boxes of 15-bit colours split at the population median along their widest channel. Returns
    /// 0xRRGGBB colours, each the population-weighted mean of its box (from full-precision sums).
    internal static int[] MedianCut(int[] pixels, int maxColors)
    {
        var count = new int[32768];
        var sumR = new long[32768]; var sumG = new long[32768]; var sumB = new long[32768];
        foreach (var p in pixels)
        {
            if ((uint)p < 0x80000000u) continue;  // alpha < 128: transparent, not in the palette
            int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            count[key]++; sumR[key] += r; sumG[key] += g; sumB[key] += b;
        }
        var bins = new List<int>();
        for (int i = 0; i < count.Length; i++) if (count[i] > 0) bins.Add(i);
        if (bins.Count == 0) return [0];
        var keys = bins.ToArray();

        if (keys.Length <= maxColors)
            return Enumerable.Range(0, keys.Length).Select(i => Mean(i, i + 1)).ToArray();

        var boxes = new List<(int Start, int End)> { (0, keys.Length) };
        while (boxes.Count < maxColors)
        {
            int best = -1; double bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var (s, e) = boxes[i];
                if (e - s < 2) continue;
                var (range, _) = Widest(s, e);
                long population = 0;
                for (int k = s; k < e; k++) population += count[keys[k]];
                double score = population * (double)(range + 1);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;
            var (start, end) = boxes[best];
            var (_, axis) = Widest(start, end);
            int shift = axis == 0 ? 10 : axis == 1 ? 5 : 0;
            Array.Sort(keys, start, end - start, Comparer<int>.Create((a, b) =>
            {
                int c = ((a >> shift) & 31).CompareTo((b >> shift) & 31);
                return c != 0 ? c : a.CompareTo(b);
            }));
            long total = 0;
            for (int k = start; k < end; k++) total += count[keys[k]];
            long half = total / 2, running = 0;
            int split = start + 1;
            for (int k = start; k < end - 1; k++)
            {
                running += count[keys[k]];
                split = k + 1;
                if (running >= half) break;
            }
            boxes[best] = (start, split);
            boxes.Add((split, end));
        }
        return boxes.Select(b => Mean(b.Start, b.End)).ToArray();

        (int Range, int Axis) Widest(int s, int e)
        {
            int minR = 31, maxR = 0, minG = 31, maxG = 0, minB = 31, maxB = 0;
            for (int k = s; k < e; k++)
            {
                int key = keys[k];
                int r = key >> 10, g = (key >> 5) & 31, b = key & 31;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            int dr = maxR - minR, dg = maxG - minG, db = maxB - minB;
            // Green is weighted a little higher: the eye resolves it best.
            if (dg * 1.2 >= dr && dg * 1.2 >= db) return (dg, 1);
            return dr >= db ? (dr, 0) : (db, 2);
        }

        int Mean(int s, int e)
        {
            long n = 0, r = 0, g = 0, b = 0;
            for (int k = s; k < e; k++)
            {
                int key = keys[k];
                n += count[key]; r += sumR[key]; g += sumG[key]; b += sumB[key];
            }
            if (n == 0) return 0;
            return (int)((r + n / 2) / n) << 16 | (int)((g + n / 2) / n) << 8 | (int)((b + n / 2) / n);
        }
    }

    sealed class Nearest(int[] palette)
    {
        // 6 bits per channel cache of the nearest palette entry.
        readonly short[] cache = Enumerable.Repeat((short)-1, 1 << 18).ToArray();

        public int Find(int r, int g, int b)
        {
            int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
            int hit = cache[key];
            if (hit >= 0) return hit;
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int c = palette[i];
                int dr = ((c >> 16) & 0xFF) - r, dg = ((c >> 8) & 0xFF) - g, db = (c & 0xFF) - b;
                int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                if (d < bestDistance) { bestDistance = d; best = i; if (d == 0) break; }
            }
            cache[key] = (short)best;
            return best;
        }
    }

    static readonly int[] Bayer8 =
    [
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    static byte[] MapOrdered(Raster frame, int[] palette, int transparentIndex)
    {
        const int spread = 24, exact = 6;
        var nearest = new Nearest(palette);
        var result = new byte[frame.Data.Length];
        int w = frame.Width;
        for (int i = 0; i < frame.Data.Length; i++)
        {
            int p = frame.Data[i];
            if (transparentIndex >= 0 && (uint)p < 0x80000000u) { result[i] = (byte)transparentIndex; continue; }
            int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            int index = nearest.Find(r, g, b);
            int c = palette[index];
            if (Math.Abs(((c >> 16) & 0xFF) - r) + Math.Abs(((c >> 8) & 0xFF) - g) + Math.Abs((c & 0xFF) - b) > exact)
            {
                int x = i % w, y = i / w;
                int t = (Bayer8[((y & 7) << 3) | (x & 7)] - 32) * spread / 64;
                index = nearest.Find(Math.Clamp(r + t, 0, 255), Math.Clamp(g + t, 0, 255), Math.Clamp(b + t, 0, 255));
            }
            result[i] = (byte)index;
        }
        return result;
    }

    static byte[] MapDithered(Raster frame, int[] palette, int transparentIndex)
    {
        int w = frame.Width, h = frame.Height;
        var nearest = new Nearest(palette);
        var result = new byte[w * h];
        var current = new int[(w + 2) * 3];
        var next = new int[(w + 2) * 3];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(next);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = frame.Data[i];
                if (transparentIndex >= 0 && (uint)p < 0x80000000u) { result[i] = (byte)transparentIndex; continue; }
                int e = (x + 1) * 3;
                // Errors are kept in 1/16ths.
                int r = Math.Clamp(((p >> 16) & 0xFF) + current[e] / 16, 0, 255);
                int g = Math.Clamp(((p >> 8) & 0xFF) + current[e + 1] / 16, 0, 255);
                int b = Math.Clamp((p & 0xFF) + current[e + 2] / 16, 0, 255);
                int index = nearest.Find(r, g, b);
                result[i] = (byte)index;
                int c = palette[index];
                int er = r - ((c >> 16) & 0xFF), eg = g - ((c >> 8) & 0xFF), eb = b - (c & 0xFF);
                Spread(current, e + 3, er, eg, eb, 7);
                Spread(next, e - 3, er, eg, eb, 3);
                Spread(next, e, er, eg, eb, 5);
                Spread(next, e + 3, er, eg, eb, 1);
            }
            (current, next) = (next, current);
        }
        return result;

        static void Spread(int[] row, int at, int r, int g, int b, int weight)
        {
            row[at] += r * weight; row[at + 1] += g * weight; row[at + 2] += b * weight;
        }
    }

    // MARK: LZW

    static byte[] Lzw(byte[] indices, int minCodeSize)
    {
        var output = new MemoryStream(indices.Length / 2 + 16);
        int clear = 1 << minCodeSize, eoi = clear + 1;
        int codeSize = minCodeSize + 1, next = eoi + 1;
        // A trie of codes: first child, next sibling and the byte that led to each code.
        var child = new short[4096];
        var sibling = new short[4096];
        var symbol = new byte[4096];
        Array.Fill(child, (short)-1);

        int bitBuffer = 0, bitCount = 0;
        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);
        if (indices.Length == 0) { Emit(eoi); Flush(); return output.ToArray(); }
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            byte k = indices[i];
            int found = -1;
            for (int c = child[prefix]; c >= 0; c = sibling[c])
                if (symbol[c] == k) { found = c; break; }
            if (found >= 0) { prefix = found; continue; }

            Emit(prefix);
            if (next == 4096)
            {
                Emit(clear);
                Array.Fill(child, (short)-1);
                next = eoi + 1;
                codeSize = minCodeSize + 1;
            }
            else
            {
                if (next >= (1 << codeSize)) codeSize++;
                symbol[next] = k;
                sibling[next] = child[prefix];
                child[next] = -1;
                child[prefix] = (short)next;
                next++;
            }
            prefix = k;
        }
        Emit(prefix);
        Emit(eoi);
        Flush();
        return output.ToArray();

        void Flush()
        {
            if (bitCount > 0) output.WriteByte((byte)bitBuffer);
            bitBuffer = 0; bitCount = 0;
        }
    }
}
