using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Swivel.Core;

internal static class QrGardenChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach(var level in Enum.GetValues<QrCorrection>())foreach(int bytes in new[]{1,100,500,QrCode.Limit(level)})
        {
            var code=QrCode.Encode(new string('x',bytes),level);byte[] flat=code.Raster();int n=code.Size;double half=(n-1)/2.0,extent=n+8;
            foreach(var template in Enum.GetValues<QrGardenTemplate>())foreach(var season in Enum.GetValues<QrGardenSeason>())
            {
                var garden=QrGarden.Build(code,template,season);var cells=garden.Boxes.Where(x=>x.Bottom==0&&x.Top>0).ToDictionary(x=>(x.X,x.Z));
                check(garden.Boxes.Count<=n*n+6&&garden.Size==n,"Garden bounded model count");
                check(garden.Boxes[0]==new GardenBox(0,0,-n*.025,0,extent,extent,0xffffff,template==QrGardenTemplate.Island?0x56859cU:0xc6bca7U),"Complete four-module white slab");
                for(int y=0;y<n;y++)for(int x=0;x<n;x++)check(cells.ContainsKey((x-half,y-half))==code[x,y],"Top projection dark cells exactly source QR");
                foreach(var box in garden.Boxes)
                {
                    check(double.IsFinite(box.Top)&&box.Top>box.Bottom&&box.Width>0&&box.Depth>0&&box.Color<=0xffffff&&box.Side<=0xffffff,"Finite positive garden cuboid");
                    if(box.Bottom==0)check(box.Width==1&&box.Depth==1&&box.Top>0&&((box.Color>>16&255)*77+(box.Color>>8&255)*150+(box.Color&255)*29)<110*256,"Dark top squares and color contrast");
                    double bound=Math.Sqrt(Math.Pow(Math.Abs(box.X)+box.Width/2,2)+Math.Pow(Math.Max(Math.Abs(box.Bottom),Math.Abs(box.Top)),2)+Math.Pow(Math.Abs(box.Z)+box.Depth/2,2));
                    check(bound<extent*.8,"All corner radii fit aspect-independent camera sphere");
                }
                check(code.Raster().SequenceEqual(flat),"3D never mutates flat QR output");
                check(garden.Boxes.SequenceEqual(QrGarden.Build(code,template,season).Boxes),"Garden deterministic without ID or random seed persistence");
            }
            foreach(var size in new[]{(1.0,8.0),(500.0,2000.0),(420.0,420.0),(760.0,420.0),(4096.0,200.0)})
            {
                double width=QrGarden.ViewWidth(n,size.Item1,size.Item2);check(width>=extent*1.65&&width*size.Item2/size.Item1>=extent*1.65-1e-10,"Camera fits both axes including wide-window top view");
            }
        }
        foreach(var bad in new[]{-1,3,int.MaxValue})
        {reject(()=>QrGarden.Validate((QrGardenTemplate)bad,QrGardenSeason.Summer),"Closed garden template");reject(()=>QrGarden.Validate(QrGardenTemplate.Tree,(QrGardenSeason)bad),"Closed garden season");}
        foreach(string value in new[]{"","Tree","winter","unknown"}){reject(()=>QrGarden.Template(value),"Closed template key");reject(()=>QrGarden.Season(value),"Closed season key");}
        foreach(var t in Enum.GetValues<QrGardenTemplate>())check(QrGarden.Template(QrGarden.Key(t))==t,"Template key roundtrip");
        foreach(var s in Enum.GetValues<QrGardenSeason>())check(QrGarden.Season(QrGarden.Key(s))==s,"Season key roundtrip");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();reject(()=>QrGarden.Build(QrCode.Encode("cancel"),QrGardenTemplate.Tree,QrGardenSeason.Summer,cancelled.Token),"Precancelled garden");}
        foreach(double bad in new[]{0,-1,double.NaN,double.PositiveInfinity})reject(()=>QrGarden.ViewWidth(21,bad,100),"Invalid viewport");
        reject(()=>QrGarden.ViewWidth(177,double.MaxValue,double.Epsilon),"Viewport ratio overflow");
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
}
