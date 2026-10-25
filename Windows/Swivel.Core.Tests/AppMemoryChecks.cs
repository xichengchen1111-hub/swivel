using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class AppMemoryChecks
{
    private sealed class Item(uint pid,ulong? memory,string? path,Action? step=null) : IAppMemoryProcess
    {
        public bool Closed;
        public ulong? CreationTime(){step?.Invoke();return 133_000_000_000_000_000UL+pid;}
        public string? ImagePath(){step?.Invoke();return path;}
        public ulong? WorkingSet(){step?.Invoke();return memory;}
        public void Dispose()=>Closed=true;
    }
    private sealed class Source(IReadOnlyList<uint> ids,Func<uint,Item?> open,bool limited=false,bool unavailable=false) : IAppMemorySource
    {
        public int Opened,Enumerated;
        public AppMemoryIds Enumerate(){Enumerated++;return new(ids,limited,unavailable);}
        public IAppMemoryProcess? Open(uint pid){Opened++;return open(pid);}
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string L(string text)=>text;
        var items=new[]{new Item(1,100,@"C:\Users\private-owner\App\app.exe"),new Item(2,200,@"C:\Users\private-owner\App\app.exe"),new Item(3,null,@"C:\Users\private-owner\Other\app.exe"),new Item(4,400,@"C:\Users\private-owner\App\App.exe"),new Item(5,50,null)};
        var source=new Source(new uint[]{1,2,3,4,5,6},pid=>pid<=5?items[(int)pid-1]:null);
        var snapshot=AppMemory.Scan(source,default);
        check(source.Enumerated==1&&source.Opened==6&&items.All(x=>x.Closed),"Process read handles close after every snapshot entry");
        check(snapshot.Processes.Count==6&&snapshot.Groups.Count==5&&snapshot.Partial&&snapshot.KnownBytes==750&&snapshot.Unmeasured==2,"Working sets sum only known entries with explicit unknowns");
        check(snapshot.Groups.First().Path==@"C:\Users\private-owner\App\App.exe"&&snapshot.Groups.First().KnownBytes==400,"Process groups descend by exact known subtotal");
        var joined=snapshot.Groups.Single(x=>x.Count==2);check(joined.KnownBytes==300&&joined.Unmeasured==0,"Exactly equal reported paths join");
        check(snapshot.Groups.Count(x=>x.Path?.EndsWith("app.exe",StringComparison.Ordinal)==true)==2,"Same basename in separate paths remains separate");
        check(snapshot.Groups.Count(x=>x.Path is null)==2,"Unknown paths remain separate by PID");
        string hidden=AppMemory.Report(snapshot,false,L,default),shown=AppMemory.Report(snapshot,true,L,default);
        check(!hidden.Contains("private-owner")&&!hidden.Contains(@"C:\")&&shown.Contains(@"C:\Users\private-owner"),"Report and names never leak full Windows paths when hidden on a Mac host");
        check(hidden.Contains("Executable Path Groups")&&hidden.Contains("1, 2")&&hidden.Contains("Recorded Processes"),"Full report includes grouped subtotals and every process without exposing paths");
        check(AppMemory.Describe(joined,false,L).Contains("300")&&!AppMemory.Describe(joined,false,L).Contains("private-owner"),"Selected group details obey identical path privacy");
        check(AppMemory.Name(snapshot.Processes[0],L)=="app.exe"&&hidden.Contains(AppMemory.Measurement)&&hidden.Contains(AppMemory.Grouping),"Stored path basename and scope are explicit in copies");
        var limit=AppMemory.ScanWithClock(new Source(new uint[]{1,2},_=>throw new Exception("must not open")),()=>TimeSpan.FromSeconds(10),default);
        check(limit.TimeLimited&&limit.Processes.Count==2&&limit.Processes.All(x=>x.Issues==AppMemoryIssue.TimeLimit)&&limit.Unmeasured==2,"Soft timeout retains every unqueried enumerated PID");
        var full=AppMemory.Scan(new Source(new uint[]{1},_=>null,limited:true),default);check(full.Partial&&full.EnumerationLimited&&AppMemory.Status(full,L).Contains("incomplete"),"Full enumeration buffer is explicitly partial");
        var empty=AppMemory.Scan(new Source(Array.Empty<uint>(),_=>null,unavailable:true),default);check(empty.Partial&&empty.EnumerationUnavailable&&AppMemory.Header(empty,false,L).Contains("Unknown"),"Enumeration failure never presents an empty list as zero memory");
        var duplicate=AppMemory.Scan(new Source(new uint[]{1,1},_=>new Item(1,0,@"C:\a.exe")),default);check(duplicate.DuplicatesOmitted&&duplicate.Processes.Count==1&&duplicate.KnownBytes==0,"Duplicate PID never double-counted; genuine known zero remains valid");
        var max=AppMemory.Scan(new Source(Enumerable.Range(1,4096).Select(x=>(uint)x).ToArray(),pid=>new Item(pid,ulong.MaxValue,@"C:\same.exe")),default);
        check(max.KnownBytes==(decimal)ulong.MaxValue*4096&&max.Groups.Single().Count==4096,"4096 maximum unsigned native counters cannot overflow decimal subtotal");
        reject(()=>AppMemory.Scan(new Source(new uint[4097],_=>null),default),"Source cannot exceed maximum PID budget");
        foreach(string path in new[]{"",@"C:relative.exe","/unix/path.exe",@"C:\a"+"\ud800",@"C:\a"+"\0x.exe",@"C:\"+new string('x',4094)})
        {
            var bad=AppMemory.Scan(new Source(new uint[]{1},_=>new Item(1,123,path)),default);check(bad.Processes.Single().Path is null&&bad.Partial&&bad.KnownBytes==123,"Bad/unsupported/too-long paths stay unknown without losing readable memory");
        }
        string exactPath=@"C:\"+new string('a',4093);check(AppMemory.Scan(new Source(new uint[]{1},_=>new Item(1,1,exactPath)),default).Processes[0].Path==exactPath,"Exactly 4096 path units retained without truncation");
        string hostile=@"C:\Users\private-owner\a"+"\u202e\u00a0\ue000"+".exe";
        var visible=AppMemory.Scan(new Source(new uint[]{1},_=>new Item(1,1,hostile)),default);
        check(AppMemory.Report(visible,true,L,default).Contains("[U+202E]")&&AppMemory.Report(visible,true,L,default).Contains("[U+00A0]")&&!AppMemory.Report(visible,false,L,default).Contains("private-owner"),"Hidden name/path characters are visible without exposing parent directories");
        var large=AppMemory.Scan(new Source(Enumerable.Range(1,4096).Select(x=>(uint)x).ToArray(),pid=>new Item(pid,1,@"C:\"+new string('x',4000)+@"\a.exe")),default);
        check(Encoding.UTF8.GetByteCount(AppMemory.Report(large,false,L,default))<AppMemory.MaxReportBytes,"Large recorded paths stay cheap in hidden-path report");
        reject(()=>AppMemory.Report(large,true,L,default),"Expanded full-path report is refused whole at UTF8 byte budget");
        for(int phase=1;phase<=3;phase++)
        {
            using var cancel=new CancellationTokenSource();int calls=0;var item=new Item(1,1,@"C:\x.exe",()=>{if(++calls==phase)cancel.Cancel();});
            reject(()=>AppMemory.Scan(new Source(new uint[]{1},_=>item),cancel.Token),"Cancellation after each process field aborts snapshot");
            check(item.Closed&&calls==phase,"Each cancelled process field closes handle and makes no later call");
        }
        var pre=new Source(new uint[]{1},_=>null);reject(()=>AppMemory.Scan(pre,new CancellationToken(true)),"App memory pre-cancel avoids enumeration");check(pre.Enumerated==0&&pre.Opened==0,"No process APIs before pre-cancel");
        var prefs=new Prefs();check(!prefs.IsOn(Feature.Doctor)&&!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.appmemory"),"App memory defaults off with Doctor");prefs.Set(Feature.Doctor,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.appmemory"),"App memory uses explicit no-file Doctor action");
    }
}
