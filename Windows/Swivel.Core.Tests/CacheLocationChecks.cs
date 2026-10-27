using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class CacheLocationChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var choices=CacheLocations.Choices(@"C:\Users\me\Temp\",@"D:\LegacyCache");
        check(choices.Count==2&&choices[0].Kind==CacheLocationKind.Temporary&&choices[1].Kind==CacheLocationKind.LegacyInternetCache,"Only two fixed Windows-reported location kinds are offered");
        check(choices[0].Path==@"C:\Users\me\Temp"&&choices[0].ReportedPath==@"C:\Users\me\Temp\"&&choices[0].CanAttempt&&choices[1].Path==@"D:\LegacyCache","One trailing separator removed without rewriting the source path or asserting access");
        foreach(string? path in new string?[]{null,"",@"C:\",@"C:",@"relative",@"\\server\cache",@"\\?\C:\cache",@"C:\cache\\",@"C:\cache\..\Documents",@"C:\cache.\x",@"C:\cache:stream",@"C:\AUX\x","C:\\cache\u202e",new string('x',5000),"C:\\cache\0bad","C:\\cache"+char.ConvertFromUtf32(0xe0001),"C:\\cache\ud800"})
        {
            var blocked=CacheLocations.FromReported(CacheLocationKind.Temporary,path);
            check(!blocked.CanAttempt&&blocked.Path is null&&!string.IsNullOrWhiteSpace(blocked.Issue)&&blocked.ReportedPath==(path??""),"Unsafe/unknown/drive-root reported location stays visible but cannot authorize a scan");
        }
        check(CacheLocations.VisiblePath("a\u0085\u2028"+char.ConvertFromUtf32(0xe0001)+"😀\ud800")=="a[U+0085][U+2028][U+E0001]😀[U+D800]","Reported path display escapes supplementary format/controls/invalid UTF16 without damaging emoji");
        check(CacheLocations.VisiblePath(new string('a',511)+"😀")==new string('a',511)+"…","Path preview limit never splits a Unicode scalar");
        var max=CacheLocations.FromReported(CacheLocationKind.Temporary,@"C:\"+new string('x',237));var beyond=CacheLocations.FromReported(CacheLocationKind.Temporary,@"C:\"+new string('x',238));
        check(max.Path?.Length==240&&!beyond.CanAttempt,"Location length policy has exact 240-character boundary");
        var same=CacheLocations.Choices(@"C:\one",@"C:\one");check(same.All(c=>c.CanAttempt)&&same.Count==2,"Overlapping reports stay separate choices without aggregated reclaimable-size claim");
        reject(()=>CacheLocations.FromReported((CacheLocationKind)99,@"C:\cache"),"Unknown location kind rejected");reject(()=>((IList<CacheLocation>)choices).Clear(),"Shared chooser list is immutable");
        var prefs=new Prefs();string before=prefs.Serialize();_ =CacheLocations.FromReported(CacheLocationKind.Temporary,@"C:\does-not-exist");check(prefs.Serialize()==before&&!prefs.IsOn(Feature.Doctor),"Constructing location hints performs no preference mutation or implicit enablement");
        var action=Catalog.Actions.Single(a=>a.Id=="doctor.caches");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id),"Cache-location check default-off and uses no unrelated file selection");
        prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&WindowsDoctor.Areas.Single(a=>a.Id=="caches").ActionIds.SequenceEqual(new[]{action.Id,"doctor.appcaches"}),"Cache area exposes the location check and the app-cache review when enabled");
    }
}
