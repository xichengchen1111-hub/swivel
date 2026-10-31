using System;
using System.Linq;
using Swivel.Core;

internal static class DoctorDiagnosisChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var drive=new MountedDrive('E',3,"NTFS",0,null,null,null){Hardware=new(7,false,"vendor","product",true,null),VolumeName="volume",Identity=new("volume","instance","ancestor",1,1),Protected=false,Label="label"};
        check(DriveEject.Eligible(drive),"USB fixed media with a verified non-system identity is eligible");
        foreach(uint bus in new uint[]{0,1,3,11,17,99})check(!DriveEject.Eligible(drive with{Hardware=drive.Hardware with{Bus=bus}}),"Unknown/internal buses do not authorize eject");
        foreach(uint bus in new uint[]{7,12,13})check(DriveEject.Eligible(drive with{Hardware=drive.Hardware with{Bus=bus}}),"USB/SD/MMC eligibility");
        check(DriveEject.Eligible(drive with{Hardware=drive.Hardware with{Bus=1,Removable=true}}),"Known removable bus eligibility");
        check(!DriveEject.Eligible(drive with{Hardware=drive.Hardware with{Bus=0,Removable=true}}),"Unknown bus stays ineligible despite removable flag");
        foreach(bool? protect in new bool?[]{true,null})check(!DriveEject.Eligible(drive with{Protected=protect}),"Protected or unknown system-volume state refuses eject");
        check(!DriveEject.Eligible(drive with{Identity=null})&&!DriveEject.Eligible(drive with{Type=4}),"Missing identity and network drives refuse eject");
        check(!DriveEject.Eligible(drive with{Label=null}),"Unavailable label cannot be used in an eject confirmation");
        check(DriveEject.Same(drive,drive)&&!DriveEject.Same(drive,drive with{Label="changed"})&&!DriveEject.Same(drive,drive with{Identity=drive.Identity with{Volume="other"}}),"Recheck binds volume/device/label identity");
        check(DoctorFacts.Visible("USB\n\u202Edevice")=="USB[U+000A][U+202E]device","Device text uses visible-text sanitizer");
        for(uint veto=0;veto<=13;veto++)check(!string.IsNullOrWhiteSpace(DriveEject.Veto(veto)),"Every documented PNP veto has a plain reason");
        check(DriveEject.Veto(3).Contains("program")&&DriveEject.Veto(5).Contains("program")&&DriveEject.Veto(99)==DriveEject.Veto(0),"Open handles/program veto and unknown veto are plain language");
        var normal=new SlowMachineSnapshot(Array.Empty<SlowProcess>(),20,1,10,0,10,100,1000,TimeSpan.FromDays(1),false,Array.Empty<string>());
        bool Has(SlowMachineSnapshot s,SlowFindingKind k)=>SlowMachine.Findings(s).Any(f=>f.Kind==k);
        foreach(double value in new[]{89.999,90,90.001})check(Has(normal with{MemoryLoad=value},SlowFindingKind.Memory)==(value>=90),"Physical memory 90% inclusive boundary");
        foreach(ulong used in new ulong[]{899,900,901})check(Has(normal with{CommitUsed=used,CommitLimit=1000},SlowFindingKind.Memory)==(used>=900),"Commit 90% inclusive boundary");
        foreach(double value in new[]{24.999,25,25.001})check(Has(normal with{Processes=new[]{new SlowProcess(1,"busy",value,1)}},SlowFindingKind.Cpu)==(value>=25),"Process 25% total-machine CPU boundary");
        foreach(ulong free in new ulong[]{9,10,11})check(Has(normal with{SystemFree=free,SystemTotal=100},SlowFindingKind.Storage)==(free<10),"System drive 10% strict boundary");
        foreach(double days in new[]{13.999,14,14.001})check(Has(normal with{Uptime=TimeSpan.FromDays(days)},SlowFindingKind.Restart)==(days>=14),"14-day inclusive uptime boundary");
        check(Has(normal with{PendingReboot=true},SlowFindingKind.Restart),"Readable pending restart produces finding");
        check(!Has(normal with{CommitUsed=1,CommitLimit=0,SystemFree=101,SystemTotal=100},SlowFindingKind.Memory)&&!Has(normal with{SystemFree=101,SystemTotal=100},SlowFindingKind.Storage),"Invalid denominators stay unknown");
        check(!Has(normal with{MemoryLoad=100.001,CommitUsed=11,CommitLimit=10},SlowFindingKind.Memory),"Invalid memory percentages and commit pairs do not trigger a finding");
        check(!Has(normal with{Processes=new[]{new SlowProcess(1,"bad",double.NaN,1),new SlowProcess(2,"bad",100.001,1)}},SlowFindingKind.Cpu),"Nonfinite or oversized CPU percentages do not trigger a finding");
        check(SlowMachine.CpuShare(1,1,TimeSpan.FromSeconds(1),1)==0&&SlowMachine.CpuShare(0,1,TimeSpan.Zero,1) is null&&SlowMachine.CpuShare(0,1,TimeSpan.FromSeconds(1),0) is null,"Zero usage and invalid CPU denominators remain distinct");
        var all=normal with{MemoryLoad=99,PendingReboot=true,SystemFree=0,Processes=new[]{new SlowProcess(1,"bad\n\u202E",80,123)}};
        check(SlowMachine.Findings(all).Count==5&&SlowMachine.Findings(normal).Count==1,"Sentence selection is bounded to one through five findings");
        check(SlowMachine.Findings(all).Select(f=>f.Action).SequenceEqual(new[]{SlowAction.AppMemory,SlowAction.UsageMonitor,SlowAction.CleanUp,SlowAction.WindowsUpdate,SlowAction.None}),"Each selected sentence routes to exactly its intended existing tool or Settings page");
        check(SlowMachine.Findings(all).Last().Kind==SlowFindingKind.Moment&&SlowMachine.Findings(all).All(f=>Enum.IsDefined(f.Action)),"Moment-in-time qualifier always retained, closed action selection");
        check(SlowMachine.CpuShare(0,TimeSpan.TicksPerSecond,TimeSpan.FromSeconds(1),4)==25&&SlowMachine.CpuShare(2,1,TimeSpan.FromSeconds(1),4) is null,"CPU is machine share, backwards counter unavailable");
    }
}
