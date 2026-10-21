using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinImaging = Windows.Graphics.Imaging;

namespace Swivel;

/// Applying a saved image recipe (ImagePresets.swift, `ImagePreset.apply`): the model lives in Core/ImagePreset.cs.
static class PresetImaging
{
    /// Resizes (never enlarging), converts and saves a copy named "name processed.ext" beside the original. The
    /// image is upright (EXIF orientation applied, the tag reset to 1), animated images use their first frame, JPG
    /// composites transparency onto white, and metadata is removed or kept as the preset says. Everything is encoded
    /// in memory before anything is written, and the result is moved into place, so a failure leaves no file behind.
    public static string Apply(this ImagePreset preset, string path)
    {
        if (!preset.IsValid) throw new ToolError(L("Check the preset name, size and quality."));
        var name = System.IO.Path.GetFileName(path);
        if (!File.Exists(path)) throw new ToolError(L("Could not read %@", name));

        BitmapMetadata? sourceMetadata = null;
        if (!preset.StripMetadata) sourceMetadata = ReadMetadata(path);

        BitmapSource image;
        try { image = Images.Load(path, preset.LongEdge == 0 ? null : preset.LongEdge); }
        catch (Exception ex) when (ex is not (ToolError or NotImplementedException)) { throw new ToolError(L("Could not read %@", name)); }
        // Images.Load caps the long edge but never enlarges; scale down here too in case a decoder ignored the cap.
        if (preset.LongEdge > 0 && Math.Max(image.PixelWidth, image.PixelHeight) > preset.LongEdge)
            image = ScaleToFit(image, preset.LongEdge);

        bool jpeg = preset.Format == ImagePreset.PresetFormat.Jpeg;
        BitmapSource pixels = jpeg ? Flatten(image) : image;
        byte[] data = preset.Format == ImagePreset.PresetFormat.Heic
            ? EncodeHeic(pixels, preset.Quality, sourceMetadata)
            : Encode(pixels, preset.Format, preset.Quality, sourceMetadata);

        var ext = ImagePreset.FormatExtension(preset.Format);
        return WriteBeside(path, ext, " processed", data);
    }

    /// Writes `data` to a temp file in the same folder, then moves it to a unique "name suffix.ext" — never over an
    /// existing file, even one that appeared after the name was chosen.
    public static string WriteBeside(string source, string ext, string suffix, byte[] data)
    {
        var folder = System.IO.Path.GetDirectoryName(source) ?? ".";
        var temp = System.IO.Path.Combine(folder, $".swivel-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            for (int attempt = 0; ; attempt++)
            {
                var result = Paths.Output(source, ext, suffix);
                try
                {
                    File.Move(temp, result, overwrite: false);
                    return result;
                }
                catch (IOException) when (attempt < 20 && File.Exists(result)) { /* Taken meanwhile: pick the next name. */ }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// The first frame's metadata, or null when the file has none (or it can't be read; the pixels decide that).
    static BitmapMetadata? ReadMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || decoder.Frames[0].Metadata is not BitmapMetadata metadata) return null;
            var copy = metadata.Clone();
            copy.Freeze();
            return copy;
        }
        catch (Exception) { return null; }
    }

    /// Composites transparency onto white (JPG has no alpha channel).
    public static BitmapSource Flatten(BitmapSource image)
    {
        if (!Images.HasAlpha(image)) return image;
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var bytes = new byte[stride * height];
        source.CopyPixels(bytes, stride, 0);
        var output = new byte[width * 3 * height];
        for (int i = 0, o = 0; i < bytes.Length; i += 4, o += 3)
        {
            int a = bytes[i + 3];
            output[o] = (byte)((bytes[i] * a + 255 * (255 - a) + 127) / 255);
            output[o + 1] = (byte)((bytes[i + 1] * a + 255 * (255 - a) + 127) / 255);
            output[o + 2] = (byte)((bytes[i + 2] * a + 255 * (255 - a) + 127) / 255);
        }
        var flat = BitmapSource.Create(width, height, image.DpiX > 0 ? image.DpiX : 96, image.DpiY > 0 ? image.DpiY : 96,
            PixelFormats.Bgr24, null, output, width * 3);
        flat.Freeze();
        return flat;
    }

    static BitmapSource ScaleToFit(BitmapSource image, int longEdge)
    {
        double scale = (double)longEdge / Math.Max(image.PixelWidth, image.PixelHeight);
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }

    static byte[] Encode(BitmapSource pixels, ImagePreset.PresetFormat format, int quality, BitmapMetadata? source)
    {
        BitmapEncoder encoder = format switch
        {
            ImagePreset.PresetFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ImagePreset.PresetFormat.Png => new PngBitmapEncoder(),
            _ => new TiffBitmapEncoder(),
        };
        var container = format switch
        {
            ImagePreset.PresetFormat.Jpeg => "jpg",
            ImagePreset.PresetFormat.Png => "png",
            _ => "tiff",
        };
        var metadata = source is null ? null : CarryMetadata(source, container, pixels.PixelWidth, pixels.PixelHeight);
        try
        {
            return EncodeFrame(encoder, pixels, metadata);
        }
        catch (Exception) when (metadata != null)
        {
            // A metadata block this encoder refuses: keep what the common properties can carry.
            var common = CommonMetadata(source!, container);
            try
            {
                var retry = format switch
                {
                    ImagePreset.PresetFormat.Jpeg => (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
                    ImagePreset.PresetFormat.Png => new PngBitmapEncoder(),
                    _ => new TiffBitmapEncoder(),
                };
                return EncodeFrame(retry, pixels, common);
            }
            catch (Exception) { throw new ToolError(L("Could not encode the image.")); }
        }
        catch (Exception ex) when (ex is not ToolError) { throw new ToolError(L("Could not encode the image.")); }
    }

    static byte[] EncodeFrame(BitmapEncoder encoder, BitmapSource pixels, BitmapMetadata? metadata)
    {
        encoder.Frames.Add(BitmapFrame.Create(pixels, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// The source's metadata for the output container: the whole block when the container is the same (orientation
    /// reset to 1 and the EXIF pixel size updated), otherwise what the common photo properties carry across formats.
    static BitmapMetadata? CarryMetadata(BitmapMetadata source, string container, int width, int height)
    {
        if (string.Equals(source.Format, container, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var copy = source.Clone();
                var (ifd, exif) = container switch
                {
                    "jpg" => ("/app1/ifd", "/app1/ifd/exif"),
                    "tiff" => ("/ifd", "/ifd/exif"),
                    _ => ("", ""),
                };
                if (ifd.Length > 0)
                {
                    SetIfPresent(copy, ifd + "/{ushort=274}", (ushort)1);
                    SetIfPresent(copy, exif + "/{ushort=40962}", (uint)width);
                    SetIfPresent(copy, exif + "/{ushort=40963}", (uint)height);
                }
                foreach (var xmp in new[] { "/xmp/tiff:Orientation", "/app1/ifd/xmp/tiff:Orientation", "/ifd/xmp/tiff:Orientation" })
                    SetIfPresent(copy, xmp, "1");
                return copy;
            }
            catch (Exception) { /* Fall through to the common properties. */ }
        }
        return CommonMetadata(source, container);
    }

    static void SetIfPresent(BitmapMetadata metadata, string query, object value)
    {
        try
        {
            if (metadata.ContainsQuery(query)) metadata.SetQuery(query, value);
        }
        catch (Exception) { }
    }

    /// Author, title, comment, keywords, copyright, camera, date and rating, plus the EXIF and GPS blocks where both
    /// containers have them (JPG and TIFF).
    static BitmapMetadata? CommonMetadata(BitmapMetadata source, string container)
    {
        BitmapMetadata target;
        try { target = new BitmapMetadata(container); } catch (Exception) { return null; }
        bool any = false;
        void copy(Func<BitmapMetadata, object?> get, Action<BitmapMetadata, object> set)
        {
            try
            {
                if (get(source) is { } value && !(value is string s && s.Length == 0))
                {
                    set(target, value);
                    any = true;
                }
            }
            catch (Exception) { }
        }
        copy(m => m.Author, (m, v) => m.Author = (System.Collections.ObjectModel.ReadOnlyCollection<string>)v);
        copy(m => m.Title, (m, v) => m.Title = (string)v);
        copy(m => m.Subject, (m, v) => m.Subject = (string)v);
        copy(m => m.Comment, (m, v) => m.Comment = (string)v);
        copy(m => m.Keywords, (m, v) => m.Keywords = (System.Collections.ObjectModel.ReadOnlyCollection<string>)v);
        copy(m => m.Copyright, (m, v) => m.Copyright = (string)v);
        copy(m => m.CameraManufacturer, (m, v) => m.CameraManufacturer = (string)v);
        copy(m => m.CameraModel, (m, v) => m.CameraModel = (string)v);
        copy(m => m.DateTaken, (m, v) => m.DateTaken = (string)v);
        copy(m => m.ApplicationName, (m, v) => m.ApplicationName = (string)v);
        copy(m => m.Rating == 0 ? null : m.Rating, (m, v) => m.Rating = (int)v);

        string? Block(string format, string name) => format switch
        {
            "jpg" => "/app1/ifd/" + name,
            "tiff" => "/ifd/" + name,
            _ => null,
        };
        foreach (var block in new[] { "exif", "gps" })
        {
            if (Block(source.Format, block) is not { } from || Block(container, block) is not { } to) continue;
            try
            {
                if (source.ContainsQuery(from) && source.GetQuery(from) is BitmapMetadata value)
                {
                    target.SetQuery(to, value);
                    any = true;
                }
            }
            catch (Exception) { }
        }
        return any ? target : null;
    }

    /// HEIC through Windows' HEIF encoder (the HEIF and HEVC Video Extensions from the Microsoft Store).
    static byte[] EncodeHeic(BitmapSource pixels, int quality, BitmapMetadata? source)
    {
        try
        {
            return Task.Run(async () =>
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var options = new List<KeyValuePair<string, WinImaging.BitmapTypedValue>>
                {
                    new("ImageQuality", new WinImaging.BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single)),
                };
                var encoder = await WinImaging.BitmapEncoder.CreateAsync(WinImaging.BitmapEncoder.HeifEncoderId, stream, options);
                using var bitmap = Bitmaps.ToSoftwareBitmap(pixels);
                encoder.SetSoftwareBitmap(bitmap);
                if (source != null) await CopyCommonProperties(source, encoder.BitmapProperties);
                await encoder.FlushAsync();
                var bytes = new byte[stream.Size];
                stream.Seek(0);
                await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
                return bytes;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not ToolError)
        {
            throw new ToolError(L("This image format is not available on this PC."));
        }
    }

    static async Task CopyCommonProperties(BitmapMetadata source, WinImaging.BitmapProperties target)
    {
        var values = new List<KeyValuePair<string, WinImaging.BitmapTypedValue>>();
        void add(string key, Func<object?> get, Windows.Foundation.PropertyType type)
        {
            try
            {
                if (get() is { } value && !(value is string s && s.Length == 0)) values.Add(new(key, new WinImaging.BitmapTypedValue(value, type)));
            }
            catch (Exception) { }
        }
        add("System.Title", () => source.Title, Windows.Foundation.PropertyType.String);
        add("System.Comment", () => source.Comment, Windows.Foundation.PropertyType.String);
        add("System.Copyright", () => source.Copyright, Windows.Foundation.PropertyType.String);
        add("System.Author", () => source.Author?.ToArray(), Windows.Foundation.PropertyType.StringArray);
        add("System.Keywords", () => source.Keywords?.ToArray(), Windows.Foundation.PropertyType.StringArray);
        add("System.Photo.CameraManufacturer", () => source.CameraManufacturer, Windows.Foundation.PropertyType.String);
        add("System.Photo.CameraModel", () => source.CameraModel, Windows.Foundation.PropertyType.String);
        foreach (var value in values)
        {
            try { await target.SetPropertiesAsync([value]); } catch (Exception) { /* This encoder doesn't take that property. */ }
        }
    }
}

/// Conversions between WPF bitmaps and WinRT SoftwareBitmaps (for Windows.Graphics.Imaging and Windows.Media.Ocr).
static class Bitmaps
{
    /// A BGRA8 premultiplied SoftwareBitmap with the same pixels.
    public static WinImaging.SoftwareBitmap ToSoftwareBitmap(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var bytes = new byte[stride * height];
        source.CopyPixels(bytes, stride, 0);
        return WinImaging.SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), WinImaging.BitmapPixelFormat.Bgra8, width, height,
            WinImaging.BitmapAlphaMode.Premultiplied);
    }
}

/// Image presets from the Wheel, Quick Actions and the settings window.
static class PresetActions
{
    /// Shows the files that failed (title, details). Tests replace it so no message box appears.
    public static Action<string, string> ReportFailures { get; set; } = (title, details) =>
    {
        if (Application.Current is { } app)
            app.Dispatcher.Invoke(() => MessageBox.Show(details, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    };

    public static Task<ActionResult> Run(ImagePreset preset, IReadOnlyList<string> paths) => Run(preset, paths, progress: null);

    /// `progress` hears (files finished, total) before each file, so a caller can show how far along it is.
    public static async Task<ActionResult> Run(ImagePreset preset, IReadOnlyList<string> paths, Action<int, int>? progress)
    {
        if (!Feature.ImagePresets.IsOn()) throw new ToolError(L("This feature is turned off."));
        var output = new List<string>();
        var failures = new List<string>();
        for (int index = 0; index < paths.Count; index++)
        {
            progress?.Invoke(index, paths.Count);
            var path = paths[index];
            try { output.Add(await Task.Run(() => preset.Apply(path))); }
            catch (Exception ex) { failures.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (failures.Count == 0) return Catalog.Saved(output);
        var message = L("Saved %d files; %d could not be processed.", output.Count, failures.Count);
        ReportFailures(message, string.Join("\n", failures.Take(8)));
        return new ActionResult(message, output);
    }
}
