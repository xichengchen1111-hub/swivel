using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public sealed record SlowProcess(int Id,string Name,double? CpuShare,ulong? WorkingSet);
public sealed record SlowMachineSnapshot(IReadOnlyList<SlowProcess> Processes,double? MemoryLoad,ulong? CommitUsed,ulong? CommitLimit,ulong? PagefileUsed,ulong? PagefileTotal,ulong? SystemFree,ulong? SystemTotal,TimeSpan Uptime,bool? PendingReboot,IReadOnlyList<string> Issues);
public enum SlowAction { None, UsageMonitor, AppMemory, CleanUp, WindowsUpdate }
public enum SlowFindingKind { Memory, Cpu, Storage, Restart, Moment }
public sealed record SlowFinding(SlowFindingKind Kind,string Text,SlowAction Action);
public static class SlowMachine
{
    public const string Limits="This is a moment-in-time sample, not a heat sensor or a diagnosis of the cause. CPU share is a percentage of all logical processors; processes that exit or deny access can be missing. Working sets may share memory. Only common pending-restart registry flags are checked; an unknown value is not a clean bill of health.";
    public const string Thresholds="Review thresholds: physical memory or commit at least 90%; one process at least 25% of total CPU; system-drive free space below 10%; uptime at least 14 days or a readable pending-restart flag. Pagefile use alone is not evidence of a slowdown.";
    public static double? CpuShare(ulong before,ulong after,TimeSpan elapsed,int processors)=>after>=before&&elapsed.Ticks>0&&processors>0?Math.Clamp((double)((decimal)(after-before)/elapsed.Ticks/processors*100),0,100):null;
    public static IReadOnlyList<SlowFinding> Findings(SlowMachineSnapshot s)
    {
        var findings=new List<SlowFinding>();
        if(s.MemoryLoad is >=90 and <=100||s.CommitUsed is ulong used&&s.CommitLimit is >0&&used<=s.CommitLimit&&100m*used/s.CommitLimit>=90)
            findings.Add(new(SlowFindingKind.Memory,"Memory or commit is nearly full; review the three largest working sets below.",SlowAction.AppMemory));
        if(s.Processes.Any(p=>p.CpuShare is >=25 and <=100))findings.Add(new(SlowFindingKind.Cpu,"A process used at least a quarter of this PC's CPU during the sample; review the busiest processes below.",SlowAction.UsageMonitor));
        if(s.SystemFree is ulong free&&s.SystemTotal is >0&&free<=s.SystemTotal&&100m*free/s.SystemTotal<10)findings.Add(new(SlowFindingKind.Storage,"The system drive has less than 10% free space; review files you no longer need.",SlowAction.CleanUp));
        if(s.PendingReboot==true||s.Uptime>=TimeSpan.FromDays(14))findings.Add(new(SlowFindingKind.Restart,"Windows reports a pending restart or has been running for at least 14 days; review Windows Update when convenient.",SlowAction.WindowsUpdate));
        findings.Add(new(SlowFindingKind.Moment,findings.Count==0?"No review threshold was reached in this moment-in-time sample; unavailable readings can hide a problem.":"This sample is only a moment in time; it does not measure temperature or prove why the PC is slow.",SlowAction.None));
        return findings.AsReadOnly();
    }
}
