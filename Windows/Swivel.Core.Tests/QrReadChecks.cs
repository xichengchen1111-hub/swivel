using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Swivel.Core;

internal static class QrReadChecks
{
    internal static PixelImage Image(QrCode code,int scale=1)
    {
        byte[] mono=code.Raster(scale),bgra=new byte[mono.Length*4];
        for(int i=0;i<mono.Length;i++){bgra[i*4]=bgra[i*4+1]=bgra[i*4+2]=mono[i];bgra[i*4+3]=255;}
        int side=(code.Size+8)*scale;return new(side,side,bgra);
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach(var level in Enum.GetValues<QrCorrection>())
        {
            string field=level switch{QrCorrection.Low=>"DataWords",QrCorrection.Medium=>"MediumDataWords",QrCorrection.Quartile=>"QuartileDataWords",_=>"HighDataWords"};
            var capacities=(int[])typeof(QrCode).GetField(field,BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;int previous=0;
            for(int version=1;version<=40;version++)
            {
                int max=capacities[version-1]-(version<10?3:4);
                foreach(int length in new[]{previous+1,max})
                {
                    string text=string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*17+version)%94)));
                    var code=QrCode.Encode(text,level);var read=QrFlatRead.Read(Image(code),default);
                    check(read.Text==text&&read.Version==version&&read.Correction==level&&read.Mask==code.Mask&&read.PixelScale==1,"Read every level/version at both capacity edges without changing text");
                }
                previous=max;
            }
            foreach(string text in new[]{"Swivel","中文 繁體 日本語 😀", "first\r\nsecond\tline",new string('x',QrCode.Limit(level))})
            {
                var code=QrCode.Encode(text,level);var image=Image(code,3);var before=(byte[])image.Bgra.Clone();
                check(QrFlatRead.Read(image,default).Text==text&&image.Bgra.SequenceEqual(before),"Integer scale/Unicode/control whitespace decode without mutating input");
            }
        }
        var fixture=Image(QrCode.Encode("https://example.com/?x=1&y=2"),2);int side=fixture.Width;
        void Damage(int x,int y,int component,byte value)
        {
            var copy=(byte[])fixture.Bgra.Clone();copy[(y*side+x)*4+component]=value;
            reject(()=>QrFlatRead.Read(new(side,side,copy),default),"Invalid pixel/quiet border/function/data/alpha is refused without partial text");
        }
        Damage(0,0,0,0);Damage(9,9,3,0);Damage(9,9,1,100);Damage(side-10,side-10,0,100);
        var near=(byte[])fixture.Bgra.Clone();for(int i=0;i<near.Length;i+=4)for(int c=0;c<3;c++)near[i+c]=near[i+c]==0?(byte)63:(byte)192;
        check(QrFlatRead.Read(new(side,side,near),default).Text=="https://example.com/?x=1&y=2","Opaque near black/white threshold boundaries retain modules");
        var version7=Image(QrCode.Encode(new string('x',150)));int s7=version7.Width;
        foreach((int x,int y) in new[]{(4,4),(12,4),(s7-15,4),(s7-5,s7-5)})
        {
            var changed=(byte[])version7.Bgra.Clone();int at=(y*s7+x)*4;for(int c=0;c<3;c++)changed[at+c]=(byte)(255-changed[at+c]);
            reject(()=>QrFlatRead.Read(new(s7,s7,changed),default),"Function/version/format or payload/parity corruption is not silently corrected");
        }
        reject(()=>QrFlatRead.Read(fixture,new CancellationToken(true)),"Precancelled QR read stops before raster work");
        reject(()=>QrFlatRead.Read(new(1,2,new byte[8]),default),"Nonsquare image is a clear bounded refusal");
        reject(()=>QrFlatRead.Read(new(29,29,new byte[1]),default),"Malformed pixel buffer is refused");
        check(QrFlatRead.VisibleText("a\0\n\t\u202e\u2028😀[U+0000]")=="a[U+0000][U+000A][U+0009][U+202E][U+2028]😀[U+0000]","Untrusted control/bidi/line characters display visibly without damaging Unicode");
        var prefs=new Prefs();check(!prefs.IsOn(Feature.QrCodes)&&Catalog.Actions.Single(x=>x.Id=="qr.readflat").Feature==Feature.QrCodes&&!Catalog.Actions.Single(x=>x.Id=="qr.readflat").RequiresFiles,"QR reader remains default-off and uses its own explicit picker");
    }
}
