using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinImaging = Windows.Graphics.Imaging;

namespace Swivel;

// Port of Sources/Swivel/Images.swift. Decoding goes through WIC (WPF's BitmapDecoder uses every installed codec,
// including the HEIF / WebP / AVIF / raw Store extensions); JPG, PNG, TIFF and BMP are written with WPF's encoders,
// GIF with Swivel's own encoder (WIC's GIF encoder quantizes to a fixed palette), and HEIC / AVIF / WebP through
// Windows.Graphics.Imaging when this PC has an encoder for them.

sealed record ImageFormat(string Name, string Ext, string[] Extensions);

static class Images
{
    static readonly ImageFormat[] Candidates =
    [
        new("JPG", "jpg", ["jpg", "jpeg", "jpe", "jfif"]),
        new("PNG", "png", ["png"]),
        new("HEIC", "heic", ["heic", "heif", "hif"]),
        new("WEBP", "webp", ["webp"]),
        new("TIFF", "tiff", ["tiff", "tif"]),
        new("AVIF", "avif", ["avif"]),
        new("GIF", "gif", ["gif"]),
        new("BMP", "bmp", ["bmp", "dib"]),
    ];

    /// Which formats this PC can write, probed once (a real encode, because the HEIF encoder is registered even when
    /// the HEVC Video Extensions it needs are missing). Started in the background at first use of the class.
    static readonly Lazy<HashSet<string>> writable = new(ProbeWritable, LazyThreadSafetyMode.ExecutionAndPublication);

    static Images() => _ = Task.Run(() => writable.Value);

    /// Starts the one-time codec probes (image encoders, HEVC encoder) in the background, so the first Convert menu
    /// doesn't wait for them. Call once at startup.
    public static void WarmUp() => Transcode.WarmUp();

    /// The formats this PC can write (codecs such as HEIF/AVIF/WebP depend on installed Store extensions), in the Mac order.
    public static List<ImageFormat> OutputFormats => Candidates.Where(f => writable.Value.Contains(f.Ext)).ToList();

    public static List<FileOption> ToolItems =>
    [
        new("Resize 50%", path => Task.Run(() => Transform(path, " small", 0.5, null))),
        new("Rotate", path => Task.Run(() => Transform(path, " rotated", 1, RotatedRight))),
        new("Flip", path => Task.Run(() => Transform(path, " flipped", 1, FlippedHorizontally))),
        new("Grayscale", path => Task.Run(() => Transform(path, " gray", 1, Grayscale))),
    ];

    public static Task<List<string>> Convert(string path, ImageFormat format) => Task.Run(() =>
    {
        var image = Load(path);
        var output = Paths.Output(path, format.Ext);
        Write(image, output, format.Ext, 0.92);
        return new List<string> { output };
    });

    /// One page per image, each page the image's size in points (pixels at 72 dpi, like PDFPage(image:)).
    public static Task<List<string>> MakePdf(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        if (paths.Count == 0) return new List<string>();
        var output = Paths.Output(paths[0], "pdf");
        ImagesPdf.Write(paths, output);
        return new List<string> { output };
    });

    // MARK: Decoding

    /// Decodes the image upright (EXIF orientation applied), optionally capped so its long edge is at most maxPixels.
    /// The result is frozen (usable from any thread), 96 dpi so one pixel is one WPF unit, sRGB, and Bgra32 when it
    /// has transparent pixels, Bgr32 otherwise. Never enlarges.
    public static BitmapSource Load(string path, int? maxPixels = null) => LoadRaster(path, maxPixels).ToBitmap();

    /// The upright pixel size without decoding the pixels.
    public static (int Width, int Height) PixelSize(string path)
    {
        try
        {
            using var stream = OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var orientation = Orientation(frame);
            return orientation >= 5 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
    }

    internal static Raster LoadRaster(string path, int? maxPixels = null)
    {
        Raster raster;
        int orientation;
        try
        {
            using var stream = OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            orientation = Orientation(frame);
            raster = Raster.From(ToSrgb(frame));
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
        raster = raster.Oriented(orientation);
        if (maxPixels is int max && max > 0 && Math.Max(raster.Width, raster.Height) > max)
        {
            var (w, h) = Fit(raster.Width, raster.Height, max);
            raster = raster.Resized(w, h);
        }
        return raster;
    }

    /// Dimensions scaled so the long edge is `max` (at least 1 pixel each).
    public static (int Width, int Height) Fit(int width, int height, int max)
    {
        if (Math.Max(width, height) <= max) return (width, height);
        double scale = (double)max / Math.Max(width, height);
        return width >= height
            ? (max, Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)))
            : (Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)), max);
    }

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// EXIF orientation (1…8) from System.Photo.Orientation, 1 when absent or unreadable.
    internal static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is { } value)
            {
                int n = System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                return n is >= 1 and <= 8 ? n : 1;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or FormatException
                                      or System.Runtime.InteropServices.COMException or InvalidCastException or OverflowException) { }
        return 1;
    }

    /// Converts an embedded colour profile (Display P3 photos, Adobe RGB) to sRGB, so untagged output looks the same.
    static BitmapSource ToSrgb(BitmapFrame frame)
    {
        try
        {
            var contexts = frame.ColorContexts;
            if (contexts is { Count: > 0 })
            {
                var converted = new ColorConvertedBitmap(frame, contexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
                converted.Freeze();
                return converted;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException
                                      or System.Runtime.InteropServices.COMException or IOException) { }
        return frame;
    }

    // MARK: Encoding

    /// Encodes to the format for `ext` ("jpg", "png", "heic", "tiff", "gif", "bmp", "webp", "avif"); JPG/BMP flatten
    /// transparency onto white. Writes atomically (temp file, then move) so a failure leaves no half-written file.
    public static void Write(BitmapSource image, string path, string ext, double quality)
    {
        var data = Encode(image, ext, quality);
        WriteAtomically(path, data);
    }

    /// Writes bytes to a hidden temp file in the destination folder, then moves it into place (never over a file).
    public static void WriteAtomically(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temp = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            try { File.SetAttributes(temp, FileAttributes.Hidden); } catch (IOException) { }
            File.Move(temp, path, overwrite: false);
            Unhide(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new ToolError(L("Couldn't save %@", Path.GetFileName(path)));
        }
    }

    /// Clears the Hidden flag a temp file carried into its final name.
    internal static void Unhide(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Encodes to bytes (for size estimates in Compress).
    public static byte[] Encode(BitmapSource image, string ext, double quality)
    {
        var key = Canonical(ext);
        if (!writable.Value.Contains(key)) throw new ToolError(L("This PC can't write %@ images", key.ToUpperInvariant()));
        quality = Math.Clamp(quality, 0.01, 1);
        if (key is "jpg" or "bmp" && HasAlpha(image)) image = Flattened(image);
        if (key == "gif") return GifWriter.EncodeStill(Raster.From(image));
        if (key is "heic" or "webp" or "avif") return EncodeWinRT(image, key, quality);

        BitmapEncoder encoder = key switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(quality * 100), 1, 100) },
            "png" => new PngBitmapEncoder(),
            // LZW keeps TIFF lossless but smaller than ImageIO's uncompressed default; every TIFF reader supports it.
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new BmpBitmapEncoder(),
        };
        try
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Couldn't encode the image")); }
    }

    /// "jpeg" → "jpg", "tif" → "tiff", "heif" → "heic"… (the `Ext` of the matching format), else the input lowercased.
    internal static string Canonical(string ext)
    {
        var e = ext.TrimStart('.').ToLowerInvariant();
        return Candidates.FirstOrDefault(f => f.Extensions.Contains(e))?.Ext ?? e;
    }

    /// The source's own extension (as spelled) if this PC can write that format, else "png".
    public static string WritableExt(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        var key = Canonical(ext);
        return Candidates.Any(f => f.Ext == key) && writable.Value.Contains(key) ? ext : "png";
    }

    /// True when the image has an alpha channel with at least one pixel that isn't fully opaque.
    public static bool HasAlpha(BitmapSource image)
    {
        var f = image.Format;
        if (f == PixelFormats.Bgr32 || f == PixelFormats.Bgr24 || f == PixelFormats.Rgb24 || f == PixelFormats.Gray8 ||
            f == PixelFormats.Gray16 || f == PixelFormats.Gray32Float || f == PixelFormats.BlackWhite || f == PixelFormats.Gray2 ||
            f == PixelFormats.Gray4 || f == PixelFormats.Cmyk32 || f == PixelFormats.Bgr565 || f == PixelFormats.Bgr555 ||
            f == PixelFormats.Rgb48 || f == PixelFormats.Bgr101010 || f == PixelFormats.Rgb128Float)
            return false;
        if (f == PixelFormats.Indexed8 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed1)
            if (image.Palette is { } palette && palette.Colors.All(c => c.A == 255)) return false;
        return Raster.From(image).Alpha;
    }

    // MARK: Transforms (also used by the editors)

    public static BitmapSource Flattened(BitmapSource image) => Raster.From(image).Flattened().ToBitmap();

    /// 90° clockwise.
    public static BitmapSource RotatedRight(BitmapSource image) => Raster.From(image).RotatedRight().ToBitmap();

    public static BitmapSource FlippedHorizontally(BitmapSource image) => Raster.From(image).FlippedHorizontally().ToBitmap();

    /// Flattened onto white and converted to 8-bit gray (like drawing into a DeviceGray context).
    public static BitmapSource Grayscale(BitmapSource image) => Raster.From(image).Flattened().ToGray8();

    /// High-quality resize to exactly width × height (area averaging when shrinking).
    public static BitmapSource Resized(BitmapSource image, int width, int height) => Raster.From(image).Resized(width, height).ToBitmap();

    static List<string> Transform(string path, string suffix, double scale, Func<BitmapSource, BitmapSource>? change)
    {
        int? maxPixels = null;
        if (scale != 1)
        {
            var (w, h) = PixelSize(path);
            maxPixels = Math.Max(1, (int)(Math.Max(w, h) * scale));
        }
        var image = Load(path, maxPixels);
        if (change != null) image = change(image);
        var ext = WritableExt(path);
        var output = Paths.Output(path, ext, suffix);
        Write(image, output, ext, 0.9);
        return [output];
    }

    // MARK: WinRT encoders (HEIC, AVIF, WebP)

    static byte[] EncodeWinRT(BitmapSource image, string key, double quality)
    {
        var codec = WinRTCodec(key) ?? throw new ToolError(L("This PC can't write %@ images", key.ToUpperInvariant()));
        var raster = Raster.From(image);
        try
        {
            // WinRT async work on a pool thread, so blocking here never waits on the caller's dispatcher.
            return Task.Run(() => EncodeWinRTAsync(raster, codec.Id, codec.Av1, quality)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Couldn't encode the image")); }
    }

    static async Task<byte[]> EncodeWinRTAsync(Raster raster, Guid codecId, bool av1, double quality)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var options = new WinImaging.BitmapPropertySet
        {
            ["ImageQuality"] = new WinImaging.BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single),
        };
        // WIC's HEIF encoder writes AVIF when asked for AV1 compression (WICHeifCompressionAV1 = 3), on PCs with an AV1 encoder.
        if (av1) options["HeifCompressionMethod"] = new WinImaging.BitmapTypedValue((byte)3, Windows.Foundation.PropertyType.UInt8);
        var encoder = await WinImaging.BitmapEncoder.CreateAsync(codecId, stream, options);
        var bytes = new byte[raster.Width * raster.Height * 4];
        Buffer.BlockCopy(raster.Data, 0, bytes, 0, bytes.Length);
        encoder.SetPixelData(WinImaging.BitmapPixelFormat.Bgra8,
            raster.Alpha ? WinImaging.BitmapAlphaMode.Straight : WinImaging.BitmapAlphaMode.Ignore,
            (uint)raster.Width, (uint)raster.Height, 96, 96, bytes);
        await encoder.FlushAsync();
        var result = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(result);
        return result;
    }

    static readonly Lazy<WinImaging.BitmapCodecInformation[]> encoders = new(() =>
    {
        try { return WinImaging.BitmapEncoder.GetEncoderInformationEnumerator().ToArray(); }
        catch (Exception) { return []; }
    });

    static (Guid Id, bool Av1)? WinRTCodec(string key)
    {
        WinImaging.BitmapCodecInformation? Find(string ext) =>
            encoders.Value.FirstOrDefault(c => c.FileExtensions.Any(e => string.Equals(e, "." + ext, StringComparison.OrdinalIgnoreCase)));
        switch (key)
        {
            case "heic": return Find("heic") is { } heif ? (heif.CodecId, false) : null;
            case "webp": return Find("webp") is { } webp ? (webp.CodecId, false) : null;
            case "avif":
                if (Find("avif") is { } avif) return (avif.CodecId, false);
                return Find("heic") is { } h ? (h.CodecId, true) : null;
            default: return null;
        }
    }

    static HashSet<string> ProbeWritable()
    {
        var set = new HashSet<string> { "jpg", "png", "tiff", "gif", "bmp" };
        var probe = new Raster(16, 16, Enumerable.Repeat(unchecked((int)0xFF3366CC), 256).ToArray(), false);
        foreach (var key in new[] { "heic", "webp", "avif" })
        {
            if (WinRTCodec(key) is not { } codec) continue;
            try
            {
                var data = Task.Run(() => EncodeWinRTAsync(probe, codec.Id, codec.Av1, 0.9)).GetAwaiter().GetResult();
                if (data.Length < 16) continue;
                // An encoder that ignored the AV1 request would write HEVC under an .avif name: require the brand.
                if (key == "avif" && !HasBrand(data, "avif")) continue;
                if (key == "heic" && !(HasBrand(data, "heic") || HasBrand(data, "heix") || HasBrand(data, "mif1"))) continue;
                set.Add(key);
            }
            catch (Exception) { }
        }
        return set;
    }

    /// Whether an ISO-BMFF file's ftyp box lists `brand` (major or compatible).
    static bool HasBrand(byte[] data, string brand)
    {
        if (data.Length < 16 || System.Text.Encoding.ASCII.GetString(data, 4, 4) != "ftyp") return false;
        int size = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        size = Math.Min(size, data.Length);
        for (int i = 8; i + 4 <= size; i += 4)
            if (i != 12 && System.Text.Encoding.ASCII.GetString(data, i, 4) == brand) return true;
        return false;
    }
}

/// Straight-alpha BGRA pixels, one int per pixel (little-endian B, G, R, A), top row first — the working form of the
/// converters. `Alpha` says whether any pixel is not fully opaque.
sealed class Raster
{
    public readonly int Width, Height;
    public readonly int[] Data;
    public readonly bool Alpha;

    public Raster(int width, int height, int[] data, bool alpha)
    {
        Width = width; Height = height; Data = data; Alpha = alpha;
    }

    public Raster(int width, int height, int[] data) : this(width, height, data, AnyTransparent(data)) { }

    static bool AnyTransparent(int[] data)
    {
        foreach (var p in data) if ((uint)p < 0xFF000000u) return true;
        return false;
    }

    public static Raster From(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var data = new int[(long)w * h];
        bgra.CopyPixels(data, w * 4, 0);
        return new Raster(w, h, data);
    }

    public BitmapSource ToBitmap()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, Alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, Data, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public BitmapSource ToGray8()
    {
        var gray = new byte[Width * Height];
        for (int i = 0; i < Data.Length; i++)
        {
            int p = Data[i];
            int b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
            gray[i] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, gray, Width);
        bitmap.Freeze();
        return bitmap;
    }

    /// Transparency composited onto white.
    public Raster Flattened()
    {
        if (!Alpha) return this;
        var result = new int[Data.Length];
        for (int i = 0; i < Data.Length; i++)
        {
            int p = Data[i];
            int a = (p >> 24) & 0xFF, inv = 255 - a;
            int b = ((p & 0xFF) * a + 255 * inv + 127) / 255;
            int g = (((p >> 8) & 0xFF) * a + 255 * inv + 127) / 255;
            int r = (((p >> 16) & 0xFF) * a + 255 * inv + 127) / 255;
            result[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        return new Raster(Width, Height, result, false);
    }

    /// Applies EXIF orientation 1…8 so the pixels are upright.
    public Raster Oriented(int orientation) => orientation switch
    {
        2 => FlippedHorizontally(),
        3 => Rotated180(),
        4 => FlippedVertically(),
        5 => RotatedRight().FlippedHorizontally(),  // transpose
        6 => RotatedRight(),
        7 => RotatedRight().FlippedVertically(),    // transverse
        8 => RotatedLeft(),
        _ => this,
    };

    public Raster RotatedRight()
    {
        int w = Width, h = Height;
        var result = new int[Data.Length];
        // Source (x, y) lands at (h - 1 - y, x) in the h × w result.
        for (int y = 0; y < h; y++)
        {
            int row = y * w, column = h - 1 - y;
            for (int x = 0; x < w; x++) result[x * h + column] = Data[row + x];
        }
        return new Raster(h, w, result, Alpha);
    }

    public Raster RotatedLeft()
    {
        int w = Width, h = Height;
        var result = new int[Data.Length];
        // Source (x, y) lands at (y, w - 1 - x).
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) result[(w - 1 - x) * h + y] = Data[row + x];
        }
        return new Raster(h, w, result, Alpha);
    }

    public Raster Rotated180()
    {
        var result = (int[])Data.Clone();
        Array.Reverse(result);
        return new Raster(Width, Height, result, Alpha);
    }

    public Raster FlippedHorizontally()
    {
        var result = (int[])Data.Clone();
        for (int y = 0; y < Height; y++) Array.Reverse(result, y * Width, Width);
        return new Raster(Width, Height, result, Alpha);
    }

    public Raster FlippedVertically()
    {
        var result = new int[Data.Length];
        for (int y = 0; y < Height; y++) Array.Copy(Data, y * Width, result, (Height - 1 - y) * Width, Width);
        return new Raster(Width, Height, result, Alpha);
    }

    public Raster Cropped(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x); height = Math.Clamp(height, 1, Height - y);
        if (x == 0 && y == 0 && width == Width && height == Height) return this;
        var result = new int[width * height];
        for (int row = 0; row < height; row++) Array.Copy(Data, (y + row) * Width + x, result, row * width, width);
        return new Raster(width, height, result, Alpha);
    }

    /// Separable resampling in premultiplied space: area averaging when shrinking (every source pixel weighs by the
    /// area it covers, like ImageIO's thumbnails), linear interpolation when enlarging.
    public Raster Resized(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return this;
        var (xStart, xWeights) = Weights(Width, width);
        var (yStart, yWeights) = Weights(Height, height);

        // Premultiplied float source, 4 channels.
        int sw = Width, sh = Height;
        var horizontal = new float[width * sh * 4];
        Parallel.For(0, sh, y =>
        {
            int srcRow = y * sw, dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                int start = xStart[x];
                var weights = xWeights[x];
                for (int k = 0; k < weights.Length; k++)
                {
                    int p = Data[srcRow + start + k];
                    float wa = weights[k] * ((p >> 24) & 0xFF);
                    b += wa * (p & 0xFF); g += wa * ((p >> 8) & 0xFF); r += wa * ((p >> 16) & 0xFF); a += wa;
                }
                int o = dstRow + x * 4;
                horizontal[o] = b; horizontal[o + 1] = g; horizontal[o + 2] = r; horizontal[o + 3] = a;
            }
        });

        var result = new int[width * height];
        bool alpha = Alpha;
        Parallel.For(0, height, y =>
        {
            int start = yStart[y];
            var weights = yWeights[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int o = ((start + k) * width + x) * 4;
                    float wk = weights[k];
                    b += wk * horizontal[o]; g += wk * horizontal[o + 1]; r += wk * horizontal[o + 2]; a += wk * horizontal[o + 3];
                }
                int pixel;
                if (a <= 0.5f) pixel = 0;
                else
                {
                    int ia = Math.Clamp((int)(a + 0.5f), 0, 255);
                    int ib = Math.Clamp((int)(b / a + 0.5f), 0, 255), ig = Math.Clamp((int)(g / a + 0.5f), 0, 255), ir = Math.Clamp((int)(r / a + 0.5f), 0, 255);
                    if (!alpha) ia = 255;
                    pixel = (ia << 24) | (ir << 16) | (ig << 8) | ib;
                }
                result[y * width + x] = pixel;
            }
        });
        return new Raster(width, height, result, alpha && AnyTransparent(result));
    }

    /// Per output index: the first source index and the weights of the source pixels from there (summing to 1).
    static (int[] Start, float[][] Weights) Weights(int source, int target)
    {
        var start = new int[target];
        var weights = new float[target][];
        double scale = (double)source / target;
        for (int i = 0; i < target; i++)
        {
            if (scale > 1)
            {
                double from = i * scale, to = Math.Min(source, (i + 1) * scale);
                int first = (int)Math.Floor(from), last = Math.Min(source - 1, (int)Math.Ceiling(to) - 1);
                var w = new float[last - first + 1];
                for (int k = first; k <= last; k++)
                    w[k - first] = (float)((Math.Min(to, k + 1) - Math.Max(from, k)) / scale);
                start[i] = first;
                weights[i] = Normalize(w);
            }
            else
            {
                double center = (i + 0.5) * scale - 0.5;
                int left = (int)Math.Floor(center);
                double frac = center - left;
                int a = Math.Clamp(left, 0, source - 1), b = Math.Clamp(left + 1, 0, source - 1);
                if (a == b) { start[i] = a; weights[i] = [1f]; }
                else { start[i] = a; weights[i] = [(float)(1 - frac), (float)frac]; }
            }
        }
        return (start, weights);
    }

    static float[] Normalize(float[] w)
    {
        float sum = 0;
        foreach (var v in w) sum += v;
        if (sum > 0) for (int i = 0; i < w.Length; i++) w[i] /= sum;
        return w;
    }
}
