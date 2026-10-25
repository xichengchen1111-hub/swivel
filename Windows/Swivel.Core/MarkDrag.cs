using System;

namespace Swivel.Core;

public static class MarkDrag
{
    // Every move is derived from the mouse-down geometry, never from the previous
    // event. Clamping at an edge cannot accumulate drift or change point order.
    public static ImageMark Update(ImageMark original,ImagePoint down,ImagePoint pointer,bool resize,bool keepAspect,int width,int height)
    {
        original.Validate(width,height);
        if (!double.IsFinite(down.X) || !double.IsFinite(down.Y) || !double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) throw new ArgumentException("Invalid pointer coordinate.");
        var frame = MarkEdit.Frame(original); double dx = pointer.X-down.X,dy = pointer.Y-down.Y;
        if (dx == 0 && dy == 0) return original;
        var previousFrame = frame;
        if (resize && original.Kind is not MarkKind.Text and not MarkKind.Number && (frame.Width > 0 || frame.Height > 0))
        {
            double w = frame.Width == 0 ? 0 : Math.Clamp(frame.Width+dx,Math.Min(1,frame.Width),width-frame.X);
            double h = frame.Height == 0 ? 0 : Math.Clamp(frame.Height+dy,Math.Min(1,frame.Height),height-frame.Y);
            if (keepAspect && frame.Width > 0 && frame.Height > 0)
            {
                double minimum = Math.Min(1,Math.Max(1/frame.Width,1/frame.Height));
                double maximum = Math.Min((width-frame.X)/frame.Width,(height-frame.Y)/frame.Height);
                double scale = Math.Clamp(Math.Max(w/frame.Width,h/frame.Height),minimum,maximum); w = frame.Width*scale; h = frame.Height*scale;
            }
            if (original.Kind == MarkKind.Redact) { w = Math.Max(1,Math.Round(w)); h = Math.Max(1,Math.Round(h)); }
            // A mathematically exact edge can round one ulp outside after scaling.
            // Keep the requested ratio to floating precision, then make bounds exact.
            w = Inside(frame.X,w,width); h = Inside(frame.Y,h,height);
            frame = frame with { Width = w,Height = h };
        }
        else
        {
            double x = Math.Clamp(frame.X+dx,0,width-frame.Width), y = Math.Clamp(frame.Y+dy,0,height-frame.Height);
            if (original.Kind == MarkKind.Redact) { x = Math.Round(x); y = Math.Round(y); }
            x = Inside(frame.Width,x,width); y = Inside(frame.Height,y,height);
            frame = frame with { X = x,Y = y };
        }
        return frame == previousFrame ? original : MarkEdit.Apply(original,frame,original.Rgb,original.Width,original.Text,width,height);
    }
    private static double Inside(double anchor,double extent,double boundary)
    {
        extent=Math.Min(extent,boundary-anchor);
        if(anchor+extent>boundary)extent=Math.BitDecrement(extent);
        return Math.Max(0,extent);
    }
}
