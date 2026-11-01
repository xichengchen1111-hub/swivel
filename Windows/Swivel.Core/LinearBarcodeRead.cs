using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record LinearBarcodeResult(string Type,string Text,int X,int Y,int Width,int Height,int ClockwiseOrientationDegrees);

/// Original scan-line decoder. The tables below are symbology parameters, not decoder code.
/// Code 39 deliberately requires Mod43 (removed from the result) and six payload characters;
/// ITF requires Mod10 (retained) and eight digits. Unchecked short symbols are too easy to find
/// in text and QR textures. Both also need seven spatially agreeing lines, versus three otherwise.
public static class LinearBarcodeRead
{
    public const int MaxResults=16;
    // Sampling bounds work on long, thin and 16 MP hostile images. Small bar heights between
    // sampled lines can be missed; there is no timeout that abandons still-running work.
    private const int MaxLines=512,MaxGroups=512,MaxText=128;
    private const string Code39Alphabet="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
    private static readonly int[] Code39Patterns={0x034,0x121,0x061,0x160,0x031,0x130,0x070,0x025,0x124,0x064,0x109,0x049,0x148,0x019,0x118,0x058,0x00d,0x10c,0x04c,0x01c,0x103,0x043,0x142,0x013,0x112,0x052,0x007,0x106,0x046,0x016,0x181,0x0c1,0x1c0,0x091,0x190,0x0d0,0x085,0x184,0x0c4,0x0a8,0x0a2,0x08a,0x02a};
    private static readonly string[] EanWidths={"3211","2221","2122","1411","1132","1231","1114","1312","1213","3112"};
    private static readonly string[] EanParity={"LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG","LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL"};
    private static readonly int[] ItfPatterns={6,17,9,24,5,20,12,3,18,10};
    private static readonly string[] Code128Widths={
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212",
        "112232","122132","122231","113222","123122","123221","223211","221132","221231","213212","223112","312131",
        "311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321",
        "112313","132113","132311","211313","231113","231311","112133","112331","132131","113123","113321","133121",
        "313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114",
        "122411","142112","142211","241211","221114","413111","241112","134111","111242","121142","121241","114212",
        "124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113",
        "114311","411113","411311","113141","114131","311141","411131","211412","211214","211232","2331112"};

    private sealed class Group
    {
        internal required string Type,Text;
        internal int Start,End,FirstRow,LastRow,Count,Step;
        internal double Module;
        internal bool Vertical,Reverse;
    }
    private sealed record Decoded(string Type,string Text,int End,double Module);

    public static IReadOnlyList<LinearBarcodeResult> Read(PixelImage image,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();image.Validate();
        var groups=new List<Group>();
        Scan(image,false,groups,token);Scan(image,true,groups,token);
        var results=new List<LinearBarcodeResult>();
        foreach(var g in groups)
        {
            token.ThrowIfCancellationRequested();
            bool weak=g.Type is "Code 39" or "ITF";
            if(g.Count<(weak?7:3)||g.LastRow-g.FirstRow<g.Module*(weak?10:4))continue;
            int length=g.Vertical?image.Height:image.Width;
            int start=g.Reverse?length-g.End:g.Start,end=g.Reverse?length-g.Start:g.End;
            int across=Math.Min(g.Vertical?image.Width:image.Height,g.LastRow+g.Step)-g.FirstRow;
            var result=g.Vertical?new LinearBarcodeResult(g.Type,g.Text,g.FirstRow,start,across,end-start,g.Reverse?270:90)
                :new(g.Type,g.Text,start,g.FirstRow,end-start,across,g.Reverse?180:0);
            // A reversal-symmetric message can agree in both directions. Keep one spatial result.
            if(results.Any(r=>r.Type==result.Type&&r.Text==result.Text&&Overlap(r,result)))continue;
            results.Add(result);
        }
        token.ThrowIfCancellationRequested();
        return results.OrderBy(r=>r.Y).ThenBy(r=>r.X).ThenBy(r=>r.Type,StringComparer.Ordinal).Take(MaxResults).ToArray();
    }

    private static bool Overlap(LinearBarcodeResult a,LinearBarcodeResult b) =>
        Math.Min(a.X+a.Width,b.X+b.Width)-Math.Max(a.X,b.X)>Math.Min(a.Width,b.Width)*0.8
        &&Math.Min(a.Y+a.Height,b.Y+b.Height)-Math.Max(a.Y,b.Y)>Math.Min(a.Height,b.Height)*0.8;

    private static void Scan(PixelImage image,bool vertical,List<Group> groups,CancellationToken token)
    {
        int length=vertical?image.Height:image.Width,rows=vertical?image.Width:image.Height;
        if(length<60||rows<5)return;
        int step=Math.Max(1,(rows+MaxLines-1)/MaxLines);
        var gray=new byte[length];var histogram=new int[256];var runs=new List<int>(length+2);var positions=new List<int>(length+2);
        for(int row=0;row<rows;row+=step)
        {
            token.ThrowIfCancellationRequested();Array.Clear(histogram);
            int low=255,high=0;
            for(int p=0;p<length;p++)
            {
                int x=vertical?row:p,y=vertical?p:row,i=(y*image.Width+x)*4,a=image.Bgra[i+3];
                int luma=(117*image.Bgra[i]+601*image.Bgra[i+1]+306*image.Bgra[i+2])>>10;
                byte value=(byte)((luma*a+255*(255-a)+127)/255);gray[p]=value;histogram[value]++;low=Math.Min(low,value);high=Math.Max(high,value);
            }
            if(high-low<80)continue;
            int threshold=Threshold(histogram,length);
            runs.Clear();positions.Clear();bool dark=false;int start=0;
            for(int p=0;p<length;p++)
            {
                bool next=gray[p]<threshold;if(next==dark)continue;
                positions.Add(start);runs.Add(p-start);dark=next;start=p;
            }
            positions.Add(start);runs.Add(length-start);
            if(dark){positions.Add(length);runs.Add(0);}
            for(int direction=0;direction<2;direction++)
            {
                token.ThrowIfCancellationRequested();
                for(int p=1;p+6<runs.Count;p+=2)
                {
                    // Cheap pixel floor; each decoder then checks its module-based quiet zones.
                    if(runs[p-1]<7)continue;
                    token.ThrowIfCancellationRequested();
                    Decoded? found=Ean(runs,p,true)??Ean(runs,p,false)??Code128(runs,p,token)??Code39(runs,p,token)??Itf(runs,p,token);
                    if(found is null)continue;
                    int end=positions[found.End]+runs[found.End];
                    Add(groups,found,positions[p],end,row,step,vertical,direction==1);
                }
                runs.Reverse();positions.Clear();int offset=0;
                foreach(int run in runs){positions.Add(offset);offset+=run;}
            }
        }
    }

    private static void Add(List<Group> groups,Decoded found,int start,int end,int row,int step,bool vertical,bool reverse)
    {
        double tolerance=Math.Max(2,found.Module*1.5);
        var existing=groups.LastOrDefault(g=>g.Vertical==vertical&&g.Reverse==reverse&&g.Type==found.Type&&g.Text==found.Text
            &&row-g.LastRow<=step*2&&row>g.LastRow&&Math.Abs(start-g.Start)<=tolerance&&Math.Abs(end-g.End)<=tolerance);
        if(existing is not null){existing.LastRow=row;existing.Count++;return;}
        if(groups.Count>=MaxGroups)
        {
            // A failed one-line candidate must not occupy a slot for the rest of the image.
            int stale=groups.FindIndex(g=>(g.Vertical!=vertical||g.LastRow<row-step*2)
                &&(g.Count<(g.Type is "Code 39" or "ITF"?7:3)||g.LastRow-g.FirstRow<g.Module*(g.Type is "Code 39" or "ITF"?10:4)));
            if(stale<0)return;groups.RemoveAt(stale);
        }
        groups.Add(new(){Type=found.Type,Text=found.Text,Start=start,End=end,FirstRow=row,LastRow=row,Count=1,Step=step,Module=found.Module,Vertical=vertical,Reverse=reverse});
    }

    private static int Threshold(int[] histogram,int total)
    {
        long sum=0;for(int i=0;i<256;i++)sum+=(long)i*histogram[i];
        long below=0,count=0;double best=-1;int threshold=128;
        for(int i=0;i<255;i++)
        {
            count+=histogram[i];below+=(long)i*histogram[i];long rest=total-count;if(count==0||rest==0)continue;
            double delta=(double)below/count-(double)(sum-below)/rest,score=count*rest*delta*delta;
            if(score>best){best=score;threshold=i+1;}
        }
        return threshold;
    }

    private static double Error(List<int> runs,int at,string pattern,double module,bool reverse=false)
    {
        if(at<0||at+pattern.Length>runs.Count||module<0.8)return double.PositiveInfinity;
        double sum=0;
        for(int i=0;i<pattern.Length;i++)
        {
            double expected=(pattern[reverse?pattern.Length-1-i:i]-'0')*module,error=Math.Abs(runs[at+i]-expected);
            if(error>Math.Max(0.8,module*0.45))return double.PositiveInfinity;sum+=error;
        }
        return sum/(pattern.Length*module);
    }
    private static bool Fits(List<int> runs,int at,string pattern,double module)=>Error(runs,at,pattern,module)<=0.23;
    private static bool Quiet(List<int> runs,int at,double module,int modules)=>at>=0&&at<runs.Count&&runs[at]+0.5>=module*modules;
    private static int Sum(List<int> runs,int start,int count){int sum=0;for(int i=0;i<count;i++)sum+=runs[start+i];return sum;}
    private static int Digit(List<int> runs,int at,double module,bool allowG,out bool g)
    {
        int best=-1;double score=double.PositiveInfinity;bool ambiguous=false;g=false;
        for(int digit=0;digit<10;digit++)for(int reverse=0;reverse<(allowG?2:1);reverse++)
        {
            double error=Error(runs,at,EanWidths[digit],module,reverse==1);if(error>0.23)continue;
            if(error+0.05<score){score=error;best=digit;g=reverse==1;ambiguous=false;}else if(Math.Abs(error-score)<=0.05)ambiguous=true;
        }
        return ambiguous?-1:best;
    }
    private static Decoded? Ean(List<int> runs,int start,bool thirteen)
    {
        int count=thirteen?59:43;if(start+count>=runs.Count)return null;
        double module=Sum(runs,start,count)/(thirteen?95.0:67.0);
        if(!Fits(runs,start,"111",module)||!Quiet(runs,start-1,module,thirteen?9:7))return null;
        int digits=thirteen?6:4,at=start+3;var left=new StringBuilder();var right=new StringBuilder();var parity=new StringBuilder();
        for(int i=0;i<digits;i++,at+=4)
        {
            int digit=Digit(runs,at,module,thirteen,out bool g);if(digit<0)return null;left.Append((char)('0'+digit));parity.Append(g?'G':'L');
        }
        if(!Fits(runs,at,"11111",module))return null;at+=5;
        for(int i=0;i<digits;i++,at+=4){int digit=Digit(runs,at,module,false,out _);if(digit<0)return null;right.Append((char)('0'+digit));}
        if(!Fits(runs,at,"111",module))return null;
        string value;string type;
        if(thirteen)
        {
            int leading=Array.IndexOf(EanParity,parity.ToString());if(leading<0)return null;
            value=(char)('0'+leading)+left.ToString()+right;type="EAN-13";
            if(leading==0){value=value[1..];type="UPC-A";}
            if(!Quiet(runs,start-1,module,leading==0?9:11)||!Quiet(runs,at+3,module,leading==0?9:7))return null;
        }
        else{value=left.ToString()+right;type="EAN-8";if(!Quiet(runs,at+3,module,7))return null;}
        return Mod10(value)?new(type,value,at+2,module):null;
    }

    private static bool Mod10(string value)
    {
        int sum=0,weight=1;for(int i=value.Length-1;i>=0;i--){sum+=(value[i]-'0')*weight;weight=4-weight;}return sum%10==0;
    }

    private static int Code128Value(List<int> runs,int at,double module,int first,int last)
    {
        int best=-1;double score=double.PositiveInfinity;bool ambiguous=false;
        for(int value=first;value<=last;value++)
        {
            double error=Error(runs,at,Code128Widths[value],module);if(error>0.23)continue;
            if(error+0.05<score){score=error;best=value;ambiguous=false;}else if(Math.Abs(error-score)<=0.05)ambiguous=true;
        }
        return ambiguous?-1:best;
    }
    private static Decoded? Code128(List<int> runs,int start,CancellationToken token)
    {
        double module=Sum(runs,start,6)/11.0;
        int first=Code128Value(runs,start,module,103,105);
        if(first<0||!Quiet(runs,start-1,module,10))return null;
        var values=new List<int>();int at=start+6;
        for(int symbol=0;symbol<=MaxText+1&&at+6<runs.Count;symbol++,at+=6)
        {
            token.ThrowIfCancellationRequested();
            if(Fits(runs,at,Code128Widths[106],module)&&Quiet(runs,at+7,module,10))
            {
                if(values.Count<2)return null;
                int sum=first;for(int i=0;i<values.Count-1;i++)sum+=(i+1)*values[i];
                if(sum%103!=values[^1])return null;
                string? text=Code128Text(first,values);
                return text is null?null:new("Code 128",text,at+6,module);
            }
            int value=Code128Value(runs,at,module,0,102);if(value<0)return null;values.Add(value);
        }
        return null;
    }
    private static string? Code128Text(int first,List<int> values)
    {
        var text=new StringBuilder();int set=first-103;
        for(int i=0;i<values.Count-1;i++)
        {
            int value=values[i];
            if(set==2)
            {
                if(value<100){text.Append((char)('0'+value/10));text.Append((char)('0'+value%10));}
                else if(value is 100 or 101)set=101-value;
                else if(value==102){if(i>0)text.Append('\u001d');}
                else return null;
            }
            else if(value<96)text.Append((char)(set==0&&value>=64?value-64:value+32));
            else if(value==98)
            {
                if(++i>=values.Count-1||values[i]>=96)return null;
                int shifted=values[i];text.Append((char)(set==1&&shifted>=64?shifted-64:shifted+32));
            }
            else if(value==99)set=2;
            else if(value==100&&set==0)set=1;
            else if(value==101&&set==1)set=0;
            else if(value==102){if(i>0)text.Append('\u001d');}
            else return null; // FNC2/3/4 have no lossless plain-text meaning in this reader.
            if(text.Length>MaxText)return null;
        }
        return text.Length>0?text.ToString():null;
    }

    private static int WidePattern(List<int> runs,int at,int count,int stride,double narrow,double wide)
    {
        if(at+(count-1)*stride>=runs.Count)return -1;
        int pattern=0;
        for(int i=0;i<count;i++)
        {
            int run=runs[at+i*stride];double n=Math.Abs(run-narrow),w=Math.Abs(run-wide),tolerance=Math.Max(0.8,narrow*0.4);
            if(Math.Min(n,w)>tolerance||Math.Abs(n-w)<narrow*0.25)return -1;
            pattern=pattern*2+(w<n?1:0);
        }
        return pattern;
    }
    private static Decoded? Code39(List<int> runs,int start,CancellationToken token)
    {
        if(start+9>=runs.Count)return null;
        // The start asterisk identifies its six narrow and three wide elements directly.
        double narrow=(runs[start]+runs[start+2]+runs[start+3]+runs[start+5]+runs[start+7]+runs[start+8])/6.0;
        double wide=(runs[start+1]+runs[start+4]+runs[start+6])/3.0;
        if(narrow<0.8||wide<narrow*1.8||wide>narrow*3.2||WidePattern(runs,start,9,1,narrow,wide)!=0x094||!Quiet(runs,start-1,narrow,10))return null;
        var values=new List<int>();int at=start+10;
        if(!Fits(runs,start+9,"1",narrow))return null;
        for(int symbol=0;symbol<=MaxText+1&&at+9<runs.Count;symbol++,at+=10)
        {
            token.ThrowIfCancellationRequested();int pattern=WidePattern(runs,at,9,1,narrow,wide);
            if(pattern==0x094)
            {
                if(values.Count<7||!Quiet(runs,at+9,narrow,10))return null;
                int sum=0;for(int i=0;i<values.Count-1;i++)sum+=values[i];if(sum%43!=values[^1])return null;
                string text=new(values.Take(values.Count-1).Select(v=>Code39Alphabet[v]).ToArray());
                return new("Code 39",text,at+8,narrow);
            }
            int value=Array.IndexOf(Code39Patterns,pattern);if(value<0||!Fits(runs,at+9,"1",narrow))return null;values.Add(value);
        }
        return null;
    }
    private static Decoded? Itf(List<int> runs,int start,CancellationToken token)
    {
        if(start+14>=runs.Count)return null;
        double narrow=Sum(runs,start,4)/4.0;
        if(!Fits(runs,start,"1111",narrow)||!Quiet(runs,start-1,narrow,10))return null;
        int at=start+4;int[] sorted=runs.GetRange(at,10).Order().ToArray();double wide=sorted.Skip(6).Average();
        if(wide<narrow*1.8||wide>narrow*3.2)return null;
        var text=new StringBuilder();
        for(int pair=0;pair<=MaxText/2&&at+3<runs.Count;pair++,at+=10)
        {
            token.ThrowIfCancellationRequested();
            if(text.Length>=8&&WidePattern(runs,at,3,1,narrow,wide)==4&&Quiet(runs,at+3,narrow,10))
            {
                string value=text.ToString();return Mod10(value)?new("ITF",value,at+2,narrow):null;
            }
            if(text.Length>=MaxText||at+10>=runs.Count)return null;
            int bars=Array.IndexOf(ItfPatterns,WidePattern(runs,at,5,2,narrow,wide));
            int spaces=Array.IndexOf(ItfPatterns,WidePattern(runs,at+1,5,2,narrow,wide));
            if(bars<0||spaces<0)return null;text.Append((char)('0'+bars));text.Append((char)('0'+spaces));
        }
        return null;
    }
}
