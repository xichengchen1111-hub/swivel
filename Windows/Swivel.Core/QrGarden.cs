using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

public enum QrGardenTemplate { Tree, Bouquet, Island }
public enum QrGardenSeason { Summer, Spring, Autumn }
public readonly record struct GardenBox(double X,double Z,double Bottom,double Top,double Width,double Depth,uint Color,uint Side);

// Original module reliefs. Each dark module owns exactly its unit square;
// pale modules and the four-module border expose the white slab below.
// This is scene data only, not an assertion about scans of a lit 3D view.
public sealed class QrGarden
{
    public const string Invalid="Choose a supported QR garden template and season.";
    public int Size{get;}
    public IReadOnlyList<GardenBox> Boxes{get;}
    private QrGarden(int size,List<GardenBox> boxes){Size=size;Boxes=boxes.AsReadOnly();}
    public static void Validate(QrGardenTemplate template,QrGardenSeason season)
    {if(!Enum.IsDefined(template)||!Enum.IsDefined(season))throw new UserError(Invalid);}
    public static string TemplateName(QrGardenTemplate template)=>template switch{QrGardenTemplate.Tree=>"Tree",QrGardenTemplate.Bouquet=>"Bouquet",QrGardenTemplate.Island=>"Island",_=>throw new UserError(Invalid)};
    public static string SeasonName(QrGardenSeason season)=>season switch{QrGardenSeason.Summer=>"Summer",QrGardenSeason.Spring=>"Spring",QrGardenSeason.Autumn=>"Autumn",_=>throw new UserError(Invalid)};
    public static QrGardenTemplate Template(string value)=>value switch{"tree"=>QrGardenTemplate.Tree,"bouquet"=>QrGardenTemplate.Bouquet,"island"=>QrGardenTemplate.Island,_=>throw new UserError(Invalid)};
    public static QrGardenSeason Season(string value)=>value switch{"summer"=>QrGardenSeason.Summer,"spring"=>QrGardenSeason.Spring,"autumn"=>QrGardenSeason.Autumn,_=>throw new UserError(Invalid)};
    public static string Key(QrGardenTemplate value){Validate(value,QrGardenSeason.Summer);return value.ToString().ToLowerInvariant();}
    public static string Key(QrGardenSeason value){Validate(QrGardenTemplate.Tree,value);return value.ToString().ToLowerInvariant();}
    private static uint Shade(uint color)=>((color>>16&255)*3/5<<16)|((color>>8&255)*3/5<<8)|((color&255)*3/5);
    // All boxes fit a sphere smaller than .8*extent about the origin.
    // Orthographic Width is horizontal; preserve the same minimum vertical span.
    public static double ViewWidth(int size,double viewportWidth,double viewportHeight)
    {
        if(size is <21 or >177||(size-17)%4!=0||!double.IsFinite(viewportWidth)||!double.IsFinite(viewportHeight)||viewportWidth<=0||viewportHeight<=0)throw new ArgumentOutOfRangeException();
        double width=(size+8)*1.65*Math.Max(1,viewportWidth/viewportHeight);if(!double.IsFinite(width))throw new ArgumentOutOfRangeException();return width;
    }
    public static QrGarden Build(QrCode code,QrGardenTemplate template,QrGardenSeason season,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(code);token.ThrowIfCancellationRequested();Validate(template,season);
        int n=code.Size;double half=(n-1)/2.0,extent=n+QrCode.QuietZone*2;
        var boxes=new List<GardenBox>(n*n+6){new(0,0,-n*.025,0,extent,extent,0xffffff,template==QrGardenTemplate.Island?0x56859cU:0xc6bca7U)};
        uint[] palette=(template,season) switch{
            (QrGardenTemplate.Bouquet,QrGardenSeason.Spring)=>new uint[]{0x7e234f,0x68255d,0x823047},
            (QrGardenTemplate.Bouquet,QrGardenSeason.Autumn)=>new uint[]{0x81350f,0x6a2518,0x753a13},
            (QrGardenTemplate.Bouquet,_)=>new uint[]{0x74234d,0x5a285e,0x873a24},
            (_,QrGardenSeason.Spring)=>new uint[]{0x24552e,0x325d28,0x19503b},
            (_,QrGardenSeason.Autumn)=>new uint[]{0x783711,0x6d281c,0x674213},
            _=>new uint[]{0x174c32,0x225b35,0x184a40}};
        for(int row=0;row<n;row++)
        {
            token.ThrowIfCancellationRequested();
            for(int column=0;column<n;column++)if(code[column,row])
            {
                double x=column-half,z=row-half,r=Math.Sqrt(x*x+z*z)/n;
                uint hash=unchecked((uint)(row*73856093)^((uint)column*19349663U)^((uint)n*83492791U));
                double relief=template switch{
                    QrGardenTemplate.Tree=>.045+.16*Math.Sqrt(Math.Max(0,1-2*r*r)),
                    QrGardenTemplate.Bouquet=>.04+.065*(1+Math.Sin(Math.Atan2(z,x)*5+r*15)),
                    _=>.025+.17*Math.Max(0,1-1.5*r)};
                uint color=palette[hash%3];boxes.Add(new(x,z,0,n*(relief+(hash%11)*.0007),1,1,color,Shade(color)));
            }
        }
        // Decoration stays below the opaque slab, never above a light module.
        if(template==QrGardenTemplate.Tree)boxes.Add(new(0,0,-n*.28,-n*.025,n*.12,n*.12,0x69432b,0x543522));
        else if(template==QrGardenTemplate.Bouquet)
        {
            boxes.Add(new(0,0,-n*.25,-n*.08,n*.27,n*.27,0x70416d,0x54324f));
            for(int i=-1;i<=1;i++)boxes.Add(new(i*n*.07,0,-n*.13,-n*.025,n*.035,n*.035,0x285035,0x1f3f2a));
        }
        token.ThrowIfCancellationRequested();return new(n,boxes);
    }
}
