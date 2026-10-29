using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

/// The QR reader used by Read QR Image and the agent's read_codes: the strict flat reader first
/// (its result is returned unchanged), then one bounded, original photo/screenshot attempt.
/// Photo steps: grayscale, block-local threshold, 1:1:3:1:1 finder runs checked across and down,
/// a few candidate finder triples, a version estimate from finder spacing (confirmed by version
/// information from version 7), the lower-right alignment square for perspective when found
/// (else a three-finder affine fit), grid sampling, then the shared format/RS/text decoder.
public static class QrPhotoRead
{
    public const string Scope="Reading first tries exact flat QR images: square, at 0°, 90°, 180° or 270°, with an exact four-module white border and uniform whole-number pixel scaling, read without error correction. If that does not match, a photo reader looks for one dark-on-light Model 2 QR code anywhere in the image (up to 16 megapixels): any rotation, mild perspective (larger codes need a more head-on view), moderate blur, noise, uneven lighting and colored backgrounds. Flat images that fail the exact rules, such as smoothly resized or slightly damaged ones, are read this way too. It finds the three corner squares, uses the lower-right alignment square when it can, samples the grid and then uses the code's own error correction. Only one code is read per image; when several codes are present, which one is read is not defined. Mirrored, light-on-dark, curved, very small (under about 2 pixels per module) or heavily damaged codes, codes with covered corner squares, and barcodes are not supported. Numeric, alphanumeric, ASCII bytes and explicit UTF-8 ECI 26 only; other byte encodings, Kanji, FNC1 and structured append are not supported. Error correction can in rare cases accept a different valid message. This does not authenticate the text or its source.";
    public const string NoCode="No supported QR code was found. The image may contain no QR code, or one that is too small, blurred, curved, mirrored, light-on-dark, partly covered or damaged beyond its error correction, or text in an unsupported format.";
    internal const int MaxCandidates=12,MaxTriples=16,MaxStoredCandidates=512;

    public static QrReadResult Read(PixelImage image,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();image.Validate();
        try{return QrFlatRead.Read(image,token);}
        catch(UserError error)when(error.Message==QrFlatRead.NoCode){}
        token.ThrowIfCancellationRequested();
        return ReadPhoto(image,token)??throw new UserError(NoCode);
    }

    private sealed class Finder
    {
        public double X,Y,Module;public int Count;
    }
    private readonly record struct Point(double X,double Y);

    internal static QrReadResult? ReadPhoto(PixelImage image,CancellationToken token)
    {
        int width=image.Width,height=image.Height;
        if(width<21||height<21)return null;
        var dark=Binarize(Gray(image,token),width,height,token);
        var finders=FindFinders(dark,width,height,token);
        foreach(var (topLeft,topRight,bottomLeft) in Triples(finders,token))
        {
            token.ThrowIfCancellationRequested();
            var result=TryTriple(dark,width,height,topLeft,topRight,bottomLeft,token);
            if(result is not null)return result;
        }
        return null;
    }

    // Alpha is composited over white, then integer luma (BT.601 weights).
    private static byte[] Gray(PixelImage image,CancellationToken token)
    {
        var b=image.Bgra;var gray=new byte[image.Width*image.Height];
        for(int y=0;y<image.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<image.Width;x++)
            {
                int i=y*image.Width+x,p=i*4,luma=(b[p]*117+b[p+1]*601+b[p+2]*306)>>10,a=b[p+3];
                gray[i]=(byte)((luma*a+255*(255-a)+127)/255);
            }
        }
        return gray;
    }

    // 8×8 blocks; each block's threshold mixes the mean and min/max midpoint of its 5×5
    // block neighborhood. Nearly uniform neighborhoods use the global Otsu threshold instead,
    // so large modules and plain backgrounds don't turn into noise.
    private static byte[] Binarize(byte[] gray,int width,int height,CancellationToken token)
    {
        const int block=8;int bw=(width+block-1)/block,bh=(height+block-1)/block;
        var means=new int[bw*bh];var mins=new byte[bw*bh];var maxs=new byte[bw*bh];var histogram=new long[256];
        for(int by=0;by<bh;by++)
        {
            token.ThrowIfCancellationRequested();
            for(int bx=0;bx<bw;bx++)
            {
                int sum=0,count=0,low=255,high=0;
                for(int y=by*block;y<Math.Min(height,by*block+block);y++)for(int x=bx*block;x<Math.Min(width,bx*block+block);x++)
                {int v=gray[y*width+x];sum+=v;count++;if(v<low)low=v;if(v>high)high=v;histogram[v]++;}
                means[by*bw+bx]=sum/count;mins[by*bw+bx]=(byte)low;maxs[by*bw+bx]=(byte)high;
            }
        }
        int global=Otsu(histogram,(long)width*height);
        var dark=new byte[width*height];
        for(int by=0;by<bh;by++)
        {
            token.ThrowIfCancellationRequested();
            for(int bx=0;bx<bw;bx++)
            {
                int sum=0,count=0,low=255,high=0;
                for(int ny=Math.Max(0,by-2);ny<=Math.Min(bh-1,by+2);ny++)for(int nx=Math.Max(0,bx-2);nx<=Math.Min(bw-1,bx+2);nx++)
                {int k=ny*bw+nx;sum+=means[k];count++;low=Math.Min(low,mins[k]);high=Math.Max(high,maxs[k]);}
                int threshold=high-low<40?global:(sum/count+(low+high)/2)/2;
                for(int y=by*block;y<Math.Min(height,by*block+block);y++)for(int x=bx*block;x<Math.Min(width,bx*block+block);x++)
                    dark[y*width+x]=gray[y*width+x]<threshold?(byte)1:(byte)0;
            }
        }
        return dark;
    }

    private static int Otsu(long[] histogram,long total)
    {
        double sum=0;for(int i=0;i<256;i++)sum+=i*(double)histogram[i];
        double below=0,weight=0,best=-1;int threshold=128;
        for(int t=0;t<256;t++)
        {
            weight+=histogram[t];if(weight==0)continue;double rest=total-weight;if(rest==0)break;
            below+=t*(double)histogram[t];double m0=below/weight,m1=(sum-below)/rest,between=weight*rest*(m0-m1)*(m0-m1);
            if(between>best){best=between;threshold=t+1;}
        }
        return threshold;
    }

    private static bool Ratio(ReadOnlySpan<int> runs,out double module)
    {
        int total=0;foreach(int run in runs)total+=run;module=total/7.0;
        if(total<7)return false;double v=module*0.7;
        return Math.Abs(runs[0]-module)<v&&Math.Abs(runs[1]-module)<v&&Math.Abs(runs[2]-3*module)<3*v&&Math.Abs(runs[3]-module)<v&&Math.Abs(runs[4]-module)<v;
    }

    private static List<Finder> FindFinders(byte[] dark,int width,int height,CancellationToken token)
    {
        var found=new List<Finder>();Span<int> runs=stackalloc int[5];var starts=new List<int>();var lengths=new List<int>();
        for(int y=0;y<height;y++)
        {
            token.ThrowIfCancellationRequested();starts.Clear();lengths.Clear();int row=y*width;
            for(int x=0;x<width;)
            {
                int start=x;byte color=dark[row+x];while(x<width&&dark[row+x]==color)x++;
                if(color==1||starts.Count>0){starts.Add(start);lengths.Add(x-start);}
            }
            // Runs alternate and the list starts dark, so even indices are dark.
            for(int i=0;i+4<starts.Count;i+=2)
            {
                for(int k=0;k<5;k++)runs[k]=lengths[i+k];
                if(!Ratio(runs,out double module))continue;
                double cx=starts[i+2]+lengths[i+2]/2.0;
                int maxRun=(int)Math.Ceiling(module*14);
                if(!Cross(dark,width,height,cx,y,false,out double cy,out double vertical,maxRun)||Math.Abs(vertical-module)>module*0.5)continue;
                if(!Cross(dark,width,height,cx,cy,true,out double rx,out double horizontal,maxRun)||Math.Abs(horizontal-module)>module*0.5)continue;
                if(!Cross(dark,width,height,rx,cy,false,out double ry,out double again,maxRun))continue;
                double size=(horizontal+again)/2;Add(found,rx,ry,size,y);
            }
        }
        var strong=found.Where(f=>f.Count>=2).ToList();if(strong.Count<3)strong=found;
        return strong.OrderByDescending(f=>f.Count).ThenBy(f=>f.Y).ThenBy(f=>f.X).Take(MaxCandidates).ToList();
    }

    // Merges a hit into a nearby candidate. When the list is full, the lowest-count candidate well
    // above the current row (rows are scanned top down, so it can't grow) makes room for a new one;
    // the final choice also ranks by count.
    private static void Add(List<Finder> found,double x,double y,double module,int row)
    {
        foreach(var f in found)if(Math.Abs(f.X-x)<=f.Module*2&&Math.Abs(f.Y-y)<=f.Module*2&&Math.Abs(f.Module-module)<=Math.Max(1,f.Module*0.5))
        {
            f.X=(f.X*f.Count+x)/(f.Count+1);f.Y=(f.Y*f.Count+y)/(f.Count+1);f.Module=(f.Module*f.Count+module)/(f.Count+1);f.Count++;return;
        }
        var entry=new Finder{X=x,Y=y,Module=module,Count=1};
        if(found.Count<MaxStoredCandidates){found.Add(entry);return;}
        int stale=-1;
        for(int i=0;i<found.Count;i++)if(found[i].Y<row-found[i].Module*8&&(stale<0||found[i].Count<found[stale].Count))stale=i;
        if(stale>=0)found[stale]=entry;
    }

    // Walks out from (x,y) along a row or column, collecting the five finder runs; returns the
    // middle run's center on that axis.
    // maxRun bounds every walk (a crafted image could otherwise make each check scan a whole column).
    private static bool Cross(byte[] dark,int width,int height,double x,double y,bool horizontal,out double center,out double module,int maxRun=int.MaxValue)
    {
        center=0;module=0;int px=(int)x,py=(int)y;
        if(px<0||py<0||px>=width||py>=height)return false;
        int limit=horizontal?width:height,origin=horizontal?px:py;
        bool Dark(int i)=>horizontal?dark[py*width+i]==1:dark[i*width+px]==1;
        if(!Dark(origin))return false;
        Span<int> runs=stackalloc int[5];
        int i=origin;while(i>=0&&Dark(i)&&runs[2]<=maxRun){runs[2]++;i--;}int low=i+1;
        while(i>=0&&!Dark(i)&&runs[1]<=maxRun){runs[1]++;i--;}if(i<0||runs[1]>maxRun||runs[2]>maxRun)return false;
        while(i>=0&&Dark(i)&&runs[0]<=runs[2]){runs[0]++;i--;}
        i=origin+1;while(i<limit&&Dark(i)&&runs[2]<=maxRun){runs[2]++;i++;}int high=i;
        while(i<limit&&!Dark(i)&&runs[3]<=maxRun){runs[3]++;i++;}if(i>=limit||runs[2]>maxRun||runs[3]>maxRun)return false;
        while(i<limit&&Dark(i)&&runs[4]<=runs[2]){runs[4]++;i++;}
        if(!Ratio(runs,out module))return false;
        center=(low+high)/2.0;return true;
    }

    // Candidate triples ranked by how square and even they look; only the best few are sampled.
    private static IEnumerable<(Point,Point,Point)> Triples(List<Finder> finders,CancellationToken token)
    {
        var ranked=new List<(double Score,Point A,Point B,Point C)>();
        for(int i=0;i<finders.Count;i++)for(int j=i+1;j<finders.Count;j++)for(int k=j+1;k<finders.Count;k++)
        {
            token.ThrowIfCancellationRequested();
            var f=new[]{finders[i],finders[j],finders[k]};
            double minModule=f.Min(x=>x.Module),maxModule=f.Max(x=>x.Module);
            if(maxModule>minModule*1.6)continue;
            // Top left is the vertex opposite the longest side.
            double Dist(Finder a,Finder b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
            double ab=Dist(f[0],f[1]),bc=Dist(f[1],f[2]),ca=Dist(f[2],f[0]);
            int corner=bc>=ab&&bc>=ca?0:ca>=ab?1:2;
            var tl=f[corner];var p=f[(corner+1)%3];var q=f[(corner+2)%3];
            double s1=Dist(tl,p),s2=Dist(tl,q),hyp=Dist(p,q),module=(f[0].Module+f[1].Module+f[2].Module)/3;
            if(Math.Min(s1,s2)<module*8||Math.Max(s1,s2)>module*250)continue;
            double aspect=Math.Max(s1,s2)/Math.Min(s1,s2),right=Math.Abs(hyp*hyp/(s1*s1+s2*s2)-1);
            if(aspect>1.6||right>0.35)continue;
            double cross=(p.X-tl.X)*(q.Y-tl.Y)-(p.Y-tl.Y)*(q.X-tl.X);
            var (tr,bl)=cross>0?(p,q):(q,p);
            // Perspective makes near finders larger, so module mismatch counts half; finders seen in
            // few rows (often data-area look-alikes) rank lower.
            int fewest=Math.Min(f[0].Count,Math.Min(f[1].Count,f[2].Count));
            ranked.Add((aspect-1+right+(maxModule/minModule-1)/2+1.0/fewest,new(tl.X,tl.Y),new(tr.X,tr.Y),new(bl.X,bl.Y)));
        }
        foreach(var t in ranked.OrderBy(t=>t.Score).Take(MaxTriples))yield return (t.A,t.B,t.C);
    }

    private static QrReadResult? TryTriple(byte[] dark,int width,int height,Point tl,Point tr,Point bl,CancellationToken token)
    {
        double angle=Math.Atan2(tr.Y-tl.Y,tr.X-tl.X);
        // Finder runs across a turned square are longer by 1/cos of the turn folded into 0–45°.
        double fold=Math.Abs(Math.IEEERemainder(angle,Math.PI/2)),stretch=1/Math.Cos(fold);
        var sizes=new double[3];double module=0;int count=0;var corners=new[]{tl,tr,bl};
        for(int k=0;k<3;k++)
            if(Cross(dark,width,height,corners[k].X,corners[k].Y,true,out _,out double h)&&Cross(dark,width,height,corners[k].X,corners[k].Y,false,out _,out double v))
            {sizes[k]=(h+v)/2/stretch;module+=sizes[k];count++;}
        if(count==0)return null;module/=count;
        for(int k=0;k<3;k++)if(sizes[k]==0)sizes[k]=module;
        // Each side is measured in the modules of its own two finders, which differ under perspective.
        double span=(Distance(tl,tr)/((sizes[0]+sizes[1])/2)+Distance(tl,bl)/((sizes[0]+sizes[2])/2))/2+7;
        int nearest=(int)Math.Round((span-17)/4)*4+17;
        var dimensions=new List<int>();
        foreach(int d in new[]{nearest,nearest+4,nearest-4})if(d is >=21 and <=177)dimensions.Add(d);
        var tried=new HashSet<int>();
        foreach(int first in dimensions)
        {
            int dimension=first;
            for(int pass=0;pass<2;pass++)
            {
                token.ThrowIfCancellationRequested();if(!tried.Add(dimension))break;
                int? confirmed=null;
                foreach(bool alignment in new[]{true,false})
                {
                    if(alignment&&dimension==21)continue;
                    var transform=Fit(dark,width,height,tl,tr,bl,dimension,module,stretch,alignment,token);
                    if(transform is null)continue;
                    var grid=Sample(dark,width,height,transform,dimension,token);
                    if(grid is null)continue;
                    if(dimension>=45)
                    {
                        // Version information decides the size from version 7 on.
                        int? version=QrCode.ReadVersionInformation(grid,token);
                        if(version is null)continue;
                        if(17+4*version.Value!=dimension){confirmed??=17+4*version.Value;continue;}
                    }
                    var decoded=QrCode.ReadSampledGrid(grid,Math.Max(1,(int)Math.Round(module)),token);
                    if(decoded is null)continue;
                    int degrees=((int)Math.Round(angle*180/Math.PI)%360+360)%360;
                    return decoded.Result with{ClockwiseOrientationDegrees=degrees,Photo=true,CorrectedCodewords=decoded.CorrectedCodewords,CorrectedBlocks=decoded.CorrectedBlocks};
                }
                if(confirmed is null)break;dimension=confirmed.Value;
            }
        }
        return null;
    }

    private static double Distance(Point a,Point b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));

    // Module coordinates → image: a projective fit through the finder centers and either the
    // detected lower-right alignment center or the affine fourth corner.
    private static double[]? Fit(byte[] dark,int width,int height,Point tl,Point tr,Point bl,int dimension,double module,double stretch,bool alignment,CancellationToken token)
    {
        double far=dimension-3.5;
        var source=new List<Point>{new(3.5,3.5),new(far,3.5),new(3.5,far)};var target=new List<Point>{tl,tr,bl};
        if(alignment)
        {
            double a=dimension-6.5;var affine=Solve(source.Append(new(far,far)).ToArray(),target.Append(new(tr.X+bl.X-tl.X,tr.Y+bl.Y-tl.Y)).ToArray());
            if(affine is null)return null;var guess=Map(affine,a,a);
            var found=FindAlignment(dark,width,height,guess,Map(affine,a+1,a),Map(affine,a,a+1),module,stretch,Math.Max(7,dimension*0.12),token);
            if(found is null)return null;source.Add(new(a,a));target.Add(found.Value);
        }
        else{source.Add(new(far,far));target.Add(new(tr.X+bl.X-tl.X,tr.Y+bl.Y-tl.Y));}
        return Solve(source.ToArray(),target.ToArray());
    }

    // Light-dark-light runs of about one module each, checked across and down; the candidate whose
    // surroundings best match the 5×5 alignment square (sampled along the fitted module axes) wins,
    // then the one nearest the guess.
    private static Point? FindAlignment(byte[] dark,int width,int height,Point guess,Point right,Point down,double module,double stretch,double modules,CancellationToken token)
    {
        // The three-finder guess drifts further under perspective as codes get larger.
        double expected=module*stretch;int radius=(int)Math.Ceiling(module*modules);
        double ux=right.X-guess.X,uy=right.Y-guess.Y,vx=down.X-guess.X,vy=down.Y-guess.Y;
        int Matches(double cx,double cy)
        {
            int matches=0;
            for(int j=-2;j<=2;j++)for(int i=-2;i<=2;i++)
            {
                int x=(int)Math.Floor(cx+i*ux+j*vx),y=(int)Math.Floor(cy+i*uy+j*vy);
                bool want=Math.Max(Math.Abs(i),Math.Abs(j))!=1;
                if(x>=0&&y>=0&&x<width&&y<height&&(dark[y*width+x]==1)==want)matches++;
            }
            return matches;
        }
        int bestMatches=21;
        int x0=Math.Max(0,(int)guess.X-radius),x1=Math.Min(width-1,(int)guess.X+radius),y0=Math.Max(0,(int)guess.Y-radius),y1=Math.Min(height-1,(int)guess.Y+radius);
        Point? best=null;double bestDistance=double.MaxValue;
        bool Near(int run)=>run>=expected*0.4&&run<=expected*1.7;
        bool Check(double cx,double cy,bool horizontal,out double center)
        {
            center=0;int px=(int)cx,py=(int)cy;if(px<0||py<0||px>=width||py>=height)return false;
            bool Dark(int i)=>horizontal?dark[py*width+i]==1:dark[i*width+px]==1;
            int limit=horizontal?width:height,origin=horizontal?px:py;if(!Dark(origin))return false;
            int i=origin,mid=0;while(i>=0&&Dark(i)){mid++;i--;}int low=i+1,before=0;
            while(i>=0&&!Dark(i)){before++;i--;}if(i<0)return false;
            i=origin+1;while(i<limit&&Dark(i)){mid++;i++;}int high=i,after=0;
            while(i<limit&&!Dark(i)){after++;i++;}if(i>=limit)return false;
            if(!Near(mid)||!Near(before)||!Near(after))return false;center=(low+high)/2.0;return true;
        }
        for(int y=y0;y<=y1;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=x0;x<=x1;x++)
            {
                if(dark[y*width+x]!=1||x>0&&dark[y*width+x-1]==1)continue;
                if(!Check(x,y,true,out double cx)||!Check(cx,y,false,out double cy)||!Check(cx,cy,true,out double rx))continue;
                double distance=(rx-guess.X)*(rx-guess.X)+(cy-guess.Y)*(cy-guess.Y);int matches=Matches(rx,cy);
                if(matches>bestMatches||matches==bestMatches&&distance<bestDistance){bestMatches=matches;bestDistance=distance;best=new(rx,cy);}
            }
        }
        return best;
    }

    // Eight-unknown projective mapping through four point pairs (Gaussian elimination).
    private static double[]? Solve(Point[] from,Point[] to)
    {
        var m=new double[8,9];
        for(int i=0;i<4;i++)
        {
            double u=from[i].X,v=from[i].Y,x=to[i].X,y=to[i].Y;
            double[] r1={u,v,1,0,0,0,-u*x,-v*x,x},r2={0,0,0,u,v,1,-u*y,-v*y,y};
            for(int j=0;j<9;j++){m[2*i,j]=r1[j];m[2*i+1,j]=r2[j];}
        }
        for(int c=0;c<8;c++)
        {
            int pivot=c;for(int r=c+1;r<8;r++)if(Math.Abs(m[r,c])>Math.Abs(m[pivot,c]))pivot=r;
            if(Math.Abs(m[pivot,c])<1e-9)return null;
            for(int j=0;j<9;j++)(m[c,j],m[pivot,j])=(m[pivot,j],m[c,j]);
            for(int r=0;r<8;r++)
            {
                if(r==c)continue;double f=m[r,c]/m[c,c];if(f==0)continue;
                for(int j=c;j<9;j++)m[r,j]-=f*m[c,j];
            }
        }
        var h=new double[8];for(int i=0;i<8;i++)h[i]=m[i,8]/m[i,i];
        return h.All(double.IsFinite)?h:null;
    }

    private static Point Map(double[] h,double u,double v)
    {
        double w=h[6]*u+h[7]*v+1;
        return new((h[0]*u+h[1]*v+h[2])/w,(h[3]*u+h[4]*v+h[5])/w);
    }

    // Five-point majority per module center; a module outside the image fails this fit.
    private static bool[,]? Sample(byte[] dark,int width,int height,double[] transform,int dimension,CancellationToken token)
    {
        var grid=new bool[dimension,dimension];
        ReadOnlySpan<double> du=stackalloc double[]{0,-0.2,0.2,-0.2,0.2},dv=stackalloc double[]{0,-0.2,-0.2,0.2,0.2};
        for(int my=0;my<dimension;my++)
        {
            token.ThrowIfCancellationRequested();
            for(int mx=0;mx<dimension;mx++)
            {
                int votes=0;
                for(int k=0;k<5;k++)
                {
                    var p=Map(transform,mx+0.5+du[k],my+0.5+dv[k]);
                    if(!double.IsFinite(p.X)||!double.IsFinite(p.Y))return null;
                    int x=(int)Math.Floor(p.X),y=(int)Math.Floor(p.Y);
                    if(x<0||y<0||x>=width||y>=height)return null;
                    votes+=dark[y*width+x];
                }
                grid[mx,my]=votes>=3;
            }
        }
        return grid;
    }
}
