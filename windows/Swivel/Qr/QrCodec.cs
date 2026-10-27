using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

// QR codes and barcodes without a platform API (the Mac app uses Vision's VNDetectBarcodesRequest and Core Image's
// CIQRCodeGenerator; Windows has neither for still images). Implemented from ISO/IEC 18004 and the 1D symbology
// specifications: QrCodecTables (constants, GF(256), Reed–Solomon), QrCodecEncoder, QrCodecImage (luminance,
// binarisation, perspective), QrCodecDetector (finding symbols), QrCodecDecoder (grid → text), BarcodeReader (1D).

enum BarcodeFormat { Qr, Ean13, Ean8, UpcA, UpcE, Code128, Code39, Itf, Codabar }

enum QrErrorCorrection { L, M, Q, H }

/// One code found in an image. Bounds are in image pixels.
sealed record DecodedCode(string Text, BarcodeFormat Format, Rect Bounds);

/// A QR symbol: Size × Size modules, true = dark. No quiet zone.
sealed class QrMatrix(int size)
{
    readonly bool[] modules = new bool[size * size];
    public int Size { get; } = size;
    /// The symbol version, 1–40.
    public int Version => (Size - 17) / 4;
    public bool this[int x, int y]
    {
        get => modules[y * Size + x];
        set => modules[y * Size + x] = value;
    }
}

static class QrCodec
{
    /// Long edge of the image the first (fast) pass works on; larger images are reduced by an integer factor.
    const int WorkingSize = 1600;
    /// Small images are enlarged to at least this long edge, so codes with one or two pixels per module still read.
    const int MinimumSize = 400;
    /// Same limit as the Mac app's Images.load(maxPixels:).
    const int MaxFilePixels = 8000;

    /// Every QR code and barcode in the image, in any orientation (Vision's VNDetectBarcodesRequest on the Mac).
    /// Thread-safe; pass a frozen bitmap when calling from a background thread.
    public static IReadOnlyList<DecodedCode> Read(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (argb, w, h) = image.IsFrozen || image.CheckAccess() ? Pixels(image) : image.Dispatcher.Invoke(() => Pixels(image));
        return Read(argb, w, h);
    }

    /// Loads an image file (any format WIC reads, EXIF orientation applied) and reads it, off the calling thread.
    /// Throws ToolError when the file can't be decoded.
    public static Task<IReadOnlyList<DecodedCode>> ReadFile(string path) => Task.Run(() =>
    {
        var raster = Images.LoadRaster(path, MaxFilePixels);
        return Read(raster.Data, raster.Width, raster.Height);
    });

    /// Encodes text as a QR code (smallest version that fits, best mask), like CIQRCodeGenerator: numeric or
    /// alphanumeric mode when the whole text allows it, otherwise UTF-8 bytes. Throws ToolError when it's too long.
    public static QrMatrix Encode(string text, QrErrorCorrection level = QrErrorCorrection.M)
    {
        ArgumentNullException.ThrowIfNull(text);
        return QrEncoder.Encode(QrEncoder.SegmentsFor(text), level) ?? throw new ToolError(L("This content is too long for a QR code."));
    }

    /// The largest number of UTF-8 bytes a byte-mode symbol holds at this level (version 40).
    public static int ByteCapacity(QrErrorCorrection level = QrErrorCorrection.M) =>
        (QrTables.DataCodewords(QrTables.MaxVersion, level) * 8 - 4 - 16) / 8;

    /// A crisp black-on-white image: `scale` pixels per module and a `quietZone`-module white margin. Frozen.
    public static BitmapSource Render(QrMatrix matrix, int scale = 8, int quietZone = 4)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        scale = Math.Max(1, scale);
        quietZone = Math.Max(0, quietZone);
        int modules = matrix.Size + 2 * quietZone, side = modules * scale;
        var pixels = new byte[side * side];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < matrix.Size; y++)
            for (int x = 0; x < matrix.Size; x++)
            {
                if (!matrix[x, y]) continue;
                int top = (y + quietZone) * scale, left = (x + quietZone) * scale;
                for (int dy = 0; dy < scale; dy++) Array.Fill(pixels, (byte)0, (top + dy) * side + left, scale);
            }
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
        bitmap.Freeze();
        return bitmap;
    }


    static (int[] Argb, int Width, int Height) Pixels(BitmapSource image)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var data = new int[(long)w * h];
        if (data.Length > 0) bgra.CopyPixels(data, w * 4, 0);
        return (data, w, h);
    }

    internal static IReadOnlyList<DecodedCode> Read(int[] argb, int width, int height) =>
        Read(QrLuma.FromArgb(argb, width, height), () => QrLuma.ColourContrast(argb, width, height));

    /// The reading pipeline: QR passes on luminance, then (when they find nothing and the image has colour) on the
    /// colour-contrast image `colour` supplies, then 1D barcodes on the working copy and, when it finds none, at
    /// full resolution.
    internal static IReadOnlyList<DecodedCode> Read(QrLuma full, Func<QrLuma?>? colour = null)
    {
        if (full.Width == 0 || full.Height == 0) return [];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<DecodedCode>();
        var seen = new HashSet<(BarcodeFormat, string)>();
        void Add(string text, BarcodeFormat format, double minX, double minY, double maxX, double maxY, double fx, double fy)
        {
            if (text.Length > 0 && seen.Add((format, text)))
                results.Add(new DecodedCode(text, format, Clamp(new Rect(minX * fx, minY * fy, (maxX - minX) * fx, (maxY - minY) * fy), full)));
        }

        var (work, sx, sy, down) = Working(full);
        var binary = QrBinarizer.Hybrid(work);
        QrPasses(full, work, binary, sx, sy, down, clock, results, Add);
        if (results.Count == 0 && clock.ElapsedMilliseconds < TimeBudget && colour?.Invoke() is { } tinted)
        {
            var (tintedWork, tx, ty, tintedDown) = Working(tinted);
            QrPasses(tinted, tintedWork, QrBinarizer.Hybrid(tintedWork), tx, ty, tintedDown, clock, results, Add);
        }

        int before = results.Count;
        foreach (var hit in BarcodeReader.Scan(work, binary)) Add(hit.Text, hit.Format, hit.MinX, hit.MinY, hit.MaxX, hit.MaxY, sx, sy);
        if (results.Count == before && down > 1)
            foreach (var hit in BarcodeReader.Scan(full, null)) Add(hit.Text, hit.Format, hit.MinX, hit.MinY, hit.MaxX, hit.MaxY, 1, 1);

        // Reading order like the Mac app: top to bottom, then left to right within a band of 5 % of the height.
        double band = full.Height * 0.05;
        results.Sort((a, b) =>
        {
            double ay = a.Bounds.Y + a.Bounds.Height / 2, by = b.Bounds.Y + b.Bounds.Height / 2;
            if (Math.Abs(ay - by) > band) return ay.CompareTo(by);
            return a.Bounds.X.CompareTo(b.Bounds.X);
        });
        return results;
    }

    /// Fallback passes stop starting after this many milliseconds (pure texture can otherwise keep them busy); the
    /// first pass always runs.
    const long TimeBudget = 1500;

    /// The working copy (reduced or enlarged) and the factors back to original pixels.
    static (QrLuma Work, double Sx, double Sy, int Down) Working(QrLuma full)
    {
        int longEdge = Math.Max(full.Width, full.Height);
        int down = longEdge > WorkingSize ? (int)Math.Ceiling((double)longEdge / WorkingSize) : 1;
        int up = longEdge < MinimumSize ? Math.Min(8, (int)Math.Ceiling((double)MinimumSize / longEdge)) : 1;
        var work = down > 1 ? full.Downscale(down) : up > 1 ? full.Upscale(up) : full;
        return (work, (double)full.Width / work.Width, (double)full.Height / work.Height, down);
    }

    delegate void AddCode(string text, BarcodeFormat format, double minX, double minY, double maxX, double maxY, double fx, double fy);

    /// QR passes, cheapest first, until one finds a code: the working copy; smoothed (noise); full resolution (small
    /// codes in big photos); a global threshold; closed (seams filled), reduced by 2, 4 and 8 and darkened (huge
    /// blocky modules drawn smaller than their cells, with gaps and texture, as in rendered 3D scenes); enlarged
    /// (tiny codes).
    static void QrPasses(QrLuma full, QrLuma work, QrBinary binary, double sx, double sy, int down,
                         System.Diagnostics.Stopwatch clock, List<DecodedCode> results, AddCode add)
    {
        void Detect(QrBinary image, double fx, double fy)
        {
            foreach (var hit in QrDetector.Detect(image)) add(hit.Text, BarcodeFormat.Qr, hit.MinX, hit.MinY, hit.MaxX, hit.MaxY, fx, fy);
        }
        bool More() => results.Count == 0 && clock.ElapsedMilliseconds < TimeBudget;

        Detect(binary, sx, sy);
        if (More()) Detect(QrBinarizer.Hybrid(work.Smooth()), sx, sy);
        if (More() && down > 1) Detect(QrBinarizer.Hybrid(full), 1, 1);
        if (More()) Detect(QrBinarizer.Global(work), sx, sy);
        for (int factor = 1; More() && factor <= 8; factor *= 2)
        {
            if (Math.Min(work.Width, work.Height) / factor < 60) break;
            var reduced = factor == 1 ? work : work.Downscale(factor);
            double fx = sx * work.Width / reduced.Width, fy = sy * work.Height / reduced.Height;
            if (factor > 1) Detect(QrBinarizer.Hybrid(reduced), fx, fy);
            foreach (int radius in new[] { 2, 3 })
                if (More()) Detect(QrBinarizer.Hybrid(reduced.Closed(radius)), fx, fy);
            if (More() && factor > 1) Detect(QrBinarizer.Hybrid(reduced.Darkened(1)), fx, fy);
        }
        if (More() && Math.Max(work.Width, work.Height) <= 1000) Detect(QrBinarizer.Hybrid(work.Upscale(2)), sx / 2, sy / 2);
    }

    static Rect Clamp(Rect r, QrLuma image)
    {
        double x0 = Math.Clamp(r.X, 0, image.Width), y0 = Math.Clamp(r.Y, 0, image.Height);
        double x1 = Math.Clamp(r.X + r.Width, 0, image.Width), y1 = Math.Clamp(r.Y + r.Height, 0, image.Height);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}
