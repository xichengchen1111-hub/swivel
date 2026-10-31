using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ServiceReader
{
    private const int BufferBytes=256*1024;
    internal static ServiceSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();IntPtr manager=IntPtr.Zero,buffer=IntPtr.Zero;var rows=new List<ServiceRecord>();var issues=new List<ServiceReadIssue>();
        try
        {
            manager=OpenSCManagerW(null,null,4);uint openError=manager==IntPtr.Zero?unchecked((uint)Marshal.GetLastWin32Error()):0;
            token.ThrowIfCancellationRequested();
            if(manager==IntPtr.Zero)issues.Add(new(ServiceIssuePhase.Open,openError));
            else
            {
                buffer=Marshal.AllocHGlobal(BufferBytes);Marshal.Copy(new byte[BufferBytes],0,buffer,BufferBytes);token.ThrowIfCancellationRequested();uint resume=0;
                bool ok=EnumServicesStatusExW(manager,0,0x30,3,buffer,BufferBytes,out uint needed,out uint returned,ref resume,null);uint error=ok?0:unchecked((uint)Marshal.GetLastWin32Error());token.ThrowIfCancellationRequested();
                if(!ok&&error!=234)issues.Add(new(ServiceIssuePhase.Enumerate,error));
                else
                {
                    if(!ok)issues.Add(new(ServiceIssuePhase.BufferLimit,error,NeededBytes:needed));
                    int stride=Marshal.SizeOf<NativeRecord>();
                    if((ulong)returned*(uint)stride>BufferBytes)issues.Add(new(ServiceIssuePhase.InvalidRecord,ReportedRecords:returned));
                    else if(returned>ServiceRecords.MaxRecords)issues.Add(new(ServiceIssuePhase.RecordLimit,ReportedRecords:returned));
                    else
                    {
                        int startOfStrings=checked((int)returned*stride);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int copiedUnits=0;
                        for(int i=0;i<(int)returned;i++)
                        {
                            token.ThrowIfCancellationRequested();var entry=Marshal.PtrToStructure<NativeRecord>(IntPtr.Add(buffer,i*stride));
                            string? name=ReadName(buffer,startOfStrings,entry.Name),display=ReadName(buffer,startOfStrings,entry.Display);
                            if(string.IsNullOrEmpty(name)||display is null){issues.Add(new(ServiceIssuePhase.InvalidRecord,Record:i+1));continue;}
                            if(!names.Add(name)){issues.Add(new(ServiceIssuePhase.DuplicateName,Record:i+1));continue;}
                            if(name.Length+display.Length>ServiceRecords.MaxNameUnits-copiedUnits){issues.Add(new(ServiceIssuePhase.NameBudget,Record:i+1));continue;}
                            copiedUnits+=name.Length+display.Length;rows.Add(new(name,display,entry.Type,entry.State,entry.ProcessId));
                        }
                    }
                }
            }
        }
        finally
        {
            try{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);}
            finally{if(manager!=IntPtr.Zero&&!CloseServiceHandle(manager))issues.Add(new(ServiceIssuePhase.Close,unchecked((uint)Marshal.GetLastWin32Error())));}
        }
        token.ThrowIfCancellationRequested();return new(rows,issues,DateTimeOffset.Now);
    }
    // Reuse the status enumerator; query configuration only for an explicit F08 inspection.
    internal static IReadOnlyList<RegisteredCommand> Commands(CancellationToken token,Func<bool> stop,List<string> issues)
    {
        token.ThrowIfCancellationRequested();
        if(stop()){issues.Add(RegisteredTargets.Incomplete);return Array.Empty<RegisteredCommand>();}
        var snapshot=Read(token);var rows=new List<RegisteredCommand>();
        if(snapshot.Partial)issues.Add(RegisteredTargets.Incomplete);
        IntPtr manager=IntPtr.Zero,buffer=IntPtr.Zero;
        try
        {
            if(stop()){issues.Add(RegisteredTargets.Incomplete);return rows;}
            manager=OpenSCManagerW(null,null,1);token.ThrowIfCancellationRequested();
            if(manager==IntPtr.Zero){issues.Add(RegisteredTargets.Incomplete);return rows;}
            buffer=Marshal.AllocHGlobal(ConfigBytes);
            foreach(var service in snapshot.Services)
            {
                token.ThrowIfCancellationRequested();
                if(stop()||rows.Count>=1024){issues.Add(RegisteredTargets.Incomplete);break;}
                IntPtr handle=OpenServiceW(manager,service.Name,1);string? command=null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if(handle!=IntPtr.Zero&&!stop())
                    {
                        Marshal.Copy(new byte[ConfigBytes],0,buffer,ConfigBytes);
                        bool ok=QueryServiceConfigW(handle,buffer,ConfigBytes,out _);token.ThrowIfCancellationRequested();
                        if(ok)command=ConfigCommand(buffer);
                    }
                    if(command is null)issues.Add(RegisteredTargets.Incomplete);
                    rows.Add(new("Services",service.Name,command));
                }
                finally { if(handle!=IntPtr.Zero&&!CloseServiceHandle(handle))issues.Add(RegisteredTargets.Incomplete); }
            }
        }
        finally
        {
            try{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);}
            finally{if(manager!=IntPtr.Zero&&!CloseServiceHandle(manager))issues.Add(RegisteredTargets.Incomplete);}
        }
        token.ThrowIfCancellationRequested();return rows.AsReadOnly();
    }
    // QueryServiceConfigW documents an 8 KiB maximum: https://learn.microsoft.com/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw
    internal const int ConfigBytes=8192;
    internal static string? ConfigCommand(IntPtr buffer)
    {
        var config=Marshal.PtrToStructure<NativeConfig>(buffer);
        ulong start=unchecked((ulong)buffer.ToInt64()),pointer=unchecked((ulong)config.Binary.ToInt64());
        if(pointer<start||pointer-start<(ulong)Marshal.SizeOf<NativeConfig>()||pointer-start>=ConfigBytes||((pointer-start)&1)!=0)return null;
        int available=(ConfigBytes-(int)(pointer-start))/2;
        for(int i=0;i<Math.Min(available,RegisteredTargets.MaxCommand+1);i++)
            if(Marshal.ReadInt16(config.Binary,i*2)==0)return Marshal.PtrToStringUni(config.Binary,i);
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]internal struct NativeConfig
    {
        public uint Type,Start,Error;public IntPtr Binary,Group;public uint Tag;public IntPtr Dependencies,Account,Display;
    }
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern IntPtr OpenServiceW(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceConfigW(IntPtr service,IntPtr config,uint bytes,out uint needed);
    private static string? ReadName(IntPtr buffer,int startOfStrings,IntPtr pointer)
    {
        ulong start=unchecked((ulong)buffer.ToInt64()),value=unchecked((ulong)pointer.ToInt64());
        if(value<start||(value-start)>=(ulong)BufferBytes||(value-start)<(ulong)startOfStrings||((value-start)&1)!=0)return null;
        int offset=(int)(value-start),available=(BufferBytes-offset)/2,limit=Math.Min(available,ServiceRecords.MaxName+1);
        for(int n=0;n<limit;n++)if(Marshal.ReadInt16(pointer,n*2)==0)return Marshal.PtrToStringUni(pointer,n);
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRecord
    {
        public IntPtr Name,Display;public uint Type,State,Controls,Win32Exit,ServiceExit,Checkpoint,WaitHint,ProcessId,Flags;
    }
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumServicesStatusExW(IntPtr manager,uint level,uint type,uint state,IntPtr buffer,uint bytes,out uint needed,out uint returned,ref uint resume,string? group);
    [DllImport("advapi32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(IntPtr manager);
}
