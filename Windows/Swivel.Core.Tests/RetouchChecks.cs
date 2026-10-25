using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class RetouchChecks
{
    private static PixelImage Texture(int width,int height)
    {
        var bytes = new byte[width*height*4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int p = (y*width+x)*4; byte value = (byte)((x/4+y/4)%2 == 0 ? 30 : 220); bytes[p] = bytes[p+1] = bytes[p+2] = value; bytes[p+3] = 255; }
        return new(width,height,bytes);
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var original = Texture(96,80); var damaged = new PixelImage(96,80,(byte[])original.Bgra.Clone()); var mask = new bool[96*80];
        for (int y = 28; y < 52; y++) for (int x = 32; x < 60; x++) { int p = y*96+x; mask[p] = true; damaged.Bgra[p*4] = 255; damaged.Bgra[p*4+1] = damaged.Bgra[p*4+2] = 0; }
        var repaired = Retouch.Complete(damaged,mask,CancellationToken.None); double error = 0; int count = 0; bool outside = true,donors = true;
        for (int p = 0; p < mask.Length; p++)
            if (mask[p]) { error += Math.Abs(repaired.Bgra[p*4]-original.Bgra[p*4]); count++; donors &= repaired.Bgra[p*4] == repaired.Bgra[p*4+1] && repaired.Bgra[p*4+1] == repaired.Bgra[p*4+2]; }
            else outside &= repaired.Bgra.AsSpan(p*4,4).SequenceEqual(damaged.Bgra.AsSpan(p*4,4));
        Console.WriteLine($"Retouch texture MAE: {error/count:0.###}; average-fill MAE: 95");
        check(error/count < 20,"Retouch restores checker phase better than average fill"); check(outside && donors,"Retouch preserves outside bytes and never copies painted donor object");
        var small = Texture(40,40);
        foreach (var testMask in new[] { Enumerable.Range(0,1600).Select(p => p%40 < 5).ToArray(),Enumerable.Range(0,1600).Select(p => p%40 < 36).ToArray(),Enumerable.Range(0,1600).Select(p => p == 820).ToArray(),new bool[1600] })
        {
            var painted = new PixelImage(40,40,(byte[])small.Bgra.Clone());
            for (int p = 0; p < testMask.Length; p++) if (testMask[p]) { painted.Bgra[p*4] = 255; painted.Bgra[p*4+1] = painted.Bgra[p*4+2] = 0; }
            var value = Retouch.Complete(painted,testMask,CancellationToken.None);
            check(Enumerable.Range(0,1600).Where(p => !testMask[p]).All(p => value.Bgra.AsSpan(p*4,4).SequenceEqual(small.Bgra.AsSpan(p*4,4))),"Retouch exact outside pixels on edge/90-percent/single/empty masks");
            check(Enumerable.Range(0,1600).Where(p => testMask[p]).All(p => value.Bgra[p*4] == value.Bgra[p*4+1] && value.Bgra[p*4+1] == value.Bgra[p*4+2]),"Every masked pixel is replaced from background even at 90-percent coverage");
        }
        reject(() => Retouch.Complete(small,Enumerable.Repeat(true,1600).ToArray(),CancellationToken.None),"Fully painted repair refused");
        var large = Texture(256,192); var largeMask = new bool[256*192];
        for (int y = 60; y < 130; y++) for (int x = 80; x < 160; x++) { int p = y*256+x; largeMask[p] = true; large.Bgra[p*4] = 255; large.Bgra[p*4+1] = 0; }
        var pyramid = Retouch.Complete(large,largeMask,CancellationToken.None);
        check(Enumerable.Range(0,largeMask.Length).All(p => largeMask[p] ? pyramid.Bgra[p*4] == pyramid.Bgra[p*4+1] : pyramid.Bgra.AsSpan(p*4,4).SequenceEqual(large.Bgra.AsSpan(p*4,4))),"Coarse guide cannot introduce painted pixels into donors");
        var normalized = Texture(160,100); byte[] before = (byte[])normalized.Bgra.Clone();
        for (int x = 65; x < 78; x++) { int p = (30*160+x)*4; normalized.Bgra[p] = 255; normalized.Bgra[p+1] = 0; }
        var stroke = new RetouchStroke(new[] { new ImagePoint(.4,.3),new ImagePoint(.5,.3) },.05);
        var result = Retouch.Apply(normalized,new[] { stroke },CancellationToken.None);
        check(Enumerable.Range(65,13).All(x => result.Bgra[(30*160+x)*4] == result.Bgra[(30*160+x)*4+1]),"Normalized brush uses top-down upright coordinates");
        check(result.Bgra.AsSpan(0,160*20*4).SequenceEqual(before.AsSpan(0,160*20*4)),"Repair preserves untouched rows exactly");
        var alpha = new PixelImage(20,20,Enumerable.Range(0,1600).Select(i => (byte)(i*19%256)).ToArray()); var alphaMask = new bool[400]; alphaMask[210] = true;
        var alphaOutput = Retouch.Complete(alpha,alphaMask,CancellationToken.None);
        check(Enumerable.Range(0,400).Where(p => !alphaMask[p]).All(p => alphaOutput.Bgra.AsSpan(p*4,4).SequenceEqual(alpha.Bgra.AsSpan(p*4,4))),"Retouch preserves unpainted transparent BGRA exactly");
        var separate = new[] { new RetouchStroke(new[] { new ImagePoint(.1,.1) },.01),new RetouchStroke(new[] { new ImagePoint(.9,.9) },.01) };
        check(Retouch.Regions(4000,3000,separate,CancellationToken.None).Length == 2,"Distant repairs keep separate bounded contexts");
        check(Retouch.Regions(1000,800,new[] { new RetouchStroke(new[] { new ImagePoint(.4,.5) },.05),new RetouchStroke(new[] { new ImagePoint(.45,.5) },.05) },CancellationToken.None).Length == 1,"Intersecting context windows merge before donor selection");
        check(Retouch.Regions(1000,800,new[] { .2,.4,.3 }.Select(x => new RetouchStroke(new[] { new ImagePoint(x,.5) },.05)).ToArray(),CancellationToken.None).Single().Strokes.Length == 3,"A third ROI bridging two earlier windows merges all three");
        var oneDonor = Enumerable.Repeat(true,1600).ToArray(); oneDonor[0] = false; var one = Retouch.Complete(small,oneDonor,CancellationToken.None);
        check(Enumerable.Range(0,1600).All(p => one.Bgra.AsSpan(p*4,4).SequenceEqual(small.Bgra.AsSpan(0,4))),"One remaining donor fills every painted pixel");
        reject(() => Retouch.Regions(4000,3000,new[] { new RetouchStroke(new[] { new ImagePoint(0,0),new ImagePoint(1,1) },.1) },CancellationToken.None),"Large sparse region refused before maps allocated");
        reject(() => new RetouchStroke(new[] { new ImagePoint(double.NaN,0) },.1).Validate(),"Invalid brush point refused");
        reject(() => Retouch.Regions(100,100,Enumerable.Repeat(stroke,101).ToArray(),CancellationToken.None),"Repair stroke budget");
        var dense = new RetouchStroke(Enumerable.Repeat(new ImagePoint(.5,.5),2001).ToArray(),.15);
        reject(() => Retouch.Regions(1000,1000,Enumerable.Repeat(dense,10).ToArray(),CancellationToken.None),"Repair rejects more than 20000 input points before raster allocation");
        reject(() => Retouch.Apply(Texture(1000,1000),Enumerable.Repeat(dense,4).ToArray(),CancellationToken.None),"Repair mask raster work is capped at 100 million visits");
        foreach (var size in new[] { (1,40),(40,1),(1,1) })
        {
            var thin = Texture(size.Item1,size.Item2); var thinMask = new bool[size.Item1*size.Item2]; if (thinMask.Length > 1) thinMask[0] = true;
            var thinResult = Retouch.Complete(thin,thinMask,CancellationToken.None); check(thinResult.Bgra.Length == thin.Bgra.Length,"Thin or single-pixel repair remains bounded");
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); reject(() => Retouch.Apply(normalized,new[] { stroke },cancel.Token),"Repair pre-cancellation");
        var cancelSource = Texture(1000,900); var cancelMask = Enumerable.Range(0,900000).Select(p => p%1000 >= 50 && p%1000 < 950).ToArray();
        using (var during = new CancellationTokenSource())
        {
            during.CancelAfter(20); bool stopped = false;
            try { Retouch.Complete(cancelSource,cancelMask,during.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped,"Repair observes asynchronous cancellation during nontrivial synthesis");
        }
        var timer = Stopwatch.StartNew(); Retouch.Apply(Texture(1400,900),new[] { new RetouchStroke(new[] { new ImagePoint(.5,.5) },.04) },CancellationToken.None); timer.Stop(); Console.WriteLine($"Retouch 1400x900 / 56px brush: {timer.Elapsed.TotalSeconds:0.000}s (this Mac, core only)");
    }
}
