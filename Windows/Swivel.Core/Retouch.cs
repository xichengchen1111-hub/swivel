using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

/// Coordinates are normalized upright, TOP-DOWN; diameter is a fraction of the longer side.
public sealed record RetouchStroke(ImagePoint[] Points,double Diameter)
{
    public void Validate()
    {
        if (!double.IsFinite(Diameter) || Diameter is < .001 or > .3 || Points is null || Points.Length is < 1 or > 2048 || Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            throw new UserError("Use a bounded repair brush and points inside the image.");
    }
}
public sealed record RetouchRegion(PixelCrop Bounds,int[] Strokes);

/// Port of Swivel's original exemplar synthesis, not an external library/model.
/// Only fully unpainted patches donate; every unmasked source BGRA byte is retained.
public static class Retouch
{
    public const int MaxRegionPixels = 2_000_000;
    public static RetouchRegion[] Regions(int width,int height,IReadOnlyList<RetouchStroke> strokes,CancellationToken token)
    {
        ImageRules.Validate(width,height);
        if (strokes.Count > 100 || strokes.Any(x => x is null)) throw new UserError("Use up to 100 repair strokes per image.");
        int points = 0; var result = new List<RetouchRegion>();
        foreach (var stroke in strokes) { stroke.Validate(); points += stroke.Points.Length; }
        if (points > 20000) throw new UserError("This repair mask is too complex. Use fewer or shorter strokes.");
        for (int index = 0; index < strokes.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var stroke = strokes[index]; double radius = Math.Max(.5,stroke.Diameter*Math.Max(width,height)/2);
            double x1 = stroke.Points.Min(p => p.X)*width-radius,y1 = stroke.Points.Min(p => p.Y)*height-radius;
            double x2 = stroke.Points.Max(p => p.X)*width+radius+1,y2 = stroke.Points.Max(p => p.Y)*height+radius+1;
            double margin = Math.Clamp(Math.Max(x2-x1,y2-y1)*.6,32,160);
            int left = (int)Math.Max(0,Math.Floor(x1-margin)),top = (int)Math.Max(0,Math.Floor(y1-margin));
            int right = (int)Math.Min(width,Math.Ceiling(x2+margin)),bottom = (int)Math.Min(height,Math.Ceiling(y2+margin));
            var box = new PixelCrop(left,top,right-left,bottom-top); var indices = new List<int> { index };
            for (int i = 0; i < result.Count;)
            {
                token.ThrowIfCancellationRequested(); var other = result[i].Bounds;
                if (box.X < other.X+other.Width && other.X < box.X+box.Width && box.Y < other.Y+other.Height && other.Y < box.Y+box.Height)
                {
                    left = Math.Min(box.X,other.X); top = Math.Min(box.Y,other.Y); right = Math.Max(box.X+box.Width,other.X+other.Width); bottom = Math.Max(box.Y+box.Height,other.Y+other.Height);
                    box = new(left,top,right-left,bottom-top); indices.AddRange(result[i].Strokes); result.RemoveAt(i); i = 0;
                }
                else i++;
            }
            if ((long)box.Width*box.Height > MaxRegionPixels) throw new UserError("This repair area is too large. Use smaller, separate strokes.");
            result.Add(new(box,indices.ToArray()));
        }
        return result.ToArray();
    }
    public static PixelImage Apply(PixelImage source,IReadOnlyList<RetouchStroke> strokes,CancellationToken token)
    {
        source.Validate(); token.ThrowIfCancellationRequested();
        var regions = Regions(source.Width,source.Height,strokes,token);
        var output = (byte[])source.Bgra.Clone(); long visits = 0;
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested(); var box = region.Bounds; int w = box.Width,h = box.Height;
            var bytes = new byte[w*h*4]; var mask = new bool[w*h];
            for (int y = 0; y < h; y++) { token.ThrowIfCancellationRequested(); Array.Copy(source.Bgra,((box.Y+y)*source.Width+box.X)*4,bytes,y*w*4,w*4); }
            foreach (int index in region.Strokes)
            {
                var stroke = strokes[index]; double radius = Math.Max(.5,stroke.Diameter*Math.Max(source.Width,source.Height)/2);
                for (int i = 0; i < stroke.Points.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var a = stroke.Points[Math.Max(0,i-1)]; var b = stroke.Points[i];
                    double ax = a.X*source.Width-box.X,ay = a.Y*source.Height-box.Y,bx = b.X*source.Width-box.X,by = b.Y*source.Height-box.Y;
                    int left = (int)Math.Max(0,Math.Floor(Math.Min(ax,bx)-radius)),right = (int)Math.Min(w,Math.Ceiling(Math.Max(ax,bx)+radius+1));
                    int top = (int)Math.Max(0,Math.Floor(Math.Min(ay,by)-radius)),bottom = (int)Math.Min(h,Math.Ceiling(Math.Max(ay,by)+radius+1));
                    visits += (long)(right-left)*(bottom-top);
                    if (visits > 100_000_000) throw new UserError("This repair mask is too complex. Use fewer or shorter strokes.");
                    double dx = bx-ax,dy = by-ay,length = dx*dx+dy*dy;
                    for (int y = top; y < bottom; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = left; x < right; x++)
                        {
                            double px = x+.5,py = y+.5,t = length > 0 ? Math.Clamp(((px-ax)*dx+(py-ay)*dy)/length,0,1) : 0;
                            double ex = px-ax-t*dx,ey = py-ay-t*dy;
                            if (ex*ex+ey*ey <= radius*radius) mask[y*w+x] = true;
                        }
                    }
                }
            }
            var repaired = Complete(new(w,h,bytes),mask,token);
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++) if (mask[y*w+x]) Array.Copy(repaired.Bgra,(y*w+x)*4,output,((box.Y+y)*source.Width+box.X+x)*4,4);
            }
        }
        token.ThrowIfCancellationRequested(); return new(source.Width,source.Height,output);
    }
    private readonly record struct Context(int X,int Y,double Weight,byte B,byte G,byte R,byte A);
    public static PixelImage Complete(PixelImage input,bool[] mask,CancellationToken token)
    {
        input.Validate(); token.ThrowIfCancellationRequested(); int w = input.Width,h = input.Height,count = w*h;
        if (count > MaxRegionPixels || mask.Length != count) throw new UserError("This repair area is too large. Use smaller, separate strokes.");
        var known = new List<int>(); bool hasMask = false;
        for (int p = 0; p < count; p++) { if (p%4096 == 0) token.ThrowIfCancellationRequested(); if (mask[p]) hasMask = true; else known.Add(p); }
        if (!hasMask) return new(w,h,(byte[])input.Bgra.Clone());
        if (known.Count == 0) throw new UserError("Leave some background unpainted to repair this area.");
        PixelImage? guide = null;
        if (Math.Max(w,h) > 180 && Math.Min(w,h) > 16)
        {
            int nw = (w+1)/2,nh = (h+1)/2; var small = new byte[nw*nh*4]; var coarseMask = new bool[nw*nh];
            for (int y = 0; y < nh; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < nw; x++)
                {
                    int b = 0,g = 0,r = 0,a = 0,n = 0;
                    for (int yy = y*2; yy < Math.Min(h,y*2+2); yy++) for (int xx = x*2; xx < Math.Min(w,x*2+2); xx++)
                    {
                        int p = yy*w+xx; if (mask[p]) { coarseMask[y*nw+x] = true; continue; }
                        n++; b += input.Bgra[p*4]; g += input.Bgra[p*4+1]; r += input.Bgra[p*4+2]; a += input.Bgra[p*4+3];
                    }
                    if (n > 0) { int p = (y*nw+x)*4; small[p] = (byte)(b/n); small[p+1] = (byte)(g/n); small[p+2] = (byte)(r/n); small[p+3] = (byte)(a/n); }
                }
            }
            if (coarseMask.Any(x => !x)) guide = Complete(new(nw,nh,small),coarseMask,token);
        }
        var output = (byte[])input.Bgra.Clone(); var filled = mask.Select(x => !x).ToArray(); var donors = Enumerable.Repeat(-1,count).ToArray(); ulong random = 0x8e73c9b5;
        int Next(int upper) { random = unchecked(random*6364136223846793005UL+1442695040888963407UL); return (int)((random>>32)%(ulong)Math.Max(1,upper)); }
        var integral = new int[(w+1)*(h+1)];
        for (int y = 0; y < h; y++) { token.ThrowIfCancellationRequested(); int row = 0; for (int x = 0; x < w; x++) { row += mask[y*w+x] ? 1 : 0; integral[(y+1)*(w+1)+x+1] = integral[y*(w+1)+x+1]+row; } }
        int radius = Math.Min(3,(Math.Min(w,h)-1)/2); var sources = new List<int>();
        bool KnownPatch(int x,int y)
        {
            if (x < radius || x+radius >= w || y < radius || y+radius >= h) return false;
            int a = (y-radius)*(w+1),b = (y+radius+1)*(w+1),left = x-radius,right = x+radius+1;
            return integral[b+right]-integral[b+left]-integral[a+right]+integral[a+left] == 0;
        }
        while (true)
        {
            for (int i = 0; i < known.Count; i++) { if (i%4096 == 0) token.ThrowIfCancellationRequested(); int p = known[i]; if (KnownPatch(p%w,p/w)) sources.Add(p); }
            if (sources.Count > 0 || radius == 0) break; radius--;
        }
        IEnumerable<int> Neighbours(int p) { if (p%w > 0) yield return p-1; if (p%w+1 < w) yield return p+1; if (p >= w) yield return p-w; if (p+w < count) yield return p+w; }
        var queue = new List<int>(); var enqueued = new bool[count];
        for (int p = 0; p < count; p++) { if (p%4096 == 0) token.ThrowIfCancellationRequested(); if (mask[p] && Neighbours(p).Any(n => filled[n])) { queue.Add(p); enqueued[p] = true; } }
        var context = new List<Context>(49);
        for (int head = 0; head < queue.Count; head++)
        {
            if (head%32 == 0) token.ThrowIfCancellationRequested(); int p = queue[head]; if (filled[p]) continue; int x = p%w,y = p/w; context.Clear();
            for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = x+dx,ty = y+dy; if (tx < 0 || tx >= w || ty < 0 || ty >= h) continue;
                int t = ty*w+tx,j = t*4;
                if (filled[t]) context.Add(new(dx,dy,mask[t] ? .6 : 1,output[j],output[j+1],output[j+2],output[j+3]));
                else if (guide is not null) { j = (ty/2*guide.Width+tx/2)*4; context.Add(new(dx,dy,.03,guide.Bgra[j],guide.Bgra[j+1],guide.Bgra[j+2],guide.Bgra[j+3])); }
            }
            int best = sources[Next(sources.Count)]; double score = double.PositiveInfinity;
            void Consider(int q)
            {
                if (q < 0 || q >= count) return; int sx = q%w,sy = q/w; if (!KnownPatch(sx,sy)) return; double distance = 0;
                foreach (var c in context)
                {
                    int j = ((sy+c.Y)*w+sx+c.X)*4;
                    int b = input.Bgra[j]-c.B,g = input.Bgra[j+1]-c.G,r = input.Bgra[j+2]-c.R,a = input.Bgra[j+3]-c.A;
                    distance += c.Weight*(b*b+g*g+r*r+a*a); if (distance >= score) return;
                }
                if (distance < score) { score = distance; best = q; }
            }
            foreach (int n in Neighbours(p)) if (filled[n] && donors[n] >= 0) Consider(donors[n]+p-n);
            for (int i = 0; i < 64; i++) Consider(sources[Next(sources.Count)]);
            for (int reach = Math.Max(w,h); reach > 0; reach/=2) Consider(Math.Clamp(best/w+Next(2*reach+1)-reach,0,h-1)*w+Math.Clamp(best%w+Next(2*reach+1)-reach,0,w-1));
            int copyRadius = Math.Max(0,radius-1);
            for (int dy = -copyRadius; dy <= copyRadius; dy++) for (int dx = -copyRadius; dx <= copyRadius; dx++)
            {
                int tx = x+dx,ty = y+dy; if (tx < 0 || tx >= w || ty < 0 || ty >= h) continue; int t = ty*w+tx; if (filled[t]) continue;
                int donor = best+dy*w+dx; Array.Copy(input.Bgra,donor*4,output,t*4,4); filled[t] = true; donors[t] = donor;
                foreach (int n in Neighbours(t)) if (!filled[n] && !enqueued[n]) { queue.Add(n); enqueued[n] = true; }
            }
        }
        token.ThrowIfCancellationRequested(); return new(w,h,output);
    }
}
