using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class MarkupChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var bytes=Enumerable.Range(0,20*12*4).Select(i=>(byte)(i%256)).ToArray();var source=new PixelImage(20,12,bytes);
        var rectangles=new[]{new PixelCrop(2,3,4,5),new PixelCrop(4,4,5,2),new PixelCrop(0,0,1,1)};
        var redacted=PixelRedact.Apply(source,rectangles,CancellationToken.None);bool right=true;
        for(int y=0;y<12;y++)for(int x=0;x<20;x++)
        {
            int i=(y*20+x)*4;bool covered=rectangles.Any(r=>x>=r.X && x<r.X+r.Width && y>=r.Y && y<r.Y+r.Height);
            right&=covered?redacted.Bgra[i]==0 && redacted.Bgra[i+1]==0 && redacted.Bgra[i+2]==0 && redacted.Bgra[i+3]==255:bytes.AsSpan(i,4).SequenceEqual(redacted.Bgra.AsSpan(i,4));
        }
        check(right,"Redaction replaces every covered RGBA and preserves every uncovered RGBA including alpha");
        check(!ReferenceEquals(bytes,redacted.Bgra),"Redaction leaves source buffer untouched");
        reject(()=>PixelRedact.Apply(source,new[]{new PixelCrop(19,0,2,1)},CancellationToken.None),"Out-of-bounds redaction refused");
        using var stop=new CancellationTokenSource();stop.Cancel();reject(()=>PixelRedact.Apply(source,rectangles,stop.Token),"Redaction cancellable before work");
        new ImageMark(MarkKind.Arrow,new[]{new ImagePoint(0,0),new ImagePoint(20,12)},0xffffff,2).Validate(20,12);
        reject(()=>new ImageMark(MarkKind.Text,new[]{new ImagePoint(0,0)},0,2,"bad\0text").Validate(20,12),"Annotation control text rejected");
        reject(()=>new ImageMark(MarkKind.Pen,new[]{new ImagePoint(double.NaN,0),new ImagePoint(2,2)},0,2).Validate(20,12),"Nonfinite drawing coordinates rejected");
        new ImageMark(MarkKind.Number,new[]{new ImagePoint(10,10)},0xff0000,4,"100").Validate(20,12);
        foreach (string value in new[] { "0","101","-1","abc","１２" }) reject(()=>new ImageMark(MarkKind.Number,new[]{new ImagePoint(10,10)},0,4,value).Validate(20,12),"Number annotation range and ASCII contract");
        check(MarkGeometry.Constrain(new(10,10),new(80,50),MarkKind.Rectangle,100,100)==new ImagePoint(50,50),"Shift rectangle stays square");
        check(MarkGeometry.Constrain(new(80,60),new(10,10),MarkKind.Ellipse,100,100)==new ImagePoint(30,10),"Reverse Shift circle preserves drag direction");
        var random = new Random(1024);
        for (int i=0;i<40;i++)
        {
            var start=new ImagePoint(random.Next(101),random.Next(81)); var end=new ImagePoint(random.Next(101),random.Next(81));
            var point=MarkGeometry.Constrain(start,end,MarkKind.Arrow,100,80); double x=point.X-start.X,y=point.Y-start.Y;
            check(point.X>=0&&point.X<=100&&point.Y>=0&&point.Y<=80&&(Math.Abs(x)<1e-8||Math.Abs(y)<1e-8||Math.Abs(Math.Abs(x)-Math.Abs(y))<1e-8),"Shift arrow hits exact 45-degree steps without leaving image");
        }
        foreach(var style in Enum.GetValues<CollageStyle>())for(int count=2;count<=12;count++)
        {
            var settings=new CollageSettings(3000,2400,8,style);var frames=settings.Layout(count);bool valid=frames.Count==count;
            for(int i=0;i<count;i++)
            {
                var a=frames[i];valid&=a.X>=0 && a.Y>=0 && a.Width>=16 && a.Height>=16 && a.X+a.Width<=settings.Width && a.Y+a.Height<=settings.Height;
                for(int j=i+1;j<count;j++){var b=frames[j];valid&=a.X+a.Width<=b.X || b.X+b.Width<=a.X || a.Y+a.Height<=b.Y || b.Y+b.Height<=a.Y;}
            }
            check(valid,"Collage layout is bounded and non-overlapping: "+style+"/"+count);
        }
        reject(()=>new CollageSettings().Layout(1),"Collage requires multiple images");
        reject(()=>new CollageSettings(256,256,100,CollageStyle.Row).Layout(12),"Impossible collage gap refused");
        reject(()=>new CollageSettings(4001,3000).Layout(2),"Collage dimension budget");
    }
}
