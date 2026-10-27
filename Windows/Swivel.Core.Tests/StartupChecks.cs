using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class StartupChecks
{
    private static RegistrationText Text(string s)=>new(s,RegistrationValueState.Present);
    private sealed class Source(Func<StartupLocation,StartupOpen> open):IStartupSource{public StartupOpen Open(StartupLocation l)=>open(l);}
    private sealed class Root(Func<uint,StartupRead> at,Action? dispose=null):IStartupRoot
    {public bool Disposed;public int Calls;public StartupRead At(uint index){Calls++;return at(index);}public void Dispose(){Disposed=true;dispose?.Invoke();}}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        const string secret="C:\\private\\token-SENTINEL --key=SECRET %TEMP%";
        int disposed=0,opened=0;var complete=StartupRecords.Scan(new Source(l=>{opened++;return new(new Root(i=>i==0?new(StartupReadKind.Item,"startup 🦜",Text(secret),2):new(StartupReadKind.End),()=>disposed++),RegistrationOpenState.Open);}),default);
        check(complete.Records.Count==8&&complete.Roots.Count==8&&!complete.Partial&&opened==8&&disposed==8,"Startup exactly eight fixed source/view/kind locations and all roots disposed");
        check(complete.Roots.Select(r=>r.Location).Distinct().Count()==8,"Startup shared-view data not silently merged");
        foreach(string lang in new[]{"en","zh-Hans","zh-Hant","ja"})
        {
            string L(string k)=>Strings.L(k,lang);string hidden=StartupRecords.Report(complete,false,L,default),shown=StartupRecords.Report(complete,true,L,default);
            check(!hidden.Contains("SENTINEL")&&!hidden.Contains("SECRET")&&!hidden.Contains("%TEMP%"),"Startup report hides every command by default in all languages");
            check(shown.Contains(secret)&&shown.Contains("startup 🦜"),"Startup explicit report keeps raw environment text without expansion");
            foreach(var row in complete.Records){check(!StartupRecords.Describe(row,false,L).Contains("SENTINEL"),"Startup selected details use same privacy flag");check(StartupRecords.Describe(row,true,L).Contains(secret),"Startup selected explicit details include complete command");}
        }
        var root=new Root(i=>i switch{0=>new(StartupReadKind.Item,"Name",Text("first"),1),1=>new(StartupReadKind.Item,"name",Text("second"),1),2=>new(StartupReadKind.Item,"",Text("default"),1),3=>new(StartupReadKind.TooLarge),4=>new(StartupReadKind.Item,"bad\ud800",Text("valid"),1),_=>new(StartupReadKind.End)});
        var special=StartupRecords.Scan(new Source(l=>l==new StartupLocation(RegistrationSource.Machine64,false)?new(root,RegistrationOpenState.Open):new(null,RegistrationOpenState.Missing)),default);
        check(special.Records.Count==5&&special.Partial&&root.Disposed&&special.Roots[0].Issues.HasFlag(StartupRootIssue.Duplicates),"Startup duplicate/oversize/invalid-name positions stay visible");
        check(special.Records[1].Issues.HasFlag(StartupIssue.Duplicate)&&special.Records[2].Name==""&&StartupRecords.Name(special.Records[2],k=>k)=="Unnamed registry value","Startup duplicate comparison OrdinalIgnoreCase and unnamed value explicit");
        check(special.Records[3].Name is null&&special.Records[3].Command.State==RegistrationValueState.TooLarge&&special.Records[4].Name is null,"Startup incomplete names never expose a prefix");
        var full=StartupRecords.Scan(new Source(_=>new(new Root(i=>new(StartupReadKind.Item,"n"+i,Text("c"),1)),RegistrationOpenState.Open)),default);
        check(full.Records.Count==2048&&full.Roots.All(x=>x.Recorded==256&&x.Issues==StartupRootIssue.Limit),"Startup per-root256 and global2048 attempts, exact cap conservatively partial");
        var unavailable=StartupRecords.Scan(new Source(_=>new(new Root(_=>new(StartupReadKind.Unavailable)),RegistrationOpenState.Open)),default);
        check(unavailable.Records.Count==8&&unavailable.Partial&&unavailable.Roots.All(r=>r.Issues==StartupRootIssue.Unavailable),"Startup failing positions and all unavailable root statuses remain visible");
        var missing=StartupRecords.Scan(new Source(_=>new(null,RegistrationOpenState.Missing)),default);check(!missing.Partial&&missing.Records.Count==0&&missing.Roots.Count==8,"Missing is recorded separately from failed queries, zero rows not proof no startup");
        TimeSpan time=TimeSpan.Zero;var late=new Root(_=>{time=TimeSpan.FromSeconds(11);return new(StartupReadKind.Item,"late",Text("returned"),1);});int lateOpens=0;
        var timed=StartupRecords.ScanWithClock(new Source(_=>{lateOpens++;return new(late,RegistrationOpenState.Open);}),()=>time,default);
        check(lateOpens==1&&late.Calls==1&&late.Disposed&&timed.Records.Count==1&&timed.Roots.All(r=>r.Issues.HasFlag(StartupRootIssue.TimeLimit)),"Late-returned observation retained, no additional call after deadline, all remaining roots marked");
        time=TimeSpan.Zero;var lateEndRoot=new Root(_=>{time=TimeSpan.FromSeconds(10);return new(StartupReadKind.End);});var lateEnd=StartupRecords.ScanWithClock(new Source(_=>new(lateEndRoot,RegistrationOpenState.Open)),()=>time,default);check(lateEnd.Partial&&lateEnd.Roots[0].Issues==StartupRootIssue.TimeLimit&&lateEndRoot.Disposed,"Late end-of-enumeration still marked time-limited");
        time=TimeSpan.Zero;var lateMissing=StartupRecords.ScanWithClock(new Source(_=>{time=TimeSpan.FromSeconds(10);return new(null,RegistrationOpenState.Missing);}),()=>time,default);check(lateMissing.Roots[0].Issues==(StartupRootIssue.Missing|StartupRootIssue.TimeLimit),"Late missing-open preserves both returned fact and deadline");
        foreach(string invalid in new[]{"a\0b","\ud800","\udc00",new string('x',2049)})check(StartupRecords.Validate(Text(invalid)).State is RegistrationValueState.Invalid or RegistrationValueState.TooLarge,"Startup invalid/overlimit command refuses whole string");
        check(StartupRecords.Validate(Text(new string('x',2048))).State==RegistrationValueState.Present&&StartupRecords.ValidName(new string('x',256))&&!StartupRecords.ValidName(new string('x',257)),"Startup exact UTF16 bounds accepted plusone refused");
        check(StartupRecords.ValidName("name\\value")&&StartupRecords.ValidName(""),"Value names are labels, never child paths");
        check(StartupRecords.Validate(Text(" \t\r\n")).State==RegistrationValueState.Empty&&StartupRecords.Validate(new("secret",(RegistrationValueState)999)).State==RegistrationValueState.Invalid,"Startup blank and unknown states canonicalized");
        foreach(var state in Enum.GetValues<RegistrationValueState>())if(state!=RegistrationValueState.Present)check(StartupRecords.Validate(new("raw-secret",state)).Value is null,"Nonpresent startup command drops stale raw text");
        var controls=complete.Records[0] with{Name="n\u202e\n",Command=Text("\ufeffa\u202e\n")};string safe=StartupRecords.Describe(controls,true,k=>k);check(safe.Contains("[U+FEFF]")&&safe.Contains("[U+202E]")&&!safe.Contains('\u202e'),"Startup names/commands escape invisibles rather than expanding or hiding BOM");
        using(var cancel=new CancellationTokenSource())
        {var cancelRoot=new Root(_=>{cancel.Cancel();return new(StartupReadKind.Item,"n",Text(secret),1);});reject(()=>StartupRecords.Scan(new Source(_=>new(cancelRoot,RegistrationOpenState.Open)),cancel.Token),"Startup read cancellation propagated");check(cancelRoot.Disposed,"Startup cancellation releases native source root");}
        reject(()=>StartupRecords.Scan(new Source(_=>throw new Exception("must not open")),new CancellationToken(true)),"Startup precancel opens no location");
        var failed=new Root(_=>throw new IOException("injected"));reject(()=>StartupRecords.Scan(new Source(_=>new(failed,RegistrationOpenState.Open)),default),"Unexpected source failure propagates");check(failed.Disposed,"Startup unexpected failure releases root");
        var huge=full with{Records=full.Records.Select(r=>r with{Command=Text(new string('\u202e',2048))}).ToArray()};
        check(!StartupRecords.Report(huge,false,k=>k,default).Contains("[U+202E]"),"Hidden commands do not consume report budget");reject(()=>StartupRecords.Report(huge,true,k=>k,default),"Final escaped localized report over4MiB refused whole");
        reject(()=>StartupRecords.Report(complete,false,k=>k,new CancellationToken(true)),"Startup report honors cancellation");
        var prefs=new Prefs();check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.startup"),"Startup action Doctor defaultoff");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.startup"),"Startup action needs no files and independent Doctor switch");prefs.SetCustomActions(new[]{"doctor.startup"});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()=="doctor.startup","Startup stable MyWheel ID roundtrip");
    }
}
