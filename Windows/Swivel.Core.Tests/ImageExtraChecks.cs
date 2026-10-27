using System;
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
