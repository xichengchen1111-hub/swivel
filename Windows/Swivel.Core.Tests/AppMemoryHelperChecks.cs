using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class AppMemoryHelperChecks
{
    private sealed record Row(uint Pid,uint? Parent,string? Path,ulong? Created=null,ulong Memory=1);
    private sealed class Item(Row row) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>row.Created??133_000_000_000_000_000UL+row.Pid;
        public string? ImagePath()=>row.Path;
        public ulong? WorkingSet()=>row.Memory;
        public void Dispose(){}
    }
    private sealed class Source(IReadOnlyList<Row> rows,Func<int,AppMemoryParentIds?>? parents=null,string[]? excluded=null,Action? step=null) : IAppMemorySource
    {
        public int ParentReads;
        public AppMemoryIds Enumerate()=>new(rows.Select(x=>x.Pid).ToArray());
        public IAppMemoryProcess? Open(uint pid){step?.Invoke();return new Item(rows.Single(x=>x.Pid==pid));}
        public AppMemoryParentIds? ParentIds()
        {
            int call=ParentReads++;
            return parents is not null?parents(call):new(rows.Where(x=>x.Parent.HasValue).ToDictionary(x=>x.Pid,x=>x.Parent!.Value),ExcludedDirectories:excluded);
        }
    }
    private sealed class NoParents(IReadOnlyList<Row> rows) : IAppMemorySource
    {
        public AppMemoryIds Enumerate()=>new(rows.Select(x=>x.Pid).ToArray());
        public IAppMemoryProcess? Open(uint pid)=>new Item(rows.Single(x=>x.Pid==pid));
    }
    private static AppMemoryGroup GroupOf(AppMemorySnapshot snapshot,uint pid)=>snapshot.HelperGroups!.Single(g=>g.Processes.Any(p=>p.Pid==pid));
    private static bool Together(AppMemorySnapshot snapshot,uint a,uint b)=>ReferenceEquals(GroupOf(snapshot,a),GroupOf(snapshot,b));
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string L(string text)=>text;
        const string App=@"C:\Program Files\Maker\App\app.exe";
        const ulong T=133_000_000_000_000_000UL;
        var rows=new[]
        {
            new Row(10,4,App,T),
            new Row(11,10,@"C:\Program Files\Maker\App\helper.exe",T+5),           // same folder
            new Row(12,10,@"C:\Program Files\Maker\App\bin\gpu\renderer.exe",T+6), // inside the folder
            new Row(13,12,@"C:\Program Files\Maker\App\bin\gpu\crash\crash.exe",T+7), // chain through 12
            new Row(14,10,@"C:\Program Files\Other\tool.exe",T+8),                // another folder
            new Row(15,10,@"C:\Program Files\Maker\App\late.exe",T-1),            // older than its "parent": reused PID
            new Row(16,10,@"C:\Program Files\Maker\Apple\x.exe",T+9),             // shared prefix, not inside
            new Row(17,10,@"C:\Program Files\Maker\app\case.exe",T+9),            // different case
            new Row(18,99,@"C:\Program Files\Maker\App\orphan.exe",T+9),          // parent not in snapshot
            new Row(19,10,null,T+9),                                              // unknown path
            new Row(20,10,@"C:\Program Files\Maker\App\..\Escape\e.exe",T+9),     // dot segment
            new Row(21,10,@"C:\Program Files\Maker\App\self.exe",null),          // unknown start time (fake reports 0)
        };
        var snapshot=AppMemory.Scan(new Source(rows.Select(r=>r.Pid==21?r with{Created=0}:r).ToArray()),default);
        check(snapshot.ParentIssues==AppMemoryParentIssue.None&&snapshot.HelperGroups is not null,"Parents read twice and identical enable helper grouping");
        check(snapshot.Groups.Count==rows.Length&&snapshot.Groups.All(g=>g.Helpers is null),"Exact-path grouping is unchanged when helper grouping exists");
        check(Together(snapshot,10,11)&&Together(snapshot,10,12),"Helpers in the parent's folder or a subfolder join the parent's group");
        check(Together(snapshot,10,13),"Helper chains follow accepted links to the root app");
        var app=GroupOf(snapshot,10);
        check(app.Path==App&&app.Count==4&&app.KnownBytes==4&&app.Helpers!.Select(h=>(h.Pid,h.Parent)).SequenceEqual(new[]{(11u,10u),(12u,10u),(13u,12u)}),"Helper group records root path, subtotal and each immediate parent link");
        foreach(var (pid,reason) in new[]{(14u,"Different-folder child stays separate"),(15u,"Child older than parent (PID reuse) stays separate"),(16u,"Sibling folder sharing a name prefix stays separate"),(17u,"Case-different folder stays separate"),(18u,"Missing parent stays separate"),(19u,"Unknown path stays separate by PID"),(20u,"Dot-segment path never counts as inside"),(21u,"Unknown start time stays separate")})
            check(GroupOf(snapshot,pid).Count==1&&GroupOf(snapshot,pid).Helpers!.Count==0,reason);
        check(snapshot.Processes.Single(p=>p.Pid==14).Parent==10&&snapshot.Processes.Single(p=>p.Pid==18).Parent==99,"Reported parents are recorded even when not attached");
        check(GroupOf(snapshot,19).Path is null&&snapshot.HelperGroups!.Sum(g=>g.Count)==rows.Length,"Every process appears in exactly one helper group, unknown ones visible");

        string hidden=AppMemory.Report(snapshot,false,L,default,true),plain=AppMemory.Report(snapshot,false,L,default);
        check(hidden.Contains("Helper Process Groups")&&hidden.Contains(AppMemory.HelperGrouping)&&hidden.Contains("helper.exe 11 (started by 10)")&&!hidden.Contains("Program Files"),"Helper report lists attached helpers by name without hidden paths");
        check(!plain.Contains("Helper Process Groups")&&!plain.Contains(AppMemory.HelperGrouping)&&plain.Contains("Executable Path Groups"),"Report without the option keeps exact-path groups");
        check(AppMemory.Describe(snapshot.Processes.Single(p=>p.Pid==11),false,L).Contains("Reported parent process identifier: 10"),"Process details show the reported parent");

        // Equal start times are allowed, so a two-process cycle is possible: both stay separate.
        var cycle=AppMemory.Scan(new Source(new[]{new Row(1,2,@"C:\A\a.exe",T),new Row(2,1,@"C:\A\b.exe",T),new Row(3,1,@"C:\A\c.exe",T+1)}),default);
        check(cycle.HelperGroups!.Count==3&&cycle.HelperGroups.All(g=>g.Helpers!.Count==0),"Parent cycles and chains into cycles stay separate");
        var selfParent=AppMemory.Scan(new Source(new[]{new Row(1,1,@"C:\A\a.exe")}),default);check(selfParent.HelperGroups!.Single().Helpers!.Count==0,"Self-parent is ignored");
        var root=AppMemory.Scan(new Source(new[]{new Row(1,0,@"C:\launcher.exe",T),new Row(2,1,@"C:\Any\x.exe",T+1),new Row(3,0,@"\\server\share\l.exe",T),new Row(4,3,@"\\server\share\sub\y.exe",T+1)}),default);
        check(root.HelperGroups!.Count==4,"Parents at a drive or share root never collect helpers");
        var windows=AppMemory.Scan(new Source(new[]{new Row(1,0,@"C:\Windows\explorer.exe",T),new Row(2,1,@"C:\Windows\System32\notepad.exe",T+1),new Row(3,2,@"C:\Windows\System32\sub\z.exe",T+2)},excluded:new[]{@"C:\WINDOWS\"}),default);
        check(windows.HelperGroups!.Count==3,"Parents in the Windows folder are excluded, case-insensitively");
        var same=AppMemory.Scan(new Source(new[]{new Row(1,0,@"C:\A\a.exe",T),new Row(2,1,@"C:\A\a.exe",T+1),new Row(3,0,@"C:\A\a.exe",T+2)}),default);
        check(same.HelperGroups!.Single().Count==3&&same.HelperGroups!.Single().Helpers!.Single()==new AppMemoryHelper(2,1),"Same-path instances still share one group");

        var changed=AppMemory.Scan(new Source(new[]{new Row(1,0,@"C:\A\a.exe",T),new Row(2,1,@"C:\A\b.exe",T+1),new Row(3,1,@"C:\A\c.exe",T+1)},
            call=>new(call==0?new Dictionary<uint,uint>{[1]=0,[2]=1,[3]=1}:new Dictionary<uint,uint>{[1]=0,[2]=7})),default);
        check(changed.ParentIssues==AppMemoryParentIssue.Changed&&changed.Processes.Single(p=>p.Pid==2).Parent is null&&changed.Processes.Single(p=>p.Pid==3).Parent is null&&changed.HelperGroups!.Count==3,"Parents that differ or vanish between snapshots are not used");
        check(AppMemory.ParentStatus(changed,L).Contains("missing or changed"),"Changed parents are stated");
        var unavailable=AppMemory.Scan(new Source(new[]{new Row(1,null,@"C:\A\a.exe")},_=>new(new Dictionary<uint,uint>(),Unavailable:true)),default);
        check(unavailable.ParentIssues==AppMemoryParentIssue.Unavailable&&unavailable.HelperGroups!.Count==1&&AppMemory.ParentStatus(unavailable,L).Contains("unavailable"),"Unavailable parent snapshot keeps every process separate and says so");
        var limited=AppMemory.Scan(new Source(new[]{new Row(1,0,@"C:\A\a.exe",T),new Row(2,1,@"C:\A\b.exe",T+1)},_=>new(new Dictionary<uint,uint>{[1]=0,[2]=1},Limited:true)),default);
        check(limited.ParentIssues==AppMemoryParentIssue.Limited&&Together(limited,1,2)&&AppMemory.ParentStatus(limited,L).Contains("incomplete"),"Limited parent list is stated; matching entries still apply");

        var none=AppMemory.Scan(new NoParents(new[]{new Row(1,null,@"C:\A\a.exe")}),default);
        check(none.HelperGroups is null&&none.ParentIssues==AppMemoryParentIssue.NotRead&&AppMemory.GroupsFor(none,true)==none.Groups&&!AppMemory.Report(none,false,L,default,true).Contains("Helper Process Groups"),"Sources without parents leave helper grouping unavailable and exact groups in use");
        int ticks=0;var timed=new Source(new[]{new Row(1,0,@"C:\A\a.exe",T),new Row(2,1,@"C:\A\b.exe",T+1)});
        var late=AppMemory.ScanWithClock(timed,()=>TimeSpan.FromSeconds(ticks++>=7?10:0),default);
        check(late.ParentIssues==AppMemoryParentIssue.TimeLimit&&late.HelperGroups is null&&timed.ParentReads==1&&AppMemory.ParentStatus(late,L).Contains("time limit"),"Time limit before the second parent snapshot disables helper grouping");
        var early=new Source(new[]{new Row(1,0,@"C:\A\a.exe")});var expired=AppMemory.ScanWithClock(early,()=>TimeSpan.FromSeconds(10),default);
        check(early.ParentReads==0&&expired.HelperGroups is null&&expired.ParentIssues==AppMemoryParentIssue.TimeLimit,"Expired clock reads no parents at all");

        reject(()=>AppMemory.Scan(new Source(new[]{new Row(1,null,@"C:\A\a.exe")},_=>new(Enumerable.Range(0,4097).ToDictionary(x=>(uint)x,x=>0u))),default),"Parent list cannot exceed the process budget");
        reject(()=>AppMemory.Scan(new Source(new[]{new Row(1,null,@"C:\A\a.exe")},excluded:new[]{"relative"}),default),"Excluded folders must be absolute paths");
        reject(()=>AppMemory.Scan(new Source(new[]{new Row(1,null,@"C:\A\a.exe")},excluded:Enumerable.Repeat(@"C:\W",17).ToArray()),default),"Excluded folder list is bounded");
        using(var cancel=new CancellationTokenSource())
        {
            var cancelling=new Source(new[]{new Row(1,0,@"C:\A\a.exe")},call=>{cancel.Cancel();return new(new Dictionary<uint,uint>());});
            reject(()=>AppMemory.Scan(cancelling,cancel.Token),"Cancellation during a parent snapshot aborts the scan");
            check(cancelling.ParentReads==1,"No further parent read after cancellation");
        }
        reject(()=>AppMemory.HelperGroups(snapshot.Processes,Array.Empty<string>(),new CancellationToken(true)),"Helper grouping honours cancellation");
        var many=Enumerable.Range(1,4096).Select(x=>new Row((uint)x,x==1?0u:(uint)x-1,@"C:\A"+string.Concat(Enumerable.Repeat(@"\d",Math.Min(x,200)))+@"\p.exe",T+(ulong)x)).ToArray();
        var deep=AppMemory.Scan(new Source(many),default);
        check(deep.HelperGroups!.Count==1&&deep.HelperGroups[0].Count==4096&&deep.HelperGroups[0].Helpers!.Count==4095,"A 4096-deep helper chain is grouped within budget");
    }
}
