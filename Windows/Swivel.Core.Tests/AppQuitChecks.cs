using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class AppQuitChecks
{
    private const ulong T=133_000_000_000_000_000UL;
    private const string Windows=@"C:\Windows";
    private static AppMemoryProcess P(uint pid,string? path,uint? session=1,ulong? created=null,ulong? cpu=null)=>new(pid,created??T+pid,path,1,AppMemoryIssue.None,null,cpu,session);
    private static AppMemoryGroup G(params AppMemoryProcess[] processes)=>new(processes[0].Path,processes,processes.Length,0);
    private static AppMemoryGroup G(AppMemoryProcess[] processes,AppMemoryHelper[] helpers)=>new(processes[0].Path,processes,processes.Length,0,helpers);
    private sealed class Lease(CloseWindowInfo info) : ICloseWindowLease
    {
        public CloseWindowInfo Info{get;}=info;
        public Func<CloseWindowInfo?>? Current;
        public int Sends;
        public CloseWindowInfo? Inspect(CancellationToken token)=>Current is null?Info:Current();
        public CloseWindowsResult Send(CancellationToken token){Sends++;return CloseWindowsResult.Processed;}
        public void Dispose(){}
    }
    private sealed class Source(Lease[] leases) : ICloseWindowsSource
    {
        public CloseWindowsEnumeration Enumerate(CancellationToken token)=>new(leases.Select(x=>x.Info.Handle).ToArray());
        public ICloseWindowLease? Open(nint window,CancellationToken token)=>leases.Single(x=>x.Info.Handle==window);
    }
    private sealed class Probe(Func<AppQuitTarget,AppQuitState> state) : IAppQuitProbe
    {
        public int Calls;
        public AppQuitState State(AppQuitTarget target){Calls++;return state(target);}
    }
    private sealed class Item(uint pid,ulong? cpu,uint? session) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>T+pid;
        public string? ImagePath()=>@"C:\Apps\a"+pid+".exe";
        public ulong? WorkingSet()=>1;
        public ulong? CpuTime()=>cpu;
        public uint? Session()=>session;
        public void Dispose(){}
    }
    private sealed class Plain(uint pid) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>T+pid;
        public string? ImagePath()=>@"C:\Apps\p.exe";
        public ulong? WorkingSet()=>1;
        public void Dispose(){}
    }
    private sealed class Src(Func<uint,IAppMemoryProcess> open,params uint[] ids) : IAppMemorySource
    {
        public AppMemoryIds Enumerate()=>new(ids);
        public IAppMemoryProcess? Open(uint pid)=>open(pid);
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string L(string text)=>text;
        const uint Self=50;
        const string App=@"C:\Program Files\Maker\App\app.exe",Helper=@"C:\Program Files\Maker\App\sub\helper.exe";

        // Eligibility.
        AppQuitBlock E(AppMemoryGroup g)=>AppQuit.Eligibility(g,Self,1,Windows);
        check(E(G(P(10,App)))==AppQuitBlock.None,"Ordinary app in this session is offered");
        check(E(G(P(10,App),P(Self,App)))==AppQuitBlock.Self&&E(G(P(Self,@"C:\Apps\Swivel.Windows.exe")))==AppQuitBlock.Self,"A group containing Swivel is never offered, even as a member");
        check(E(G(P(11,@"C:\Apps\Swivel.exe")))==AppQuitBlock.Essential&&E(G(P(12,@"D:\Tools\EXPLORER.EXE")))==AppQuitBlock.Essential&&E(G(P(13,@"C:\x\csrss.exe")))==AppQuitBlock.Essential&&E(G(P(14,@"C:\x\winlogon.exe")))==AppQuitBlock.Essential,
            "Shell and essential names are never offered, case-insensitively and wherever they are");
        check(E(G(P(15,@"c:\windows\System32\notepad.exe")))==AppQuitBlock.SystemFolder&&AppQuit.Eligibility(G(P(15,@"C:\Windows\notepad.exe")),Self,1,@"C:\Windows\")==AppQuitBlock.SystemFolder,"Everything in the Windows folder is never offered");
        check(E(G(P(16,@"C:\WindowsApps\x.exe")))==AppQuitBlock.None,"A sibling folder that only starts with the Windows folder name is not the Windows folder");
        check(E(G(P(17,App,0)))==AppQuitBlock.OtherSession&&E(G(P(18,App,2)))==AppQuitBlock.OtherSession,"Services in session 0 and other sessions are never offered");
        check(E(G(P(19,App,null)))==AppQuitBlock.Unverified&&E(G(P(20,App) with{Created=null}))==AppQuitBlock.Unverified,"Unknown session or start time is not offered");
        check(E(G(P(21,null)))==AppQuitBlock.NoPath&&E(G(P(4,App)))==AppQuitBlock.Self&&E(G(P(0,App)))==AppQuitBlock.Self,"Unknown paths and system identifiers are not offered");
        check(AppQuit.Eligibility(G(P(10,App)),Self,0,Windows)==AppQuitBlock.OtherSession,"Nothing is offered when Swivel itself runs in session 0");

        // Targets keep the app first and drop blocked members.
        var ordered=G(new[]{P(30,App),P(31,Helper),P(32,@"C:\Program Files\Maker\App\conhost.exe"),P(33,Helper,2)},new[]{new AppMemoryHelper(31,30),new AppMemoryHelper(32,30),new AppMemoryHelper(33,30)});
        var targets=AppQuit.Targets(ordered,Self,1,Windows);
        check(targets.Select(x=>x.Pid).SequenceEqual(new uint[]{30,31})&&!targets[0].Helper&&targets[1].Helper&&targets[0].Created==T+30&&targets[0].Path==App,"Targets: app first, then helpers; essential or other-session members are left out");
        check(AppQuit.Targets(G(P(Self,App)),Self,1,Windows).Count==0,"A blocked group has no targets");

        // Plan: only exact identity matches are used.
        CloseWindowInfo W(int handle,uint pid,ulong created,string path)=>new((nint)handle,pid,1000u+pid,created,1,"Title "+handle,"Class",path);
        var windows=new[]{W(1,30,T+30,App),W(2,31,T+31,Helper),W(3,30,T+30,App),W(4,30,T+999,App),W(5,31,T+31,@"C:\Other\helper.exe"),W(6,77,T+77,App)};
        var plan=AppQuit.Plan(new[]{ordered},windows,Self,1,Windows);
        var first=plan.Apps.Single();
        check(first.Windows.SequenceEqual(new[]{0,2,1})&&plan.Windows.SequenceEqual(new[]{0,2,1}),"Plan sends to every matching window, main process first, then helpers");
        check(first.Replaced==2,"Windows whose identifier was reused (other start time or path) are skipped and counted");
        check(first.WithoutWindows.Count==0&&first.Unverified==2,"Left-out members are counted");
        var noWindow=AppQuit.Plan(new[]{G(P(40,App))},windows,Self,1,Windows).Apps.Single();
        check(noWindow.Windows.Count==0&&noWindow.WithoutWindows.Single().Pid==40,"A process with no listed window is reported, not asked");
        var refused=AppQuit.Plan(new[]{G(P(41,@"C:\Windows\x.exe")),G(P(42,App))},windows,Self,1,Windows);
        check(refused.Refused.Count==1&&refused.Apps.Count==1,"Blocked groups are refused with nothing sent");
        var twice=AppQuit.Plan(new[]{ordered,G(P(30,App))},windows,Self,1,Windows);
        check(twice.Apps.Count==1&&twice.Refused.Count==1&&twice.Windows.Count==3,"A process ticked twice is asked once");
        reject(()=>AppQuit.Plan(Enumerable.Range(100,33).Select(i=>G(P((uint)i,App))).ToArray(),windows,Self,1,Windows),"At most 32 apps per request");
        reject(()=>AppQuit.Plan(new[]{ordered},windows,Self,1,Windows,new CancellationToken(true)),"Planning honours cancellation");
        string details=AppQuit.Details(plan,windows,L);
        check(details.Contains("Title 1")&&details.Contains("Title 2")&&details.Contains("Helper window")&&!details.Contains("Title 4")&&details.Contains("replaced: 2"),"Confirmation lists exactly the windows that will be asked");
        check(AppQuit.Details(refused,windows,L).Contains("Not offered, nothing sent: x.exe"),"Confirmation names refused apps");

        // Verify-before-send: a window whose process changed after planning is not sent.
        var leases=windows.Select(x=>new Lease(x)).ToArray();
        leases[2].Current=()=>leases[2].Info with{Created=T+12345};
        leases[1].Current=()=>null;
        using(var session=CloseWindows.Scan(new Source(leases),default))
        {
            var live=AppQuit.Plan(new[]{ordered},session.Windows,Self,1,Windows);
            var results=session.RequestEach(live.Windows,default);
            check(results.SequenceEqual(new CloseWindowsResult?[]{CloseWindowsResult.Processed,CloseWindowsResult.Changed,CloseWindowsResult.Changed})
                &&leases[0].Sends==1&&leases[1].Sends==0&&leases[2].Sends==0&&leases.Skip(3).All(x=>x.Sends==0),"Each window is checked again right before sending; replaced or vanished processes are skipped");
        }

        // Exit check after the wait.
        var probe=new Probe(t=>t.Pid==30?AppQuitState.Exited:t.Pid==31?AppQuitState.Running:throw new InvalidOperationException("injected"));
        var all=new[]{targets[0],targets[1],new AppQuitTarget(99,T,@"C:\Apps\z.exe",false)};
        var states=AppQuit.Check(all,probe,default);
        check(states.SequenceEqual(new[]{AppQuitState.Exited,AppQuitState.Running,AppQuitState.Unknown}),"Exit check reads each process; a failing probe is Unknown, never Exited");
        string outcome=AppQuit.Outcome(all,states,L);
        check(outcome.Contains("exited: 1")&&outcome.Contains("Still running: 1")&&outcome.Contains("Still running · helper.exe · 31")&&outcome.Contains("Unknown · z.exe · 99")&&!outcome.Contains("app.exe"),"Outcome names every process still running and forces nothing");
        string partial=AppQuit.Outcome(all,states,L,new HashSet<uint>{30});
        check(partial.Contains("Not asked, still running · helper.exe · 31")&&partial.Contains("Unknown · z.exe · 99"),"Processes that were never sent a request are listed apart from those asked");
        reject(()=>AppQuit.Check(all,probe,new CancellationToken(true)),"Exit check honours cancellation");
        reject(()=>AppQuit.Outcome(all,states.Take(2).ToArray(),L),"Outcome requires one state per process");

        // CPU deltas.
        var second=TimeSpan.FromSeconds(1);
        check(AppCpu.Percent(0,10_000_000,second)==100&&AppCpu.Percent(5_000_000,7_500_000,TimeSpan.FromSeconds(2))==12.5,"CPU percent of one processor from 100 ns time deltas");
        check(AppCpu.Percent(0,40_000_000,second)==400,"CPU can exceed 100% on several processors");
        check(AppCpu.Percent(null,1,second) is null&&AppCpu.Percent(1,null,second) is null,"Missing samples give no value");
        check(AppCpu.Percent(10,5,second) is null,"A counter that went backwards (reset or wrap) gives no value");
        check(AppCpu.Percent(1,2,TimeSpan.Zero) is null&&AppCpu.Percent(1,2,TimeSpan.FromTicks(-5)) is null,"No elapsed time gives no value");
        check(AppCpu.Percent(0,ulong.MaxValue,TimeSpan.FromTicks(1)) is double huge&&huge>1e20,"Largest counter delta does not overflow");
        var before=new AppMemorySnapshot(default,new[]{P(60,App,cpu:1_000_000),P(61,App,cpu:0),P(62,App,created:T+1,cpu:0),P(64,App,cpu:null)},Array.Empty<AppMemoryGroup>(),false,false,false,false);
        var group=G(P(60,App,cpu:3_000_000),P(61,App,cpu:1_000_000),P(62,App,cpu:5_000_000),P(63,App,cpu:9_000_000),P(64,App,cpu:5));
        var cpu=AppCpu.Group(group,before,second);
        check(cpu.Percent is double v&&Math.Abs(v-30)<1e-9&&cpu.Unmeasured==3,"Group CPU sums only processes with two readings of the same start time");
        check(AppCpu.Group(group,(AppMemorySnapshot?)null,second)==(null,5),"No previous snapshot gives Unknown");
        check(AppCpu.Group(G(P(62,App,cpu:5_000_000)),before,second).Percent is null,"A replaced process identifier is not measured");

        // Scan reads the optional values; sources without them keep the old records.
        var read=AppMemory.Scan(new Src(pid=>new Item(pid,pid*10,pid==2?null:1u),1,2),default);
        check(read.Processes[0].Cpu==10&&read.Processes[0].Session==1&&read.Processes[1].Session is null&&read.Processes.All(x=>x.Issues==AppMemoryIssue.None),"Scan records CPU time and session without adding issues");
        var plain=AppMemory.Scan(new Src(pid=>new Plain(pid),1),default);
        check(plain.Processes[0]==new AppMemoryProcess(1,T+1,@"C:\Apps\p.exe",1,AppMemoryIssue.None),"Sources without the optional readings produce unchanged records");
        check(AppMemory.Report(read,true,L,default)==AppMemory.Report(read with{Processes=read.Processes.Select(x=>x with{Cpu=null,Session=null}).ToArray()},true,L,default),"Reports do not include the optional readings");
    }
}
