using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

/// Read-only parent process identifiers from one Toolhelp32 process snapshot.
/// No process is opened and no process memory is read.
internal static class ProcessParentReader
{
    private const uint SnapProcess=0x2,NoMoreFiles=18;
    internal static AppMemoryParentIds Read()
    {
        var excluded=Excluded();
        try
        {
            using var snapshot=CreateToolhelp32Snapshot(SnapProcess,0);
            if(snapshot.IsInvalid)return new(new Dictionary<uint,uint>(),Unavailable:true,ExcludedDirectories:excluded);
            var parents=new Dictionary<uint,uint>();var repeated=new HashSet<uint>();
            var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};
            if(!Process32FirstW(snapshot,ref entry))
                return new(parents,Unavailable:Marshal.GetLastPInvokeError()!=NoMoreFiles,ExcludedDirectories:excluded);
            for(int count=0;;count++)
            {
                if(count>=AppMemory.MaxProcesses)return new(Clean(parents,repeated),Limited:true,ExcludedDirectories:excluded);
                // A repeated identifier is ambiguous, so neither answer is used.
                if(!repeated.Contains(entry.ProcessId)&&!parents.TryAdd(entry.ProcessId,entry.ParentProcessId)){parents.Remove(entry.ProcessId);repeated.Add(entry.ProcessId);}
                entry.Size=(uint)Marshal.SizeOf<ProcessEntry>();
                if(!Process32NextW(snapshot,ref entry))
                {
                    bool done=Marshal.GetLastPInvokeError()==NoMoreFiles;
                    return new(Clean(parents,repeated),Limited:!done||repeated.Count>0,ExcludedDirectories:excluded);
                }
            }
        }
        catch(Exception e)when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        {return new(new Dictionary<uint,uint>(),Unavailable:true,ExcludedDirectories:excluded);}
    }
    private static Dictionary<uint,uint> Clean(Dictionary<uint,uint> parents,HashSet<uint> repeated){foreach(uint pid in repeated)parents.Remove(pid);return parents;}
    private static string[] Excluded()
    {
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        return windows.Length>=3&&windows[1]==':'&&windows[2]=='\\'&&windows.IndexOf('\0')<0&&windows.Length<AppMemory.MaxPathUnits-2?new[]{windows}:Array.Empty<string>();
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct ProcessEntry
    {
        public uint Size,Usage,ProcessId;public nuint DefaultHeapId;public uint ModuleId,Threads,ParentProcessId;public int PriorityClassBase;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string ExeFile;
    }
    internal sealed class SnapshotHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {protected override bool ReleaseHandle()=>CloseHandle(handle);}
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SnapshotHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SnapshotHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
