using System;
using System.Linq;
using System.Threading;
using Swivel.Core;

// Synthetic "photos" of codes from the existing encoder: an independent inverse-mapping renderer
// with supersampling, colored backgrounds, blur and deterministic noise. No real camera photos.
internal static class QrPhotoReadChecks
{
    internal sealed record Scene(double Degrees=0,double Module=4,double Perspective=0,int Blur=0,int Noise=0,int Width=0,int Height=0,bool Mirror=false,double ShiftX=0,double ShiftY=0);

    // Maps image pixels back to code coordinates (quiet zone included, 0..side).
    private static double[] Homography((double X,double Y)[] from,(double X,double Y)[] to)
    {
        var m=new double[8,9];
        for(int i=0;i<4;i++)
        {
            var (u,v)=from[i];var (x,y)=to[i];
            double[] a={u,v,1,0,0,0,-u*x,-v*x,x},b={0,0,0,u,v,1,-u*y,-v*y,y};
            for(int j=0;j<9;j++){m[2*i,j]=a[j];m[2*i+1,j]=b[j];}
        }
        for(int c=0;c<8;c++)
        {
            int p=c;for(int r=c+1;r<8;r++)if(Math.Abs(m[r,c])>Math.Abs(m[p,c]))p=r;
            for(int j=0;j<9;j++)(m[c,j],m[p,j])=(m[p,j],m[c,j]);
            for(int r=0;r<8;r++){if(r==c)continue;double f=m[r,c]/m[c,c];for(int j=c;j<9;j++)m[r,j]-=f*m[c,j];}
        }
        return Enumerable.Range(0,8).Select(i=>m[i,8]/m[i,i]).ToArray();
    }

    internal static PixelImage Render(QrCode code,Scene scene) => Render(new[]{(code,scene)},scene);
    internal static PixelImage Render((QrCode Code,Scene Scene)[] codes,Scene canvas)
    {
        var layouts=codes.Select(item=>
        {
            int side=item.Code.Size+8;double extent=side*item.Scene.Module;
            int w=canvas.Width>0?canvas.Width:(int)Math.Ceiling(extent*1.6)+40,h=canvas.Height>0?canvas.Height:w;
            double cx=w/2.0+item.Scene.ShiftX,cy=h/2.0+item.Scene.ShiftY,r=item.Scene.Degrees*Math.PI/180,half=extent/2;
            (double,double) Corner(double sx,double sy,double pinch)
            {
                double x=sx*half*(1-pinch),y=sy*half;
                return (cx+x*Math.Cos(r)-y*Math.Sin(r),cy+x*Math.Sin(r)+y*Math.Cos(r));
            }
            // Perspective narrows the top edge (a code tilted away from the camera).
            double p=item.Scene.Perspective;
            var quad=new[]{Corner(-1,-1,p),Corner(1,-1,p),Corner(1,1,-p*0.3),Corner(-1,1,-p*0.3)};
            var square=new (double,double)[]{(0,0),(side,0),(side,side),(0,side)};
            return (Code:item.Code,Side:side,Inverse:Homography(quad,square),Raster:item.Code.Raster(1),Mirror:item.Scene.Mirror,W:w,H:h);
        }).ToArray();
        int width=layouts[0].W,height=layouts[0].H;var bgra=new byte[width*height*4];
        uint seed=12345;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            // Uneven warm background lighting.
            double light=0.78+0.2*x/width-0.1*y/height;
            double r=200*light,g=185*light,b=150*light;int dark=0,inside=0;
            foreach(var l in layouts)
            {
                for(int sy=0;sy<3;sy++)for(int sx=0;sx<3;sx++)
                {
                    double px=x+(sx+0.5)/3,py=y+(sy+0.5)/3;var hm=l.Inverse;
                    double w=hm[6]*px+hm[7]*py+1,u=(hm[0]*px+hm[1]*py+hm[2])/w,v=(hm[3]*px+hm[4]*py+hm[5])/w;
                    if(u<0||v<0||u>=l.Side||v>=l.Side)continue;inside++;
                    int mu=(int)u,mv=(int)v;if(l.Mirror)mu=l.Side-1-mu;
                    if(l.Raster[mv*l.Side+mu]==0)dark++;
                }
            }
            if(inside>0)
            {
                // Paper of the code is lighter than the background; ink is a dark blue-gray.
                double paper=inside/9.0,ink=dark/9.0;
                r=r*(1-paper)+240*light*paper-ink*(240*light-45);g=g*(1-paper)+236*light*paper-ink*(236*light-40);b=b*(1-paper)+228*light*paper-ink*(228*light-70);
            }
            int i=(y*width+x)*4;bgra[i]=(byte)Math.Clamp(b,0,255);bgra[i+1]=(byte)Math.Clamp(g,0,255);bgra[i+2]=(byte)Math.Clamp(r,0,255);bgra[i+3]=255;
        }
        for(int pass=0;pass<canvas.Blur;pass++)bgra=Blur(bgra,width,height);
        if(canvas.Noise>0)for(int i=0;i<bgra.Length;i+=4)for(int c=0;c<3;c++)
        {
            seed=seed*1664525+1013904223;int delta=(int)(seed>>24)%(2*canvas.Noise+1)-canvas.Noise;
            bgra[i+c]=(byte)Math.Clamp(bgra[i+c]+delta,0,255);
        }
        return new(width,height,bgra);
    }

    private static byte[] Blur(byte[] input,int width,int height)
    {
        var output=(byte[])input.Clone();
        for(int y=1;y<height-1;y++)for(int x=1;x<width-1;x++)for(int c=0;c<3;c++)
        {
            int sum=0;for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)sum+=input[((y+dy)*width+x+dx)*4+c];
            output[(y*width+x)*4+c]=(byte)(sum/9);
        }
        return output;
    }

    private static int AngleDifference(int a,int b){int d=Math.Abs(a-b)%360;return Math.Min(d,360-d);}

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        // Versions 2 (alignment), 5 and 8 (version information), mixed correction levels.
        var codes=new[]
        {
            (Text:"https://example.com/photo?id=42",Level:QrCorrection.Medium),
            (Text:"Swivel photo reader 照片 "+new string('x',60),Level:QrCorrection.Quartile),
            (Text:string.Concat(Enumerable.Range(0,150).Select(i=>(char)('A'+i%26))),Level:QrCorrection.Low),
        }.Select(c=>(c.Text,Code:QrCode.Encode(c.Text,c.Level))).ToArray();
        check(codes.Select(c=>c.Code.Version).SequenceEqual(new[]{codes[0].Code.Version,codes[1].Code.Version,codes[2].Code.Version})&&codes[0].Code.Version>=2&&codes[2].Code.Version>=7,
            "Photo fixtures cover alignment and version-information sizes: "+string.Join(",",codes.Select(c=>c.Code.Version)));

        foreach(var (text,code) in codes)foreach(double degrees in new[]{0.0,17,90,133,251})
        {
            var image=Render(code,new(degrees,4.2));var before=(byte[])image.Bgra.Clone();
            var result=QrPhotoRead.Read(image,default);
            check(result.Text==text&&result.Photo&&result.Version==code.Version&&result.Correction==code.Correction&&image.Bgra.SequenceEqual(before),
                $"Rotated photo QR v{code.Version} at {degrees}° decodes without changing the input");
            check(AngleDifference(result.ClockwiseOrientationDegrees,(int)degrees)<=2,$"Photo orientation v{code.Version} {degrees}° ≈ {result.ClockwiseOrientationDegrees}°");
            check(Math.Abs(result.PixelScale-4)<=1,"Photo module size estimate is reported in pixels");
        }

        foreach(var (text,code) in codes)
        {
            check(QrPhotoRead.Read(Render(code,new(8,3.1)),default).Text==text,$"Scaled-down photo QR v{code.Version} (3.1 px/module) decodes");
            check(QrPhotoRead.Read(Render(code,new(-12,6.5,Perspective:0.12)),default).Text==text,$"Perspective-warped photo QR v{code.Version} decodes");
            check(QrPhotoRead.Read(Render(code,new(33,5,Blur:2,Noise:28)),default).Text==text,$"Blurred, noisy photo QR v{code.Version} on a colored background decodes");
        }

        foreach(var (text,code) in codes)
            check(QrPhotoRead.Read(Render(code,new(0,2.4)),default).Text==text,$"Photo QR v{code.Version} at 2.4 px/module decodes");
        // A code below 16 megapixels of finder-like texture is still found (full candidate list).
        {
            var below=Render(new[]{(codes[1].Code,new Scene(11,4,ShiftY:1500))},new(Width:1600,Height:3600));
            for(int y=0;y<2400;y++)for(int x=0;x<1600;x++)
            {
                int mx=x/2%9,my=y/2%9;bool ink=mx<7&&my<7&&(mx is 0 or 6||my is 0 or 6||mx is >=2 and <=4&&my is >=2 and <=4);
                int i=(y*1600+x)*4;below.Bgra[i]=below.Bgra[i+1]=below.Bgra[i+2]=ink?(byte)20:(byte)235;
            }
            check(QrPhotoRead.Read(below,default).Text==codes[1].Text,"A code below a field of finder look-alikes is still read");
        }
        // Off-center code in a larger picture with a strong lighting gradient.
        var small=codes[0];
        var offCenter=Render(new[]{(small.Code,new Scene(21,3.5,ShiftX:-150,ShiftY:90))},new(Width:700,Height:520,Noise:12));
        check(QrPhotoRead.Read(offCenter,default).Text==small.Text,"Small off-center code in a larger picture decodes");

        // Several codes: exactly one complete text, of one of them, is returned.
        var other=(Text:"second code",Code:QrCode.Encode("second code",QrCorrection.Medium));
        var pair=Render(new[]{(small.Code,new Scene(5,4,ShiftX:-190)),(other.Code,new Scene(-9,4,ShiftX:190))},new(Width:820,Height:420));
        string chosen=QrPhotoRead.Read(pair,default).Text;
        check(chosen==small.Text||chosen==other.Text,"Two codes in one image return one complete text");

        // Exact flat fixtures still take the strict path with an identical result.
        foreach(var (text,code) in codes)foreach(int scale in new[]{1,3})
        {
            var flat=QrReadChecks.Image(code,scale);var strict=QrFlatRead.Read(flat,default);var combined=QrPhotoRead.Read(flat,default);
            check(combined==strict&&!combined.Photo&&combined.CorrectedCodewords==0,"Exact flat fixtures decode identically through the combined reader");
        }

        // Negative cases.
        var blank=Render(codes[0].Code,new(10,4));var plain=new PixelImage(blank.Width,blank.Height,(byte[])blank.Bgra.Clone());
        for(int i=0;i<plain.Bgra.Length;i+=4){plain.Bgra[i]=150;plain.Bgra[i+1]=170;plain.Bgra[i+2]=190;}
        UserError? none=null;try{QrPhotoRead.Read(plain,default);}catch(UserError e){none=e;}
        check(none?.Message==QrPhotoRead.NoCode,"An image with no code reports the photo reader's no-code message");
        var noise=Render(codes[0].Code,new(0,4,Noise:127));for(int i=0;i<noise.Bgra.Length;i+=4)noise.Bgra[i]=noise.Bgra[i+1]=noise.Bgra[i+2]=(byte)((i*2654435761u>>13)&255);
        reject(()=>QrPhotoRead.Read(noise,default),"Random noise is not read as a code");
        foreach(var (text,code) in codes.Take(2))
        {
            // Cover the top-left finder with background-colored paint.
            var damaged=Render(code,new(0,4));int side=code.Size+8;double extent=side*4,origin=damaged.Width/2.0-extent/2;
            for(int y=(int)(origin+4*4-2);y<(int)(origin+11*4+2);y++)for(int x=(int)(origin+4*4-2);x<(int)(origin+11*4+2);x++)
            {int p=(y*damaged.Width+x)*4;damaged.Bgra[p]=damaged.Bgra[p+1]=damaged.Bgra[p+2]=228;}
            reject(()=>QrPhotoRead.Read(damaged,default),$"A covered corner finder is refused v{code.Version}");
            reject(()=>QrPhotoRead.Read(Render(code,new(25,4,Mirror:true)),default),$"A mirrored photo QR is refused v{code.Version}");
        }
        reject(()=>QrPhotoRead.Read(Render(codes[0].Code,new(0,1.2)),default),"A code under the module-size floor is refused");
        reject(()=>QrPhotoRead.Read(blank,new CancellationToken(true)),"Precancelled photo read does no work");
        reject(()=>QrPhotoRead.Read(new(4001,4001,new byte[4]),default),"Inputs over 16 megapixels are refused before reading");
        // Worst-case bound: 16 megapixels of random 1–4 pixel blocks (many finder-like runs) holds no code.
        var busy=new byte[4000*4000*4];uint state=7;
        for(int y=0;y<4000;y++)for(int x=0;x<4000;x++)
        {
            int cell=(y/3)*1400+(x/3);uint hash=(uint)cell*2654435761u^state;hash^=hash>>15;hash*=2246822519u;hash^=hash>>13;
            byte v=(hash&1)==0?(byte)30:(byte)225;int i=(y*4000+x)*4;busy[i]=busy[i+1]=busy[i+2]=v;busy[i+3]=255;
        }
        var watch=System.Diagnostics.Stopwatch.StartNew();
        reject(()=>QrPhotoRead.Read(new(4000,4000,busy),default),"A 16-megapixel random pattern holds no code");
        Console.WriteLine($"QR photo reader, 16 MP random blocks without a code: {watch.Elapsed.TotalSeconds:F2}s (this Mac, core only)");
        // Tiled finder look-alikes (2 px modules, 9-module period) stress the candidate list and triples.
        for(int y=0;y<4000;y++)for(int x=0;x<4000;x++)
        {
            int mx=x/2%9,my=y/2%9;bool ink=mx<7&&my<7&&(mx is 0 or 6||my is 0 or 6||mx is >=2 and <=4&&my is >=2 and <=4);
            int i=(y*4000+x)*4;busy[i]=busy[i+1]=busy[i+2]=ink?(byte)20:(byte)235;
        }
        watch.Restart();
        reject(()=>QrPhotoRead.Read(new(4000,4000,busy),default),"A 16-megapixel grid of finder look-alikes holds no code");
        Console.WriteLine($"QR photo reader, 16 MP grid of finder look-alikes: {watch.Elapsed.TotalSeconds:F2}s (this Mac, core only)");
        // Reviewer's stripes: every row offers ~500 finder-like runs and long dark columns.
        for(int y=0;y<4000;y++)for(int x=0;x<4000;x++)
        {
            int phase=x%8;bool ink=y==0||y==3999||y!=1&&y!=3998&&(phase==0||phase>=2&&phase<=4||phase==6);
            int i=(y*4000+x)*4;busy[i]=busy[i+1]=busy[i+2]=ink?(byte)20:(byte)235;
        }
        watch.Restart();
        reject(()=>QrPhotoRead.Read(new(4000,4000,busy),default),"16 megapixels of finder-ratio stripes hold no code");
        Console.WriteLine($"QR photo reader, 16 MP finder-ratio stripes: {watch.Elapsed.TotalSeconds:F2}s (this Mac, core only)");
        check(watch.Elapsed.TotalSeconds<3,"Finder cross-checks are bounded on crafted stripes");
        // Larger versions under perspective rely on the scaled alignment search.
        foreach(int length in new[]{200,400})
        {
            string text=new string('q',length);var code=QrCode.Encode(text,QrCorrection.Low);
            check(QrPhotoRead.Read(Render(code,new(14,4,Perspective:0.08)),default).Text==text,$"Perspective-warped v{code.Version} decodes with the alignment square");
        }
        // Sampled reads stop at the standard's misdecode protection: 1-L corrects at most 2 codewords.
        {
            var code=QrCode.Encode("HELLO",QrCorrection.Low);int n=code.Size;var raster=code.Raster(1);
            check(code.Version==1,"Misdecode fixture is version 1-L");
            var read=typeof(QrCode).GetMethod("ReadSampledGrid",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            foreach(int damaged in new[]{2,3})
            {
                var grid=new bool[n,n];for(int y=0;y<n;y++)for(int x=0;x<n;x++)grid[x,y]=raster[(y+4)*(n+8)+x+4]==0;
                // The right-hand column's module 4k rows above the corner is bit 8k: codeword k.
                for(int k=0;k<damaged;k++)grid[n-1,n-1-4*k]^=true;
                var result=(QrDataRecoveryResult?)read.Invoke(null,new object[]{grid,1,CancellationToken.None});
                check(damaged==2?result?.Result.Text=="HELLO"&&result.CorrectedCodewords==2:result is null,$"Sampled 1-L read with {damaged} damaged codewords respects misdecode protection");
            }
        }
        using(var cancel=new CancellationTokenSource())
        {
            int polls=0;var image=Render(codes[2].Code,new(40,4,Noise:10));
            // Cancellation reaches the photo stage's inner loops (after the strict reader gives up).
            var thread=new Thread(()=>{while(!cancel.IsCancellationRequested){polls++;Thread.Sleep(1);if(polls>3)cancel.Cancel();}});
            thread.Start();bool cancelled=false;
            try{for(int i=0;i<50&&!cancelled;i++)QrPhotoRead.Read(image,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            thread.Join();check(cancelled,"A photo read observes cancellation");
        }
    }
}
