using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ImageRecipeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string path = Path.Combine(directory, "crop colors.png");
        const int width = 128, height = 96;
        var pixels = new byte[width * height * 4];
        var random = new Random(42); random.NextBytes(pixels);
        for (int i = 0; i < width * height; i++) pixels[i * 4 + 3] = (byte)(i % 3 == 0 ? 0 : 255);
        var source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var output = File.Create(path)) encoder.Save(output);
        byte[] original = File.ReadAllBytes(path);
        using (var document = ImageTools.Open(path, CancellationToken.None))
        {
            check(document.Bitmap.IsFrozen, "Preview is immutable and can cross STA boundaries");
            reject(() => File.WriteAllBytes(path, new byte[1]), "Crop lease prevents original changing before save");
            reject(() => File.Move(path, path + ".moved"), "Crop lease pins source identity");
            var rect = new PixelCrop(17, 11, 31, 23);
            string crop = ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: rect);
            var decoded = Decode(crop);
            check(decoded.PixelWidth == rect.Width && decoded.PixelHeight == rect.Height, "Cropped dimensions match preview selection");
            var expected = new byte[rect.Width * rect.Height * 4];
            document.Bitmap.CopyPixels(new System.Windows.Int32Rect(rect.X, rect.Y, rect.Width, rect.Height), expected, rect.Width * 4, 0);
            var actual = Pixels(decoded);
            check(expected.SequenceEqual(actual), "Every cropped BGRA pixel equals frozen preview including transparency");
            check(Math.Abs(decoded.DpiX - 96) < 0.1 && Path.GetExtension(crop) == ".png", "Crop metadata/DPI policy and format");
            string second = ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: rect);
            check(crop != second && File.Exists(crop), "Repeated crop never overwrites earlier output");
            int count = Directory.GetFiles(directory).Length;
            reject(() => ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: new PixelCrop(127, 95, 2, 2)), "Invalid crop refused before output");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            reject(() => ImageTools.Export(document, "image.crop", 1920, cancelled.Token, crop: rect), "Cancelled crop creates no output");
            check(Directory.GetFiles(directory).Length == count, "Invalid and cancelled crops leak no files");
            var full = Decode(ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: new PixelCrop(0, 0, width, height)));
            check(Pixels(full).SequenceEqual(Pixels(document.Bitmap)), "Full-image crop preserves all normalized pixels");
            var last = Decode(ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: new PixelCrop(width - 1, height - 1, 1, 1)));
            check(Pixels(last).SequenceEqual(Pixels(document.Bitmap)[^4..]), "Bottom-right one-pixel crop");
        }
        // The same colored fixture with EXIF orientation 6 must be upright before crop math.
        string oriented = Path.Combine(directory, "crop orientation.tiff");
        var metadata = new BitmapMetadata("tiff"); metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
        var tiff = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        tiff.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using (var stream = File.Create(oriented)) tiff.Save(stream);
        using (var document = ImageTools.Open(oriented, CancellationToken.None))
        {
            check(document.Bitmap.PixelWidth == height && document.Bitmap.PixelHeight == width, "Crop preview uses upright EXIF dimensions");
            // Upright top-left comes from the stored bottom-left pixel (90 degrees clockwise).
            var rotated = Pixels(document.Bitmap); var originalDecoded = Pixels(Decode(oriented));
            check(rotated[..4].SequenceEqual(originalDecoded[((height - 1) * width * 4)..(((height - 1) * width + 1) * 4)]), "EXIF 6 pixel mapping independent of crop implementation");
            string cropped = ImageTools.Export(document, "image.crop", 1920, CancellationToken.None, crop: new PixelCrop(0, 0, 1, 1));
            check(Pixels(Decode(cropped)).SequenceEqual(rotated[..4]), "Crop applies after EXIF orientation, not before");
        }
        string low = ImageTools.Run(path, "image.compress", 1920, CancellationToken.None, quality: 1);
        string high = ImageTools.Run(path, "image.compress", 1920, CancellationToken.None, quality: 100);
        check(new FileInfo(low).Length < new FileInfo(high).Length, "Quality control reaches JPEG encoder for noisy fixture");
        foreach (string result in new[] { low, high })
        {
            var image = Decode(result);
            check(image.PixelWidth == width && image.PixelHeight == height && Path.GetExtension(result) == ".jpg", "JPEG compression keeps dimensions");
        }
        reject(() => ImageTools.Run(path, "image.compress", 1920, CancellationToken.None, quality: 0), "Compression rejects invalid quality");
        string id = "preset." + Guid.NewGuid().ToString("N");
        foreach (string format in ImageRecipe.Formats)
        {
            if (format == "gif")
            {
                // Random colors and resampled alpha edges exceed GIF's 256 colors/binary alpha: refused, never flattened.
                int before = Directory.GetFiles(directory, "*.gif").Length;
                reject(() => ImageTools.Run(path, id, 1920, CancellationToken.None, new ImageRecipe(format, 32, 44)), "GIF preset refuses partial alpha and too many colors");
                check(Directory.GetFiles(directory, "*.gif").Length == before, "Refused GIF preset leaves no output");
                continue;
            }
            string output = ImageTools.Run(path, id, 1920, CancellationToken.None, new ImageRecipe(format, 32, 44));
            var image = Decode(output);
            check(image.PixelWidth == 32 && image.PixelHeight == 24 && Path.GetExtension(output) == "." + format, "Preset format and fitted dimensions: " + format);
        }
        string unchangedSize = ImageTools.Run(path, id, 1920, CancellationToken.None, new ImageRecipe("png"));
        check(Decode(unchangedSize).PixelWidth == width, "Preset with no size limit preserves dimensions");
        string neutral = ImageTools.Run(path,id,1920,CancellationToken.None,new("png",Adjustments:new()));
        check(Pixels(Decode(neutral)).SequenceEqual(Pixels(Decode(unchangedSize))),"Neutral photo preset preserves every normalized pixel");
        string mono = ImageTools.Run(path,id,1920,CancellationToken.None,new("png",Adjustments:new(Saturation:-1)));
        var monochrome = Pixels(Decode(mono)); var baseline = Pixels(Decode(unchangedSize)); bool grayscale = true, alpha = true;
        for (int i = 0; i < monochrome.Length; i+=4) { grayscale &= baseline[i+3] == 0 ? monochrome.AsSpan(i,3).SequenceEqual(baseline.AsSpan(i,3)) : monochrome[i] == monochrome[i+1] && monochrome[i] == monochrome[i+2]; alpha &= monochrome[i+3] == baseline[i+3]; } // Fully transparent pixels keep their hidden RGB by design.
        check(grayscale && alpha,"Photo recipe runs adjustments while preserving alpha");
        string smallMono = ImageTools.Run(path,id,1920,CancellationToken.None,new("jpg",32,80,new(Saturation:-1)));
        check(Decode(smallMono).PixelWidth == 32 && Decode(smallMono).PixelHeight == 24,"Photo recipe combines adjustments resize and output format");
        var autoRecipe = new ImageRecipe("png",Adjustments:new(Contrast:.2,Look:PhotoLook.Film,LookStrength:.5),AutoPerImage:true);
        string automatic = ImageTools.Run(path,id,1920,CancellationToken.None,autoRecipe);
        using (var presetSource = ImageTools.Open(path,CancellationToken.None))
        {
            var originalPixels = ImageTools.ReadPixels(presetSource.Bitmap,0,CancellationToken.None);
            var expected = PhotoAdjust.Apply(originalPixels,autoRecipe.ResolvePhoto(originalPixels),CancellationToken.None);
            check(Pixels(Decode(automatic)).SequenceEqual(expected.Bgra),"Per-image Auto export uses original normalized pixels and preserves other recipe settings");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original), "All batch 2 operations preserve original bytes");
        check(!Directory.GetFiles(directory, ".swivel-*.tmp").Any(), "Batch 2 leaves no staging files");
        // All editor leases were disposed; the source is now renameable again.
        File.Move(path, path + ".moved"); File.Move(path + ".moved", path);
        check(File.Exists(path), "Closing preview releases file lease");
    }
    private static BitmapFrame Decode(string path)
    {
        using var input = File.OpenRead(path);
        return BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
}
