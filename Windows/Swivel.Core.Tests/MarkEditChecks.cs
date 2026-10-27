using System;
using System.Linq;
using Swivel.Core;

internal static class MarkEditChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var pen = new ImageMark(MarkKind.Pen,new[] { new ImagePoint(20,10),new ImagePoint(30,30),new ImagePoint(40,20) },0xff0000,2);
        var moved = MarkEdit.Apply(pen,new(5,6,40,10),0x123456,4,"",100,80);
        check(moved.Points.SequenceEqual(new[] { new ImagePoint(5,6),new ImagePoint(25,16),new ImagePoint(45,11) }) && moved.Width == 4 && moved.Rgb == 0x123456,"Editing translates and scales every pen point, preserving order");
        check(pen.Points[0] == new ImagePoint(20,10) && pen.Width == 2,"Mark edits do not mutate original geometry");
        var text = new ImageMark(MarkKind.Text,new[] { new ImagePoint(15,20) },0,3,"old");
        var changed = MarkEdit.Apply(text,new(40,60,0,0),0xffffff,6,"中文\nnew",100,80);
        check(changed.Points.Single() == new ImagePoint(40,60) && changed.Text == "中文\nnew" && changed.Width == 6,"Text edit changes anchor, content and size");
        check(MarkEdit.Apply(text,new(0,0,0,0),0,2,"first\r\n中文\rlast",100,80).Text == "first\n中文\nlast","Windows CRLF and pasted CR normalize before edited text validation");
        var line = new ImageMark(MarkKind.Arrow,new[] { new ImagePoint(50,20),new ImagePoint(10,20) },0,2);
        var arrow = MarkEdit.Apply(line,new(0,80,100,0),0,2,"",100,80);
        check(arrow.Points[0] == new ImagePoint(100,80) && arrow.Points[1] == new ImagePoint(0,80),"Reframing preserves arrow direction and horizontal dimension");
        foreach (var frame in new[] { new MarkFrame(-1,0,20,20),new MarkFrame(90,0,20,20),new MarkFrame(0,0,double.NaN,20),new MarkFrame(0,0,0,20),new MarkFrame(0,double.PositiveInfinity,20,20) })
            reject(() => MarkEdit.Apply(pen,frame,0,2,"",100,80),"Invalid mark frame refused");
        reject(() => MarkEdit.Apply(line,new(0,0,100,10),0,2,"",100,80),"Degenerate dimension cannot silently distort a line");
        reject(() => MarkEdit.Apply(text,new(0,0,0,0),0,2,"",100,80),"Edited text cannot become blank");
        reject(() => MarkEdit.Apply(pen,new(0,0,20,20),0x1000000,2,"",100,80),"Edited RGB range checked");
        var redact = new ImageMark(MarkKind.Redact,new[] { new ImagePoint(2,2),new ImagePoint(10,10) },0,2);
        reject(() => MarkEdit.Apply(redact,new(1.5,0,8,8),0,2,"",100,80),"Redaction edits keep exact pixel coverage");
        var numbered = text with { Kind = MarkKind.Number,Text = "01" };
        check(MarkEdit.NextNumber(new[] { numbered,numbered with { Text = "2" },numbered with { Text = "100" } }) == "3","Automatic labels skip occupied numbers including zero-padded edits");
        check(MarkEdit.NextNumber(new[] { numbered with { Text = "2" } }) == "1","Deleting a label makes its number available again");
        reject(() => MarkEdit.NextNumber(Enumerable.Range(1,100).Select(n => numbered with { Text = n.ToString() }).ToArray()),"No 101st automatic number");
        var drag = MarkDrag.Update(pen,new(30,20),new(150,-100),false,false,100,80);
        check(MarkEdit.Frame(drag) == new MarkFrame(80,0,20,20) && drag.Points[1] == new ImagePoint(90,20),"Drag clamps entire geometry at image edge without distortion");
        var resized = MarkDrag.Update(pen,new(40,30),new(80,70),true,false,100,80);
        check(MarkEdit.Frame(resized) == new MarkFrame(20,10,60,60) && resized.Points[1] == new ImagePoint(50,70),"Corner resizing scales points from immutable original");
        var wide = pen with { Points = new[] { new ImagePoint(20,10),new ImagePoint(60,30) } };
        var constrained = MarkDrag.Update(wide,new(60,30),new(200,75),true,true,100,80);
        var cf=MarkEdit.Frame(constrained);check(cf.Width==80 && cf.Height==40 && cf.X+cf.Width==100,"Shift ratio constrained at right boundary");
        var tiny = MarkDrag.Update(pen,new(40,30),new(-500,-500),true,false,100,80);
        check(MarkEdit.Frame(tiny).Width == 1 && MarkEdit.Frame(tiny).Height == 1,"Resize cannot cross origin or collapse shape");
        var rd = MarkDrag.Update(redact,new(10,10),new(10.6,11.2),false,false,100,80);
        check(MarkEdit.Frame(rd) == new MarkFrame(3,3,8,8),"Redaction drag snaps all edges to pixels");
        var rr=MarkDrag.Update(redact,new(10,10),new(12.6,13.2),true,false,100,80);
        check(MarkEdit.Frame(rr)==new MarkFrame(2,2,11,11),"Redaction resize remains integer");
        var horizontal=MarkDrag.Update(line,new(50,20),new(90,60),true,false,100,80);
        check(MarkEdit.Frame(horizontal).Height==0 && horizontal.Points[0].X>horizontal.Points[1].X,"Resizing degenerate line preserves zero dimension and arrow direction");
        var labelDrag=MarkDrag.Update(text,new(15,20),new(25,40),true,false,100,80);
        check(labelDrag.Points[0]==new ImagePoint(25,40) && labelDrag.Width==text.Width,"Text handles move anchor and retain font size");
        reject(()=>MarkDrag.Update(pen,new(0,0),new(double.NaN,0),false,false,100,80),"Nonfinite pointer rejected");
        var fractional=pen with{Points=new[]{new ImagePoint(3.453560802154243,10),new ImagePoint(61.90100307576358,25),new ImagePoint(90.75148266274482,40)}};
        check(ReferenceEquals(MarkDrag.Update(fractional,new(50,20),new(50,20),false,false,100,80),fractional),"Click-only fractional pen returns exact original, not a lossy geometry recomputation");
        check(ReferenceEquals(MarkDrag.Update(drag,new(20,20),new(21,19),false,false,100,80),drag),"Clamped no-op drag returns exact original");
        var decimalEdge=pen with{Kind=MarkKind.Rectangle,Points=new[]{new ImagePoint(.2,10),new ImagePoint(10.18,15)}};
        var fitted=MarkDrag.Update(decimalEdge,new(10.18,15),new(100,80),true,true,100,80);var fitFrame=MarkEdit.Frame(fitted);
        check(fitFrame.X+fitFrame.Width<=100 && fitFrame.Y+fitFrame.Height<=80 && Math.Abs(fitFrame.Width/fitFrame.Height-9.98/5)<1e-12,"Shift resize at decimal image edge stays inside to one-ulp precision");
    }
}
