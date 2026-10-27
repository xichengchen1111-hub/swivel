using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum MarkKind { Pen, Arrow, Rectangle, Ellipse, Text, Redact, Number }
public readonly record struct ImagePoint(double X, double Y);
public sealed record ImageMark(MarkKind Kind, ImagePoint[] Points, uint Rgb, double Width, string Text = "")
{
    public void Validate(int width,int height)
    {
        ImageRules.Validate(width,height);
        if (!Enum.IsDefined(Kind) || Points is null || Points.Length is < 1 or > 2048 || Rgb>0xffffff || !double.IsFinite(Width) || Width is < 1 or > 200 ||
            Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.Y<0||p.X>width||p.Y>height) || Text is null || Text.Length>200 || Text.Any(c=>char.IsControl(c) && c!='\n'))
            throw new UserError("The annotation is outside its supported size or image bounds.");
        if (Kind is not MarkKind.Text and not MarkKind.Number && Points.Length<2) throw new UserError("Draw a mark before saving.");
        if (Kind==MarkKind.Text && string.IsNullOrWhiteSpace(Text)) throw new UserError("Enter annotation text first.");
        if (Kind==MarkKind.Number && (Text.Length is <1 or >3 || Text.Any(c=>c<'0'||c>'9') || !int.TryParse(Text,out int number) || number is <1 or >100)) throw new UserError("Number labels must be from 1 to 100.");
    }
    public PixelCrop Rectangle(int width,int height) => PixelCrop.Drag(width,height,Points[0].X,Points[0].Y,Points[^1].X,Points[^1].Y);
}
public static class MarkGeometry
{
    public static ImagePoint Constrain(ImagePoint start,ImagePoint end,MarkKind kind,int width,int height)
    {
        ImageRules.Validate(width,height);
        if (new[] { start.X,start.Y,end.X,end.Y }.Any(v => !double.IsFinite(v)) || start.X < 0 || start.Y < 0 || start.X > width || start.Y > height || end.X < 0 || end.Y < 0 || end.X > width || end.Y > height) throw new ArgumentException("Invalid mark coordinates.");
        double dx=end.X-start.X,dy=end.Y-start.Y;
        if (kind is MarkKind.Rectangle or MarkKind.Ellipse)
        {
            double size=Math.Min(Math.Abs(dx),Math.Abs(dy)); return new(start.X+Math.Sign(dx)*size,start.Y+Math.Sign(dy)*size);
        }
        if (kind != MarkKind.Arrow) return end;
        double angle=Math.Round(Math.Atan2(dy,dx)/(Math.PI/4))*Math.PI/4, length=Math.Sqrt(dx*dx+dy*dy);
        double x=Math.Cos(angle),y=Math.Sin(angle);
        if (x>1e-9) length=Math.Min(length,(width-start.X)/x); else if (x < -1e-9) length=Math.Min(length,-start.X/x);
        if (y>1e-9) length=Math.Min(length,(height-start.Y)/y); else if (y < -1e-9) length=Math.Min(length,-start.Y/y);
        return new(Math.Clamp(start.X+x*length,0,width),Math.Clamp(start.Y+y*length,0,height));
    }
}
public static class PixelRedact
{
    public static PixelImage Apply(PixelImage source,IReadOnlyList<PixelCrop> rectangles,CancellationToken token)
    {
        source.Validate(); if(rectangles.Count>100) throw new UserError("Use up to 100 marks per image.");
        foreach(var rect in rectangles) rect.Validate(source.Width,source.Height);
        token.ThrowIfCancellationRequested(); var pixels=(byte[])source.Bgra.Clone();
        for(int y=0;y<source.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            // Merge overlaps per scan line, so 100 large masks still touch each pixel once.
            var spans=rectangles.Where(r=>y>=r.Y && y<r.Y+r.Height).OrderBy(r=>r.X).ToArray();
            int start=-1,end=-1;
            foreach(var r in spans)
            {
                if(start<0){start=r.X;end=r.X+r.Width;}
                else if(r.X<=end) end=Math.Max(end,r.X+r.Width);
                else { Fill(start,end);start=r.X;end=r.X+r.Width; }
            }
            if(start>=0) Fill(start,end);
            void Fill(int left,int right)
            {
                Array.Clear(pixels,(y*source.Width+left)*4,(right-left)*4);
                for(int x=left;x<right;x++) pixels[(y*source.Width+x)*4+3]=255;
            }
        }
        return new(source.Width,source.Height,pixels);
    }
}
