using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class ImageExtraChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        check(new ResizeOptions(ResizeBasis.Width, 500).Size(1000, 2000) == (500,1000), "Resize by width preserves aspect");
        check(new ResizeOptions(ResizeBasis.Height, 500).Size(1000, 2000) == (250,500), "Resize by height preserves aspect");
        check(new ResizeOptions(ResizeBasis.Percent, 25).Size(1000, 2000) == (250,500), "Percent resize");
        check(new ResizeOptions(ResizeBasis.Percent, 200).Size(1000, 2000) == (1000,2000), "Enlargement requires opt in");
        check(new ResizeOptions(ResizeBasis.Percent, 200, true).Size(1000, 2000) == (2000,4000), "Explicit enlargement");
        check(new ResizeOptions(ResizeBasis.Percent, .1).Size(1, 16384) == (1,16), "Tiny dimensions retain a pixel");
        reject(() => new ResizeOptions(ResizeBasis.Percent, 400, true).Size(4000, 4000), "Enlargement cannot bypass pixel budget");
        foreach (double value in new[] { double.NaN, 0, 8193, 32.5 }) reject(() => new ResizeOptions(Value:value).Validate(), "Invalid resize options");
        int calls = 0;
        var result = JpegBudget.Encode(5000, quality => { calls++; return new byte[quality * 100]; }, CancellationToken.None);
        check(result.Length == 5000 && calls <= 9, "Measured JPEG budget and bounded encodes");
        result = JpegBudget.Encode(5000, quality => new byte[quality == 50 ? 4999 : quality * 110], CancellationToken.None);
        check(result.Length <= 5000, "Nonmonotonic JPEG sizes never exceed target");
        reject(() => JpegBudget.Encode(4096, _ => new byte[5000], CancellationToken.None), "Unmet budget refuses output");
        reject(() => JpegBudget.Encode(1, _ => Array.Empty<byte>(), CancellationToken.None), "Invalid byte target");
        using var cancellation = new CancellationTokenSource();
        reject(() => JpegBudget.Encode(4096, _ => { cancellation.Cancel(); return new byte[1]; }, cancellation.Token), "Cancel between codec passes");

        // An agent's ceiling: quality first, then 15% shorter long edges down to 320 pixels.
        var roomy = JpegBudget.Fit(1_000_000_000, 1000, 500, (w, h, q) => new byte[w * h * q / 1000], CancellationToken.None);
        check(roomy.Quality == 100 && (roomy.Width, roomy.Height) == (1000, 500), "Fit keeps full quality and size when they fit");
        var lowered = JpegBudget.Fit(20_000, 1000, 500, (w, h, q) => new byte[w * h * q / 1000], CancellationToken.None);
        check(lowered.Quality == 40 && lowered.Data.Length == 20_000 && (lowered.Width, lowered.Height) == (1000, 500), "Fit finds the highest fitting quality before shrinking");
        var sizes = new System.Collections.Generic.List<int>();
        var shrunk = JpegBudget.Fit(20_000, 1000, 500, (w, h, q) => { if (q == 1) sizes.Add(w); return new byte[w * h / 10 + q]; }, CancellationToken.None);
        check(shrunk.Width == 613 && shrunk.Data.Length <= 20_000 && Math.Abs(shrunk.Height - 306.5) <= 1 && sizes.SequenceEqual(new[] { 1000, 850, 722, 613 }), "Fit shrinks the long edge in 15% steps only as far as needed");
        int tries = 0;
        try { JpegBudget.Fit(5000, 4000, 3000, (w, h, q) => { tries++; return new byte[1_000_000]; }, CancellationToken.None); check(false, "Fit refuses an unreachable target"); }
        catch (McpToolError error) { check(error.Message.Contains("1,000,000 bytes", StringComparison.Ordinal) && tries < 20, "Fit refuses an unreachable target, naming the smallest size, after a bounded number of tries"); }
        tries = 0;
        try { JpegBudget.Fit(5000, 200, 100, (w, h, q) => { tries++; return new byte[9000]; }, CancellationToken.None); check(false, "Fit never shrinks an image below 320 pixels"); }
        catch (McpToolError) { check(tries == 1, "Fit never shrinks an image already under 320 pixels"); }
        var tall = JpegBudget.Fit(200_000, 1170, 13000, (w, h, q) => new byte[w * h / 10 + q], CancellationToken.None);
        check(tall.Height == 4165 && tall.Width == 375 && tall.Data.Length <= 200_000, "Fit shrinks images with a long edge above 8192 (a 13,000-pixel screenshot)");
        reject(() => JpegBudget.Fit(0, 100, 100, (w, h, q) => new byte[1], CancellationToken.None), "Fit refuses a zero target");
        reject(() => JpegBudget.Fit(5000, 0, 100, (w, h, q) => new byte[1], CancellationToken.None), "Fit refuses an empty image");
        using var stopFit = new CancellationTokenSource();
        reject(() => JpegBudget.Fit(5000, 4000, 3000, (w, h, q) => { stopFit.Cancel(); return new byte[1_000_000]; }, stopFit.Token), "Fit stops when cancelled between encodes");

        // Videos: total rate from length (5% for the container), AAC sound, smaller pictures at low rates.
        var big = VideoBudget.Plan(50_000_000, new VideoProbe(1920, 1080, 100, true, 0));
        check(big == new VideoTarget(3_671_688, 128_000, 1920, 1080), "Video budget at a roomy target: " + big);
        check(VideoBudget.Plan(50_000_000, new VideoProbe(1920, 1080, 100, true, 2_000_000)) == new VideoTarget(2_000_000, 128_000, 1920, 1080), "Video budget never exceeds the original's rate, and keeps its size when there is room");
        check(VideoBudget.Plan(50_000_000, new VideoProbe(1920, 1080, 60, true, 400_000)) == new VideoTarget(400_000, 128_000, 1920, 1080), "Video budget keeps a low-rate screen recording's size");
        check(VideoBudget.Plan(3_500_000, new VideoProbe(1920, 1080, 100, true, 0)) == new VideoTarget(169_688, 96_000, 640, 360), "Video budget at a low rate: smaller picture and 96 kbit/s sound");
        check(VideoBudget.Plan(9_210_527, new VideoProbe(1080, 1920, 100, false, 0)) is { AudioBitrate: 0, Width: 480, Height: 854 }, "Video budget caps the long edge of a portrait video, no sound");
        check(VideoBudget.Plan(500_000_000, new VideoProbe(1001, 563, 100, false, 0)) is { Width: 1000, Height: 562 }, "Video budget keeps even sides");
        check(VideoBudget.Plan(500_000_000, new VideoProbe(8192, 2, 1, false, 0)) is { Width: 1920, Height: 2 }, "Video budget keeps at least two pixels per side");
        check(VideoBudget.MinimumBytes(new VideoProbe(640, 360, 10, false, 0)) == 201_465 && VideoBudget.MinimumBytes(new VideoProbe(640, 360, 60, true, 0)) == 1_946_202, "Video budget's smallest target, with 4 KB for the container");
        int refusedMinimums = 0;
        for (int ms = 1; ms <= 20_000; ms++)
            foreach (bool sound in new[] { false, true })
            {
                var length = new VideoProbe(640, 360, ms / 1000.0, sound, 0);
                try { VideoBudget.Plan(VideoBudget.MinimumBytes(length), length); } catch (McpToolError) { refusedMinimums++; }
            }
        check(refusedMinimums == 0, "Video budget accepts the smallest target it names for every whole-millisecond length up to 20 s: " + refusedMinimums);
        reject(() => VideoBudget.Plan(3_000_000, new VideoProbe(1920, 1080, 100, true, 0)), "Video budget refuses a target too small for the length");
        check(VideoBudget.Plan(201_465, new VideoProbe(640, 360, 10, false, 0)).VideoBitrate == 150_000, "Video budget accepts its own smallest target");
        var floor = VideoBudget.Plan(1_946_202, new VideoProbe(1920, 1080, 60, true, 0));
        check(VideoBudget.Retry(floor, 1_946_202, 2_500_000, new VideoProbe(1920, 1080, 60, true, 0)) == floor, "Video retry at the lowest rate and size returns the same target (nothing smaller to try)");
        var retry = VideoBudget.Retry(new VideoTarget(1_000_000, 128_000, 854, 480), 10_000_000, 12_000_000, new VideoProbe(1920, 1080, 100, true, 0));
        check(retry == new VideoTarget(797_000, 96_000, 854, 480), "Video budget retries at a rate scaled by the overshoot, with 96 kbit/s sound under 1 Mbit/s: " + retry);
        check(VideoBudget.Retry(retry, 10_000_000, 1_000_000_000, new VideoProbe(1920, 1080, 100, true, 0)).VideoBitrate == 150_000, "Video retry never goes below the lowest rate");
        foreach (var bad in new[] { new VideoProbe(1920, 1080, 0, true, 0), new VideoProbe(1920, 1080, double.NaN, true, 0), new VideoProbe(1920, 1080, 7201, true, 0), new VideoProbe(1, 1080, 10, true, 0), new VideoProbe(8193, 1080, 10, true, 0) })
            reject(() => VideoBudget.Plan(1_000_000_000, bad), "Video budget refuses " + bad);
        check(VideoBudget.IsVideo(@"C:\a\b.MOV") && VideoBudget.IsVideo("c.m4v") && !VideoBudget.IsVideo("d.avi") && !VideoBudget.IsVideo("e.mp4.txt"), "Video budget's file types");
        foreach (var style in Enum.GetValues<FrameStyle>())
        {
            var layout = new ImageFrame(1000, 800, 50, 500, true, style).Layout(2000, 1000);
            check(layout.Width == 900 && layout.Height == 450 && layout.X == 50 && layout.Y == 175 && layout.Radius == 225, "Frame geometry and clamped corners " + style);
        }
        check(new ImageFrame(500,500,50).Layout(100,80).Width == 100, "Frame never enlarges source");
        reject(() => new ImageFrame(100,100,50).Layout(10,10), "No empty frame interior");
        reject(() => new ImageFrame(4001,4000).Layout(10,10), "Frame canvas limit");
        reject(() => new ImageFrame(100,100,10,-1).Layout(10,10), "Frame corner bounds");
    }
}
