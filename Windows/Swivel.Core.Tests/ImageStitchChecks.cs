using System;
using System.Linq;
using Swivel.Core;

internal static class ImageStitchChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Sizes: the narrower width, each height in proportion, never enlarged.
        check(ImageStitch.Size(800, 600, 400, 100) == (400, 300, 100), "The wider image is scaled down to the narrower one's width");
        check(ImageStitch.Size(400, 100, 800, 600) == (400, 100, 300), "Either order matches the narrower width");
        check(ImageStitch.Size(300, 200, 300, 50) == (300, 200, 50), "Equal widths keep both images as they are");
        check(ImageStitch.Size(3, 3, 2, 9) == (2, 2, 9) && ImageStitch.Size(4, 1, 2, 5) == (2, 1, 5) && ImageStitch.Size(1000, 1, 10, 10) == (10, 1, 10),
            "Heights round half up and never reach zero");
        check(ImageStitch.Size(1, 16384, 1, 16384) == (1, 16384, 16384), "Two 16,384-pixel-tall images fit the 32,768 limit");
        reject(() => ImageStitch.Size(0, 5, 5, 5), "An empty image is refused");
        reject(() => ImageStitch.Size(1, 20000, 1, 20000), "A result taller than 32,768 pixels is refused");
        reject(() => ImageStitch.Size(6000, 6000, 6000, 1000), "More than 40 million source pixels is refused");
        reject(() => ImageStitch.Size(32769, 1, 1, 1), "A side over 32,768 is refused");
        string? message = null; try { ImageStitch.Size(1, 20000, 1, 20000); } catch (UserError error) { message = error.Message; }
        check(message == ImageStitch.TooLarge && Strings.Table.ContainsKey(ImageStitch.TooLarge) && Strings.Table.ContainsKey(ImageStitch.ChooseTwo), "The size refusal is a translated user message");

        // Stacking: upper rows, then lower rows.
        var top = new PixelImage(2, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var bottom = new PixelImage(2, 2, Enumerable.Range(10, 16).Select(i => (byte)i).ToArray());
        var joined = ImageStitch.Stack(top, bottom);
        check(joined.Width == 2 && joined.Height == 3 && joined.Bgra.Take(8).SequenceEqual(top.Bgra) && joined.Bgra.Skip(8).SequenceEqual(bottom.Bgra), "The upper image's rows come first, then the lower's");
        reject(() => ImageStitch.Stack(top, new PixelImage(3, 1, new byte[12])), "Images of different widths are not stacked");
        reject(() => ImageStitch.Stack(top, bottom, new System.Threading.CancellationToken(true)), "Stacking stops when cancelled");

        // Hover: only the lower half of another print, held half a second.
        var hover = new StitchHover(); var t = TimeSpan.FromSeconds(10);
        check(StitchHover.InZone(50, 100) && StitchHover.InZone(100, 100) && !StitchHover.InZone(49.9, 100) && !StitchHover.InZone(101, 100) && !StitchHover.InZone(80, 0), "The stitch zone is the lower half of the print");
        check(hover.Update("a.png", "b.png", 80, 100, t) == StitchHoverState.Holding && hover.Update("a.png", "b.png", 70, 100, t + TimeSpan.FromSeconds(0.49)) == StitchHoverState.Holding,
            "Resting on the lower half first holds");
        check(hover.Update("a.png", "b.png", 90, 100, t + TimeSpan.FromSeconds(0.5)) == StitchHoverState.Ready && hover.IsReady(t + TimeSpan.FromSeconds(0.5)), "Half a second later it is ready, while moving within the half");
        check(hover.Update("a.png", "b.png", 30, 100, t + TimeSpan.FromSeconds(0.6)) == StitchHoverState.None && hover.Target is null
            && hover.Update("a.png", "b.png", 80, 100, t + TimeSpan.FromSeconds(0.7)) == StitchHoverState.Holding, "Moving to the upper half cancels; coming back starts over");
        check(hover.Update("a.png", "c.png", 80, 100, t + TimeSpan.FromSeconds(1.5)) == StitchHoverState.Holding, "Another print starts over");
        check(hover.Update("a.png", "A.PNG", 80, 100, t + TimeSpan.FromSeconds(3)) == StitchHoverState.None, "A print never stitches onto itself (paths compared without case)");
        hover.Update("a.png", "b.png", 80, 100, t); hover.Reset();
        check(!hover.IsReady(t + TimeSpan.FromSeconds(5)) && hover.Update("a.png", "b.png", 80, 100, t + TimeSpan.FromSeconds(5)) == StitchHoverState.Holding, "Leaving the print resets the hold");

        foreach (string text in new[] { "Stitch Another Print Below", "Stitch Images", "Stitching images…", "Release to save {1} below {0} as a new image.", "Keep holding to stitch {1} below {0}…",
            "Not stitched: hold over the lower half of a print for half a second before releasing.", "Saved {0} beside the upper image. The originals were not changed; hang them again to see them on the line." })
            check(Strings.Table.TryGetValue(text, out var translated) && translated.All(x => !text.Contains("{1}") || x.Contains("{1}") && x.Contains("{0}")), "Stitch text translated with its placeholders: " + text);
    }
}
