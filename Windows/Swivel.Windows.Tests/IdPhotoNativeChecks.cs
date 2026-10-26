using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class IdPhotoNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: the model mask, Windows' face detector and the JPEG's DPI.
        if (!File.Exists(CutoutModel.ModelPath)) { check(false, "ID Photo needs the pinned background model"); return; }
        string root = Path.Combine(directory, "idphoto"); Directory.CreateDirectory(root);
        int w = 600, h = 800;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool head = (x - 300) * (x - 300) * 4 / 3 + (y - 330) * (y - 330) < 170 * 170, body = y > 520 && Math.Abs(x - 300) < 220 - (800 - y) / 3;
                pixels[i] = head || body ? (byte)60 : (byte)200; pixels[i + 1] = head || body ? (byte)90 : (byte)210; pixels[i + 2] = head || body ? (byte)170 : (byte)120; pixels[i + 3] = 255;
            }
        string photo = Path.Combine(root, "portrait.png");
        using (var file = File.Create(photo)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4))); encoder.Save(file); }

        using var document = IdPhotoDocument.Open(photo, default);
        // A drawn shape is not a face: the detector (or its absence) is reported, never a crash.
        try { document.DetectAsync(default).GetAwaiter().GetResult(); check(false, "ID Photo found a face in a drawn shape"); }
        catch (UserError error) { Console.WriteLine("ID Photo face detector on a drawing: " + error.Message); check(!document.Ready, "ID Photo stays unready without a face"); }
        reject(() => document.Save(new IdPhotoSettings(), default), "ID Photo refuses to save before a face is found");

        // With a face supplied, the saved JPEG has the chosen size and DPI and an opaque blue background.
        double scale = (double)Math.Min(1, IdPhotoDocument.PreviewEdge / (double)Math.Max(w, h));
        document.UseFace(new IdFace(190 * scale, 250 * scale, 220 * scale, 240 * scale));
        var settings = new IdPhotoSettings(413, 579, 350, 0x438EDB);
        check(document.Preview(settings, default) is { PixelHeight: > 0 } preview && preview.PixelHeight <= 480, "ID Photo preview is small");
        string saved = document.Save(settings, default);
        check(saved == Path.Combine(root, "portrait id.jpg"), "ID Photo writes beside the original: " + saved);
        var frame = BitmapFrame.Create(new Uri(saved), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        check(frame.PixelWidth == 413 && frame.PixelHeight == 579 && Math.Abs(frame.DpiX - 350) < 0.5 && Math.Abs(frame.DpiY - 350) < 0.5, $"ID Photo JPEG size and DPI ({frame.PixelWidth}×{frame.PixelHeight}, {frame.DpiX} dpi)");
        var bgr = new byte[413 * 579 * 4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(bgr, 413 * 4, 0);
        check(Math.Abs(bgr[4 * 5] - 0xDB) <= 8 && Math.Abs(bgr[4 * 5 + 1] - 0x8E) <= 8 && Math.Abs(bgr[4 * 5 + 2] - 0x43) <= 8, "ID Photo corner shows the chosen background");
        check(document.Save(settings, default) == Path.Combine(root, "portrait id 2.jpg"), "ID Photo never overwrites an earlier result");
    }
}
