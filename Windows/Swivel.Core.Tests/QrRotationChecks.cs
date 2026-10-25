using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Swivel.Core;

internal static class QrRotationChecks
{
    // Independent destination-to-source pixel permutations, including the quiet zone.
    private static PixelImage Turn(PixelImage input,int clockwiseTurns)
    {
        int side=input.Width;var pixels=new byte[input.Bgra.Length];
        for(int y=0;y<side;y++)for(int x=0;x<side;x++)
        {
            (int sx,int sy)=clockwiseTurns switch
            {
                0=>(x,y),1=>(y,side-1-x),2=>(side-1-x,side-1-y),3=>(side-1-y,x),
                _=>throw new ArgumentOutOfRangeException(nameof(clockwiseTurns))
            };
            Array.Copy(input.Bgra,(sy*side+sx)*4,pixels,(y*side+x)*4,4);
        }
        return new(side,side,pixels);
    }

    private static PixelImage Mirror(PixelImage input)
    {
        int side=input.Width;var pixels=new byte[input.Bgra.Length];
        for(int y=0;y<side;y++)for(int x=0;x<side;x++)
            Array.Copy(input.Bgra,(y*side+side-1-x)*4,pixels,(y*side+x)*4,4);
        return new(side,side,pixels);
    }

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var masks=new HashSet<int>();
        foreach(var level in Enum.GetValues<QrCorrection>())
        {
            string field=level switch{QrCorrection.Low=>"DataWords",QrCorrection.Medium=>"MediumDataWords",QrCorrection.Quartile=>"QuartileDataWords",_=>"HighDataWords"};
            var capacities=(int[])typeof(QrCode).GetField(field,BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            for(int version=1;version<=40;version++)
            {
                int length=capacities[version-1]-(version<10?3:4);
                string text=string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*23+version*3)%94)));
                var code=QrCode.Encode(text,level);masks.Add(code.Mask);var source=QrReadChecks.Image(code);
                for(int turns=0;turns<4;turns++)
                {
                    var image=Turn(source,turns);var before=(byte[])image.Bgra.Clone();var actual=QrFlatRead.Read(image,default);
                    check(actual.Text==text&&actual.Version==version&&actual.Correction==level&&actual.Mask==code.Mask&&actual.PixelScale==1&&actual.ClockwiseOrientationDegrees==turns*90,"Every QR version/correction reads all four exact orientations with original metadata");
                    check(image.Bgra.SequenceEqual(before),"Rotated reading never mutates original raster or border");
                }
                if(version is 1 or 7 or 10 or 27 or 40)
                {
                    var mirrored=Mirror(source);
                    for(int turns=0;turns<4;turns++)
                    {
                        var image=Turn(mirrored,turns);
                        reject(()=>QrFlatRead.Read(image,default),"Mirrored QR is refused in all quarter-turn orientations");
                    }
                }
            }
        }
        check(masks.Count==8,"Orientation fixtures exercise all eight mask patterns");

        foreach(int scale in new[]{1,2,3,8})foreach(string text in new[]{"https://example.com/?x=1&y=2","中文 繁體 日本語 😀","line\r\nnext\tend","literal [U+0000]"})
        {
            var source=QrReadChecks.Image(QrCode.Encode(text,QrCorrection.High),scale);
            // Near-black/white threshold boundaries remain just as strict after rotation.
            for(int i=0;i<source.Bgra.Length;i+=4)for(int c=0;c<3;c++)source.Bgra[i+c]=source.Bgra[i+c]==0?(byte)63:(byte)192;
            for(int turns=0;turns<4;turns++)
            {
                var actual=QrFlatRead.Read(Turn(source,turns),default);
                check(actual.Text==text&&actual.ClockwiseOrientationDegrees==turns*90&&actual.PixelScale==scale,"Scaled quarter turns preserve URL/Unicode/control payload without interpretation");
            }
        }

        foreach(int length in new[]{1,150,QrCode.MaxBytes})
        {
            var code=QrCode.Encode(new string('x',length));var source=QrReadChecks.Image(code,2);int size=code.Size;
            foreach((int mx,int my) in new[]{(0,0),(4,4),(12,4),(4+size-1,4+size-1),(4+size-2,4+size-1)})
            {
                var damaged=(byte[])source.Bgra.Clone();
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                {
                    int at=((my*2+dy)*source.Width+mx*2+dx)*4;
                    for(int c=0;c<3;c++)damaged[at+c]=(byte)(255-damaged[at+c]);
                }
                for(int turns=0;turns<4;turns++)
                {
                    var image=Turn(new(source.Width,source.Height,damaged),turns);
                    reject(()=>QrFlatRead.Read(image,default),"Quiet-zone/finder/format/data damage remains refused after any quarter turn");
                }
            }
        }

        var valid=QrReadChecks.Image(QrCode.Encode("Orientation boundary"),2);
        foreach(int turns in new[]{0,1,2,3})
        {
            var image=Turn(valid,turns);reject(()=>QrFlatRead.Read(image,new CancellationToken(true)),"Precancelled rotated read stops before raster work");
            int p=(9*image.Width+9)*4;image.Bgra[p+3]=254;
            reject(()=>QrFlatRead.Read(image,default),"Rotation does not relax opaque-pixel validation");
        }
        check(new QrReadResult("legacy",1,QrCorrection.Low,0,1).ClockwiseOrientationDegrees==0,"Existing five-argument result construction retains upright default");
    }
}
