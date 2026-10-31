using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

public enum QrGardenTemplate { Tree, Bouquet, Island }
public enum QrGardenSeason { Summer, Spring, Autumn }
public readonly record struct GardenBox(double X,double Z,double Bottom,double Top,double Width,double Depth,uint Color,uint Side);

// Original voxel compositions, inspired by the Mac garden's three-layer design.
// Only Ground + Cover survive the turn. Each module has a complete unit top;
// every additional persistent voxel stays inside a module of the same colour class.
public sealed class QrGarden
{
    public const string Invalid="Choose a supported QR garden template and season.";
    // One slab + one box/module + 1024 cover details + 4096 fillers + 4096 decorations.
    // Five visible faces/box (no undersides): at v40 <= 40,546 boxes / 202,730 quads.
    public const int MaxBoxes=177*177+9217,MaxFaces=MaxBoxes*5;
    public int Size{get;}
    public IReadOnlyList<GardenBox> Ground{get;}
    public IReadOnlyList<GardenBox> Cover{get;}
    public IReadOnlyList<GardenBox> Structure{get;}
    public int BoxCount=>Ground.Count+Cover.Count+Structure.Count;
    public int FaceCount=>BoxCount*5;
    private QrGarden(int size,List<GardenBox> ground,List<GardenBox> cover,List<GardenBox> structure)
    {Size=size;Ground=ground.AsReadOnly();Cover=cover.AsReadOnly();Structure=structure.AsReadOnly();}
    public static void Validate(QrGardenTemplate template,QrGardenSeason season)
    {if(!Enum.IsDefined(template)||!Enum.IsDefined(season))throw new UserError(Invalid);}
    public static string TemplateName(QrGardenTemplate template)=>template switch{QrGardenTemplate.Tree=>"Tree",QrGardenTemplate.Bouquet=>"Bouquet",QrGardenTemplate.Island=>"Island",_=>throw new UserError(Invalid)};
    public static string SeasonName(QrGardenSeason season)=>season switch{QrGardenSeason.Summer=>"Summer",QrGardenSeason.Spring=>"Spring",QrGardenSeason.Autumn=>"Autumn",_=>throw new UserError(Invalid)};
    public static QrGardenTemplate Template(string value)=>value switch{"tree"=>QrGardenTemplate.Tree,"bouquet"=>QrGardenTemplate.Bouquet,"island"=>QrGardenTemplate.Island,_=>throw new UserError(Invalid)};
    public static QrGardenSeason Season(string value)=>value switch{"summer"=>QrGardenSeason.Summer,"spring"=>QrGardenSeason.Spring,"autumn"=>QrGardenSeason.Autumn,_=>throw new UserError(Invalid)};
    public static string Key(QrGardenTemplate value){Validate(value,QrGardenSeason.Summer);return value.ToString().ToLowerInvariant();}
    public static string Key(QrGardenSeason value){Validate(QrGardenTemplate.Tree,value);return value.ToString().ToLowerInvariant();}
    public static QrGarden Build(QrCode code,QrGardenTemplate template,QrGardenSeason season,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(code);token.ThrowIfCancellationRequested();Validate(template,season);
        return new Builder(code,template,season,token).Build();
    }

    private sealed class Builder
    {
        private readonly QrCode code;private readonly QrGardenTemplate template;private readonly QrGardenSeason season;private readonly CancellationToken token;
        private readonly int n;private readonly double half,scale;private readonly uint seed;
        private readonly List<GardenBox> ground=new(),cover=new(),structure=new();
        private int details,fillers,decorations;
        private readonly record struct Bloom(double X,double Z,uint Color);
        internal Builder(QrCode code,QrGardenTemplate template,QrGardenSeason season,CancellationToken token)
        {
            this.code=code;this.template=template;this.season=season;this.token=token;n=code.Size;half=(n-1)/2.0;scale=n/29.0;
            uint hash=2166136261;for(int r=0;r<n;r++){token.ThrowIfCancellationRequested();for(int c=0;c<n;c++)hash=unchecked((hash^(code[c,r]?1U:0U))*16777619);}
            seed=hash^(uint)template*747796405U;
        }
        private uint Hash(int x,int z,int salt=0)
        {uint h=unchecked(seed^(uint)x*1597334677U^(uint)z*3812015801U^(uint)salt*2798796415U);h^=h>>16;h=unchecked(h*2246822519U);h^=h>>13;return h;}
        private double Unit(int x,int z,int salt=0)=>(Hash(x,z,salt)&65535)/65535.0;
        private static uint Pick(uint[] colors,uint hash)=>colors[hash%(uint)colors.Length];
        private static GardenBox Box(double x,double z,double bottom,double top,double width,uint color,double? depth=null,uint? side=null)
            =>new(x,z,bottom,top,width,depth??width,color,side??color);
        private void Detail(GardenBox box){if(details<1024){cover.Add(box);details++;}}
        private void Filler(GardenBox box){if(fillers<4096){structure.Add(box);fillers++;}}
        private void Decor(GardenBox box){token.ThrowIfCancellationRequested();if(decorations<4096){structure.Add(box);decorations++;}}
        private double Reach(double x,double z,double radius)
        {
            double angle=Math.Atan2(z,x),phase=(seed%1000)*.01;
            return radius*(1+.055*Math.Sin(3*angle+phase)+.035*Math.Cos(7*angle-phase));
        }
        private void Line(double ax,double ay,double az,double bx,double by,double bz,double width,uint color,int steps=20)
        {
            double length=Math.Sqrt((bx-ax)*(bx-ax)+(by-ay)*(by-ay)+(bz-az)*(bz-az));
            steps=Math.Clamp(Math.Max(steps,(int)Math.Ceiling(length/(width*.65))),1,96);
            for(int i=0;i<=steps;i++)
            {double t=i/(double)steps,w=width*(1-.35*t);Decor(Box(ax+(bx-ax)*t,az+(bz-az)*t,ay+(by-ay)*t-w/2,ay+(by-ay)*t+w/2,w,color));}
        }
        internal QrGarden Build()
        {
            uint slab=template==QrGardenTemplate.Island?0xd5edf4U:0xf1ece3U,edge=template==QrGardenTemplate.Island?0x528faeU:0xa58d73U;
            ground.Add(Box(0,0,-.7*scale,0,n+8,slab,side:edge));
            switch(template){case QrGardenTemplate.Tree:Tree();break;case QrGardenTemplate.Bouquet:Bouquet();break;case QrGardenTemplate.Island:Island();break;}
            token.ThrowIfCancellationRequested();return new(n,ground,cover,structure);
        }
        private uint[] Foliage=>season switch{
            QrGardenSeason.Spring=>new uint[]{0xb94e76,0xa84069,0xc65c86,0x923c67,0x477b38},
            QrGardenSeason.Autumn=>new uint[]{0xa85419,0x963c1c,0xb56b22,0x813724,0x886325},
            _=>new uint[]{0x34772d,0x407f30,0x28652c,0x4c8732,0x235b34}};
        private uint[] Flowers=>season switch{
            QrGardenSeason.Spring=>new uint[]{0xbc4b78,0xbd624b,0x9b386c,0xb46b37,0x9b5597},
            QrGardenSeason.Autumn=>new uint[]{0x8b2e45,0xae5b27,0x987124,0x723a64,0x9b4638},
            _=>new uint[]{0x4c5ca5,0x7953a6,0x387ca4,0xb14772,0x9c7321}};
        private void Tree()
        {
            uint[] leaves=Foliage,grass=season==QrGardenSeason.Autumn?new uint[]{0x886b2d,0x7e5d27,0x957339}:new uint[]{0x427b32,0x36702c,0x50843a};
            int spacing=Math.Max(1,(int)Math.Ceiling(n/70.0)),detailRate=Math.Max(7,n*n/1000);
            for(int r=0;r<n;r++)
            {
                token.ThrowIfCancellationRequested();
                for(int c=0;c<n;c++)
                {
                    double x=c-half,z=r-half,d=Math.Sqrt(x*x+z*z),reach=Reach(x,z,n*.4),t=d/reach;
                    uint hash=Hash(c,r);bool dark=code[c,r];
                    if(!dark)ground.Add(Box(x,z,0,.08*scale,1,(hash&1)==0?0xf0eae0U:0xeae5dbU));
                    if(t<1)
                    {
                        double top=n*(.26+.40*Math.Sqrt(1-t*t))+(Unit(c,r,1)-.5)*scale;
                        uint color=Pick(leaves,hash);
                        if(dark)
                        {
                            cover.Add(Box(x,z,top-.85*scale,top,1,color));
                            if(hash%(uint)detailRate==0)Detail(Box(x,z,top-2.1*scale,top-1.2*scale,.90,color));
                        }
                        else if(r%spacing==0&&c%spacing==0&&(hash&3)!=0)Filler(Box(x,z,top-1.4*scale,top-.5*scale,.94,color));
                    }
                    else if(dark)
                    {
                        double top=(.22+Unit(c,r,1)*.4)*scale;uint color=Pick(grass,hash);
                        cover.Add(Box(x,z,0,top,1,color));
                        if(hash%(uint)detailRate==0)Detail(Box(x+.19,z-.14,top,top+.35*scale,.22,color));
                    }
                    else if(season!=QrGardenSeason.Summer&&hash%23==0)Decor(Box(x+.2,z-.15,.09*scale,.15*scale,.28,season==QrGardenSeason.Spring?0xda8fa6U:0xbb7629U));
                }
            }
            // A tapering, curved trunk, radiating branches and exposed roots.
            for(int i=0;i<30;i++)
            {
                double t=i/29.0,y=n*.43*t,w=n*(.046-.021*t),x=Math.Sin(t*2.4)*n*.025*t;
                Decor(Box(x,Math.Cos(t*2)*n*.015*t,y-w/2,y+w/2,w,0x795238));
            }
            for(int i=0;i<8;i++)
            {
                double a=i*Math.PI/4+.2,reach=n*(.20+Unit(i,0)*.08);
                Line(0,n*.27,0,Math.Cos(a)*reach,n*(.38+Unit(i,1)*.12),Math.Sin(a)*reach,n*.022,0x795238);
            }
            for(int i=0;i<5;i++){double a=i*Math.PI*.4;Line(0,n*.04,0,Math.Cos(a)*n*.11,.12*scale,Math.Sin(a)*n*.11,n*.022,0x795238,10);}
        }
        private void Bouquet()
        {
            uint[] flowers=Flowers,leaves={0x40763c,0x356a3b,0x508044};
            int count=Math.Clamp(n/2,10,24),spacing=Math.Max(1,(int)Math.Ceiling(n/70.0)),detailRate=Math.Max(7,n*n/1000);var blooms=new Bloom[count];
            // Phyllotaxis keeps the nearest-bloom search bounded at 24, including v40.
            for(int i=0;i<count;i++)
            {double angle=i*2.3999632297,radius=n*.34*Math.Sqrt((i+.5)/count);blooms[i]=new(Math.Cos(angle)*radius,Math.Sin(angle)*radius,i%6==0?leaves[i%3]:flowers[i%flowers.Length]);}
            for(int r=0;r<n;r++)
            {
                token.ThrowIfCancellationRequested();
                for(int c=0;c<n;c++)
                {
                    double x=c-half,z=r-half,d=Math.Sqrt(x*x+z*z),reach=Reach(x,z,n*.39);uint hash=Hash(c,r);bool dark=code[c,r];
                    if(!dark)ground.Add(Box(x,z,0,.07*scale,1,(hash&1)==0?0xf5ece8U:0xeee5e0U));
                    if(d<reach)
                    {
                        Bloom nearest=blooms[0];double distance=double.MaxValue;
                        foreach(var b in blooms){double q=(b.X-x)*(b.X-x)+(b.Z-z)*(b.Z-z);if(q<distance){distance=q;nearest=b;}}
                        double top=n*(.38+.25*Math.Sqrt(1-d*d/(reach*reach)))+scale*Math.Max(0,1.2-Math.Sqrt(distance)/scale*.55);
                        if(dark)
                        {
                            cover.Add(Box(x,z,top-.8*scale,top,1,nearest.Color));
                            if(distance<scale*scale*.45)Detail(Box(x,z,top,top+.3*scale,.45,Pick(flowers,hash)));
                            else if(hash%(uint)detailRate==0)Detail(Box(x,z,top-1.8*scale,top-.95*scale,.86,Pick(leaves,hash)));
                        }
                        else if(r%spacing==0&&c%spacing==0&&(hash&3)!=0)Filler(Box(x,z,top-1.3*scale,top-.5*scale,.94,nearest.Color));
                    }
                    else if(dark)cover.Add(Box(x,z,0,(.12+Unit(c,r)*.2)*scale,1,(hash&1)==0?Pick(leaves,hash):Pick(flowers,hash)));
                }
            }
            double knot=n*.12;
            foreach(var bloom in blooms)
            {
                Line(bloom.X,n*.38-.6*scale,bloom.Z,0,knot,0,n*.01,0x4d7839,16);
                Line(0,knot,0,bloom.X*.07,.1*scale,bloom.Z*.07,n*.01,0x4d7839,8);
            }
            // Open kraft-paper cone; a tissue rim and a ribbon gathered at the stems.
            for(int ring=0;ring<22;ring++)
            {
                double t=ring/21.0,y=n*(.10+.42*t),radius=n*(.045+.31*Math.Pow(t,.85));int around=24+ring*2;
                for(int j=0;j<around;j++)
                {
                    double a=j*Math.Tau/around,gap=Math.Abs(Math.IEEERemainder(a-Math.PI/4,Math.Tau));
                    if(gap<.25+1.2*t*t)continue;
                    double w=n*.026;Decor(Box(Math.Cos(a)*radius,Math.Sin(a)*radius,y-w/2,y+w/2,w,ring==21?0xe6cbb0U:0xc29d75U));
                    if(ring==21&&j%2==0)Decor(Box(Math.Cos(a)*(radius-.35*scale),Math.Sin(a)*(radius-.35*scale),y,y+.6*scale,.65*scale,0xf5e3e3));
                }
            }
            uint ribbon=season==QrGardenSeason.Summer?0x53649dU:0xa03e59U;
            for(int i=0;i<20;i++){double a=i*Math.Tau/20;Decor(Box(Math.Cos(a)*n*.058,Math.Sin(a)*n*.058,knot-.3*scale,knot+.35*scale,.55*scale,ribbon));}
            foreach(int side in new[]{-1,1})
            {
                Line(n*.04,knot,n*.04,n*(.07+side*.04),knot+n*.04,n*(.07-side*.04),n*.018,ribbon,10);
                Line(n*.04,knot,n*.04,n*(.06+side*.02),knot-n*.07,n*(.06-side*.02),n*.014,ribbon,10);
            }
        }
        private void Island()
        {
            uint[] green=season switch{
                QrGardenSeason.Spring=>new uint[]{0x4c8341,0x407a3c,0x5b8845,0xa05079},
                QrGardenSeason.Autumn=>new uint[]{0x7c7d35,0x8b6b2b,0x646f31,0x995b28},
                _=>new uint[]{0x287441,0x368039,0x24683e,0x447e35}};
            uint[] reef={0x296b79,0x245c70,0x397784};var heights=new double[n,n];
            for(int r=0;r<n;r++)
            {
                token.ThrowIfCancellationRequested();
                for(int c=0;c<n;c++)
                {
                    double x=c-half,z=r-half,d=Math.Sqrt(x*x+z*z),reach=Reach(x,z,n*.4);uint hash=Hash(c,r);bool land=d<reach,dark=code[c,r];
                    double height=land?.3*scale+n*.15*Math.Pow(1-d/reach,1.5):.06*scale;heights[c,r]=height;
                    if(dark)
                    {
                        uint color=land?Pick(green,hash):Pick(reef,hash);double top=height+(land?.3+Unit(c,r)*.45:.12)*scale;
                        cover.Add(Box(x,z,0,top,1,color,side:land?0x8a6946U:color));
                    }
                    else ground.Add(Box(x,z,0,height,1,land?(height<n*.05?0xf0dfb4U:0xece7dbU):((hash&1)==0?0xcfe9f2U:0xdaf0f5U),side:land?0xbba982U:0x99c9dcU));
                }
            }
            // Three bent palms. Only fronds confined to dark cells persist at the top.
            for(int p=0;p<3;p++)
            {
                double x=n*(p==0?-.06:p==1?.18:-.18),z=n*(p==0?.03:p==1?-.13:-.19),h=n*(p==0?.40:p==1?.29:.24),a=p*2.4+.5;
                double y=heights[(int)Math.Round(x+half),(int)Math.Round(z+half)],cx=x+Math.Cos(a)*h*.18,cz=z+Math.Sin(a)*h*.18,cy=y+h;
                Line(x,y,z,(x+cx)/2+.45*scale,y+h*.5,(z+cz)/2,n*.026,0x9c784e,20);
                Line((x+cx)/2+.45*scale,y+h*.5,(z+cz)/2,cx,cy,cz,n*.021,0x9c784e,20);
                for(int k=0;k<3;k++)Decor(Box(cx+Math.Cos(k*2.1)*.45*scale,cz+Math.Sin(k*2.1)*.45*scale,cy-.7*scale,cy-.2*scale,.5*scale,0x70502c));
                double reach=h*.48;int spacing=Math.Max(1,(int)Math.Ceiling(n/70.0));
                for(int r=0;r<n;r++)
                {
                    token.ThrowIfCancellationRequested();
                    for(int c=0;c<n;c++)
                    {
                        double dx=c-half-cx,dz=r-half-cz,d=Math.Sqrt(dx*dx+dz*dz),off=Math.Abs(Math.IEEERemainder(Math.Atan2(dz,dx)-a,Math.Tau/7));
                        if(d>reach||d*off>n*.027)continue;
                        double top=cy+.4*scale-n*.07*Math.Pow(d/reach,2);if(top<=heights[c,r]+scale)continue;
                        uint color=Pick(green,Hash(c,r,p));
                        if(code[c,r])Detail(Box(c-half,r-half,top-.4*scale,top,1,color));
                        else if(c%spacing==0&&r%spacing==0)Filler(Box(c-half,r-half,top-.5*scale,top,.94,color));
                    }
                }
            }
            double boatX=n*.36,boatZ=n*.42;
            for(int i=0;i<5;i++)Decor(Box(boatX+(i-2)*.7*scale,boatZ,.1*scale,.55*scale,.75*scale,0xb57449));
            Line(boatX,.55*scale,boatZ,boatX,3.5*scale,boatZ,.16*scale,0x745136,12);
            for(int i=0;i<5;i++){double w=(1.8-i*.3)*scale;Decor(Box(boatX-w/2,boatZ,(1+i*.48)*scale,(1.48+i*.48)*scale,w,0xf8f1e3,.14*scale));}
        }
    }
}
