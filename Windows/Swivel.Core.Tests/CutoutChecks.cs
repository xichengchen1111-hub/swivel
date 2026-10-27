using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class CutoutChecks
{
    private static PixelImage Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return new(w, h, px);
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Input: white normalises to (1 − mean) / std per RGB channel; transparency counts as white.
        foreach (var image in new[] { Solid(1000, 700, 255, 255, 255), Solid(37, 501, 9, 9, 9, 0), Solid(1, 1, 255, 255, 255) })
        {
            var input = Cutout.Input(image, default);
            float[] expected = { (1 - 0.485f) / 0.229f, (1 - 0.456f) / 0.224f, (1 - 0.406f) / 0.225f };
            check(input.Length == 3 * 320 * 320 && Enumerable.Range(0, 3).All(c => input.Skip(c * 320 * 320).Take(320 * 320).All(v => Math.Abs(v - expected[c]) < 1e-4)), $"Cutout input of a white/transparent {image.Width}×{image.Height} image");
        }
        // Channel order: a pure red image puts its brightness in channel 0 only.
        var red = Cutout.Input(Solid(64, 64, 0, 0, 255), default);
        check(Math.Abs(red[0] - (1 - 0.485f) / 0.229f) < 1e-4 && Math.Abs(red[320 * 320] - (0 - 0.456f) / 0.224f) < 1e-4 && Math.Abs(red[2 * 320 * 320] - (0 - 0.406f) / 0.225f) < 1e-4, "Cutout input is RGB, divided by the brightest value");
        // A half-black, half-white image keeps its edge in the right place after the resize.
        var half = Solid(640, 100, 255, 255, 255);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 320; x++) { int i = (y * 640 + x) * 4; half.Bgra[i] = half.Bgra[i + 1] = half.Bgra[i + 2] = 0; }
        var halfInput = Cutout.Input(half, default);
        check(halfInput[50 * 320 + 100] < -1.5f && halfInput[50 * 320 + 220] > 1.5f, "Cutout input keeps the image's layout");

        // Mask: min–max normalised, enlarged, refused when nothing stands out.
        var prediction = new float[320 * 320];
        for (int y = 0; y < 320; y++) for (int x = 0; x < 320; x++) prediction[y * 320 + x] = x >= 80 && x < 240 && y >= 80 && y < 240 ? 0.9f : 0.1f;
        var mask = Cutout.Mask(prediction, 1000, 500, default);
        check(mask.Length == 500_000 && mask[250 * 1000 + 500] == 255 && mask[10 * 1000 + 10] == 0 && mask.Count(m => m >= 128) is > 100_000 and < 150_000, "Cutout mask scales the subject to the image");
        var mostly = Enumerable.Repeat(0.9f, 320 * 320).ToArray(); mostly[0] = 0.1f;
        check(Cutout.Mask(mostly, 1, 1, default).Length == 1 && Cutout.Mask(prediction, 16000, 1000, default).Length == 16_000_000, "Cutout mask at the smallest and largest sizes");
        reject(() => Cutout.Mask(Enumerable.Repeat(0.5f, 320 * 320).ToArray(), 10, 10, default), "Cutout refuses a flat prediction");
        reject(() => { var p = (float[])prediction.Clone(); p[5] = float.NaN; Cutout.Mask(p, 10, 10, default); }, "Cutout refuses a prediction with NaN");
        reject(() => { var p = new float[320 * 320]; p[0] = 1; Cutout.Mask(p, 1000, 1000, default); }, "Cutout refuses a subject smaller than a thousandth of the image");
        reject(() => Cutout.Mask(new float[10], 10, 10, default), "Cutout refuses a wrong-sized prediction");
        reject(() => Cutout.Mask(prediction, 20000, 20000, default), "Cutout refuses an oversized image");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            reject(() => Cutout.Mask(prediction, 100, 100, cancelled.Token), "Cutout mask honours cancellation");
            reject(() => Cutout.Input(Solid(100, 100, 1, 2, 3), cancelled.Token), "Cutout input honours cancellation");
        }

        // Apply: alpha multiplied with the source's own, hidden colour cleared.
        var image2 = Solid(4, 1, 10, 20, 30, 200);
        check(Cutout.Apply(image2, new byte[] { 15, 16, 0, 0 }).Bgra is var faint && faint[3] == 0 && faint[0] == 0 && faint[7] == 13, "Cutout drops mask values below the floor");
        var hidden = Solid(40, 1, 255, 255, 255); for (int x = 0; x < 20; x++) { hidden.Bgra[x * 4] = hidden.Bgra[x * 4 + 1] = hidden.Bgra[x * 4 + 2] = 0; hidden.Bgra[x * 4 + 3] = 0; }
        var hiddenBlur = Cutout.BlurBackground(hidden, new byte[40], default);
        check(Enumerable.Range(20, 20).All(x => hiddenBlur.Bgra[x * 4] == 255), "Blur Background never mixes colour hidden under transparency into visible pixels");
        var cut = Cutout.Apply(image2, new byte[] { 255, 128, 0, 255 });
        check(cut.Bgra[3] == 200 && cut.Bgra[7] == 100 && cut.Bgra[11] == 0 && cut.Bgra[8] == 0 && cut.Bgra[9] == 0 && cut.Bgra[10] == 0 && cut.Bgra[4] == 10 && cut.Bgra[15] == 200, "Cutout keeps subject colour, multiplies alpha and clears fully transparent colour");
        check(image2.Bgra[11] == 200, "Cutout leaves the source pixels untouched");

        // Blur: the subject stays exact, the background loses detail and gets none of the subject's colour.
        int w = 400, h = 300;
        var scene = Solid(w, h, 0, 0, 0);
        var subject = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, i = p * 4;
                bool inside = Math.Abs(x - 200) < 60 && Math.Abs(y - 150) < 60;
                subject[p] = inside ? (byte)255 : (byte)0;
                if (inside) { scene.Bgra[i + 2] = 255; } // red subject
                else if (((x / 4) + (y / 4)) % 2 == 0) { scene.Bgra[i] = 255; scene.Bgra[i + 1] = 255; } // cyan checks on black
            }
        var blurred = Cutout.BlurBackground(scene, subject, default);
        bool subjectExact = true; int maxRedOutside = 0; double before = 0, after = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, i = p * 4;
                if (subject[p] == 255) { subjectExact &= blurred.Bgra[i + 2] == 255 && blurred.Bgra[i] == 0; continue; }
                maxRedOutside = Math.Max(maxRedOutside, blurred.Bgra[i + 2]);
                if (x + 1 < w && subject[p + 1] == 0) { before += Math.Abs(scene.Bgra[i] - scene.Bgra[i + 4]); after += Math.Abs(blurred.Bgra[i] - blurred.Bgra[i + 4]); }
            }
        check(subjectExact, "Blur Background keeps the subject exact");
        check(maxRedOutside <= 2, $"Blur Background lets no subject colour into the background (max red {maxRedOutside})");
        check(after < before * 0.2, $"Blur Background smooths the background ({after:F0} vs {before:F0})");
        check(blurred.Bgra.Where((_, i) => i % 4 == 3).All(a => a == 255), "Blur Background keeps alpha");
        // A mask that covers everything returns the image unchanged.
        check(Cutout.BlurBackground(scene, Enumerable.Repeat((byte)255, w * h).ToArray(), default).Bgra.SequenceEqual(scene.Bgra), "Blur Background with no background is a no-op");
        reject(() => Cutout.Apply(image2, new byte[3]), "Cutout refuses a mask of the wrong size");
        check(Catalog.Actions.Any(a => a.Id == "image.removebg" && a.ImageOnly) && Catalog.Actions.Any(a => a.Id == "image.blurbg" && a.ImageOnly) && ImageOutputInfo.Format("image.removebg") == "png", "Remove/Blur Background are image actions writing PNG");
        check(Cutout.ModelBytes == 175_997_641 && Cutout.ModelSha256.Length == 64, "Cutout model pin");
    }
}
