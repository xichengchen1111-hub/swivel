using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public enum GifDither { None, FloydSteinberg }

// Original bounded, deterministic RGB histogram/weighted-box quantizer.
// The default bin-to-box mapping is retained byte-for-byte. Optional serpentine
// error diffusion uses the same palette, two rows, and exact RGB nearest colors.
public static class GifPalette
{
    public const string Invalid="Choose a GIF palette limit from 2 to 256 entries, including transparency.";
    private struct Bin {internal int Count;internal long R,G,B;}
    public sealed class Result
    {
        private readonly PixelImage image;
        public int Width=>image.Width;public int Height=>image.Height;
        public int Limit{get;}public int Colors{get;}public long ChangedOpaquePixels{get;}public GifDither Dither{get;}
        internal Result(PixelImage image,int limit,int colors,long changed,GifDither dither){this.image=image;Limit=limit;Colors=colors;ChangedOpaquePixels=changed;Dither=dither;}
        public PixelImage Preview(int edge,CancellationToken token)=>Thumbnail(image,edge,token);
        public void Write(Stream output,CancellationToken token)=>GifExport.Write(image,output,token);
    }
    private sealed class Box
    {
        internal int Start,Count,Axis;internal long Weight,R,G,B,Score;
        internal Box(int start,int count,int[] indices,Bin[] histogram,CancellationToken token)
        {
            Start=start;Count=count;int r0=31,g0=31,b0=31,r1=0,g1=0,b1=0;
            for(int i=start;i<start+count;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();int key=indices[i],r=key>>10,g=(key>>5)&31,b=key&31;var bin=histogram[key];
                r0=Math.Min(r0,r);g0=Math.Min(g0,g);b0=Math.Min(b0,b);r1=Math.Max(r1,r);g1=Math.Max(g1,g);b1=Math.Max(b1,b);
                Weight+=bin.Count;R+=bin.R;G+=bin.G;B+=bin.B;
            }
            int range=r1-r0;Axis=0;if(g1-g0>range){range=g1-g0;Axis=1;}if(b1-b0>range){range=b1-b0;Axis=2;}
            Score=count>1?(range+1)*Weight:0;
        }
    }
    public static Result Reduce(PixelImage input,int maximum,CancellationToken token)
        =>Reduce(input,maximum,GifDither.None,token);
    public static Result Reduce(PixelImage input,int maximum,GifDither dither,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(input);input.Validate();
        if(maximum is <2 or >256)throw new UserError(Invalid);
        if(dither is not GifDither.None and not GifDither.FloydSteinberg)throw new ArgumentOutOfRangeException(nameof(dither));
        var histogram=new Bin[32768];var exact=new HashSet<int>();bool excess=false,transparent=false;
        int pixels=input.Width*input.Height;
        for(int i=0;i<pixels;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();int p=i*4,a=input.Bgra[p+3],b=input.Bgra[p],g=input.Bgra[p+1],r=input.Bgra[p+2];
            if(a is not 0 and not 255)throw new UserError(GifExport.Alpha);
            int color=a==0?-1:b|(g<<8)|(r<<16);
            if(!excess&&exact.Add(color)&&exact.Count>maximum){excess=true;exact.Clear();}
            if(a==0){transparent=true;continue;}
            int key=(r>>3)<<10|(g>>3)<<5|(b>>3);ref var bin=ref histogram[key];bin.Count++;bin.R+=r;bin.G+=g;bin.B+=b;
        }
        token.ThrowIfCancellationRequested();var output=new byte[input.Bgra.Length];long changed=0;int colors;
        if(!excess)
        {
            // Preserve every visible normalized RGB when it already fits the limit.
            // Copy in cancellable pages; hidden transparent RGB is normalized below.
            for(int at=0;at<output.Length;at+=65536){token.ThrowIfCancellationRequested();input.Bgra.AsSpan(at,Math.Min(65536,output.Length-at)).CopyTo(output.AsSpan(at));}
            colors=exact.Count;
        }
        else
        {
            int used=0;foreach(var bin in histogram)if(bin.Count>0)used++;
            var indices=new int[used];for(int k=0,at=0;k<histogram.Length;k++)if(histogram[k].Count>0)indices[at++]=k;
            var boxes=new List<Box>(maximum){new(0,used,indices,histogram,token)};int opaqueLimit=maximum-(transparent?1:0);
            while(boxes.Count<opaqueLimit)
            {
                token.ThrowIfCancellationRequested();int chosen=-1;long best=0;
                for(int i=0;i<boxes.Count;i++)if(boxes[i].Score>best){best=boxes[i].Score;chosen=i;}
                if(chosen<0)break;var box=boxes[chosen];int axis=box.Axis;
                int Component(int key)=>axis==0?key>>10:axis==1?(key>>5)&31:key&31;
                // Unique bin ID breaks ties: sort order is deterministic without
                // throwing from a comparer (Array.Sort wraps comparer exceptions).
                Array.Sort(indices,box.Start,box.Count,Comparer<int>.Create((a,b)=>{int c=Component(a).CompareTo(Component(b));return c!=0?c:a.CompareTo(b);}));
                token.ThrowIfCancellationRequested();long sum=0,half=(box.Weight+1)/2;int cut=box.Start;
                while(cut<box.Start+box.Count-1){sum+=histogram[indices[cut++]].Count;if(sum>=half)break;}
                // Retire parent and append both children. Creation order breaks
                // equal-score ties in later iterations.
                boxes.RemoveAt(chosen);boxes.Add(new(box.Start,cut-box.Start,indices,histogram,token));boxes.Add(new(cut,box.Start+box.Count-cut,indices,histogram,token));
            }
            var map=new int[32768];var palette=new HashSet<int>();if(transparent)palette.Add(-1);
            foreach(var box in boxes)
            {
                token.ThrowIfCancellationRequested();int r=(int)((box.R+box.Weight/2)/box.Weight),g=(int)((box.G+box.Weight/2)/box.Weight),b=(int)((box.B+box.Weight/2)/box.Weight),color=b|(g<<8)|(r<<16);palette.Add(color);
                for(int i=box.Start;i<box.Start+box.Count;i++){if((i&4095)==0)token.ThrowIfCancellationRequested();map[indices[i]]=color;}
            }
            colors=palette.Count;
            if(dither==GifDither.FloydSteinberg)
                (changed,colors)=Diffuse(input,output,palette,token);
            else for(int i=0;i<pixels;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();int p=i*4;if(input.Bgra[p+3]==0)continue;
                int b=input.Bgra[p],g=input.Bgra[p+1],r=input.Bgra[p+2],color=map[(r>>3)<<10|(g>>3)<<5|(b>>3)];
                output[p]=(byte)color;output[p+1]=(byte)(color>>8);output[p+2]=(byte)(color>>16);output[p+3]=255;
                if(color!=(b|(g<<8)|(r<<16)))changed++;
            }
        }
        for(int i=0;i<pixels;i++){if((i&4095)==0)token.ThrowIfCancellationRequested();int p=i*4;if(output[p+3]==0)output[p]=output[p+1]=output[p+2]=0;}
        token.ThrowIfCancellationRequested();return new(new(input.Width,input.Height,output),maximum,colors,changed,dither);
    }
    private static (long Changed,int Colors) Diffuse(PixelImage input,byte[] output,HashSet<int> palette,CancellationToken token)
    {
        // Transparent slots never participate in RGB search. All nodes together
        // contain at most 256 entries; sorted color IDs deterministically break ties.
        var opaque=new List<int>(palette.Count);foreach(int color in palette)if(color>=0)opaque.Add(color);
        var search=new ColorSearch(opaque.ToArray());var used=new HashSet<int>();long changed=0;
        // At the 16,384-pixel width limit, these two double rows total 786,528 bytes.
        // Padding swallows outside-image error; it is never redistributed inward.
        var current=new double[(input.Width+2)*3];var next=new double[current.Length];
        for(int y=0;y<input.Height;y++)
        {
            token.ThrowIfCancellationRequested();int direction=(y&1)==0?1:-1;
            for(int x=direction==1?0:input.Width-1;x>=0&&x<input.Width;x+=direction)
            {
                if((x&1023)==0)token.ThrowIfCancellationRequested();int p=(y*input.Width+x)*4,at=(x+1)*3;
                // Do not flatten transparency or diffuse through transparent pixels.
                if(input.Bgra[p+3]==0){used.Add(-1);continue;}
                double b=Math.Clamp(input.Bgra[p]+current[at],0,255),g=Math.Clamp(input.Bgra[p+1]+current[at+1],0,255),r=Math.Clamp(input.Bgra[p+2]+current[at+2],0,255);
                int color=search.Nearest(b,g,r);used.Add(color);output[p]=(byte)color;output[p+1]=(byte)(color>>8);output[p+2]=(byte)(color>>16);output[p+3]=255;
                if(color!=(input.Bgra[p]|input.Bgra[p+1]<<8|input.Bgra[p+2]<<16))changed++;
                double eb=b-output[p],eg=g-output[p+1],er=r-output[p+2];
                Add(current,at+direction*3,7d/16,eb,eg,er);Add(next,at-direction*3,3d/16,eb,eg,er);Add(next,at,5d/16,eb,eg,er);Add(next,at+direction*3,1d/16,eb,eg,er);
            }
            (current,next)=(next,current);Array.Clear(next);
        }
        token.ThrowIfCancellationRequested();return(changed,used.Count);
    }
    private static void Add(double[] row,int at,double weight,double b,double g,double r)
    {row[at]+=b*weight;row[at+1]+=g*weight;row[at+2]+=r*weight;}
    private sealed class ColorSearch
    {
        private readonly struct Node(int color,int axis,int left,int right)
        {internal readonly int Color=color,Axis=axis,Left=left,Right=right;}
        private readonly Node[] nodes;private int count;private readonly int root;
        internal ColorSearch(int[] colors){nodes=new Node[colors.Length];root=Build(colors,0,colors.Length);}
        private static int Component(int color,int axis)=>(color>>(axis*8))&255;
        private int Build(int[] colors,int start,int length)
        {
            if(length==0)return-1;int axis=0,spread=-1;
            for(int a=0;a<3;a++){int low=255,high=0;for(int i=start;i<start+length;i++){int value=Component(colors[i],a);low=Math.Min(low,value);high=Math.Max(high,value);}if(high-low>spread){spread=high-low;axis=a;}}
            Array.Sort(colors,start,length,Comparer<int>.Create((a,b)=>{int order=Component(a,axis).CompareTo(Component(b,axis));return order!=0?order:a.CompareTo(b);}));
            int middle=start+length/2,index=count++,color=colors[middle];int left=Build(colors,start,middle-start),right=Build(colors,middle+1,start+length-middle-1);nodes[index]=new(color,axis,left,right);return index;
        }
        internal int Nearest(double b,double g,double r)
        {int best=int.MaxValue;double distance=double.PositiveInfinity;Visit(root,b,g,r,ref best,ref distance);return best;}
        private void Visit(int index,double b,double g,double r,ref int best,ref double distance)
        {
            if(index<0)return;var node=nodes[index];double db=b-Component(node.Color,0),dg=g-Component(node.Color,1),dr=r-Component(node.Color,2),candidate=db*db+dg*dg+dr*dr;
            if(candidate<distance||candidate==distance&&node.Color<best){distance=candidate;best=node.Color;}
            double delta=node.Axis==0?db:node.Axis==1?dg:dr;int first=delta<0?node.Left:node.Right,second=delta<0?node.Right:node.Left;
            Visit(first,b,g,r,ref best,ref distance);if(delta*delta<=distance)Visit(second,b,g,r,ref best,ref distance);
        }
    }
    public static PixelImage Thumbnail(PixelImage input,int edge,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();input.Validate();if(edge is <1 or >960)throw new ArgumentOutOfRangeException(nameof(edge));
        double scale=Math.Min(1,(double)edge/Math.Max(input.Width,input.Height));
        var fit=(Width:Math.Max(1,(int)Math.Round(input.Width*scale)),Height:Math.Max(1,(int)Math.Round(input.Height*scale)));var data=new byte[fit.Width*fit.Height*4];
        for(int y=0;y<fit.Height;y++)
        {
            token.ThrowIfCancellationRequested();int sourceY=(int)((long)y*input.Height/fit.Height);
            for(int x=0;x<fit.Width;x++){int sourceX=(int)((long)x*input.Width/fit.Width);input.Bgra.AsSpan((sourceY*input.Width+sourceX)*4,4).CopyTo(data.AsSpan((y*fit.Width+x)*4,4));}
        }
        token.ThrowIfCancellationRequested();return new(fit.Width,fit.Height,data);
    }
}
