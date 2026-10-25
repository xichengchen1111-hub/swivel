using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

public readonly record struct MarkFrame(double X,double Y,double Width,double Height);
public static class MarkEdit
{
    public static string NextNumber(IReadOnlyList<ImageMark> marks)
    {
        var used = new HashSet<int>();
        foreach (var mark in marks.Where(m => m.Kind == MarkKind.Number))
        {
            if (!int.TryParse(mark.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int number) || number is < 1 or > 100) throw new UserError("Number labels must be from 1 to 100.");
            used.Add(number);
        }
        int next = Enumerable.Range(1,100).FirstOrDefault(n => !used.Contains(n));
        if (next == 0) throw new UserError("Use up to 100 marks per image.");
        return next.ToString(CultureInfo.InvariantCulture);
    }
    public static MarkFrame Frame(ImageMark mark)
    {
        if (mark.Points is null || mark.Points.Length == 0) throw new ArgumentException("Missing mark points.");
        double x = mark.Points.Min(p => p.X),y = mark.Points.Min(p => p.Y);
        return new(x,y,mark.Points.Max(p => p.X)-x,mark.Points.Max(p => p.Y)-y);
    }
    public static ImageMark Apply(ImageMark mark,MarkFrame frame,uint rgb,double stroke,string text,int width,int height)
    {
        mark.Validate(width,height); var old = Frame(mark);
        if (text is null) throw new UserError("Enter annotation text first.");
        text = text.Replace("\r\n","\n").Replace('\r','\n');
        if (new[] { frame.X,frame.Y,frame.Width,frame.Height }.Any(x => !double.IsFinite(x)) || frame.X < 0 || frame.Y < 0 || frame.Width < 0 || frame.Height < 0 || frame.X+frame.Width > width || frame.Y+frame.Height > height ||
            (old.Width == 0 ? frame.Width != 0 : frame.Width <= 0) || (old.Height == 0 ? frame.Height != 0 : frame.Height <= 0))
            throw new UserError("Keep the mark's position and size inside the image. A zero-width or zero-height mark keeps that dimension.");
        if (mark.Kind == MarkKind.Redact && new[] { frame.X,frame.Y,frame.Width,frame.Height }.Any(x => x != Math.Round(x)))
            throw new UserError("Redaction position and size use whole pixels.");
        var points = mark.Points.Select(p => new ImagePoint(old.Width == 0 ? frame.X : Math.Clamp(frame.X+(p.X-old.X)/old.Width*frame.Width,0,width),old.Height == 0 ? frame.Y : Math.Clamp(frame.Y+(p.Y-old.Y)/old.Height*frame.Height,0,height))).ToArray();
        var result = mark with { Points = points,Rgb = rgb,Width = stroke,Text = text };
        result.Validate(width,height); return result;
    }
}
