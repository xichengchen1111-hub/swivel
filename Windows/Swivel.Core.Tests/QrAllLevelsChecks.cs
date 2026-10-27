using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Swivel.Core;

internal static class QrAllLevelsChecks
{
    // Frozen standard parameter facts, separate from the encoder's compact
    // arrays. No third-party implementation is part of the app or these tests.
    private static readonly JsonDocument Parameters=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","qr-qh-parameters.json")));
    internal static (int[] Blocks,int Ecc,int Data) Facts(QrCorrection level,int version)
    {
        var row=Parameters.RootElement[version-1].GetProperty(level==QrCorrection.Quartile?"Q":"H");
        return(row.GetProperty("blocks").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),row.GetProperty("ecc").GetInt32(),row.GetProperty("data").GetInt32());
    }
    internal static int[] Formats(QrCorrection level)=>level==QrCorrection.Quartile?
        new[]{0x355f,0x3068,0x3f31,0x3a06,0x24b4,0x2183,0x2eda,0x2bed}:
        new[]{0x1689,0x13be,0x1ce7,0x19d0,0x0762,0x0255,0x0d0c,0x083b};
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check((int)QrCorrection.Low==0&&(int)QrCorrection.Medium==1&&(int)QrCorrection.Quartile==2&&(int)QrCorrection.High==3,"QR appended enum values preserve old settings");
        foreach(var level in new[]{QrCorrection.Quartile,QrCorrection.High})
        {
            int previous=0;
            for(int version=1;version<=40;version++)
            {
                var facts=Facts(level,version);int maximum=facts.Data-(version<10?3:4);
                foreach(int length in new[]{previous+1,maximum})
                {
                    string payload=string.Concat(Enumerable.Range(0,length).Select(i=>(char)('!'+(i*37+version)%94)));
                    var code=QrCode.Encode(payload,level);check(code.Version==version&&code.Correction==level,"Q/H exact smallest version at both boundaries");
                    QrChecks.Inspect(code,payload,check);
                }
                previous=maximum;
            }
            check(previous==QrCode.Limit(level)&&QrCode.Level(level)==(level==QrCorrection.Quartile?"Q":"H"),"Q/H ECI capacity and labels match v40 standard data budget");
            int max=QrCode.Limit(level);
            foreach(string payload in new[]{new string('中',max/3),string.Concat(Enumerable.Repeat("😀",max/4))+new string('x',max%4)})
            {
                check(QrCode.Payload(payload,level).Length==max,"Q/H Unicode byte ceiling without silent shortening");
                QrChecks.Inspect(QrCode.Encode(payload,level),payload,check);
            }
            foreach(string bad in new[]{new string('x',max+1),new string('中',max/3+1),"", "\ud800", "a\0b"})
                reject(()=>QrCode.Encode(bad,level),"Q/H malformed/over-capacity input refused instead of downgrade");
            var entry=new QrEntry(Guid.NewGuid().ToString("N"),"New level "+QrCode.Level(level),"中文 😀","Group",level);
            byte[] bytes=QrLibrary.Encode(new[]{entry});var restored=QrLibrary.Decode(bytes).Single();
            check(restored.Name==entry.Name&&restored.Text==entry.Text&&restored.Group==entry.Group&&restored.Correction==level&&restored.Id!=entry.Id,"Q/H portable backup preserves level and generates local ID");
            var prefs=new Prefs();prefs.SaveQr(entry);string before=prefs.Serialize();
            check(Prefs.Parse(before).QrCodes.Single()==entry&&!prefs.IsOn(Feature.QrCodes),"Q/H prefs keep ID/content/group and default-off switch");
            int changes=0;prefs.Changed+=()=>changes++;
            reject(()=>prefs.ImportQr(new[]{new QrEntry(Guid.NewGuid().ToString("N"),"Before conflict","new"),entry with{Id=Guid.NewGuid().ToString("N"),Correction=QrCorrection.Low}}),"Q/H level conflict refuses whole merge");
            check(prefs.Serialize()==before&&changes==0,"Q/H merge conflict leaves switches/library/IDs unchanged");
            var maximumEntries=Enumerable.Range(0,32).Select(i=>new QrEntry(Guid.NewGuid().ToString("N"),new string((char)(0x4e00+i),48),new string('<',max),new string('組',32),level)).ToArray();
            byte[] full=QrLibrary.Encode(maximumEntries);check(full.Length<QrLibrary.MaxBytes&&QrLibrary.Decode(full).All(x=>x.Correction==level&&x.Text.Length==max),"32 maximum escaped Q/H entries fit existing 1MiB portable budget");
        }
        foreach(var invalid in new[]{(QrCorrection)(-1),(QrCorrection)4,(QrCorrection)int.MaxValue})
        {reject(()=>QrCode.Level(invalid),"QR level label refuses undefined enum");reject(()=>QrCode.CapacityMessage(invalid),"QR capacity message refuses undefined enum");}
    }
}
