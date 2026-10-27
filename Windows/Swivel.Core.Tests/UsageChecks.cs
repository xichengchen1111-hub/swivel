using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

internal static class UsageChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var zero = new CpuTimes(0,0,0);
        check(UsageMath.CpuPercent(null,zero) is null,"CPU requires a baseline");
        check(UsageMath.CpuPercent(zero,zero) is null,"Zero elapsed CPU time is unknown rather than idle");
        check(UsageMath.CpuPercent(zero,new(100,100,0)) == 0,"Idle is included in kernel and removed exactly once");
        check(UsageMath.CpuPercent(zero,new(0,100,100)) == 100,"Fully busy CPU reaches 100 percent");
        check(UsageMath.CpuPercent(zero,new(50,100,100)) == 75,"CPU includes user and nonidle kernel");
        check(UsageMath.CpuPercent(new(1,2,3),new(0,3,4)) is null && UsageMath.CpuPercent(new(1,2,3),new(2,1,4)) is null
            && UsageMath.CpuPercent(new(1,2,3),new(2,3,2)) is null,"Counter rollback in any component produces a gap");
        check(UsageMath.CpuPercent(zero,new(101,100,100)) is null,"Impossible idle greater than kernel is rejected");
        check(UsageMath.CpuPercent(zero,new(0,ulong.MaxValue,ulong.MaxValue)) == 100,"CPU addition and multiplication do not overflow ulong");
        check(UsageMath.CpuPercent(zero,new(ulong.MaxValue,ulong.MaxValue,ulong.MaxValue)) == 50,"Maximum unsigned times retain exact ratio");
        check(UsageMath.MemoryPercent(null) is null && UsageMath.MemoryPercent(new(0,0)) is null && UsageMath.MemoryPercent(new(10,11)) is null,"Missing or inconsistent memory is unavailable");
        check(UsageMath.MemoryPercent(new(100,25)) == 75 && UsageMath.MemoryPercent(new(100,100)) == 0 && UsageMath.MemoryPercent(new(100,0)) == 100,"Physical memory uses total minus available with inclusive endpoints");
        check(UsageMath.MemoryPercent(new(ulong.MaxValue,0)) == 100,"Maximum physical byte arithmetic is bounded");
        for (uint i = 1; i <= 300; i++)
        {
            var first = new CpuTimes(i*3,i*5,i*7); var next = new CpuTimes(first.Idle+i,first.Kernel+i*4,first.User+i*6);
            check(UsageMath.CpuPercent(first,next) == 90,"CPU deltas ignore absolute counters: "+i);
            check(Math.Abs(UsageMath.MemoryPercent(new(i*1000,i*300))!.Value-70) < 1e-12,"Physical memory ratio independent of total: "+i);
        }
        var reading = new UsageReading(new(0,10,10),new(1000,250),CpuSampleStatus.Available);
        var history = new UsageHistory();
        check(history.Add(reading,0).CpuPercent is null && history.Points.Single().MemoryPercent == 75,"First sample contains only known memory and a CPU baseline");
        check(history.Add(reading with {Cpu=new(5,20,20)},2).CpuPercent == 75,"Second sample computes the first CPU interval");
        history.Break(3);
        check(history.Points.Last().CpuPercent is null && history.Points.Last().MemoryPercent is null,"Pause appends an explicit graph gap");
        check(history.Add(reading with {Cpu=new(500,1000,1000)},50).CpuPercent is null,"Resume never averages across hidden time");
        check(history.Add(new(null,new(1000,500),CpuSampleStatus.Unavailable),52).CpuPercent is null,"Failed CPU is a gap while memory continues");
        check(history.Add(reading,54).CpuPercent is null,"A good sample following failure needs a fresh baseline");
        check(history.Add(reading with {CpuStatus=CpuSampleStatus.MultipleProcessorGroups},56).CpuPercent is null,"Multiple groups cannot be misreported as whole-system CPU");
        check(history.Add(new(null,new(10,11),CpuSampleStatus.Unavailable),58).UsedBytes is null,"Inconsistent memory never reports underflowed used bytes");
        var before = history.Points.ToArray();
        foreach (double bad in new[] {-1.0,double.NaN,double.PositiveInfinity,1.0})
        { reject(()=>history.Add(reading,bad),"Invalid/nonmonotonic timestamp rejected"); check(history.Points.SequenceEqual(before),"Invalid sample leaves history intact"); }
        reject(()=>history.Break(-1),"Invalid gap rejected");
        for (int i=60;i<300;i++) history.Add(reading,i);
        check(history.Points.Count==61 && history.Points[0].Seconds==239 && history.Points[^1].Seconds==299,"Dense sampling is bounded to 61 entries");
        history.Add(reading,1000); check(history.Points.Count==1,"Samples older than two minutes are discarded even after a long interruption");
        var prefs = new Prefs();
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.monitor"),"Monitoring respects default-off Doctor");
        prefs.Set(Feature.Doctor,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.monitor" && !x.RequiresFiles),"Monitoring requires an explicit action but no file input");
        prefs.Set(Feature.MyWheel,true); prefs.SetCustomActions(new[]{"doctor.monitor"});
        check(Catalog.ForCustomWheel(prefs,Array.Empty<string>()).Single().Id=="doctor.monitor","Monitoring has a stable custom-wheel ID");
        Lifecycle(check,reject);
    }
    private static void Lifecycle(Action<bool,string> check,Action<Action,string> reject)
    {
        var reading = new UsageReading(new(0,1,1),new(10,5),CpuSampleStatus.Available);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int reads=0;
        var sampler = new UsageSampler(_=>{Interlocked.Increment(ref reads);entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();return reading;},default);
        check(sampler.SampleAsync().GetAwaiter().GetResult() is null && reads==0,"Constructing a disabled sampler performs no read");
        sampler.SetEnabled(true); var pending=sampler.SampleAsync();
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(10)),"Controlled native call entered");
            check(sampler.SampleAsync().GetAwaiter().GetResult() is null && reads==1,"Overlapping timer ticks never start a second native call");
            sampler.SetEnabled(false); sampler.SetEnabled(true);
        }
        finally {release.Set();}
        check(pending.GetAwaiter().GetResult() is null,"Pause and resume discard an in-flight prior-generation result");
        check(sampler.SampleAsync().GetAwaiter().GetResult()==reading && reads==2,"A later active read can run after the old worker finishes");
        sampler.DisposeAsync().AsTask().GetAwaiter().GetResult(); sampler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        sampler.SetEnabled(true); check(sampler.SampleAsync().GetAwaiter().GetResult() is null && reads==2,"Repeated disposal is safe and cannot restart sampling");
        using var closingEntered=new ManualResetEventSlim();using var closingRelease=new ManualResetEventSlim();
        var closing=new UsageSampler(_=>{closingEntered.Set();if(!closingRelease.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();return reading;},default);
        closing.SetEnabled(true);var old=closing.SampleAsync(); Task stopping;
        try {check(closingEntered.Wait(TimeSpan.FromSeconds(10)),"In-flight close fixture entered");stopping=closing.DisposeAsync().AsTask();check(!stopping.IsCompleted,"Close waits for a blocked native call to return");}
        finally{closingRelease.Set();}
        stopping.GetAwaiter().GetResult();check(old.GetAwaiter().GetResult() is null,"Closed worker cannot deliver a late sample");
        using var cancellation=new CancellationTokenSource(); var cancelled=new UsageSampler(_=>reading,cancellation.Token);
        cancelled.SetEnabled(true);cancellation.Cancel();check(cancelled.SampleAsync().GetAwaiter().GetResult() is null,"External cancellation prevents native reads");cancelled.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var failing=new UsageSampler(_=>throw new InvalidOperationException("fixture"),default);failing.SetEnabled(true);
        reject(()=>failing.SampleAsync().GetAwaiter().GetResult(),"Worker failure is observed by its caller");
        reject(()=>failing.DisposeAsync().AsTask().GetAwaiter().GetResult(),"Shutdown still joins a failed worker and releases cancellation resources");
    }
}
