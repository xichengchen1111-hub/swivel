using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

// Original test-only encoders. They do not call or inspect the decoder's tables/helpers.
// Width and parity tables below are symbology facts; rendering/checksum generation is separate.
internal static class LinearBarcodeReadChecks
{
    private static readonly string[] LeftDigits={"0001101","0011001","0010011","0111101","0100011","0110001","0101111","0111011","0110111","0001011"};
    private static readonly string[] EanParity={"LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG","LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL"};
    private const string Alphabet39="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
    private static readonly int[] Wide39={
        0x034,0x121,0x061,0x160,0x031,0x130,0x070,0x025,0x124,0x064,
        0x109,0x049,0x148,0x019,0x118,0x058,0x00d,0x10c,0x04c,0x01c,
        0x103,0x043,0x142,0x013,0x112,0x052,0x007,0x106,0x046,0x016,
        0x181,0x0c1,0x1c0,0x091,0x190,0x0d0,0x085,0x184,0x0c4,0x0a8,0x0a2,0x08a,0x02a
    };
    private static readonly string[] TwoOfFive={"nnwwn","wnnnw","nwnnw","wwnnn","nnwnw","wnwnn","nwwnn","nnnww","wnnwn","nwnwn"};
    private static readonly string[] Width128={
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212",
        "112232","122132","122231","113222","123122","123221","223211","221132","221231","213212","223112","312131",
        "311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321",
        "112313","132113","132311","211313","231113","231311","112133","112331","132131","113123","113321","133121",
        "313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114",
        "122411","142112","142211","241211","221114","413111","241112","134111","111242","121142","121241","114212",
        "124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113",
        "114311","411113","411311","113141","114131","311141","411131","211412","211214","211232","2331112"
    };

    internal static int CheckDigit(string data)
    {
        int sum=0;for(int i=data.Length-1,multiplier=3;i>=0;i--,multiplier=4-multiplier)sum+=(data[i]-'0')*multiplier;
        return (10-sum%10)%10;
    }

    private static string Opposite(string bits)=>new(bits.Select(c=>c=='0'?'1':'0').ToArray());
    private static string Widths(string widths)
    {
        var b=new StringBuilder();for(int i=0;i<widths.Length;i++)b.Append(i%2==0?'1':'0',widths[i]-'0');return b.ToString();
    }
    private static string Code39(string text,bool badCheck)
    {
        int sum=text.Sum(c=>Alphabet39.IndexOf(c));char checksum=Alphabet39[(sum+(badCheck?1:0))%43];
        var b=new StringBuilder();
        foreach(char c in "*"+text+checksum+"*")
        {
            if(b.Length!=0)b.Append('0');
            int pattern=c=='*'?0x094:Wide39[Alphabet39.IndexOf(c)];
            for(int i=0;i<9;i++)b.Append(i%2==0?'1':'0',(pattern&(1<<(8-i)))==0?1:3);
        }
        return b.ToString();
    }
    internal static string Code128(int start,IReadOnlyList<int> values,bool badCheck=false)
    {
        var b=new StringBuilder(Widths(Width128[start]));int checksum=start;
        for(int i=0;i<values.Count;i++){b.Append(Widths(Width128[values[i]]));checksum+=values[i]*(i+1);}
        b.Append(Widths(Width128[(checksum+(badCheck?1:0))%103]));b.Append(Widths(Width128[106]));return b.ToString();
    }

    internal static string Encode(string type,string text,bool badCheck=false)
    {
        if(type=="Code 39")return Code39(text,badCheck);
        if(type=="Code 128")return Code128(104,text.Select(c=>c-' ').ToArray(),badCheck);
        if(badCheck)text=text[..^1]+(char)('0'+(text[^1]-'0'+1)%10);
        if(type=="ITF")
        {
            var b=new StringBuilder("1010");
            for(int i=0;i<text.Length;i+=2)
            {
                var bars=TwoOfFive[text[i]-'0'];var spaces=TwoOfFive[text[i+1]-'0'];
                for(int j=0;j<5;j++){b.Append('1',bars[j]=='w'?3:1);b.Append('0',spaces[j]=='w'?3:1);}
            }
            b.Append("11101");return b.ToString();
        }
        var result=new StringBuilder("101");
        if(type=="EAN-8")
        {
            for(int i=0;i<4;i++)result.Append(LeftDigits[text[i]-'0']);result.Append("01010");
            for(int i=4;i<8;i++)result.Append(Opposite(LeftDigits[text[i]-'0']));
        }
        else
        {
            if(type=="UPC-A")text="0"+text;
            string parity=EanParity[text[0]-'0'];
            for(int i=1;i<=6;i++)
            {
                string digit=LeftDigits[text[i]-'0'];result.Append(parity[i-1]=='L'?digit:Opposite(new(digit.Reverse().ToArray())));
            }
            result.Append("01010");for(int i=7;i<=12;i++)result.Append(Opposite(LeftDigits[text[i]-'0']));
        }
        result.Append("101");return result.ToString();
    }

    internal static PixelImage Render(string type,string text,int scale=3,int barHeight=72,bool badCheck=false,int quiet=12)
        =>Raster(Encode(type,text,badCheck),scale,barHeight,quiet);
    internal static PixelImage Raster(string bits,int scale=3,int barHeight=72,int quiet=12)
    {
        int width=(bits.Length+quiet*2)*scale,height=barHeight+24;var pixels=Enumerable.Repeat((byte)255,width*height*4).ToArray();
        for(int i=0;i<bits.Length;i++)if(bits[i]=='1')
            for(int y=12;y<height-12;y++)for(int x=(quiet+i)*scale;x<(quiet+i+1)*scale;x++)
            {int p=(y*width+x)*4;pixels[p]=pixels[p+1]=pixels[p+2]=0;}
        return new(width,height,pixels);
    }
    internal static PixelImage Compose(int width,int height,params (PixelImage Image,int X,int Y)[] items)
    {
        var pixels=Enumerable.Repeat((byte)255,checked(width*height*4)).ToArray();
        foreach(var (image,x,y) in items)
            for(int row=0;row<image.Height;row++)Buffer.BlockCopy(image.Bgra,row*image.Width*4,pixels,((y+row)*width+x)*4,image.Width*4);
        return new(width,height,pixels);
    }
    internal static PixelImage Rotate(PixelImage source,int turns)
    {
        while(turns-->0)
        {
            int width=source.Height,height=source.Width;var pixels=new byte[source.Bgra.Length];
            for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)
                Buffer.BlockCopy(source.Bgra,(y*source.Width+x)*4,pixels,(x*width+width-1-y)*4,4);
            source=new(width,height,pixels);
        }
        return source;
    }
    internal static PixelImage Degrade(PixelImage source,bool blur,int noise)
    {
        var pixels=(byte[])source.Bgra.Clone();uint seed=0x5e001;
        for(int y=1;y<source.Height-1;y++)for(int x=1;x<source.Width-1;x++)
        {
            int value=source.Bgra[(y*source.Width+x)*4];
            if(blur){int sum=0;for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)sum+=source.Bgra[((y+dy)*source.Width+x+dx)*4];value=sum/9;}
            seed=seed*1664525+1013904223;int delta=noise==0?0:(int)(seed>>16)%(noise*2+1)-noise;
            int p=(y*source.Width+x)*4;pixels[p]=pixels[p+1]=pixels[p+2]=(byte)Math.Clamp(value+delta,0,255);
        }
        return new(source.Width,source.Height,pixels);
    }
    private static PixelImage Crop(PixelImage source,int left,int right)
    {
        int width=source.Width-left-right;var pixels=new byte[width*source.Height*4];
        for(int y=0;y<source.Height;y++)Buffer.BlockCopy(source.Bgra,(y*source.Width+left)*4,pixels,y*width*4,width*4);
        return new(width,source.Height,pixels);
    }

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        int count=0;var verify=check;var refuse=reject;
        check=(value,name)=>{verify(value,name);count++;};reject=(action,name)=>{refuse(action,name);count++;};
        var cases=new[]{
            (Type:"EAN-13",Text:"5901234123457"),(Type:"UPC-A",Text:"036000291452"),(Type:"EAN-8",Text:"96385074"),
            (Type:"Code 128",Text:"Swivel-128 / 42"),(Type:"Code 39",Text:"SWIVEL-39"),(Type:"ITF",Text:"12345670")
        };
        check(CheckDigit("590123412345")==7&&CheckDigit("03600029145")==2&&CheckDigit("9638507")==4&&CheckDigit("1234567")==0,"Independent GS1 checksum fixtures are known valid");
        foreach(var (type,text) in cases)
        {
            var clean=Render(type,text);var original=(byte[])clean.Bgra.Clone();
            for(int turn=0;turn<4;turn++)
            {
                var read=LinearBarcodeRead.Read(Rotate(clean,turn),default);
                check(read.Count==1&&read[0].Type==type&&read[0].Text==text,$"Independent {type} fixture reads once at {turn*90} degrees");
                if(read.Count>0)check(read[0].ClockwiseOrientationDegrees==turn*90,$"{type} reports exact orthogonal orientation {turn*90}");
            }
            check(clean.Bgra.SequenceEqual(original),$"{type} reading preserves source pixels");
            var noisy=LinearBarcodeRead.Read(Degrade(clean,false,28),default);
            check(noisy.Count==1&&noisy[0].Type==type&&noisy[0].Text==text,$"{type} deterministic additive noise reads");
            var blurred=LinearBarcodeRead.Read(Degrade(Render(type,text,4),true,12),default);
            check(blurred.Count==1&&blurred[0].Type==type&&blurred[0].Text==text,$"{type} mild blur and noise reads");
            check(LinearBarcodeRead.Read(Render(type,text,badCheck:true),default).Count==0,$"{type} invalid check digit is refused");
            check(LinearBarcodeRead.Read(Render(type,text,quiet:0),default).Count==0,$"{type} absent quiet zones are refused");
            check(LinearBarcodeRead.Read(Crop(clean,0,12*3+15),default).Count==0,$"{type} truncated stop/data are refused");
            check(LinearBarcodeRead.Read(Crop(clean,12*3+15,0),default).Count==0,$"{type} truncated start/data are refused");
            check(LinearBarcodeRead.Read(Render(type,text,barHeight:1),default).Count==0,$"{type} one-line texture cannot satisfy agreement");
        }

        // Every digit and leading parity is rendered independently; EAN-13 zero is canonical UPC-A.
        for(int leading=0;leading<10;leading++)
        {
            string data=leading+"12345678901",ean=data+CheckDigit(data);string expected=leading==0?ean[1..]:ean;
            var read=LinearBarcodeRead.Read(Render("EAN-13",ean),default);
            check(read.Count==1&&read[0].Text==expected&&read[0].Type==(leading==0?"UPC-A":"EAN-13"),"EAN-13 leading parity "+leading);
        }
        for(int digit=0;digit<10;digit++)
        {
            string data=new((char)('0'+digit),7),text=data+CheckDigit(data);
            var read=LinearBarcodeRead.Read(Render("ITF",text),default);
            check(read.Count==1&&read[0].Type=="ITF"&&read[0].Text==text,"ITF digit widths "+digit);
            data=new string((char)('0'+digit),7);text=data+CheckDigit(data);
            read=LinearBarcodeRead.Read(Render("EAN-8",text),default);
            check(read.Count==1&&read[0].Type=="EAN-8"&&read[0].Text==text,"EAN-8 digit patterns "+digit);
        }
        foreach(string text in new[]{"ABCDEF","0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%","AAAAAA","999999"})
        {
            var read=LinearBarcodeRead.Read(Render("Code 39",text,2),default);
            check(read.Count==1&&read[0].Text==text&&read[0].Type=="Code 39","Code39 alphabet/checksum: "+text);
        }
        check(LinearBarcodeRead.Read(Render("Code 39","SHORT"),default).Count==0,"Code39 below minimum six-character payload is refused despite valid checksum");
        check(LinearBarcodeRead.Read(Render("ITF","123457"),default).Count==0,"ITF below minimum eight digits is refused despite valid checksum");
        var control128=Raster(Code128(103,new[]{33,34,35,64,65,66}));
        var controls=LinearBarcodeRead.Read(control128,default);
        check(controls.Count==1&&controls[0].Text=="ABC\0\u0001\u0002","Code128 set A preserves controls as data");
        var digits128=LinearBarcodeRead.Read(Raster(Code128(105,new[]{12,34,56,78,90,0})),default);
        check(digits128.Count==1&&digits128[0].Text=="123456789000","Code128 set C preserves paired digits and leading zero pair");
        var switch128=LinearBarcodeRead.Read(Raster(Code128(104,new[]{33,34,99,12,34,100,67,68})),default);
        check(switch128.Count==1&&switch128[0].Text=="AB1234cd","Code128 B-C-B switching decodes");
        var shift128=LinearBarcodeRead.Read(Raster(Code128(104,new[]{33,34,98,64,35})),default);
        check(shift128.Count==1&&shift128[0].Text=="AB\0C","Code128 SHIFT applies to exactly one symbol");
        var allB=LinearBarcodeRead.Read(Render("Code 128",new string(Enumerable.Range(32,96).Select(i=>(char)i).ToArray()),2),default);
        check(allB.Count==1&&allB[0].Text.SequenceEqual(Enumerable.Range(32,96).Select(i=>(char)i)),"Code128 every set-B character is independently encoded");
        var allA=LinearBarcodeRead.Read(Raster(Code128(103,Enumerable.Range(0,96).ToArray()),2),default);
        check(allA.Count==1&&allA[0].Text.SequenceEqual(Enumerable.Range(0,96).Select(i=>(char)(i<64?i+32:i-64))),"Code128 every set-A character is independently encoded");
        for(int begin=0;begin<100;begin+=40)
        {
            int[] values=Enumerable.Range(begin,Math.Min(40,100-begin)).ToArray();
            var read=LinearBarcodeRead.Read(Raster(Code128(105,values),2),default);
            check(read.Count==1&&read[0].Text==string.Concat(values.Select(i=>i.ToString("D2",System.Globalization.CultureInfo.InvariantCulture))),"Code128 every set-C pair is independently encoded");
        }
        foreach(int fnc in new[]{96,97,100})
            check(LinearBarcodeRead.Read(Raster(Code128(104,new[]{33,34,fnc,35,36})),default).Count==0,"Code128 unsupported FNC2/3/4 refuses the whole payload");
        foreach(string type in new[]{"Code 128","Code 39","ITF"})
        {
            string atLimit=type=="ITF"?new string('2',127)+CheckDigit(new string('2',127)):new string('A',128);
            var read=LinearBarcodeRead.Read(Render(type,atLimit,2),default);
            check(read.Count==1&&read[0].Text==atLimit,$"{type} exact 128-character payload limit reads");
            string overLimit=type=="ITF"?new string('2',129)+CheckDigit(new string('2',129)):new string('A',129);
            check(LinearBarcodeRead.Read(Render(type,overLimit,2),default).Count==0,$"{type} over-limit valid checksum refuses without truncation");
        }

        // Vertical and horizontal separation, duplicate values, and no top-to-bottom starvation.
        var first=Render(cases[0].Type,cases[0].Text);var second=Render(cases[4].Type,cases[4].Text);
        var pair=Compose(first.Width+second.Width+40,first.Height+40,(first,0,10),(second,first.Width+40,10));
        var pairRead=LinearBarcodeRead.Read(pair,default);
        check(pairRead.Count==2&&pairRead.Any(r=>r.Text==cases[0].Text)&&pairRead.Any(r=>r.Text==cases[4].Text),"Two horizontal independent barcodes both decode");
        var repeated=Compose(first.Width,first.Height*2+40,(first,0,0),(first,0,first.Height+40));
        var copies=LinearBarcodeRead.Read(repeated,default);
        check(copies.Count==2&&copies.All(r=>r.Text==cases[0].Text)&&copies[0].Y<copies[1].Y,"Separate spatial copies of an identical barcode remain separate");
        var six=new List<(PixelImage Image,int X,int Y)>();int widest=cases.Max(c=>Render(c.Type,c.Text).Width);
        for(int i=0;i<cases.Length;i++)six.Add((Render(cases[i].Type,cases[i].Text),0,i*120));
        var all=LinearBarcodeRead.Read(Compose(widest,720,six.ToArray()),default);
        check(all.Count==6&&cases.All(c=>all.Any(r=>r.Type==c.Type&&r.Text==c.Text)),"Six independently encoded symbologies in one image all decode");
        var failedNeighbor=Compose(first.Width+second.Width+40,first.Height+40,(Render("EAN-13",cases[0].Text,badCheck:true),0,10),(second,first.Width+40,10));
        var surviving=LinearBarcodeRead.Read(failedNeighbor,default);
        check(surviving.Count==1&&surviving[0].Text==cases[4].Text,"A neighboring invalid candidate cannot hide a valid barcode");
        var decoys=Raster(string.Concat(Enumerable.Repeat("10000000",180)),1);
        var afterDecoys=LinearBarcodeRead.Read(Compose(decoys.Width+first.Width+20,first.Height,(decoys,0,0),(first,decoys.Width+20,0)),default);
        check(afterDecoys.Count==1&&afterDecoys[0].Text==cases[0].Text,"More than 128 failed starts cannot hide a rightmost valid barcode");

        var stripes=Raster(string.Concat(Enumerable.Repeat("1010",100)));
        check(LinearBarcodeRead.Read(stripes,default).Count==0,"Uniform stripes are not barcodes");
        var noisePixels=Enumerable.Repeat((byte)255,640*240*4).ToArray();uint state=124;
        for(int i=0;i<noisePixels.Length;i+=4){state=state*1664525+1013904223;byte v=(byte)(state>>24);noisePixels[i]=noisePixels[i+1]=noisePixels[i+2]=v;}
        check(LinearBarcodeRead.Read(new(640,240,noisePixels),default).Count==0,"Seeded random noise is not a barcode");
        // Deterministic block glyphs, with ordinary repeated upright stems and gaps.
        var glyphs=Enumerable.Repeat((byte)255,640*180*4).ToArray();
        for(int y=20;y<160;y++)for(int x=20;x<620;x++)
        {
            int cx=(x-20)/20,px=(x-20)%20,py=(y-20)%28;
            bool ink=px<3&&py<20||px<13&&(py<3||py is >=9 and <12||py is >=17 and <20)&&cx%3!=0;
            if(ink){int i=(y*640+x)*4;glyphs[i]=glyphs[i+1]=glyphs[i+2]=0;}
        }
        check(LinearBarcodeRead.Read(new(640,180,glyphs),default).Count==0,"Block text-like stems and letter bars are not barcodes");
        check(LinearBarcodeRead.Read(QrFixtureEncoder.Render("BARCODE NEGATIVE TEXT",4),default).Count==0,"QR modules do not become a linear barcode");
        reject(()=>LinearBarcodeRead.Read(first,new CancellationToken(true)),"Precancelled barcode read stops");
        reject(()=>LinearBarcodeRead.Read(new(4001,4001,new byte[4]),default),"Oversize barcode image is refused before buffer work");
        reject(()=>LinearBarcodeRead.Read(new(400,400,new byte[4]),default),"Malformed barcode pixel buffer is refused");
        var capItems=new List<(PixelImage Image,int X,int Y)>();
        for(int i=0;i<20;i++)capItems.Add((first,0,i*100));
        var capped=LinearBarcodeRead.Read(Compose(first.Width,2000,capItems.ToArray()),default);
        check(capped.Count is >0 and <=16,"Barcode result count is bounded on many symbols");
        Console.WriteLine($"Independent linear fixture checks: {count}");
    }
}
