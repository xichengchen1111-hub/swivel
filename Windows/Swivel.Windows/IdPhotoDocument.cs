using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Swivel.Windows;

/// One portrait held open for ID Photo: the subject mask from the pinned background model
/// and the face from Windows' own face detector, both found once on this PC; previews and the
/// saved JPEG are cut from them.
public sealed class IdPhotoDocument : IDisposable
{
    public const int PreviewEdge = 900;
    private const string NoDetector = "Windows face detection is not available on this PC.";
    private readonly ImageTools.Document input;
    private readonly PixelImage full, small;
    private readonly byte[] fullMask, smallMask;
    private readonly IdPhoto.Prepared preview;
    private IdPhoto.Prepared? prepared;
    private IdFace fullFace, smallFace;
    private double fullTop, smallTop;
    public bool Ready { get; private set; }

    private IdPhotoDocument(ImageTools.Document input, PixelImage full, byte[] fullMask, PixelImage small, byte[] smallMask, IdPhoto.Prepared preview)
    { this.input = input; this.full = full; this.fullMask = fullMask; this.small = small; this.smallMask = smallMask; this.preview = preview; }

    /// Reads the image and finds the subject. Runs on an image worker thread.
    public static IdPhotoDocument Open(string path, CancellationToken token)
    {
        var input = ImageTools.Open(path, token);
        try
        {
            var full = ImageTools.ReadPixels(input.Bitmap, 0, token);
            var prediction = CutoutModel.Predict(Cutout.Input(full, token), token);
            var fullMask = Cutout.Mask(prediction, full.Width, full.Height, token);
            var small = ImageTools.ReadPixels(input.Bitmap, PreviewEdge, token);
            var smallMask = Cutout.Mask(prediction, small.Width, small.Height, token);
            return new IdPhotoDocument(input, full, fullMask, small, smallMask, IdPhoto.Prepare(small, smallMask, token));
        }
        catch { input.Dispose(); throw; }
    }

    /// Finds exactly one face with Windows' face detector (on the preview-sized copy).
    public async Task DetectAsync(CancellationToken token)
    {
        if (!FaceDetector.IsSupported || !FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8)) throw new UserError(NoDetector);
        var detector = await NativeAsync.Join(FaceDetector.CreateAsync(), token).ConfigureAwait(false);
        using var color = new SoftwareBitmap(BitmapPixelFormat.Bgra8, small.Width, small.Height, BitmapAlphaMode.Ignore);
        color.CopyFromBuffer(OcrText.White(small).Bgra.AsBuffer());
        using var gray = SoftwareBitmap.Convert(color, BitmapPixelFormat.Gray8);
        var faces = await NativeAsync.Join(detector.DetectFacesAsync(gray), token).ConfigureAwait(false);
        var found = faces.Take(9).Select(f => new IdFace(f.FaceBox.X, f.FaceBox.Y, f.FaceBox.Width, f.FaceBox.Height)).ToArray();
        UseFace(IdPhoto.SingleFace(found));
    }

    /// The face in preview pixels; tests supply one directly.
    internal void UseFace(IdFace face)
    {
        smallFace = face;
        smallTop = IdPhoto.HeadTop(smallMask, small.Width, small.Height, smallFace);
        double sx = (double)full.Width / small.Width, sy = (double)full.Height / small.Height;
        fullFace = new IdFace(smallFace.X * sx, smallFace.Y * sy, smallFace.Width * sx, smallFace.Height * sy);
        fullTop = IdPhoto.HeadTop(fullMask, full.Width, full.Height, fullFace);
        Ready = true;
    }

    /// A small preview of the result, at most 480 pixels tall or wide.
    public BitmapSource Preview(IdPhotoSettings settings, CancellationToken token)
    {
        if (!Ready) throw new InvalidOperationException("Detect the face first.");
        double scale = Math.Min(1, 480.0 / Math.Max(settings.Width, settings.Height));
        int w = Math.Max(1, (int)Math.Round(settings.Width * scale)), h = Math.Max(1, (int)Math.Round(settings.Height * scale));
        var crop = IdPhoto.Crop(w, h, smallFace, smallTop, settings.Zoom, settings.Vertical);
        return Bitmap(IdPhoto.Render(preview, crop, w, h, settings.Background, token), 96);
    }

    /// The full-size JPEG beside the original, carrying the chosen DPI.
    public string Save(IdPhotoSettings settings, CancellationToken token)
    {
        if (!Ready) throw new InvalidOperationException("Detect the face first.");
        IdPhoto.Dimensions(4, settings.Width, settings.Height, false, settings.Dpi);
        prepared ??= IdPhoto.Prepare(full, fullMask, token);
        var crop = IdPhoto.Crop(settings.Width, settings.Height, fullFace, fullTop, settings.Zoom, settings.Vertical);
        var image = Bitmap(IdPhoto.Render(prepared, crop, settings.Width, settings.Height, settings.Background, token), settings.Dpi);
        token.ThrowIfCancellationRequested();
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        byte[] bytes = memory.ToArray();
        return NativeFiles.WriteBeside(input.Input, Path.GetFileNameWithoutExtension(input.Input.Path) + " id", "jpg", stream => stream.Write(bytes), token);
    }

    private static BitmapSource Bitmap(PixelImage image, int dpi)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, dpi, dpi, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }

    public void Dispose() => input.Dispose();
}
