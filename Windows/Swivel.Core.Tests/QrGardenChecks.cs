using System;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Swivel.Core;

internal static class QrGardenChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var tree=QrGarden.Build(QrCode.Encode("Layer regression"),QrGardenTemplate.Tree,QrGardenSeason.Spring);
        check(tree.Cover.Any(b=>b.Bottom>0)&&tree.Structure.Any(b=>b.Bottom>0&&b.Width<1),"Tree has separate canopy foliage and structure above the ground, rather than a solid relief");
        double[] worst=new double[3];int maxBoxes=0;
        foreach(var level in Enum.GetValues<QrCorrection>())
        {
            string field=level switch{QrCorrection.Low=>"DataWords",QrCorrection.Medium=>"MediumDataWords",QrCorrection.Quartile=>"QuartileDataWords",_=>"HighDataWords"};
            var capacities=(int[])typeof(QrCode).GetField(field,BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            for(int version=1;version<=40;version++)
            {
                int length=capacities[version-1]-(version<10?3:4);
                var code=QrCode.Encode(new string('x',length),level);byte[] flat=code.Raster(1);
                check(code.Version==version,"Garden fixture reaches each supported QR version/level");
                foreach(var template in Enum.GetValues<QrGardenTemplate>())foreach(var season in Enum.GetValues<QrGardenSeason>())
                {
                    var watch=Stopwatch.StartNew();var garden=QrGarden.Build(code,template,season);watch.Stop();
                    if(version==40)worst[(int)template]=Math.Max(worst[(int)template],watch.Elapsed.TotalMilliseconds);
                    maxBoxes=Math.Max(maxBoxes,garden.BoxCount);
                    check(garden.Size==code.Size&&garden.BoxCount<=code.Size*code.Size+9217&&garden.FaceCount<=QrGarden.MaxFaces,"Garden model and rendered faces have hard bounds at every size");
                    EveryTemplateTopSurfaceMatchesTheCode(code,garden,check);
                    check(garden.Structure.Count>30&&garden.Structure.Any(b=>b.Top>code.Size*.1),"All compositions have independent above-ground structure");
                    if(version is 1 or 17 or 40)
                    {
                        var again=QrGarden.Build(code,template,season);
                        check(garden.Ground.SequenceEqual(again.Ground)&&garden.Cover.SequenceEqual(again.Cover)&&garden.Structure.SequenceEqual(again.Structure),"All layers deterministic without new persisted seeds");
                    }
                }
                check(code.Raster(1).SequenceEqual(flat),"All gardens leave flat code bytes identical");
            }
        }
        foreach(var template in Enum.GetValues<QrGardenTemplate>())
        {
            check(worst[(int)template]<5000,"Largest garden core build stays below a conservative five-second regression budget");
            Console.WriteLine($"QR garden v40 {template}: worst core build {worst[(int)template]:F2} ms (all seasons/levels, this Mac only)");
        }
        Console.WriteLine($"QR garden measured maximum: {maxBoxes} boxes / {maxBoxes*5} quads; hard bound {QrGarden.MaxBoxes} boxes / {QrGarden.MaxFaces} quads");
        foreach(var bad in new[]{-1,3,int.MaxValue})
        {reject(()=>QrGarden.Validate((QrGardenTemplate)bad,QrGardenSeason.Summer),"Closed garden template");reject(()=>QrGarden.Validate(QrGardenTemplate.Tree,(QrGardenSeason)bad),"Closed garden season");}
        foreach(string value in new[]{"","Tree","winter","unknown"}){reject(()=>QrGarden.Template(value),"Closed template key");reject(()=>QrGarden.Season(value),"Closed season key");}
        foreach(var t in Enum.GetValues<QrGardenTemplate>())check(QrGarden.Template(QrGarden.Key(t))==t,"Template key roundtrip");
        foreach(var s in Enum.GetValues<QrGardenSeason>())check(QrGarden.Season(QrGarden.Key(s))==s,"Season key roundtrip");
        foreach(var t in Enum.GetValues<QrGardenTemplate>())foreach(var s in Enum.GetValues<QrGardenSeason>())
        {
            bool cancelled=false;try{QrGarden.Build(QrCode.Encode("cancel"),t,s,new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}
            check(cancelled,"Precancelled generation throws cancellation rather than publishing a partial scene");
        }
        reject(()=>QrGarden.Build(null!,QrGardenTemplate.Tree,QrGardenSeason.Summer),"Null QR refuses before geometry allocation");
        Motion(check,reject);
        var entry=new QrEntry(Guid.NewGuid().ToString("N"),"Garden","中文 🦜","Work",QrCorrection.High);var pref=new Prefs();pref.SaveQr(entry);string old=pref.Serialize();byte[] oldBackup=QrLibrary.Encode(new[]{entry});
        using(var parsed=JsonDocument.Parse(old)){var saved=parsed.RootElement.GetProperty("QrCodes")[0];check(!saved.TryGetProperty("Template",out _)&&!saved.TryGetProperty("Season",out _)&&!Encoding.UTF8.GetString(oldBackup).Contains("Template"),"Default choices retain legacy QR JSON shape");}
        foreach(var t in Enum.GetValues<QrGardenTemplate>())foreach(var s in Enum.GetValues<QrGardenSeason>())
        {
            var chosen=entry with{Template=t,Season=s};pref.SaveQr(chosen);check(Prefs.Parse(pref.Serialize()).QrCodes.Single()==chosen&&!pref.IsOn(Feature.QrCodes),"Garden choices persist with unchanged IDs and off switch");
            var backup=QrLibrary.Decode(QrLibrary.Encode(new[]{chosen})).Single();check(backup.Template==t&&backup.Season==s&&backup.Text==entry.Text&&backup.Id!=entry.Id,"Complete garden backup with fresh imported IDs");
            check(QrLibrary.Merge(new[]{chosen},new[]{backup}).Single()==chosen,"Identical garden import preserves local ID");
            if(t!=entry.Template||s!=entry.Season)reject(()=>QrLibrary.Merge(new[]{entry},new[]{chosen}),"Different garden choices conflict instead of silent discard");
        }
        pref.SaveQr(entry);check(pref.Serialize()==old&&QrLibrary.Encode(new[]{entry}).SequenceEqual(oldBackup),"Returning defaults restores exact old JSON bytes");
        string json=Encoding.UTF8.GetString(QrLibrary.Encode(new[]{entry with{Template=QrGardenTemplate.Island,Season=QrGardenSeason.Autumn}}));
        foreach(string field in new[]{"Template","Season"})foreach(string bad in new[]{"null","3","[]","{}","\"unknown\"","\"Summer\""})
        {
            string oldValue=field=="Template"?"island":"autumn";reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(json.Replace("\""+field+"\": \""+oldValue+"\"","\""+field+"\": "+bad))),"Wrong garden backup values refuse whole import");
        }
        string duplicate=json.Replace("\"Season\": \"autumn\"","\"Season\": \"autumn\", \"Season\": \"summer\"");reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(duplicate)),"Duplicate garden JSON property refuses");
        string Mac(string season,string template)=>"[{\"id\":\""+Guid.NewGuid().ToString("D")+"\",\"name\":\"Mac\",\"value\":\"text\",\"season\":\""+season+"\",\"template\":\""+template+"\"}]";
        foreach(var t in Enum.GetValues<QrGardenTemplate>())foreach(var s in Enum.GetValues<QrGardenSeason>())
        {var imported=MacQrLibrary.Decode(Encoding.UTF8.GetBytes(Mac(QrGarden.Key(s),QrGarden.Key(t)))).Compatible.Single();check(imported.Template==t&&imported.Season==s&&imported.Correction==QrCorrection.Medium,"Mac known choices and M level retained");}
        var unknown=MacQrLibrary.Decode(Encoding.UTF8.GetBytes(Mac("winter","new"))).Rows.Single();check(unknown.Entry is null&&unknown.Season=="winter"&&unknown.Template=="new"&&unknown.Issue==QrGarden.Invalid,"Unknown Mac choices per-row issue with raw values retained");
        foreach(var bad in new[]{entry with{Template=(QrGardenTemplate)99},entry with{Season=(QrGardenSeason)99}})reject(()=>pref.SaveQr(bad),"Invalid persisted garden enums refuse before mutation");
        check(pref.Serialize()==old,"Invalid garden mutation leaves preferences exact");
    }

    private static void EveryTemplateTopSurfaceMatchesTheCode(QrCode code,QrGarden garden,Action<bool,string> check)
    {
        int n=code.Size;double half=(n-1)/2.0;var full=new bool[n*n];double darkest=0,lightest=1,darkLinear=0,lightLinear=1;
        var slab=garden.Ground[0];
        check(slab.X==0&&slab.Z==0&&slab.Width==n+8&&slab.Depth==n+8&&slab.Top==0&&Luma(slab.Color)>.82,"Pale continuous four-module quiet zone");
        // This checks complete rectangle footprints, not just module centres. A full unit
        // surface exists at every module; all overlying persistent faces stay in that same
        // module and colour class. Thus no sample or pixel-resolution assumption is needed.
        foreach(var box in garden.Ground.Skip(1).Concat(garden.Cover))
        {
            int c=(int)Math.Round(box.X+half),r=(int)Math.Round(box.Z+half);double x=c-half,z=r-half;
            check(c>=0&&c<n&&r>=0&&r<n&&box.X-box.Width/2>=x-.5-1e-10&&box.X+box.Width/2<=x+.5+1e-10&&box.Z-box.Depth/2>=z-.5-1e-10&&box.Z+box.Depth/2<=z+.5+1e-10,"Every persistent face confined to its module; none pollute light modules or quiet zone");
            double luma=Luma(box.Color),linear=Linear(box.Color);
            check(code[c,r]?luma<.60:luma>.82,"Every possible visible persistent top face has its source module's colour class");
            if(code[c,r]){darkest=Math.Max(darkest,luma);darkLinear=Math.Max(darkLinear,linear);}else{lightest=Math.Min(lightest,luma);lightLinear=Math.Min(lightLinear,linear);}
            if(box.X==x&&box.Z==z&&box.Width==1&&box.Depth==1&&box.Top>0)full[r*n+c]=true;
        }
        for(int i=0;i<full.Length;i++)check(full[i],"Every QR module has a complete opaque unit top surface with no gaps");
        lightest=Math.Min(lightest,Luma(slab.Color));lightLinear=Math.Min(lightLinear,Linear(slab.Color));
        check(lightest-darkest>.22&&(lightLinear+.05)/(darkLinear+.05)>=3,"Per-module baked colour contrast: luma gap > .22 and linear contrast >= 3:1");
        foreach(var box in garden.Ground.Concat(garden.Cover).Concat(garden.Structure))
        {
            check(double.IsFinite(box.X)&&double.IsFinite(box.Z)&&double.IsFinite(box.Bottom)&&double.IsFinite(box.Top)&&double.IsFinite(box.Width)&&double.IsFinite(box.Depth)&&box.Top>box.Bottom&&box.Width>0&&box.Depth>0&&box.Color<=0xffffff&&box.Side<=0xffffff,"Finite valid voxel in each layer");
            check(Math.Abs(box.X)+box.Width/2<(n+8)/2.0&&Math.Abs(box.Z)+box.Depth/2<(n+8)/2.0||box==slab,"All geometry fits the slab footprint");
        }
    }
    private static double Luma(uint color)=>(.2126*(color>>16&255)+.7152*(color>>8&255)+.0722*(color&255))/255;
    private static double Linear(uint color)
    {
        static double Channel(uint v){double c=v/255.0;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
        return .2126*Channel(color>>16&255)+.7152*Channel(color>>8&255)+.0722*Channel(color&255);
    }
    private static void Motion(Action<bool,string> check,Action<Action,string> reject)
    {
        var turn=new QrGardenMotion();check(turn.Yaw==45&&turn.Elevation==32&&turn.StructureOpacity==1&&!turn.Moving,"Initial three-quarter garden pose");
        turn.Turn(true,false);double priorElevation=turn.Elevation,priorOpacity=turn.StructureOpacity;
        for(int i=0;i<240;i++)
        {
            turn.Advance(1.0/120);check(turn.Elevation>=priorElevation&&turn.StructureOpacity<=priorOpacity&&turn.Yaw is >=0 and <=45&&turn.StructureOpacity is >=0 and <=1,"One critical spring moves camera and fade monotonically without overshoot");
            priorElevation=turn.Elevation;priorOpacity=turn.StructureOpacity;
        }
        check(!turn.Moving&&turn.Yaw==0&&turn.Elevation==90&&turn.StructureOpacity==0,"Exact north-up top view with zero structure after spring settles");
        turn.Turn(false,false);turn.Advance(.18);check(turn.Elevation is >32 and <90,"Return turn has intermediate camera poses");
        double yaw=turn.Yaw,elevation=turn.Elevation;turn.Turn(true,false);check(turn.Yaw==yaw&&turn.Elevation==elevation,"Rapid turn reversal does not jump camera");
        turn.Advance(10);check(!turn.Moving&&turn.StructureOpacity==0,"Long stalled frame settles safely");
        turn.Turn(false,false);turn.Advance(.12);double beforeReverse=turn.Elevation;turn.Turn(true,false);turn.Advance(.001);
        check(turn.Elevation<beforeReverse,"Rapid canonical reversal preserves spring momentum rather than restarting with zero velocity");
        turn.Advance(10);
        turn.Turn(false,true);check(!turn.Moving&&turn.Yaw==45&&turn.Elevation==32&&turn.StructureOpacity==1,"Reduced motion jumps to scene");
        turn.Turn(true,false);turn.Advance(.05);turn.Advance(0,true);check(!turn.Moving&&turn.Elevation==90&&turn.StructureOpacity==0,"Reduced motion changed during animation snaps camera and fade together");
        turn.SetView(215,90);check(turn.Yaw==215&&turn.StructureOpacity==0&&!turn.Moving,"Manual top elevation also hides all structure");
        turn.Turn(false,false);turn.Stop();check(!turn.Moving&&turn.Elevation==32,"Stop settles motion without leaving a spring active");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1}){reject(()=>turn.Advance(bad),"Hostile frame interval refuses");reject(()=>turn.SetView(bad,40),"Hostile azimuth refuses");}
        foreach(double bad in new[]{0,19.99,90.01,double.NaN,double.NegativeInfinity})reject(()=>turn.SetView(45,bad),"Hostile camera elevation refuses");
    }
}
