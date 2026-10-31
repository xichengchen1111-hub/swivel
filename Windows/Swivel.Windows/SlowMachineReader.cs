using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class SlowMachineReader
{
    private sealed record ProcessTime(ulong? Created,ulong? Cpu,long Stamp,string Name,ulong? Memory);
    internal static Task<SlowMachineSnapshot> SampleAsync(CancellationToken token,TimeSpan? duration=null)
    {
        token.ThrowIfCancellationRequested();var wait=duration??TimeSpan.FromSeconds(5);
        if(wait<TimeSpan.Zero||wait>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(duration));
        return Task.Run(async()=>
        {
            var issues=new List<string>();var source=AppMemoryReader.Processes;
            uint processors=GetActiveProcessorCount(0xffff);if(processors==0||processors>int.MaxValue)issues.Add("The system processor count is unavailable; CPU shares are unknown.");
            var before=ReadProcesses(source,false,issues,token);await Task.Delay(wait,token).ConfigureAwait(false);
            var after=ReadProcesses(source,true,issues,token);var rows=new List<SlowProcess>();
            foreach(var pair in after)
            {
                token.ThrowIfCancellationRequested();double? cpu=null;var p=pair.Value;
                if(before.TryGetValue(pair.Key,out var first)&&p.Created is >0&&p.Created==first.Created&&first.Cpu is ulong start&&p.Cpu is ulong end)
                    cpu=SlowMachine.CpuShare(start,end,Stopwatch.GetElapsedTime(first.Stamp,p.Stamp),processors<=int.MaxValue?(int)processors:0);
                rows.Add(new((int)pair.Key,p.Name,cpu,p.Memory));
            }
            double? load=null;ulong? committed=null,limit=null,pageUsed=null,pageTotal=null,free=null,total=null;
            token.ThrowIfCancellationRequested();var memory=new UsageReader.MemoryStatus{Length=(uint)Marshal.SizeOf<UsageReader.MemoryStatus>()};
            if(GlobalMemoryStatusEx(ref memory)&&memory.Load<=100)load=memory.Load;else issues.Add("System memory load is unavailable.");
            token.ThrowIfCancellationRequested();var perf=new PerformanceInfo{Size=(uint)Marshal.SizeOf<PerformanceInfo>()};
            if(K32GetPerformanceInfo(ref perf,perf.Size)&&TryBytes(perf.CommitTotal,perf.PageSize,out ulong c)&&TryBytes(perf.CommitLimit,perf.PageSize,out ulong l)&&c<=l&&l>0){committed=c;limit=l;}
            else issues.Add("System commit readings are unavailable.");
            token.ThrowIfCancellationRequested();ulong usedPages=0,totalPages=0;bool valid=true;int pageCount=0;
            PageCallback callback=(IntPtr context,ref PageInfo page,IntPtr filename)=>
            {
                // Names are deliberately not dereferenced. Exceptions must never escape a native callback.
                if(token.IsCancellationRequested||++pageCount>1024){valid=false;return false;}
                ulong used=page.TotalInUse.ToUInt64(),size=page.TotalSize.ToUInt64();
                if(used>size||ulong.MaxValue-usedPages<used||ulong.MaxValue-totalPages<size){valid=false;return false;}
                usedPages+=used;totalPages+=size;return true;
            };
            if(K32EnumPageFilesW(callback,IntPtr.Zero)&&valid&&TryBytes((UIntPtr)usedPages,(UIntPtr)(uint)Environment.SystemPageSize,out ulong pu)&&TryBytes((UIntPtr)totalPages,(UIntPtr)(uint)Environment.SystemPageSize,out ulong pt)){pageUsed=pu;pageTotal=pt;}
            else issues.Add("Pagefile use is unavailable.");
            GC.KeepAlive(callback);token.ThrowIfCancellationRequested();
            string? system=DriveDeviceReader.WindowsVolume();
            if(system is not null&&GetDiskFreeSpaceExW(system,out ulong freeBytes,out ulong sizeBytes,out _)&&freeBytes<=sizeBytes&&sizeBytes>0){free=freeBytes;total=sizeBytes;}
            else issues.Add("System-drive free space is unavailable.");
            token.ThrowIfCancellationRequested();bool? reboot;
            try{reboot=PendingReboot();}catch(Exception e)when(e is UnauthorizedAccessException or SecurityException or System.IO.IOException){reboot=null;}if(reboot is null)issues.Add("Pending-restart flags are unavailable or incomplete.");
            token.ThrowIfCancellationRequested();return new SlowMachineSnapshot(rows.AsReadOnly(),load,committed,limit,pageUsed,pageTotal,free,total,TimeSpan.FromMilliseconds(GetTickCount64()),reboot,issues.Distinct().ToArray());
        },token);
    }
    private static Dictionary<uint,ProcessTime> ReadProcesses(IAppMemorySource source,bool details,List<string> issues,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var ids=source.Enumerate();var rows=new Dictionary<uint,ProcessTime>();var clock=Stopwatch.StartNew();
        if(ids.Limited||ids.Unavailable)issues.Add("The process list is partial or unavailable.");
        foreach(uint pid in ids.Ids.Take(AppMemory.MaxProcesses).Distinct())
        {
            token.ThrowIfCancellationRequested();if(clock.Elapsed>TimeSpan.FromSeconds(3)){issues.Add("The process list reached its time limit (partial).");break;}
            if(pid==0)continue;using var process=source.Open(pid);token.ThrowIfCancellationRequested();
            if(process is null){issues.Add("Some processes exited or denied access; their readings are unavailable.");continue;}
            ulong? created=process.CreationTime(),cpu=process.CpuTime();long stamp=Stopwatch.GetTimestamp();string name="";ulong? memory=null;
            if(details)
            {
                string? path=process.ImagePath();if(path is not null)name=path[(path.LastIndexOf('\\')+1)..];
                memory=process.WorkingSet();if(name.Length==0||memory is null||cpu is null)issues.Add("Some processes exited or denied access; their readings are unavailable.");
            }
            rows[pid]=new(created,cpu,stamp,name,memory);
        }
        token.ThrowIfCancellationRequested();return rows;
    }
    private static bool TryBytes(UIntPtr pages,UIntPtr size,out ulong bytes)
    {ulong p=pages.ToUInt64(),s=size.ToUInt64();bytes=0;if(s==0||p>ulong.MaxValue/s)return false;bytes=p*s;return true;}
    private static bool? PendingReboot()
    {
        bool unknown=false;
        using var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        foreach(string path in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"})
        {
            try{using var key=registry.OpenSubKey(path,false);if(key is not null)return true;}
            catch(Exception e)when(e is UnauthorizedAccessException or SecurityException or System.IO.IOException){unknown=true;}
        }
        try
        {
            using var key=registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager",false);
            if(key is null)unknown=true;
            else
            {
                uint size=0;int result=RegQueryValueExW(key.Handle,"PendingFileRenameOperations",IntPtr.Zero,out uint type,IntPtr.Zero,ref size);
                if(result==0&&type==7&&size>4)return true;
                if(result!=2&&(result!=0||type!=7))unknown=true;
            }
        }
        catch(Exception e)when(e is UnauthorizedAccessException or SecurityException or System.IO.IOException){unknown=true;}
        return unknown?null:false;
    }
    [StructLayout(LayoutKind.Sequential)]private struct PerformanceInfo
    {public uint Size;public UIntPtr CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;public uint HandleCount,ProcessCount,ThreadCount;}
    [StructLayout(LayoutKind.Sequential)]private struct PageInfo{public uint Size,Reserved;public UIntPtr TotalSize,TotalInUse,PeakUsage;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)][return:MarshalAs(UnmanagedType.Bool)]private delegate bool PageCallback(IntPtr context,ref PageInfo page,IntPtr filename);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalMemoryStatusEx(ref UsageReader.MemoryStatus memory);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool K32GetPerformanceInfo(ref PerformanceInfo info,uint size);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool K32EnumPageFilesW(PageCallback callback,IntPtr context);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetDiskFreeSpaceExW(string root,out ulong available,out ulong total,out ulong free);
    [DllImport("kernel32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern int RegQueryValueExW(SafeRegistryHandle key,string name,IntPtr reserved,out uint type,IntPtr data,ref uint size);
}
