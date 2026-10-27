using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public static class ImageTools
{
    // A crop editor holds this lease until Save/Cancel. Preview and export use the same
    // immutable upright pixels, while the original and every ancestor remain pinned.
    public sealed class Document : IDisposable
    {
        internal NativeFiles.Input Input { get; }
        public BitmapSource Bitmap { get; }
        internal Document(NativeFiles.Input input, BitmapSource bitmap) { Input = input; Bitmap = bitmap; }
        public void Dispose() => Input.Dispose();
    }
    public static Document Open(string path, CancellationToken cancellation)
    {
        var input = NativeFiles.OpenRead(path);
        try { return new Document(input, Decode(input, cancellation)); }
        catch { input.Dispose(); throw; }
    }
    public static Document OpenTile(string path, int width, int height, bool fill, CancellationToken cancellation)
    {
        ImageRules.Validate(width, height);
        var input = NativeFiles.OpenRead(path);
        try
        {
            BitmapSource source = Decode(input, cancellation);
            if (fill)
            {
                var crop = PixelCrop.Center(source.PixelWidth, source.PixelHeight, (double)width / height);
                source = new CroppedBitmap(source, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
            }
            double scale = Math.Min(1, Math.Min((double)width / source.PixelWidth, (double)height / source.PixelHeight));
            int targetWidth = Math.Max(1, (int)Math.Round(source.PixelWidth * scale)), targetHeight = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            source = new TransformedBitmap(source, new ScaleTransform((double)targetWidth / source.PixelWidth, (double)targetHeight / source.PixelHeight));
            var tile = CopyPixels(source, cancellation); tile.Freeze();
            return new Document(input, tile);
        }
        catch { input.Dispose(); throw; }
    }
    internal static BitmapSource Decode(NativeFiles.Input input, CancellationToken cancellation) => Decode(input.Stream, input.Path, cancellation);
    internal static BitmapSource DecodeSnapshot(byte[] bytes, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes, false);
        return Decode(stream, path, cancellation);
    }
    private static BitmapSource Decode(Stream stream, string path, CancellationToken cancellation)
    {
        if (stream.Length > ImageRules.MaxInputBytes) throw new UserError("Image files are limited to 128 MB.");
        cancellation.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var pixels = GifImage.Read(stream, cancellation);
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, pixels.Width * 4);
            cancellation.ThrowIfCancellationRequested(); bitmap.Freeze(); return bitmap;
        }
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) RejectAnimatedPng(stream, cancellation);
        var decoder = CreateDecoder(stream, path);
        if (decoder.Frames.Count != 1) throw new UserError("Multi-frame images are not supported yet.");
        var frame = decoder.Frames[0];
        ImageRules.Validate(frame.PixelWidth, frame.PixelHeight);
        if (!IsSdrFormat(frame.Format))
            throw new UserError("HDR, high-bit-depth and CMYK images are not supported yet.");
        cancellation.ThrowIfCancellationRequested();
        BitmapSource source = frame;
        if (frame.ColorContexts is { Count: > 0 })
            source = new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        source = Orient(source, Orientation(frame));
        var clean = CopyPixels(source, cancellation);
        clean.Freeze();
        return clean;
    }
    private static BitmapDecoder CreateDecoder(NativeFiles.Input input) => CreateDecoder(input.Stream, input.Path);
    private static BitmapDecoder CreateDecoder(Stream stream, string path)
    {
        stream.Position = 0;
        // OnDemand lets us inspect dimensions/frame count before copying decoded pixels.
        // IgnoreImageCache and stream-only input prevent URI fetches and stale path cache reads.
        const BitmapCreateOptions options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache;
        const BitmapCacheOption cache = BitmapCacheOption.OnDemand;
        // Select only built-in codecs, rather than invoking an arbitrary installed WIC decoder.
        return Path.GetExtension(path).ToLowerInvariant() switch {
            ".png" => new PngBitmapDecoder(stream, options, cache),
            ".jpg" or ".jpeg" => new JpegBitmapDecoder(stream, options, cache),
            ".bmp" => new BmpBitmapDecoder(stream, options, cache),
            ".tif" or ".tiff" => new TiffBitmapDecoder(stream, options, cache),
            _ => throw new UserError(GifImage.InputHint)
        };
    }
    public static IReadOnlyList<(string Label, string Value)> Metadata(Document document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(document.Input.Path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return new[] { ("Pixel dimensions", $"{document.Bitmap.PixelWidth} × {document.Bitmap.PixelHeight}"), ("File size", $"{document.Input.Stream.Length:N0} B") };
        var frame = CreateDecoder(document.Input).Frames[0];
        var fields = new List<(string, string)> { ("Pixel dimensions", $"{frame.PixelWidth} × {frame.PixelHeight}"), ("File size", $"{document.Input.Stream.Length:N0} B") };
        if (frame.Metadata is not BitmapMetadata metadata) return fields;
        void Add(string label, Func<string?> read)
        {
            token.ThrowIfCancellationRequested();
            try { string? text = read(); if (!string.IsNullOrWhiteSpace(text)) fields.Add((label, VisibleMetadata(text))); }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        }
        Add("Title", () => metadata.Title); Add("Subject", () => metadata.Subject);
        Add("Author", () => metadata.Author is null ? null : string.Join(", ", metadata.Author.Take(8).Select(VisibleMetadata)));
        Add("Date Taken", () => metadata.DateTaken); Add("Camera Manufacturer", () => metadata.CameraManufacturer); Add("Camera Model", () => metadata.CameraModel);
        Add("Copyright", () => metadata.Copyright); Add("Comment", () => metadata.Comment);
        return fields;
    }
    private static string VisibleMetadata(string text)
    {
        var visible = new StringBuilder();
        foreach (char c in text.Take(2048))
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) visible.Append($"[U+{(int)c:X4}]");
            else visible.Append(c);
        }
        if (text.Length > 2048) visible.Append('…');
        return visible.ToString();
    }
    public static string Run(string path, string action, int edge, CancellationToken cancellation, ImageRecipe? recipe = null, int quality = 90, ResizeOptions? resize = null, long? targetBytes = null)
    {
        using var document = Open(path, cancellation);
        return Export(document, action, edge, cancellation, recipe, quality: quality, resize: resize, targetBytes: targetBytes);
    }
    public static string Export(Document document, string action, int edge, CancellationToken cancellation,
        ImageRecipe? recipe = null, PixelCrop? crop = null, int quality = 90, PhotoSettings? adjustments = null, IReadOnlyList<ImageMark>? marks = null, ResizeOptions? resize = null, long? targetBytes = null, IReadOnlyList<RetouchStroke>? retouch = null)
    {
        cancellation.ThrowIfCancellationRequested();
        BitmapSource source = document.Bitmap;
        string path = document.Input.Path;
        string extension = ImageOutputInfo.Format(action, recipe);
        string suffix = " converted";
        if (action == "image.resize")
        {
            var size = (resize ?? new ResizeOptions(Value: edge)).Size(source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform((double)size.Width / source.PixelWidth, (double)size.Height / source.PixelHeight));
            suffix = $" {size.Width}×{size.Height}";
        }
        else if (action == "image.rotate")
        {
            source = new TransformedBitmap(source, new RotateTransform(90));
            suffix = " rotated";
        }
        else if (action == "image.flip")
        {
            source = new TransformedBitmap(source, new ScaleTransform(-1, 1)); suffix = " flipped";
        }
        else if (action is "image.adjust" or "image.gray")
        {
            var pixels = ReadPixels(source, 0, cancellation);
            var adjusted = PhotoAdjust.Apply(pixels, action == "image.gray" ? new PhotoSettings(Saturation: -1) : adjustments ?? throw new ArgumentException("Missing photo adjustments."), cancellation);
            source = BitmapSource.Create(adjusted.Width, adjusted.Height, 96, 96, PixelFormats.Bgra32, null, adjusted.Bgra, adjusted.Width * 4);
            suffix = action == "image.gray" ? " grayscale" : " edited";
        }
        else if (action is "image.removebg" or "image.blurbg")
        {
            var pixels = ReadPixels(source, 0, cancellation);
            var mask = Cutout.Mask(CutoutModel.Predict(Cutout.Input(pixels, cancellation), cancellation), pixels.Width, pixels.Height, cancellation);
            var result = action == "image.removebg" ? Cutout.Apply(pixels, mask) : Cutout.BlurBackground(pixels, mask, cancellation);
            source = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, result.Bgra, result.Width * 4);
            suffix = action == "image.removebg" ? " cutout" : " blurred";
        }
        else if (action == "image.retouch")
        {
            if (retouch is null || retouch.Count == 0) throw new UserError("Paint an area before saving a repair.");
            var repaired = Retouch.Apply(ReadPixels(source,0,cancellation),retouch,cancellation);
            source = BitmapSource.Create(repaired.Width,repaired.Height,96,96,PixelFormats.Bgra32,null,repaired.Bgra,repaired.Width*4); suffix = " retouched";
        }
        else if (action is "image.redact" or "image.annotate")
        {
            if (marks is null || marks.Count == 0) throw new UserError("Draw a mark before saving.");
            source = MarkupTools.Render(source, marks, cancellation);
            suffix = action == "image.redact" ? " redacted" : " annotated";
        }
        else if (action == "image.crop")
        {
            if (crop is null) throw new ArgumentException("Missing crop rectangle.");
            var rect = crop.Value; rect.Validate(source.PixelWidth, source.PixelHeight);
            source = new CroppedBitmap(source, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            suffix = " cropped";
        }
        else if (action == "image.compress")
        {
            new ImageRecipe("jpg", Quality: quality).Validate();
            suffix = " compressed";
        }
        else if (action.StartsWith("preset.", StringComparison.Ordinal))
        {
            if (recipe is null) throw new ArgumentException("Missing preset recipe.");
            recipe.Validate();
            extension = recipe.Format; quality = recipe.Quality;
            if (recipe.AutoPerImage || recipe.Adjustments is { } photo && photo != new PhotoSettings())
            {
                var pixels = ReadPixels(source,0,cancellation);
                var adjusted = PhotoAdjust.Apply(pixels,recipe.ResolvePhoto(pixels),cancellation);
                source = BitmapSource.Create(adjusted.Width,adjusted.Height,96,96,PixelFormats.Bgra32,null,adjusted.Bgra,adjusted.Width*4);
            }
            if (recipe.LongEdge > 0)
            {
                var size = ImageRules.Fit(source.PixelWidth, source.PixelHeight, recipe.LongEdge);
                source = new TransformedBitmap(source, new ScaleTransform((double)size.Width / source.PixelWidth, (double)size.Height / source.PixelHeight));
            }
            // User-supplied preset names are display-only and never become file paths.
            suffix = " preset";
        }
        else if (action is not "convert.png" and not "convert.jpg" and not "convert.bmp" and not "convert.tiff" and not "convert.gif")
            throw new ArgumentException("Unknown image action.");
        cancellation.ThrowIfCancellationRequested();
        if (extension is "jpg" or "bmp") source = OnWhite(source);
        var clean = CopyPixels(source, cancellation);
        if (action == "image.compress" && targetBytes.HasValue)
        {
            var bytes = JpegBudget.Encode(targetBytes.Value, level => {
                var jpeg = new JpegBitmapEncoder { QualityLevel = level }; jpeg.Frames.Add(BitmapFrame.Create(clean));
                using var memory = new MemoryStream(); jpeg.Save(memory); return memory.ToArray();
            }, cancellation);
            return NativeFiles.WriteBeside(document.Input, Path.GetFileNameWithoutExtension(path) + suffix, "jpg", stream => stream.Write(bytes), cancellation);
        }
        if (extension == "gif")
        {
            var pixels = ReadPixels(clean, 0, cancellation);
            return NativeFiles.WriteBeside(document.Input, Path.GetFileNameWithoutExtension(path) + suffix, "gif",
                output => GifExport.Write(pixels, output, cancellation), cancellation);
        }
        BitmapEncoder encoder = extension switch {
            "jpg" => new JpegBitmapEncoder { QualityLevel = quality },
            "bmp" => new BmpBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(clean));
        return NativeFiles.WriteBeside(document.Input, Path.GetFileNameWithoutExtension(path) + suffix, extension, encoder.Save, cancellation);
    }

    public static PixelImage ReadPixels(BitmapSource source, int previewEdge, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (previewEdge > 0)
        {
            var size = ImageRules.Fit(source.PixelWidth, source.PixelHeight, previewEdge);
            source = new TransformedBitmap(source, new ScaleTransform((double)size.Width / source.PixelWidth, (double)size.Height / source.PixelHeight));
        }
        source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bytes, source.PixelWidth * 4, 0); cancellation.ThrowIfCancellationRequested();
        return new(source.PixelWidth, source.PixelHeight, bytes);
    }

    private static BitmapSource CopyPixels(BitmapSource source, CancellationToken cancellation)
    {
        // Drop metadata and normalize DPI after baking orientation and color conversion.
        int stride = checked((source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        cancellation.ThrowIfCancellationRequested();
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, source.Format, null, pixels, stride);
    }

    public static bool IsSdrFormat(PixelFormat format) => format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
        format == PixelFormats.Bgr32 || format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24 ||
        format == PixelFormats.Gray8 || format == PixelFormats.Gray4 || format == PixelFormats.Gray2 ||
        format == PixelFormats.BlackWhite || format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 ||
        format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8 || format == PixelFormats.Bgr555 || format == PixelFormats.Bgr565;

    private static void RejectAnimatedPng(Stream input, CancellationToken cancellation)
    {
        // WIC can expose only APNG's default image. Detect animation explicitly rather than
        // silently exporting its first frame under a single-frame-only promise.
        Span<byte> header = stackalloc byte[8];
        input.ReadExactly(header);
        if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new UserError(GifImage.InputHint);
        while (input.Position < input.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (header[4..].SequenceEqual("acTL"u8)) throw new UserError("Multi-frame images are not supported yet.");
            if ((long)size + 4 > input.Length - input.Position) throw new UserError(GifImage.InputHint);
            input.Seek(size + 4L, SeekOrigin.Current);
            if (header[4..].SequenceEqual("IEND"u8)) break;
        }
        input.Position = 0;
    }

    private static ushort Orientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort orientation)
            {
                if (orientation is < 1 or > 8) throw new UserError("This image has invalid orientation metadata.");
                return orientation;
            }
        }
        return 1;
    }
    private static BitmapSource Orient(BitmapSource source, ushort orientation)
    {
        // Output-coordinate affine maps for all eight EXIF orientations; WPF translates
        // transformed bounds to the new bitmap origin.
        Matrix matrix = orientation switch {
            2 => new(-1, 0, 0, 1, 0, 0), 3 => new(-1, 0, 0, -1, 0, 0),
            4 => new(1, 0, 0, -1, 0, 0), 5 => new(0, 1, 1, 0, 0, 0),
            6 => new(0, 1, -1, 0, 0, 0), 7 => new(0, -1, -1, 0, 0, 0),
            8 => new(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
        };
        return orientation == 1 ? source : new TransformedBitmap(source, new MatrixTransform(matrix));
    }
    private static BitmapSource OnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            context.DrawRectangle(Brushes.White, null, rect);
            context.DrawImage(source, rect);
        }
        var bitmap = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
    }
}
