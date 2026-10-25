using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public static class MarkupTools
{
    public static BitmapSource Render(BitmapSource source,IReadOnlyList<ImageMark> marks,CancellationToken token)
    {
        if(marks.Count>100) throw new UserError("Use up to 100 marks per image.");
        foreach(var mark in marks) mark.Validate(source.PixelWidth,source.PixelHeight);
        token.ThrowIfCancellationRequested();
        // Redactions use integer pixel replacement after other drawing. No translucent
        // overlay, hidden layer, blurred source pixels or original thumbnail is saved.
        var drawings=marks.Where(m=>m.Kind!=MarkKind.Redact).ToArray();
        BitmapSource result=source;
        if(drawings.Length>0)
        {
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));
                foreach(var mark in drawings){token.ThrowIfCancellationRequested();Draw(dc,mark);}
            }
            var bitmap=new RenderTargetBitmap(source.PixelWidth,source.PixelHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            result=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
        }
        var rectangles=marks.Where(m=>m.Kind==MarkKind.Redact).Select(m=>m.Rectangle(source.PixelWidth,source.PixelHeight)).ToArray();
        if(rectangles.Length>0)
        {
            var pixels=PixelRedact.Apply(ImageTools.ReadPixels(result,0,token),rectangles,token);
            result=BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4);
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    public static Rect VisualBounds(ImageMark mark)
    {
        var group = new DrawingGroup();using(var dc=group.Open())Draw(dc,mark);
        var bounds=group.Bounds;
        if(bounds.IsEmpty){var frame=MarkEdit.Frame(mark);bounds=new Rect(frame.X,frame.Y,frame.Width,frame.Height);}
        return bounds;
    }
    public static void Draw(DrawingContext dc,ImageMark mark)
    {
        var brush=new SolidColorBrush(Color.FromRgb((byte)(mark.Rgb>>16),(byte)(mark.Rgb>>8),(byte)mark.Rgb));
        var pen=new Pen(brush,mark.Width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        var a=new Point(mark.Points[0].X,mark.Points[0].Y);var b=new Point(mark.Points[^1].X,mark.Points[^1].Y);
        var rect=new Rect(a,b);
        switch(mark.Kind)
        {
            case MarkKind.Pen:
                var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(a,false,false);context.PolyLineTo(mark.Points.Skip(1).Select(p=>new Point(p.X,p.Y)).ToArray(),true,false);}
                dc.DrawGeometry(null,pen,geometry);break;
            case MarkKind.Arrow:
                dc.DrawLine(pen,a,b);var delta=a-b;
                if(delta.Length>0){delta.Normalize();double length=Math.Min((a-b).Length*.4,mark.Width*4+10);var side=new Vector(-delta.Y,delta.X);dc.DrawLine(pen,b,b+delta*length+side*length*.5);dc.DrawLine(pen,b,b+delta*length-side*length*.5);}break;
            case MarkKind.Rectangle:dc.DrawRectangle(null,pen,rect);break;
            case MarkKind.Ellipse:dc.DrawEllipse(null,pen,new Point(rect.X+rect.Width/2,rect.Y+rect.Height/2),rect.Width/2,rect.Height/2);break;
            case MarkKind.Text:
                var text=new FormattedText(mark.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),Math.Max(12,mark.Width*4),brush,1);
                dc.DrawText(text,a);break;
            case MarkKind.Number:
                double radius=Math.Max(10,mark.Width*3);
                dc.DrawEllipse(brush,null,a,radius,radius);
                bool light=((mark.Rgb>>16)*299+((mark.Rgb>>8)&255)*587+(mark.Rgb&255)*114)>150000;
                var label=new FormattedText(mark.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),radius*(mark.Text.Length==3?.85:1.1),light?Brushes.Black:Brushes.White,1);
                dc.DrawText(label,new Point(a.X-label.Width/2,a.Y-label.Height/2));break;
            case MarkKind.Redact:dc.DrawRectangle(Brushes.Black,null,rect);break;
        }
    }
}
