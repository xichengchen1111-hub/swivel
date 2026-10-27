using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

public sealed record PdfPageSize(double Width,double Height);
public sealed record PdfRasterPage(int Index,int Width,int Height,double PointsWidth,double PointsHeight,int Turn);
public enum PdfRasterOutput{CombinedPdf,SeparatePdf,Png}
public static class PdfRasterPlan
{
    public const int MaxPages=1000,MaxSelected=50;
    public const string Selection="Enter 1–50 page numbers or ranges, such as 1,3-5,2. Only ASCII numbers are accepted; repeats and descending ranges are allowed. The listed order is the output order.";
    public const string Invalid="Choose valid PDF pages, a longest edge from 400–4,000 pixels and a rotation of 0, 90, 180 or 270 degrees. Pages must have finite dimensions from 1 to 14,400 points per side.";
    public const string Limit="PDF input is limited to 10 files, 32 MiB each and 128 MiB in total; at most 1,000 source pages and 50 selected pages. Each rendered page is limited to 16 megapixels and the selected total to 100 megapixels. These are not hard limits on the system renderer's total memory or CPU use.";
    public static PdfPageSize FromDip(double width,double height)
    {
        double w=width*0.75,h=height*0.75;
        if(!double.IsFinite(w)||!double.IsFinite(h)||w<1||h<1||w>14400||h>14400)throw new UserError(Invalid);
        return new(w,h);
    }
    public static int[] Parse(string text,int count)
    {
        if(count is <1 or >MaxPages||text.Length is <1 or >512)throw new UserError(Selection);var pages=new List<int>();
        int Number(string s){s=s.Trim(' ','\t');if(!int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<1||n>count||s.Any(c=>c is <'0' or >'9'))throw new UserError(Selection);return n-1;}
        void Add(int n){if(pages.Count>=MaxSelected)throw new UserError(Selection);pages.Add(n);}
        foreach(string part in text.Split(','))
        {
            var range=part.Split('-');if(range.Length==1)Add(Number(part));else if(range.Length==2){int start=Number(range[0]),end=Number(range[1]),step=start<=end?1:-1;for(int n=start;;n+=step){Add(n);if(n==end)break;}}else throw new UserError(Selection);
        }
        return pages.ToArray();
    }
    public static PdfRasterPage[] Build(IReadOnlyList<PdfPageSize> pages,IReadOnlyList<int> order,int edge,int turn)
    {
        if(pages.Count is <1 or >MaxPages||order.Count is <1 or >MaxSelected||edge is <400 or >4000||turn is not 0 and not 90 and not 180 and not 270)throw new UserError(Invalid);
        var result=new List<PdfRasterPage>();long total=0;
        foreach(int index in order)
        {
            if(index<0||index>=pages.Count)throw new UserError(Invalid);var page=pages[index];
            if(!double.IsFinite(page.Width)||!double.IsFinite(page.Height)||page.Width<1||page.Height<1||page.Width>14400||page.Height>14400)throw new UserError(Invalid);
            double scale=Math.Min(edge/page.Width,edge/page.Height);int w=Math.Max(1,(int)Math.Round(page.Width*scale,MidpointRounding.AwayFromZero)),h=Math.Max(1,(int)Math.Round(page.Height*scale,MidpointRounding.AwayFromZero));
            if(w>edge||h>edge)throw new UserError(Invalid);ImageRules.Validate(w,h);total+=(long)w*h;if(total>100_000_000)throw new UserError(Limit);
            bool rotated=turn is 90 or 270;result.Add(new(index,rotated?h:w,rotated?w:h,rotated?page.Height:page.Width,rotated?page.Width:page.Height,turn));
        }
        return result.ToArray();
    }
    public static PixelImage Rotate(PixelImage source,int turn)
    {
        source.Validate();if(turn is not 0 and not 90 and not 180 and not 270)throw new UserError(Invalid);bool swap=turn is 90 or 270;int w=swap?source.Height:source.Width,h=swap?source.Width:source.Height;var bytes=new byte[source.Bgra.Length];
        for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++){(int ox,int oy)=turn switch{90=>(source.Height-1-y,x),180=>(source.Width-1-x,source.Height-1-y),270=>(y,source.Width-1-x),_=>(x,y)};source.Bgra.AsSpan((y*source.Width+x)*4,4).CopyTo(bytes.AsSpan((oy*w+ox)*4,4));}
        return new(w,h,bytes);
    }
}
