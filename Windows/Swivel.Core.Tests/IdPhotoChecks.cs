using System;
using System.Linq;
using Swivel.Core;

internal static class IdPhotoChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(IdPhoto.Dimensions(0, 0, 0, false, 300) == (295, 413) && IdPhoto.Dimensions(1, 0, 0, false, 300) == (413, 579) && IdPhoto.Dimensions(3, 0, 0, false, 300) == (600, 600), "ID Photo fixed pixel presets");
        check(IdPhoto.Dimensions(2, 0, 0, false, 300) == (390, 567) && IdPhoto.Dimensions(2, 0, 0, false, 600) == (780, 1134), "ID Photo passport size follows the DPI");
        check(IdPhoto.Dimensions(4, 35, 45, true, 300) == (413, 531) && IdPhoto.Dimensions(4, 4000, 4000, false, 72) == (4000, 4000), "ID Photo custom pixels and millimetres");
        foreach (var bad in new (int, double, double, bool, int)[] { (4, 0, 10, false, 300), (4, 4001, 10, false, 300), (4, 10, double.NaN, false, 300), (0, 0, 0, false, 71), (0, 0, 0, false, 1201), (5, 1, 1, false, 300), (4, 400, 400, true, 300) })
            reject(() => IdPhoto.Dimensions(bad.Item1, bad.Item2, bad.Item3, bad.Item4, bad.Item5), $"ID Photo size refused: {bad}");
        reject(() => IdPhoto.SingleFace(Array.Empty<IdFace>()), "ID Photo needs a face");
        reject(() => IdPhoto.SingleFace(new[] { new IdFace(0, 0, 50, 50), new IdFace(100, 0, 50, 50) }), "ID Photo refuses two faces");
        reject(() => IdPhoto.SingleFace(new[] { new IdFace(0, 0, 2, 2) }), "ID Photo refuses a speck");

        // Head top from the mask: a head (hair) reaching 60 px above a face box at y 100.
        int w = 200, h = 300;
        var mask = new byte[w * h];
        for (int y = 40; y < 260; y++) for (int x = 50; x < 150; x++) mask[y * w + x] = 255;
        var face = new IdFace(60, 100, 80, 100);
        check(IdPhoto.HeadTop(mask, w, h, face) == 40, "ID Photo head top follows the subject mask");
        check(Math.Abs(IdPhoto.HeadTop(new byte[w * h], w, h, face) - 60) < 1e-9, "ID Photo head top falls back to the face box");
        var tall = new byte[w * h]; for (int y = 0; y < 260; y++) for (int x = 50; x < 150; x++) tall[y * w + x] = 255;
        check(IdPhoto.HeadTop(tall, w, h, face) <= 0, "ID Photo head top when the subject reaches the photo's top edge");

        var backdrop = new byte[w * h]; for (int y = 20; y < 300; y++) for (int x = 50; x < 150; x++) backdrop[y * w + x] = 255;
        check(Math.Abs(IdPhoto.HeadTop(backdrop, w, h, new IdFace(60, 200, 80, 60)) - 176) < 1e-9, "ID Photo head top ignores a subject reaching past the search limit");
        // Crop: head 70% of the height with its top 13% down, centred.
        var crop = IdPhoto.Crop(295, 413, face, 40, 1, 0);
        double head = 200 - 40, scale = 413 / crop.Height;
        check(Math.Abs(head * scale - 413 * 0.7) < 1e-6 && Math.Abs((40 - crop.Y) / crop.Height - 0.13) < 1e-9 && Math.Abs(crop.X + crop.Width / 2 - 100) < 1e-9 && Math.Abs(crop.Width / crop.Height - 295.0 / 413) < 1e-9, "ID Photo crop places the head");
        var zoomed = IdPhoto.Crop(295, 413, face, 40, 1.5, 0);
        check(Math.Abs(zoomed.Height - crop.Height / 1.5) < 1e-9 && Math.Abs(zoomed.Y + zoomed.Height * 0.48 - 120) < 1e-9, "ID Photo zoom enlarges around the head's centre");
        var up = IdPhoto.Crop(295, 413, face, 40, 1, 0.1);
        check(up.Y > crop.Y && Math.Abs(up.Y - crop.Y - 0.1 * crop.Height) < 1e-9, "ID Photo vertical moves the person up");
        reject(() => IdPhoto.Crop(295, 413, face, double.NaN, 1, 0), "ID Photo crop refuses a bad head top");

        // Render: a red subject on a green background, with a half-mixed edge column.
        var pixels = new byte[w * h * 4]; var subject = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, i = p * 4; pixels[i + 3] = 255;
                if (x >= 60 && x < 140) { pixels[i + 2] = 220; subject[p] = 255; }
                else if (x == 59) { pixels[i + 2] = 110; pixels[i + 1] = 100; subject[p] = 128; }
                else pixels[i + 1] = 200;
            }
        var prepared = IdPhoto.Prepare(new PixelImage(w, h, pixels), subject, default);
        var same = IdPhoto.Render(prepared, new IdCrop(0, 0, w, h), w, h, 0xFFFFFF, default);
        int At(int x, int y, int c) => same.Bgra[(y * w + x) * 4 + c];
        check(At(100, 150, 2) == 220 && At(100, 150, 1) == 0 && At(10, 150, 0) == 255 && At(10, 150, 1) == 255 && At(10, 150, 2) == 255, "ID Photo keeps the subject exact and fills the background");
        check(At(59, 150, 2) is > 225 and < 245 && At(59, 150, 1) is > 115 and < 140 && At(59, 150, 0) is > 115 and < 140, $"ID Photo edge swaps the old green for white ({At(59, 150, 2)},{At(59, 150, 1)},{At(59, 150, 0)})");
        var blue = IdPhoto.Render(prepared, new IdCrop(-100, -100, w + 200, h + 200), 100, 125, 0x438EDB, default);
        check(blue.Bgra[0] == 0xDB && blue.Bgra[1] == 0x8E && blue.Bgra[2] == 0x43 && blue.Bgra[3] == 255 && blue.Bgra.Where((_, i) => i % 4 == 3).All(a => a == 255), "ID Photo pads outside the photo with the background and is opaque");
        reject(() => IdPhoto.Render(prepared, crop, 4001, 10, 0, default), "ID Photo refuses an oversized output");
        reject(() => IdPhoto.Render(prepared, crop, 10, 10, 0x1000000, default), "ID Photo refuses an invalid colour");
        using (var cancelled = new System.Threading.CancellationTokenSource())
        {
            cancelled.Cancel();
            reject(() => IdPhoto.Render(prepared, crop, 295, 413, 0xFFFFFF, cancelled.Token), "ID Photo render honours cancellation");
        }
        check(Catalog.Actions.Any(a => a.Id == "image.idphoto" && a.SingleFile && a.ImageOnly), "ID Photo catalog entry");
    }
}
