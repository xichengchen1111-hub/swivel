using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class AppMemoryReader
{
    internal static AppMemorySnapshot Read(CancellationToken token)=>AppMemory.Scan(new Source(),token);
    /// The same limited process queries, for checks that need running programs' paths.
    internal static IAppMemorySource Processes=>new Source();
    private static bool Unavailable(Exception e)=>e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
    private sealed class Source : IAppMemorySource
    {
        public AppMemoryIds Enumerate()
        {
            try
            {
                var ids=new uint[AppMemory.MaxProcesses];uint bytes=(uint)ids.Length*4;
                if(!K32EnumProcesses(ids,bytes,out uint written)||written==0||written>bytes||written%4!=0)return new(Array.Empty<uint>(),Unavailable:true);
                int count=(int)(written/4);return new(ids.AsSpan(0,count).ToArray(),written==bytes);
            }
            catch(Exception e)when(Unavailable(e)){return new(Array.Empty<uint>(),Unavailable:true);}
        }
        public IAppMemoryProcess? Open(uint pid)
        {
            if(pid==0)return null;
            try
            {
                var handle=OpenProcess(0x1000,false,pid); // QUERY_LIMITED_INFORMATION only.
                if(handle.IsInvalid){handle.Dispose();return null;}
                try{return new Process(handle);}catch{handle.Dispose();throw;}
            }
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public AppMemoryParentIds ParentIds()=>ProcessParentReader.Read();
    }
    private sealed class Process(SafeProcessHandle handle) : IAppMemoryProcess
    {
        public ulong? CreationTime()
        {
            try{return GetProcessTimes(handle,out var created,out _,out _,out _)?created.Value:null;}
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public string? ImagePath()
        {
            try
            {
                var buffer=new StringBuilder(AppMemory.MaxPathUnits+1);uint length=(uint)buffer.Capacity;
                if(!QueryFullProcessImageNameW(handle,0,buffer,ref length)||length==0||length>AppMemory.MaxPathUnits||buffer.Length!=length)return null;
                return buffer.ToString();
            }
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public ulong? WorkingSet()
        {
            try
            {
                uint size=(uint)Marshal.SizeOf<MemoryCounters>();
                return K32GetProcessMemoryInfo(handle,out var memory,size)&&memory.Size==size?(ulong)memory.WorkingSetSize:null;
            }
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public ulong? CpuTime()
        {
            try
            {
                if(!GetProcessTimes(handle,out _,out _,out var kernel,out var user))return null;
                ulong a=kernel.Value,b=user.Value;return b>ulong.MaxValue-a?null:a+b;
            }
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public uint? Session()
        {
            try{return ProcessIdToSessionId(GetProcessId(handle),out uint session)?session:null;}
            catch(Exception e)when(Unavailable(e)){return null;}
        }
        public void Dispose()=>handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeTime
    {public uint Low,High;public readonly ulong Value=>((ulong)High<<32)|Low;}
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryCounters
    {
        public uint Size,PageFaultCount;
        public nuint PeakWorkingSetSize,WorkingSetSize,QuotaPeakPagedPoolUsage,QuotaPagedPoolUsage,QuotaPeakNonPagedPoolUsage,QuotaNonPagedPoolUsage,PagefileUsage,PeakPagefileUsage;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool K32EnumProcesses([Out] uint[] ids,uint bytes,out uint written);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle,out NativeTime created,out NativeTime exit,out NativeTime kernel,out NativeTime user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle,uint flags,StringBuilder path,ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(SafeProcessHandle handle);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid,out uint session);
    [DllImport("kernel32.dll",SetLastError=true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle handle,out MemoryCounters counters,uint bytes);
}
