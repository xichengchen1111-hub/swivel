using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class InstalledAppsChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        RegistrationText Text(string? value)=>new(value,RegistrationValueState.Present);
        var made=new List<Entry>();int opens=0;var root=new Root(i=>i<5?new(RegistrationKeyKind.Item,i==0?"private-key-owner":i==1?"PRIVATE-KEY-OWNER":i==2?"bad\\child":i==3?"empty-name":"no-name"):new(RegistrationKeyKind.End),key=>
        {opens++;var entry=new Entry(f=>key=="empty-name"?Text("  "):key=="no-name"?RegistrationText.Unknown(RegistrationValueState.Missing):Text(f==RegistrationField.Name?"App\u202eName":f==RegistrationField.Version?"1.0":"Publisher"));made.Add(entry);return entry;});
        var source=Single(root);var snap=InstalledApps.Scan(source,default);
        check(snap.Roots.Count==4&&snap.Records.Count==5&&snap.Partial,"All four fixed views and every enumerated record remain visible");
        check(opens==3&&made.All(x=>x.Disposed)&&root.Disposed,"Case-insensitive duplicate keys not reopened;each entry and root disposed");
        check(snap.Records.Count(x=>x.Issues.HasFlag(RegistrationEntryIssue.Duplicate))==1&&snap.Records.Count(x=>x.Issues.HasFlag(RegistrationEntryIssue.KeyInvalid))==1,"Duplicate and invalid names retained with explicit issues");
        check(snap.Roots[0].Issues.HasFlag(RegistrationRootIssue.Duplicates)&&snap.Roots.Skip(1).All(x=>x.Issues==RegistrationRootIssue.Missing),"Missing views distinct from duplicate/partial views");
        string hidden=InstalledApps.Report(snap,false,x=>x,default),shown=InstalledApps.Report(snap,true,x=>x,default);
        check(!hidden.Contains("private-key-owner")&&!hidden.Contains("PRIVATE-KEY-OWNER")&&shown.Contains("private-key-owner"),"Raw subkeys hidden in all records by default,explicit opt-in only");
        check(hidden.Contains("App[U+202E]Name")&&!hidden.Contains('\u202e'),"Reported invisible text is visibly escaped");
        check(hidden.Contains(InstalledApps.Scope)&&hidden.Contains(InstalledApps.Limits)&&hidden.Contains(InstalledApps.Privacy)&&hidden.Contains("not a count of unique installed apps"),"Copies carry all scope/privacy/record-count limitations");
        check(snap.Records.Any(x=>x.Name.State==RegistrationValueState.Empty)&&snap.Records.Any(x=>x.Name.State==RegistrationValueState.Missing),"Missing/empty display names not replaced with raw keys");
        foreach(var state in Enum.GetValues<RegistrationValueState>())
        {
            var value=InstalledApps.Validate(new(state==RegistrationValueState.Present?"value":"must-not-retain",state));
            check(state==RegistrationValueState.Present?value.Value=="value":value.Value is null,"Nonpresent fields discard any stale raw string");
            check(!string.IsNullOrEmpty(InstalledApps.Value(value,x=>x)),"Every field status has an explicit label");
        }
        foreach(string value in new[]{"a\0b","\ud800","\udc00","a\ud800b"})check(InstalledApps.Validate(Text(value)).State==RegistrationValueState.Invalid,"Malformed Unicode or embeddedNUL rejected as a whole field");
        check(InstalledApps.Validate(Text(new string('界',1024))).State==RegistrationValueState.Present&&InstalledApps.Validate(Text(new string('界',1025))).State==RegistrationValueState.TooLarge,"Text UTF16 exact bound accepted/+1 refused,never truncated");
        check(InstalledApps.Validate(new("secret",(RegistrationValueState)999)).State==RegistrationValueState.Invalid&&InstalledApps.Validate(Text("\t \n")).State==RegistrationValueState.Empty,"Unknown status and blank text mapped explicitly");
        foreach(string? key in new string?[]{null,"","x\\y","\0","\ud800",new string('k',256)})check(!InstalledApps.ValidKey(key),"Invalid registry key names cannot become child paths");
        check(InstalledApps.ValidKey(new string('k',255))&&InstalledApps.ValidKey("safe / key 🐦"),"Registry key limits use registry names,not filesystem policy");
        int entryDisposals=0,rootDisposals=0;
        var full=new Source(s=>new(new Root(i=>new(RegistrationKeyKind.Item,"entry"+i),_=>new Entry(f=>Text(f==RegistrationField.Name?"Same reported app":"v"),()=>entryDisposals++),()=>rootDisposals++),RegistrationOpenState.Open));
        var maximum=InstalledApps.Scan(full,default);
        check(maximum.Records.Count==4096&&maximum.Roots.All(x=>x.Recorded==1024&&x.Issues==RegistrationRootIssue.Limit)&&maximum.Partial,"Per-view1024 and total4096 cap preserve potentially incomplete status");
        check(entryDisposals==4096&&rootDisposals==4,"All bounded scan entries/root leases disposed");
        check(maximum.Records.Select(x=>x.Source).Distinct().Count()==4,"Shared/aliased views not silently deduplicated or treated as unique app count");
        check(maximum.Records.Take(1024).All(x=>x.Source==RegistrationSource.Machine64)&&maximum.Records.Take(1024).Select(x=>x.Ordinal).SequenceEqual(Enumerable.Range(0,1024)),"Identical names sort stably by source and enumeration ordinal");
        var unavailable=new Root(_=>new(RegistrationKeyKind.Unavailable),_=>throw new Exception("Must not open"));var failed=InstalledApps.Scan(Single(unavailable),default);
        check(failed.Records.Count==0&&failed.Partial&&failed.Roots[0].Issues==RegistrationRootIssue.Unavailable&&unavailable.Disposed,"Enumeration failures visible even with zero rows and root released");
        var denied=InstalledApps.Scan(new Source(_=>new(null,RegistrationOpenState.Unavailable)),default);check(denied.Partial&&denied.Roots.All(x=>x.Issues==RegistrationRootIssue.Unavailable),"Denied/unavailable roots not mistaken for missing/empty apps");
        var empty=InstalledApps.Scan(new Source(_=>new(null,RegistrationOpenState.Missing)),default);check(!empty.Partial&&empty.Records.Count==0&&empty.Roots.Count==4,"Absent locations are normal but scope still excludes other app sources");
        var badValues=InstalledApps.Scan(Single(new Root(i=>i==0?new(RegistrationKeyKind.Item,"one"):new(RegistrationKeyKind.End),_=>new Entry(_=>Text("\ud800")))),default);
        check(badValues.Partial&&badValues.Records[0].Name.State==RegistrationValueState.Invalid,"Invalid field data retained as status rather than aborting/guessing encoding");
        TimeSpan elapsed=TimeSpan.Zero;int reads=0;
        var slowEntry=new Entry(_=>{reads++;elapsed=TimeSpan.FromSeconds(10);return Text("Known before limit");});var slowRoot=new Root(_=>new(RegistrationKeyKind.Item,"slow"),_=>slowEntry);
        var timed=InstalledApps.ScanWithClock(Single(slowRoot),()=>elapsed,default);
        check(reads==1&&timed.Records.Count==1&&timed.Records[0].Name.Value=="Known before limit"&&timed.Records[0].Version.State==RegistrationValueState.TimeLimit&&timed.Records[0].Publisher.State==RegistrationValueState.TimeLimit,"Time limit preserves known fields and records all unqueried fields explicitly");
        check(timed.Roots.All(x=>x.Issues.HasFlag(RegistrationRootIssue.TimeLimit))&&slowEntry.Disposed&&slowRoot.Disposed,"Later views time-unqueried,not empty;leases disposed after blocked call returns");
        elapsed=TimeSpan.Zero;
        var failedBeforeLimit=InstalledApps.ScanWithClock(Single(new Root(_=>new(RegistrationKeyKind.Item,"failed"),_=>new Entry(_=>{elapsed=TimeSpan.FromSeconds(10);return RegistrationText.Unknown(RegistrationValueState.Unavailable);}))),()=>elapsed,default);
        check(failedBeforeLimit.Records[0].Name.State==RegistrationValueState.Unavailable&&failedBeforeLimit.Records[0].Version.State==RegistrationValueState.TimeLimit,"Field queried but unavailable retains its real reason when later fields reach time limit");
        int calls=0;reject(()=>InstalledApps.Scan(new Source(_=>{calls++;throw new Exception();}),new CancellationToken(true)),"Precancel scan refuses before source access");check(calls==0,"Precancel invokes no registry source");
        using(var cancellation=new CancellationTokenSource())
        {
            var entry=new Entry(_=>{cancellation.Cancel();return Text("late");});var parent=new Root(_=>new(RegistrationKeyKind.Item,"one"),_=>entry);
            reject(()=>InstalledApps.Scan(Single(parent),cancellation.Token),"Field cancellation prevents a late successful snapshot");check(entry.Disposed&&parent.Disposed,"Cancellation closes current entry and root handles");
        }
        string baseline=InstalledApps.Report(empty,false,x=>x,default);int fill=InstalledApps.MaxReportBytes-Encoding.UTF8.GetByteCount(baseline);
        string L(string key)=>key==InstalledApps.Scope?key+new string('x',fill):key;
        check(Encoding.UTF8.GetByteCount(InstalledApps.Report(empty,false,L,default))==InstalledApps.MaxReportBytes,"Report UTF8 exact4MiB including all header/newlines/views accepted");
        reject(()=>InstalledApps.Report(empty,false,key=>key==InstalledApps.Scope?L(key)+"x":key,default),"Report onebyte over4MiB refused whole");
        reject(()=>InstalledApps.Report(snap,false,x=>x,new CancellationToken(true)),"Precancel report makes no copy payload");
        var prefs=new Prefs();check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.installedapps"),"Installed records absent while Doctor defaultoff");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.installedapps"),"Installed records available by explicit action without files");
    }
    private static Source Single(Root root)=>new(s=>s==RegistrationSource.Machine64?new(root,RegistrationOpenState.Open):new(null,RegistrationOpenState.Missing));
    private sealed class Source(Func<RegistrationSource,RegistrationOpen> open):IInstalledAppsSource{public RegistrationOpen Open(RegistrationSource source)=>open(source);}
    private sealed class Root(Func<uint,RegistrationKey> at,Func<string,IRegistrationEntry?> open,Action? dispose=null):IRegistrationRoot
    {public bool Disposed{get;private set;}public RegistrationKey At(uint index)=>at(index);public IRegistrationEntry? Open(string key)=>open(key);public void Dispose(){Disposed=true;dispose?.Invoke();}}
    private sealed class Entry(Func<RegistrationField,RegistrationText> read,Action? dispose=null):IRegistrationEntry
    {public bool Disposed{get;private set;}public RegistrationText Read(RegistrationField field)=>read(field);public void Dispose(){Disposed=true;dispose?.Invoke();}}
}
