using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed record AppQuitOutcome(IReadOnlyList<int> Windows,IReadOnlyList<CloseWindowsResult?> Results,IReadOnlyList<AppQuitTarget> Targets,IReadOnlyList<AppQuitState> States,IReadOnlySet<uint> Asked);

/// The App Memory quit flow: listed windows, then the existing normal close requests, then a
/// read-only check of which processes still run. Nothing here ends a process.
internal static class AppQuitReader
{
    internal static uint Session{get{using var self=System.Diagnostics.Process.GetCurrentProcess();return (uint)self.SessionId;}}
    internal static string WindowsFolder=>Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
    internal static Task<CloseWindowsSession> ReadWindowsAsync(CancellationToken token)=>Task.Run(()=>CloseWindowsReader.Read(token,quit:true),token);
    internal static AppQuitPlan Plan(IReadOnlyList<AppMemoryGroup> groups,CloseWindowsSession session,CancellationToken token)
        =>AppQuit.Plan(groups,session.Windows,(uint)Environment.ProcessId,Session,WindowsFolder,token);
    /// Sends the plan's requests, then waits up to AppQuit.Wait for the processes to exit. The
    /// session must stay undisposed until this completes.
    internal static async Task<AppQuitOutcome> RunAsync(CloseWindowsSession session,AppQuitPlan plan,IAppQuitProbe probe,CancellationToken token)
    {
        var windows=plan.Windows;var targets=plan.Targets;
        var results=await CloseWindowsReader.RequestEachAsync(session,windows,token);
        token.ThrowIfCancellationRequested();
        // Only processes that were sent a request (processed or uncertain) are waited for.
        var asked=new HashSet<uint>();
        for(int i=0;i<windows.Count;i++)if(results[i] is CloseWindowsResult.Processed or CloseWindowsResult.Unknown)asked.Add(session.Windows[windows[i]].ProcessId);
        var deadline=DateTime.UtcNow+AppQuit.Wait;IReadOnlyList<AppQuitState> states;
        while(true)
        {
            states=await Task.Run(()=>AppQuit.Check(targets,probe,token),token);
            bool waiting=false;for(int i=0;i<states.Count;i++)waiting|=states[i]==AppQuitState.Running&&asked.Contains(targets[i].Pid);
            if(!waiting||DateTime.UtcNow>=deadline)break;
            await Task.Delay(250,token);
        }
        return new(windows,results,targets,states,asked);
    }
    internal sealed class Probe : IAppQuitProbe
    {
        private const uint QueryAndSynchronize=0x00101000,Timeout=0x102,InvalidParameter=87;
        public AppQuitState State(AppQuitTarget target)
        {
            using var handle=OpenProcess(QueryAndSynchronize,false,target.Pid);
            // No such identifier means the recorded process is gone; other failures stay unknown.
            if(handle.IsInvalid)return Marshal.GetLastPInvokeError()==InvalidParameter?AppQuitState.Exited:AppQuitState.Unknown;
            uint wait=WaitForSingleObject(handle,0);
            if(wait==0)return AppQuitState.Exited;
            if(wait!=Timeout)return AppQuitState.Unknown;
            if(!GetProcessTimes(handle,out var created,out _,out _,out _))return AppQuitState.Unknown;
            // The identifier now belongs to another process: the recorded one has exited.
            if(created.Value!=target.Created)return AppQuitState.Exited;
            var path=new StringBuilder(AppMemory.MaxPathUnits+1);uint length=(uint)path.Capacity;
            if(!QueryFullProcessImageNameW(handle,0,path,ref length)||length==0||path.Length!=length)return AppQuitState.Unknown;
            return string.Equals(path.ToString(),target.Path,StringComparison.Ordinal)?AppQuitState.Running:AppQuitState.Exited;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTime {public uint Low,High;public readonly ulong Value=>((ulong)High<<32)|Low;}
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle,out NativeTime created,out NativeTime exit,out NativeTime kernel,out NativeTime user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle,uint flags,StringBuilder path,ref uint length);
}
